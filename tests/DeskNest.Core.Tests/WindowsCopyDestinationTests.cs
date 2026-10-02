using DeskNest.Core.Storage;
using DeskNest.Core.Workspace;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class WindowsCopyDestinationTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        "DeskNext-copy-destination-" + Guid.NewGuid().ToString("N"))).FullName;

    [WindowsHandleFact]
    public void NativeAncestorRefusalPrecedesMissingParentCreation()
    {
        string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        using (var held = WindowsDirectoryLease.Open(source))
        {
            string id = WindowsFileIdentity.Capture(held.Handle).NativeId;
            Assert.Throws<InvalidDataException>(() => WindowsDirectoryLease.Open(
                Path.Combine(source, "new", "deeper"), create: true,
                requiredVolumePath: held.VolumePath, forbiddenAncestorNativeId: id));
            Assert.Empty(Directory.GetFileSystemEntries(source));
        }
        Directory.Move(source, source + "-released"); // Refusal releases its own handles.
    }

    [WindowsHandleFact]
    public void WrongVolumeRefusalPrecedesAnyParentCreation()
    {
        string parent = Path.Combine(root, "new", "deeper");
        Assert.Throws<NotSupportedException>(() => WindowsDirectoryLease.Open(parent, create: true,
            requiredVolumePath: @"\\?\Volume{00000000-0000-0000-0000-000000000000}\"));
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [WindowsVolumeAliasFact]
    public async Task NativeVolumeAliasSelfCopyCannotCreateSourceChildren()
    {
        // The runner supplies an EXISTING alternate drive letter to this volume root.
        // This test never creates/removes DOS devices or changes host drive mappings.
        string alias = Environment.GetEnvironmentVariable("DESKNEXT_TEST_VOLUME_ALIAS")!;
        string originalDrive = Path.GetPathRoot(root)!;
        Assert.Equal(alias, Path.GetPathRoot(alias));
        Assert.False(string.Equals(alias, originalDrive, StringComparison.OrdinalIgnoreCase));
        string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        string sentinel = Path.Combine(source, "sentinel.txt");
        File.WriteAllText(sentinel, "preserve");
        string aliasedSource = Path.Combine(alias, source[originalDrive.Length..]);
        // Positive control through ordinary Win32 file opens: this really is the SAME object,
        // not a missing drive or an unrelated volume which would make refusal vacuous.
        Assert.Equal(FileIdentity.Capture(sentinel),
            FileIdentity.Capture(Path.Combine(aliasedSource, "sentinel.txt")));
        string destination = Path.Combine(aliasedSource, "new", "copy");
        var error = await Record.ExceptionAsync(() => WindowsDirectoryCopyLease.CreateAsync(source, destination));
        Assert.NotNull(error);
        if (error is InvalidDataException)
            Assert.Equal("The resolved target is inside the source tree.", error.Message);
        else
        {
            // Some native DOS-device aliases are refused before ancestry traversal.
            // Only that precise kernel refusal is acceptable, not arbitrary I/O failure.
            Assert.IsType<IOException>(error);
            Assert.Contains("NTSTATUS 0xC000050B", error.Message);
            Assert.IsType<System.ComponentModel.Win32Exception>(error.InnerException);
        }
        Assert.False(Directory.Exists(Path.GetDirectoryName(destination)));
        Assert.Equal(new[] { "sentinel.txt" }, Directory.GetFileSystemEntries(source).Select(Path.GetFileName));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(source, "sentinel.txt")));
    }

    [WindowsHandleTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task TargetDisappearingAfterPreflightIsCreatedOnlyForManagedSpaces(bool directory, bool mapped)
    {
        string sourceRoot = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        string targetRoot = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
        string retired = Path.Combine(root, "retired-target");
        string path = Path.Combine(sourceRoot, directory ? "tree" : "item.txt");
        if (directory) Directory.CreateDirectory(path);
        string content = directory ? Path.Combine(path, "item.txt") : path;
        await File.WriteAllTextAsync(content, "preserved source");
        await File.WriteAllTextAsync(Path.Combine(targetRoot, "sentinel.txt"), "existing target");
        var source = new WorkspaceSpace(Guid.NewGuid(), "Source", "", SpaceStorageMode.Managed, sourceRoot);
        var target = new WorkspaceSpace(Guid.NewGuid(), "Target", "",
            mapped ? SpaceStorageMode.Mapped : SpaceStorageMode.Managed, targetRoot);
        var file = new WorkspaceFile(Guid.NewGuid(), source.Id, Path.GetFileName(path), path, directory);
        await using var store = await WorkspaceStore.OpenAsync(Path.Combine(root, "state"));
        await store.UpdateAsync(s => s with { Spaces = [source, target], Files = [file] });
        var coordinator = new ManualOrganizationCoordinator(store,
            new DesktopOrganizationTransaction(Path.Combine(store.DataDirectory, "organization-recovery.json")));
        long revision = store.Snapshot.Revision;
        await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.CopyFileAsync(file.Id, target.Id, revision - 1));
        Assert.False(new CopyRecoveryJournal(store.DataDirectory).Exists);
        var copy = coordinator.CopyFileAsync(file.Id, target.Id,
            beforeCopyPreparation: () => Directory.Move(targetRoot, retired));
        if (mapped)
        {
            await Assert.ThrowsAsync<IOException>(() => copy);
            Assert.False(Directory.Exists(targetRoot));
            Assert.Equal(revision, store.Snapshot.Revision);
            Assert.Single(store.Snapshot.Files);
            Assert.Empty(store.Snapshot.Operations);
            var intent = new CopyRecoveryJournal(store.DataDirectory);
            Assert.True(intent.Exists);
            await Assert.ThrowsAsync<InvalidDataException>(() => coordinator.RecoverPendingAsync());
            Assert.True(intent.Exists);
        }
        else
        {
            await copy;
            Assert.Equal(2, store.Snapshot.Files.Count);
            Assert.True(Directory.Exists(targetRoot));
            string copied = Path.Combine(targetRoot, file.Name);
            Assert.Equal("preserved source", File.ReadAllText(directory ? Path.Combine(copied, "item.txt") : copied));
            Assert.False(new CopyRecoveryJournal(store.DataDirectory).Exists);
        }
        Assert.Equal("preserved source", File.ReadAllText(content));
        Assert.Equal("existing target", File.ReadAllText(Path.Combine(retired, "sentinel.txt")));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

public sealed class WindowsVolumeAliasFactAttribute : FactAttribute
{
    public WindowsVolumeAliasFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("DESKNEXT_TEST_VOLUME_ALIAS")))
            Skip = "Requires native Windows and DESKNEXT_TEST_VOLUME_ALIAS naming an existing alternate volume-root drive letter; no host mappings are changed by this test.";
    }
}
