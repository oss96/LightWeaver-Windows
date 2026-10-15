# LightWeaver

A native Windows Jellyfin client built for playback quality and responsiveness. It pairs a WPF
(Fluent, dark) shell with an embedded **mpv** (libmpv) player that owns its own D3D11 rendering
window — which is what makes NVIDIA RTX Video Super Resolution, RTX Video HDR and true HDR
passthrough possible, none of which survive being composited through a UI framework. LightWeaver is
the Windows member of a per-platform client family (LightWeaver-Windows, -MacOS, -Android, -iOS —
one repo each) rather than one cross-platform build.

<!-- screenshots -->

## Features

### Playback and video quality

- Direct play through embedded mpv (`vo=gpu-next`, `hwdec=d3d11va`) with resume
- **NVIDIA RTX Video Super Resolution** (`d3d11vpp` scaling) and **RTX Video HDR**
  (`nvidia-true-hdr`), toggled in Settings
- Native HDR passthrough on an mpv-owned HDR-capable swapchain
- Hardware decoding via `d3d11va`
- Automatic HLS transcoding fallback: a failed direct play retries once through the server
  transcoder, with a bitrate cap in Settings
- Audio output device picker with bitstream passthrough — the Settings row names which formats the
  selected output actually accepts, and a device that cannot take them falls back to decoded audio
  with a notice instead of stalling. Volume normalization is switchable from the player too.
  Playback appears as LightWeaver in the Windows volume mixer
- Playback speed control (0.5x–2x), mute, and a brief volume indicator on any change
- Video buffer size in Settings: **Recommended** by default, with **Less memory** and
  **More read-ahead** presets and a custom size under Advanced
- Settings show **Unlimited**, bitrate presets and **Custom** for the streaming cap, select
  **Sans-serif (default)** for the subtitle font, and display the effective download folder.
  Redundant helper text is removed; the logging explanation follows the current toggle state.
- Subtitle text and border colors use named presets with visible swatches and a custom hex
  color option. Border size, shadow offset and vertical position offer presets and custom values.
- RTX HDR peak brightness stays manually adjustable in nits. **Use display value** copies the
  current screen's Windows-reported HDR peak when available; it keeps the saved value if detection
  fails or HDR is off. The reported value comes from display metadata or an override, not a measurement.
- Unavailable Settings controls are disabled with a reason on hover: RTX options require a
  supported GPU, RTX HDR also requires HDR enabled on the current display, and passthrough
  requires a compatible output. The auto-play countdown is disabled while auto-play is off.
  Saved preferences are retained, and display availability refreshes while Settings is open.
- **Alternate versions**: when an item has more than one media source — a 4K remux beside a 1080p
  encode, on movies and, since Jellyfin 12, on episodes — a Versions button on the detail view
  picks which one to play, and a VERSIONS group in the player's track flyout switches between them
  mid-playback without losing your position, pause state, or audio and subtitle choices. Unnamed
  sources are labelled by resolution, codec and container, with size or bitrate to tell two
  same-named versions apart. The choice is remembered per item, and is ignored if the file it
  named is gone. A downloaded copy still wins: it is one specific version, and streaming another
  over the network would defeat the point of having downloaded it

RTX VSR and RTX Video HDR apply to SDR content only (an NVIDIA limitation). On HDR or Dolby Vision
sources the toggles grey out with the reason on hover, the filters are suppressed, and mpv's own
tone mapping takes over. Without an RTX GPU the toggles are disabled outright.

During a server item, use the player's Quality button beside the audio and subtitle controls.
Its own flyout offers Default, Unlimited, 20, 10, 4 or 1 Mbps, and Force transcode. The Info panel
contains playback statistics and media details.
Default uses the bitrate saved in Settings. Choices apply to the current item only and reset on
the next one, and nothing here writes Settings. The player negotiates the replacement before it
touches the current stream, so a failed negotiation keeps playing what you were watching;
position, pause state and your audio and subtitle choices carry across, including burned-in
subtitles and an explicit Subtitles off. The controls are visible but disabled while a file
loads, and absent for local files and direct URLs.

