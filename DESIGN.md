# Design

Source: Claude Design project "Screen-group design spec", handed off 2026-10-17, stored in `design/claude-design/screen-group-design-spec/`.
Token values: `src/LightWeaver.WinUI/Theme/Tokens.xaml` (control styles in `src/LightWeaver.WinUI/Theme/Styles.xaml`, icon glyphs in `src/LightWeaver.WinUI/Theme/Icons.cs`).

This is LightWeaver's current design and the starting point for any design work. The WinUI 3
shell (v2, `src/LightWeaver.WinUI`) implements it. The "woven light" spec in
`design/LightWeaver Jellyfin design system/` (v1.0, July 2026: storm surfaces, Marcellus,
stormlight glows) is the legacy design of the WPF v1 shell (`src/LightWeaver`), which stays
in the repository as legacy source. New work does not follow it, and v2 uses none of its tokens.

## Direction
A native Windows 11 app, not a themed one: Mica window, a content layer under the bar, card fill for
groups, acrylic flyouts, Segoe UI Variable and Segoe Fluent Icons. Dark only, with one garnet accent
used as the system tint. The media (posters, backdrops, video) is the brightest thing on screen and
the chrome stays quiet. Speed is part of the look: states snap, nothing glows, nothing lingers.

## System
- Type: `LwFont` (Segoe UI Variable Text) for all UI, `LwMonoFont` (Cascadia Mono) for logs and
  technical values. Page title 22 bold; section and panel headings 15 semibold; filter labels 12
  semibold in `LwText2Brush` (`LwFilterLabel`); body and controls 13; tokens and status 12; card
  subtitle 11.5.
- Color: window `LwWindowBrush`, content layer `LwLayerBrush` with `LwLayerTopBrush` edge; panels
  `LwPanelBrush`, overlay pane `LwPaneBrush`, flyouts and toasts `LwPopupBrush` with
  `LwPopupBorderBrush`, flyouts, menus and toasts over video `LwPlayerGlassBrush` with
  `LwPlayerGlassBorderBrush` and message text over video `LwPlayerTextBrush` (88 %); text `LwTextBrush` / `LwText2Brush` / `LwText3Brush`; accent `LwAccentBrush`
  (fills), `LwAccentFgBrush` (indicators, links, progress, accent text), `LwAccentSoftBrush`
  (selected tokens, highlighted buttons, player bar toggles that are on); states `LwErrorBrush`, `LwOkBrush`, `LwOffBrush`;
  placeholder art `LwPlaceholderBrush`. Warning banner (InfoBar Warning) `#433519` with a `#FCE100`
  glyph, as drawn in design 14a.
- Spacing and layout: page gutters 24, header padding 24,16,24,14, panel padding 18,16; poster grid
  `UniformGridLayout` min 140 wide, 14 column gap, 20 row gap. Card rows (Home, detail) as drawn
  for a 1440 DIP window: wide cards `LwHomeWideCardWidth` 296, Home posters `LwHomePosterWidth` 150,
  detail posters `LwDetailPosterWidth` 140, people `LwDetailPersonWidth` 120, cards 14 apart, Home
  rows 28 apart; above 1440 they grow with the window (`CardScale`, see Rules). The detail page's
  season grid is a poster grid of `LwDetailPosterWidth` and its episode grid one of
  `LwHomeWideCardWidth`, 12 column gap, 14 row gap, at least two columns, both at the rows' scale. On
  the detail page More like this and Cast and crew share the row below the preview 3 : 2 at every
  width, and the preview is never under 300 tall (`DetailModel.PreviewMinHeight`); an empty row
  keeps its place with a quiet 13 `LwText2Brush` line. Bar height `LwBarHeight` (56). Minimum
  window 900 × 600, fluid to 4K; Advanced Search docks its 300 px filter column at 1100 px and wider.
- Shape and depth: `LwControlRadius` 4 (buttons, fields), `LwItemRadius` 6, `LwCardRadius` 8 (art),
  `LwGroupRadius` 8 (bar groups, flyouts, toasts), `LwTokenRadius` 12 (filter tokens), segments 3.
  Depth comes from fills and 1 px strokes; the only shadow is the flyout/toast one.
- Motion: hover and press snap (no brush transitions, ≤ 80 ms); flyouts and menus open and close
  at once, without WinUI's slide-and-fade (`FastUi.cs`, held by `tests/FastUi.Tests`); nothing over
  200 ms except the nav morph width (240 ms); toasts in and out in 120 ms; skeletons only after
  150 ms; Advanced Search debounces filter changes 150 ms and dims the old grid to 0.6 while
  the request runs (no skeleton swap). Its manual Refresh does not dim: the grid stays at full
  opacity and only the header's Updating indicator shows.
  The player grows and shrinks in 180 ms, cubic ease-out, all edges together, from the detail
  preview or from the clicked card's artwork to the window and back, landing exactly on the final
  rect; a new title's video fades in over 120 ms once its first frame is shown, over the artwork
  that stood in for it. Windows' own window animations are off while the window enters or leaves
  full screen, and the content is laid out for the new size before the window changes, so the old
  layout never shows in the new window rect. Into full screen the video first grows to the window
  and the window then covers the monitor; out of it the window comes back first and the video then
  shrinks onto the preview, so the window change never runs inside either 180 ms. Every animation runs on the compositor, never as a
  per-frame layout on the UI thread. Artwork from disk or the network waits (at most 250 ms) while
  the grow, shrink or the nav morph's opening runs; artwork already in memory shows in a page's
  first frame, and a page switch never holds artwork back.

