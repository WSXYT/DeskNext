using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using DeskNest.Inference;
using Microsoft.ML.OnnxRuntime;

internal static class DirectMlModel
{
    internal static Probe.Result Run(Probe.Request request, Probe.Tensors tensors, string directory,
        JsonElement config, int adapter, string evidence)
    {
        int length = tensors.InputIds.Single().Length, markers = request.Candidates.Length;
        if (length is < 1 or > 1024 || tensors.AttentionMask.Length != 1 || tensors.AttentionMask[0].Length != length ||
            tensors.MarkerPos.Length != 1 || tensors.MarkerPos[0].Length != markers || tensors.MarkerMask.Length != 1 ||
            tensors.MarkerMask[0].Length != markers || tensors.MarkerPos[0].Any(p => p < 0 || p >= length) ||
            tensors.MarkerMask[0].Any(p => !p) || tensors.AttentionMask[0].Any(p => p is not (0 or 1)) ||
            tensors.Qtype.Length != 1 || tensors.Qtype[0] != 0)
            throw new InvalidDataException("Invalid fixed input shape.");
        float[] hidden;
        using var attention = OrtValue.CreateTensorValueFromMemory(tensors.AttentionMask[0], [1, length]);
        using var options = new RunOptions();
        var clock = Stopwatch.StartNew();
        Console.Error.WriteLine("GPU_PHASE: encoder");
        using (var encoder = Session(directory, "encoder", length, markers, adapter, evidence))
        {
            using var ids = OrtValue.CreateTensorValueFromMemory(tensors.InputIds[0], [1, length]);
            using var embedding = OrtValue.CreateTensorValueFromMemory(ReadTokenRows(directory, tensors.InputIds[0]), [1, length, 768]);
            using var encoded = encoder.Run(options, new Dictionary<string, OrtValue>
            {
                ["input_ids"] = ids, ["attention_mask"] = attention, ["embedding"] = embedding
            }, ["last_hidden_state"]);
            hidden = encoded.First().GetTensorDataAsSpan<float>().ToArray();
            VerifyGpuProfile(encoder.EndProfiling());
        }
        Console.Error.WriteLine("GPU_PHASE: encoder disposed; head");
        using var head = Session(directory, "head", length, markers, adapter, evidence);
        using var h = OrtValue.CreateTensorValueFromMemory(hidden, [1, length, 768]);
        using var positions = OrtValue.CreateTensorValueFromMemory(tensors.MarkerPos[0], [1, markers]);
        using var masks = OrtValue.CreateTensorValueFromMemory(tensors.MarkerMask[0], [1, markers]);
        using var qtype = OrtValue.CreateTensorValueFromMemory(tensors.Qtype, [1, 1]);
        using var output = head.Run(options, new Dictionary<string, OrtValue>
        {
            ["hidden_states"] = h, ["marker_pos"] = positions, ["marker_mask"] = masks,
            ["qtype"] = qtype, ["attention_mask"] = attention
        }, ["logits", "act_logits"]);
        var result = Probe.Decide(request, tensors, output.First().GetTensorDataAsSpan<float>().ToArray(),
            output.Skip(1).First().GetTensorDataAsSpan<float>().ToArray(), config);
        VerifyGpuProfile(head.EndProfiling());
        Console.Error.WriteLine($"GPU_GRAPHS_MS: {clock.ElapsedMilliseconds}");
        return result;
    }

    private static InferenceSession Session(string directory, string name, int length, int markers, int adapter, string evidence)
    {
        using var options = new SessionOptions
        {
            ProfileOutputPathPrefix = Path.Combine(evidence, name), EnableProfiling = true,
            ExecutionMode = ExecutionMode.ORT_SEQUENTIAL, EnableMemoryPattern = false,
            EnableCpuMemArena = false, IntraOpNumThreads = 1, InterOpNumThreads = 1
        };
        options.AddSessionConfigEntry("session.disable_prepacking", "1");
        options.AddFreeDimensionOverrideByName("batch", 1);
        options.AddFreeDimensionOverrideByName("seq", length);
        if (name == "head") options.AddFreeDimensionOverrideByName("markers", markers);
        options.AppendExecutionProvider_DML(adapter);
        // No retry with CPU if the explicit DirectML session cannot load or execute.
        return new InferenceSession(Path.Combine(directory, name + ".onnx"), options);
    }

    private static void VerifyGpuProfile(string path)
    {
        using var file = File.OpenRead(path);
        using var profile = JsonDocument.Parse(file);
        int gpu = 0;
        foreach (var e in profile.RootElement.EnumerateArray())
        {
            if (!e.TryGetProperty("args", out var args) || !args.TryGetProperty("provider", out var provider)) continue;
            if (provider.GetString() == "DmlExecutionProvider") gpu++;
            else if (provider.GetString() == "CPUExecutionProvider")
                throw new InvalidOperationException("Unexpected CPU graph kernel; this GPU-only graph experiment refuses fallback.");
        }
        if (gpu == 0) throw new InvalidOperationException("No DirectML kernel evidence.");
        Console.Error.WriteLine($"GPU_PROFILE: {Path.GetFileName(path)} DirectML kernels={gpu}, CPU kernels=0");
    }

    private static float[] ReadTokenRows(string directory, long[] tokenIds)
    {
        using var layout = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "embedding-layout.json")));
        var root = layout.RootElement;
        if (root.GetProperty("file").GetString() != "token-embedding-source.bin" ||
            root.GetProperty("width").GetInt32() != 768 || root.GetProperty("rows").GetInt32() != 256000 ||
            root.GetProperty("input").GetString() != "embedding" || root.GetProperty("format").GetString() != "little-endian-float32")
            throw new InvalidDataException("Unexpected embedding layout.");
        long offset = root.GetProperty("offset").GetInt64();
        using var file = File.OpenHandle(Path.Combine(directory, "token-embedding-source.bin"), FileMode.Open, FileAccess.Read, FileShare.Read);
        if (offset < 0 || RandomAccess.GetLength(file) < checked(offset + 256000L * 768 * 4))
            throw new InvalidDataException("Incomplete embedding data.");
        var rows = new float[checked(tokenIds.Length * 768)];
        Span<byte> row = stackalloc byte[768 * 4];
        for (int i = 0; i < tokenIds.Length; i++)
        {
            if (tokenIds[i] < 0 || tokenIds[i] >= 256000) throw new InvalidDataException("Invalid token index.");
            int done = 0;
            while (done < row.Length)
            {
                int count = RandomAccess.Read(file, row[done..], checked(offset + tokenIds[i] * 768 * 4 + done));
                if (count == 0) throw new EndOfStreamException("Incomplete embedding row.");
                done += count;
            }
            for (int j = 0; j < 768; j++) rows[i * 768 + j] = BinaryPrimitives.ReadSingleLittleEndian(row.Slice(j * 4, 4));
        }
        return rows;
    }
}
