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

The client builds on `Jellyfin.Sdk` 2026.10.12, generated from the 12.0 API spec, with the Kiota
2.x runtime. That spec leaves out every route of Jellyfin's `HlsSegmentController`, which is
hidden from the OpenAPI document, so the SDK has no builder for `DELETE /Videos/ActiveEncodings`
even though Jellyfin 12.4 still serves it. `DiscardUnplayedDecisionAsync` sends that teardown as
an authorized raw request with the session's device id and the decision's `PlaySessionId`, and
logs any non-success status as a failure.

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
The German `de` and Arabic `ar` packs each translate all 1691 current keys with matching source
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

### Right-to-left layout

`Localization.UiDirection` mirrors the interface when `L.IsRightToLeft` (Arabic).
`UiDirection.Follow` binds a content root's `FlowDirection` to the UI language through `LiveText`, so a live language
switch flips it. It is applied to content roots and never to a Window: a right-to-left Window sets
`WS_EX_LAYOUTRTL` on its HWND, which mpv's child window would inherit and which mirrors the client
coordinates the overlay is aligned by. For the same reason title bars stay unmirrored. In
`MainWindow` only `BrowseLayer` and `ChromeLayer` follow; the root grid and the video layer stay
LeftToRight. The overlay follows on `OverlayRoot`. Separate windows (media info, the profile
switcher) follow on their content, the OSD flyouts on their child, and Home's code-created
Customize flyout takes its button's direction. Code that measures a popup against the window flips
X when the trigger is mirrored. `UiDirection.Register` gives every ToolTip and ContextMenu its
placement target's direction as it opens, so a tooltip on a pinned transport button reads like its
button.

The overlay's `ControlsPanel` (seek bar, time code, volume and transport) is pinned LeftToRight, as
are card progress fills (`MediaTemplates.xaml`), the episode-row progress bars on the detail page
and the mini-player's resize grip. Font glyphs never mirror on their own; a directional one opts
in with `UiDirection.MirrorGlyph`, a centred `ScaleTransform(-1, 1)` bound to the element's flow direction.
Back, Forward and the see-all chevron use it; media transport glyphs do not.

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

## UI zoom in the legacy WPF shell

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

## Player overlay

### Seek gesture

`Views/SeekGesture` is the seek bar's press-to-release gesture as one pure state machine (Idle,
Pressed, Dragging, Released, TornDown). The overlay feeds it the slider's events and acts only on
the outcome each returns (Commit, Abandon or nothing), so a gesture commits once or not at all.
Two flags were not enough because WPF reports two different endings through the same event:
`Thumb.OnMouseLeftButtonUp` releases capture before it raises `DragCompleted`, so every good drag
sees a `LostMouseCapture`, and so does a drag torn down by Alt+Tab. `Released` (the preview-up seen
during a drag) tells them apart: a capture loss after it is the release's own and logs nothing.
A teardown (capture lost mid-gesture, window deactivated, a cancelled `DragCompleted`) moves to
`TornDown`, logs one `event=seek outcome=abandon`, and absorbs everything until the next press, so
a thumb that kept its capture through Alt+Tab commits nothing when the button finally comes up.
On deactivation the overlay also calls `Thumb.CancelDrag()` so the thumb stops tracking input
aimed at another app. A plain press with no thumb drag captures the slider itself, so a release
outside the bar still reaches the handler and ends the gesture instead of leaving the bar frozen.
That capture is posted at Input priority rather than taken in the preview-down: on the knob the
thumb captures later, in its own bubbling mouse-down, and an earlier slider capture was either
stolen (an abandon on every knob drag) or kept the press from the thumb. The posted capture tears
the gesture down when another element holds the mouse, but not when the slider (or an element
inside it) does: under a UI stall an earlier press's posted capture can take the mouse for the
next press, and treating that as captured elsewhere ended the next press's own gesture.
`IsActive` drives `PlayerViewModel.IsSeeking`. `PlaybackHarness --seek-gesture` runs the state
machine's orderings without a window; it also runs in the default harness run.

### OSD flyouts

WPF keeps a popup on the screen, not inside its window, and does not move an open one with its
window. `Views/OsdFlyoutPlacement` handles both for the info, quality, speed, queue, tracks and
audio-device flyouts. On open and on a content size change it sets the offsets from the overlay
root's layout: left-aligned to the button where that fits, right-aligned where it does not (or
always, for quality), clamped 8 units inside the overlay, with `MaxWidth`/`MaxHeight` capped to the
room there so a long list scrolls. All of it is in `OverlayRoot` layout units, so neither UI zoom
nor monitor DPI enters the arithmetic. The queue flyout is anchored above `SeekSlider` rather than
on its badge. A real size or location change of the overlay (posted at Loaded priority, since both
events precede the layout pass) re-places every open flyout and closes one whose button is no
longer visible, such as in mini mode. A popup does not follow its button either, and a later
layout pass can move the button after that re-place: the row fit folding or unfolding a group, a
control the view model shows or hides, the compact padding, a zoom step. So while an attached
flyout is open the placement watches `OverlayRoot.LayoutUpdated`: each placement records the
button's origin, and a pass that finds it moved by more than half a unit re-places that flyout
(`FlyoutReplaced reason=moved`). A pass that finds the button hidden leaves the flyout alone:
buttons are hidden for a moment around an overlay resize and while a quality change rebuilds the
row, and closing on those dismissed flyouts mid-use on the guest. A button that stays hidden is
closed by the settled resize/move pass above. A pass with no move does nothing, which also ends the loop after a
re-place's own layout pass, and the handler is unsubscribed once no attached flyout is open. A
content resize places the flyout from the child's `SizeChanged`, inside the layout pass and before
`LayoutUpdated`, and records the origin too; when the button moved in that same pass, that
placement forces the popup to reposition itself, since `LayoutUpdated` then sees no move. A
flyout whose button the fit moves into or out of the overflow tray is closed by the fit instead.
The popups themselves stay LeftToRight under a right-to-left
language and translate the button origin back to a left-based coordinate.

### Transport row fit

`UpdateTrackButtons` fits `ControlsRow` by measuring natural widths against the bar's inner width,
not by width thresholds. Level 0 is the Video/Audio/Subs trio, level 1 the merged Tracks pill, and
level 1 + n folds the first n of `FoldGroups` into the **More controls** tray: shortcuts and
mini-player, info and speed, audio device, the chapter pair, the jump pair, quality, then the
queue. A group folds whole. Play/pause, next/previous, mute and volume, the track pills and
fullscreen are never in it. Because every width is a natural width, independent of the current
decision, the levels cannot oscillate; returning to a lower level needs 12 extra units. A group
with nothing shown (no chapters, one audio device) folds nothing and is skipped. The state line
logs `folded=<n>`.

The tray is an in-tree `WrapPanel` in a zero-size `Canvas` at the overflow button, not a Popup: a
flyout opened from a control inside a `StaysOpen=False` popup would close that popup and lose its
anchor. Folded controls are the same elements, moved, so their AutomationIds, handlers and
anchored flyouts survive, and they return to their declared slot in the DockPanel. The tray grows
leftwards and wraps at the overlay's left edge, measured against the LeftToRight-pinned
`ControlsPanel` rather than the root, which mirrors under a right-to-left language. Escape, a press on the video, mini mode or a fit
that folds nothing closes it; a flyout anchored to a control inside it closes with it. Unlike a
flyout, the open tray leaves other keys to playback.

### Legibility over video

The SyncPlay status and icon, and the buffering indicator, sit on `LwScrimBrush` (storm-0 at 0.72
alpha), which keeps text-1 at 7.2:1 over pure white without adding light over HDR video. Queue rows
set explicit text colours (text-2 for the number and title, text-1 for the playing row), because
`LwOsdFlyoutItem` replaces the theme's ListBoxItem style and the rows used to inherit whatever the
window resolved to. The SyncPlay count is users, not devices: the server builds
`GroupInfoDto.Participants` with `Select(UserName).Distinct()` and no API reports a session count.
Counting `UserJoined`/`UserLeft` instead would drift on reconnects.

The browse toast sits bottom-right at most 360 wide, stacked above the update banner, clear of the
header row and the library's A–Z rail.

## v2 full screen

`Shell/WindowFullscreen.cs` makes the WinUI window full screen without an AppWindow presenter switch
(the switch restored through the window's restored rect, onto the second monitor, under DWM's
animation). On entering it drops `WS_THICKFRAME` and `WS_MAXIMIZE`, sets the window to the monitor's
`rcMonitor` (not `rcWork`), turns the whole rect into client area through a `WM_NCCALCSIZE` subclass,
hides the caption buttons window and marks the window with `ITaskbarList2::MarkFullscreenWindow`.
`FullscreenStyle.Covers` (LightWeaver.Core) is what `Reassert` checks: no sizing border, neither
maximized nor minimized, exactly the monitor rect.

The caption, system menu and minimize and maximize boxes stay set in full screen. Without
`WS_CAPTION` the Windows App SDK destroys its caption buttons window (`ReunionWindowingCaptionControls`)
and builds a new one when the bit comes back, and UI Automation can still hold the old window's
provider: in a guest run (2026-10-30) a suite's `FindFirst` called through the freed
`CTitleBarCaptionButtonsFragmentRootProvider` and Control Flow Guard ended the app with 0xc0000409
in `uiautomationcore!UiaNodeTraverser::Traverse`. A full-screen stress loop (40 entries and exits,
each followed by a walk of the whole UIA tree) saw a new caption buttons window after every exit and
12 to 15 failed walks in full screen; keeping the caption, that window kept one runtime id and no walk
failed. Full screen hides that window with `ShowWindow` instead, and leaving shows it again.
The SDK shows it again whenever the window is activated (Alt+Tab back, a restore from minimized),
and it then stayed over the video through resizes and DPI changes (guest probe, 2026-11-01). So a
subclass on the caption buttons window takes `SWP_SHOWWINDOW` out of every `WM_WINDOWPOSCHANGING`
while its parent covers the monitor (`FullscreenStyle.HiddenFlags`).

