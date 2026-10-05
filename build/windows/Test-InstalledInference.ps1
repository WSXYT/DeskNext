param(
    [Parameter(Mandatory=$true)][string]$Bundle,
    [Parameter(Mandatory=$true)][string]$ModelDirectory,
    [Parameter(Mandatory=$true)][string]$ReferenceDirectory,
    [Parameter(Mandatory=$true)][string]$Python
)
# Developer-only integration runner. Python is an oracle dependency here, never an application runtime dependency.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$Bundle = [IO.Path]::GetFullPath($Bundle)
$ModelDirectory = [IO.Path]::GetFullPath($ModelDirectory)
$ReferenceDirectory = [IO.Path]::GetFullPath($ReferenceDirectory)
$Python = [IO.Path]::GetFullPath($Python)
foreach ($stage in @('A','B','C')) {
    if (!(Test-Path -LiteralPath (Join-Path $ReferenceDirectory ('p1-parity-' + $stage + '.json')))) { throw 'Run fresh A/B/C stages first.' }
}
if (Test-Path -LiteralPath (Join-Path $ReferenceDirectory 'p1-parity-D.json')) { throw 'Use a fresh reference directory; prior D evidence is preserved.' }
$manifest = Get-Content -LiteralPath (Join-Path $Bundle 'payload/package.json') -Raw | ConvertFrom-Json
$evidence = Join-Path $ReferenceDirectory 'installed'
if (Test-Path -LiteralPath $evidence) { throw 'Installation evidence directory already exists.' }
New-Item -ItemType Directory -Path $evidence | Out-Null
$installRoot = Join-Path ([IO.Path]::GetTempPath()) ('DeskNext.InstalledInference-' + [Guid]::NewGuid().ToString('N'))
$installRoot = Join-Path $installRoot 'Installed App With Spaces'
[IO.File]::WriteAllText((Join-Path $evidence 'install-root.txt'), $installRoot)
$installer = Join-Path $Bundle 'Install.ps1'
& $installer -Action Install -InstallRoot $installRoot -Payload (Join-Path $Bundle 'payload')
$app = Join-Path $installRoot ('versions/' + $manifest.version + '/DeskNest.App.exe')
& $Python (Join-Path $repository 'tests/Inference.Tests/parity_probe.py') D --worker $app --bundle $ModelDirectory --output $ReferenceDirectory *> (Join-Path $evidence 'parity-D.log')
if ($LASTEXITCODE -ne 0) { throw 'Installed stage D failed; installation and logs are retained.' }
$parity = @(Get-Content -LiteralPath (Join-Path $ReferenceDirectory 'p1-parity-D.json') -Raw | ConvertFrom-Json)
if ($parity.Count -ne 3 -or (Get-Content -LiteralPath (Join-Path $evidence 'parity-D.log') -Raw) -notmatch 'A-D: all raw inputs, tokens, logits, probabilities and actions matched') {
    throw 'Installed D did not produce complete comparison evidence.'
}
$appHash = (Get-FileHash -LiteralPath $app -Algorithm SHA256).Hash
& node (Join-Path $repository 'tests/run-app-probe.mjs') $app --headless-smoke ('--local-model=' + $ModelDirectory) *> (Join-Path $evidence 'model-workflow.log')
if ($LASTEXITCODE -ne 0) { throw 'Installed model workflow failed; installation and logs are retained.' }
$workflow = Get-Content -LiteralPath (Join-Path $evidence 'model-workflow.log') -Raw
if ($workflow -notmatch 'MODEL_SUGGESTION_MANUAL_IMPORT_UNDO_VERIFIED: true' -or $workflow -notmatch '"LocalClassificationPreviewVerified": true') {
    throw 'Installed model-to-confirmed-import evidence is missing.'
}
$sentinel = Join-Path $installRoot 'user-sentinel.txt'
[IO.File]::WriteAllText($sentinel, 'preserve')
& $installer -Action Uninstall -InstallRoot $installRoot
if ((Test-Path -LiteralPath $app) -or (Get-Content -LiteralPath $sentinel -Raw) -ne 'preserve') { throw 'Uninstall did not preserve the sentinel or remove the app.' }
$result = [ordered]@{
    version = $manifest.version; signed = $false; installedStageD = $true; appSha256 = $appHash
    modelManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $ModelDirectory 'manifest.json') -Algorithm SHA256).Hash
    installedModelConfirmedImportUndo = $true; uninstallPreservedUnknownFiles = $true
    scope = 'Fixed three-case A-D parity plus separate real-model UI confirmation/import/undo fixture; not quality or unattended execution acceptance.'
}
[IO.File]::WriteAllText((Join-Path $evidence 'result.json'), ($result | ConvertTo-Json -Depth 4))
$result | ConvertTo-Json -Depth 4
