using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless;
using DeskNest.App.Localization;
using DeskNest.Platform;

namespace DeskNest.App.Services;

// Developer-only, isolated workbench workflow. No selection persists to a user's workspace.
internal static class DirectMlPreviewSmoke
{
    internal static int Run(string[] args)
    {
        string? root = null;
        bool passed = false;
        long pressureFreeMiB = -1;
        var clock = Stopwatch.StartNew();
        var report = new Dictionary<string, object?>
        {
            ["Success"] = false, ["DirectMlPreviewVerified"] = false,
            ["LocalClassificationPreviewVerified"] = false, ["LocalWorkerReuseVerified"] = false,
            ["Scope"] = "Isolated headless production commands with an explicitly supplied NVIDIA worker; not normal App backend selection, native compositor, quality or release acceptance."
        };
        try
        {
            if (!OperatingSystem.IsWindows() || args.Length != 4) throw new ArgumentException("Use --directml-preview-smoke --gpu-worker=<absolute-exe> --local-model=<absolute-model> --gpu-adapter=<DXGI-index> on Windows.");
            string Value(string prefix) => args.Single(a => a.StartsWith(prefix, StringComparison.Ordinal))[prefix.Length..];
            string worker = PlatformFileActions.RequireExistingLocalPath(Value("--gpu-worker="));
            if (Path.GetFileName(worker) != "DeskNest.DirectMLProbe.exe") throw new ArgumentException("Use the explicitly built isolated NVIDIA worker.");
            string model = PlatformFileActions.RequireExistingLocalPath(Value("--local-model="));
            if (!Directory.Exists(model) || !int.TryParse(Value("--gpu-adapter="), out int adapter) || adapter is < 0 or > 15)
                throw new ArgumentException("Invalid model directory or DXGI adapter index.");
            Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal;
            root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "DeskNext.GpuPreview." + Guid.NewGuid().ToString("N"))).FullName;
            report["EvidenceDirectory"] = root;
            report["Worker"] = worker;
            report["AdapterIndex"] = adapter;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(75));
            using var pressure = new Timer(_ =>
            {
                var memory = new MemoryStatus { Length = (uint)Marshal.SizeOf<MemoryStatus>() };
                if (GlobalMemoryStatusEx(ref memory) && (memory.AvailablePhysical < 192UL * 1048576 || memory.AvailablePageFile < 768UL * 1048576))
                {
                    Interlocked.CompareExchange(ref pressureFreeMiB, (long)(memory.AvailablePhysical / 1048576), -1);
                    deadline.Cancel();
                }
            }, null, 0, 100);
            AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            var config = new ExperimentalNvidiaSession(worker, model, adapter, Path.Combine(root, "gpu-workers"));
            int starts = 0;
            ProcessStartInfo Start() { starts++; return config.CreateWorkerStart(); }
            HeadlessSmokeRunner.AwaitOnUIThread(HeadlessSmokeRunner.VerifyLocalClassificationPreviewAsync(root, model, Start, deadline.Token),
                "explicit NVIDIA workbench preview", 90);
            int graphProfiles = 0;
            foreach (string file in Directory.EnumerateFiles(root, "*.json", SearchOption.AllDirectories)
                .Where(p => Path.GetFileName(p).StartsWith("encoder_", StringComparison.Ordinal) || Path.GetFileName(p).StartsWith("head_", StringComparison.Ordinal)))
            {
                using var profile = JsonDocument.Parse(File.ReadAllText(file));
                var providers = profile.RootElement.EnumerateArray().Where(e => e.TryGetProperty("args", out var a) && a.TryGetProperty("provider", out _))
                    .Select(e => e.GetProperty("args").GetProperty("provider").GetString()).ToArray();
                if (!providers.Contains("DmlExecutionProvider") || providers.Contains("CPUExecutionProvider"))
                    throw new InvalidDataException("A completed graph lacks GPU-only execution evidence.");
                graphProfiles++;
            }
            if (graphProfiles < 8) throw new InvalidDataException("Expected four completed two-graph NVIDIA previews.");
            report["DirectMlGraphProfiles"] = graphProfiles;
            report["Success"] = report["DirectMlPreviewVerified"] = report["LocalClassificationPreviewVerified"] = report["LocalWorkerReuseVerified"] = true;
            report["WorkerStarts"] = starts;
            report["ProviderLabel"] = LocalizationManager.Instance["Classification.ExperimentalNvidia"];
            passed = true;
        }
        catch (Exception error) { report["Error"] = error.ToString(); }
        finally
        {
            // Retain only this isolated fixture/profiles for review, including on failure. No user files were used.
            report["ElapsedMs"] = clock.ElapsedMilliseconds;
            if (pressureFreeMiB >= 0) report["CancelledForLowMemoryMiB"] = pressureFreeMiB;
            if (root is not null) File.WriteAllText(Path.Combine(root, "result.json"), JsonSerializer.Serialize(report));
            Console.WriteLine("PROBE_RESULT_JSON:");
            Console.WriteLine(JsonSerializer.Serialize(report));
        }
        return passed ? 0 : 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatus
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatus memory);
}
