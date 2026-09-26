using DeskNest.Core.Storage;
using DeskNest.Core.Workspace;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class OrganizationCommitTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "DeskNest-commit-" + Guid.NewGuid())).FullName;

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, false)]
    [InlineData(false, false, true)]
    [InlineData(false, true, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task RestartHonorsWorkspaceCommitAcrossFileDirectoryAndUndoBoundaries(
        bool directory, bool committed, bool undo)
    {
        string left = Directory.CreateDirectory(Path.Combine(root, "left")).FullName;
        string right = Directory.CreateDirectory(Path.Combine(root, "right")).FullName;
        string name = directory ? "folder" : "file.txt";
        string original = Path.Combine(left, name);
        string moved = Path.Combine(right, name);
        string from = undo ? moved : original;
        string to = undo ? original : moved;
        if (directory) Directory.CreateDirectory(from);
        await File.WriteAllTextAsync(directory ? Path.Combine(from, "content.txt") : from, "keep me");
        var leftSpace = new WorkspaceSpace(Guid.NewGuid(), "Left", "", SpaceStorageMode.Managed, left);
        var rightSpace = new WorkspaceSpace(Guid.NewGuid(), "Right", "", SpaceStorageMode.Managed, right);
        var file = new WorkspaceFile(Guid.NewGuid(), undo ? rightSpace.Id : leftSpace.Id, name, from, directory);
        var operation = new ProposedOperation(Guid.NewGuid(), file.Id, rightSpace.Id,
            undo ? ProposedOperationStatus.Completed : ProposedOperationStatus.PendingUser, DateTimeOffset.UtcNow)
        {
            SourceSpaceId = leftSpace.Id, SourcePath = original, DestinationPath = moved,
            CommittedTransactionId = undo ? Guid.NewGuid() : null
        };
        string journalPath = Path.Combine(root, "operation.json");
        var transaction = new DesktopOrganizationTransaction(journalPath);
        await using (var store = await WorkspaceStore.OpenAsync(root))
        {
            await store.UpdateAsync(s => s with { Spaces = [leftSpace, rightSpace], Files = [file], Operations = [operation] });
            var result = directory
                ? await transaction.ExecuteDirectoriesAsync([new(from, to)], retainJournalUntilCommit: true)
                : await transaction.ExecuteAsync([new(from, to)], retainJournalUntilCommit: true);
            Assert.True(transaction.HasRecoveryJournal);
            // Simulate interruption on either side of the metadata commit, leaving the
            // actual retained journal, not a hand-authored approximation of it.
            if (committed)
                await store.UpdateAsync(s => s with
                {
                    Files = [file with { Path = to, SpaceId = undo ? leftSpace.Id : rightSpace.Id }],
                    Operations = [operation with
                    {
                        Status = undo ? ProposedOperationStatus.Undone : ProposedOperationStatus.Completed,
                        CommittedTransactionId = result.OperationId
                    }]
                });
        }
        await using var reopened = await WorkspaceStore.OpenAsync(root);
        var restartedTransaction = new DesktopOrganizationTransaction(journalPath);
        await new ManualOrganizationCoordinator(reopened, restartedTransaction).RecoverPendingAsync();
        string expected = committed ? to : from;
        string absent = committed ? from : to;
        Assert.Equal(expected, reopened.Snapshot.Files.Single().Path);
        Assert.Equal("keep me", await File.ReadAllTextAsync(directory ? Path.Combine(expected, "content.txt") : expected));
        Assert.False(File.Exists(absent) || Directory.Exists(absent));
        Assert.False(restartedTransaction.HasRecoveryJournal);
        Assert.Equal(committed
            ? undo ? ProposedOperationStatus.Undone : ProposedOperationStatus.Completed
            : undo ? ProposedOperationStatus.Completed : ProposedOperationStatus.RecoveryRequired,
            reopened.Snapshot.Operations.Single().Status);
        if (committed)
        {
            // Startup cleanup must checkpoint too, not merely the normal commit path.
            var snapshot = reopened.Snapshot;
            await reopened.DisposeAsync();
            await File.WriteAllTextAsync(Path.Combine(root, "workspace.json"), "{corrupt after startup cleanup");
            await using var backupReload = await WorkspaceStore.OpenAsync(root);
            Assert.Equal(snapshot.Files.Single(), backupReload.Snapshot.Files.Single());
            Assert.Equal(snapshot.Operations.Single().CommittedTransactionId,
                backupReload.Snapshot.Operations.Single().CommittedTransactionId);
        }
    }

    [Fact]
    public async Task CommitRequiresExactTransactionIdAndBlocksNewMovesUntilAcknowledged()
    {
        string from = Path.Combine(root, "from.txt");
        string to = Path.Combine(root, "to.txt");
        await File.WriteAllTextAsync(from, "data");
        var transaction = new DesktopOrganizationTransaction(Path.Combine(root, "operation.json"));
        var result = await transaction.ExecuteAsync([new(from, to)], retainJournalUntilCommit: true);
        await Assert.ThrowsAsync<InvalidDataException>(() => transaction.CommitAsync(Guid.NewGuid()));
        await Assert.ThrowsAsync<InvalidOperationException>(() => transaction.ExecuteAsync([new(to, from)]));
        Assert.True(transaction.HasRecoveryJournal);
        await transaction.CommitAsync(result.OperationId);
        Assert.False(transaction.HasRecoveryJournal);
        Assert.True(File.Exists(to));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SecondCoordinatorCannotRecoverAnActiveMetadataCommit(bool directory, bool separateTransaction)
    {
        var left = new WorkspaceSpace(Guid.NewGuid(), "Left", "", SpaceStorageMode.Managed,
            Directory.CreateDirectory(Path.Combine(root, "left")).FullName);
        var right = new WorkspaceSpace(Guid.NewGuid(), "Right", "", SpaceStorageMode.Managed,
            Directory.CreateDirectory(Path.Combine(root, "right")).FullName);
        string source = Path.Combine(left.Folder, "item");
        string destination = Path.Combine(right.Folder, "item");
        if (directory) Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(directory ? Path.Combine(source, "content.txt") : source, "keep me");
        var file = new WorkspaceFile(Guid.NewGuid(), left.Id, "item", source, directory);
        await using var store = await WorkspaceStore.OpenAsync(root);
        await store.UpdateAsync(s => s with { Spaces = [left, right], Files = [file] });
        using var allowMove = new ManualResetEventSlim();
        using var allowMetadata = new ManualResetEventSlim();
        var atMove = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var atMetadata = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool PauseMove()
        {
            atMove.SetResult();
            if (!allowMove.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Move barrier timed out.");
            return true;
        }
        string journalPath = Path.Combine(root, "operation.json");
        var transaction = new DesktopOrganizationTransaction(journalPath,
            moveGuard: _ => PauseMove(), directoryMoveGuard: _ => PauseMove());
        var first = new ManualOrganizationCoordinator(store, transaction);
        var second = new ManualOrganizationCoordinator(store,
            separateTransaction ? new DesktopOrganizationTransaction(journalPath) : transaction);
        Task moving = Task.Run(() => first.MoveFileAsync(file.Id, right.Id));
        Task blockingMetadata = Task.CompletedTask;
        Task recovering = Task.CompletedTask;
        try
        {
            await atMove.Task.WaitAsync(TimeSpan.FromSeconds(15));
            blockingMetadata = Task.Run(() => store.UpdateAsync(s =>
            {
                atMetadata.SetResult();
                if (!allowMetadata.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException("Metadata barrier timed out.");
                return s;
            }));
            await atMetadata.Task.WaitAsync(TimeSpan.FromSeconds(15));
            // Queue recovery before the physical transaction releases its lock.
            recovering = second.RecoverPendingAsync();
            // An intentionally wrong ID is a read-only barrier: it acquires the
            // transaction gate after Execute and proves the retained journal exists.
            Task probe = transaction.CommitAsync(Guid.NewGuid());
            allowMove.Set();
            await Assert.ThrowsAsync<InvalidDataException>(() => probe.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.False(recovering.IsCompleted);
            Assert.True(directory ? Directory.Exists(destination) : File.Exists(destination));
            Assert.Equal(source, store.Snapshot.Files.Single().Path); // commit is still blocked
        }
        finally
        {
            allowMove.Set();
            allowMetadata.Set();
            await Task.WhenAll(moving, blockingMetadata, recovering).WaitAsync(TimeSpan.FromSeconds(15));
        }
        Assert.Equal(destination, store.Snapshot.Files.Single().Path);
        Assert.NotNull(store.Snapshot.Operations.Single().CommittedTransactionId);
        Assert.Equal(ProposedOperationStatus.Completed, store.Snapshot.Operations.Single().Status);
        Assert.False(transaction.HasRecoveryJournal);
    }

    [Fact]
    public async Task WorkspaceDisposalKeepsExclusiveOwnerUntilOrganizationFinishes()
    {
        var store = await WorkspaceStore.OpenAsync(root);
        await store.OrganizationGate.WaitAsync();
        Task disposing = store.DisposeAsync().AsTask();
        try
        {
            Assert.False(disposing.IsCompleted);
            await Assert.ThrowsAnyAsync<IOException>(() => WorkspaceStore.OpenAsync(root));
        }
        finally { store.OrganizationGate.Release(); }
        await disposing;
        await Assert.ThrowsAsync<ObjectDisposedException>(() => store.UpdateAsync(s => s));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => new ManualOrganizationCoordinator(store,
            new DesktopOrganizationTransaction(Path.Combine(root, "operation.json"))).RecoverPendingAsync());
        await store.DisposeAsync(); // idempotent
        await using var reopened = await WorkspaceStore.OpenAsync(root);
    }

    [Theory]
    [InlineData(false, "move")]
    [InlineData(true, "move")]
    [InlineData(false, "rename")]
    [InlineData(true, "rename")]
    [InlineData(false, "undo")]
    [InlineData(true, "undo")]
    public async Task CorruptPrimaryAfterJournalCleanupKeepsCommittedPhysicalPaths(bool directory, string action)
    {
        var left = new WorkspaceSpace(Guid.NewGuid(), "Left", "", SpaceStorageMode.Managed,
            Directory.CreateDirectory(Path.Combine(root, "left")).FullName);
        var right = new WorkspaceSpace(Guid.NewGuid(), "Right", "", SpaceStorageMode.Managed,
            Directory.CreateDirectory(Path.Combine(root, "right")).FullName);
        string source = Path.Combine(left.Folder, "item");
        if (directory) Directory.CreateDirectory(source);
        await File.WriteAllTextAsync(directory ? Path.Combine(source, "content.txt") : source, "keep me");
        var file = new WorkspaceFile(Guid.NewGuid(), left.Id, "item", source, directory);
        string journal = Path.Combine(root, "operation.json");
        WorkspaceState committed;
        await using (var store = await WorkspaceStore.OpenAsync(root))
        {
            await store.UpdateAsync(s => s with { Spaces = [left, right], Files = [file] });
            var transaction = new DesktopOrganizationTransaction(journal);
            var coordinator = new ManualOrganizationCoordinator(store, transaction);
            if (action == "rename") await coordinator.RenameFileAsync(file.Id, "renamed");
            else
            {
                await coordinator.MoveFileAsync(file.Id, right.Id);
                if (action == "undo") await coordinator.UndoOperationAsync(store.Snapshot.Operations.Single().Id);
            }
            committed = store.Snapshot;
            Assert.False(transaction.HasRecoveryJournal);
        }
        await File.WriteAllTextAsync(Path.Combine(root, "workspace.json"), "{corrupt");
        await using var reopened = await WorkspaceStore.OpenAsync(root);
        await new ManualOrganizationCoordinator(reopened, new DesktopOrganizationTransaction(journal)).RecoverPendingAsync();
        Assert.Equal(committed.Revision, reopened.Snapshot.Revision);
        Assert.Equal(committed.Files.Single(), reopened.Snapshot.Files.Single());
        Assert.Equal(committed.Operations.Single().CommittedTransactionId,
            reopened.Snapshot.Operations.Single().CommittedTransactionId);
        Assert.Equal(committed.Operations.Single().Status, reopened.Snapshot.Operations.Single().Status);
        string path = reopened.Snapshot.Files.Single().Path;
        Assert.Equal("keep me", await File.ReadAllTextAsync(directory ? Path.Combine(path, "content.txt") : path));
    }

    [Fact]
    public async Task FailedBackupCheckpointRetainsCommitEvidenceForRestartRepair()
    {
        string source = Path.Combine(root, "source");
        string destination = Path.Combine(root, "destination");
        await File.WriteAllTextAsync(source, "keep me");
        var space = new WorkspaceSpace(Guid.NewGuid(), "One", "", SpaceStorageMode.Managed, root);
        var file = new WorkspaceFile(Guid.NewGuid(), space.Id, "source", source, false);
        var transaction = new DesktopOrganizationTransaction(Path.Combine(root, "operation.json"));
        Guid transactionId;
        await using (var store = await WorkspaceStore.OpenAsync(root))
        {
            await store.UpdateAsync(s => s with { Spaces = [space], Files = [file] });
            var result = await transaction.ExecuteAsync([new(source, destination)], retainJournalUntilCommit: true);
            transactionId = result.OperationId;
            await store.UpdateAsync(s => s with
            {
                Files = [file with { Name = "destination", Path = destination }],
                Operations = [new ProposedOperation(Guid.NewGuid(), file.Id, space.Id,
                    ProposedOperationStatus.Completed, DateTimeOffset.UtcNow)
                {
                    SourceSpaceId = space.Id, SourcePath = source, DestinationPath = destination,
                    CommittedTransactionId = transactionId
                }]
            });
            // Simulate corruption between metadata save and checkpoint. Rotating this
            // damaged predecessor must fail verification and retain the physical journal.
            await File.WriteAllTextAsync(Path.Combine(root, "workspace.json"), "{damaged predecessor");
            await Assert.ThrowsAsync<IOException>(() => store.CheckpointRecoveryBackupAsync());
            Assert.True(transaction.HasRecoveryJournal);
            Assert.Equal(transactionId, store.Snapshot.Operations.Single().CommittedTransactionId);
        }
        await using (var reopened = await WorkspaceStore.OpenAsync(root))
        {
            await new ManualOrganizationCoordinator(reopened, transaction).RecoverPendingAsync();
            Assert.False(transaction.HasRecoveryJournal);
            Assert.Equal(destination, reopened.Snapshot.Files.Single().Path);
        }
        await File.WriteAllTextAsync(Path.Combine(root, "workspace.json"), "{corrupt again");
        await using var fallback = await WorkspaceStore.OpenAsync(root);
        Assert.Equal(destination, fallback.Snapshot.Files.Single().Path);
        Assert.Equal(transactionId, fallback.Snapshot.Operations.Single().CommittedTransactionId);
        Assert.Equal("keep me", await File.ReadAllTextAsync(destination));
    }

    [Fact]
    public async Task IncompleteReceiptCannotBeAcknowledgedEvenWithMatchingCommitId()
    {
        string source = Path.Combine(root, "source");
        string destination = Path.Combine(root, "destination");
        await File.WriteAllTextAsync(source, "untouched");
        Guid id = Guid.NewGuid();
        var journal = new OrganizationRecoveryJournal(id, "Prepared",
            [new(source, destination, FileIdentity.Capture(source), false)],
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        string path = Path.Combine(root, "operation.json");
        await File.WriteAllTextAsync(path, System.Text.Json.JsonSerializer.Serialize(journal));
        var transaction = new DesktopOrganizationTransaction(path);
        await Assert.ThrowsAsync<InvalidDataException>(() => transaction.CommitAsync(id));
        await Assert.ThrowsAsync<InvalidDataException>(() => transaction.RecoverAsync(committedTransactionIds: new HashSet<Guid> { id }));
        Assert.True(transaction.HasRecoveryJournal);
        Assert.True(File.Exists(source));
    }

    public void Dispose() => Directory.Delete(root, true);
}