### Jellyfin integration

- **SyncPlay**: create or join a watch group from the browse bar or player, see its participants,
  and share playback and the queue. The SyncPlay icon opens an integrated dropdown with group
  controls; the status to its left shows the current group state and participant count.
  Pause, seek, stop, next/previous, queue selection and removal follow the server's group updates.
  Leaving a group keeps the current video playing locally.
  Group playback uses seek corrections to stay in sync; speed and frame stepping are unavailable.
  Leave the group before changing playback quality or the media version.
  In-progress downloads use the server stream in a group so every member can seek freely.
  Live session messages accept Jellyfin's compact and dashed identifiers for group and queue updates.
  The participant list is checked against the server after every join and leave, so one of two
  devices on the same account leaving does not empty it on the other.
- Login including Quick Connect, with DPAPI-persisted tokens and auto-reconnect. The password
  field has a show/hide button, and leaving the sign-in screen masks it again
- Multiple server/user profiles with a `Ctrl+U` switcher, also available on the login screen when
  a saved user exists. Sessions stay warm, so switching back is
  instant and restores that profile's exact browse state; playback and downloads continue under the
  profile that started them
- Watch-state reporting, favorites, and mark-watched toggles that update the card you came from
- **Live session**: LightWeaver appears as a controllable session in the Jellyfin dashboard and is
  a valid **Play on** target from the web and mobile apps. Transport (play, pause, stop, seek,
  next/previous, skip), volume, mute and fullscreen are all driven from the remote, messages sent
  to the session appear as a toast, and a remote play can start an item, queue it next, or add it
  to the end. A remote play brings the window to the front — a client that starts playing while
  minimised is indistinguishable from one that ignored you. Two limits worth knowing: a queue
  command is accepted only by the profile that owns the current playback, and a track pick made
  from the dashboard is declined while a non-default **version** is playing, because the stream
  numbering the dashboard shows belongs to a different file than the one on screen
- **Server-pushed updates**: the app holds the Jellyfin session WebSocket, so library changes and
  watched/favourite toggles made on another device land without waiting for a poll. A delete on the
  server drops the affected folders from the browse cache instead of leaving them on screen
- Media segments for intro/credits skipping (native segments, the IntroSkipper plugin, or a
  chapter-name fallback), trickplay seek previews, and chapter markers
- External critic scores on the detail view (IMDb / Rotten Tomatoes / Metacritic) via OMDB, with a
  masked API-key setting and a 7-day disk cache
- **Stream from URL** (`Ctrl+L`, or the sidebar entry): paste a direct HTTP(S) media link and play
  it with the normal player controls. No URL history is kept, and queries and fragments are excluded
  from titles and diagnostics

### Browsing and library

- Home rows with a Customize flyout that reorders or disables rails per profile — a disabled rail is
  never fetched
- Library grids with sorting, watched and genre filters, an A–Z letter rail, collection (BoxSet)
  browsing and per-library genre grids
- Playlist detail and Play all keep the server's entry order and repeated items, including
  playlists longer than 500 entries. Watched changes update every occurrence of the item
- Server-wide quick search plus advanced multi-filter search (type, genre, year, watched, and a
  person type-ahead with mouse and keyboard selection) with type-grouped result sections and
  removable seed chips; cast, crew and genre labels deep-link into a pre-filtered search
- Item detail with backdrop, rating and community score, 4K/HDR10/audio badges derived from real
  media info, Resume / From-start, an inline season episode list, a circular cast grid, More-like-this,
  and a Watch Trailer button
- Clickable series/season links on episode details; single-season shows open straight to the episode
  list
- A hover action bar on every card: direct play, mark watched, media info, and a self-authenticating
  Copy stream URL
- Clickable Home rail headers that open a full "see all" listing with infinite scrolling
- Browser-style Back/Forward history (on-screen buttons or the mouse side buttons) that restores
  scroll and filter state