`MarkFullscreenWindow` runs from the dispatcher queue, after the window change rather than inside it:
the change runs in a `CompositionTarget.Rendering` callback, `MarkFullscreenWindow` sends to the
taskbar's thread and waits, and a XAML dispatcher timer that fired meanwhile re-entered XAML there,
which fails fast (0xc000027b, a stowed `E_UNEXPECTED` in
`CXcpDispatcher::OnReentrancyProtectedWindowMessage` under `TaskbarList::MarkFullscreenWindow`).

None of that hides the taskbar on its own: the Windows App SDK sets the window property
`NonRudeHWND`, and the shell never treats a window carrying it as a full-screen app, whatever its
rect and `MarkFullscreenWindow` say. Measured on the owner's desktop (2.0.0, auto-hide taskbar,
2026-10-29): with the property on, `SHQueryUserNotificationState` stayed `QUNS_ACCEPTS_NOTIFICATIONS`
with the player in the foreground and the taskbar stayed topmost and slid up over the bar; with it
removed, it reported `QUNS_BUSY` and the taskbar dropped below the player, also while another monitor
had the focus. Full screen removes the property before covering the monitor and leaving puts the
saved value back before the window changes, so a maximized window on a monitor with an auto-hidden
taskbar is not taken for a full-screen app.

XAML places its content (the `Microsoft.UI.Content.DesktopChildSiteBridge` child) one pixel below the
top of a window that is not maximized, for a top border; full screen has none, so pixel row 0 showed
the backdrop as a light line and the 2159-row content letterboxed the 16:9 video one column in from
each side. `WindowFullscreen` subclasses that child and, while the window covers its monitor, turns
every `WM_WINDOWPOSCHANGING` into one that fills the client area (`FullscreenStyle.FillingFlags`);
XAML repositions it as before once the window has left full screen.

A maximized window had the same kind of strip on a monitor at another scale than the main one. The
Windows App SDK takes the main monitor's frame off the top of a maximized client area (11 px at
150%), while the window hangs over each monitor by that monitor's own frame (8 px at 100%): on the
owner's 1440p monitor next to the 4K one the client started at y=343 under a work area starting at
340, and the rows between showed light. `MaximizedFrame` subclasses the window from the start and,
after the SDK's `WM_NCCALCSIZE`, sets the top of a maximized client area to its monitor's work area
(`WindowFrame.MaximizedClient`); measured afterwards, the content is exactly the work area.

Leaving puts the style and rect back in one `SetWindowPos`. A window that was maximized gets
`WS_MAXIMIZE` back at its maximized rect, and its restored rect from the placement saved on entering
(covering the monitor while not maximized can overwrite it), set with a non-activating show command
so nothing moves. A window minimized in full screen gets its saved placement back, with
`WPF_RESTORETOMAXIMIZED` if it was maximized. DWM's transitions stay off around both changes.

The content is laid out for the new size before the window changes: `MainWindow` pins `Root` to the
target size in DIPs (the monitor at its scale on entering, the window's earlier content size on
leaving), waits two `CompositionTarget.Rendering` ticks and a `DwmFlush`, then changes the window.
Without it, DWM shows the new rect with the old layout in its corner until XAML has rendered again
(about 300 ms on the WARP guest). The pin stays until `XamlRoot.Size` reports the window's new size
(`WindowChange.CanRelease`, at most 500 ms): XAML takes the new size a few frames after the window
changed, and a pin released at once laid the content out for the size XAML still had, the old one
(the guest capture of 2026-10-22 showed the 1920-wide layout clipped in the 1264-wide window for two
frames after leaving, and the window-sized layout in the corner of the full-screen window after
entering). The top bar keeps the caption buttons' width through full screen and through the frames
after it in which the title bar reports a zero inset (`WindowChange.TakesCaptionInset`), so the
layout made before leaving already reserves them. Between the new layout and the window change,
the full player would show that layout in the old window rect (the monitor-wide layout clipped to
the window on entering, a window-sized one in the corner of the monitor on leaving), so it is held
where it was: `LayerTransition.Hold` scales, moves and clips its visual to the window's rect before
the change, with the black backdrop under it and the controls hidden, and lets go in the same UI
tick as the window change. `SetOsFullscreen` runs a callback once the window has changed: leaving
full screen for the preview starts the shrink there. It used to start with the change, two frames
before the `DwmFlush` and `SetWindowPos` that block the UI thread (185 ms on the guest, 369 ms
low-end); the next layout pass found less than a frame of the 180 ms left and `LayerMotion.Plan`
snapped it, so the shrink showed as two frames. The grow already ends before the window grows. A
change still waiting is flushed before the mini-player switch, before closing and before the
opposite change.

Because the change lands frames after it was asked for, `WindowFullscreen` reads the window's
style, rect and placement when it applies it, not when asked (`FullscreenStyle.Entry`): a window
minimized meanwhile stays minimized and covers the monitor once restored, and one that lost the
foreground covers it without activating or rising. Leaving never activates the window and reads
the minimized state when it applies. While covering, the subclass also reasserts on
`WM_STYLECHANGED`, so a style change that keeps the rect (`WS_MAXIMIZE` or the frame put back)
is undone too; the reassertion ignores the style change it makes itself.

## v2 Home layout and the Downloads row

`home-layout.json` (`Settings/HomeLayoutStore.cs`) is shared by the 1.x WPF Home and v2. Each
shows only some of the ids: `Libraries` is the WPF app's alone, `Downloads` v2's alone, and the
WPF Home throws on an id it has no rail for, so it drops `Downloads` when it loads. `Save` keeps
every saved entry the caller left out, after the given ones, so saving from either app keeps the
other's row and its switch. A section missing from the file is appended as
`EnabledByDefault` has it: on, except `Downloads`. Reset writes `Defaults()`.

The Downloads row (`HomePage.ShowDownloads`) is built from `DownloadManager.Snapshot()` through
`DownloadsModel.HomeItems`: completed downloads of the profile's server, newest first, each as its
`PlaybackMetadata` (or its name and poster when a download has none). It is local, so it shows
before and without the server. The server comes from Home's profile key
(`HomeLayoutStore.ServerOf`), not the session: a start without the server opens cached Home under
the saved profile while `JellyfinService.ServerUrl` is still empty. `DownloadsChanged` fires on every progress tick from the download
thread; the page queues one UI visit at a time and refills the row only when its list of ids
changed. A card opens the detail page, or plays the download through `IAppShell.Play` while
`Connectivity.IsOffline`.

## v2 Recently Added See all

The Home row reads `/Items/Latest` per video library (24 each, `RowSize`), which groups new
episodes into their season or show but has no paging and no total. See all reads `/Items`
instead, which pages and counts: `RecentList.Movies` (`Movie,Video`, `SortBy=DateCreated`) and
`RecentList.Shows` (`Series`, `SortBy=DateLastContentAdded`), both descending and `IsUnplayed` when
the user's `HidePlayedInLatest` is on, as Latest does. One query cannot do both: on Jellyfin 12.2
`DateLastContentAdded` sorts by `DateLastMediaAdded`, which only folders carry, so every movie sorts
after every show. `RecentlyAddedFeed` reads each list once, whole, as ids and dates only (no images
or user data; 449 titles in about a second on the test server), merges them by the date each is
sorted by into a snapshot of the order, and reads the titles of the next 100 ids by id when the grid
is scrolled to within two screens of its end. The snapshot is read in the background: the first
page is each list's first 100 titles with what a card shows (`GetRecentlyAddedHeadAsync`, with the
list's `TotalRecordCount` for the count), merged the same way. The later pages walk the snapshot
and pass over every title already shown; a title the snapshot puts above the first page's last
title that the first page did not have (added between the two reads, or moved up past it) is
inserted in its place (`FeedInsert`), the rest are appended, so the grid ends up in the snapshot's
order with every title once. Reading the whole order first left the grid blank (no cards, no count)
until it answered; until the first page lands the grid shows the library grid's skeleton after
150 ms. Paging by offset into a list the server sorts afresh
each time skipped titles: a show whose new episode moved it ahead of the offset was never read, and
a title removed or watched meanwhile pushed an unread one behind it. A page is used up only once
its titles arrive, so a failed read gives the same titles on the next try. A refresh takes a new
snapshot. Sorted by name it is one `SortName` list. Against the test server (452 titles, 449 unplayed) the first 24 match the row title for
title except where the row shows a season or a single episode, which See all shows as its series,
and a series whose season the row dates by the season's own creation date (Babylon Berlin, a new
season of episodes from September, sorts eighth in See all by its newest episode on 13 October).

## Browse cache freshness

`AppViewModel` keeps a user-data ledger: every item passed to `NotifyItemUserDataChanged` is
recorded with its time, for the active profile only (a profile change clears it), up to 512 items,
oldest dropped first. A view rendering a cache entry calls `ApplyUserDataLedger` with the entry's
`CachedAtUtc`, which takes the watched, favorite and resume fields of every newer update and leaves
the entry's metadata alone. Library grids and the detail page's cached season list use it, so a
toggle made elsewhere shows on a view opened later before its identity sweep lands. An entry's
`CachedAtUtc` is the time of the oldest data it holds, not of the write: the prefetcher stamps a
fresh run with its first page's request, a resumed run with the entry's own stamp and a verified
one with its sweep's start; the incremental refresh stamps its sweep's start, the ordered and
season loads their request's start, and the local-mode user-data write the stamp of the list it
saves. A prefetch page that lands on an open grid goes through the ledger with that stamp too. A
write-time stamp hid a toggle made between a big folder's first and last page. "More like
this" is an `ObservableCollection` with the same revision guard as the item fetch, so a toggle
swaps the card in place. The detail page skips re-rendering when its own toggle's update fans back
as the same instance, so a toggle renders it once.

