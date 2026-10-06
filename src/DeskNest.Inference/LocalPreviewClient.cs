using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;

namespace DeskNest.Inference;

/// <summary>One CPU preview through the existing framed worker; no file-operation authority.</summary>
public static class LocalPreviewClient
{
    // ponytail: cold-start one worker per preview; consider reuse only after measuring the latency.
    private const int MaximumFrame = 1024 * 1024;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Checks actual CPU worker loading and one valid inference, not classification accuracy.</summary>
    public static async Task CheckModelAsync(ProcessStartInfo start, string modelDirectory, CancellationToken cancellationToken = default)
    {
        var request = new Probe.Request(Guid.NewGuid().ToString("N"), 0, "Quarterly report.txt",
            "Classify the sample filename.", [
                new Probe.Candidate("documents", "Written documents and reports."),
                new Probe.Candidate(Probe.Ambiguous, "The filename is unclear."),
                new Probe.Candidate(Probe.Insufficient, "No category fits.")]);
        // Any validated distribution is acceptable: successful loading is not a quality decision.
        await RunAsync(start, modelDirectory, request, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<Probe.Result> RunAsync(ProcessStartInfo start, string modelDirectory,
        Probe.Request request, CancellationToken cancellationToken = default)
    {
        Probe.Validate(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(modelDirectory) || !Directory.Exists(modelDirectory))
            throw new DirectoryNotFoundException("Choose an existing local model bundle directory.");
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        if (payload.Length > MaximumFrame) throw new InvalidDataException("Request frame too large.");
        string expectedModelHash = Probe.ModelManifestHash(modelDirectory);
        payload = JsonSerializer.SerializeToUtf8Bytes(new Probe.WorkerRequest(Probe.WorkerProtocolVersion, expectedModelHash, request), Json);
        if (payload.Length > MaximumFrame) throw new InvalidDataException("Request frame too large.");
        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.RedirectStandardInput = start.RedirectStandardOutput = start.RedirectStandardError = true;
        // Keep the recognized verb: older two-argument hosts refuse the extra protocol argument
        // instead of interpreting an unknown verb as a request to open the normal GUI.
        start.ArgumentList.Add("--inference-worker");
        start.ArgumentList.Add(modelDirectory);
        start.ArgumentList.Add("--protocol=1");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        using var process = Process.Start(start) ?? throw new IOException("Cannot start the local CPU worker.");
        Task<string> errors = ReadErrorsAsync(process.StandardError, token);
        try
        {
            byte[] header = new byte[4];
            BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
            await process.StandardInput.BaseStream.WriteAsync(header, token).ConfigureAwait(false);
            await process.StandardInput.BaseStream.WriteAsync(payload, token).ConfigureAwait(false);
            process.StandardInput.Close(); // One request; EOF makes the existing worker exit normally.
            try { await process.StandardOutput.BaseStream.ReadExactlyAsync(header, token).ConfigureAwait(false); }
            catch (EndOfStreamException)
            {
                await process.WaitForExitAsync(token).ConfigureAwait(false);
                throw new InvalidDataException("Local model could not produce a result: " + await errors.ConfigureAwait(false));
            }
            int length = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (length is < 1 or > MaximumFrame) throw new InvalidDataException("Invalid worker response size.");
            byte[] response = new byte[length];
            await process.StandardOutput.BaseStream.ReadExactlyAsync(response, token).ConfigureAwait(false);
            if (await process.StandardOutput.BaseStream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0)
                throw new InvalidDataException("Unexpected trailing worker output.");
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            string error = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new IOException("Local CPU worker failed: " + error);
            var reply = JsonSerializer.Deserialize<Probe.WorkerReply>(response, Json)
                ?? throw new InvalidDataException("Empty worker reply.");
            ValidateReply(request, expectedModelHash, reply);
            if (!Probe.ModelManifestHash(modelDirectory).Equals(expectedModelHash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Selected model changed before the result could be accepted.");
            return reply.Result;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Local model preview exceeded two minutes.");
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch (InvalidOperationException) { } // Exited between the check and Kill.
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            try { await errors.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
    }

    public static void ValidateReply(Probe.Request request, string expectedModelHash, Probe.WorkerReply reply)
    {
        if (expectedModelHash is not { Length: 64 } || expectedModelHash.Any(c => !Uri.IsHexDigit(c)) ||
            reply.ProtocolVersion != Probe.WorkerProtocolVersion || reply.Result is null ||
            !string.Equals(reply.ModelManifestSha256, expectedModelHash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Worker protocol or model identity does not match this preview.");
        ValidateResult(request, reply.Result);
    }

    public static void ValidateResult(Probe.Request request, Probe.Result result)
    {
        if (result.RequestId != request.RequestId || result.Revision != request.Revision ||
            result.Probabilities is null || result.Probabilities.Length != request.Candidates.Length ||
            result.Probabilities.Any(p => !double.IsFinite(p) || p is < 0 or > 1) ||
            Math.Abs(result.Probabilities.Sum() - 1) > 1e-6)
            throw new InvalidDataException("Worker result does not match this request.");
        var ranked = Enumerable.Range(0, request.Candidates.Length)
            .OrderByDescending(i => result.Probabilities[i])
            .ThenBy(i => request.Candidates[i].Id, StringComparer.Ordinal).ToArray();
        if (result.Choice != request.Candidates[ranked[0]].Id)
            throw new InvalidDataException("Worker choice does not match its probabilities.");
    }

    private static async Task<string> ReadErrorsAsync(StreamReader reader, CancellationToken token)
    {
        char[] prefix = new char[4096];
        int count = await reader.ReadBlockAsync(prefix.AsMemory(), token).ConfigureAwait(false);
        await reader.BaseStream.CopyToAsync(Stream.Null, token).ConfigureAwait(false);
        return new string(prefix, 0, count);
    }
}
