using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace DeskNest.Core.Storage;

/// <summary>Windows NTFS handle operations for guarded file moves and internal copy experiments.</summary>
internal static class WindowsFileHandles
{
    private const uint ReadAttributes = 0x80, Synchronize = 0x100000, Delete = 0x10000;
    private const uint DirectoryOptions = 0x200021; // OPEN_REPARSE_POINT | SYNCHRONOUS_IO_NONALERT | DIRECTORY_FILE
    private const uint FileOptions = 0x200060; // OPEN_REPARSE_POINT | SYNCHRONOUS_IO_NONALERT | NON_DIRECTORY_FILE

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString { public ushort Length, MaximumLength; public IntPtr Buffer; }
    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public uint Length;
        public IntPtr RootDirectory, ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor, SecurityQualityOfService;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public IntPtr Status; public UIntPtr Information; }
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeTag { public uint Attributes, ReparseTag; }

    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtCreateFile(out SafeFileHandle handle, uint access,
        ref ObjectAttributes attributes, out IoStatusBlock status, IntPtr allocationSize,
        uint fileAttributes, uint shareAccess, uint disposition, uint options, IntPtr ea, uint eaLength);
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatusBlock status,
        IntPtr information, uint length, uint informationClass);
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern uint RtlNtStatusToDosError(int status);
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryInformationFile(SafeFileHandle handle, out IoStatusBlock status,
        [Out] byte[] information, uint length, uint informationClass);
    [DllImport("ntdll.dll", ExactSpelling = true)]
    private static extern int NtQueryDirectoryFile(SafeFileHandle handle, IntPtr @event, IntPtr apc,
        IntPtr context, out IoStatusBlock status, IntPtr information, uint length, uint infoClass,
        [MarshalAs(UnmanagedType.U1)] bool singleEntry, IntPtr name,
        [MarshalAs(UnmanagedType.U1)] bool restart);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle handle, StringBuilder path,
        uint length, uint flags);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass,
        out AttributeTag info, uint size);

    internal static SafeFileHandle OpenRoot(string root)
    {
        var handle = Open(null, @"\??\" + root, ReadAttributes | Synchronize | 0x20,
            directory: true, disposition: 1);
        try
        {
            // SUBST/drive aliases to a subdirectory are not a stable volume-root contract.
            string canonical = GetVolumePath(handle);
            if (!canonical.StartsWith(@"\\?\Volume{", StringComparison.OrdinalIgnoreCase) ||
                canonical.IndexOf('}') != canonical.Length - 2 || canonical[^1] != '\\')
                throw new NotSupportedException("A native local volume root is required; directory drive aliases are unsupported.");
            return handle;
        }
        catch { handle.Dispose(); throw; }
    }

    internal static string GetVolumePath(SafeFileHandle handle)
    {
        var path = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandleW(handle, path, (uint)path.Capacity, 1); // VOLUME_NAME_GUID
        if (length == 0 || length >= path.Capacity)
            throw new IOException("Cannot capture the opened object's volume-qualified path.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        return path.ToString();
    }

    internal static SafeFileHandle OpenDirectory(SafeFileHandle parent, string name, bool create = false,
        bool allowDelete = false, bool shareWrite = false)
    {
        RequireLeaf(name);
        return Open(parent, name, ReadAttributes | Synchronize | 0x21 |
            (allowDelete ? Delete : 0),
            directory: true, disposition: create ? 3u : 1u, shareAccess: shareWrite ? 3u : 1u);
    }

    internal static SafeFileHandle CreateDirectory(SafeFileHandle parent, string name, bool allowDelete = false)
    {
        RequireLeaf(name);
        return Open(parent, name, ReadAttributes | Synchronize | 0x21 | (allowDelete ? Delete : 0),
            directory: true, disposition: 2); // FILE_CREATE: never adopt a pre-existing temporary directory.
    }

    internal static SafeFileHandle OpenFile(SafeFileHandle parent, string name, bool create = false, bool allowDelete = false)
    {
        RequireLeaf(name);
        return Open(parent, name, Synchronize | (create ? 0xC0000000 | Delete : 0x80000000) | (allowDelete ? Delete : 0),
            directory: false, disposition: create ? 2u : 1u);
    }

    private static SafeFileHandle Open(SafeFileHandle? parent, string name, uint access,
        bool directory, uint disposition, uint shareAccess = 1)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native file operations require Windows NTFS.");
        bool retained = false;
        IntPtr text = Marshal.StringToHGlobalUni(name);
        IntPtr descriptor = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            parent?.DangerousAddRef(ref retained);
            ushort length = checked((ushort)(name.Length * 2));
            Marshal.StructureToPtr(new UnicodeString { Length = length, MaximumLength = length, Buffer = text }, descriptor, false);
            var attributes = new ObjectAttributes
            {
                Length = (uint)Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent?.DangerousGetHandle() ?? IntPtr.Zero,
                ObjectName = descriptor, Attributes = 0x1040 // OBJ_CASE_INSENSITIVE | OBJ_DONT_REPARSE
            };
            // Files deny writes/replacement. Rename-compatible directory handles share
            // writes for IopOpenLinkOrRenameTarget, but still deny directory replacement.
            // Attribute-only handles can mutate reparse metadata even with share-READ.
            int status = NtCreateFile(out var handle, access, ref attributes, out _, IntPtr.Zero,
                0x80, shareAccess, disposition, directory ? DirectoryOptions : FileOptions, IntPtr.Zero, 0);
            if (status < 0) { handle.Dispose(); ThrowStatus(status); }
            try
            {
                RequireOrdinaryObject(handle, directory);
                WindowsFileIdentity.Capture(handle); // Verify NTFS and a handle-derived 128-bit identity.
                return handle;
            }
            catch { handle.Dispose(); throw; }
        }
        finally
        {
            if (retained) parent!.DangerousRelease();
            Marshal.FreeHGlobal(descriptor);
            Marshal.FreeHGlobal(text);
        }
    }

    internal sealed record DirectoryEntry(string Name, bool IsDirectory);

    internal static IReadOnlyList<DirectoryEntry> Enumerate(SafeFileHandle directory, int maximumEntries,
        CancellationToken token = default)
    {
        if (maximumEntries < 0 || maximumEntries > DesktopOrganizationTransaction.MaximumDirectoryEntries)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        token.ThrowIfCancellationRequested();
        RequireOrdinaryObject(directory, directory: true);
        const int capacity = 64 * 1024, headerSize = 64;
        IntPtr buffer = Marshal.AllocHGlobal(capacity);
        var entries = new List<DirectoryEntry>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool restart = true;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int status = NtQueryDirectoryFile(directory, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero,
                    out var result, buffer, capacity, 1, false, IntPtr.Zero, restart);
                restart = false;
                if (status == unchecked((int)0x80000006)) break; // STATUS_NO_MORE_FILES
                if (status != 0) ThrowStatus(status);
                ulong count = result.Information.ToUInt64();
                if (count < headerSize || count > capacity)
                    throw new IOException("Invalid or incomplete native directory enumeration.");
                int offset = 0, available = (int)count;
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    if (offset > available - headerSize) throw new IOException("Truncated directory entry.");
                    int next = Marshal.ReadInt32(buffer, offset);
                    uint attributes = unchecked((uint)Marshal.ReadInt32(buffer, offset + 56));
                    int nameBytes = Marshal.ReadInt32(buffer, offset + 60);
                    if (nameBytes <= 0 || (nameBytes & 1) != 0 || nameBytes > available - offset - headerSize || nameBytes > 510)
                        throw new IOException("Invalid native directory name length.");
                    string name = Marshal.PtrToStringUni(buffer + offset + headerSize, nameBytes / 2)!;
                    if (name is not ("." or ".."))
                    {
                        RequireLeaf(name);
                        if ((attributes & 0x400) != 0) throw new IOException("Linked directory members are ineligible.");
                        if (!names.Add(name) || entries.Count >= maximumEntries)
                            throw new IOException("Directory enumeration exceeded its budget or contained duplicate names.");
                        entries.Add(new(name, (attributes & 0x10) != 0));
                    }
                    if (next == 0) break;
                    if (next < headerSize + nameBytes || (next & 7) != 0 || next > available - offset - headerSize)
                        throw new IOException("Invalid native directory entry offset.");
                    offset += next;
                }
            }
            RequireOrdinaryObject(directory, directory: true);
            return entries.OrderBy(e => e.Name, StringComparer.Ordinal).ToArray();
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static void RequireOrdinaryObject(SafeFileHandle handle, bool directory)
    {
        if (!GetFileInformationByHandleEx(handle, 9, out AttributeTag tag, 8))
            throw new IOException("Cannot inspect opened object attributes.", new Win32Exception(Marshal.GetLastWin32Error()));
        if ((tag.Attributes & 0x400) != 0 || ((tag.Attributes & 0x10) != 0) != directory)
            throw new IOException("Linked or unexpected object type is not eligible for native copy.");
    }

    internal static void RequireCopyableContent(SafeFileHandle handle, bool directory)
    {
        RequireOrdinaryObject(handle, directory);
        if (!GetFileInformationByHandleEx(handle, 9, out AttributeTag attributes, 8))
            throw new IOException("Cannot inspect copy metadata.", new Win32Exception(Marshal.GetLastWin32Error()));
        if ((attributes.Attributes & (uint)(System.IO.FileAttributes.Encrypted | System.IO.FileAttributes.Offline)) != 0)
            throw new NotSupportedException("Encrypted or offline content requires a separate verified copy policy.");

        // FILE_STREAM_INFORMATION (22): only the unnamed NTFS data stream is supported.
        // A bounded query that cannot fit is a refusal, never permission to ignore extra streams.
        byte[] buffer = new byte[4096];
        int status = NtQueryInformationFile(handle, out var result, buffer, (uint)buffer.Length, 22);
        if (status == unchecked((int)0x80000005) || status == unchecked((int)0xC0000023))
            throw new NotSupportedException("Copy stream metadata exceeds the supported bounded query.");
        if (status != 0) ThrowStatus(status);
        ulong length = result.Information.ToUInt64();
        if (length > (ulong)buffer.Length) throw new IOException("Invalid native stream information length.");
        RequireDefaultDataStream(buffer.AsSpan(0, (int)length), directory);
    }

    internal static void RequireDefaultDataStream(ReadOnlySpan<byte> data, bool directory)
    {
        if (data.IsEmpty && directory) return; // An ordinary NTFS directory may have no data streams.
        const int headerSize = 24;
        if (data.Length < headerSize) throw new IOException("Truncated native stream information.");
        int next = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data);
        int nameLength = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        if (nameLength < 0 || (nameLength & 1) != 0 || nameLength > data.Length - headerSize ||
            System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data[8..]) < 0 ||
            System.Buffers.Binary.BinaryPrimitives.ReadInt64LittleEndian(data[16..]) < 0)
            throw new IOException("Invalid native stream metadata.");
        string name = Encoding.Unicode.GetString(data.Slice(headerSize, nameLength));
        if (next != 0 || name != "::$DATA")
            throw new NotSupportedException("Named data streams are not supported by the current copy protocol.");
    }

    internal static void RenameInDirectory(SafeFileHandle handle, string destinationName) =>
        Rename(handle, destinationName, null);

    internal static void RenameToDirectory(SafeFileHandle handle, SafeFileHandle parent, string destinationName) =>
        Rename(handle, destinationName, parent);

    private static void Rename(SafeFileHandle handle, string destinationName, SafeFileHandle? parent)
    {
        RequireLeaf(destinationName);
        // FILE_RENAME_INFORMATION: no replacement. A NULL root renames in the same
        // directory; otherwise the simple leaf is resolved relative to the held root.
        int rootOffset = IntPtr.Size == 8 ? 8 : 4;
        int lengthOffset = rootOffset + IntPtr.Size;
        int nameOffset = lengthOffset + sizeof(uint);
        byte[] name = System.Text.Encoding.Unicode.GetBytes(destinationName);
        int size = nameOffset + name.Length + 8;
        IntPtr buffer = Marshal.AllocHGlobal(size);
        bool retained = false;
        try
        {
            parent?.DangerousAddRef(ref retained);
            Marshal.Copy(new byte[size], 0, buffer, size);
            Marshal.WriteIntPtr(buffer, rootOffset, parent?.DangerousGetHandle() ?? IntPtr.Zero);
            Marshal.WriteInt32(buffer, lengthOffset, name.Length);
            Marshal.Copy(name, 0, buffer + nameOffset, name.Length);
            int status = NtSetInformationFile(handle, out _, buffer, (uint)size, 10); // FileRenameInformation
            if (status < 0) ThrowStatus(status);
        }
        finally
        {
            if (retained) parent!.DangerousRelease();
            Marshal.FreeHGlobal(buffer);
        }
    }

    internal static void MoveFile(string sourcePath, string destinationPath, FileIdentity expected,
        CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        if (!Path.IsPathFullyQualified(sourcePath) || !Path.IsPathFullyQualified(destinationPath))
            throw new ArgumentException("A native move requires absolute local paths.");
        using var sourceParent = WindowsDirectoryLease.Open(Path.GetDirectoryName(sourcePath)!, forRename: true);
        using var source = OpenFile(sourceParent.Handle, Path.GetFileName(sourcePath), allowDelete: true);
        if (WindowsFileIdentity.CaptureContent(source, token) != expected)
            throw new IOException("The opened source does not match its prepared move receipt.");
        using var targetParent = WindowsDirectoryLease.Open(Path.GetDirectoryName(destinationPath)!, create: true,
            requiredVolumePath: sourceParent.VolumePath, forRename: true);
        sourceParent.VerifyPathBinding();
        targetParent.VerifyPathBinding();
        token.ThrowIfCancellationRequested();
        RenameToDirectory(source, targetParent.Handle, Path.GetFileName(destinationPath));
        // Once renamed, finish verification without cancellation; the caller must record
        // completion or retain its existing journal for unreceipted-move reconciliation.
        sourceParent.VerifyPathBinding();
        targetParent.VerifyPathBinding();
        if (WindowsFileIdentity.CaptureContent(source) != expected)
            throw new IOException("The moved object changed before its receipt could be recorded.");
    }

    internal static void DeleteOwnedFile(SafeFileHandle handle)
    {
        IntPtr buffer = Marshal.AllocHGlobal(1);
        try
        {
            Marshal.WriteByte(buffer, 1); // BOOLEAN, not a four-byte Win32 BOOL.
            int status = NtSetInformationFile(handle, out _, buffer, 1, 13); // FileDispositionInformation
            if (status < 0) ThrowStatus(status);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    internal static void RequireLeaf(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.Length > 255 ||
            name.EndsWith('.') || name.EndsWith(' ') || name.Any(char.IsControl) ||
            name.IndexOfAny(['/', '\\', ':', '<', '>', '"', '|', '?', '*']) >= 0)
            throw new InvalidDataException("A single ordinary filesystem name is required.");
    }

    private static void ThrowStatus(int status) => throw new IOException(
        $"Native file operation refused (NTSTATUS 0x{status:X8}).",
        new Win32Exception(checked((int)RtlNtStatusToDosError(status))));
}

