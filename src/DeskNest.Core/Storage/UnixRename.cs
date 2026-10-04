using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeskNest.Core.Storage;

/// <summary>
/// Same-volume, no-replace renames relative to no-follow directory handles.
/// Unix handles do not prevent other processes renaming entries: recheck the path
/// bindings and recorded receipt after the syscall; failures retain the caller's journal.
/// </summary>
internal static class UnixRename
{
    internal static void Move(string source, string destination, string expectedId, bool directory,
        Func<string, bool> matchesReceipt, CancellationToken token = default, Action? beforeRename = null,
        bool createDestinationParents = true)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("Verified Unix rename is unavailable.");
        if (!UnixFileIdentity.IsNativeId(expectedId) || !Path.IsPathFullyQualified(source) ||
            !Path.IsPathFullyQualified(destination))
            throw new InvalidDataException("Unix rename requires absolute paths and recorded native identity.");
        source = Path.TrimEndingDirectorySeparator(source);
        destination = Path.TrimEndingDirectorySeparator(destination);
        token.ThrowIfCancellationRequested();
        using var from = DirectoryHandles.Open(Path.GetDirectoryName(source)!);
        using var item = OpenAt(from.Handle, Path.GetFileName(source), directory);
        if (UnixFileIdentity.Capture(item, directory).NativeId != expectedId || !matchesReceipt(source))
            throw new IOException("Unix source no longer matches its recorded receipt.");
        using var to = DirectoryHandles.Open(Path.GetDirectoryName(destination)!, create: createDestinationParents,
            requiredDevice: expectedId[..22], forbiddenId: directory ? expectedId : null);
        from.VerifyBinding();
        to.VerifyBinding();
        token.ThrowIfCancellationRequested();
        beforeRename?.Invoke(); // Trusted test seam; never read from workspace/journal data.
        int result;
        try
        {
            result = OperatingSystem.IsLinux()
                ? renameat2(from.Handle, Path.GetFileName(source), to.Handle, Path.GetFileName(destination), 1)
                : renameatx_np(from.Handle, Path.GetFileName(source), to.Handle, Path.GetFileName(destination), 4);
        }
        catch (EntryPointNotFoundException error)
        { throw new NotSupportedException("The system has no supported atomic no-replace rename.", error); }
        if (result != 0) throw NativeError("Atomic no-replace rename refused");
        // No cancellation after a successful rename until the caller can checkpoint it.
        from.VerifyBinding();
        to.VerifyBinding();
        using var published = OpenAt(to.Handle, Path.GetFileName(destination), directory);
        if (UnixFileIdentity.Capture(published, directory).NativeId != expectedId || !matchesReceipt(destination))
            throw new IOException("Unix rename completed without a matching receipt; reconcile the retained journal.");
    }

    private static int Flags(bool directory) => OperatingSystem.IsLinux()
        ? 0x20000 | 0x80000 | (directory ? 0x10000 : 0x800) // NOFOLLOW, CLOEXEC, DIRECTORY/NONBLOCK
        : 0x100 | 0x1000000 | (directory ? 0x100000 : 0x4);

    private static SafeFileHandle OpenAt(SafeFileHandle parent, string name, bool directory)
    {
        int fd = openat(parent, name, Flags(directory));
        if (fd < 0) throw NativeError("No-follow open refused");
        return new SafeFileHandle((IntPtr)fd, ownsHandle: true);
    }

    private sealed class DirectoryHandles(string path) : IDisposable
    {
        private readonly List<SafeFileHandle> handles = [];
        private readonly List<string> identities = [];
        internal SafeFileHandle Handle => handles[^1];

        internal static DirectoryHandles Open(string path, bool create = false,
            string? requiredDevice = null, string? forbiddenId = null)
        {
            if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("An absolute parent path is required.");
            var chain = new DirectoryHandles(Path.GetFullPath(path));
            try
            {
                int fd = open("/", Flags(directory: true));
                if (fd < 0) throw NativeError("Cannot open filesystem root");
                chain.Add(new SafeFileHandle((IntPtr)fd, ownsHandle: true), forbiddenId);
                string[] parts = Path.GetFullPath(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length > DesktopOrganizationTransaction.MaximumDirectoryDepth)
                    throw new IOException("Directory ancestry exceeds its depth budget.");
                foreach (string part in parts)
                {
                    int child = openat(chain.Handle, part, Flags(directory: true));
                    if (child < 0 && Marshal.GetLastPInvokeError() == 2 && create) // ENOENT only
                    {
                        chain.RequireDevice(requiredDevice);
                        if (mkdirat(chain.Handle, part, 0x1FF) != 0 && Marshal.GetLastPInvokeError() != 17) // 0777 / EEXIST
                            throw NativeError("Cannot create destination parent");
                        child = openat(chain.Handle, part, Flags(directory: true));
                    }
                    if (child < 0) throw NativeError("No-follow directory traversal refused");
                    chain.Add(new SafeFileHandle((IntPtr)child, ownsHandle: true), forbiddenId);
                }
                chain.RequireDevice(requiredDevice);
                return chain;
            }
            catch { chain.Dispose(); throw; }
        }

        private void Add(SafeFileHandle handle, string? forbiddenId)
        {
            handles.Add(handle);
            string id = UnixFileIdentity.Capture(handle, directory: true).NativeId;
            identities.Add(id);
            if (id == forbiddenId) throw new IOException("The destination is inside the source directory.");
        }

        private void RequireDevice(string? requiredDevice)
        {
            if (requiredDevice is not null && !identities[^1].StartsWith(requiredDevice, StringComparison.Ordinal))
                throw new NotSupportedException("Cross-device parent creation or rename is unsupported.");
        }

        internal void VerifyBinding()
        {
            using var current = Open(path);
            if (!identities.SequenceEqual(current.identities))
                throw new IOException("A Unix parent pathname no longer names its retained directory chain.");
        }

        public void Dispose()
        {
            for (int i = handles.Count - 1; i >= 0; i--) handles[i].Dispose();
        }
    }

    private static IOException NativeError(string message) => new(message,
        new Win32Exception(Marshal.GetLastPInvokeError()));

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);
    [DllImport("libc", SetLastError = true)]
    private static extern int openat(SafeFileHandle parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int flags);
    [DllImport("libc", SetLastError = true)]
    private static extern int mkdirat(SafeFileHandle parent, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, uint mode);
    [DllImport("libc", SetLastError = true)]
    private static extern int renameat2(SafeFileHandle from, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        SafeFileHandle to, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, uint flags);
    [DllImport("libc", SetLastError = true)]
    private static extern int renameatx_np(SafeFileHandle from, [MarshalAs(UnmanagedType.LPUTF8Str)] string name,
        SafeFileHandle to, [MarshalAs(UnmanagedType.LPUTF8Str)] string destination, uint flags);
}
