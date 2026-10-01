using System.Text.Json;
using DeskNest.Core.Storage;
using DeskNest.Core.Workspace;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class NativeIdentityTests
{
    [Fact]
    public async Task SameContentReplacementCannotAuthorizeFileRecovery()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestDirectory();
        string source = Path.Combine(root.Path, "source.txt");
        string destination = Path.Combine(root.Path, "destination.txt");
        string journal = Path.Combine(root.Path, "journal.json");
        await File.WriteAllTextAsync(source, "unchanged content");
        var transaction = new DesktopOrganizationTransaction(journal);
        var result = await transaction.ExecuteAsync([new(source, destination)], retainJournalUntilCommit: true);
        var expected = result.Receipts.Single().Identity;
        Assert.NotNull(expected.NativeId);

        File.Delete(destination);
        await File.WriteAllTextAsync(destination, "unchanged content");
        File.SetLastWriteTimeUtc(destination, new DateTime(expected.LastWriteTimeUtcTicks, DateTimeKind.Utc));
        var replacement = FileIdentity.Capture(destination);
        Assert.Equal(expected.Sha256, replacement.Sha256);
        Assert.Equal(expected.LastWriteTimeUtcTicks, replacement.LastWriteTimeUtcTicks);
        Assert.NotEqual(expected.NativeId, replacement.NativeId);

        await Assert.ThrowsAsync<IOException>(() => transaction.RecoverAsync());
        Assert.True(File.Exists(journal));
        Assert.False(File.Exists(source));
        Assert.True(File.Exists(destination));
    }

    [Fact]
    public async Task SameContentReplacementCannotAuthorizeDirectoryRecovery()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestDirectory();
        string source = Path.Combine(root.Path, "source");
        string destination = Path.Combine(root.Path, "destination");
        string journal = Path.Combine(root.Path, "journal.json");
        Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(Path.Combine(source, "item.txt"), "unchanged content");
        var transaction = new DesktopOrganizationTransaction(journal);
        var result = await transaction.ExecuteDirectoriesAsync([new(source, destination)],
            retainJournalUntilCommit: true);
        var expected = result.DirectoryReceipts!.Single().Files.Single().Identity;
        string item = Path.Combine(destination, "item.txt");
        File.Delete(item);
        await File.WriteAllTextAsync(item, "unchanged content");
        File.SetLastWriteTimeUtc(item, new DateTime(expected.LastWriteTimeUtcTicks, DateTimeKind.Utc));
        Assert.NotEqual(expected.NativeId, FileIdentity.Capture(item).NativeId);

        await Assert.ThrowsAsync<IOException>(() => transaction.RecoverAsync());
        Assert.True(File.Exists(journal));
        Assert.False(Directory.Exists(source));
    }

    [Fact]
    public async Task ReopenedWorkspaceRefusesReplacedFileUndo()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestDirectory();
        string sourceFolder = Path.Combine(root.Path, "source");
        string targetFolder = Path.Combine(root.Path, "target");
        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(targetFolder);
        string source = Path.Combine(sourceFolder, "item.txt");
        string destination = Path.Combine(targetFolder, "item.txt");
        await File.WriteAllTextAsync(source, "unchanged content");
        var sourceSpace = new WorkspaceSpace(Guid.NewGuid(), "Source", "", SpaceStorageMode.Managed, sourceFolder);
        var targetSpace = new WorkspaceSpace(Guid.NewGuid(), "Target", "", SpaceStorageMode.Managed, targetFolder);
        var file = new WorkspaceFile(Guid.NewGuid(), sourceSpace.Id, "item.txt", source, false);
        Guid operationId;
        await using (var store = await WorkspaceStore.OpenAsync(root.Path))
        {
            await store.UpdateAsync(state => state with { Spaces = [sourceSpace, targetSpace], Files = [file] });
            await new ManualOrganizationCoordinator(store,
                new DesktopOrganizationTransaction(Path.Combine(root.Path, "journal.json")))
                .MoveFileAsync(file.Id, targetSpace.Id);
            var operation = store.Snapshot.Operations.Single();
            operationId = operation.Id;
            Assert.NotNull(operation.OriginalNativeId);
        }
        await using var reopened = await WorkspaceStore.OpenAsync(root.Path);
        var expected = reopened.Snapshot.Operations.Single();
        File.Delete(destination);
        await File.WriteAllTextAsync(destination, "unchanged content");
        File.SetLastWriteTimeUtc(destination, new DateTime(expected.OriginalLastWriteUtcTicks!.Value, DateTimeKind.Utc));
        Assert.NotEqual(expected.OriginalNativeId, FileIdentity.Capture(destination).NativeId);

        await Assert.ThrowsAsync<IOException>(() => new ManualOrganizationCoordinator(reopened,
            new DesktopOrganizationTransaction(Path.Combine(root.Path, "journal.json")))
            .UndoOperationAsync(operationId));
        Assert.Equal(ProposedOperationStatus.RecoveryRequired, reopened.Snapshot.Operations.Single().Status);
        Assert.False(File.Exists(source));
    }

    [Fact]
    public async Task LegacyHashOnlyReceiptDoesNotAuthorizeWindowsRecovery()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var root = new TestDirectory();
        string source = Path.Combine(root.Path, "source.txt");
        string destination = Path.Combine(root.Path, "destination.txt");
        string journal = Path.Combine(root.Path, "journal.json");
        await File.WriteAllTextAsync(destination, "unchanged content");
        var identity = FileIdentity.Capture(destination) with { NativeId = null };
        var recovery = new OrganizationRecoveryJournal(Guid.NewGuid(), "Moving",
            [new OrganizationMoveReceipt(source, destination, identity, true)],
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        await File.WriteAllTextAsync(journal, JsonSerializer.Serialize(recovery));

        await Assert.ThrowsAsync<IOException>(() => new DesktopOrganizationTransaction(journal).RecoverAsync());
        Assert.True(File.Exists(journal));
        Assert.True(File.Exists(destination));
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "DeskNest.NativeIdentity.Tests", Guid.NewGuid().ToString("N"))).FullName;

        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
