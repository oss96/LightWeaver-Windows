# LightWeaver

A native Windows Jellyfin client built for playback quality and responsiveness. Since 2.0 it pairs a
WinUI 3 shell with an embedded **mpv** (libmpv) player that renders and presents the video itself
with D3D11, into a swapchain it owns behind the controls — which is what makes NVIDIA RTX Video
Super Resolution, RTX Video HDR and true HDR passthrough possible, none of which survive frames
being routed through a UI framework. Versions 1.x were a WPF app. LightWeaver is
the Windows member of a per-platform client family (LightWeaver-Windows, -MacOS, -Android, -iOS —
one repo each) rather than one cross-platform build.

<!-- screenshots -->

## Features

### Playback and video quality

- Direct play through embedded mpv (`vo=gpu-next`, `hwdec=d3d11va`) with resume. Hardware decoding
  can be turned off in Settings › Video
- mpv renders into a composition swapchain behind the WinUI controls, configured from the
  display's own HDR peak, black level and primaries, so HDR sources reach an HDR display as HDR
- **NVIDIA RTX Video Super Resolution** and **RTX Video HDR**. Settings › Video holds their
  defaults and the RTX HDR peak brightness (400 to 2000 nits). The player bar's "RTX VSR" and
  "RTX HDR" toggles turn them on and off for the playing video and save the choice as the Settings
  default (the Settings rows and the toggles always agree). The toggles show only on an RTX GPU
  with an SDR source, are left out of the compact bar (detail preview, mini player) and have no
  shortcut
- A failed direct play retries once through the server's transcoder. Settings › Video caps the
  streaming bitrate (Unlimited, or 60 down to 2 Mbps) and sets the video buffer (75 to 1200 MB)
- Audio output device and bitstream passthrough in Settings › Audio; an output that cannot take
  passthrough falls back to decoded audio and says so. Volume normalization is a Settings switch;
  it stays off for a SyncPlay group's item, where its 16 s look-ahead would hold up every start
  and seek for the whole group
- Quality menus name each tier with its bitrate beside it (High · 1080p · 20 Mbps; As in
  Settings shows the Settings cap) and have an "Always transcode" switch; the player and the
  detail page offer the same tiers (As in Settings, Original, 20, 10, 4 and 1 Mbps). The
  player's quality menu lists an item's alternate versions; the detail page shows them in a
  drop-down
- Playback speed from 0.5x to 2x, in the speed flyout or with `+` / `-`

### Player

- **Preview and full screen**: Play on a detail page plays in the preview. The preview has the video's shape, decided when the page opens from the size the list it was opened from carried (an anamorphic DVD is 16:9 or 4:3 rather than its stored 1.25:1; a show, a season or a title without a known size is 16:9), and keeps it: a video of another shape plays letterboxed inside it. It is as large as fits beside the info panel while it and the "More like this" and "Cast and crew" rows, side by side below it, stay in the window; when the window is too short for that, the info panel takes the spare width. The rows never press the preview under 300 DIP tall; in a shorter window the page scrolls them into view. Before playback the preview carries the player's own bar: its seek bar sets where Play starts, back and forward move that start, a version drop-down (when the title has two or more versions) and the audio, subtitle and quality buttons set what plays, with the quality menu offering the player's tiers and "Always transcode", Info shows the media info and full screen plays straight into full screen. Full screen (the bar's button, `F` or a
  double-click on the video) grows it to fill the screen and back again, playing or paused as it
  was; the preview's "Auto full screen" switch opens every Play full screen. Esc leaves full screen;
  Back returns to the preview, paused. Full screen covers the whole monitor the window is on,
  edge to edge, without Windows' window animation, and the taskbar (an auto-hidden one too) stays
  under it, with the title, Back and the bar over the video; leaving it puts the window back in one
  step (a maximized window is maximized again and never passes through its restored size; a
  restored or snapped one returns to its exact rect), and
  the video then shrinks onto the preview. Either way the page is laid out for the new size
  before the window changes, so the old layout never shows in the new window. A click on the video pauses and resumes at once, and the
  player's shortcuts work whenever it is on screen. Audio and subtitles have a button each; where the bar is too narrow for both of its groups on one row (the preview in a small window) they fold into one Tracks button whose flyout lists audio and subtitles side by side. The player's flyouts open above the whole bar, so the seek bar and the times stay in view. Tracks that the file names only by a language code are listed by the language's name. Its tooltips open when you point at a control or reach it with the keyboard, not when the bar appears under a mouse that has not moved. Played
  from a card anywhere else (its play button or its menu) the player grows out of the card's
  artwork across the window in the same 180 ms. A new title never shows the previous one's last
  frame: until its first frame is on screen the card's artwork (on the detail page, the preview's
  backdrop) stands in, and the video then fades in over 120 ms. When the player's bar or the preview's bar runs out
  of room, its options move to a row above instead of being pushed out of reach. The seek preview
  keeps the thumbnails' own shape, as large as fits above the bar, and stays inside the player at
  its edges. The bar has a seek bar with chapters, buffer and trickplay previews, back/forward (Settings ›
  Playback), next, volume, and flyouts for audio and subtitles (with a 0.1 s subtitle offset),
  quality and version, speed, the queue and live stats, drawn as dim glass over the video; Skip intro, the Up Next card, keyboard
  shortcuts and an idle hide. The wheel over the player changes the volume; every volume or mute change shows the level
  in a readout at the top right for a moment, without bringing up the bar.
