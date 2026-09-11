# Builds the LightWeaver installer: self-contained publish + Inno Setup compile.
# Usage: .\installer.ps1        ->  dist\LightWeaver-<version>-setup.exe
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# 1) publish (also produces the portable zip)
& (Join-Path $root 'publish.ps1')

# 2) version from the csproj
$csproj = Join-Path $root 'src\LightWeaver\LightWeaver.csproj'
$version = (Select-String -Path $csproj -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
if (-not $version) { Write-Error 'No <Version> found in LightWeaver.csproj' }

# 3) compile the installer
$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $cmd = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($cmd) { $iscc = $cmd.Source }
}
if (-not $iscc) { Write-Error 'Inno Setup 6 not found - install with: winget install -e --id JRSoftware.InnoSetup' }

& $iscc "/DMyAppVersion=$version" (Join-Path $root 'installer\LightWeaver.iss')
if ($LASTEXITCODE -ne 0) { Write-Error 'ISCC failed' }

$setup = Join-Path $root "dist\LightWeaver-$version-setup.exe"

# 4) sign the installer itself. This is the one signature most users actually see: the setup exe is
# what SmartScreen inspects at download, and an unsigned installer warns even when everything it
# carries is signed. The app binaries inside were already signed by publish.ps1 above.
& (Join-Path $root 'sign.ps1') -Path $setup

Write-Host "Done: $setup"
Get-Item $setup | Select-Object Name, @{n='SizeMB';e={[math]::Round($_.Length/1MB,1)}}
