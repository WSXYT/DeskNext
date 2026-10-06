using System.Diagnostics;
using Xunit;

namespace DeskNest.Inference.Tests;

public sealed class LocalPreviewSessionTests
{
    [Fact]
    public async Task SessionReusesOnlyMatchingModelAndRetiresOnCancelErrorIdleAndDisposal()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "DeskNext-session-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string manifest = Path.Combine(root, "manifest.json");
            await File.WriteAllTextAsync(manifest, "{}");
            string fixture = Path.Combine(root, "worker.mjs");
            // Framing/lifetime fixture only; this is not a model or native inference acceptance test.
            await File.WriteAllTextAsync(fixture, """
                let pending = Buffer.alloc(0);
                process.stdin.on('data', bytes => {
                  pending = Buffer.concat([pending, bytes]);
                  while (pending.length >= 4 && pending.length >= pending.readInt32LE(0) + 4) {
                    const size = pending.readInt32LE(0);
                    const envelope = JSON.parse(pending.subarray(4, 4 + size));
                    pending = pending.subarray(4 + size);
                    const r = envelope.request;
                    if (r.state === 'wait') continue;
                    const reply = Buffer.from(JSON.stringify({protocolVersion:r.state==='wrong'?99:1,
                      modelManifestSha256:envelope.modelManifestSha256,
                      result:{requestId:r.requestId,revision:r.revision,choice:r.candidates[0].id,
                        probabilities:r.candidates.map((_,i)=>i===0?1:0)}}));
                    const header = Buffer.alloc(4); header.writeInt32LE(reply.length);
                    process.stdout.write(header); process.stdout.write(reply);
                  }
                });
                """);
            int starts = 0;
            ProcessStartInfo Start()
            {
                starts++;
                var info = new ProcessStartInfo("node");
                info.ArgumentList.Add(fixture);
                return info;
            }
            Probe.Request Request(string state = "normal") => new(Guid.NewGuid().ToString("N"), starts, state, "Choose", [
                new("documents", "Documents"), new(Probe.Ambiguous, "Unknown"), new(Probe.Insufficient, "None")]);
            await using var session = new LocalPreviewSession(Start, TimeSpan.FromMilliseconds(500));
            var first = Request();
            Assert.Equal(first.RequestId, (await session.RunAsync(root, first)).RequestId);
            var second = Request();
            Assert.Equal(second.RequestId, (await session.RunAsync(root, second)).RequestId);
            Assert.Equal(1, starts);
            await File.WriteAllTextAsync(manifest, "{ }");
            await session.RunAsync(root, Request());
            Assert.Equal(2, starts);
            await Assert.ThrowsAsync<InvalidDataException>(() => session.RunAsync(root, Request("wrong")));
            Assert.Null(session.WorkerProcessId);
            using var stop = new CancellationTokenSource();
            var waiting = session.RunAsync(root, Request("wait"), stop.Token);
            await UntilAsync(() => session.WorkerProcessId is not null);
            await Assert.ThrowsAsync<InvalidOperationException>(() => session.RunAsync(root, Request()));
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
            Assert.Null(session.WorkerProcessId);
            await session.RunAsync(root, Request());
            int beforeIdle = starts;
            await UntilAsync(() => session.WorkerProcessId is null);
            Assert.Null(session.LastShutdownError);
            await session.RunAsync(root, Request());
            Assert.Equal(beforeIdle + 1, starts);
            var disposePending = session.RunAsync(root, Request("wait"));
            await session.DisposeAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => disposePending);
            Assert.Null(session.WorkerProcessId);
            await Assert.ThrowsAsync<ObjectDisposedException>(() => session.RunAsync(root, Request()));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task UntilAsync(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!ready()) await Task.Delay(20, timeout.Token);
    }
}
