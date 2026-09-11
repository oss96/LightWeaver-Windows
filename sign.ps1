# Authenticode-signs LightWeaver binaries. Called by publish.ps1 (the app exe, before the zip and
# before Inno picks the files up) and by installer.ps1 (the setup exe, after ISCC).
#
# WHEN SIGNING IS NOT CONFIGURED THIS IS A NO-OP, and says so on one line. That is deliberate: a
# release must be buildable on a machine with no certificate, and failing instead would make
# publish.ps1 unusable for anyone without one. Set LIGHTWEAVER_SIGN_REQUIRE=1 (or pass -Require) to
# turn a missing configuration into an error, which is what a real release should do - an unsigned
# release published silently is the failure mode that costs most.
#
# CONFIGURATION lives in the environment, never in the repo, because every route involves either a
# secret or an account identity:
#
#   LIGHTWEAVER_SIGN_MODE = artifact-signing | certstore | pfx     (unset = no-op)
#
#   artifact-signing  Microsoft's managed signing service (Azure Trusted Signing, being rebranded
#                     Azure Artifact Signing). No key ever reaches this machine. Check availability
#                     in your country first - see docs/CODE-SIGNING.md.
#     LIGHTWEAVER_SIGN_DLIB      path to Azure.CodeSigning.Dlib.dll
#     LIGHTWEAVER_SIGN_METADATA  path to the JSON metadata (endpoint, account, certificate profile)
#
#   certstore         a certificate already in this user's store - which is how a hardware token
#                     presents itself, and OV/EV certs have required one since 2023. THE RIGHT MODE
#                     FOR ANY REAL CERTIFICATE: there is no password for this script to handle.
#     LIGHTWEAVER_SIGN_THUMBPRINT   SHA1 thumbprint, no spaces
#
#   pfx               a .pfx file on disk. FOR TESTING ONLY, and not merely because the key is
#                     exportable: signtool takes the password as a COMMAND-LINE argument, so while
#                     it runs any process under this user can read it, and a machine with
#                     process-creation auditing (event 4688) or Sysmon writes the whole command
#                     line - password included - permanently into an event log. This script never
#                     prints or stores it; that is not the same as it being unobservable. Import a
#                     real certificate into the store and use certstore instead.
#     LIGHTWEAVER_SIGN_PFX            path to the .pfx
#     LIGHTWEAVER_SIGN_PFX_PASSWORD   passed to signtool; see the exposure note above
#
# TIMESTAMPING IS NOT OPTIONAL here. An un-timestamped signature dies with the certificate; a
# timestamped one keeps verifying after it expires. Override the server with
# LIGHTWEAVER_SIGN_TIMESTAMP_URL if a CA requires its own.
param(
    [Parameter(Mandatory = $true)][string[]]$Path,
    [switch]$Require
)
$ErrorActionPreference = 'Stop'

# Honoured from the environment as well as the switch: the operator runs publish.ps1 / installer.ps1
# / release.ps1, never this script directly, so a switch alone is unreachable without a passthrough
# parameter on all three.
$required = $Require -or ($env:LIGHTWEAVER_SIGN_REQUIRE -eq '1')

$mode = $env:LIGHTWEAVER_SIGN_MODE
if (-not $mode) {
    if ($required) {
        Write-Error 'Signing is REQUIRED (LIGHTWEAVER_SIGN_REQUIRE=1) but LIGHTWEAVER_SIGN_MODE is not set - see docs/CODE-SIGNING.md.'
    }
    Write-Host 'Signing: SKIPPED (LIGHTWEAVER_SIGN_MODE not set). Output is unsigned; Windows will warn users about an unknown publisher. See docs/CODE-SIGNING.md.'
    return
}

# signtool ships with the Windows SDK and is not on PATH by default.
#
# Sort by the SDK version PARSED OUT OF THE PATH, not by the path string. The legacy versionless
# layout 'Windows Kits\10\bin\x64\signtool.exe', left behind by old SDKs, string-sorts ABOVE every
# 'bin\10.0.NNNNN.0\x64' path when descending ('x' > '1'), so a plain sort hands the oldest binary
# to a machine carrying that remnant. An old signtool rejects /dlib outright, so the failure is loud
# rather than silent - but "newest wins" has to actually be true.
$signtool = Get-ChildItem -Path @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    ) -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match '\\x64\\' } |
    Sort-Object -Property @{ Expression = {
        if ($_.FullName -match '\\bin\\(\d+(?:\.\d+){1,3})\\') { [version]$Matches[1] } else { [version]'0.0.0.0' }
    } } -Descending |
    Select-Object -First 1 -ExpandProperty FullName
if (-not $signtool) {
    # PATH last, not first: whatever is on PATH is unversioned and unknowable.
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty Source
}
if (-not $signtool) {
    Write-Error 'signtool.exe not found. Install the Windows SDK signing tools: winget install -e --id Microsoft.WindowsSDK.SigningTools'
}

