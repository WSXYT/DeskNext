param([string]$Publisher = (Join-Path $PSScriptRoot '..\Publish-Windows.ps1'))
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Join-Path ([IO.Path]::GetTempPath()) ('DeskNest-publish-test-' + [Guid]::NewGuid().ToString('N'))
function Assert([bool]$Condition, [string]$Message) { if (!$Condition) { throw $Message } }
function Expect-Failure([scriptblock]$Code) {
    $failed = $false
    try { & $Code | Out-Null } catch { $failed = $true }
    Assert $failed 'Expected packaging refusal.'
}
function Snapshot([string]$Directory) {
    return @(Get-ChildItem -LiteralPath $Directory -File -Recurse | Sort-Object FullName | ForEach-Object {
        $_.FullName.Substring($Directory.Length) + ':' + (Get-FileHash -LiteralPath $_.FullName).Hash
    }) -join "`n"
}
try {
    # An isolated fake repository exercises the real publisher without a build or
    # modifying project licenses/scripts. The payload is never launched.
    [IO.Directory]::CreateDirectory((Join-Path $root 'build/windows')) | Out-Null
    $script = Join-Path $root 'build/Publish-Windows.ps1'
    [IO.File]::Copy([IO.Path]::GetFullPath($Publisher), $script)
    foreach ($relative in @('LICENSE', 'THIRD_PARTY_NOTICES.md', 'build/windows/Install.ps1', 'build/windows/README.md')) {
        [IO.File]::WriteAllText((Join-Path $root $relative), ('fixture ' + $relative))
    }
    $gpuOutput = Join-Path $root 'gpu-refusal'
    Expect-Failure { & $script -Version 'gpu' -OutputRoot $gpuOutput -SkipBuild -IncludeExperimentalNvidia }
    Assert (!(Test-Path -LiteralPath $gpuOutput)) 'GPU SkipBuild refusal created output.'
    $output = Join-Path $root 'output'
    $payload = Join-Path $output 'DeskNest-win-x64/payload'
    [IO.Directory]::CreateDirectory($payload) | Out-Null
    [IO.File]::WriteAllText((Join-Path $payload 'DeskNest.App.exe'), 'synthetic executable fixture')
    & $script -Version 'retry' -OutputRoot $output -SkipBuild | Out-Null
    $before = Snapshot $output
    [IO.File]::WriteAllText((Join-Path $root 'LICENSE'), 'changed license')
    [IO.File]::WriteAllText((Join-Path $root 'build/windows/Install.ps1'), 'changed installer')
    Expect-Failure { & $script -Version 'retry' -OutputRoot $output -SkipBuild }
    Assert ((Snapshot $output) -ceq $before) 'Refused existing-manifest retry modified the bundle or archive.'
    # Archive-only guard must also run before copies, not merely at CreateFromDirectory.
    [IO.File]::Delete((Join-Path $payload 'package.json'))
    $before = Snapshot $output
    Expect-Failure { & $script -Version 'retry' -OutputRoot $output -SkipBuild }
    Assert ((Snapshot $output) -ceq $before) 'Refused existing-archive retry modified the bundle or archive.'
    Write-Output 'PASS: existing manifest and archive refusals leave all bundle/archive hashes unchanged.'
} finally { if (Test-Path $root) { Remove-Item -LiteralPath $root -Recurse -Force } }
