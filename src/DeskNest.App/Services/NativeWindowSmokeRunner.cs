using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    private static bool _spaceWindow;
    private static bool _manualWorkflow;

    public static int Run(string[] args)
    {
        IsActive = true;
        _exitCode = 1;
        _inspectRecovery = args.Contains("--inspect-recovery", StringComparer.Ordinal);
        _spaceWindow = args.Contains("--space-window", StringComparer.Ordinal);
        _manualWorkflow = args.Contains("--manual-workflow", StringComparer.Ordinal);
        var tempDir = Path.Combine(Path.GetTempPath(), "DeskNest.NativeSmoke." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            if (_manualWorkflow && (_inspectRecovery || _spaceWindow))
                throw new ArgumentException("Run the manual workflow separately from recovery/space-window inspection.");
            var spaceId = Guid.NewGuid();
            var testSpace = new WorkspaceSpace(spaceId, "NativeSmokeSpace", "Temporary space for native window smoke", SpaceStorageMode.Managed, Path.Combine(tempDir, "SmokeSpace"));

            var files = new List<WorkspaceFile>();
            if (_spaceWindow)
            {
                Directory.CreateDirectory(testSpace.Folder);
                string file = Path.Combine(testSpace.Folder, "NativeSmoke.txt");
                File.WriteAllText(file, "Isolated native window fixture");
                files.Add(new WorkspaceFile(Guid.NewGuid(), spaceId, Path.GetFileName(file), file, false));
            }

            // Initialize isolated workspace state in temporary directory (never default user app data)
            ActiveTempStore = Task.Run(async () =>
            {
                var store = await WorkspaceStore.OpenAsync(tempDir);
                await store.UpdateAsync(s => s with
                {
                    OnboardingComplete = true,
                    OnboardingStep = 5,
                    Spaces = [testSpace],
                    Files = files,
                    Settings = s.Settings with { ManagedRoot = Path.Combine(tempDir, "Managed") }
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

    // Uses the actual native clipboard, but commands rather than physical key/mouse injection.
    // Require an empty clipboard; never save/restore arbitrary user clipboard formats.
    private static async Task VerifyManualWorkflowAsync(Window window)
    {
        if (window.DataContext is not MainWindowViewModel main || main.Studio is not { } studio || ActiveTempStore is not { } store)
            throw new InvalidOperationException("The native manual workflow requires its isolated workspace.");
        var clipboard = window.Clipboard ?? throw new InvalidOperationException("Native clipboard is unavailable.");
        if ((await clipboard.GetDataFormatsAsync()).Any())
            throw new InvalidOperationException("Native manual workflow requires an empty clipboard; existing clipboard content was not changed.");
        var ownedIds = new HashSet<Guid>();
        try
        {
            var source = store.Snapshot.Spaces.Single();
            Directory.CreateDirectory(source.Folder);
            foreach (bool directory in new[] { false, true })
            {
                string path = Path.Combine(source.Folder, directory ? "Native project" : "Native document.txt");
                if (directory) Directory.CreateDirectory(Path.Combine(path, "empty"));
                string content = directory ? Path.Combine(path, "document.txt") : path;
                await File.WriteAllTextAsync(content, "native clipboard fixture");
                studio.SelectSpace(studio.AllSpaces.Single(s => s.Id == source.Id));
                await studio.DropPathsOnSpaceAsync([path]);
                var file = store.Snapshot.Files.Single(f => f.Path == path);
                ownedIds.Add(file.Id);
                studio.OpenAddSpaceDialog();
                studio.NewSpaceName = directory ? "Directory destination" : "File destination";
                await studio.ConfirmAddSpaceAsync();
                var target = studio.AllSpaces.Single(s => s.Name == studio.NewSpaceName);
                if (Directory.Exists(target.Folder))
                    throw new InvalidOperationException("A new managed destination must start without a physical folder.");
                studio.SelectSpace(studio.AllSpaces.Single(s => s.Id == source.Id));
                studio.SelectFile(studio.SelectedSpace!.Files.Single(f => f.Id == file.Id));
                await studio.ExecuteCutFileCommand.ExecuteAsync(studio.SelectedFile);
                var payload = await AvaloniaClipboardBridge.TryGetFilePayloadAsync(clipboard);
                if (payload is null || AvaloniaClipboardBridge.ResolveCutSource(payload, store.Snapshot)?.Id != file.Id)
                    throw new InvalidOperationException("The native clipboard did not round-trip the exact cut subject.");
                await studio.ExecutePasteFileCommand.ExecuteAsync(target);
                var moved = store.Snapshot.Files.Single(f => f.Id == file.Id);
                var operation = store.Snapshot.Operations.Single(o => o.FileId == file.Id);
                string destination = Path.Combine(target.Folder, file.Name);
                if (moved.SpaceId != target.Id || moved.Path != destination || File.Exists(path) || Directory.Exists(path) ||
                    operation.Status != ProposedOperationStatus.Completed ||
                    await AvaloniaClipboardBridge.TryGetFilePayloadAsync(clipboard) is not null)
                    throw new InvalidOperationException("Native cut/paste did not commit the move and clear its clipboard payload.");
                await studio.ExecuteUndoManualMoveCommand.ExecuteAsync(operation.Id);
                if (File.ReadAllText(content) != "native clipboard fixture" || File.Exists(destination) || Directory.Exists(destination) ||
                    (directory && !Directory.Exists(Path.Combine(path, "empty"))) ||
                    store.Snapshot.Files.Single(f => f.Id == file.Id).Path != path ||
                    store.Snapshot.Operations.Single(o => o.Id == operation.Id).Status != ProposedOperationStatus.Undone)
                    throw new InvalidOperationException("Native manual undo did not restore the original item.");

                if (OperatingSystem.IsWindows())
                {
                    studio.SelectSpace(studio.AllSpaces.Single(s => s.Id == source.Id));
                    studio.SelectFile(studio.SelectedSpace!.Files.Single(f => f.Id == file.Id));
                    await studio.ExecuteCopyFileCommand.ExecuteAsync(studio.SelectedFile);
                    var copiedPayload = await AvaloniaClipboardBridge.TryGetFilePayloadAsync(clipboard);
                    if (copiedPayload is null || copiedPayload.IsCut ||
                        AvaloniaClipboardBridge.ResolveFileSource(copiedPayload, store.Snapshot)?.Id != file.Id)
                        throw new InvalidOperationException("The native clipboard did not round-trip the exact copy subject.");
                    int historyCount = store.Snapshot.Operations.Count;
                    await studio.ExecutePasteFileCommand.ExecuteAsync(studio.AllSpaces.Single(s => s.Id == target.Id));
                    var copied = store.Snapshot.Files.SingleOrDefault(f => f.SpaceId == target.Id);
                    if (copied is null || copied.Id == file.Id || copied.Publication is null ||
                        store.Snapshot.Operations.Count != historyCount ||
                        File.ReadAllText(content) != "native clipboard fixture" ||
                        File.ReadAllText(directory ? Path.Combine(destination, "document.txt") : destination) != "native clipboard fixture" ||
                        (directory && !Directory.Exists(Path.Combine(destination, "empty"))) ||
                        (await AvaloniaClipboardBridge.TryGetFilePayloadAsync(clipboard))?.IsCut != false)
                        throw new InvalidOperationException("Native copy must retain source/clipboard and enroll a distinct publication without undo history.");

                    // Existing confirmation/undo commands, from outside the managed space.
                    string external = Path.Combine(store.DataDirectory, directory ? "External project" : "External document.txt");
                    if (directory) Directory.CreateDirectory(Path.Combine(external, "empty"));
                    string externalContent = directory ? Path.Combine(external, "document.txt") : external;
                    await File.WriteAllTextAsync(externalContent, "native import fixture");
                    await studio.RegisterPathToTriageAsync(external);
                    var pending = studio.PendingItems.Single(p => p.Path == external);
                    pending.TargetSpace = studio.AllSpaces.Single(s => s.Id == target.Id);
                    studio.OpenImportConfirmation(pending);
                    if (!studio.IsImportConfirmationOpen || studio.ImportSourcePath != external)
                        throw new InvalidOperationException("Native import must require confirmation of its external source.");
                    await studio.ConfirmImportAsync();
                    var imported = store.Snapshot.Operations.Single(o => o.ImportSource?.Id == pending.Id);
                    if (studio.IsImportConfirmationOpen || imported.Status != ProposedOperationStatus.Completed ||
                        File.Exists(external) || Directory.Exists(external))
                        throw new InvalidOperationException("Confirmed native import did not move the external item.");
                    await studio.ExecuteUndoManualMoveCommand.ExecuteAsync(imported.Id);
                    if (File.ReadAllText(externalContent) != "native import fixture" ||
                        (directory && !Directory.Exists(Path.Combine(external, "empty"))) ||
                        !store.Snapshot.Pending.Any(p => p.Id == pending.Id) ||
                        store.Snapshot.Operations.Single(o => o.Id == imported.Id).Status != ProposedOperationStatus.Undone)
                        throw new InvalidOperationException("Native import undo must restore both the external item and pending review.");
                    // Only our fixture payload, never arbitrary clipboard formats.
                    var remainingCopy = await AvaloniaClipboardBridge.TryGetFilePayloadAsync(clipboard);
                    if (remainingCopy?.SourceFileId == file.Id) await clipboard.ClearAsync();
                }
            }
        }
        finally
        {
            // On a failed test clear only a payload belonging to these temporary fixture files.
            var remaining = await AvaloniaClipboardBridge.TryGetFilePayloadAsync(clipboard);
            if (remaining?.SourceFileId is { } id && ownedIds.Contains(id)) await clipboard.ClearAsync();
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

                    bool manualWorkflowVerified = false;
                    if (_manualWorkflow)
                    {
                        await VerifyManualWorkflowAsync(window);
                        manualWorkflowVerified = true;
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
                    string? spaceHandle = null;
                    if (_spaceWindow)
                    {
                        var view = window.GetVisualDescendants().OfType<StudioView>().Single();
                        var floating = view.OpenSelectedSpaceWindow() ?? throw new InvalidOperationException("No space window was created.");
                        floating.UpdateLayout();
                        var handle = floating.TryGetPlatformHandle()?.Handle;
                        if (handle is null || handle == IntPtr.Zero || handle == platformHandle ||
                            !floating.IsVisible || floating.Bounds.Width <= 0 || floating.Space?.Files.Count != 1)
                            throw new InvalidOperationException("Space window needs a distinct native handle, layout and catalog.");
                        spaceHandle = $"0x{handle.Value.ToInt64():X}";
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
                        ManualWorkflowVerified = manualWorkflowVerified,
                        NativeClipboardRoundTripVerified = manualWorkflowVerified,
                        SpaceWindowVerified = spaceHandle is not null,
                        SpaceWindowHandle = spaceHandle,
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
