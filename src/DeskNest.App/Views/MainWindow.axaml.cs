using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using DeskNest.App.ViewModels;

namespace DeskNest.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var vm = new MainWindowViewModel(clipboardProvider: () => this.Clipboard);
        DataContext = vm;
        Activated += (_, _) => Themes.ThemeManager.Instance.RefreshAutomaticAccent();

        Closed += async (s, e) =>
        {
            if (DataContext is MainWindowViewModel mvm)
            {
                await mvm.DisposeAsync();
            }
        };
    }

    public MainWindow(MainWindowViewModel viewModel)
    {
        InitializeComponent();
        viewModel.ClipboardProvider ??= () => this.Clipboard;
        DataContext = viewModel;
        Activated += (_, _) => Themes.ThemeManager.Instance.RefreshAutomaticAccent();

        Closed += async (s, e) =>
        {
            if (DataContext is MainWindowViewModel mvm)
            {
                await mvm.DisposeAsync();
            }
        };
    }

    private bool _drainingFlow, _allowFlowClose;

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel || _allowFlowClose) return;
        if (_drainingFlow) { e.Cancel = true; return; }
        if (DataContext is MainWindowViewModel { Studio: { } studio } vm && studio.RunManualFlowCommand.IsRunning)
        {
            e.Cancel = true;
            _drainingFlow = true;
            _ = CloseAfterFlowAsync(vm);
        }
    }

    private async System.Threading.Tasks.Task CloseAfterFlowAsync(MainWindowViewModel vm)
    {
        try
        {
            await vm.StopFlowPromptRunAsync();
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                _allowFlowClose = true;
                try { Close(); }
                finally { _allowFlowClose = _drainingFlow = false; }
            });
        }
        catch (Exception error)
        {
            _drainingFlow = false;
            if (vm.Studio is { } studio) studio.FlowNotice = vm.Localizer.GetString("Flow.RunFailed", error.Message);
        }
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == WindowStateProperty)
        {
            UpdateMaximizeIcon();
            if (this.FindControl<Border>("WindowFrame") is { } frame)
            {
                bool edgeToEdge = WindowState is WindowState.Maximized or WindowState.FullScreen;
                frame.CornerRadius = !edgeToEdge && this.TryFindResource("RadiusWindow", out var radius)
                    && radius is CornerRadius corners ? corners : new CornerRadius(0);
                frame.BorderThickness = new Thickness(edgeToEdge ? 0 : 1);
            }
        }
    }

    private void UpdateMaximizeIcon()
    {
        var icon = this.FindControl<PathIcon>("MaximizeIcon");
        if (icon != null)
        {
            var key = WindowState == WindowState.Maximized ? "IconChromeRestore" : "IconChromeMaximize";
            if (this.TryFindResource(key, out var res) && res is StreamGeometry geom)
            {
                icon.Data = geom;
            }
        }
    }

    private void OnMinimizeWindowClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnMaximizeRestoreWindowClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseWindowClick(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnTitleBarDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (e.Source is Visual visual && (visual is Button || visual.FindAncestorOfType<Button>() != null || visual is ComboBox || visual.FindAncestorOfType<ComboBox>() != null))
        {
            return;
        }
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnTitleBarPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            if (e.Source is Visual visual && (visual is Button || visual.FindAncestorOfType<Button>() != null || visual is ComboBox || visual.FindAncestorOfType<ComboBox>() != null))
            {
                return;
            }
            BeginMoveDrag(e);
        }
    }
}
