using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using DeskNest.Inference;
using Microsoft.ML.OnnxRuntime;
using Tokenizers.HuggingFace.Tokenizer;

internal static class DirectMlModel
{
    internal static Probe.Result RunVerified(Probe.Request request, Probe.Tensors? tensors, string directory,
        int adapter, string evidence, string? expectedModelHash = null, bool exportFeatures = false)
    {
        Probe.Validate(request);
        if (!Path.IsPathFullyQualified(directory)) throw new ArgumentException("Model directory must be absolute.");
        string hash = Probe.ModelManifestHash(directory);
        if (expectedModelHash is not null && !hash.Equals(expectedModelHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Selected model does not match the worker request.");
        // Pin derived assets independently of manifest formatting/provenance additions.
        using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "manifest.json"))))
        {
            var expected = new Dictionary<string, string>
            {
                ["encoder.onnx"] = "3e2480db75d05e904239ba026ecd0cadb0f77a088003ff040448b3b2531e5a44",
                ["encoder.onnx.data"] = "43968e05afccf9a3a17b1878a079559a58e49726f2da0190a11228c8f410e4a8",
                ["head.onnx"] = "f84dd6386d78b803ef823dbbcd0f1cbc67d7081c5d48883ba6d1f85b69caa5e3",
                ["head.onnx.data"] = "2493d0bbe40c3f972f158b531964df01eee26accafffe11cc861c43fcc011b2a",
                ["rl_agent_config.json"] = "25061739243b617ad88d1219ba6f8a9c86c5881ca28df024fa2d9b3b2fcc30c6",
                ["tokenizer.json"] = "609d8f4c067cd3950f88594c5a802616cea245823836ef5848ee4fc40aab5b6f",
                ["token-embedding-source.bin"] = "f1e49a027352064d72831863456be6160748854296bff8332df14130dbf79834",
                ["embedding-layout.json"] = "ecf33e0dd953e843f289990d0ea4d3fb79d1406d4db22db66a9ff001a24662d4"
            };
            var files = manifest.RootElement.GetProperty("files").EnumerateObject().ToArray();
            if (files.Length != expected.Count || files.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count() != expected.Count ||
                files.Any(f => !expected.TryGetValue(f.Name, out var digest) || f.Value.GetString() != digest))
                throw new InvalidDataException("Not the pinned row-lookup experiment assets.");
        }
        Probe.VerifyModel(directory);
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "rl_agent_config.json")));
        if (tensors is null)
        {
            // Host tokenization is not GPU inference. Release it before loading either graph.
            using var tokenizer = Tokenizer.FromFile(Path.Combine(directory, "tokenizer.json"));
            tensors = Probe.Encode(request, tokenizer, config.RootElement, directory);
        }
        var result = Run(request, tensors, directory, config.RootElement, adapter, evidence, exportFeatures);
        if (!Probe.ModelManifestHash(directory).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Model manifest changed during the request.");
        return result;
    }

    internal static Probe.Result Run(Probe.Request request, Probe.Tensors tensors, string directory,
        JsonElement config, int adapter, string evidence, bool exportFeatures = false)
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
        if (exportFeatures)
        {
            // Explicit training-data preparation only. Never enabled for a workbench/versioned request.
            if (!BitConverter.IsLittleEndian) throw new PlatformNotSupportedException("Feature export requires little-endian FP32.");
            using var features = new FileStream(Path.Combine(evidence, "encoder-features.f32"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            features.Write(System.Runtime.InteropServices.MemoryMarshal.AsBytes(hidden.AsSpan()));
            using var metadata = new FileStream(Path.Combine(evidence, "feature-input.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None);
            JsonSerializer.Serialize(metadata, new { request, tensors, shape = new[] { 1, length, 768 }, dtype = "float32-le",
                scope = "Frozen encoder outputs for explicit developer training; not labels, accepted quality or production state." },
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
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
