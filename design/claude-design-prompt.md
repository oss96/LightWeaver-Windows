# Claude Design prompt — LightWeaver design system

Design a complete design system and key-screen mockups for **LightWeaver**, a native
Windows desktop media client for Jellyfin (WPF, Fluent-era Windows 11 app). Deliver it as a
single self-contained HTML design-spec page (tokens + component specs + full-screen
mockups), suitable as an implementation handoff.

## Brand & mood — "woven light"

The name nods to the Lightweavers of Brandon Sanderson's Cosmere: Radiants who weave
Stormlight into illusions. That is the emotional core of the design — **a dark, storm-quiet
world where light is the precious, living element**. The app's chrome is the darkness; the
media (posters, backdrops, video) is the light being woven.

Interpret, don't copy: no copyrighted names, symbols, or artwork anywhere in the UI. The
inspiration should read as atmosphere to a fan and simply as "gorgeous dark media app" to
everyone else.

Design language cues to build from:

- **Base: highstorm night.** Near-black surfaces with a cold cast — deep slate/indigo
  rather than neutral gray. Layered elevations feel like depths of storm cloud.
- **Stormlight as the interaction language.** Focus, hover, active, and progress states
  glow softly — a cool luminous white with a faint blue-teal haze, like light escaping an
  infused gem. Glow is diffuse and restrained: luminance bloom, never neon strokes.
- **Gemstone accents.** A small accent family drawn from polished gems on dark cloth.
  Primary accent: **garnet** (the Lightweavers' stone) — a deep, slightly luminous red for
  primary actions and brand moments. Support it with sapphire and emerald tones for
  semantic states (info/success), amber-topaz for warnings. Gems read as *lit from within*.
- **Woven-light details.** Sparse, subtle ornamentation: fine interlaced line patterns or
  faint fractal-geometric filigree (a nod to Cryptic patterns) used only in empty states,
  the login screen, and section dividers — never behind content grids.
- **Glyph-like iconography.** Geometric, slightly calligraphic icon set with consistent
  stroke weight; feels like a glyph alphabet without copying any real glyphs.
- **Typography.** A display face with a hint of the epic for titles/hero text (elegant,
  high-contrast serif or inscribed-feeling sans — still crisply modern), a clean humanist
  sans for UI, and a monospace for technical/log surfaces. All must be freely licensable
  and bundleable.

## Product context (what the screens must do)

LightWeaver is a performance-obsessed Jellyfin client. Video playback is rendered natively
by mpv underneath the UI; playback chrome floats over the video as an overlay. Dark theme
only. Mouse + keyboard desktop ergonomics (hover states matter); window is resizable from
~1000×700 up to 4K fullscreen.

Screens to design (full mockups):

1. **Connect / Login** — server URL entry, then user credentials. This is the brand moment:
   the one screen allowed full atmosphere (woven-light ornament, hero glow).
2. **Home** — Continue Watching (with progress), Next Up, Recently Added rows; horizontal
   poster/landscape card rails; persistent left nav.
3. **Library grid** — poster grid with sort/filter controls, alphabet or scroll position
   aid, pagination-friendly.
4. **Item detail** — backdrop hero image fading into the dark base, poster, metadata,
   play/resume actions, season/episode list for series, cast row.
5. **Player OSD overlay** — the critical screen. It floats over live video, so it must be
   high-contrast but **low total luminance** (video may be HDR; avoid large bright
   surfaces). Include: seek bar with buffered range + chapter ticks, time, play/pause,
   skip, volume, audio/subtitle track selectors, settings gear, an episode title area, and
   quality/feature badges (e.g. "RTX HDR", "RTX VSR", "4K", "HDR10") as small glowing gem
   pills. Also design its hidden/idle behavior (fade-out).
6. **Settings** — sectioned settings with toggles, dropdowns, and a playback section where
   RTX Video Super Resolution / RTX Video HDR toggles live.

## Component inventory (spec every state: rest / hover / pressed / focused / disabled)

Buttons (primary garnet, secondary, ghost, destructive), toggle switch, segmented control,
text field, dropdown/select, left navigation rail (icons + labels, collapsed variant),
search field, cards (poster, landscape, episode row), progress indicators (watched bar on
cards, buffering spinner as a slowly breathing glow), status/badge pills (the gem pills),
seek bar, volume slider, context menu, modal dialog, toast/notification, tooltip, empty
state, skeleton loaders.

## Tokens & constraints

- Full token sheet: color palette (base ladder, text ladder, accent gems, semantic),
  spacing scale, radius ladder, elevation/glow levels, type ladder, motion durations.
- Dark only; WCAG AA contrast for all text on its surface.
- Implementation target is WPF: gradients, opacity, and soft shadows are cheap; full-screen
  backdrop blur is not — use blur sparingly and always with a solid-ish fallback.
- Motion: slow, weighty, air-like — light drifting, not bouncing. Specify durations/easings.
- The video and artwork are the heroes. When in doubt, make the chrome darker and quieter
  and let the glow carry the identity.
