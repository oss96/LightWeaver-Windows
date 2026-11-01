# Codex Instructions for LightWeaver

## Project overview
LightWeaver is a native Windows Jellyfin client around an embedded libmpv player, in C# / .NET 10.
The shipping app, from 2.0, is a WinUI 3 app on the current design in `DESIGN.md`
(`src/LightWeaver.WinUI`, built as `LightWeaver.exe`, with the WPF-free shared code linked through
`src/LightWeaver.Core`). The 1.x WPF (Fluent dark) app (`src/LightWeaver`, on the legacy "woven
light" design) stays in the repository and the solution but is no longer released. mpv does the
decoding and rendering with D3D11 itself, which is what makes RTX Video Super Resolution, RTX Video
HDR and HDR passthrough possible. It is the Windows member of a per-platform client family
(LightWeaver-Windows / -MacOS / -Android / -iOS, one repo each).

## Build & run
```
dotnet build
dotnet run --project src/LightWeaver.WinUI
dotnet run --project src/LightWeaver.WinUI -- "D:\movie.mkv"
dotnet run --project src/LightWeaver
```
Requires the .NET 10 SDK and `libmpv-2.dll` (mpv >= 0.40) in `native/win-x64/` or on `PATH`.
Debug builds use an isolated app-data folder (`%LOCALAPPDATA%\LightWeaver-Debug`), so running the
dev build never touches an installed build's login, settings or watched state. The app version
lives only in `Directory.Build.props`.

## Load-bearing architecture rule
mpv **renders and presents the video itself** with `vo=gpu-next` on D3D11, into a target it owns:
in v2 a composition swapchain (`MpvVideoTarget.Composition`) shown by a `SwapChainPanel` behind the
WinUI controls, configured for HDR from the display's DXGI peak, contrast and primaries; in the
WPF app a child HWND via `wid`, with the controls in a transparent overlay window. Never route
frames through the UI framework (`D3DImage`, the render API into a texture, copying frames into
XAML) — that breaks RTX Video HDR and HDR passthrough, which are core requirements.

## Rules
- Localize UI text while developing. Add or update keyed English strings in
  `localization/catalog.json` in the same change, and use `L.Text`, `L.Format`, or
  `{loc:Text Key}` in the UI. Preserve format placeholders. Protocol identifiers, saved
  values, resource keys, automation IDs and logs stay invariant.
- Always write the translations. Every new or changed English string gets a translation in
  every language under `localization/locales/` (German and Arabic) in the same change; never
  hand over an empty translation. Each one is a draft: its `sourceHashes` entry stays empty,
  so the app falls back to English until the owner approves it in the translation editor,
  which records the hash. For changed text, rewrite the existing translation and empty its
  hash.
- Every UI-text handoff must include a translation-editor link filtered to the strings
  created or modified by that work. Use `http://127.0.0.1:5386/?changed=<base-commit>` with
  the full commit hash from before the change. Before sharing any translation-editor link
  for the user to inspect or approve translations, check that the local editor server is
  running and start it if it is not. Confirm it responds before handing over the link.
  Starting the editor opens its session link in the user's browser, after which plain
  links work there. Use a fixed base commit so the filter still works after committing
  the change.
- `dotnet build -warnaserror` must pass before every commit. Warnings are errors here; do not
  commit around them.
- Tests: add or update a unit test (`tests/*.Tests`) with every logic change, and the static
  checks (`tests/FastUi.Tests`, `tests/LocalizationAudit`, `tests/LocalizationRuntime`) with every
  UI change; both run before a merge. Run `tests/PlaybackHarness` only when asked. The
  maintainer's UI end-to-end suites are updated only when the UI changes, and run before a new
  release is created. Keep tests lean and relevant.
- After every significant code change, update `README.md`.
- Match the surrounding code. The project has settled patterns for view models, mpv property
  access, settings persistence and logging — use them rather than introducing a new one.
- Keyboard shortcuts depend on focus. Whenever a change touches a shortcut, a window, a page,
  the player or a flyout, check which element holds keyboard focus by default and after every
  transition (window shown, page shown, player opened, grown or shrunk, flyout closed, video
  clicked), and that the shortcut handler receives the key from there. A shortcut that only
  works after an extra click is a focus bug.

## Where things live
- `README.md` — features, requirements, install, build, shortcuts, release build
- `docs/CODE-SIGNING.md` — Authenticode setup for `publish.ps1` / `installer.ps1`
- `DESIGN.md` — the current design (Claude Design "Screen-group design spec", handed off
  2026-10-17): direction, system, components, screens, rules and change log. Start design work here
- `design/claude-design/screen-group-design-spec/` — that handoff, frozen: never edit it
- `design/proposals/` — design change proposals, each linked from a `DESIGN.md` change entry
- `design/LightWeaver Jellyfin design system/` and the other `design/` prompts and packages — the
  legacy "woven light" design of the 1.x WPF shell, kept with it

Open work is tracked by the maintainer outside the repository.

## Maintainer setup
If `.agents/internal/agent/AGENTS.md` exists in this checkout, read it before doing anything else; it carries the maintainer's private environment, verification and release process, and overrides this file where they differ. Public clones do not have it and need nothing from it.
