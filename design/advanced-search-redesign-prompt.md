# Claude Design prompt — LightWeaver "Advanced Search" screen redesign

## What this is
Redesign ONE screen — the **Advanced Search** screen of **LightWeaver**, a native Windows
(WPF) desktop Jellyfin media client. Deliver a full-screen mockup plus specs for any new or
adjusted components. This screen was added late and currently looks off; it must slot into the
**existing "woven light" design system** — do NOT invent a new visual language. Treat the
shipped system as fixed and authoritative: consume its tokens and components, and extend only
where this screen genuinely needs something new.

## Design system (reuse it — don't restate it)
Consume the existing **"woven light"** design system as the fixed, authoritative source of
truth — it is specified in `design/LightWeaver Jellyfin design system/LightWeaver Design
Spec.dc.html` and implemented as WPF `Lw*` token dictionaries. Do not re-describe or reinvent
the visual language; build this screen out of it. Reuse existing components over new ones
wherever possible: the left nav rail, content header, `LwDropdown`, `LwTextField`,
`LwCheckDropdown` (multi-select), `LwIconToggle`, the poster / landscape / episode card
templates (with their watched/favorite badges + resume progress), the A–Z rail, gem pills,
tooltips, focus glow, and the motion tokens. Dark only; WCAG AA text contrast; WPF-implementable
effects. Any genuinely new token or component must be flagged and justified.

## Where the screen lives (shell context — important)
It is a content screen INSIDE the persistent **left icon nav rail** (Home + the server's
libraries + Settings + profile) and the app's **content header**. The header carries: Back /
Forward buttons, the screen title, a right-aligned global **"Search your libraries" pill**, and
an **"Advanced"** affordance. The window resizes from ~1000×700 up to 4K; the rail collapses to
icons below ~1160px. The redesign must decide how this screen coexists with the header's global
search pill (see Problem 5).

## Purpose of the screen
One place to combine free text with several filters at once, **server-wide**, and browse the
matches — richer than the header's quick single-term search and broader than one library's
filter bar. It is also the destination that "click a cast/crew member" and "click a genre"
deep-link into, **pre-filtered** (so the design must look right when it opens already carrying
an active filter, e.g. a person or a genre).

## The controls it must present (exact filter model — do not add/remove facets)
- **Free-text term** (title / keyword).
- **Item type** — MULTI-select: Movie / Series / Episode (default = all three).
- **Genre** — MULTI-select, from the server-wide genre list (can be long).
- **Watched state** — single-select: All / Unwatched / Watched.
- **Year range** — from–to, optional 4-digit years.
- **Sort field** — Name / Date added / Premiere date / Community rating / Random.
- **Sort order** — ascending / descending.
- **Results** — a paged, infinitely-scrolled grid of mixed media (movies, series, episodes),
  each with artwork, a title, a subtitle (e.g. "Series · S1E2", or a movie year), plus the
  watched/favorite badges and resume-progress the shared cards already carry. A card opens the
  item's detail view on click.

## Problems in the current build to SOLVE (the reason for the redesign)
1. **Mixed card shapes clash.** Results mix portrait 2:3 movie posters with landscape 16:9
   episode cards in the same grid → ragged rows, uneven heights, misaligned titles. Choose a
   coherent treatment (e.g. one normalized card shape for this mixed grid, or clearly
   segmenting/grouping by type) and specify it precisely.
2. **Wasted width / too few columns.** Results use only ~3 columns and ~60% of the width, with
   a large empty band on the right. Use the space; specify responsive column counts by width.
3. **Filter panel wraps awkwardly** — controls reflow so one facet ("Sort") orphans onto a
   second row leaving dead space. Design a deliberate, balanced filter layout that reflows
   cleanly at every width (define the wrap/stack order).
4. **Year fields read as empty/unclear** — two blank boxes and a bare "–", no hint. Give the
   year range a clear affordance (labels/placeholders/"From–To").
5. **Two search inputs on one screen** — the header's global "Search your libraries" pill AND
   this screen's own free-text field are both present and ambiguous. RESOLVE it: decide whether
   the screen hides/absorbs the header pill, or whether the on-screen term field is the single
   obvious input — and state the intended model.
6. **Redundant "Advanced" affordance** is still shown while already on this screen. Specify its
   state here (hidden / disabled / an explicit "active/here" state).
7. **Poor default landing** — with no query it dumps ALL items Name-sorted, so the first
   impression is a wall of artwork-less extras with placeholder glyphs. Design an intentional
   initial state (an inviting prompt/empty state using the system's woven-light empty-state
   component, or a curated default), plus the **no-artwork card fallback** and the
   **no-results empty state**.
8. **No result feedback** — there's no result count. Add a count / result-summary treatment
   (mono), matching how the library grid shows "N items".
9. **Multi-select isn't discoverable** — Type and Genre look identical to the single-select
   dropdowns. Give multi-select a distinct, self-evident treatment (selected-value chips, a
   count badge, etc.), including how many chosen values display before truncating.
10. **Ambiguous sort-order toggle** — a bare arrow doesn't convey A→Z vs Z→A. Make direction
    legible (icon + label/tooltip, or a labeled segmented control).
11. **Caption labels tiny/dim** — verify the field captions meet AA contrast and are legible.

## Deliverables
- One **full-screen mockup** at a representative desktop width (~1440px), rendered inside the
  existing nav rail + header, using only woven-light tokens/components, showing a populated
  results state.
- **State coverage:** (a) initial / no-query state; (b) active filters + results; (c) a
  multi-select (Type or Genre) in its open + partially-selected state; (d) zero-results empty
  state; (e) loading / skeleton; (f) a narrow-width (~1040px, rail collapsed) reflow of both
  the filter panel and the results grid; (g) opened pre-filtered on a single person or genre.
- **Component specs** (rest / hover / pressed / focused / disabled) for anything new or
  adjusted: the filter-bar container + field grouping, the multi-select treatment (chips /
  count), the year-range control, the result-count/summary, the normalized results card (if you
  introduce one), and the empty / no-results state — each as an extension of an existing
  component family, with explicit token references.
- A short **rationale** for the key decisions: the card-shape strategy (Problem 1), the
  two-search resolution (Problem 5), and the default-state choice (Problem 7).
- **Motion notes** consistent with the system (slow, air-like) for: applying a filter, the
  results reflowing, and a multi-select opening.

## Constraints / non-goals
- Do NOT redesign the nav rail, the header shell, the cards' badge/progress language, or the
  token system — reuse them. This is one screen inside a finished system.
- Keep total luminance restrained; artwork carries the color; gem accents stay small and
  lit-from-within.
- Everything must be implementable in WPF with the existing `Lw*` resources. Flag any genuinely
  new token or component you have to introduce, and justify it.