## Components
| Component | Code | Design file |
| --- | --- | --- |
| Top bar (history, nav morph, search field, right group, avatar) | `src/LightWeaver.WinUI/Shell/TopBar.xaml` | `Chrome.dc.html`, `01 Nav Morph.dc.html` |
| Card (poster, wide, person, square) | `src/LightWeaver.WinUI/Controls/MediaCard.xaml` | `Card.dc.html` |
| Item menu | `src/LightWeaver.WinUI/Controls/ItemMenu.cs` | `Menu.dc.html` |
| Card row, home row | `src/LightWeaver.WinUI/Controls/CardRow.cs`, `HomeRow.cs` | `01 Nav Morph.dc.html`, `03 See More.dc.html` |
| Token field | `src/LightWeaver.WinUI/Controls/TokenBox.xaml` | `05 Advanced Search.dc.html` |
| Range slider | `src/LightWeaver.WinUI/Controls/RangeSlider.cs` | `05 Advanced Search.dc.html` |
| A–Z rail | `src/LightWeaver.WinUI/Controls/AlphaRail.cs` | `02 Library Grid.dc.html` |
| Toast | `src/LightWeaver.WinUI/Shell/ToastHost.cs` | `14 Errors and Toasts.dc.html` (14c) |
| Server unreachable, offline banner | `src/LightWeaver.WinUI/Shell/ServerUnreachableView.xaml` | `14 Errors and Toasts.dc.html` (14a) |
| SyncPlay panel | `src/LightWeaver.WinUI/Controls/SyncPlayPanel.cs` | `09 SyncPlay.dc.html` |
| Settings pane | `src/LightWeaver.WinUI/Pages/SettingsPage.cs` | `SettingsPane.dc.html`, `10 Settings.dc.html` |
| Colour swatch (subtitle colour) | `LwSwatch` in `src/LightWeaver.WinUI/Theme/Styles.xaml`, `src/LightWeaver.WinUI/Pages/SettingsPage.cs` | `SettingsPane.dc.html` |
| Directional icon (mirrors right to left) | `LwDirectionalIcon` in `src/LightWeaver.WinUI/Theme/Styles.xaml`, `Ui.DirectionalIcon` | `Chrome.dc.html`, `Card.dc.html` |
| Player bar toggle (RTX VSR, RTX HDR) | `LwBarToggle` in `src/LightWeaver.WinUI/Theme/Styles.xaml`, `src/LightWeaver.WinUI/Player/PlayerView.xaml` | `design/proposals/2026-10-20-player-rtx-toggles.html` |
| Drop overlay, Play a link dialog | `src/LightWeaver.WinUI/Shell/MainWindow.Files.cs` | `design/proposals/2026-10-22-stream-url.html` (A2–A5, C1) |
| Up Next for this show (detail page button and flyout) | `src/LightWeaver.WinUI/Controls/UpNextLeadFlyout.cs` | `design/proposals/2026-10-23-v2-player-port.html` (3) |

## Screens
| Screen | Code or route | Design file |
| --- | --- | --- |
| Nav morph | `Shell/TopBar.xaml` | `01 Nav Morph.dc.html` |
| Library grid | `Pages/LibraryPage.xaml` | `02 Library Grid.dc.html` |
| See more | `Pages/SectionPage.xaml` | `03 See More.dc.html` |
| Search | `Pages/SearchPage.cs` | `04 Search.dc.html` |
| Advanced search | `Search/AdvancedSearchPage.xaml` | `05 Advanced Search.dc.html` |
| Playlists | `Pages/PlaylistsPage.cs`, `Pages/PlaylistPage.xaml` | `06 Playlists.dc.html` |
| Person | `Pages/PersonPage.xaml` | `07 Person.dc.html` |
| Downloads | `Pages/DownloadsPage.cs` | `08 Downloads.dc.html` |
| SyncPlay | `Shell/MainWindow.SyncPlay.cs` | `09 SyncPlay.dc.html` |
| Settings | `Pages/SettingsPage.cs` | `10 Settings.dc.html` |
| Profile menu | `Shell/MainWindow.Profile.cs` | `11 Profile Menu.dc.html` |
| Login | `Pages/LoginPage.xaml` | `12 Login.dc.html` |
| Player | `Player/PlayerView.xaml` | `13 Player.dc.html` |
| Errors and toasts | `Shell/ServerUnreachableView.xaml`, `Shell/ToastHost.cs` | `14 Errors and Toasts.dc.html` |
| Home, detail | `Pages/HomePage.cs`, `Pages/DetailPage.xaml` | owner screenshots in `project/uploads/` |

Paths in the Code column are under `src/LightWeaver.WinUI/`; design files are under
`design/claude-design/screen-group-design-spec/project/`.

## Rules
- One garnet accent, as the system tint: `LwAccentBrush` for fills (primary button, a dialog's
  default button, count badge), `LwAccentFgBrush` for indicators, progress and accent text, never a
  fill. Nothing else is custom-coloured.
- No glows and no hover fades: hover and press change in ≤ 80 ms; nothing animates over 200 ms
  except the nav morph width.
- Ship stock WinUI controls where the component sheet in `00 Overview.dc.html` names one; build
  custom only where it says "Custom".
- Over video, only dim glass (55–72 % black), white text at 70–88 %, no white or large accent fills;
  flyouts stay inside the window.
- Esc closes the innermost layer (menu → flyout → nav morph → search results → player); every
  popover returns focus to its button. Opening a panel puts focus on its first control, never on a
  button whose tooltip would open; a toast never takes focus on its own. A page switch never drops
  focus onto the bar: focus that was on the page that leaves goes to the new page's host (no ring,
  no tooltip), where the page may move it and the next Tab enters the page; a bar button the user
  pressed keeps it. Over video, a control's tooltip opens only for keyboard focus or after the
  pointer has moved since the controls appeared, never for controls that appear under a resting
  pointer or take focus in code.
- Grids add columns as the window grows; rows clip at the trailing edge; the search field absorbs bar
  width, down to 160.
- Experiment: Home rows and the detail page's More like this and Cast and crew grow with the window
  from `LwCardScaleFrom` (1440 DIP), × width ÷ 1440, up to `LwCardScaleMax` (1.5×, reached at
  2160). Card widths, the gaps between cards, the heading-to-cards gap and the gaps between Home
  rows take the full factor; card captions, the caption gap and row headings take
  `LwCardTextShare` (half) of the growth. Gutters, the page bar and card controls (play button,
  action pill, badges, ‹ ›) stay as drawn. The detail page's season and episode grid takes the
  same scale for its card width, gaps and captions. The library grid, See more and the player do
  not scale.
- Loading keeps what is on screen: dim the old content, never swap it for skeletons mid-session.
- The detail page lays out once: its geometry is decided before any lookup answers, and late data
  fills reserved space and never moves the layout. The preview keeps the shape it opened with (a
  video of another shape plays letterboxed in it); the rows hold their place with placeholder
  cards and keep it when empty.
- A refresh the user asks for updates in place: no dim, and the scroll position stays. Results land
  as a keyed diff, not a clear and refill: a title that stays where it was keeps its card and
  artwork; a title that changed place is removed and inserted again, and keeps its artwork only
  when the grid hands back the same card.
- Advanced Search changes only when the user acts (filters, text, Refresh). No background polling,
  live status or change summaries (owner, 2026-10-20).
- A tooltip names its shortcut in parentheses after the label, as in "Refresh (F5)" and
  "Pause (Space)".
