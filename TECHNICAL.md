# LightWeaver — Technical Documentation

## Stack

- **UI shell:** C# / .NET 10, WPF with the built-in Fluent theme (`ThemeMode="Dark"`,
  `WPF0001` suppressed while the API is marked experimental)
- **Player core:** libmpv (mpv ≥ 0.40), embedded as a **child HWND** via mpv's `wid` option
- **Server API:** Jellyfin via the official `Jellyfin.Sdk` NuGet (see the Jellyfin layer below)

## The one load-bearing design decision

**mpv owns its rendering window and swapchain.** LightWeaver does *not* composite video through
WPF's render pipeline (no `D3DImage`, no render-API-to-texture). Instead a child HWND is
handed to mpv (`wid`), and mpv renders with `vo=gpu-next` on D3D11.

Why: RTX Video HDR requires an HDR-capable D3D11 swapchain, and HDR passthrough in general
only works when mpv controls presentation. Any texture-sharing/composition approach loses
HDR and adds latency. This mirrors how mpv.exe itself and MPC-style players work.

Consequences:
- The video HWND punches through WPF (classic airspace limitation). Playback controls must
  be drawn either in a separate transparent top-level window positioned over the video, or
  as native chrome outside the video rect. This is exactly how the OSD is built
  (`Views/OverlayWindow`, below).
- The WPF shell never touches frames; UI jank and video playback are fully decoupled.

## RTX feature wiring (mpv options)

### Playback source and lifetime additions (2026-09-05)

- `MainWindow.Streaming.cs` navigates to the `OpenStreamView` UserControl from
  `NavStream` (below the sidebar divider, above Settings) or the rebindable
  `PlayerAction.OpenStream` shortcut while browsing (default Ctrl+L). Settings
  uses the existing generated capture row and KeyBindings persistence, including
  Escape cancellation, conflict takeover and reset. The old fixed Chrome binding
  is removed; the browse resolver, sidebar tooltip and shortcut help follow the
  saved chord. Bare character bindings do not intercept text editing.
  Repeated invocation focuses the existing page.
  `StreamAddress` accepts absolute HTTP(S) addresses without user-info; invalid
  input keeps the page open. Submission and Unloaded clear the field so navigation
  history cannot retain a pasted signed link. `PlayUrl` remains the shared
  external-source path. Invocation during playback does nothing, like Settings.
  Network display names contain only the decoded final path component or host.
  App-managed diagnostics strip all HTTP(S) queries, fragments and user-info,
  including provider-specific signed URL parameters. The explicit test hook
  `LIGHTWEAVER_MPV_LOG` writes raw mpv output and bypasses this filtering.
- `AppSettings.VideoBufferMiB` clamps persisted values to 32–1024 MiB, default 128.
  `MpvPlayer.Create` applies the byte budget as `demuxer-max-bytes` and sets
  `cache-secs=3600`; mpv's `cache=auto` source detection remains in force. Settings
  applies changes to an existing player through `ApplyVideoBuffer`, as well as
  persisting them for future instances. The budget limits forward demuxed packets,
  not total process memory or an exact number of playback seconds.
  Settings presents Recommended (128 MiB), Less memory (32 MiB), More read-ahead
  (512 MiB), and Custom. Numeric entry lives in Advanced video buffer; persisted
  non-preset values select Custom without saving during initialization. Invalid
  custom input displays the valid range and retains the last saved value.
  Guidance explains that read-ahead can smooth brief network dips but cannot
  repair a consistently slow connection.
- A closed overlay is detached from `MainWindow`; bounds synchronization returns
  while its owner closes. This prevents a pending visibility/position callback
  from trying to show a permanently closed WPF window. A cancelled owner close
  re-enables synchronization.
- Debug tests can set `LIGHTWEAVER_TEST_APPDATA_ROOT` to an absolute isolated
  directory. Release ignores the variable. This separates settings, credentials,
  downloads and device identity across test runs and worktrees.

`tests/PlaybackHarness` verifies actual mpv option readback and exercises the real
WPF overlay lifetime. Its closed-overlay case reproduced the archived
`SyncOverlayBounds` exception before the repair and passes afterward.
`verify/playback-tests/test-buffer-stream-url.ps1` verifies settings persistence,
URL validation, HTTP playback, seek and display privacy using a loopback fixture
and the worktree-specific guarded Debug close. The 2026-09-05 user-authorized
desktop suite now covers 29 assertions, including shortcut capture, cancellation,
removal of the old chord, live application, restart persistence, conflict unbinding
and reset, as well as preset transitions, incremental
numeric entry through preset prefixes without collapsing the editor, invalid
custom input, shortcut discoverability, same-window URL entry, clearing pasted
links across back/forward navigation, and actual normal-log privacy checks.
`PlaybackHarness --remote-buffer` separately verified playback against the remote
server named by `LIGHTWEAVER_REMOTE_SERVER` (the fixture skips when that variable
is unset) using the TEST account after matching its public server ID to the saved
LAN endpoint.
Playback reached 5.1 seconds with a 32 MiB limit and 33,608,624 forward cache bytes
observed (mpv may exceed the byte target by a packet). Credentials stay in memory
and travel in an authorization header; the fixture prints only numeric evidence.
The larger scope is planned outside this repository.

### RTX configuration

- Hardware decode: `hwdec=d3d11va` (required — `d3d11vpp` operates on D3D11 hardware frames)
- RTX Video Super Resolution: `vf=d3d11vpp=scale=2:scaling-mode=nvidia`
- RTX Video HDR: `vf=d3d11vpp=nvidia-true-hdr`
- Both combined: `vf=d3d11vpp=scaling-mode=nvidia:scale=2.0:nvidia-true-hdr`

Toggled at runtime via `vf set <filter>` / `vf clr ""` commands (`MpvPlayer.SetRtxFilters`),
surfaced as toggle buttons on the overlay. NVIDIA-only and driver-dependent; command failures
are reported through the player's log event, not thrown.

Verified on an RTX 5070 Ti with libmpv 0.41: every toggle combination logs
"NVIDIA RTX Super Resolution enabled" / "NVIDIA RTX Video HDR enabled", hwdec stays on
d3d11, playback continues through filter changes.

**HDR output measured at the display (2026-08-01,
`verify/visual-tests/test-hdr-output.ps1`):** with Windows HDR on, the app window's
composited FP16 values peak at 2.97 linear scRGB (~238 nits, the boosted SDR presentation)
with RTX HDR off and 5.17 (~415 nits) with it on — a 1.74x peak rise, so the filter's
output genuinely leaves the window as HDR rather than being tone-mapped back to SDR.
Capture foot-guns the suite exists to encode: WGC **window** capture only (monitor capture
measures whatever overlaps the parked rect on a live desktop); the legacy
`IDXGIOutputDuplication` returns SDR-tone-mapped 8-bit on HDR desktops on current Windows
(and `DuplicateOutput1` fails E_FAIL), so the probe is Windows.Graphics.Capture with an
explicit `R16G16B16A16_FLOAT` frame pool (`verify/tools/HdrProbe`); and the assertion is
the OFF→ON **ratio**, because SDR white on an HDR desktop is already boosted well above
scRGB 1.0.

**HDR sources are guarded**: NVIDIA's video processing supports SDR input only — on
PQ/HLG content the d3d11vpp NVIDIA path outputs a solid green frame (found live with a
Dolby Vision movie). `MpvPlayer` observes `video-params/gamma` and applies the requested
RTX chain only to SDR content (and only once the gamma is actually known, so PQ frames
never touch the filter); the toggles stay selectable but log a suppression message. Note for the settings screen: mpv tags
RTX Video HDR output as 1000-nit max-luma *by guess* and suggests
`--vf-add=format=max-luma=<value>` to match the NVIDIA control panel value — exposed since
Phase 3 as the RTX "peak brightness" setting (appended to the filter chain when HDR is on).

