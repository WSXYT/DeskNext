using System;
using System.Linq;
using Avalonia;
using Avalonia.Fonts.Inter;
using DeskNest.App.Services;

namespace DeskNest.App;

public static class Program
{
    internal static ExperimentalNvidiaSession? NvidiaSession { get; private set; }
    // Initialization code. Don't use any Avalonia, third-party APIs or any
    // SynchronizationContext-reliant code before AppMain is called: things aren't initialized
    // yet and stuff might break.
    [STAThread]
    public static int Main(string[] args)
    {
        // Dispatch before Avalonia, workspace ownership, or any desktop service is initialized.
        if (args.Length > 0 && args[0] == "--inference-worker")
        {
            bool versioned = args.Length == 3 && args[2] == "--protocol=1";
            if (args.Length != 2 && !versioned) return 2;
            try
            {
                // Own the process-global environment until all worker sessions have closed.
                // Explicit release avoids ORT 1.22 macOS static logger teardown (#24579).
                using var environment = Microsoft.ML.OnnxRuntime.OrtEnv.Instance();
                DeskNest.Inference.Probe.Worker(args[1], Console.OpenStandardInput(), Console.OpenStandardOutput(),
                    versioned: versioned);
                return 0;
            }
            catch (Exception error)
            {
                Console.Error.WriteLine(error.Message);
                return 1;
            }
        }

        // Explicit isolated GPU/workbench fixture only; normal startup and saved settings stay CPU-backed.
        if (args.Contains("--directml-preview-smoke", StringComparer.Ordinal))
            return DirectMlPreviewSmoke.Run(args);

        // Session-only opt-in. Extra/malformed arguments must not silently open a CPU session.
        if (args.Any(a => a == "--experimental-nvidia" || a.StartsWith("--gpu-", StringComparison.Ordinal)))
        {
            try { NvidiaSession = ExperimentalNvidiaSession.Parse(args); }
            catch (Exception error) { Console.Error.WriteLine(error.Message); return 2; }
            try { return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
            finally { NvidiaSession = null; }
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

        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            // Match the actual blur surface to the borderless window, not just its XAML content.
            .With(new Win32PlatformOptions { WinUICompositionBackdropCornerRadius = 8 })
            .WithInterFont()
            .LogToTrace();
}
