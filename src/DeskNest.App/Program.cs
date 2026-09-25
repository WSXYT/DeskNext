using System;
using System.Linq;
using Avalonia;
using Avalonia.Fonts.Inter;
using DeskNest.App.Services;

namespace DeskNest.App;

public static class Program
{
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // Support headless smoke and probe diagnostics from CLI
        if (args.Any(a => a.Equals("--headless-smoke", StringComparison.OrdinalIgnoreCase) ||
                          a.Equals("--smoke", StringComparison.OrdinalIgnoreCase) ||
                          a.Equals("--probe", StringComparison.OrdinalIgnoreCase) ||
                          a.Equals("--native-library", StringComparison.OrdinalIgnoreCase) ||
                          a.StartsWith("--native-library=", StringComparison.OrdinalIgnoreCase)))
        {
            return HeadlessSmokeRunner.RunSmokeAsync(args).GetAwaiter().GetResult();
        }

        // Standard desktop GUI execution
        return BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
