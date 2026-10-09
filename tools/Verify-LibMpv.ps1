# Verifies the locally supplied native library without loading or executing it.
param(
    [string]$LibraryPath = (Join-Path $PSScriptRoot '..\native\win-x64\libmpv-2.dll'),
    [string]$ManifestPath = (Join-Path $PSScriptRoot '..\dependencies\libmpv.json')
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $LibraryPath -PathType Leaf)) {
    throw 'libmpv-2.dll is missing. Supply the build recorded in dependencies/libmpv.json.'
}

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($manifest.version) -or $manifest.sha256 -notmatch '^[a-fA-F0-9]{64}$') {
    throw 'Invalid libmpv manifest: version and a 64-digit SHA-256 are required.'
}

$actualHash = (Get-FileHash -LiteralPath $LibraryPath -Algorithm SHA256).Hash
if ($actualHash -ne $manifest.sha256) {
    throw 'libmpv-2.dll SHA-256 does not match dependencies/libmpv.json. Verify its source before updating the manifest.'
}

$fileVersion = (Get-Item -LiteralPath $LibraryPath).VersionInfo.FileVersion
if ($fileVersion -ne $manifest.version) {
    throw 'libmpv-2.dll file version does not match dependencies/libmpv.json.'
}

Write-Host "Verified libmpv $($manifest.version) (SHA-256 matches)."
