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
  with a notice instead of stalling. Volume normalization is switchable from the player too
- Playback speed control (0.5x–2x), mute, and a brief volume indicator on any change
- Video buffer size in Settings: **Recommended** by default, with **Less memory** and
  **More read-ahead** presets and a custom size under Advanced

RTX VSR and RTX Video HDR apply to SDR content only (an NVIDIA limitation). On HDR or Dolby Vision
sources the toggles grey out with the reason on hover, the filters are suppressed, and mpv's own
tone mapping takes over. Without an RTX GPU the toggles are disabled outright.

### Jellyfin integration

- Login including Quick Connect, with DPAPI-persisted tokens and auto-reconnect
- Multiple server/user profiles with a `Ctrl+U` switcher. Sessions stay warm, so switching back is
  instant and restores that profile's exact browse state; playback and downloads continue under the
  profile that started them
- Watch-state reporting, favorites, and mark-watched toggles that update the card you came from
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
- Server-wide quick search plus advanced multi-filter search (type, genre, year, watched, and a
  person type-ahead) with type-grouped result sections and removable seed chips; cast, crew and
  genre labels deep-link into a pre-filtered search
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

### Player controls and shortcuts

- Transparent overlay OSD over the mpv window, with a now-playing title, release-date badge and
  status badges (RTX HDR / HDR, RTX VSR, source resolution) shown only while active
- Configurable jump forward/back (10 s by default) plus a 30 s seek on `Ctrl+Arrow`; click the
  on-screen time to type a position
- Track selection during playback for video, audio and subtitles, with preferred-language auto-select
  (bibliographic codes like `ger`/`deu` treated as one language), external/sidecar subtitles, and
  language badges on track rows
- Auto-play next episode with an Up Next card and countdown that cancels when you seek back out of
  the credits and pauses when playback does
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
  drift from the app
- The window goes down to 700x480 without losing anything: the track pickers fold into one Tracks
  flyout, the detail action row wraps, search filters move behind a More filters disclosure, and the
  A–Z rail becomes a dropdown

### Downloads and offline

- Download movies, episodes, or whole seasons and series at original quality or a transcoded
  1080p/720p/480p tier, with size estimates
- Pause, resume and cancel, with HTTP-Range resume and recovery across restarts
- A Downloads screen behind its own nav-rail entry and downloaded badges on cards
- Local-first playback: a downloaded item plays from disk with no server round-trip

### Updates, diagnostics, and log privacy

- Self-updater reading GitHub Releases, with an immediate **Settings → Updates → Check for updates**
  action plus startup and six-hour checks, explicit status, and three policies: manual install
  (default), automatic download, or verified silent installation when you normally exit. Staging is
  resumable and replaces its metadata atomically
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
- Disk-backed image and browse caches with a configurable item cap in Settings → Storage, plus
  window position and size persistence

## Requirements

- Windows 10/11 (x64)
- A Jellyfin server (see [Status](#status) for tested versions)
- `libmpv-2.dll` (mpv >= 0.40 for RTX Video HDR) — bundled in the installer; for a source build,
  place it in `native/win-x64/` (gitignored; copied next to the exe at build time) or anywhere on
  `PATH`
- For RTX features: an NVIDIA RTX GPU (20-series or newer) with RTX Video enabled in the NVIDIA App
- To build: the .NET 10 SDK

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

## Keyboard shortcuts

Every player key below is rebindable in Settings → Shortcuts. Rebinding *adds* a key rather than
moving one, so `F11` keeps working regardless. Press `?` anywhere, or the keyboard button in the
player OSD, for the live list.

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

[TECHNICAL.md](TECHNICAL.md) has the full picture: the mpv integration, the negotiation and
transcode fallback, the caching layers, the download manager, the logging contract, and a design
decisions log explaining why each of them looks the way it does.

One rule is load-bearing and everything else is arranged around it:

> mpv **owns its rendering window** (child HWND via `wid`, `vo=gpu-next`, D3D11). Video is never
> composited through WPF (`D3DImage`, render-API-to-texture, and so on) — that breaks RTX Video HDR
> and HDR passthrough, which are core requirements. Playback controls live in a transparent overlay
> window, not inside the video HWND's rect.

## Status

Feature-complete for daily use and fully skinned in the "woven light" design system across every
screen. Runs against **Jellyfin 12.0** and **10.11**. The client uses only current authorization
forms, so 12.0 turning legacy authorization off does not affect it.

Known issues and the closed-bug index are in [BUGS.md](BUGS.md). Open work is tracked by the
maintainer outside the repository.

## Design

[design/](design/) holds the "woven light" design system: the design prompt, the generated spec
(tokens, component states, and full-screen mockups for all six screens), and the Advanced Search
redesign package.

## Contributing

Issues and pull requests are welcome. Before opening a PR:

- `dotnet build -warnaserror` must pass. Warnings are errors in this project
- If the change is significant, update [README.md](README.md) and [TECHNICAL.md](TECHNICAL.md)
- Respect the load-bearing rule above — a change that routes video through WPF will not be merged

## License

Zero-Clause BSD (0BSD). See [LICENSE](LICENSE).
