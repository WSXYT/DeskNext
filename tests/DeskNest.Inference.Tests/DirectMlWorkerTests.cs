using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace DeskNest.Inference.Tests;

// Opt-in hardware checks only. The normal suite never starts a GPU or downloads a model.
public sealed class DirectMlWorkerTests(ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static string Required(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException("Missing " + name);

    [DirectMlFact]
    public async Task ExistingClientReusesVersionedGpuWorkerAndRetiresItOnCancellation()
    {
        string model = Required("DESKNEXT_DIRECTML_MODEL"), probe = Required("DESKNEXT_DIRECTML_PROBE");
        string cases = Required("DESKNEXT_DIRECTML_CASES");
        string evidence = Path.Combine(Required("DESKNEXT_DIRECTML_EVIDENCE"), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(evidence);
        int starts = 0;
        ProcessStartInfo Start()
        {
            var start = new ProcessStartInfo(probe);
            start.ArgumentList.Add("--adapter=" + Required("DESKNEXT_DIRECTML_ADAPTER"));
            start.ArgumentList.Add("--profiles=" + Path.Combine(evidence, "worker-" + ++starts));
            return start; // LocalPreviewSession appends the existing worker verb/protocol itself.
        }
        await using var session = new LocalPreviewSession(Start);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        int pressureRefused = 0;
        using var pressure = new Timer(_ =>
        {
            var memory = new NativeMemory { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMemory>() };
            if (GlobalMemoryStatusEx(ref memory) && (memory.AvailablePhysical < 192UL * 1024 * 1024 || memory.AvailablePageFile < 768UL * 1024 * 1024) &&
                Interlocked.CompareExchange(ref pressureRefused, 1, 0) == 0)
            {
                File.WriteAllText(Path.Combine(evidence, "memory-refusal.json"), JsonSerializer.Serialize(new
                { freeMiB = memory.AvailablePhysical / 1048576, freeCommitMiB = memory.AvailablePageFile / 1048576, success = false }));
                deadline.Cancel(); // The shared client kills/drains only its own worker.
            }
        }, null, 0, 100);
        int? firstPid = null;
        Probe.Request? last = null;
        for (int i = 0; i < 3; i++)
        {
            using var data = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(cases, i + "-input.json")));
            var request = data.RootElement.GetProperty("request").Deserialize<Probe.Request>(Json)!;
            var expected = JsonSerializer.Deserialize<Probe.Result>(await File.ReadAllTextAsync(Path.Combine(cases, i + "-reference.json")), Json)!;
            var clock = Stopwatch.StartNew();
            var result = await session.RunAsync(model, request, deadline.Token);
            Assert.Equal(JsonSerializer.Serialize(expected.Tensors, Json), JsonSerializer.Serialize(result.Tensors, Json));
            Assert.Equal(expected.Choice, result.Choice);
            Assert.Equal(expected.Action, result.Action);
            Assert.Equal(expected.Logits.Length, result.Logits.Length);
            double logitDelta = expected.Logits.Zip(result.Logits, (a, b) => Math.Abs((double)a - b)).Max();
            double probabilityDelta = expected.Probabilities.Zip(result.Probabilities, (a, b) => Math.Abs(a - b)).Max();
            Assert.InRange(logitDelta, 0, 1e-4);
            Assert.InRange(probabilityDelta, 0, 1e-4);
            firstPid ??= session.WorkerProcessId;
            Assert.Equal(firstPid, session.WorkerProcessId);
            Assert.Equal(1, starts);
            await File.WriteAllTextAsync(Path.Combine(evidence, i + "-result.json"), JsonSerializer.Serialize(result, Json));
            output.WriteLine($"GPU_CLIENT_CASE: case={i}, samePid={firstPid}, elapsedMs={clock.ElapsedMilliseconds}, logitDelta={logitDelta}, probabilityDelta={probabilityDelta}");
            last = request;
        }
        // Cancel during the next real request; completed records remain intact. No automatic restart/fallback.
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        Task<Probe.Result> pending = session.RunAsync(model, last! with { RequestId = "cancelled-gpu-request" }, cancel.Token);
        await Task.Delay(50, deadline.Token);
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Null(session.WorkerProcessId);
        Assert.Equal(1, starts);
        await session.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => session.RunAsync(model, last!));
        output.WriteLine("GPU_CLIENT_VERIFIED: v1 hash-bound frames; 3 raw requests; one NVIDIA process; cancellation drained; no CPU retry. Evidence=" + evidence);
    }

    [DirectMlFact]
    public async Task InvalidWorkerFramesRefuseBeforeGraphExecution()
    {
        string probe = Required("DESKNEXT_DIRECTML_PROBE");
        string root = Path.Combine(Required("DESKNEXT_DIRECTML_EVIDENCE"), "refusals-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var request = new Probe.Request("refused", 0, "report.txt", "Classify", [new("doc", "Documents"), new(Probe.Ambiguous, "Unclear"), new(Probe.Insufficient, "None")]);
        byte[] Frame(object value)
        {
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(value, Json), framed = new byte[body.Length + 4];
            BinaryPrimitives.WriteInt32LittleEndian(framed, body.Length);
            body.CopyTo(framed, 4);
            return framed;
        }
        byte[][] frames = [
            [1, 0, 16, 0], // > 1 MiB, refused without body allocation/model reads
            Frame(new Probe.WorkerRequest(99, new string('0', 64), request)),
            Frame(new Probe.WorkerRequest(1, new string('0', 64), request))
        ];
        for (int i = 0; i < frames.Length; i++)
        {
            string profiles = Path.Combine(root, i.ToString());
            var start = new ProcessStartInfo(probe) { RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (string arg in new[] { "--adapter=" + Required("DESKNEXT_DIRECTML_ADAPTER"), "--profiles=" + profiles,
                "--inference-worker", Required("DESKNEXT_DIRECTML_MODEL"), "--protocol=1" }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start)!;
            try
            {
                var errors = process.StandardError.ReadToEndAsync();
                var results = process.StandardOutput.ReadToEndAsync();
                await process.StandardInput.BaseStream.WriteAsync(frames[i]);
                process.StandardInput.Close();
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(1, process.ExitCode);
                Assert.Equal(string.Empty, await results);
                Assert.Contains("InvalidDataException", await errors);
                Assert.False(Directory.Exists(profiles));
            }
            finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
        }
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct NativeMemory
    {
        public uint Length, Load;
        public ulong TotalPhysical, AvailablePhysical, TotalPageFile, AvailablePageFile, TotalVirtual, AvailableVirtual, AvailableExtendedVirtual;
    }
    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref NativeMemory memory);
}

public sealed class DirectMlFactAttribute : FactAttribute
{
    public DirectMlFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || new[] { "PROBE", "MODEL", "CASES", "EVIDENCE", "ADAPTER" }
            .Any(name => string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DESKNEXT_DIRECTML_" + name))))
            Skip = "Explicit NVIDIA DirectML fixture required; a skip is not GPU execution evidence.";
    }
}
