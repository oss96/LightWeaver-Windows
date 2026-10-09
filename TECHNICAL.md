# Technical notes

## Jellyfin 12 compatibility

Playlist detail uses `/Playlists/{id}/Items`, preserving every occurrence in server order.
`GetPlaylistItemsAsync` pages the endpoint until its reported total is reached. The view uses
an ordered list cache under `playlist-entries`, shared by neither the folder prefetch nor its
id-keyed diff/merge. Queue state identifies the playing occurrence by index. Watched updates
replace every matching occurrence in the visible grid.

Playback negotiation selects an explicitly requested media-source id from the response, even
if another source is listed first. An absent selected source returns no decision and tears
down the unused session. With no requested version, the server's first source stays the default.

The device profile declares PGS/VobSub as Embed. This allows embedded graphical subtitles in
the original stream; it does not advertise the External graphical-subtitle path added by
[Jellyfin #17512](https://github.com/jellyfin/jellyfin/pull/17512). Forced HLS playback can burn
them in, with `DeliveryMethod=Encode` recorded in the decision so mpv's copy is suppressed.
External SRT files use `subrip`, which remains declared in the profile. Unlimited playback
sends an explicit 1 Gbps cap instead of omitting the bitrate.

The client requests `SortName`, while [Jellyfin #17503](https://github.com/jellyfin/jellyfin/pull/17503)
changes `ItemSortBy.Name` to CleanName. Keep the existing SortName cache key/comparer unless
a measured server/client mismatch requires a change. An empty Similar response collapses
its row and skeleton; no empty heading remains.

`PlaybackHarness --quality-contract` covers reordered/default/missing media sources,
occurrence-preserving playlist paging, and queue position with repeated ids. The windowless
`--jellyfin12-probe` measures server responses as TEST and creates/deletes one private test
playlist. Missing prerequisites return an incomplete result, never a pass. The guest-only
`lw-jellyfin12` suite runs production playlist/Similar views with synthetic HTTP plus actual
TEST PGS playback. Multi-source server data is not currently available in the scanned library;
the adversarial multi-source responses are exercised through the production SDK contract test.

## Localization

`localization/catalog.json` maps stable keys to English `text`, a translator `context`, and
`sources`. Reuse a key when its English wording changes. Context distinguishes identical text
used for different purposes. Sources identify the UI call sites, not protocol or log strings.

`localization/locales/<culture>.json` contains `language`, `strings`, and `sourceHashes`.
Each translated key carries the SHA-256 hash of its English source. The editor writes the
current hash after validating the translation. A changed source makes older translations stale.
Blank cells remove the translation and restore English fallback.
The German `de` and Arabic `ar` packs each translate all 970 current keys with matching source
hashes and preserved format placeholders. German keyboard names include `Pos1` and `Entf`.

The shared `src/LightWeaver.Localization` project owns the file schema and placeholder rules.
The WPF app embeds the catalog and locale files. `Localization.L` selects a saved UI culture
at startup, tries its locale and parent locales, then falls back to English per string.
Malformed packs are ignored. `L.Text(key)` returns plain text; `L.Format(key, args)` formats
using the selected UI culture; `{loc:Text Key}` supplies XAML text. Machine parsing keeps
`CurrentCulture`. UI language selection saves `AppSettings.UiLanguage` and updates the existing interface immediately.
`L.Source` notifies live XAML bindings, explicit code-created label bindings, and weak view/model
observers. Cached display bindings are refreshed without editing user inputs, resetting filters,
reloading navigation, or issuing playback commands. Choice lists retain their invariant values.
System default uses the Windows UI culture captured before explicit language selection.
Embedded pack edits still require a rebuild; switching between embedded languages requires no restart.

`LibraryNames` resolves client-only aliases from `AppSettings.LibraryNameTranslations`, scoped
by server URL, library ID and culture. Parent cultures supply aliases when a regional alias is
absent. Conventional English names have reviewed catalog translations; custom names fall back
to their original spelling. Only roots returned by UserViews carry `LibraryNameServer`, which
is excluded from JSON along with `LibraryDisplayName`. Rail labels/tooltips, Home library cards
and navigation title providers use the display name without changing `MediaItem.Name`, IDs,
sorting or automation IDs. Settings edits persist on Save names, Enter, language change or
reopening the page. Saving names broadcasts the existing display refresh without a network call.

Display labels must never double as lookup identifiers or persisted values. Media-info fields
and library states use fixed identifiers; localization happens when displaying them. Search
groups also use invariant category ranks, so identical translated headings keep separate sections.
Groups stack vertically, with wrapping headers and wrapping cards inside each group.
The SyncPlay flyout scrolls its available-group and creation controls while keeping Close fixed.
Joined-group details scroll separately with Leave fixed above Close, including long group names
and participant lists at smaller sizes.
mpv still owns its D3D11 child window. Localization does not change the video rendering architecture.

## Settings controls

`LwDropdown` forwards `ItemTemplateSelector` to its selected-content presenter so
`DisplayMemberPath` applies to both menu items and the closed combobox. Quality and font
rows keep their saved values separate from localized labels.

The bitrate picker saves `0` for Unlimited and offers presets plus a validated custom
integer from 1 to 10000 Mbps. Language changes preserve the selected preset and any custom
draft. The default subtitle font remains an empty saved value, which mpv resolves to
`sans-serif`. The download folder shows `DownloadStore.DefaultDirectory` when no override
is saved; unrelated settings changes keep that empty sentinel rather than saving a Debug path.

`DisplayBrightness` enumerates DXGI adapters and outputs and queries `IDXGIOutput6.GetDesc1`
for the monitor containing the Settings window. Only an attached HDR output with finite peak
luminance from 1 to 10000 nits supplies a value. Detection is explicit, creates no rendering
device, releases every COM reference, and preserves the manual setting when unavailable.
`MaxLuminance` is the display's reported small-area peak (EDID or override), not a measurement
or full-screen brightness. The native positive HDR path requires an HDR display for verification.

Settings reuse `GpuCapabilities` for RTX availability and refresh DXGI display information
off the UI thread every two seconds while loaded. A confirmed SDR output disables RTX HDR
and its brightness row; unavailable luminance disables Use display value. Unknown detection
does not invent an unsupported-device verdict. The existing WASAPI probe disables passthrough
on confirmed incompatible endpoints; an enabled saved preference can still be switched off.
Capability updates change enabled state and localized tooltips without saving settings.
Passthrough and normalization remain mutually exclusive, and the countdown follows auto-play.

Subtitle colors display White/Black defaults, named colors and Custom with an explicit hex
field and a swatch. Empty saved color sentinels remain the mpv defaults. Numeric subtitle
presets retain arbitrary saved values as Custom; border size accepts 0–20, shadow offset
accepts -20–20 (including fractional values), and position accepts whole values from 0–150.
Invalid drafts show a localized error and preserve the last saved value across language changes.

## Translation editor

`tools/TranslationEditor` is an ASP.NET Core app bound to IPv4 loopback on port 5386. Its static
UI loads the catalog and language columns, supports URL filters, and saves patches for edited
cells. Translation files stay in the checkout and are included in subsequent app builds.
The editor interface uses the same catalog and reviewed English/German/Arabic packs. `ui=ar`
selects an Arabic interface separately from `lang=ar`, which filters translation columns.
The interface language persists locally; switching it preserves filters and unsaved drafts.
Arabic fields use RTL even under an English interface, while source text and invariant keys
retain LTR direction. The localization audit checks the editor's JavaScript and HTML key references.

Writes require JSON and the editor's custom header, with same-origin checks when Origin is
present. Language codes are validated against .NET cultures. Saves validate the file revision,
source hashes and composite placeholders before replacing a language file atomically. On a
conflict, the browser retains drafts for review. The changed filter compares the current catalog
and translations with a validated Git commit. Git output is decoded as UTF-8, including when
the editor runs without a console, so Unicode translations remain outside the changed filter
unless their content changed. `working-tree` means HEAD; use a full base commit
for a handoff link that remains useful after committing.
Reload, saving and language creation lock conflicting editor actions until their requests finish.

## Verification

```powershell
dotnet build LightWeaver.slnx -warnaserror
dotnet build tests/PlaybackHarness/PlaybackHarness.csproj -warnaserror
dotnet run --project tests/LocalizationAudit
dotnet run --project tests/LocalizationRuntime
dotnet run --project tests/TranslationEditor.Tests
```

The runtime checks compile the production resolver with synthetic embedded packs without
opening a window. They cover parent-locale selection, culture-specific formatting, unchanged
machine parsing culture, missing/stale/invalid translations and malformed packs. Editor checks
use temporary repositories and language files, not the user's app data.
The source audit checks catalog references, format argument counts, source paths, invariant
call sites and common UI-literal locations. It supplements code review rather than replacing it.

Windows UI checks run on the Hyper-V guest through the `forgejo-vm` MCP route `lw-localization`.
The localization fixture uses synthetic language content and isolated app data; it requires
no Jellyfin login. Browser verification uses the T3 collaborative preview against the local
editor. Builds and automated checks are separate from the user's normal Debug acceptance.
The guest route requires localization-specific assertions, including fresh English and German
processes, restart persistence, repeated live English/German/Arabic changes, unchanged view and
player generations, preservation of invalid input, cached template/track labels, and separate
search categories with identical translated headings.

## Download language and progress integration

The download picker uses live localization bindings for its labels, named choice suffixes and
confirmation. Content updates preserve the existing ComboBoxItem identities and selected indices,
so language switching does not pin an automatic audio choice or enable an incompatible track.
Download rows derive translated language summaries from saved track indices and ordinals; persisted
resolution identifiers, filenames and playback preferences remain invariant. Legacy rows retain
recorded summaries when no structured selection metadata exists. Progress, speed, readiness and
rail activity text use the catalog with German and Arabic translations.
