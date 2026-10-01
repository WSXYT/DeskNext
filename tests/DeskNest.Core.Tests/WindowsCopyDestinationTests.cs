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
