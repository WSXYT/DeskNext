# Only a bounded, owned VHDX on a disposable hosted runner. No physical disk selection.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsWindows -or $env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Mounted-volume probes require a disposable GitHub-hosted Windows runner.'
}
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'The hosted VHDX fixture needs an elevated runner; no elevation prompt will be shown.'
}
Add-Type @'
using System.Text;
using System.Runtime.InteropServices;
public static class DeskNextMountedVolumeFixture {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern uint QueryDosDevice(string name, StringBuilder target, uint length);
}
'@
$letter = 'Z'
function Assert-FreeLetter {
    $buffer = [Text.StringBuilder]::new(4096)
    if ([DeskNextMountedVolumeFixture]::QueryDosDevice("${letter}:", $buffer, 4096) -ne 0 -or
        [Runtime.InteropServices.Marshal]::GetLastWin32Error() -ne 2) {
        throw 'Z: is not confirmed unused; refusing to replace any mapping.'
    }
}
Assert-FreeLetter
$leaf = 'DeskNext.WinMountProbe-' + [Guid]::NewGuid().ToString('N')
$root = Join-Path ([IO.Path]::GetTempPath()) $leaf
$mount = Join-Path $root 'mounted'
$image = Join-Path $root 'fixture.vhdx'
$data = "${letter}:\$leaf"
$mountPath = $mount.TrimEnd('\') + '\'
$evidence = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "../../artifacts/windows-volume-results/$leaf"))
$project = Join-Path $PSScriptRoot '../../tests/DeskNest.Core.Tests/DeskNest.Core.Tests.csproj'
New-Item -ItemType Directory -Path $root | Out-Null
New-Item -ItemType Directory -Path $mount | Out-Null
[IO.Directory]::CreateDirectory($evidence) | Out-Null
[IO.File]::WriteAllText((Join-Path $root 'owner'), 'DeskNext Windows volume fixture')
$previous = @{}
foreach ($key in @('DESKNEXT_WINDOWS_VOLUME_ROOT', 'DESKNEXT_WINDOWS_VOLUME_DATA', 'DESKNEXT_WINDOWS_VOLUME_PHASE')) {
    $previous[$key] = [Environment]::GetEnvironmentVariable($key)
}
$env:DESKNEXT_WINDOWS_VOLUME_ROOT = $root
$env:DESKNEXT_WINDOWS_VOLUME_DATA = $data

