# M0 scored interaction review

Date: 2026-09-21
Build reviewed: `InterCat.Desktop` at theme version 1.0.0, synthetic workspace only
Method: the application was launched and driven on Windows 11 build 10.0.26220.0 at 1456 × 939 and at the
minimum size of 1080 × 700. Screens were captured from the application window, and the accessibility tree
was read and driven through UI Automation. On the second pass of 2026-09-21 the window was also rendered
headlessly at both sizes and at every rung of the ladder, and those frames were read. This is an observed
review, not a read of the source.

The M0 exit gate requires the §17 prototype questions to be run as a scored usability review with the
score and the observed failures recorded. Scores are 0 to 5, where 3 means the prototype supports the
task with a stated gap and 5 means it supports it with no gap found.

| Screen | File |
|---|---|
| Workspace at 1456 × 939 | `images/m0-workspace-1456x939.png` |
| Accessible table equivalents | `images/m0-accessible-tables.png` |
| Workspace at the 1080 × 700 minimum | `images/m0-minimum-size-1080x700.png` |
| Ladder at L0, after the ladder was built | `images/m0-ladder-l0-1456x939.png` |
| Ladder at L3, breadcrumb and filter chain | `images/m0-ladder-l3-1456x939.png` |
| Ladder at L5 at the minimum size | `images/m0-ladder-l5-1080x700.png` |

## The four §17 questions

### 1. Can a new user discover a surprising but real communication relationship in under two minutes? — 3/5

The workspace opens with both panes populated, a ranked process list already focused on one process, and
a legend naming every mechanism present. Selecting a process in the rail highlights it in the graph and
keeps the inspector in step, so a relationship is reachable in a few seconds.

The score is held at 3 because nothing yet ranks *surprise*. The list is ordered by synthetic activity, so
a user finds the loudest relationship rather than the unexpected one. §5.2's ranking scopes and the L0–L5
ladder are not implemented, so "surprising" is currently the user's own judgement.

### 2. Can they explain exactly why InterCat drew the edge? — 2/5

Partly. Each edge carries three independent channels: hue for the mechanism, thickness for magnitude and a
dash pattern for evidence quality, with a legend that names the mechanisms and an inspector panel that
states the quality wording. The relationship table repeats the same facts as text, including
`bytes unknown` where no measurement exists.

The score is 2 because the chain stops at the qualifier. There is no per-edge evidence list, no rule
identity, no rule version and no record-level drill-down, so a user can see *that* an edge is a candidate
but cannot yet reach *which records* made it one. R4's full contract lands with IC-017 and the evidence
inspector of M2.

### 3. Can they distinguish an unmeasured channel from a quiet one? — 4/5

Yes, in three places at once. The timeline draws a coverage gap as a hatched interval above the data, the
health strip states `Coverage gap: 2 seconds · not extrapolated`, and the interval table carries the
coverage word per row. Unknown byte values read `bytes unknown` rather than `0`, and the legend states
what the hatch and the open outline mean.

The remaining point is lost because the coverage hatch is currently drawn as a cross rather than the
diagonal hatch §6.6 specifies, and because an `Unmeasured` ranking group does not exist yet.

### 4. Can they navigate from a whole-system burst to source evidence without losing their place? — 4/5

*Rescored on 2026-09-21 after the ladder was built. The original score of 2 and its reason are kept below.*

The L0–L5 ladder is there. `Enter` or a double-click descends one rung; `Esc` and `Alt`+`Left` ascend to
exactly the rung that was left, restoring its viewport, selection, lane grouping, graph focus and filter
set as one navigation state. `E` reaches evidence in one step from any rung, carrying the rung's own scope
as a visible filter. The breadcrumb names the level and the selection at every rung and returns to any of
them. Every filter a descent implies is shown in the filter bar and removable with `Delete` without
changing the level. A rung with nothing in it names the source that would supply it rather than showing an
empty pane. Twelve source records sit at the bottom, each naming its provider, its event id and its raw
clock reading.

The score is 4, not 5, for one observed reason: the graph and the timeline do not change with the rung.
At L3 the graph still draws the whole machine rather than the channel's participants, and the timeline
still shows the whole extent rather than the rung's scope. §3.2 gives each level its own graph and
timeline composition, and only the ranked table, the breadcrumb and the inspector follow the ladder today.
Per-level graph composition needs the deterministic layout of IC-017.

*Original finding, 2026-09-21, before the ladder existed:* "The score is 2 because the ladder itself is
missing: there is no L0–L5 descent, no breadcrumb, no navigation history and no record-level evidence at
the bottom. A user reaches a bucket and stops there."

**Total: 13 of 20**, from 11 at the first pass. The prototype now supports descending as well as seeing
and separating; explaining an edge is still the weakest of the four.

## Defects found by running it, and what was done

