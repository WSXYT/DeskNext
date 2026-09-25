using System.Text.Json;
using DeskNest.Core.Storage;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class ResilientJsonStoreTests
{
    private static int Deserialize(string json) => JsonDocument.Parse(json).RootElement.GetProperty("value").GetInt32();

    [Fact]
    public async Task LoadWithResultAsync_DistinguishesMissingFromCorruptPrimaryWithoutBackup()
    {
        using var root = new TempDirectory();
        string path = Path.Combine(root.Path, "journal.json");
        var missing = await ResilientJsonStore.LoadWithResultAsync(path, Deserialize, () => -1, "test");
        Assert.Equal(ResilientJsonLoadSource.DefaultMissing, missing.Source);
        Assert.Equal(-1, missing.Value);
        await File.WriteAllTextAsync(path, "{invalid");
        var failed = await ResilientJsonStore.LoadWithResultAsync(path, Deserialize, () => -1, "test");
        Assert.Equal(ResilientJsonLoadSource.DefaultAfterFailure, failed.Source);
        Assert.Equal(-1, failed.Value);
        Assert.Single(Directory.GetFiles(root.Path, "*.corrupt-*"));
    }

    [Fact]
    public async Task LoadWithResultAsync_RecoversBackupAfterPrimaryCorruption()
    {
        using var root = new TempDirectory();
        string path = Path.Combine(root.Path, "journal.json");
        await ResilientJsonStore.SaveAsync(path, "{\"value\":1}");
        await ResilientJsonStore.SaveAsync(path, "{\"value\":2}");
        await File.WriteAllTextAsync(path, "{invalid");
        var result = await ResilientJsonStore.LoadWithResultAsync(path, Deserialize, () => -1, "test");
        Assert.Equal(ResilientJsonLoadSource.Backup, result.Source);
        Assert.Equal(1, result.Value);
        Assert.Equal(1, Deserialize(await File.ReadAllTextAsync(path)));
        Assert.Single(Directory.GetFiles(root.Path, "*.corrupt-*"));
    }

    [Fact]
    public async Task LoadWithResultAsync_BothCorrupt_ReturnsFailureNotMissing()
    {
        using var root = new TempDirectory();
        string path = Path.Combine(root.Path, "journal.json");
        await File.WriteAllTextAsync(path, "{bad");
        await File.WriteAllTextAsync(ResilientJsonStore.GetBackupPath(path), "{bad");
        var result = await ResilientJsonStore.LoadWithResultAsync(path, Deserialize, () => -1, "test");
        Assert.Equal(ResilientJsonLoadSource.DefaultAfterFailure, result.Source);
        Assert.Equal(-1, result.Value);
        Assert.Equal(2, Directory.GetFiles(root.Path, "*.corrupt-*").Length);
    }

    [Fact]
    public async Task SaveAsync_ReplaceFailureDoesNotDestroyExistingPrimary()
    {
        using var root = new TempDirectory();
        string path = Path.Combine(root.Path, "journal.json");
        await File.WriteAllTextAsync(path, "original");
        await Assert.ThrowsAsync<IOException>(() => ResilientJsonStore.SaveAsync(path, "updated",
            (_, _, _, _) => throw new IOException("sharing violation"), _ => Task.CompletedTask));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(root.Path, "*.tmp"));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "DeskNest.Core.Tests", Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
