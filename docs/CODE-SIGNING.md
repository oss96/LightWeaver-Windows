# Code signing LightWeaver

Written 2026-08-06, for someone who has never signed a Windows application before.

## What signing actually buys you

An unsigned installer makes Windows show **"Windows protected your PC — unknown publisher"**
(SmartScreen), and Defender treats an unknown binary with more suspicion. A valid Authenticode
signature replaces "unknown publisher" with your verified name.

**Set your expectations on one point:** a signature is not an instant fix. SmartScreen also weighs
*reputation*, which accrues per signing identity as your files are downloaded and found harmless. A
brand-new certificate can still trigger a warning for a while — the difference is that the warning
now names you, and it fades as reputation builds. Historically only EV certificates bypassed this
immediately, and even that is no longer a guarantee. So sign because it is correct and because
reputation cannot start accruing until you do, not because the first signed build will be
warning-free.

## Current state

**Decision (2026-08-07): self-signed, permanently.** No code-signing certificate will be bought, so
every release from 0.6.0 onwards is signed with a self-signed certificate held in this machine's
user store. The configuration is now persistent, not per-release:

| Variable | Value |
| --- | --- |
| `LIGHTWEAVER_SIGN_MODE` | `certstore` |
| `LIGHTWEAVER_SIGN_THUMBPRINT` | the self-signed cert's SHA1, set at **User** scope |
| `LIGHTWEAVER_SIGN_REQUIRE` | `1` — a release now **fails** rather than silently shipping unsigned |
| `LIGHTWEAVER_SIGN_ALLOW_UNTRUSTED_ROOT` | `1` — **required**, see below |

`ALLOW_UNTRUSTED_ROOT` is not optional with this decision, and that is worth understanding rather
than just setting. `sign.ps1` verifies its own work with `signtool verify /pa`, which enforces the
Authenticode policy and therefore **cannot pass a self-signed certificate on a machine that does not
trust it** — it returns `CERT_E_UNTRUSTEDROOT` and the build stops. Measured while cutting 0.6.0: the
first attempt signed and timestamped both binaries perfectly and then failed the release on exactly
that. The opt-in stays explicit on purpose, so it is impossible to ship an unverifiable signature by
forgetting something; but with self-signing permanent, this branch is the normal path for every
release rather than an escape hatch. Note what it still refuses: `HashMismatch` and `NotSigned` fail
hard, so a missing or tampered signature can never be waved through.

The certificate is `CN=Oss Alali, O=LightWeaver (self-signed)`, RSA 3072 / SHA-256, created
2026-08-07 and valid to **2031-08-07**, in `Cert:\CurrentUser\My`. Recreate it with
`New-SelfSignedCertificate -Type CodeSigningCert` and update the thumbprint variable; nothing in the
repo needs changing, and the thumbprint is not a secret.

`certstore` mode is used rather than `pfx` deliberately, even for a self-signed key: `pfx` passes the
password to signtool as a **command-line argument**, so it is readable by any process running as this
user and written permanently into the event log by process-creation auditing. `certstore` has no
password for the script to handle at all.

**Be honest about what this buys, because it is narrow.** A self-signed signature does **not** silence
SmartScreen for anyone else — reputation attaches to a certificate chaining to a trusted root, and
this one chains to nothing on someone else's machine, where the signature therefore reads as *invalid*
("terminated in a root certificate which is not trusted") rather than merely absent. That can present
worse than no signature at all. What it does buy, on machines that trust the certificate: a signature
that validates, tamper-evidence, and a stable publisher identity. For a personal Jellyfin client the
author installs himself, that is the whole intended benefit.

To get that benefit on a machine, the certificate must be trusted there — export the **public**
certificate (never the private key) and import it into `Cert:\CurrentUser\Root`. Understand the
implication before doing it: that makes this key an authority for that user, so anything signed with
it will be trusted by them.

Nothing is required to build a release on a machine *without* the certificate — with no configuration
`sign.ps1` prints one line and does nothing — but with `LIGHTWEAVER_SIGN_REQUIRE=1` set here, a
misconfiguration on this machine now fails loudly instead.

## The self-updater treats the signer as the update identity

The automatic updater does not treat HTTPS or a release-page checksum as sufficient authority to
execute a downloaded installer. Immediately after staging and again immediately before launch it
validates the Authenticode digest, timestamp and code-signing EKU, then requires the installer
certificate thumbprint to match the certificate embedded in the currently running
`LightWeaver.exe`. This turns the already-running signed release into the trust anchor.

The configured certificate is self-signed, so `WinVerifyTrust` returns `CERT_E_UNTRUSTEDROOT` even
for an intact release. The updater accepts only that exact chain result, and only after the digest,
timestamp, EKU and matching-signer checks succeed. It does not accept `HashMismatch`, `NotSigned`,
a wrong signer, or a general `UnknownError` shortcut.

Certificate rotation therefore needs a bridge release: an older updater cannot trust a package
signed only by a new identity. Ship a release that teaches the updater the replacement identity
while it is still signed by the old one, or require that transition to be installed manually. The
current certificate is valid through 2031, so this is a planned operational constraint rather than
an immediate release action.