- **Type a time**: click the elapsed time and type where to go, as `ss`, `mm:ss` or `hh:mm:ss`
  (`83:45` is 83 minutes). Enter seeks, Esc or a click elsewhere cancels, and anything else
  seeks nowhere. While the field is open the keys type and no player shortcut runs.
- **Stays awake while playing**: while a video plays, Windows neither turns the screen off, starts
  the screen saver nor goes to sleep. Pausing, stopping, the end of the video and closing the
  player let it go again.
- **Subtitles load in the background**: the video starts without waiting for the server's
  external subtitle files. Until one arrives, the subtitle menu lists it as "Not available yet",
  and it becomes selectable as soon as it is loaded. One that fails to load, or an image subtitle
  (VobSub, PGS) that the server cannot convert, is listed as "Not available".
- **Subtitles carry over**: a subtitle picked in the player (or Off) is kept for the next
  episode and every later file in the session, found by its language, title and forced flag, so
  "English (SDH)" stays SDH rather than becoming the first English track. A file without it
  keeps its own choice. The detail page's subtitle pick still applies to the title it was made
  for, until you pick another in the player.
- Settings › Playback: skip intros and credits automatically, the back and forward jumps (5 to
  30 s back, 10 to 60 s forward), Up Next auto-play with a 3 to 120 s countdown, how long
  before the end the Up Next card appears in an episode without a credits marker (5 to 300 s; a
  show can have its own, from its detail page), the preferred
  audio and subtitle languages, and what the media keys' next and previous do (next title,
  chapter, or a 5 to 300 s seek). What plays shows in the Windows media overlay with its artwork,
  and the media keys play, pause, stop and step
- Settings › Subtitles styles subtitles with a live preview: font, size, five colours, border
  (none, thin, thick), shadow and vertical position. The subtitle flyout, `Z` and `X` shift
  subtitles by 0.1 s

### Jellyfin integration

- **Login**: type a host, IP or URL (https is tried first, then http, then port 8096) or pick a
  saved server, which signs in as its remembered user in one click. Then sign in with Quick
  Connect (a code you approve on a signed-in device; it renews itself before it expires) or a
  username and password, choosing from the server's public users. "Stay signed in" off forgets
  the sign-in when the app closes. Right-click a saved server to remove it. Each sign-in is its own
  device on the server, so the same account saved under a LAN and a public address stays signed in
  on both.
- **Profile menu** (the avatar): switch user on this server, switch server, Play a link… (Ctrl+L),
  Open file… (Ctrl+O), Settings (Ctrl+,) and Log out, which signs out of this server only and opens
  Login on it.
- **SyncPlay**: the bar's SyncPlay button lists the server's groups, creates one named after you,
  joins and leaves; the group's queue and commands drive the player, and joins and leaves show as
  toasts or, over video, as small pills. Several within 3 s merge into one ("Alex and Jo joined").
  The group list refreshes after every join, leave or state change and lists each group once.
  Your pause, play, seek and queue requests reach the group one at a time, in the order you made
  them. In a group a click on the video pauses or plays once the double-click time has passed, so
  a double-click for full screen sends the group nothing. A member whose video buffers carries on
  from where it stalled once the group resumes, instead of jumping ahead by the length of the
  stall, and a catch-up seek on a transcode aims ahead by the time the last one took.
- **Play on**: other Jellyfin clients can play on this app and drive it (transport, volume, mute,
  full screen, audio and subtitle picks on direct play, and messages)
- The server's library pushes drop the affected browse caches, so pages open fresh
- With an OMDb API key in Settings › Server, IMDb, Rotten Tomatoes and Metacritic scores show on
  detail pages. The key is stored encrypted for your Windows account, and scores are cached on
  disk for 7 days

### Browsing and library

