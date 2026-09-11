# LightWeaver — bug list

The working queue. Open defects go here, worst first, sourced from targeted code reviews
(player / UI / backend) plus the user's own reports from using the app. Each entry carries a
**status** so it is clear what has been proven and what is still a hypothesis:

| Status | Meaning |
|---|---|
| **confirmed** | verified in the code by reading the path, or reproduced live |
| **plausible** | the mechanism reads correctly but has not been re-verified independently |
| **unverified** | reported by a review; needs a check before acting on it |
| **fixed** | fix landed, with the verification that proves it |
| **fix implemented, runtime-unverified** | the fix is in the build and the mechanism is proven, but the fix itself has only been built and read — never run against the real hardware the bug needs |

Fix order is severity first, then whichever are cheap to do while already in that file.
The narrative of what shipped, round by round, is kept outside this repository; this file is
the queue.

## Open

_(no bug is open awaiting a diagnosis or a fix. **B39 and B41 are not closed either**: both fixes are
implemented, and both still await the runtime evidence their bugs need.)_

The Dorohedoro S1E4 playback report that sat here awaiting a repro was **dropped at the user's
request on 2026-08-06** and never became a B-entry — it was investigated on 2026-08-05, played
cleanly for 60 s against a control episode, and its metadata proved identical to six siblings.
Nothing was committed for it. If it recurs, start from `%LOCALAPPDATA%\LightWeaver\logs`: the
diagnostics round means an unrecoverable playback failure now writes a file.

Two reports **did** graduate this round, which is the rule working as intended: B33 and B34 below
were both tracker cards until a repro and a mechanism existed. B33 in particular had a *retracted*
mechanism first (2026-08-05) and only became filable once the user's own description of the
symptom arrived.

The **"a fast double-double-click does not leave fullscreen"** report was **measured and disproven
on 2026-08-07**, so it never became a B-entry (B35 was later assigned to the updater defect) —
recorded here so nobody re-files it from the same
reasoning. It was filed off the back of *reading* `OnRootMouseDown`'s `ClickCount == 2` branch, and
predicted that WPF would report counts 3 then 4 for the second gesture, leaving the player
fullscreen with a ~540 ms pause flash. It reports **1, 2, 1, 2**: WPF restarts the count, so press 4
takes the ordinary `== 2` branch and cancels press 3's pending single click, exactly as the log line
says. **What is measured** is that the reset is structural rather than a matter of timing:
`ToggleFullscreen()` runs *synchronously* inside press 2's handler, so the transition has completed
before press 3 arrives, and press 3 was only 219 ms after press 2 — well inside the 500 ms interval —
so no faster gesture can beat a synchronous transition. **Which** consequence of the transition
resets the count is *not* established: the overlay is re-parented on a fullscreen change, and that
alone restarts WPF's click tracking regardless of coordinates; separately the fixed *screen* point
now maps to a different *client* position, potentially outside press 2's double-click rect. Either
suffices, both are synchronous, so the conclusion holds — but do not carry the rect explanation
forward as fact. The predicted `e.ClickCount % 2 == 0` fix would have been dead code for this
gesture and actively wrong for genuine triple-clicks in a non-transitioning window. Leg 5 of
`backlog-tests/test-double-click-fullscreen.ps1` keeps the measurement as a green regression guard,
and would go red if the transition ever became asynchronous — the one regime where the counts could
genuinely reach 3/4.

## Known issues

Live behaviour that is understood and accepted (or watched), as opposed to filed defects.

- **B41 — playback stops after a subtitle switch and no seek brings it back.**
  **Fix implemented, RUNTIME-UNVERIFIED (2026-08-28), reported by the user.** The stuck state is a
  playback restart that never completes because audio never reaches mpv's `STATUS_PLAYING`: one
  video frame shown, clock frozen, each further seek re-entering it. Proven from the user's capture
  (`lightweaver-logs-20260828-154905.zip`), where `first video frame after restart shown` at
  15:48:30.551 is followed by no `audio ready`, no `playback restart complete` and no
  `starting audio playback` for the remaining 26 s, while every healthy restart in the same log
  finished in 60–220 ms. **Not B31/B32** (passthrough off: `audio-spdif=""`) and **not B39** (the
  output never reloaded, and B39's check requires the video clock to be *advancing*, so it stands
  down on exactly this state). `dynaudnorm` is present, as in B39, but **the mpv-internal reason
  audio never re-primes is not established** — the capture is verbose, not trace-level, and the
  1.0.1 build predates the player's detail records. Fixed by a third watchdog on the state the
  other two exclude, armed by seek / subtitle switch / unpause, repairing with an audio-chain
  rebuild and then one `seek 0 exact`, then one toast. The repair order comes from the capture: the
  user's seeks are what demonstrably did NOT work. **Verified only by build plus the B41 suite,
  which proves the watchdog's state machine against a faked clock reading and NOT the stall
  itself** — that cannot be induced from outside mpv. Whether the repair cures it stays unproven
  until the stall recurs on a build carrying the records