| Piece | Where |
| --- | --- |
| The signing step | `sign.ps1` (repo root) |
| App binaries signed | `publish.ps1` — `LightWeaver.exe` + `LightWeaver.dll`, **before** the zip and before Inno reads the folder |
| Installer signed | `installer.ps1` — `dist\LightWeaver-<version>-setup.exe`, **after** ISCC |

Only our own two binaries are signed. The .NET runtime files are already signed by Microsoft, and
signing a third party's `libmpv-2.dll` with our certificate would assert authorship we do not have —
verified: it stays `NotSigned` through a full run.

**Known gap:** the uninstaller Inno extracts on the user's machine (`unins000.exe`) is **not**
signed. Signing `setup.exe` after ISCC cannot reach a binary that ISCC embeds, so closing this needs
Inno's own `SignTool=` directive wired to call `sign.ps1` during compilation. Some AV products flag
an unsigned uninstaller, so it is worth doing when a real certificate arrives.

## Step 1 — get a certificate

### Option A: an OV certificate from a CA — the realistic route from Germany

Sectigo, DigiCert, GlobalSign and similar, roughly **$200–400/year**.

Since June 2023 the CA/Browser Forum rules require the private key to live on **hardware**, so you
either get a USB token in the post or rent a cloud HSM. That is more friction and more to lose, but
it is a certificate you own outright and can use anywhere, and — unlike Option B — it is actually
available to an EU individual or company today.

A token presents itself as a certificate in your user store, so:

```powershell
$env:LIGHTWEAVER_SIGN_MODE       = 'certstore'
$env:LIGHTWEAVER_SIGN_THUMBPRINT = '<SHA1 thumbprint, no spaces>'
```

Find it with `Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert | Format-List Subject, Thumbprint`.

Use `certstore`, never `pfx` — see the warning under Option C.

EV certificates cost more again. Given the reputation caveat above, the extra spend is hard to
justify for this project.

### Option B: Azure Trusted Signing / Artifact Signing — probably not available to you

Microsoft's managed signing service, being rebranded from **Azure Trusted Signing** to **Azure
Artifact Signing**. It is the nicest option on paper: around **$9.99/month** (Basic, up to 5,000
signatures), **no hardware token**, the private key never touches your machine, and certificates are
short-lived and rotated for you.

**But check eligibility before planning around it, because it very likely excludes Germany.**
Microsoft's own documentation says access is limited to organizations in the **USA and Canada** with
a verifiable operating history of **at least three years**, that individual developers must likewise
be in the **US or Canada**, and that **individual onboarding has been paused**. There is a Microsoft
Q&A thread titled *"Can't create a new Trusted Signing Individual identity because my country is not
available"*, which is the symptom you would hit.

A correction worth recording, since it is the reason this section was rewritten: secondary sources
(vendor comparison sites and a 2026 news article) claim eligibility extends to EU and UK businesses
and self-employed individuals. Microsoft's own docs contradict them. **Trust the Microsoft pages
linked at the bottom, not the summaries** — this doc initially recommended the service on the
strength of those summaries and was wrong.

A second foot-gun if it ever does open up: granting yourself the signing role has repeatedly led
people into needing a **Microsoft Entra ID P2** licence, a separate cost the $9.99 plan does not
include.

If it becomes available, set:

```powershell
$env:LIGHTWEAVER_SIGN_MODE     = 'artifact-signing'
$env:LIGHTWEAVER_SIGN_DLIB     = 'C:\path\to\Azure.CodeSigning.Dlib.dll'
$env:LIGHTWEAVER_SIGN_METADATA = 'C:\path\to\metadata.json'   # endpoint, account, cert profile
```

### Option C: self-signed (what is configured today)

Proves the plumbing, helps no user. Created with:

```powershell
New-SelfSignedCertificate -Type CodeSigningCert `
  -Subject 'CN=LightWeaver Dev (self-signed, not for distribution)' `
  -CertStoreLocation Cert:\CurrentUser\My `
  -KeyExportPolicy NonExportable -KeyUsage DigitalSignature `
  -NotAfter (Get-Date).AddYears(3)
```

Used via `certstore` mode plus **`LIGHTWEAVER_SIGN_ALLOW_UNTRUSTED_ROOT=1`**, because a self-signed
certificate cannot chain to a trusted root and `signtool verify /pa` correctly refuses it.

Be precise about what that variable excuses, because it is broader than its name suggests.
`Get-AuthenticodeSignature` funnels most chain and policy failures into `UnknownError` — not only an
untrusted root but also a **revoked** certificate, a **wrong-EKU** certificate, or one that was
**expired when it signed**. So it excuses any *chain-trust* failure. What it never excuses is a
missing or tampered signature: `HashMismatch` and `NotSigned` fail hard, as does any mixture
(verified per status), and a signature and timestamp must already be present before the check is
reached.

**Never set it for a real release.** Its whole purpose is to make "this is not distributable" loud
rather than silent.

