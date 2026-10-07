using System.Diagnostics;
using DeskNest.Platform;

namespace DeskNest.App.Services;

// Explicit launch arguments only. Never serialized, auto-detected, or read from workspace/Flow data.
internal sealed class ExperimentalNvidiaSession
{
    internal string WorkerPath { get; }
    internal string ModelDirectory { get; }
    internal int Adapter { get; }
    private readonly string _profiles;
    private int _starts;

    internal ExperimentalNvidiaSession(string worker, string model, int adapter, string? evidenceRoot = null)
    {
        _profiles = evidenceRoot ?? Path.Combine(Path.GetTempPath(), "DeskNext.GpuSession." + Guid.NewGuid().ToString("N"));
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Experimental DirectML requires Windows.");
        WorkerPath = PlatformFileActions.RequireExistingLocalPath(worker);
        ModelDirectory = PlatformFileActions.RequireExistingLocalPath(model);
        if (Path.GetFileName(WorkerPath) != "DeskNest.DirectMLProbe.exe" || !Directory.Exists(ModelDirectory) || adapter is < 0 or > 15)
            throw new ArgumentException("Supply the trusted isolated NVIDIA worker, model directory and DXGI adapter index.");
        Adapter = adapter;
    }

    internal static ExperimentalNvidiaSession Parse(string[] args)
    {
        if (args.Length != 4 || !args.Contains("--experimental-nvidia", StringComparer.Ordinal))
            throw new ArgumentException("Use --experimental-nvidia --gpu-worker=<absolute-exe> --gpu-model=<absolute-model> --gpu-adapter=<DXGI-index>.");
        string Value(string prefix) => args.Single(a => a.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];
        if (!int.TryParse(Value("--gpu-adapter="), out int adapter)) throw new ArgumentException("Invalid DXGI adapter index.");
        return new ExperimentalNvidiaSession(Value("--gpu-worker="), Value("--gpu-model="), adapter);
    }

    internal ProcessStartInfo CreateWorkerStart()
    {
        // Revalidate paths on start; native-runtime/asset checks remain in the explicitly supplied worker.
        var start = new ProcessStartInfo(PlatformFileActions.RequireExistingLocalPath(WorkerPath));
        PlatformFileActions.RequireExistingLocalPath(ModelDirectory);
        start.ArgumentList.Add("--adapter=" + Adapter);
        start.ArgumentList.Add("--profiles=" + Path.Combine(_profiles, Interlocked.Increment(ref _starts).ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return start;
    }
}
