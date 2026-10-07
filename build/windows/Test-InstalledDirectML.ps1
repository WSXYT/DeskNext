param(
    [Parameter(Mandatory=$true)][string]$Bundle,
    [Parameter(Mandatory=$true)][string]$ModelDirectory,
    [Parameter(Mandatory=$true)][string]$EvidenceDirectory,
    [ValidateRange(0,15)][int]$Adapter = 1
)
# Opt-in local GPU integration only. No CPU model execution, user workspace or public release.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$Bundle = [IO.Path]::GetFullPath($Bundle)
$ModelDirectory = [IO.Path]::GetFullPath($ModelDirectory)
$EvidenceDirectory = [IO.Path]::GetFullPath($EvidenceDirectory)
if (Test-Path -LiteralPath $EvidenceDirectory) { throw 'Evidence directory already exists; preserve it.' }
$gpuRelative = 'worker/ort-1.24.4/win-x64/directml'
$payload = Join-Path $Bundle 'payload'
$manifest = Get-Content -LiteralPath (Join-Path $payload 'package.json') -Raw | ConvertFrom-Json
$workerRelative = $gpuRelative + '/DeskNest.DirectMLProbe.exe'
if (@($manifest.files | Where-Object { $_.path -eq $workerRelative }).Count -ne 1) { throw 'Bundle lacks its optional NVIDIA worker.' }
if (@($manifest.files | Where-Object { $_.path -match '\.(onnx|safetensors)$|(^|/)test/recovery/' }).Count) {
    throw 'Use the focused model-free package without the recovery probe.'
}
$modelManifest = Join-Path $ModelDirectory 'manifest.json'
$modelHash = (Get-FileHash -LiteralPath $modelManifest -Algorithm SHA256).Hash
[IO.Directory]::CreateDirectory($EvidenceDirectory) | Out-Null
$installRoot = Join-Path ([IO.Path]::GetTempPath()) ('DeskNext.InstalledGpu-' + [Guid]::NewGuid().ToString('N'))
$installRoot = Join-Path $installRoot 'Installed App With Spaces'
[IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'install-root.txt'), $installRoot)
$installer = Join-Path $Bundle 'Install.ps1'
& $installer -Action Install -InstallRoot $installRoot -Payload $payload
$versionRoot = Join-Path $installRoot ('versions/' + $manifest.version)
$app = Join-Path $versionRoot 'DeskNest.App.exe'
$worker = Join-Path $versionRoot $workerRelative
$cpuOrt = Join-Path $versionRoot 'onnxruntime.dll'
$gpuOrt = Join-Path (Join-Path $versionRoot $gpuRelative) 'onnxruntime.dll'
if ((Get-FileHash -LiteralPath $cpuOrt).Hash -eq (Get-FileHash -LiteralPath $gpuOrt).Hash -or
    (Test-Path -LiteralPath (Join-Path $versionRoot 'DirectML.dll'))) {
    throw 'CPU and DirectML native runtime isolation was not preserved.'
}
$log = Join-Path $EvidenceDirectory 'installed-gpu-workflow.log'
& node (Join-Path $repository 'tests/run-app-probe.mjs') $app '--directml-preview-smoke' ('--gpu-worker=' + $worker) ('--local-model=' + $ModelDirectory) ('--gpu-adapter=' + $Adapter) *> $log
if ($LASTEXITCODE -ne 0) { throw ('Installed GPU workflow failed; retain installation and log: ' + $log) }
$parts = (Get-Content -LiteralPath $log -Raw).Split([string[]]@('PROBE_RESULT_JSON:'), [StringSplitOptions]::None)
if ($parts.Count -ne 2) { throw 'Missing terminal GPU workflow evidence.' }
$result = $parts[1].Trim() | ConvertFrom-Json
foreach ($flag in @('Success','DirectMlPreviewVerified','LocalClassificationPreviewVerified','LocalWorkerReuseVerified')) {
    if ($result.$flag -isnot [bool] -or !$result.$flag) { throw ('GPU workflow did not verify ' + $flag) }
}
if ($result.Worker -cne $worker -or $result.DirectMlGraphProfiles -lt 8) { throw 'Workflow did not use the installed NVIDIA worker.' }
$appHash = (Get-FileHash -LiteralPath $app).Hash
$workerHash = (Get-FileHash -LiteralPath $worker).Hash
# Preserve the passed workflow independently if the outer command is interrupted during uninstall.
[IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'workflow-result.json'), ([ordered]@{
    version=$manifest.version; installedGpuWorkflowVerified=$true; appSha256=$appHash; workerSha256=$workerHash
    modelManifestSha256=$modelHash; runtimeIsolationVerified=$true; workflow=$result; uninstallCompleted=$false
} | ConvertTo-Json -Depth 8))
$sentinel = Join-Path $installRoot 'user-sentinel.txt'
[IO.File]::WriteAllText($sentinel, 'preserve')
& $installer -Action Uninstall -InstallRoot $installRoot
if ((Test-Path -LiteralPath $app) -or (Test-Path -LiteralPath $worker) -or
    [IO.File]::ReadAllText($sentinel) -ne 'preserve' -or
    (Get-FileHash -LiteralPath $modelManifest -Algorithm SHA256).Hash -ne $modelHash) {
    throw 'Uninstall failed to preserve model/unknown files or remove owned executables.'
}
$summary = [ordered]@{
    version=$manifest.version; signed=$false; installedGpuWorkflowVerified=$true; adapter=$Adapter
    appSha256=$appHash; workerSha256=$workerHash; modelManifestSha256=$modelHash
    directMlGraphProfiles=$result.DirectMlGraphProfiles; elapsedMs=$result.ElapsedMs
    cpuGpuRuntimeIsolationVerified=$true; uninstallPreservedModelAndUnknownFiles=$true
    workflowEvidence=$result.EvidenceDirectory
    scope='Installed App + installed NVIDIA worker using headless production preview/confirmation/import/undo commands; no native desktop, quality, signing, automatic selection or clean-machine claim.'
}
[IO.File]::WriteAllText((Join-Path $EvidenceDirectory 'result.json'), ($summary | ConvertTo-Json -Depth 4))
$summary | ConvertTo-Json -Depth 4
