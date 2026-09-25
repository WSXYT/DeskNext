using System.Text.Json;
using DeskNest.Core.Storage;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class DesktopOrganizationTransactionTests
{
    [Fact]
    public async Task ExecuteMovesFileAndRemovesJournalArtifacts()
    {
        using var root = new TempDirectory();
        string source = Path.Combine(root.Path, "source.txt");
        string destination = Path.Combine(root.Path, "nested", "destination.txt");
        string journal = Path.Combine(root.Path, "operation.json");
        await File.WriteAllTextAsync(source, "content");

        var result = await new DesktopOrganizationTransaction(journal)
            .ExecuteAsync([new OrganizationMove(source, destination)]);

        Assert.Equal(OrganizationTransactionStatus.Completed, result.Status);
        Assert.False(File.Exists(source));
        Assert.Equal("content", await File.ReadAllTextAsync(destination));
        Assert.False(File.Exists(journal));
        Assert.False(File.Exists(ResilientJsonStore.GetBackupPath(journal)));
    }

    [Fact]
    public async Task RecoveryRefusesChangedDestinationAndKeepsJournal()
    {
        using var root = new TempDirectory();
        string source = Path.Combine(root.Path, "source.txt");
        string destination = Path.Combine(root.Path, "destination.txt");
        string journal = Path.Combine(root.Path, "operation.json");
        await File.WriteAllTextAsync(destination, "original");
        var identity = FileIdentity.Capture(destination);
        var journalValue = new OrganizationRecoveryJournal(
            Guid.NewGuid(), "RecoveryRequired",
            [new OrganizationMoveReceipt(source, destination, identity, true)],
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(journal, JsonSerializer.Serialize(journalValue));

        await File.WriteAllTextAsync(destination, "external change");
        await Assert.ThrowsAsync<IOException>(() =>
            new DesktopOrganizationTransaction(journal).RecoverAsync());
        Assert.True(File.Exists(journal));
    }

    [Fact]
    public async Task RecoverRestoresMatchingDestinationAndRemovesJournal()
    {
        using var root = new TempDirectory();
        string source = Path.Combine(root.Path, "source.txt");
        string destination = Path.Combine(root.Path, "destination.txt");
        string journal = Path.Combine(root.Path, "operation.json");
        await File.WriteAllTextAsync(destination, "original");
        var identity = FileIdentity.Capture(destination);
        var journalValue = new OrganizationRecoveryJournal(
            Guid.NewGuid(), "RecoveryRequired",
            [new OrganizationMoveReceipt(source, destination, identity, true)],
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(journal, JsonSerializer.Serialize(journalValue));

        var result = await new DesktopOrganizationTransaction(journal).RecoverAsync();

        Assert.Equal(OrganizationTransactionStatus.Completed, result.Status);
        Assert.Equal("original", await File.ReadAllTextAsync(source));
        Assert.False(File.Exists(destination));
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public async Task ExecuteRejectsExistingDestinationBeforeChangingSource()
    {
        using var root = new TempDirectory();
        string source = Path.Combine(root.Path, "source.txt");
        string destination = Path.Combine(root.Path, "destination.txt");
        await File.WriteAllTextAsync(source, "source");
        await File.WriteAllTextAsync(destination, "existing");

        var transaction = new DesktopOrganizationTransaction(Path.Combine(root.Path, "operation.json"));
        await Assert.ThrowsAsync<IOException>(() => transaction.ExecuteAsync(
            [new OrganizationMove(source, destination)]));

        Assert.True(File.Exists(source));
        Assert.Equal("existing", await File.ReadAllTextAsync(destination));
    }

    private sealed class TempDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "DeskNest.Core.Tests", Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