- **B39 — audio goes silent after an audio output device change while video keeps playing.**
  **Fix implemented, RUNTIME-UNVERIFIED (2026-08-17).** Mechanism proven from mpv's source at the
  shipped commit (`mpv v0.41.0-744-g304426c39`) plus two real-hardware captures:
  `reload_audio_output()` (`player/audio.c:765-798`) destroys everything buffered in the audio filter
  chain without moving the decoder, and the only compensating rewind (`player/audio.c:120-123`) is
  gated on `audio_status == STATUS_PLAYING` — so a reload arriving during a still-pending delayed
  start issues no refresh seek and the audio restart point jumps **forward**, leaving mpv in
  `delaying audio start` with no bound (observed 77.8 s, then 149.9 s). Both an in-app device switch
  and a Windows default-device change on `auto` reach that same function, and one physical switch can
  fire several reloads, which is how the gap ratchets. The 16 s quantum per reload comes from
  `dynaudnorm`'s 500 ms × 31 window: dynaudnorm is the **amplifier**, not the defect, and it stays
  enabled. Fixed by a generation-guarded audio-sync watchdog that re-issues the rewind mpv skipped —
  one reload, one verify, silent on success. **Verified only by `dotnet build -c Debug/-c Release
  -warnaserror` (0 warnings / 0 errors) and static review; never exercised against a real audio
  endpoint.** The discriminating suite
  `test-audio-sync-after-device-change` exists and **has never
  been run** — it needs a real exclusive-mode WASAPI open on the user's desktop and the user's
  go-ahead, and the test machine cannot host it (no real audio endpoint). Two foot-guns recorded in
  the full entry: `current-ao` going null was the **disproven**
  first diagnosis, and `audio-pts` is `M_PROPERTY_UNAVAILABLE` in exactly the stuck state, so the
  absence of a reading — not a numeric comparison — is the only signal that can fire
- Settings live-apply changes RTX/subtitle state of the running playback when the
  *defaults* are edited — slightly surprising but useful; revisit if it confuses
- ~~LAN quirk (this network, documented in TECHNICAL): the server intermittently drops
  parallel connection bursts (SYN loss)~~ — **root-caused to the CLIENT NIC and fixed
  (2026-08-01).** The Unraid/Docker side was investigated first and exonerated with
  evidence: zero drops in every server-side counter over 26 days of uptime (host + container
  TCP listen queues, conntrack, e1000e missed, qdisc, syslog) and a live 600/600
  parallel-connect burst. The real defect: this PC's Intel I219-V ran with `Receive Buffers`
  at the driver default 256 and had accumulated 29 707 received-discarded packets — a
  client-dropped SYN-ACK reads exactly like server-side SYN loss. Raised to 2048; discard
  counter re-zeroed by the adapter reset, so it doubles as the regression check. Full
  write-up in TECHNICAL ("LAN robustness"). The app's client-side mitigations stay by design
- The test library is a LIVE library: content appears during long verification
  runs (a series grew a virtual Specials season mid-sweep; new movies landed
  between an app query and its API cross-check). Suites now pin fixtures and
  filter IsMissing, but API-vs-UI cross-checks can still race a library scan

## Closed — id index

Full write-ups, in filing order, are kept outside this repository. TECHNICAL.md cites these ids
directly; they are stable. There is no B30 — the number was never issued. Everything below is closed
**except B39 and B41**, which remain listed while their fixes await the runtime evidence described
under "Known issues" above.

