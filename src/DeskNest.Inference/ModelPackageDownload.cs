using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace DeskNest.Inference;

/// <summary>One pinned HTTPS artifact; interrupted downloads resume only with a strong ETag and exact range.</summary>
public static class ModelPackageDownload
{
    public const string DownloadUrl = "https://github.com/WSXYT/DeskNext/releases/download/model-laya-multilingual-fp32-v1/desknext-laya-multilingual-fp32-v1.zip";
    private static readonly HttpClient Client = new(new HttpClientHandler { MaxAutomaticRedirections = 5 })
        { Timeout = TimeSpan.FromSeconds(60) };

    /// <summary>Downloads then uses the same offline installer. Activation remains an explicit settings save.</summary>
    public static async Task<string> InstallAsync(string cacheRoot, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        string archive = await DownloadAsync(cacheRoot, new ScaledProgress(progress, 0, 70), cancellationToken).ConfigureAwait(false);
        return await LocalModelInstaller.InstallArchiveAsync(archive, cacheRoot, new ScaledProgress(progress, 70, 30), cancellationToken).ConfigureAwait(false);
    }

    private sealed class ScaledProgress(IProgress<int>? target, int start, int range) : IProgress<int>
    {
        public void Report(int value) => target?.Report(start + value * range / 100);
    }

    public static Task<string> DownloadAsync(string cacheRoot, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default) => DownloadAsync(Client, cacheRoot, progress, cancellationToken);

    // Client injection is for protocol checks; callers cannot override the pinned URL, size or digest.
    public static async Task<string> DownloadAsync(HttpClient client, string cacheRoot, IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(cacheRoot)) throw new ArgumentException("Model cache must be an absolute path.", nameof(cacheRoot));
        cacheRoot = Path.GetFullPath(cacheRoot);
        string path = Path.Combine(cacheRoot, ".model-download-" + LocalModelInstaller.ArchiveSha256 + ".partial");
        string etagPath = path + ".etag";
        LocalModelInstaller.RequirePlainAncestors(path);
        LocalModelInstaller.RequirePlainAncestors(etagPath);
        Directory.CreateDirectory(cacheRoot);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(45));
        var token = deadline.Token;
        // Exclusive ownership also serializes the associated ETag. These are download cache files, not active models.
        await using var file = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
            131072, FileOptions.Asynchronous);
        try
        {
            if (file.Length == LocalModelInstaller.ArchiveBytes && await HasPinnedHashAsync(file, token).ConfigureAwait(false))
            {
                progress?.Report(100);
                return path;
            }
            EntityTagHeaderValue? etag = null;
            if (File.Exists(etagPath) && new FileInfo(etagPath).Length is > 0 and <= 1024)
                EntityTagHeaderValue.TryParse(await File.ReadAllTextAsync(etagPath, token).ConfigureAwait(false), out etag);
            long offset = file.Length is > 0 and < LocalModelInstaller.ArchiveBytes && etag is { IsWeak: false } && etag.Tag != "*"
                ? file.Length : 0;
            using var request = new HttpRequestMessage(HttpMethod.Get, DownloadUrl);
            request.Headers.AcceptEncoding.ParseAdd("identity");
            if (offset > 0)
            {
                request.Headers.Range = new RangeHeaderValue(offset, null);
                request.Headers.IfRange = new RangeConditionHeaderValue(etag!);
            }
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri?.Scheme != Uri.UriSchemeHttps || response.Content.Headers.ContentEncoding.Count != 0)
                throw new InvalidDataException("The model response must use HTTPS and unencoded archive bytes.");
            if (response.StatusCode == HttpStatusCode.PartialContent)
            {
                var range = response.Content.Headers.ContentRange;
                if (offset == 0 || range is null || range.Unit != "bytes" || range.From != offset ||
                    range.To != LocalModelInstaller.ArchiveBytes - 1 || range.Length != LocalModelInstaller.ArchiveBytes ||
                    response.Headers.ETag is not { IsWeak: false } returnedTag || !returnedTag.Equals(etag))
                    throw new InvalidDataException("Model resume headers do not match the retained download.");
            }
            else if (response.StatusCode == HttpStatusCode.OK) offset = 0; // Server declined Range: replace partial bytes, never append.
            else throw new InvalidDataException("Unexpected model download status.");
            if (response.Content.Headers.ContentLength is long length && length != LocalModelInstaller.ArchiveBytes - offset)
                throw new InvalidDataException("Model download length differs from the pinned release.");
            if (offset == 0)
            {
                file.SetLength(0);
                var returnedTag = response.Headers.ETag;
                string tag = returnedTag is { IsWeak: false } && returnedTag.Tag != "*" && returnedTag.Tag.Length <= 1024
                    ? returnedTag.ToString() : string.Empty;
                LocalModelInstaller.RequirePlainAncestors(etagPath);
                await File.WriteAllTextAsync(etagPath, tag, token).ConfigureAwait(false);
            }
            file.Position = offset;
            progress?.Report((int)(offset * 100 / LocalModelInstaller.ArchiveBytes));
            await using var input = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            byte[] buffer = new byte[131072];
            long received = offset;
            int reported = -1;
            int count;
            while ((count = await ReadWithIdleTimeoutAsync(input, buffer, token).ConfigureAwait(false)) != 0)
            {
                if (count > LocalModelInstaller.ArchiveBytes - received)
                    throw new InvalidDataException("Model download exceeds the pinned size.");
                await file.WriteAsync(buffer.AsMemory(0, count), token).ConfigureAwait(false);
                received += count;
                int percent = (int)(received * 100 / LocalModelInstaller.ArchiveBytes);
                if (percent != reported) { progress?.Report(percent); reported = percent; }
            }
            await file.FlushAsync(token).ConfigureAwait(false);
            if (received != LocalModelInstaller.ArchiveBytes)
                throw new InvalidDataException("Model download interrupted; retry to resume when the server supports it.");
            if (!await HasPinnedHashAsync(file, token).ConfigureAwait(false))
                throw new InvalidDataException("Downloaded model differs from the application-pinned release; retry starts a fresh download.");
            return path;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(deadline.IsCancellationRequested
                ? "Model download exceeded 45 minutes. Retained data can be retried."
                : "Model server did not respond before the network timeout. Retained data can be retried.");
        }
    }

    private static async Task<int> ReadWithIdleTimeoutAsync(Stream input, byte[] buffer, CancellationToken token)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(token);
        idle.CancelAfter(TimeSpan.FromSeconds(60));
        try { return await input.ReadAsync(buffer, idle.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            throw new TimeoutException("Model download received no data for 60 seconds. Retry to resume retained data.");
        }
    }

    private static async Task<bool> HasPinnedHashAsync(FileStream file, CancellationToken token)
    {
        file.Position = 0;
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false))
            .Equals(LocalModelInstaller.ArchiveSha256, StringComparison.OrdinalIgnoreCase);
    }
}
