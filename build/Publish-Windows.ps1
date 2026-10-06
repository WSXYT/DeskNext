param(
    [string]$Version = '0.3.0-dev',
    [string]$OutputRoot,
    [switch]$SkipBuild,
    [switch]$IncludeRecoveryProbe,
    [string]$PoggetNativeLibrary
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ($Version -notmatch '^[A-Za-z0-9][A-Za-z0-9.-]{0,63}$') { throw 'Invalid package version.' }
if (!$OutputRoot) { $OutputRoot = Join-Path $repository ('artifacts\packages\' + $Version) }
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$bundle = Join-Path $OutputRoot 'DeskNest-win-x64'
$payload = Join-Path $bundle 'payload'
$manifestPath = Join-Path $payload 'package.json'
$archive = Join-Path $OutputRoot ('DeskNest-' + $Version + '-win-x64-unsigned.zip')
# Refusal must be side-effect free, including SkipBuild and archive-only retries.
if ((Test-Path -LiteralPath $manifestPath) -or (Test-Path -LiteralPath $archive)) {
    throw 'Manifest or archive already exists; refusing to change an existing package.'
}
$nativeArgs = @()
if ($PoggetNativeLibrary) {
    $PoggetNativeLibrary = [IO.Path]::GetFullPath($PoggetNativeLibrary)
    if (!(Test-Path -LiteralPath $PoggetNativeLibrary -PathType Leaf) -or
        [IO.Path]::GetFileName($PoggetNativeLibrary) -ne 'desknest_pogget.dll' -or
        ((Get-Item -LiteralPath $PoggetNativeLibrary).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Supply a compiled, non-linked Windows desknest_pogget.dll.'
    }
    $nativeArgs = @('-p:PoggetNativeLibrary=' + $PoggetNativeLibrary)
    if ($SkipBuild) { throw 'Native library selection requires a fresh build, not SkipBuild.' }
}
if (!$SkipBuild) {
    if (Test-Path -LiteralPath $bundle) { throw 'Output already exists. Use a fresh version or output directory.' }
    New-Item -ItemType Directory -Path $payload -Force | Out-Null
    & dotnet publish (Join-Path $repository 'src\DeskNest.App\DeskNest.App.csproj') -c Release -r win-x64 --self-contained true -m:1 --disable-build-servers -p:UseSharedCompilation=false @nativeArgs -o $payload
    if ($LASTEXITCODE -ne 0) { throw 'UI publish failed.' }
    & dotnet publish (Join-Path $repository 'src\DeskNest.Inference\DeskNest.Inference.csproj') -c Release -r win-x64 --self-contained true -m:1 --disable-build-servers -p:UseSharedCompilation=false -o (Join-Path $payload 'worker\cpu')
    if ($LASTEXITCODE -ne 0) { throw 'Inference worker publish failed.' }
    if ($IncludeRecoveryProbe) {
        & dotnet publish (Join-Path $repository 'tests\DeskNest.RecoveryProbe\DeskNest.RecoveryProbe.csproj') -c Release -r win-x64 --self-contained true -m:1 --disable-build-servers -p:UseSharedCompilation=false -o (Join-Path $payload 'test\recovery')
        if ($LASTEXITCODE -ne 0) { throw 'Test-only recovery probe publish failed.' }
    }
}
if ($IncludeRecoveryProbe -and !(Test-Path -LiteralPath (Join-Path $payload 'test\recovery\DeskNest.RecoveryProbe.exe'))) {
    throw 'The requested test-only recovery probe is absent.'
}
if (!(Test-Path -LiteralPath (Join-Path $payload 'DeskNest.App.exe'))) { throw 'Published application not found.' }
Copy-Item -LiteralPath (Join-Path $repository 'LICENSE'),(Join-Path $repository 'THIRD_PARTY_NOTICES.md') -Destination $payload
Copy-Item -LiteralPath (Join-Path $repository 'build\windows\Install.ps1') -Destination $bundle
Copy-Item -LiteralPath (Join-Path $repository 'build\windows\README.md') -Destination $bundle
$files = @(Get-ChildItem -File -Recurse -LiteralPath $payload | Sort-Object FullName | ForEach-Object {
    if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Package contains a link.' }
    [ordered]@{
        path = $_.FullName.Substring($payload.Length + 1).Replace('\','/')
        length = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
$manifest = [ordered]@{ schemaVersion = 1; rid = 'win-x64'; version = $Version; signed = $false; files = $files }
[IO.File]::WriteAllText($manifestPath, ($manifest | ConvertTo-Json -Depth 6))
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($bundle, $archive)
Get-FileHash -LiteralPath $archive -Algorithm SHA256 | Format-List
Write-Output ('Unsigned development package: ' + $archive)