# PowerShell 5.1 turns a native executable's stderr into an ErrorRecord, and under
# $ErrorActionPreference='Stop' that is a TERMINATING error - so signtool merely PRINTING its
# failure would abort this script before the exit-code checks below could interpret it. Measured:
# with stderr redirected, the untrusted-root branch never ran at all and the run died with
# NativeCommandError instead. Whether that happens depends on how the CALLER redirects output,
# which is exactly the kind of dependency a release script must not have.
#
# Out-Host, not a bare call: signtool's stdout would otherwise join this function's OUTPUT stream
# and be returned alongside the exit code, so the caller receives an array of log lines instead of a
# number and every `-ne 0` check silently misreads it. Measured - the first version reported
# "signtool sign failed with exit code <the entire signtool transcript> 0" on a successful sign.
#
# Exit codes are the contract with signtool, so run it under 'Continue' and judge $LASTEXITCODE.
function Invoke-SignTool {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try {
        & $signtool @Arguments | Out-Host
        return $LASTEXITCODE
    }
    finally { $ErrorActionPreference = $previous }
}

# The third member of the native-command family this script keeps running into, and the one that
# only appeared once self-signing became permanent: A NATIVE EXIT CODE OUTLIVES THE SCRIPT THAT
# DELIBERATELY HANDLED IT.
#
# `signtool verify /pa` exits 1 for a self-signed chain. That is expected and handled above - this
# script returns success - but $LASTEXITCODE is still 1 afterwards, and it propagates out through
# publish.ps1 and installer.ps1 to whoever called them. release.ps1 does
# `& installer.ps1; if ($LASTEXITCODE -ne 0) { Write-Error 'installer.ps1 failed' }`, so a release
# aborted on "installer.ps1 failed" with a perfectly good, freshly signed setup.exe sitting in dist\.
#
# Measured 2026-08-07 while cutting 0.6.0. Clearing it on the success paths keeps this script's
# contract honest: it throws on failure and returns on success, so a caller that judges by exit code
# must not be handed a stale 1 from a check we already decided was fine.
function Reset-NativeExitCode {
    $global:LASTEXITCODE = 0
}

$timestamp = if ($env:LIGHTWEAVER_SIGN_TIMESTAMP_URL) { $env:LIGHTWEAVER_SIGN_TIMESTAMP_URL }
             elseif ($mode -eq 'artifact-signing') { 'http://timestamp.acs.microsoft.com' }
             else { 'http://timestamp.digicert.com' }

# SHA256 for both the file digest (/fd) and the timestamp digest (/td). SHA1 is not accepted by
# current Windows trust policy, and omitting /td silently timestamps with SHA1.
$common = @('sign', '/fd', 'SHA256', '/tr', $timestamp, '/td', 'SHA256', '/v')

switch ($mode) {
    'artifact-signing' {
        if (-not $env:LIGHTWEAVER_SIGN_DLIB -or -not (Test-Path -LiteralPath $env:LIGHTWEAVER_SIGN_DLIB)) {
            Write-Error "LIGHTWEAVER_SIGN_DLIB is not set or does not exist: '$env:LIGHTWEAVER_SIGN_DLIB'"
        }
        if (-not $env:LIGHTWEAVER_SIGN_METADATA -or -not (Test-Path -LiteralPath $env:LIGHTWEAVER_SIGN_METADATA)) {
            Write-Error "LIGHTWEAVER_SIGN_METADATA is not set or does not exist: '$env:LIGHTWEAVER_SIGN_METADATA'"
        }
        $modeArgs = @('/dlib', $env:LIGHTWEAVER_SIGN_DLIB, '/dmdf', $env:LIGHTWEAVER_SIGN_METADATA)
    }
    'certstore' {
        if (-not $env:LIGHTWEAVER_SIGN_THUMBPRINT) { Write-Error 'LIGHTWEAVER_SIGN_THUMBPRINT is not set.' }
        $modeArgs = @('/sha1', ($env:LIGHTWEAVER_SIGN_THUMBPRINT -replace '\s', ''))
    }
    'pfx' {
        if (-not $env:LIGHTWEAVER_SIGN_PFX -or -not (Test-Path -LiteralPath $env:LIGHTWEAVER_SIGN_PFX)) {
            Write-Error "LIGHTWEAVER_SIGN_PFX is not set or does not exist: '$env:LIGHTWEAVER_SIGN_PFX'"
        }
        $modeArgs = @('/f', $env:LIGHTWEAVER_SIGN_PFX)
        if ($env:LIGHTWEAVER_SIGN_PFX_PASSWORD) {
            # Note the exposure caveat in the header. Also: PS 5.1's native-argument quoting mangles
            # a value containing a double quote or a trailing backslash, which surfaces as signtool
            # reporting a wrong password. One more reason certstore is the mode for a real cert.
            $modeArgs += @('/p', $env:LIGHTWEAVER_SIGN_PFX_PASSWORD)
        }
    }
    default { Write-Error "Unknown LIGHTWEAVER_SIGN_MODE '$mode' - expected artifact-signing, certstore or pfx." }
}