| Id | Bug | Status |
|---|---|---|
| **B1** | Up-next EOF guard is dead code → a queue episode can be silently skipped | critical · fixed (2026-07-29) |
| **B2** | Preferred-language auto-select is anchored to the wrong event | high → low · fixed (2026-07-29) |
| **B3** | Up-next countdown can't be cancelled, and runs while paused | high · fixed (2026-07-29) |
| **B4** | A faded-out OSD is still clickable | medium-high · fixed (2026-07-29) |
| **B5** | RTX filter state is mutated from the mpv event thread | medium · fixed (2026-07-29) |
| **B6** | Media-key Next is disabled exactly when the OSD Next works | medium-low · fixed (2026-07-29) |
| **B7** | Phase 9 key forwarding steals keys from open OSD flyouts | low-medium → confirmed then fixed (2026-07-29) · mine (Phase 9 M1) |
| **B8** | `IsSeeking` can latch on permanently | low · fixed (2026-07-29) |
| **B9** | Key auto-repeat drives discrete actions | low · fixed (2026-07-29) |
| **B10** | Fast next-next can consume the resume seek on the dying file | low · fixed (2026-07-29) |
| **B11** | The OSD stays behind when the window moves without resizing | medium · fixed (2026-07-29) |
| **B12** | `WithRetry` retries a non-idempotent POST, orphaning a server play session | medium · fixed (2026-07-30) |
| **B13** | The LAN-burst mitigation was never applied to the component that causes the bursts | high · fixed (2026-07-30) |
| **B14** | Images are always fetched with the *active* profile's token | medium · fixed (2026-07-30) |
| **B15** | A truncated download is marked Completed | medium · fixed (2026-07-30) |
| **B16** | Pause immediately after starting a download latches it on "Downloading" forever | medium-low · fixed (2026-07-30) |
| **B17** | UserData may be absent from every query that omits `UserId` | NOT A BUG · measured and closed (2026-07-30) |
| **B18** | A dead connection hangs a download forever | low-medium · fixed (2026-07-30) |
| **B19** | `MetadataCache` never evicts, and its lock table grows for the process lifetime | low · fixed (2026-07-30) |
| **B20** | The decoded-bitmap LRU is capped by count, not bytes | low-medium · fixed (2026-07-30) |
| **B21** | Image disk eviction runs once per session and then never again | low · fixed (2026-07-30) |
| **B22** | Closing the app during playback loses the stop report | medium-low → LOW · fixed (2026-07-30) |
| **B23** | `PendingStopReport` is published at one of five stop sites | low · fixed (2026-07-30) |
| **B24** | The browse back-stack is unbounded and holds every view instance alive | medium-low · fixed (2026-07-30) |
| **B25** | The Downloads rail item stays lit on railless destinations | low · fixed (2026-07-30) |
| **B26** | Playback metadata is six serial round trips under one catch-all | medium-low · fixed (2026-07-30) |
| **B27** | Every abandoned login leaks a `JellyfinService` | low · fixed (2026-07-30) |
| **B28** | `CloseSession` disposes the active service while `Jellyfin` still points at it | low · fixed (2026-07-30) |
| **B29** | The Debug build's "(Debug)" title marker never appears | medium-low · fixed (2026-07-31, Phase 10 M1) |
| **B31** | Audio passthrough on a device that can't bitstream stops playback entirely | critical · fixed (2026-08-01) |
| **B32** | The B31 recovery fires once and then stands down — the next playback that needs it hangs at Loading… forever | critical · fixed (2026-08-01) |
| **B33** | A mouse click cannot bring the app forward while the player runs — and it pauses the video from the background instead | high · fixed (2026-08-06) |
| **B34** | A double-click on the video toggles pause on its way to fullscreen | medium · fixed (2026-08-06) |
| **B35** | A successful pre-release check suppresses discovery for 24 hours, with no manual escape | medium · fixed (2026-08-07) |
| **B36** | Download choices are fetched only after the click, and can open after leaving the detail | medium · fixed (2026-08-09) |
| **B37** | A transient Windows sharing denial aborts resumable update staging while replacing progress metadata | high · fixed (2026-08-09) |
| **B38** | Diagnostics export includes foreign `.log` files that never passed AppLog redaction | high · fixed (2026-08-09) |
| **B39** | Audio goes silent after an audio output device change while video keeps playing | high · fix implemented, RUNTIME-UNVERIFIED (2026-08-17) — built and reviewed, never run against a real audio endpoint |
| **B40** | The first S press in a file cycles the subtitle track and then snaps straight back | medium-high · fixed (2026-08-20) — the final run on the test machine passed all revision-3 boundaries, including late-external acknowledgement |
| **B41** | Playback stops after a subtitle switch and no seek brings it back | high · fix implemented, runtime-unverified (2026-08-28) — the watchdog's state machine is proven by its own suite; the stall itself cannot be induced from outside mpv |
| **B42** | Shift+Win+Arrow moves the overlay to the next monitor but not the window | medium · fixed (2026-09-05) — final run on the test machine (`lw-player-overlay-input`, 132/0/0); leg B proves translate, ignore-while-maximized, ignore-while-fullscreen, mini, and exactly five `OverlayExternalMove` lines for five external moves |
| **B43** | The shortcuts panel orphans a group heading at the scroll fold | medium-low · fixed (2026-09-05) — the same run; leg C proves both panel hosts at four window sizes, the discriminating one being 1280×720 DIU (viewport 192..679) |

## Review sources

- ~~**UI / shell**~~ — run 2026-07-30; findings were B22–B28.
- ~~**Backend / data**~~ — run 2026-07-30; findings were B12–B21.
- **The user's own reports from using the app** — the highest-signal source, since these are
  observed rather than inferred. They arrive on the maintainer's own tracker and graduate to a
  B-entry when a mechanism is established.

The round log — what each fixing round taught, including the diagnoses that were wrong first —
is kept outside this repository.
