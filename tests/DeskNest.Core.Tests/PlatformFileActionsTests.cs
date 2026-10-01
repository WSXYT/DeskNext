using DeskNest.Platform;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class PlatformFileActionsTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "DeskNest-preview-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public async Task TextAndDirectoryPreviewsAreBoundedAndReadOnly()
    {
        var file = Path.Combine(_root, "plain.txt");
        await File.WriteAllTextAsync(file, new string('a', 10_000));

        var preview = await PlatformFileActions.ReadPreviewAsync(file, maximumBytes: 128);

        Assert.Equal("text", preview.Kind);
        Assert.Equal(128, preview.Content.Length);
        Assert.True(preview.Truncated);
        Assert.Equal(10_000, new FileInfo(file).Length);

        await File.WriteAllTextAsync(Path.Combine(_root, "second.txt"), "unchanged");
        var directory = await PlatformFileActions.ReadPreviewAsync(_root, maximumEntries: 1);
        Assert.Equal("directory", directory.Kind);
        Assert.True(directory.Truncated);
        Assert.DoesNotContain(Environment.NewLine, directory.Content);
    }

    [Theory]
    [InlineData("binary.exe")]
    [InlineData("script.cmd")]
    [InlineData("page.html")]
    public async Task PreviewNeverLaunchesExecutableOrActiveContent(string name)
    {
        var file = Path.Combine(_root, name);
        await File.WriteAllTextAsync(file, "DO NOT EXECUTE");

        var preview = await PlatformFileActions.ReadPreviewAsync(file);

        Assert.Equal("metadata", preview.Kind);
        Assert.Empty(preview.Content);
        Assert.Equal("DO NOT EXECUTE", await File.ReadAllTextAsync(file));
    }

    [Fact]
    public async Task InvalidTextCancellationAndUriInputsAreRefused()
    {
        var file = Path.Combine(_root, "binary.txt");
        await File.WriteAllBytesAsync(file, [0xff, 0xff, 0, 1]);

        Assert.Equal("metadata", (await PlatformFileActions.ReadPreviewAsync(file)).Kind);
        await Assert.ThrowsAsync<ArgumentException>(() => PlatformFileActions.OpenAsync("https://example.com"));
        await Assert.ThrowsAsync<ArgumentException>(() => PlatformFileActions.ReadPreviewAsync("relative.txt"));

        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PlatformFileActions.ReadPreviewAsync(file, stop.Token));
    }

    [Fact]
    public async Task FilesystemRootCannotRevealAContainingFolder()
    {
        await Assert.ThrowsAsync<NotSupportedException>(
            () => PlatformFileActions.RevealAsync(Path.GetPathRoot(_root)!));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }
}