- Cache-first browsing: whole folders are cached with background prefetch, sorting and filtering are
  local with no round-trips, a background identity sweep diffs in changes, and going offline falls
  back to cached results with an explicit timestamp
- A consistent loading language: shape-matched skeletons on first paint, throbbing dots while a
  fetch runs behind content that is already on screen, and everything but the first paint held back
  250 ms so a fast answer shows no indicator at all
- App-wide zoom from 50% to 200% (`Ctrl++` / `Ctrl+-`, `Ctrl+Wheel` or a touchpad pinch, `Ctrl+0`
  to reset). The whole interface scales, including cards, artwork and the player controls, not just
  the text, and grids re-flow to the new size. A short readout shows the level, which is remembered
  across launches. The video picture and the mini-player's compact controls are not zoomed
- Zoom stops at the largest level that still gives the interface as much room as the app's
  smallest window has at 100%: 125% on a 1024x768 screen or 1920x1080 at 150% scaling,
  150% on 1366x768 at 100%, 175% on 1920x1080 at 125% and 200% on 1920x1080 at 100%. The readout
  says "Screen limit" when a step in is refused there. While limited, the window cannot be resized
  smaller than the zoomed layout needs. Settings > Zoom > *Limit zoom to the screen size* turns
  the limit off after a warning that controls can then be cut off or hidden

### Player controls and shortcuts

- Transparent overlay OSD over the mpv window, with a now-playing title, release-date badge and
  status badges (RTX HDR / HDR, RTX VSR, source resolution) shown only while active
- Configurable jump forward/back (10 s by default) plus a 30 s seek on `Ctrl+Arrow`; click the
  on-screen time to type a position
- Track selection during playback for video, audio and subtitles, with preferred-language auto-select
  (bibliographic codes like `ger`/`deu` treated as one language), external/sidecar subtitles, and
  language badges on track rows. Your subtitle pick carries into the next file by description rather
  than track number, down to the variant: choosing "English (SDH)" keeps SDH on the next episode
  rather than plain English, as long as that episode names the track the same way
- Auto-play next episode with an Up Next card and countdown that cancels when you seek back out of
  the credits and pauses when playback does. Settings has the auto-play toggle and the countdown,
  from 3 to 120 seconds, default 10; with auto-play off the card still offers Play now. The card
  appears at the episode's credits marker. Without one it appears a set time before the end:
  15, 30 or 60 seconds or a custom 5 to 300, default 30, chosen in Settings. The Up Next pill on
  series and season pages and the Up Next button on episode pages override that time for the whole
  show (Global, 15, 30, 60 or custom). Both open the same flyout and change one value, saved
  locally per server and Jellyfin user. If the episode ends first, the remaining countdown keeps
  running on the last frame; Dismiss stops it there.
- A playback queue: Play all / Shuffle on seasons, series and server playlists, `N`/`P` keys, and an
  overlay queue popup with jump and remove
- Dedicated next/previous-episode buttons that step by series order across season boundaries
- Subtitle styling (font, size, colors, border, shadow, base position). Subtitles lift above the
  control bar while it is visible and sit in the letterbox band on wider-than-window films, falling
  back over the picture when the band is too small
- Fullscreen (`F11`, the OSD button, or a double-click), and a mini-player (`Ctrl+M`) — a compact
  always-on-top window with drag-to-move and a 16:9-locked resize grip from 320x180 to 960x540
- Windows media integration: media keys and the volume-overlay card via SMTC, plus taskbar thumbnail
  buttons. Next/Previous are remappable to chapter stepping or +/-N-second seeks
- An info panel with live playback stats and server media metadata
- Every player key is rebindable in Settings → Shortcuts. Press `?` anywhere for the full list — the
  panel is generated from the same definitions the handlers use, so it shows your rebinds and cannot
  drift from the app. Binding a key another action already holds moves it, even when the older
  binding was saved under a different spelling of the same key (`Oem6` for `]`)
