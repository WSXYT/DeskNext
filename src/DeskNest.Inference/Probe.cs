using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Tokenizers.HuggingFace.Tokenizer;

namespace DeskNest.Inference;

// P1 CPU-only diagnostic process. It has no filesystem operation API and is not a sandbox.
public static class Probe
{
    public const string Ambiguous = "filename-ambiguous";
    public const string Insufficient = "categories-insufficient";
    private const int MaxFrame = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static string AssetPath(string directory, string name)
    {
        if (string.IsNullOrWhiteSpace(name) || Path.IsPathRooted(name) || name.Contains('/') || name.Contains('\\'))
            throw new InvalidDataException("Unsafe model path");
        var root = Path.GetFullPath(directory);
        var path = Path.GetFullPath(Path.Combine(root, name));
        var prefix = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Unsafe model path");
        return path;
    }

    public sealed record Candidate(string Id, string? Description);
    public sealed record Request(string RequestId, long Revision, string State, string Instructions, Candidate[] Candidates);
    public sealed record Tensors(long[][] InputIds, long[][] AttentionMask, long[][] MarkerPos, bool[][] MarkerMask, long[] Qtype);
    public sealed record Result(string RequestId, long Revision, Tensors Tensors, float[] Logits, double[] Probabilities,
        string Choice, string? BestReal, string Action, double Confidence, double AnswerConfidence, double ActProbability, double Temperature);

    public static Tensors Encode(Request request, Tokenizer tokenizer, JsonElement config, string directory)
    {
        Validate(request);
        int maxLen = config.GetProperty("max_len").GetInt32();
        int headMaxLen = config.GetProperty("head_max_len").GetInt32();
        using var tokJson = JsonDocument.Parse(File.ReadAllText(AssetPath(directory, "tokenizer.json")));
        var added = tokJson.RootElement.GetProperty("added_tokens").EnumerateArray()
            .ToDictionary(x => x.GetProperty("content").GetString()!, x => x.GetProperty("id").GetInt64());
        // The multilingual checkpoint uses <bos>/<eos>/<pad>/<mask>. Never assume English IDs.
        long Special(params string[] names) => names.Where(added.ContainsKey).Select(n => added[n]).First();
        string mask = new[] { "[MASK]", "<mask>" }.First(added.ContainsKey);
        long cls = Special("[CLS]", "<bos>", "<s>");
        long sep = Special("[SEP]", "<eos>", "</s>");
        long maskId = added[mask];
        long[] Tokens(string text) => tokenizer.Encode(text.Replace(mask, " "), false).First().Ids.Select(x => (long)x).ToArray();
        var head = Tokens("choice question: " + request.Instructions);
        var options = request.Candidates.Select(c =>
            (new[] { maskId }).Concat(Tokens(" " + c.Id + (string.IsNullOrEmpty(c.Description) ? "" : ": " + c.Description))).ToArray()).ToArray();
        int budget = headMaxLen - options.Sum(x => x.Length);
        // The upstream truncates descriptions/head. Product requests reject this instead of silently dropping evidence.
        if (budget < 16 || head.Length > budget || options.Any(x => x.Length > 49))
            throw new InvalidDataException("Question exceeds the checkpoint head budget");
        var ids = new List<long> { cls };
        ids.AddRange(head);
        ids.Add(sep);
        var markers = new List<long>();
        foreach (var option in options) { markers.Add(ids.Count); ids.AddRange(option); }
        ids.Add(sep);
        var state = Tokens(request.State);
        if (ids.Count + state.Length + 1 > maxLen)
            throw new InvalidDataException("State exceeds the checkpoint sequence budget");
        ids.AddRange(state);
        ids.Add(sep);
        return new Tensors([ids.ToArray()], [Enumerable.Repeat(1L, ids.Count).ToArray()],
            [markers.ToArray()], [Enumerable.Repeat(true, markers.Count).ToArray()], [0]);
    }

