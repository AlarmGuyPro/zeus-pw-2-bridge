# SPDX-License-Identifier: GPL-2.0-or-later
#requires -Version 7
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$buildScript = Join-Path $repoRoot 'src/Pw2Bridge/build-package.ps1'
& $buildScript

$runtimeRoot = Join-Path $repoRoot 'src/Pw2Bridge/bin/Release/net10.0/runtimes'
$probe = Join-Path $runtimeRoot ("unowned-" + [Guid]::NewGuid().ToString('N') + '.txt')
try {
    # A directory-wide staging copy would wrongly include this undeclared file.
    [void](New-Item -Path $probe -ItemType File -Value 'package isolation regression probe')
    & $buildScript
    & (Join-Path $PSScriptRoot 'test-package.ps1')
    Write-Host 'PASS: repeated package build excludes undeclared output'
}
finally {
    if (Test-Path -LiteralPath $probe) { Remove-Item -LiteralPath $probe -Force }
}
