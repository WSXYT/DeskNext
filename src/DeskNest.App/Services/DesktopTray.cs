using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using CommunityToolkit.Mvvm.Input;
using DeskNest.App.Localization;
using DeskNest.App.ViewModels;
using DeskNest.App.Views;

namespace DeskNest.App.Services;

// Optional native tray entry. Closing the workbench still closes the application;
// an unavailable tray never leaves the user with a hidden, inaccessible workspace.
internal sealed class DesktopTray : IDisposable
{
    private readonly Application _application;
    private readonly Window _window;
    private readonly MainWindowViewModel _workspace;
    private readonly TrayIcon _icon;
    private readonly NativeMenuItem _show;
    private readonly NativeMenuItem _capsule;
    private readonly NativeMenuItem _exit;
    private bool _disposed;
    internal NativeMenu Menu { get; } = new();

    internal DesktopTray(Application application, Window window, MainWindowViewModel workspace, Action exit)
    {
        _application = application;
        _window = window;
        _workspace = workspace;
        _show = new NativeMenuItem { Command = new RelayCommand(ShowWorkbench) };
        _capsule = new NativeMenuItem { Command = new RelayCommand(() =>
        {
            if (_disposed || !_workspace.IsStudioActive) return;
            ShowWorkbench();
            _window.GetVisualDescendants().OfType<StudioView>().FirstOrDefault()?.OpenDropCapsuleWindow();
        }) };
        _exit = new NativeMenuItem { Command = new RelayCommand(() => { if (!_disposed) exit(); }) };
        Menu.Items.Add(_show);
        Menu.Items.Add(_capsule);
        Menu.Items.Add(new NativeMenuItemSeparator());
        Menu.Items.Add(_exit);
        _icon = new TrayIcon { Icon = CreateIcon(application), ToolTipText = "DeskNext", Menu = Menu, IsVisible = true };
        _icon.Clicked += OnClicked;
        var icons = TrayIcon.GetIcons(application) ?? new TrayIcons();
        icons.Add(_icon);
        TrayIcon.SetIcons(application, icons);
        LocalizationManager.Instance.PropertyChanged += OnStateChanged;
        workspace.PropertyChanged += OnStateChanged;
        window.Closed += OnClosed;
        RefreshMenu();
    }

    private static WindowIcon CreateIcon(Application application)
    {
        // Reuse the hand-drawn brand geometry, not an additional icon package/asset pipeline.
        var glyph = new PathIcon
        {
            Data = (Geometry)application.FindResource("IconDeskNext")!,
            Foreground = new SolidColorBrush(Color.Parse("#187B68"))
        };
        glyph.Measure(new Size(32, 32));
        glyph.Arrange(new Rect(0, 0, 32, 32));
        using var bitmap = new RenderTargetBitmap(new PixelSize(32, 32));
        bitmap.Render(glyph);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        stream.Position = 0;
        return new WindowIcon(stream);
    }

    private void RefreshMenu()
    {
        var localizer = LocalizationManager.Instance;
        _show.Header = localizer["Spaces.ReturnToStudio"];
        _capsule.Header = localizer["Capsule.Title"];
        _capsule.IsEnabled = _workspace.IsStudioActive;
        _exit.Header = localizer["Action.Exit"];
    }

    private void ShowWorkbench()
    {
        if (_disposed) return;
        _window.Show();
        if (_window.WindowState == WindowState.Minimized) _window.WindowState = WindowState.Normal;
        _window.Activate();
    }

    private void OnClicked(object? sender, EventArgs e) => ShowWorkbench();
    private void OnStateChanged(object? sender, PropertyChangedEventArgs e) => RefreshMenu();
    private void OnClosed(object? sender, EventArgs e) => Dispose();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        LocalizationManager.Instance.PropertyChanged -= OnStateChanged;
        _workspace.PropertyChanged -= OnStateChanged;
        _window.Closed -= OnClosed;
        _icon.Clicked -= OnClicked;
        TrayIcon.GetIcons(_application)?.Remove(_icon);
        _icon.Dispose();
    }
}
