param(
    [Parameter(Mandatory=$true)][string]$Bundle,
    [switch]$NativeWindow,
    [switch]$RecoveryProbe,
    [switch]$ManualWorkflow
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$Bundle = [IO.Path]::GetFullPath($Bundle)
$installer = Join-Path $Bundle 'Install.ps1'
$manifest = Get-Content -Raw -LiteralPath (Join-Path $Bundle 'payload\package.json') | ConvertFrom-Json
if ($RecoveryProbe -and !(Test-Path -LiteralPath (Join-Path $Bundle 'payload\test\recovery\DeskNest.RecoveryProbe.exe'))) {
    throw 'The bundle does not contain the optional test-only recovery probe.'
}
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('DeskNest-package-test-' + [Guid]::NewGuid().ToString('N'))
$installRoot = Join-Path $testRoot 'Installed App With Spaces'
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
function Invoke-Probe([string]$Exe, [string]$Argument, [string]$LogName) {
    $log = Join-Path $testRoot $LogName
    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = $Exe
    $start.Arguments = $Argument
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $timedOut = !$process.WaitForExit(60000)
        if ($timedOut) {
            $process.Kill()
            if (!$process.WaitForExit(5000)) { throw ('Probe did not terminate: ' + $LogName) }
        }
        $streams = [Threading.Tasks.Task[]]@($stdout, $stderr)
        if (![Threading.Tasks.Task]::WaitAll($streams, 5000)) {
            throw ('Probe output did not close: ' + $LogName)
        }
        $output = $stdout.GetAwaiter().GetResult()
        $content = $output + $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText($log, $content)
        Write-Host $content
        if ($timedOut -or $process.ExitCode -ne 0) {
            throw ('Installed probe failed (timeout=' + $timedOut + '). Evidence: ' + $log)
        }
        if ($Argument.StartsWith('--headless-smoke') -or $Argument.StartsWith('--native-window-smoke')) {
            $marker = if ($Argument.StartsWith('--headless-smoke')) { 'PROBE_RESULT_JSON:' } else { 'NATIVE_WINDOW_RESULT_JSON:' }
            $parts = $output.Split([string[]]@($marker), [StringSplitOptions]::None)
            if ($parts.Count -ne 2) { throw ('Missing terminal probe evidence: ' + $log) }
            $result = $parts[1].Trim() | ConvertFrom-Json
            if ($result.Success -isnot [bool] -or !$result.Success) {
                throw ('Probe did not report success: ' + $log)
            }
            if ($Argument.Contains('--manual-workflow') -and
                ($result.ManualWorkflowVerified -isnot [bool] -or !$result.ManualWorkflowVerified -or
                 $result.NativeClipboardRoundTripVerified -isnot [bool] -or !$result.NativeClipboardRoundTripVerified)) {
                throw ('Native manual workflow and clipboard round-trip were not verified: ' + $log)
            }
            if ($Argument.Contains('--inspect-recovery') -and
                ($result.RecoveryInspectionVerified -isnot [bool] -or !$result.RecoveryInspectionVerified)) {
                throw ('Recovery inspection was not verified: ' + $log)
            }
        }
        return $log
    } finally {
        $process.Dispose()
    }
}
# Evidence is retained, even on failure. No test touches the user's workspace.
& $installer -Payload (Join-Path $Bundle 'payload') -InstallRoot $installRoot
$exe = Join-Path $installRoot ('versions\' + $manifest.version + '\DeskNest.App.exe')
Invoke-Probe $exe '--headless-smoke' 'installed-headless.log' | Out-Null
if ($NativeWindow) {
    Invoke-Probe $exe '--native-window-smoke' 'installed-native.log' | Out-Null
    Invoke-Probe $exe '--native-window-smoke --inspect-recovery' 'installed-native-recovery.log' | Out-Null
}
if ($ManualWorkflow) {
    Invoke-Probe $exe '--native-window-smoke --manual-workflow' 'installed-native-manual.log' | Out-Null
}
if ($RecoveryProbe) {
    $driver = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..\tests\recovery-kill.mjs'))
    $probeExe = Join-Path $installRoot ('versions\' + $manifest.version + '\test\recovery\DeskNest.RecoveryProbe.exe')
    $recoveryLog = Invoke-Probe 'node.exe' ('"' + $driver + '" "' + $probeExe + '"') 'installed-recovery.log'
    $expectedKinds = @('file', 'directory', 'committed-file', 'committed-directory',
        'rename-file', 'rename-directory', 'delete-file', 'delete-directory',
        'undo-file', 'undo-directory', 'copy-file', 'copy-directory',
        'committed-copy-file', 'committed-copy-directory',
        'unreceipted-file', 'unreceipted-directory',
        'reverse-unreceipted-file', 'reverse-unreceipted-directory',
        'staged-copy-file', 'staged-copy-directory')
    $results = @(Get-Content -LiteralPath $recoveryLog |
        Where-Object { $_ -match '^\{"kind":' } | ForEach-Object { $_ | ConvertFrom-Json })
    if ($results.Count -ne $expectedKinds.Count -or
        (($results.kind | Sort-Object) -join ',') -cne (($expectedKinds | Sort-Object) -join ',') -or
        @($results | Where-Object { $_.Success -ne $true -or $_.forciblyTerminated -ne $true }).Count -ne 0) {
        throw 'Installed recovery probe did not pass every expected force-kill scenario.'
    }
}
$sentinel = Join-Path $installRoot 'user-sentinel.txt'
[IO.File]::WriteAllText($sentinel, 'preserve')
& $installer -Action Uninstall -InstallRoot $installRoot
if ((Test-Path -LiteralPath $exe) -or
    ($RecoveryProbe -and (Test-Path -LiteralPath $probeExe)) -or
    [IO.File]::ReadAllText($sentinel) -ne 'preserve') {
    throw 'Uninstall did not preserve its file-ownership boundary.'
}
[ordered]@{
    version = $manifest.version
    rid = $manifest.rid
    signed = $false
    installedHeadlessPassed = $true
    nativeWindowRequested = [bool]$NativeWindow
    nativeRecoveryInspectionRequested = [bool]$NativeWindow
    nativeManualWorkflowRequested = [bool]$ManualWorkflow
    installedRecoveryProbePassed = [bool]$RecoveryProbe
    uninstallPreservedUnknownFiles = $true
    evidenceDirectory = $testRoot
} | ConvertTo-Json | Tee-Object -FilePath (Join-Path $testRoot 'result.json')
