# M0 scored interaction review

Date: 2026-09-21
Build reviewed: `InterCat.Desktop` at theme version 1.0.0, synthetic workspace only
Method: the application was launched and driven on Windows 11 build 10.0.26220.0 at 1456 × 939 and at the
minimum size of 1080 × 700. Screens were captured from the application window, and the accessibility tree
was read and driven through UI Automation. This is an observed review, not a read of the source.

The M0 exit gate requires the §17 prototype questions to be run as a scored usability review with the
score and the observed failures recorded. Scores are 0 to 5, where 3 means the prototype supports the
task with a stated gap and 5 means it supports it with no gap found.

| Screen | File |
|---|---|
| Workspace at 1456 × 939 | `images/m0-workspace-1456x939.png` |
| Accessible table equivalents | `images/m0-accessible-tables.png` |
| Workspace at the 1080 × 700 minimum | `images/m0-minimum-size-1080x700.png` |

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

### 4. Can they navigate from a whole-system burst to source evidence without losing their place? — 2/5

A burst can be selected in the timeline and the selection survives across panes through one revisioned
selection coordinator, so the panes never disagree. Keyboard paths exist for the graph, the timeline, the
rail and the table toggle.

The score is 2 because the ladder itself is missing: there is no L0–L5 descent, no breadcrumb, no
navigation history and no record-level evidence at the bottom. A user reaches a bucket and stops there.
This is the largest single gap between the prototype and §3.2, and it belongs to M2.

**Total: 11 of 20.** The prototype supports seeing and separating; it does not yet support explaining or
descending.

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
| 9 | The coverage gap is drawn as a cross, not the diagonal hatch of §6.6 | Open, tracked against IC-008's remaining drawing work |
| 10 | Graph node positions are fixed synthetic coordinates, so nodes can still crowd at small sizes | Open by design: deterministic layout is IC-017 |

## Accessibility observations

The UI Automation tree was read from a separate process. It exposes the window, both buttons with their
names, and both lists with names. The table toggle was invoked through the automation `Invoke` pattern and
the tables appeared, so the path from an assistive technology to the table equivalent works end to end.

Not verified in this session: the `T` and `Escape` shortcuts were exercised through the code path but
synthetic key delivery could not be confirmed from an automation script, so the keyboard shortcut is
recorded as implemented and unverified. A headless UI test lane (§26.1's `InterCat.Ui.Tests`) is the right
place to assert it, and it does not exist yet. No screen-reader audit was run.

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

Live data, real capture volumes, graph layout at scale, the L0–L5 ladder, the minimap, brushing, multi-
selection, navigation history, reduced-motion behaviour and screen-reader output. Every score above is
about a synthetic five-process workspace, and none of it is evidence about Windows capture coverage.