- The window goes down to 700x480 without losing anything: the track pickers fold into one Tracks
  flyout, the detail action row wraps, search filters move behind a More filters disclosure, and the
  A–Z rail becomes a dropdown

### Downloads and offline

- Choose named audio and subtitle tracks before downloading, or keep all languages. The picker
  starts with your preferred or default audio track and your preferred subtitles or None.
  Quality choices remain available; incompatible audio and subtitle options are disabled in
  their menus. Changing quality replaces unavailable choices with default audio or no subtitles.
  All audio languages uses Original quality because Jellyfin's smaller downloads contain one
  audio track.
  Original keeps all embedded tracks; individual choices set the initial playback tracks.
  Separate subtitles are saved beside the video for offline playback. Embedded graphical
  subtitles also require Original quality; unsupported separate formats are identified before queueing
- Download selection, progress, speed and readiness text follow the current UI language, including
  German and Arabic. Language changes keep quality and track selections intact.
- Each download shows its language choices. A completed video remains playable if subtitle saving
  fails, and Retry fetches the missing subtitles without downloading the video again. An item is
  ready offline only after its selected subtitles finish. Episode batches use each episode's
  preferred or default audio until you explicitly select a named track. They retain the chosen
  quality and report episodes with incompatible choices instead of upgrading to Original.
  Episode batches resolve each episode's
  own language tracks and report episodes that lack the requested language instead of substituting it
- Live per-download progress, transfer speed and remaining time, with approximate labels for
  transcoded size estimates and visible activity when the total size is unknown
- Downloaded-duration estimates and a visible Play button that enables after the startup buffer
  arrives. Partial playback starts from the beginning and can buffer when it catches up or seeks
  beyond downloaded content; completed rows show full offline availability
- The Downloads rail badge counts active transfers in both expanded and collapsed layouts;
  the page summary separately counts downloading, queued and ready-offline items
- Playback from Downloads shows "Starting playback" while refreshing Jellyfin metadata for up to
  750 ms. Finished videos use the current resume position when it arrives in time, or saved metadata
  when the server is unavailable or slow. Episode continuation loads in the background after startup

- Download movies, episodes, or whole seasons and series at original quality or a transcoded
  1080p/720p/480p tier, with size estimates
- Pause, resume and cancel, with HTTP-Range resume and recovery across restarts. Downloading an
  item again whose file is still on disk (after **Clear completed**, say) recognises a finished
  original-quality file and marks it complete, and restarts any other leftover from the start
- A Downloads screen behind its own nav-rail entry and downloaded badges on cards
- Local-first playback: a downloaded item plays its video from disk, with saved metadata available offline
- Play a download while it is still downloading, from the bytes already on disk instead of pulling
  the same file a second time over the network. A completed download still wins; a running one is
  offered once a few MiB have landed and the part you are resuming into has arrived, and a stream
  that cannot continue — a format with no playable prefix such as a non-faststart MP4, or a
  download you pause mid-film — falls back at the position you were at: to the finished file on
  disk when the download completed under you, otherwise to the normal server stream. Started from
  the Downloads screen with no server reachable there is nothing to fall back to, so it says so
  rather than stopping on the last frame

### Updates, diagnostics, and log privacy

- Self-updater reading GitHub Releases, with an immediate **Settings → Updates → Check for updates**
  action plus startup and six-hour checks, explicit status, and three policies: manual install
  (default), automatic download, or verified silent installation when you normally exit. Choosing
  **Install now** under that last policy installs silently and starts the new version afterwards.
  Staging is resumable and replaces its metadata atomically
- Diagnostics logging: a crash file for any unhandled exception, and a playback file — container,
  codecs, decoder, RTX and direct-play/transcode state — whenever mpv fails a file. Settings →
  Diagnostics shows the verbose state and log-folder size and can export the logs as one ZIP, open
  the folder, or clear them
- Logs in `%LOCALAPPDATA%\LightWeaver\logs` redact tokens, authorization values and your user name,
  use bounded retention, and are not written at all on a healthy non-verbose run
