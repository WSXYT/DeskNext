param(
    [string]$ModelDirectory = "$PSScriptRoot/../artifacts/model/exported-multilingual",
    [string]$OutputDirectory = "$PSScriptRoot/../artifacts/model-release"
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = [IO.Path]::GetFullPath($ModelDirectory)
$output = [IO.Path]::GetFullPath($OutputDirectory)
$manifest = Get-Content -LiteralPath "$root/manifest.json" -Raw | ConvertFrom-Json
$names = @('encoder.onnx','encoder.onnx.data','head.onnx','head.onnx.data','rl_agent_config.json','tokenizer.json')
if ((($manifest.files.PSObject.Properties.Name | Sort-Object) -join '|') -cne (($names | Sort-Object) -join '|')) {
    throw 'Unexpected model file set.'
}
if ($manifest.sourceCommit -ne '970dc8c5f63d7b886a68409493f37d569424f933' -or
    $manifest.checkpointRevision -ne '55cf4c4ebb4ebe31b2550e8bdf3bd21b99753851' -or
    $manifest.weightsSha256 -ne '9d628fd971b700382ac6f65920a86f149777b2e748e0c955fb3b19695aa8f204') {
    throw 'Unexpected model provenance.'
}
$files = [ordered]@{}
foreach ($name in $names + 'manifest.json') { $files[$name] = Join-Path $root $name }
$files['LICENSE-Laya.txt'] = "$PSScriptRoot/../artifacts/laya-source/LICENSE"
$files['NOTICE.md'] = "$PSScriptRoot/model/NOTICE.md"
$files['export-strict.patch'] = "$PSScriptRoot/../tests/Inference.Tests/export-strict.patch"
foreach ($path in $files.Values) {
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or
        ((Get-Item -LiteralPath $path).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "Missing or linked file: $path" }
}
foreach ($name in $names) {
    if ((Get-FileHash -LiteralPath $files[$name] -Algorithm SHA256).Hash -ine $manifest.files.$name) { throw "Hash mismatch: $name" }
}
if ((Get-FileHash -LiteralPath $files['export-strict.patch'] -Algorithm SHA256).Hash -ine $manifest.exportPatchSha256) {
    throw 'Export patch does not match the recorded provenance.'
}
[IO.Directory]::CreateDirectory($output) | Out-Null
$archive = Join-Path $output 'desknext-laya-multilingual-fp32-v1.zip'
$partial = "$archive.partial"
if ((Test-Path -LiteralPath $archive) -or (Test-Path -LiteralPath $partial)) { throw 'Refusing to replace an existing model artifact.' }
$zip = [IO.Compression.ZipFile]::Open($partial, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($name in $files.Keys) {
        $entry = $zip.CreateEntry($name, [IO.Compression.CompressionLevel]::Fastest)
        $entry.LastWriteTime = [DateTimeOffset]::new(2026, 9, 24, 0, 0, 0, [TimeSpan]::Zero)
        $input = [IO.File]::OpenRead($files[$name])
        try {
            $target = $entry.Open()
            try { $input.CopyTo($target) } finally { $target.Dispose() }
        } finally { $input.Dispose() }
    }
} finally { $zip.Dispose() }
$zip = [IO.Compression.ZipFile]::OpenRead($partial)
try {
    if ($zip.Entries.Count -ne $files.Count) { throw 'Unexpected archive entry count.' }
    foreach ($name in $files.Keys) {
        $stream = $zip.GetEntry($name).Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try { $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-','') }
        finally { $sha.Dispose(); $stream.Dispose() }
        $expected = if ($names -contains $name) { $manifest.files.$name } else { (Get-FileHash -LiteralPath $files[$name] -Algorithm SHA256).Hash }
        if ($hash -ine $expected) { throw "Archive verification failed: $name" }
    }
} finally { $zip.Dispose() }
[IO.File]::Move($partial, $archive)
$receipt = [ordered]@{ archive = $archive; length = (Get-Item -LiteralPath $archive).Length; sha256 = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant(); entries = $files.Count }
$receipt | ConvertTo-Json | Set-Content -LiteralPath "$output/package-receipt.json" -Encoding UTF8
$receipt | ConvertTo-Json
