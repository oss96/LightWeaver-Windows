# Technical notes

## Localization

`localization/catalog.json` maps stable keys to English `text`, a translator `context`, and
`sources`. Reuse a key when its English wording changes. Context distinguishes identical text
used for different purposes. Sources identify the UI call sites, not protocol or log strings.

`localization/locales/<culture>.json` contains `language`, `strings`, and `sourceHashes`.
Each translated key carries the SHA-256 hash of its English source. The editor writes the
current hash after validating the translation. A changed source makes older translations stale.
Blank cells remove the translation and restore English fallback.
The German `de` and Arabic `ar` packs each translate all 812 current keys with matching source
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

## Translation editor

`tools/TranslationEditor` is an ASP.NET Core app bound to IPv4 loopback on port 5386. Its static
UI loads the catalog and language columns, supports URL filters, and saves patches for edited
cells. Translation files stay in the checkout and are included in subsequent app builds.

Writes require JSON and the editor's custom header, with same-origin checks when Origin is
present. Language codes are validated against .NET cultures. Saves validate the file revision,
source hashes and composite placeholders before replacing a language file atomically. On a
conflict, the browser retains drafts for review. The changed filter compares the current catalog
and translations with a validated Git commit. `working-tree` means HEAD; use a full base commit
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
