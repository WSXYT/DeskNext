using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Xunit;
using Xunit.Abstractions;

namespace DeskNest.Inference.Tests;

public sealed class ModelPackageDownloadTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "DeskNext.ModelDownload-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("resume")]
    [InlineData("range-ignored")]
    [InlineData("wrong-range")]
    [InlineData("changed-etag")]
    [InlineData("unavailable")]
    public async Task RetryValidatesRangeAndEntityBeforeWriting(string mode)
    {
        using var first = new HttpClient(new Handler(request => Reply(request, HttpStatusCode.OK, "abc")));
        await Assert.ThrowsAsync<InvalidDataException>(() => ModelPackageDownload.DownloadAsync(first, _root));
        string partial = Directory.GetFiles(_root, "*.partial").Single();
        Assert.Equal("abc", await File.ReadAllTextAsync(partial));
        using var retry = new HttpClient(new Handler(request =>
        {
            Assert.Equal(ModelPackageDownload.DownloadUrl, request.RequestUri!.AbsoluteUri);
            Assert.Equal(3, request.Headers.Range!.Ranges.Single().From);
            Assert.Equal("\"release-a\"", request.Headers.IfRange!.EntityTag!.Tag);
            var status = mode == "unavailable" ? HttpStatusCode.ServiceUnavailable :
                mode == "range-ignored" ? HttpStatusCode.OK : HttpStatusCode.PartialContent;
            var response = Reply(request, status, "def");
            if (status == HttpStatusCode.PartialContent)
            {
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(mode == "wrong-range" ? 4 : 3,
                    LocalModelInstaller.ArchiveBytes - 1, LocalModelInstaller.ArchiveBytes);
                response.Content.Headers.ContentLength = LocalModelInstaller.ArchiveBytes - 3;
            }
            if (mode == "changed-etag") response.Headers.ETag = new EntityTagHeaderValue("\"release-b\"");
            return response;
        }));
        // These deliberately truncated bodies prove resume/restart/refusal without allocating a fake 813 MB model.
        if (mode == "unavailable") await Assert.ThrowsAsync<HttpRequestException>(() => ModelPackageDownload.DownloadAsync(retry, _root));
        else await Assert.ThrowsAsync<InvalidDataException>(() => ModelPackageDownload.DownloadAsync(retry, _root));
        Assert.Equal(mode == "resume" ? "abcdef" : mode == "range-ignored" ? "def" : "abc", await File.ReadAllTextAsync(partial));
        Assert.Empty(Directory.GetDirectories(_root));
    }

    [Fact]
    public async Task PrecancelledDownloadMakesNoRequestOrCache()
    {
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("No request expected.")));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModelPackageDownload.DownloadAsync(client, _root,
            cancellationToken: new CancellationToken(true)));
        Assert.False(Directory.Exists(_root));
    }

    [Fact]
    public async Task CacheCopyDoesNotCreateTargetsForMissingPartialOrCancelledSources()
    {
        string destination = Path.Combine(_root, "new-cache");
        string source = Path.Combine(_root, "old-cache");
        Assert.False(await ModelPackageDownload.CopyVerifiedCacheAsync(source, destination));
        Directory.CreateDirectory(source);
        string partial = Path.Combine(source, ".model-download-" + LocalModelInstaller.ArchiveSha256 + ".partial");
        await File.WriteAllTextAsync(partial, "incomplete");
        Assert.False(await ModelPackageDownload.CopyVerifiedCacheAsync(source, destination));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModelPackageDownload.CopyVerifiedCacheAsync(source, destination,
            cancellationToken: new CancellationToken(true)));
        await Assert.ThrowsAsync<ArgumentException>(() => ModelPackageDownload.CopyVerifiedCacheAsync("relative", destination));
        Assert.False(Directory.Exists(destination));
        Assert.Equal("incomplete", await File.ReadAllTextAsync(partial));
    }

    [LocalModelPackageFact]
    public async Task VerifiedCacheCopiesWithoutNetworkOverwriteOrDeletingOldBytes()
    {
        string source = Directory.CreateDirectory(Path.Combine(_root, "old-cache")).FullName;
        string name = ".model-download-" + LocalModelInstaller.ArchiveSha256 + ".partial";
        string original = Path.Combine(source, name);
        File.Copy(Environment.GetEnvironmentVariable("DESKNEXT_TEST_MODEL_ARCHIVE")!, original);
        string destination = Path.Combine(_root, "new-cache");
        Assert.True(await ModelPackageDownload.CopyVerifiedCacheAsync(source, destination));
        using var offline = new HttpClient(new Handler(_ => throw new InvalidOperationException("Migration must permit offline cache reuse.")));
        Assert.Equal(Path.Combine(destination, name), await ModelPackageDownload.DownloadAsync(offline, destination));
        Assert.Equal(original, await ModelPackageDownload.DownloadAsync(offline, source));
        string occupied = Directory.CreateDirectory(Path.Combine(_root, "occupied")).FullName;
        await File.WriteAllTextAsync(Path.Combine(occupied, name), "preserve existing partial");
        Assert.False(await ModelPackageDownload.CopyVerifiedCacheAsync(source, occupied));
        Assert.Equal("preserve existing partial", await File.ReadAllTextAsync(Path.Combine(occupied, name)));
        using var stop = new CancellationTokenSource();
        string interrupted = Path.Combine(_root, "interrupted");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModelPackageDownload.CopyVerifiedCacheAsync(source, interrupted,
            new InterruptDownload(stop), stop.Token));
        Assert.InRange(new FileInfo(Path.Combine(interrupted, name)).Length, 1, LocalModelInstaller.ArchiveBytes - 1);
        Assert.Equal(original, await ModelPackageDownload.DownloadAsync(offline, source));
        output.WriteLine("MODEL_CACHE_RELOCATION_VERIFIED: pinned bytes, offline reuse, retained original, collision refusal and cancellation.");
    }

    [OnlineModelDownloadFact]
    public async Task PublicArtifactCanResumeAfterCancellationAndBeReusedOffline()
    {
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ModelPackageDownload.DownloadAsync(_root, new InterruptDownload(stop), stop.Token));
        string partial = Directory.GetFiles(_root, "*.partial").Single();
        long retained = new FileInfo(partial).Length;
        Assert.InRange(retained, 1, LocalModelInstaller.ArchiveBytes - 1);
        string verified = await ModelPackageDownload.DownloadAsync(_root);
        Assert.Equal(partial, verified);
        Assert.Equal(LocalModelInstaller.ArchiveBytes, new FileInfo(verified).Length);
        using var offline = new HttpClient(new Handler(_ => throw new InvalidOperationException("Verified cache must not need a request.")));
        Assert.Equal(verified, await ModelPackageDownload.DownloadAsync(offline, _root));
        output.WriteLine($"MODEL_PACKAGE_DOWNLOAD_VERIFIED: public HTTPS, cancellation retained {retained} bytes, pinned SHA-256, offline cache reuse.");
    }

    private static HttpResponseMessage Reply(HttpRequestMessage request, HttpStatusCode status, string body)
    {
        var response = new HttpResponseMessage(status) { RequestMessage = request, Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body)) };
        response.Content.Headers.ContentLength = LocalModelInstaller.ArchiveBytes;
        response.Headers.ETag = new EntityTagHeaderValue("\"release-a\"");
        return response;
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request));
    }
    private sealed class InterruptDownload(CancellationTokenSource stop) : IProgress<int>
    {
        public void Report(int value) { if (value >= 1) stop.Cancel(); }
    }
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
}

public sealed class OnlineModelDownloadFactAttribute : FactAttribute
{
    public OnlineModelDownloadFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("DESKNEXT_TEST_MODEL_DOWNLOAD") != "1")
            Skip = "Explicit opt-in required for the approximately 813 MB public model download.";
    }
}
