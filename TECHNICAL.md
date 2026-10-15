# Technical notes

## Saved profiles on the login screen

The login form shows the existing localized profile-switcher link when saved credentials exist.
It routes through the main window's normal profile-switch flow, including cold validation and
warm-session reuse. The switcher only disables the stored active profile when the current
Jellyfin service is connected, so a lone saved profile can be chosen from the login form.
Password sign-in, Quick Connect and profile switching share a UI-thread operation gate.
The login form disables its account controls, both password fields and the show/hide button
while an operation is pending, and the switcher
also checks the gate for menu and keyboard entry points. Quick Connect cancels when its view
unloads, and authentication completions retain their originating Jellyfin service.
The guest localization fixture checks profile visibility, selection, cancellation, reconnect,
warm return, and standard and 700x480 layouts with an isolated synthetic profile store.

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
The German `de` and Arabic `ar` packs each translate all 974 current keys with matching source
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

`SettingsStore.Save` never throws; `SettingsStore.LastSaveFailure` records why the latest save
wrote nothing. `ReadDenied` is set at load when the existing file refuses reads and stays for the
process, because every later save is refused to protect that file. `WriteFailed` covers IO and
access failures on the write and clears on the next successful save. Settings shows a pinned
banner above its scroller from that value after its own saves and whenever the page becomes
visible, so failures from saves made elsewhere (the player's audio-device pick, a show's Up Next
lead) appear on return.

Slider `ValueChanged` updates labels and live limits (image cache bytes, folder cache items,
subtitle scale) on every tick but defers the file write by 500 ms. A thumb drag holds the write
until `DragCompleted`. Any non-slider save writes the pending value with it; leaving the page and
`MainWindow.OnClosing` flush it through `SettingsView.FlushPendingSave`. Cache and log readouts
are re-read whenever the page becomes visible.

Shortcut capture moves a chord with `PlayerActionDispatcher.Rebind`, which unbinds any other
action whose saved chord is `ChordEquals` to the new one, so Key-enum alias spellings and modifier
order cannot leave two actions on one key.

## UI zoom

`Theme/UiZoom` owns one shared `ScaleTransform`, mutated in place, that is the `LayoutTransform`
of `MainWindow`'s `BrowseLayer` and `ChromeLayer` (banners, toast, modal, shortcuts panel, zoom
readout) and of the player overlay's `OverlayRoot`. A layout transform re-measures the tree, so
cards, artwork, icons and text scale together and wrapping grids re-flow to the smaller logical
width. WPF gives a popup the transforms above it only as it opens, and a ToolTip or ContextMenu,
whose popup is created outside the visual tree, none at all. `Theme/ZoomPopups` covers both:
every `Popup` sets `ZoomPopups.Follow`, which keeps an open one's child at the zoom (a popup
created in code, such as Home's Customize flyout, takes its placement target's), and class handlers give each tooltip and context menu its placement target's scale as it
opens (100% in the mini-player). Separate windows (media info, the profile switcher) take a
frozen snapshot, because the shared instance would keep a reference to every closed window that
held it. The mpv `VideoArea` is never under the
transform. The overlay drops it in mini mode, whose 480x270 layout is fixed.

Steps follow a browser-style ladder from 50% to 200%. `Ctrl++`, `Ctrl+-` and `Ctrl+0` are
`AppShortcuts.Chrome` entries; `AppShortcuts.ChromeAliases` adds the numpad keys and
`Ctrl+Shift+=` (US `+`) without listing them twice in the shortcuts panel. They win over a player
binding on the same chord in both windows: the main window's KeyBindings run before its player
keys, and the overlay's forward skips the player bindings for zoom chords. Settings → Shortcuts
does not let a player action take any registered app chord in the first place:
`AppShortcuts.OwnerOf` names the app shortcut that owns a chord (an alias answers with its listed
entry), the key editor refuses such a capture and keeps the old key with a note on the row, and a
saved binding that already collides (a hand-edited or pre-update settings.json) is flagged but
left as saved. Space and F11 stay open to Play / pause and Toggle fullscreen, the player actions
their chrome twins duplicate. Ctrl+wheel is taken
on the tunnel in both windows and accumulates deltas to one step per 120, so touchpad pinches and
high-resolution wheels do not jump to either end. The overlay forwards zoom chords to the owner
even while a panel or flyout owns its keyboard. `AppSettings.UiZoom` persists the level through a
400 ms debounce, flushed on close, and `MainWindow` applies it before `InitializeComponent`. It
saves on `UiZoom.PreferredChanged`, not on a change of the applied scale, so a Ctrl+0 that a cap
keeps from changing the screen is still remembered.