Diagnostics env vars: `LIGHTWEAVER_MPV_LOG=<path>` (verbose mpv log to file),
`LIGHTWEAVER_MPV_MUTE=1` (start muted — automated test runs),
`LIGHTWEAVER_UI_LOG=<path>` (overlay input event log — and, since Phase 10 M3, the
`OsdGeometry` letterbox-margin lines from `MpvPlayer`, deliberately in the same stream),
`LIGHTWEAVER_STARTUP_LOG=<path>` (startup ordering: `App.OnStartup` enter/exit, the
`MainWindow` ctor incl. the Debug title marker, `OnSourceInitialized` — added to diagnose
B29 and kept, since startup ordering is otherwise unobservable),
`LIGHTWEAVER_FAKE_GPU=<adapter name>` (simulate the display adapter for the RTX
capability probe — the no-RTX paths can't be tested on RTX hardware).

## Player core implementation

### libmpv bindings (`Player/Native/`)

Source-generated P/Invoke (`[LibraryImport]`, `StringMarshalling.Utf8`) against the libmpv
client API. Rules inherited from the predecessor and mpv's client.h contract:

- All options — **including `wid`** — are set before `mpv_initialize`.
- Strings returned by mpv arrive as `nint`: heap strings (`mpv_get_property_string`) are read
  with `PtrToStringUTF8` then freed with `mpv_free`; `mpv_error_string` is static — never freed.
- `mpv_command` takes a NULL-terminated UTF-8 `char**`; `LibMpv.Command(ctx, params string[])`
  builds it (CoTaskMem allocations freed in `finally`).
- Library discovery: a `DllImportResolver` tries the bundled `libmpv-2.dll` next to the exe
  first (`AppContext.BaseDirectory`), then falls back to the default PATH search. The dll is
  copied from `native/win-x64/` at build time when present (gitignored, ~112 MB).

### Threading model (`Player/MpvPlayer.cs`)

A dedicated background thread ("mpv-events") blocks in `mpv_wait_event(-1)` and exits on
`MPV_EVENT_SHUTDOWN`. State reaches WPF via `Dispatcher.BeginInvoke`; all public events raise
on the UI thread. `time-pos` updates are coalesced (latest-value-wins, at most one pending
dispatch) so mpv's high-frequency property changes can't flood the Dispatcher queue.

Observed properties (typed formats, no string parsing): `time-pos`/`duration`/`volume` as
`MPV_FORMAT_DOUBLE`, `pause`/`mute`/`eof-reached`/`paused-for-cache` as `MPV_FORMAT_FLAG`
(`paused-for-cache` → `IsBuffering` drives the OSD buffering ring, M26), and
`video-params/w`/`h` as `MPV_FORMAT_INT64` (with `video-params/gamma` and the RTX
actually-applied flags they raise `VideoStateChanged`, which drives the OSD status badges).
Phase 8 M7.1 consolidated those badges: RTX-active state (HDR + VSR) is shown ONLY by the
garnet gem toggles (top-right), so the passive blue status pills under the title carry just
what the toggles can't — native-source "HDR" (`StatusHdrLabel`, PQ/HLG only) and the source
resolution. The duplicating "RTX HDR"/"RTX VSR" pills were removed.
A property-change
event's data pointer is null when the property is unavailable (e.g. between files) — guarded
(width/height reset to 0 so the resolution badge hides between files).
`mpv_node` marshalling is deliberately deferred until track selection needs `track-list`.

**Letterbox geometry from `osd-dimensions` (Phase 10 M3).** `osd-dimensions/mt`, `/mb` and `/h`
are observed as `MPV_FORMAT_INT64` **leaf sub-fields** — mpv's property machinery resolves
`a/b` paths and notifies observers of a sub-field when the parent map changes, so this needs no
`mpv_node` marshalling (which stays deferred, above). They surface as `MpvPlayer.OsdMarginTop`
/ `OsdMarginBottom` / `OsdHeight`, the derived `BottomBandFraction` (`mb/h`), and the
`OsdGeometryChanged` event. Why mpv's numbers rather than `video-params/w`+`h` against the
window size: `osd-dimensions` describes the picture **actually drawn on screen**, so it already
accounts for anamorphic PAR, cropping, rotation and the RTX `d3d11vpp=scale=2` upscale — source-pixel
arithmetic misses all four and would report a band that isn't there.

- **Unit rule, load-bearing: these are mpv-side PIXELS and must never be mixed with WPF DIPs.**
  `OverlayWindow` works in DIPs, so mixing silently multiplies by the display's DPI factor.
  Consumers should reason with `BottomBandFraction`: a ratio of two mpv pixel values is
  unit-consistent and DPI-independent, so it stays valid across monitors, scale factors and
  window sizes. Measured (render area 1600×900, 2.39:1 fixture): `mt=115 mb=115 h=900`,
  `mb/h = 0.1278` against a computed expectation of `(1 - 1.7778/2.3881)/2 = 0.1278`; the same
  clip in a 4:3 area gives `0.2218` vs `0.2208` expected. A 16:9 clip in a 16:9 area reports
  `mt=mb=0`.
- **The three leaves are coalesced** (latest-value-wins, at most one pending dispatch — the
  `time-pos` shape). mpv sends one property-change event per leaf microseconds apart, and
  applying each on its own dispatcher operation published geometry that never existed on screen:
  measured `mb=115` against a stale `h=1`, i.e. a "band fraction" of 115. Before the VO
  configures, mpv reports `h=1` with zero margins — harmless (the fraction is 0), but it is why
  nothing may treat a single leaf as a complete geometry.
- Applied via `Post(...)` to the dispatcher like every other observation, because it writes
  public state and raises an event and this class promises both happen on the UI thread
  (BUGS.md **B5** is the already-fixed bug from breaking exactly that). The null-data case
  (between files) zeroes the affected leaf, as the `video-params` handler does.
- Diagnostics: margin changes append to the **`LIGHTWEAVER_UI_LOG`** stream —
  `OsdGeometry mt=<px> mb=<px> h=<px> band=<mb/h>` — deliberately the same file
  `OverlayWindow` writes its input events to, so one tail shows both what the app believes the
  band is and what the overlay did about it. No new UI.
- **Diagnostics only — the intended placement consumer never materialised.** M3 was built as
  plumbing for M4's band placement, and M4's calibration then found there is nothing for a
  consumer to compute (see *Subtitles in the letterbox band* below): `sub-pos` cannot address the
  band at all, and the placement mpv does perform is anchored to the render surface and does not
  vary with band height, so no app-side geometry feeds it. `OsdGeometryChanged` is therefore
  subscribed by nothing, and `UpdateSubtitlePosition` is unchanged by both M3 and M4. What the
  margins are still good for is what this suite actually uses them for: telling a test — or a
  human reading the log — where the picture really is on screen, which is the only reliable way
  to assert "the subtitle is below the picture" and the only source that survives RTX upscaling.
  Kept deliberately; not to be mistaken for a wired-up feature.

Shutdown ordering (client.h contract — never destroy while a thread waits on events):
`Dispose` sends `quit` (fallback `mpv_wakeup`) → joins the event thread → `mpv_terminate_destroy`.

Fixed init option set: `vo=gpu-next`, `hwdec=d3d11va`, `osc=no`, `input-default-bindings=no`,
`input-vo-keyboard=no`, `volume-max=200`, `keep-open=yes` (EOF holds the last frame instead of
tearing down; end-of-media is detected via the observed `eof-reached` flag).

### Video host (`Player/MpvPlayerHost.cs`)

An `HwndHost` whose `BuildWindowCore` creates a plain `"static"` child window
(`WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS`) and raises `HwndReady` — guaranteeing the HWND
exists before mpv receives it as `wid` (`MPV_FORMAT_INT64`). `Focusable=false` so keyboard
input stays with the shell.

### Overlay controls (`Views/OverlayWindow.xaml`)

Because the video HWND punches through WPF airspace, the OSD is a separate owned window:
`WindowStyle=None`, `AllowsTransparency=True`, `ShowInTaskbar=False`, `Owner=MainWindow`
(ownership keeps z-order above the video without Topmost hacks). Its root grid has an
alpha-1 background (`#01000000`) — invisible but hit-testable — so the overlay receives
**all** mouse input over the video. That one trick provides idle detection for auto-hide
(2.5 s fade), click-to-pause, double-click fullscreen, and drag-drop (the child HWND would
otherwise swallow drops).

Two coupled details make the idle hide actually hide (BUGS.md B4). An Opacity-0 element is
still fully hit-testable in WPF, so the fade-out's `Completed` also drops `IsHitTestVisible` on
the faded layer (ControlsPanel/TitlePanel/BackButton/GemPillPanel) and `ShowControls` restores
it — otherwise a click into the apparently-empty video can hit an invisible Back button or RTX
toggle, and click-to-pause is swallowed by `ControlsPanel.IsMouseOver`. But changing
`IsHitTestVisible` makes WPF re-run hit-testing, and that **synthesises a MouseMove**: taken as
user activity it woke the OSD ~1 ms after it faded, so `OnRootMouseMove` ignores a move whose
pointer position is unchanged. Note also that `HideControls` refuses to fade while the pointer
is over ControlsPanel/BackButton/GemPillPanel, and the idle timer stops itself after firing —
so a faded control can only end up under the pointer via a later layout change.

MainWindow syncs the overlay to the video rect in `SyncOverlayBounds()` — driven by
`LocationChanged`/`SizeChanged`/`StateChanged`/`DpiChanged`/visibility events **and by
`WM_WINDOWPOSCHANGED` in the `OnWindowSizing` hook**, converting `PointToScreen` physical pixels
to DIUs via `VisualTreeHelper.GetDpi`. The Win32 message is the load-bearing one for moves: at
`LocationChanged` time `PointToScreen` still reports the *pre-move* origin, so a move-only
reposition (Win+Shift+arrow, a window utility, an external `SetWindowPos`) computed the old
position and nothing re-ran it — the OSD stayed behind, detached from the video (BUGS.md B11).
`WM_WINDOWPOSCHANGED` arrives after the move is in effect; the hook does not mark it handled, so
WPF still gets it. Because that fires per drag step, the sync no-ops when the computed rect is
unchanged (each write is a `SetWindowPos` on the overlay HWND) — that skip, and the four writes,
now live in `OverlayWindow.SetBoundsFromOwner`, which also records the origin it produced (in
PHYSICAL pixels) for the hook below. Fullscreen = `WindowStyle.None` + `WindowState.Maximized`
(menu collapsed), toggled through `Normal` so an already-maximized window re-measures.

**An external move aimed at the OVERLAY drags the owner (2026-09-05, BUGS.md B42)** — the mirror
image of B11, and the reason the fix for B11 alone did not settle the question. The overlay covers the whole
video, so it is routinely the foreground window after any click on it (`test-shortcut-focus`
reports `foreground = 'overlay'` after all three of its interaction paths). A shell gesture aimed
at "the current window" — `Win+Shift+Arrow` to the next monitor, a window-management utility —
therefore issues its `SetWindowPos` against the OVERLAY hwnd, and nothing observed the overlay's
own position: the OSD walked off to the next monitor and the player stayed put. So
`OverlayWindow.OnSourceInitialized` adds a second `HwndSource` hook, `ExternalMoveHook`, on
`WM_WINDOWPOSCHANGING`. It ignores, in order: `SWP_NOMOVE` (z-order and size-only work, including
`EnsureAboveOwner`); no recorded expectation yet; an origin within 1 px of where
`SetBoundsFromOwner` last put the window — our own sync; `SWP_STATECHANGED` or the `-32000`
minimize sentinel; and
a proposed rect whose centre is on no monitor. What is left is external: the overlay raises
`ExternalMoveRequested` and then **vetoes its own move** by OR-ing `SWP_NOMOVE | SWP_NOSIZE` into
the `WINDOWPOS` and writing it back (the same `Marshal.StructureToPtr` trick the owner's
mini-player clamp uses). `MainWindow.MoveWithOverlay` answers it three ways, and the one log line
`OverlayExternalMove to=<x>,<y> owner=<Normal|Maximized|Mini> action=<translate|remaximize|ignore|skipped>`
names which: **translate** while `Normal` (mini included) — the owner moves to
`proposed − inset`, both rects read with `GetWindowRect`, and the owner's own
`WM_WINDOWPOSCHANGED` then puts the overlay exactly on the proposed origin; **ignore** while
maximized or fullscreen and the proposal is on the same monitor — the veto is the whole answer,
because dropping a maximized window out of its state is not what the gesture means; **remaximize**
when it is on another monitor — restore, place the restored rect at the same offset inside the
target monitor's work area (clamped), maximize again, `_isFullscreen` untouched; and **skipped**
when the owner could not act at all (no overlay, a re-entrant call, an HWND that does not exist
yet) — a distinct value because it and `ignore` look identical from outside and only the log tells
them apart. Re-entrancy is held off by `_movingOwner`; there is no DPI arithmetic anywhere in the
path, because the owner's `DpiChanged` re-syncs after a move across a scaling boundary.

The expectation the hook compares against is read back with `GetWindowRect` after
`SetBoundsFromOwner`'s writes, not scaled from the DIUs it was handed: `VisualTreeHelper.GetDpi`
reports the primary monitor's scale until the window has an HWND, and on a desktop that mixes 150%
and 100% the scaled origin can miss the real one by more than the 1 px tolerance — which would
make the first show read as an external move. A scaled value is still written *before* the writes
as an interim, since WPF applies `Left` and `Top` as two separate `SetWindowPos` calls (the first
lands at (new left, OLD top), which is what `_settingBounds` covers). While there is no HWND the
expectation stays null, i.e. the hook's "no expectation yet" case above.

*Accepted caveat:* a **snap** gesture on the overlay (`Win+Left`/`Win+Right`) moves the owner by
the proposed origin and IGNORES the proposed size, so the window lands at the snap position at its
old size rather than filling half the monitor. Honouring the size would mean re-deriving an owner
rect from a video-area rect — the inverse of `SyncOverlayBounds`, chrome and all — for a gesture
whose real target is the shell window; the same gesture on the main window still snaps normally.

**The OSD fullscreen button (2026-08-05)** is a fourth route into the same `ToggleFullscreen` —
F11, a double-click on the video and Escape were already there, and only the keyboard and the
double-click made it discoverable. `OverlayWindow` raises `FullscreenRequested`; the owner routes
it, exactly as `ShortcutsRequested` and `MiniPlayerRequested` are routed, so all four paths share
one implementation. It is declared **first** among the `DockPanel`'s right-docked children, which
puts it rightmost (where video players put it) and makes it the last to be squeezed when the row
runs out of width at the 700×480 minimum.

The glyph is **pushed** by `MainWindow.ToggleFullscreen` via `OverlayWindow.SetFullscreen(bool)`,
not inferred inside the overlay. The overlay cannot observe the owner's `WindowState`, so a locally
inferred glyph is correct only for toggles the button itself started and drifts the first time F11
or the double-click is used. The suite pins that specifically: it enters fullscreen **by F11** and
asserts the button's UIA name is "Leave fullscreen". Removing the push was confirmed to leave the
button working while both label assertions failed — the enter/leave assertions alone would not
have noticed. New icons `IconFullscreen` (E5D0, which `IconMiniRestore` already borrows for a
different button) and `IconFullscreenExit` (E5D1).

MVVM-lite: hand-rolled `ObservableObject`/`RelayCommand` (`ViewModels/`) — zero NuGet
dependencies so far; `PlayerViewModel` bridges overlay bindings to `MpvPlayer`, with an
`IsSeeking` guard so position feedback doesn't fight the seek-slider thumb during drags.

The Video/Audio/Subs track lists — three sections of one `TracksPopup` since P10 M11 — share
`TrackOptionTemplate` (`Views/MediaTemplates.xaml`).
The selected track is indicated by a garnet border + `LwGlowGarnet` bloom + garnet-lit SemiBold
text on the whole row (Phase 7 M8 — replacing the old 20px check-glyph gutter), driven by the
**data** `TrackOption.IsSelected` (mpv's live `vid`/`aid`/`sid`), *not* `ListBoxItem.IsSelected`.
The same state is mirrored to UIA via `AutomationProperties.ItemStatus="True"` on the selected
row so tests can read selection without a visible glyph. The queue and speed popups keep their
check glyph — they are action/position lists, not stream tracks.

## Jellyfin layer

- **SDK:** `Jellyfin.Sdk` 2025.10.21 (Kiota-generated `JellyfinApiClient`), with
  `Microsoft.Kiota.Abstractions` pinned ≥ 1.22.2 (transitive 1.20.1 has a known CVE).
  `JellyfinService` wraps connection lifecycle + all queries; views bind to the
  lightweight `MediaItem` model, never SDK DTOs.
- **Jellyfin 12 (server on 12.0.0 since 2026-09-08).** The changelog was triaged against every
  server call the app makes. One thing
  broke: 12.0 disables **legacy authorization** by default and migrates existing installs to
  off (server PR #15559). Legacy means the `X-Emby-Authorization` / `X-Emby-Token` /
  `X-MediaBrowser-Token` headers, the `Emby` scheme and the lower-case **`api_key`** query
  parameter; current means `Authorization: MediaBrowser …` and **`ApiKey`**. The SDK client
  sends the full current header, and the token-only `MediaBrowser Token="…"` the app hands
  mpv, `ImageCache` and `DownloadManager` is accepted (the server fills Client/Device from the
  token's device row). Only `GetShareableStreamUrl` used `api_key`; it emits `ApiKey` now.
  Not affected, checked by reading: the `GetItems` recursive change (every call that passes
  `IncludeItemTypes` already sets `Recursive`), Quick Connect (POST Initiate; the GET was
  removed), the removed/obsolete routes, image upscaling (the app requests `maxWidth`, which
  never upscaled), the hidden HLS controllers (mpv plays the server-issued `TranscodingUrl`).
  Behaviour changes still to be measured live are on the "Jellyfin 12 behaviour checks" card:
  PGS/VobSub now stay in an HLS remux because the profile declares them embeddable (PR
  #17512), episodes have alternate versions (PR #16828) and the app takes
  `MediaSources.FirstOrDefault()`, `/Persons` is access-filtered (PR #17466), name sorting
  uses CleanName server-side while `BrowseFolderCache.ApplyQuery` sorts on SortName, and
  Similar depends on a per-library recommendation source.
  **SDK bump:** the 12.0-spec SDK depends on `Microsoft.Kiota.*` 2.0+, so the direct
  `Microsoft.Kiota.Abstractions` pin must move to 2.x with it (1.22.2 fails restore with
  NU1605). A trial build against `2026.9.8-unstable` + Kiota 2.1.1 compiled with
  `-warnaserror` clean, so no call site changes shape; the pin waits for the stable package.
- **Auth:** `AuthenticateUserByName` → token into `JellyfinSdkSettings`. Startup
  reconnect validates the saved token with a real `Users/Me` probe before trusting it.
  Friendly error mapping (DNS / connect / 401 / timeout).
- **Credentials (v2, Phase 5 M7):** DPAPI (`ProtectedData`, CurrentUser) at
  `%LOCALAPPDATA%\LightWeaver\credentials.dat` (atomic temp+move writes) — never the
  password. The payload is `{Profiles: SavedCredentials[], ActiveIndex}`; a v1 file
  (single credentials object) is read transparently as a one-profile store and only
  rewritten on the next save, so a crash mid-migration can't lose it. Profiles are
  keyed by server URL + username (upsert); `ServerName` (public system info) is
  display-only. Logout removes just the active profile. Device id: persisted random
  GUID (no Windows username leak).
- **Quick Connect (Phase 5 M7):** login view → Enabled probe → `QuickConnect/Initiate`
  → code panel → poll `QuickConnect/Connect(secret)` every 2 s (3-minute cap, Cancel
  returns to the form; a 404 on Connect means the code expired) →
  `Users/AuthenticateWithQuickConnect`. The switcher (profile menu / Ctrl+U) lists
  profiles; since the warm-session work (Phase 7, see "Warm multi-account sessions"
  below) switching to an already-live profile is an instant pointer swap with
  background token revalidation — only the first activation of a profile connects cold.
- **Streaming (negotiated, Phase 5 M0):** every server playback starts with a **POST
  `Items/{id}/PlaybackInfo`** (`NegotiatePlaybackAsync`): direct play
  (`{server}/Videos/{id}/stream?static=true`) when the server allows it under the
  bitrate cap, else the server's HLS `TranscodingUrl` (mpv plays it natively with the
  same auth header). mpv authenticates via
  `http-header-fields: Authorization: MediaBrowser Token="…"` so the token stays out of
  URLs/logs (the server's TranscodingUrl does embed an ApiKey param — server-issued).
  The `DeviceProfile` is minimal by design (mpv direct-plays everything): match-all
  DirectPlayProfiles, one HLS/ts+h264 TranscodingProfile, and subtitle profiles. Two
  server behaviors found live (Jellyfin 10.11): "unlimited" must be sent as an explicit
  huge `MaxStreamingBitrate` (1 Gbps) — omitting the field triggers a ~8 Mbps server
  default that transcodes everything — and external SRTs report codec `subrip`, which
  must be in SubtitleProfiles or the server transcodes with `SubtitleCodecNotSupported`.
  Negotiation failure (POST throws) falls back to plain direct play with client-side
  reporting. A **failed direct play retries once through the transcoder**
  (`forceTranscode`: EnableDirectPlay/DirectStream=false) before surfacing the error
  toast. `MaxStreamingBitrateMbps` setting (0 = unlimited) is the cap and the test
  lever; the info panel shows the play method. Diagnostics:
  `LIGHTWEAVER_BREAK_DIRECT_PLAY=1` mangles direct URLs to exercise the retry path.
- **Resume:** `UserData.PlaybackPositionTicks` → seek once mpv raises FileLoaded (works
  for HLS transcodes too — Jellyfin serves a full VOD playlist, so client seeks map to
  segment requests).
- **Detail-view media gem pills (Phase 6 P2):** `GetMediaStreamsAsync` (GET PlaybackInfo,
  default source) returns `MediaSourceStreams` whose `Summary` carries resolution, aspect,
  codec, native HDR (from `VideoRangeType`; DOVI\* → "Dolby Vision" — the *source's* range,
  independent of the RTX Video HDR display toggle) and a compact default-audio badge
  (`AudioPillLabel`: friendly codec + channel layout, e.g. "DD 2.0" / "TRUEHD 7.1", with
  Atmos / DTS:X surfaced from the stream profile). The redesigned `ItemDetailView` maps
  those into glowing gem pills — stormlight-blue for the resolution bucket (4K/1080p/720p)
  and audio, topaz-amber for a non-SDR range — alongside the metadata row (release date ·
  runtime · boxed official rating · topaz community score). A local storm-gradient scrim sits
  behind that row so pale backdrop artwork cannot erase its text. The per-stream `MediaStreamChoice`
  video/audio/subtitle lists are still returned but the detail view no longer exposes track
  dropdowns: **track selection happens in the player during playback** (the OSD popups), so
  `PlayRequested` is a plain `(item, resumeTicks)` and `PlayItem` carries no preselection.
  The old `TrackPreselection` plumbing (`ApplyPreselection`/`MapByOrdinal`/deferred external-
  sub match/PreselectionIndices) was removed with the dropdowns; `ApplyLanguagePreferences`
  now only runs the preferred-language auto-select (one-shot guard unchanged). mpv's
  `vid`/`aid`/`sid` stay sticky per file — `LoadFile` resets them to `auto`, so an in-player
  choice never leaks into the next queue item. (The obsolete `test-detail-tracks.ps1`
  exercises the removed dropdowns and no longer applies.)

  **Corrected 2026-08-05 for subtitles.** That last sentence described the behaviour accurately
  and sold it as a feature; it is the reported bug. Pick English subtitles on episode 1 and
  episode 2 starts on whatever mpv defaults to. The app now remembers the user's in-player
  subtitle choice for the session and re-resolves it per file:

  - `PlayerViewModel.SubtitleChosen` now raises **before** changing `sid`, carrying generation, id
    and the exact `(Lang, Title, Forced)` descriptor. The id is valid only for that load; the
    descriptor remains the session-sticky cross-file choice. `Off` is explicit and remembered too.
  - Re-resolved by `ApplyStickySubtitle`: same language **and** forced-ness, then same language,
    then same title (which is what carries an external subtitle's identity). No counterpart in this
    file leaves mpv's default alone rather than forcing `Off`, and the memory survives so a later
    episode can still honour it.
  - It outranks `PreferredSubtitleLanguage` — a live choice is more recent and more specific than a
    stored default.
  - `LoadFile` synchronously publishes an empty track snapshot for the new request. Until current,
    nonstale `FileLoaded`, every nonempty observed `track-list` is suppressed; START_FILE's event
    generation is advisory for those observations, not a correctness boundary. At `FileLoaded`,
    `RefreshTracks` synchronously reads/parses mpv's authoritative current property, rechecks the
    request generation, and publishes it. Later embedded/external waves publish normally.
  - `MainWindow` owns one `SubtitleGenerationState` per load. Its latest pending intent blocks the
    language-first cross-file fallback. The current `FileLoaded` and every later matching
    `TracksChanged` reconcile only the exact generation-local id plus descriptor until mpv reports
    it selected. `Off` always sends `sid=no`, and an empty list cannot acknowledge it.
  - With no same-generation intent, the remembered cross-file choice retains its established
    language-first fallback and retries as embedded or external tracks arrive. External subtitle
    batches are playback-sequence checked and applied once per generation, including a safe
    transcode retry; a stale `FileLoaded` performs no subtitle side effects.
  - A `CycleSubtitle` key received before `FileLoaded` increments a generation-owned count and is
    handled without reading the empty UI collection. After the authoritative refresh, sticky/default
    restoration runs first; the current snapshot is refreshed once more when needed, then the count
    executes against current options. One queued press reaches the next subtitle, two reach Off in
    the controlled three-row fixture. If no real subtitle exists yet, the count waits for a late
    embedded/external wave.
  - `PlayerViewModel` still owns a generation-scoped subtitle cursor for post-load interaction. While mpv's observed `Selected`
    value lags, rapid presses advance from the latest requested row instead of repeating the same
    transition; an observation only releases the cursor when it acknowledges that request.
  - **B40 revision 3 (fixed and verified on the test machine).** The initial post-load latch fixed
    the reported bounce, but review found it could not distinguish an early incoming choice from a
    stale outgoing list. Generation stamping makes that distinction explicit, and the pending exact
    intent survives a delayed `FILE_LOADED` without invoking cross-file fallback.

    A strengthened run on the test machine exercised a three-row fixture with two English tracks: the first `S` moved
    from English to English SDH, both the early and settled reads stayed there, and the explicit
    early-equals-settled assertion passed; the second press reached the distinct Off row and the
    third wrapped to English (`RESULT: PASS`). The suite waits three seconds after the overlay
    appears, so this historical green run proves the old post-load path only. It does not verify the
    revised generation-safe mechanism.

    The revised suite uses a Debug-only bounded `LIGHTWEAVER_TEST_FILE_LOADED_DELAY_MS` hook (12 s,
    longer than `Start-App`'s 4 s settle), structural enter/action/exit marker ordering, and fresh
    processes for early one-`S`, rapid two-`S`, and normal post-load legs. The hook and markers are
    absent in Release and in Debug when the variable is unset. A revision-1 run on the test machine
    passed all three
    legs: early English -> English SDH before `FILE_LOADED` and still after it, with `S` before exit
    and enter < action < exit; two rapid `S` requests reached Off inside the delay; and the normal
    path produced English -> English SDH settled -> Off -> English (`RESULT: PASS`). An earlier
    bring-up run passed those product behaviors but failed only a redundant
    harness timing assertion that required the slow flyout read itself to finish before delay exit;
    that assertion was moved to the actual boundary immediately before sending `S`. This run does
    not verify revision 2.

    Revision 2 separates the newest requested `LoadGeneration` from `_eventGeneration`, which is
    advanced only when `MPV_EVENT_START_FILE` consumes the FIFO generation queued immediately before
    each `loadfile`. `track-list` and `FileLoaded` stamp the event generation, never the newer live
    request, and stale observations are dropped before or at UI dispatch. A failed load command
    removes its queued generation. The synthetic track clear still carries the new request generation.

    The Debug delay yields from the UI `FileLoaded` callback, leaving mpv's event thread free to
    publish the incoming track list. A narrowly scoped companion hook holds only the first
    post-`SelectSubtitleTrack` acknowledgement for that generation until the delayed current
    callback completes, then releases it; the initial incoming list is never held. A second
    DEBUG-only hook schedules local B through the real shell only after A's acknowledgement has
    entered the hold, plus a bounded extra delay; its records contain generations and counts only,
    never the path. B's `sid=auto` reset supplies the
    deterministic outgoing generation-1 observation, which must be dropped before `START_FILE`
    consumes generation 2. The held A acknowledgement and stale A `FileLoaded` are discarded without
    publication, while B publishes incoming rows and its early exact intent survives B
    `FileLoaded`. Rapid-two and post-load remain separate fresh-process legs. All hooks are inert
    when unset and compiled out of Release. Two revision-2 bring-ups exposed harness-only boundaries:
    the first delayed mpv's event thread and blocked B's rows; the next used a wall-clock scheduler
    that raced UIA and let B's key arrive after its delay.

    The final run on the test machine passed the corrected 25-second, hold-triggered suite. In one process, A exposed three rows and
    plain English, held its first acknowledgement, and triggered B; B was requested during A's
    generation-1 UI delay, reset `sid` while event generation remained 1, dropped the outgoing A
    observation before `START_FILE` consumed generation 2, then exposed three incoming rows and
    plain English. B received `S` before delay exit and held the acknowledgement; stale A
    `FileLoaded` was ignored and discarded A's held rows without publication; B's current delay then
    exited, released its acknowledgement, and remained on English SDH with action < hold < exit <
    release. The rapid-two leg reached Off and the post-load leg completed English -> English SDH
    settled -> Off -> English; `RESULT: PASS`. This closed revision 2.

    Revision 3 removes the hold-based correctness mechanism. Its Debug discriminator captures A's
    authoritative rows, requests B after A's refresh, then injects those A rows immediately after
    START_FILE B with advisory generation 2. Because B has not reached its authoritative
    `FileLoaded` refresh, the rows must be suppressed and the track UI must remain empty. One `S`
    is queued in that empty interval and must select B's English SDH after refresh; a fresh delayed
    process queues two presses with no rows and must settle on Off. The existing post-load full-cycle
    leg remains. `mpv_get_property_string` is safe for this synchronous snapshot: libmpv's official
    client API is generally fully thread-safe and serializes calls through the playback core, and
    this project already uses the same call from UI-thread diagnostics/property paths. START_FILE
    ownership remains only for stale `FileLoaded` rejection and structural diagnostics.

    The final revision-3 run on the test machine
    passed the authoritative boundary. A published authoritative rows; after B
    was requested and reached START_FILE2, captured A rows were injected with advisory generation 2
    and suppressed by the pre-FileLoaded gate. B remained at zero rows with no authoritative
    snapshot, queued one `S` before delay exit, then published its authoritative current snapshot
    and settled on English SDH. A fresh rapid-two leg queued both presses with zero rows and settled
    on Off; the post-load leg completed the full English -> English SDH -> Off -> English cycle.
    `RESULT: PASS`. The revision-2 PASS above is retained as superseded evidence; this run proves the
    authoritative A-to-B, queued one-S, rapid-two, and post-load boundaries.

    Final review found one untested late-wave case. A prior non-Off sticky can meet a matching
    external subtitle only after the next file reached FileLoaded with no subtitles. If the external
    observation still says Off, sticky restoration issues a selection command; executing the queued
    pre-load cycle from that same snapshot would consume Off -> external invisibly. The sticky apply
    result now distinguishes no match, already selected, and selection command issued. A command
    leaves the generation-owned count pending; only a later authoritative/observed snapshot that
    acknowledges the restored target releases the cycle. At that boundary `PlayerViewModel` rebuilds
    its rows from the same current mpv snapshot and primes the generation cursor to the exact restored
    id (or Off) before the count advances, so subscriber timing cannot turn external -> Off into the
    stale Off -> external transition. If the exact target is absent the wait remains. Repeated late
    waves continue retrying, and the next load replaces the whole state if no acknowledgement arrives.
    A DEBUG-only fourth leg establishes sticky English SDH on A, loads subtitle-free B, queues `S`,
    adds a matching external from Off, and asserts command < cursor prime < observed acknowledgement
    < queued cycle.
    The hook is generation-scoped, bounded, path-silent, and absent from Release. Two bounded
    bring-up runs failed on harness timing rather than product assertions. The first
    passed late-external, rapid-two, and post-load behavior,
    but its same-process leg timed out before the delayed `FileLoaded` enter marker. The second
    passed the hardened same-process and rapid-two legs, but
    the late-external fixture window had already closed, so the suite refused to send a late key.

    The final deterministic run on the test machine
    passed the full suite. It proved same-process stale suppression and queued
    one-`S`, rapid-two to Off, late external trigger -> request -> delayed `FileLoaded` -> sticky
    command -> cursor prime -> observed acknowledgement -> queued cycle -> final Off, and normal
    post-load wrap. `RESULT: PASS`. This closes B40's product verification.
    On 2026-08-20 the user exercised the normal freshly built Debug window with the controlled
    Off / English / English SDH fixture and confirmed that repeated `S` presses worked.
  - **Still open, deliberately:** cross-file, that same language-before-title order restores plain
    "English" on the next episode even when the remembered pick was "English (SDH)". The reason is
    the *ordering*, not a missing `Lang`: both tracks match the remembered language, so the language
    branch returns the **first** of them and the title is never reached. `Title` is the fallback for
    "the remembered language is not in this file at all" — the case an external subtitle's identity
    depends on — not merely for "no language was recorded". Out of scope for B40 and filed on the
    backlog (`6hHQ4CMRMpfcpHcm`).

  Pinned by `backlog-tests/test-sticky-subtitles.ps1`: pick a subtitle on episode 1, advance the
  queue, require the same **language** on episode 2 (the label legitimately differs — "generated
  (eng)" became "English (eng)"), and separately that choosing `Off` also survives. Note the
  suite reads the active row from `AutomationProperties.ItemStatus`, not
  `SelectionItemPattern.IsSelected`, which is false for every row in that flyout.
- **Inline season episode list (Phase 6 P2):** an episode detail fetches its own season's
  episodes (`GetEpisodesAsync(seriesId, seasonId)`, once per item) and renders them as
  thumbnail + progress rows under a "Season N" header with a "View all N episodes" link
  (→ the season's episode list); rows navigate to the sibling episode's detail. Hovering
  a row's **thumbnail** (only — not the rest of the row) reveals a scrim + play button
  (`EpisodeHoverPlay`, `Focusable=False` per the card-button foot-gun) that raises
  `PlayRequested` for that episode directly (resume-aware, `e.Handled` so the row never
  also navigates); the garnet-marked current episode stays fully non-interactive. The complete
  row is persisted through the profile-scoped browse cache and painted before its guarded
  background refresh; a newer local watched/resume change is merged only into requests that
  started before that change. Movies
  have no list, so the cast grid spans full width; episode/series details put cast in a
  fixed 250-px rail beside the list (`UpdateContentLayout` swaps the two grid columns).
- **Clickable series/season eyebrow (backlog 2026-07-11; relocated Phase 6 P2):** the
  episode detail's eyebrow (above the Marcellus title) is "Series · Season · E5" built as
  `Hyperlink` inlines (`DetailSeriesLink`/`DetailSeasonLink` automation ids; inherited
  stormlight foreground + hover underline instead of the theme's blue). Non-episodes show
  the item's type word ("MOVIE") instead. `MediaItem` carries `SeasonId`/`SeasonName`
  (mapped in `MapOne`; fallbacks: `ParentId` is the season id for an episode, the label
  falls back to "Season {ParentIndexNumber}"). A click raises the existing `ItemSelected`
  event with a minimal locally built Series/Season `MediaItem` — no fetch needed; MainWindow
  routes it through the normal browse path (`CreateLibraryView` → `NavigateTo`), whose Season
  branch requires `SeriesId` on the item for the episodes query.
- **Progress reporting:** `PlaybackReporter` — start on FileLoaded, progress every 5 s
  (`isPaused` included), stop on back/close as fire-and-forget (teardown must not lose
  it); the **server-issued `PlaySessionId`** from negotiation plus `MediaSourceId` and
  the real `PlayMethod` (DirectPlay/Transcode) ride every report, so the server ties
  them to its own transcode session; a client-minted GUID remains the fallback when
  negotiation failed; ticks = seconds × 10⁷. Server-verified.
  Note: Jellyfin discards resume positions below its minimum-progress threshold.
- **Stop/refresh ordering:** `Stop()` returns its POST task; `StopPlaybackAndReturn`
  stashes it on `AppViewModel.PendingStopReport` and the detail view awaits it (capped
  10 s — this LAN can need a SYN retry) before re-fetching UserData, so the refresh GET
  can't outrun the stop POST. The detail view refreshes on `IsVisibleChanged`, not
  `Loaded`: back-from-playback re-shows the same instance via a BrowseLayer visibility
  flip, which never re-raises `Loaded`.
- **LAN robustness (measured live):** parallel connection bursts to the server
  intermittently lose SYNs (~21 s retransmit ladder), and Windows WPAD auto-detect can
  stall first requests. The shared `HttpClient` uses `UseProxy=false`,
  `ConnectTimeout=4s`, warm pooled connections, `MaxConnectionsPerServer=4`, and data
  queries retry once on transport stalls.
  **Root cause found on the CLIENT, not the server (2026-08-01).** A full server-side sweep
  exonerated the Unraid box — zero drops in every counter that could see one over 26 days of
  uptime (host and container-namespace TCP listen queues, conntrack 124/262 144 with zero
  insert failures, e1000e `missed=0`, qdisc 21/3.9 B, no syslog link events), and a live
  600/600 parallel-connect burst passed. This PC's Intel I219-V, meanwhile, had **29 707
  received-discarded packets** with `Receive Buffers` at the driver default **256** — a known
  I219-V weakness under bursts, and a client-dropped SYN-ACK is indistinguishable from
  server-side SYN loss. Raised to **2048** (survives reboots; re-check after driver updates
  with `Get-NetAdapterAdvancedProperty -Name Ethernet -RegistryKeyword '*ReceiveBuffers'`).
  The adapter reset zeroed the discard counter, so any future non-zero reading is a fresh
  signal. The client-side mitigations above stay — they are correct engineering against any
  flaky LAN, and the WPAD stall is unrelated to the NIC.
  **`ImageCache` carries the same configuration, and it is the one that matters most**
  (B13, 2026-07-30). It had none of it until then — a bare `new HttpClient()`, so
  `MaxConnectionsPerServer` sat at its `int.MaxValue` default while this is the client asked
  for every poster and backdrop on screen at once. Measured cold Home load: 13 images opened
  **11 simultaneous connections**, roughly one per image with no reuse; capping it took the
  peak to 7–8, of which 4 are `JellyfinService`'s own. Both clients must keep these settings —
  configuring one and not the other is what produced the bug, and the two are easy to read in
  isolation and think fine.

- **Mini-player (Phase 5 M9):** a display mode of the main window, not a separate
  window — mpv keeps owning its child HWND (the load-bearing rule). Entering saves
  the rect/state (RestoreBounds when maximized), drops the chrome
  (`WindowStyle=None`, menu hidden, `Topmost`), and resizes to 480×270 DIP at the
  bottom-right of the prior rect; the generic `SyncOverlayBounds` keeps the overlay
  glued. Drag-to-move: the press lands on the overlay window, so it forwards a
  `MiniDragRequested` and MainWindow sends itself `WM_NCLBUTTONDOWN`/`HTCAPTION`
  (plain `DragMove` can't cross windows); in mini mode video-area clicks are drags,
  not pause. The overlay slims via `SetCurrentValue(Visibility)` +
  `InvalidateProperty` on the way back — plain sets would destroy the
  HasChapters/Has*Choices visibility bindings. Fullscreen and mini exit each other;
  leaving playback or closing the app exits mini first so window-state persistence
  never records the mini rect.
- **Watched tracking + the EOF pause leak (Phase 7 M18):** completion is server-native
  and works: the client never sets `Played` on finish — the 5 s progress reports and
  the stop report (position ≈ duration after EOF + Back) cross Jellyfin's ~90%
  threshold, which flips `Played` and zeroes the resume position. The bug behind
  "watched isn't tracked": `keep-open=yes` pauses mpv at EOF and `pause` is a sticky
  property, so the NEXT `loadfile` started frozen (progress never advanced — looking
  exactly like broken tracking, and freezing Up Next/auto-advance too). `LoadFile`
  now resets `pause=no` alongside its speed/track-pick resets. No Trakt integration
  needed — there is no server-capability gap.
- **Per-file state resets belong where the load is *initiated* (BUGS.md B1/B2):** `PlayCore`
  is the single funnel every load goes through, and it now clears `_langPrefsApplied` and calls
  `PlayerViewModel.ResetFileGeometry()` (zeroes position/duration) there — not in the
  `FileLoaded` handler. mpv reports the new file's `track-list` **before**
  `MPV_EVENT_FILE_LOADED` (measured: `.496` vs `.499` local, `.324` vs `.325` for a server
  stream), so a `FileLoaded`-anchored reset lands after the state it was meant to precede. The
  zeroed duration is also what marks a mid-transition: the EOF up-next guard and
  `UpdateUpNext` both key off `DurationSeconds > 0`, so a stale `eof-reached`/position post
  from the outgoing file cannot advance past the incoming one. Note mpv emits a *further*
  `track-list` notification ~500 ms after load (and on every `aid`/`sid` change) — convenient,
  but nothing may depend on it.
- **Modal layer (Phase 7 M23):** `Views/ModalHost` — an in-app overlay (top layer of
  the shell root Grid, never a native MessageBox/top-level window): full-bleed
  `LwScrimBrush` + a centered w440 `LwModalCard` (storm-3, r12, elevation-3), entry
  fade + 24 px rise over 480 ms. `ConfirmAsync(title, body, label, destructive)` /
  `AlertAsync(...)` return Tasks via a TaskCompletionSource (a superseding call
  cancels the pending one); Esc and scrim click-away cancel confirms. Consumers:
  Logout (ember destructive confirm) and the mpv-init failure alert;
  `SwitchServerWindow` restyled to the card language. The OS `OpenFileDialog` stays
  native by design.
- **Segmented control (Phase 7 M22):** `Views/LwSegmentedControl` (a `ListBox`
  subtype — Items/SelectedIndex/UIA stay stock) + the keyed `LwSegmented` style whose
  template overlays a storm-4 thumb Border behind the segments; the control slides it
  under the selected container (TranslateTransform + Width, 240 ms cubic ease; instant
  on layout). First consumer: the library watched filter. **Foot-gun:** XAML event
  attributes on custom element types go through the runtime `EventConverter`, which
  needs the handler's signature to match the delegate EXACTLY (`RoutedEventArgs`
  handlers that BAML accepts for built-in types throw XamlParseException here) — wire
  such handlers in code-behind instead.
- **Transport mapping (Phase 7 M17):** `AppSettings.MediaNextAction`/`MediaPrevAction`
  (`TransportAction`: QueueStep · ChapterStep · Seek, + `TransportSeekSeconds`) decide
  what Media Next/Previous do. `PlayerViewModel.TransportNext()/TransportPrev()`
  consult the setting live, `CanSkip*` follows the mapping (Chapter/Seek are always
  actionable), and the OS buttons refresh via the `CanSkip*` PropertyChanged
  notifications. Settings has per-direction dropdowns + a seconds field that shows only
  while a direction is mapped to Seek. **Scope narrowed in Phase 10: the mapping now
  reaches the media keys and SMTC only, not the OSD pair** — see the merged next/previous
  entry below for why.
- **Configurable shortcuts (Phase 7 M16):** `Player/PlayerActions.cs` holds the
  `PlayerAction` enum and `PlayerActionDispatcher` — the single routing point mapping
  actions onto existing `PlayerViewModel` calls. Bindings live in
  `AppSettings.KeyBindings` (`action name → "[Ctrl+][Alt+][Shift+]KeyName"`, `""` =
  unbound); `Resolve()` overlays saved entries on the shipped defaults so partial/old
  files stay valid. `MainWindow.OnKeyDown` is one lookup (`TryResolve` → `Dispatch`) —
  no `KeyGesture` restriction on plain alphanumerics, numpad `Add`/`Subtract` fold
  onto `OemPlus`/`OemMinus`. Defaults follow the spec (Space/arrows/volume/M/A/S/V;
  skip-segment moved to `C`). The Settings "Shortcuts" section is generated from the
  enum: click-to-capture rows (window-level `PreviewKeyDown`, Esc cancels, modifier
  keys alone keep waiting); assigning a chord already in use unbinds its previous
  owner; reset clears the saved map. App-chrome chords stay `InputBindings` — but since
  Phase 10 M9 they are **built from a table**, not written as literals (below).
- **Shortcuts overlay + the chrome shortcut table (Phase 10 M9):** `Player/AppShortcuts.cs`
  is the single source for every keyboard affordance, and `Views/ShortcutsOverlay.xaml`
  renders it. Three populations feed `BuildRows(settings)`: the rebindable `PlayerAction`s
  (via `PlayerActionDispatcher.Resolve` — so the panel follows a rebind with no relaunch),
  `AppShortcuts.Chrome`, and a short list of chordless affordances (mouse thumb buttons, the
  wheel, `Enter` on an armed Up Next). The load-bearing part is `Chrome`: `MainWindow`'s ctor
  **iterates it to construct its `KeyBinding`s** instead of the eight chord literals it used
  to carry, so a chord cannot appear in the panel and not in the app, or the reverse. The
  handlers stay in `MainWindow` (their guards read private window state) and are selected by
  `Id` in `InvokeChromeShortcut`, which is also the one place that blocks every chrome chord
  except `Esc` while the panel is up.
  Three details worth keeping:
  - **`Space` is registered twice on purpose** — a guarded chrome `InputBinding` *and*
    `PlayerAction.TogglePause`'s default. `ChromeShortcut.DuplicateOf` suppresses the second
    row only while the two are literally the same keystroke; rebind TogglePause to `K` and
    the panel lists both, because Space still pauses.
  - **`?` is matched on the typed character, not a chord.** A window `KeyBinding` fires while
    the search pill holds focus and would eat the character, and `?` is a different virtual
    key per layout (Shift+/ US, Shift+ß German). `MainWindow.OnWindowTextInput` (mirrored on
    `OverlayWindow`) checks `e.Text == "?"` and skips when a `TextBoxBase`/`PasswordBox` has
    focus, without marking the event handled — so a `?` typed into search still types.
  - **The panel exists twice.** WPF cannot draw over the mpv child HWND, so there is one
    instance in `MainWindow` and one in `OverlayWindow`; `ToggleShortcutsOverlay` picks the
    host by playback state, and the player one is inert in mini mode.
  - **A heading never scrolls without its first row (the fold guard, 2026-09-05, BUGS.md B43).** The columns
    are Playback (15 rows) + Mouse (3) on the left, Audio & subtitles (6) + General (8) on the
    right — the closest of the four pairings, but 18 rows do not fit everywhere. At 1920x1080 and
    150% the window is 1280x720 DIUs and the scroller's viewport about 495, so the left column's
    second heading ("Mouse") landed just above the fold with its rows below it: a section that
    reads as empty. After every open (`DispatcherPriority.Loaded`, so it measures real positions)
    and on `Scroller.SizeChanged` — but not while the open's queued run is still pending, or the
    first open would run the guard twice and log two `fold-guard` lines, since the panel lays out
    with `IsOpen` already true — `ApplyFoldGuard` undoes its previous pushes, and for each
    column's second and later groups compares the heading's top and its first row's bottom
    against `ViewportHeight`. A heading above the fold with its first row below it is pushed
    below the fold by widening the PREVIOUS group container's bottom margin (the group's own
    panel is inside the container being measured, so padding that would move heading and fold
    together), and the push is logged as `event=fold-guard column=<l|r> group=<title> pushed=<px>`.
    Only the fold at scroll offset 0 is guarded, deliberately: the panel opens at the top and is
    read from the top, and chasing `VerticalOffset` would re-lay-out on every wheel notch and move
    a heading under the reader's pointer.
  Chord spelling moved to a **token map** (`PlayerActionDispatcher.ChordSpellings`) when the
  chrome table brought `Back` in: the old chain of substring `Replace`s would have rendered
  `OemBackslash` as "OemBackspaceslash".
- **Adaptation at the 700x480 window minimum (Phase 10 M11).** Standing rule: a control that does
  not fit is moved, or moved into a menu, never silently dropped. The load-bearing fact behind all
  four fixes is that **nothing in this app scrolls horizontally**, so a control past the left or
  right edge is unreachable, while one below the bottom edge is merely below the fold on a page
  that scrolls. Four adaptations:
  - **`OverlayWindow`** — the OSD row is a `DockPanel`, which squeezes right-docked children to
    **zero width** once space runs out, with no warning and no clipping artefact to notice. The
    Video/Audio/Subs pills became one `TracksButton` + a sectioned `TracksPopup`, and
    `UpdateCompactControls` additionally drops the bar's side padding 40 -> 16 and the volume
    slider 96 -> 56 below 900 DIP. Both were needed: after the merge alone the row was still
    ~100 physical px over budget and ate the new button too. **The merge is no longer
    unconditional** — see the track-button fit test below.
  - **`ItemDetailView`** — the action row is a `WrapPanel`, not a `StackPanel`.
  - **`AdvancedSearchView`** — GENRE / WATCHED / YEAR live behind a `MoreFiltersButton`
    disclosure. `UpdateMoreFiltersBadge` counts the non-default ones and force-opens the panel
    when any is active, so a collapsed filter can never narrow the results invisibly (this is
    what makes a disclosure acceptable rather than a trap).
  - **`LibraryView`** — `LetterJumpBox` appears exactly when M3.4 hides the A-Z rail for width;
    both drive `_letter` + `ResetAndReloadAsync`, and `SyncLetterJumpSelection` keeps them one
    piece of state shown two ways. The `x:Name` and automation id keep their "Jump" spelling
    after the 2026-08-05 semantic change, because suites assert that id string.
- **Track buttons: two shapes, chosen by measurement (M2).** M11's merge was right at the
  minimum and a tax everywhere else: at 1600 DIP and up there is room for all three pills, and
  one `Tracks` button costs the user a click and a read to find out which types even have a
  choice. Both shapes ship now — `VideoTracksButton`/`AudioTracksButton`/`SubtitleTracksButton`
  and the merged `TracksButton` — and `OverlayWindow.UpdateTrackButtons` picks between them.
  Exactly one shape is on screen at a time, so all four carry a **local** `Visibility` written
  from code and no binding (`SetMiniMode`'s `InvalidateProperty` restore therefore still works,
  and `SetMiniMode(false)` re-runs the decision because the window has been resized twice by
  then).
  - **It is a fit test, not a threshold.** The optional controls (queue pair, chapter pair,
    next/prev pair, audio-device button) change what the row has left by a few hundred DIP
    between the emptiest and the fullest file, so any single width threshold is wrong for most
    files. The test sums `ActualWidth + margins` over the visible `ControlsRow` children, adds
    the pills that *would* show, plus 6 DIP of slack, and splits when that fits
    `ControlsRow.ActualWidth`.
  - **The sum EXCLUDES all four track buttons.** Including them would make the measurement
    depend on the decision it is about to make, and the two states would oscillate at the
    boundary. The pill widths are measured once (a collapsed element measures to zero, so each
    is shown for the length of one synchronous call and its `DesiredSize` cached).
  - Evaluation is deferred with `Dispatcher.BeginInvoke(DispatcherPriority.Loaded)` — below
    layout, so every `ActualWidth` it reads is current — and repeated requests coalesce.
    Triggers: `ControlsRow`/window `SizeChanged`, the view-model properties that add, remove or
    resize a row control, and leaving mini mode. Each change of the decision logs
    `TrackButtons split=/used=/row=` to the UI log and `event=state track-buttons` to the app
    log.
  - **One popup, per-button mode.** All four buttons open the same `TracksPopup` through
    `OpenTracks(mode, placementTarget)`; a section is shown when the mode asks for it AND the
    type has a choice, so a per-type pill can never open an empty flyout. Clicking the button
    the flyout is already showing closes it; clicking a *different* track button switches
    sections and leaves it open. Suites must go through `Open-TrackList` in `driver.ps1` rather
    than clicking an id, because they cannot know which shape they will meet.
  - **The flyout has ONE scroller.** `TracksScroller` is the only thing that scrolls: the three
    `ListBox`es carry `ScrollViewer.VerticalScrollBarVisibility="Disabled"` so each lays out at
    full height, and a `PreviewMouseWheel` on the section stack forwards to it
    (`WheelScroll.ForwardTo`) so a notch over a list cannot move a private viewport instead.
- **Mute + OSD skip pair (Phase 7 M13/M14):** the OSD MuteButton toggles mpv's real
  `mute` property (`PlayerViewModel.IsMuted` follows the observed flag, so the glyph
  proves the round trip; volume level untouched); plain `M` toggles it, and the player
  key switch now requires no modifiers so chords stay with their InputBindings
  (Ctrl+M = mini-player). `UpdateSystemTransportEnabled` drives the SMTC next/prev enable
  state from `CanSkipNext`/`CanSkipPrevious`, so the OS buttons can never disagree with the
  OSD (BUGS.md B6: it used to re-derive from the queue and ignore an armed Up Next, leaving
  the media key dead while the OSD button worked).
- **One next/previous pair (Phase 10 — merged from two).** The OSD used to carry *two*
  adjacent pairs: a queue pair (`QueuePrevButton`/`QueueNextButton`, skip glyphs, driven by
  the M17 mapping) and a dedicated episode pair (`PrevEpisodeButton`/`NextEpisodeButton`,
  double-arrow glyphs, pure series-order stepping). In the everyday case — an episode playing
  from a season queue that is already in series order — they did the same thing side by side,
  which is how the duplication was reported. They were **not** redundant in general, so this
  is a merge rather than a deletion: a single `PrevButton`/`NextButton` with precedence
  **queue entry → armed Up Next → series-order neighbour** (`CanGoNext`/`CanGoPrevious`/
  `GoNext()`/`GoPrevious()`), which keeps every case the episode pair alone would have dropped
  (playlists, shuffled queues and movie queues, none of which it could step) and every case the
  queue pair alone would have dropped (an episode played with no queue, where it offered no
  "previous" at all). `ShowNextPrevButtons` is simply "either direction is meaningful", so a
  queue-less movie shows neither.
  The pair deliberately **no longer honours the M17 chapter/seek mapping**. Chapter stepping
  has its own dedicated buttons a few controls along, already shown only when the file has
  chapters, and seeking has the jump pair — so projecting the mapping onto this pair made one
  of those redundant *and* removed the only OSD route to the next episode whenever the setting
  was not QueueStep. `PlayerAction.QueueNext`/`QueuePrev` (the N/P keys) run the same merged
  calls so a key and a button mean the same thing; the enum names are unchanged because saved
  key mappings persist by name.
  Series-order neighbours still come from one `GetAdjacentEpisodesAsync` (AdjacentTo; spans
  season boundaries) per episode playback, feeding `PlayerViewModel.SetEpisodeNeighbors` — and
  the queue-less Up Next arm, which previously made its own `GetNextEpisodeAsync` call. A
  series-order step raises `EpisodeStepRequested` → `OnEpisodePlayRequested` (M15
  "watch from here": queue rebuilt from the target onward). Hidden in mini mode via the
  ControlsRow slimming.
- **Buffering indicator (Phase 7 M26):** mpv's `paused-for-cache` flag is observed
  (`ObsPausedForCache`, flag format) → `MpvPlayer.IsBuffering` → `PlayerViewModel.IsBuffering`
  → a centered `Views/ActivityDots` band in the overlay (three dots throbbing in sequence,
  opacity 0.22–0.55 to stay inside the HDR glow budget; it was a breathing ring until
  2026-08-03 — see the loading-language section). It sits outside the idle-fade group so a stall stays
  visible with the controls faded, and is drawn only in the transparent overlay window
  (the mpv HWND is never composited — load-bearing rule). Verified by throttling a local
  HTTP stream until mpv genuinely stalls (`test-buffering.ps1`).
- **Mini-player corner resize (Phase 7 M12):** a `MiniResizeGrip` in the overlay's
  bottom-right (mini mode only) forwards `MiniResizeRequested`; MainWindow sends
  itself `WM_NCLBUTTONDOWN`/`HTBOTTOMRIGHT` for a native sizing loop
  (`ResizeMode=CanResize` in mini supplies the required `WS_THICKFRAME`). An
  `HwndSource` hook enforces the contract: `WM_SIZING` locks the drag rect to 16:9
  and clamps 320×180..960×540 DIP; the last mini size is session-remembered and
  reused on the next enter. Three foot-guns found live: (1) `WM_NCCALCSIZE` must
  return 0 while mini or THICKFRAME's invisible ~10px border insets the video/overlay
  from the window edge; (2) releasing a size drag AT the screen edge triggers
  Windows' vertical-maximize arrangement which bypasses `WM_SIZING` — the hook also
  clamps `WM_WINDOWPOSCHANGING` while mini; (3) `WS_MAXIMIZEBOX` is stripped in mini
  (restored on exit) because every Windows snap/arrange feature keys off it and
  fights the aspect lock. The grip TextBlock needs a near-transparent Background —
  background-less text only hit-tests its glyph pixels and presses fell through to
  drag-to-move.
- **Update service (B35, replacing Phase 5 M8):** `Updates.UpdateService` is the one
  application-lifetime coordinator for the anonymous GitHub release client
  (`Updates.GitHubUpdateClient`, feed `https://api.github.com/repos/oss96/LightWeaver-Windows/releases?per_page=10`
  with the `User-Agent`/`Accept`/`X-GitHub-Api-Version` headers GitHub requires), explicit
  `UpdatePhase` state, streamed/resumable staging, and installer handoff. It checks before
  Jellyfin startup, then every six hours while the process remains open; **Check for updates** always
  performs a single-flight request immediately. Up-to-date, available, downloading, ready,
  installing, feed/network failure, and an asset-less release are distinct states rather than
  the old `null` result. The obsolete `%LOCALAPPDATA%\LightWeaver\update-check.txt` 24-hour
  stamp is ignored and removed. Only stable three-part tags are considered; drafts and
  prereleases are skipped. `LIGHTWEAVER_UPDATE_API` and exact fixture SHA-256 are Debug-only
  loopback seams.

  Packages stage under `%LOCALAPPDATA%\LightWeaver\updates` (`.part` + atomic rename +
  non-secret version/URL/expected-size/SHA-256 metadata), resume with HTTP Range, retain only the
  newest relevant staged package, and revalidate pending metadata and package bytes during both
  process restoration and launch. A completed part is promoted locally instead of issuing an
  invalid end-of-file Range; corrupt pending bytes and superseded versions are retired so retry
  can fetch clean/current bytes. B37 hardened the frequently replaced `partial.json`: each write
  uses a process/thread/GUID-specific temporary path and retries the atomic replace for a bounded
  620 ms when Windows reports a transient sharing `IOException`/`UnauthorizedAccessException`.
  This absorbs antivirus/indexer or second-process read windows without weakening persistent
  permission/storage failures, and always attempts to remove its own temp file. A Debug-only
  fail-once record seam proves the denial occurred and the transfer still reaches Ready. Production
  accepts only the expected HTTPS hosts and asset name through every redirect — exact-host matches
  on `github.com` (release page and asset URL) plus `objects.githubusercontent.com` and
  `release-assets.githubusercontent.com` (where an asset download redirects) — checks the
  Authenticode digest/timestamp/code-signing EKU, and requires the installer signer to match the
  running LightWeaver signer. The permanent self-signed certificate's *specific* untrusted-root
  result is allowed only after those integrity/identity checks; missing signature, bad digest,
  wrong signer, and every other reported trust failure remain hard rejects.

  `AppSettings.UpdatePolicy` is global and defaults to `ManualInstall`: all modes still detect
  and notify, while `AutoDownload` stages automatically and `AutoDownloadAndInstall` also hands
  the verified installer to Inno silently after a normal user Exit/X. Windows logoff/shutdown,
  crashes, termination, and incomplete downloads never launch it. The guard is WPF's application-
  level `Application.SessionEnding` event, not `Microsoft.Win32.SystemEvents.SessionEnding` (whose
  ordering relative to `Window.OnClosing` is not deterministic); an exit-time install leaves
  the app closed. Explicit **Install now** / **Download and install** actions verify and queue the
  handoff, close the window, finish the bounded playback stop report and mpv disposal, and only
  then start Inno from `OnClosed`. Settings owns the persistent current-version/last-check/status/progress/action
  surface, while the dismissible browse banner and profile-menu item mirror the same service.
  Debug builds add exact-SHA, short-timeout, metadata-replace failure, no-launch recorder, and session-ending simulation
  fixture hooks; none is compiled into Release.
  `release.ps1`
  publishes: installer.ps1 build (`-SkipBuild` reuses dist), GCM token for the API,
  replace-release-keep-tag semantics, changelog from `git log` since the last tag,
  multipart asset upload via curl.exe (PS 5.1 gotchas: `git credential fill` needs
  LF stdin via `cmd /c`, and native stderr must not hit the PS stream).
- **System media controls (Phase 5 M6):** TFM is `net10.0-windows10.0.19041.0`
  (Windows SDK contract for WinRT; min OS 10.0.17763). `SystemMediaControls` wraps
  SMTC for the Win32 window: WPF has no `GetForCurrentView`, so the instance comes
  from `ISystemMediaTransportControlsInterop.GetForWindow` — obtained via raw
  `RoGetActivationFactory` (manual HSTRING) and a ComImport interface with three
  IInspectable padding slots, then `WinRT.MarshalInterface<>.FromAbi`. ButtonPressed
  arrives off-thread and is re-dispatched; Next/Previous map to the queue (Next falls
  back to an armed Up Next). The DisplayUpdater thumbnail must be a local file
  (`RandomAccessStreamReference.CreateFromFile` on the image disk cache — SMTC can't
  carry our Authorization header); timeline updates every 5 s. Taskbar thumbnail
  buttons are plain WPF `TaskbarItemInfo.ThumbButtonInfos` with vector DrawingImage
  icons, sharing the overlay's jump/pause commands, enabled only while playing.
  Verified against the OS's own `GlobalSystemMediaTransportControlsSessionManager`.
- **Collections & genres (Phase 5 M5):** BoxSets browse through the same generic
  `GetItemsAsync(ParentId)` path as folders (BoxSet was already in `IsBrowsable`).
  `MediaItem.CollectionType` (from the UserViews dto) gates the **Genres** button on
  movie/show library headers → `GenresView` (tile grid from the `Genres` endpoint) →
  a `LibraryView` constructed with an initial genre: the `_forcedGenre` field filters
  the first page load and becomes the combo selection once the genre list arrives
  (then clears, so "All genres" works). Caveat seen live: auto-created TMDb
  collections can be empty stubs (ChildCount 0) — an empty members grid is correct
  behavior, not a client bug.
- **Richer detail view (Phase 5 M4):** `MediaItem` carries `IsFavorite` and `People`
  (the `/Items/{id}` detail endpoint returns People with no Fields param needed —
  list queries omit them). Cast & crew is a horizontal row of disk-cached circular
  portraits; "More like this" uses `Items[id].Similar` (fetched once per item — note
  the endpoint tie-breaks equal similarity scores randomly, so consecutive calls
  return different orders/sets). Favorite toggling uses `UserFavoriteItems` with an
  explicit UserId (same server quirk as the played toggle); Home gains a Favorites
  row (`Filters=IsFavorite, Recursive`). Similar-row clicks navigate through the same
  browse-item path as any card.
- **The detail overflow menu is episode-only (2026-08-05).** It used to carry "Mark as
  watched" and "Add to favorites" alongside the episode navigation, invoking the very same
  handlers as the check and heart icon buttons two positions to its left — a menu restating
  controls already on screen, which the source comment admitted at the time. Removing the two
  duplicates left navigation as the only content, so on a movie, series, season or playlist
  the `⋮` would have opened an empty popup. `MoreButton` now renders only when
  `HasOverflowActions` holds, which reduces to "an episode that knows its `SeriesId`" —
  *both* entries need `SeriesId`, so it alone decides whether either can be built. The button
  and the menu read the same predicate on purpose: split them and they drift into a button
  that opens nothing. Nothing was lost with the duplicates, because "Go to series" / "Go to
  season" are also on the eyebrow above the title as `Hyperlink`s (`DetailSeriesLink` /
  `DetailSeasonLink`), gated on the same conditions and keyboard-focusable with UIA peers.
  Pinned by `phase5-tests/test-m4-detail.ps1` for the movie case (button *absent*, since
  Collapsed removes it from the UIA tree, while both toggles stay); the episode case is
  screenshot evidence in `.artifacts/detail-overflow/`, because reaching an episode detail is
  four navigation hops past where that suite goes.
- **External ratings (Phase 7 M1):** `MediaItem.ProviderIds` (populated in `MapOne`
  from `dto.ProviderIds.AdditionalData`, with `ImdbId`/`TmdbId` case-insensitive
  accessors) feeds a new `Metadata/` layer: `OmdbClient.GetByImdbIdAsync(imdbId, key)`
  — a static `HttpClient` + `System.Text.Json` client (15 s timeout, any failure returns
  null silently) hitting `omdbapi.com/?i={imdb}&apikey=`
  — returns `ExternalRatings(Imdb, RottenTomatoes, Metacritic)`. The detail view fetches
  it once per item (guarded by `_ratingsLoadedFor`) only when the `OmdbApiKey` setting
  and the item's IMDb id are both present, rendering up to three chips in `RatingsRow`
  beside the topaz community score — each is the **provider's logo + the score**: bundled
  `Theme/Logos/imdb.png` (gold badge) and `rottentomatoes.png` (tomato) images, plus
  Metacritic rendered as its signature band-colored score box (green ≥61 / yellow 40–60 /
  red ≤39). A blank key / no IMDb id / no match leaves the row collapsed (never an error). The key lives in a shared **External
  metadata** Settings section (`AppSettings.OmdbApiKey`). Chip AutomationIds
  (`RatingChipImdb`/`RatingChipRt`/`RatingChipMc`) sit on the inner TextBlocks — a
  WrapPanel/Border has no UIA peer. The key field in Settings is a masked `PasswordBox`
  with an eye **peek** button that swaps to a plain-text twin (`OmdbKeyReveal`) — the value
  is never logged. **Detail-view loads run concurrently:** `RefreshAsync` fetches the fresh
  item, then fires `LoadStreamsAsync`/`LoadSeasonAsync`/`LoadSimilarAsync`/`LoadRatingsAsync`
  in parallel (each updates only its own region), so the 15 s OMDB timeout can't stall
  "More like this"/cast the way the old sequential chain did. The cast/episodes/similar
  ListBoxes forward the mouse wheel to the page scroller (`OnContentWheel`). In the season
  episode list the episode being viewed stays selected as a garnet-lit, non-clickable
  "you are here" marker (`IsHitTestVisible=false` + an `OnEpisodeSelection` guard;
  `RequestBringIntoView` is suppressed so clicking it never scrolls the page).
- **Watch Trailer (Phase 7 M2):** implemented **keyless** — a deliberate deviation from
  the planned TMDB path (the user has no TMDB key). Uses Jellyfin's own
  `BaseItemDto.RemoteTrailers` (YouTube URLs the server's metadata providers already
  fetched — present on ~all test-server movies): `MapOne` picks the first trailer
  (YouTube preferred) into `MediaItem.TrailerUrl` via `PickTrailerUrl`. The detail view
  shows a secondary **Trailer** button (`DetailTrailerButton`, `smart_display` glyph)
  only when `TrailerUrl` is non-null; the click opens it in the default browser via
  `Process.Start(UseShellExecute)` — mpv stays reserved for library media, so the
  load-bearing HWND rule is untouched. No TMDB client, no key, no new Settings field.
- **Library filters & sorting (Phase 5 M3):** `GetItemsAsync` carries sortBy/sortOrder/
  filter/genre/NameStartsWithOrGreater; `LibraryView` shows the sort/filter/genre row
  only for paged folder grids (seasons/episodes/playlists keep their inherent order)
  and resets the grid on every change (a `_loadGeneration` counter drops stale async
  results). The A–Z rail is a letter-button strip (panels have no UIA peer — the
  buttons carry `Letter<X>` automation ids) shown only on Name sort; a letter reloads
  the paged grid via `NameStartsWith` — an **exact prefix**, so clicking M shows only M —
  and `#` resets to every letter. State is per-visit by design.

  Until 2026-08-05 this sent `NameStartsWithOrGreater`, the server's letter-*jump*
  parameter: clicking M returned M **through Z**. That is the right parameter for a client
  that scrolls a continuous list to a position, and the wrong one here, because the rail has
  no scroll or jump mechanism at all — a letter click wipes the grid and requeries from index
  0, so "or greater" only ever showed up as a filter that failed to filter.

  Two consequences worth knowing before touching this. **The server filters on `SortName`,
  not the displayed name**, and `SortName` strips leading articles: "The Matrix" and "The
  Mauritanian" correctly appear under M with displayed names starting "The". Any assertion of
  the form "every visible name starts with the clicked letter" is therefore wrong, and will
  fail on a correct build. **And an empty letter is now an ordinary outcome** — Q, X and Y
  are all empty in a 274-film library — where before only Z could come back empty, so
  `LibraryView`'s empty state names the letter instead of blaming "this view or its filters".
- **Responsive library layout (Phase 8 M3):** the header is a 2-row / 2-col grid.
  `OnViewSizeChanged` → `UpdateHeaderLayout` measures the left cluster (item count or the
  Play-all/Shuffle queue actions) plus the sort/filter cluster's intrinsic single-line
  width; when they can't share one line the cluster (a `WrapPanel`) drops to a full-width
  second row and flows onto more lines, so `SortBox` never clips and `OrderButton` never
  disappears at 700 px. The A–Z rail lives in its own reserved grid column (Auto width,
  so it collapses to 0 and can never overlap cards) and `UpdateRailVisibility` hides it
  below a 700 px view width (min size) as well as off Name sort. The genre controls
  (Genres button + dropdown) are both gated on `CollectionType` movies/tvshows.
- **Grouped search results (Phase 8 M3):** `SearchResultsView` binds a `CollectionViewSource`
  with a `PropertyGroupDescription` on the item `Type` (a converter maps to
  Movies/Shows/Episodes/Collections/People sections); results are pre-sorted by section rank.
  Each section is a `GroupStyle` with a Marcellus header and its own `WrapPanel`, so mixed
  portrait/landscape cards no longer share one ragged row. Cards use the shared
  `LwMediaGridItem` container (no default ListBoxItem chrome).
- **Single-season shortcut:** `LibraryView.LoadAsync`'s Series branch already fetches
  `GetSeasonsAsync`; when the result is exactly one season it then fetches that season's
  episodes (`GetEpisodesAsync`) and shows them instead of the redundant one-tile season
  grid. Strict count (`== 1`) so a show with a "Specials" season keeps the season list —
  otherwise Specials would be unreachable. Because a Series occupies a single browse-stack
  frame, only the frame's contents change: no frame is added/removed, so back-navigation
  and the series-name header are unchanged. The type-based card template selector renders
  episode (landscape) cards automatically, and Play all/Shuffle stay correct (a Series
  source routes through `GetSeriesEpisodesAsync`).
- **Library tiles as icons:** the Home "Libraries" rail renders each library as its
  collection-type Material glyph on a storm-2 card (`LibraryCardTemplate`) instead of
  Jellyfin's auto-generated collage preview. `MediaCardTemplateSelector` picks it for
  `MediaItem.IsLibrary` items (top-level `CollectionFolder`/`UserView`, or anything with a
  `CollectionType`), ahead of the episode/poster branches. The glyph comes from the shared
  `LibraryIcons.ResourceKey(collectionType)` mapping — the same one the nav rail uses — so a
  library's tile and its nav-rail entry always match; `CollectionTypeToIconConverter`
  resolves the key to the glyph string from `Theme/Icons.xaml` (codepoints stay defined in
  one place). Only the top-level library views satisfy `IsLibrary`, so media cards, box sets,
  and library grids are untouched.
- **Subtitle styling + audio options (Phase 5 M2):** `MpvPlayer.ApplySubtitleStyle`
  (sub-font/-font-size/-color/-border-color/-border-size/-shadow-offset; hex colors
  validated, empty = mpv defaults) and `ApplyAudioOptions` (audio-device, audio-spdif
  passthrough list, dynaudnorm via `af set/clr`) are called both at Create and from the
  settings live-apply. Passthrough and normalization are mutually exclusive
  (bitstreaming bypasses filters) — enforced in the Settings UI (each disables the
  other) and defensively in ApplyAudioOptions (passthrough wins). The **base subtitle
  position** (`SubtitleBasePosition`, sub-pos percent) is the resting value the
  control-bar lift composes against: `min(base, computed-lift)` while the bar shows,
  `base` when idle (previously hardcoded 100). Playback **speed** is an `MpvPlayer.Speed`
  property (0.25–4 clamp, OSD flash via `show-text`, reset to 1× in LoadFile);
  the overlay has a 7-preset popup and `+`/`-` keys step through the presets.
  Audio devices come from mpv's `audio-device-list` JSON — enumerable only while an
  mpv instance exists, so the settings combo shows a hint before first playback.
  The OSD has its own switcher (`AudioDeviceButton` beside the volume slider, hidden
  below two real outputs): a track-popup-style flyout whose garnet-selected row is
  `MpvPlayer.CurrentAudioDevice` (the applied mpv property, not the saved value); an
  OSD pick calls `SelectAudioDevice` live and persists to `AppSettings.AudioDevice`,
  so Settings and OSD share one source of truth. A saved device that is no longer
  present falls back to `auto` without rewriting the preference — validated in
  `ApplyAudioOptions` when mpv can already enumerate, plus a `RefreshAudioDevices`
  self-heal on TracksChanged for the early-Create case (mpv reinits the ao in place).
- **Playback queue (Phase 5 M1):** `PlayQueue` (on `AppViewModel`) is pure ordered
  state (items + current index, `Changed` event); `PlayerViewModel` mirrors it into
  overlay bindings (`QueueRows`/`HasQueue`/`QueueBadge`) and raises `QueuePlayRequested`,
  which MainWindow turns into a `PlayItem(..., fromQueue: true)` — non-queue playbacks
  clear the queue. Entry points: Play all / Shuffle buttons on season, series, playlist,
  album and artist views. Albums use a recursive audio query ordered by disc then track;
  artists use a recursive, duplicate-free audio query rather than direct children, so tracks
  nested under albums are included. Shuffle applies Fisher-Yates to the resulting playable
  queue. **Up Next precedence:** next-in-queue arms the card
  synchronously in `PlayItem`; the `AdjacentTo` lookup remains the no-queue fallback and is
  skipped while a queue is active. Both arm the card **regardless of the
  `AutoPlayNextEpisode` setting** — since 2026-07-30 that setting is passed through as
  `autoAdvance` and governs only whether the card advances by itself, so the
  queue-vs-setting precedence question it used to raise no longer exists (see the Up Next
  state machine below). EOF/countdown advancement flows through the same
  `UpNextPlayRequested` event, which advances the queue index via `TryAdvanceTo`.
  The queue popup ListBox is deliberately non-virtualized (queues are ≤500 rows;
  UIA readers need real containers). The playing row can't be removed.
- **Queue building on play (Phase 7 M15):** Play on a Season/Series filters the fetched
  episodes to `!Played` (a fully-watched container falls back to the full ordered list
  so Play is never a no-op; playlists keep full server order; Shuffle shuffles the same
  set). Detail-view Play on an **episode** routes through `OnEpisodePlayRequested`:
  fetch the show's episodes, slice from the chosen one to the end (no watched filter —
  an explicit start means "watch from here"), `Queue.Set(slice, 0)`, play as a queue.
  `GetEpisodesAsync`/`GetSeriesEpisodesAsync` now pass `UserId` — without it the server
  omits `UserData` and `Played` is always false.
- **Virtual episodes are excluded everywhere**: all three `Shows/{id}/Episodes` fetches
  (season list, series order, `AdjacentTo`) send `IsMissing=false`, so metadata-only
  placeholders (unaired/missing files) never enter season lists, queues, batch downloads,
  or episode stepping — matching Jellyfin's default "hide missing episodes" UX. Found by
  the final sweep when a test-server series grew a virtual Specials season: sampling the
  first "episode" for the batch-download quality picker silently returned no options.

## Shell / navigation

`MainWindow` hosts a state machine (Loading → Login → Browse → Playing): browse views
(Login/Home/Library/Detail) swap inside a `ContentControl` driven by a browser-style
navigation history (`ViewModels/NavigationService`, Phase 7 M3 — replaces the old
append-only stack); the mpv video layer (`VideoArea` + `MpvPlayerHost`) is shown only
while playing and its HWND/player are created lazily on first playback.

**Navigation history (Phase 7 M3).** `NavigationService` holds back + forward stacks of
`NavFrame(UserControl View, string Title, RadioButton? Rail, bool IsRailRoot)`. `Navigate`
pushes the current frame onto back and clears forward (standard browser semantics); frames
hold the **live view instance**, so Back/Forward restore scroll offset and filter/sort
state byte-for-exact. Crucially, rail Home and rail library clicks now `Navigate` (record
history) instead of truncating the stack the way they used to — so Back after a rail jump
returns to where you were, not Home. `IsRailRoot` + a rail-identity check de-dupes
re-selecting the rail item you're already on. New frames inherit the current frame's `Rail`
(a detail opened inside a library keeps that library lit); the active-rail highlight is
history-driven in `UpdateBrowseHeader` from `Current.Rail`. `BrowseBackButton` /
`BrowseForwardButton` show only when `CanGoBack` / `CanGoForward`. Search refines in place
via `ReplaceCurrent` (no per-keystroke history entry). `Frames` (back + current + forward)
is the fan-out set for the watched/favorite card-refresh broadcast.

**Warm multi-account sessions (Phase 7).** Profiles keep their sessions alive across
switches. `AppViewModel` holds `_sessions: Dictionary<string, JellyfinService>` keyed by
the HomeLayoutStore profile key (`{UserId:N}@{ServerUrl}`); `Jellyfin` is the ACTIVE
session (a fresh scratch instance while the login form is up, so a half-typed login can
never mutate a warm session). Switching to a warm profile is pure pointer swap + a
BACKGROUND `ValidateAsync` (401/403 = revoked → `CloseSession` + toast + login form;
transient network errors are NOT treated as invalid) — `LIGHTWEAVER_SESSION_LOG` traces
`warm-activate` / `cold-connect {ms}` / `revalidate-*` / `logout` for the verify suite.
`MainWindow` mirrors this with per-profile `ShellSession` bundles (NavigationService +
Home/search views + rail `RadioButton`s — NavFrame.Rail identity survives the round
trip), swapped in `OnSessionActivated` before the Browse state re-applies; the
user-data fan-out only ever touches the active shell, so profiles can't cross-pollute
cached grids. Playback pins `_playbackJf` — the session that started it — for progress
reports (`PlaybackReporter` samples its client per `Start`), queue advancement, Up Next,
episode stepping and transcode retries, so a mid-play switch never retargets a running
playback; leaving playback lands on the ACTIVE profile's shell. Downloads resolve
URL+auth per item via `AppViewModel.FindSessionByServer(item.ServerUrl)`, so in-flight
downloads survive profile switches and pause only when their originating session closes.
`JellyfinService` is `IDisposable` (per-session `HttpClient`); logout closes + disposes
the session and prunes its shell. The switcher (`SwitchServerWindow`) marks
instantly-switchable rows with "· warm".

**Mouse back/forward (Phase 7 M7).** The mouse side-buttons map to navigation the standard
Windows way — XButton1 = back, XButton2 = forward. WPF's `MouseAction` enum has no XButton
members, so these can't be `InputBindings`; instead a window-level `PreviewMouseDown`
(`MainWindow.OnWindowMouseDown`) branches on `e.ChangedButton`: in Browse it drives
`BrowseBack`/`BrowseForward`, in the player XButton1 = stop-and-return (XButton2 reserved).
The overlay is a separate top-level window over the video HWND, so it carries the same
mapping (`OverlayWindow.OnOverlayMouseDown` → `BackRequested`, suppressed in mini mode).
No global OS hook — WPF surfaces the thumb buttons through routed events.

**Advanced search (Phase 7 M4).** `AdvancedSearchView` is a browse-stack frame that mixes free
text with multiple filters at once — multi-select item types + genres (via a new `CheckDropdown`
control), watched state, and a year range — over a paged, infinitely-scrolled `LwMediaGridItem`
grid (the same generation-guarded stale-drop as `LibraryView`). The filter set is the immutable
`AdvancedSearchQuery` record, mapped onto one recursive `/Items` call by
`JellyfinService.AdvancedSearchAsync`; a seed-query constructor overload lets M5/M6 deep-link in
pre-filtered. Entry is an "Advanced" affordance in the browse header beside the quick-search pill.
Cast/crew portraits on the detail view are clickable (`PersonEntry.Id` → `PersonSelected` →
`OpenAdvancedSearch(PersonIds=[id])`), deep-linking into a person-filtered advanced search (Phase 7 M5).
The detail view's genre row is likewise per-genre clickable — `RenderGenres` builds `Hyperlink`
inlines (`DetailGenreLink`) via the shared `MakeLink` helper (also used by the eyebrow and season
"view all" links); a click raises `GenreSelected` → `OpenAdvancedSearch(Genres=[g])`, the *global*
genre path (Phase 7 M6). The library `Genres` button → `GenresView` → library-scoped
`LibraryView(genre)` drill (Phase 5 M5) is deliberately kept as the within-a-library path.

**Home rail "see all" section views (Phase 7).** Each content rail header on Home is a
see-all link (`LwSectionHeaderLink` — the Marcellus title plus a quiet chevron that lights on
hover) raising `HomeView.SeeAllRequested(HomeSectionId)`; `MainWindow.OnSeeAllRequested` pushes
ONE generic `SectionView` (a paged `LwMediaGridItem` grid with infinite scroll, a mono item
count, M25 skeleton first-load, and the M24 empty state) parameterized by a
`Func<int,int,Task<(List<MediaItem>,int)>>` query delegate. Continue Watching and Next Up page
their dedicated endpoints natively (`StartIndex` + `EnableTotalRecordCount` —
`GetResumePagedAsync`/`GetNextUpPagedAsync`); Recently Added is the equivalent recursive
`/Items` DateCreated-desc query (`GetLatestPagedAsync` — the rail's `/Items/Latest` endpoint
cannot page), and Favorites is the rail's query paged (`GetFavoritesPagedAsync`). The Libraries
rail has no see-all: it already lists every library. `SectionView` participates in the
`ItemUserDataChanged` fan-out so watched/favorite toggles update its cards in place.

**The loading language (2026-08-02).** Every surface that presents items reports that it is
fetching them, in one of three registered ways. The tier is chosen by *what is already on
screen*, not by which control is loading:

| Tier | When | Mechanism | Timing |
|---|---|---|---|
| 1 | first paint of an empty surface | skeleton, shape-matched to what will land | immediate |
| 2 | fetch while real content is on screen | activity dots | after 250 ms |
| 3 | rails filling in around a painted page | skeleton rail | after 250 ms |

The threshold is `SkeletonFactory.RevealDelayMs` (250). It is a C# const, not a XAML duration
token, because every consumer is a code-behind timer — a resource key nothing binds to would
be dead weight. 250 ms sits above LAN response times, so on this network an indicator that
would only flash is never shown at all, and below the ~400 ms at which a still screen starts
reading as stuck. Tier 1 deliberately does **not** wait: holding a blank screen for a quarter
second at navigation time is itself the thing that reads as broken.

**The video host needs its own window class, or the indicator is invisible (2026-08-03).**
The child HWND mpv renders into was created from the stock Win32 `"static"` class. A `"static"`
control has no background brush of its own: `DefWindowProc` erases it with the brush returned
from `WM_CTLCOLORSTATIC`, WPF's host does not answer that message, and the fallback is
`COLOR_WINDOW` — the **system** window colour, `#F3F3F3` under a light Windows theme. The app's
dark theme is WPF-side and never reaches a raw HWND. So starting playback flashed white, and
because the activity dots are near-white (`LwLightCore #DDF1FA` at 0.22–0.55) they disappeared
into it — which is how the user reported it: "the screen is white and the loading indicator
cannot be seen".

`MpvPlayerHost` now registers its own class whose only purpose is an `hbrBackground` of
`LwStorm0`, the same colour as the letterbox bands, so loading → first frame is seamless.
It must be the **class brush**: the window is created `WS_VISIBLE`, so it erases itself inside
`BuildWindowCore` — *before* `HwndHost` has installed the subclass that routes messages to
`WndProc` — and nothing invalidates it afterwards, so a `WM_ERASEBKGND` override compiles and
never runs. `lpfnWndProc` is `DefWindowProcW`'s own address rather than a managed delegate, so
no callback lifetime has to outlive the windows built from the class.

Measured by bursting DXGI frames from the Play click, 30 ms apart: unfixed, the first frame
reads **216–217** mean luminance and every later one 2.8 (void black), so the flash is short —
one frame at that granularity, because the brush erases once and mpv then presents black over
it. Its brevity is not the point; **where** it lands is. The captured frame shows the loading
overlay fully composed — title, RTX badges, caption — over white with the dots gone. Fixed:
**0 bright frames of 45**. Pinned by a leg in `phase10-tests/test-loading-indicator.ps1` that
bursts from the click and fails above 100 mean luminance; confirmed to fail on the reverted
build (1 frame at 206.7) while all four UIA loading assertions still passed, which is precisely
why the guard has to be made of pixels.

**The activity indicator is `Views/ActivityDots` (2026-08-03), and it replaced a ring.**
Tier 2 originally reused the player's `LwBufferingIndicator` at 18 px via `BasedOn`, on the
reasoning that both target `Ellipse` so the breathe storyboard would be inherited. It was —
and that was the mistake. The breathe is `0.92 → 1.08` scale over `LwDurAmbient` (2.4 s),
which at 44 px over dark video reads as ambience but at 18 px is a **1.4 px change spread
across two and a half seconds**. It read as a static circle, and review rejected it. The
lesson is about size, not about the storyboard: a motion budget does not scale down with the
control it is attached to.

The replacement is three dots throbbing in sequence — 1.05 s per dot, staggered 140 ms, each
bright for about a third of its cycle so the lit one appears to travel. It is a **control, not
a style**, because three staggered children cannot be expressed as setters on one `Ellipse`,
and its storyboard is assembled in **code**: the keyframe values come from dependency
properties (`DotSize`, `Gap`, `DimOpacity`, `PeakOpacity`) and a XAML storyboard holding
bindings cannot be frozen. The timing is therefore C# consts rather than a `Metrics.xaml`
`Duration` token — a token nothing binds to would be a second source of truth, and
`Metrics.xaml` carries a pointer to the file instead.

Two things it does on purpose:
- **`OnCreateAutomationPeer` returns null.** The `Ellipse` it replaced was a `Shape` and had
  no automation peer, so suppressing the peer keeps the UIA tree byte-identical to what the
  suites already assert against. The ids stay on the transparent marker `TextBlock` beside it.
- **The storyboard runs only while `IsVisible`.** These indicators are collapsed almost all of
  the time and WPF keeps animating collapsed elements; five instances × six timelines is not
  free.

The player uses the same control at `DotSize="9" Gap="7"` but with **`PeakOpacity="0.55"`** —
that overlay composites over HDR video and 0.55 is its glow budget, so the browse pills' 0.95
peak is not available there. `LwBufferingIndicator` and `LwActivityRingSmall` are both gone
from `Theme/Controls.xaml`; the comment left in their place records why. The player's
dots+caption *composition* is still deliberately un-extracted — it is specific to the
transport interplay and pinned by `test-buffering.ps1` and `test-loading-indicator.ps1`.

**The generation-guard contract** is the part a naive implementation gets wrong, and the two
halves are asymmetric:

- An **append-owned** indicator (`LoadMoreIndicator`, one per grid) hides **unconditionally**
  in its `finally` — on success, on exception, and on the stale-generation early return. That
  is safe precisely because a superseding requery paints its own full-grid skeleton through
  `ResetAndReloadAsync`, so there is no other owner's indicator to stamp on.
- A **shared** indicator (the top-bar `SearchSpinnerHost`, one for the whole shell) hides only
  when `gen == _searchGeneration`, so a superseded search cannot switch off the ring that the
  newer search still running behind it owns.

Backwards, the first latches the skeleton and the second flickers the ring off mid-fetch on
every fast follow-up keystroke. Both directions are pinned by the idle re-check at the end of
each leg in `backlog-tests/test-loading-items.ps1`.

Surfaces and their ids (the id names still say "spinner" — they are load-bearing strings in
live suites, and renaming them would buy nothing): `GenresSkeleton` (tier 1, tile-shaped
200×90 to match the real
template — not the poster card), `SearchSpinner` (tier 2, covers both the first search, where
no results view exists yet to skeleton, and a refinement, where blanking visible results would
be worse), `LoadMoreSpinner` (tier 2, on all three infinite-scroll grids), and
`DetailSimilarSkeleton` / `DetailEpisodesSkeleton` (tier 3, `Rail(withTitle:false)` and
`Rows()` respectively, since episode rows are full-width rows and the card rail would promise
the wrong silhouette). Deliberately excluded: the detail view's media-stream pills and OMDb
ratings — metadata enrichment amending an already-readable row, where OMDb's occasional 15 s
would make a spinner read as breakage.

`LIGHTWEAVER_SLOW_LOAD_MS` now also covers the append path and the search path; without it the
LAN outruns UIA polling and the correct behaviour is to show nothing, so every leg would fail
against a working build.

**Advanced Search visual polish (2026-07-15).** The global Advanced Search screen navigates
*railless* (`NavFrame` with `Rail=null`); `UpdateBrowseHeader` then calls `ClearActiveRail`
(unchecks `NavHome` + every `LibrariesNav` item) so no library reads active there, while Home
and library frames still light up via `SetActiveRail`. The browse-header search pill
(`SearchPill`) shrinks responsively 360→200 px via `UpdateHeaderLayout` (invoked from the window
`SizeChanged`) so it never clips on narrow windows. The shared card templates' no-artwork
fallback is a centered dim `IconMovies` glyph sitting *behind* the `CachedImage` (real posters
cover it) instead of the item title — artless items no longer show a duplicated title. Its
`AsSearchBox` uses `LwTextField` (rounded rectangle), not the `LwSearchField` pill, to match the
adjacent filter controls.

**Advanced Search redesign (2026-07-19).** The screen was rebuilt to the generated spec at
`design/advanced-search-redesign/SPEC.md` (11 problems → solutions). Key mechanics: a
deliberate two-row filter band (fixed row assignments in a `Grid`; only Search/Genre star-shrink);
**type-grouped sections** for mixed-type results — one capped (24) `AdvancedSearchAsync` per
selected type in parallel, rendered as Marcellus header + mono count + "Show only <type> →"
link over a plain-template `ListBox` of the native shared cards, with a single-hit-type
fallback to the flat grid (paging stays on the flat grid only); a **prompt state** instead of
an unconstrained server dump (`HasAnyConstraint` gates the first query; starter chips set the
Type facet); the **global search pill + Advanced affordance hide** while the current nav frame
is an `AdvancedSearchView` (in `UpdateBrowseHeader`); deep-link seeds (M5 person via the new
presentational `AdvancedSearchQuery.PersonName`, M6 genre) render as a **removable garnet
context chip** (new `LwGarnetChipFillBrush`/`LwGarnetChipBorderBrush` glass tokens) that stays
in sync with the Genre dropdown; engaged facets light a stormlight ring overlay; the sort-order
toggle is labelled per sort field (A–Z / Newest / Highest, hidden for Random); `CheckDropdown`
gained real checkbox rows, a joined-labels + count-badge summary, and a "Clear selection"
footer. Query contract (`AdvancedSearchQuery`/`AdvancedSearchAsync`) and the M4 AutomationIds
are unchanged; suites: `test-ass-redesign.ps1` (new) + updated `test-advanced-search.ps1`
(grouped-aware) and `test-genre-search.ps1` (sections + seed chip).

**Person filter you can type (2026-08-05).** Until now the person filter existed but had no
input: `PersonIds` could only be set by the cast-face deep link, so it was a filter nobody could
reach from the search screen itself. Advanced Search's disclosure gained a **PERSON type-ahead**
— `JellyfinService.SearchPeopleAsync` (`/Persons`, `SearchTerm` + `Limit` + `UserId`) returning
the existing `PersonEntry` rather than a new record, so the typed picker and the deep link
converge on one type and one piece of state (`_personIds`/`_personName`, rendered by the same
garnet chip). The box is a *transient picker*: it empties on commit, and the chip is the only
witness of the applied filter.

Four things here are measured rather than assumed, and each would be easy to get wrong twice:

- **`/Persons` matches substrings and orders alphabetically by full name, not by relevance.**
  `searchTerm=tom` returns "Azgad Crisostomo" and "Edward Holtom" and *no* `Tom*` at all inside
  40 rows. Typing more resolves it immediately (`tom h` → Tom Hanks first; `hanks`, `basinger`,
  `eminem` each land on one person), so no client-side re-ranking was added — but any assertion
  of the form "the first suggestion is our person" would be asserting the server's collation, and
  the suite deliberately asserts *contains* instead. `TotalRecordCount` is also capped at `Limit`
  on this endpoint, so it cannot be used to size the result set.
- **Suggestion rows are `Focusable="False"`** (`LwSuggestItem`). A focusable row takes keyboard
  focus on click → the anchor `TextBox` raises `LostKeyboardFocus` → the popup closes *before*
  the click commits. Arrow keys drive `SelectedIndex` from the box's own key handler, so the caret
  never leaves the box, and the `Popup` is `StaysOpen="True"` (unlike `CheckDropdown`'s) because a
  capture-taking popup would spend the user's first click back into the box on closing itself.
- **The commit is not gated on `IsMouseOver`,** which was the first shape written here. A UIA
  `SelectionItemPattern.Select()` sets no mouse state, so a mouse-only guard makes the commit
  undrivable by a live suite — untestable by construction. It is gated on a
  `_personKeyNavigating` flag instead, so arrow-key highlighting opts out and everything else
  commits.
- **Row A is a `WrapPanel` now, not a fixed 4-column `Grid`.** The Grid could not reflow, so a
  visible seed chip pushed the last column past the window edge: measured at the 700×480
  minimum, `AsMoreFiltersButton` sat at x=1187..1356 against a window ending at 1250 — two
  thirds off-screen, and with it the only route to GENRE, WATCHED, YEAR and PERSON. Applying a
  person therefore locked you out of changing it. `SEARCH` takes a fixed 300 (it was `*` between
  234 and 314) because a `WrapPanel` has no star sizing. The suite now asserts this as
  **geometry at the real minimum with a chip applied**, because the two presence assertions that
  were already there are precisely what let it through.

The debounce is a 300 ms `DispatcherTimer` (the input-debounce idiom of `SearchBox` and the
shell's quick search — *not* the 250 ms `SkeletonFactory.RevealDelayMs`, which is the
indicator-reveal rule), with a two-part staleness guard: a generation counter *and* a re-read of
the box, because `WithRetry` can hold a stalled lookup long enough for an older request to land
after a newer one and commit a person the user never saw. A lookup failure degrades silently the
way `LoadGenresAsync` does; `StatusText` belongs to the results region and is not hijacked.

**Left nav rail (Phase 6 P1).** Primary navigation is a left icon rail (woven-light
`LwNavRailItem`/`LwNavRailButton` styles), replacing the old top File menu. It carries
Home (a `RadioButton` in group `NavRail`; the checked "you are here" state is a stormlight
wash + 2 px light bar + lit glyph — never a gem fill), the server's real libraries (fetched
via `GetLibrariesAsync`, one `RadioButton` each, iconed by `CollectionType`), Settings, and
a profile affordance. The profile button's `LwContextMenu` holds the migrated File actions
(Open Ctrl+O, Switch user/server Ctrl+U, Update, Log out, Exit); all `InputBindings` are
unchanged. Below 1160 px the rail collapses 232 → 64 (icons only, labels → tooltips) by
setting the inherited `Theme.NavRail.IsCollapsed` attached flag on the rail container, which
cascades to every item template. The rail lives inside the browse layer, so it hides
automatically during playback (no explicit fullscreen/mini toggling). Search moved into a
pill in the content header; `SearchBox`/`BrowseTitle`/`BrowseBackButton` AutomationIds and
behavior are preserved. Window minimums are `MinWidth=700 MinHeight=480`.

Home rows forward plain mouse-wheel input to the page ScrollViewer (nested horizontal
ScrollViewers otherwise swallow it); Shift+wheel scrolls the hovered row.

**Wheel coverage (Phase 7 scroll-wheel audit).** `Views/WheelScroll.cs` is a shared
`PreviewMouseWheel` forwarder for regions that sit *outside* a scroller and were wheel-dead:
the library header band and A–Z rail forward to the library grid's scroller, the Advanced
Search filter band to its results grid. The detail view's inner lists forward vertical wheel
to the page and honor Shift+wheel for horizontal rows (same contract as Home). Wheel over a
closed ComboBox scrolls the page and never changes the selection (WPF default, asserted by
`test-scroll-wheel.ps1` via raw `MOUSEEVENTF_WHEEL` injection + UIA ScrollPattern offsets).

**That audit's "16/16, complete" was wrong twice, corrected 2026-08-05.** No scrolling defect was
found on re-audit — every surface scrolls — but the claim of coverage did not hold:

- **Two of the sixteen had stopped running.** The advanced-search leg waited for `AsResultsGrid` on
  "the default broad query", which the 2026-07-19 ASS redesign replaced with a prompt state that
  deliberately runs nothing. It then *threw* on the null rather than failing, taking the two
  Settings assertions after it down with it. So the suite had been aborting four days after the
  audit declared it complete. It now clicks a starter chip first, and wheels over `AsSortBox`
  rather than `AsWatchedFilter` — P10 M11 moved the latter behind the More-filters disclosure,
  where it is not in the tree at all.
- **Three surfaces were never in it.** The genres grid; the shortcuts overlay's scroller; and the
  nav rail, whose `ScrollViewer` did not exist until 2026-08-03 and so could not have been audited
  in July. All three scroll natively (measured: genre grid 0→100, shortcuts overlay 0→17.8 at the
  window minimum, nav rail 0→88.3). The rail's scroller also had no automation id, which is why
  nothing could have asserted it; it is `NavRailScroll` now.

Two measurement notes for anyone extending that suite: UIA reports `VerticalScrollPercent = -1`
(`ScrollPattern.NoScroll`) when content fits, so a skip guard testing for `0` misreads it as a
scroll position — the shortcuts overlay does not overflow at every size and must be tested at one
where it does. ("Only at the 700×480 minimum", as this said until 2026-09-05, was measured with a
stale row census: it already overflows at a 1280×720 DIU window, which is what 1920x1080 at 150%
is — see the fold guard above.) And `FindFirst` does not accept `TreeScope.Ancestors`; it throws. Climb with a `TreeWalker`
to reach a popup list's enclosing scroller.

Overlay gotchas found live (both are regressions any hide/show overlay scheme hits):
- A hidden-then-reshown owned window can land **below** its owner and silently swallow
  all mouse input → `EnsureAboveOwner()` (SetWindowPos HWND_TOP, NOACTIVATE) on every
  bounds sync.
- State changes run before layout — sizing the overlay from `VideoArea.ActualWidth`
  right after making it visible reads 0 → sync skips unmeasured layouts and re-runs on
  `VideoArea.SizeChanged`.
- **Keyboard focus follows overlay clicks (Phase 9 M1)** — the cause of the long-standing
  "player shortcuts work only sometimes". Every shortcut lives in `MainWindow`; the overlay
  had no key handling at all. `ShowActivated="False"` only governs `Show()`, so any click on
  the overlay — click-to-pause, the seek bar, any OSD button — moved keyboard focus to the
  overlay and every shortcut died until the main window was re-activated by other means.
  Fix: the overlay forwards **`PreviewKeyDown`** to `MainWindow.HandleOverlayKey`, which runs
  the shared `HandlePlayerKey` and then retries unhandled keys against the window's own
  `InputBindings` (so Ctrl+M, F11 and Esc work from the player too — they had the identical
  bug). `WS_EX_NOACTIVATE` is also set on the overlay, but it is **not sufficient alone**:
  WPF focuses the clicked control and that `SetFocus` makes the overlay the focus window
  regardless — measured, an arrow key after a video click moved the volume 50 → **50.1**,
  i.e. the focused `Slider` consumed it as its own `SmallChange`. Tunneling (not bubbling) is
  required for the same reason: a focused Slider marks arrow keys handled before a bubbling
  window handler would ever see them.
- **A click on the overlay DOES change activation now (B33, 2026-08-06)** — read the entry above
  with this one, because for a year `WS_EX_NOACTIVATE` meant a click on the overlay activated
  nothing at all, and that turned out to be a user-visible bug: *"it doesn't regain focus when I
  click with the mouse; when I ALT+Tab, it works"*. The overlay covers the whole video area, so
  while the player runs it is the window the mouse lands on, and a `NOACTIVATE` window answers
  `WM_MOUSEACTIVATE` with `MA_NOACTIVATE` and forwards activation to nobody. Alt+Tab activates
  `MainWindow` directly and never consults the overlay, which is exactly why that one route kept
  working and hid the bug for so long. Fix: a `WM_MOUSEACTIVATE` hook
  (`OverlayWindow.OwnerActivationHook`) that activates the **owner** — the same direction Phase 9
  M1 wanted focus to go, so the two are not in tension — and returns **`MA_NOACTIVATE`**, letting
  the click through so one click both focuses and acts. Two traps, both measured:
  - **Guard on the foreground window's PROCESS, never its handle.** `WM_MOUSEACTIVATE` arrives on
    *every* video click, because this owned popup is never itself the active window even while the
    app is frontmost. A handle comparison against the owner also ate clicks whenever any other
    window of ours held the foreground — and the overlay routinely does: `test-shortcut-focus`
    reports `foreground = 'overlay'` after all three of its interaction paths. On the handle
    version the click-to-pause leg failed while both click-to-activate legs still passed.
  - **The click used to be EATEN, and that was reversed on 2026-08-09.** It returned
    `MA_NOACTIVATEANDEAT`, so the first click on an unfocused player only focused it — you clicked
    once to focus and again to pause. The argument for it was that click-to-focus is the intent and
    the pause an unasked-for side effect; the user reported the two-click result as a bug and stated
    the behaviour they wanted: *click LightWeaver while it is unfocused and the video pauses*, in
    one click. The argument was ours rather than theirs, so it lost.
    - **The negative control that justified eating does not survive scrutiny of what it measured.**
      It ran with the hook **off**: the click paused the video while the app stayed in the
      *background* — side effect done, focus not gained. The current path still calls
      `SetForegroundWindow(owner)` **first** and only then lets the click through, so both happen.
      That distinction is the whole case, and it was available in August too.
    - **What passing through changes, none of it separable** — `WM_MOUSEACTIVATE` carries no notion
      of *where* the click landed, so this is per-window, not per-control: an OSD button now acts on
      the first press; a double-click on unfocused video now reaches fullscreen (it used to pause,
      because the eaten press reset WPF's click count — an "accepted cost" that is simply gone);
      mini-window drag works on the first press; XButton1-back acts.
    - **Residual case, now the lesser cost:** if `SetForegroundWindow` loses to another process's
      foreground lock (`activated owner=False` in the UI log), the click acts on a player that
      stayed in the background. Previously that case was a dead click instead.
  Guarded by `backlog-tests/test-focus-on-click.ps1` (local only — real player, real mouse, real
  foreground changes). It takes the foreground with a window **of its own** rather than another
  application's, and raises the app's Z-order without activating it, since a backgrounded app can
  end up under something else and the click is then silently skipped.
- **A double-click on the video used to toggle pause on its way to fullscreen (B34, 2026-08-06;
  re-answered 2026-08-09)** — WPF raises `MouseDown` **twice** for a double-click, `ClickCount` 1
  then 2, and `OnRootMouseDown` acted on both, so a double-click left playback in the opposite
  state. **Every press now toggles pause immediately, and the promoted second press also toggles
  fullscreen** — press 2 undoes press 1's pause, and the pair nets to zero.
  - **The first answer was a deferral, and it was the wrong shape.** B34's fix waited out the
    system double-click time (`GetDoubleClickTime()` + a 40 ms margin) before committing the
    single click, reasoning that nothing can know at press 1 which gesture is being made. The
    premise is true; the conclusion is not — press 2 can **undo** press 1 rather than press 1
    waiting for press 2. It was reported as input lag and then measured: **554 ms** from click to
    pause on a default 500 ms mouse setting, against 21 ms for the same action from the OSD button
    and 6 ms from the keyboard. After the change the video click measures **19 ms**, i.e. the same
    as the OSD button. The harness is `bug-tests/measure-input-latency.ps1`, which times all four
    input routes plus a browse-screen control, and prints a poll-cost noise floor so the legs can
    be read against it.
  - **No mainstream player defers.** YouTube, Netflix and MPC-HC all act on press 1 and let press 2
    cancel it; VLC and mpv sidestep the conflict by leaving single-click unbound (mpv's default
    `input.conf` binds only `MBTN_LEFT_DBL`). Deferring was ours alone. The user chose the
    act-immediately camp.
  - **The accepted cost is now a transient pause** for the duration of the gesture — typically
    ~150 ms — with the OSD glyph flickering with it, exactly as on YouTube. That is the right way
    round: the artefact costs a *double*-click a frame, where the deferral cost *every single*
    click half a second.
  - `e.ClickCount == 2`, not `% 2 == 0`: the fullscreen transition is synchronous and restarts
    WPF's click tracking, so a four-press burst reads 1, 2, 1, 2 rather than 1, 2, 3, 4 (measured
    — see the same suite's leg 5).
  Guarded by `backlog-tests/test-double-click-fullscreen.ps1`. It asserts **both** halves of the
  gesture, because fullscreen always worked, and leg 3 now asserts the click is acted on **within
  250 ms** as well as that it pauses at all — a bound chosen to sit below the removed deferral and
  well above the ~19 ms the current build costs, so the deferral cannot come back unnoticed.
  **Synthetic double-clicks need realistic press durations** — a first version fired down/up with
  no delay and WPF reported `ClickCount=1` for both presses, intermittently, so one gesture in a run
  promoted and another did not. Hold the button ~80 ms. **The suite needs an uncontended desktop**:
  on a run where the app lost the foreground, `OwnerActivationHook` ate the first press of each
  gesture (a `MouseActivate:` line in `LIGHTWEAVER_UI_LOG` — it read `click eaten` at the time and
  reads `click passed through` since 2026-08-09) and legs 2–4 failed with no
  `RootMouseDown` line at all — read that signature as the desktop, not as the app.
- **RTX toggles hide rather than disable (Phase 9 M4)** — `Visibility` bound to `RtxAvailable`,
  which already merges the static GPU cause (`GpuCapabilities`' EnumDisplayDevices probe) with
  the dynamic PQ/HLG suppression. `IsChecked` remains the user's stored preference, so an
  unavailable source hides the toggles without unchecking them. Consequence to know: the
  per-cause reason strings are only reachable while available — a hidden element has no tooltip
  — so `backlog-tests/test-rtx-availability.ps1` asserts presence/absence per cause instead.
- **Settings rows and the 480 px minimum (Phase 9 M5)** — the download-folder row is the first
  with three controls, and it does not fit the old idiom (fixed-width field docked right, label
  filling): box + Browse + Clear needs ~475 px, more than the card has at the window minimum,
  and the row is silently pushed out of the card, which `ClipToBounds` then hides. Controls dock
  right with real minimums and the **label** fills and wraps. Folder picking uses
  `Microsoft.Win32.OpenFolderDialog` (WPF-native since .NET 8 — no WinForms reference).
  Test-side: an **owned** window (Settings, like the overlay) is a child Window element of its
  owner in the UIA tree, not a child of the desktop root, and geometry checks must measure
  against the *card*, not the window, or clipped controls pass.
- **OSD flyout rows (Phase 9 M3)** — all four track/device lists share `LwOsdFlyoutItem`, a
  container style whose template is a bare stretched `ContentPresenter`. The default Fluent
  `ListBoxItem` neither stretches its content nor drops its own padding, which is why the garnet
  selection border hugged the text instead of spanning the row. Selection in these lists is
  **data** state (`TrackOption`/`AudioDeviceOption.IsSelected`, from mpv), never
  `ListBoxItem.IsSelected`, so the container deliberately carries no selected/hover chrome — but
  its Border must keep a non-null `Background`, or the gaps between glyphs fall through to the
  video window. The device list uses its own `AudioDeviceOptionTemplate`: the track template
  reserves the language badge's 26+8 px even when empty (deliberate, to keep track rows aligned),
  and device rows have no badge, so reusing it left a dead 87 px indent.
- **Clicking the volume track sets the volume (2026-08-05)** — `VolumeSlider` gained
  `IsMoveToPointEnabled="True"`. Without it WPF routes a track click to the
  `Slider.DecreaseLarge`/`IncreaseLarge` `RepeatButton`s that `LwSlider`'s template puts in the
  `Track`, and `RangeBase.LargeChange` defaults to **1** — so a click anywhere on the track moved
  the volume by one, measured 50 → 49. With the attribute, a click at the 15 % mark gives 11.1.
  `SeekSlider` has carried the attribute since it was written; the volume slider shares its
  *style* but not this per-instance property, which is why only one of the two ever behaved — a
  reminder that `IsMoveToPointEnabled` cannot be set from a `Style` the two sliders share, so any
  future slider needs it declared on the instance. `test-volume-pill.ps1` asserts it with a real
  click, since every other assertion in that suite drives the slider through
  `RangeValuePattern.SetValue` and so cannot see click behaviour at all.
- **Wheel over the player = volume (Phase 9 M2)** — handled on the overlay root grid, ±5 to
  match the Up/Down shortcuts. `e.Handled = true`. Nothing was needed to protect the flyouts:
  popups are their own HWNDs, so a wheel over an open list scrolls that list and never reaches
  this handler. It used to call `ShowControls()` + restart the idle timer so the slider was
  seen moving; **P10 M10 removed that** — the volume pill carries the feedback now.
- **Loading state** — `MpvPlayer.IsLoading` spans `LoadFile` → `MPV_EVENT_PLAYBACK_RESTART`
  (mpv's "ready to display frames"). While it holds, `ControlsPanel` is `Collapsed` via
  `PlayerViewModel.TransportVisible` and the centre ring shows with the caption `Loading…`;
  `Buffering…` is the same ring driven by `paused-for-cache`. Three decisions in it:
  - **`PlaybackRestart`, not `FileLoaded` or `time-pos`.** `FILE_LOADED` fires when the demuxer
    has headers — long before a frame exists — and `time-pos` reads a resume position while
    nothing has been decoded. Either would end the loading state too early, which is the whole
    defect being fixed.
  - **Hidden, not dimmed.** `Visibility` removes the bar from the UIA tree, so "the controls are
    absent during loading" is assertable rather than a matter of appearance. A bar parked at
    0:00 with a live pause glyph is indistinguishable from a stuck player — the user reported
    exactly that after B31.
  - **A stall keeps the controls.** Only loading hides them: mid-film the position is real and
    seeking out of a stall is reasonable. `HideControls` also bails while `IsLoading`, so Back and
    the title stay up — without that the idle timer, which can expire before playback begins,
    left the ring alone on black with no way out but a mouse move.
  Covered by `phase10-tests/test-loading-indicator.ps1` and two assertions in `test-buffering.ps1`.
- **Passthrough failure recovery (B31)** — bitstreaming is all-or-nothing per device, and mpv
  handles the "nothing" case by wedging: when the output rejects the spdif format it retries as
  AC3, walks its AO list, then re-opens the track with the software decoder **without bringing an
  audio output back up**, and playback never starts — no clock, no audio, no frames, while the
  demuxer fills its cache. `MpvPlayer.EvaluateAudioOutput` recovers by clearing `audio-spdif` and
  deselecting/reselecting the track (the one sequence that reliably makes mpv re-run audio init —
  and what a manual seek was achieving by accident), then raises `PassthroughUnavailable` for the
  toast. Per playback: `AudioPassthrough` stays the saved preference so a capable output starts
  working again on its own. Details that are load-bearing:
  - **The trigger is a 1.2 s debounce, not an edge.** mpv clears `current-ao` while tearing down
    the previous output on *every* load, so acting on the null itself fires on ordinary AAC files
    — measured, by the control leg of the suite. A healthy output appears ~190–300 ms after the
    file opens; what marks the failure is a null that persists.
  - **The eval reads `current-ao` LIVE, never the observed cache (B32).** mpv does not publish
    the property going away when playback stops, so the cache can still hold the previous file's
    `"wasapi"` while the new file has no output — which made the recovery fire once per session
    and then stand down: any settings save re-arms passthrough (`ApplyAudioOptions` resets
    `_passthroughGaveUp`, by design), and the next playback then hung at Loading… forever.
    Measured 2026-08-01: `currentAo=wasapi liveAo=(null)` in the stuck state — the live read is
    truthful there.
  - **`HasAudioTrack` gates the recovery but must not end it (B32's second mode).** On a slow
    server open the debounce can beat mpv's track list; "no audio track" is then a transient,
    not a fact about the file. While `IsLoading`, the eval re-arms and looks again; a genuinely
    video-only file resolves when `PLAYBACK_RESTART` (or `EndFile`) clears `IsLoading`, which
    ends the polling.
  - **The watchdog traces to `LIGHTWEAVER_UI_LOG`** (`AoWatchdog arm/eval/skip` lines, the
    `OsdGeometry` precedent) — B32 was diagnosed from exactly that trace, and its silence was
    the reason the first misdiagnosis survived a round.
  Covered by `phase10-tests/test-passthrough-fallback.ps1` and (B32, fail-without-fix measured)
  `phase10-tests/test-passthrough-rearm.ps1`.
- **Volume normalization (`dynaudnorm`)** — verified live 2026-08-01 (mpv reports
  `dynaudnorm (dynaudnorm.00)` in its user filter list and instantiates it in the chain), so the
  sibling of the B31 setting is sound. There is deliberately **no capability probe** for it: it is
  a software filter inside mpv, not something an endpoint has to accept, so there is nothing to
  ask WASAPI. What did transfer from B31 is visibility — a failed `af set` used to reach `Trace`
  and nowhere else; `AudioFilterUnavailable` now surfaces it as a toast, raised only when a filter
  was actually requested. Covered by `phase10-tests/test-audio-filters.ps1`, which asserts mpv's
  filter list rather than the command being sent (the latter passes even when mpv rejects the
  filter) and carries an off-state control leg. What that 2026-08-01 check did **not** cover is the
  filter's interaction with an audio output change — see B39 below, where dynaudnorm's 15.5 s of
  buffered audio sets the magnitude of an mpv bug without being the bug.
- **Audio-restart drift after an output change (B39)** — an output device change can leave audio
  **silent while the picture plays on normally**, for minutes. mpv's `reload_audio_output()`
  (`player/audio.c:765-798` at the shipped `mpv v0.41.0-744-g304426c39`) tears the AO down, resets
  `audio_status` to SYNCING and calls `mp_output_chain_reset_harder()`, which **destroys every frame
  buffered inside the audio filter chain** without touching the decoder or the demuxer. The only
  compensating rewind in that path (`player/audio.c:120-123`) is gated on
  `audio_status == STATUS_PLAYING`, so a reload arriving while a previous delayed start is still
  pending discards the buffered audio and issues **no refresh seek**: the decoder carries on from
  where it had read, the audio restart point jumps **forward** past the video clock, and mpv sits in
  `delaying audio start … diff=D` until the video clock catches up, the file ends, or something
  seeks — there is no bound (observed 77.8 s, then 149.9 s). Both triggers land in that one function:
  an in-app `audio-device` write (the option is `UPDATE_AUDIO`) and a Windows default-device change
  while on `auto` (`ao_wasapi_changenotify.c`'s `OnDefaultDeviceChanged` → `ao_request_reload`), and
  one physical switch can fire several reloads, which is how the gap ratchets. Upstream mpv issue
  **#16559** (open, `os:win`) is the same thing, with `frame-step`/`seek` as the reporter's
  workaround — i.e. the missing refresh seek. Four things here are load-bearing:
  - **`dynaudnorm` is the amplifier, not the defect, and it stays on.** It does not mangle PTS; the
    defect is mpv's `STATUS_PLAYING` gate. But it is the only thing putting destructible audio
    between decoder and AO: FFmpeg's defaults (`af_dynaudnorm.c`, `framelen=500` ms,
    `gausssize=31`, consumed and emitted in exact 500 ms chunks) hold ~15.5 s inside the filter, so
    after a reset the decoder must supply 31 fresh chunks plus ~1 to prime the AO — exactly
    32 × 0.5 = **16.000 s** of forward jump per uncompensated reload. The recorded audio-pts pair
    `101.513` / `181.513` is 80.000 s = 5 × 16.000 s apart, with `.513` as the 0.5 s quantization
    fingerprint. The A/B is **retrospective, from two independent captures rather than one
    controlled session**: normalization off
    (`%TEMP%\lightweaver-tests\audio-device-cycle-mpv.log`, 2026-08-09 12:30, `[af] User filter list:
    (empty)`) gave 151 `delaying audio start` lines with every diff between **0.001 s and 1.334 s**,
    all converging, over 12 real switches across 6 endpoints including 48k stereo → 48k mono → 96k
    stereo → 44.1k mono and a wasapi → openal driver change; normalization on gave the two
    minute-scale gaps. The ON-leg log has since rotated away, so its figures survive as a
    transcription only.
  - **Foot-gun: `audio-pts` is UNREADABLE in exactly the stuck state, so the absence of a reading is
    the signal.** `mp_property_audio_pts` (`player/command.c:871-880`) returns
    `M_PROPERTY_UNAVAILABLE` whenever `audio_status < STATUS_PLAYING` — which is precisely
    `delaying audio start`. A detector that compares live `audio-pts` against `time-pos` can
    therefore **never fire** (the 101.513/181.513 numbers came from `MP_VERBOSE` internals, not the
    property API). `MpvPlayer.IsAudioStuck` treats "no reading" as stuck and keeps the numeric
    divergence as a *secondary* check for when `audio-pts` does read. That inverts the usual
    reasoning, so the surrounding context is what makes it sound: an audio track is selected, a live
    `current-ao` exists, `time-pos` advanced ≥ 0.5 s since the arm, and the file is **not near its
    end** — `audio-pts` is unavailable at audio EOF too, so without the 10 s `time-remaining` guard
    every normal end of file would read as stuck and earn a pointless reload. 10 s is safe because
    the failure quantum is 16 s.
  - **The repair is the rewind mpv skipped, and it is the B31 sequence reused.** The passthrough
    recovery's `aid=no`/restore pair became `ReloadSelectedAudioTrack(trigger)` and serves both
    callers: it makes mpv re-run audio init and re-fill the filter from the current position, and
    unlike a seek it costs no video frame. Bounded by construction — once per passthrough fallback,
    once per load generation here.
  - **One repair, verified once, silent unless it fails; passthrough wins ties.**
    `ArmAudioSyncCheck` hangs off `MPV_EVENT_AUDIO_RECONFIG` (the event a reload necessarily
    produces, whichever trigger caused it) with a **3000 ms** debounce — measured, not cautious: the
    healthy population above tops out at 1.334 s and the failure quantum is 16 s, so 3 s clears every
    healthy resync with better than 2× margin and still repairs long before the video clock could
    catch up. The check is generation-guarded at both the arm and the verify, does **exactly one**
    reload per load, re-checks once, and raises `AudioResyncFailed` (one toast, no device name) only
    when that one-shot repair failed. Success says nothing, because "nothing to do" is the normal
    outcome of every reconfig. While a B31/B32 passthrough recovery is live the watchdog stands down
    entirely — that recovery rebuilds the chain itself, and `ApplyAudioOptions` makes passthrough and
    normalization mutually exclusive, so the amplifier is not even present in that configuration.
    Traces to `LIGHTWEAVER_UI_LOG` as `AudioSyncWatchdog arm/eval/skip/stale/verify`, the same
    precedent as `AoWatchdog`.
  **Verification status, stated plainly: this is `-warnaserror` builds (Debug and Release, 0 warnings
  / 0 errors) plus static review, and nothing else.** It has never been exercised against a real
  audio endpoint. `bug-tests/test-audio-sync-after-device-change.ps1` is written and **has never been
  run**: it needs a real exclusive-mode WASAPI open on the user's desktop and the user's go-ahead, and
  it cannot run on the test machine, which has no real audio endpoint. Treat the behaviour above as
  intended-and-built, not as measured. Also worth keeping: the **first** diagnosis of this bug was
  that `current-ao` goes null, a null-AO watchdog was planned on that basis, and it was **disproven**
  — every reload ended in a valid WASAPI AO with no error and no underrun. "AO ready" and "video clock
  advancing" were both true while audio was silent, which is why neither could be the oracle.
- **A playback restart that never completes (B41)** — the third watchdog, on the one state the
  other two exclude by construction. B31/B32 covers "no audio output at all"; B39 covers "video
  keeps advancing while audio does not", and therefore stands down on a frozen clock. B41 is the
  frozen clock **with** an output present: mpv shows the first frame after a seek and then never
  finishes the restart, because audio never reaches `STATUS_PLAYING`. The user's capture shows it
  as `first video frame after restart shown` with no `audio ready`, no `playback restart complete`
  and no `starting audio playback` for the following 26 s, against 60–220 ms for every healthy
  restart in the same log.
  - **Arming** (`ArmPlaybackStallCheck`): `MPV_EVENT_SEEK`, a `SelectSubtitleTrack` write — whose
    demuxer refresh seek is internal to mpv and raises no event of its own, which is why nothing
    else would arm it — and leaving pause, which has no completion event to wait for at all.
    Arming is a timer restart, so it stays cheap and unconditional; the state is read at the eval.
  - **The oracle is the clock, not the event count** (`EvaluatePlaybackStall`, 5 s later): the
    position has not moved, and none of paused, `paused-for-cache`, loading, no-audio-track,
    no-`current-ao` or within 10 s of the end applies. `core-idle` and `audio-pts` are read and
    written down but do **not** gate the repair — the state is already established, and recording
    them is how the next report arrives with the two facts this one had to infer.
  - **The repair order comes from the capture, not from taste.** The user's seeks are the thing
    that demonstrably did not work — three in a row re-entered the state — so stage 1 is
    `ReloadSelectedAudioTrack`, the B31/B39 chain rebuild, which is the untried move. Stage 2 is
    `seek 0 exact`, kept only because one seek did end the first incident. Then one toast
    (`PlaybackStalled`), and `_playbackStallGaveUp` latches for the load.
  - Traces to `LIGHTWEAVER_UI_LOG` as `PlaybackStallWatchdog arm/eval/verify/stale`, and writes
    `event=playback_stall` records to `app.log` under verbose.
  **Verification status, stated plainly:** `-warnaserror` Debug and Release builds (0/0) plus
  `bug-tests/test-b41-playback-stall.ps1`, which proves the *state machine* — arming on all three
  triggers, silence on healthy playback, silence on a paused player, the stage-1 recovery, the
  stage-2 escalation and exactly one toast — against a genuinely playing player with only the clock
  *reading* faked (`LIGHTWEAVER_TEST_STALL_FREEZE`, Debug only). **The stall itself is not
  reproduced and cannot be**: it is internal to mpv, and nothing outside mpv induces it on demand.
  So whether the repair cures it is unproven, and stays unproven until the stall recurs on a build
  carrying the records above. The mpv-internal cause is also unestablished — `dynaudnorm` is present
  as in B39, and every stalled seek in the capture targeted a position before the demuxer's cached
  range, but so did the one seek that recovered, so that is correlation and not mechanism.
- **`Player/AudioPassthroughProbe.cs`** — asks WASAPI which bitstream formats an endpoint accepts,
  via `IAudioClient::IsFormatSupported` in exclusive mode. That call is a pure query: it neither
  opens nor takes the device, so it is safe to run while something plays, and it is the same
  question mpv's own output asks — which is why its answer matches what playback will do. The
  Settings passthrough row shows the result, because before it the toggle read as active on a
  device that could never honour it. **It gates itself on a PCM control through the identical
  call**: on a machine whose outputs are all analog or virtual every answer is negative, so a
  wrong format blob would be indistinguishable from an honest "no" — if the control fails the
  probe returns `Unknown` and the row stays collapsed. The one exception is itself a verdict:
  an endpoint that refuses exclusive mode even for PCM cannot bitstream, since passthrough is
  only ever attempted in exclusive mode. Caveat worth keeping: no output on the development
  machine accepts any bitstream format, so the **positive** direction is unvalidated there.
- **The volume indicator (P10 M10)** — `VolumePill` in `OverlayWindow.xaml`, a transient
  top-right readout with its own `_volumePillTimer` (1200 ms hold, restarted per change, then a
  300 ms fade), deliberately outside the idle-fade group so the two lifecycles do not touch:
  a volume change no longer wakes the bar, and the bar's idle fade cannot take the pill away.
  Three things in it are decisions rather than defaults:
  - **It listens to `PlayerViewModel.VolumeFeedback`, not `PropertyChanged`.** mpv reports
    `volume` *and* `mute` when the property observers are registered, so a `PropertyChanged`
    subscriber would flash the pill at the start of every file. `VolumeFeedback` fires only on
    user intent, and it needs just two raise sites because the `Volume` **setter is already the
    single choke point** for all four triggers (Up/Down, wheel, the OSD slider's two-way
    binding) — mpv's own `VolumeChanged` writes the backing field directly instead. Mute is the
    second site, and it is *pending-flagged*: `ToggleMute` sets `_muteFeedbackPending` and the
    `MuteChanged` handler consumes it, because mute round-trips through mpv and the state is
    only correct to display once mpv reports it back.
  - **The glyph carries mute; the text always carries the level.** mpv's mute is independent of
    the level, so "Up" while muted really does move the volume — a pill reading only `Muted`
    would repeat itself while the level climbed behind it. Crossed speaker + `65%` says both.
  - **The offset is fixed (top 64), not stacked under `GemPillPanel`.** Those RTX toggles are
    hidden on a non-RTX GPU and on PQ/HLG sources, so a stacked pill would sit somewhere
    different depending on the GPU and the source. 64 clears the gem row (top 28 + height 24);
    the suite measures the gap rather than assuming it.
  It is also the **one piece of OSD furniture that survives mini mode** (`UpdateVolumePillScale`
  shrinks it instead of `SetMiniMode` collapsing it, as it does for the title, the gem pills and
  the back button): the wheel still changes the volume at 480×270 and, with the bar no longer
  waking, nothing else there would say so. Verified by
  `phase10-tests/test-volume-pill.ps1`.
- **`Key.ToString()` is not symmetric across the enum's aliases** — WPF returns whichever
  name was declared first for a value, so `Key.OemOpenBrackets` → `"OemOpenBrackets"` but
  `Key.OemCloseBrackets` → **`"Oem6"`**. The shipped default for NextChapter is spelled
  `"OemCloseBrackets"`, so the string comparison in `PlayerActionDispatcher.TryResolve` never
  matched a real `]` press: `[` worked, `]` had **never** worked. `ChordOf` now canonicalizes
  the aliased Oem names, and `ChordEquals` compares by parsed `Key` so chords already saved in
  the alias spelling keep working. Found by the M1 every-shortcut sweep.

## Phase 3 features

- **Track selection:** mpv's `track-list` is observed as a JSON string property (no
  `mpv_node` marshalling needed) and parsed with System.Text.Json; selection via the
  `aid`/`sid` string properties (`sid=no` disables subs). Overlay popups list tracks with
  the selected one checked; `B`/`V` cycle. Preferred audio/subtitle language auto-applies
  once per file when the first track list arrives. **These preferences are local to the
  client by decision (user, 2026-09-08):** they do not read the Jellyfin profile's
  `UserDto.Configuration` (AudioLanguagePreference, SubtitleMode, Remember*Selections) and
  the app does not report chosen stream indexes back for the server's "remember
  selections" — the same account may behave differently here than in the web client, and
  that is intended. The preference is picked from a
  curated dropdown in Settings (`Player/Languages.cs`, "None" = no preference) and stored
  as an ISO 639-2/T code; matching is *equivalence-based*, not a prefix compare —
  `Languages.Matches` canonicalizes both sides through `LanguageBadge` so `deu` matches
  containers tagged `ger`/`de` (Matroska commonly stores bibliographic codes). Legacy
  hand-typed codes select their equivalent dropdown row; a code the list doesn't know is
  kept as an extra "(saved)" row rather than being silently rewritten.
- **Chapters:** server metadata (`BaseItemDto.Chapters`, ticks) is authoritative for
  server items; mpv's observed `chapter-list` is the fallback for local files. Markers
  are a hit-test-invisible Canvas overlaid on the seek slider (no Slider retemplating);
  `[`/`]` and overlay buttons navigate with a 3 s restart-current rule. Because list
  queries omit chapters, playback metadata re-fetches the full item (a UI-copy race
  otherwise silently dropped chapters/segments depending on click timing).
- **Skip intro/credits:** triple fallback — native `/MediaSegments/{itemId}` (requires a
  segment-provider plugin server-side; returns empty on stock servers), the IntroSkipper
  plugin's `/Episode/{id}/IntroSkipperSegments` (seconds-based, episodes only), then
  chapter-name heuristics ("Intro"/"Introduction"/"Credits"/"End Credits"). Floating
  overlay button during the segment (visible even with controls hidden), `S` key, and
  per-type auto-skip (fires once per segment per playback so seeking back only re-shows
  the button). All errors collapse to "no segments".
- **Settings:** `%LOCALAPPDATA%\LightWeaver\settings.json` (defensive load, atomic save,
  save-on-change). hwdec + `sub-scale` applied at mpv init; RTX defaults via the existing
  filter path with `format=max-luma=<nits>` appended when HDR is on. The settings window
  (Ctrl+,) live-applies sub-scale/hwdec/RTX to a running player.
  **Failure policy — one for the whole store (2026-08-09).** Every caller is a UI event
  handler or a field initializer (`SettingsView`'s save-on-change paths,
  `PlayerViewModel.SelectAudioDevice`, `Migrate`), and none of them can do anything useful with
  an exception, so
  **`Save` never throws**: it catches `IOException`/`UnauthorizedAccessException`, logs, and
  returns `false`. A save that silently does not happen is worse than one that does and far
  better than losing the session.
  - **`UnauthorizedAccessException` is not an `IOException`,** and `Load`'s read caught only
    the latter until 2026-08-09. `File.ReadAllText` raises it when the file's ACL denies read
    — and `Load` runs from `MainWindow`'s **field initializer**, so it escaped as a crash during
    construction: no window, no message, in a method whose own summary says "defensive load".
    Measured: the pre-fix build exits `-532462766` (unhandled managed exception) with the ACL
    denied. (The API raises the same type for a *directory* path, but that cannot reach the read:
    `File.Exists` returns false for a directory, so it is handled as file-missing.)
  - **A permission failure and a corrupt file are handled differently, on purpose.** Both
    fall back to defaults, but a read denied by *permissions* also latches
    `_readDeniedByPermissions`, and `Save` then refuses to overwrite the file at all —
    writing defaults over settings we were never allowed to read would turn a permissions
    problem into permanent data loss the user could not see. A *corrupt* file is deliberately
    still overwritten, because that is the only way back to a working config.
  - Guarded by `bug-tests/test-settings-acl-denied.ps1`, which applies a real `icacls /deny`
    to the Debug profile's settings.json (nothing else can produce a genuine
    `UnauthorizedAccessException`) and restores it in `finally`. It fails four ways on the
    pre-fix build. **The save-refusal half has to be DRIVEN, and the first version of the suite
    did not drive it** — with the file unreadable, `Load` returns defaults already stamped
    current, so `Migrate` never runs and nothing else writes the file unless the user acts. "The
    file was not overwritten" was therefore true on every build, including one with the guard
    deleted. The suite now clicks Settings → shortcuts *Reset to defaults* (an unconditional
    `Save` with no dialog) and asserts the refusal reaches the log.
  **Schema version + migrations (2026-08-07).** `AppSettings.SettingsVersion` (current: 1)
  exists because `Save` serializes *every* property, so a saved file cannot distinguish
  "the user chose this value" from "this was the default at the time" — which makes changing
  any shipped default unsafe on its own. The marker's default is **0, deliberately**: a file
  written before the property existed lacks it, deserializes to 0, and is thereby known to
  be unmigrated. Do not "tidy" that to the current version — it silently disables every
  migration. `SettingsStore.Load` runs `Migrate` when the loaded version is behind, then
  stamps and saves; the no-file and corrupt-file paths return settings already stamped
  current so a migration can never fire against a fresh default set. **v1** moved
  `SkipForwardSeconds` from 30 to 10, and only where it still read 30, so a deliberate 45
  survives. That guard now lives in `Save` itself rather than around the migration's call to
  it — see the store's failure policy above; on failure the migrated values are kept for the
  session and the marker is left unstamped, so the idempotent migration simply retries next
  launch. Two blind spots are
  accepted rather than papered over: someone who deliberately typed 30 on an older build is
  indistinguishable from the default and moves to 10 once, and downgrade-then-upgrade re-runs
  the migration because an older build drops the unknown key on save.
  `UpdatePolicy` is an additive property, not a changed historical default, so old files
  deserialize to the safe `ManualInstall` initializer without a schema-version bump.
- **Packaging:** `publish.ps1` → self-contained win-x64 publish + libmpv bundle check →
  versioned zip under `dist/`. Verified to run from a clean extraction.
  `installer.ps1` → `installer/LightWeaver.iss` (Inno Setup 6) → per-user setup.exe
  (`PrivilegesRequired=lowest`, installs to `%LOCALAPPDATA%\Programs\LightWeaver`,
  Start menu entry, optional desktop icon). A stable `AppId` GUID makes every future
  setup an in-place update; `[InstallDelete]` clears `{app}` first so renamed/removed
  assemblies never linger. App data is never touched by install/update/uninstall.
- **Authenticode signing (2026-08-06):** `sign.ps1` is a stage of both scripts —
  `publish.ps1` signs `LightWeaver.exe` + `LightWeaver.dll` **before** the zip and before ISCC
  reads the folder (signing later would leave an unsigned exe inside a signed installer, an
  arrangement that looks fine and helps nobody), and `installer.ps1` signs the setup exe
  **after** ISCC. Configured entirely through the environment; **unconfigured it is a no-op**, so
  a release still builds on a machine with no certificate — with `LIGHTWEAVER_SIGN_REQUIRE=1` that
  becomes a hard failure instead, which is what a real release wants. Always timestamped, and a
  missing timestamp is a hard error: an un-timestamped signature stops verifying the day the
  certificate expires. Only our own two binaries are signed — re-signing a third party's
  `libmpv-2.dll` would assert authorship we do not have. Full setup and the certificate options in
  [docs/CODE-SIGNING.md](docs/CODE-SIGNING.md). Two PowerShell 5.1 traps are recorded there, both
  found by measurement and both silent: `$ErrorActionPreference='Stop'` makes a native exe's
  **stderr** a terminating error, so signtool merely *printing* a failure aborted the script before
  its exit-code handling ran (and whether it did depended on how the *caller* redirected output);
  and a native command's **stdout joins its enclosing function's return value**, so the exit code
  came back as an array and a successful sign reported "failed with exit code &lt;transcript&gt; 0".
  Known gap: Inno's embedded `unins000.exe` is unsigned, since signing setup.exe afterwards cannot
  reach it.
- **Rename migration (LightWeever → LightWeaver):** `App.OnStartup` moves
  `%LOCALAPPDATA%\LightWeever` → `LightWeaver` before anything reads settings —
  whole-folder move normally; when both exist it gap-fills without overwriting and
  retires the legacy folder. Credentials (DPAPI is user-scoped, path-independent),
  settings, window state, and the image cache all survive the update.
- **Debug environment (isolated app-data):** every app-data path resolves through `AppPaths.Root`.
  Debug builds (`#if DEBUG`) use `%LOCALAPPDATA%\LightWeaver-Debug` and title the window
  "LightWeaver (Debug)"; release builds use `%LOCALAPPDATA%\LightWeaver`. This keeps the dev build
  — and the verify suite, which always drives the Debug exe — from touching the installed build's
  credentials/settings/watched state; the legacy-folder migration is release-only. The verify
  `driver.ps1` seeds the debug login by copying `credentials.dat` from the release folder and
  resolves the window by the `LightWeaver*` prefix.
  The title marker is appended in `MainWindow`'s **constructor**, immediately after
  `InitializeComponent`, onto the static base title from `MainWindow.xaml` — deliberately not
  from a deferred dispatcher callback. B29: it used to be queued from `App.OnStartup` at
  `DispatcherPriority.ApplicationIdle`, which ran and assigned correctly but only ~330 ms after
  the HWND existed (measured 326/338/366 ms), leaving the window mistitled for its first third
  of a second — and `ApplicationIdle`, the lowest foreground priority, is starvable by
  render/animation work, so that delay had no upper bound. Setting it in the ctor makes the
  title correct before the HWND is created. Because `driver.ps1` matches a *prefix* (one driver
  must serve both builds), the **exact**-name assertion lives in
  `bug-tests/test-b29-debug-title.ps1`.

## Known-issues round (post-Phase 3)

- **Playback-error toast:** mpv end-file (reason `error`) raises
  `MpvPlayer.PlaybackFailed` (UI thread) with mpv's error string; `MainWindow` leaves
  playback and shows a dismissible auto-hiding toast in the browse layer — it can't live
  over the video because the mpv HWND covers WPF content. UIA gotcha: WPF `Border` has
  no automation peer, so the toast's `AutomationId` sits on the `TextBlock`.
- **OSD now-playing title:** `PlayerViewModel.SetNowPlaying(title, subtitle, release)` fed from
  `MediaItem.PlaybackTitle/PlaybackSubtitle/PlaybackRelease` (episodes: series / "S1E1 · name";
  movies: name / year; local files: file name). `TitlePanel` sits next to the overlay Back
  button and joins the controls' fade animations.
- **Release-date badge (2026-08-05).** A film already showed its *year* as the OSD subtitle; an
  episode showed no date at all, and `ProductionYear` could never have supplied one, because every
  episode of a series shares a single production year. `MediaItem` gained `PremiereDate` (mapped
  from the DTO — the server returns it on ordinary list queries, no extra `Fields` needed), and
  `BadgeRelease` renders it as a pill in the existing `StatusBadgePanel` beside the HDR and
  resolution pills. A badge rather than more subtitle text: for an episode that line is already
  "S1E1 · Episode name" under `CharacterEllipsis` in a width-constrained panel, so a date appended
  there would push the episode name out. Living in that panel also inherits the idle fade and the
  mini-mode collapse for free. Hidden when the server has no premiere date — no fallback to the
  year, which for a film merely repeats the subtitle less precisely.

  The item-detail metadata row uses the same `PlaybackRelease` value before falling back to
  `ProductionYear`, so movies and episodes show the exact release/air date outside playback too.

  It formats as a long date in **local** time (`d MMMM yyyy`). Long, because this is read at a
  glance and "12 March 2024" cannot be misread the way 12/03/2024 can. Local, because the server
  stores air dates as an *instant* — commonly the broadcaster's local midnight expressed in UTC,
  so values like `2022-09-07T22:00Z` are normal — and the date of an instant only means anything
  in some timezone, of which the viewer's is the only one known. The consequence, worth knowing
  before someone "fixes" it: the badge can read one day off a listing site quoting the
  broadcaster's zone. Films are unaffected; they carry midnight UTC.

  `backlog-tests/test-osd-release-badge.ps1` asserts the badge against the server's own
  `PremiereDate`, formatted identically, for a film and an episode — presence alone would pass on
  a build showing the wrong item's date, the production year, or a timezone-shifted value.
- **Track-list reset:** `MpvPlayer.LoadFile` empties `Tracks` and raises
  `TracksChanged` before issuing `loadfile`, so the overlay popups never show the
  previous file's rows while the new file enumerates (they still live-update as tracks
  appear — that part is inherent to mpv's incremental `track-list`).
- **Landscape episode cards:** `MediaCardTemplateSelector` (poster vs 16:9 episode
  template) wired into the home-row style and the library grid; episodes get
  `ThumbUrl` = their Primary still at `maxWidth=480`, falling back in order to the parent's
  real `/Images/Thumb`, the series Primary, and the episode Backdrop. The image type in the
  fallback URL must match `ParentThumbImageTag`; asking `/Images/Primary` with that tag can
  return portrait art or fail.
- **RTX availability:** the green-frame guard's `hdrContent` (gamma `pq`/`hlg`) is
  exposed as `MpvPlayer.RtxUnavailable` + `RtxAvailabilityChanged` (posted to the UI
  thread from the gamma re-evaluation, which runs on the event thread). The overlay
  toggles bind `IsEnabled` to the projected `RtxAvailable` and swap their tooltip to an
  explanation while disabled (`ToolTipService.ShowOnDisabled`). `IsChecked` remains the
  user's preference — it is never rewritten by suppression.

## Phase 4 — Experience

- **Window-state persistence:** `WindowStateStore` →
  `%LOCALAPPDATA%\LightWeaver\window-state.json` (machine state, deliberately separate
  from settings.json). Saved in `OnClosing` (normal bounds via `RestoreBounds` when
  maximized/minimized; closing from fullscreen substitutes the pre-fullscreen state),
  restored in `OnSourceInitialized`. Restore ignores positions whose intersection with
  the current virtual screen is under ~120×80 DIP — monitor layouts change.
- **Jump forward/back:** `MpvPlayer.SeekRelative` (`seek <s> relative`, keyframe-fast,
  mpv clamps at the edges); overlay ⏪/⏩ buttons next to the chapter buttons and
  Right/Left arrows in `MainWindow.OnKeyDown` (Playing state only). Sizes come from
  `SkipForwardSeconds`/`SkipBackwardSeconds` (Settings → Behavior), read per keypress
  so settings edits apply immediately. **Both default to 10 s since 2026-08-07** (forward
  was 30 s before — changing a shipped default needed a migration; see the settings-schema
  note below).
- **Large seek (2026-08-07):** `Ctrl+Right`/`Ctrl+Left` seek by `LargeSkipSeconds`
  (default 30, Settings → Behavior), via `PlayerAction.SeekForwardLarge`/`SeekBackLarge`
  and `PlayerViewModel.JumpForwardLarge`/`JumpBackLarge`. One value for both directions,
  unlike the asymmetric plain pair: a coarse step exists to cover ground, and an
  asymmetric one is harder to undo than it is useful. Deliberately **no OSD buttons** —
  the overlay pair stays on the plain jump. These are the first shipped defaults in the
  app that are a chord rather than a bare key, and nothing had to change to support that:
  `ChordOf` already canonicalises modifiers, `DisplayChord` already spells them, and the
  Settings rebind rows appear on their own because `BuildShortcutRows` iterates
  `PlayerAction`. Both are in `RepeatsWhileHeld` — the same continuous-seek class as the
  plain pair.
  **Testing trap:** a 30 s seek cannot be measured on the 30 s `multitrack.mkv` fixture —
  it clamps to EOF and keyframe-snaps to ~25 s, so both forward seeks land on the same
  ceiling and a magnitude assertion passes on the clamp distance instead.
  `phase9-tests\test-all-shortcuts.ps1` therefore asserts routing only; exact magnitude
  lives in `phase4-tests\test-m1-jump.ps1`, which uses the long fixture and pauses first.
- **Frame stepping (2026-08-07):** `.` and `,` step one frame, via
  `MpvPlayer.FrameStep`/`FrameBackStep` over mpv's `frame-step`/`frame-back-step` and
  `PlayerAction.FrameStepForward`/`FrameStepBack`. **No OSD buttons**, by request.
  `RepeatsWhileHeld` is **deliberately asymmetric**: forward repeats, back does not. mpv
  implements `frame-back-step` as a hi-res backward seek plus a re-decode — on default
  keyint that is a keyframe-to-target decode per repeat — so auto-repeat would stall the
  player, the same failure shape as B9's held `N`. Forward is one cheap decode per step, and
  `LibMpv.Command` is synchronous, so a held key gets natural backpressure rather than
  queueing. (Note the app is therefore *stricter* than mpv, whose own input.conf allows
  repeat on both.)
  **Frame stepping pauses mpv as a side effect, and nothing mirrors that by hand.** `pause`
  is an observed property whose handler writes `MpvPlayer._pause` and raises `PauseChanged`
  in one dispatched lambda, so `PlayerViewModel.IsPaused`, the OSD glyph, SMTC, the taskbar
  thumb icon, the Up Next countdown and the server progress report all converge on their
  own — each reads observed state rather than assuming playback continues.
- **Rebindable fullscreen (2026-08-07):** `PlayerAction.ToggleFullscreen`, default `F11`, so
  fullscreen is editable in Settings → Shortcuts like every other player key. It follows the
  **`Space`/`TogglePause` dual-registration precedent**: the function is registered twice —
  the unconditional chrome `F11` `InputBinding` *and* the rebindable action — and
  `ChromeShortcut.DuplicateOf` makes `BuildRows` print one panel row *while the two are the
  same keystroke*. Rebind it to `F` and both rows appear, which is honest: F11 genuinely
  still works, because the chrome binding is unconditional. `AppShortcuts.GroupOf` needs an
  explicit `ToggleFullscreen => ShortcutGroup.General` case, or the surviving row silently
  migrates to Playback (the rebindable default group) the moment it renders instead of the
  chrome one.
  **How it reaches `MainWindow`:** `PlayerActionDispatcher.Dispatch` only receives the
  `PlayerViewModel`, but `ToggleFullscreen()` is a window method — so the VM raises
  `FullscreenRequested` and `MainWindow` subscribes, matching the existing
  `QueuePlayRequested`/`EpisodeStepRequested`/`SubtitleBasePositionChanged` bridges. A
  special case inside `HandlePlayerKey` was rejected because it would leave `Dispatch`
  silently no-opping one enum member — a trap for the next caller (SMTC, an OSD button).
  **Inherited wart, not a new one:** binding some *other* action to F11 steals it and unbinds
  `ToggleFullscreen`, after which F11 resolves differently depending on which window has
  focus (main window: chrome wins; overlay focused: `HandleOverlayKey` runs
  `HandlePlayerKey` first, so the stolen action wins). `Space` has behaved this way since it
  gained the same dual registration.
- **Click the OSD time to set the position (2026-08-07):** clicking `TimeText` swaps it for a
  collapsed `TimeEditBox` prefilled with the *elapsed* figure; `ss` / `mm:ss` / `hh:mm:ss`
  (minutes may exceed 59), Enter seeks through the slider's own `PlayerViewModel.Seek`, Esc
  cancels, unparseable input reverts silently with **no** seek, out-of-range clamps to
  `DurationSeconds`. Excluded in mini mode, **and the affordance is suppressed with it** — no
  I-beam, no hover underline — following `ToggleShortcuts`'s "a 480x270 window cannot show it".
  **The resting display is load-bearing and must not change shape.** `driver.ps1`'s
  `Read-TimeDisplay` finds the time by the regex `^\d+:\d+ / \d+:\d+$` and several suites parse
  it, so `TimeText` stays one `TextBlock` with one unchanged binding — the editor *replaces* it
  rather than splitting it.
  Three details that are easy to get wrong here:
  - `TimeText` needs a near-transparent `Background` (`#01000000`). A background-less
    `TextBlock` hit-tests **glyph pixels only**, so presses between digits would fall through
    to the video. `MiniResizeGrip` documents the same trap.
  - `EndTimeEdit` must `Keyboard.Focus(this)` **before** collapsing the box. A collapsed element
    keeps WPF keyboard focus, so collapsing a focused `TextBox` would leave
    `IsKeyboardFocusWithin` true and **kill every player shortcut for the rest of playback**.
  - The overlay forwards every `PreviewKeyDown` to `HandlePlayerKey`, so while the editor holds
    the keyboard it must handle Enter/Escape locally and forward nothing else, or arrows seek
    and Space pauses while you type. The guard is scoped to `TimeEditBox` specifically — a
    broader `TextBoxBase` test would kill shortcuts whenever any overlay text box had focus.
  A `_timeEditActive` flag joins the `HideControls` guard beside `AnyFlyoutOpen` and the idle
  timer is stopped and restarted around an edit, so the OSD cannot fade out from under the
  field. `Deactivated` cancels the edit alongside `AbandonSeekGesture` (the B8 precedent), so
  Alt+Tab mid-edit cannot leave the flag pinning the OSD awake forever.
- **External subtitles:** `JellyfinService.GetExternalSubtitlesAsync` — GET
  `/Items/{id}/PlaybackInfo?userId=` → default media source's `MediaStreams` filtered to
  `Type == Subtitle && IsExternal`. `DeliveryUrl` wins when present (relative →
  prefixed with ServerUrl), else the `/Videos/{itemId}/{mediaSourceId}/Subtitles/{index}
  /Stream.vtt` fallback; mpv authenticates via the already-set `http-header-fields`.
  `MpvPlayer.AddExternalSubtitle` issues `sub-add <url> auto <title> <lang>` (trailing
  args must be omitted rather than passed empty). Ordering: the PlaybackInfo fetch and
  mpv's `FileLoaded` race — whichever finishes second triggers the adds (`sub-add`
  before `loadfile` would be dropped). Tracks surface in the Subs popup through the
  existing track-list observation; no popup changes were needed.
- **Search:** `SearchAsync` = `/Items` with `SearchTerm` + `Recursive=true` (required
  for the term to filter at all) + `IncludeItemTypes=[Movie, Series, Episode]`,
  limit 40. The browse header is now always visible in Browse state (it hosts the
  search box; the back button still collapses at the root). Queries are debounced
  300 ms with a min length of 2; results land in a `SearchResultsView` pushed onto
  the browse stack once and then refined in place (top-of-stack identity check), so
  typing never stacks views. A stale-query guard drops responses that no longer match
  the box. Esc clears the box and pops the results view.
- **Watched & favorite badges:** `UserData.Played` → `MediaItem.Played`,
  `UserData.IsFavorite` → `MediaItem.IsFavorite`; both card templates show a top-right
  emerald checkmark (watched) and a top-left garnet heart (favorite) over the artwork,
  next to the existing progress bar. The detail view's toggle calls
  `UserPlayedItems[itemId].PostAsync` (with `DatePlayed`) / `.DeleteAsync` (favorite:
  `UserFavoriteItems`), passing `UserId` explicitly (servers can reject the inferred
  user on these endpoints), then re-fetches the item so the button label always shows
  server truth.
- **Badge freshness on back-navigation:** `MediaItem` is immutable and non-observable,
  and `LibraryView` is a cached browse-stack instance that only loads once
  (`_items.Count == 0` guard), so a watched/favorite toggle on the detail view above it
  would otherwise leave the underlying card stale until the list was rebuilt two levels
  up. Fix: after a successful toggle the detail view calls
  `AppViewModel.NotifyItemUserDataChanged(fresh)`; `MainWindow` (subscribed once,
  app-lifetime — no per-view leak) walks the browse stack and calls
  `LibraryView.ApplyUserDataUpdate(fresh)`, which replaces the matching item in the
  `ObservableCollection` so the card rebinds. No refetch, no scroll/paging reset.
  Since M10 the fan-out also updates the live `HomeView` and `ItemDetailView`. Home removes a
  completed item from Continue Watching and Recently Added; Next Up and Favorites remove a
  completed episode and coalesce a profile-bound lookup for the series successor. The pending
  operation is versioned, so another completion while the lookup is in flight forces a refetch
  instead of reinserting the newly watched episode. Every state-bearing row uses an
  `ObservableCollection`, so badge-only updates still rebind in place.
- **Home on-reveal refresh:** `HomeView` is created once and re-shown (never re-`Loaded`) on return from the
  player or a library/detail view, so it now re-fetches the three watching-driven rows
  (Continue Watching / Next Up / Recently Added) on `IsVisibleChanged`, exactly like
  `ItemDetailView`: it awaits `AppViewModel.PendingStopReport` (capped 10 s) first so the GET
  can't outrun the fire-and-forget stop POST. `_loadedOnce` (set in `LoadAsync`'s `finally`)
  gates the handler so the initial reveal doesn't race the first load; `_refreshing` guards
  rapid reveal flips. No skeletons on refresh and the page keeps its scroll offset (`FillRow`
  only swaps row items). Favorites/Libraries are left as-is; direct user-data fan-out handles
  the watched/favorite action immediately.
- **Home section customization (Phase 7 M11):** rail order + on/off live in
  `Settings/HomeLayoutStore` — `home-layout.json` is a dictionary keyed
  `{UserId:N}@{ServerUrl}` so layouts are genuinely per-profile (the global
  settings.json stays player-only). Load reconciles like Velly's HomeSectionsStore:
  duplicates/unknown ids dropped, missing sections appended enabled. `HomeView` keeps
  its five named section/row pairs (stable AutomationIds) inside a `SectionsHost`
  whose children are reordered per layout; disabled rails are skipped at fetch time
  (Latest still fetches library ids when the Libraries rail is off — it needs them).
  The customize flyout is a code-built `Popup` (save-on-change; toggling a rail on
  fetches just that rail). **Foot-guns:** a `Popup` inherits font properties through
  its `PlacementTarget` — anchored to an icon-font button, plain text ligates into
  glyphs ("home" → 🏠) unless the font is set explicitly; and `Border`/`StackPanel`
  have no automation peer, so the popup's UIA marker sits on its header TextBlock.
- **Drag-to-reorder in that flyout (2026-08-05).** The up/down arrows stay — they are the keyboard
  route and the one that still works at the 700×480 minimum — and a six-dot grip per row is a
  second input method beside them. Three things here were arrived at by measurement, each after a
  version that looked correct and did nothing:
  - **It does not use `DragDrop.DoDragDrop`.** An OLE drag inside a `Popup` does not work here:
    the drag loop takes mouse capture, the `StaysOpen="false"` popup reads that as a click
    elsewhere and dismisses itself, and the rows the drop needed are gone before it lands. The
    gesture completed and `home-layout.json` was never written — twice, the second time with the
    popup explicitly pinned open. It uses plain `Mouse.Capture` on the grip with manual
    hit-testing instead, which keeps every event on the handle, works inside a popup, and is
    drivable by synthetic input.
  - **The grip is a `Button`, not a `Border`.** A `Border` has no automation peer, so the id first
    rode a transparent `FontSize="1"` TextBlock over the dots — and that 6×1 px marker was found
    by UIA on one run and absent on the next, with a rect that aimed the synthetic press at the
    wrong row entirely. A `Button` brings its own peer with the handle's real rect, and is
    keyboard-focusable for free.
  - **A drag inserts; an arrow swaps.** `MoveSectionTo` removes and re-inserts, decrementing the
    insertion index when it is past the source, because dragging row 1 onto row 4 means "put it
    there" — a swap would fling row 4 up to position 1. Correct for a single-step arrow, wrong for
    a drag across several rows, so `MoveSection` (swap) and `MoveSectionTo` (insert) both exist on
    purpose, and the suite's assertion distinguishes them rather than merely proving that
    something moved.

  The insertion line is drawn on the target row's own `BorderThickness` from the same
  `InsertIndexAt` call the commit uses, so feedback and outcome cannot disagree, and no adorner
  layer is needed (a `Popup` does not share the main window's). For whoever next writes a
  synthetic drag here: move the cursor with `SetCursorPos` — a `SendInput` absolute-coordinate
  version put it at (6593,1577) when asked for (7784,998), so the press landed on the grip and the
  gesture then never moved, which reads exactly like a broken feature.
- **Card hover actions (Phase 7 M10):** the shared poster/episode templates carry a centered
  `CardHoverPlay` button for playable items and a bottom-right action bar
  (`LwCardActionBar`/`LwCardActionButton`, scrim + hairline)
  revealed on card hover or keyboard focus — hidden it is `Opacity=0` **and**
  `IsHitTestVisible=False` so it never swallows the card's click. Cards are
  `DataTemplate`s with no code-behind, so the buttons raise static `RoutedUICommand`s
  (`Views/CardCommands.cs`: PlayItem / ToggleWatched / ShowMediaInfo / CopyStreamUrl /
  OpenCardMenu) with the `MediaItem` as parameter; `MainWindow` registers the
  `CommandBinding`s once. **Foot-gun:** the action buttons are `Focusable=False` —
  keyboard focus entering a `ListBoxItem`'s subtree makes the `Selector` select the
  item, and row selection is the cards' navigate signal, so a focusable button both
  ran its action *and* opened the detail view (seen live). Copy stream URL uses
  `GetShareableStreamUrl` (direct-play URL + `&ApiKey=` — external players can't send
  the auth header; the token is embedded, handle accordingly. The parameter was `api_key`
  until 2026-09-08: Jellyfin 12 disables legacy authorization by default and the lower-case
  spelling is one of the legacy forms, so it 401s on a 12.0 server). Media info is an interim
  non-modal popup (owned window, click-away/Esc closes) of the overlay info-panel rows;
  it upgrades to the styled modal with M23.
- **Episode detail poster:** an episode's own Primary image is a landscape still, which
  looks wrong in the detail view's 2:3 poster frame, so `ItemDetailView` sources the
  poster from the episode's **season** (`GetPrimaryImageUrl(SeasonId ?? ParentId, null)`),
  falling back to the episode's own poster when there is no season (Specials/orphans).
  Scoped to the detail view only — the Home Continue-Watching / Next-Up cards keep the
  episode still.
- **Auto-play next episode:** `GetNextEpisodeAsync` uses `Shows[seriesId].Episodes`
  with `AdjacentTo` (returns neighbors; the item after the current id is the next
  episode), fetched in the playback-metadata chain for every episode. The card state machine
  lives in `PlayerViewModel`: armed via `SetUpNext(item, seconds, autoAdvance)`, triggered by
  position updates at the credits-segment start (when known) or `duration − 30 s`, counts down
  `AutoPlayCountdownSeconds` on a `DispatcherTimer`. Card click / "Play now" / Enter play
  immediately; Dismiss cancels for the rest of the file; `EndReached` with a pending
  (undismissed) card advances right away.
  **`AutoPlayNextEpisode` governs auto-advance, not the card** (decided 2026-07-30). With it
  off the card is still armed and shown — the next episode with a Play action — and *nothing*
  moves until the user accepts: no countdown starts and the `EndReached` handler declines to
  advance, so mpv simply holds the last frame (`keep-open=yes`) with the offer up. This is why
  card visibility is `_upNextCardVisible` and **not** `_upNextCountdownActive`, which is what
  it used to be: the two were the same thing only while every card had a timer.
  `UpNextHasCountdown` drives the countdown line's visibility and `UpNextCountdownText`
  returns `""` in manual mode, so text and visibility can't disagree. Note for test authors:
  a `Collapsed` element is **absent from the UIA tree**, so `UpNextCountdown` is a countdown
  probe and `UpNextPlayButton` is the mode-agnostic card probe. The countdown is **cancelled by seeking back before the trigger** (re-arming on a
  later crossing) and its tick is **skipped while paused or buffering** — it is wall-clock, so
  without those it abandoned the position the user had just seeked to, or started the next
  episode while they were away (BUGS.md B3). An armed card suppresses credits auto-skip (it would jump to EOF and cut
  the countdown short) and `UpNextPlayRequested` funnels into the normal `PlayItem`
  path, so stop reports and the OSD title behave like a manual start.
- **Trickplay seek previews:** the SDK leaves `BaseItemDto.Trickplay` untyped and the
  single-item builder has no `Fields` param, so `GetTrickplayAsync` re-fetches
  `/Items/{id}?fields=Trickplay` via `GetRawAsync` and parses
  `{ mediaSourceId → { "<width>" → TrickplayInfo } }` with System.Text.Json (some items
  carry an empty `Trickplay {}` — sources without a valid bucket are skipped), picking
  the bucket nearest 320 px. `TrickplayProvider` maps a position to sheet index + crop
  rect (`thumb = clamp(ms/Interval)`, `sheet = thumb / (TileW*TileH)`, row/col from the
  remainder; the last sheet is short — out-of-bounds crops return null). Sheets come
  from `/Videos/{id}/Trickplay/{w}/{sheet}.jpg?mediaSourceId=` through the image disk
  cache. Overlay: `SeekSlider.PreviewMouseMove` (tunneling, so it fires mid-drag too)
  drives a non-hit-testable Popup above the hover x showing `CroppedBitmap` +
  timestamp; fetches dedupe on thumb index and carry a generation counter so stale
  loads never overwrite newer ones. No trickplay → the same popup, timestamp only.
- **Image disk cache:** `Imaging/ImageCache` —
  `%LOCALAPPDATA%\LightWeaver\imagecache\<sha1(url)>.img`, trimmed LRU by last-access
  time to the configured cap (default 500 MB) once per session (NTFS last-access updates
  are often disabled, so hits stamp `LastAccessTimeUtc` explicitly). Downloads carry the Jellyfin auth header,
  dedupe concurrent requests per URL, and write temp-then-move so torn writes never
  become cache entries. Decoding happens on the thread pool (`BitmapCacheOption.OnLoad`
  + `DecodePixelWidth`, frozen before crossing threads) with a small in-memory LRU of
  decoded bitmaps on top. XAML binds through the `CachedImage.SourceUrl`/`DecodeWidth`
  attached properties (an `Image.Source` converter can't work — converters must return
  synchronously); the async completion re-checks the current URL because virtualized
  containers recycle. `GetFileAsync` is public for raw assets (trickplay tiles).
  Since M19 the cap is a settable `MaxCacheBytes` (seeded from
  `AppSettings.ImageCacheMaxMb` at startup) and `CurrentSizeBytes()`/`Clear()` exist
  for the M21 cache UI. The M21 STORAGE Settings section surfaces them: per-cache size
  readouts (invariant-culture units — locale commas break parsing) with Clear
  buttons, Clear-all, and the 100–2000 MB image-cap slider (save-on-change,
  live-applied).
- **Metadata cache (Phase 7 M19):** `Imaging/DiskJsonCache` —
  `%LOCALAPPDATA%\LightWeaver\<dir>\<sha1(key)>.json`, generic
  `GetOrFetchAsync<T>(key, ttl, fetch)` with atomic temp-then-move writes and a
  single-flight gate per key; `LIGHTWEAVER_CACHE_LOG` emits hit/miss/stale lines for
  tests. Two static facades over it, each owning its directory, its lock table and its
  own eviction schedule: `MetadataCache` (`metacache`) and `ExternalMetadataCache`
  (`externalcache`, OMDB scores keyed `omdb:ratings:{imdbId}`, 7-day TTL — global data,
  so the key carries no server/profile, and never the API key). Separate directories are
  load-bearing: the STORAGE section sizes and clears "Media info" and "External metadata
  (OMDB)" independently, and one directory cannot be measured or emptied as two.
  `GetMediaStreamsAsync` caches its user-state-free projection for 1 h
  (re-opened details serve media info from disk; the network path stays the fetch
  delegate so `WithRetry` applies on misses). Full item-detail responses remain uncached.
  Folder browse caching (`BrowseFolderCache`): `FolderCacheEntry<MediaItem>` stores the
  entire folder in the server's canonical order (`SortName` ascending, unfiltered) under
  `browse:{profileKey}:folder:{folderId:N}:v2` (schema 2). Folders are prefetched to
  completion in the background by `BrowsePrefetcher` (500 items/page, 150 ms pacing gap,
  page waiters for fast scrolling, cancellable on profile switch, session close, or exit).
  The folder cache size ceiling is configurable in Settings > Storage (`FolderCacheMaxItems`,
  default 5,000, 500â€“20,000 range); folders exceeding this ceiling are marked `Truncated`
  and fall back to server-side paging. While caching an initial visit, a floating status pill
  (`StatusPill` with `ActivityDots` + `Caching X of Y...`, styled identically to `LoadMoreIndicator`)
  keeps the user informed at the bottom of the grid and disappears on completion. During refresh,
  a similar pill indicates `Showing cached results while refreshing...` or offline status.
  When a complete folder cache exists, `LibraryView` enters local query mode immediately:
  sorts (`SortName`, `DateCreated`, `PremiereDate`, `CommunityRating`, `Random`), filters
  (`Played`, `Unplayed`), genres, and letter rail prefixes are evaluated purely in-memory
  via `BrowseFolderCache.ApplyQuery` with zero server round-trips.
  Incremental refresh (`RefreshFolderAsync`): on loading a cached folder, a background sweep
  pages identity rows (`Id`, `Etag`, `Played`, `IsFavorite`) via `SweepFolderAsync` (500/page).
  `BrowseFolderCache.Diff` identifies added, removed, and modified items; added and changed items
  are batch-refetched via `GetItemsByIdsAsync` (100/batch); and `BrowseFolderCache.Merge` produces
  the updated canonical list. In-flight user updates are preserved via `ReconcileNetworkUserData`.
  When displayed item IDs match, cards are updated in place to retain scroll position and focus.
  User data updates in local mode are debounced to disk (2-second DispatcherTimer, flushed on
  view `Unloaded`). Network failures gracefully fall back to retaining cached cards with an
  explicit timestamp (`Offline - showing cached results from {local time}`).
  Both caches are swept at 30 days from `MainWindow`'s ctor (B19: a cache with only a
  manual Clear grows forever and keeps entries for items long gone from the library).
  The TTL and the sweep answer different questions — the TTL decides how *fresh* an
  entry is, the sweep how long an unused one occupies disk.

## Backlog round (2026-07-10, post-Phase 4)

- **RTX capability probe:** `Player/GpuCapabilities` enumerates desktop display
  adapters (`EnumDisplayDevices`, no WMI dependency) once per process; no NVIDIA
  adapter → "no NVIDIA RTX GPU detected", NVIDIA without "RTX" in the name → "no RTX
  video support". Detection failure counts as capable — never disable a feature
  without evidence. `MpvPlayer.RtxUnavailable` (bool) became `RtxUnavailableReason`
  (string?, null = available): the static GPU cause wins over the dynamic HDR-source
  cause, the filter chain stays suppressed on incapable GPUs, and the overlay toggles
  bind their tooltip to per-cause text from `PlayerViewModel.RtxVsrTooltip/RtxHdrTooltip`.
- **Info panel (ⓘ):** one popup, two sections per user decision. Top: live playback
  state polled from mpv (`video-format`, `video-params/*`, `container-fps`,
  `hwdec-current`, `video-bitrate`, `audio-codec-name`/`audio-params/*`,
  `frame-drop-count`, plus `MpvPlayer.RtxVsrActive/RtxHdrActive` — the *applied* filter
  state, not the toggle wish) on a 1 s `DispatcherTimer` that only runs while the popup
  is open. Rows update **in place** (label-sequence compare) — a Clear+Add reset every
  second flickers and churns the UIA tree out from under automated readers (found
  live: UIA saw an empty list). Bottom: "Media info" `Expander` (collapsed by default,
  hidden entirely for local files) fed once per playback from
  `JellyfinService.GetMediaInfoAsync` — PlaybackInfo's default source mapped to
  container/size/total-bitrate plus per-stream video (codec+profile, WxH, fps,
  `VideoRangeType`, bitrate), audio (codec, layout, language, kbps) and subtitle lines.
- **Subtitle lift:** while the control bar is visible the overlay computes
  `sub-pos = 100 − (barHeight+margin)/overlayHeight·100` (clamped 60..100) and on fade restores
  the user's configured **base position** (`SubtitleBasePosition`, default 100) — *not* a
  hardcoded 100; the two compose as `min(base, lift)`, so a line already resting above the bar
  does not move when the bar appears. mpv-rendered subs never sit under the controls; burned-in
  subs are untouched by definition. Re-applied on resize/visibility/mini-mode and on settings
  live-apply, so bar-geometry changes track. `UpdateSubtitlePosition` is the **only** writer of
  `sub-pos` (Phase 10 M4 closed the settings bypass — see below).
- **Subtitles in the letterbox band (Phase 10 M4)** — two init options, no app-side geometry:
  `sub-use-margins=yes` + `sub-ass-force-margins=yes` (`MpvPlayer.Create`). That is the whole
  mechanism, and it is deliberately much less than the plan called for, because calibration
  measured mpv instead of trusting the story. All rows below are mpv-surface pixels on a 2.39:1
  clip (1280×536) in a 16:9 render area at height 900, where mpv reports `mt=mb=115 h=900` — so
  **the picture ends at y=785 and the band is 785..900**:

  | config | subtitle kind | sub-pos 100 | 110 | 125 | 150 |
  |---|---|---|---|---|---|
  | mpv defaults (pre-M4) | SRT / converted | 822..849 — **in band** | not rendered | not rendered | not rendered |
  | mpv defaults (pre-M4) | dialogue ASS | 720..748 — over the picture | not rendered | not rendered | not rendered |
  | `sub-use-margins=no` | SRT / converted | 707..734 — over the picture | 770..783 (half-height, clipped at the picture edge) | not rendered | not rendered |
  | both options on | SRT / converted | 822..849 — in band | not rendered | not rendered | not rendered |
  | both options on | dialogue ASS | 835..863 — **in band** | not rendered | not rendered | not rendered |
  | either | ASS `\pos` sign | 249..279 | 249..279 | 249..279 | 249..279 |

  Four conclusions, each of which killed a piece of the planned design:
  - **`sub-pos` is not a lever into the band.** Values above 100 push the text below the render
    surface, where it is clipped and renders *nowhere at all* — 110, 125 and 150 produced no
    subtitle in every configuration. `sub-pos` stays what it always was, the 0..100 control-bar
    lift. (The `sub-margin-y` family was rejected for a different reason and stays rejected: it
    stacks with `sub-pos` instead of replacing it.)
  - **`sub-use-margins` is the real lever, and it is mpv's default**, so SRT/converted subtitles
    already landed in the band on the shipped build — verified against a build of the previous
    commit, which measured byte-identical to setting it explicitly. It switches the subtitle area
    from the picture rect to the whole render surface; with it off, subtitles are *clipped* at the
    picture edge rather than spilling into the band. It is pinned so a future default change or a
    stray user config cannot silently undo the placement.
  - **The one real defect was dialogue-style ASS**, which ignores `sub-use-margins` unless
    `sub-ass-force-margins=yes`: 720..748 (over the picture) → 835..863 (in the band). This is the
    only behaviour M4 changes, which is why its suite uses an ASS fixture — an SRT fixture cannot
    see it. Cost on the rest of a library: **none, measured.** On a 16:9 source (no band) the
    dialogue row is 835..863 *both* with and without the options, so forcing margins does not
    override the ASS author's intended `MarginV` where there is no band to move into.
  - **There is no eligibility threshold, because placement self-gates.** The anchor is the surface
    bottom, not the band: same clip, sub-pos 100, four render-area aspects →

    | area aspect | `mb` | band `mb/h` | picture bottom | subtitle row | in band? |
    |---|---|---|---|---|---|
    | 1.7778 | 115 | 0.1278 | 785 | 822..849 | yes |
    | 2.10 | 55 | 0.0611 | 845 | 821..847 | no |
    | 2.29 | 19 | 0.0211 | 881 | 820..847 | no |
    | 2.388 | 0 | 0 | 900 | 821..848 | no |

    The row does not move (SRT; ~51 px above the surface bottom throughout). Whether it lands in
    the band is purely whether the band exceeds the text height plus that margin, so a band too
    small degrades to exactly the old over-the-picture look — no threshold, no `mb/h ≥ 0.06`
    constant, and nothing for `OsdGeometryChanged` to feed. The one asymmetry worth knowing: for
    **ASS** the anchor is not pixel-constant, because ASS is scaled onto the drawn picture, so a
    taller picture means a bigger font *and* a proportionally bigger `MarginV` (gap to the surface
    bottom 37 px at band 0.128, 46 px at band 0.021). The suite asserts the gap, not an exact row.
  - **`\pos`-tagged ASS events are unaffected** — constant at 249..279 across both margin options
    and all four `sub-pos` values, matching the computed author position to within 1 px. Typeset
    signs do not move.

  Verified by the `test-sub-letterbox` suite (pixel-scanning; fixtures generated on demand so it
  needs no provisioning), which fails on the
  band leg without the two options.
- **One writer for `sub-pos` (Phase 10 M4):** settings live-apply used to call
  `MpvPlayer.SetSubtitlePosition` directly, which the next control-bar show/hide simply overwrote.
  It now calls `PlayerViewModel.NotifySubtitleBasePositionChanged()`; `OverlayWindow` subscribes to
  `SubtitleBasePositionChanged` and recomposes. Outside playback nothing is listening, which is
  correct — the overlay recomputes on `IsVisibleChanged` when playback next starts.
- **Track UI:** the track-list parse now includes `video` tracks (+ `codec`,
  `demux-w/h` for row labels), selected via the `vid` property behind a Video dropdown;
  audio/sub rows carry a two-letter language badge (`Player/LanguageBadge` — .NET
  neutral-culture map plus the ISO 639-2 *bibliographic* codes Matroska commonly uses:
  ger/fre/dut/…; unknown/und → no badge, and the badge slot keeps its width via
  `Visibility.Hidden` so rows align). All three dropdown buttons collapse when there is
  no choice: Video/Audio need ≥2 tracks, Subs ≥1 real track (the synthetic "Off" row is
  the alternative). The overlay Back button joins the controls' fade animations (and
  its `IsMouseOver` joins the hide guard).

## Downloads — offline playback (Phase 7 M20)

`src/LightWeaver/Downloads/` (mirrors the `Imaging/` split; the resume/persistence
strategy is a direct port of Velly's `DownloadManager.kt`):

- **`DownloadItem`** — one tracked download: item/server identity, display fields, the
  file path, the picked quality, and the transcode knobs (`MaxWidth`/`VideoBitRate`/
  `Container`) so an interrupted download resumes with the exact same server URL.
- **`DownloadStore`** — JSON index at `%LOCALAPPDATA%\LightWeaver\downloads\index.json`
  (atomic tmp-then-move, defensive load, enum names not ordinals). The index location is
  fixed; only the media directory is configurable — moving it never orphans metadata.
- **`DownloadManager`** — the engine. In-memory `ConcurrentDictionary` is the live source
  of truth; **only status transitions hit disk** (progress ticks are memory + a coalesced
  `DownloadsChanged` event). A pump keeps at most `MaxParallelDownloads` jobs active,
  the rest wait as `Queued`. **HTTP-Range resume:** an existing partial file continues
  with `Range: bytes=<length>-`; a `206` appends (`Content-Range` gives the true total),
  a `200` rewrites from scratch (transcoded streams don't support ranges). Pause/cancel
  ride per-item `CancellationTokenSource`s; `ResumeInterrupted()` re-queues anything left
  `Downloading/Queued` by the previous session (restart recovery). A completed entry whose
  file vanished self-heals (drops, falls back to streaming). Test hooks:
  `LIGHTWEAVER_DOWNLOAD_THROTTLE_KBPS` (observable pause/concurrency on a fast LAN) and
  `LIGHTWEAVER_DOWNLOAD_LOG` (transition/offset evidence).
- **URLs** — `JellyfinService.GetDownloadUrl`: original quality = the same
  `/Videos/{id}/stream?static=true` direct-play URL; transcode tiers =
  `static=false&videoCodec=h264&audioCodec=aac&maxWidth=…&videoBitRate=…`.
  `GetDownloadOptionsAsync` builds the resolution picker from PlaybackInfo: the original
  source (exact size) + 1080p/720p/480p tiers below the source height (size estimated
  from tier bitrate × runtime). Auth is the standard `Authorization` header — tokens
  never land in URLs or the index.
- **Entry points** — the detail view's `DetailDownloadButton` (movie/episode; a completed
  item's button turns into a confirmed remove) and the season/series `DownloadAllButton`
  in the library header. Batch = the M15 episode rules (unwatched first, full list when
  everything is watched, already-downloaded skipped); one quality pick (sampled from the
  first episode) applies to the whole batch, and batch items omit the sampled
  `MediaSourceId` (it belongs to that first episode only).
- **Detail option preload (B36)** — each retained `ItemDetailView` starts one cancellable
  `GetDownloadOptionsAsync` when it becomes visible. The fetch is not awaited by navigation and
  does not disturb the ordinary Download glyph. A click reuses that task; only a click that still
  has to wait replaces the glyph with `ActivityDots` and disables duplicates. Leaving the view
  cancels its token, increments a generation and clears click intent. Re-entering starts fresh but
  never resurrects the old click. Completion must match token, generation and visibility; task
  identity additionally guards failure cleanup from discarding a newer preload. `MainWindow`
  requires the originating detail to still be
  `_nav.Current.View` immediately before opening `ModalHost.ChooseAsync`, so a retained Back/Forward
  frame cannot open a stale picker. Caller cancellation is terminal and bypasses `WithRetry`;
  successful-empty and transport-failure results stay distinct. A reported failure discards only
  the exact failed task, allowing the next same-view click to retry without racing a newer task.
  Debug-only `LIGHTWEAVER_DOWNLOAD_OPTIONS_DELAY_MS` and
  `LIGHTWEAVER_DOWNLOAD_OPTIONS_FAIL_ONCE` make the slow, canceled and fail-then-retry paths
  deterministic. Batch download remains click-driven through the legacy non-cancellable overload.
- **Downloads screen** — `Views/DownloadsView` behind the `NavDownloads` rail entry: per-row
  live progress (row VMs update in place on a 300 ms coalescing tick — no list rebuilds),
  pause/resume/cancel/delete + Pause all / Clear completed (keeps files) / Delete all
  (removes files; confirmed via the M23 modal). The resolution picker is `ModalHost.ChooseAsync`,
  a new single-select option-list mode on the modal layer.
- **Card badge** — a sapphire `download_done` circle, bottom-right of the artwork on both
  card templates. `MediaItem` is immutable, so completion/removal rebroadcasts the item
  through the `NotifyItemUserDataChanged` fan-out and cards re-run
  `DownloadedToVisibleConverter` on rebind.
- **Local-first playback** — the single change point is `PlayItem`: a completed download
  short-circuits negotiation and calls `PlayCore(localPath, authHeader: null)`. Everything
  downstream (mpv `loadfile`, resume seek, queue, overlay) is unchanged — the **load-bearing
  rule is intact**: mpv keeps its render HWND; only the source switches from an
  authenticated URL to a local path. Local playback deliberately reports no progress to
  the server (no PlaybackReporter session); offline progress **sync-back on reconnect is a
  noted follow-up**, out of scope by plan.
- **Settings** — DOWNLOADS section: download folder (empty = app-data default), simultaneous
  downloads (1–4), preferred download quality (preselects the picker row).

## Project structure

```
LightWeaver.slnx      solution (.NET 10 XML solution format)
native/win-x64/       libmpv-2.dll drop point (gitignored, copied to output at build)
publish.ps1           self-contained win-x64 release zip
installer.ps1         publish + Inno Setup compile -> per-user setup.exe
installer/            LightWeaver.iss (Inno Setup script)
src/LightWeaver/      WPF app
  Player/
    Native/           LibMpv.cs (P/Invoke), MpvEnums.cs, MpvStructs.cs
    MpvPlayer.cs      high-level player API + event loop (tracks, chapters, RTX)
    MpvPlayerHost.cs  HwndHost child window for mpv (wid)
    GpuCapabilities.cs  RTX-GPU probe (EnumDisplayDevices) for toggle availability
    LanguageBadge.cs  ISO 639 language tag -> two-letter track-row badge
    Languages.cs      curated preferred-language dropdown list
    PlayerActions.cs  PlayerAction enum + dispatcher (configurable shortcuts, M16)
  Jellyfin/           JellyfinService (SDK wrapper), CredentialStore (DPAPI),
                      MediaItem model, AdvancedSearchQuery, PlaybackReporter,
                      MediaSegmentsClient, TrickplayProvider (position → tile sheet + crop)
  Metadata/           OmdbClient + ExternalRatings (IMDb/RT/MC critic scores by IMDb id)
  Downloads/          DownloadItem/DownloadStore/DownloadManager (offline downloads, M20)
  Imaging/            ImageCache (disk + memory LRU), DiskJsonCache (TTL'd JSON, M19)
                      behind MetadataCache + ExternalMetadataCache, CachedImage
                      attached properties
  Diagnostics/        AppLog — crash / playback-failure / rolling logs (see below)
  Settings/           AppSettings + SettingsStore (JSON), WindowStateStore,
                      HomeLayoutStore (per-profile Home rail layout, M11)
  ViewModels/         ObservableObject, RelayCommand, PlayerViewModel, AppViewModel,
                      NavigationService (back/forward history, M3)
  Views/              LoginView, HomeView, LibraryView, ItemDetailView, SettingsWindow,
                      SearchResultsView, AdvancedSearchView (+ CheckDropdown), GenresView,
                      SectionView (Home see-all), DownloadsView, OverlayWindow (OSD),
                      ModalHost (modal layer, M23), EmptyState, SkeletonFactory,
                      LwSegmentedControl, WheelScroll, CardCommands,
                      MediaTemplates (cards), MediaCardTemplateSelector
```

## Diagnostics logging (`Diagnostics/AppLog.cs`, 2026-08-03; ZIP export 2026-08-05)

Release builds used to write **no log file at all**: every log site was gated on a
`LIGHTWEAVER_*_LOG` environment variable (test instrumentation) and the three `Trace.WriteLine`
calls only reach `OutputDebugString`, so a crash left nothing but a WER entry. Four files now
live in `%LOCALAPPDATA%\LightWeaver\logs` (`…\LightWeaver-Debug\logs` for Debug builds):

| File | Written when | Contents |
|---|---|---|
| `crash-<ts>.log` | an unhandled exception escapes | environment header, full `InnerException` chain with stacks, breadcrumb ring |
| `playback-<ts>.log` | mpv fails a file and the app could **not** recover | mpv's reason, the **stream summary**, breadcrumb ring |
| `app.log` | an error the app catches and recovers from; app events and diagnostic detail when verbose | rolling, session banner per launch per file |
| `mpv.log` | verbose logging is on | every mpv message, redacted, written by a background thread |

General app-event call sites use five distinct entry points; choosing one is part of the
diagnostics contract (`MpvLine` is the separate mpv event-thread path described below):

| Entry point | With verbose off | With verbose on | Incident ring |
|---|---|---|---|
| `Breadcrumb(area, message)` | memory only | memory only | yes |
| `Info(area, message)` | memory only | `app.log` | yes |
| `Detail(area, message, exception?)` | completely inert | `app.log`, including the redacted `InnerException` chain | **never** |
| `Milestone(area, message)` | `app.log` | `app.log` | yes |
| `Error(area, message, exception?)` | `app.log` | `app.log` | yes |

`Detail` checks the volatile opt-in flag before formatting, redaction, exception traversal or IO,
and its whole active path is no-throw. It is for high-volume state needed only during an explicitly
enabled diagnostic run; keeping it out of the 500-entry ring prevents repetitive snapshots from
evicting the sparse transitions that explain a later crash or playback failure.

New machine-readable app messages follow one grep-friendly convention: start with a stable,
lower-snake-case `event=<name>`, then whitespace-separated lower-snake-case `key=value` fields,
for example `event=check trigger=startup outcome=current from_version=1.0.1 elapsed_ms=142`. Format numbers invariantly and use
bounded enum/state/outcome/count/timing values.

`outcome=` is **scoped to its event, not a shared enum**, and deliberately so: `success`/`failure`/
`cancelled` recur across the service boundaries, but a lookup's real answer is
`hit|miss|stale|unreadable`, a negotiation's is `direct_play|transcode|no_transcode_offered|…`, and
an update check's is `available|current|failure|noop`. Flattening those into one global vocabulary
would either lose the distinction that makes each record worth reading or force every event to the
lowest common denominator. So a token outside the common set — `outcome=available` is the one that
prompted this note — is correct as long as the owning event's bullet below enumerates its set.
Whatever the event, the set must be **closed and bounded**: no free-form text ever reaches an
`outcome=`. The `area` argument is likewise an open convention rather than an enum — the owning
component's name in lower_snake_case (`navigation`, `main`, `login`, `home`, `library`, `search`,
`section`, `settings`, `downloads`, `player`, `overlay`, `shortcut`, `updates`, `cache`, `session`,
`audio`, `jellyfin`, `omdb`, `imagecache`, `credentials`, …) — so audit coverage by grepping
`event=`, not by area.

This is a **structural-only privacy boundary**:
diagnostic events do not include access tokens, headers, response bodies, usernames, server labels
or URLs, media titles, query/person/genre text, filesystem paths, fonts, raw audio endpoint ids, or
complete settings objects. A media item's GUID and type are allowed only where correlation needs
them — interaction/load, playback negotiation and the download lifecycle; search records use term
length and result counts. An identity with no bounded form of its own is HASHED rather than dropped:
a cache key becomes `key_hash=<8 hex>` and a profile key becomes `session=<8 hex>`
(`AppLog.ShortHash`), which keeps records joinable while carrying no server URL, user id or
catalogue id. The two are salted differently, on purpose: `key_hash` is hashed over a
**per-process random salt** plus the key, `session=` is a plain digest of the profile key. The
asymmetry follows the input's guessability — a profile key contains a random user GUID and is not
enumerable, whereas a cache key is a fixed prefix over a small public id space (see the cache
bullet below). Settings changes
come from an explicit safe-value whitelist, with audio output reduced to `default`/`custom` and
download resolution mapped to `original`/`1080p`/`720p`/`480p`/`unset`/`custom` rather than copied
from the deserialized file. Storage, folder-open and export failures log only a bounded exception
type; their path-bearing exception messages/stacks are never passed to `AppLog`.
Redaction remains mandatory defense in depth for exceptions and third-party output; it is not
permission for a new call site to log sensitive free-form content and hope the regexes remove it.

The verbose app-flow layer is owned at semantic choke points rather than at render/binding sites:

- `NavigationService` writes one `navigate`/`back`/`forward`/`reset`/`replace` record with view
  type, root flag and stack depths. Callers log only the user action that requested the transition.
- Screen owners write generation-aware load start/outcome records with elapsed milliseconds,
  result counts and explicit stale-drop outcomes. Caught failures use `Detail(..., exception)`.
  A timeout-shaped cancellation from an API with no caller cancellation token is a current
  `failure`; `cancelled` is reserved for operations with an explicit caller-owned token.
- `MainWindow`, cards, rails, modals, shortcuts and the player overlay use stable action ids.
  Mouse move/hover/scroll, live ticks and binding refreshes are deliberately absent; a seek is
  recorded only when committed or abandoned. Invalid typed times expose only length/parse outcome.
- `OverlayWindow` is the single keyboard bridge record before forwarding a player key to its owner.

Below the screen owners, the same convention covers the boundaries where work actually leaves the
process or reaches the disk. These are a different question from `event=load`: that one says a screen
filled, these say which request paid for it, whether the transport misbehaved, and whether state
reached disk.

- **`JellyfinService` — one record per call, not per screen.** `WithRetry` writes
  `event=request op=<caller> outcome=success|failure|cancelled elapsed_ms=… attempts=1|2`, taking the
  operation name from `[CallerMemberName]` (`GetResumePagedAsync` → `get_resume_paged`, cached and
  lower-snaked) so ~30 call sites needed no edit and cannot drift from their own names. A fired retry
  also gets its own `event=retry op=… reason=<exception type> attempt=2` line: without one, a LAN that
  drops the first SYN of every burst is indistinguishable in the log from a healthy one, since the
  retry succeeds. `NegotiatePlaybackAsync` is instrumented directly because it is deliberately *not*
  retry-wrapped (B12) — `event=negotiate outcome=direct_play|transcode|no_transcode_offered|incomplete|failure`
  is the record that explains a silent downgrade to client-side reporting.
- **`PlaybackReporter` no longer fails silently.** Its `Guard` swallowed every exception, so a server
  rejecting progress reports showed up only as watched state that never advanced. It now writes
  `event=report op=start|progress|stop outcome=failure error=<type>`. Success is recorded for `start`
  and `stop` only — a per-tick success line would be the loudest thing in the file for the whole
  runtime of every item.
- **Caches log hashed identities.** `DiskJsonCache` writes
  `event=lookup cache=metacache|externalcache outcome=hit|miss|stale|unreadable key_hash=<8 hex>` and
  one `event=fetch … outcome=stored|empty|failure|store_failed elapsed_ms=…` per miss. The **key never
  appears**: keys are `streams:{server}:{itemId}` and `omdb:ratings:{imdbId}`. `key_hash` is 8 hex of
  SHA-256 over a **per-process random salt** (16 bytes, never logged, never persisted) plus the key.
  It was originally the first 8 characters of the entry's own SHA-1 *filename*, so a record and the
  file it describes could be paired by eye; that convenience is deliberately traded away, because it
  was the same property as being **precomputable**. A fixed key prefix over a public id space is a
  lookup table: `omdb:ratings:{imdbId}` ranges over ~10^7 catalogue ids, this file documents the
  format, and 32 bits of unsalted digest resolves each token to essentially one title — so a shared
  bundle from a verbose run would have told the recipient which films had been opened, which is the
  leak the download records were changed to avoid. Salting keeps everything the field is for
  (one token per key for the life of a run, distinct tokens for distinct keys) and costs only the
  cross-file eyeball join. The on-disk filename scheme is unchanged, so no cache is invalidated. The
  separate `LIGHTWEAVER_CACHE_LOG` hook is untouched and still prints raw keys: it is test
  instrumentation that never leaves the machine, and `phase7-tests/test-m19-cache.ps1` uses it to
  enumerate the run's keys and prove none of them reaches `app.log`.
- **`OmdbClient` stopped being a `catch { return null; }`.** A wrong API key, a rate-limited account
  and a dead network were all indistinguishable from "this film has no critic scores". Now
  `event=fetch source=omdb outcome=success|no_scores|no_match|failure elapsed_ms=… scores=<0-3>` plus
  `status=` and `error=` where the exception carries them. Not logged: the request URL (the key rides
  it as a query parameter), the key, and the IMDb id — the bracketing `[cache]` pair supplies the join.
- **`ImageCache` is summary-only, by construction.** It is asked for every poster and backdrop on
  screen, so the hot path does nothing but bump an interlocked counter. One
  `event=summary reason=threshold|clear|shutdown ops=… mem_hit=… disk_hit=… download=… download_failed=…
  bytes_downloaded=… mem_entries=… mem_bytes=…` record covers each window: every 50 operations, on
  `Clear`, and once from `App.OnExit`. A window with nothing counted writes nothing, so the
  no-write-on-a-healthy-run rule survives. The exit flush is what makes the record assertable —
  a suite closing the app through the guard always gets the tail.
- **Downloads: lifecycle at the state machine, silence inside the byte loop.** `enqueue`, `start`,
  `http`, `pause`, `resume`, `remove`, `cancelled`, `pause_all`, `clear_completed`, `delete_all`,
  `resume_interrupted`, `complete`, `failed`, each with the item GUID, `status=` (the state the event
  moves the item *to*, lower-snaked like every other field — the enum's own PascalCase spelling stays
  in the on-disk index, which is a serialization format), byte counts and elapsed time. **Nothing in the transfer loop logs**: its
  finest granularity is the 512 KB progress notification, i.e. ~2000 records for a 1 GB file — enough
  to consume the rolling file's one retained generation on a single download. Byte progress is UI
  state, not diagnostics. Two pre-existing calls were replaced rather than kept: the completion `Info`
  became `event=complete`, and the failure `Error` **stopped passing the exception object** — an IO
  failure here names the file it could not write, and a download's filename is built from the media
  title, so the message and stack were exactly the leak the structural contract exists to prevent
  (redaction masks the user directory, not the title). Type, byte count and state remain.
- **Persistence says whether it worked.** `SettingsStore`, `HomeLayoutStore`, `DownloadStore` and
  `CredentialStore` write `event=load|save|clear outcome=success|absent|empty|fallback_defaults|
  unreadable|failure` with counts and payload **sizes**, never content. `DownloadStore` and
  `CredentialStore` had silent catches on their most user-visible failures — an unreadable index
  ("my downloads emptied themselves") and an unreadable credential store ("I was logged out for no
  reason") — and those two are `Error`, so they land even with verbose off. Every one of them logs the
  exception **type** only: their messages name app-data paths.
- **Session correlation without the profile key.** M2 dropped the profile identity from the session
  records entirely, which made a multi-profile log unreadable — `warm_activate` /
  `revalidate_invalid` pairs could not be attributed, and switching back and forth looked like the
  same event twice. `AppLog.ShortHash` (8 hex of SHA-256) restores an identity that is one-way,
  stable for the life of the profile and distinct per profile, so the records read
  `event=warm_activate session=1a2b3c4d`. Redaction is *not* the mechanism that could have solved
  this: `{userId:N}@{serverUrl}` contains no secret *name*, so no rule matches it. The same token
  appears on the Home-layout records, which are keyed by the same string, and the
  `LIGHTWEAVER_SESSION_LOG` hook keeps its exact legacy protocol (raw keys included) because
  `test-warm-switch.ps1` reads it. `RedactCheck --detail-contract` asserts the four properties
  directly — shape, determinism, distinctness, and that no rule eats the field — rather than
  inferring them from a log file.

- **The player records semantics, never ticks.** `MpvPlayer`, `PlayerViewModel` and
  `PlayerActionDispatcher` write `[player]` records for the sparse events that mean something:
  `event=audio_options` (one per apply: device category, passthrough, normalization),
  `event=loading state=start|cleared`, `event=file_loaded gen=`, `event=tracks video=… audio=… sub=…`,
  `event=chapters count=`, `event=audio_reconfig`, `event=audio_output ao=`, `event=end_file reason=`,
  `event=playback outcome=failure reason=`, `event=hwdec state=`, `event=rtx_chain`,
  `event=rtx_availability outcome=… reason=`, `event=passthrough outcome=fallback trigger=`, and
  `event=audio_device outcome=applied|fallback`. What is **not** recorded is the larger half of the
  design: `time-pos`, the `video-params` and `osd-dimensions` leaves, the `pause`/`volume`/`mute`
  echoes and the Up Next countdown tick all arrive many times a second or a second apart forever, and
  one line each would bury everything above *and* would mean a player parked on a paused frame kept
  writing to disk. The change-gated records follow the CONTENT rather than the property: mpv
  republishes `track-list` on every selection change, so `event=tracks` is written only when the
  counts differ and is reset by `LoadFile`; `current-ao` and the RTX applied state are likewise
  transition-only. mpv's own verbose stream is not duplicated here — it already goes to `mpv.log`
  through `AppLog.MpvLine`, and these records are the app's *interpretation*.
- **Threading is part of the contract.** Nothing in the player writes a log line on mpv's event
  thread: file IO measured 364 µs per line and that thread delivers the events the loading indicator
  waits on. The event-loop sites record from inside the dispatcher callback they already post, and
  the one that has no such callback (`end_file`) tests `AppLog.Verbose` *before* it even posts.
- **The action layer records refusals, not successes.** A key press already writes
  `[shortcut] event=invoke` and an OSD press `[overlay] event=interaction`, and both funnel into
  `PlayerViewModel` — so a success record underneath them would write one user action two or three
  times. What those intent records cannot know is that the action did nothing, which is exactly what
  `event=action action=<id> outcome=noop reason=<token>` says: `insufficient_tracks` on the three
  cycle actions, `no_chapters`/`last_chapter`, `no_active_segment`, `nothing_next`/`nothing_previous`,
  `queue_end`/`queue_start`, `no_up_next`, `no_player`, and `unmapped_action` for a `PlayerAction`
  that reaches a binding before it reaches the dispatcher's switch. The Up Next card records its own
  state machine at the arm, the disarm and the two ways it fires — never per countdown tick.
  Note one asymmetry found while writing the suite: `Space` is registered twice on purpose
  (`AppShortcuts.Chrome` **and** `PlayerAction.TogglePause`), so a key that reaches the main window is
  served by the chrome binding and reads `scope=chrome`, while `Right` reads `scope=player`.
- **An audio device is a category plus a hash.** `Say("audio device set to '<endpoint>'")` and
  `Say("sub-add failed for <url>")` are gone: a WASAPI endpoint id or description names a person's
  hardware and a subtitle URL names both the server and the item. The records carry
  `category=auto|custom` (or `reason=device_absent`) plus `device_hash=<8 hex>` from
  `AppLog.ShortHash`, which is the third hashed identity in this file after `key_hash` and `session`
  and is asserted by `RedactCheck --detail-contract` the same way. Redaction could not have solved
  this either — an endpoint id contains no secret *name*. GPU adapter names are a deliberate
  exception and stay readable, because `AppLog.EnvironmentHeader` already prints them in every
  incident file; what the RTX records avoid is free *text* (`RtxUnavailableReason` is a tooltip
  sentence), so `GpuCapabilities.RtxUnavailableToken` supplies the bounded token instead.

- **The updater says what it decided, never what it fetched.** `[updates]` covers the whole
  lifecycle — `check` (with `trigger=startup|automatic|manual`, `from_version`/`to_version` and
  `outcome=available|current|failure|noop`), `feed`, `asset`, `download_start` (carrying
  `resume=`), `download_progress`, `download`, `verify`, `promote`, `install_queue`,
  `exit_install`, `restore`, the automatic-check scheduler's own `start`/`stop`, the `cleanup*`
  family with counts, and `phase` for every state transition. Versions are PUBLIC and printed in full; what never appears is the feed JSON,
  any URI or host, any SHA-256 **digest**, any staging/pending/partial/launch **path**, and the
  Authenticode signer. Verification therefore logs its *outcome*, never the hash it compared, and
  the feed record carries only a release count plus `endpoint=fixture|production` — a redirect is
  a hop number, not a destination. There is no hashed identity here at all: an updater has nothing
  personal to correlate, so unlike `key_hash`/`session`/`device_hash` it needed no token.
- **Two rules kept the updater's records sparse, and both are structural rather than remembered.**
  Phase transitions funnel through one place (`RecordStatus`, behind the `SetStatus` setter) which
  returns before formatting when verbose is off and emits `event=phase` **only when the phase
  actually changes**. That matters because `SetStatus` is called from inside the download's
  byte-copy loop on every 128 KiB read — a naive record there fires hundreds of times per
  transfer. Download progress is coalesced to 10% buckets in the same funnel, so a 16 MiB transfer
  that raises 128 progress callbacks writes at most 11 lines (0,10,…,100). The bucket resets when a
  transfer starts, so a second download in one session logs its own; a **resumed** transfer starting
  at 40% emits `percent=40` and never back-fills the buckets it skipped; and an unknown or zero
  total size emits nothing rather than dividing by zero. `phase5-tests/test-m8-update.ps1` proves
  the cap is real rather than incidental — 16 MiB over a delayed loopback fixture, asserting the
  record count stays ≤ 11 with no bucket repeated, which fails immediately if the coalescing is
  removed.
- **The updater's silent `catch` blocks are the reason this milestone existed.** Its best-effort
  cleanup helpers swallowed `IOException`/`UnauthorizedAccessException` with no trace at all, so a
  staging directory that would not clear left nothing to read afterwards. They still swallow —
  behaviour is unchanged, this milestone is observability only — but now leave a bounded
  `error=<TypeName>` record. No updater site passes an `Exception` **object** to an `AppLog` entry
  point: these messages carry file paths and URLs, so the type name is the whole payload. That rule
  is asserted statically, and the assertion is name-independent — it rejects a trailing bare
  identifier in any `AppLog.Detail|Info|Error` call in these four files, not just one spelled `ex`.
  `WriteMetadata` used to be the one bounded exception to the sparseness rule, recording per failed
  replace attempt (up to `MetadataWriteAttempts - 1` per call): survivable on its own, but metadata
  is rewritten on every 128 KiB read, so a pathological antivirus that fails-then-succeeds on each
  replace multiplied its own burst by five for the length of a transfer. It now counts the failures
  and writes **one** `event=metadata outcome=failure kind=… attempts=<n> error=<TypeName>` per call,
  from the `finally`, so the give-up path reports its attempt count too instead of leaving the
  caller's own failure record as the only trace. A first-attempt success still writes nothing.

`backlog-tests/test-verbose-player-flow.ps1` is the player half of the contract, and its third leg
is the one that keeps the design honest: it loads the local fixture, drives every refusal the fixture
can produce, then snapshots `app.log`'s LINE COUNT and requires it not to move — once over 20 s
paused, once over 8 s playing. Both were verified non-vacuous by temporarily adding a one-second
`Detail` tick, which fails both legs. The suite also asserts one record per press (the double-logging
guard), that no `[player] event=action` carries a non-`noop` outcome, that the track record appears
at most twice for one file, and — statically, against the source — that the per-tick property
handlers and the two coalescing posts contain no `Detail(` call at all.
`phase7-tests/test-audio-device.ps1` covers what a local fixture cannot: a real device switch, where
it requires `category=custom device_hash=<8 hex>` in `app.log` and the endpoint id to be absent from
it entirely, plus the same pair for the missing-device fallback.

Two properties of the updater matrix are worth knowing before editing it. Every leg closes its app
through the tracked guard and nothing else, so a refusal is a `throw` — the suite therefore catches
its own abort and still prints a `RESULT:` line rather than reading as a harness crash, and it will
**not** roll its Debug app-data sandbox back while a Debug process is alive: deleting that directory
under a live process half-succeeds on the files it holds open and then leaves the restoring
`Move-Item` with an existing destination, which would strand the real profile in `%TEMP%` with only a
`Remove-Item` error to explain it. It fails loudly and names the backup instead. Its
policy-cancellation leg throttles the fixture to 600 ms per 32 KiB chunk (~38 s for the 2 MiB
payload) rather than 300 ms (~19 s), because the UIA work between the transfer starting and the
policy flip costs twelve seconds or more on a slow test machine, and a transfer that finishes first leaves
nothing to cancel — which fails as a missing record, the least informative shape a failure can take.

`backlog-tests/test-verbose-app-flow.ps1` enables verbose logging in isolated Debug app data,
drives Login through overlay routes, checks record order/singularity and forbidden sentinels, and
restores settings from the exact original bytes without printing their contents. It also asserts the
two hashed identities by SHAPE rather than by value, so it never has to read a credential to know
them: `{userId:N}@{serverUrl}` means 32 hex followed by `@` must never appear, every `session=` field
must be exactly 8 lowercase hex (`\b` matters — `server_session=true` is not a token), every
`key_hash=` likewise, and the raw cache-key prefixes (`streams:`, `omdb:ratings:`) must be absent.
The other three suites cover the same contract from the inside: `phase7-tests/test-m19-cache.ps1`
opens two different items and asserts the `key_hash` **properties** the salt makes untestable by
recomputation — present, one token per distinct key, the same token for three lookups of one key,
no raw key anywhere in `app.log`, and no token equal to that entry's SHA-1 filename prefix (the
assertion that fails if the salt is ever dropped); `phase7-tests/test-warm-switch.ps1` proves two
profiles yield two **distinct**
session tokens and that both raw profile keys are absent; and
`phase7-tests/test-offline-downloads.ps1 -PreloadOnly` drives a real throttled transfer through
enqueue → start → http → pause → delete-all, asserts each record, and bounds the total number of
download records so a future per-chunk log site fails the suite. That leg is gated to `-PreloadOnly`
on purpose: the full run kills the process twice to test crash recovery, which is not a mode a
logging assertion should share.

The rules that shape it, each load-bearing:

- **A run where nothing fails writes no file.** The breadcrumb ring is memory-only (500 lines,
  each clipped to 1000 chars) and each file's session banner is written lazily, before that file's
  first real entry — one shared flag was a bug: whichever of `app.log`/`mpv.log` wrote first
  consumed the only banner. `backlog-tests/test-diagnostics-logs.ps1` asserts the no-write rule
  first. A *recovered* failure costs one `app.log` line; an incident FILE means something actually
  broke. That distinction is why a direct-play failure that succeeds on transcode retry writes a
  line and not a file — transcode fallback is the normal path on some libraries, and incident files
  share a ten-slot budget with crash logs.
- **Every line is redacted**, including each file's title line and mpv's own output. Rules cover
  `api_key`/`access_token`/`token`/`secret`/`password`/`pw` followed by `=`, `=>` or `%3D`, bare or
  quoted (a bare `:` deliberately does **not** count — see below); any `Authorization:` header value
  whatever the scheme, **on any line** (`RegexOptions.Multiline`: without it `$` matched only
  end-of-input, so a header on any line but the last leaked, and exception messages are routinely
  multi-line); the `X-Emby-*` headers;
  and `C:\Users\<name>` → `<user>` (either slash), since stack traces and app-data errors otherwise
  carry a real person's name. The rules are deliberately broader than what this app formats itself,
  because mpv echoes header and URL values the app never builds — the Jellyfin token reaches it
  through `http-header-fields`.

  One judgement call: a bare `name:` separator is **not** treated as a credential — only `name=` or
  a *quoted* JSON key. With `:` accepted anywhere, the rules turned the playback log's most
  important line, `item: The Secret: Dare to Dream [Movie] …`, into `The Secret: <redacted> to
  Dream` — eating a film title in the one field that justifies the file. Over-redaction of
  diagnostics is a defect too, so `verify/tools/RedactCheck` asserts **both** directions:
  representative URL, header, updater, settings, session and multiline-exception secrets must
  vanish, while real diagnostic lines, structured events and titles survive byte-identical. It calls the
  private `AppLog.Redact` by reflection on the built assembly, so there is no copy of the rules to
  drift, and the suite runs it as its own leg. Five shapes reached it as regressions: `Token=SECRET`
  unquoted; `api_key="SECRET"` (a quantifier bug meant it did not match at all);
  `{"AccessToken": "…"}` — Jellyfin's own auth-response shape, which leaked while the docs claimed it
  was covered; `Authorization:` on a non-final line; and, in the other direction,
  `D:\Movies\Secret=Agent\a.mkv`, truncated mid-path until a lookbehind excluded names preceded by a
  path separator. The value class still allows `/` and `\` on purpose: narrowing it to stop at a
  slash would redact a base64 token's prefix and leak its tail, which is worse than either failure.
- **Verbose mpv detail is written by us, not by mpv.** `mpv_request_log_messages` goes from
  `warn` to `v`; mpv's own `log-file` option cannot be filtered, so it stays reachable only
  through `LIGHTWEAVER_MPV_LOG` for tests. Only `warn`/`error`/`fatal` are still forwarded to the
  `LogMessage` event, so raising the level changes neither what the app reacts to nor what the ring
  holds.
- **The mpv event thread never touches the disk.** Lines are queued (bounded at 8192, overflow
  counted and declared in the file) and drained in batches by one background thread. Measured:
  `AppendAllText` costs **364 µs** per line and one local file at level `v` emits ~300 lines inside
  the load burst — ~110 ms of blocking on the thread that delivers `FILE_LOADED` and
  `PLAYBACK_RESTART`, which is what the loading indicator waits on. A network source is chattier.
  `WriteIncident` and app exit flush the queue, so `mpv.log` **on disk** is current as of an
  incident — note that this does not put those lines in the ring. The ring's mpv source is separate:
  warn/error/fatal lines are breadcrumbed on the event thread where they arrive, precisely because a
  `Dispatcher.Post` never runs when the UI thread is the one dying, so mpv's last words used to be
  missing from the crash file that exists to carry them. Each source breadcrumbs exactly once —
  mpv's own lines in the event loop, the app's player-side messages in `MpvPlayer.Say`.
- **The stream summary is the point of the playback file.** mpv's reason string alone
  ("unrecognized file format") names no container, codec, decoder or play method;
  `MainWindow.WritePlaybackFailureLog` records item, direct-play/transcode/local, mpv version and
  `hwdec-current`, container, position, selected video/audio codecs with resolution and gamma,
  track counts, RTX state, and the decode/audio settings in force. It runs while the failure is
  fresh — those properties are gone once mpv moves on. Numbers are invariant-formatted; a German
  locale first wrote `position: 0,0 s`, and comma decimals are what broke log parsing before.
- **Retention**: the **count** cap is what fires — newest 10 incident files. The 20 MB folder cap
  is a backstop kept unreachable by construction: rolling files rotate at **2 MB** keeping one
  generation (4 × 2 MB ≈ 8 MB) and an incident file is bounded by 500 ring lines × 1000 chars.
  This matters because a size pass must sacrifice something and no ordering is neutral: the first
  version deleted oldest-first regardless of size and **destroyed 15 crash logs to reclaim 84
  bytes** against an 8 MB overage (measured, not hypothetical). When the backstop does fire it
  spends the cheapest data first — previous rolling generations, then the *largest* incident files
  — so small crash logs are the last thing to go. Rotation counts the incoming entry, not just the
  file's current length (an mpv batch is thousands of lines, so "2 MB" was really 2 MB plus a
  batch), and both the total and the deletions cover **only the four names this facility writes**:
  dropping an unrelated 25 MB `.log` into the folder — the settings page hands the user an Open
  folder button — previously made the size pass delete every crash log and reclaim nothing, because
  the file responsible was in no deletable class.
- **Crashes are logged once, not swallowed.** `DispatcherUnhandledException` and the AppDomain
  handler write the file and then let the process die as before — continuing on invalidated state
  produces a second, wronger bug report. Because not setting `e.Handled` makes WPF rethrow, a UI
  crash reaches BOTH handlers, so `AppLog.Crash` de-duplicates by exception identity (measured:
  two files 6 ms apart before it did). `UnobservedTaskException` calls `SetObserved()` **before**
  logging, so a throw inside logging on the finalizer thread cannot escalate a benign unobserved
  exception into a process kill.
- `LIGHTWEAVER_CRASH_TEST=dispatcher|background|task` raises a deliberate exception ~3 s after
  startup; **any other value is a no-op** (an earlier version treated `=0` or a typo as
  "dispatcher", i.e. a shipped binary that crashes itself). `task` defers its GC to a later tick
  because collecting immediately after `Task.Run` raced a task that had not faulted yet — 1 crash
  file in 4 runs before the fix. The hook exists because a crash handler nobody has watched fire
  is not a feature.
- **Logging must not throw.** The write-side entry points used by dying threads, mpv's event thread,
  and download workers treat logging as best-effort and catch `Exception`. `ExportTo` is the
  deliberate exception: failures that prevent creating the requested bundle propagate to the
  Settings handler so the UI can report them instead of claiming success.

The setting is `AppSettings.VerboseLogging`, surfaced in the **DIAGNOSTICS** group at the bottom
of Settings as a checked/unchecked **Verbose logging** control beside the folder-size/path readout
and **Export…**, **Open folder**, and **Clear** actions. It live-applies to `app.log` immediately;
mpv's level is read at `MpvPlayer.Create`, so it takes effect from the next playback.

**Export…** flushes queued verbose mpv output, then writes one ZIP containing the current AppLog-owned
files (`app[.1].log`, `mpv[.1].log`, `crash-*.log`, and `playback-*.log`) plus a generated
`about.txt` with the app/runtime/OS versions, log count, and verbose state. It deliberately excludes
unrelated `*.log`, `settings.json`, and `credentials.dat`. The facility redacts its own log lines
when writing them; export performs no second redaction pass and does not bypass the bounded retention
described above. The size readout and **Clear** likewise operate on `*.log`; **Open folder** creates
the folder first so it also works before a healthy install has written a log.

## Design decisions log

- **Per-platform native clients** (LightWeaver-Windows / -MacOS / -Android / -iOS, one repo
  each) instead of one cross-platform codebase — predecessor (Velly, Kotlin Multiplatform /
  Compose Desktop) proved the concept but suffered from Swing/Compose interop quirks and
  sluggish UI chrome.
- **WPF over WinUI 3** — WinUI 3's windowing/HWND-hosting rough edges were judged a worse
  trade than WPF's age; .NET 10 Fluent theme closes the visual gap.
- **Credentials:** Windows DPAPI (`ProtectedData`) instead of hand-rolled AES — it's the
  platform-native answer on a Windows-only client.
- **Verification is local; there is no CI** (decided 2026-07-10). The build gate is
  `dotnet build -warnaserror` on the developer's machine before every commit. Behaviour is proven
  by the live UI Automation suites, which launch the real app, take the foreground and drive
  synthetic input — so they run on a dedicated test machine rather than on whichever desktop is
  being worked on. The suites that genuinely need real hardware (an RTX GPU, an exclusive-mode
  WASAPI endpoint, an HDR output, real foreground timing) are the exception and run where that
  hardware is. Releases are built locally too (`publish.ps1` / `installer.ps1` / `release.ps1`).
  Hosting that verification was never practical: the suites need an interactive desktop with a GPU,
  an audio endpoint, staged libmpv and real media fixtures.
- **Visual QA harness (Phase 7 M9)** — the `test-visual-qa` suite
  tours the live app across every screen and writes a labeled screenshot gallery +
  manifest for model review. Main-window screens capture via `PrintWindow`; the player
  OSD/mini-player — invisible to `PrintWindow` because the transparent overlay window is
  composited by DWM over the separate mpv-owned child HWND — capture via a bundled DXGI
  **Desktop Duplication** console tool (`LwCapture`, built on
  first use) that reads the post-composition framebuffer, handles the HDR (FP16 scRGB)
  primary monitor by tone-mapping to SDR, pokes the desktop to defeat the
  static-screen-first-frame-is-black duplication quirk, and resolves window rects itself
  (per-monitor-DPI-aware) because DXGI speaks true physical pixels while the
  system-DPI-aware driver sees scaled coordinates on the 100%-scale test monitor.
- **Releases ship from this repo, not a separate downloads repo** (decided 2026-07-10, for
  Phase 5 M8): locally built `setup.exe` artifacts are uploaded as GitHub release assets on
  `oss96/LightWeaver-Windows` by `release.ps1`. The repo is public, so the shipped update
  checker reads the release feed anonymously (no token — see "Update checker" above).

## Design system — "woven light" (tokens implemented, Phase 6)

A full visual design system was generated from `design/claude-design-prompt.md` and lives at
`design/LightWeaver Jellyfin design system/LightWeaver Design Spec.dc.html` (keep `support.js`
alongside it). It defines the dark "woven light" language — near-black cold surfaces,
stormlight glow interaction states, a garnet primary accent with sapphire/emerald/amber
semantics, a glyph-like icon set, and full-screen mockups for all six screens (login, home,
library, detail, player OSD, settings). (Icon deviation: the app ships Google Material Icons
Rounded rather than the spec's bespoke glyph alphabet — see `Icons.xaml` below.)

Phase 6 Phase 0 translated the tokens into WPF ResourceDictionaries under
`src/LightWeaver/Theme/`, merged into `App.xaml` (order matters — later dictionaries resolve
StaticResources from earlier ones):

- `Colors.xaml` — surface ladder (`LwStorm0..4`, hairline/stroke), text ladder (`LwText1..4`),
  stormlight interaction colors (`LwLightCore/Haze/Dim`), gem triads
  (garnet/sapphire/emerald/topaz/ember, core + lit), plus per-state button/toggle/chip fills
  taken verbatim from the spec's component sheet (all `Lw*` keys). **Phase 8 M1 — single
  interactive accent:** anything interactive and on/active is garnet. The toggle on-state
  brushes (`LwToggleOn*`) were retargeted from stormlight blue to garnet, and the checked
  OSD gem-toggle reuses the garnet chip glass (`LwGarnetChip*`, incl. the new
  `LwGarnetChipTextBrush`). Passive/informational pills (status/language) stay blue gem;
  rating-brand colors, the emerald watched / sapphire download badges, and the structural
  stormlight glow language (focus rings, active nav) are semantic exemptions.
- `Effects.xaml` — `DropShadowEffect` tokens: elevation ladder (`LwElevation1..3`) and glows
  (`LwGlowFocus/Hover/Garnet/Ember/Knob/ToggleOn`; glow = ShadowDepth 0, blur ≤ 28, per the
  spec's "glow, cheaply" rule — no full-screen blur anywhere). Phase 8 M1 added
  `LwGlowKnobGarnet` (slider handle) and `LwGlowPillGarnet` (checked OSD pill), and recolored
  `LwGlowToggleOn` to garnet, so the slider thumb and toggle bloom match the garnet accent.
- `Typography.xaml` — bundled OFL fonts as embedded resources (`Theme/Fonts/`: Marcellus,
  Source Sans 3 regular+semibold, JetBrains Mono; ~1.2 MB added to the installer) exposed as
  `LwFontDisplay/Ui/Mono` (Segoe UI fallback), plus the type-ladder TextBlock styles
  (`LwTypeDisplayXL/Display/Title/Heading/Body/Label/Caption/Mono`). WPF has no
  letter-spacing; the spec's tracking is approximated by casing at call sites.
- `Metrics.xaml` — spacing scale `LwSp1..10` / `LwPad1..10` (4-px base), the radius ladder
  (below), and motion `Duration` tokens (`LwDurInstant/GlowIn/GlowOut/Drift/Ambient`). Two
  helpers ship beside it because WPF's `Border` cannot express those shapes from a token alone:
  `HalfHeightRadiusConverter` (`LwRFullFromHeight`) for full-round silhouettes and
  `RoundedClip` (`theme:RoundedClip.Radius`) for round-clipping artwork — both explained under
  the foot-guns below.

#### The radius ladder — Material 3's shape scale (Phase 10 M5)

The ladder is defined once in `Metrics.xaml` and mirrored by the design spec
(`design/LightWeaver Jellyfin design system/LightWeaver Design Spec.dc.html`) — **spec and
XAML are one source and are edited together**. M5 retargeted the pre-existing 4/8/12 ladder
onto Material 3's steps:

| Token | Was | Is | M3 step | Family |
|---|---|---|---|---|
| `LwR1` | 4 | **8** | small | inputs, menu/flyout rows, thumbs, skeletons, small chrome |
| `LwR2` | 8 | **12** | medium | cards, secondary containers, popovers/flyouts |
| `LwR3` | 12 | **16** | large | modal inner surfaces, large popovers, brand marks |
| `LwR4` | — | **28** | extra-large | modal-weight cards — `LwModalCard`; the sign-in and Up-Next cards follow in M6 |
| `LwRFull` | — | **999** | full | **square consumers only** — see the foot-gun below |
| `LwRPill` | 999 | 999 | full | **square consumers only** (badges, the 22-sq seed-remove button) |

`LwRFull` and `LwRPill` share the value 999 but stay **semantically distinct on purpose**: a
later change to the chip family must not drag every button with it. Neither is a general-purpose
"make this fully round" token — both are only correct on a square element, for the reason below.
The fully-round *families* (buttons, pills, chips, toggles, nav indicators, OSD chrome) bind
`LwRFullFromHeight` instead.

**Rounded rect, not a squircle — a deliberate approximation.** These are WPF `CornerRadius`
values, i.e. circular-arc corners, not true superellipse squircles. A genuine squircle needs a
`Path`/`Geometry` or a per-component clip, which would bypass the token layer and every
`Border` that consumes it; the cascade (one edit → many screens) is worth more than the
curvature.

**Foot-gun: `999` does not mean "stadium" in WPF.** This is the single most useful thing this
milestone learned, and it is unlike CSS `border-radius: 999px`. `Border.GenerateGeometry`
resolves corner overlap per **edge**, clamping the horizontal arc radius to width/2 and the
vertical one to height/2 *independently*. The corners therefore become quarter-**ellipses** that
meet in the middle: at `CornerRadius=999` a `w × h` Border is exactly the **ellipse inscribed in
its box**. On a square element that is a circle (correct); on anything wider than tall it is a
lens.

Measured on a standalone 510×60 Border, counting opaque columns in the top row — a true stadium
needs `w − h` = 450 of 510 (88 %):

| `CornerRadius` | flat top columns | shape |
|---|---|---|
| **999** | **0 / 510** | lens (the inscribed ellipse) |
| 255 | 0 / 510 | lens |
| 60 | 390 / 510 | over-rounded |
| **30 (= height/2)** | **450 / 510** | **stadium** |

And in the app, on the login screen's 510×60 primary button at 999: the top edge sagged 28 px at
the ends with only 24 % of columns at the flat minimum. The one value that makes both clamps bind
at the same radius — and so keeps the corners circular — is exactly `height/2`.

Because the shared templates serve consumers from 28 px (`LwCardActionButton`) to 44 px
(`LwOsdPlayButton`), that value cannot be a static token. The full-round family therefore binds
`Theme/HalfHeightRadiusConverter.cs` (resource key `LwRFullFromHeight`) to the chrome Border's
own `ActualHeight`: stadium when wide, circle when square, at any height, and still entirely
inside the token/template layer. The usage pattern, verbatim:

```xml
CornerRadius="{Binding ActualHeight, RelativeSource={RelativeSource Self},
               Converter={StaticResource LwRFullFromHeight}}"
```

Consumers, in two waves. **Buttons and chrome**: `LwButtonTemplate` (all four button styles +
`LwNavButton` + `LwIconButton` + `LwCardActionButton`), `LwNavRailItemTemplate`,
`LwNavRailButton`, `LwSegmented` (container + thumb), `LwToggleSwitch`'s track, `LwIconToggle`,
`LwOsdButton`, `LwOsdPlayButton`, AdvancedSearch's `OrderToggle`. **Pills, chips and badges**
(the `LwRPill` family, which had been ellipses since Phase 6 rather than stadiums): `LwChip`,
`LwGemPillStormlight`, `LwGemPillTopaz`, `LwSearchField`, `LwOsdStatusPillBlue`,
`LwOsdStatusPillAmber`, `LwOsdGemToggle`, AdvancedSearch's `StarterChip` and seed chip, and
`CheckDropdown`'s count badge. **Third wave, Phase 10 M7 — the input family**: `LwTextField`,
`LwPasswordField`, `LwDropdown`, `CheckDropdown`'s trigger chrome, and AdvancedSearch's
`EngagedRing`.

That third wave is the answer to a question M5 and M6 never asked: *what does a static token
look like on controls of different heights?* The answer is "different things", and the Advanced
Search facet row was where it showed worst. Its two rows carried one `LwR2` across five control
families and rendered as four heights and three silhouettes — a rounded-rect text field beside
two short floating pills beside an almost-stadium dropdown beside two true stadiums. Settings had
the same disease more quietly: the same 12 read as a rounded rect on the h40 number box and as a
stadium on the h34 language dropdown directly below it. Binding the radius to the element's own
height makes the silhouette a property of the shape rather than of the token, so the row is
self-consistent whatever heights it ends up with.

Text inputs joining the *full-round* family (rather than staying at a Material-style small
radius) is a deliberate call with in-app precedent: `LwSearchField` is a text input and has been
a stadium since M5's second wave.

**The height bug underneath it, which mattered more than the radius.** `LwDropdown` and
`CheckDropdown` both rendered their chrome **20 DIP tall regardless of their declared height** —
measured at 30 px against the 57 px text field beside them on the Advanced Search row, an 18 DIP
gap that read as the two facets floating in a taller row. Both templates already forced
`HorizontalAlignment="Stretch"` on their inner `ToggleButton`; neither forced the vertical pair,
and the Fluent theme centres button-family controls, so the chrome shrink-wrapped its content.
Both now set `VerticalAlignment`/`VerticalContentAlignment="Stretch"`, and `LwDropdown`'s default
height moved 34 → 38 to match `LwTextField`, `LwPasswordField`, `LwSegmented` and `OrderToggle`,
so a filter row is one row height instead of four.

Worth keeping as a lesson: the first column scan taken to measure this hit the pill's **corner
arc** rather than its flat middle and reported 26 px — a plausible number that would have sent
the fix at the wrong target. Scan a silhouette at its centre, not near its edge.

Every one of them resolves to exactly `ActualHeight / 2`, and the rendered silhouette matches the
analytic stadium to within antialiasing. Selected measurements (flat top row, actual vs. the
`(w − h) / w` a stadium requires — and what `999` gave before):

| Consumer | size | at 999 | height-bound | stadium target |
|---|---|---|---|---|
| `LwSearchField` | 300 × 38 | 0.0 % | **87.3 %** | 87.3 % |
| `LwChip` ("DOLBY DIGITAL PLUS 7.1") | 157 × 22 | 0.0 % | **85.4 %** | 86.0 % |
| `LwOsdGemToggle` ("RTX VSR") | 67 × 24 | 0.0 % | **63.2 %** | 64.2 % |
| `LwOsdStatusPillBlue` ("RTX HDR") | 61 × 20 | 0.0 % | **66.1 %** | 67.2 % |
| login primary button | 510 × 60 | 0.0 % | **88.2 %** | 88.2 % |
| `LwIconButton` (square) | 42 × 42 | 0.0 % | **0.0 %** | 0.0 % (circle — 999 was already right) |

**What stays literal, and why.** `MainWindow.xaml`'s `SearchPill` had hand-computed
`CornerRadius="19"` (= 38/2), which is the *correct* value — swapping it for `LwRPill` would
regress a 360 px pill into a lens. M6 moved it onto `LwRFullFromHeight` instead of leaving the
literal: same 19 px (verified pixel-identical over a 4560-point probe of the header band), but
self-documenting and it survives a height change. `MediaTemplates.xaml`'s 22-sq state badges and
the circular avatars (`ItemDetailView` 40-sq cast, `MainWindow` 26-sq profile) keep their
literals because they are square, where 999 and `height/2` agree. The 26 × 16 language badge
keeps radius 3, `CheckDropdown`'s 16-sq check-square keeps radius 4, and `ItemDetailView`'s
rating box keeps radius 4 and its critic-logo box radius 3: all are smaller than
`LwR1` (8) is wide, so any ladder step swallows them — commented in place as deliberate. And
`LwSlider`'s track
radius `2` and fill `2,0,0,2` stay literal: `2` is already the exact half-height stadium for a
4 px track, and the fill's asymmetry (square right end, butting the thumb) is not expressible as
a scalar token — while `LwRFull` would have turned a ~1780 px seek track into one long lens.

**Radii built in C# (Phase 10 M7).** Code cannot use `StaticResource`, so every constructed
`CornerRadius` resolves the ladder by name — `(CornerRadius)FindResource("LwR1")` and friends:
the media-info popup and Home's customize popup (`LwR3`), the two critic-rating chips (`LwR1`),
and all three `SkeletonFactory` blocks (`LwR1`, which clamps to half-height on the 13 px and
10 px text-line bars so they render as stadiums — they had been hard-coded 3 and read as sharp
rectangles under a rounded poster block, while also drifting from `LwSkeletonBlock`, the style
those very Borders carry).

Two real defects hid in that code-behind layer precisely because the XAML sweeps could not see
it, and both were found by screenshotting the running app rather than by reading:

- **The detail gem pills were ellipses.** `AddGemPill` built `CornerRadius(999)` on a `Height=22`
  Border ~70 px wide — the exact lens M5 diagnosed, still shipping. Note that the planned
  disposition (retarget to `LwRPill`) would have changed nothing, since `LwRPill` *is* 999. They
  now bind through `LwRFullFromHeight` like the rest of the pill family; bound rather than
  hard-coded to 11 so a change to `Height` cannot silently reintroduce the lens.
- **The critic logos rendered square.** `LogoChip`'s 16 px logo box used `CornerRadius(3)` plus
  `ClipToBounds="True"`, so the IMDb and Rotten Tomatoes marks had hard 90° corners while the
  method's own summary called the logo "rounded 16px" — `ClipToBounds` does not round-clip, the
  same finding M6 made on card artwork. Fixed the same way: `RoundedClip` on a `Grid` wrapper,
  never on the `Image`.

**Timing caveat.** The binding reads `ActualHeight`, which is 0 until the first layout pass, so
the converter returns `CornerRadius(0)` for that one pass and the corner resolves on the next.
No flash is observable in practice (it happens before the element is composited), but it is why
the converter guards against NaN/∞/≤0 rather than dividing blindly.

**Second WPF foot-gun, same family: `ClipToBounds` does not round-clip.** A `Border` with both
`CornerRadius` and `ClipToBounds="True"` clips its children to the Border's **rectangular**
bounds, not to its rounded geometry. So a rounded Border wrapping an `Image` shows the image's
square corners through the rounding — measured on `PosterCardTemplate` with a bright poster: a
perfect 90° corner, zero arc pixels, and it had been that way since Phase 6 (the radius only ever
showed on the faint hairline and on image-less cards). Rounding a *child* requires an explicit
rounded `Clip` geometry or an `OpacityMask` on the host; there is no `CornerRadius` shortcut.

**Phase 10 M6 fixed it with `Theme/RoundedClip.cs`** — an attached behaviour,
`theme:RoundedClip.Radius="<inner radius>"`, that keeps a `RectangleGeometry` with equal
`RadiusX`/`RadiusY` on the element's `Clip`, rebuilt on every `SizeChanged` (cards, thumbs and
hero posters are all data-templated and sized by their parents, so a static geometry cannot
work). It replaces `ClipToBounds` outright: the rounded clip also does that property's
rectangular job of containing `UniformToFill` overflow. Measured on the same bright poster, the
first-column inset per row from the card's true corner:

| | dy 0 → 17 |
|---|---|
| before (HEAD) | `1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,1,0,…` — flat at the hairline, artwork square behind it |
| after (M6) | `13,11,9,8,6,5,4,3,3,2,2,2,1,1,1,1,1,0` — a real quadrant, reaching 0 at dy 17 |

17 device px at this host's 150 % scale is 11 DIP, i.e. exactly the inner radius asked for
(`LwR2` 12 − the 1 px hairline), so the artwork's arc sits concentric inside the stroke's.

**Third foot-gun, found while fixing the second: attach the clip to a CONTAINER, never to the
`Image`.** `Image.ArrangeOverride` returns the *stretch-computed content size*, not the arrange
slot, so an `Image` with `Stretch="UniformToFill"` whose source aspect differs from its slot has
an `ActualWidth`/`ActualHeight` **larger than the slot**. A clip built from those numbers is the
overflowing box itself and therefore trims nothing — and with `ClipToBounds` now removed, the
artwork escapes the Border entirely. Measured on the Downloads row thumb (a 2:3 poster in a
45 × 66 frame): square corners *and* the poster bleeding past the card. `Grid` is safe because
`Grid.ArrangeOverride` always returns `finalSize`, so its size **is** the slot; all seven
artwork sites therefore carry the attached property on a `Grid`. The trap hides well — a site
whose artwork happens to match its slot's aspect (a 2:3 poster in a 2:3 frame, a square headshot
in a square avatar) clips correctly by luck, which is why four of the seven looked fine.

The seven artwork sites and their inner radii: `PosterCardTemplate` and `EpisodeCardTemplate`
(11 = `LwR2` − 1 px hairline), the detail episode-row thumb (8, no stroke), the detail hero
poster (11), the cast avatar (19 = 20 − 1, which is what finally made the "circular avatar"
circular instead of a square photo behind a round hairline), the Downloads row thumb (8) and the
Up-Next thumb (8). `LibraryCardTemplate` deliberately has none: it holds no artwork, and its
glyph's `LwGlowHover` is meant to bleed past the card edge.
- `Icons.xaml` — **the app-wide icon source (2026-07-13)**. Icons are Google's Material Icons
  (Rounded style), bundled as the static OTF `Theme/Fonts/MaterialIconsRound-Regular.otf`
  (Apache 2.0, ~400 KB; `MaterialIconsRound.codepoints` ships beside it as the name→hex map)
  and referenced by codepoint via `LwFontIcon` + `<sys:String>` resources (`IconHome`,
  `IconSearch`, `IconPlay`, `IconFavorite`, …). Glyphs inherit the control's `Foreground`, so
  they theme like text. Adding an icon: find the name at <https://fonts.google.com/icons>
  (style = Rounded), look its codepoint up in the `.codepoints` file, add a `sys:String` row,
  and set `FontFamily="{StaticResource LwFontIcon}"` + the string as `Text`/`Content`. The
  static Rounded OTF is used rather than the 15 MB variable Material Symbols because WPF renders
  variable fonts only at their default instance; fill vs. outline is chosen per glyph
  (`favorite` vs `favorite_border`). This replaced the earlier hand-drawn `Path`/`StreamGeometry`
  glyphs and stray unicode symbols (`←`, `⏸`, `✓`, `♥`, …) app-wide — nav rail, browse header
  search/back, dropdown chevrons, sort-order toggle, card watched badges, detail
  favorite/watched/more/play, and the whole player OSD transport bar. Exception: the taskbar
  thumb-button icons stay `DrawingImage` geometry (`ThumbButtonInfo.ImageSource` needs an
  `ImageSource`, not a font glyph).
- `Controls.xaml` — keyed component styles with rest/hover/pressed/focused/disabled states:
  `LwButtonPrimary/Secondary/Ghost/Destructive`, `LwIconButton` (42-sq glass icon button —
  detail favorite/watched/more), `LwToggleSwitch` (CheckBox), `LwChip`, `LwSlider` (garnet
  played-fill + infused thumb), `LwFlyoutBorder` + `LwFlyoutListItem` (popup chrome),
  `LwNavButton`, `LwNavRailItem`/
  `LwNavRailButton` (left rail), `LwContextMenu`/`LwMenuItem`/`LwMenuSeparator`,
  `LwTextField`/`LwSearchField`/`LwPasswordField`/`LwLinkButton`, `LwDropdown`/
  `LwComboBoxItem`/`LwIconToggle` (sort/filter + settings controls), the media card
  templates + `LwMediaCardItem`/`LwMediaGridItem` (`Views/MediaTemplates.xaml`), the OSD set
  `LwOsdButton`/`LwOsdPlayButton`/`LwOsdGemToggle`/`LwOsdPillButton`/`LwOsdFlyout`, and
  `LwToolTip`. Focus is an infused-light border + glow shadow, never a dotted rectangle.
  Phase 10 M5 deleted `LwCaptionButton`/`LwCaptionCloseButton` from this set: they had zero
  references anywhere (the window frame is deliberately OS-drawn, per the Phase 6 plan), and a
  shape sweep that "updates" dead styles only manufactures review surface.

Everything is opt-in (keyed, no implicit styles), so merging the dictionaries changed no
existing screen. The Phase 6 re-skin applied the system across the whole app, screen by
screen: app shell + left icon nav rail (P1); item detail S4 (P2 — backdrop dissolve,
Marcellus title, media gem pills, inline season list, circular cast grid); Home S2 (P3 —
card rails with progress/watched badges, Marcellus headers, right edge fade); Library grid
S3 (P4 — styled sort/filter dropdowns, mono count, A-Z rail); Connect/Login S1 (P5 — woven
crosshatch ornament + hero glow, garnet gem mark, Marcellus wordmark, Quick Connect); Player
OSD S5 (P6 — low-luminance storm-0 scrims, garnet seek + glow handle, RTX gem pills, kept in
the overlay window per the load-bearing rule); Settings S6 (P7 — sectioned storm-2 row cards
with right-aligned toggles/dropdowns/fields). No functional regression — the full live suite
(mini-player, detail, detail-links, SMTC, search, filters, plus per-screen checks) stayed
green through the re-skin (P8).
