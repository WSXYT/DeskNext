using DeskNest.Core.Storage;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class UnixRenameTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        "DeskNext-unix-rename-" + Guid.NewGuid().ToString("N"))).FullName;

    [UnixTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ATargetAppearingAtTheSyscallCannotBeOverwritten(bool directory)
    {
        string source = Path.Combine(root, "source"), destination = Path.Combine(root, "new-parent", "destination");
        var (id, matches) = Source(source, directory);
        Assert.Throws<IOException>(() => UnixRename.Move(source, destination, id, directory, matches,
            beforeRename: () =>
            {
                if (directory) Directory.CreateDirectory(destination); // Even an empty directory must survive.
                else File.WriteAllText(destination, "occupied");
            }));
        Assert.True(matches(source));
        if (directory) Assert.Empty(Directory.GetFileSystemEntries(destination));
        else Assert.Equal("occupied", File.ReadAllText(destination));
    }

    [UnixTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReboundParentCannotRedirectTheRenameIntoTheLinkTarget(bool directory)
    {
        string source = Path.Combine(root, "source"), parent = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
        string outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        string retired = Path.Combine(root, "retired");
        var (id, matches) = Source(source, directory);
        try
        {
            Assert.Throws<IOException>(() => UnixRename.Move(source, Path.Combine(parent, "destination"), id, directory, matches,
                beforeRename: () =>
                {
                    Directory.Move(parent, retired);
                    Directory.CreateSymbolicLink(parent, outside);
                }));
            Assert.Empty(Directory.GetFileSystemEntries(outside));
            // Unix does not pin names: the original parent receives the object and the
            // post-rename binding check refuses to authorize a Completed receipt.
            Assert.True(matches(Path.Combine(retired, "destination")));
        }
        finally { if (Directory.Exists(parent) && new DirectoryInfo(parent).LinkTarget is not null) Directory.Delete(parent); }
    }

    private static (string Id, Func<string, bool> Matches) Source(string path, bool directory)
    {
        if (directory)
        {
            Directory.CreateDirectory(Path.Combine(path, "empty"));
            File.WriteAllText(Path.Combine(path, "item.txt"), "preserved");
            var receipt = DesktopOrganizationTransaction.CaptureDirectoryReceipt(path, path);
            return (receipt.DirectoryNativeIds![""], candidate => DesktopOrganizationTransaction.DirectoryManifestMatches(
                candidate, receipt.Files, receipt.Directories, receipt.DirectoryNativeIds));
        }
        File.WriteAllText(path, "preserved");
        var identity = FileIdentity.Capture(path);
        return (identity.NativeId!, candidate => DesktopOrganizationTransaction.IdentityMatches(candidate, identity));
    }

    public void Dispose() => Directory.Delete(root, recursive: true);
}

public sealed class UnixTheoryAttribute : TheoryAttribute
{
    public UnixTheoryAttribute()
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            Skip = "Requires native Linux/macOS rename and no-follow semantics.";
    }
}
