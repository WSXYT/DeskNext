using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskNest.Core.Storage;

/// <summary>Conservative local mount boundary, not an object identity or a filesystem snapshot.</summary>
internal static class FileSystemVolume
{
    internal static void RequireSameVolume(string source, string destination)
    {
        string sourceVolume = Identify(source);
        string destinationVolume = Identify(destination);
        if (!string.Equals(sourceVolume, destinationVolume, StringComparison.Ordinal))
            throw new NotSupportedException("Cross-volume moves require verified copy/commit support and are disabled.");
    }

    internal static void RequireNoReparsePoints(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("Reparse-point lookup requires an absolute path.", nameof(path));
        string current = Path.GetFullPath(path);
        while (!File.Exists(current) && !Directory.Exists(current))
        {
            current = Path.GetDirectoryName(current)
                ?? throw new IOException("No existing ancestor for reparse-point lookup.");
        }
        while (true)
        {
            var attributes = File.GetAttributes(current);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"Reparse-point path components are not eligible: {current}");
            string? parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
            if (parent is null || string.Equals(parent, current,
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                break;
            current = parent;
        }
    }


    internal static string Identify(string path)
    {
        if (!Path.IsPathFullyQualified(path))
            throw new ArgumentException("Volume lookup requires an absolute path.", nameof(path));
        string existing = Path.GetFullPath(path);
        while (!File.Exists(existing) && !Directory.Exists(existing))
            existing = Path.GetDirectoryName(existing)
                ?? throw new IOException("No existing ancestor for volume lookup.");

        if (OperatingSystem.IsWindows())
        {
            var mount = new StringBuilder(32768);
            if (!GetVolumePathNameW(existing, mount, (uint)mount.Capacity))
                throw new IOException("Cannot determine volume mount point.", new Win32Exception(Marshal.GetLastWin32Error()));
            var volume = new StringBuilder(128);
            // UNC shares and filesystems without stable local volume names fail closed.
            if (!GetVolumeNameForVolumeMountPointW(mount.ToString(), volume, (uint)volume.Capacity))
                throw new IOException("Cannot determine a local volume identity.", new Win32Exception(Marshal.GetLastWin32Error()));
            return volume.ToString().ToUpperInvariant();
        }

        if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            IntPtr resolved = realpath(existing, IntPtr.Zero);
            if (resolved == IntPtr.Zero)
                throw new IOException("Cannot resolve physical path for volume lookup.");
            try { existing = Marshal.PtrToStringUTF8(resolved) ?? throw new IOException("Empty resolved path."); }
            finally { free(resolved); }
        }

        if (OperatingSystem.IsLinux())
            return IdentifyLinuxMount(existing, File.ReadLines("/proc/self/mountinfo"));

        if (OperatingSystem.IsMacOS())
        {
            // DriveInfo enumerates mounted filesystems on Unix. Different mount points
            // are intentionally treated as different volumes, even on one device.
            string mount = FindMount(existing, DriveInfo.GetDrives().Select(d => d.Name));
            var drive = new DriveInfo(mount);
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable or DriveType.Ram))
                throw new IOException("The mounted filesystem is not a supported local volume.");
            return "macos-mount:" + mount;
        }

        throw new PlatformNotSupportedException("Safe volume lookup is unavailable on this platform.");
    }

    internal static string IdentifyLinuxMount(string path, IEnumerable<string> lines)
    {
        string? identity = null;
        int longest = -1;
        var matchingMounts = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in lines)
        {
            string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int separator = Array.IndexOf(fields, "-");
            if (separator < 6 || separator + 3 >= fields.Length ||
                !uint.TryParse(fields[0], out _) || !fields[2].Contains(':'))
                throw new IOException("Malformed mount information; volume lookup refused.");
            string mount = DecodeMountPath(fields[4]);
            if (!ContainsPath(path, mount))
                continue;
            // Check ancestors too: an overmount can hide an otherwise longer child.
            // Ambiguity must not depend on mountinfo record order.
            if (!matchingMounts.Add(mount))
                throw new IOException("Ambiguous stacked mount; volume lookup refused.");
            if (mount.Length < longest)
                continue;
            longest = mount.Length;
            string filesystem = fields[separator + 1];
            // Explicit local filesystem allowlist: unknown/network/FUSE mounts do not
            // inherit local rename assumptions. Extend only with platform evidence.
            identity = filesystem is "ext4" or "ext3" or "ext2" or "xfs" or "btrfs" or
                "tmpfs" or "overlay" or "zfs" or "f2fs"
                ? $"linux-mount:{fields[0]}:{fields[2]}" : null;
        }
        return identity ?? throw new IOException("No supported local mount matches the path.");
    }

    internal static string FindMount(string path, IEnumerable<string> mounts) =>
        mounts.Where(m => ContainsPath(path, m)).OrderByDescending(m => m.Length).FirstOrDefault()
            ?? throw new IOException("No mount matches the path.");

    private static bool ContainsPath(string path, string mount) =>
        mount.StartsWith('/') && (path == mount ||
            path.StartsWith(mount.TrimEnd('/') + "/", StringComparison.Ordinal));

    private static string DecodeMountPath(string path) => path
        .Replace(@"\040", " ", StringComparison.Ordinal)
        .Replace(@"\011", "\t", StringComparison.Ordinal)
        .Replace(@"\012", "\n", StringComparison.Ordinal)
        .Replace(@"\134", "\\", StringComparison.Ordinal);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr realpath([MarshalAs(UnmanagedType.LPUTF8Str)] string path, IntPtr buffer);

    [DllImport("libc")]
    private static extern void free(IntPtr pointer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string fileName, StringBuilder volumePathName, uint bufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, StringBuilder volumeName, uint bufferLength);
}