    public static void Validate(Request request)
    {
        if (string.IsNullOrWhiteSpace(request.RequestId) || request.State is null || request.Instructions is null ||
            request.Candidates is null || request.Candidates.Length < 3 || request.Candidates.Length > 256 ||
            request.Candidates.Any(c => string.IsNullOrWhiteSpace(c.Id) || c.Description is null) ||
            request.Candidates.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != request.Candidates.Length ||
            request.Candidates.Count(c => c.Id == Ambiguous) != 1 || request.Candidates.Count(c => c.Id == Insufficient) != 1)
            throw new InvalidDataException("Invalid request or missing/duplicate special candidate");
    }

    public static double Temperature(JsonElement config, int count)
    {
        string bucket = count <= 2 ? "2" : count <= 5 ? "3-5" : count <= 10 ? "6-10" : "11+";
        var byOptions = config.GetProperty("temperature_by_options");
        var temp = byOptions.TryGetProperty("choice:" + bucket, out var value) ? value.GetDouble() :
            config.GetProperty("temperature")[0].GetDouble();
        if (!double.IsFinite(temp)) throw new InvalidDataException("Invalid temperature");
        return Math.Clamp(temp, 0.5, 5.0);
    }

    public static Result Decide(Request request, Tensors tensors, float[] logits, float[] acts, JsonElement config)
    {
        Validate(request);
        int k = request.Candidates.Length;
        if (logits.Length < k || acts.Length < 2 || logits.Take(k).Any(x => !float.IsFinite(x)) || acts.Any(x => !float.IsFinite(x)))
            throw new InvalidDataException("Invalid graph outputs");
        var temp = Temperature(config, k);
        var z = logits.Take(k).Select(x => x / temp).ToArray();
        var max = z.Max();
        var exp = z.Select(x => Math.Exp(x - max)).ToArray();
        var sum = exp.Sum();
        var p = exp.Select(x => x / sum).ToArray();
        var chosen = Enumerable.Range(0, k).OrderByDescending(i => p[i]).ThenBy(i => request.Candidates[i].Id, StringComparer.Ordinal).ToArray();
        int winner = chosen[0];
        string? bestReal = chosen.Select(i => request.Candidates[i].Id).FirstOrDefault(id => id != Ambiguous && id != Insufficient);
        // P1 never authorizes a move; a near tie or a special result must remain pending.
        string action = p[chosen[0]] - p[chosen[1]] <= 0.0002 || request.Candidates[winner].Id is Ambiguous or Insufficient
            ? "pending" : "proposed";
        var ent = -p.Sum(v => v * Math.Log(Math.Max(v, 1e-12)));
        var actMax = acts.Max();
        var actExp = acts.Select(a => Math.Exp(a - actMax)).ToArray();
        return new Result(request.RequestId, request.Revision, tensors, logits.Take(k).ToArray(), p,
            request.Candidates[winner].Id, bestReal, action,
            Math.Clamp(1 - ent / Math.Log(k), 0, 1), p.Max(), actExp[0] / actExp.Sum(), temp);
    }

    public static void VerifyModel(string directory)
    {
        // Probe manifests are hashes, not signatures; never use this as a production installer trust decision.
        using var manifest = JsonDocument.Parse(File.ReadAllText(AssetPath(directory, "manifest.json")));
        var root = manifest.RootElement;
        if (root.GetProperty("sourceCommit").GetString() != "970dc8c5f63d7b886a68409493f37d569424f933" ||
            root.GetProperty("checkpointRevision").GetString() != "55cf4c4ebb4ebe31b2550e8bdf3bd21b99753851" ||
            root.GetProperty("weightsSha256").GetString() != "9d628fd971b700382ac6f65920a86f149777b2e748e0c955fb3b19695aa8f204")
            throw new InvalidDataException("Model provenance mismatch");
        foreach (var entry in root.GetProperty("files").EnumerateObject())
        {
            var name = entry.Name;
            if (name.Contains('/') || name.Contains('\\') || name is "." or "..") throw new InvalidDataException("Unsafe model path");
            var path = AssetPath(directory, name);
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Model symlink rejected");
            using var asset = File.OpenRead(path);
            var hash = Convert.ToHexString(SHA256.HashData(asset)).ToLowerInvariant();
            if (hash != entry.Value.GetString()) throw new InvalidDataException("Model hash mismatch: " + name);
        }
        foreach (var required in new[] { "encoder.onnx", "head.onnx", "tokenizer.json", "rl_agent_config.json" })
            if (!root.GetProperty("files").TryGetProperty(required, out _)) throw new InvalidDataException("Missing model asset: " + required);
    }

