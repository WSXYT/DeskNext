using DeskNest.Core.Storage;
using DeskNest.Core.Workspace;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class DesktopOrganizationCopyTransactionTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(Path.Combine(
        Path.GetTempPath(), "DeskNest-copy-" + Guid.NewGuid().ToString("N"))).FullName;

    [Fact]
    public async Task FileCopyPublishesVerifiedSiblingAndLeavesSourceUntouched()
    {
        string source = Path.Combine(root, "source.txt");
        string destination = Path.Combine(root, "target", "copy.txt");
        await File.WriteAllTextAsync(source, "copy me");

        await DesktopOrganizationCopyTransaction.CopyFileAsync(source, destination);

        Assert.Equal("copy me", await File.ReadAllTextAsync(source));
        Assert.Equal("copy me", await File.ReadAllTextAsync(destination));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(destination)!, ".*.desknest-copy-*.tmp"));
    }

    [Fact]
    public async Task DirectoryCopyPreservesEmptyTopologyAndRefusesExistingDestination()
    {
        string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "empty", "nested"));
        await File.WriteAllTextAsync(Path.Combine(source, "content.txt"), "directory");
        string destination = Path.Combine(root, "target");

        await DesktopOrganizationCopyTransaction.CopyDirectoryAsync(source, destination);

        Assert.True(Directory.Exists(Path.Combine(destination, "empty", "nested")));
        Assert.Equal("directory", await File.ReadAllTextAsync(Path.Combine(destination, "content.txt")));
        await Assert.ThrowsAsync<IOException>(() =>
            DesktopOrganizationCopyTransaction.CopyDirectoryAsync(source, destination));
    }

    [Fact]
    public async Task DirectoryCopyRejectsDescendantBeforeCreatingDestinationParent()
    {
        string source = Directory.CreateDirectory(Path.Combine(root, "source-tree")).FullName;
        string destination = Path.Combine(source, "new-parent", "copied-tree");
        await File.WriteAllTextAsync(Path.Combine(source, "keep.txt"), "untouched");

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            DesktopOrganizationCopyTransaction.CopyDirectoryAsync(source, destination));

        Assert.False(Directory.Exists(Path.GetDirectoryName(destination)!));
        Assert.Equal("untouched", await File.ReadAllTextAsync(Path.Combine(source, "keep.txt")));
    }

    [WindowsHandleTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailedMetadataEnrollmentPreservesPublishedCopyForManualReconciliation(bool directory)
    {
        string left = Directory.CreateDirectory(Path.Combine(root, "left")).FullName;
        string right = Directory.CreateDirectory(Path.Combine(root, "right")).FullName;
        string name = directory ? "tree" : "item.txt";
        string source = Path.Combine(left, name);
        if (directory)
        {
            Directory.CreateDirectory(Path.Combine(source, "empty"));
            await File.WriteAllTextAsync(Path.Combine(source, "content.txt"), "untouched");
        }
        else await File.WriteAllTextAsync(source, "untouched");
        var leftSpace = new WorkspaceSpace(Guid.NewGuid(), "Left", "", SpaceStorageMode.Managed, left);
        var rightSpace = new WorkspaceSpace(Guid.NewGuid(), "Right", "", SpaceStorageMode.Managed, right);
        var file = new WorkspaceFile(Guid.NewGuid(), leftSpace.Id, name, source, directory);
        string published = Path.Combine(right, name);

        await using var store = await WorkspaceStore.OpenAsync(root);
        await store.UpdateAsync(state => state with { Spaces = [leftSpace, rightSpace], Files = [file] });
        var coordinator = new ManualOrganizationCoordinator(store,
            new DesktopOrganizationTransaction(Path.Combine(root, "move.json")),
            copyMetadataGuard: path => path == published ? false : throw new Exception("Unexpected destination"));

        var error = await Assert.ThrowsAsync<IOException>(() => coordinator.CopyFileAsync(file.Id, rightSpace.Id));
        Assert.Contains(published, error.Message);
        Assert.Single(store.Snapshot.Files);
        Assert.Equal("untouched", await File.ReadAllTextAsync(directory
            ? Path.Combine(published, "content.txt") : published));
        Assert.Equal("untouched", await File.ReadAllTextAsync(directory
            ? Path.Combine(source, "content.txt") : source));
        if (directory) Assert.True(Directory.Exists(Path.Combine(published, "empty")));
    }

    [Fact]
    public async Task CopyThroughCoordinatorEnrollsNewMetadataWithoutCreatingMoveHistory()
    {
        string left = Directory.CreateDirectory(Path.Combine(root, "left")).FullName;
        string right = Directory.CreateDirectory(Path.Combine(root, "right")).FullName;
        string source = Path.Combine(left, "item.txt");
        await File.WriteAllTextAsync(source, "metadata");
        var leftSpace = new WorkspaceSpace(Guid.NewGuid(), "Left", "", SpaceStorageMode.Managed, left);
        var rightSpace = new WorkspaceSpace(Guid.NewGuid(), "Right", "", SpaceStorageMode.Managed, right);
        var file = new WorkspaceFile(Guid.NewGuid(), leftSpace.Id, "item.txt", source, false);

        await using var store = await WorkspaceStore.OpenAsync(root);
        await store.UpdateAsync(state => state with { Spaces = [leftSpace, rightSpace], Files = [file] });
        var coordinator = new ManualOrganizationCoordinator(store,
            new DesktopOrganizationTransaction(Path.Combine(root, "move.json")));
        if (!OperatingSystem.IsWindows())
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => coordinator.CopyFileAsync(file.Id, rightSpace.Id));
            Assert.Single(store.Snapshot.Files);
            Assert.Empty(Directory.EnumerateFileSystemEntries(right));
            Assert.Equal("metadata", await File.ReadAllTextAsync(source));
            return;
        }
        var result = await coordinator.CopyFileAsync(file.Id, rightSpace.Id);

        Assert.NotEqual(file.Id, result.FileId);
        Assert.Equal(2, store.Snapshot.Files.Count);
        Assert.Empty(store.Snapshot.Operations);
        Assert.True(File.Exists(result.DestinationPath));
        Assert.True(File.Exists(source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelledCopyDoesNotCreateDestinationParent(bool directory)
    {
        string source = Path.Combine(root, directory ? "source-tree" : "source.txt");
        if (directory) Directory.CreateDirectory(source);
        else await File.WriteAllTextAsync(source, "unchanged");
        string destination = Path.Combine(root, "not-created", directory ? "tree" : "file.txt");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        if (directory)
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                DesktopOrganizationCopyTransaction.CopyDirectoryAsync(source, destination, cancellation.Token));
        else
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                DesktopOrganizationCopyTransaction.CopyFileAsync(source, destination, cancellation.Token));

        Assert.False(Directory.Exists(Path.GetDirectoryName(destination)!));
        Assert.True(directory ? Directory.Exists(source) : File.Exists(source));
    }

    [Fact]
    public void CancelledSnapshotAndIdentityCaptureStopBeforeReading()
    {
        string source = Path.Combine(root, "source.txt");
        File.WriteAllText(source, "unchanged");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => FileIdentity.Capture(source, cancellation.Token));
        Assert.ThrowsAny<OperationCanceledException>(() =>
            DesktopOrganizationTransaction.CaptureDirectorySnapshot(root, cancellation.Token));
    }

    public void Dispose()
    {
        try { Directory.Delete(root, recursive: true); }
        catch { }
    }
}