- **Home**: Continue Watching, Next Up, Recently Added and Favorites rows (empty rows are left
  out). "Title →" opens the row as a full grid (**See more**) with a filter, sort and card size;
  ‹ › page through a row. Recently Added lists each video library's newest (new episodes under
  their show, dated by the newest episode), newest first across the libraries; a season card is
  titled with its show, the season under it. Its See more is the whole list in the same order,
  read from the server a page at a time as you scroll, like a library (skeleton cards while the
  first page loads, which comes before the full order is read): movies and videos by the
  date they were added and shows by the date of their newest episode, merged newest first (or by
  name), with no cap; watched titles stay out when the server's "hide played in latest" setting
  keeps them out of the row. Next Up
  shows how many episodes follow. A started title's card fills
  its progress bar to the share watched, at any card size. Hovering or focusing a card
  shows a round play button on its artwork: it plays or resumes a title, plays the next episode
  of a show or season, and plays a playlist. Every card has a ⋯ / right-click menu: play or
  resume, play from the beginning, mark played, favourites, go to series, info (Ctrl+I), and in
  Continue Watching "Remove", with Undo. Info on a title with a file opens its media info: for
  each version the container, path, size and bitrate, then every video stream (codec, profile,
  resolution, frame rate, range, bitrate, color details), audio stream (codec, channel layout,
  language, bitrate, sample rate, default) and subtitle (language, codec, default, forced,
  external), with Copy; on a show, season or collection it opens the detail page. On start, Home
  paints the rows it last showed for the profile (kept in the metadata cache) at once and then
  updates them from the server, touching only the cards that changed. On a window wider than
  1440 DIP the Home rows and the detail page's More like this, Cast and crew and season and
  episode grid grow with it, up to 1.5× at 2160 DIP: cards and the gaps between them by the full
  factor, captions and row headings by half as much (an experiment). A card's artwork loads again
  only once the card is more than 15 % wider than it was decoded for.
- **Caching and Back**: artwork is cached on disk in the same `imagecache` folder and under the
  same names as 1.x, so an upgrade keeps it; a start reads posters from disk instead of the
  network. Artwork is requested at a few fixed widths (160, 240, 320, 400, 480, 640, 800, 960,
  1280, 1600 and 1920 px, the next one at or above the width shown) and decoded at the width
  shown, so resizing the window or changing the poster size within a step keeps hitting the
  cache, and a grid that shrank uses the wider copy already on disk. The cache is trimmed
  least-recently-used to the image cache limit (500 MB unless
  1.x's Settings changed it) in a background sweep; Settings › Storage shows its size and
  clears it. Disk reads and decodes take turns through two to four slots (half the cores; one
  decode at a time on two cores or fewer and while the player grows or shrinks or the nav group
  opens), the cards on screen first, and a card scrolled past, recycled or left behind on another
  page drops its request before it costs anything, so a fully cached library of thousands of
  titles never starts thousands of reads and decodes at once. Reading, downloading and checking an
  image's size happen off the UI thread, and artwork read from disk or the network lands on the
  cards a little per frame (about 2 ms of the UI thread each), held back during those animations
  for at most 250 ms; artwork already decoded in memory shows in a page's first frame. Back and Forward show the kept page at once and
  refresh it in the background: a row or grid with the same items updates its cards in place (no
  rebuild, no artwork reload), and a detail page keeps the chosen start, version and tracks
  unless the title to play changed. Home
  and a library opened again from the top bar show their kept page as you left it, the same way;
  Home stays loaded, hidden, while other pages show, so its artwork is on screen in its first
  frame when you come back; a Home built again paints the rows it last showed in its first frame. With
  verbose logging, `app.log` times every page switch (`event=nav_shown key=… mode=back
  sync_ms=… elapsed_ms=…`) and Home's snapshot read and refresh.
- **Libraries and Playlists** in the top bar morph the group in place into your libraries or
  playlists (All first); choosing one opens it, Esc or a click outside contracts it. The chosen
  library or playlist slides into the place of Libraries or Playlists as the group contracts and
  stays there with its name and icon until you go elsewhere. With many libraries the group grows
  into the search field down to its minimum, then scrolls.
- **Library grid**: All / Unplayed / Played, a multi-select genre filter (also a removable chip),
  sort by name, date added, release date, rating or runtime in either order, poster size and
  titles (remembered per library), and an A–Z rail while sorted by name. The whole library loads
  in the background; the last copy shows at once.
- **Detail pages** for movies, shows, seasons and episodes. The large preview on the left is the
  player before playback: its bottom bar sets the start time (drag the seek bar; a started title
  starts at its resume point), the version, audio, subtitles and quality, and Play uses them.
  The right panel is the info panel for a movie, the seasons of a show (the next episode to play
  is shown in the preview) or the episodes of a season. Seasons fill as many columns as fit
  posters the size of More like this, episodes as many as fit Home's wide cards, at least two, so
  a wider panel shows more of them rather than bigger ones. With an OMDb API key in Settings,
  IMDb, Rotten Tomatoes and Metacritic scores fill a line under the chips. Below are "More like
  this →" (opens Advanced Search for the same kind of title and genre) and "Cast and crew →"
  (everyone credited). The page is laid out once, in its final shape, as it opens: the rows hold
  their place from the start (skeleton cards after 150 ms), a row with nothing to show keeps it
  with a quiet line such as "No similar titles", and everything that arrives later fills space
  that was already there instead of moving the preview, the rows or the panel. On a show, a
  season or an episode, the Up Next button (a stopwatch, beside Download) sets when the Up Next
  card appears for that whole show: Global (the Settings value), 15, 30 or 60 s, or a custom
  5 to 300 s.
- **Search** from the top bar: results appear as you type, grouped into Movies, Shows, People,
  Collections and Episodes, with chips that filter by category and "More filters", which carries
  the query into Advanced search. Each group's row fills the window's width and clips at its
  trailing edge, with "See all" when the group has more. Enter opens the first result; Esc or clearing the field returns
  to the page you were on. The empty field lists your recent searches (per device and profile).
  Each group shows as soon as its own request answers, cards that stay in the results are kept
  as they are, and artwork loads only for cards on screen. The total next to the heading waits
  until every group has answered (while you type on, the previous total stays), so it never
  reads a part of the results as all of them.
- **Advanced search** (filters for type, genre, person, year
  range, watched state, minimum rating and sort; results update as filters change; active filters
  appear as removable tokens; when nothing matches, the screen names the filter to remove).
  Opening the filter column puts focus on its first filter, so Esc closes it at once.
  Refresh (the button at the end of the results header, or `F5`) runs the search again for
  everything loaded so far: the grid keeps its scroll position and stays fully visible while
  "Updating" shows, titles that stay keep their cards and artwork, and only titles that were
  added, removed, moved or changed on the server update. A failed refresh keeps the results.
  An episode without artwork of its own (a special, an extra) shows its show's poster, here and
  on every poster card. Back returns to the results as you left them.
- **Person page**: photo, roles, birth and bio (More expands it), and the filmography grouped by
  role, with a role filter and a sort. "Advanced search" there searches for that person only.
- **Playlists**: Playlists → All lists your playlists (sort, New playlist); a playlist without
  artwork shows the playlist glyph, and an empty one shows no runtime, as on its page. Every
  card's ⋯ has Add to playlist. A playlist page plays all or shuffled, and its rows can be reordered (drag,
  Alt+Up/Down or Move up/down), removed with Undo, and played from any row. Rename and Delete are
  in the ⋯ menus.