- Right to left, a glyph that points along the reading direction mirrors (`LwDirectionalIcon`):
  back and forward, row and See all arrows, paging and disclosure chevrons. Media transport glyphs
  and glyphs with digits stay as drawn. Text from the server (overviews, bios) aligns by its own
  direction, not the interface's.
- Every icon code point exists in Segoe MDL2 Assets as well as Segoe Fluent Icons, so Windows 10
  draws it too.
- A file or a link can be dropped on every page but Login, and plays at once, replacing what
  plays. While one is dragged over the window the content layer (the whole window over the full
  player) dims with `LwOverlayBrush` and one `LwPopupBrush` card names what will play; anything
  else gets no overlay and the no-drop cursor.
- Top-bar labels keep their own width, up to 72 px, from a 1000 DIP window
  (`LwBarLabelsNaturalFrom`); below that, where the search field nears its minimum, items are the
  drawn 64 px and labels trim at 60.

## Changes

### 2026-10-28 · Detail: version drop-down and the player's quality options before Play
- What changed: the detail preview's bar starts its right-hand group with a version drop-down when
  the movie or episode has two or more versions: a `LwSubtleButton` 160 wide and 38 tall, padding
  8,0, the version's name (12.5, trimmed; with its size or bitrate when two versions share a name;
  "Version", disabled, until the versions load) and `Icons.ExpandMore` at 10, name and tooltip
  "Version: <name>", AutomationId `DetailVersion`. It opens the bar's radio menu, one entry per
  version with its size or bitrate ("1080p · 4.2 GB"); the versions left the quality menu. The
  quality menu lists the player's tiers (As in Settings, Original, 20, 10, 4, 1 Mbps, from
  `PlayerViewModel.QualityTierMbps`) and Always transcode (`DetailForceTranscode`). Play carries
  the tier and Always transcode, and the player's Quality flyout opens showing them. In both menus
  every tier reads its plain name with its bitrate beside it ("High · 1080p · 20 Mbps", "As in
  Settings · 20 Mbps", "Original quality"), so the Exact bitrates switch and its setting are gone.
- Why: the owner wanted audio, video and subtitle control on movies and episodes before Play: a
  visible choice between two versions of the same title, and the transcoding options the player
  has. The detail menu listed other caps (40, 8, 2 Mbps), had no Always transcode, and sent a
  Settings cap outside its list as Original. After trying it, the owner asked for the bitrate next
  to every quality name rather than behind a switch.
- Proposal: none; the owner named the controls and the labels.
- Chosen option: the drop-down shows only with two or more versions. To keep the page laying out
  once, its 160-wide slot is always held, like the trailer button's: invisible, unfocusable and
  out of UI Automation until the title has versions, so a late answer never wraps or moves the
  bar; the cost is that the bar's right group stacks above the seek row in a window narrower by
  that slot. It shows from the start when the title's list entry carries the server's version
  count (`MediaItem.MediaSourceCount`, asked for with the video's size in every list query),
  else when the versions answer.

### 2026-10-28 · A started download says so with the download arrow
- What changed: the toast that confirms a download started or was queued shows the Download glyph
  (`Icons.Download`) in the info colour instead of the success check, as a new `Download` toast
  kind; it lasts as an info toast does. Toasts over video show their kind's glyph too.
- Why: the user read the check as "done" on a title that had only begun downloading.
- Proposal: none; a single fix within the toast's existing layout.
- Chosen option: the single fix (new component variant: the Download toast).

### 2026-10-28 · Toasts over video read as messages
- What changed: while the full player is shown, a toast (for example "This part of the video is
  still downloading.") is a compact toast on player glass instead of a borderless dim pill:
  `LwPlayerGlassBrush` fill, 1 px `LwPlayerGlassBorderBrush` stroke, `LwGroupRadius`, the toast
  shadow, the toast kind's glyph at 16 in its colour, 13 text in the new
  `LwPlayerTextBrush` (88 % white), at least 36 tall. No dismiss button: it takes no input and
  leaves after 3 s, top left under the bar as before (`PlayerView.ShowAnnouncement`).
- Why: the user said the toast needed a border to read as a message; the old pill read as a status
  label and nearly vanished over dark scenes.
- Proposal: `design/proposals/2026-10-28-player-message-pill.html`.
- Chosen option: B, compact toast over video (new component variant; new token `LwPlayerTextBrush`).

### 2026-10-25: app zoom, removals and cached browsing

The remaining v1 app and offline ports use the existing cards, dialog choices, warning InfoBar
and info toast. App zoom reflows chrome and controls while the video keeps its own target.
Settings > Shortcuts includes the screen-limit switch with the existing confirmation copy.
Offline cached pages show their timestamp above the page, and deleted cards are removed in place.
Download track choices use the existing dialog controls and persist with the download.
Chosen direction: [app and offline port proposal](design/proposals/2026-10-25-v2-app-offline-port.html).
The proposal is based on source; native screenshots and UI suite execution remain a post-merge,
pre-release gate under the maintainer testing policy.

Newest first.

### 2026-10-23 · Player: type a time, Up Next timing
- What changed:
  - Player: the elapsed time is a subtle button (`LwSubtleButton`, padding 4,0, like the remaining
    time), named "Go to a time". A click puts a 76 × 24 field in its place: dim glass (55 % black,
    1 px 20 % white stroke, `LwControlRadius`), `LwBareTextBox` at 12 in white, prefilled with the
    position and selected; its tooltip gives the format. Enter seeks, Esc or a click elsewhere closes
    it; input that is not a time seeks nowhere, with no error state.
  - Settings › Playback › Up next: a third stepper row, "Show Up Next before the end" (5 to 300 s,
    `Icons.Clock`); Countdown runs 3 to 120 s again.
  - Detail page (show, season, episode): an Up Next button after Download (32 × 32 `LwButton`,
    `Icons.Timer`) opens a flyout (new component, `UpNextLeadFlyout`): heading in `LwFilterLabel`,
    segments Global · 15 s · 30 s · 60 s · Custom (`LwSegmentTrack`), a 5 to 300 number box while
    Custom is chosen, and a status line at 12 in `LwText2Brush`. A choice saves at once.
- Why: the owner picked these WPF player features for 2.0 on 2026-10-23. The handoff draws the
  elapsed time as plain text (13b) and the Up next group with two rows, and has no per-show setting.
- Proposal: [`design/proposals/2026-10-23-v2-player-port.html`](design/proposals/2026-10-23-v2-player-port.html).
- Chosen option: one suggestion per undrawn state, applied at once (within the system; one new
  component built from existing controls). Departures from WPF: the series pill that showed the
  show's value ("Up Next · 60 s") became an icon button whose tooltip says it, as the WPF episode
  page did; the field works in the compact bar and the mini-player too; an invalid custom value
  cannot be entered (the number box clamps), so the WPF error line is gone.

### 2026-10-23 · Settings › Updates: the updater's states
- What changed:
  - Settings › Updates keeps the drawn pane (`SettingsPane.dc.html`) and shows every state of the
    updater in it. The version row has "Last checked …" under it once a check has run, and the state
    as its value (13, `LwText2Brush`, at most 320 wide, wrapping): Not checked yet, Checking…, Up to
    date, Version X is available, Downloading… N %, ready to install (or, under automatic install,
    ready to install when LightWeaver closes), Starting the installer…, or the failure in
    `LwErrorBrush`. A download adds the Downloads row's track before the percentage (4 px,
    `LwTrackBrush`, fill `LwAccentFgBrush`, 160 wide).
  - The foot keeps one accent button: the action the state offers (Download and install, Download,
    Install now, Open release page, Retry), with Check for updates beside it as a plain button; when
    checking is all there is to do, Check for updates is the accent button, as drawn.
  - The policy's first choice reads "Notify me, install when I choose" instead of "Only when I
    check": it checks at start and every six hours like the others.
  - Outside Settings › Updates, the info toast (14c) says once per version that an update was found
    under the notify-only policy ("Open" goes to Settings › Updates) or is downloaded and ready
    ("Install now"). On Login it waits for the first page after sign-in; while Settings › Updates
    shows the state, that counts as told. No banner and no badge.
- Why: the owner made the in-app updater a blocker for 2.0. v2 only showed "Version X is available"
  after a manual check; the WPF app checks, downloads, verifies and installs, and shows its state in
  Settings and a banner. The handoff draws the up-to-date state only.
- Proposal: [`design/proposals/2026-10-23-v2-updater-states.html`](design/proposals/2026-10-23-v2-updater-states.html).
- Chosen option: the single suggestion, applied at once (within the system). Departure from WPF:
  the toast replaces the WPF browse banner and its menu item, which the design has no place for.

### 2026-10-23 · See all first paint and Home art after Settings
- What changed:
  - Recently Added's See all draws at once like the library grid: the library grid's skeleton
    cards after 150 ms until the first page lands, and the first page comes as soon as the server
    answers for it, while the order of every title is read in the background for the pages after
    it. Paging, the count of every title and never losing or repeating a title are unchanged.
  - Home stays in the window, collapsed, while another page shows, so its artwork is on screen in
    its first frame when it shows again (Settings and back, detail and back).
- Why: the 2026-10-22 post-merge capture of master 4e0e1c5 on the test VM. See all showed nothing
  at all (no grid, no count, no skeleton) for at least 0.5 s: it read every title's id and date
  before drawing. `normal-page-navigation.mp4`: back on Home from Settings every card was blank in
  the first frame and the artwork landed card by card over about 220 ms; the same blank frames are
  in every capture since Home is reused, low-end pass included, because XAML decodes an image
  again when its element comes back into the tree.
- Proposal: none; applied directly from the capture findings.
- Chosen option: the single fix per finding, within the system.

### 2026-10-23 · Play a link, open a file, and dropped links
- What changed:
  - Profile menu: "Play a link…" (`Icons.Link` U+E71B, Ctrl+L) and "Open file…" (`Icons.OpenFile`
    U+E8E5, Ctrl+O) between the switch submenus and Settings, in a group of their own, each with
    its shortcut as Settings shows Ctrl+,. Settings › Keys › Navigation lists both.
  - Play a link: the `LwDialog` keeps only the field (placeholder `https://`, left to right in every
    language), one inline error line and Play / Cancel. A link on the clipboard is filled in and
    selected; Play is disabled while the field is empty. A link with a user name or password gets
    the existing "Enter a full HTTP or HTTPS URL…" line, any other refusal the existing "Use a full
    http:// or https:// link to the media…" line.
  - Drop overlay (new component): while a file or a link (a web link, or text that is one) is
    dragged over the window, the content layer dims with `LwOverlayBrush`, the bar stays sharp, and
    one `LwPopupBrush` card (`LwPopupBorderBrush`, `LwGroupRadius`, 360 wide, padding 24, the
    toast's shadow) shows the link or file glyph at 36 in `LwText2Brush`, "Drop to play" at 15
    semibold and the link's host and path (left to right) or the file's name at 13 in
    `LwText2Brush`, trimmed at 300. Over the full player it covers the whole window. It appears at
    once and leaves on drop or drag-out. A drop replaces what plays; in a SyncPlay group the
    existing toast says to leave the group.
  - Player, for links: the title follows the path (the file name without its extension, or the
    folder above a generic `index`, `master`, `playlist`, `manifest`, `stream` or `live`) with the
    host as subtitle, or the host alone; mpv's media-title replaces it once the file has loaded,
    when it is a real name. Until the first frame a 40 px ring (`LwAccentFgBrush` on 14 % white)
    spins in the middle of the black surface (P1). A failed link has its own message (P4), and
    Info's play method reads "Link"; a local file opened this way reads "Local file".
  - A link or file opened over the detail page's preview takes the full window, and Back stops
    it instead of shrinking it onto that page's preview, also when the player was already full.
- Why: the owner found that "the stream via url is gone": v2 had kept only the unlisted Ctrl+L, and a
  link dragged onto the window was ignored.
- Proposal: [`design/proposals/2026-10-22-stream-url.html`](design/proposals/2026-10-22-stream-url.html).
- Chosen option: a mix the owner picked: A's two profile-menu items and slimmed dialog, C's drag and
  drop (not its Ctrl+V anywhere), and the "All" section. B (the search field taking links) was not
  built. Departures: the dialog's errors reuse the existing strings instead of A4's new wording;
  the Arabic "Open file…" keeps the existing "فتح ملف…" rather than A6's "فتح ملف محلي…"; mpv's
  media-title is read once, when the file has loaded, so an ICY name that changes later does not
  update the title.

### 2026-10-22 · Design-review capture fixes, third round
- What changed:
  - Leaving full screen: the content keeps the layout made for the window it returns to until XAML
    itself has the window's new size, and the top bar keeps the caption buttons' width through full
    screen, so the first frame in the restored window is the final layout.
  - Entering and leaving full screen: while the content is laid out for the size the window is
    about to have, the full player keeps being drawn at the window's current rect, controls hidden,
    and the window then changes in one step; the controls show once it has.
  - Leaving full screen for the preview: the shrink starts once the window is back, instead of
    with the window change, so it runs its whole 180 ms.
  - Player bar: when its two groups do not fit on one row (the compact bar in a 900 × 600
    window's preview), Audio and Subtitles fold into one Tracks button (`Icons.Subtitles`, name
    "Audio and subtitles", AutomationId `PlayerTracks`) whose flyout shows the audio and subtitle
    lists side by side, as design 13c draws Tracks; the row stays one row. They unfold when the
    row is wide enough again (`PlayerBar.FoldsTracks`).
  - Player flyouts open above the whole transport bar, 8 above its top and centred over their
    button, instead of above the button, which put them over the seek row.
  - Search: the results header shows its total only once every group has answered, and keeps the
    previous query's total while a new one is still coming in (`QuickSearch.HeaderTotal`); the
    groups and chips still land one by one.
  - Recently Added (row and See all): a season card is titled with its show and has the season's
    name ("Season 2", "Specials") as its line, as an episode card is titled with its show.
  - Player tooltips: they open for keyboard focus, or once the pointer has moved since the controls
    last appeared (`PlayerTooltips`); controls shown again under a resting pointer open none (rule
    change: when a tooltip opens over video).
  - Player audio and subtitle lists: a track the file names only by a language code ("eng") is
    listed by the language's name in the interface language ("English", "Englisch",
    `LanguageBadge.Name`), as the detail panel names it; a titled track reads "Title (English)".
  - Recently Added's See all pages like a library grid: it fixes the order of every title when it
    opens (ids and dates only) and reads the titles 100 at a time as the grid scrolls to within two
    screens of its end, with no cap, and counts every title; a title never goes missing because
    the library changed while scrolling or a page failed to load. Newest first, movies and videos by the date they were added and shows by the date of
    their newest episode, merged (`RecentList`, `RecentlyAddedFeed`); new episodes stand under
    their show, as in the row, never one card each. By name it reads one list. The Home row stays
    on the server's Latest list, now 24 per library (its own size), newest first across the
    libraries. Supersedes the shared 100-per-library Latest list of the entry below.
- Why: the 2026-10-22 post-merge capture of v2 on the test VM. `normal-player-shrink.mp4`
  (window 1264 × 792): frames 12–16 had Settings and the avatar under the minimise and maximise
  buttons (the bar had taken full screen's zero caption inset); frames 17–18 showed the 1920-wide
  layout clipped in the window, with no right group and no info panel (the layout was let go before
  XAML had the new size). `normal-player-grow.mp4` frames 13–18 and `lowend-player-grow.mp4`
  frame 3: the window-sized player in the top-left of the black full-screen window; frame 12 the
  monitor-wide layout clipped to the window just before. The frame log had `player_shrink` at
  2 frames, the longest 185.6 ms (369.3 ms low-end): the window change ran inside the shrink.
  `en-900x600-077-player-preview.png`: the compact bar's right-hand buttons wrapped onto a row
  above the transport buttons. `en-1920x1080-051-player-full-flyout-quality.png`: the Quality
  flyout covered the end of the seek bar and the remaining time. `en-1280x800-057` and
  `en-900x600-073-search-results.png`: "131 results" and no Episodes chip for "the", against 203
  with Episodes at 1920, 1050, de and ar. Not a size limit: the server has 72 movies, 32 shows,
  72 episodes, 3 collections and 24 people for "the", 203 in all; the Episodes request, the last
  of five started at once on a client that opens four connections to the server, and queued
  behind the Home refresh that ran just before, had not answered when the shot was taken, and
  the header read the partial 131 as the total. `en-1920x1080-008-section-see-all.png`: season
  cards read "Season 1", "Season 2", "Season 7" with no show; the server's Latest list groups
  several new episodes of one season as that season. `de-1920x1080-008-player-full-paused.png`:
  "Abspielen (Leertaste)" stood open over Play with no focus ring; the bar had appeared under the
  resting pointer when the grow ended. `en-1920x1080-049-player-full-flyout-audio.png`: the Audio
  flyout listed "eng" where the detail panel said "English". Recently Added's See all: the
  owner's decision that it paginates like the other libraries, with no 100-per-library cap.
- Proposal: none; applied directly from the capture findings.
- Chosen option: the single fix per finding, within the system.

### 2026-10-22 · Design-review capture rerun fixes
- What changed:
  - Dialogs: every ContentDialog is an `LwDialog`, which carries the AccentButton brushes in its
    own theme dictionaries, so its default button fills with `LwAccentBrush` (hover
    `LwAccentHoverBrush`) with white text and no border. The 2026-10-21 override at the root of
    `Tokens.xaml` never reached the button: WinUI's `AccentButtonStyle` finds its brushes in its
    own theme dictionaries first.
  - Playlists: an empty playlist (child count 0) shows the playlist glyph and no watched check on
    its tile, and the glyph in its page's header, whatever image tag or played state the server
    still reports. The rule is `PlaylistModel.HasEntries` / `ArtworkTag`.
  - Artwork: a picture already decoded in memory lands at once, so a page being shown (Home
    rebuilt after Settings) has its artwork in its first frame again; only pictures read from disk
    or the network go through the per-frame queue. The queue is held only while the player grows
    or shrinks and while the nav morph opens; the morph's closing and the chosen item's slide come
    with a page switch and no longer hold it.
  - Nav morph: the group opens no narrower than it is closed, as measured with its labels
    (`NavMorphWidth.Open`); the floor was three 64 px items, narrower than a closed group whose
    German or Arabic labels take up to 76 px each.
  - Focus: a page switch moves focus that was on the leaving page to the new page's host
    (`MainWindow.HoldFocusForPage`) instead of letting it fall to Back (rule change: where focus
    goes after navigation).
  - Recently Added: See all lists what the Home row lists, only longer: each video library's
    newest titles from the server's Latest list (new episodes under their show), the same 100 per
    library for both, newest first across the libraries (`HomeSections.LatestLibraries` /
    `Newest`). Both now ask for each title's date added, and a show for the date its newest
    episode was added, so the row interleaves the libraries by date instead of listing one
    library after the other.
- Why: the 2026-10-22 design-review capture of v2 at master 46a0fd2 on the test VM.
  `en-1920x1080-005-media-info-dialog.png`: Media info's Close button was still the light accent
  (240,140,156) with dark text. `en-1920x1080-021-playlists.png`: "LW M18 Adv" and "LW Queue
  Test", both 0 items, showed a stale series collage with a watched check; Jellyfin keeps an
  emptied playlist's collage tag and reports it played. `normal-page-navigation.mp4`: back on Home
  from Settings, every card stayed blank for about 170 ms (five 30 fps frames) and the art then
  landed at once, against about 50 ms before the per-frame queue, which also queued memory hits.
  The morph floor came from a code review of the same round, not from a screenshot.
  `en-1920x1080-008-section-see-all.png`: after See all, Back had a focus ring and its "Back
  (Alt+Left)" tooltip open; the focused control left with Home and focus fell to the bar's first
  button. The same shot showed an empty Recently Added: See all paged six Items requests of 100
  (about 1.2 s each on the test server, 9034 titles with every episode) before drawing anything,
  and the shot came first. That list was not the row's either: the row uses Latest (unwatched,
  grouped by show).
- Proposal: none; applied directly from the capture findings.
- Chosen option: the single fix per finding, within the system.

### 2026-10-22 · Detail: the page lays out once
- What changed: the detail page opens in its final layout and stays in it while its lookups
  answer. The preview's shape is decided when the page opens, from the size the list it was opened
  from carried (16:9 for a show, a season, or a title without one), and kept: a video of another
  shape plays letterboxed inside it rather than resizing it. More like this and Cast and crew hold
  their place from the start with placeholder cards of the real cards' size (`LwPlaceholderBrush`
  art, `LwCardRadius`, blank caption lines), invisible for the first 150 ms and skeletons after, as
  the library grid's skeleton draws them. A row with nothing to show keeps its place: its heading
  without the arrow, and a quiet line under it ("No similar titles", "No cast or crew listed", 13,
  `LwText2Brush`), instead of collapsing late and growing the preview. In the info panel the genres
  line is always there, OMDb scores take their own line under the chips, held from the start while a
  key is set, each action button keeps the width of its wider label (Play or Resume, Mark watched
  or Watched, Favorite or Favorited), the trailer button keeps an invisible slot at the end of the
  action row until the title has a trailer, and the seasons or episodes heading shows at once.
- Why: the owner saw the page "adjust multiple times until it stabilizes" after every open. The
  preview changed shape when the source's streams answered and again when mpv reported the video's
  size, the rows were first guessed at, then measured, then collapsed when empty, each move resized
  the preview and the side panel, and the season grid re-counted its columns after each.
- Proposal: none; the owner asked for the fix, and the skeleton follows the library grid's and the
  "skeletons only after 150 ms" rule.
- Chosen option: keep an empty row's place with a quiet line rather than collapse it once both
  rows are known, so no answer ever moves the layout; the cost is an empty band under a title with
  neither similar titles nor credits. The frame log's `detail_settle` line counts the layout
  changes after a detail page is shown (`layout_changes`, 0 when it laid out once).

### 2026-10-21 · Detail: the season and episode grid sizes its cards like the rows
- What changed: the info panel's season and episode grid no longer splits the panel into two
  stretched columns. It is a grid like the library's: as many columns as fit cards of the detail
  size, `floor((width + gap) / (card + gap))`, at least two, the cards stretched to share the
  leftover. Seasons use `LwDetailPosterWidth`, episodes `LwHomeWideCardWidth`, both × the window's
  `CardScale` like More like this, and the gaps (12, 14) and card captions scale with them; badges
  stay as drawn. On the owner's 2560 DIP window (1.5×, posters 210) a 740 DIP panel shows three
  seasons of 222, a 990 DIP panel four of 224.5 and a 1300 DIP panel five of 238; the narrowest
  panel keeps two columns of about 125. Episodes keep two columns until three 444 DIP cards fit.
  The arithmetic is `DetailModel.ChildGrid` in `LightWeaver.Core/Details`.
- Why: the owner found the seasons "a bit too big". The card-scale experiment shrank the 16:9
  preview, so the info panel grew from about 740 to 990 DIP, and its two stretched columns made
  each season poster about 460 DIP wide, more than twice the 210 DIP posters of More like this
  below it, with only two seasons in view.
- Proposal: none; one concrete problem with one fix, applied directly.
- Chosen option: stretch the cards to fill the row, as the library grid and See more do, rather
  than leave a ragged gap at the panel's edge.

### 2026-10-21 · Focus: Advanced Search and toasts
- What changed: opening Advanced Search's filter column focuses its first filter (Movies) instead
  of its Close button, so Esc still closes it and no tooltip opens over the top bar. A toast
  refuses focus that falls to it when the focused control leaves the tree, and Settings gives
  focus back to a control it rebuilds; a click, a tap or Tab still reaches a toast's buttons.
- Why: captures `en-1920x1080-039`, `en-1050x700-066` and `de`/`ar` `-1920x1080-005`: the Close
  filters tooltip covered Playlists in the top bar. `en-1920x1080-036-toast.png`: Clear on
  Settings → Storage rebuilt the page, focus fell onto the new toast's dismiss button and drew
  its focus ring.
- Proposal: none; applied directly from the capture findings.
- Chosen option: the single fix (rule change: where focus goes when a panel opens; toasts).

### 2026-10-21 · Playlists: tiles agree with their page
- What changed: a playlist tile without artwork shows the playlist glyph (`Icons.PlaylistPlay`,
  40, `LwText3Brush`) in the middle of its square, as the playlist page's header does. An empty
  playlist's line reads "0 items" with no runtime.
- Why: capture `en-1920x1080-021-playlists.png`: the empty "LW suite 4780" was a blank tile, and
  "LW M18 Adv" read "0 items · 52 min" under a collage. Jellyfin 12.2 keeps a playlist's runtime
  and collage after its entries leave the library, while its child count and its item list are
  live and both 0, so the runtime was the stale value.
- Proposal: none; applied directly from the capture findings.
- Chosen option: the single fix, within the system.

### 2026-10-21 · Top bar: search field minimum 160
- What changed: the search field's minimum width is 160 instead of the 200 drawn in
  `Chrome.dc.html`, and the nav morph grows into the field down to 160.
- Why: at the 900 × 600 minimum window the bar's fixed parts leave the field about 165 DIP, so
  the 200 minimum was clipped and cut off the search button (`en-900x600-069-home.png`,
  `-071-search-results.png`). At 160 the field keeps clear, Advanced search and search, with
  about 50 DIP for typed text.
- Proposal: none; applied directly from the capture findings.
- Chosen option: the single fix (rule change: the search field's minimum).

### 2026-10-21 · Search: groups fill the window's width
- What changed: under All, a group's row holds every card that starts inside the page's width,
  the last one clipping at the trailing edge, instead of at most eight. "See all" shows when a
  card is cut off or the group has more. At 1920 that is 16 posters or 8 episode stills; each
  group still asks the server for 24. The arithmetic is `QuickSearch.RowFit`.
- Why: capture `en-1920x1080-037-search-results.png` (and `de`/`ar` `-004`): Movies, Shows and
  People stopped after eight cards at x ≈ 955 and left half the window empty, against "grids add
  columns as the window grows".
- Proposal: none; applied directly from the capture findings.
- Chosen option: the single fix, within the system (design 04 note 4d: rows clip at the
  trailing edge, like Home rows).

### 2026-10-21 · Detail page: a usable preview in small windows
- What changed: More like this and Cast and crew sit side by side (3 : 2) at every width; below
  1100 DIP they used to stack. The rows may press the preview down to 300 DIP tall and no
  further (`DetailModel.PreviewMinHeight`); a scope film whose cell is too narrow for that keeps
  its shape below it. In a shorter window the page scrolls the rows into view. At 1050 × 700 the
  preview is about 530 × 300 beside a 480 wide info panel; at 900 × 600 it is 533 × 300 and the
  rows start just below the fold.
- Why: the design-review capture of 2026-10-21 (`en-1050x700-064-detail-movie.png`) showed a
  preview about 35 DIP tall and the info panel as a strip, because the two stacked rows took the
  whole height and the preview had no floor. 300 holds the preview's title, Play and a one-line
  bar without overlap.
- Proposal: none; applied directly from the capture findings.
- Chosen option: the single fix. Side-by-side rows follow the owner's detail sketch
  (`project/uploads/movie-details.png`) and the "rows clip at the trailing edge" rule.

### 2026-10-21 · Player: full screen covers the monitor, taskbar included
- What changed: full screen covers the whole monitor the window is on with the taskbar beneath it,
  so the video uses the monitor's full height and the player's own chrome (the 110 DIP top
  gradient with Back, title and episode; the transport bar) lies over the video from the top edge
  to the bottom edge and idle-hides with the cursor after 2.5 s, as 13 and 13i draw it. The content
  is laid out for the new size before the window grows or shrinks, on entering and on leaving.
- Why: the owner's screenshot (4K at 150 %) showed the taskbar over the player, hiding the bar's
  button row so only the seek row showed above it. The black band at the top of that screenshot is
  the film's own letterbox (a 2:1 picture on a 16:9 screen, about 120 px at top and bottom), with
  Back and the title over it as designed; it is not reserved layout. A guest capture also showed the
  old window-sized layout in the corner of a black full-screen window for about 300 ms on entering,
  and a cropped video on leaving.
- Proposal: none; a defect fix that brings full screen back to the existing design, within the
  system.
- Chosen option: the single fix, applied at once.

### 2026-10-21 · Design-review capture fixes
- What changed:
  - Icons: `Icons.Clock` moved from U+E917 (Segoe Fluent Icons only) to U+E121, the same drawing,
    which Segoe MDL2 Assets also has; Countdown uses the new `Icons.Timer` (stopwatch, the
    design's "timer"). A Settings row's icon slot is a fixed 18 px.
  - Player flyouts and the detail preview's menus: dim glass `LwPlayerGlassBrush`
    (rgba(18,18,20,0.72), design 13's flyout fill) with `LwPlayerGlassBorderBrush` (9 % white) and
    `LwGroupRadius`, through `LwPlayerFlyoutPresenter` and `LwPlayerMenuPresenter`.
  - Right to left: `LwDirectionalIcon` / `Ui.DirectionalIcon` mirror back and forward, row and
    See all arrows, the row and nav-morph paging chevrons, the See more breadcrumb and the login
    and offline-view chevrons.
  - Subtitle colour: `LwSwatch`, a 20 px circle 8 px apart with a 2 px `LwAccentFgBrush` ring
    2 px outside the chosen one (new token `LwSwatchEdgeBrush` for the dark edge); white shows as
    chosen while the setting is unset.
  - Dialogs: WinUI's AccentButton brushes point at `LwAccentBrush` (hover `LwAccentHoverBrush`)
    with white text, so every ContentDialog's default button fills with the garnet accent
    (superseded 2026-10-22: the override did not reach the button; see `LwDialog`).
  - Top bar: Home, Libraries, Playlists, Downloads, SyncPlay and Settings take their label's
    width, 64 to 76 px (`LwBarItemFlexible`, tokens `LwBarItemWidth`, `LwBarItemMaxWidth`), from a
    1000 DIP window; the nav morph's items stay 64.
  - Overviews and bios: `TextReadingOrder` and `TextAlignment` `DetectFromContent`.
  - Motion: flyouts open without animation, as the code has done since `FastUi`.
- Why: the 2026-10-21 design-review capture of v2 at master 19d35b2 on the test VM showed empty
  glyph boxes in two Settings rows, opaque grey player flyouts, unmirrored arrows in Arabic, stock
  radio circles in the colour picker, the light accent as a dialog fill, German and Arabic bar
  labels cut short at 1920 DIP and an English synopsis right-aligned in Arabic.
- Proposal: none; the fixes follow the design files (`SettingsPane.dc.html`, `13 Player.dc.html`,
  `Chrome.dc.html`) and the Rules above.
- Chosen option: the single fix per finding, applied at once. Departures: the flyout glass is the
  design's drawn 72 %, not the 66 % of the Skip intro pill; top-bar labels may run to 72 px where
  Chrome.dc.html draws 60, because German and Arabic labels do not fit 60.

### 2026-10-21 · Motion: grow, shrink and nav morph on the compositor
- What changed: nothing on screen is meant to look different; the motion is implemented again.
  The player's grow and shrink (180 ms, cubic ease-out, all edges together, card or preview rect
  to window and back, corners `LwCardRadius` / `LwGroupRadius` as before) is laid out once at the
  end rect and drawn at each frame's rect by a compositor scale, translation and rounded clip that
  keep the picture's shape. The nav morph (240 ms, cubic ease-out) lays the group out at its wider
  width and draws the pill (`LwGlassBrush` fill, `LwStrokeBrush` 1 px stroke, `LwGroupRadius`) and
  the search field's chrome (`LwControlRadius`) as composition shapes, reveals the items with a
  clip and moves the search field's text and buttons with the group; right-to-left mirrors it as
  before. Artwork that finishes during these animations lands after them, at most 250 ms late,
  and otherwise a few cards per frame.
- Why: the owner asked that thumbnails loading never make the animations lag. Both animations
  were driven from the UI thread (a per-frame resize and a dependent width storyboard), and a VM
  capture of master showed the grow as 4 frames in 193 ms with one of 165 ms and the morph at a
  p95 of 50 to 132 ms, so they looked like cuts.
- Proposal: none; an implementation change that keeps the existing motion.
- Chosen option: applied at once at the owner's request. Two small differences: a search
  placeholder or query long enough to be trimmed shows the trimming of the morph's narrower end
  for its 240 ms, and a picture whose first video frame arrives mid-grow keeps the artwork's
  shape for the rest of that grow.

### 2026-10-21 · Experiment: card rows grow with the window
- What changed: from 1440 DIP of window width, Home rows and the detail page's More like this and
  Cast and crew scale linearly with the window, × width ÷ 1440, capped at 1.5× (reached at
  2160 DIP). Cards, the gaps between them and between Home rows take the full factor; card titles
  and subtitles, the caption gap, row headings and their arrow grow by half the factor, so at 1.5×
  wide cards are 444 and posters 225 while titles are 16.25, subtitles 14.4 and headings 21.25.
  Below 1440 nothing changes. The rows re-lay out only when the factor moves by 2 % or reaches
  either end, and a card's artwork loads again only once the card is more than 15 % wider than it
  was decoded for, at the `ArtworkSizes` step that fits. New tokens: `LwHomeWideCardWidth`,
  `LwHomePosterWidth`, `LwDetailPosterWidth`, `LwDetailPersonWidth` (the former literals),
  `LwCardScaleFrom`, `LwCardScaleMax`, `LwCardTextShare`; the arithmetic is `CardScale` in
  `LightWeaver.Core/Home`.
- Why: on the owner's 2560 × 1392 DIP window (4K at 150 %) Home left its bottom third empty,
  because the card sizes were drawn for about 1440 × 900 and stayed fixed. The owner asked to try
  bigger items and judge by eye. Text follows half the growth, not all of it, so captions stay
  below the 22 page title and do not outgrow the bar's 13 text, yet are not left small under the
  larger art; set `LwCardTextShare` to 1 to scale text with the cards. On the detail page the
  taller rows leave less height for the preview, so a 16:9 preview gets smaller and the info panel
  wider.
- Proposal: `docs/design-proposals/2026-10-21-home-and-detail-density.html` in the maintainer's private companion repository;
  it is built from screenshots of a personal library, so it is not published here.
- Chosen option: Home B, "Cards grow with the window", as an experiment for the owner to judge.
  Unlike the proposal, which kept text and gaps fixed, captions, headings and gaps scale too, and
  the detail rows are included.

### 2026-10-20 · Player: RTX VSR and RTX HDR toggles
- What changed: two text toggles, "RTX VSR" and "RTX HDR", at the start of the player bar's right
  group (`LwBarToggle`: 38 high, min width 44, padding 8,0, 13 semibold, `LwControlRadius`; off
  transparent with 78 % white text, on `LwAccentSoftBrush` with white text; hover and press snap to
  `LwHoverBrush` / `LwPressBrush`). Tooltip and name "RTX Video Super Resolution" / "RTX Video
  HDR", AutomationIds `PlayerVsrToggle` / `PlayerRtxHdrToggle`; a click does not take focus. A
  toggle saves the Settings default and the save applies it to the playing video, as a change on
  the Settings page now does too. Shown only while RTX can apply (an RTX GPU and an SDR source) and
  never in the compact bar or the detail page's preselection bar; no shortcut.
- Why: the owner could only turn the RTX filters off on the Settings page; the WPF player has
  these toggles in its overlay.
- Proposal: [`design/proposals/2026-10-20-player-rtx-toggles.html`](design/proposals/2026-10-20-player-rtx-toggles.html).
- Chosen option: the single suggestion, applied at once (parity with WPF; new component variant
  `LwBarToggle`).

### 2026-10-20 · Player transitions: card play and leaving full screen
- What changed: Play on a card (its button or its menu) grows the player out of the card's artwork
  rect (`LwCardRadius`) into the window in the same 180 ms ease-out as the detail preview, controls
  hidden until it lands. From a new title's request until its first frame is shown the video is
  held back: the card's picture fitted on black, or in the detail preview the page's backdrop,
  stands in; the video then fades in over 120 ms with its fit already set. Full screen covers the
  monitor without a presenter switch and without Windows' window animation; leaving puts the
  window back in one step (a maximized window stays maximized) and the shrink lands on the
  preview's final rect, bar row included.
- Why: the owner's recordings showed a card play as an instant cut, the previous file's last frame
  for 0.7 s, five black frames and a size jump; and leaving full screen doubled the picture under
  Windows' restore animation, drew the window on the second monitor and jumped twice at the end.
- Proposal: `docs/design-proposals/2026-10-20-player-transitions.html` in the maintainer's private companion repository;
  it is built from screenshots of a personal library, so it is not published here.
- Chosen option: the single suggestion, applied at once (motion within the system: the existing
  180 ms grow and the toast's 120 ms fade).

### 2026-10-20 · Advanced search: manual Refresh
- What changed: a Refresh button at the trailing end of the results header (`LwSubtleButton`,
  32 × 32, `Icons.Refresh` at 16, tooltip "Refresh (F5)", AutomationId `AdvancedSearchRefresh`)
  and `F5` while the page is shown and not under the full player or mini-player. Refresh re-runs the current query for every title loaded so
  far and shows the existing Updating ring and text; the grid keeps its scroll position and full
  opacity, and a failed refresh keeps the results. Every answer, filter changes included, now
  lands as a keyed diff by item id instead of a clear and refill. Titles that left are removed and
  new ones inserted; a title that changed place is removed and inserted again (the grid recycles
  moved cards, so the diff never raises a move), and keeps its decoded art only when the grid
  returns the same card instance; titles that stay in place keep their cards and art and take the
  server's newer copy in place. Filter changes still dim to 0.6.
- Why: going Back keeps the old grid, so a title watched on another device still showed as
  unplayed, and the only way to refresh was to change a filter and change it back.
- Proposal: [`design/proposals/2026-10-20-advanced-search-live-updates.html`](design/proposals/2026-10-20-advanced-search-live-updates.html).
- Chosen option: A, manual Refresh button only — owner declined live updates. The status line,
  socket subscriptions, fallback polling and change summaries of option A were not built.
