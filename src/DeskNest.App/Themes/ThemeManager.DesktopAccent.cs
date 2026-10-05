using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using DeskNest.Core.Workspace;

namespace DeskNest.App.Themes;

public sealed partial class ThemeManager
{
    private AccentColorSource _accentSource;
    private string? _manualAccent;
    private IPlatformSettings? _desktopSettings;
    private string? _wallpaperStamp;

    public void ApplyAccentSettings(AccentColorSource source, string? manualColor)
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(() => ApplyAccentSettings(source, manualColor));
            return;
        }
        if (_accentSource != source) _wallpaperStamp = null;
        _accentSource = source;
        _manualAccent = manualColor;
        RefreshAutomaticAccent();
    }

    // Refresh on startup, OS color changes and window activation; no background polling service.
    public void RefreshAutomaticAccent()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(RefreshAutomaticAccent);
            return;
        }
        if (_accentSource == AccentColorSource.Manual || !OperatingSystem.IsWindows())
        {
            ApplyAccent(_manualAccent);
            return;
        }
        if (_desktopSettings is null && Application.Current?.PlatformSettings is { } settings)
        {
            _desktopSettings = settings;
            settings.ColorValuesChanged += (_, _) =>
            {
                _wallpaperStamp = null;
                RefreshAutomaticAccent();
            };
        }
        string? systemColor = _desktopSettings is null ? null : Hex(_desktopSettings.GetColorValues().AccentColor1);
        if (_accentSource == AccentColorSource.Windows)
        {
            ApplyAccent(systemColor ?? _manualAccent);
            return;
        }
        try
        {
            var path = new StringBuilder(32768);
            if (!SystemParametersInfo(0x0073, (uint)path.Capacity, path, 0) || path.Length == 0)
            { ApplyAccent(systemColor ?? _manualAccent); return; }
            string fullPath = DeskNest.Platform.PlatformFileActions.RequireExistingLocalPath(path.ToString());
            string stamp = fullPath + "|" + File.GetLastWriteTimeUtc(fullPath).Ticks;
            if (_wallpaperStamp == stamp) return;
            _wallpaperStamp = stamp;
            ApplyAccent(systemColor ?? _manualAccent); // Predictable fallback while the thumbnail is decoded.
            _ = ReadWallpaperAsync(fullPath, stamp);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        { ApplyAccent(systemColor ?? _manualAccent); }
    }

    private async Task ReadWallpaperAsync(string path, string stamp)
    {
        try
        {
            Color color = await Task.Run(() => ReadWallpaperColor(path));
            if (_accentSource == AccentColorSource.Wallpaper && _wallpaperStamp == stamp)
                ApplyAccent(Hex(color));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { /* Unsupported wallpaper keeps the system-color fallback; no settings or OS files are written. */ }
    }

    internal static Color ReadWallpaperColor(string path)
    {
        using var stream = File.OpenRead(path);
        using var bitmap = Bitmap.DecodeToWidth(stream, 64);
        if (bitmap.PixelSize.Height is < 1 or > 4096)
            throw new NotSupportedException("Unsupported wallpaper dimensions.");
        // CopyPixels converts the decoded image to a known BGRA format.
        using var pixels = new WriteableBitmap(bitmap.PixelSize, new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var buffer = pixels.Lock();
        bitmap.CopyPixels(buffer);
        var bytes = new byte[buffer.RowBytes * buffer.Size.Height];
        Marshal.Copy(buffer.Address, bytes, 0, bytes.Length);
        var bins = new Dictionary<int, (long R, long G, long B, int Count)>();
        for (int y = 0; y < buffer.Size.Height; y++)
        for (int x = 0; x < buffer.Size.Width; x++)
        {
            int i = y * buffer.RowBytes + x * 4;
            int b = bytes[i], g = bytes[i + 1], r = bytes[i + 2];
            if (bytes[i + 3] < 128) continue;
            int high = Math.Max(r, Math.Max(g, b)), low = Math.Min(r, Math.Min(g, b));
            if (high < 32 || low > 230) continue;
            int key = (r >> 4) << 8 | (g >> 4) << 4 | b >> 4;
            var bin = bins.GetValueOrDefault(key);
            bins[key] = (bin.R + r, bin.G + g, bin.B + b, bin.Count + 1);
        }
        if (bins.Count == 0) return Color.Parse("#60727B");
        var winner = bins.Values.MaxBy(bin => bin.Count);
        return Color.FromRgb((byte)(winner.R / winner.Count), (byte)(winner.G / winner.Count), (byte)(winner.B / winner.Count));
    }

    private static string Hex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, StringBuilder value, uint flags);
}
