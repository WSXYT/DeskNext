using DeskNest.Core.Storage;
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
        using (var original = WindowsDirectoryLease.Open(originalDrive))
        using (var alternate = WindowsDirectoryLease.Open(alias))
            Assert.Equal(original.VolumePath, alternate.VolumePath, ignoreCase: true);
        string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        File.WriteAllText(Path.Combine(source, "sentinel.txt"), "preserve");
        string aliasedSource = Path.Combine(alias, source[originalDrive.Length..]);
        string destination = Path.Combine(aliasedSource, "new", "copy");
        await Assert.ThrowsAsync<InvalidDataException>(() => WindowsDirectoryCopyLease.CreateAsync(source, destination));
        Assert.Equal(new[] { "sentinel.txt" }, Directory.GetFileSystemEntries(source).Select(Path.GetFileName));
        Assert.Equal("preserve", File.ReadAllText(Path.Combine(source, "sentinel.txt")));
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
