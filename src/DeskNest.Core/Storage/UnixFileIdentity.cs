using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace DeskNest.Core.Storage;

/// <summary>Same-handle Unix evidence. This does not lock a pathname or freeze a filesystem.</summary>
internal static class UnixFileIdentity
{
    internal sealed record Metadata(string NativeId, long Length, long LastWriteTicks,
        long ChangeSeconds, uint ChangeNanoseconds);

    internal static Metadata Capture(SafeFileHandle handle, bool directory = false)
    {
        if (!BitConverter.IsLittleEndian || RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException("Unix file evidence requires a supported 64-bit ABI.");

        if (OperatingSystem.IsLinux())
        {
            // Kernel statx has an architecture-independent layout; no libc struct-stat assumptions.
            Statx data;
            int result;
            try { result = statx(handle, "", 0x1000, 0xFFF, out data); } // AT_EMPTY_PATH
            catch (EntryPointNotFoundException error)
            { throw new NotSupportedException("The system cannot supply statx file evidence.", error); }
            if (result != 0) throw NativeError();
            const uint required = 0xBC3; // type, mode, inode, size, mtime, ctime and birth time
            if ((data.Mask & required) != required)
                throw new NotSupportedException("The filesystem cannot supply inode and birth-time evidence.");
            RequireKind(data.Mode, directory);
            return Create("linux", ((ulong)data.DeviceMajor << 32) | data.DeviceMinor, data.Inode,
                checked((long)data.Size), data.Modified, data.Changed, data.Born);
        }

        if (OperatingSystem.IsMacOS())
        {
            DarwinStat data;
            int result = RuntimeInformation.ProcessArchitecture == Architecture.X64
                ? fstat_inode64(handle, out data) : fstat(handle, out data);
            if (result != 0) throw NativeError();
            RequireKind(data.Mode, directory);
            return Create("macos", data.Device, data.Inode, data.Size,
                data.Modified, data.Changed, data.Born);
        }
        throw new PlatformNotSupportedException("Native file identity is unavailable on this platform.");
    }

    internal static Metadata CaptureDirectory(string path)
    {
        // Read-only, close-on-exec, and no-follow on the leaf. Ancestor checks remain with the caller.
        int flags = OperatingSystem.IsLinux() ? 0x10000 | 0x20000 | 0x80000 :
            OperatingSystem.IsMacOS() ? 0x100000 | 0x100 | 0x1000000 :
            throw new PlatformNotSupportedException("Native directory identity is unavailable.");
        int fd = open(path, flags);
        if (fd < 0) throw NativeError();
        using var handle = new SafeFileHandle((IntPtr)fd, ownsHandle: true);
        return Capture(handle, directory: true);
    }

    internal static bool IsNativeId(string? value, bool requireCurrentPlatform = true)
    {
        string prefix = OperatingSystem.IsLinux() ? "linux:" : "macos:";
        return value is { Length: 64 } &&
            (requireCurrentPlatform ? value.StartsWith(prefix, StringComparison.Ordinal) :
                value.StartsWith("linux:", StringComparison.Ordinal) || value.StartsWith("macos:", StringComparison.Ordinal)) &&
            value[22] == ':' && value[39] == ':' &&
            value.AsSpan(6, 16).ToString().All(Uri.IsHexDigit) &&
            value.AsSpan(23, 16).ToString().All(Uri.IsHexDigit) &&
            value.AsSpan(40).ToString().All(Uri.IsHexDigit);
    }

    private static Metadata Create(string platform, ulong device, ulong inode, long size,
        Timestamp modified, Timestamp changed, Timestamp born)
    {
        if (inode == 0 || size < 0 || modified.Nanoseconds >= 1_000_000_000 ||
            changed.Nanoseconds >= 1_000_000_000 || born.Nanoseconds >= 1_000_000_000)
            throw new IOException("Invalid native file evidence.");
        long ticks = checked(DateTime.UnixEpoch.Ticks + modified.Seconds * TimeSpan.TicksPerSecond + modified.Nanoseconds / 100);
        if (ticks < 0 || ticks > DateTime.MaxValue.Ticks)
            throw new IOException("Native file timestamp is outside the supported range.");
        // Birth time distinguishes inode reuse; unlike ctime it survives a same-volume rename.
        string id = $"{platform}:{device:X16}:{inode:X16}:{born.Seconds:X16}{born.Nanoseconds:X8}";
        return new(id, size, ticks, changed.Seconds, changed.Nanoseconds);
    }

    private static void RequireKind(ushort mode, bool directory)
    {
        if ((mode & 0xF000) != (directory ? 0x4000 : 0x8000))
            throw new IOException("Native object type does not match its file-move evidence.");
    }

    private static IOException NativeError() => new("Cannot read native file evidence.",
        new Win32Exception(Marshal.GetLastPInvokeError()));

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct Timestamp
    {
        [FieldOffset(0)] public long Seconds;
        [FieldOffset(8)] public uint Nanoseconds;
    }

    [StructLayout(LayoutKind.Explicit, Size = 256)]
    private struct Statx
    {
        [FieldOffset(0)] public uint Mask;
        [FieldOffset(28)] public ushort Mode;
        [FieldOffset(32)] public ulong Inode;
        [FieldOffset(40)] public ulong Size;
        [FieldOffset(80)] public Timestamp Born;
        [FieldOffset(96)] public Timestamp Changed;
        [FieldOffset(112)] public Timestamp Modified;
        [FieldOffset(136)] public uint DeviceMajor;
        [FieldOffset(140)] public uint DeviceMinor;
    }

    [StructLayout(LayoutKind.Explicit, Size = 144)]
    private struct DarwinStat
    {
        [FieldOffset(0)] public uint Device;
        [FieldOffset(4)] public ushort Mode;
        [FieldOffset(8)] public ulong Inode;
        [FieldOffset(48)] public Timestamp Modified;
        [FieldOffset(64)] public Timestamp Changed;
        [FieldOffset(80)] public Timestamp Born;
        [FieldOffset(96)] public long Size;
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags);

    [DllImport("libc", SetLastError = true)]
    private static extern int statx(SafeFileHandle fd, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, uint mask, out Statx data);

    [DllImport("libc", SetLastError = true)]
    private static extern int fstat(SafeFileHandle fd, out DarwinStat data);

    [DllImport("libc", EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int fstat_inode64(SafeFileHandle fd, out DarwinStat data);
}