Removed items go through `AppViewModel.NotifyItemsRemoved`, a set of ids. Two sources feed it: the
removed ids of a session-socket `LibraryChanged` batch (its folder ids are library ids, so views
below a library never matched them), and a 404 on open. `MainWindow.OnItemsRemoved` has every
view in the history drop the cards: `LibraryView` from its list and its cache entry
(`BrowsePrefetcher.RemoveItemsAsync` for a paged folder, the ordered entry for series, seasons,
playlists and albums), `HomeView` from its rows, `SectionView` from its page, `ItemDetailView`
from More like this and the episode list, `SearchResultsView` from its grouped results, and
`AdvancedSearchView` from its paged grid or its mixed type sections. The search views were left
out, so a result deleted on the server answered 404 when opened and the page went back to the
same dead card. Both drop the card in place without a reload: an empty type group goes with its
last card, Advanced Search lowers the summary and section counts, and a section left with no
cards closes. Their responses go through the removal ledger below as well. A removal from a grid
that pages by count (Advanced Search's paged grid, the see-all listing) bumps that view's append
epoch. An append carries the epoch and the card count it started at; one that lands after either
moved is dropped and asked again at the new count. It was asked at the old count, so it skipped a
result per removed card or repeated the cards at the boundary. Quick search results are not paged.
Advanced Search's mixed sections preview 24 cards each and do not page. A removal that empties a
preview while the section's remaining total is above zero refills it with that type's first page
again, under the query the sections were built from; the server's count replaces the section's
and the summary follows. A section closes only when its total reaches zero, and the view shows the
zero-results state only when every section's total has. It used to show "Nothing here" as soon as
every preview was empty, under a summary that still counted the rest. The paged grid does the same
when removals empty it with results left: its first page again, then the zero-results state only
once the server confirms nothing is left. The first load hands off the same way: a preview, the
paged grid, or the grid a single-type mixed result becomes, whose every card was removed while the
requests were out but whose total is above zero starts its refill as soon as the results are
installed. Mixed mode kept such sections up but never refilled them, so two types of 100 matches
each, with both 24-card previews removed in flight, left two empty sections counting 76 for good. A refill, or any paged-grid append, whose every card was removed while it
ran is asked again at the same offset when the adjusted total still counts results past it, up
to twice (`RemovalRefill.MaxRemovedRetries`). Running out of those retries is not an answer: the
adjusted total stays, the append path stays open, an emptied grid shows its loading skeleton and
an emptied section keeps its header and count, and `RefillBackoff` asks again after 2 s, doubling
per consecutive exhaustion up to 30 s, under the view's generation and profile checks and only
while the view is visible (playback collapses the browse layer without unloading the view, so
the check is visibility, not `IsLoaded`); showing the view again, including coming back from
the player, the next scroll or the next removal asks at once. A failed refill of an emptied grid or section schedules the same retry. All three places
apply one rule (`Views/RemovalRefill.cs`): an empty grid or section with a remaining total above
zero is pending, never "Nothing here" and never closed, and has a refill in flight or scheduled;
only a confirmed total of zero, the server's count less the removed cards or a page that listed
nothing at its offset, shows "Nothing here" or closes a section. Exhaustion used to set the
total to zero: with 250 results, the 50 shown removed and three refill pages each removed in
flight, the view showed "Nothing here" with 50 still counted and refused to ask again. The grid's
refill used to take such a page for the end: with 150 results and the 50 shown removed, the
refill listed 51 to 100, those were removed while it was out, and since the grid was already
empty no epoch moved, so it showed "Nothing here" with 101 to 150 still on the server. The
section refill asked again without a bound. The see-all listing (`SectionView`) follows the same
rules: removals that empty its grid with a total above zero ask for the first page again through
the normal append, as does a first page whose every card was removed while it ran, with the same
retry, bound and backoff, and "Nothing here" shows only once the remaining total is zero. It used to show
it as soon as the grid was empty. `GenresView` holds
genre names, not item cards, and the downloads list keeps a local copy on purpose. A prefetch run holds its folder in memory and rewrites
the entry on every page, so a direct edit came back on the next page and shifted the server
offsets by one. Removals for a folder being prefetched are queued on the run instead, as are the
ids of every removal for that profile: the run drops them before each request and each store,
filters them out of later pages, verifies the folder through the sweep before storing it as
complete, and applies anything left to the entry after its last store. While it applies them
the run is still registered, so a view that opened the folder then used to be turned away and
never asked again, and each of its appends sat out the 15 s page wait. Such a request is now kept
on the run, which starts the new run once it has unregistered; with no request, it releases any
waiter left behind so the append fetches a server page at once. A `LibraryChanged` batch cancels
the profile's runs and waits for them to end, settle included, before it deletes any entry: a
cancel does not stop a store or a settle edit already under way, and one that landed after the
delete brought the entry back. That wait covers only the runs that exist when it starts, so the
batch marks the profile as invalidating (`BrowsePrefetcher.BeginInvalidate`) until its deletes
are done: a `Start`, or a settling run's restart, meanwhile only records the folder, and the runs
start when the last overlapping invalidation ends. Without it a view loading during the delete
started a run that read the entry first and stored it, deleted items included, afterwards. The
writes made beside the prefetcher, the incremental refresh's store, a direct removal edit and the
local-mode user-data write, take an invalidation epoch before they read and delete the entry they
wrote again when an invalidation began, ended or was under way since then. The local-mode write's
epoch is the one taken before its list was read (the cache read, the refresh's sweep, or the
prefetch's last store), so a list from before a delete does not bring the entry back. The grid also remembers the
ids it dropped and filters them out of every cache-derived list, and its appends continue a cached
list by id rather than by count, so a disagreement about one removed item can no longer repeat or
skip the card at the page boundary. `NavigationService.RemoveFrames` then drops back and
forward frames for the removed items. A 404 (`JellyfinService.IsNotFound`) on a detail page or a
library container raises `ItemNotFound`. `MainWindow` broadcasts the removal first, which drops
every other page for the item from Back and Forward, then `NavigationService.DiscardCurrent` drops
the dead page itself (it does not go onto Forward) for the nearest surviving frame, or Home when
none is left, and shows one toast per item. Going back first used to land on an earlier page for the
same item, flash it and toast twice, and left the dead page up when there was nothing behind it.
An ordered container's 404 also clears its "showing cached results" status. Any other error still
leaves the cached copy up. A library change also refreshes Home's Favorites row.

The fan-out only edits what is on screen and in the cache when it runs. A request that started
before the removal could answer with the item afterwards, pass its view's generation check, and
paint it back; a season's episode list did that with an episode deleted under it, because the
batch names the library, so the season view never reloaded, and its store rewrote the ordered
entry after the removal's own edit had finished. `AppViewModel` therefore keeps a removal ledger
beside the user-data ledger: every id passed to `NotifyItemsRemoved` with its time, recorded
before the fan-out, for the active profile only (another profile clears it), up to 4096 ids,
oldest dropped first, and locked because cache stores check it off the UI thread.
`DropRemovedItems` drops the items a removal newer than the data names: a network response by
its request's start, a cache entry by its `CachedAtUtc`. An item re-added under the same id later
comes back from the next request. Every response goes through it before it is stored or shown:
LibraryView's ordered loads, its server pages (first page and appends, whose total shrinks by
what was dropped) and its incremental refresh, every cache-derived list the grid renders, the
detail page's episode list (cached and fetched) and More like this, Home's row fetches and Next
Up rolls, and the see-all listing's pages. The detail page now removes episodes from its own
season entry too.

Every write of a browse entry holds that entry's write gate (`MetadataCache.LockAsync`, a
`KeyedAsyncLock` per cache directory, one gate per key): `StoreBrowseListAsync` and
`RemoveFromBrowseListAsync` for ordered entries, `BrowseFolderCache.StoreAsync`, `UpdateAsync` and
`RemoveItemsAsync` for folders, and through those the prefetcher's page stores and settle edits,
the direct removal edit, the incremental refresh's store and the local-mode user-data write. Two
removals used to read the same list unlocked, one wrote [B,C] and the other [A,C], and the deleted
item came back on the next start. The removal reconciliation runs inside the gate, so a write
filters against the removals known when it writes: an ordered store, the refresh's store and the
user-data write drop what the removal ledger names newer than their data's stamp, and a prefetch
store drains its run's queued removals there. Each writes again before it releases the gate when a
removal was recorded during the write. The ledger records a removal before the fan-out edits any
entry, so that edit either ran before such a write, which then leaves the item out too, or waits
for the gate and edits the finished write. The gate is held for file work only, never across a
network request, and a holder takes one key; the folder index's own gate is taken after release.
Deletes (an invalidation, a 404) are not gated; the invalidation epoch above covers a write that
straddles one.

