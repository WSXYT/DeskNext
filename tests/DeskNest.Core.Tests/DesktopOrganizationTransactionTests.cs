using System.Diagnostics;
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

    [Fact]
    public async Task ExecuteAndRecoverMovesDirectoryOnlyWhenManifestIsUnchanged()
    {
        using var root = new TempDirectory();
        string source = Path.Combine(root.Path, "source-dir");
        string destination = Path.Combine(root.Path, "destination-dir");
        string journal = Path.Combine(root.Path, "operation.json");
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        await File.WriteAllTextAsync(Path.Combine(source, "nested", "item.txt"), "item");

        var transaction = new DesktopOrganizationTransaction(journal);
        var result = await transaction.ExecuteDirectoriesAsync([
            new OrganizationDirectoryMove(source, destination)]);

        Assert.Equal(OrganizationTransactionStatus.Completed, result.Status);
        Assert.False(Directory.Exists(source));
        Assert.Equal("item", await File.ReadAllTextAsync(Path.Combine(destination, "nested", "item.txt")));
        Assert.False(File.Exists(journal));

        var identity = FileIdentity.Capture(Path.Combine(destination, "nested", "item.txt"));
        var recoveryJournal = new OrganizationRecoveryJournal(
            Guid.NewGuid(), "RecoveryRequired", [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new OrganizationDirectoryMoveReceipt(
                source, destination,
                [new DirectoryFileReceipt(Path.Combine("nested", "item.txt"), identity)], true)
                { Directories = ["nested"] }]);
        await File.WriteAllTextAsync(journal, JsonSerializer.Serialize(recoveryJournal));
        var restored = await new DesktopOrganizationTransaction(journal).RecoverAsync();

        Assert.Equal(OrganizationTransactionStatus.Completed, restored.Status);
        Assert.True(File.Exists(Path.Combine(source, "nested", "item.txt")));
        Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task CrossVolumeDirectoryMovesExposeUnsupportedCapabilityAndFailClosed()
    {
        using var root = new TempDirectory();
        string source = Path.Combine(root.Path, "source-dir");
        Directory.CreateDirectory(source);
        var transaction = new DesktopOrganizationTransaction(Path.Combine(root.Path, "operation.json"));

        Assert.False(transaction.SupportsCrossVolumeDirectoryMoves);
        Assert.Equal(
            OrganizationDirectoryMoveSupport.SameVolumeAtomicWithManifest,
            transaction.DirectoryMoveSupport);

        string sourceRoot = Path.GetPathRoot(root.Path)!;
        string? alternateRoot = DriveInfo.GetDrives()
            .Select(drive => drive.RootDirectory.FullName)
            .FirstOrDefault(candidate => !string.Equals(candidate, sourceRoot,
                StringComparison.OrdinalIgnoreCase));
        if (alternateRoot is null)
            return;

        await Assert.ThrowsAsync<NotSupportedException>(() => transaction.ExecuteDirectoriesAsync([
            new OrganizationDirectoryMove(source, Path.Combine(alternateRoot, "DeskNest-test-destination"))]));
        Assert.True(Directory.Exists(source));
    }

    [Fact]
    public async Task ExecuteDirectoriesRejectsDestinationInsideSource()
    {
        using var root = new TempDirectory();
        string source = Path.Combine(root.Path, "source-dir");
        Directory.CreateDirectory(source);
        var transaction = new DesktopOrganizationTransaction(Path.Combine(root.Path, "operation.json"));

        await Assert.ThrowsAsync<InvalidDataException>(() => transaction.ExecuteDirectoriesAsync([
            new OrganizationDirectoryMove(source, Path.Combine(source, "nested"))]));
    }

    [Fact]
    public async Task FaultInjectionLeavesRecoveryJournalForCompletedMoves()
    {
        using var root = new TempDirectory();
        string sourceOne = Path.Combine(root.Path, "one.txt");
        string sourceTwo = Path.Combine(root.Path, "two.txt");
        string destinationOne = Path.Combine(root.Path, "out-one.txt");
        string destinationTwo = Path.Combine(root.Path, "out-two.txt");
        string journal = Path.Combine(root.Path, "operation.json");
        await File.WriteAllTextAsync(sourceOne, "one");
        await File.WriteAllTextAsync(sourceTwo, "two");

        var transaction = new DesktopOrganizationTransaction(journal, move =>
            !move.SourcePath.EndsWith("two.txt", StringComparison.OrdinalIgnoreCase));
        await Assert.ThrowsAsync<IOException>(() => transaction.ExecuteAsync([
            new OrganizationMove(sourceOne, destinationOne),
            new OrganizationMove(sourceTwo, destinationTwo)]));

        Assert.False(File.Exists(sourceOne));
        Assert.True(File.Exists(destinationOne));
        Assert.True(File.Exists(sourceTwo));
        Assert.True(File.Exists(journal));

        var restored = await transaction.RecoverAsync();
        Assert.Equal(OrganizationTransactionStatus.Completed, restored.Status);
        Assert.True(File.Exists(sourceOne));
        Assert.False(File.Exists(destinationOne));
    }

    [Fact]
    public async Task DirectoryFaultInjectionLeavesRecoverableJournalForCompletedMoves()
    {
        using var root = new TempDirectory();
        string sourceOne = Path.Combine(root.Path, "one");
        string sourceTwo = Path.Combine(root.Path, "two");
        string destinationOne = Path.Combine(root.Path, "out-one");
        string destinationTwo = Path.Combine(root.Path, "out-two");
        string journal = Path.Combine(root.Path, "operation.json");
        Directory.CreateDirectory(sourceOne);
        Directory.CreateDirectory(sourceTwo);
        await File.WriteAllTextAsync(Path.Combine(sourceOne, "one.txt"), "one");
        await File.WriteAllTextAsync(Path.Combine(sourceTwo, "two.txt"), "two");

        var transaction = new DesktopOrganizationTransaction(journal, directoryMoveGuard: move =>
            !move.SourcePath.EndsWith("two", StringComparison.OrdinalIgnoreCase));
        await Assert.ThrowsAsync<IOException>(() => transaction.ExecuteDirectoriesAsync([
            new OrganizationDirectoryMove(sourceOne, destinationOne),
            new OrganizationDirectoryMove(sourceTwo, destinationTwo)]));

        Assert.False(Directory.Exists(sourceOne));
        Assert.True(Directory.Exists(destinationOne));
        Assert.True(Directory.Exists(sourceTwo));
        Assert.True(File.Exists(journal));

        var restored = await transaction.RecoverAsync();
        Assert.Equal(OrganizationTransactionStatus.Completed, restored.Status);
        Assert.True(File.Exists(Path.Combine(sourceOne, "one.txt")));
        Assert.False(Directory.Exists(destinationOne));
    }

    [Fact]
    public async Task ExecuteRejectsDuplicateSourceBeforeMovingAnything()
    {
        using var root = new TempDirectory();
        string source = Path.Combine(root.Path, "source.txt");
        await File.WriteAllTextAsync(source, "source");
        string journal = Path.Combine(root.Path, "operation.json");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new DesktopOrganizationTransaction(journal).ExecuteAsync([
                new OrganizationMove(source, Path.Combine(root.Path, "one.txt")),
                new OrganizationMove(source, Path.Combine(root.Path, "two.txt"))]));

        Assert.True(File.Exists(source));
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public async Task ExecuteRejectsReparsePointDestinationParentBeforeMoving()
    {
        using var root = new TempDirectory();
        string source = Path.Combine(root.Path, "source.txt");
        string outside = Directory.CreateDirectory(Path.Combine(root.Path, "outside")).FullName;
        string trashRoot = Path.Combine(root.Path, ".desknest-trash");
        await File.WriteAllTextAsync(source, "source");
        string sentinel = Path.Combine(outside, "sentinel.txt");
        await File.WriteAllTextAsync(sentinel, "untouched");
        if (OperatingSystem.IsWindows())
        {
            using var process = Process.Start(new ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                $"/d /c mklink /J \"{trashRoot}\" \"{outside}\"")
            { UseShellExecute = false, CreateNoWindow = true });
            Assert.NotNull(process);
            Assert.True(process!.WaitForExit(10_000));
            Assert.Equal(0, process.ExitCode);
        }
        else
        {
            Directory.CreateSymbolicLink(trashRoot, outside);
        }

        try
        {
            string journal = Path.Combine(root.Path, "operation.json");
            var transaction = new DesktopOrganizationTransaction(journal);
            await Assert.ThrowsAsync<IOException>(() => transaction.ExecuteAsync([
                new OrganizationMove(source, Path.Combine(trashRoot, "op", "source.txt"))]));
            Assert.True(File.Exists(source));
            Assert.Equal("untouched", await File.ReadAllTextAsync(sentinel));
            Assert.False(Directory.Exists(Path.Combine(outside, "op")));
            Assert.False(transaction.HasRecoveryJournal);
        }
        finally { Directory.Delete(trashRoot); }
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
