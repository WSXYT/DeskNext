using DeskNest.Core.Storage;
using DeskNest.Core.Workspace;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class CopyRecoveryTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        "DeskNext-copy-recovery-" + Guid.NewGuid().ToString("N"))).FullName;

    [WindowsHandleTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnenrolledCopyRetainsIntentAndObjectsAcrossRestart(bool directory)
    {
        var (source, target, file) = Fixture(directory);
        var journal = new CopyRecoveryJournal(root);
        await using (var store = await WorkspaceStore.OpenAsync(root))
        {
            await store.UpdateAsync(s => s with { Spaces = [source, target], Files = [file] });
            var coordinator = new ManualOrganizationCoordinator(store,
                new DesktopOrganizationTransaction(Path.Combine(root, "move.json")), _ => false);
            await Assert.ThrowsAsync<IOException>(() => coordinator.CopyFileAsync(file.Id, target.Id));
            Assert.True(journal.Exists);
            var intent = journal.Read();
            Assert.Equal(file.Id, intent.SourceFileId);
            Assert.Equal(Path.Combine(target.Folder, file.Name), intent.DestinationPath);
            Assert.Equal(Path.Combine(target.Folder, (directory ? ".desknext-tree-" : ".desknext-copy-") +
                intent.FileId.ToString("N")), intent.TemporaryPath);
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.MoveFileAsync(file.Id, target.Id));
        }
        byte[] before = File.ReadAllBytes(journal.JournalPath);
        await using var reopened = await WorkspaceStore.OpenAsync(root);
        var recovering = new ManualOrganizationCoordinator(reopened, new DesktopOrganizationTransaction(Path.Combine(root, "move.json")));
        await Assert.ThrowsAsync<InvalidDataException>(() => recovering.RecoverPendingAsync());
        Assert.Equal(before, File.ReadAllBytes(journal.JournalPath));
        Assert.Single(reopened.Snapshot.Files);
        Assert.Equal("preserved", File.ReadAllText(directory ? Path.Combine(file.Path, "item.txt") : file.Path));
        string destination = Path.Combine(target.Folder, file.Name);
        Assert.Equal("preserved", File.ReadAllText(directory ? Path.Combine(destination, "item.txt") : destination));
    }

    [WindowsHandleTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CommittedIntentOnlyAcknowledgesVerifiedDurablePublication(bool directory, bool tamper)
    {
        var (source, target, file) = Fixture(directory);
        var journal = new CopyRecoveryJournal(root);
        Guid copiedId;
        string destination;
        await using (var store = await WorkspaceStore.OpenAsync(root))
        {
            await store.UpdateAsync(s => s with { Spaces = [source, target], Files = [file] });
            var copying = new ManualOrganizationCoordinator(store,
                new DesktopOrganizationTransaction(Path.Combine(root, "move.json")));
            await Assert.ThrowsAsync<IOException>(() => copying.CopyFileAsync(file.Id, target.Id,
                beforeCopyAcknowledgement: () => throw new IOException("Injected stop before copy acknowledgement.")));
            // Actual coordinator failure, not a hand-authored replacement intent.
            var intent = journal.Read();
            copiedId = intent.FileId;
            destination = intent.DestinationPath;
            Assert.NotNull(store.Snapshot.Files.Single(f => f.Id == copiedId).Publication);
            Assert.Equal(File.ReadAllBytes(Path.Combine(root, "workspace.json")),
                File.ReadAllBytes(Path.Combine(root, "workspace.json.bak")));
        }
        if (tamper) File.WriteAllText(directory ? Path.Combine(destination, "item.txt") : destination, "external");
        await using var reopened = await WorkspaceStore.OpenAsync(root);
        var coordinator = new ManualOrganizationCoordinator(reopened, new DesktopOrganizationTransaction(Path.Combine(root, "move.json")));
        if (tamper)
        {
            await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.RecoverPendingAsync());
            Assert.True(journal.Exists);
            Assert.Equal("external", File.ReadAllText(directory ? Path.Combine(destination, "item.txt") : destination));
        }
        else
        {
            await coordinator.RecoverPendingAsync();
            Assert.False(journal.Exists);
            var copy = reopened.Snapshot.Files.Single(f => f.Id == copiedId);
            WindowsPublicationVerifier.Verify(copy.Path, copy.Publication!);
        }
        Assert.Equal(2, reopened.Snapshot.Files.Count);
        Assert.Empty(reopened.Snapshot.Operations);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("null")]
    [InlineData("{\"version\":99}")]
    public async Task CorruptIntentIsPreservedAndNeverTreatedAsMissing(string damaged)
    {
        await using var store = await WorkspaceStore.OpenAsync(root);
        var journal = new CopyRecoveryJournal(root);
        File.WriteAllText(journal.JournalPath, damaged);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ManualOrganizationCoordinator(store,
            new DesktopOrganizationTransaction(Path.Combine(root, "move.json"))).RecoverPendingAsync());
        Assert.True(journal.Exists);
        Assert.Equal(damaged, File.ReadAllText(journal.JournalPath));
    }

    [Fact]
    public async Task IntentCannotBeOverwrittenOrAcknowledgedWithAnotherId()
    {
        var (_, target, file) = Fixture(false);
        var journal = new CopyRecoveryJournal(root);
        var intent = new CopyRecoveryIntent(1, Guid.NewGuid(), file.Id, target.Id,
            file.Path, Path.Combine(target.Folder, file.Name), false);
        await journal.BeginAsync(intent, default);
        byte[] before = File.ReadAllBytes(journal.JournalPath);
        await Assert.ThrowsAsync<IOException>(() => journal.BeginAsync(intent with { FileId = Guid.NewGuid() }, default));
        Assert.Throws<InvalidDataException>(() => journal.Complete(intent with { FileId = Guid.NewGuid() }));
        Assert.Equal(before, File.ReadAllBytes(journal.JournalPath));
    }

    [WindowsHandleFact]
    public async Task PrecancelledCopyDoesNotCreateIntent()
    {
        var (source, target, file) = Fixture(false);
        await using var store = await WorkspaceStore.OpenAsync(root);
        await store.UpdateAsync(s => s with { Spaces = [source, target], Files = [file] });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new ManualOrganizationCoordinator(store,
            new DesktopOrganizationTransaction(Path.Combine(root, "move.json"))).CopyFileAsync(file.Id, target.Id, new(true)));
        Assert.False(new CopyRecoveryJournal(root).Exists);
        Assert.Empty(Directory.EnumerateFileSystemEntries(target.Folder));
    }

    [WindowsHandleTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StagedWriteFailureCannotPublishAndRetainsIntent(bool directory)
    {
        var (source, target, file) = Fixture(directory);
        string sourceContent = directory ? Path.Combine(file.Path, "item.txt") : file.Path;
        string expected = new string('x', 200_000);
        File.WriteAllText(sourceContent, expected);
        var journal = new CopyRecoveryJournal(root);
        string moveJournal = Path.Combine(root, "move.json");
        await using (var store = await WorkspaceStore.OpenAsync(root))
        {
            await store.UpdateAsync(s => s with { Spaces = [source, target], Files = [file] });
            long revision = store.Snapshot.Revision;
            var coordinator = new ManualOrganizationCoordinator(store, new DesktopOrganizationTransaction(moveJournal));
            bool reached = false;
            await Assert.ThrowsAsync<IOException>(() => coordinator.CopyFileAsync(file.Id, target.Id,
                afterFirstStagedWrite: () =>
                {
                    reached = true;
                    throw new IOException("Injected partial-write failure.");
                }));
            Assert.True(reached);
            Assert.Equal(revision, store.Snapshot.Revision);
            Assert.Single(store.Snapshot.Files);
            Assert.Empty(store.Snapshot.Operations);
            Assert.Equal(expected, File.ReadAllText(sourceContent));
            var intent = journal.Read();
            Assert.False(File.Exists(intent.DestinationPath) || Directory.Exists(intent.DestinationPath));
            if (directory)
            {
                Assert.Equal(new string('x', 65_536), File.ReadAllText(Path.Combine(intent.TemporaryPath, "item.txt")));
                Assert.True(Directory.Exists(Path.Combine(intent.TemporaryPath, "empty")));
            }
            else
            {
                // An ordinary exception closes/deletes only the owned temporary file handle;
                // SIGKILL bypasses that cleanup and is covered by the process probe.
                Assert.False(File.Exists(intent.TemporaryPath));
            }
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.MoveFileAsync(file.Id, target.Id));
        }
        byte[] retained = File.ReadAllBytes(journal.JournalPath);
        await using var reopened = await WorkspaceStore.OpenAsync(root);
        await Assert.ThrowsAsync<InvalidDataException>(() => new ManualOrganizationCoordinator(reopened,
            new DesktopOrganizationTransaction(moveJournal)).RecoverPendingAsync());
        Assert.Equal(retained, File.ReadAllBytes(journal.JournalPath));
        Assert.Equal(expected, File.ReadAllText(sourceContent));
        Assert.Single(reopened.Snapshot.Files);
    }

    private (WorkspaceSpace Source, WorkspaceSpace Target, WorkspaceFile File) Fixture(bool directory)
    {
        string left = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        string right = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
        var source = new WorkspaceSpace(Guid.NewGuid(), "Source", "", SpaceStorageMode.Managed, left);
        var target = new WorkspaceSpace(Guid.NewGuid(), "Target", "", SpaceStorageMode.Managed, right);
        string name = directory ? "tree" : "item.txt";
        string path = Path.Combine(left, name);
        if (directory)
        {
            Directory.CreateDirectory(Path.Combine(path, "empty"));
            File.WriteAllText(Path.Combine(path, "item.txt"), "preserved");
        }
        else File.WriteAllText(path, "preserved");
        return (source, target, new WorkspaceFile(Guid.NewGuid(), source.Id, name, path, directory));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}