function Get-OwnedDisk {
    $images = @(Get-DiskImage -ImagePath $image)
    if ($images.Count -ne 1 -or !$images[0].Attached -or $images[0].ImagePath -ne $image) {
        throw 'The exact fixture image is not attached.'
    }
    $disks = @($images[0] | Get-Disk)
    if ($disks.Count -ne 1 -or $disks[0].IsBoot -or $disks[0].IsSystem -or
        [string]$disks[0].BusType -ne 'File Backed Virtual' -or $disks[0].Size -gt 64MB -or $disks[0].Size -lt 60MB) {
        throw 'Image association did not resolve to one bounded file-backed non-system disk.'
    }
    return $disks[0]
}
function Get-OwnedPartition {
    $disk = Get-OwnedDisk
    $parts = @(Get-Partition -DiskNumber $disk.Number)
    if ($parts.Count -ne 1 -or $parts[0].IsBoot -or $parts[0].IsSystem) {
        throw 'Expected exactly one owned non-system partition.'
    }
    return $parts[0]
}
function Assert-DriveOwnership {
    $part = Get-OwnedPartition
    $drive = Get-Partition -DriveLetter $letter
    if ($drive.DiskNumber -ne $part.DiskNumber -or $drive.PartitionNumber -ne $part.PartitionNumber) {
        throw 'The requested drive letter does not refer to the owned VHDX partition.'
    }
    $volume = $part | Get-Volume
    if ($volume.FileSystemType -ne 'NTFS') { throw 'The fixture volume is not NTFS.' }
}
function Invoke-Phase([string]$phase, [string]$method) {
    $env:DESKNEXT_WINDOWS_VOLUME_PHASE = $phase
    $results = Join-Path $evidence $phase
    dotnet test $project -c Release --no-build --no-restore `
        --filter "FullyQualifiedName~WindowsMountedVolumeTests.$method" `
        --logger 'trx;LogFileName=volume.trx' --results-directory $results
    if ($LASTEXITCODE -ne 0) { throw "Windows volume phase failed: $phase" }
    [xml]$trx = Get-Content -LiteralPath (Join-Path $results 'volume.trx') -Raw
    $counts = $trx.SelectSingleNode('//*[local-name()="Counters"]')
    if (!$counts -or [int]$counts.total -ne 1 -or [int]$counts.executed -ne 1 -or
        [int]$counts.passed -ne 1 -or [int]$counts.failed -ne 0 -or [int]$counts.notExecuted -ne 0) {
        throw "Missing, skipped or failed native volume test: $phase"
    }
}
$passed = $false
try {
    # diskpart creates/attaches only this new image. Partition/format operations use its checked CIM object.
    $commands = Join-Path $root 'create.txt'
    @("create vdisk file=`"$image`" maximum=64 type=expandable", "select vdisk file=`"$image`"", 'attach vdisk') |
        Set-Content -LiteralPath $commands -Encoding ascii
    & "$env:SystemRoot\System32\diskpart.exe" /s $commands | Tee-Object -FilePath (Join-Path $evidence 'diskpart.log')
    if ($LASTEXITCODE -ne 0) { throw 'Owned VHDX creation/attachment failed.' }
    $disk = Get-OwnedDisk
    if ($disk.PartitionStyle -ne 'RAW') { throw 'New image unexpectedly has a partition table.' }
    Assert-FreeLetter
    $part = $disk | Initialize-Disk -PartitionStyle MBR -PassThru |
        New-Partition -UseMaximumSize -DriveLetter $letter
    $part | Format-Volume -FileSystem NTFS -NewFileSystemLabel DeskNextProbe -Force -Confirm:$false | Out-Null
    Assert-DriveOwnership
    Get-OwnedPartition | Add-PartitionAccessPath -AccessPath $mountPath
    New-Item -ItemType Directory -Path $data | Out-Null
    [IO.File]::WriteAllText((Join-Path $data 'owner'), 'DeskNext Windows volume fixture')
    Invoke-Phase 'attached' 'AttachedMountRefusesBatchesAndRecordsRecoverableMoves'

    # This is a clean detach/availability test, NOT sudden unplug or power loss.
    Dismount-DiskImage -ImagePath $image | Out-Null
    if ((Get-DiskImage -ImagePath $image).Attached) { throw 'The fixture did not detach.' }
    Invoke-Phase 'detached' 'UnavailableVolumePreservesJournalsAndRefusesNewBatches'

    Mount-DiskImage -ImagePath $image -NoDriveLetter | Out-Null
    $part = Get-OwnedPartition
    if ($part.DriveLetter -ne $letter) {
        Assert-FreeLetter
        $part | Add-PartitionAccessPath -AccessPath "${letter}:\"
    }
    Assert-DriveOwnership
    Invoke-Phase 'reattached' 'SameVolumeReturnsAndOriginalReceiptsAuthorizeRecovery'
    $passed = $true
} catch {
    $_ | Out-String | Set-Content -LiteralPath (Join-Path $evidence 'failure.log')
    throw
} finally {
    $clean = $true
    foreach ($key in $previous.Keys) { [Environment]::SetEnvironmentVariable($key, $previous[$key]) }
    try {
        if (Test-Path -LiteralPath $image) {
            if ((Get-DiskImage -ImagePath $image).Attached) {
                try {
                    $part = Get-OwnedPartition
                    foreach ($access in @("${letter}:\", $mountPath)) {
                        if ($part.AccessPaths -contains $access) {
                            $part | Remove-PartitionAccessPath -AccessPath $access
                        }
                    }
                } finally { Dismount-DiskImage -ImagePath $image | Out-Null }
            }
            if ((Get-DiskImage -ImagePath $image).Attached) { throw 'Image remains attached.' }
        }
        # No /s: remove only the owned empty directory/reparse entry, never traverse its target.
        & "$env:SystemRoot\System32\cmd.exe" /d /c "rmdir `"$mount`""
        if ($LASTEXITCODE -ne 0) { throw 'Owned mount entry could not be safely removed.' }
    } catch {
        $clean = $false
        Write-Warning "Cleanup failed; no recursive deletion: $root; $_"
    }
    if ($passed -and $clean) { Remove-Item -LiteralPath $root -Recurse -Force }
    else { Write-Warning "Fixture retained: $root" }
    if (!$clean) { throw 'VHDX cleanup was not verified.' }
}
$terminal = @{ windowsMountedVolumeVerified = $true; offlineJournalPreserved = $true;
    reattachedRecoveryVerified = $true; powerLossVerified = $false } | ConvertTo-Json -Compress
$terminal | Set-Content -LiteralPath (Join-Path $evidence 'result.json')
Write-Output "WINDOWS_VOLUME_RESULT_JSON:$terminal"
