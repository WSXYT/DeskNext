using System.Runtime.InteropServices;
using DeskNest.Core.Storage;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace DeskNest.Core.Tests;

public sealed class WindowsHandleCopyTests : IDisposable
{
    private readonly string root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(),
        "DeskNext-handles-" + Guid.NewGuid().ToString("N"))).FullName;

    [WindowsHandleFact]
    public void RelativeCreateAndNoReplaceRenameKeepTheSameObject()
    {
        string parent = Path.Combine(root, "target", "nested");
        using (var lease = WindowsDirectoryLease.Open(parent, create: true))
        using (var file = WindowsFileHandles.OpenFile(lease.Handle, "temporary", create: true))
        {
            RandomAccess.Write(file, "exact object"u8, 0);
            RandomAccess.FlushToDisk(file);
            var before = WindowsFileIdentity.Capture(file);
            WindowsFileHandles.RenameInDirectory(file, "published.txt");
            Assert.Equal(before.NativeId, WindowsFileIdentity.Capture(file).NativeId);
            Assert.Throws<IOException>(() => Directory.Move(parent, parent + "-replaced"));
        }
        Assert.False(File.Exists(Path.Combine(parent, "temporary")));
        Assert.Equal("exact object", File.ReadAllText(Path.Combine(parent, "published.txt")));
    }

    [WindowsHandleFact]
    public void RenameCollisionAndHandleDeletionPreserveExistingDestination()
    {
        File.WriteAllText(Path.Combine(root, "occupied"), "keep");
        using (var lease = WindowsDirectoryLease.Open(root))
        using (var file = WindowsFileHandles.OpenFile(lease.Handle, "temporary", create: true))
        {
            RandomAccess.Write(file, "new"u8, 0);
            Assert.Throws<IOException>(() => WindowsFileHandles.RenameInDirectory(file, "occupied"));
            WindowsFileHandles.DeleteOwnedFile(file);
        }
        Assert.False(File.Exists(Path.Combine(root, "temporary")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(root, "occupied")));
    }

    [WindowsHandleFact]
    public void DirectoryLeaseDeniesGenericWriteButNotAllAttributeMutations()
    {
        using var lease = WindowsDirectoryLease.Open(root);
        using var writer = CreateFileW(root, 0x40000000, 7, IntPtr.Zero, 3,
            0x02200000, IntPtr.Zero); // GENERIC_WRITE, BACKUP_SEMANTICS | OPEN_REPARSE_POINT
        int error = Marshal.GetLastWin32Error();
        Assert.True(writer.IsInvalid, "Pinned directory unexpectedly admitted a concurrent write handle.");
        Assert.Equal(32, error); // ERROR_SHARING_VIOLATION, not an unrelated privilege failure.
    }

    [WindowsHandleFact]
    public void RelativeOpenRefusesDirectoryJunctionWithoutTouchingTarget()
    {
        string outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        File.WriteAllText(Path.Combine(outside, "sentinel"), "keep");
        string link = Path.Combine(root, "link");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J");
        start.ArgumentList.Add(link); start.ArgumentList.Add(outside);
        using var process = System.Diagnostics.Process.Start(start)!;
        process.WaitForExit();
        Assert.Equal(0, process.ExitCode);
        try
        {
            Assert.Throws<IOException>(() => WindowsDirectoryLease.Open(Path.Combine(link, "escape"), create: true));
            Assert.False(Directory.Exists(Path.Combine(outside, "escape")));
            Assert.Equal("keep", File.ReadAllText(Path.Combine(outside, "sentinel")));
        }
        finally { Directory.Delete(link); }
    }

    [WindowsHandleFact]
    public async Task PublishedCopyLeaseDeniesReplacementUntilEnrollmentFinishes()
    {
        string source = Path.Combine(root, "source.txt");
        string destination = Path.Combine(root, "target", "published.txt");
        File.WriteAllText(source, "retained object");
        FileIdentity receipt;
        using (var copy = await WindowsFileCopyLease.CreateAsync(source, destination))
        {
            receipt = copy.VerifyForEnrollment();
            Assert.Throws<IOException>(() => File.Delete(destination));
            Assert.Throws<IOException>(() => File.WriteAllText(destination, "replacement"));
            Assert.Throws<IOException>(() => Directory.Move(Path.GetDirectoryName(destination)!, destination + "-parent"));
            Assert.Equal(receipt, copy.VerifyForEnrollment());
        }
        Assert.Equal("retained object", File.ReadAllText(source));
        Assert.Equal("retained object", File.ReadAllText(destination));
        Assert.Equal(receipt, FileIdentity.Capture(destination));
    }

    [WindowsHandleFact]
    public async Task FailedPublicationPreservesBothSourceAndOccupiedDestination()
    {
        string source = Path.Combine(root, "source.txt");
        string destination = Path.Combine(root, "existing.txt");
        File.WriteAllText(source, "source");
        File.WriteAllText(destination, "existing");
        await Assert.ThrowsAsync<IOException>(() => WindowsFileCopyLease.CreateAsync(source, destination));
        Assert.Equal("source", File.ReadAllText(source));
        Assert.Equal("existing", File.ReadAllText(destination));
        Assert.Empty(Directory.GetFiles(root, ".desknext-copy-*"));
    }

    [WindowsHandleFact]
    public void RetargetedDirectoryCannotRedirectRelativeCreation()
    {
        string outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        string control = Directory.CreateDirectory(Path.Combine(root, "control")).FullName;
        string pinned = Directory.CreateDirectory(Path.Combine(root, "pinned")).FullName;
        byte[] buffer = JunctionBuffer(outside);
        using (var controlHandle = CreateFileW(control, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero))
        {
            Assert.False(controlHandle.IsInvalid);
            Assert.True(DeviceIoControl(controlHandle, 0x900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero),
                "Positive-control junction creation failed: " + Marshal.GetLastWin32Error());
        }
        Directory.Delete(control);
        try
        {
            using var lease = WindowsDirectoryLease.Open(pinned);
            using var attacker = CreateFileW(pinned, 0x100, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            Assert.False(attacker.IsInvalid);
            Assert.True(DeviceIoControl(attacker, 0x900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero),
                "The fixture must demonstrate that attribute-only access can change reparse metadata.");
            Assert.Throws<IOException>(() => WindowsFileHandles.OpenFile(lease.Handle, "relative-output", create: true));
            Assert.Throws<IOException>(() => WindowsFileHandles.OpenDirectory(lease.Handle, "relative-directory", create: true));
            Assert.Throws<IOException>(() => WindowsFileHandles.Enumerate(lease.Handle, 100));
            Assert.Throws<IOException>(lease.VerifyOrdinaryAncestors);
            Assert.False(File.Exists(Path.Combine(outside, "relative-output")));
            Assert.False(Directory.Exists(Path.Combine(outside, "relative-directory")));
        }
        finally
        {
            if ((File.GetAttributes(pinned) & FileAttributes.ReparsePoint) != 0) Directory.Delete(pinned);
        }
    }

    [WindowsHandleFact]
    public void NativeEnumerationRestartsAndHonorsExactBudgetAndUnicode()
    {
        Directory.CreateDirectory(Path.Combine(root, "empty"));
        File.WriteAllText(Path.Combine(root, "中文😀.txt"), "content");
        File.WriteAllText(Path.Combine(root, "a.txt"), "content");
        using var lease = WindowsDirectoryLease.Open(root);
        var first = WindowsFileHandles.Enumerate(lease.Handle, 3);
        Assert.Equal(new[] { "a.txt", "empty", "中文😀.txt" }, first.Select(e => e.Name));
        Assert.True(first[1].IsDirectory);
        Assert.Equal(first, WindowsFileHandles.Enumerate(lease.Handle, 3));
        Assert.Throws<IOException>(() => WindowsFileHandles.Enumerate(lease.Handle, 2));
        Assert.Throws<OperationCanceledException>(() => WindowsFileHandles.Enumerate(lease.Handle, 3, new(true)));
    }

    [WindowsHandleFact]
    public void NativeEnumerationReadsMultipleBuffersWithoutDroppingEntries()
    {
        for (int i = 0; i < 1100; i++) File.WriteAllText(Path.Combine(root, $"entry-{i:D4}.txt"), "");
        using var lease = WindowsDirectoryLease.Open(root);
        var entries = WindowsFileHandles.Enumerate(lease.Handle, 1100);
        Assert.Equal(1100, entries.Count);
        Assert.Equal("entry-0000.txt", entries[0].Name);
        Assert.Equal("entry-1099.txt", entries[^1].Name);
    }

    [WindowsHandleFact]
    public async Task HandleRelativeDirectoryCopyPreservesTopologyAndRetainsPublishedLease()
    {
        string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        Directory.CreateDirectory(Path.Combine(source, "empty"));
        Directory.CreateDirectory(Path.Combine(source, "nested"));
        File.WriteAllText(Path.Combine(source, "nested", "item.txt"), "directory copy");
        string destination = Path.Combine(root, "target", "published");
        using (var copy = await WindowsDirectoryCopyLease.CreateAsync(source, destination))
        {
            Assert.Equal(destination, copy.Receipt.DestinationPath);
            Assert.Contains(copy.Receipt.Nodes, node => node.RelativePath == "empty" && node.IsDirectory);
            Assert.Contains(copy.Receipt.Nodes, node => node.RelativePath == "nested/item.txt" && !node.IsDirectory);
            Assert.Equal("directory copy", File.ReadAllText(Path.Combine(destination, "nested", "item.txt")));
            Assert.Throws<IOException>(() => File.Delete(Path.Combine(destination, "nested", "item.txt")));
            Assert.Throws<IOException>(() => Directory.Move(destination, destination + "-renamed"));
        }
        Assert.Equal("directory copy", File.ReadAllText(Path.Combine(destination, "nested", "item.txt")));
        File.Delete(Path.Combine(destination, "nested", "item.txt"));
    }

    [WindowsHandleFact]
    public async Task HandleRelativeDirectoryCopyRefusesExistingDestination()
    {
        string source = Directory.CreateDirectory(Path.Combine(root, "source")).FullName;
        File.WriteAllText(Path.Combine(source, "item.txt"), "source");
        string destination = Directory.CreateDirectory(Path.Combine(root, "target", "existing")).FullName;
        File.WriteAllText(Path.Combine(destination, "keep.txt"), "keep");
        await Assert.ThrowsAsync<IOException>(() => WindowsDirectoryCopyLease.CreateAsync(source, destination));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(destination, "keep.txt")));
        Assert.Empty(Directory.GetDirectories(Path.GetDirectoryName(destination)!, ".desknext-tree-*"));
    }

    [WindowsHandleFact]
    public void NativeTreeLeaseCapturesEmptyDirectoryIdentitiesAndRejectsTopologyChanges()
    {
        string empty = Directory.CreateDirectory(Path.Combine(root, "empty")).FullName;
        string nested = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
        string source = Path.Combine(nested, "item.txt");
        File.WriteAllText(source, "retained");
        using var parent = WindowsDirectoryLease.Open(root);
        using (var tree = WindowsTreeLease.Capture(parent.Handle, maximumEntries: 3, maximumDepth: 2))
        {
            Assert.Equal(4, tree.Identities.Count); // root plus three entries
            Assert.All(tree.Identities, entry => Assert.Equal(49, entry.NativeId.Length));
            Assert.Contains(tree.Identities, entry => entry.RelativePath == "empty" && entry.IsDirectory && entry.File is null);
            Assert.Throws<IOException>(() => Directory.Move(empty, empty + "-replaced"));
            Assert.Throws<IOException>(() => File.WriteAllText(source, "changed"));
            tree.VerifyUnchanged();
            File.WriteAllText(Path.Combine(nested, "new.txt"), "addition");
            Assert.Throws<IOException>(() => tree.VerifyUnchanged());
        }
        File.WriteAllText(source, "handles released");
        Assert.Equal("handles released", File.ReadAllText(source));
    }

    [WindowsHandleFact]
    public void NativeTreeBudgetFailureReleasesDescendantsAndDoesNotCloseBorrowedRoot()
    {
        string folder = Directory.CreateDirectory(Path.Combine(root, "nested")).FullName;
        string file = Path.Combine(folder, "item.txt");
        File.WriteAllText(file, "content");
        using var parent = WindowsDirectoryLease.Open(root);
        Assert.Throws<IOException>(() => WindowsTreeLease.Capture(parent.Handle, maximumEntries: 1));
        Assert.Throws<IOException>(() => WindowsTreeLease.Capture(parent.Handle, maximumDepth: 1));
        Assert.Throws<OperationCanceledException>(() => WindowsTreeLease.Capture(parent.Handle, new(true)));
        using var tree = WindowsTreeLease.Capture(parent.Handle, maximumEntries: 2, maximumDepth: 2);
        Assert.Equal(3, tree.Identities.Count);
    }

    [WindowsHandleFact]
    public void RelativeRenamePinsParentsAndRefusesAReparseChangedAfterOpening()
    {
        string target = Directory.CreateDirectory(Path.Combine(root, "target")).FullName;
        string outside = Directory.CreateDirectory(Path.Combine(root, "outside")).FullName;
        try
        {
            using var sourceParent = WindowsDirectoryLease.Open(root, forRename: true);
            using var targetParent = WindowsDirectoryLease.Open(target, forRename: true);
            using var file = WindowsFileHandles.OpenFile(sourceParent.Handle, "source.txt", create: true);
            RandomAccess.Write(file, "rename evidence"u8, 0);
            RandomAccess.FlushToDisk(file);
            var identity = WindowsFileIdentity.CaptureContent(file);
            Assert.Throws<IOException>(() => Directory.Move(target, target + "-replaced"));
            WindowsFileHandles.RenameToDirectory(file, targetParent.Handle, "moved.txt");
            Assert.Equal(identity, WindowsFileIdentity.CaptureContent(file));
            File.WriteAllText(Path.Combine(root, "occupied.txt"), "preserve");
            Assert.Throws<IOException>(() => WindowsFileHandles.RenameToDirectory(file, sourceParent.Handle, "occupied.txt"));
            WindowsFileHandles.RenameToDirectory(file, sourceParent.Handle, "source.txt");

            using var attributes = CreateFileW(target, 0x100, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            byte[] buffer = JunctionBuffer(outside);
            Assert.False(attributes.IsInvalid);
            Assert.True(DeviceIoControl(attributes, 0x900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero));
            Assert.Throws<IOException>(() => WindowsFileHandles.RenameToDirectory(file, targetParent.Handle, "redirected.txt"));
            Assert.Empty(Directory.GetFileSystemEntries(outside));
            Assert.True(File.Exists(Path.Combine(root, "source.txt")));
            Assert.Equal("preserve", File.ReadAllText(Path.Combine(root, "occupied.txt")));
        }
        finally
        {
            if ((File.GetAttributes(target) & FileAttributes.ReparsePoint) != 0) Directory.Delete(target);
        }
    }

    private static byte[] JunctionBuffer(string target)
    {
        byte[] substitute = System.Text.Encoding.Unicode.GetBytes(@"\??\" + target);
        byte[] print = System.Text.Encoding.Unicode.GetBytes(target);
        byte[] result = new byte[16 + substitute.Length + 2 + print.Length + 2];
        BitConverter.GetBytes(0xA0000003u).CopyTo(result, 0);
        BitConverter.GetBytes(checked((ushort)(result.Length - 8))).CopyTo(result, 4);
        BitConverter.GetBytes(checked((ushort)substitute.Length)).CopyTo(result, 10);
        BitConverter.GetBytes(checked((ushort)(substitute.Length + 2))).CopyTo(result, 12);
        BitConverter.GetBytes(checked((ushort)print.Length)).CopyTo(result, 14);
        substitute.CopyTo(result, 16);
        print.CopyTo(result, 16 + substitute.Length + 2);
        return result;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[] input, int inputSize,
        IntPtr output, int outputSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);

    public void Dispose() => Directory.Delete(root, recursive: true);
}

public sealed class WindowsHandleTheoryAttribute : TheoryAttribute
{
    public WindowsHandleTheoryAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows NTFS handle semantics require a native Windows runner.";
    }
}

public sealed class WindowsHandleFactAttribute : FactAttribute
{
    public WindowsHandleFactAttribute()
    {
        if (!OperatingSystem.IsWindows()) Skip = "Windows NTFS handle semantics require a native Windows runner.";
    }
}
