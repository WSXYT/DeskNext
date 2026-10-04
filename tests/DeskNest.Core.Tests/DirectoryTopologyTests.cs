using System.Text.Json;
using DeskNest.Core.Storage;
using DeskNest.Core.Workspace;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class DirectoryTopologyTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        "DeskNest-topology-" + Guid.NewGuid())).FullName;
    private string Source => Path.Combine(root, "source", "tree");
    private string Destination => Path.Combine(root, "target", "tree");
    private string Journal => Path.Combine(root, "operation.json");
    private string StoreDirectory => Path.Combine(root, "state");

    private void CreateTree()
    {
        Directory.CreateDirectory(Path.Combine(Source, "empty", "nested"));
        Directory.CreateDirectory(Path.Combine(root, "target"));
        File.WriteAllText(Path.Combine(Source, "content.txt"), "preserve");
    }

    private static void ChangeTree(string path, string change)
    {
        string empty = Path.Combine(path, "empty", "nested");
        switch (change)
        {
            case "add": Directory.CreateDirectory(Path.Combine(path, "new-empty")); break;
            case "remove": Directory.Delete(empty); break;
            case "rename": Directory.Move(empty, empty + "-renamed"); break;
            case "replace": Directory.Delete(empty); File.WriteAllText(empty, ""); break;
            default: throw new ArgumentOutOfRangeException(nameof(change));
        }
    }

    [Fact]
    public async Task EmptyDirectoriesSurviveJournalRestartRecovery()
    {
        CreateTree();
        var result = await new DesktopOrganizationTransaction(Journal).ExecuteDirectoriesAsync(
            [new(Source, Destination)], retainJournalUntilCommit: true);
        var receipt = Assert.Single(result.DirectoryReceipts!);
        Assert.Equal(new[] { "empty", Path.Combine("empty", "nested") }, receipt.Directories);
        var restarted = new DesktopOrganizationTransaction(Journal);
        await restarted.RecoverAsync();
        Assert.True(Directory.Exists(Path.Combine(Source, "empty", "nested")));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(Source, "content.txt")));
        Assert.False(Directory.Exists(Destination));
        Assert.False(restarted.HasRecoveryJournal);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("rename")]
    [InlineData("replace")]
    public async Task ChangedTopologyRefusesForwardMoveAndPreservesEvidence(string change)
    {
        CreateTree();
        var transaction = new DesktopOrganizationTransaction(Journal, directoryMoveGuard: _ =>
        {
            ChangeTree(Source, change);
            return true;
        });
        await Assert.ThrowsAsync<IOException>(() => transaction.ExecuteDirectoriesAsync([new(Source, Destination)]));
        Assert.True(Directory.Exists(Source));
        Assert.False(Directory.Exists(Destination));
        Assert.True(transaction.HasRecoveryJournal);
        await Assert.ThrowsAsync<IOException>(() => new DesktopOrganizationTransaction(Journal).RecoverAsync());
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(Source, "content.txt")));
    }

    [Theory]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("rename")]
    [InlineData("replace")]
    public async Task ChangedTopologyRefusesRecoveryAndKeepsDestination(string change)
    {
        CreateTree();
        await new DesktopOrganizationTransaction(Journal).ExecuteDirectoriesAsync(
            [new(Source, Destination)], retainJournalUntilCommit: true);
        ChangeTree(Destination, change);
        var restarted = new DesktopOrganizationTransaction(Journal);
        await Assert.ThrowsAsync<IOException>(() => restarted.RecoverAsync());
        Assert.True(restarted.HasRecoveryJournal);
        Assert.False(Directory.Exists(Source));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(Destination, "content.txt")));
    }

    [Fact]
    public async Task ChangedCheckpointedTopologyStopsBeforeRemainingReverseMoves()
    {
        CreateTree();
        string second = Path.Combine(root, "second");
        string secondTarget = Path.Combine(root, "second-target");
        Directory.CreateDirectory(Path.Combine(second, "empty"));
        await new DesktopOrganizationTransaction(Journal).ExecuteDirectoriesAsync(
            [new(Source, Destination), new(second, secondTarget)], retainJournalUntilCommit: true);
        int restores = 0;
        await Assert.ThrowsAsync<IOException>(() => new DesktopOrganizationTransaction(Journal,
            restoreGuard: _ => ++restores == 1).RecoverAsync());
        Directory.CreateDirectory(Path.Combine(second, "unexpected"));
        int retries = 0;
        var restarted = new DesktopOrganizationTransaction(Journal, restoreGuard: _ => { retries++; return true; });
        await Assert.ThrowsAsync<IOException>(() => restarted.RecoverAsync());
        Assert.Equal(0, retries);
        Assert.True(Directory.Exists(Destination));
        Assert.False(Directory.Exists(Source));
        Assert.True(restarted.HasRecoveryJournal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyJournalWithoutTopologyCannotAuthorizeRecoveryOrAcknowledgement(bool committed)
    {
        CreateTree();
        Directory.Move(Source, Destination);
        var journal = new OrganizationRecoveryJournal(Guid.NewGuid(), "Moving", [],
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            [new OrganizationDirectoryMoveReceipt(Source, Destination,
                DesktopOrganizationTransaction.CaptureDirectoryManifest(Destination), true)]);
        string json = JsonSerializer.Serialize(journal);
        await File.WriteAllTextAsync(Journal, json);
        await File.WriteAllTextAsync(Journal + ".bak", json);
        var transaction = new DesktopOrganizationTransaction(Journal);
        if (committed)
            await Assert.ThrowsAsync<InvalidDataException>(() => transaction.CommitAsync(journal.OperationId));
        else
            await Assert.ThrowsAsync<InvalidDataException>(() => transaction.RecoverAsync());
        Assert.True(transaction.HasRecoveryJournal);
        Assert.True(File.Exists(Journal + ".recovery-required"));
        Assert.Equal(json, await File.ReadAllTextAsync(Journal));
        Assert.Equal(json, await File.ReadAllTextAsync(Journal + ".bak"));
        Assert.True(Directory.Exists(Destination));
        Assert.False(Directory.Exists(Source));
    }

    private async Task<(Guid File, Guid Target)> SeedWorkspace(WorkspaceStore store)
    {
        CreateTree();
        var source = new WorkspaceSpace(Guid.NewGuid(), "Source", "", SpaceStorageMode.Managed,
            Path.GetDirectoryName(Source)!);
        var target = new WorkspaceSpace(Guid.NewGuid(), "Target", "", SpaceStorageMode.Managed,
            Path.GetDirectoryName(Destination)!);
        var file = new WorkspaceFile(Guid.NewGuid(), source.Id, "tree", Source, true);
        await store.UpdateAsync(s => s with { Spaces = [source, target], Files = [file] });
        return (file.Id, target.Id);
    }

    [Theory]
    [InlineData("unchanged")]
    [InlineData("add")]
    [InlineData("remove")]
    [InlineData("rename")]
    [InlineData("replace")]
    [InlineData("legacy")]
    public async Task WorkspaceRestartUndoRequiresPersistedUnchangedTopology(string change)
    {
        Guid operationId;
        await using (var store = await WorkspaceStore.OpenAsync(StoreDirectory))
        {
            var ids = await SeedWorkspace(store);
            var coordinator = new ManualOrganizationCoordinator(store, new DesktopOrganizationTransaction(Journal));
            await coordinator.MoveFileAsync(ids.File, ids.Target);
            var operation = Assert.Single(store.Snapshot.Operations);
            operationId = operation.Id;
            Assert.Equal(new[] { "empty", Path.Combine("empty", "nested") }, operation.OriginalDirectoryPaths);
            Assert.Equal(3, operation.OriginalDirectoryNativeIds?.Count);
            if (change == "legacy")
                await store.UpdateAsync(s => s with
                {
                    Operations = [operation with { OriginalDirectoryPaths = null, OriginalDirectoryNativeIds = null }]
                });
        }
        if (change is not ("unchanged" or "legacy")) ChangeTree(Destination, change);
        await using var reopened = await WorkspaceStore.OpenAsync(StoreDirectory);
        var restarted = new ManualOrganizationCoordinator(reopened, new DesktopOrganizationTransaction(Journal));
        if (change == "unchanged")
        {
            await restarted.UndoOperationAsync(operationId);
            Assert.Equal(ProposedOperationStatus.Undone, Assert.Single(reopened.Snapshot.Operations).Status);
            Assert.Equal(Source, Assert.Single(reopened.Snapshot.Files).Path);
            Assert.True(Directory.Exists(Path.Combine(Source, "empty", "nested")));
            Assert.False(Directory.Exists(Destination));
        }
        else
        {
            if (change == "legacy")
                await Assert.ThrowsAsync<InvalidDataException>(() => restarted.UndoOperationAsync(operationId));
            else
                await Assert.ThrowsAsync<IOException>(() => restarted.UndoOperationAsync(operationId));
            Assert.Equal(ProposedOperationStatus.RecoveryRequired, Assert.Single(reopened.Snapshot.Operations).Status);
            Assert.Equal(Destination, Assert.Single(reopened.Snapshot.Files).Path);
            Assert.True(Directory.Exists(Destination));
            Assert.False(Directory.Exists(Source));
        }
        Assert.False(new DesktopOrganizationTransaction(Journal).HasRecoveryJournal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LegacyDeepManifestReopensButCannotAuthorizeUndo(bool fromBackup)
    {
        WorkspaceState legacy;
        Guid operationId;
        await using (var store = await WorkspaceStore.OpenAsync(StoreDirectory))
        {
            var ids = await SeedWorkspace(store);
            await new ManualOrganizationCoordinator(store, new DesktopOrganizationTransaction(Journal))
                .MoveFileAsync(ids.File, ids.Target);
            legacy = store.Snapshot;
            operationId = Assert.Single(legacy.Operations).Id;
        }

        // The old file-only schema allowed 129 components below its 4,096-character bound.
        string relative = Path.Combine(Enumerable.Repeat("a", 128).Append("entry.txt").ToArray());
        string deepFile = Path.Combine(Destination, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(deepFile)!);
        await File.WriteAllTextAsync(deepFile, "legacy content");
        var identity = FileIdentity.Capture(deepFile);
        var operation = Assert.Single(legacy.Operations);
        legacy = legacy with
        {
            Operations = [operation with
            {
                OriginalDirectoryPaths = null,
                OriginalDirectoryNativeIds = null,
                OriginalDirectoryManifest = [.. operation.OriginalDirectoryManifest!,
                    new(relative, identity.Length, identity.LastWriteTimeUtcTicks, identity.Sha256)]
            }]
        };
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };
        string json = JsonSerializer.Serialize(legacy, jsonOptions);
        Assert.DoesNotContain("originalDirectoryPaths", json);
        Assert.DoesNotContain("originalDirectoryNativeIds", json);
        string workspace = Path.Combine(StoreDirectory, "workspace.json");
        await File.WriteAllTextAsync(workspace, json);
        await File.WriteAllTextAsync(ResilientJsonStore.GetBackupPath(workspace), json);
        if (fromBackup) await File.WriteAllTextAsync(workspace, "{damaged primary");

        await using (var reopened = await WorkspaceStore.OpenAsync(StoreDirectory))
        {
            Assert.Null(Assert.Single(reopened.Snapshot.Operations).OriginalDirectoryPaths);
            Assert.Contains(Assert.Single(reopened.Snapshot.Operations).OriginalDirectoryManifest!,
                entry => entry.RelativePath == relative);
            var coordinator = new ManualOrganizationCoordinator(reopened, new DesktopOrganizationTransaction(Journal));
            await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.UndoOperationAsync(operationId));
            Assert.Equal(ProposedOperationStatus.RecoveryRequired, Assert.Single(reopened.Snapshot.Operations).Status);
            // Presence of a new topology field must still select strict validation.
            long revision = reopened.Snapshot.Revision;
            await Assert.ThrowsAsync<InvalidDataException>(() => reopened.UpdateAsync(s => s with
            {
                Operations = [Assert.Single(s.Operations) with { OriginalDirectoryPaths = [] }]
            }));
            Assert.Equal(revision, reopened.Snapshot.Revision);
        }
        await using var restarted = await WorkspaceStore.OpenAsync(StoreDirectory);
        Assert.Equal(ProposedOperationStatus.RecoveryRequired, Assert.Single(restarted.Snapshot.Operations).Status);
        Assert.False(Directory.Exists(Source));
        Assert.Equal("legacy content", await File.ReadAllTextAsync(deepFile));
        Assert.False(new DesktopOrganizationTransaction(Journal).HasRecoveryJournal);
        Assert.False(File.Exists(workspace + ".recovery-required"));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("a/../b")]
    [InlineData("a//b")]
    [InlineData(".")]
    [InlineData("")]
    [InlineData("C:relative")]
    [InlineData(null)]
    public async Task WorkspaceRefusesInvalidTopologyWithoutSaving(string? entry)
    {
        await using var store = await WorkspaceStore.OpenAsync(StoreDirectory);
        var ids = await SeedWorkspace(store);
        long revision = store.Snapshot.Revision;
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateAsync(s => s with
        {
            Operations = [new ProposedOperation(Guid.NewGuid(), ids.File, ids.Target,
                ProposedOperationStatus.Completed, DateTimeOffset.UtcNow)
            {
                OriginalDirectoryManifest = [], OriginalDirectoryPaths = [entry!]
            }]
        }));
        Assert.Equal(revision, store.Snapshot.Revision);
        Assert.Empty(store.Snapshot.Operations);
    }

    [Fact]
    public void ManifestRejectsDuplicateConflictingAndMissingAncestorPaths()
    {
        var identity = new FileIdentity(0, 0, new string('0', 64));
        Assert.Throws<InvalidDataException>(() => DesktopOrganizationTransaction.ValidateDirectoryManifest([], ["a", "a"]));
        Assert.Throws<InvalidDataException>(() => DesktopOrganizationTransaction.ValidateDirectoryManifest([], ["a/b"]));
        Assert.Throws<InvalidDataException>(() => DesktopOrganizationTransaction.ValidateDirectoryManifest([new("a", identity)], ["a"]));
        Assert.Throws<InvalidDataException>(() => DesktopOrganizationTransaction.ValidateDirectoryManifest([new("a/b", identity)], []));
        Assert.Throws<InvalidDataException>(() => DesktopOrganizationTransaction.ValidateDirectoryManifest(
            [new("a", identity), new("a", identity)], []));
        Assert.Throws<InvalidDataException>(() => DesktopOrganizationTransaction.ValidateDirectoryManifest(
            [new("a", identity with { Sha256 = new string('z', 64) })], []));
    }

    [Fact]
    public void ManifestEntryAndDepthBudgetsHaveExactBoundaries()
    {
        int limit = DesktopOrganizationTransaction.MaximumDirectoryEntries;
        var directories = Enumerable.Range(0, limit).Select(i => "dir" + i).ToArray();
        DesktopOrganizationTransaction.ValidateDirectoryManifest([], directories);
        Assert.Throws<InvalidDataException>(() => DesktopOrganizationTransaction.ValidateDirectoryManifest([], [.. directories, "overflow"]));
        int depth = DesktopOrganizationTransaction.MaximumDirectoryDepth;
        Assert.True(DesktopOrganizationTransaction.IsValidManifestPath(string.Join('/', Enumerable.Repeat("a", depth))));
        Assert.False(DesktopOrganizationTransaction.IsValidManifestPath(string.Join('/', Enumerable.Repeat("a", depth + 1))));
    }

    [Fact]
    public void CaptureRejectsOverDeepTreeBeforeJournalCreation()
    {
        string leaf = Source;
        for (int i = 0; i <= DesktopOrganizationTransaction.MaximumDirectoryDepth; i++)
            leaf = Path.Combine(leaf, "a");
        Directory.CreateDirectory(leaf);
        Assert.Throws<IOException>(() => DesktopOrganizationTransaction.CaptureDirectorySnapshot(Source));
        Assert.False(File.Exists(Journal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FilelessTreesHaveExplicitTopologyAndRecover(bool nested)
    {
        Directory.CreateDirectory(nested ? Path.Combine(Source, "empty") : Source);
        var transaction = new DesktopOrganizationTransaction(Journal);
        var result = await transaction.ExecuteDirectoriesAsync([new(Source, Destination)],
            retainJournalUntilCommit: true);
        var receipt = Assert.Single(result.DirectoryReceipts!);
        Assert.Empty(receipt.Files);
        Assert.NotNull(receipt.Directories);
        Assert.Equal(nested ? new[] { "empty" } : [], receipt.Directories);
        await new DesktopOrganizationTransaction(Journal).RecoverAsync();
        Assert.True(Directory.Exists(Source));
        Assert.Equal(nested, Directory.Exists(Path.Combine(Source, "empty")));
        Assert.False(Directory.Exists(Destination));
        Assert.False(transaction.HasRecoveryJournal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RootAndChildDirectoryLinksAreRefusedWithoutFollowing(bool atRoot)
    {
        string outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        string sentinel = Path.Combine(outside, "sentinel.txt");
        File.WriteAllText(sentinel, "untouched");
        string link = atRoot ? Source : Path.Combine(Source, "link");
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (OperatingSystem.IsWindows())
        {
            // Junction creation needs no Developer Mode or elevation on the Windows test host.
            var start = new System.Diagnostics.ProcessStartInfo(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                $"/d /c mklink /J \"{link}\" \"{outside}\"")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            using var process = System.Diagnostics.Process.Start(start)!;
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                throw new TimeoutException("Test junction creation did not finish.");
            }
            Assert.True(process.ExitCode == 0, process.StandardError.ReadToEnd());
        }
        else Directory.CreateSymbolicLink(link, outside);
        try
        {
            Assert.Throws<IOException>(() => DesktopOrganizationTransaction.CaptureDirectorySnapshot(Source));
            Assert.Equal("untouched", File.ReadAllText(sentinel));
            Assert.False(File.Exists(Journal));
        }
        finally { Directory.Delete(link); }
    }

    public void Dispose() => Directory.Delete(root, true);
}
