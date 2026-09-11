# Builds a self-contained win-x64 release with bundled libmpv and zips it into dist/.
# Usage: .\publish.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

$libmpv = Join-Path $root 'native\win-x64\libmpv-2.dll'
if (-not (Test-Path $libmpv)) {
    Write-Error "native\win-x64\libmpv-2.dll is missing - drop an mpv >= 0.40 libmpv build there first."
}

$csproj = Join-Path $root 'src\LightWeaver\LightWeaver.csproj'
$version = (Select-String -Path $csproj -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
if (-not $version) { Write-Error 'No <Version> found in LightWeaver.csproj' }

$publishDir = Join-Path $root 'dist\publish'
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

Write-Host "Publishing LightWeaver $version (self-contained win-x64)..."
dotnet publish $csproj -c Release -r win-x64 --self-contained true -o $publishDir
if ($LASTEXITCODE -ne 0) { Write-Error 'dotnet publish failed' }

if (-not (Test-Path (Join-Path $publishDir 'libmpv-2.dll'))) {
    Write-Error 'libmpv-2.dll did not land in the publish output'
}

# Sign BEFORE the zip and before installer.ps1 compiles this directory, so the portable zip and the
# installed copy carry the same signature. Signing after either one would leave an unsigned exe
# inside a signed installer, which is the arrangement that looks fine and helps nobody.
#
# Only our own two binaries: the apphost the user launches and the managed assembly behind it. The
# .NET runtime files are already signed by Microsoft, and re-signing a third party's libmpv-2.dll
# with our certificate would assert authorship we do not have.
& (Join-Path $root 'sign.ps1') -Path @(
    (Join-Path $publishDir 'LightWeaver.exe'),
    (Join-Path $publishDir 'LightWeaver.dll')
)

$zip = Join-Path $root "dist\LightWeaver-$version-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zip

Write-Host "Done: $zip"
Get-Item $zip | Select-Object Name, @{n='SizeMB';e={[math]::Round($_.Length/1MB,1)}}