### App zoom and library changes

- Zoom the interface with Ctrl+Plus, Ctrl+Minus, Ctrl+0, Ctrl+wheel or a touchpad pinch.
  The saved zoom applies to the bar, pages, toasts and player controls. Video keeps its own
  rendering size. Settings > Shortcuts can limit zoom to what the current screen can show;
  disabling that limit asks first. The mini-player keeps its compact layout.
- Items deleted on the server disappear from open pages when the server reports the removal.
  A deleted title's detail page explains that the item is gone. Pending loads cannot restore
  the removed cards, and the removal records belong to the current server and profile.
  A title restored on the server can appear again on a fresh load.

### Downloads and offline

- Choose audio and subtitle tracks for each download. The choices are saved with the file and
  applied when it plays, including while it is still downloading. A season or show download
  matches the choices to each episode, preserving forced subtitles. A downloaded file stays
  playable if another server is active, its own server is not signed in, or the title has since
  been deleted from the server. A downloaded episode offers Up Next and the episodes around it
  while its server is reachable. The player names a download's audio and subtitle tracks from the
  languages saved with it, where the file itself carries none.
- Cached Home and library pages remain browsable when the server is unreachable. A banner gives
  the cached results' timestamp and offers Retry now. With no saved results, the server-unreachable
  page still offers Downloads. Cached data stays scoped to the saved server and profile.
- **Downloads**: Download (or Download all for a season or show) from any card's ⋯ menu or a
  detail page. The Downloads page lists Downloading (play, pause, resume, cancel), Queued (drag to
  reorder, remove), Failed (retry) and Downloaded (play, delete with Undo), with the quality for
  new downloads (original or a 4K/1080p/720p/480p cap, or ask each time), a storage meter and Pause
  all. The bar's Downloads badge counts running and queued items; downloaded cards are marked
  available offline, and lose the mark when the download is deleted. Offline, the page explains
  that downloaded titles still play. Downloads made by 1.x stay listed and playable. A title still
  downloading plays from the bytes already on disk once enough of it has arrived, instead of
  fetching the same file again over the network; its row's Play starts from the beginning once the
  8 MB startup buffer is on disk. Where those bytes cannot play, for example a file whose index is
  at its end, or when they run out, playback goes on from the server at the same position while
  the download keeps running, as in 1.x. Its timeline shows the title's full length with the
  downloaded part marked, as the buffered part is when streaming; seeking past that part says the
  video is still downloading instead of waiting for it.
- **Errors and toasts**: when the server stops answering, server pages show "Can't reach …" with
  Retry, Switch server, Open Downloads and the technical reason, retry by themselves every 15 s,
  and list downloaded titles in an "Available offline" row that still plays. A title that fails
  to play shows a panel over the video with Retry at the same position, a second way to play it
  where one helps (original quality after a failed transcode, or another version), and the error
  text with Copy. Toasts stack at the bottom centre (at most three); over video they become pills.
  A toast never takes focus on its own, and a Settings control that rebuilds keeps its focus.
  Opening another page moves the keyboard focus into it rather than onto Back, so no tooltip
  opens over the bar; the next Tab starts in the page.

