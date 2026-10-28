# Builds the installer and publishes a GitHub release with the setup.exe as the asset.
# Usage: .\release.ps1 [-SkipBuild] [-NotesFile <path>]
#   -SkipBuild  reuses dist\LightWeaver-<version>-setup.exe from a previous build.
#   -NotesFile  publishes these release notes instead of deriving them from commit subjects.
#               Prefer it: commit subjects are written for whoever maintains the code, and they
#               read like build plumbing to everyone else.
# Publishing goes through the gh CLI, which must already be authenticated (gh auth status).
# If a release for the version's tag already exists, its release entry is replaced
# (the tag itself is kept for history).
param(
    [switch]$SkipBuild,
    [string]$NotesFile
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$repo = 'oss96/LightWeaver-Windows'

$props = Join-Path $root 'Directory.Build.props'
$version = (Select-String -Path $props -Pattern '<Version>([^<]+)</Version>').Matches[0].Groups[1].Value
if (-not $version) { Write-Error 'No <Version> found in Directory.Build.props' }

if ($NotesFile) {
    if (-not (Test-Path -LiteralPath $NotesFile)) { Write-Error "No such notes file: $NotesFile" }
    $body = [IO.File]::ReadAllText($NotesFile).TrimEnd()
    if (-not $body) { Write-Error "$NotesFile is empty" }
} else {
    # Changelog: commits since the previous release tag (best-effort). Convenience only - these
    # are commit subjects, written for whoever maintains the code. Prefer -NotesFile for anything
    # that ends up on a public release page.
    Push-Location $root
    try {
        # Exclude the version being released: if its tag was pushed before this script runs
        # (the release/X.Y.Z + tag flow), describe would return it and the range would be empty,
        # so the changelog silently degraded to the "LightWeaver <version>" fallback (0.3.1).
        $lastTag = cmd /c "git describe --tags --abbrev=0 --exclude=$version 2>nul"
        $range = if ($lastTag) { "$lastTag..HEAD" } else { 'HEAD' }
        $log = cmd /c "git log --pretty=format:`"- %s`" $range 2>nul" | Select-Object -First 40
        $body = if ($log) { ($log -join "`n") } else { "LightWeaver $version" }
    } finally { Pop-Location }
}

$setup = Join-Path $root "dist\LightWeaver-$version-setup.exe"
if (-not $SkipBuild) {
    & (Join-Path $root 'installer.ps1')
    if ($LASTEXITCODE -ne 0) { Write-Error 'installer.ps1 failed' }
}
if (-not (Test-Path $setup)) { Write-Error "Missing $setup - build first (or drop -SkipBuild)" }

# Check the asset the way the updater will, on every path including -SkipBuild, which reuses a file
# from disk: signed and untampered, timestamped, signed by the same certificate as the app it
# installs, and stamped with this release's version. A clean signature alone is not enough - the
# updater refuses an installer whose product version is not the release tag.
$setupSig = Get-AuthenticodeSignature -LiteralPath $setup
if (-not $setupSig.SignerCertificate -or $setupSig.Status -in @('HashMismatch', 'NotSigned')) {
    Write-Error "$setup is unsigned or was modified after signing (status $($setupSig.Status))"
}
if (-not $setupSig.TimeStamperCertificate) { Write-Error "$setup is signed but not timestamped" }
$appExe = Join-Path $root 'dist\publish\LightWeaver.exe'
if (-not (Test-Path -LiteralPath $appExe)) { Write-Error "Missing $appExe - the installer's app cannot be checked" }
$appSig = Get-AuthenticodeSignature -LiteralPath $appExe
if (-not $appSig.SignerCertificate -or $appSig.SignerCertificate.Thumbprint -ne $setupSig.SignerCertificate.Thumbprint) {
    Write-Error 'The installer and the app it installs are signed by different certificates; installed clients would refuse the update'
}
$setupVersion = "$((Get-Item -LiteralPath $setup).VersionInfo.ProductVersion)".Trim()
if ($setupVersion -ne $version) {
    Write-Error "$setup carries product version '$setupVersion', not $version; installed clients would refuse the update"
}

if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { Write-Error 'The gh CLI is not on PATH' }
# cmd isolates gh's stderr (PS 5.1 turns native stderr into terminating errors).
cmd /c "gh auth status >nul 2>&1"
if ($LASTEXITCODE -ne 0) { Write-Error 'gh is not authenticated - run: gh auth login' }

# Replace an existing release entry for this tag (keep the tag itself: no --cleanup-tag).
cmd /c "gh release view $version --repo $repo >nul 2>&1"
if ($LASTEXITCODE -eq 0) {
    Write-Host "Deleting existing release entry for $version (the tag is kept)..."
    # gh writes progress to stderr, which PS 5.1 turns into a terminating NativeCommandError under
    # 'Stop' whenever the host redirects it (any non-interactive run). Drop to 'Continue' for the
    # call itself and judge it by its exit code, which is what actually says whether it worked.
    $ErrorActionPreference = 'Continue'
    & gh release delete $version --yes --repo $repo
    $ghExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($ghExit -ne 0) { Write-Error 'Deleting the existing release entry failed' }
}

Write-Host "Creating release $version with $(Split-Path $setup -Leaf) ($([math]::Round((Get-Item $setup).Length/1MB,1)) MB)..."
$notes = Join-Path $env:TEMP "lw-release-notes-$version.md"
try {
    [IO.File]::WriteAllText($notes, $body)
    # 'Continue' for the same reason as the delete above: gh's upload progress goes to stderr.
    $ErrorActionPreference = 'Continue'
    $url = & gh release create $version $setup --title "LightWeaver $version" --notes-file $notes --repo $repo --verify-tag
    $ghExit = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($ghExit -ne 0) { Write-Error 'Release creation failed' }
} finally {
    $ErrorActionPreference = 'Stop'
    Remove-Item $notes -Force -ErrorAction SilentlyContinue
}

Write-Host "Done: release $version published with LightWeaver-$version-setup.exe"
Write-Host "  $url"
