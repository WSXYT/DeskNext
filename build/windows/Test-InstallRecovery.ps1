param(
    [string]$Installer = (Join-Path $PSScriptRoot 'Install.ps1'),
    [string]$CasePattern = '.*'
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
$root = Join-Path ([IO.Path]::GetTempPath()) ('DeskNest-install-recovery-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$failures = [Collections.Generic.List[string]]::new()
$links = [Collections.Generic.List[string]]::new()
$passed = 0
$skipped = 0
function Assert([bool]$Value, [string]$Message) { if (!$Value) { throw $Message } }
function Expect-Failure([scriptblock]$Code) {
    $failed = $false
    try { & $Code | Out-Null } catch { $failed = $true }
    Assert $failed 'Expected fail-closed refusal.'
}
function Case([string]$Name, [scriptblock]$Code) {
    if ($Name -notmatch $CasePattern) { return }
    try { & $Code; $script:passed++; Write-Output ('PASS: ' + $Name) }
    catch { $failures.Add($Name + ': ' + $_.Exception.Message); Write-Output ('FAIL: ' + $Name + ': ' + $_.Exception.Message) }
}
function Payload([string]$Version) {
    $path = Join-Path $root ('payload-' + $Version)
    [IO.Directory]::CreateDirectory((Join-Path $path 'nested')) | Out-Null
    $files = @('early.txt', 'DeskNest.App.exe', 'nested/helper.txt') | ForEach-Object {
        $file = Join-Path $path $_
        [IO.File]::WriteAllText($file, ('fixture ' + $Version + ' ' + $_))
        [pscustomobject]@{ path = $_; length = (Get-Item $file).Length; sha256 = (Get-FileHash $file).Hash }
    }
    $manifest = [pscustomobject]@{ schemaVersion = 1; rid = 'win-x64'; version = $Version; files = @($files) }
    [IO.File]::WriteAllText((Join-Path $path 'package.json'), ($manifest | ConvertTo-Json -Depth 8))
    return $path
}
function Owner([string]$Path) { return (Get-Content -Raw -LiteralPath (Join-Path $Path '.desknest-install.json') | ConvertFrom-Json) }
function Link([string]$Path, [string]$Target) {
    $start = [Diagnostics.ProcessStartInfo]::new('cmd.exe', ('/d /c mklink "' + $Path + '" "' + $Target + '"'))
    $start.UseShellExecute = $false
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (!$process.WaitForExit(10000)) { $process.Kill(); throw 'Symlink fixture timed out.' }
        if ($process.ExitCode -ne 0) { return $false }
    } finally { $process.Dispose() }
    $links.Add($Path)
    return $true
}
try {
    Case 'invalid first payload leaves no root; corrected retry works' {
        $payload = Payload 'invalid-retry'
        $install = Join-Path $root 'invalid-app'
        $file = Join-Path $payload 'early.txt'
        $original = [IO.File]::ReadAllBytes($file)
        [IO.File]::WriteAllText($file, 'corrupt')
        Expect-Failure { & $Installer -InstallRoot $install -Payload $payload }
        Assert (!(Test-Path $install)) 'Invalid input created an ownerless root.'
        [IO.File]::WriteAllBytes($file, $original)
        & $Installer -InstallRoot $install -Payload $payload | Out-Null
        Assert ((Owner $install).activeVersion -eq 'invalid-retry') 'Corrected retry did not install.'
    }
    Case 'bootstrap interruption leaves InstallRoot absent and retry safe' {
        $payload = Payload 'bootstrap'
        $install = Join-Path $root 'bootstrap-app'
        Expect-Failure { & $Installer -InstallRoot $install -Payload $payload -TestCheckpoint {
            param($point) if ($point -eq 'owned-bootstrap') { throw 'injected bootstrap interruption' }
        } }
        Assert (!(Test-Path $install)) 'Interrupted bootstrap published an ownerless root.'
        & $Installer -InstallRoot $install -Payload $payload | Out-Null
        Assert ((Owner $install).activeVersion -eq 'bootstrap') 'Bootstrap retry failed.'
    }
    Case 'torn temporary copy is retained while a fresh copy resumes' {
        $payload = Payload 'copy-retry'
        $install = Join-Path $root 'copy-app'
        Expect-Failure { & $Installer -InstallRoot $install -Payload $payload -TestCheckpoint {
            param($point) if ($point -eq 'copied-temporary-file') { throw 'injected copy interruption' }
        } }
        $temporary = @(Get-ChildItem -Force -LiteralPath $install -Filter '.copy-*.tmp')
        Assert ($temporary.Count -eq 1) 'Missing interrupted-copy evidence.'
        [IO.File]::WriteAllText($temporary[0].FullName, 'partial data')
        & $Installer -InstallRoot $install -Payload $payload | Out-Null
        Assert ((Owner $install).activeVersion -eq 'copy-retry') 'Copy retry failed.'
        Assert ([IO.File]::ReadAllText($temporary[0].FullName) -eq 'partial data') 'Interrupted-copy evidence was overwritten.'
    }
    Case 'staging interruption resumes matching files and preserves unknown files' {
        $payload = Payload 'staging-retry'
        $install = Join-Path $root 'staging-app'
        Expect-Failure { & $Installer -InstallRoot $install -Payload $payload -TestCheckpoint {
            param($point) if ($point -eq 'staged-file') { throw 'injected staging interruption' }
        } }
        $pending = (Owner $install).pendingInstall
        Assert ($null -ne $pending) 'No durable pending ownership.'
        $unknown = Join-Path (Join-Path $install $pending.stage) 'user-note.txt'
        [IO.File]::WriteAllText($unknown, 'preserve')
        & $Installer -InstallRoot $install -Payload $payload | Out-Null
        Assert ([IO.File]::ReadAllText((Join-Path $install 'versions/staging-retry/user-note.txt')) -eq 'preserve') 'Lost unknown staging file.'
        Assert ($null -eq (Owner $install).pendingInstall) 'Pending operation not committed.'
    }
    Case 'changed staged payload refuses resume without overwriting' {
        $payload = Payload 'changed-stage'
        $install = Join-Path $root 'changed-stage-app'
        Expect-Failure { & $Installer -InstallRoot $install -Payload $payload -TestCheckpoint {
            param($point) if ($point -eq 'staged-file') { throw 'injected staging interruption' }
        } }
        $file = Join-Path (Join-Path $install (Owner $install).pendingInstall.stage) 'early.txt'
        [IO.File]::WriteAllText($file, 'user edit')
        Expect-Failure { & $Installer -InstallRoot $install -Payload $payload }
        Assert ([IO.File]::ReadAllText($file) -eq 'user edit') 'Changed staged payload was overwritten.'
        Assert (!(Test-Path (Join-Path $install 'versions/changed-stage'))) 'Changed staging content was published.'
    }
    Case 'pending install rejects a different package without changing ownership' {
        $payload = Payload 'pending-exact'
        $other = Payload 'pending-other'
        $install = Join-Path $root 'pending-exact-app'
        Expect-Failure { & $Installer -InstallRoot $install -Payload $payload -TestCheckpoint {
            param($point) if ($point -eq 'staged-file') { throw 'injected staging interruption' }
        } }
        $ownerPath = Join-Path $install '.desknest-install.json'
        $before = (Get-FileHash $ownerPath).Hash
        Expect-Failure { & $Installer -InstallRoot $install -Payload $other }
        Assert ((Get-FileHash $ownerPath).Hash -eq $before) 'Refused package changed pending ownership.'
        Assert (!(Test-Path (Join-Path $install 'versions/pending-other'))) 'Different pending package was published.'
        & $Installer -InstallRoot $install -Payload $payload | Out-Null
    }
    Case 'schema-1 ownership upgrades without discarding installed versions' {
        $payload = Payload 'legacy-one'
        $next = Payload 'legacy-two'
        $install = Join-Path $root 'legacy-app'
        & $Installer -InstallRoot $install -Payload $payload | Out-Null
        $ownerPath = Join-Path $install '.desknest-install.json'
        $legacy = [pscustomobject]@{ schemaVersion = 1; appId = 'app.desknest.preview';
            activeVersion = 'legacy-one'; versions = @('legacy-one') }
        [IO.File]::WriteAllText($ownerPath, ($legacy | ConvertTo-Json -Depth 8))
        & $Installer -InstallRoot $install -Payload $next | Out-Null
        Assert ((Owner $install).schemaVersion -eq 2) 'Legacy ownership was not upgraded.'
        Assert (@((Owner $install).versions).Count -eq 2) 'Legacy installed version lost.'
        & $Installer -Action Uninstall -InstallRoot $install | Out-Null
    }
    foreach ($upgrade in @($false, $true)) {
        Case ('publication interruption resumes exact package; upgrade=' + $upgrade) {
            $version = 'published-' + $upgrade
            $payload = Payload $version
            $install = Join-Path $root ('published-app-' + $upgrade)
            if ($upgrade) {
                $previous = Payload 'previous'
                & $Installer -InstallRoot $install -Payload $previous | Out-Null
            }
            Expect-Failure { & $Installer -InstallRoot $install -Payload $payload -TestCheckpoint {
                param($point) if ($point -eq 'published-version') { throw 'injected publish interruption' }
            } }
            Assert (Test-Path (Join-Path $install ('versions/' + $version + '/DeskNest.App.exe'))) 'Version was not published at checkpoint.'
            Assert ($null -ne (Owner $install).pendingInstall) 'Publication has no pending owner.'
            & $Installer -InstallRoot $install -Payload $payload | Out-Null
            Assert ((Owner $install).activeVersion -eq $version) 'Published version was not reconciled.'
            if ($upgrade) { Assert (Test-Path (Join-Path $install 'versions/previous/DeskNest.App.exe')) 'Previous version lost.' }
        }
    }
    Case 'delete-denying real handle leaves resumable uninstall plan' {
        $payload = Payload 'locked'
        $install = Join-Path $root 'locked-app'
        & $Installer -InstallRoot $install -Payload $payload | Out-Null
        $directory = Join-Path $install 'versions/locked'
        $exe = Join-Path $directory 'DeskNest.App.exe'
        $unknown = Join-Path $directory 'personal.txt'
        [IO.File]::WriteAllText($unknown, 'preserve')
        $handle = [IO.File]::Open($exe, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
        try { Expect-Failure { & $Installer -Action Uninstall -InstallRoot $install } }
        finally { $handle.Dispose() }
        Assert (!(Test-Path (Join-Path $directory 'early.txt'))) 'Fixture did not reach partial uninstall.'
        & $Installer -Action Uninstall -InstallRoot $install | Out-Null
        Assert (!(Test-Path $exe)) 'Uninstall did not resume.'
        Assert ([IO.File]::ReadAllText($unknown) -eq 'preserve') 'Unknown version file was deleted.'
        & $Installer -Action Uninstall -InstallRoot $install | Out-Null
    }
    Case 'partial removal refuses modified remaining files before further deletion' {
        $payload = Payload 'removed-file'
        $install = Join-Path $root 'removed-file-app'
        & $Installer -InstallRoot $install -Payload $payload | Out-Null
        Expect-Failure { & $Installer -Action Uninstall -InstallRoot $install -TestCheckpoint {
            param($point) if ($point -eq 'removed-file') { throw 'injected delete interruption' }
        } }
        $directory = Join-Path $install 'versions/removed-file'
        Assert (!(Test-Path (Join-Path $directory 'early.txt'))) 'Fixture did not delete first file.'
        $exe = Join-Path $directory 'DeskNest.App.exe'
        $original = [IO.File]::ReadAllBytes($exe)
        [IO.File]::WriteAllText($exe, 'external edit')
        Expect-Failure { & $Installer -Action Uninstall -InstallRoot $install }
        Assert (Test-Path (Join-Path $directory 'nested/helper.txt')) 'Deleted more files before detecting modification.'
        Assert ([IO.File]::ReadAllText($exe) -eq 'external edit') 'Modified file was touched.'
        [IO.File]::WriteAllBytes($exe, $original)
        & $Installer -Action Uninstall -InstallRoot $install | Out-Null
        Assert (!(Test-Path $exe)) 'Removal could not resume after verification.'
    }
    Case 'resume after one version manifest was removed' {
        $payload1 = Payload 'manifest-one'
        $payload2 = Payload 'manifest-two'
        $install = Join-Path $root 'removed-manifest-app'
        & $Installer -InstallRoot $install -Payload $payload1 | Out-Null
        & $Installer -InstallRoot $install -Payload $payload2 | Out-Null
        Expect-Failure { & $Installer -Action Uninstall -InstallRoot $install -TestCheckpoint {
            param($point) if ($point -eq 'removed-manifest') { throw 'injected manifest interruption' }
        } }
        Assert (!(Test-Path (Join-Path $install 'versions/manifest-one/package.json'))) 'Fixture did not remove first manifest.'
        Assert (Test-Path (Join-Path $install 'versions/manifest-two/DeskNest.App.exe')) 'Second version unexpectedly removed.'
        & $Installer -Action Uninstall -InstallRoot $install | Out-Null
        Assert (@((Owner $install).versions).Count -eq 0) 'Owned versions were not reconciled.'
    }
    foreach ($unknownFile in @($false, $true)) {
        Case ('uninstall/reinstall/repeated uninstall; unknown root file=' + $unknownFile) {
            $version = 'reinstall-' + $unknownFile
            $payload = Payload $version
            $install = Join-Path $root ('reinstall-app-' + $unknownFile)
            & $Installer -InstallRoot $install -Payload $payload | Out-Null
            $unknown = Join-Path $install 'user-note.txt'
            if ($unknownFile) { [IO.File]::WriteAllText($unknown, 'preserve') }
            & $Installer -Action Uninstall -InstallRoot $install | Out-Null
            Assert (@((Owner $install).versions).Count -eq 0) 'Terminal ownership not retained.'
            & $Installer -InstallRoot $install -Payload $payload | Out-Null
            Assert (Test-Path (Join-Path $install ('versions/' + $version + '/DeskNest.App.exe'))) 'Same version reinstall failed.'
            & $Installer -Action Uninstall -InstallRoot $install | Out-Null
            & $Installer -Action Uninstall -InstallRoot $install | Out-Null
            if ($unknownFile) { Assert ([IO.File]::ReadAllText($unknown) -eq 'preserve') 'Unknown root file was deleted.' }
        }
    }
    Case 'unknown retired-version files stay put; a different version can reuse the root' {
        $payload = Payload 'retired'
        $newPayload = Payload 'retired-next'
        $install = Join-Path $root 'retired-app'
        & $Installer -InstallRoot $install -Payload $payload | Out-Null
        $unknown = Join-Path $install 'versions/retired/user-note.txt'
        [IO.File]::WriteAllText($unknown, 'preserve')
        & $Installer -Action Uninstall -InstallRoot $install | Out-Null
        Expect-Failure { & $Installer -InstallRoot $install -Payload $payload }
        & $Installer -InstallRoot $install -Payload $newPayload | Out-Null
        Assert ((Owner $install).activeVersion -eq 'retired-next') 'Owned root could not be reused for a vacant version.'
        Assert ([IO.File]::ReadAllText($unknown) -eq 'preserve') 'Unknown retired-version file was moved or deleted.'
    }
    Case 'ordinary damaged install cannot silently ignore missing payload' {
        $payload = Payload 'damaged'
        $install = Join-Path $root 'damaged-app'
        & $Installer -InstallRoot $install -Payload $payload | Out-Null
        [IO.File]::Delete((Join-Path $install 'versions/damaged/early.txt'))
        Expect-Failure { & $Installer -Action Uninstall -InstallRoot $install }
        $owner = Owner $install
        if ($owner.schemaVersion -eq 2) { Assert ($null -eq $owner.removal) 'Damaged installation acquired deletion authority.' }
        Assert (Test-Path (Join-Path $install 'versions/damaged/DeskNest.App.exe')) 'Damaged installation deleted remaining payload.'
    }
    Case 'unowned root stays refused' {
        $payload = Payload 'unowned'
        $install = Join-Path $root 'unowned-app'
        [IO.Directory]::CreateDirectory($install) | Out-Null
        Expect-Failure { & $Installer -InstallRoot $install -Payload $payload }
        Assert (@(Get-ChildItem -Force $install).Count -eq 0) 'Touched unowned root.'
    }
    foreach ($installed in @($false, $true)) {
        $payload = Payload ('linked-' + $installed)
        $install = Join-Path $root ('linked-app-' + $installed)
        if ($installed) { & $Installer -InstallRoot $install -Payload $payload | Out-Null }
        $manifest = if ($installed) { Join-Path $install ('versions/linked-' + $installed + '/package.json') } else { Join-Path $payload 'package.json' }
        $external = Join-Path $root ('external-manifest-' + $installed + '.json')
        [IO.File]::Move($manifest, $external)
        if (Link $manifest $external) {
            Case ('manifest leaf symlink refusal; installed=' + $installed) {
                if ($installed) { Expect-Failure { & $Installer -Action Uninstall -InstallRoot $install } }
                else { Expect-Failure { & $Installer -InstallRoot $install -Payload $payload }; Assert (!(Test-Path $install)) 'Published linked-manifest payload.' }
                Assert (Test-Path $external) 'External manifest was removed.'
                Assert (((Get-Item -Force $manifest).Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) 'Manifest link was removed.'
            }
        } else {
            $skipped++
            [IO.File]::Move($external, $manifest)
            Write-Output 'SKIP: real file-symlink fixture requires Windows symlink permission.'
        }
    }
    Write-Output ('RESULT: passed=' + $passed + '; skipped=' + $skipped + '; failed=' + $failures.Count)
    if ($failures.Count) { throw ($failures -join [Environment]::NewLine) }
} finally {
    foreach ($link in $links) {
        if (Test-Path -LiteralPath ([IO.Path]::GetDirectoryName($link))) { [IO.File]::Delete($link) }
    }
    Remove-Item -LiteralPath $root -Recurse -Force
}
