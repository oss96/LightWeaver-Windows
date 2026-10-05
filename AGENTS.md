# Codex Instructions for LightWeaver

## Project overview
LightWeaver is a native Windows Jellyfin client: a C# / .NET 10 WPF (Fluent dark) shell around an
embedded libmpv player. mpv does the decoding and rendering in its own D3D11 window, which is what
makes RTX Video Super Resolution, RTX Video HDR and HDR passthrough possible. It is the Windows
member of a per-platform client family (LightWeaver-Windows / -MacOS / -Android / -iOS, one repo
each).

## Build & run
```
dotnet build
dotnet run --project src/LightWeaver
dotnet run --project src/LightWeaver -- "D:\movie.mkv"
```
Requires the .NET 10 SDK and `libmpv-2.dll` (mpv >= 0.40) in `native/win-x64/` or on `PATH`.
Debug builds use an isolated app-data folder (`%LOCALAPPDATA%\LightWeaver-Debug`), so running the
dev build never touches an installed build's login, settings or watched state.

## Load-bearing architecture rule
mpv **owns its rendering window** (child HWND via `wid`, `vo=gpu-next`, D3D11). Never
composite video through WPF (`D3DImage`, render API to texture, etc.) — that breaks RTX
Video HDR and HDR passthrough, which are core requirements. Playback controls go in a
transparent overlay window, not inside the video HWND's rect.

## Rules
- Localize UI text while developing. Add or update keyed English strings in
  `localization/catalog.json` in the same change, and use `L.Text`, `L.Format`, or
  `{loc:Text Key}` in the UI. Update existing translations for changed text; translations
  awaiting review must stay marked stale so the app falls back to English. Preserve format
  placeholders. Protocol identifiers, saved values, resource keys, automation IDs and logs
  stay invariant.
- Every UI-text handoff must include a translation-editor link filtered to the strings
  created or modified by that work. Use `http://127.0.0.1:5386/?changed=<base-commit>` with
  the full commit hash from before the change. Start the local editor before handing over
  the link. Use a fixed base commit so the filter still works after committing the change.
- `dotnet build -warnaserror` must pass before every commit. Warnings are errors here; do not
  commit around them.
- After every significant code change, update `README.md`.
- Match the surrounding code. The project has settled patterns for view models, mpv property
  access, settings persistence and logging — use them rather than introducing a new one.

## Where things live
- `README.md` — features, requirements, install, build, shortcuts, release build
- `docs/CODE-SIGNING.md` — Authenticode setup for `publish.ps1` / `installer.ps1`
- `design/` — the "woven light" design system: prompts, spec and mockups

Open work is tracked by the maintainer outside the repository.

## Maintainer setup
If `.agents/internal/agent/AGENTS.md` exists in this checkout, read it before doing anything else; it carries the maintainer's private environment, verification and release process, and overrides this file where they differ. Public clones do not have it and need nothing from it.
