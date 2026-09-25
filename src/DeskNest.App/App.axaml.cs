using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using DeskNest.App.Views;

namespace DeskNest.App;

public partial class App : Application
{
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
                desktop.MainWindow = new MainWindow();
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}
