# A temporary loopback SMB share on a disposable hosted runner. No service/firewall/credential changes.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Network-volume probes require a disposable GitHub-hosted Windows runner.'
}
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The temporary SMB fixture needs an elevated runner; no elevation prompt will be shown.'
}
foreach ($service in @('LanmanServer', 'LanmanWorkstation')) {
    if ((Get-Service -Name $service).Status -ne 'Running') {
        throw "Required SMB service is not already running: $service; no service settings will be changed."
    }
}
Add-Type @'
using System.Text;
using System.Runtime.InteropServices;
public static class DeskNextNetworkVolumeFixture {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern uint QueryDosDevice(string name, StringBuilder target, uint length);
}
'@
function Assert-FreeLetter {
    $buffer = [Text.StringBuilder]::new(4096)
    if ([DeskNextNetworkVolumeFixture]::QueryDosDevice('Z:', $buffer, 4096) -ne 0 -or
        [Runtime.InteropServices.Marshal]::GetLastWin32Error() -ne 2) {
        throw 'Z: is not confirmed unused; refusing to replace any mapping.'
    }
}
Assert-FreeLetter
$id = [Guid]::NewGuid().ToString('N')
$leaf = 'DeskNext.WinMountProbe-' + $id
$root = Join-Path ([IO.Path]::GetTempPath()) $leaf
$shareName = 'DeskNextProbe-' + $id
$shareRoot = Join-Path $root 'shared'
$remote = "\\127.0.0.1\$shareName"
$evidence = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../artifacts/windows-volume-results/$leaf"))
$project = Join-Path $PSScriptRoot '../../tests/DeskNest.Core.Tests/DeskNest.Core.Tests.csproj'
New-Item -ItemType Directory -Path $root | Out-Null
New-Item -ItemType Directory -Path (Join-Path $shareRoot $leaf) -Force | Out-Null
[IO.Directory]::CreateDirectory($evidence) | Out-Null
foreach ($owner in @((Join-Path $root 'owner'), (Join-Path $shareRoot "$leaf/owner"))) {
    [IO.File]::WriteAllText($owner, 'DeskNext Windows volume fixture')
}
$previous = @{}
foreach ($key in @('DESKNEXT_WINDOWS_VOLUME_ROOT', 'DESKNEXT_WINDOWS_VOLUME_DATA',
    'DESKNEXT_WINDOWS_VOLUME_PHASE', 'DESKNEXT_WINDOWS_VOLUME_UNC')) {
    $previous[$key] = [Environment]::GetEnvironmentVariable($key)
}
$passed = $false
try {
    # Only the synthetic data subdirectory is exported, with access restricted to the current identity.
    New-SmbShare -Name $shareName -Path $shareRoot -Temporary -CachingMode None `
        -FullAccess $identity.Name -EncryptData $true | Out-Null
    $share = @(Get-SmbShare -Name $shareName)
    if ($share.Count -ne 1 -or $share[0].Path -ne $shareRoot -or !$share[0].Temporary) {
        throw 'The temporary share does not resolve to the owned data directory.'
    }
    Assert-FreeLetter
    New-SmbMapping -LocalPath 'Z:' -RemotePath $remote -Persistent $false | Out-Null
    $mapping = @(Get-SmbMapping -LocalPath 'Z:')
    if ($mapping.Count -ne 1 -or $mapping[0].RemotePath -ne $remote) {
        throw 'The drive letter does not resolve to the owned loopback share.'
    }
    $env:DESKNEXT_WINDOWS_VOLUME_ROOT = $root
    $env:DESKNEXT_WINDOWS_VOLUME_DATA = "Z:\$leaf"
    $env:DESKNEXT_WINDOWS_VOLUME_UNC = "$remote\$leaf"
    $env:DESKNEXT_WINDOWS_VOLUME_PHASE = 'network'
    dotnet test $project -c Release --no-build --no-restore `
        --filter 'FullyQualifiedName~WindowsMountedVolumeTests.NetworkAliasesRefuseBatchesBeforeMutation' `
        --logger 'trx;LogFileName=network.trx' --results-directory $evidence
    if ($LASTEXITCODE -ne 0) { throw 'Network-volume refusal test failed.' }
    [xml]$trx = Get-Content -LiteralPath (Join-Path $evidence 'network.trx') -Raw
    $counts = $trx.SelectSingleNode('//*[local-name()="Counters"]')
    if (!$counts -or [int]$counts.total -ne 1 -or [int]$counts.executed -ne 1 -or
        [int]$counts.passed -ne 1 -or [int]$counts.failed -ne 0 -or [int]$counts.notExecuted -ne 0) {
        throw 'Missing, skipped or failed native network-volume test.'
    }
    $passed = $true
} catch {
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $evidence 'failure.log')
    throw
} finally {
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key]) }
    $clean = $true
    try {
        $mapping = @(Get-SmbMapping -LocalPath 'Z:' -ErrorAction SilentlyContinue)
        if ($mapping.Count -ne 0) {
            if ($mapping.Count -ne 1 -or $mapping[0].RemotePath -ne $remote) {
                throw 'Refusing to remove a different drive mapping.'
            }
            $mapping[0] | Remove-SmbMapping -Force
        }
        Assert-FreeLetter
        $share = @(Get-SmbShare -Name $shareName -ErrorAction SilentlyContinue)
        if ($share.Count -ne 0) {
            if ($share.Count -ne 1 -or $share[0].Path -ne $shareRoot) {
                throw 'Refusing to remove a different share.'
            }
            Remove-SmbShare -Name $shareName -Force
        }
        if (@(Get-SmbShare -Name $shareName -ErrorAction SilentlyContinue).Count -ne 0) {
            throw 'Owned share remains published.'
        }
    } catch {
        $clean = $false
        Write-Warning "Cleanup failed; no recursive deletion: $root; $_"
    }
    if ($passed -and $clean) { Remove-Item -LiteralPath $root -Recurse -Force }
    else { Write-Warning "Fixture retained: $root" }
    if (!$clean) { throw 'SMB fixture cleanup was not verified.' }
}
$terminal = @{ windowsNetworkVolumeRefusalVerified = $true; uncAndMappedDriveVerified = $true;
    batchesRefusedBeforeMutation = 8; shareAndMappingRemoved = $true; remoteServerFailureVerified = $false } | ConvertTo-Json -Compress
$terminal | Set-Content -LiteralPath (Join-Path $evidence 'result.json')
Write-Output "WINDOWS_NETWORK_VOLUME_RESULT_JSON:$terminal"
