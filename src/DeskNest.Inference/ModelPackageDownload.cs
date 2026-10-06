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
    public static async Task<string> InstallAsync(string cacheRoot, IProgress<int>? progress = null, CancellationToken cancellationToken = default,
        string? destinationRoot = null, IReadOnlyList<string>? reusableCacheRoots = null)
    {
        if (destinationRoot is not null)
        {
            if (!Path.IsPathFullyQualified(destinationRoot)) throw new ArgumentException("Model installation requires an absolute destination.");
            LocalModelInstaller.RequirePlainAncestors(destinationRoot);
        }
        var downloadProgress = new ScaledProgress(progress, 0, 70);
        if (reusableCacheRoots is not null)
        {
            var roots = reusableCacheRoots.Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal).ToArray();
            bool copied = false;
            // Prefer a complete verified cache to a partial one, even if it is in a legacy location.
            foreach (bool partial in new[] { false, true })
            {
                foreach (string previous in roots)
                    if (await CopyCacheAsync(previous, cacheRoot, partial, downloadProgress, cancellationToken).ConfigureAwait(false)) { copied = true; break; }
                if (copied) break;
            }
        }
        string archive = await DownloadAsync(cacheRoot, downloadProgress, cancellationToken).ConfigureAwait(false);
        return await LocalModelInstaller.InstallArchiveAsync(archive, destinationRoot ?? cacheRoot, new ScaledProgress(progress, 70, 30), cancellationToken).ConfigureAwait(false);
    }

    private static string ArchivePath(string root) => Path.Combine(root, ".model-download-" + LocalModelInstaller.ArchiveSha256 + ".partial");

    /// <summary>Copies only a complete pinned cache. Never overwrites a destination or removes old/partial files.</summary>
    public static Task<bool> CopyVerifiedCacheAsync(string sourceRoot, string destinationRoot,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default) =>
        CopyCacheAsync(sourceRoot, destinationRoot, false, progress, cancellationToken);

    /// <summary>Copies resumable partial bytes plus their strong ETag. These bytes are not a verified model.</summary>
    public static Task<bool> CopyPartialCacheAsync(string sourceRoot, string destinationRoot,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default) =>
        CopyCacheAsync(sourceRoot, destinationRoot, true, progress, cancellationToken);

    private static async Task<bool> CopyCacheAsync(string sourceRoot, string destinationRoot, bool partial,
        IProgress<int>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(sourceRoot) || !Path.IsPathFullyQualified(destinationRoot))
            throw new ArgumentException("Model caches must use absolute filesystem paths.");
        string sourcePath = ArchivePath(Path.GetFullPath(sourceRoot));
        string destinationPath = ArchivePath(Path.GetFullPath(destinationRoot));
        LocalModelInstaller.RequirePlainAncestors(sourcePath);
        LocalModelInstaller.RequirePlainAncestors(destinationPath);
        string destinationTag = destinationPath + ".etag";
        LocalModelInstaller.RequirePlainAncestors(destinationTag);
        if (File.Exists(destinationPath) || Directory.Exists(destinationPath) || File.Exists(destinationTag) || Directory.Exists(destinationTag) ||
            !File.Exists(sourcePath)) return false;
        // Cooperating downloaders own the data file and its ETag under this same exclusive lock.
        await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.None, 131072, FileOptions.Asynchronous);
        long sourceLength = source.Length;
        EntityTagHeaderValue? etag = null;
        if (partial)
        {
            if (sourceLength <= 0 || sourceLength >= LocalModelInstaller.ArchiveBytes) return false;
            etag = await ReadStrongETagAsync(sourcePath + ".etag", cancellationToken).ConfigureAwait(false);
            if (etag is null) return false;
        }
        else if (sourceLength != LocalModelInstaller.ArchiveBytes || !await HasPinnedHashAsync(source, cancellationToken).ConfigureAwait(false)) return false;
        LocalModelInstaller.RequirePlainAncestors(destinationPath);
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.ReadWrite,
            FileShare.None, 131072, FileOptions.Asynchronous);
        source.Position = 0;
        byte[] buffer = new byte[131072];
        long written = 0;
        int reported = -1;
        while (written < sourceLength)
        {
            int count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, sourceLength - written)), cancellationToken).ConfigureAwait(false);
            if (count == 0) throw new InvalidDataException("The source model cache changed during copying.");
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
            written += count;
            int percent = (int)(written * 99 / LocalModelInstaller.ArchiveBytes);
            if (percent != reported) { progress?.Report(percent); reported = percent; }
        }
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        if (!partial && !await HasPinnedHashAsync(destination, cancellationToken).ConfigureAwait(false))
            throw new InvalidDataException("The copied model cache failed verification; both copies are retained.");
        if (etag is not null)
        {
            LocalModelInstaller.RequirePlainAncestors(destinationTag);
            await using var tag = new FileStream(destinationTag, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
            await tag.WriteAsync(System.Text.Encoding.UTF8.GetBytes(etag.ToString()), cancellationToken).ConfigureAwait(false);
            await tag.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        progress?.Report((int)(sourceLength * 100 / LocalModelInstaller.ArchiveBytes));
        return true;
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
        string path = ArchivePath(cacheRoot);
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
            EntityTagHeaderValue? etag = await ReadStrongETagAsync(etagPath, token).ConfigureAwait(false);
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

    private static async Task<EntityTagHeaderValue?> ReadStrongETagAsync(string path, CancellationToken token)
    {
        LocalModelInstaller.RequirePlainAncestors(path);
        if (!File.Exists(path)) return null;
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (file.Length is <= 0 or > 1024) return null;
        byte[] bytes = new byte[(int)file.Length];
        await file.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        return EntityTagHeaderValue.TryParse(System.Text.Encoding.UTF8.GetString(bytes), out var tag) && !tag.IsWeak && tag.Tag != "*"
            ? tag : null;
    }

    private static async Task<bool> HasPinnedHashAsync(FileStream file, CancellationToken token)
    {
        file.Position = 0;
        return Convert.ToHexString(await SHA256.HashDataAsync(file, token).ConfigureAwait(false))
            .Equals(LocalModelInstaller.ArchiveSha256, StringComparison.OrdinalIgnoreCase);
    }
}