### Local files and links

- **Local files and links**: Open file… (Ctrl+O), a file dropped on the window or a path on the
  command line plays a local file. Play a link… (Ctrl+L) plays a direct HTTP(S) video or stream
  link; a link on the clipboard is filled in. A link dragged onto the window from a browser plays
  too: while a file or link is dragged over it, the page dims and a card names what will play (the
  file name, or the link's host and path). A link plays under the name its path gives, skipping
  generic names such as `index` or `master`, with its host under it, until the stream names itself;
  a ring shows until its first frame, Info's play method reads "Link", and a link that fails says
  so. No link history is kept, and queries stay out of titles and logs. Ctrl+M turns full video
  into an always-on-top mini-player (Esc or Ctrl+M returns). The window opens where it was closed.
  Cards' ⋯ menus copy a stream URL; detail pages with a trailer open it.

### Settings and interface

- **Settings** (Ctrl+,): server and profile (saved servers, switch user, Quick Connect approval,
  log out, the OMDb key for ratings), Home rows (order and visibility), playback (with the media
  keys), subtitle style with a live preview,
  audio, video (decoding, RTX, bitrate, buffer), downloads, caches, the app language (live),
  the keyboard shortcuts, diagnostics (log tail, export) and updates. After an update, Updates
  offers **Revert to <version>**: the version installed before it (the installer records it in
  `previous-version.txt` next to the app), downloaded from its GitHub release, verified like an
  update and installed over this one, with updates set to manual so it does not update itself
  back. Sign-in, settings and downloads stay. 2.0 is the first version that can do this, so an
  update from 1.x to 2.0 can go back to that 1.x. Shortcuts are rebound by
  clicking one and pressing the new keys (Esc cancels, Backspace clears, a key already in use is
  named with Replace); the player uses the same rebindable map as 1.x, plus `F` for full
  screen and `Ctrl+I` for stats; the fixed app keys, `Ctrl+L` and `Ctrl+O` among them, are listed
  under Navigation. A search box filters rows
  across all sections; every change applies at once.
- **Right-to-left**: Arabic mirrors the whole window, top bar included; the seek bar keeps time
  running left to right. Arrows and chevrons that point along the reading direction (Back,
  Forward, row and See all arrows, paging chevrons) mirror with it; media transport glyphs do not.
  An overview or bio aligns by the direction of its own text, so an English synopsis stays left
  aligned under Arabic.
- **Top bar labels** show in full in German and Arabic on a window 1000 DIP or wider (an item
  grows to fit its label, up to 72 px of text); on a narrower window the items keep the drawn 64 px
  and long labels are trimmed.
- WinUI's default animations are switched off (`FastUi.cs`), because stock WinUI motion measured
  three to five times slower than the WPF app. `tests/FastUi.Tests` fails when a flyout, template
  or transition brings them back.
- The app's own animations run on the compositor, not the UI thread, so artwork loading or a busy
  page cannot make them stutter: the player's grow and shrink (laid out once at the end rect,
  then scaled, moved and clipped), the nav morph (the pill drawn as a composition shape, the search
  field moved with it), the item slide, the video's fade-in and the toasts. A window that changes
  size while the grow, shrink or morph runs moves its end; with less than a frame left, it lands
  there at once.
- Icons are the system's Segoe Fluent Icons (`Theme/Icons.cs`), with Segoe MDL2 Assets as the
  Windows 10 fallback; every code point used exists in both. New UI text goes into the shared
  catalog with `tools/Localization/merge-strings.py`.
