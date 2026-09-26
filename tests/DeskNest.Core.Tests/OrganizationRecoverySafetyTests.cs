using System.Text.Json;
using DeskNest.Core.Storage;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class OrganizationRecoverySafetyTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "DeskNest-recovery-" + Guid.NewGuid())).FullName;
    private string Journal => Path.Combine(root, "operation.json");

    [Fact]
    public async Task DoubleCorruptionPreservesEvidenceAndBlocksSubsequentInstances()
    {
        await File.WriteAllTextAsync(Journal, "{broken");
        await File.WriteAllTextAsync(Journal + ".bak", "null");
        var transaction = new DesktopOrganizationTransaction(Journal);
        await Assert.ThrowsAsync<InvalidDataException>(() => transaction.RecoverAsync());
        Assert.Equal("{broken", await File.ReadAllTextAsync(Journal));
        Assert.Equal("null", await File.ReadAllTextAsync(Journal + ".bak"));
        Assert.True(File.Exists(Journal + ".recovery-required"));
        // Even if someone removes the damaged copies, startup cannot silently reset.
        File.Delete(Journal);
        File.Delete(Journal + ".bak");
        var restarted = new DesktopOrganizationTransaction(Journal);
        Assert.True(restarted.HasRecoveryJournal);
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.RecoverAsync());
        string source = Path.Combine(root, "source.txt");
        await File.WriteAllTextAsync(source, "untouched");
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ExecuteAsync(
            [new(source, Path.Combine(root, "target.txt"))]));
        Assert.True(File.Exists(source));
    }

    [Fact]
    public async Task BackupOnlyJournalBlocksNewMovesAndCanRecover()
    {
        string source = Path.Combine(root, "source.txt");
        string destination = Path.Combine(root, "target.txt");
        await File.WriteAllTextAsync(destination, "moved");
        await SaveJournal(Journal + ".bak", source, destination, true);
        var transaction = new DesktopOrganizationTransaction(Journal);
        Assert.True(transaction.HasRecoveryJournal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.ExecuteAsync([new(destination, source)]));
        await transaction.RecoverAsync();
        Assert.Equal("moved", await File.ReadAllTextAsync(source));
        Assert.False(transaction.HasRecoveryJournal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenameWithoutAcknowledgementNeverDiscardsJournal(bool backupOnly)
    {
        string source = Path.Combine(root, "source.txt");
        string destination = Path.Combine(root, "target.txt");
        await File.WriteAllTextAsync(destination, "moved before crash");
        await SaveJournal(backupOnly ? Journal + ".bak" : Journal, source, destination, false);
        var transaction = new DesktopOrganizationTransaction(Journal);
        await Assert.ThrowsAsync<IOException>(() => transaction.RecoverAsync());
        Assert.True(transaction.HasRecoveryJournal);
        Assert.Equal("moved before crash", await File.ReadAllTextAsync(destination));
        Assert.False(File.Exists(source));
    }

    [Fact]
    public async Task UntouchedPreparedMoveCanBeSafelyCleared()
    {
        string source = Path.Combine(root, "source.txt");
        string destination = Path.Combine(root, "target.txt");
        await File.WriteAllTextAsync(source, "never moved");
        await SaveJournal(Journal, source, destination, false);
        var transaction = new DesktopOrganizationTransaction(Journal);
        await transaction.RecoverAsync();
        Assert.False(transaction.HasRecoveryJournal);
        Assert.Equal("never moved", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task DirectoryRenameWithoutAcknowledgementRequiresManualReconciliation()
    {
        string source = Path.Combine(root, "source");
        string destination = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
        await File.WriteAllTextAsync(Path.Combine(destination, "file.txt"), "moved");
        var receipt = new OrganizationDirectoryMoveReceipt(source, destination,
            DesktopOrganizationTransaction.CaptureDirectoryManifest(destination), false);
        var journal = new OrganizationRecoveryJournal(Guid.NewGuid(), "Prepared", [],
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, [receipt]);
        await File.WriteAllTextAsync(Journal, JsonSerializer.Serialize(journal));
        var transaction = new DesktopOrganizationTransaction(Journal);
        await Assert.ThrowsAsync<IOException>(() => transaction.RecoverAsync());
        Assert.True(transaction.HasRecoveryJournal);
        Assert.True(Directory.Exists(destination));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task NullReceiptPathsTryValidBackupAndMarkDoubleCorruption(bool directory, bool nullSource)
    {
        string source = Path.Combine(root, "source.txt");
        string destination = Path.Combine(root, "destination.txt");
        await File.WriteAllTextAsync(source, "unchanged");
        await SaveJournal(Journal + ".bak", source, destination, false);
        var identity = FileIdentity.Capture(source);
        string badSource = nullSource ? null! : source;
        string badDestination = nullSource ? destination : null!;
        var malformed = new OrganizationRecoveryJournal(Guid.NewGuid(), "Prepared",
            directory ? [] : [new(badSource, badDestination, identity, false)],
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            directory ? [new(badSource, badDestination, [], false)] : null);
        string invalidJson = JsonSerializer.Serialize(malformed);
        await File.WriteAllTextAsync(Journal, invalidJson);
        var transaction = new DesktopOrganizationTransaction(Journal);
        await transaction.RecoverAsync();
        Assert.False(transaction.HasRecoveryJournal);
        Assert.Equal("unchanged", await File.ReadAllTextAsync(source));

        await File.WriteAllTextAsync(Journal, invalidJson);
        await File.WriteAllTextAsync(Journal + ".bak", invalidJson);
        await Assert.ThrowsAsync<InvalidDataException>(() => transaction.RecoverAsync());
        Assert.True(File.Exists(Journal + ".recovery-required"));
        Assert.Equal(invalidJson, await File.ReadAllTextAsync(Journal));
    }

    private Task SaveJournal(string path, string source, string destination, bool completed)
    {
        var identity = FileIdentity.Capture(File.Exists(destination) ? destination : source);
        var journal = new OrganizationRecoveryJournal(Guid.NewGuid(), "Prepared",
            [new(source, destination, identity, completed)], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        return File.WriteAllTextAsync(path, JsonSerializer.Serialize(journal));
    }

    public void Dispose() => Directory.Delete(root, true);
}
