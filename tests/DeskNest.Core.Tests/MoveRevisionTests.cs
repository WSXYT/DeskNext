using DeskNest.Core.Storage;
using DeskNest.Core.Workspace;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class MoveRevisionTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        "DeskNext-move-revision-" + Guid.NewGuid().ToString("N"))).FullName;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StaleSnapshotCannotMoveAnItemAfterWaitingForOrganizationGate(bool changeWhileWaiting)
    {
        var (source, target, file) = Fixture();
        await using var store = await WorkspaceStore.OpenAsync(Path.Combine(root, "workspace"));
        await store.UpdateAsync(s => s with { Spaces = [source, target], Files = [file] });
        long revision = store.Snapshot.Revision;
        var transaction = new DesktopOrganizationTransaction(Path.Combine(store.DataDirectory, "move.json"));
        var coordinator = new ManualOrganizationCoordinator(store, transaction);
        if (!changeWhileWaiting) await store.UpdateAsync(s => s);
        await store.OrganizationGate.WaitAsync();
        Task<ManualOrganizationResult> moving;
        try
        {
            moving = coordinator.MoveFileAsync(file.Id, target.Id, expectedWorkspaceRevision: revision);
            Assert.False(moving.IsCompleted);
            if (changeWhileWaiting) await store.UpdateAsync(s => s);
        }
        finally { store.OrganizationGate.Release(); }
        await Assert.ThrowsAsync<InvalidDataException>(() => moving);
        Assert.Equal("original", File.ReadAllText(file.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target.Folder));
        Assert.Empty(store.Snapshot.Operations);
        Assert.Equal(file.Path, store.Snapshot.Files.Single().Path);
        Assert.False(transaction.HasRecoveryJournal);
    }

    [Fact]
    public async Task MatchingRevisionUsesTheNormalJournaledMove()
    {
        var (source, target, file) = Fixture();
        await using var store = await WorkspaceStore.OpenAsync(Path.Combine(root, "workspace"));
        await store.UpdateAsync(s => s with { Spaces = [source, target], Files = [file] });
        var transaction = new DesktopOrganizationTransaction(Path.Combine(store.DataDirectory, "move.json"));
        var result = await new ManualOrganizationCoordinator(store, transaction).MoveFileAsync(
            file.Id, target.Id, expectedWorkspaceRevision: store.Snapshot.Revision);
        Assert.False(File.Exists(file.Path));
        Assert.Equal("original", File.ReadAllText(result.DestinationPath));
        Assert.Equal(ProposedOperationStatus.Completed, store.Snapshot.Operations.Single().Status);
        Assert.False(transaction.HasRecoveryJournal);
    }

    private (WorkspaceSpace Source, WorkspaceSpace Target, WorkspaceFile File) Fixture()
    {
        string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        string target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
        var from = new WorkspaceSpace(Guid.NewGuid(), "Source", "", SpaceStorageMode.Managed, source);
        var to = new WorkspaceSpace(Guid.NewGuid(), "Target", "", SpaceStorageMode.Managed, target);
        string file = Path.Combine(source, "item.txt");
        File.WriteAllText(file, "original");
        return (from, to, new WorkspaceFile(Guid.NewGuid(), from.Id, "item.txt", file, false));
    }

    [Fact]
    public async Task RevisionIsCheckedAgainInsideTheMetadataGate()
    {
        var (source, target, file) = Fixture();
        await using var store = await WorkspaceStore.OpenAsync(Path.Combine(root, "workspace"));
        await store.UpdateAsync(s => s with { Spaces = [source, target], Files = [file] });
        long revision = store.Snapshot.Revision;
        var transaction = new DesktopOrganizationTransaction(Path.Combine(store.DataDirectory, "move.json"));
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holder = Task.Run(() => store.UpdateAsync(state =>
        {
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Metadata gate fixture timed out.");
            return state;
        }));
        Task<ManualOrganizationResult> moving;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            moving = new ManualOrganizationCoordinator(store, transaction).MoveFileAsync(
                file.Id, target.Id, expectedWorkspaceRevision: revision);
            Assert.False(moving.IsCompleted);
        }
        finally { release.Set(); await holder; }
        await Assert.ThrowsAsync<InvalidDataException>(() => moving);
        Assert.Equal("original", File.ReadAllText(file.Path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(target.Folder));
        Assert.Empty(store.Snapshot.Operations);
        Assert.False(transaction.HasRecoveryJournal);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
