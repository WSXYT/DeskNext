using System.Diagnostics;
using System.Text.Json;
using DeskNest.Inference;
using Microsoft.ML.OnnxRuntime;
using Tokenizers.HuggingFace.Tokenizer;

// Developer-only NVIDIA DirectML runner. One request, no CPU backend or filesystem-operation authority.
// Build/run separately from DeskNest.App: the native runtime and experimental model are not release assets.
if (!OperatingSystem.IsWindows() || args.Length != 3 || !int.TryParse(args[1], out int adapter) || adapter < 0)
{
    Console.Error.WriteLine("Usage (Windows): DeskNest.DirectMLProbe <row-lookup-model> <DXGI-adapter-index> <new-evidence-directory>");
    return 2;
}
try
{
    Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
    var adapterInfo = NvidiaAdapter.Read(adapter); // Do not confuse nvidia-smi indices with DXGI ordering.
    string modelDirectory = Path.GetFullPath(args[0]), evidence = Path.GetFullPath(args[2]);
    if (Path.Exists(evidence)) throw new IOException("Evidence directory must be new.");
    Directory.CreateDirectory(evidence);
    using var input = Console.OpenStandardInput();
    using var bytes = new MemoryStream();
    var buffer = new byte[8192];
    int count;
    while ((count = input.Read(buffer)) > 0)
    {
        if (bytes.Length + count > 1024 * 1024) throw new InvalidDataException("Request exceeds 1 MiB.");
        bytes.Write(buffer, 0, count);
    }
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    var selected = JsonSerializer.Deserialize<ProbeInput>(bytes.ToArray(), json) ?? throw new InvalidDataException("Missing request.");
    Probe.Validate(selected.Request);
    var clock = Stopwatch.StartNew();
    using var environment = OrtEnv.Instance();
    string hash = Probe.ModelManifestHash(modelDirectory);
    // Pin every derived asset separately; extra/different graphs are not accepted by this experiment.
    using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(modelDirectory, "manifest.json"))))
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
    Probe.VerifyModel(modelDirectory);
    using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(modelDirectory, "rl_agent_config.json")));
    var tensors = selected.Tensors;
    if (tensors is null)
    {
        // Host tokenization is not GPU inference. Dispose it before loading either neural graph.
        using var tokenizer = Tokenizer.FromFile(Path.Combine(modelDirectory, "tokenizer.json"));
        tensors = Probe.Encode(selected.Request, tokenizer, config.RootElement, modelDirectory);
    }
    var result = DirectMlModel.Run(selected.Request, tensors, modelDirectory, config.RootElement, adapter, evidence);
    if (!Probe.ModelManifestHash(modelDirectory).Equals(hash, StringComparison.OrdinalIgnoreCase))
        throw new InvalidDataException("Model manifest changed during the request.");
    using var process = Process.GetCurrentProcess();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        success = true, provider = "DirectML", adapter = adapterInfo, modelManifestSha256 = hash,
        inputMode = selected.Tensors is null ? "shared-production-tokenizer" : "supplied-frozen-tensors",
        scope = "Experimental local NVIDIA inference; host tokenization, embedding reads and postprocessing; no automatic runtime selection or release acceptance.",
        totalMs = clock.ElapsedMilliseconds, hostPeakWorkingSetBytes = process.PeakWorkingSet64,
        result
    }, json));
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}

internal sealed record ProbeInput(Probe.Request Request, Probe.Tensors? Tensors);
