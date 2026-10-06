using System.Diagnostics;

namespace DeskNest.Inference;

/// <summary>One serialized, versioned CPU worker. Cancellation retires it; an idle worker releases its model.</summary>
public sealed class LocalPreviewSession : IAsyncDisposable
{
    private readonly Func<ProcessStartInfo> _start;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Timer _idleTimer;
    private readonly TimeSpan _idleTimeout;
    private Process? _process;
    private Task<string>? _errors;
    private string? _directory, _modelHash;
    private long _lastUsed;
    private int _disposed, _requestClaim, _processId;

    public int? WorkerProcessId => Volatile.Read(ref _processId) is var id && id != 0 ? id : null;
    public string? LastShutdownError { get; private set; }

    public LocalPreviewSession(Func<ProcessStartInfo> start, TimeSpan? idleTimeout = null)
    {
        _start = start ?? throw new ArgumentNullException(nameof(start));
        _idleTimeout = idleTimeout ?? TimeSpan.FromMinutes(1);
        if (_idleTimeout <= TimeSpan.Zero || _idleTimeout > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));
        _idleTimer = new Timer(state => { _ = ExpireAsync(); }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public async Task<Probe.Result> RunAsync(string modelDirectory, Probe.Request request, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        // No unbounded work queue. The UI already serializes its preview command; other callers get a busy error.
        if (Interlocked.CompareExchange(ref _requestClaim, 1, 0) != 0)
            throw new InvalidOperationException("The local CPU worker is busy.");
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            timeout.CancelAfter(TimeSpan.FromMinutes(2));
            var token = timeout.Token;
            byte[] payload = LocalPreviewClient.PrepareRequest(modelDirectory, request, token, out string hash);
            string directory = Path.GetFullPath(modelDirectory);
            await _gate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                _idleTimer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                if (_process is null || _process.HasExited || _directory != directory || _modelHash != hash)
                {
                    await CloseWorkerAsync(kill: false).ConfigureAwait(false);
                    _process = LocalPreviewClient.StartWorker(_start(), directory);
                    _errors = LocalPreviewClient.ReadErrorsAsync(_process.StandardError, CancellationToken.None);
                    _directory = directory;
                    _modelHash = hash;
                    Volatile.Write(ref _processId, _process.Id);
                    LastShutdownError = null;
                }
                await LocalPreviewClient.WriteRequestAsync(_process, payload, token).ConfigureAwait(false);
                Probe.WorkerReply reply;
                try { reply = await LocalPreviewClient.ReadReplyAsync(_process, token).ConfigureAwait(false); }
                catch (EndOfStreamException)
                {
                    await _process.WaitForExitAsync(token).ConfigureAwait(false);
                    throw new InvalidDataException("Local model could not produce a result: " + await _errors!.ConfigureAwait(false));
                }
                if (_process.HasExited && _process.ExitCode != 0)
                    throw new IOException("Local CPU worker failed: " + await _errors!.ConfigureAwait(false));
                LocalPreviewClient.ValidateReply(request, hash, reply);
                if (!Probe.ModelManifestHash(directory).Equals(hash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Selected model changed before the result could be accepted.");
                return reply.Result;
            }
            catch
            {
                _directory = null; // A failed request must never reuse a possibly desynchronized stream.
                await CloseWorkerAsync(kill: true).ConfigureAwait(false);
                throw;
            }
            finally
            {
                _lastUsed = Stopwatch.GetTimestamp();
                if (Volatile.Read(ref _disposed) == 0 && _process is not null)
                    _idleTimer.Change(_idleTimeout, Timeout.InfiniteTimeSpan);
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            throw new TimeoutException("Local model preview exceeded two minutes.");
        }
        finally { Volatile.Write(ref _requestClaim, 0); }
    }

    /// <summary>Drain the current request, then close the worker. Cancel that request first for immediate stopping.</summary>
    public async Task StopAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await CloseWorkerAsync(kill: false).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    private async Task ExpireAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) == 0 && _process is not null && Stopwatch.GetElapsedTime(_lastUsed) >= _idleTimeout)
                await CloseWorkerAsync(kill: false).ConfigureAwait(false);
        }
        catch (Exception error) { _directory = null; LastShutdownError = error.Message; }
        finally { _gate.Release(); }
    }

    private async Task CloseWorkerAsync(bool kill)
    {
        var process = _process;
        if (process is null) return;
        _directory = null;
        if (!process.HasExited)
        {
            if (!kill)
            {
                process.StandardInput.Close();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    LastShutdownError = "The local worker did not stop within five seconds and was terminated.";
                    kill = true;
                }
            }
            if (kill && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { }
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
        }
        string diagnostic = string.Empty;
        diagnostic = await _errors!.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        int exitCode = process.ExitCode;
        process.Dispose();
        _process = null;
        _errors = null;
        _modelHash = null;
        Volatile.Write(ref _processId, 0);
        if (!kill && exitCode != 0) throw new IOException("Local CPU worker did not exit cleanly: " + diagnostic);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lifetime.Cancel();
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            _idleTimer.Dispose();
            await CloseWorkerAsync(kill: false).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        // Keep the semaphore and cancellation source usable for already queued callers/idle callbacks.
    }
}