    public static Result Run(Request request, string directory, Tensors? fixture = null)
    {
        VerifyModel(directory);
        using var configDoc = JsonDocument.Parse(File.ReadAllText(AssetPath(directory, "rl_agent_config.json")));
        Tensors tensors;
        if (fixture is null)
        {
            using var tokenizer = Tokenizer.FromFile(AssetPath(directory, "tokenizer.json"));
            tensors = Encode(request, tokenizer, configDoc.RootElement, directory);
        }
        else tensors = fixture;
        Validate(request);
        int n = tensors.InputIds.Length, l = tensors.InputIds[0].Length, k = tensors.MarkerPos[0].Length;
        if (n != 1 || k != request.Candidates.Length || tensors.AttentionMask[0].Length != l || tensors.MarkerMask[0].Length != k ||
            tensors.Qtype.Length != 1 || tensors.Qtype[0] != 0 || tensors.InputIds[0].Any(x => x < 0) ||
            tensors.AttentionMask[0].Any(x => x != 0 && x != 1) ||
            tensors.MarkerPos[0].Any(x => x < 0 || x >= l) || tensors.MarkerMask[0].Any(x => !x))
            throw new InvalidDataException("Invalid input tensors");
        using var encoder = new InferenceSession(AssetPath(directory, "encoder.onnx"));
        using var head = new InferenceSession(AssetPath(directory, "head.onnx"));
        using var ids = OrtValue.CreateTensorValueFromMemory(tensors.InputIds[0], [1, l]);
        using var attention = OrtValue.CreateTensorValueFromMemory(tensors.AttentionMask[0], [1, l]);
        using var encoded = encoder.Run(new RunOptions(), new Dictionary<string, OrtValue> { ["input_ids"] = ids, ["attention_mask"] = attention }, ["last_hidden_state"]);
        var hidden = encoded.First().GetTensorDataAsSpan<float>().ToArray();
        long dimension = hidden.Length / l;
        using var h = OrtValue.CreateTensorValueFromMemory(hidden, [1, l, dimension]);
        using var positions = OrtValue.CreateTensorValueFromMemory(tensors.MarkerPos[0], [1, k]);
        using var masks = OrtValue.CreateTensorValueFromMemory(tensors.MarkerMask[0], [1, k]);
        using var qtype = OrtValue.CreateTensorValueFromMemory(tensors.Qtype, [1, 1]);
        using var outputs = head.Run(new RunOptions(), new Dictionary<string, OrtValue> {
            ["hidden_states"] = h, ["marker_pos"] = positions, ["marker_mask"] = masks,
            ["qtype"] = qtype, ["attention_mask"] = attention }, ["logits", "act_logits"]);
        return Decide(request, tensors, outputs.First().GetTensorDataAsSpan<float>().ToArray(),
            outputs.Skip(1).First().GetTensorDataAsSpan<float>().ToArray(), configDoc.RootElement);
    }

    public static void Worker(string directory, Stream input, Stream output)
    {
        Span<byte> header = stackalloc byte[4];
        while (true)
        {
            if (input.Read(header[..1]) == 0) return;
            input.ReadExactly(header[1..]);
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length < 1 || length > MaxFrame) throw new InvalidDataException("Invalid frame size");
            byte[] data = new byte[length];
            input.ReadExactly(data);
            var request = JsonSerializer.Deserialize<Request>(data, Json) ?? throw new InvalidDataException("Null request");
            byte[] response = JsonSerializer.SerializeToUtf8Bytes(Run(request, directory), Json);
            if (response.Length > MaxFrame) throw new InvalidDataException("Response frame too large");
            BinaryPrimitives.WriteInt32LittleEndian(header, response.Length);
            output.Write(header);
            output.Write(response);
            output.Flush();
        }
    }
}
