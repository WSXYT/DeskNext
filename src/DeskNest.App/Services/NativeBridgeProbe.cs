using System;
using System.IO;
using System.Runtime.InteropServices;

namespace DeskNest.App.Services;

/// <summary>
/// Result of the native Pogget C ABI load and execution probe.
/// </summary>
public sealed class NativeProbeExecutionResult
{
    public bool LibraryLoaded { get; set; }
    public bool SymbolResolved { get; set; }
    public bool LayoutInvoked { get; set; }
    public bool Success { get; set; }
    public string LibraryPath { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string? Error { get; set; }
}

/// <summary>
/// Struct layout matching dn_icon in native/pogget-bridge/bridge.h.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct DnIcon
{
    public ulong Id;
    public IntPtr Name; // UTF-8 const char*
    public float X;
    public float Y;
    public int Page;
    public int Visible;
}

/// <summary>
/// Struct layout matching dn_layout_config in native/pogget-bridge/bridge.h.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct DnLayoutConfig
{
    public int Width;
    public int Height;
    public int IconSize;
    public int Gap;
    public int Columns;
    public int Rows;
    public int FlowMode;
    public int CurrentPage;
    public int ListView;
}

/// <summary>
/// Struct layout matching dn_layout_result in native/pogget-bridge/bridge.h.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct DnLayoutResult
{
    public int PageCount;
    public int CurrentPage;
    public int Columns;
    public int Rows;
}

/// <summary>
/// C ABI function pointer delegate matching dn_layout in native/pogget-bridge/bridge.h.
/// int dn_layout(const dn_layout_config* config, dn_icon* icons, uint32_t count, dn_layout_result* result);
/// </summary>
[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
public delegate int DnLayoutDelegate(
    ref DnLayoutConfig config,
    IntPtr icons,
    uint count,
    out DnLayoutResult result);

/// <summary>
/// Opt-in probe for dynamically loading the compiled Pogget C ABI library,
/// resolving dn_layout, and verifying execution without filesystem side effects.
/// </summary>
public static class NativeBridgeProbe
{
    public static NativeProbeExecutionResult ExecuteProbe(string libraryPath)
    {
        var probeResult = new NativeProbeExecutionResult
        {
            LibraryPath = libraryPath
        };

        if (string.IsNullOrWhiteSpace(libraryPath))
        {
            probeResult.Error = "Native library path was empty or whitespace.";
            return probeResult;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(libraryPath);
            probeResult.LibraryPath = fullPath;
        }
        catch (Exception ex)
        {
            probeResult.Error = $"Invalid native library path: {ex.Message}";
            return probeResult;
        }

        if (!File.Exists(fullPath))
        {
            probeResult.Error = $"Native library file not found at path: '{fullPath}'";
            return probeResult;
        }

        IntPtr handle = IntPtr.Zero;
        try
        {
            handle = NativeLibrary.Load(fullPath);
            probeResult.LibraryLoaded = true;
        }
        catch (Exception ex)
        {
            probeResult.Error = $"NativeLibrary.Load failed for '{fullPath}': {ex.Message}";
            return probeResult;
        }

        try
        {
            if (!NativeLibrary.TryGetExport(handle, "dn_layout", out var exportPtr))
            {
                probeResult.Error = $"Failed to locate export symbol 'dn_layout' in '{fullPath}'.";
                return probeResult;
            }
            probeResult.SymbolResolved = true;

            var dnLayout = Marshal.GetDelegateForFunctionPointer<DnLayoutDelegate>(exportPtr);

            // Set up test icons for harmless in-memory layout execution
            const int iconCount = 2;
            int structSize = Marshal.SizeOf<DnIcon>();
            IntPtr buffer = Marshal.AllocHGlobal(structSize * iconCount);
            IntPtr name1 = IntPtr.Zero;
            IntPtr name2 = IntPtr.Zero;

            try
            {
                name1 = Marshal.StringToCoTaskMemUTF8("probe_icon_alpha");
                name2 = Marshal.StringToCoTaskMemUTF8("probe_icon_beta");

                var icon1 = new DnIcon
                {
                    Id = 1001,
                    Name = name1,
                    X = 0f,
                    Y = 0f,
                    Page = 0,
                    Visible = 0
                };

                var icon2 = new DnIcon
                {
                    Id = 1002,
                    Name = name2,
                    X = 0f,
                    Y = 0f,
                    Page = 0,
                    Visible = 0
                };

                Marshal.StructureToPtr(icon1, buffer, false);
                Marshal.StructureToPtr(icon2, IntPtr.Add(buffer, structSize), false);

                var config = new DnLayoutConfig
                {
                    Width = 800,
                    Height = 600,
                    IconSize = 48,
                    Gap = 10,
                    Columns = 4,
                    Rows = 4,
                    FlowMode = 0,
                    CurrentPage = 0,
                    ListView = 0
                };

                var layoutResult = new DnLayoutResult();
                int status = dnLayout(ref config, buffer, (uint)iconCount, out layoutResult);
                probeResult.LayoutInvoked = true;

                if (status != 1)
                {
                    probeResult.Error = $"dn_layout returned failure code {status} (expected 1).";
                    return probeResult;
                }

                var outIcon1 = Marshal.PtrToStructure<DnIcon>(buffer);
                var outIcon2 = Marshal.PtrToStructure<DnIcon>(IntPtr.Add(buffer, structSize));

                if (layoutResult.PageCount < 1)
                {
                    probeResult.Error = $"dn_layout returned invalid page count: {layoutResult.PageCount}";
                    return probeResult;
                }

                if (outIcon1.Visible != 1 || outIcon2.Visible != 1)
                {
                    probeResult.Error = $"dn_layout returned unexpected visibility flags: icon1.Visible={outIcon1.Visible}, icon2.Visible={outIcon2.Visible}";
                    return probeResult;
                }

                probeResult.Success = true;
                probeResult.Details = $"dn_layout ABI call succeeded: pageCount={layoutResult.PageCount}, cols={layoutResult.Columns}, rows={layoutResult.Rows}, icon1=(x={outIcon1.X:F1}, y={outIcon1.Y:F1}, vis={outIcon1.Visible}), icon2=(x={outIcon2.X:F1}, y={outIcon2.Y:F1}, vis={outIcon2.Visible})";
                return probeResult;
            }
            finally
            {
                if (buffer != IntPtr.Zero)
                    Marshal.FreeHGlobal(buffer);
                if (name1 != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(name1);
                if (name2 != IntPtr.Zero)
                    Marshal.FreeCoTaskMem(name2);
            }
        }
        catch (Exception ex)
        {
            probeResult.Error = $"Exception during native dn_layout invocation: {ex.Message}";
            return probeResult;
        }
        finally
        {
            if (handle != IntPtr.Zero)
            {
                NativeLibrary.Free(handle);
            }
        }
    }
}
