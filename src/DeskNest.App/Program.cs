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
        // Dispatch before Avalonia, workspace ownership, or any desktop service is initialized.
        if (args.Length > 0 && args[0] == "--inference-worker")
        {
            if (args.Length != 2) return 2;
            try
            {
                DeskNest.Inference.Probe.Worker(args[1], Console.OpenStandardInput(), Console.OpenStandardOutput());
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }

        // Support opt-in native OS desktop window smoke test
        if (args.Any(a => a.Equals("--native-window-smoke", StringComparison.OrdinalIgnoreCase)))
        {
            return NativeWindowSmokeRunner.Run(args);
        }

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
