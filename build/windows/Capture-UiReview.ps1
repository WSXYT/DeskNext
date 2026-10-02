param(
    [string]$App = "$PSScriptRoot/../../src/DeskNest.App/bin/Release/net10.0/DeskNest.App.exe"
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
# Developer-only, opt-in desktop capture. Never included in the application payload.
# Pixels between our windows may include desktop background; keep these local artifacts private.
Add-Type -AssemblyName System.Drawing
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class DeskNextReviewNative {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll", SetLastError=true)] public static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll", SetLastError=true)] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
}
'@
$priorDpi = [DeskNextReviewNative]::SetThreadDpiAwarenessContext([IntPtr](-4))
if ($priorDpi -eq [IntPtr]::Zero) { throw 'Could not enable pixel-accurate capture for this thread.' }
$output = Join-Path "$PSScriptRoot/../../artifacts/ui-review" ('native-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0,6))
[IO.Directory]::CreateDirectory($output) | Out-Null
$output = [IO.Path]::GetFullPath($output)
$start = New-Object Diagnostics.ProcessStartInfo
$start.FileName = (Resolve-Path -LiteralPath $App).Path
$start.Arguments = '--native-window-smoke --desktop-review'
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$process = [Diagnostics.Process]::Start($start)
$errors = $process.StandardError.ReadToEndAsync()
$log = New-Object Text.StringBuilder
try {
    $ready = $null
    while ($null -eq $ready) {
        $read = $process.StandardOutput.ReadLineAsync()
        if (!$read.Wait(20000)) { throw 'Desktop review did not report ready within the startup budget.' }
        $line = $read.GetAwaiter().GetResult()
        if ($null -eq $line) { throw 'Desktop review exited without opening all three windows.' }
        [void]$log.AppendLine($line)
        if ($line.StartsWith('DESKTOP_REVIEW_READY:')) {
            $ready = $line.Substring('DESKTOP_REVIEW_READY:'.Length) | ConvertFrom-Json
        }
    }
    if (@($ready.Windows).Count -ne 3) { throw 'Expected main, space and capsule windows.' }
    $rectangles = @($ready.Windows | ForEach-Object {
        $rect = New-Object DeskNextReviewNative+Rect
        if (![DeskNextReviewNative]::GetWindowRect([IntPtr]([long]$_.Handle), [ref]$rect)) { throw 'Native review window vanished.' }
        $rect
    })
    $left = ($rectangles.Left | Measure-Object -Minimum).Minimum
    $top = ($rectangles.Top | Measure-Object -Minimum).Minimum
    $right = ($rectangles.Right | Measure-Object -Maximum).Maximum
    $bottom = ($rectangles.Bottom | Measure-Object -Maximum).Maximum
    $bitmap = New-Object Drawing.Bitmap ([int]($right-$left)), ([int]($bottom-$top))
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen([int]$left, [int]$top, 0, 0, $bitmap.Size, [Drawing.CopyPixelOperation]::SourceCopy)
        $bitmap.Save((Join-Path $output 'desktop-composite.png'), [Drawing.Imaging.ImageFormat]::Png)
    } finally { $graphics.Dispose(); $bitmap.Dispose() }
    $rest = $process.StandardOutput.ReadToEndAsync()
    if (!$process.WaitForExit(15000)) { throw 'Desktop review did not close its temporary windows.' }
    [void]$log.Append($rest.GetAwaiter().GetResult())
    $match = [regex]::Match($log.ToString(), 'NATIVE_WINDOW_RESULT_JSON:\s*(\{[\s\S]*\})\s*$')
    if (!$match.Success) { throw 'Missing terminal native-window evidence.' }
    $result = $match.Groups[1].Value | ConvertFrom-Json
    if ($process.ExitCode -ne 0 -or $result.Success -cne $true -or $result.DesktopReviewShown -cne $true) {
        throw 'Native desktop review did not complete successfully; retain the log, not an acceptance claim.'
    }
    @{
        kind = 'native-desktop-composite'; windows = $ready.Windows
        pixels = @{ left = $left; top = $top; width = $right-$left; height = $bottom-$top }
        nativeResult = $result
        scope = 'Real OS pixels; isolated sample files; one display/scale only. Not multi-DPI, physical-input, material or release acceptance.'
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $output 'capture.json') -Encoding UTF8
    Write-Output "Review capture: $output"
} finally {
    if (!$process.HasExited) { $process.Kill(); $process.WaitForExit() }
    [void]$log.AppendLine($errors.GetAwaiter().GetResult())
    $log.ToString() | Set-Content -LiteralPath (Join-Path $output 'native-window.log') -Encoding UTF8
    $process.Dispose()
    [void][DeskNextReviewNative]::SetThreadDpiAwarenessContext($priorDpi)
}
