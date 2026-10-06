using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DeskNest.App.Services;

// Shared file-type icons from Windows, not a hand-drawn imitation. Bounded process-lifetime cache.
internal static class SystemFileIcons
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<Bitmap?>>> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly ConcurrentDictionary<string, string> Failures = new(StringComparer.OrdinalIgnoreCase);
    private static readonly SemaphoreSlim LoadSlots = new(2, 2);
    internal static string? FailureFor(string extension) => Failures.TryGetValue(extension, out var reason) ? reason : null;
    internal static Task<Bitmap?> GetAsync(string name, bool directory)
    {
        if (!OperatingSystem.IsWindows()) return Task.FromResult<Bitmap?>(null);
        string key = directory ? "<folder>" : System.IO.Path.GetExtension(name);
        if (Cache.Count >= 128 && !Cache.ContainsKey(key)) key = "";
        return Cache.GetOrAdd(key, k => new(() => LoadOnStaAsync(k))).Value;
    }

    private static async Task<Bitmap?> LoadOnStaAsync(string key)
    {
        if (!OperatingSystem.IsWindows()) return null;
        await LoadSlots.WaitAsync().ConfigureAwait(false);
        try
        {
            var completion = new TaskCompletionSource<Bitmap?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try { completion.SetResult(Load(key, key == "<folder>")); }
                catch (Exception error) { completion.SetException(error); }
            }) { IsBackground = true, Name = "DeskNext shell icon" };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            return await completion.Task.ConfigureAwait(false);
        }
        finally { LoadSlots.Release(); }
    }

    private static Bitmap? Failed(string extension, string stage)
    {
        Failures[extension] = stage;
        return null;
    }

    private static Bitmap? Load(string extension, bool directory)
    {
        IntPtr dc = IntPtr.Zero, bitmap = IntPtr.Zero, previous = IntPtr.Zero, icon = IntPtr.Zero;
        // Shell icon handlers run on their own STA, not an arbitrary thread-pool apartment.
        int com = CoInitializeEx(IntPtr.Zero, 2);
        if (com < 0) return Failed(extension, $"CoInitializeEx: 0x{com:X8}");
        try
        {
            // USEFILEATTRIBUTES obtains the registered type icon without opening or executing a user file.
            if (SHGetFileInfo(directory ? "folder" : "file" + extension, directory ? 0x10u : 0x80u,
                out var info, (uint)Marshal.SizeOf<ShellFileInfo>(), 0x100 | 0x10) == IntPtr.Zero) return Failed(extension, "SHGetFileInfo");
            icon = info.Icon;
            dc = CreateCompatibleDC(IntPtr.Zero);
            var header = new BitmapInfo { Size = 40, Width = 48, Height = -48, Planes = 1, BitCount = 32 };
            bitmap = CreateDIBSection(dc, ref header, 0, out var pixels, IntPtr.Zero, 0);
            if (dc == IntPtr.Zero || bitmap == IntPtr.Zero || pixels == IntPtr.Zero) return Failed(extension, "CreateDIBSection/CreateCompatibleDC");
            previous = SelectObject(dc, bitmap);
            var data = new byte[48 * 48 * 4];
            Marshal.Copy(data, 0, pixels, data.Length);
            if (!DrawIconEx(dc, 0, 0, icon, 48, 48, 0, IntPtr.Zero, 3))
                return Failed(extension, $"DrawIconEx: error={Marshal.GetLastPInvokeError()}, icon={icon}, selected={previous}");
            GdiFlush();
            Marshal.Copy(pixels, data, 0, data.Length);
            var result = new WriteableBitmap(new PixelSize(48, 48), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
            using (var target = result.Lock()) Marshal.Copy(data, 0, target.Address, data.Length);
            return result;
        }
        finally
        {
            if (previous != IntPtr.Zero && dc != IntPtr.Zero) SelectObject(dc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (dc != IntPtr.Zero) DeleteDC(dc);
            if (icon != IntPtr.Zero) DestroyIcon(icon);
            if (com >= 0) CoUninitialize();
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShellFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string TypeName;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfo
    {
        public uint Size; public int Width, Height; public ushort Planes, BitCount;
        public uint Compression, ImageSize; public int XPixels, YPixels; public uint ColorsUsed, ColorsImportant;
    }
    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint flags);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("shell32.dll", EntryPoint = "SHGetFileInfoW", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, out ShellFileInfo info, uint size, uint flags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateDIBSection(IntPtr dc, ref BitmapInfo info, uint usage, out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();
    [DllImport("user32.dll", SetLastError = true)] private static extern bool DrawIconEx(IntPtr dc, int x, int y, IntPtr icon, int width, int height, uint step, IntPtr brush, uint flags);
    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr icon);
}
