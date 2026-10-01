# A direct DOS-device alias on an ephemeral GitHub runner, never a user's desktop.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Volume alias provisioning is restricted to ephemeral GitHub-hosted runners.'
}
Add-Type @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class DeskNextAliasFixture {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    public static extern uint QueryDosDevice(string name, StringBuilder target, uint length);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DefineDosDevice(uint flags, string name, string target);
}
'@
$buffer = [Text.StringBuilder]::new(4096)
if ([DeskNextAliasFixture]::QueryDosDevice('Z:', $buffer, 4096) -ne 0) {
    throw 'Z: is occupied; refusing to replace it.'
}
if ([Runtime.InteropServices.Marshal]::GetLastWin32Error() -ne 2) {
    throw 'Could not establish that Z: is unused.'
}
$root = [IO.Path]::GetPathRoot([IO.Path]::GetTempPath()).TrimEnd('\')
$buffer.Clear() | Out-Null
if ([DeskNextAliasFixture]::QueryDosDevice($root, $buffer, 4096) -eq 0) {
    throw 'Could not resolve the source native volume device.'
}
$target = $buffer.ToString()
if (!$target.StartsWith('\Device\HarddiskVolume')) { throw 'Unsupported native volume fixture.' }
# RAW_TARGET_PATH | NO_BROADCAST_SYSTEM: one native device hop, not SUBST's extra alias hop.
if (![DeskNextAliasFixture]::DefineDosDevice(9, 'Z:', $target)) {
    throw 'Could not create direct native test alias.'
}
$previous = $env:DESKNEXT_TEST_VOLUME_ALIAS
try {
    $env:DESKNEXT_TEST_VOLUME_ALIAS = 'Z:\'
    $project = Join-Path $PSScriptRoot '../../tests/DeskNest.Core.Tests/DeskNest.Core.Tests.csproj'
    dotnet test $project -c Release --no-build --no-restore `
        --filter 'FullyQualifiedName~WindowsCopyDestinationTests'
    if ($LASTEXITCODE -ne 0) { throw 'Native alias tests failed.' }
} finally {
    $env:DESKNEXT_TEST_VOLUME_ALIAS = $previous
    # Remove only this exact raw target, not an unrelated later mapping.
    if (![DeskNextAliasFixture]::DefineDosDevice(15, 'Z:', $target)) {
        throw 'Exact-target test alias cleanup failed.'
    }
}
