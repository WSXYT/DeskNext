using Xunit;
using Xunit.Abstractions;

namespace DeskNest.Inference.Tests;

public sealed class LocalModelInstallerTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        "DeskNext.ModelInstall-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public async Task InvalidPackagePreservesInputAndExistingModel()
    {
        string package = Path.Combine(_root, "invalid.zip");
        await File.WriteAllTextAsync(package, "not a supported model package");
        string cache = Directory.CreateDirectory(Path.Combine(_root, "cache")).FullName;
        string existing = Path.Combine(cache, "existing-model");
        await File.WriteAllTextAsync(existing, "preserve");
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalModelInstaller.InstallArchiveAsync(package, cache));
        Assert.Equal("not a supported model package", await File.ReadAllTextAsync(package));
        Assert.Equal("preserve", await File.ReadAllTextAsync(existing));
        Assert.Empty(Directory.GetDirectories(cache));
    }

    [Fact]
    public async Task CancellationBeforeStartCreatesNoCache()
    {
        string cache = Path.Combine(_root, "cancelled-cache");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => LocalModelInstaller.InstallArchiveAsync(
            Path.Combine(_root, "missing.zip"), cache, cancellationToken: new CancellationToken(true)));
        Assert.False(Directory.Exists(cache));
    }

    [Fact]
    public async Task RelativePathsAreNotResolvedAgainstTheProcessDirectory()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => LocalModelInstaller.InstallArchiveAsync("relative.zip", _root));
        await Assert.ThrowsAsync<ArgumentException>(() => LocalModelInstaller.InstallArchiveAsync(Path.Combine(_root, "missing.zip"), "relative-cache"));
    }

    [LocalModelPackageFact]
    public async Task PinnedPackageInstallsBesideExistingModelWithoutChangingInput()
    {
        string package = Environment.GetEnvironmentVariable("DESKNEXT_TEST_MODEL_ARCHIVE")!;
        var before = new FileInfo(package);
        long length = before.Length;
        DateTime written = before.LastWriteTimeUtc;
        string cache = Directory.CreateDirectory(Path.Combine(_root, "cache")).FullName;
        string existing = Path.Combine(cache, "existing-model");
        await File.WriteAllTextAsync(existing, "preserve");
        string installed = await LocalModelInstaller.InstallArchiveAsync(package, cache);
        Assert.Equal(cache, Path.GetDirectoryName(installed));
        Assert.StartsWith("laya-multilingual-fp32-", Path.GetFileName(installed));
        Assert.Equal(10, Directory.GetFiles(installed).Length);
        Assert.True(File.Exists(Path.Combine(installed, "manifest.json")));
        Assert.Empty(Directory.GetDirectories(cache, ".model-unpack-*"));
        Assert.Equal("preserve", await File.ReadAllTextAsync(existing));
        Assert.Equal(length, new FileInfo(package).Length);
        Assert.Equal(written, File.GetLastWriteTimeUtc(package));
        output.WriteLine("MODEL_PACKAGE_INSTALL_VERIFIED: pinned archive, manifest and model hashes; old model and input preserved; activation not performed.");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

public sealed class LocalModelPackageFactAttribute : FactAttribute
{
    public LocalModelPackageFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DESKNEXT_TEST_MODEL_ARCHIVE")))
            Skip = "Requires the separately supplied pinned model package; no automatic download.";
    }
}