- **Frame timing** (test hook): with `LIGHTWEAVER_FRAME_LOG=<path>` set, every animation (player
  grow and shrink, the video's fade-in, nav morph and slide, toast fades) and every page switch appends one JSON line to
  that file: name, duration, frames, mean, p50, p95 and max frame time, the frames over 16.7 ms
  and 33.3 ms, and then the artwork that landed meanwhile: `images`, `image_frames` (frames with
  any) and `images_max_per_frame` (`FrameStats` in `LightWeaver.Core`). Opening a detail page also
  writes a `detail_settle` line over the 2 s after its first frame, with `layout_changes` (how often
  the preview's rect, the rows' top, the side panel's width or the overview's or the grid's place in
  it changed; 0 when the page laid out once) and `layout_settled_ms` (when the last change came). Unset, nothing is recorded.

### Updates, diagnostics, and log privacy

- Self-updater reading GitHub Releases, in both the WPF app and v2, with an immediate
  **Settings → Updates → Check for updates** action plus startup and six-hour checks, explicit
  status with download progress, and three policies: notify only (default; **Download and install**
  when you choose), automatic download, or verified silent installation when you normally exit (not
  when Windows logs off or shuts down). Choosing **Install now** under that last policy installs
  silently and starts the new version afterwards. Staging is resumable and replaces its metadata
  atomically, and stale setups in the `updates` folder are removed at start. The WPF app shows a
  found update in a banner; v2 says so once in a toast outside Settings → Updates
- Diagnostics logging: a crash file for any exception that escapes the UI thread, another thread
  or an unobserved task. Settings → Diagnostics switches verbose logging, shows the tail of the
  log, opens the log folder and exports the logs as one ZIP
- Logs in `%LOCALAPPDATA%\LightWeaver\logs` redact tokens, authorization values and your user name,
  use bounded retention, and are not written at all on a healthy non-verbose run. A failure line
  records the exception type (`error=HttpRequestException`), never its message, because a message
  can carry a server address, a file path or a title. Crash files are the exception and keep the
  full exception
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
- Disk-backed image, metadata, ratings and browse caches: Settings → Storage shows the image,
  metadata and ratings cache sizes and clears each of the four caches or all of them. The window position and size are kept
- Settings save as you change them

## Requirements

- Windows 10 version 2004 (build 19041) or later, or Windows 11, x64. The installer refuses older
  versions
- A Jellyfin server (see [Status](#status) for tested versions)
- `libmpv-2.dll` (mpv >= 0.40 for RTX Video HDR) — bundled in the installer; for a source build,
  place it in `native/win-x64/` (gitignored; copied next to the exe at build time). Debug builds
  also find it on `PATH`; release builds load only the copy next to the exe
- For RTX features: an NVIDIA RTX GPU (20-series or newer) with RTX Video enabled in the NVIDIA App
- To build: the .NET 10.0.300 SDK or any later 10.0 SDK, as selected by `global.json`

## Install

Download `LightWeaver-<version>-setup.exe` from the
[Releases](https://github.com/oss96/LightWeaver-Windows/releases) page and run it.

The installer is per-user — no admin prompt, installed to `%LOCALAPPDATA%\Programs\LightWeaver` —
and creates a Start menu entry (a desktop icon is optional). It updates in place: running a newer
setup over an existing install keeps your credentials and all preferences, as does uninstalling.
From 2.0 on, the setup also records the version it replaced, so Settings → Updates can revert to it.
2.0 installs over 1.x in place: the same install folder, Start menu entry, exe name and app data.

The app can also update itself — see Settings → Updates.

## Build from source

```
dotnet build                                                         # compile
dotnet run --project src/LightWeaver.WinUI                           # start
dotnet run --project src/LightWeaver.WinUI -- "D:\movie.mkv"         # play a file immediately
dotnet run --project src/LightWeaver.WinUI -- --page advanced-search # open Advanced Search
```

Debug builds run in an isolated app-data environment (`%LOCALAPPDATA%\LightWeaver-Debug`) and title
their window "LightWeaver (Debug)", so running the dev build never touches the installed build's
login, settings or watched state. This is selected automatically via `#if DEBUG`.

The version lives in one place, `Directory.Build.props`: the app, its libraries, the scripts under
[Release build](#release-build), the update check and the client version sent to Jellyfin all read
it.

## LightWeaver 2 (WinUI 3)

Version 2 moves the whole app to WinUI 3 with a new design, recorded in [DESIGN.md](DESIGN.md), and
is the shipping app from 2.0. The 1.x WPF app (`src/LightWeaver`, still on the legacy "woven light"
design) stays in the repository and the solution, and still builds
(`dotnet run --project src/LightWeaver`), but is no longer released.

- `src/LightWeaver.Core` compiles the WPF app's framework-free code (Jellyfin client, downloads,
  settings, localization, view models) in place, so both apps share it without moving files.
- `src/LightWeaver.WinUI` is the WinUI 3 app, built as `LightWeaver.exe` like 1.x, so taskbar pins,
  GPU profiles and the process name carry over: unpackaged and self-contained, using only the
  WinUI, Foundation, InteractiveExperiences and Runtime parts of the Windows App SDK. A Debug
  build reuses the WPF Debug build's saved login (`%LOCALAPPDATA%\LightWeaver-Debug`).
- Window position and size are saved in `window-placement.json`; 1.x's `window-state.json` is not
  read, so the first start of 2.0 opens the window at its default size and place once.
- A crash file is written for any exception that escapes the UI thread, another thread or an
  unobserved task, and queued log lines are flushed when the window closes.

Tests: `dotnet run --project tests/SearchModel.Tests`, `dotnet run --project tests/Login.Tests`,
`dotnet run --project tests/Home.Tests`,
`dotnet run --project tests/Library.Tests`, `dotnet run --project tests/Details.Tests`, `dotnet run --project tests/SearchPerson.Tests`, `dotnet run --project tests/Playlists.Tests`, `dotnet run --project tests/Downloads.Tests`, `dotnet run --project tests/Shortcuts.Tests`, `dotnet run --project tests/PlayerSettings.Tests`, `dotnet run --project tests/FrameTiming.Tests`, `dotnet run --project tests/Updates.Tests`, `dotnet run --project tests/SyncPlay.Tests`,
`dotnet run --project tests/FastUi.Tests -- .`
`dotnet run --project tests/LocalizationAudit -- .`
and `dotnet run --project tests/LocalizationRuntime`. Unit tests and these static checks run
before a merge. A maintainer UI suite drives the screens through UI Automation on the test VM
before a new release is created. Its full run builds once and runs nine groups, sharing app
sessions for Home/Library, Detail/Player, Search/Advanced Search and Settings/Layout. Individual
scenarios report their own results and timings; their standalone entry points remain available.

## Dependency maintenance

The shipped app's NuGet versions live in `src/LightWeaver.Core/LightWeaver.Core.csproj` (the
Jellyfin client and DPAPI) and `src/LightWeaver.WinUI/LightWeaver.WinUI.csproj` (the Windows App
SDK). Alongside `Jellyfin.Sdk`, explicit references in Core, as in the WPF app's
`src/LightWeaver/LightWeaver.csproj`, keep all six Kiota runtime packages on 2.1.2, the set
`Jellyfin.Sdk` 2026.10.12 (the Jellyfin 12.0 API) is built on, and `Std.UriTemplate` on 2.0.12.
Renovate can propose updates for each reference. It groups Kiota updates and keeps them below 3.0
until a separate Jellyfin compatibility migration, and moves the four Windows App SDK packages
(WinUI, Foundation, InteractiveExperiences, Runtime) together. Updates require review and merge,
then a new app release. Kiota brings in two indirect packages:
`Microsoft.Extensions.DependencyInjection.Abstractions` and `Microsoft.IO.RecyclableMemoryStream`.

`global.json` selects .NET SDK 10.0.300 and permits any later 10.0 feature band or patch,
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
before releasing. Debug source builds can still use another local build or find libmpv on
`PATH`; release builds and release packaging require the bundled, recorded build. Bundled fonts and build-machine tools such
as Inno Setup and GitHub CLI continue to be updated manually.

To inspect the resolved packages and check for updates or known vulnerabilities:

```powershell
dotnet package list --project src/LightWeaver.WinUI/LightWeaver.WinUI.csproj --include-transitive
dotnet package list --project src/LightWeaver.WinUI/LightWeaver.WinUI.csproj --include-transitive --outdated
dotnet package list --project src/LightWeaver.WinUI/LightWeaver.WinUI.csproj --include-transitive --vulnerable
```

## UI languages and translation editor

UI text lives in [`localization/catalog.json`](localization/catalog.json). English is the
source language. Language files in `localization/locales/` are embedded during the build;
choose an available UI language in Settings to apply it immediately. The current view, inputs
and playback stay in place. System default
uses the Windows UI language when a matching pack is available. Missing, outdated or invalid
translations fall back to English.
The German (`de`) and Arabic (`ar`) packs cover playback controls, settings, downloads, SyncPlay
and error messages. New or changed translations awaiting review fall back to English. Select Deutsch or العربية
to use them.
Arabic mirrors the layout: navigation, lists and dialogs read right to left, and directional
icons such as Back, Forward and the see-all chevrons point the other way. The player's transport
controls, seek bar, time codes and progress bars stay left to right, as in other media players,
and window title bars are not mirrored. Switching language while the app runs updates the
direction immediately.
Standard library names such as Movies and TV Shows follow the interface language immediately.

Run the local editor from this checkout:

```powershell
dotnet run --project tools/TranslationEditor
```

The editor opens itself in your default browser through a session link, which it also prints
(`--no-browser` only prints it). That browser profile keeps the session, so later links such as
[the translation editor](http://127.0.0.1:5386/) open directly. Its interface supports English, German and
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

The player keys are rebindable in Settings → Shortcuts: click a shortcut and press the new keys
(Esc cancels, Backspace clears, and a key already in use is named with Replace). The fixed app keys
are listed there under Navigation.

| Key | Action |
|---|---|
| `Space` | Play / pause |
| `Left` / `Right` | Seek back (Settings › Playback › Skip back) / forward 10 s |
| `Ctrl+Left` / `Ctrl+Right` | Seek back / forward 30 s (Settings › Playback › Skip forward) |
| `.` / `,` | Next / previous frame |
| `Up` / `Down`, wheel over the player | Volume |
| `M` | Mute |
| `+` / `-` | Playback speed |
| `A` / `S` / `V` | Cycle audio / subtitle / video track |
| `[` / `]` | Previous / next chapter |
| `Z` / `X` | Subtitles 0.1 s earlier / later |
| `C` | Skip intro or credits |
| `N` / `P` | Next / previous in queue |
| `F`, `F11`, double-click | Full screen |
| `Esc` | Leave full screen, close a flyout or the mini-player |
| `Ctrl+I` | Playback stats |
| `Ctrl+M` | Mini-player |
| `Ctrl+O` | Open file |
| `Ctrl+L` | Play a link |
| `Ctrl+F` | Search |
| `Ctrl+,` | Settings |
| `Alt+Left` / `Alt+Right`, mouse side buttons | Back / Forward |
| `F5` | Refresh Advanced search |

Click the video to pause and resume; click the elapsed time to type a position; drop a file or
a link on the window to play it.

## Release build

```
.\publish.ps1     # dist/publish/ and dist/LightWeaver-<version>-win-x64.zip (self-contained, libmpv bundled)
.\installer.ps1   # dist/LightWeaver-<version>-setup.exe (Inno Setup; runs publish.ps1 first)
.\release.ps1     # installer.ps1 + a GitHub release with the setup.exe asset, published with `gh`
```

All three package the WinUI 3 app (`src/LightWeaver.WinUI`) at the version in
`Directory.Build.props`. The publish folder holds `LightWeaver.exe`, `LightWeaver.pri` (the
resource index, which carries the compiled XAML), the .NET and Windows App SDK runtimes and
`libmpv-2.dll`. The installer keeps 1.x's AppId and install folder, so it replaces an installed
1.x in place, and refuses Windows versions before 10.0.19041.

`installer.ps1` requires Inno Setup 6 (`winget install -e --id JRSoftware.InnoSetup`), and
`release.ps1` requires the [GitHub CLI](https://cli.github.com/) authenticated against the
repository.

Both build scripts Authenticode-sign what they produce **when signing is configured**, and print one
line and carry on when it is not — so a release is buildable on a machine with no certificate.
`publish.ps1` signs our own binaries (`LightWeaver.exe`, `LightWeaver.dll`, `LightWeaver.Core.dll`
and `LightWeaver.Localization.dll`) before zipping, so the portable zip and the installed copy
carry the same signature, and the updater can compare an installer's signer with the running
exe's; `installer.ps1` signs the setup exe, which is the
signature SmartScreen inspects at download. Everything is timestamped, so signatures outlive the
certificate. Setup, certificate options and costs: [docs/CODE-SIGNING.md](docs/CODE-SIGNING.md).

## Architecture

One rule is load-bearing and everything else is arranged around it:

> mpv **renders and presents the video itself** with `vo=gpu-next` on D3D11, into a target it owns:
> in the WinUI 3 app a composition swapchain (`MpvVideoTarget.Composition`) shown by a
> `SwapChainPanel` behind the controls, configured for HDR from the display's peak, contrast and
> primaries; in the 1.x WPF app a child HWND via `wid`, with the controls in a transparent overlay
> window. Frames are never routed through the UI framework (`D3DImage`, render-API-to-texture,
> copying frames into XAML, and so on) — that breaks RTX Video HDR and HDR passthrough, which are
> core requirements.

## Status

Feature-complete for daily use. LightWeaver 2.0, the WinUI 3 app on the current design in
[DESIGN.md](DESIGN.md), is the shipping app; the 1.x WPF app, on the legacy "woven light"
design, stays in the repository but is no longer released. Runs against
**Jellyfin 12.0** and **10.11**. The client uses only current authorization forms, so 12.0 turning
legacy authorization off does not affect it.
Authenticated API, session socket and media requests carry the current client version and device
identity alongside the token, so Jellyfin updates its device record after an app upgrade.

Known issues and open work are tracked by the maintainer outside the repository.

## Design

[DESIGN.md](DESIGN.md) is the current design and the place to start any design work. It records
the Claude Design "Screen-group design spec" handed off on 2026-10-17: direction, type, color,
spacing, shape and motion, the components and screens with their code and design files, the rules,
and every change since the handoff. Token values live in
`src/LightWeaver.WinUI/Theme/Tokens.xaml`. The handoff itself is kept unchanged in
[design/claude-design/](design/claude-design/), and proposals for later changes in
[design/proposals/](design/proposals/). LightWeaver 2 implements this design.

The rest of [design/](design/) is the legacy "woven light" design of the WPF app: the design
prompt, the generated spec (tokens, motion timings, component states, the status indicators the
app shows, and full-screen mockups for its six screens), and the Advanced Search redesign
package. It applies to the 1.x WPF app only.

[design/app-icon.icon](design/app-icon.icon) is the app icon source: the
Gold-Violet "Shallan" seal as an Icon Composer bundle (SVG layers plus `icon.json`).
`src/LightWeaver.WinUI/Assets/LightWeaver.ico` is rendered from it: a `#251B3E` rounded plate under
the gold layers, at 16–256 px. The exe, the window, the taskbar, Alt-Tab and the installer all use
that file, and so does the WPF app's exe.

## Contributing

Issues and pull requests are welcome. Before opening a PR:

- `dotnet build -warnaserror` must pass. Warnings are errors in this project
- If the change is significant, update [README.md](README.md)
- Respect the load-bearing rule above — a change that routes video through the UI framework will not
  be merged

## License

Zero-Clause BSD (0BSD). See [LICENSE](LICENSE).
