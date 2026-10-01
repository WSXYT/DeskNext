param(
    [ValidateSet('Install','Uninstall')][string]$Action = 'Install',
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\DeskNestPreview'),
    [string]$Payload = (Join-Path $PSScriptRoot 'payload'),
    # Trusted test harness only; no hook is loaded from package or ownership data.
    [scriptblock]$TestCheckpoint
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-PlainPath([string]$Path) {
    $cursor = [IO.Path]::GetFullPath($Path)
    while ($cursor) {
        if (Test-Path -LiteralPath $cursor) {
            if ((Get-Item -Force -LiteralPath $cursor).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Installation paths may not traverse links or junctions.'
            }
        }
        $cursor = [IO.Path]::GetDirectoryName($cursor)
    }
}
function Resolve-Child([string]$Root, [string]$Relative) {
    if ([string]::IsNullOrWhiteSpace($Relative) -or [IO.Path]::IsPathRooted($Relative) -or
        $Relative.Contains(':') -or $Relative.Contains('\') -or
        @($Relative.Split('/') | Where-Object { $_ -in @('', '.', '..') -or $_.EndsWith('.') -or $_.EndsWith(' ') }).Count) {
        throw 'Invalid package relative path.'
    }
    $full = [IO.Path]::GetFullPath((Join-Path $Root $Relative))
    if (!$full.StartsWith($Root.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Package path escapes its root.'
    }
    Assert-PlainPath $full
    return $full
}
function Assert-Package($Manifest) {
    if ($Manifest.schemaVersion -ne 1 -or $Manifest.rid -ne 'win-x64' -or
        $Manifest.version -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]{0,63}$' -or
        @($Manifest.files).Count -lt 1 -or @($Manifest.files).Count -gt 10000) { throw 'Unsupported package manifest.' }
    $seen = @{}
    foreach ($entry in $Manifest.files) {
        if ($entry.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $entry.length -lt 0 -or
            $entry.path -eq 'package.json' -or $seen.ContainsKey($entry.path)) {
            throw 'Malformed or duplicate package entry.'
        }
        $seen[$entry.path] = $true
    }
    if (!$seen.ContainsKey('DeskNest.App.exe')) { throw 'Package has no application entry point.' }
}
function Read-Package([string]$Path) {
    Assert-PlainPath $Path
    $manifest = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    Assert-Package $manifest
    return $manifest
}
function Assert-Content([string]$Path, $Entry) {
    Assert-PlainPath $Path
    if (!(Test-Path -LiteralPath $Path -PathType Leaf) -or
        (Get-Item -LiteralPath $Path).Length -ne $Entry.length -or
        (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ne $Entry.sha256) {
        throw ('Package content verification failed: ' + $Entry.path)
    }
}
function Get-Receipt([string]$Path, [string]$Relative) {
    Assert-PlainPath $Path
    return [pscustomobject]@{ path = $Relative; length = (Get-Item -LiteralPath $Path).Length;
        sha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
}
function Save-Owner([string]$Path, $Owner) {
    Assert-PlainPath $Path
    Assert-PlainPath ($Path + '.bak')
    $temp = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $bytes = [Text.Encoding]::UTF8.GetBytes(($Owner | ConvertTo-Json -Depth 15))
    $stream = [IO.FileStream]::new($temp, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write,
        [IO.FileShare]::None, 4096, [IO.FileOptions]::WriteThrough)
    try { $stream.Write($bytes, 0, $bytes.Length); $stream.Flush($true) } finally { $stream.Dispose() }
    if (Test-Path -LiteralPath $Path) { [IO.File]::Replace($temp, $Path, ($Path + '.bak')) }
    else { [IO.File]::Move($temp, $Path) }
}
function Read-Owner([string]$Path) {
    Assert-PlainPath $Path
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'Refusing to modify an unowned installation directory.' }
    $record = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($record.appId -ne 'app.desknest.preview' -or $record.schemaVersion -notin @(1,2) -or
        @($record.versions).Count -gt 100) { throw 'Invalid installation ownership record.' }
    $seen = @{}
    foreach ($version in $record.versions) {
        if ($version -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]{0,63}$' -or $seen.ContainsKey($version)) {
            throw 'Invalid owned version.'
        }
        $seen[$version] = $true
        Resolve-Child $InstallRoot ('versions/' + $version) | Out-Null
    }
    if ($record.activeVersion -ne '' -and !$seen.ContainsKey($record.activeVersion)) { throw 'Invalid active version.' }
    if ($record.schemaVersion -eq 1) {
        $record | Add-Member -NotePropertyName pendingInstall -NotePropertyValue $null
        $record | Add-Member -NotePropertyName removal -NotePropertyValue $null
        $record.schemaVersion = 2
    }
    if ($null -ne $record.pendingInstall) {
        $pending = $record.pendingInstall
        Assert-Package $pending.package
        if ($pending.stage -notmatch '^staging-[a-f0-9]{32}$' -or
            $pending.manifest.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $pending.manifest.length -lt 0 -or
            $pending.manifest.path -ne 'package.json' -or $seen.ContainsKey($pending.package.version) -or
            $null -ne $record.removal) { throw 'Invalid pending installation.' }
        Resolve-Child $InstallRoot $pending.stage | Out-Null
    }
    if ($null -ne $record.removal) {
        if (@($record.removal.entries).Count -gt 100000) { throw 'Uninstall plan exceeds its budget.' }
        $paths = @{}
        foreach ($entry in $record.removal.entries) {
            $parts = $entry.path.Split('/')
            if ($parts.Count -lt 3 -or $parts[0] -ne 'versions' -or !$seen.ContainsKey($parts[1]) -or
                $entry.sha256 -notmatch '^[a-fA-F0-9]{64}$' -or $entry.length -lt 0 -or $paths.ContainsKey($entry.path)) {
                throw 'Invalid uninstall plan entry.'
            }
            $paths[$entry.path] = $true
            Resolve-Child $InstallRoot $entry.path | Out-Null
        }
    }
    return $record
}
function Checkpoint([string]$Point) {
    if ($null -ne $TestCheckpoint) { & $TestCheckpoint $Point }
}
function Copy-VerifiedFile([string]$Source, [string]$Target, $Entry) {
    Assert-PlainPath $Source
    Assert-PlainPath $Target
    if (!(Test-Path -LiteralPath $Target)) {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Target)) | Out-Null
        # A torn copy never occupies a final staging name. Interrupted temporary
        # files stay at the installation root for diagnosis; retry never overwrites them.
        $temp = Resolve-Child $InstallRoot ('.copy-' + [Guid]::NewGuid().ToString('N') + '.tmp')
        [IO.File]::Copy($Source, $temp, $false)
        Checkpoint 'copied-temporary-file'
        Assert-Content $temp $Entry
        [IO.File]::Move($temp, $Target)
    }
    Assert-Content $Target $Entry
}

$InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\','/')
Assert-PlainPath $InstallRoot
if ($InstallRoot -eq [IO.Path]::GetPathRoot($InstallRoot).TrimEnd('\','/')) { throw 'A volume root is not an installation directory.' }
$ownerPath = Join-Path $InstallRoot '.desknest-install.json'

# Validate the entire source before creating any installation directory.
if ($Action -eq 'Install') {
    $Payload = [IO.Path]::GetFullPath($Payload)
    Assert-PlainPath $Payload
    $manifestPath = Join-Path $Payload 'package.json'
    $manifest = Read-Package $manifestPath
    $manifestReceipt = Get-Receipt $manifestPath 'package.json'
    Resolve-Child $InstallRoot ('versions/' + $manifest.version) | Out-Null
    foreach ($entry in $manifest.files) { Assert-Content (Resolve-Child $Payload $entry.path) $entry }
}
if (!(Test-Path -LiteralPath $InstallRoot)) {
    if ($Action -eq 'Uninstall') { throw 'Installation does not exist.' }
    # Publish an already-owned root atomically: a failed bootstrap never leaves an
    # ownerless InstallRoot. Abandoned bootstrap folders are retained, not guessed away.
    $parent = [IO.Path]::GetDirectoryName($InstallRoot)
    Assert-PlainPath $parent
    [IO.Directory]::CreateDirectory($parent) | Out-Null
    $bootstrap = Join-Path $parent ('.desknest-bootstrap-' + [Guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($bootstrap) | Out-Null
    $initial = [pscustomobject]@{ schemaVersion = 2; appId = 'app.desknest.preview'; activeVersion = '';
        versions = @(); pendingInstall = $null; removal = $null }
    Save-Owner (Join-Path $bootstrap '.desknest-install.json') $initial
    Checkpoint 'owned-bootstrap'
    [IO.Directory]::Move($bootstrap, $InstallRoot)
}
Read-Owner $ownerPath | Out-Null
$lockPath = Join-Path $InstallRoot '.install.lock'
Assert-PlainPath $lockPath
$lock = [IO.File]::Open($lockPath, 'OpenOrCreate', 'ReadWrite', 'None')
try {
    # Primary ownership is authoritative; never resume deletion from a stale backup.
    $owner = Read-Owner $ownerPath
    if ($Action -eq 'Install') {
        if ($null -ne $owner.removal) { throw 'Finish the pending uninstall before installing.' }
        $versionDirectory = Resolve-Child $InstallRoot ('versions/' + $manifest.version)
        if ($null -eq $owner.pendingInstall) {
            if (@($owner.versions).Count -ge 100) { throw 'Installed version budget reached.' }
            if ($owner.versions -contains $manifest.version -or (Test-Path -LiteralPath $versionDirectory)) {
                throw 'This version path already exists; preserved without overwriting. Choose a new version or reconcile retained files.'
            }
            $owner.pendingInstall = [pscustomobject]@{ package = $manifest; manifest = $manifestReceipt;
                stage = ('staging-' + [Guid]::NewGuid().ToString('N')) }
            Save-Owner $ownerPath $owner
        } else {
            if ($owner.pendingInstall.package.version -ne $manifest.version -or
                $owner.pendingInstall.manifest.sha256 -ne $manifestReceipt.sha256 -or
                $owner.pendingInstall.manifest.length -ne $manifestReceipt.length) {
                throw 'Resume requires the exact pending package; existing evidence preserved.'
            }
        }
        $pending = $owner.pendingInstall
        $stage = Resolve-Child $InstallRoot $pending.stage
        if (Test-Path -LiteralPath $versionDirectory) {
            # Publication succeeded but the ownership commit was interrupted.
            if (Test-Path -LiteralPath $stage) { throw 'Both staging and published version exist; reconcile manually.' }
        } else {
            [IO.Directory]::CreateDirectory($stage) | Out-Null
            foreach ($entry in $manifest.files) {
                $target = Resolve-Child $stage $entry.path
                Copy-VerifiedFile (Resolve-Child $Payload $entry.path) $target $entry
                # Matching completed copies resume; modified copies are never overwritten.
                Checkpoint 'staged-file'
            }
            Assert-Content $manifestPath $pending.manifest
            $stagedManifest = Resolve-Child $stage 'package.json'
            Copy-VerifiedFile $manifestPath $stagedManifest $pending.manifest
            [IO.Directory]::CreateDirectory((Resolve-Child $InstallRoot 'versions')) | Out-Null
            [IO.Directory]::Move($stage, $versionDirectory)
            Checkpoint 'published-version'
        }
        Assert-Content (Resolve-Child $versionDirectory 'package.json') $pending.manifest
        foreach ($entry in $manifest.files) { Assert-Content (Resolve-Child $versionDirectory $entry.path) $entry }
        $owner.versions = @($owner.versions) + $manifest.version
        $owner.activeVersion = $manifest.version
        $owner.pendingInstall = $null
        Save-Owner $ownerPath $owner
        Write-Output ('Installed unsigned development version: ' + $manifest.version)
        Write-Output ('Launch: ' + (Join-Path $versionDirectory 'DeskNest.App.exe'))
    } else {
        if ($null -ne $owner.pendingInstall) { throw 'Resolve the pending installation before uninstalling.' }
        if ($null -eq $owner.removal) {
            $entries = [Collections.Generic.List[object]]::new()
            foreach ($version in $owner.versions) {
                $directory = Resolve-Child $InstallRoot ('versions/' + $version)
                $packagePath = Resolve-Child $directory 'package.json'
                $package = Read-Package $packagePath
                if ($package.version -ne $version) { throw 'Version ownership mismatch.' }
                foreach ($entry in $package.files) {
                    Assert-Content (Resolve-Child $directory $entry.path) $entry
                    $entries.Add([pscustomobject]@{ path = ('versions/' + $version + '/' + $entry.path);
                        length = $entry.length; sha256 = $entry.sha256 })
                }
                $entries.Add((Get-Receipt $packagePath ('versions/' + $version + '/package.json')))
                if ($entries.Count -gt 100000) { throw 'Uninstall plan exceeds its budget.' }
            }
            # This durable plan, including manifests, authorizes missing files ONLY
            # during explicit removal resume, never in an ordinary damaged install.
            $owner.removal = [pscustomobject]@{ entries = @($entries.ToArray()) }
            Save-Owner $ownerPath $owner
        }
        foreach ($entry in $owner.removal.entries) {
            $target = Resolve-Child $InstallRoot $entry.path
            if (Test-Path -LiteralPath $target) { Assert-Content $target $entry }
        }
        foreach ($entry in $owner.removal.entries) {
            $target = Resolve-Child $InstallRoot $entry.path
            if (Test-Path -LiteralPath $target) {
                Assert-Content $target $entry
                [IO.File]::Delete($target)
                if ($entry.path.EndsWith('/package.json')) { Checkpoint 'removed-manifest' }
                else { Checkpoint 'removed-file' }
            }
        }
        $directories = @($owner.removal.entries | ForEach-Object {
            $parent = [IO.Path]::GetDirectoryName((Resolve-Child $InstallRoot $_.path))
            while ($parent -ne $InstallRoot) {
                $parent
                $parent = [IO.Path]::GetDirectoryName($parent)
            }
        } | Sort-Object -Unique | Sort-Object Length -Descending)
        foreach ($directory in $directories) {
            Assert-PlainPath $directory
            if ((Test-Path -LiteralPath $directory) -and @(Get-ChildItem -Force -LiteralPath $directory).Count -eq 0) {
                [IO.Directory]::Delete($directory)
            }
        }
        # Keep terminal ownership so reinstall/repeated uninstall can safely reuse
        # this root without accepting arbitrary ownerless directories.
        $owner.versions = @()
        $owner.activeVersion = ''
        $owner.removal = $null
        Save-Owner $ownerPath $owner
        Write-Output 'Application payloads removed. Workspace, models and unknown files were preserved; ownership retained.'
    }
} finally { $lock.Dispose() }
