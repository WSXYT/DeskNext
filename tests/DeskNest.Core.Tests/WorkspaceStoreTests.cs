using DeskNest.Core.Storage;
using DeskNest.Core.Workspace;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class WorkspaceStoreTests
{
    [Fact]
    public async Task PersistsRealSpacesSettingsAndTriageWithoutTouchingSourceFiles()
    {
        using var temp = new TemporaryDirectory();
        string source = Path.Combine(temp.Path, "leave-original.txt");
        await File.WriteAllTextAsync(source, "untouched");
        var mapped = new WorkspaceSpace(Guid.NewGuid(), "Studio", "Art files", SpaceStorageMode.Mapped, temp.Path)
            { WindowPlacement = new(-1200, 40, 420, 480) };
        var pending = new PendingFile(Guid.NewGuid(), "leave-original.txt", source,
            TriageReason.CategoriesInsufficient, mapped.Id, DateTimeOffset.UtcNow);
        await using (var store = await WorkspaceStore.OpenAsync(temp.Path))
        {
            Assert.Empty(store.Snapshot.Spaces);
            var saved = await store.UpdateAsync(state => state with
            {
                OnboardingStep = 5,
                OnboardingComplete = true,
                Preset = "custom",
                Settings = state.Settings with { Language = "ar-SA", Provider = InferenceProvider.Jev },
                Spaces = [mapped],
                Pending = [pending]
            });
            Assert.Equal(1, saved.Revision);
            saved.Spaces.Clear(); // public snapshots must never mutate the durable state
            Assert.Single(store.Snapshot.Spaces);
        }
        await using (var reopened = await WorkspaceStore.OpenAsync(temp.Path))
        {
            Assert.Equal("ar-SA", reopened.Snapshot.Settings.Language);
            Assert.Equal(InferenceProvider.Jev, reopened.Snapshot.Settings.Provider);
            Assert.Equal(mapped.WindowPlacement, Assert.Single(reopened.Snapshot.Spaces).WindowPlacement);
            Assert.Single(reopened.Snapshot.Pending);
        }
        Assert.Equal("untouched", await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task RejectsDuplicateSpaceAndConcurrentWriterWithoutLosingLastSnapshot()
    {
        using var temp = new TemporaryDirectory();
        await using var store = await WorkspaceStore.OpenAsync(temp.Path);
        await Assert.ThrowsAnyAsync<IOException>(() => WorkspaceStore.OpenAsync(temp.Path));
        var space = new WorkspaceSpace(Guid.NewGuid(), "One", "", SpaceStorageMode.Managed, temp.Path);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateAsync(state => state with { Spaces = [space, space] }));
        Assert.Equal(0, store.Snapshot.Revision);
        Assert.Empty(store.Snapshot.Spaces);
        await store.UpdateAsync(state => state with { Spaces = [space] });
        Assert.Equal(1, store.Snapshot.Revision);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.UpdateAsync(state => state with
            { Spaces = [space with { WindowPlacement = new(0, 0, 0, 480) }] }));
        Assert.Equal(1, store.Snapshot.Revision);
        Assert.Null(Assert.Single(store.Snapshot.Spaces).WindowPlacement);
    }

    [Fact]
    public async Task CorruptPrimaryUsesBackupButDoubleCorruptionNeverResetsToEmpty()
    {
        using var temp = new TemporaryDirectory();
        string path = Path.Combine(temp.Path, "workspace.json");
        await using (var store = await WorkspaceStore.OpenAsync(temp.Path))
        {
            await store.UpdateAsync(state => state with { Preset = "office" });
            await store.UpdateAsync(state => state with { Preset = "creative" });
        }
        await File.WriteAllTextAsync(path, "{invalid");
        await using (var recovered = await WorkspaceStore.OpenAsync(temp.Path))
            Assert.Equal("office", recovered.Snapshot.Preset);
        await File.WriteAllTextAsync(path, "{invalid");
        await File.WriteAllTextAsync(path + ".bak", "{invalid");
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkspaceStore.OpenAsync(temp.Path));
        Assert.True(File.Exists(path + ".recovery-required"));
        // The imported resilient store quarantines corrupt copies; next boot must not
        // interpret their absence as a legitimate clean install.
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkspaceStore.OpenAsync(temp.Path));
    }

    [Fact]
    public async Task FutureSchemaFailsClosedWithoutOverwritingUserMetadata()
    {
        using var temp = new TemporaryDirectory();
        string path = Path.Combine(temp.Path, "workspace.json");
        await using (var store = await WorkspaceStore.OpenAsync(temp.Path))
            await store.UpdateAsync(state => state with { Preset = "development" });
        var original = await File.ReadAllTextAsync(path);
        var future = original.Replace("\"schemaVersion\":1", "\"schemaVersion\":999", StringComparison.Ordinal);
        Assert.NotEqual(original, future);
        await File.WriteAllTextAsync(path, future);
        await Assert.ThrowsAsync<InvalidDataException>(() => WorkspaceStore.OpenAsync(temp.Path));
        Assert.Equal(future, await File.ReadAllTextAsync(path));
    }

    [Fact]
    public async Task ManualCoordinatorUpdatesMetadataOnlyAfterSuccessfulMove()
    {
        using var temp = new TemporaryDirectory();
        string sourceFolder = Path.Combine(temp.Path, "source");
        string targetFolder = Path.Combine(temp.Path, "target");
        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(targetFolder);
        string sourcePath = Path.Combine(sourceFolder, "source.txt");
        await File.WriteAllTextAsync(sourcePath, "manual");
        var sourceSpace = new WorkspaceSpace(Guid.NewGuid(), "Source", "", SpaceStorageMode.Managed, sourceFolder);
        var targetSpace = new WorkspaceSpace(Guid.NewGuid(), "Target", "", SpaceStorageMode.Managed, targetFolder);
        var file = new WorkspaceFile(Guid.NewGuid(), sourceSpace.Id, "source.txt", sourcePath, false);

        await using var store = await WorkspaceStore.OpenAsync(temp.Path);
        await store.UpdateAsync(state => state with { Spaces = [sourceSpace, targetSpace], Files = [file] });
        var result = await new ManualOrganizationCoordinator(
            store, new DesktopOrganizationTransaction(Path.Combine(temp.Path, "operation.json")))
            .MoveFileAsync(file.Id, targetSpace.Id);

        Assert.Equal(targetSpace.Id, result.TargetSpaceId);
        Assert.False(File.Exists(sourcePath));
        Assert.True(File.Exists(Path.Combine(targetFolder, "source.txt")));
        Assert.Equal(targetSpace.Id, store.Snapshot.Files.Single().SpaceId);
        Assert.Equal(Path.Combine(targetFolder, "source.txt"), store.Snapshot.Files.Single().Path);
        Assert.Equal(ProposedOperationStatus.Completed, store.Snapshot.Operations.Single().Status);
    }

    [Fact]
    public async Task ManualCoordinatorUndoesCompletedMoveThroughTransaction()
    {
        using var temp = new TemporaryDirectory();
        string sourceFolder = Path.Combine(temp.Path, "source");
        string targetFolder = Path.Combine(temp.Path, "target");
        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(targetFolder);
        string sourcePath = Path.Combine(sourceFolder, "source.txt");
        await File.WriteAllTextAsync(sourcePath, "undo");
        var sourceSpace = new WorkspaceSpace(Guid.NewGuid(), "Source", "", SpaceStorageMode.Managed, sourceFolder);
        var targetSpace = new WorkspaceSpace(Guid.NewGuid(), "Target", "", SpaceStorageMode.Managed, targetFolder);
        var file = new WorkspaceFile(Guid.NewGuid(), sourceSpace.Id, "source.txt", sourcePath, false);
        await using var store = await WorkspaceStore.OpenAsync(temp.Path);
        await store.UpdateAsync(state => state with { Spaces = [sourceSpace, targetSpace], Files = [file] });
        var coordinator = new ManualOrganizationCoordinator(
            store, new DesktopOrganizationTransaction(Path.Combine(temp.Path, "operation.json")));

        await coordinator.MoveFileAsync(file.Id, targetSpace.Id);
        var operation = store.Snapshot.Operations.Single();
        await coordinator.UndoOperationAsync(operation.Id);

        Assert.True(File.Exists(sourcePath));
        Assert.False(File.Exists(Path.Combine(targetFolder, "source.txt")));
        Assert.Equal(sourceSpace.Id, store.Snapshot.Files.Single().SpaceId);
        Assert.Equal(ProposedOperationStatus.Undone, store.Snapshot.Operations.Single().Status);
    }

    [Fact]
    public async Task ManualCoordinatorUndoesCompletedDirectoryMoveThroughManifest()
    {
        using var temp = new TemporaryDirectory();
        string sourceFolder = Path.Combine(temp.Path, "source");
        string targetFolder = Path.Combine(temp.Path, "target");
        string sourceDirectory = Path.Combine(sourceFolder, "project");
        Directory.CreateDirectory(Path.Combine(sourceDirectory, "nested"));
        Directory.CreateDirectory(targetFolder);
        string sourcePath = Path.Combine(sourceDirectory, "nested", "item.txt");
        await File.WriteAllTextAsync(sourcePath, "directory undo");
        var sourceSpace = new WorkspaceSpace(Guid.NewGuid(), "Source", "", SpaceStorageMode.Managed, sourceFolder);
        var targetSpace = new WorkspaceSpace(Guid.NewGuid(), "Target", "", SpaceStorageMode.Managed, targetFolder);
        var file = new WorkspaceFile(Guid.NewGuid(), sourceSpace.Id, "project", sourceDirectory, true);
        await using var store = await WorkspaceStore.OpenAsync(temp.Path);
        await store.UpdateAsync(state => state with { Spaces = [sourceSpace, targetSpace], Files = [file] });
        var coordinator = new ManualOrganizationCoordinator(
            store, new DesktopOrganizationTransaction(Path.Combine(temp.Path, "operation.json")));

        await coordinator.MoveFileAsync(file.Id, targetSpace.Id);
        var operation = store.Snapshot.Operations.Single();
        Assert.NotNull(operation.OriginalDirectoryManifest);
        await coordinator.UndoOperationAsync(operation.Id);

        Assert.True(File.Exists(sourcePath));
        Assert.False(Directory.Exists(Path.Combine(targetFolder, "project")));
        Assert.Equal(sourceDirectory, store.Snapshot.Files.Single().Path);
        Assert.Equal(ProposedOperationStatus.Undone, store.Snapshot.Operations.Single().Status);
    }

    [Fact]
    public async Task DirectoryUndoRefusesChangedManifestAndMarksRecoveryRequired()
    {
        using var temp = new TemporaryDirectory();
        string sourceFolder = Path.Combine(temp.Path, "source");
        string targetFolder = Path.Combine(temp.Path, "target");
        string sourceDirectory = Path.Combine(sourceFolder, "project");
        Directory.CreateDirectory(sourceDirectory);
        Directory.CreateDirectory(targetFolder);
        await File.WriteAllTextAsync(Path.Combine(sourceDirectory, "item.txt"), "original");
        var sourceSpace = new WorkspaceSpace(Guid.NewGuid(), "Source", "", SpaceStorageMode.Managed, sourceFolder);
        var targetSpace = new WorkspaceSpace(Guid.NewGuid(), "Target", "", SpaceStorageMode.Managed, targetFolder);
        var file = new WorkspaceFile(Guid.NewGuid(), sourceSpace.Id, "project", sourceDirectory, true);
        await using var store = await WorkspaceStore.OpenAsync(temp.Path);
        await store.UpdateAsync(state => state with { Spaces = [sourceSpace, targetSpace], Files = [file] });
        var coordinator = new ManualOrganizationCoordinator(
            store, new DesktopOrganizationTransaction(Path.Combine(temp.Path, "operation.json")));

        await coordinator.MoveFileAsync(file.Id, targetSpace.Id);
        var operation = store.Snapshot.Operations.Single();
        await File.WriteAllTextAsync(Path.Combine(targetFolder, "project", "item.txt"), "changed");

        await Assert.ThrowsAsync<IOException>(() => coordinator.UndoOperationAsync(operation.Id));
        Assert.Equal(ProposedOperationStatus.RecoveryRequired, store.Snapshot.Operations.Single().Status);
        Assert.True(File.Exists(Path.Combine(targetFolder, "project", "item.txt")));
        Assert.False(Directory.Exists(sourceDirectory));
    }

    [Fact]
    public async Task ManagedDeleteMovesIntoApplicationTrashAndUndoRestores()
    {
        using var temp = new TemporaryDirectory();
        string managedFolder = Path.Combine(temp.Path, "managed");
        Directory.CreateDirectory(managedFolder);
        string sourcePath = Path.Combine(managedFolder, "remove.txt");
        await File.WriteAllTextAsync(sourcePath, "trash me");
        var space = new WorkspaceSpace(Guid.NewGuid(), "Managed", "", SpaceStorageMode.Managed, managedFolder);
        var file = new WorkspaceFile(Guid.NewGuid(), space.Id, "remove.txt", sourcePath, false);
        await using var store = await WorkspaceStore.OpenAsync(temp.Path);
        await store.UpdateAsync(state => state with { Spaces = [space], Files = [file] });
        var coordinator = new ManualOrganizationCoordinator(store,
            new DesktopOrganizationTransaction(Path.Combine(temp.Path, "operation.json")));

        await coordinator.DeleteManagedFileAsync(file.Id);
        var deleted = store.Snapshot.Files.Single();
        var operation = store.Snapshot.Operations.Single();
        Assert.True(deleted.IsInTrash);
        Assert.False(File.Exists(sourcePath));
        Assert.True(File.Exists(deleted.Path));
        Assert.StartsWith(Path.Combine(managedFolder, ".desknest-trash"), deleted.Path,
            StringComparison.OrdinalIgnoreCase);

        await coordinator.UndoOperationAsync(operation.Id);
        Assert.False(store.Snapshot.Files.Single().IsInTrash);
        Assert.True(File.Exists(sourcePath));
        Assert.False(File.Exists(deleted.Path));
        Assert.Equal(ProposedOperationStatus.Undone, store.Snapshot.Operations.Single().Status);
    }

    [Fact]
    public async Task ManagedDeleteRefusesMappedReference()
    {
        using var temp = new TemporaryDirectory();
        string sourcePath = Path.Combine(temp.Path, "mapped.txt");
        await File.WriteAllTextAsync(sourcePath, "keep");
        var space = new WorkspaceSpace(Guid.NewGuid(), "Mapped", "", SpaceStorageMode.Mapped, temp.Path);
        var file = new WorkspaceFile(Guid.NewGuid(), space.Id, "mapped.txt", sourcePath, false);
        await using var store = await WorkspaceStore.OpenAsync(temp.Path);
        await store.UpdateAsync(state => state with { Spaces = [space], Files = [file] });
        var coordinator = new ManualOrganizationCoordinator(store,
            new DesktopOrganizationTransaction(Path.Combine(temp.Path, "operation.json")));

        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.DeleteManagedFileAsync(file.Id));
        Assert.True(File.Exists(sourcePath));
        Assert.Empty(store.Snapshot.Operations);
    }


    [Fact]
    public async Task ManualCoordinatorRenamesManagedFileThroughTransactionAndUndo()
    {
        using var temp = new TemporaryDirectory();
        string managedFolder = Path.Combine(temp.Path, "managed");
        Directory.CreateDirectory(managedFolder);
        string sourcePath = Path.Combine(managedFolder, "old.txt");
        string renamedPath = Path.Combine(managedFolder, "new.txt");
        await File.WriteAllTextAsync(sourcePath, "rename");
        var space = new WorkspaceSpace(Guid.NewGuid(), "Managed", "", SpaceStorageMode.Managed, managedFolder);
        var file = new WorkspaceFile(Guid.NewGuid(), space.Id, "old.txt", sourcePath, false);
        await using var store = await WorkspaceStore.OpenAsync(temp.Path);
        await store.UpdateAsync(state => state with { Spaces = [space], Files = [file] });
        var coordinator = new ManualOrganizationCoordinator(
            store, new DesktopOrganizationTransaction(Path.Combine(temp.Path, "operation.json")));

        await coordinator.RenameFileAsync(file.Id, "new.txt");
        var operation = store.Snapshot.Operations.Single();
        Assert.False(File.Exists(sourcePath));
        Assert.True(File.Exists(renamedPath));
        Assert.Equal("new.txt", store.Snapshot.Files.Single().Name);
        Assert.Equal(ProposedOperationStatus.Completed, operation.Status);

        await coordinator.UndoOperationAsync(operation.Id);
        Assert.True(File.Exists(sourcePath));
        Assert.False(File.Exists(renamedPath));
        Assert.Equal("old.txt", store.Snapshot.Files.Single().Name);
        Assert.Equal(ProposedOperationStatus.Undone, store.Snapshot.Operations.Single().Status);
    }

    [Fact]
    public async Task ManualCoordinatorRemovesMappedReferenceWithoutTouchingSource()
    {
        using var temp = new TemporaryDirectory();
        string externalFolder = Path.Combine(temp.Path, "external");
        Directory.CreateDirectory(externalFolder);
        string sourcePath = Path.Combine(externalFolder, "reference.txt");
        await File.WriteAllTextAsync(sourcePath, "preserve");
        var space = new WorkspaceSpace(Guid.NewGuid(), "Mapped", "", SpaceStorageMode.Mapped, externalFolder);
        var file = new WorkspaceFile(Guid.NewGuid(), space.Id, "reference.txt", sourcePath, false);
        await using var store = await WorkspaceStore.OpenAsync(temp.Path);
        await store.UpdateAsync(state => state with { Spaces = [space], Files = [file] });
        var coordinator = new ManualOrganizationCoordinator(
            store, new DesktopOrganizationTransaction(Path.Combine(temp.Path, "operation.json")));

        await coordinator.RemoveMappedReferenceAsync(file.Id);

        Assert.Empty(store.Snapshot.Files);
        Assert.Equal("preserve", await File.ReadAllTextAsync(sourcePath));
    }

    [Fact]
    public async Task UndoRefusesModifiedDestinationAndMarksRecoveryRequired()
    {
        using var temp = new TemporaryDirectory();
        string sourceFolder = Path.Combine(temp.Path, "source");
        string targetFolder = Path.Combine(temp.Path, "target");
        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(targetFolder);
        string sourcePath = Path.Combine(sourceFolder, "source.txt");
        string destinationPath = Path.Combine(targetFolder, "source.txt");
        await File.WriteAllTextAsync(sourcePath, "undo-safe");
        var sourceSpace = new WorkspaceSpace(Guid.NewGuid(), "Source", "", SpaceStorageMode.Managed, sourceFolder);
        var targetSpace = new WorkspaceSpace(Guid.NewGuid(), "Target", "", SpaceStorageMode.Managed, targetFolder);
        var file = new WorkspaceFile(Guid.NewGuid(), sourceSpace.Id, "source.txt", sourcePath, false);
        await using var store = await WorkspaceStore.OpenAsync(temp.Path);
        await store.UpdateAsync(state => state with { Spaces = [sourceSpace, targetSpace], Files = [file] });
        var coordinator = new ManualOrganizationCoordinator(
            store, new DesktopOrganizationTransaction(Path.Combine(temp.Path, "operation.json")));

        await coordinator.MoveFileAsync(file.Id, targetSpace.Id);
        var operation = store.Snapshot.Operations.Single();
        await File.WriteAllTextAsync(destinationPath, "modified");

        await Assert.ThrowsAsync<IOException>(() => coordinator.UndoOperationAsync(operation.Id));
        Assert.Equal(ProposedOperationStatus.RecoveryRequired, store.Snapshot.Operations.Single().Status);
        Assert.Equal("modified", await File.ReadAllTextAsync(destinationPath));
        Assert.False(File.Exists(sourcePath));
    }

    [Fact]
    public async Task ManualCoordinatorLeavesMetadataUnchangedWhenMoveIsRefused()
    {
        using var temp = new TemporaryDirectory();
        string sourceFolder = Path.Combine(temp.Path, "source");
        string targetFolder = Path.Combine(temp.Path, "target");
        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(targetFolder);
        string sourcePath = Path.Combine(sourceFolder, "source.txt");
        await File.WriteAllTextAsync(sourcePath, "manual");
        var sourceSpace = new WorkspaceSpace(Guid.NewGuid(), "Source", "", SpaceStorageMode.Managed, sourceFolder);
        var targetSpace = new WorkspaceSpace(Guid.NewGuid(), "Target", "", SpaceStorageMode.Managed, targetFolder);
        var file = new WorkspaceFile(Guid.NewGuid(), sourceSpace.Id, "source.txt", sourcePath, false);
        await using var store = await WorkspaceStore.OpenAsync(temp.Path);
        await store.UpdateAsync(state => state with { Spaces = [sourceSpace, targetSpace], Files = [file] });

        await Assert.ThrowsAsync<IOException>(() => new ManualOrganizationCoordinator(
            store, new DesktopOrganizationTransaction(Path.Combine(temp.Path, "operation.json"), _ => false))
            .MoveFileAsync(file.Id, targetSpace.Id));

        Assert.Equal(sourcePath, store.Snapshot.Files.Single().Path);
        Assert.Equal(ProposedOperationStatus.RecoveryRequired, store.Snapshot.Operations.Single().Status);
        Assert.True(File.Exists(sourcePath));
        Assert.False(File.Exists(Path.Combine(targetFolder, "source.txt")));
    }

    [Fact]
    public async Task CoordinatorMarksPendingOperationForManualRecoveryAfterRestart()
    {
        using var temp = new TemporaryDirectory();
        string sourceFolder = Path.Combine(temp.Path, "source");
        string targetFolder = Path.Combine(temp.Path, "target");
        Directory.CreateDirectory(sourceFolder);
        Directory.CreateDirectory(targetFolder);
        string sourcePath = Path.Combine(sourceFolder, "source.txt");
        string destinationPath = Path.Combine(targetFolder, "source.txt");
        await File.WriteAllTextAsync(sourcePath, "pending");
        var sourceSpace = new WorkspaceSpace(Guid.NewGuid(), "Source", "", SpaceStorageMode.Managed, sourceFolder);
        var targetSpace = new WorkspaceSpace(Guid.NewGuid(), "Target", "", SpaceStorageMode.Managed, targetFolder);
        var file = new WorkspaceFile(Guid.NewGuid(), sourceSpace.Id, "source.txt", sourcePath, false);
        await using var store = await WorkspaceStore.OpenAsync(temp.Path);
        await store.UpdateAsync(state => state with
        {
            Spaces = [sourceSpace, targetSpace],
            Files = [file],
            Operations = [new ProposedOperation(Guid.NewGuid(), file.Id, targetSpace.Id,
                ProposedOperationStatus.PendingUser, DateTimeOffset.UtcNow)
            {
                SourcePath = sourcePath,
                DestinationPath = destinationPath
            }]
        });

        await new ManualOrganizationCoordinator(
            store, new DesktopOrganizationTransaction(Path.Combine(temp.Path, "operation.json")))
            .RecoverPendingAsync();

        Assert.Equal(ProposedOperationStatus.RecoveryRequired, store.Snapshot.Operations.Single().Status);
        Assert.True(File.Exists(sourcePath));
        Assert.False(File.Exists(destinationPath));
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = Directory.CreateDirectory(System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "DeskNest.P2.Tests", Guid.NewGuid().ToString("N"))).FullName;
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}
