using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using DeskNest.App.ViewModels;
using DeskNest.App.Views;
using DeskNest.Core.Workspace;

namespace DeskNest.App.Services;

public static class NativeWindowSmokeRunner
{
    public static bool IsActive { get; private set; }
    public static WorkspaceStore? ActiveTempStore { get; private set; }
    private static int _exitCode = 1;
    private static bool _inspectRecovery;

    public static int Run(string[] args)
    {
        IsActive = true;
        _exitCode = 1;
        _inspectRecovery = args.Contains("--inspect-recovery", StringComparer.Ordinal);
        var tempDir = Path.Combine(Path.GetTempPath(), "DeskNest.NativeSmoke." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            var spaceId = Guid.NewGuid();
            var testSpace = new WorkspaceSpace(spaceId, "NativeSmokeSpace", "Temporary space for native window smoke", SpaceStorageMode.Managed, Path.Combine(tempDir, "SmokeSpace"));

            // Initialize isolated workspace state in temporary directory (never default user app data)
            ActiveTempStore = Task.Run(async () =>
            {
                var store = await WorkspaceStore.OpenAsync(tempDir);
                await store.UpdateAsync(s => s with
                {
                    OnboardingComplete = true,
                    OnboardingStep = 5,
                    Spaces = [testSpace]
                });
                return store;
            }).GetAwaiter().GetResult();

            if (_inspectRecovery)
                File.WriteAllText(Path.Combine(tempDir, "copy-recovery.json"), "{");
            var builder = Program.BuildAvaloniaApp();
            builder.StartWithClassicDesktopLifetime(args, ShutdownMode.OnMainWindowClose);
            return _exitCode;
        }
        catch (Exception ex)
        {
            _exitCode = 1;
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[NATIVE-WINDOW-SMOKE] Native desktop window startup failed: {ex.Message}");
            Console.ResetColor();

            var failJson = new
            {
                Success = false,
                HostOS = RuntimeInformation.OSDescription,
                HostRID = RuntimeInformation.RuntimeIdentifier,
                Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                Error = ex.Message,
                NativeWindowOpened = false
            };
            Console.WriteLine("NATIVE_WINDOW_RESULT_JSON:");
            Console.WriteLine(JsonSerializer.Serialize(failJson, new JsonSerializerOptions { WriteIndented = true }));
            return _exitCode;
        }
        finally
        {
            if (ActiveTempStore != null)
            {
                Task.Run(async () => await ActiveTempStore.DisposeAsync()).GetAwaiter().GetResult();
                ActiveTempStore = null;
            }
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch { }
            IsActive = false;
        }
    }