/// <summary>Keeps every ancestor open until disposal. Child opens are relative to these handles.</summary>
internal sealed class WindowsDirectoryLease : IDisposable
{
    private readonly List<SafeFileHandle> handles = [];
    private readonly string fullPath;
    private WindowsDirectoryLease(string fullPath) => this.fullPath = fullPath;
    internal SafeFileHandle Handle => handles[^1];
    internal SafeFileHandle RootHandle => handles[0];
    internal IReadOnlyList<string> NativeIds => handles.Select(handle => WindowsFileIdentity.Capture(handle).NativeId)
        .ToArray();
    internal string VolumePath => WindowsFileHandles.GetVolumePath(handles[0]);

    internal static WindowsDirectoryLease Open(string path, bool create = false,
        string? requiredVolumePath = null, string? forbiddenAncestorNativeId = null, bool forRename = false)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!Path.IsPathFullyQualified(path)) throw new ArgumentException("An absolute local directory is required.");
        string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string root = Path.GetPathRoot(full)!;
        if (root.Length != 3 || root[1] != ':' || !char.IsAsciiLetter(root[0]))
            throw new NotSupportedException("Only local drive paths are supported.");
        var lease = new WindowsDirectoryLease(full);
        try
        {
            lease.handles.Add(WindowsFileHandles.OpenRoot(root));
            if (requiredVolumePath is not null && !string.Equals(lease.VolumePath, requiredVolumePath, StringComparison.OrdinalIgnoreCase))
                throw new NotSupportedException("Directory copy currently requires one native NTFS volume.");
            RefuseForbiddenAncestor();
            string[] components = full[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            if (components.Length > DesktopOrganizationTransaction.MaximumDirectoryDepth)
                throw new IOException("Directory ancestor chain exceeds its depth budget.");
            foreach (string component in components)
            {
                lease.handles.Add(WindowsFileHandles.OpenDirectory(lease.Handle, component, create, shareWrite: forRename));
                // Check the actual opened object before creating anything beneath it.
                // Drive-letter aliases and short names cannot bypass native identity equality.
                RefuseForbiddenAncestor();
            }
            return lease;

            void RefuseForbiddenAncestor()
            {
                if (forbiddenAncestorNativeId is not null && string.Equals(
                    WindowsFileIdentity.Capture(lease.Handle).NativeId, forbiddenAncestorNativeId, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The resolved target is inside the source tree.");
            }
        }
        catch { lease.Dispose(); throw; }
    }

    internal void VerifyOrdinaryAncestors()
    {
        ObjectDisposedException.ThrowIf(handles.Count == 0, this);
        foreach (var handle in handles) WindowsFileHandles.RequireOrdinaryObject(handle, directory: true);
    }

    internal void VerifyPathBinding()
    {
        VerifyOrdinaryAncestors();
        using var reopened = Open(fullPath);
        if (!string.Equals(VolumePath, reopened.VolumePath, StringComparison.OrdinalIgnoreCase) ||
            !NativeIds.SequenceEqual(reopened.NativeIds, StringComparer.OrdinalIgnoreCase))
            throw new IOException("The stored directory path no longer resolves to its held ancestor chain.");
    }

    public void Dispose()
    {
        for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose();
        handles.Clear();
    }
}
