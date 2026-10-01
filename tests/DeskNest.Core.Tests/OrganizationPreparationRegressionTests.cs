using DeskNest.Core.Storage;
using DeskNest.Core.Workspace;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class OrganizationPreparationRegressionTests
{
    [Fact]
    public async Task CommitUsesPreparedReceiptAfterSourceChangesDuringPendingMetadataSave()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
            "DeskNest-receipt-" + Guid.NewGuid().ToString("N"))).FullName;
        try
        {
            string source = Path.Combine(root, "source.txt");
            await File.WriteAllTextAsync(source, "preliminary");
            var preliminary = FileIdentity.Capture(source);
            var space = new WorkspaceSpace(Guid.NewGuid(), "Space", "", SpaceStorageMode.Managed, root);
            var file = new WorkspaceFile(Guid.NewGuid(), space.Id, "source.txt", source, false);
            Guid operationId;
            await using (var store = await WorkspaceStore.OpenAsync(root))
            {
                await store.UpdateAsync(s => s with { Spaces = [space], Files = [file] });
                using var releaseMetadata = new ManualResetEventSlim();
                var metadataHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                Task holder = Task.Run(() => store.UpdateAsync(s =>
                {
                    metadataHeld.SetResult();
                    if (!releaseMetadata.Wait(TimeSpan.FromSeconds(15))) throw new TimeoutException();
                    return s;
                }));
                Task? moving = null;
                OrganizationMoveReceipt? receipt = null;
                try
                {
                    await metadataHeld.Task.WaitAsync(TimeSpan.FromSeconds(15));
                    var transaction = new DesktopOrganizationTransaction(Path.Combine(root, "operation.json"),
                        moveGuard: r => { receipt = r; return true; });
                    var coordinator = new ManualOrganizationCoordinator(store, transaction);
                    // No Task.Run: the coordinator runs synchronously through capture until
                    // its pending-metadata UpdateAsync awaits the gate held above.
                    moving = coordinator.RenameFileAsync(file.Id, "renamed.txt");
                    Assert.False(moving.IsCompleted);
                    await File.WriteAllTextAsync(source, "authoritative content changed before Prepare");
                }
                finally
                {
                    releaseMetadata.Set();
                    await holder.WaitAsync(TimeSpan.FromSeconds(15));
                    if (moving is not null) await moving.WaitAsync(TimeSpan.FromSeconds(15));
                }
                Assert.NotNull(receipt);
                Assert.NotEqual(preliminary, receipt.Identity);
                var operation = Assert.Single(store.Snapshot.Operations);
                operationId = operation.Id;
                Assert.Equal(receipt.Identity.Length, operation.OriginalLength);
                Assert.Equal(receipt.Identity.LastWriteTimeUtcTicks, operation.OriginalLastWriteUtcTicks);
                Assert.Equal(receipt.Identity.Sha256, operation.OriginalSha256);
                Assert.Equal(receipt.Identity.NativeId, operation.OriginalNativeId);
                Assert.NotNull(operation.CommittedTransactionId);
            }
            await using var reopened = await WorkspaceStore.OpenAsync(root);
            await new ManualOrganizationCoordinator(reopened,
                new DesktopOrganizationTransaction(Path.Combine(root, "operation.json"))).UndoOperationAsync(operationId);
            Assert.Equal("authoritative content changed before Prepare", await File.ReadAllTextAsync(source));
            Assert.Equal(ProposedOperationStatus.Undone, Assert.Single(reopened.Snapshot.Operations).Status);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [TwoLocalVolumesFact]
    public async Task CrossVolumeBatchIsRejectedBeforeAnyMoveJournalOrParentCreation()
    {
        string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
            "DeskNest-cross-volume-" + Guid.NewGuid().ToString("N"))).FullName;
        string other = Path.Combine(Environment.CurrentDirectory, "artifacts",
            "cross-volume-" + Guid.NewGuid().ToString("N"));
        try
        {
            string first = Path.Combine(root, "first.txt");
            string second = Path.Combine(root, "second.txt");
            string firstDestination = Path.Combine(root, "first-moved.txt");
            await File.WriteAllTextAsync(first, "first");
            await File.WriteAllTextAsync(second, "second");
            Assert.NotEqual(FileSystemVolume.Identify(root), FileSystemVolume.Identify(other));
            var transaction = new DesktopOrganizationTransaction(Path.Combine(root, "operation.json"));
            await Assert.ThrowsAsync<NotSupportedException>(() => transaction.ExecuteAsync([
                new(first, firstDestination), new(second, Path.Combine(other, "second.txt"))]));
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(second));
            Assert.False(File.Exists(firstDestination));
            Assert.False(Directory.Exists(other));
            Assert.False(transaction.HasRecoveryJournal);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
            if (Directory.Exists(other)) Directory.Delete(other, recursive: true);
        }
    }

    private sealed class TwoLocalVolumesFactAttribute : FactAttribute
    {
        public TwoLocalVolumesFactAttribute()
        {
            try
            {
                if (FileSystemVolume.Identify(Path.GetTempPath()) ==
                    FileSystemVolume.Identify(Environment.CurrentDirectory))
                    Skip = "Requires temp and test working directory on two distinct supported local volumes.";
            }
            catch (Exception ex) when (ex is IOException or NotSupportedException)
            {
                Skip = "Native local volume lookup unavailable: " + ex.Message;
            }
        }
    }
}
