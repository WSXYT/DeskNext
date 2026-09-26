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

    public void Dispose() => Directory.Delete(root, true);
}
