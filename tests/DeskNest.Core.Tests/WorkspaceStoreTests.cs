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
        var mapped = new WorkspaceSpace(Guid.NewGuid(), "Studio", "Art files", SpaceStorageMode.Mapped, temp.Path);
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
            Assert.Single(reopened.Snapshot.Spaces);
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