Code that compares a size against the window has to measure in zoomed units: the rail collapse
and header width use `UiZoom.ToLayout`, the overlay measures `OverlayRoot` rather than the window,
and the SyncPlay and per-show Up Next flyouts divide the window and monitor bounds by the zoom
before clamping. The guest route `lw-ui-zoom` drives the production windows with native chords
and Ctrl+wheel and asserts measured on-screen sizes, the rail breakpoint, grid re-flow, mini
mode, the overlay's volume-versus-zoom wheel split, an open flyout following a step, tooltips and
context menus at their owner's zoom, the key editor refusing and flagging app-owned chords, and
restart persistence.

### Screen cap

`UiZoom.Preferred` is the level the user chose and the one settings.json keeps; the applied
`Scale` is that, bounded by `Cap`. With `AppSettings.LimitUiZoomToScreen` on (the default), `Cap`
is `UiZoom.CapFor(client)`: the largest ladder step, never below 100%, at which the maximized
client area of the window's monitor still holds `MinLayoutWidth` x `MinLayoutHeight` layout units.
Moving to a smaller screen lowers the applied zoom without touching the preference, so moving back
restores it. `MainWindow` recomputes the cap in `OnSourceInitialized` (before the first frame) and,
posted, on `WM_WINDOWPOSCHANGED`, `WM_SETTINGCHANGE`, `WM_DISPLAYCHANGE` and `WM_DPICHANGED`. The
client is the monitor work area minus the window frame measured in the restored state, which is
larger than a maximized frame. While limited, `MinWidth`/`MinHeight` grow to the minimum layout
times the zoom plus the frame, so a small window cannot squeeze the layout either; the cap
guarantees that minimum fits the work area. Growing to a raised minimum anchors the top-left
corner, so a posted slide puts the window back on its monitor's work area, only when the window
was smaller than the new minimum: at once for a restored window (its actual size, not the old
minimum, decides), at the next restore for a maximized, fullscreen or minimized one whose
remembered size (`RestoreBounds`) is below the minimum by then, and on leaving the mini-player
when the remembered size is below the minimum.
It waits for `WM_EXITSIZEMOVE` while the user drags, so it never fights a move between monitors,
and a lower minimum, a window already large enough, an ordinary restore or the user's own
placement (half off-screen, snapped) never moves the window. Turning the setting off asks first through the modal
host; toggles while that question is open are ignored and the box is set to the answer. The
readout adds "Screen limit" only when a step in was stopped by the cap.

`MinLayoutWidth`/`MinLayoutHeight` (684x441) are the client of the 700x480 minimum window at
100%: zoom never gives the UI less room than the app's own smallest window. They were chosen from
a survey. The `lw-ui-zoom` route runs one child process per simulated screen
(`LIGHTWEAVER_TEST_WORK_AREA=WxH`, Debug builds only), with the window filling that work area.
For every step from 100% to 200%, with the limit off, it checks a list of expected controls on
seven surfaces: login, a library grid with rail and header, Settings, a detail page, the
shortcuts panel, the zoom-limit warning dialog and the player controls. A control passes when it
is shown, at least 98% of it survives every clip up to the window after scrolling it into view, a
hit test at its centre lands on it, and it does not overlap its neighbours. Every control passed
down to about 632x340 layout units (the header title drops out at 576 wide, a poster card no
longer fits at 316 tall), but a screenshot review of those layouts found them cramped: the header
title trimmed to one letter, rail items scrolled out of sight, Settings showing one row. Layouts
at the design minimum and above reviewed clean, so that is the floor.

The route asserts that the cap never exceeds the step every surface passes at, that it is the
highest step keeping the design minimum, that everything passes at the cap and in the smallest
window the cap allows, and, on the 1024x720 screen, the saved preference under a cap, the
Settings toggle's decline and confirm paths, the restore slide and that Ctrl+0 never reports the
limit, a preference change hidden by the cap still being saved, and that neither a parked window
nor one already large enough is moved by a step. It writes screenshots of the cap, one step over
it and that smallest window, drawn over the window background, for visual review.

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

`PlaybackHarness --settings-shell-contract` covers alias-aware rebinding and the store's
failure reasons, including an ACL-denied file left byte for byte. `--settings-shell-ui` is
guest-only (route `lw-app-shell-settings`): the login password reveal, the save banner for both
failure kinds, alias capture through the production button, cache readouts on return, slider
save counts and flush points, and 700x480 fit.

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