> **Why not `pfx` mode, even for testing?** signtool takes the password as a **command-line
> argument**. While it runs, any process running as you can read it (`Get-CimInstance
> Win32_Process`, Task Manager's command-line column, Process Explorer), and on a machine with
> process-creation auditing (event 4688) or Sysmon the **entire command line, password included, is
> written permanently into an event log** — which is a file, on a corporate baseline, forever. This
> script never prints or stores the password, but that is not the same as it being unobservable. Use
> `certstore`, where there is no password to pass at all. `pfx` mode exists only as a fallback for a
> certificate that cannot be imported.

To remove the test certificate later:
`Get-ChildItem Cert:\CurrentUser\My | Where-Object Subject -like '*LightWeaver Dev*' | Remove-Item`

## Step 2 — sign a release

```powershell
$env:LIGHTWEAVER_SIGN_MODE = 'artifact-signing'   # plus that mode's variables
.\installer.ps1
```

Set the variables in the shell you run the release from — **never commit them**.

**Set `LIGHTWEAVER_SIGN_REQUIRE=1` for a real release.** Without it, a mistyped or forgotten variable
just prints `Signing: SKIPPED` — two lines that scroll past in a three-minute build — and you publish
an unsigned release believing it was signed. With it, the build stops. The environment variable
exists because you run `publish.ps1` / `installer.ps1` / `release.ps1`, never `sign.ps1` directly, so
its `-Require` switch alone would be unreachable.

Note that `release.ps1 -SkipBuild` uploads whatever `dist\*-setup.exe` already exists and checks no
signature at all, so it can publish a stale unsigned build. Prefer a full run for a real release.

## Timestamping is not optional

Every signature here is timestamped (`/tr` + `/td SHA256`). Without it the signature **stops
verifying the day the certificate expires** — with it, it stays valid indefinitely, because the
timestamp proves the signature predated expiry. This matters more with Azure Artifact Signing, whose
certificates are deliberately short-lived. `sign.ps1` treats a missing timestamp as a hard error
rather than trusting that `/tr` reached the server; the default server is
`http://timestamp.acs.microsoft.com` for `artifact-signing` and `http://timestamp.digicert.com`
otherwise, overridable with `LIGHTWEAVER_SIGN_TIMESTAMP_URL`.

## Verifying by hand

```powershell
Get-AuthenticodeSignature .\dist\LightWeaver-0.5.0-setup.exe | Format-List Status, SignerCertificate, TimeStamperCertificate
signtool verify /pa /v .\dist\LightWeaver-0.5.0-setup.exe
```

`Status` of `Valid` is what a real certificate gives. `UnknownError` is what a self-signed chain
reports — not `NotTrusted`, which is the intuitive guess and is why `sign.ps1` accepts both.

`/pa` is required: without it signtool validates against the **driver** signing policy and can pass a
file Windows will still refuse to run cleanly.

## Two PowerShell traps this cost, both worth knowing

1. **`$ErrorActionPreference = 'Stop'` plus a native exe's stderr is a terminating error in PS 5.1.**
   signtool merely *printing* its failure aborted the script before the exit-code handling could
   interpret it — and whether it happened depended on how the *caller* redirected output. Native
   calls now run under `Continue` and are judged solely by `$LASTEXITCODE`.
2. **A native command's stdout joins its enclosing function's output stream.** The first
   `Invoke-SignTool` returned the entire signtool transcript *plus* the exit code as an array, so
   `-ne 0` misread it and a successful sign reported "failed with exit code <transcript> 0". Piping
   through `Out-Host` keeps the log visible and the return value a number.

## Sources

Authoritative — these are the ones to believe on eligibility:

- [Artifact Signing FAQ (Microsoft Learn)](https://learn.microsoft.com/en-us/azure/artifact-signing/faq)
- [Quickstart: Set up Artifact Signing](https://learn.microsoft.com/en-us/azure/artifact-signing/quickstart)
- ["Can't create a new Trusted Signing Individual identity because my country is not available"](https://learn.microsoft.com/en-nz/answers/questions/5810735/cant-create-a-new-trusted-signing-individual-ident)
- ["Can't Create an Individual Identity Validation"](https://learn.microsoft.com/en-us/answers/questions/2263177/azure-trusted-signing-cant-create-an-individual-id)
- [Trusted Signing role assignment / Entra ID P2 question](https://learn.microsoft.com/en-us/answers/questions/5595324/i-signed-up-to-generate-certificates-to-sign-my-co)

Secondary, and **contradicted above on eligibility** — kept so the disagreement is on the record
rather than rediscovered:

- [Trusted Signing is now open for individual developers (public preview announcement)](https://techcommunity.microsoft.com/blog/microsoft-security-blog/trusted-signing-is-now-open-for-individual-developers-to-sign-up-in-public-previ/4273554)
- [Code signing Windows apps may be easier with the new Azure Artifact service](https://www.devclass.com/security/2026/01/14/code-signing-windows-apps-may-be-easier-and-more-secure-with-new-azure-artifact-service/4079554)
- [Azure Trusted Signing vs a code signing certificate](https://my-ssl.com/learn/azure-trusted-signing-vs-code-signing-certificate)