A folder entry past `BrowseFolderCache.MaxItems` is marked truncated and has no prefetch to
complete it, so its prefix was never re-checked. Loading one now runs the identity sweep, capped
at the same item count, and corrects the default-query display, with a refreshing status instead
of the caching count. Either way the append epoch (`_appendEpoch`) moves, so every append in
flight, which read the old order, is dropped and its loading flag released. A display that stays
within the prefix becomes the prefix's first cards, and an append it dropped is asked again
against the prefix. Dropping it only when the card count moved was not enough: with 500 cards on
a 500-item cap, a refresh that pushed item 100 past the cap showed [1..99, 101..501], still 500
cards, so an append reading the old entry passed its checks and added item 100, and the next
server page at offset 501 skipped item 502. A display that had loaded cards past the prefix
restarts pagination from it: the grid shows the swept prefix and server pages are fetched from the prefix's count, chained one after another through the normal
append under the new epoch, until the grid holds as many cards as before; then the scroll offset
it had is put back, unless the user scrolled while it ran. A scroll event that moves the offset
without changing the grid's extent or viewport counts as the user's; the restart's own move, the
clamp when the grid shrinks to the prefix, comes with an extent change. Only a position above the
old offset used to be put back, so a user who scrolled up during a slow restart was sent back down. Two earlier fixes kept the old cards past the prefix instead, and both broke:
the first appended behind a gap, and the second, which kept whatever followed the last prefix card
on screen, lost cards once anything in the prefix moved. With 600 cards on a 500-item cap and item
550 renamed to sort first, the prefix became [550, 1..499], only 551 to 600 were kept, 500 to 549
were gone, and the next page at offset 550 repeated cards already shown, so scrolling stalled.
Which of those cards are still there, and where, only the server can say. Every append carries the
epoch and the card count it started at and lands only if neither moved; one whose count moved (a
removal took a card off the grid) is asked again at the new count. For the rest of the load,
appends continue the refreshed prefix rather than the entry on disk, which lists a moved item at
its old place until the refresh's store lands. An append past such an entry's cached part goes straight to a server page,
and `WaitForNextPageAsync` answers at once when no run is registered for the folder; both used
to wait out the full 15 s for a page no run would deliver. A prefetch that resumes from an earlier run's partial entry verifies the
whole folder through the same sweep, diff, refetch and merge before storing it as complete, since
one delete plus one add keeps the count that the drift check compares. A run takes each page's
own total rather than page 0's, and an empty page after items it already holds ends the run
through the same verify and store. With page 0's count, a removal that left a multiple of the
page size made the run meet an empty page short of that count and stop without storing the
entry as complete, so the view showed "Caching 999 of 1001" for good.

Home's Favorites row is what the server lists for `IsFavorite`. A watched favorite stays with its
badge; only an un-favorited one leaves. A library scan refreshes that row about every 30 s, and a
fetch that started before an un-favorite could land after it and put the item back. Every Home row
fetch now goes through the user-data ledger with its request's start time before it replaces the
row: newer updates are applied, and an item they changed leaves a row it no longer belongs in by
the live rules (un-favorited from Favorites, watched from Continue Watching, Next Up and Recently
Added, or a zero resume position from Continue Watching). A Next Up episode dropped that way rolls
to the series' next episode in the same slot, as a live toggle does; it used to leave the series
out of the row until the next refresh. The roll is queued after the fetched row is installed,
against the row generation that install produced: queued before it, against a guessed next
generation, it was dropped whenever a live update replaced the row in between. A music album is an ordered container like a series or
playlist: it loads through `GetAlbumTracksAsync` in disc/track order under
`album-tracks:{id}:v1`, the same list Play all queues, instead of the paged SortName folder path.
That call pages 500 tracks at a time like `GetPlaylistItemsAsync`; a single request capped an
audiobook or box set, and its Play all, at 500. The album, artist and playlist pagers stop at 200
pages, and stop early when a page makes no progress: for tracks, a page with no id not seen
before; for a playlist, whose repeats are legal, a page identical to the previous or the first
one, compared by playlist entry id. A server that ignored `StartIndex` would otherwise repeat its
first page for ever. A page with a row that has no entry id is not compared, since by item id
alone a playlist listing the same tracks twice repeats a page legally; the page cap still ends
such a list. `ItemSortBy` has no unique key to end the sort with, so tracks that tie on
every sort key can still trade places across a page boundary.
A new load releases a stale append's loading flag, so the new query's first append is not blocked.

## v2 motion and artwork off the UI thread

A WinUI window has one UI thread, so animations stay smooth while artwork loads by keeping both
off it where possible.

- **Player grow and shrink** (`Player/LayerTransition.cs`, math in `LayerMotion.Frame`): the
  layer is laid out once at the end rect; its handoff visual gets a scale about the layout's
  centre, a `Translation` and a clip, from 60 keyframes over the 180 ms cubic ease-out. The clip is
  a `CompositionGeometricClip` over a `CompositionRoundedRectangleGeometry` whose `Offset`, `Size`
  and `CornerRadius` animate: a `RectangleClip`'s corner radii cannot be animated (`StartAnimation`
  fails with E_INVALIDARG, "The specified property was not found or cannot be animated").
  The scale keeps the picture's shape (`LayerContent`: the stand-in artwork by its aspect and
  stretch, else the video by its aspect), so each frame looks as the old per-frame resize did, and
  the clip's radii are divided by the scale so the corners stay 8 px on screen, held at half the
  clip when the rect is smaller. The swapchain keeps
  the display's size and its `CompositeTransform` fit for the laid-out rect; the compositor folds
  the two scales into one, so nothing is upscaled. `Root.LayoutUpdated` measures both ends again
  and retargets from the time already spent, for a window whose size changes as full screen
  starts or ends. `LayerMotion.Plan` gives the rest: keys from exactly 0 to 1 over the time left,
  or nothing when under a frame (16 ms) is left, after the 180 ms but before the batch lands
  included; then the end rect is set at once and the transition lands on the next UI turn. A
  zero-size or NaN rect from a window between sizes does not retarget, and a frame the compositor
  would refuse (`LayerFrame.IsValid`) snaps and logs instead of animating. The scaled layer can fall short of a frame's rect (a 16:9 picture leaving a
  16:10 window covers only 1920 x 1080 of it), so the black is a separate composition rectangle
  under the layer, drawn unscaled at each frame's rect with the same corners. A scoped batch, with
  a timer as backstop, ends it. The mode follows the transition's end, so the full screen toggle
  goes by where it is heading (`FullscreenIntent.Toggle`): pressed during a grow into full screen
  (`_fullscreenGrow`), the grow ends in the window, back in the preview when it grew from there;
  pressed during the shrink, the shrink is cancelled for full screen before the window changes. A shrink whose detail page was left meanwhile stops playback instead of landing, unless
  a new title started during it: a remote play cancels the shrink and takes the full window, and
  Play on another detail page cancels it as the player is placed in that page's preview. Either
  way the left page gets its own preview and bar back.
- **Nav morph** (`Shell/NavMorphTransition.cs`, math in `NavMorphMotion`): the group is laid out
  at the wider of its two widths; its pill (fill and 1 px stroke) and the search field's chrome
  are drawn as composition shapes (`Controls/BorderShape.cs`) while the XAML brushes are off, the
  items sit under an `InsetClip`, and the search field's text, buttons and bottom line are moved
  to where the star column puts them at each frame's width. A resize mid-morph moves the search
  field's slot; `LayoutUpdated` notices (`NavMorphMotion.SlotMoved`) and the rest of the morph is
  drawn from the new slot, through the same `LayerMotion.Plan`, so a resize with under a frame
  left lands at the end width at once. Everything is in logical coordinates, which the window's
  right-to-left mirror flips.
- **Artwork** (`Services/ImageLoader.cs`): disk read, download and the `BitmapDecoder` size probe
  run on the thread pool with `ConfigureAwait(false)`; one low-priority UI visit creates the
  `BitmapImage` and calls `SetSourceAsync`, and XAML decodes off the UI thread. Decodes take turns
  through `DecodeSlots` (half the cores, two to four; one while an animation runs or on two cores
  or fewer). `DecodedCache`, `SharedLoads` and the counters are thread-safe.
