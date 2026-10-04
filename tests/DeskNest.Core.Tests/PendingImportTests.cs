using DeskNest.Core.Storage;
using DeskNest.Core.Workspace;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class PendingImportTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        "DeskNext-import-" + Guid.NewGuid().ToString("N"))).FullName;
    private string Data => Path.Combine(root, "state");
    private string Journal => Path.Combine(Data, "organization-recovery.json");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportAndUndoRestoreExternalPendingWithoutInventingASpace(bool directory)
    {
        var (pending, target) = Fixture(directory);
        await using (var store = await OpenAsync(pending, target))
        {
            var coordinator = new ManualOrganizationCoordinator(store, new(Journal));
            var result = await coordinator.ImportPendingAsync(pending.Id, target.Id, store.Snapshot.Revision);
            var imported = Assert.Single(store.Snapshot.Files);
            var operation = Assert.Single(store.Snapshot.Operations);
            Assert.Equal(result.FileId, imported.Id);
            Assert.Equal(target.Id, imported.SpaceId);
            Assert.Equal(pending, operation.ImportSource);
            Assert.Equal(Guid.Empty, operation.SourceSpaceId);
            Assert.Equal(ProposedOperationStatus.Completed, operation.Status);
            Assert.NotNull(operation.CommittedTransactionId);
            Assert.Empty(store.Snapshot.Pending);
            Assert.Single(store.Snapshot.Spaces);
            Assert.False(Exists(pending.Path));
            Assert.Equal("import fixture", File.ReadAllText(Content(imported.Path, directory)));
            Assert.False(new DesktopOrganizationTransaction(Journal).HasRecoveryJournal);
            // Normal later history must be undoable first, then survive return to the external source.
            await coordinator.RenameFileAsync(imported.Id, "renamed");
        }
        await using (var reopened = await WorkspaceStore.OpenAsync(Data))
        {
            var coordinator = new ManualOrganizationCoordinator(reopened, new(Journal));
            var operations = reopened.Snapshot.Operations;
            await coordinator.UndoOperationAsync(operations[1].Id);
            await coordinator.UndoOperationAsync(operations[0].Id);
            Assert.Empty(reopened.Snapshot.Files);
            Assert.Equal(pending, Assert.Single(reopened.Snapshot.Pending));
            Assert.All(reopened.Snapshot.Operations, o => Assert.Equal(ProposedOperationStatus.Undone, o.Status));
            Assert.Equal("import fixture", File.ReadAllText(Content(pending.Path, directory)));
            if (directory) Assert.True(Directory.Exists(Path.Combine(pending.Path, "empty")));
            Assert.Empty(Directory.GetFileSystemEntries(target.Folder));
            await reopened.UpdateAsync(s => s with { Pending = [] }); // Dismissing review does not destroy history.
        }
        await using var final = await WorkspaceStore.OpenAsync(Data);
        Assert.Empty(final.Snapshot.Files);
        Assert.Empty(final.Snapshot.Pending);
        Assert.Equal(2, final.Snapshot.Operations.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefusedImportRecoversThroughTheExistingJournalAndKeepsPending(bool directory)
    {
        var (pending, target) = Fixture(directory);
        await using (var store = await OpenAsync(pending, target))
        {
            var transaction = new DesktopOrganizationTransaction(Journal, moveGuard: _ => false, directoryMoveGuard: _ => false);
            var coordinator = new ManualOrganizationCoordinator(store, transaction);
            await Assert.ThrowsAsync<IOException>(() => coordinator.ImportPendingAsync(pending.Id, target.Id, store.Snapshot.Revision));
            Assert.True(transaction.HasRecoveryJournal);
            Assert.Empty(store.Snapshot.Files);
            Assert.Equal(pending, Assert.Single(store.Snapshot.Pending));
        }
        await using var reopened = await WorkspaceStore.OpenAsync(Data);
        await new ManualOrganizationCoordinator(reopened, new(Journal)).RecoverPendingAsync();
        Assert.False(new DesktopOrganizationTransaction(Journal).HasRecoveryJournal);
        Assert.Empty(reopened.Snapshot.Files);
        Assert.Equal(pending, Assert.Single(reopened.Snapshot.Pending));
        Assert.Equal(ProposedOperationStatus.RecoveryRequired, Assert.Single(reopened.Snapshot.Operations).Status);
        Assert.Equal("import fixture", File.ReadAllText(Content(pending.Path, directory)));
        Assert.False(Exists(Path.Combine(target.Folder, pending.Name)));
        await reopened.UpdateAsync(s => s with { Pending = [] });
        Assert.Single(reopened.Snapshot.Operations); // Failure evidence outlives a dismissed review item.
    }

    [Fact]
    public async Task OccupiedTargetAndStaleSelectionRefuseBeforeMetadataOrMove()
    {
        var (pending, target) = Fixture(false);
        Directory.CreateDirectory(target.Folder);
        string occupied = Path.Combine(target.Folder, pending.Name);
        File.WriteAllText(occupied, "do not replace");
        await using var store = await OpenAsync(pending, target);
        var coordinator = new ManualOrganizationCoordinator(store, new(Journal));
        long revision = store.Snapshot.Revision;
        await Assert.ThrowsAsync<IOException>(() => coordinator.ImportPendingAsync(pending.Id, target.Id, revision));
        Assert.Equal("do not replace", File.ReadAllText(occupied));
        File.Delete(occupied);
        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.ImportPendingAsync(pending.Id, target.Id, revision - 1));
        Assert.Equal(revision, store.Snapshot.Revision);
        Assert.Empty(store.Snapshot.Operations);
        Assert.Empty(store.Snapshot.Files);
        Assert.False(new DesktopOrganizationTransaction(Journal).HasRecoveryJournal);
        Assert.Equal("import fixture", File.ReadAllText(pending.Path));
    }

    [Fact]
    public async Task ExternalSourceHistoryCannotAuthorizeAMissingCompletedFile()
    {
        var (pending, target) = Fixture(false);
        await using var store = await OpenAsync(pending, target);
        long revision = store.Snapshot.Revision;
        var operation = new ProposedOperation(Guid.NewGuid(), Guid.NewGuid(), target.Id,
            ProposedOperationStatus.Completed, DateTimeOffset.UtcNow)
        {
            ImportSource = pending, SourcePath = pending.Path,
            DestinationPath = Path.Combine(target.Folder, pending.Name)
        };
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateAsync(s => s with { Operations = [operation] }));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateAsync(s => s with
        { Operations = [operation with { Status = ProposedOperationStatus.PendingUser, SourceSpaceId = target.Id }] }));
        Assert.Equal(revision, store.Snapshot.Revision);
        Assert.Empty(store.Snapshot.Operations);
    }

    private (PendingFile Pending, WorkspaceSpace Target) Fixture(bool directory)
    {
        string external = Directory.CreateDirectory(Path.Combine(root, "external")).FullName;
        string path = directory ? Path.Combine(external, ".", "Project") : Path.Combine(external, "document.txt");
        if (directory) Directory.CreateDirectory(Path.Combine(path, "empty"));
        File.WriteAllText(Content(path, directory), "import fixture");
        var target = new WorkspaceSpace(Guid.NewGuid(), "Documents", "", SpaceStorageMode.Managed, Path.Combine(root, "managed", "Documents"));
        return (new PendingFile(Guid.NewGuid(), Path.GetFileName(path), path, TriageReason.FilenameAmbiguous, target.Id, DateTimeOffset.UtcNow), target);
    }

    private async Task<WorkspaceStore> OpenAsync(PendingFile pending, WorkspaceSpace target)
    {
        var store = await WorkspaceStore.OpenAsync(Data);
        await store.UpdateAsync(s => s with { Spaces = [target], Pending = [pending] });
        return store;
    }
    private static string Content(string path, bool directory) => directory ? Path.Combine(path, "item.txt") : path;
    private static bool Exists(string path) => File.Exists(path) || Directory.Exists(path);
    public void Dispose() => Directory.Delete(root, true);
}