    public static void AttachAutoClose(IClassicDesktopStyleApplicationLifetime desktop, Window window)
    {
        // 15-second watchdog timer: prevents CLI hanging if Window Manager never opens window
        var watchdogTimer = new System.Threading.Timer(_ =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("[NATIVE-WINDOW-SMOKE] Watchdog timeout: Window Manager failed to open native window within 15 seconds.");
                Console.ResetColor();

                var timeoutJson = new
                {
                    Success = false,
                    HostOS = RuntimeInformation.OSDescription,
                    HostRID = RuntimeInformation.RuntimeIdentifier,
                    Error = "Window Manager Opened timeout after 15 seconds.",
                    NativeWindowOpened = false
                };
                Console.WriteLine("NATIVE_WINDOW_RESULT_JSON:");
                Console.WriteLine(JsonSerializer.Serialize(timeoutJson, new JsonSerializerOptions { WriteIndented = true }));

                _exitCode = 1;
                desktop.Shutdown(1);
            });
        }, null, TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);

        window.Opened += (s, e) =>
        {
            // Keep the watchdog active through optional UI recovery work.
            Dispatcher.UIThread.Post(async () =>
            {
                try
                {
                    var platformHandle = window.TryGetPlatformHandle()?.Handle;
                    var bounds = window.Bounds;
                    var renderScaling = window.RenderScaling;
                    var title = window.Title ?? string.Empty;

                    // Strict validation: native handle and render bounds must be valid
                    if (platformHandle == null || platformHandle.Value == IntPtr.Zero)
                    {
                        throw new InvalidOperationException("Window Manager reported null or zero native window handle.");
                    }

                    if (bounds.Width <= 0 || bounds.Height <= 0)
                    {
                        throw new InvalidOperationException($"Invalid native window render bounds: {bounds.Width}x{bounds.Height}.");
                    }

                    bool recoveryInspected = false;
                    if (_inspectRecovery)
                    {
                        if (window.DataContext is not MainWindowViewModel vm || ActiveTempStore is null)
                            throw new InvalidOperationException("Recovery inspection requires the isolated production view model.");
                        await vm.InitializeWorkspaceAsync(ActiveTempStore.DataDirectory);
                        await vm.InspectRecoveryEvidenceAsync();
                        await vm.RetryStartupAsync();
                        await vm.InspectRecoveryEvidenceAsync();
                        var evidenceBox = window.FindControl<TextBox>("RecoveryEvidenceTextBox");
                        if (!vm.IsRecoveryRequired || vm.IsLockConflict || vm.IsStudioActive || !vm.HasRecoveryEvidence ||
                            evidenceBox is null || !evidenceBox.IsVisible || !evidenceBox.IsReadOnly ||
                            evidenceBox.Text != vm.RecoveryEvidenceText ||
                            File.ReadAllText(Path.Combine(ActiveTempStore.DataDirectory, "copy-recovery.json")) != "{")
                            throw new InvalidOperationException("Native recovery inspection/retry modified evidence or exposed workspace actions.");
                        recoveryInspected = true;
                    }
                    Console.WriteLine("================================================================================");
                    Console.WriteLine($"DeskNext - Native Window Real-OS Startup Smoke ({RuntimeInformation.OSDescription})");
                    Console.WriteLine("================================================================================");
                    Console.WriteLine($"  ✓ Window Manager Native Window Opened: Handle=0x{platformHandle.Value.ToInt64():X}");
                    Console.WriteLine($"  ✓ Native Window Render Bounds: {bounds.Width:F0}x{bounds.Height:F0}");
                    Console.WriteLine($"  ✓ Native Window RenderScaling: {renderScaling:F2} (scale factor: {(int)Math.Round(renderScaling * 100)}%)");
                    Console.WriteLine($"  ✓ Window Title: '{title}'");
                    Console.WriteLine("  ✓ Temporary isolated WorkspaceStore verified; no default user data accessed.");
                    Console.WriteLine("================================================================================");

                    var jsonResult = new
                    {
                        Success = true,
                        RecoveryInspectionVerified = recoveryInspected,
                        InputMethod = "Production view-model commands in a native window; not physical keyboard/mouse injection",
                        HostOS = RuntimeInformation.OSDescription,
                        HostRID = RuntimeInformation.RuntimeIdentifier,
                        Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                        NativeHandle = $"0x{platformHandle.Value.ToInt64():X}",
                        Bounds = $"{bounds.Width:F0}x{bounds.Height:F0}",
                        RenderScaling = renderScaling,
                        ObservedScalePercent = (int)Math.Round(renderScaling * 100),
                        MultiDpiMatrixStatus = "Unverified (Single scale factor observed in current session; multi-DPI 100/150/200% matrix remains open)",
                        LinuxMacNativeWindowStatus = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                            ? "Verified on Windows; Linux and macOS physical desktop windows unverified in this run"
                            : "Unverified on Windows"
                    };
                    Console.WriteLine("NATIVE_WINDOW_RESULT_JSON:");
                    Console.WriteLine(JsonSerializer.Serialize(jsonResult, new JsonSerializerOptions { WriteIndented = true }));

                    _exitCode = 0;
                }
                catch (Exception ex)
                {
                    _exitCode = 1;
                    Console.ForegroundColor = ConsoleColor.Red;
                    Console.WriteLine($"[NATIVE-WINDOW-SMOKE] Native window validation failed: {ex.Message}");
                    Console.ResetColor();

                    var errorJson = new
                    {
                        Success = false,
                        HostOS = RuntimeInformation.OSDescription,
                        HostRID = RuntimeInformation.RuntimeIdentifier,
                        Error = ex.Message,
                        NativeWindowOpened = true
                    };
                    Console.WriteLine("NATIVE_WINDOW_RESULT_JSON:");
                    Console.WriteLine(JsonSerializer.Serialize(errorJson, new JsonSerializerOptions { WriteIndented = true }));
                }
                finally
                {
                    watchdogTimer.Dispose();
                    desktop.Shutdown(_exitCode);
                }
            }, DispatcherPriority.Render);
        };
    }
}
