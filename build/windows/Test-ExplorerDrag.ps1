param([string]$App = "$PSScriptRoot/../../src/DeskNest.App/bin/Release/net10.0/DeskNest.App.exe", [switch]$Restart)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if ((New-Object Security.Principal.WindowsPrincipal $identity).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this probe without elevation: normal Explorer cannot drag into an elevated application.'
}
# Developer-only real Explorer/OLE exercise. Temporarily moves the mouse; never uses the clipboard.
# Opens/closes only new Explorer windows for the probe's isolated folders. Keeps failure evidence.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, WindowsBase
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class ExplorerDragNative {
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(Point point);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
}
'@
$priorDpi = [ExplorerDragNative]::SetThreadDpiAwarenessContext([IntPtr](-4))
$cursor = New-Object ExplorerDragNative+Point
[void][ExplorerDragNative]::GetCursorPos([ref]$cursor)
$shell = New-Object -ComObject Shell.Application
$originalWindows = @($shell.Windows() | ForEach-Object { $_.HWND })
$ownedWindows = New-Object 'Collections.Generic.List[object]'
$output = Join-Path "$PSScriptRoot/../../artifacts/p3-publication-tests" ('explorer-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
[IO.Directory]::CreateDirectory($output) | Out-Null
$log = New-Object Text.StringBuilder

function Open-FixtureFolder($folder, $rect) {
    Start-Process explorer.exe -ArgumentList ('/n,"' + $folder + '"')
    $timer = [Diagnostics.Stopwatch]::StartNew()
    $found = $null
    while (!$found -and $timer.Elapsed.TotalSeconds -lt 10) {
        foreach ($candidate in $shell.Windows()) {
            try { if ($candidate.Document.Folder.Self.Path -eq $folder) { $found = $candidate; break } } catch { }
        }
        if (!$found) { Start-Sleep -Milliseconds 100 }
    }
    if (!$found) { throw "Explorer did not open fixture folder: $folder" }
    if ($originalWindows -contains $found.HWND) { throw 'Explorer reused a pre-existing window; refusing to drive or close it.' }
    $ownedWindows.Add($found)
    [void][ExplorerDragNative]::SetWindowPos([IntPtr]$found.HWND, [IntPtr]::Zero, $rect.X, $rect.Y, $rect.Width, $rect.Height, 0x0040)
    [void][ExplorerDragNative]::SetForegroundWindow([IntPtr]$found.HWND)
    Start-Sleep -Milliseconds 500
    return $found
}
function Drag($from, $to, $sourceWindow) {
    [void][ExplorerDragNative]::SetForegroundWindow([IntPtr]([long]$sourceWindow))
    [void][ExplorerDragNative]::SetCursorPos([int]$from.X, [int]$from.Y)
    Start-Sleep -Milliseconds 250
    [ExplorerDragNative]::mouse_event(2, 0, 0, 0, [UIntPtr]::Zero)
    try {
        for ($i = 1; $i -le 24; $i++) {
            [void][ExplorerDragNative]::SetCursorPos([int]($from.X + ($to.X-$from.X)*$i/24), [int]($from.Y + ($to.Y-$from.Y)*$i/24))
            Start-Sleep -Milliseconds 35
        }
        Start-Sleep -Milliseconds 350
    } finally { [ExplorerDragNative]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero) }
}
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = (Resolve-Path -LiteralPath $App).Path
$start.Arguments = '--native-window-smoke --explorer-drag'
if ($Restart) { $start.Arguments += ' --kill-after-explorer' }
$resumeRoot = $null
$restartErrorText = ''
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$process = [Diagnostics.Process]::Start($start)
$errors = $process.StandardError.ReadToEndAsync()
try {
    while ($true) {
        $read = $process.StandardOutput.ReadLineAsync()
        if (!$read.Wait(30000)) { throw 'Explorer probe did not reach the next bounded stage.' }
        $line = $read.GetAwaiter().GetResult()
        if ($null -eq $line) { break }
        [void]$log.AppendLine($line)
        if ($Restart -and $line.StartsWith('EXPLORER_RESTART_READY:')) {
            $resumeRoot = ($line.Substring('EXPLORER_RESTART_READY:'.Length) | ConvertFrom-Json).Root
            $process.Kill()
            if (!$process.WaitForExit(5000) -or $process.ExitCode -eq 0) { throw 'The GUI was not forcibly terminated.' }
            [void]$log.AppendLine('GUI_FORCIBLY_TERMINATED: true')
            break
        }
        if (!$line.StartsWith('EXPLORER_DRAG_READY:')) { continue }
        $stage = $line.Substring('EXPLORER_DRAG_READY:'.Length) | ConvertFrom-Json
        Write-Output ("OS drag stage: " + $stage.Stage + ", directory=" + $stage.Directory)
        $view = Open-FixtureFolder $stage.Folder $stage.ExplorerRect
        $root = [Windows.Automation.AutomationElement]::FromHandle([IntPtr]$view.HWND)
        if ($stage.Stage -eq 'inbound') {
            $view.Document.SelectItem($view.Document.Folder.ParseName($stage.Item), 29)
            $items = $root.FindAll([Windows.Automation.TreeScope]::Descendants, (New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ControlTypeProperty), ([Windows.Automation.ControlType]::ListItem)))
            $item = @($items | Where-Object { $_.Current.Name -eq $stage.Item -or $_.Current.Name -eq [IO.Path]::GetFileNameWithoutExtension($stage.Item) }) | Select-Object -First 1
            if (!$item) { throw 'Explorer fixture item is not exposed in the native file view.' }
            $rect = $item.Current.BoundingRectangle
            $point = New-Object ExplorerDragNative+Point
            $point.X = $stage.Point.X; $point.Y = $stage.Point.Y
            $hitWindow = [ExplorerDragNative]::GetAncestor([ExplorerDragNative]::WindowFromPoint($point), 2)
            Write-Output ("Inbound geometry: item=" + $rect.ToString() + "; target=" + $point.X + ',' + $point.Y + '; expected=' + $stage.Window + '; hit=' + $hitWindow.ToInt64())
            if ($hitWindow.ToInt64() -ne $stage.Window) { throw 'Another window obscures the announced drop point; no drag was injected.' }
            # Use the actual name cell, not a fixed pixel offset (which can hit the selection checkbox at high DPI).
            $nameCell = $item.FindFirst([Windows.Automation.TreeScope]::Descendants,
                (New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::AutomationIdProperty), 'System.ItemNameDisplay'))
            if (!$nameCell) {
                $nameCell = $item.FindFirst([Windows.Automation.TreeScope]::Descendants,
                    (New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ControlTypeProperty), ([Windows.Automation.ControlType]::Text)))
            }
            if (!$nameCell -or $nameCell.Current.BoundingRectangle.IsEmpty) { throw 'Explorer name cell is unavailable; refusing a guessed drag coordinate.' }
            $nameRect = $nameCell.Current.BoundingRectangle
            Write-Output ('Inbound name cell: ' + $nameRect.ToString())
            Drag @{X=$nameRect.X+($nameRect.Width/2); Y=$nameRect.Y+($nameRect.Height/2)} $stage.Point $view.HWND
        } elseif ($stage.Stage -eq 'outbound') {
            # The production Reveal action, not this driver, must have opened the managed folder.
            $located = $null
            foreach ($candidate in $shell.Windows()) {
                try { if ($candidate.Document.Folder.Self.Path -eq $stage.LocatedFolder) { $located = $candidate; break } } catch { }
            }
            if (!$located -or $originalWindows -contains $located.HWND) { throw 'Production Reveal did not open its isolated containing folder.' }
            $ownedWindows.Add($located)
            $condition = New-Object Windows.Automation.PropertyCondition ([Windows.Automation.AutomationElement]::ClassNameProperty), 'UIItemsView'
            $list = $null
            $timer = [Diagnostics.Stopwatch]::StartNew()
            while (!$list -and $timer.Elapsed.TotalSeconds -lt 5) {
                $list = $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
                if (!$list) { Start-Sleep -Milliseconds 100 }
            }
            if (!$list) { throw 'Explorer receiver UIItemsView is unavailable.' }
            $rect = $list.Current.BoundingRectangle
            Drag $stage.Point @{X=$rect.X+($rect.Width*0.75); Y=$rect.Y+($rect.Height*0.7)} $stage.Window
        } else { throw 'Unknown Explorer drag stage.' }
    }
    if ($Restart) {
        if (!$resumeRoot) { throw 'The GUI never reached the committed-import kill barrier.' }
        $start.Arguments = '--native-window-smoke --resume-explorer="' + $resumeRoot + '"'
        $restarted = [Diagnostics.Process]::Start($start)
        try {
            $restartOutput = $restarted.StandardOutput.ReadToEndAsync()
            $restartErrors = $restarted.StandardError.ReadToEndAsync()
            if (!$restarted.WaitForExit(45000)) { throw 'Restarted GUI did not finish recovery and undo.' }
            [void]$log.AppendLine($restartOutput.GetAwaiter().GetResult())
            $restartErrorText = $restartErrors.GetAwaiter().GetResult()
            if ($restarted.ExitCode -ne 0) { throw 'Restarted GUI failed; retain its workspace and log.' }
        } finally {
            if (!$restarted.HasExited) { $restarted.Kill(); $restarted.WaitForExit() }
            $restarted.Dispose()
        }
    }
    if (!$process.WaitForExit(5000)) { throw 'Explorer probe did not exit.' }
    $match = [regex]::Match($log.ToString(), 'NATIVE_WINDOW_RESULT_JSON:\s*(\{[\s\S]*\})\s*$')
    if (!$match.Success) { throw 'Missing terminal Explorer probe evidence.' }
    $result = $match.Groups[1].Value | ConvertFrom-Json
    if ($result.Success -cne $true -or
        (!$Restart -and ($process.ExitCode -ne 0 -or $result.ExplorerDragVerified -cne $true)) -or
        ($Restart -and $result.ExplorerRestartVerified -cne $true)) { throw 'Explorer drag/restart workflow failed; inspect retained logs.' }
    Write-Output "Explorer file/directory import, outgoing Copy and undo passed. Evidence: $output"
} finally {
    [ExplorerDragNative]::mouse_event(4, 0, 0, 0, [UIntPtr]::Zero)
    if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    [void]$log.AppendLine($restartErrorText)
    [void]$log.AppendLine($errors.GetAwaiter().GetResult())
    $log.ToString() | Set-Content -LiteralPath (Join-Path $output 'native-explorer.log') -Encoding UTF8
    foreach ($view in $ownedWindows) { try { $view.Quit() } catch { } }
    $process.Dispose()
    [void][ExplorerDragNative]::SetCursorPos($cursor.X, $cursor.Y)
    [void][ExplorerDragNative]::SetThreadDpiAwarenessContext($priorDpi)
}