- Verbose records are deliberately structural — navigation and action IDs, timing, outcomes and
  counts. They exclude titles, search and person text, server details, paths, free-form settings and
  raw audio-device IDs. Identities are reduced to per-run salted hashes (`key_hash=`, `session=`,
  `device_hash=`), so a shared log cannot be matched back to catalogue ids. The updater narrates its
  own lifecycle the same way, while the release feed, download URLs, package hashes, staging paths
  and the installer's signer identity never appear
- Playing a still-downloading item opens one listening socket, and only then: it binds to loopback
  (`127.0.0.1`) on an ephemeral port, serves nothing but that download's own partial file, and each
  download is reachable only through a fresh unguessable token that is retired when the playback
  ends. The socket closes again as soon as nothing is streaming
- Disk-backed image and browse caches with a configurable item cap in Settings → Storage, plus
  window position and size persistence. The cache sizes are re-read each time you return to
  Settings
- Settings save as you change them. Sliders apply live but write the file once the gesture
  settles, when you leave Settings, or when the window closes. If a save fails, a banner at the top
  of Settings says so and why: a file that could not be written, or an existing file LightWeaver is
  not allowed to read, which it then leaves untouched rather than overwrite your settings

## Requirements

- Windows 10/11 (x64)
- A Jellyfin server (see [Status](#status) for tested versions)
- `libmpv-2.dll` (mpv >= 0.40 for RTX Video HDR) — bundled in the installer; for a source build,
  place it in `native/win-x64/` (gitignored; copied next to the exe at build time) or anywhere on
  `PATH`
- For RTX features: an NVIDIA RTX GPU (20-series or newer) with RTX Video enabled in the NVIDIA App
- To build: the .NET 10.0.300 SDK or a later 10.0.3xx patch, as selected by `global.json`

## Install

Download `LightWeaver-<version>-setup.exe` from the
[Releases](https://github.com/oss96/LightWeaver-Windows/releases) page and run it.

The installer is per-user — no admin prompt, installed to `%LOCALAPPDATA%\Programs\LightWeaver` —
and creates a Start menu entry (a desktop icon is optional). It updates in place: running a newer
setup over an existing install keeps your credentials and all preferences, as does uninstalling.

The app can also update itself — see Settings → Updates.

## Build from source

```
dotnet build                                               # compile
dotnet run --project src/LightWeaver                       # start empty
dotnet run --project src/LightWeaver -- "D:\movie.mkv"     # play a file immediately
```

Debug builds run in an isolated app-data environment (`%LOCALAPPDATA%\LightWeaver-Debug`) and title
their window "LightWeaver (Debug)", so running the dev build never touches the installed build's
login, settings or watched state. This is selected automatically via `#if DEBUG`.

## Dependency maintenance

NuGet versions live in `src/LightWeaver/LightWeaver.csproj`. Alongside `Jellyfin.Sdk`, explicit
references keep all six Kiota runtime packages on 1.22.2 and `Std.UriTemplate` on 2.0.12 instead
of taking the older transitive versions from the Jellyfin SDK. Renovate can propose updates for
each reference. It groups Kiota updates and keeps them below 2.0 until a separate Jellyfin
compatibility migration. Updates require review and merge, then a new app release.
The current graph has eight explicit references and two indirect packages brought in by
Kiota: `Microsoft.Extensions.DependencyInjection.Abstractions` and
`Microsoft.IO.RecyclableMemoryStream`.

`global.json` selects .NET SDK 10.0.300 and permits later patches in the same feature band,
with preview SDKs disabled. Renovate tracks that SDK version and keeps proposals on .NET 10.
Self-contained releases bundle the .NET runtime, so runtime fixes reach installed users when
the app is rebuilt and released.

The native library is supplied locally and stays out of Git. `dependencies/libmpv.json`
records the current DLL's file version, SHA-256 and upstream source commit. The checksum was
recorded from the existing maintainer-supplied DLL; it is not an upstream binary attestation
or a download URL. To check your copy without running it:

```powershell
.\tools\Verify-LibMpv.ps1
.\tests\Verify-LibMpv.Tests.ps1   # positive and rejection checks; requires the recorded DLL
```

Publishing runs this check before building and again on the copied DLL before signing or
packaging. To update libmpv, obtain and verify a trusted Windows x64 build, replace the local
DLL, update its version, hash and source commit together in the manifest, and test playback
before releasing. Source builds can still use another local build or find libmpv on `PATH`;
release packaging requires the recorded build. Bundled fonts and build-machine tools such
as Inno Setup and GitHub CLI continue to be updated manually.

To inspect the resolved packages and check for updates or known vulnerabilities:

```powershell
dotnet package list --project src/LightWeaver/LightWeaver.csproj --include-transitive
dotnet package list --project src/LightWeaver/LightWeaver.csproj --include-transitive --outdated
dotnet package list --project src/LightWeaver/LightWeaver.csproj --include-transitive --vulnerable
```

## UI languages and translation editor

UI text lives in [`localization/catalog.json`](localization/catalog.json). English is the
source language. Language files in `localization/locales/` are embedded during the build;
choose an available UI language in Settings to apply it immediately. The current view, inputs
and playback stay in place. System default
uses the Windows UI language when a matching pack is available. Missing, outdated or invalid
translations fall back to English.
The German (`de`) and Arabic (`ar`) packs each cover all 974 current catalog entries, including
playback controls, settings, downloads, SyncPlay and error messages. Select Deutsch or العربية
to use them.
Standard library names such as Movies and TV Shows follow the interface language immediately.
Under Settings > Library names, enter custom names for the current language and choose Save names.
Leaving a field blank uses the translated standard name or the original custom name. Names are
saved per server and library; Jellyfin and other clients keep their original names.
Long translated headings wrap. On smaller windows, the SyncPlay panel scrolls while Close stays
visible.

Run the local editor from this checkout:

```powershell
dotnet run --project tools/TranslationEditor
```

Open [the translation editor](http://127.0.0.1:5386/). Its interface supports English, German and
Arabic, including right-to-left layout and Arabic editing. Use the interface-language selector
independently of the translation-column filter. Add a language column, enter translations,
then save. Each row includes its English source, key and source files. The editor checks format
placeholders and warns when the English source has changed. A save updates only edited cells;
filters do not remove other translations. Rebuild LightWeaver after editing a language file.

Filters are shareable through the URL. `q` searches text and keys, `source` filters source files,
`status` filters translation status, and `lang` chooses the language for that status. `keys`
accepts comma-separated string keys. Use `changed=working-tree` for changes against HEAD, or
`changed=<full-base-commit>` for a stable link to strings created or modified during a change.
The filter reads Unicode Git content as UTF-8 when the editor runs in the background.
Source edits and translation edits both count. Start the editor before sharing a local link.

Localize new UI text in the same change. Keep keys stable when changing English text and update
existing translations. Protocol identifiers, persisted values, automation IDs and logs remain
invariant. See [TECHNICAL.md](TECHNICAL.md) for the catalog format and verification commands.

## Keyboard shortcuts

Every player key below is rebindable in Settings → Shortcuts. Rebinding *adds* a key rather than
moving one, so `F11` keeps working regardless. A key an app shortcut uses, such as `Ctrl+M`,
`Ctrl+Plus` or `Backspace`, cannot be given to a player action (`Space` and `F11` only to play /
pause and fullscreen): Settings keeps the old key and says which shortcut has it. Press `?`
anywhere, or the keyboard button in the player OSD, for the live list.

| Key | Action |
|---|---|
| `Space` | Play / pause |
| `Left` / `Right` | Seek 10 s (configurable) |
| `Ctrl+Left` / `Ctrl+Right` | Seek 30 s (configurable) |
| `.` / `,` | Next / previous frame |
| `Up` / `Down` | Volume |
| `M` | Mute |
| `+` / `-` | Playback speed |
| `A` / `S` / `V` | Cycle audio / subtitle / video track |
| `[` / `]` | Previous / next chapter |
| `C` | Skip intro or credits |
| `N` / `P` | Next / previous in queue |
| `F11`, double-click | Fullscreen |
| `Esc` | Leave fullscreen |
| `Ctrl+M` | Mini-player |
| `Ctrl+O` | Open file |
| `Ctrl+L` | Stream from URL |
| `Ctrl+U` | Switch profile |
| `Ctrl+,` | Settings |
| `Ctrl++` / `Ctrl+-`, `Ctrl+Wheel` | Zoom the interface in / out |
| `Ctrl+0` | Reset zoom to 100% |
| `Backspace` | Back (browsing) |
| `?` | Shortcut list |

Click the video to pause; drop a file on it to play. The mouse wheel scrolls wherever content
overflows, and `Shift+Wheel` scrolls a hovered row horizontally. `Win+Shift+Left/Right` moves the
whole player to the next monitor while the video has focus, and a maximized or fullscreen window
stays that way on the monitor you sent it to.

## Release build

```
.\publish.ps1     # dist/LightWeaver-<version>-win-x64.zip (self-contained, libmpv bundled)
.\installer.ps1   # dist/LightWeaver-<version>-setup.exe (Inno Setup; runs publish.ps1 first)
.\release.ps1     # installer.ps1 + a GitHub release with the setup.exe asset, published with `gh`
```

`installer.ps1` requires Inno Setup 6 (`winget install -e --id JRSoftware.InnoSetup`), and
`release.ps1` requires the [GitHub CLI](https://cli.github.com/) authenticated against the
repository.

Both build scripts Authenticode-sign what they produce **when signing is configured**, and print one
line and carry on when it is not — so a release is buildable on a machine with no certificate.
`publish.ps1` signs `LightWeaver.exe` and `LightWeaver.dll` before zipping, so the portable zip and
the installed copy carry the same signature; `installer.ps1` signs the setup exe, which is the
signature SmartScreen inspects at download. Everything is timestamped, so signatures outlive the
certificate. Setup, certificate options and costs: [docs/CODE-SIGNING.md](docs/CODE-SIGNING.md).

## Architecture

One rule is load-bearing and everything else is arranged around it:

> mpv **owns its rendering window** (child HWND via `wid`, `vo=gpu-next`, D3D11). Video is never
> composited through WPF (`D3DImage`, render-API-to-texture, and so on) — that breaks RTX Video HDR
> and HDR passthrough, which are core requirements. Playback controls live in a transparent overlay
> window, not inside the video HWND's rect.

## Status

Feature-complete for daily use and fully skinned in the "woven light" design system across every
screen. Runs against **Jellyfin 12.0** and **10.11**. The client uses only current authorization
forms, so 12.0 turning legacy authorization off does not affect it.
Authenticated API, session socket and media requests carry the current client version and device
identity alongside the token, so Jellyfin updates its device record after an app upgrade.

Known issues and open work are tracked by the maintainer outside the repository.

## Design

[design/](design/) holds the "woven light" design system: the design prompt, the generated spec
(tokens, component states, and full-screen mockups for all six screens), and the Advanced Search
redesign package. [design/app-icon.icon](design/app-icon.icon) is the app icon source: the
Gold-Violet "Shallan" seal as an Icon Composer bundle (SVG layers plus `icon.json`).
`src/LightWeaver/Assets/LightWeaver.ico` is rendered from it: a `#251B3E` rounded plate under the
gold layers, at 16–256 px. The exe, the windows, the taskbar and the installer all use that file.

## Contributing

Issues and pull requests are welcome. Before opening a PR:

- `dotnet build -warnaserror` must pass. Warnings are errors in this project
- If the change is significant, update [README.md](README.md)
- Respect the load-bearing rule above — a change that routes video through WPF will not be merged

## License

Zero-Clause BSD (0BSD). See [LICENSE](LICENSE).
