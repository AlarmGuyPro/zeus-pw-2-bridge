# SPDX-License-Identifier: GPL-2.0-or-later
#requires -Version 7
[CmdletBinding()]
param([string] $Package)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$manifest = Get-Content -Raw "$repoRoot/src/Pw2Bridge/plugin.json" | ConvertFrom-Json
if (-not $Package) {
    $Package = "$repoRoot/artifacts/$($manifest.id)/$($manifest.id)-$($manifest.version).zip"
}
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Package))
try {
    $paths = @($archive.Entries.FullName)
    foreach ($required in @(
        'plugin.json', 'Zeus.Community.Pw2Bridge.dll', 'Zeus.Community.Pw2Bridge.deps.json',
        'System.IO.Ports.dll', 'README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md',
        'ui/pw2bridge.es.js',
        'runtimes/win/lib/net9.0/System.IO.Ports.dll',
        'runtimes/unix/lib/net9.0/System.IO.Ports.dll',
        'runtimes/linux-x64/native/libSystem.IO.Ports.Native.so',
        'runtimes/linux-arm64/native/libSystem.IO.Ports.Native.so',
        'runtimes/linux-arm/native/libSystem.IO.Ports.Native.so',
        'runtimes/osx-x64/native/libSystem.IO.Ports.Native.dylib',
        'runtimes/osx-arm64/native/libSystem.IO.Ports.Native.dylib'
    )) {
        if ($required -cnotin $paths) { throw "Missing portable package asset: $required" }
    }
    if ($paths | Where-Object { $_ -match '(^|/)(AGENTS\.md|Zeus\.Plugins\.Contracts\.dll)$' }) {
        throw 'Package includes a private instruction file or host contracts'
    }
    $reader = [IO.StreamReader]::new($archive.GetEntry('Zeus.Community.Pw2Bridge.deps.json').Open())
    try { $deps = $reader.ReadToEnd() | ConvertFrom-Json -AsHashtable }
    finally { $reader.Dispose() }
    if ($deps.runtimeTarget.name.Contains('/')) { throw 'Dependency manifest pins a single runtime' }
    $serial = $deps.targets[$deps.runtimeTarget.name]['System.IO.Ports/9.0.0'].runtimeTargets
    foreach ($runtime in @('win', 'unix')) {
        if (-not $serial.Contains("runtimes/$runtime/lib/net9.0/System.IO.Ports.dll")) {
            throw "Dependency manifest cannot resolve $runtime serial assembly"
        }
    }
    $declaredRuntimeAssets = @($deps.targets.Values | ForEach-Object {
        $_.Values | ForEach-Object {
            if ($_.Contains('runtimeTargets')) { $_.runtimeTargets.Keys }
        }
    } | Sort-Object -Unique)
    foreach ($path in $paths | Where-Object { $_.StartsWith('runtimes/') }) {
        if ($path -cnotin $declaredRuntimeAssets) { throw "Undeclared runtime asset: $path" }
    }
    foreach ($path in $declaredRuntimeAssets) {
        if ($path -cnotin $paths) { throw "Missing declared runtime asset: $path" }
    }
    Write-Host "PASS: portable package contents and dependency manifest ($($paths.Count) files)"
}
finally { $archive.Dispose() }

# Use the workspace's artifact location: system /tmp may be mounted noexec.
$extracted = Join-Path "$repoRoot/artifacts" ("package-test-" + [Guid]::NewGuid().ToString('N'))
try {
    [IO.Compression.ZipFile]::ExtractToDirectory((Resolve-Path -LiteralPath $Package), $extracted)
    Push-Location -LiteralPath (Join-Path $PSScriptRoot 'Pw2CivHarness')
    try {
        dotnet run -c Release -- package-smoke $extracted
        if ($LASTEXITCODE -ne 0) { throw "Packaged dependency smoke test failed: $LASTEXITCODE" }
    }
    finally { Pop-Location }
}
finally {
    if (Test-Path -LiteralPath $extracted) { Remove-Item -LiteralPath $extracted -Recurse -Force }
}
