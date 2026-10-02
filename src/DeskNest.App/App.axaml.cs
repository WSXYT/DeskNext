using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DeskNest.App.Views;

namespace DeskNest.App;

public partial class App : Application
{
    private Services.DesktopTray? _tray;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            if (Services.NativeWindowSmokeRunner.IsActive && Services.NativeWindowSmokeRunner.ActiveTempStore != null)
            {
                var vm = new ViewModels.MainWindowViewModel(Services.NativeWindowSmokeRunner.ActiveTempStore, ownsStore: false);
                var win = new MainWindow(vm);
                Services.NativeWindowSmokeRunner.AttachAutoClose(desktop, win);
                desktop.MainWindow = win;
            }
            else
            {
                var window = new MainWindow();
                desktop.MainWindow = window;
                window.Opened += (_, _) =>
                {
                    try
                    {
                        _tray ??= new Services.DesktopTray(this, window,
                            (ViewModels.MainWindowViewModel)window.DataContext!, () => desktop.Shutdown());
                    }
                    catch (Exception error)
                    {
                        // Optional shell integration: the visible workbench remains usable.
                        System.Diagnostics.Trace.WriteLine("Tray unavailable: " + error.Message);
                    }
                };
                desktop.Exit += (_, _) => _tray?.Dispose();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