# -LiteralPath throughout: a repo path containing [ or ] is a wildcard to Test-Path/Resolve-Path and
# would fail as the misleading "Nothing to sign".
$files = @()
foreach ($p in $Path) {
    if (-not (Test-Path -LiteralPath $p)) { Write-Error "Nothing to sign at '$p'." }
    $files += (Resolve-Path -LiteralPath $p).Path
}

Write-Host "Signing $($files.Count) file(s) in '$mode' mode, timestamped by $timestamp"

# Retry the sign step. The single likeliest cause of a failure here is a momentary timestamp-server
# hiccup, and losing a three-minute release build to one is not worth it. Signing is idempotent - a
# re-sign replaces the signature - so a retry cannot compound. A genuine misconfiguration still
# fails, just three times first.
$maxAttempts = 3
for ($attempt = 1; $attempt -le $maxAttempts; $attempt++) {
    $signExit = Invoke-SignTool -Arguments ($common + $modeArgs + $files)
    if ($signExit -eq 0) { break }
    if ($attempt -lt $maxAttempts) {
        Write-Warning "signtool sign failed (exit $signExit) on attempt $attempt of $maxAttempts; retrying in 5s. A flaky timestamp server is the usual cause."
        Start-Sleep -Seconds 5
    }
}
if ($signExit -ne 0) { Write-Error "signtool sign failed with exit code $signExit after $maxAttempts attempt(s)." }

# Verify rather than trust the signing exit code. Two checks, because they answer different
# questions and the second is what a user's Windows will actually do.
#
# 1. Is there a signature AND a timestamp on the file at all? An un-timestamped signature is the
#    silent failure worth guarding: it signs, it verifies today, and it stops verifying the day the
#    certificate expires.
foreach ($f in $files) {
    $sig = Get-AuthenticodeSignature -LiteralPath $f
    if (-not $sig.SignerCertificate) { Write-Error "No signature landed on $f" }
    if (-not $sig.TimeStamperCertificate) {
        Write-Error "$f is signed but NOT timestamped - the signature would die with the certificate. Check that /tr reached $timestamp."
    }
    Write-Host "  $(Split-Path $f -Leaf): status=$($sig.Status), signer=$($sig.SignerCertificate.Subject), timestamped=yes"
}

# 2. Does it satisfy the Authenticode policy? /pa is required: without it signtool validates against
#    the DRIVER policy and can pass a file Windows will refuse to run cleanly.
$verifyExit = Invoke-SignTool -Arguments (@('verify', '/pa', '/v') + $files)
if ($verifyExit -ne 0) {
    # A self-signed certificate cannot pass this, by definition - nothing trusts it as a root. That
    # is allowed ONLY when explicitly opted in.
    #
    # Since 2026-08-07 self-signing is the STANDING DECISION for this project rather than a test
    # scaffold (no certificate will be bought - see docs\CODE-SIGNING.md), so this branch is the
    # normal path for every release, not an escape hatch. The opt-in stays explicit anyway: it must
    # remain impossible to ship an unverifiable signature by forgetting something.
    #
    # Be honest about the blast radius: Get-AuthenticodeSignature funnels most chain and policy
    # failures into UnknownError - not only an untrusted root but also a revoked certificate, a
    # wrong-EKU certificate, or one expired at signing time. So this excuses any CHAIN-TRUST
    # failure, not solely a self-signed root. What it never excuses is a missing or tampered
    # signature: HashMismatch and NotSigned fail hard, as does any mixture (verified per status),
    # and the loop above has already proved a signature and timestamp are present.
    $allowed = $env:LIGHTWEAVER_SIGN_ALLOW_UNTRUSTED_ROOT -eq '1'
    $statuses = @($files | ForEach-Object { [string](Get-AuthenticodeSignature -LiteralPath $_).Status })
    $onlyUntrusted = @($statuses | Where-Object { $_ -ne 'UnknownError' -and $_ -ne 'NotTrusted' }).Count -eq 0
    if ($allowed -and $onlyUntrusted) {
        Write-Warning 'Signature does not chain to a trusted root - expected for a self-signed certificate, and allowed because LIGHTWEAVER_SIGN_ALLOW_UNTRUSTED_ROOT=1.'
        Write-Warning 'WHAT THIS DOES AND DOES NOT BUY: the file is signed and timestamped, so it is tamper-evident and carries a stable publisher identity. It does NOT satisfy SmartScreen for anyone else - on a machine that does not trust this certificate the signature reads as INVALID rather than absent, which can present worse than no signature. Import the public certificate into that machine''s trusted roots to get a valid signature there.'
        Write-Host 'Signing: OK (signed and timestamped; chain untrusted by design)'
        Reset-NativeExitCode
        return
    }
    Write-Error "signtool verify /pa failed with exit code $verifyExit (statuses: $($statuses -join ', ')) - the files were signed but do not validate."
}

Write-Host 'Signing: OK (signed, timestamped and verified against the Authenticode policy)'
Reset-NativeExitCode
