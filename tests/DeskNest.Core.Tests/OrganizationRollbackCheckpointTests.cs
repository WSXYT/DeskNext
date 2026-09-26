using System.Text.Json;
using DeskNest.Core.Storage;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class OrganizationRollbackCheckpointTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "DeskNest-rollback-" + Guid.NewGuid())).FullName;
    private string Journal => Path.Combine(root, "operation.json");
    private string Source(int index) => Path.Combine(root, $"source{index}");
    private string Destination(int index) => Path.Combine(root, $"destination{index}");
    private static string Content(string path, bool directory) => directory ? Path.Combine(path, "content.txt") : path;

    private async Task MovePair(bool directory)
    {
        foreach (int index in new[] { 1, 2 })
        {
            if (directory) Directory.CreateDirectory(Source(index));
            await File.WriteAllTextAsync(Content(Source(index), directory), $"data{index}");
        }
        var transaction = new DesktopOrganizationTransaction(Journal);
        if (directory)
            await transaction.ExecuteDirectoriesAsync([new(Source(1), Destination(1)), new(Source(2), Destination(2))],
                retainJournalUntilCommit: true);
        else
            await transaction.ExecuteAsync([new(Source(1), Destination(1)), new(Source(2), Destination(2))],
                retainJournalUntilCommit: true);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task InterruptedRollbackResumesAfterDurableReceipt(bool directory, bool cancel)
    {
        await MovePair(directory);
        using var cancellation = new CancellationTokenSource();
        int restores = 0;
        var transaction = new DesktopOrganizationTransaction(Journal, restoreGuard: _ =>
        {
            restores++;
            if (cancel) cancellation.Cancel();
            return restores == 1;
        });
        if (cancel)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transaction.RecoverAsync(cancellation.Token));
        else
            await Assert.ThrowsAsync<IOException>(() => transaction.RecoverAsync());

        Assert.True(transaction.HasRecoveryJournal);
        Assert.Equal("data2", await File.ReadAllTextAsync(Content(Source(2), directory)));
        Assert.Equal("data1", await File.ReadAllTextAsync(Content(Destination(1), directory)));
        var journal = JsonSerializer.Deserialize<OrganizationRecoveryJournal>(await File.ReadAllTextAsync(Journal),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("Recovering", journal.Status);
        Assert.True(directory ? journal.DirectoryMoves![1].Restored : journal.Moves[1].Restored);
        Assert.False(directory ? journal.DirectoryMoves![0].Restored : journal.Moves[0].Restored);
        await Assert.ThrowsAsync<InvalidDataException>(() => transaction.CommitAsync(journal.OperationId));
        await Assert.ThrowsAsync<InvalidDataException>(() => transaction.RecoverAsync(
            committedTransactionIds: new HashSet<Guid> { journal.OperationId }));

        int retryRestores = 0;
        var restarted = new DesktopOrganizationTransaction(Journal, restoreGuard: _ => { retryRestores++; return true; });
        await restarted.RecoverAsync();
        Assert.Equal(1, retryRestores);
        Assert.False(restarted.HasRecoveryJournal);
        foreach (int index in new[] { 1, 2 })
        {
            Assert.Equal($"data{index}", await File.ReadAllTextAsync(Content(Source(index), directory)));
            Assert.False(File.Exists(Destination(index)) || Directory.Exists(Destination(index)));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ChangedCheckpointedSourceStopsBeforeAnyFurtherRestore(bool directory)
    {
        await MovePair(directory);
        int restores = 0;
        var transaction = new DesktopOrganizationTransaction(Journal, restoreGuard: _ => ++restores == 1);
        await Assert.ThrowsAsync<IOException>(() => transaction.RecoverAsync());
        await File.WriteAllTextAsync(Content(Source(2), directory), "changed after checkpoint");
        int retries = 0;
        var restarted = new DesktopOrganizationTransaction(Journal, restoreGuard: _ => { retries++; return true; });
        await Assert.ThrowsAsync<IOException>(() => restarted.RecoverAsync());
        Assert.Equal(0, retries);
        Assert.True(restarted.HasRecoveryJournal);
        Assert.Equal("data1", await File.ReadAllTextAsync(Content(Destination(1), directory)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task RestoredPathsRequireAnExplicitPersistedCheckpoint(bool directory, bool checkpointed)
    {
        if (directory) Directory.CreateDirectory(Source(1));
        await File.WriteAllTextAsync(Content(Source(1), directory), "restored before exit");
        var journal = new OrganizationRecoveryJournal(Guid.NewGuid(), "Recovering",
            directory ? [] : [new OrganizationMoveReceipt(Source(1), Destination(1), FileIdentity.Capture(Source(1)), true)
                { Restored = checkpointed }], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            directory ? [new OrganizationDirectoryMoveReceipt(Source(1), Destination(1),
                DesktopOrganizationTransaction.CaptureDirectoryManifest(Source(1)), true) { Restored = checkpointed }] : null);
        await File.WriteAllTextAsync(Journal, JsonSerializer.Serialize(journal));
        var transaction = new DesktopOrganizationTransaction(Journal);
        if (checkpointed)
        {
            await transaction.RecoverAsync();
            Assert.False(transaction.HasRecoveryJournal);
        }
        else
        {
            // A crash between reverse rename and checkpoint is still ambiguous:
            // never infer rollback authority from a matching content hash alone.
            await Assert.ThrowsAsync<IOException>(() => transaction.RecoverAsync());
            Assert.True(transaction.HasRecoveryJournal);
        }
        Assert.Equal("restored before exit", await File.ReadAllTextAsync(Content(Source(1), directory)));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task OlderMovingBackupCannotAcknowledgeAnUncheckpointedRestore(bool directory, bool missingPrimary)
    {
        await MovePair(directory);
        var transaction = new DesktopOrganizationTransaction(Journal, restoreGuard: _ => false);
        await Assert.ThrowsAsync<IOException>(() => transaction.RecoverAsync());
        string backup = await File.ReadAllTextAsync(Journal + ".bak");
        var previous = JsonSerializer.Deserialize<OrganizationRecoveryJournal>(backup,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("Moving", previous.Status);
        Assert.True(directory ? previous.DirectoryMoves!.All(m => m.Completed && !m.Restored)
            : previous.Moves.All(m => m.Completed && !m.Restored));
        Assert.Equal(previous.OperationId.ToString("N"), await File.ReadAllTextAsync(Journal + ".rollback-started"));

        // Reproduce an exit after reverse rename but before its receipt save.
        if (directory) Directory.Move(Destination(2), Source(2));
        else File.Move(Destination(2), Source(2));
        if (missingPrimary) File.Delete(Journal);
        else await File.WriteAllTextAsync(Journal, "{broken recovering primary");

        var restarted = new DesktopOrganizationTransaction(Journal);
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.CommitAsync(previous.OperationId));
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.RecoverAsync(
            committedTransactionIds: new HashSet<Guid> { previous.OperationId }));
        Assert.Equal(backup, await File.ReadAllTextAsync(Journal + ".bak"));
        Assert.True(restarted.HasRecoveryJournal);
        await Assert.ThrowsAsync<IOException>(() => restarted.RecoverAsync());
        Assert.True(File.Exists(Journal + ".rollback-started"));
        Assert.Equal("data2", await File.ReadAllTextAsync(Content(Source(2), directory)));
        Assert.Equal("data1", await File.ReadAllTextAsync(Content(Destination(1), directory)));
    }

    [Theory]
    [InlineData("fence-only")]
    [InlineData("truncated-fence")]
    [InlineData("foreign-fence")]
    public async Task UnreconciledRollbackFenceBlocksRecoveryAndNewMoves(string damage)
    {
        await MovePair(false);
        var transaction = new DesktopOrganizationTransaction(Journal, restoreGuard: _ => false);
        await Assert.ThrowsAsync<IOException>(() => transaction.RecoverAsync());
        var journal = JsonSerializer.Deserialize<OrganizationRecoveryJournal>(await File.ReadAllTextAsync(Journal),
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        if (damage == "fence-only")
        {
            File.Delete(Journal + ".bak");
            File.Delete(Journal);
        }
        else await File.WriteAllTextAsync(Journal + ".rollback-started",
            damage == "truncated-fence" ? "" : Guid.NewGuid().ToString("N"));
        var restarted = new DesktopOrganizationTransaction(Journal);
        Assert.True(restarted.HasRecoveryJournal);
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.CommitAsync(journal.OperationId));
        await Assert.ThrowsAsync<InvalidDataException>(() => restarted.RecoverAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => restarted.ExecuteAsync(
            [new(Destination(1), Source(1))]));
        Assert.True(File.Exists(Journal + ".rollback-started"));
        Assert.Equal("data1", await File.ReadAllTextAsync(Destination(1)));
        Assert.Equal("data2", await File.ReadAllTextAsync(Destination(2)));
    }

    public void Dispose() => Directory.Delete(root, true);
}
