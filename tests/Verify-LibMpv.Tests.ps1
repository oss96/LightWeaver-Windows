# Packaging verification only: no DLL is loaded and no Windows UI is opened.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$verify = Join-Path $root 'tools\Verify-LibMpv.ps1'
$library = Join-Path $root 'native\win-x64\libmpv-2.dll'
$manifestPath = Join-Path $root 'dependencies\libmpv.json'
$fixture = Join-Path ([IO.Path]::GetTempPath()) ('lw-libmpv-verifier-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture | Out-Null
$checks = 0

function Assert-Rejected {
    param([scriptblock]$Action, [string]$ExpectedMessage)
    try {
        & $Action
    } catch {
        if ($_.Exception.Message -like "*$ExpectedMessage*") {
            Write-Host "PASS: rejects $ExpectedMessage"
            return
        }
        throw
    }
    throw "Expected verifier to reject $ExpectedMessage."
}

try {
    & $verify -LibraryPath $library -ManifestPath $manifestPath
    Write-Host 'PASS: accepts the recorded library'
    $checks++

    Assert-Rejected { & $verify -LibraryPath (Join-Path $fixture 'missing.dll') } 'libmpv-2.dll is missing'
    $checks++

    $wrongLibrary = Join-Path $fixture 'wrong.dll'
    [IO.File]::WriteAllBytes($wrongLibrary, [byte[]]@(0, 1, 2, 3))
    Assert-Rejected { & $verify -LibraryPath $wrongLibrary } 'SHA-256 does not match'
    $checks++

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $wrongManifest = Join-Path $fixture 'manifest.json'
    $manifest.version = 'wrong-version'
    [IO.File]::WriteAllText($wrongManifest, ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Assert-Rejected { & $verify -LibraryPath $library -ManifestPath $wrongManifest } 'file version does not match'
    $checks++

    $manifest.sha256 = 'not-a-sha256'
    [IO.File]::WriteAllText($wrongManifest, ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Assert-Rejected { & $verify -LibraryPath $library -ManifestPath $wrongManifest } 'Invalid libmpv manifest'
    $checks++

    $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $manifest.version = ''
    [IO.File]::WriteAllText($wrongManifest, ($manifest | ConvertTo-Json), [Text.UTF8Encoding]::new($false))
    Assert-Rejected { & $verify -LibraryPath $library -ManifestPath $wrongManifest } 'Invalid libmpv manifest'
    $checks++

    # Publishing must fail before deleting an existing output or starting dotnet/signing.
    $publishFixture = Join-Path $fixture 'publisher'
    $fixtureTools = Join-Path $publishFixture 'tools'
    $fixtureOutput = Join-Path $publishFixture 'dist\publish'
    New-Item -ItemType Directory -Path $fixtureTools, $fixtureOutput | Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'publish.ps1') -Destination $publishFixture
    Copy-Item -LiteralPath $verify -Destination $fixtureTools
    $sentinel = Join-Path $fixtureOutput 'keep.txt'
    [IO.File]::WriteAllText($sentinel, 'existing output')
    Assert-Rejected { & (Join-Path $publishFixture 'publish.ps1') } 'libmpv-2.dll is missing'
    $checks++
    if (-not (Test-Path -LiteralPath $sentinel)) { throw 'Publishing deleted its output before verification.' }
    Write-Host 'PASS: rejected publish preserves existing output'
    $checks++

    Write-Host "RESULT: PASS ($checks libmpv verification checks)"
} finally {
    $resolvedFixture = [IO.Path]::GetFullPath($fixture)
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolvedFixture.StartsWith($temporaryRoot, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Fixture cleanup escaped the temporary directory.'
    }
    Remove-Item -LiteralPath $resolvedFixture -Recurse -Force
}
