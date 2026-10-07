using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using DeskNest.Inference;
using Microsoft.ML.OnnxRuntime;

// Isolated developer runner. Never load its DirectML native assets into the CPU App directory.
bool worker = args.Length == 5 && args[0].StartsWith("--adapter=", StringComparison.Ordinal) &&
    args[1].StartsWith("--profiles=", StringComparison.Ordinal) && args[2] == "--inference-worker" && args[4] == "--protocol=1";
bool diagnostic = args.Length == 3 && !args[0].StartsWith("--", StringComparison.Ordinal);
int adapter = -1;
if (!OperatingSystem.IsWindows() || (!worker && !diagnostic) ||
    !int.TryParse(worker ? args[0]["--adapter=".Length..] : args[1], out adapter) || adapter < 0)
{
    Console.Error.WriteLine("Usage: <absolute-model> <DXGI-index> <new-evidence-directory> OR --adapter=N --profiles=<new-directory> --inference-worker <absolute-model> --protocol=1");
    return 2;
}
OrtEnv? environment = null;
try
{
    Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
    var adapterInfo = NvidiaAdapter.Read(adapter);
    string model = worker ? args[3] : args[0];
    if (!Path.IsPathFullyQualified(model)) throw new ArgumentException("Model directory must be absolute.");
    string evidence = Path.GetFullPath(worker ? args[1]["--profiles=".Length..] : args[2]);
    if (Path.Exists(evidence)) throw new IOException("Evidence directory must be new.");
    using var input = Console.OpenStandardInput();
    var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
    const int maximumFrame = 1024 * 1024;
    if (worker)
    {
        // Existing v1 contract: no new UI, socket, file executor or CPU fallback.
        using var output = Console.OpenStandardOutput();
        byte[] header = new byte[4];
        int sequence = 0;
        while (input.Read(header, 0, 1) != 0)
        {
            input.ReadExactly(header.AsSpan(1));
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is < 1 or > maximumFrame) throw new InvalidDataException("Invalid worker frame length.");
            byte[] bytes = new byte[length];
            input.ReadExactly(bytes);
            var envelope = JsonSerializer.Deserialize<Probe.WorkerRequest>(bytes, json);
            if (envelope is null || envelope.ProtocolVersion != Probe.WorkerProtocolVersion || envelope.Request is null ||
                envelope.ModelManifestSha256 is not { Length: 64 } expectedHash || expectedHash.Any(c => !Uri.IsHexDigit(c)))
                throw new InvalidDataException("Invalid worker protocol or model identity.");
            Probe.Validate(envelope.Request);
            if (!Probe.ModelManifestHash(model).Equals(expectedHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Worker model does not match the request.");
            // Lazy runtime/model loading: malformed frames never initialize sessions.
            environment ??= OrtEnv.Instance();
            string profiles = Path.Combine(evidence, (++sequence).ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(profiles);
            var result = DirectMlModel.RunVerified(envelope.Request, null, model, adapter, profiles, expectedHash);
            byte[] reply = JsonSerializer.SerializeToUtf8Bytes(new Probe.WorkerReply(Probe.WorkerProtocolVersion, expectedHash, result), json);
            if (reply.Length > maximumFrame) throw new InvalidDataException("Worker reply exceeds 1 MiB.");
            BinaryPrimitives.WriteInt32LittleEndian(header, reply.Length);
            output.Write(header);
            output.Write(reply);
            output.Flush();
            using var process = Process.GetCurrentProcess();
            Console.Error.WriteLine("DIRECTML_WORKER_REQUEST: " + JsonSerializer.Serialize(new
            { sequence, adapter = adapterInfo, hostPeakWorkingSetBytes = process.PeakWorkingSet64 }));
        }
    }
    else
    {
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = input.Read(buffer)) > 0)
        {
            if (bytes.Length + count > maximumFrame) throw new InvalidDataException("Request exceeds 1 MiB.");
            bytes.Write(buffer, 0, count);
        }
        var selected = JsonSerializer.Deserialize<ProbeInput>(bytes.ToArray(), json) ?? throw new InvalidDataException("Missing request.");
        if (selected.Request is null) throw new InvalidDataException("Missing request.");
        Probe.Validate(selected.Request);
        var clock = Stopwatch.StartNew();
        environment = OrtEnv.Instance();
        string hash = Probe.ModelManifestHash(model);
        Directory.CreateDirectory(evidence);
        var result = DirectMlModel.RunVerified(selected.Request, selected.Tensors, model, adapter, evidence, hash);
        using var process = Process.GetCurrentProcess();
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            success = true, provider = "DirectML", adapter = adapterInfo, modelManifestSha256 = hash,
            inputMode = selected.Tensors is null ? "shared-production-tokenizer" : "supplied-frozen-tensors",
            scope = "Experimental NVIDIA inference; host tokenization, embedding reads and postprocessing; no automatic selection or release acceptance.",
            totalMs = clock.ElapsedMilliseconds, hostPeakWorkingSetBytes = process.PeakWorkingSet64, result
        }, json));
    }
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine(error);
    return 1;
}
finally { environment?.Dispose(); }

internal sealed record ProbeInput(Probe.Request Request, Probe.Tensors? Tensors);