- **Applying artwork** (`Services/ImageQueue.cs` over `FrameWorkQueue`): a picture read from disk
  or the network is queued; `CompositionTarget.Rendering` runs about 2 ms of the queue per frame,
  only while it has items. A memory hit (its load task already complete when awaited) lands at
  once through `FrameWorkQueue.Submit`, so a page being shown, a rebuilt Home for one, has it before
  its first frame. XAML itself lets go of the decoded surface of an `Image` that leaves the tree
  and decodes it again, asynchronously, when it comes back, so a kept page put back in
  `PageHost` drew its cards blank and filled them in over the next frames (about 220 ms back on
  Home from Settings in the 4e0e1c5 capture's normal pass) without any request of ours. Home
  therefore has its own host (`HomeHost` in `MainWindow.xaml`) and is collapsed there, still in the
  tree, while another page shows, for as long as the history holds it (`NavHistory.Holds`). The player's grow and shrink and the nav morph's opening hold the queue; the
  morph's closing and the chosen item's slide do not, because they come with a page switch. A
  held item lands after 250 ms anyway. Each item checks that its card still wants it.
- **Measuring**: the frame log's `images`, `image_frames` and `images_max_per_frame` count queue
  runs per recorded frame, so a capture shows whether artwork landed during an animation.

## v2 detail page: laid out once

The detail page (`Pages/DetailPage.xaml.cs`) decides its geometry in its own `MeasureOverride`,
in the pass that gives it its size, from inputs that are all known before any lookup answers.
Nothing that arrives later is an input, so a page is laid out once per size (2026-10-22; before,
it re-laid itself out up to five times after opening).

- **Preview shape**: `DetailModel.PreviewAspect`, fixed in the constructor from the `MediaItem`
  the page was opened with. List requests ask for `ItemFields.Width` and `Height`
  (`JellyfinService.DetailPreviewFields`) and `MapOne` maps them, or the first video stream's size
  and display aspect on a single-item fetch, to `VideoWidth`, `VideoHeight` and
  `VideoAspectRatio`; `DetailModel.KnownAspect` turns them into a shape (a DVD frame without a
  display aspect is unknown). Shows and seasons, and titles without a size, are 16:9. The
  PlaybackInfo streams and mpv's `video-params` no longer resize the preview; mpv letterboxes a
  different shape inside it and `VideoSurface.Fit` places the picture.
- **Rows**: `CardRow.HoldPlace` fills More like this and Cast and crew with placeholder
  `MediaCard`s (no item, blank one-line captions, invisible for 150 ms, skeletons after), so the
  rows measure at their final height from the first pass. `EmptyText` keeps an empty row's place
  with a quiet line instead of collapsing it; until a row has items its heading button is out of
  UI Automation and takes no input, so the suites' "row shown" still means "row loaded".
- **Frame**: `DetailModel.Frame` places the preview and the side panel from the page size, the
  measured rows height, the fixed shape and the page's spacing. The season or episode grid's
  columns come from the side panel's width in the same pass (`DetailModel.ChildGrid`), not from a
  `SizeChanged` on the grid. The page no longer handles `SizeChanged` on the scroller, the rows or
  the grid, so there is no layout feedback between them, and a page reused from the history is laid
  out at the window's size before it is drawn.
- **Info panel**: the genres line is always there (blank when there are none); with an OMDb key the
  ratings get their own line held by invisible probe chips as wide as the three scores; every
  action keeps one width (each label sits over an invisible twin with its other text, Play/Resume,
  Mark watched/Watched, Favorite/Favorited), and the trailer button keeps its slot at the end of the
  row, invisible, unfocusable and out of UI Automation until the title has a trailer; the seasons
  or episodes heading shows from the start. A movie's or an episode's format badges get their own
  line, held from the start by one invisible probe chip (`TechProbe`).
  These fill top to bottom, and the children arrive after the item lookup, so later text does not
  push the grid.
- **Tracks**: a movie or an episode has Video, Audio and Subtitles drop-downs under its format
  badges: `LwButton`s (`VideoTracks`, `AudioTracks`, `SubtitleTracks`) named by `MarkTracks` (from
  `ApplyTexts`) after the track `DetailModel.Tracks` marks as the one Play uses (subtitles start
  with Off, a `TrackLine` without an index). Audio and Subtitles open the same radio menus as the
  bar's buttons (`ShowAudioChoices`, `ShowSubtitleChoices`), placed below the drop-down with the
  button as `ExclusionRect`, so a pick sets `_audioIndex` / `_subtitleIndex` for both. Video is never
  enabled, since `PlaybackChoice` has no video track; Audio is enabled with two or more tracks,
  Subtitles with one or more, as on the bar. A drop-down keeps its height blank, so the streams
  answering, a version switch (`_streams` null until its own arrive) and a failed lookup never move
  what is below. They are not `ComboBox`es: a ComboBox opens its list as a popup window under the
  pointer, and Windows shows the busy cursor over it until its input is ready (microsoft-ui-xaml
  #8829, open since Windows App SDK 1.4); `tests/FastUi.Tests` fails on a ComboBox in the page. The
  exclusion is the button grown by the menu's shadow (`MenuShadowAbove` 20, `MenuShadowBelow` 4), so
  a menu that flips above keeps its shadow off the pointer too. A menu opened with the pointer hands
  focus to the page once it closes, if its button still holds focus then (`ReleaseFocus`, as
  `HoldFocusForPage` does for the page host),
  so Space and the player's other keys reach the window instead of reopening the drop-down; a
  keyboard user keeps focus on the button.
- **Row panels**: More like this and Cast and crew each sit in a `Border` drawn like the info panel
  (`LwPanelBrush`, `LwStrokeBrush`, `LwGroupRadius`, padding 16,12,12,16). `Rows`' second column
  (`RowsSideColumn`) takes the side panel's width from the same `DetailModel.Frame` pass, so More
  like this is as wide as the preview and Cast and crew as the info panel. The rows' height does not
  depend on their width, so measuring them before the frame still holds.
- **Round people**: a `CardKind.Person` card (here, on the Cast page and in search) has square
  artwork with a corner radius of half its width (art, hover and current rings), captions centred,
  and its picture 1.5 × the width tall and top-aligned, so a 2:3 portrait shows its top two thirds.
- **Version drop-down**: the bar's `DetailVersion` button is 160 wide whatever the version is
  called, and `DetailModel.ShowsVersions` decides whether it shows: the loaded versions once
  `GetMediaVersionsAsync` has answered for the target, before that `MediaItem.MediaSourceCount`,
  which list requests ask for with the size (`ItemFields.MediaSourceCount`, in
  `DetailPreviewFields`; null when the server does not say). A movie or an episode
  uses the opened item's count until its target is set, so a title its list entry says has
  versions has the button from the first frame; it is disabled until the versions load. A show,
  a season, or a list that did not carry the count gets it when the versions answer. Its slot is
  held either way: hidden, the button is transparent, out of hit testing, the tab order and UI
  Automation (as the trailer button), so showing it never changes the bar's `BarRow` measure.
  `tests/FastUi.Tests` fails if it is collapsed instead.
- **Measuring**: `FrameLog.Start(name, layout: …)` samples `DetailPage.Geometry` (preview rect, rows
  top, side panel width, and the overview's and the grid's top inside the panel) every frame; `DetailSettle` counts the frames that moved anything by half a
  DIP or more. The shell starts a `detail_settle` recording at the detail page's first frame and
  stops it 2 s later; its line carries `layout_changes` and `layout_settled_ms`, which other lines
  leave out.
- **What can still move**: a caption in a script whose fallback font has taller lines than Segoe UI
  Variable (a CJK title) makes its row a few DIP taller than the placeholders, and the preview gives
  them back; an overview, genres or chips that the opening list did not carry change the panel's text
  above the grid when the item lookup answers, before the grid fills.

## v2 quality and version before Play

The detail page's quality menu and the player's Quality flyout list the same tiers,
`PlayerViewModel.QualityTierMbps` (null for the Settings cap, 0 for the original, then 20, 10, 4
and 1 Mbps), named by `QualityNames.Tier` with the plain name and the bitrate together ("High ·
1080p · 20 Mbps"; the Settings tier shows the Settings cap), with Always transcode. The Exact
bitrates switch and `AppSettings.QualityExactBitrates` were removed; an old settings file's value
is ignored on load.
`PlaybackChoice` carries the tier as a nullable `MaxBitrateMbps` and Always transcode as
`ForceTranscode`. `PlaybackController.Start` takes both as `_qualityRequest` and `_forceTranscode`,
so the negotiation forces a transcode when asked and the player's flyout shows the detail page's
pick once the file loads. A play without picks (Home, a remote play, a download) sends null and
false. Retry keeps the tier and the switch; "Play at original quality" uses
`PlaybackChoice.AtOriginalQuality`, which also drops Always transcode, so it really plays the file.
Versions, which the player lists in its Quality flyout, have their own drop-down on the detail
page (see "v2 detail page: laid out once").

## v2 format badges, collections and Shuffle

Three 1.x features, ported for 2.0 (2026-10-23).

- **Format badges**: `DetailModel.TechnicalChips` reads the `Summary` fields of the
  `MediaSourceStreams` the detail page already loads for the version Play uses
  (`JellyfinService.GetMediaStreamsAsync`): the `Resolution` field as 4K (width 3400 and up), 1080p
  (1900), 720p (1200) or SD, the `Range` field unless it is SDR (`HdrLabel`: HDR10, HDR10+, HLG,
  Dolby Vision), and the `Audio` field (`AudioPillLabel`: ATMOS or DTS:X when the stream's profile
  names them, else the codec and channels). These are the WPF detail view's gem pills; v2 draws
  them as the info panel's chips. A movie and an episode show them; a show or a season does not.
- **Collections**: `MainWindow.OpenItem` opens a `BoxSet` as a `LibraryPage` under the key
  `collection:<id>` (kept like a library, `reuse: true`), so the top bar selects no destination
  and keeps its Libraries label. `LibraryLoader` pages the box set's children with `ParentId` and
  shares the browse folder cache with the WPF app, as for a library. A box set's titles are
  linked children, so its genre filter lists the genres of the loaded titles instead of asking
  `/Genres` for the folder.
- **Shuffle**: `DetailModel.ShuffleQueue` keeps the playable episodes, then the unwatched ones
  (all of them when every one is watched), and shuffles them (`PlaylistModel.Shuffled`).
  `ItemMenu.ShuffleAsync` reads the episodes (`GetSeriesEpisodesAsync` for a show,
  `GetEpisodesAsync` for a season) and hands the queue to `IAppShell.PlayQueue`, so
  `PlaybackController.Play` sets the queue, or submits it to a SyncPlay group, as a playlist's
  Shuffle does. An empty queue shows the WPF app's "Nothing playable in here." toast.
  Removed episodes leave the list first (`ItemRemovals.Filter` from a mark taken before the
  lookup). Shuffle and a card's Play on a show, a season or a playlist claim one
  `IAppShell.PlayRequests` order: a lookup that returns after a newer request, a play, a stop or a
  profile switch plays nothing. An episode the player plays on into its show (`PlayFromEpisodeAsync`) takes
  its playback intent when asked and keeps it when its queue lookup starts it, so a newer Shuffle
  still waiting for its episodes starts after it instead of being dropped.

## v2 links and dropped files

`PlaybackController.PlayUrl` plays a local file or a direct HTTP(S) link with no server behind it.
Everything that reaches it from the shell goes through `MainWindow.PlayAddress`: the Play a link
dialog (Ctrl+L or the profile menu), the file picker (Ctrl+O or the profile menu) and a drop on the
window. Once one plays, `PlayAddress` drops the detail page's preview association, also when the
player was already full, so Back stops it instead of shrinking it onto another title's preview.
Over the preview, or while a grow or shrink runs (whose end would place it in the old preview), it
cancels the transition and takes the full window; a full player at rest, the mini-player included,
is left as it is. A link is what `StreamAddress.TryParse` accepts: absolute `http`/`https`, a host, no
user name or password. The dialog fills in a link from the clipboard (a web link or text) only when
it parses, and says which rule failed: a user name or password (`StreamLink.HasCredentials`) or not
an `http(s)` link.

A drop reads what the drag carries in `DragEnter`, under a deferral (`DropPlayOf`): the first
`StorageFile` with a path, else a `WebLink`, else text that is a link (`DropPlay.File`/`.Link`). The
older `StandardDataFormats.Uri` has the same format id as `WebLink` (`UniformResourceLocatorW`), so
asking for `WebLink` covers it. Anything else, or a drag over Login, sets no accepted operation, so
Windows shows the no-drop cursor and no overlay appears. The drop itself reads the data again, so a
drop that lands before `DragEnter` finished still plays. Moving between elements inside the window
raises a `DragLeave` followed by a `DragEnter` on `Root`; the overlay is hidden 50 ms after a leave
that no enter follows, which also covers a drag cancelled with Esc inside the window. A read can
take longer than that, so each enter's read carries a generation (`DragReads`): a leave, a drop or a
newer enter makes it stale, and a stale read neither shows the overlay nor replaces a newer result. The overlay is
added to `Root` after the player layer, so it lies over the full player too.

A link's title comes from its path only (`StreamLink.Title`): the last segment without its
extension, or the nearest folder above `index`, `master`, `playlist`, `manifest`, `stream` or `live`,
with the host (and port) as the subtitle; the host alone when nothing is left. On `FileLoaded`, mpv's
`media-title` replaces it when it is a real name (`StreamLink.MediaTitle`): mpv falls back to the
file name, and for a link that fallback can carry the query, so a media-title equal to the file
name, starting with it before a `?` or `#`, containing the query, or containing `://` is ignored.
The query and fragment never reach a title, the drop card or the log (`source=link`, no address). A
link holds the surface with a ring (`VideoSurface.Waiting`) until mpv's first frame, a failure, or
the next title. A failed link has its own cause (`FailureCause.Link`) and message, and Info's play
method reads "Link"; a local file opened this way reads "Local file", where it used to say
"Downloaded file".

## v2 typed time, Up Next lead and subtitle carry-over

Ported from the WPF player in 2.0.

- **Typed time.** `PlayerView`'s position readout is a button (`PlayerPositionButton`; the
  `PlayerPosition` text is inside it). A click puts a 76 × 24 field (`PlayerTimeEdit`) in its place,
  prefilled with the position and selected, and holds the controls up. `TimeInput.TryParse`
  (`LightWeaver.Core/Player`) takes one to three colon-separated runs of digits, weighted by
  position, invariant culture and `NumberStyles.None`, so `83:45` is 83 minutes and a sign, a
  decimal or a space inside fails; `TimeInput.Target` clamps to the duration once it is known. Enter
  seeks through `PlayerViewModel.Seek` (the SyncPlay gate included); Esc, losing focus, a grow or
  shrink, compact or mini mode, and a new or stopped title close it without seeking. While it is
  open `HandleKey` refuses every key, so the field's keys reach no shortcut, and the window's
  `OnRootKeyDown` skips player keys while a text box has focus. Closing hands focus back to the
  player before the field collapses, so the next Space pauses rather than pressing a button. Unlike
  the WPF overlay, it also works in the compact bar and the mini-player, where the field fits.
- **Up Next lead.** `PlaybackController.ArmUpNext` passes `UpNextLead.For` to
  `PlayerViewModel.SetUpNext`: the lead of the show of the title being left
  (`UpNextPreferences.ResolveLeadSeconds` with its series when it is an episode, under the session it
  plays in), else `AppSettings.UpNextLeadSeconds`. A credits marker still wins. Settings ›
  Playback › Up Next sets the global lead (5 to 300 s) and the countdown (3 to 120 s, the WPF
  range). The detail page's Up Next button on a show, season or episode (`DetailUpNextLead`) opens
  `UpNextLeadFlyout`: Global, 15, 30 or 60 s, or Custom with a 5 to 300 s number box, saved at once
  into `ShowUpNextLeadSeconds` under the WPF app's show key, so both apps share the per-show values.
  Custom saves only once its number changes, so choosing it never replaces the current lead.
- **Subtitle carry-over.** `PlayerViewModel.SubtitleChosen` (a pick in the subtitle flyout or `S`)
  is kept as a `SubtitleCarryOver` for the rest of the session: off, or language, title and forced
  flag. On each later direct-play file, `ApplyTracksAsync` matches it with the WPF app's four tiers
  (`SubtitleCarryOver.Match`): language, forced and title; language and forced; language; title
  alone. A file without a match keeps mpv's choice and is tried again on each `TracksChanged` of
  that load (external subtitles arrive late) until it matches or the user picks. The detail page's
  subtitle pick applies to the title it was made for; a pick in the player outranks it from then
  on, also after a quality or version reload of the same title. The preferred subtitle language
  applies only while nothing has been carried. A transcode keeps the server's tracks, as before.
- **Background external subtitles.** `AttachExternalSubtitlesAsync` adds the item's external
  subtitles one at a time, in the server's order, with `MpvPlayer.AddExternalSubtitleAsync`: an
  `mpv_command_async` `sub-add` whose `MPV_EVENT_COMMAND_REPLY` completes it on the UI thread
  (`SubtitleLoadRequests`), aborted after 30 s and on stop, the next load and disposal. The UI
  thread never waits on the server. `PlayerViewModel.UnavailableSubtitles` lists the ones not yet
  loaded as disabled rows in the subtitle flyout. Image streams (`IsTextSubtitleStream` false, such
  as DVDSUB) are listed as not available and never requested: Jellyfin cannot serve them as
  WebVTT and answers HTTP 400, after 14 s on a remote server. A track that arrives after
  `ApplyTracksAsync` gets the subtitle intent or carry-over, or the preferred language when that
  is what the load applied; audio is left as it is.

## v2 app zoom, live removal and offline pages

`UiZoom.Ladder` holds the shared scale preference, screen cap and wheel accumulation without a UI
framework dependency. WinUI `ZoomBox` measures its child in the available size divided by the
scale, then draws it at that scale. `MainWindow.Zoom` applies the saved preference before layout,
updates the minimum window size for the current display, and debounces saves. Zoom hosts cover
the bar, page layer, toasts and player controls; the mpv swapchain remains outside them. Zoom
chords are fixed app keys and the shortcut recorder refuses them.

`ItemRemovals.ForProfile` records removed IDs under the active session identity. Remote library
notifications prune existing pages and cached collections; pages also filter responses that
were already in flight. Requests capture a ledger revision before loading; a later removal or
re-addition rejects the earlier response. Re-added IDs are allowed in a new request. Local
pruning drops deleted titles from Advanced Search at once. A page fetched after the server
deleted a title, but before that was reported, starts too late and skips a survivor, so when
more than one page is loaded, or a page load overlapped a removal, the loaded range is fetched
again in one request and synced in place: no spinner, scroll kept. Deletions during a resync
run it once more when it ends, so a burst costs two requests. A resync skipped or replaced by a
user query stays owed until a query succeeds; an owed or failed resync keeps `HasMore` true and
the next page load retries it. `Resynced` tells the page to continue paging from the new list. The cached Home cleanup after a library
message never records the removal again, so a title re-added meanwhile stays restored. The
playback queue, Up Next and episode neighbors also discard removed targets under their playback
profile; a deleted title that is playing finishes and leaves the queue when the next one starts. A missing title uses the existing item-no-longer-on-the-server message.
A 404 does not mark the whole server unreachable.

`INavPage.CachedAtUtc` and `OfflinePlan` choose between a cached page and the existing unreachable
view. Queries no library folder holds keep their last answer in a `QueryCache`
(`LightWeaver.Core/Library`): an entry per profile, UI language and query in the metadata cache,
at most 600 titles with the server's count and when it answered, and a per-profile index that
forgets the least recently stored query past the cache's capacity. `QueryCache.Sections` holds
the See more grids (`HomeSections.CacheQuery`: one entry for a section that loads whole and
filters locally, one per filter and sort for Recently Added's visited range); `QueryCache.Searches`
holds 24 Advanced Search answers, keyed by the server query (`AdvancedSearchFilters.CacheQuery`,
without the person's display name). `SectionPage` paints the entry without a skeleton, then the
server's answer updates the cards in place through a `KeyedList`; offline it
stops at the entry. `AdvancedSearchModel` applies the entry as an answer that is still updating,
asks the server for every cached page, and stores a new query's answer after two seconds without
a newer query. Reloads are stored immediately; appended pages and resyncs are stored once the
query has settled. Entries
are filtered through `ItemRemovals` on read and write, pruned by a library message's removed ids,
and dropped with the browse cache in Settings. The shell owns one timestamped cached-results
banner and Retry now action. Home and library
cache reads use the saved profile identity during offline startup and are generation-checked
before replacing page content. `DownloadTracks` maps per-download audio/subtitle choices to each
file and to its eventual mpv tracks; these choices are saved with the download record.
The Downloads page passes the selected download record through an explicit playback route,
so the active browse server cannot replace its owner. With the owner not signed in, the file
plays with no session behind it: no metadata, negotiation or SyncPlay. A verified local file
remains playable after server deletion. Partial files retain the same track policy, and
completed episodes can load online continuation without delaying the local file. An episode
deleted while its show's episode list loads does not start. Batch subtitle matching preserves
the forced flag and skips an ambiguous counterpart. A running download's row offers Play once
`DownloadsModel.CanPlay` sees the 8 MB startup buffer, as in 1.x; it starts from the beginning
over the loopback partial stream. As 1.x's EndReached guard did,
`PlaybackController.FallBackFromPartialStream` takes over when that stream fails or ends before
the file does (ffmpeg reports a truncated or unseekable partial stream as a clean end of file, and
an MKV whose index is at its end fails that way at once), at the position reached, or where the
playback started when mpv reports the end of the file. A download that finished meanwhile
reopens its file with the same mpv tracks (the server retires the stream's token once it served
the whole file, so the next seek fails); an end of file is then a real end. Otherwise it
negotiates with the owning server for the download's media source, carrying its track choices
through `DownloadPlayback.FallbackTracks`, while the download keeps running. Without a reachable
server it stops with "The download stopped, so playback ended early." In a SyncPlay
group the partial stream is skipped and the group's item streams from the server, as in 1.x.
While it plays, the timeline ends at the title's runtime
(`PlayerViewModel.SetKnownDuration`, applied once the file loads, since a duration of 0 still
marks a file change) and its buffered range is `DownloadsModel.DownloadedFraction` of that
runtime instead of mpv's demuxer cache. `PlayerViewModel.SeekLimit` refuses a seek past what is
safely on disk (`DownloadsModel.ReachableSeconds`, padded like the partial stream's own start
check, because the loopback server answers 416 past its write head; with no size known, past the
current position) for the slider, typed time, chapter and skip-segment jumps and forward skips,
with a "still downloading" toast; it is cleared at every start. The player refreshes the
downloaded range every second, so it keeps growing while paused.
`DownloadManager.AvailabilityChanged`, raised on every state change but not on progress ticks,
keeps cards' offline badges and the detail page's Download button current while they are shown.
`DownloadTracks.Named` gives the player's track menus the language, title
and forced flag saved with a download where the file has none: embedded tracks by order (a
transcoded file's one audio track is the chosen stream), subtitle files by path.

Unit tests and static checks run before merge. The private v2 UI suites are updated for the new
controls, and run only after merge onto master and before release. No native UI verification is
claimed for the source-only development gates.

## v2 updater

v2 runs the WPF app's `UpdateService` (compiled into `LightWeaver.Core`) through one static owner,
`Services.Updater`. `App.OnLaunched` starts it right after the window is activated and does not wait
for the session restore, so an offline media server never delays it. The service is created on the
thread pool: its constructor clears stale setups and parts from `%LOCALAPPDATA%\LightWeaver\updates`
and, when a verified update is staged, hashes and Authenticode-checks it again (with chain
revocation checks, which can wait on the network). `Start` then checks at once and every six hours.
The service reports progress every 128 KiB; `Updater` keeps at most one UI-thread update queued and
it reads the newest status, so `Changed` fires on the UI thread with no backlog.

The policy is read live: Settings passes a change on through `OnPolicyChanged`, which cancels an
automatic download when switching to notify-only and resumes a found one when switching back. What
Settings › Updates shows and offers for a status and policy is `UpdatePane.For`, and when a toast
tells about an update is `UpdateNotices` (once per version: found under notify-only or without an
installer, and ready to install), both in `LightWeaver.Core/Updates` and tested in
`tests/Updates.Tests`. A notice counts as told only once it is toasted or Settings › Updates (not a
search) shows it; on Login it waits, and the window asks again after every status change and every
page shown, so an update found before sign-in is told on the first page after it. The pane updates
everything in place, "Last checked" included, so an open policy list stays open. Download and
install, and Install now, queue the installer and close the window; the installer arguments are the
WPF app's (`UpdateInstallerLauncher`). At launch, in both apps, the service opens the staged setup
read-only with `FileShare.Read`, hashes it through that handle, checks its signature and starts it
while still holding it, so it cannot be replaced or deleted between the check and the start; a setup
it cannot open is discarded.

On the window's `Closed`, after the handlers that dispose playback and SyncPlay and save the
window placement, `Updater.OnExit` starts the installer when Install now queued it or the
automatic-install policy has a staged update, then disposes the service. A subclass on the window
sets `Updater.SessionEnding` on `WM_QUERYENDSESSION` and clears it when `WM_ENDSESSION` says the
session goes on, so a logoff, a shutdown or the Restart Manager closing the app never starts an
installer, as WPF's `SessionEnding` does.

## SyncPlay drift and group audio

Between commands, `SyncPlayScheduler.ResolveDrift` compares the player with where the last
`Unpause` says the group is and seeks once the gap passes 400 ms. That reference is only valid
while the group plays to it. When this member buffers, Jellyfin moves the group to `Waiting` and
pauses the other members at the position where the buffer began, but sends this member nothing
but the state update. When it reports ready, `WaitingGroupState` sends the resume `Unpause` to
every other member (`AllExceptCurrentSession`) whenever the delay exceeds twice the highest ping,
and takes this member's position as the group's. So drift correction runs only while the
group's state is `Playing`; a `Waiting` that interrupts the last `Unpause` marks it stale, and the
next `Playing` update anchors the reference at this member's own position and the server instant
(`SyncPlayDriftAnchor`). A newly applied command clears the anchor, a restored membership keeps
it, and the binding reads command, state and anchor from one membership snapshot
(`SyncPlayClient.DriftState`). Before this, a 2.95 s buffer produced a 2958 ms forward seek past
the cache, a second buffer, and a server `Seek` back.

A drift seek on a transcode takes 1 to 2 s to play again, and the group moves on meanwhile, so
measuring again right after it lands found the member behind once more: five seeks in 6 s on
Oct 29. `SyncPlayPlayerBinding` now waits for the drift seek's playback restart (10 s at most)
before measuring again, counts `MinDriftInterval` from that restart, and leads the next target by
how long the last drift seek took to play (`SyncPlayState.DriftSeekLead`, at most 3 s), so the
second seek lands where the group is.

The audio-sync repair judges audio stuck when video advanced while audio-pts stayed unreadable.
A seek moves time-pos too, and a paused seek leaves audio-pts unreadable, so a SyncPlay seek
right after a load read as exactly that failure and reloaded the audio track mid-seek.
`MpvPlayer.VideoPlayedSince` now counts as advance only playing time: not paused and no
`MPV_EVENT_SEEK` between the two readings, in the check and in its verify pass.

In a group, a click on the video waits out the system double-click time before it pauses or
plays, and a second press inside it cancels the click, so a double-click for full screen posts
no pause and unpause to the group. Alone, the click still acts at once.

Volume normalization is `dynaudnorm`, whose defaults (500 ms frames, a 31-frame window) emit
nothing until about 16 s of audio is queued, so every start and seek first has to download 16 s
of the file. A SyncPlay member reports ready only after its playback restart, so on a remote link
the whole group waited on it. `MpvPlayer.NormalizesAudio` leaves the filter out for a group's
item (`SetGroupPlayback`, set by the WinUI controller from `PlayForGroup` before each load); the
setting itself is unchanged and applies again outside the group. A retry of a failed group item
goes back through `SyncPlayCoordinator.RetryGroupItem`, so it keeps both the group binding and the
bypass.

## One device per signed-in profile

Jellyfin ends a device's other sessions for the user when it signs in again, and the app used to
send one install-wide device id for every profile. Two profiles of one user on one server, a LAN
and a public address say, signed each other out: a 401 on one, then a 403 on the other's socket
right after signing in again. Every sign-in now uses `JellyfinService.ForSignIn`, a service with
a device id of its own, which the saved profile keeps (`SavedCredentials.DeviceId`). Reconnecting
a saved profile speaks as that id (`CredentialStore.DeviceIdFor`); a profile saved before it has
none and keeps the shared id its token was issued to.

## Server-supplied input and local secrets

A Jellyfin server, or anyone who can change plain-HTTP traffic to one, controls item metadata,
playback info and session messages. These rules keep that data from choosing files, programs,
hosts or memory on this machine (2026-10 security review):

- **Download paths.** `DownloadPaths.Extension` reduces the server's container value to at most
  12 ASCII letters and digits. `DownloadManager` refuses to queue, write or delete a path that is
  not fully qualified, has a `.`/`..` segment, or names a file that does not start with the item
  id.
- **Shell links.** Only absolute `http`/`https` links with a host and no user info
  (`WebLinks.TryParse`) reach `ShellExecute`, or the v2 app's `Launcher.LaunchUriAsync`; the
  Trailer button hides for anything else.
- **mpv URLs.** Subtitle `DeliveryUrl` and `TranscodingUrl` go through
  `JellyfinService.SameOriginUrl`: resolved against the server address, kept only with its exact
  scheme, host and port, and passed on as the normalized URI so .NET and ffmpeg agree on the host.
  The session token is an mpv-wide header, so this is what keeps it on the server. mpv runs with
  `ytdl=no`, and release builds load only the bundled `libmpv-2.dll`. `load-scripts` stays on:
  turning it off broke the track flyout over the video, and a user script already needs write
  access to the user's own mpv folder.
- **Image tokens.** The image cache and the v2 `ImageLoader` send a token only when a signed-in
  session owns the URL (`JellyfinService.OwnsUrl`): same scheme, host and port, no user info,
  under its base path at a segment boundary. Anything else is fetched without one. In both apps
  responses stop at 32 MiB after decompression, a decode that would exceed 40 megapixels at the
  requested width is refused from the frame header, and no image is decoded wider than its source
  (`ImageLimits`).
- **Bounds on server numbers.** A SyncPlay unpause pre-roll is capped at 30 s and computed in
  server time, so no `When` overflows the timer or the date conversion. Trickplay buckets need
  1-4096 px thumbnails and 1-100 tiles per side. A remote `Play` keeps at most 500 ids around its
  start item; a dashboard message is one line of at most 300 characters and names its server
  (`RemoteCommand`, used by both apps).
- **Sign-in.** A bare server address is completed with `http://`, so the login form shows the
  plain-HTTP warning for it as well. An explicit logout, and the v2 sign-out, first removes that
  session's own profile, closes the session and shows Login or the next profile; only then does
  `/Sessions/Logout` go out, with the token captured beforehand (best-effort, 3 s, not awaited),
  so a slow server cannot leave a second sign-out or a switch acting on stale state. The v2
  profile menu runs switches and sign-out under the same account gate as the WPF shell. Release
  builds only: the Debug profile holds a copy of the installed app's credentials.
- **Updates.** Besides the pinned signer, the installer's signed product version must equal the
  release version, so an older signed installer cannot ride a newer tag. `release.ps1` runs the
  same checks before it uploads.
- **Local secrets.** The OMDB key is stored as a DPAPI CurrentUser blob (`OmdbApiKeyProtected`),
  like the Jellyfin credentials; a plain-text key from an older version is sealed on the next
  save. `LIGHTWEAVER_MPV_LOG` works in Debug builds only, because mpv's log file records the
  header change that carries the token. "Copy stream URL" still includes the token by design,
  since external players need it.

`PlaybackHarness --security` asserts these rules without a window or a server.

## Diagnostics and log privacy

Failure lines in app.log record `error=<ExceptionType>` and never an exception message or object.
An HTTP or IO message can name a server URL or a file path, a download's filename comes from the
media title, and redaction keys on secret names, so it masks neither. Login and saved-session
failures add the HTTP `status=` when the server answered, which separates a dead token (401) from
a broken server. The user-facing text (toasts, status lines, the mpv init alert) still shows the
message. Crash files written by `AppLog.Crash` remain the exception and keep the full exception.
Both apps write one for an exception from the UI thread (XAML's `UnhandledException` in v2),
`AppDomain.UnhandledException` and `TaskScheduler.UnobservedTaskException`, which alone is marked
observed, and flush queued mpv log lines and the image cache summary on exit (v2: when its window
closes).

The `LIGHTWEAVER_CACHE_LOG` and download test-hook logs retry an append up to five times with a
short backoff (10 to 40 ms) on a sharing or lock violation, so a second process or a reader
holding the file delays a line instead of losing it; the download log also serialises appends
from concurrent downloads. Cache temp files are named
`{path}.{processId}.{threadId}.{guid}.tmp`, so two instances sharing the folder cannot truncate or
promote each other's partial write. The image cache uses the same naming and deletes its `.tmp` on
a failed write or move; eviction, the size readout and Clear only enumerate `*.img`, so a leftover
was never cleaned up.

## Translation editor

`tools/TranslationEditor` is an ASP.NET Core app bound to IPv4 loopback on port 5386. Its static
UI loads the catalog and language columns, supports URL filters, and saves patches for edited
cells. Translation files stay in the checkout and are included in subsequent app builds.
The editor interface uses the same catalog and reviewed English/German/Arabic packs. `ui=ar`
selects an Arabic interface separately from `lang=ar`, which filters translation columns.
The interface language persists locally; switching it preserves filters and unsaved drafts.
Arabic fields use RTL even under an English interface, while source text and invariant keys
retain LTR direction. The localization audit checks the editor's JavaScript and HTML key references.

Every `/api` request needs the `X-Translation-Editor-Session` header; without it the editor
answers 401. The session secret is 32 random bytes kept in
`%LOCALAPPDATA%\LightWeaver-TranslationEditor\session-token`, which other Windows accounts cannot
read, so another user on the machine cannot write translations through the loopback port. At
start the editor prints and opens `/#session=<secret>`. The fragment never reaches a server;
`editor.js` moves it into `localStorage` and removes it from the address bar. It is not a cookie
because cookies ignore the port: any other server on `127.0.0.1` would receive one. Storage is
per origin, port included, and the secret survives restarts, so that browser keeps working with
plain `?changed=` links.
Writes also require JSON and the editor's custom header, with same-origin checks when Origin is
present. Language codes are validated against .NET cultures. Saves validate the file revision,
source hashes and composite placeholders before replacing a language file atomically. On a
conflict, the browser retains drafts for review. The changed filter compares the current catalog
and translations with a validated Git commit. Git output is decoded as UTF-8, including when
the editor runs without a console, so Unicode translations remain outside the changed filter
unless their content changed. `working-tree` means HEAD; use a full base commit
for a handoff link that remains useful after committing.
Reload, saving and language creation lock conflicting editor actions until their requests finish.

## Maintainer end-to-end orchestration

The companion verify skill's `v2-tests/run-all.ps1` builds the WinUI Debug app once with
`-warnaserror`, then passes `-SkipBuild` to nine isolated PowerShell entry points. Four groups
share a signed-in app session: Home/Library, Detail/Player, Search/Advanced Search and
Settings/Layout. Login, migration, downloads, playlists and SyncPlay retain separate processes
and cleanup. Standalone entry points build by default; screenshot capture stays separate.

Each merged scenario prints its result and elapsed time. A failed assertion, exception or app
exit causes a fresh session before the next scenario, while preserving the aggregate failure.
The media group restores the movie's preselection choices before playback. Minimum-size page
checks run within the scenarios that already visit those pages; the preferences group checks
the minimum window and Arabic mirroring. The discovery group also verifies command-line
Advanced Search startup before returning Home for Search's initially empty history checks.

The companion's `verify/tools/check-v2-orchestration.ps1` checks parsing, shared-session recovery,
cleanup, environment restoration and the full runner's build/failure behavior with synthetic
child scripts. It launches no app and uses no UI Automation or credentials. Real UI suites run
on the Hyper-V guests before a release through the release workflow, or as an explicitly
requested one-off run through the VM queue.

## Release packaging (2.0)

2.0 ships the WinUI 3 app. `src/LightWeaver.WinUI` builds as `LightWeaver.exe` (AssemblyName
`LightWeaver`, Product and title `LightWeaver`, the app icon embedded and set on the window), the
1.x exe name, so taskbar pins, GPU driver profiles, the process name and the installer's exe carry
over. The WPF project's assembly is also `LightWeaver`; no project references both, so each keeps
its own `bin` folder, and only the WinUI project is published.

- **One version.** `Directory.Build.props` holds it for every project. The scripts read it there;
  the update check (`GitHubUpdateClient.CurrentVersion`) and the Jellyfin client version read
  `LightWeaver.Core`'s assembly version, and the log reads the exe's. `tests/Updates.Tests`
  compares the update client's version with the file.
- **Publish.** `EnableMsixTooling` is on so that `dotnet publish` copies `LightWeaver.pri`; without
  it the publish folder has no PRI and the app cannot load `App.xaml`. Release builds embed the
  compiled XAML (`.xbf`) in that PRI, so the publish folder holds no loose `.xbf` files.
- **Signing.** `publish.ps1` signs `LightWeaver.exe`, `LightWeaver.dll`, `LightWeaver.Core.dll`
  and `LightWeaver.Localization.dll`. The exe's signature is the updater's trust anchor
  (`UpdatePackageVerifier`).
- **Installer.** `MinVersion=10.0.19041` (Windows 10 2004, the app's `TargetPlatformMinVersion`).
  AppId, install folder, `[InstallDelete] {app}\*` and `CloseApplications` are unchanged from 1.x,
  so 2.0 replaces 1.7.0 in place. `previous-version.txt`, written by `[Code]`, is removed on
  uninstall through `[UninstallDelete]`.
- **From 1.x.** The window placement is saved in `window-placement.json`; 1.x's `window-state.json`
  is not migrated, so the first 2.0 start opens at the default 1440 x 900 frame once. Settings,
  sign-in, caches and downloads carry over. A revert to 1.7.0 loses the OMDb key: 2.0 saves it
  only sealed (`OmdbApiKeyProtected`), and 1.7.0 reads only the plain-text value.

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

## Download resume

A resume sends `Range: bytes=<on-disk length>-`. A 206 whose `Content-Range` starts at a different
byte is a body for another offset; appending it built a file of the wrong bytes, and the
end-of-transfer check only catches a short file, so it was marked Completed and preferred over the
server. `DownloadManager` now discards that response and restarts once from byte 0 without a
Range, the same single restart a 416 gets, logging `event=range_misaligned outcome=restart` with
the requested and returned offsets. `PartialStreamFixture` covers a server that ignores Range
(200 with the whole file) and one that answers 206 from byte 0 whatever was asked.

### Card row paging

WinUI card rows use ScrollView compositor scrolling. Their row automation IDs belong to the loaded ScrollPresenter, which exposes the scroll pattern, so row-scoped automation finds the cards and paging state. Chevron clicks target a page of whole
cards and cap the animation at 180 ms, with the system motion preference respected.
CardRowPaging tracks the requested destination and scroll correlation ID so rapid clicks
accumulate and stale completions cannot clear a newer request. Layout and content changes
clear the pending destination; native touch scrolling and focus navigation can interrupt paging.

ScrollView ignores native MouseWheel input. CardRow handles unhandled PointerWheelChanged events
across the cards, header and transparent gaps, leaving Ctrl+wheel to interface zoom. Vertical
wheel down advances in reading order; horizontal wheel follows physical left/right, reversing
the offset delta in RTL. Each wheel delta unit moves one DIP, retaining high-resolution input.
Wheel and chevron requests share the pending destination, correlation tracking and motion policy.
Requests already at that destination do not replace the in-flight operation. Overflowing rows
consume wheel input even at their boundaries; rows without overflow let the page scroll.
Host logic and FastUi checks cover the destination math and source wiring. UI Automation
ScrollPattern coverage does not exercise physical wheel routing; that remains a runtime gate.

## Home section controls

`HomePage.Sections.cs` exposes the existing `HomeLayoutStore` preferences directly on Home.
The customize button and rows share a grid inside the page scroller; the first visible
row reserves trailing header space for it. The button stays rooted while rows collapse,
so switches can hide every row without detaching the flyout anchor. The flyout lists the
current layout order, filters out the legacy Libraries row, and saves individual toggles
without replacing other profile choices. Settings changes are applied on Home re-entry.

`HomeSectionDrag` handles section drag/drop in both Home and Settings. Dedicated WinUI Thumb controls capture pointer dragging locally,
so section movement never enters the shell file-drop handler or an operating-system drag loop.
Each editor tracks its own active source, calculates insertion from row midpoints, cancels on
Escape, canceled capture or unloading, and rechecks the active profile before saving. `HomeSectionOrder.Move` preserves toggle states
and the legacy Libraries slot. The same handles support Alt+Up/Down for keyboard reordering.
