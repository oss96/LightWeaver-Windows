# Advanced Search redesign — spec

Redesign of the Advanced Search screen inside the shipped **woven light** system. Companion
mockups: `mockup-main.html` (populated, mixed-type state, 1440px) and `mockup-states.html`
(states a–f). Every value below is an existing `Lw*` token unless flagged **NEW**.

---

## Problem → solution checklist

| # | Problem | Solution |
|---|---------|----------|
| 1 | Mixed card shapes clash | **Type-grouped sections.** When >1 item type is active, results render as vertical sections (Movies / Series / Episodes) — each a homogeneous wrap-grid of its native card (poster 152×228 / landscape 292×164). Mixed mode caps each section at its first page (24 items) with a **"Show only \<type\> →"** link that sets the Type facet and switches to the single-type infinite grid (today's behavior, unchanged). No ragged rows, no cropped art. |
| 2 | Wasted width / too few columns | The grid row is a WrapPanel that fills the content width: page gutters 40 (`LwSp8`), card gutter 16 (`LwSp4`). Columns = `floor((W − 80 + 16) / 168)` for posters (168 = 152 + 16): ≈6 at 1440 (rail expanded), ≈5 at 1040 (rail collapsed), ≈9–10 at 1920, more at 4K. Remove the current fixed `Margin 0,0,30,0` + narrow WrapPanel constraint; `HorizontalAlignment=Stretch`, left-aligned rows. |
| 3 | Filter panel wraps awkwardly | **Deliberate two-row filter band** (not opportunistic wrapping). Row A: Search · Type · Genre · Watched. Row B: Year · Sort · Order, with the **result summary docked right**. The band is a storm-1 area closed by a bottom hairline (`LwHairlineBrush`), matching the library header language. At narrow widths only Search (300→220) and Genre (210→180) shrink; rows never re-flow into different rows. |
| 4 | Year fields read as empty | Caption `YEAR`, two 74-wide `LwTextField`s with placeholders **"From"** / **"To"** (`LwText3Brush`), en-dash between (`LwText3Brush`). Same focus ring as every field. |
| 5 | Two search inputs | **The header's global search pill is hidden on this screen.** The on-screen SEARCH field is the single input and receives focus on entry. Back/Forward + title stay. (Model: the pill is quick-search — a different, navigating control; two visible inputs is one too many.) |
| 6 | Redundant "Advanced" affordance | **Hidden** while on the screen (together with the pill; the title "Advanced search" is the "you are here" state). Returns on navigation away. |
| 7 | Poor default landing | **No auto-dump.** With no term and no filter, show a prompt state: the shared EmptyState gem mark + Marcellus headline **"Search everything"** + subline + three **starter chips** (Browse movies / series / episodes — chip = `LwChipFillBrush`/`LwChipBorderBrush`, text `LwLightCoreColor`) that set the Type facet and run. The first query runs when a term or any facet is set. Deep-linked entries (person/genre) carry a facet, so they load immediately. No-artwork fallback and no-results states below. |
| 8 | No result feedback | **Result summary**, mono (`LwTypeMono`, `LwText3Brush`; count value `LwText2Brush`), docked right in filter Row B: `212 results · 34 movies · 8 series · 170 episodes` (per-type breakdown only in mixed mode). Section headers additionally carry their own mono count. |
| 9 | Multi-select not discoverable | CheckDropdown value area shows joined labels (up to 2) + a **count badge** (pill 18h, `LwChipFillBrush` fill, `LwChipBorderBrush` border, `LwLightCoreColor` 11/600 text): closed reads "Action, Sci-Fi ②" or "Action ＋1". Open list rows get real 16-sq checkboxes (border `LwStrokeBrush`; checked: `LwLightDimColor` fill, `LwLightHazeColor` border, `LwLightCoreColor` check) and a **"Clear selection"** footer row. Additionally every **engaged** facet (any non-default value) lights its border `LwLightHazeColor` + `LwGlowFocus`-style 12px bloom at 0.14 — active filters are visible at a glance. |
| 10 | Ambiguous sort order | Order becomes a **labelled direction toggle** (38h, `LwTextField` chrome): arrow glyph + text that adapts to the sort field — Name → "A–Z"/"Z–A"; Date added / Premiere → "Newest"/"Oldest"; Community rating → "Highest"/"Lowest"; Random → control hidden. Tooltip: "Sort direction". |
| 11 | Caption legibility | Facet captions move from 10px mono `LwText4Brush` (disabled color — fails AA) to **11.5px SemiBold uppercase `LwFontUi`, `LwText3Brush`** (`LwTypeLabel` at 11.5) — #79809A on #0C0E16 ≈ 4.7:1, AA for this size/weight. |

---

## Layout

- Shell unchanged: nav rail (expanded 232 / collapsed 64 below 1160px), header `40,20,28,8`
  padding with Back/Forward ghosts + `LwTypeTitle` 20px "Advanced search".
- **Filter band**: padding `40,10,40,14`, bottom border 1px `LwHairlineBrush`. Facet = caption
  (5px gap) above control. Control gap 14px; row gap 12px. All filter controls are **38px
  high** (unify `LwDropdown` height 34 → 38 here via explicit `Height=38`).
- Row A: SEARCH (300, shrinks to 220 min) · TYPE (170) · GENRE (210, shrinks to 180 min) ·
  WATCHED (segmented, natural width).
- Row B: YEAR (74 – 74) · SORT (170) · ORDER (labelled toggle) · … · result summary
  (right-docked, baseline-aligned with controls).
- **Deep-link seed chip** (state f): when opened with `PersonIds` (M5) or a seeded genre
  (M6), the seed renders as the first facet of Row A — caption `PERSON` / `GENRE`, a
  **removable garnet context chip**: pill (`LwRPill`), 38h, fill `#1AD14560` (garnet-lit
  0.10a — **NEW brush `LwGarnetChipFillBrush`**, justified: no garnet chip fill exists;
  same 0.10a glass recipe as `LwChipFillBrush`), border `#73D14560` (garnet-lit 0.45a —
  **NEW `LwGarnetChipBorderBrush`**), text `#EEB9C4`, ✕ button (22-sq ghost, hover
  `#40D14560`). Removing the chip clears the seed and re-queries. A seeded genre also
  checks its row in the Genre dropdown (chip and checkbox stay in sync).

## Results area

- Padding `40,6,40,40`; vertical scroll on the whole results area (sections scroll together).
- **Section header**: Marcellus 22 (`LwTypeTitle` at 22) + mono count (`LwTypeMono`,
  `LwText3Brush`) + right-aligned link "Show only \<type\> →" (`LwLinkButton`, 13px,
  `LwLightBaseBrush`, hover `LwLightCoreBrush` + `LwGlowPillBlue` text glow). First header
  top margin 14, subsequent 26 (`LwSp6`+2).
- Cards are the **existing shared templates untouched** (PosterCardTemplate,
  EpisodeCardTemplate incl. badges, progress, hover action bar, no-artwork glyph fallback).
- **Mixed mode** (≥2 types selected AND >0 results in ≥2 sections): sections as above,
  each capped at 24 with "Show only". **Single-type mode**: today's infinite-scroll wrap
  grid, plus the result summary. If mixed mode has hits in only one section, render
  single-type layout labeled with that section's name.
- Sorting/order/watched/year/term apply to every section identically (one query per type,
  same parameters).

## Component states

**Filter field (TextField / CheckDropdown / Dropdown / order toggle)** — chrome per
`LwTextField`/`LwDropdown`: storm-2 fill, `LwStrokeBrush` border, r8 (`LwR2`).
- Hover: border `LwFocusBorderSoftBrush`.
- Focused/open: border `LwFocusBorderBrush` + `LwGlowFocus`.
- **Engaged (non-default value)**: border `LwLightHazeColor` + 12px stormlight bloom 0.14a
  (`LwGlowFocus` recipe at reduced opacity — reuse `LwGlowToggleOn`).
- Disabled: fill `LwSecondaryDisabledFillBrush`, border `LwDisabledBorderBrush`, text `LwText4Brush`.

**Watched segmented** — the shipped M22 `LwSegmented` (container storm-2/r8/pad3, segments
h30/r6, storm-4 sliding thumb + `LwGlowToggleOn`, 240ms `LwDurGlowIn` cubic ease). Inactive
label `LwText2Brush`, active `LwText1Brush`.

**Count badge** — pill, min-width 18, h18, `LwChipFillBrush` / `LwChipBorderBrush`,
`LwLightCoreColor` 11/600 centered; sits before the chevron.

**Multi-select popup** — storm-3 (`LwStorm3Brush`), r8, `LwElevation2`, 6px padding; rows 34h
r4, hover `LwGhostHoverFillBrush`; checkbox spec in P9 above; footer "Clear selection"
(`LwLinkButton` 12.5px) above a top `LwHairlineBrush` rule. Opens over content
(`Popup`), never pushes layout.

**Starter chips (state a)** — 34h pill, `LwChipFillBrush`/`LwChipBorderBrush`,
`LwLightCoreColor` 13.5px; hover adds `LwGlowHover`; press `LwGhostPressedFillBrush`.

**Empty states** — the shared `EmptyState` component (gem mark + Marcellus 21 headline +
13.5 subline `LwText3Brush`). Prompt variant adds starter chips; zero-results variant adds a
"Clear all filters" `LwLinkButton`.

**Result summary** — `LwTypeMono` 12.5, `LwText3Brush`; the leading count `LwText2Brush`.

**Skeleton (loading)** — M25 `LwSkeletonBlock` grid (12 poster-metric cards, staggered
0.2s delays), shown in the results area only; the filter band stays interactive.

## Rationale (key decisions)

- **Card shapes (P1) — group, don't normalize.** Normalizing forces either cropping 2:3 key
  art into 16:9 or episode stills into 2:3 — both destroy artwork, which carries all the
  color in this system. Grouping keeps every card native, reads like the Home screen's
  section language, gives free per-type counts, and turns type narrowing into a visible,
  clickable act. Filmography deep-links (a person's movies vs episodes) genuinely benefit.
- **Two search inputs (P5) — hide the pill.** The pill is quick-search and *navigates away*;
  keeping it visible on a screen whose whole purpose is search invites the wrong input. One
  visible search input, auto-focused, zero ambiguity. "Advanced" hides with it (P6) — the
  title is the here-state.
- **Default landing (P7) — prompt, don't dump.** An unfiltered server-wide Name-A dump
  front-loads artwork-less extras and costs a pointless heavy query. The prompt state
  teaches the screen's point (combine facets), and starter chips give a one-click browse
  path that lands in a *typed* (homogeneous, good-looking) grid.

## Motion

- **Applying a filter / new results**: results cross-fade + 8px rise over `LwDurDrift`
  (480ms, ease-out) — same recipe as modal entry; skeleton shows only if the query
  exceeds ~250ms (avoids flicker on LAN).
- **Multi-select open**: popup fade + 4px drop over `LwDurGlowIn` (240ms); close over
  `LwDurInstant`.
- **Segmented thumb**: 240ms `LwDurGlowIn` cubic ease (shipped M22 behavior).
- **Engaged-facet glow**: in over `LwDurGlowIn`, out over `LwDurGlowOut` — glow arrives
  fast, decays slowly.
- **Section entry (mixed mode)**: sections stagger-fade 60ms apart, each `LwDurDrift`.

## New tokens (flagged)

| Token | Value | Justification |
|-------|-------|---------------|
| `LwGarnetChipFillBrush` | `#1AD14560` (garnet-lit 0.10a) | Context/seed chip needs the destructive-adjacent garnet identity at chip-glass alpha; no garnet glass fill exists. Same 0.10a recipe as `LwChipFillBrush`. |
| `LwGarnetChipBorderBrush` | `#73D14560` (garnet-lit 0.45a) | Border mate of the above (chip borders run ~0.35–0.45a). |

Everything else consumes existing tokens: storm ladder, text ladder, `LwChip*`, `LwGem*`,
`LwLight*`, `LwGlow*`, `LwR*`, `LwSp*`, `LwDur*`, `LwSegmented`, `LwSkeletonBlock`,
`EmptyState`, `LwLinkButton`, card templates.

## Implementation notes (WPF)

- Filter band: `Grid` with two explicit rows (not a WrapPanel) — Row A/B assignments are
  fixed; SEARCH/GENRE get `MinWidth`/`MaxWidth` and star-sizing to absorb width.
- Mixed mode = one `ItemsControl` of section groups (header + non-virtualized WrapPanel of
  ≤24 cards) inside the existing ScrollViewer; single-type mode keeps the current
  virtualized `ItemsGrid`. Three parallel `AdvancedSearchAsync` calls (one per type,
  limit 24) populate mixed mode; their `TotalRecordCount`s feed the summary.
- Hide `SearchPill` + `AdvancedButton` when the current nav frame is an
  `AdvancedSearchView` (in `UpdateBrowseHeader`), restore otherwise.
- Order-toggle label switches on the Sort selection; hide it when Sort = Random.
- The seed chip row participates in Row A; `PersonIds` already flows through
  `AdvancedSearchQuery` — add the person's display name to the seed (M5 has it at the
  call site: `PersonEntry.Name`).
