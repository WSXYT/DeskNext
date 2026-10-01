$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$installer = Join-Path $PSScriptRoot 'Install.ps1'
$root = Join-Path ([IO.Path]::GetTempPath()) ('DeskNest-installer-test-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Make-Payload([string]$Version) {
    $path = Join-Path $root ('payload-' + $Version)
    New-Item -ItemType Directory -Path $path | Out-Null
    $exe = Join-Path $path 'DeskNest.App.exe'
    [IO.File]::WriteAllText($exe, ('synthetic installer fixture ' + $Version))
    $manifest = [ordered]@{
        schemaVersion = 1; rid = 'win-x64'; version = $Version
        files = @([ordered]@{ path = 'DeskNest.App.exe'; length = (Get-Item $exe).Length; sha256 = (Get-FileHash $exe).Hash })
    }
    [IO.File]::WriteAllText((Join-Path $path 'package.json'), ($manifest | ConvertTo-Json -Depth 6))
    return $path
}
function Expect-Failure([scriptblock]$Code) {
    $failed = $false
    try { & $Code | Out-Null } catch { $failed = $true }
    Assert $failed 'Expected operation to fail closed.'
}
try {
    $first = Make-Payload '0.3.0-test1'
    $second = Make-Payload '0.3.0-test2'
    $install = Join-Path $root 'app'
    & $installer -Payload $first -InstallRoot $install
    $firstExe = Join-Path $install 'versions\0.3.0-test1\DeskNest.App.exe'
    Assert (Test-Path $firstExe) 'Install did not publish payload.'
    $unknown = Join-Path $install 'versions\0.3.0-test1\my-notes.txt'
    [IO.File]::WriteAllText($unknown, 'user data')
    & $installer -Payload $second -InstallRoot $install
    Assert (Test-Path $firstExe) 'Upgrade removed previous version.'
    $owner = Get-Content (Join-Path $install '.desknest-install.json') -Raw | ConvertFrom-Json
    Assert ($owner.activeVersion -eq '0.3.0-test2') 'Upgrade did not select new version.'
    Expect-Failure { & $installer -Payload $second -InstallRoot $install }
    $original = [IO.File]::ReadAllBytes($firstExe)
    [IO.File]::WriteAllText($firstExe, 'modified externally')
    Expect-Failure { & $installer -Action Uninstall -InstallRoot $install }
    Assert (Test-Path (Join-Path $install 'versions\0.3.0-test2\DeskNest.App.exe')) 'Uninstall deleted before validating all payloads.'
    [IO.File]::WriteAllBytes($firstExe, $original)
    & $installer -Action Uninstall -InstallRoot $install
    Assert (!(Test-Path $firstExe)) 'Uninstall retained an owned executable.'
    Assert ((Get-Content $unknown -Raw) -eq 'user data') 'Uninstall removed unknown user files.'
    $unowned = Join-Path $root 'unowned'
    New-Item -ItemType Directory -Path $unowned | Out-Null
    Expect-Failure { & $installer -Payload $first -InstallRoot $unowned }
    $manifestPath = Join-Path $first 'package.json'
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $manifest.files += [pscustomobject]@{ path = '../outside.txt'; length = 0; sha256 = ('0' * 64) }
    [IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 6))
    Expect-Failure { & $installer -Payload $first -InstallRoot (Join-Path $root 'invalid') }
    Write-Output 'PASS: synthetic payload install, upgrade, ownership, corruption, path traversal and non-destructive uninstall.'
    Write-Output 'These fixtures do not prove native application installation or signed-package authenticity.'
} finally {
    # Only the test-owned GUID directory is recursively cleaned.
    Remove-Item -LiteralPath $root -Recurse -Force
}