| # | Defect | Status |
|---|---|---|
| 1 | The inspector's `Evidence` label and its value collided, and the value was clipped to `6,42 M` | Fixed: the value moved to its own wrapping line |
| 2 | `T` did not toggle the tables: the focused list consumed the letter for type-ahead | Fixed: the shortcut is handled while the key tunnels, so focus cannot swallow it |
| 3 | The relationship and interval tables had no column headers | Fixed: both tables now carry headers |
| 4 | The legend's item list had no accessible name in the UI Automation tree | Fixed: the list is named `Mechanism legend entries` |
| 5 | At 1080 × 700 the graph drew each node's name and PID over its neighbour | Fixed: the secondary line is dropped below 620 px of pane width |
| 6 | A node label was overdrawn by a later node's circle, so `Cache` rendered as `che` | Fixed: labels are drawn in a second pass after every circle |
| 7 | Pane subtitles were cut mid-word at the minimum size | Fixed: they ellipsize instead |
| 8 | The tables view covers the panes rather than sitting beside them | Open. Accepted for M0; a side-by-side or tabbed arrangement belongs with the M2 layout work |
| 9 | The coverage gap is drawn as a cross, not the diagonal hatch of §6.6 | Fixed: the cell carries the §6.6 diagonal hatch, clipped to the cell, and is never filled with an interpolated height |
| 10 | Graph node positions are fixed synthetic coordinates, so nodes can still crowd at small sizes | Open by design: deterministic layout is IC-017 |

## Second pass, 2026-09-21: defects found by looking at rendered frames

The window is now rendered headlessly and written to a file, so a layout defect can be found from a build
without a display. These were found by looking at those frames, at both sizes, at every rung.

| # | Defect | Status |
|---|---|---|
| 11 | At 1080 × 700 the level badge was drawn over the window title, because the breadcrumb pushed the header column wider than the header | Fixed: the header sizes to its content and the breadcrumb scrolls inside its column |
| 12 | The filter chips ran off the right edge of the window at L3, so the last filter could not be read | Fixed: the legend and filter row wraps instead of overflowing |
| 13 | The inspector kept showing the selected process's 312 observations while the rung accounted for 5, which read as a contradiction | Fixed: the inspector states the rung's own total beside the selected process, each labelled |
| 14 | Two operations at the same channel were both labelled `Send Outbound` and could not be told apart | Fixed: an operation's label carries the second it started at |
| 15 | The rung's "no single total" explanation was printed twice, once in a 250 px rail where it did not fit | Fixed: the rail states the short form and the inspector carries the reason |
| 16 | The graph and timeline do not change with the rung | Open: per-level composition needs the deterministic layout of IC-017, and is the reason question 4 scores 4 rather than 5 |
| 17 | With a deep breadcrumb the earlier crumbs scroll out of view and the scrollbar is hidden to keep the header short | Open: every crumb stays reachable by keyboard and by repeated `Esc`, so nothing is unreachable, but a deep chain is not fully visible at 1080 px |

## Accessibility observations

The UI Automation tree was read from a separate process. It exposes the window, both buttons with their
names, and both lists with names. The table toggle was invoked through the automation `Invoke` pattern and
the tables appeared, so the path from an assistive technology to the table equivalent works end to end.

Verified on the second pass: `InterCat.Ui.Tests` is a headless Avalonia lane that delivers real keys to a
real window. It asserts `T` while the ranked list holds focus — the exact situation that was broken at the
first pass — and `Enter`, `Esc`, `Alt`+`Left`, `E` and `Delete` against the ladder. The earlier
"implemented and unverified" note is therefore closed by measurement rather than by reading the code.

Still not verified: no screen-reader audit was run, and the rendered-frame lane asserts that a frame was
produced, not what a reader would announce.

## Palette verification

The palette is no longer a matter of judgement. `theme/contrast-report.json` records every measurement and
the tests in `InterCat.Desktop.Tests` fail if any of them regresses. Worst measured values on this build:

| Mode | Worst ink contrast | Worst fill contrast | Worst separation under a deficiency | Worst greyscale separation |
|---|---|---|---|---|
| Dark | 5.92 to 1 | 3.25 to 1 | 12.9 CIE76 (TCP to UDP, tritanopia) | 9.0 lightness |
| Light | 4.92 to 1 | 3.55 to 1 | 19.2 CIE76 (legacy to unknown, protanopia) | 9.3 lightness |

## Disposition of the §17 choices

| Choice | Disposition after this review |
|---|---|
| Graph projection on first open | Keep the process graph. Resource hubs are untested because no resource-centric data exists yet. |
| Amount of raw evidence retained | Unchanged; decided by ADR-002 and the M0 measurements, not by this review. |
| Background history | Unchanged: user-started bounded recording only. |
| Support breadth | Unchanged; the measured build is still outside the §1.3 matrix. |
| Content decoding depth | Not exercised: no content is captured or displayed. |
| Timing and stack depth | Not exercised. |
| Licensing and distribution | Not in scope for this review. |

## What this review does not cover

Live data, real capture volumes, graph layout at scale, per-level graph and timeline composition, the
minimap, brushing, multi-selection, reduced-motion behaviour and screen-reader output. Every score above is
about a synthetic five-process workspace, and none of it is evidence about Windows capture coverage.
