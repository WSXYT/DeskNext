using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using DeskNest.App.Localization;
using DeskNest.App.Themes;
using DeskNest.App.ViewModels;
using DeskNest.App.Views;

namespace DeskNest.App.Services;

public sealed class SmokeTestResult
{
    public bool Success { get; set; }
    public string HostOS { get; set; } = string.Empty;
    public string HostRID { get; set; } = string.Empty;
    public string Architecture { get; set; } = string.Empty;
    public string DotNetVersion { get; set; } = string.Empty;
    public string AvaloniaVersion { get; set; } = "12.1.3";
    public bool HeadlessInitialized { get; set; }
    public bool ThemeSwitchingVerified { get; set; }
    public int VerifiedLanguagesCount { get; set; }
    public bool RtlSupportVerified { get; set; }
    public bool ViewModelVerified { get; set; }
    public bool MainWindowInstantiated { get; set; }
    public bool NativeBridgePendingReported { get; set; }
    public bool CoreFileEnginePendingReported { get; set; }
    public bool InferencePendingReported { get; set; }
    public bool NativeLoadRequested { get; set; }
    public string? RequestedNativeLibraryPath { get; set; }
    public bool NativeLibraryLoaded { get; set; }
    public bool NativeLayoutInvoked { get; set; }
    public bool NativeLoadSuccess { get; set; }
    public string? NativeLoadDetails { get; set; }
    public double WorkingSetMiB { get; set; }
    public long ElapsedMs { get; set; }
    public List<string> Errors { get; set; } = new();
}

public static class HeadlessSmokeRunner
{
    public static async Task<int> RunSmokeAsync(string[] args)
    {
        var sw = Stopwatch.StartNew();

        bool nativeLoadRequested = false;
        string? requestedNativeLibPath = null;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--native-library=", StringComparison.OrdinalIgnoreCase))
            {
                nativeLoadRequested = true;
                requestedNativeLibPath = arg.Substring("--native-library=".Length).Trim('\"');
            }
            else if (arg.Equals("--native-library", StringComparison.OrdinalIgnoreCase))
            {
                nativeLoadRequested = true;
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--"))
                {
                    requestedNativeLibPath = args[i + 1].Trim('\"');
                    i++;
                }
                else
                {
                    requestedNativeLibPath = string.Empty;
                }
            }
        }

        var result = new SmokeTestResult
        {
            HostOS = RuntimeInformation.OSDescription,
            HostRID = RuntimeInformation.RuntimeIdentifier,
            Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
            DotNetVersion = RuntimeInformation.FrameworkDescription,
            NativeLoadRequested = nativeLoadRequested,
            RequestedNativeLibraryPath = requestedNativeLibPath
        };

        Console.WriteLine("================================================================================");
        Console.WriteLine("栖格 · DeskNest - P1 Avalonia 12.1.3 Desktop Shell / Headless Smoke Probe");
        Console.WriteLine("================================================================================");
        Console.WriteLine($"[HOST] OS: {result.HostOS} | RID: {result.HostRID} | Arch: {result.Architecture}");
        Console.WriteLine($"[HOST] .NET Runtime: {result.DotNetVersion}");

