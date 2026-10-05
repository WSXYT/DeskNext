using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DeskNest.App.Services;

namespace DeskNest.App.Views;

// Decodes only realized controls. Custom images belong to the control; system type icons are shared.
public sealed class FileIcon : ContentControl
{
    public static readonly StyledProperty<string?> FileNameProperty = AvaloniaProperty.Register<FileIcon, string?>(nameof(FileName));
    public static readonly StyledProperty<bool> IsDirectoryProperty = AvaloniaProperty.Register<FileIcon, bool>(nameof(IsDirectory));
    public static readonly StyledProperty<string?> CustomIconPathProperty = AvaloniaProperty.Register<FileIcon, string?>(nameof(CustomIconPath));
    public string? FileName { get => GetValue(FileNameProperty); set => SetValue(FileNameProperty, value); }
    public bool IsDirectory { get => GetValue(IsDirectoryProperty); set => SetValue(IsDirectoryProperty, value); }
    public string? CustomIconPath { get => GetValue(CustomIconPathProperty); set => SetValue(CustomIconPathProperty, value); }
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly PathIcon _fallback = new() { Foreground = Brushes.Gray };
    private Bitmap? _owned;
    private bool _attached;
    private int _request;
    public FileIcon()
    {
        Content = new Grid { Children = { _fallback, _image } };
        HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        VerticalContentAlignment = Avalonia.Layout.VerticalAlignment.Stretch;
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); _attached = true; RefreshIcon(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false; _request++; _image.Source = null; _owned?.Dispose(); _owned = null;
        base.OnDetachedFromVisualTree(e);
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (_attached && (change.Property == FileNameProperty || change.Property == IsDirectoryProperty || change.Property == CustomIconPathProperty))
            RefreshIcon();
    }
    private async void RefreshIcon()
    {
        int request = ++_request;
        string? custom = CustomIconPath;
        var key = IsDirectory ? "IconFolder" : "IconFile";
        if (this.TryFindResource(key, out var resource) && resource is Geometry geometry) _fallback.Data = geometry;
        Bitmap? owned = null;
        try
        {
            if (custom is not null) owned = await Task.Run(() => LoadCustom(custom));
            var icon = owned ?? await SystemFileIcons.GetAsync(FileName ?? "", IsDirectory);
            if (!_attached || request != _request) { owned?.Dispose(); return; }
            _image.Source = icon; _fallback.IsVisible = icon is null;
            _owned?.Dispose(); _owned = owned;
        }
        catch (Exception error) when (error is System.IO.IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            owned?.Dispose();
            if (request == _request) { _image.Source = null; _fallback.IsVisible = true; _owned?.Dispose(); _owned = null; }
        }
    }
    internal static Bitmap LoadCustom(string path)
    {
        path = DeskNest.Platform.PlatformFileActions.RequireExistingLocalPath(path);
        if (new System.IO.FileInfo(path).Length > 8 * 1024 * 1024)
            throw new System.IO.InvalidDataException("Icon image exceeds 8 MiB.");
        using var stream = System.IO.File.OpenRead(path);
        var image = Bitmap.DecodeToWidth(stream, 64);
        if (image.PixelSize.Height > 512) { image.Dispose(); throw new System.IO.InvalidDataException("Icon image is too tall."); }
        return image;
    }
}