        try
        {
            // 1. Initialize Avalonia in headless mode
            Console.WriteLine("[STEP 1] Initializing Avalonia with Skia & Headless platform...");
            var builder = AppBuilder.Configure<App>()
                .UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });

            builder.SetupWithoutStarting();
            result.HeadlessInitialized = (Application.Current != null);
            Console.WriteLine($"  ✓ Avalonia Application.Current active: {result.HeadlessInitialized}");

            // 2. Test Theme switching
            Console.WriteLine("[STEP 2] Verifying Theme Manager (Dark / Light / System)...");
            var themeMgr = ThemeManager.Instance;
            themeMgr.CurrentThemeMode = AppThemeMode.Light;
            if (themeMgr.IsDark)
                throw new InvalidOperationException("Theme did not switch to Light.");

            themeMgr.CurrentThemeMode = AppThemeMode.Dark;
            if (!themeMgr.IsDark)
                throw new InvalidOperationException("Theme did not switch to Dark.");

            result.ThemeSwitchingVerified = true;
            Console.WriteLine("  ✓ Theme switching verified successfully.");

            // 3. Test Localization across 12 languages + RTL
            Console.WriteLine("[STEP 3] Verifying 12-Language Localization Engine & RTL detection...");
            var localizer = LocalizationManager.Instance;
            var requiredKeys = new[]
            {
                "App.Title",
                "App.Tagline",
                "Nav.Overview",
                "Nav.Spaces",
                "Nav.Probe",
                "Welcome.Title",
                "Welcome.DualParadigmTitle",
                "Welcome.ManagedTitle",
                "Welcome.MappedTitle",
                "Spaces.Title",
                "Probe.Title",
                "Theme.Label",
                "Language.Label"
            };

            int validLangs = 0;
            foreach (var lang in LocalizationManager.SupportedLanguages)
            {
                localizer.CurrentLanguage = lang.Code;

                if (lang.IsRtl)
                {
                    if (localizer.FlowDirectionValue != FlowDirection.RightToLeft)
                        throw new InvalidOperationException($"Language {lang.Code} expected RTL FlowDirection but got LTR.");
                    result.RtlSupportVerified = true;
                }
                else
                {
                    if (localizer.FlowDirectionValue != FlowDirection.LeftToRight)
                        throw new InvalidOperationException($"Language {lang.Code} expected LTR FlowDirection but got RTL.");
                }

                var strings = localizer.GetAllStringsForLanguage(lang.Code);
                foreach (var reqKey in requiredKeys)
                {
                    if (!strings.TryGetValue(reqKey, out var val) || string.IsNullOrWhiteSpace(val))
                        throw new InvalidOperationException($"Language {lang.Code} missing required key: {reqKey}");
                }

                validLangs++;
                var rtlTag = lang.IsRtl ? " [RTL verified]" : "";
                Console.WriteLine($"  ✓ Language {lang.Code} ({lang.NativeName}): {strings.Count} keys verified{rtlTag}");
            }

            result.VerifiedLanguagesCount = validLangs;
            // Restore default language
            localizer.CurrentLanguage = "zh-CN";

            // 4. Test ViewModels and Gate verification
            Console.WriteLine("[STEP 4] Verifying MainWindowViewModel, Representative Spaces & Subsystem Gates...");
            var vm = new MainWindowViewModel();

            if (vm.RepresentativeSpaces.Count != 4)
                throw new InvalidOperationException($"Expected 4 representative spaces, found {vm.RepresentativeSpaces.Count}.");

            // Verify no mock deceit: verify truthful pending status
            if (vm.Probe.IsNativeBridgeReady)
                throw new InvalidOperationException("Native bridge falsely marked ready.");
            result.NativeBridgePendingReported = vm.Probe.NativeBridgeStatus.Contains("Pending P1(b)");

            if (vm.Probe.IsCoreEngineReady)
                throw new InvalidOperationException("Core engine falsely marked ready.");
            result.CoreFileEnginePendingReported = vm.Probe.CoreEngineStatus.Contains("Pending P1(a)");

            if (vm.Probe.IsInferenceReady)
                throw new InvalidOperationException("Inference worker falsely marked ready.");
            result.InferencePendingReported = vm.Probe.InferenceStatus.Contains("Pending P1(c)");

            await vm.Probe.RunProbeAsync().ConfigureAwait(false);
            if (vm.Probe.ProbeLogs.Count == 0)
                throw new InvalidOperationException("Probe logs empty after probe run.");

            result.ViewModelVerified = true;
            Console.WriteLine($"  ✓ ViewModel verified. Spaces: {vm.RepresentativeSpaces.Count}, Probe log entries: {vm.Probe.ProbeLogs.Count}");
            Console.WriteLine($"  ✓ Native bridge gate: {vm.Probe.NativeBridgeStatus}");
            Console.WriteLine($"  ✓ Core engine gate: {vm.Probe.CoreEngineStatus}");
            Console.WriteLine($"  ✓ Inference gate: {vm.Probe.InferenceStatus}");

            // 5. Instantiate MainWindow in Headless mode
            Console.WriteLine("[STEP 5] Instantiating MainWindow in Avalonia Headless platform...");
            var window = new MainWindow
            {
                DataContext = vm
            };

            result.MainWindowInstantiated = (window != null);
            var title = window?.Title ?? string.Empty;
            Console.WriteLine($"  ✓ MainWindow instantiated successfully: Title='{title}'");

            // 6. Opt-in Native Pogget C ABI load probe
            if (nativeLoadRequested)
            {
                Console.WriteLine("[STEP 6] Running opt-in Native Pogget C ABI load probe...");
                Console.WriteLine($"  Target library path: '{requestedNativeLibPath}'");

                if (string.IsNullOrWhiteSpace(requestedNativeLibPath))
                {
                    throw new ArgumentException("The --native-library option was specified, but no library path was provided.");
                }

                var nativeResult = NativeBridgeProbe.ExecuteProbe(requestedNativeLibPath);
                result.NativeLibraryLoaded = nativeResult.LibraryLoaded;
                result.NativeLayoutInvoked = nativeResult.LayoutInvoked;
                result.NativeLoadSuccess = nativeResult.Success;
                result.NativeLoadDetails = nativeResult.Details;

                if (!nativeResult.Success)
                {
                    throw new InvalidOperationException($"Native library load probe failed: {nativeResult.Error}");
                }

                vm.Probe.ApplyNativeProbeResult(nativeResult);
                Console.WriteLine($"  ✓ Native library loaded via NativeLibrary.Load: {nativeResult.LibraryPath}");
                Console.WriteLine($"  ✓ Resolved export 'dn_layout' and verified ABI execution.");
                Console.WriteLine($"  ✓ ABI Execution Details: {nativeResult.Details}");
                Console.WriteLine($"  ✓ Native bridge gate (verified opt-in probe): {vm.Probe.NativeBridgeStatus}");
            }
            else
            {
                Console.WriteLine("[STEP 6] Opt-in Native Pogget C ABI probe: Not requested (use --native-library <path> to enable).");
            }

            // 7. Memory statistics
            var workingSet = (double)Environment.WorkingSet / (1024 * 1024);
            result.WorkingSetMiB = Math.Round(workingSet, 2);
            Console.WriteLine($"[PERF] Memory working set: {result.WorkingSetMiB} MiB");

            sw.Stop();
            result.ElapsedMs = sw.ElapsedMilliseconds;
            result.Success = true;

            Console.WriteLine("================================================================================");
            Console.WriteLine($"SMOKE PROBE PASSED in {result.ElapsedMs} ms. Zero exceptions, zero mock deceit.");
            Console.WriteLine("================================================================================");

            // Print structured JSON block
            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine("PROBE_RESULT_JSON:");
            Console.WriteLine(json);

            return 0;
        }
        catch (Exception ex)
        {
            sw.Stop();
            result.Success = false;
            result.ElapsedMs = sw.ElapsedMilliseconds;
            result.Errors.Add(ex.Message);

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"[ERROR] Smoke Probe Failed: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            Console.ResetColor();

            var failJson = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
            Console.WriteLine("PROBE_RESULT_JSON:");
            Console.WriteLine(failJson);

            return 1;
        }
    }
}
