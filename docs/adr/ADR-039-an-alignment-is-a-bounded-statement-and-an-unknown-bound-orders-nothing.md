# ADR-039: An alignment is a bounded statement, and an unknown bound orders nothing

- Status: accepted; revision 254, recorded evidence revision 256
- Date: 2026-09-28
- Relates to: §8.1, §8.2, M4, I9, R3, R21, ADR-038, `contracts/workspace-v4.md`

## Context

Separately captured sessions each keep their own source clock: a QPC reading is not comparable across hosts, nor across
boots of one host, so an instant of one session says nothing about an instant of another until something relates their
clocks (§8.1). §8.2 relates them with versioned affine mappings into a workspace clock, each with an uncertainty built
from named contributions, and allows an order between two hosts' instants only beyond their combined uncertainty. M4's
exit gate asks that injected clock uncertainty be exposed, unjustified causal order rejected, and a manual alignment
persisted and reopened without changing a raw timestamp.

Two things in §8.2 needed deciding before any of it could be built. Its pair formula,
`u_pair = sqrt(u(tA)^2 + u(tB)^2) + u_systematic`, adds the systematic part again on top of each side's `u(t)`, which
already holds it. And a manual alignment's "bound" and a drift's rate needed a rule for which side of the formula they
fall on, and for what happens when the rate is not known.

## Decision

1. **The workspace's time is one member's clock.** The first alignment makes its reference member the time reference,
   whose instants are workspace time exactly; every alignment in force is to it. Aligning through another aligned member,
   which would compose two mappings and their uncertainties, is not offered yet.
2. **A manual alignment is a person's bounded statement.** It says that one instant of a member is one instant of the
   reference, within a half-width the person states; with one anchor it supplies an offset and no rate (§8.2). The
   person may bound how fast the two clocks drift apart, in parts per million. Both are bounds, so both add linearly;
   only independently measured contributions, which no mode records yet, combine in quadrature.
3. **An unknown contribution makes the uncertainty unknown.** With no drift bound stated, the drift is unknown, and so is
   the uncertainty of every instant away from the anchor itself - never zero and never a default. Nothing is then stated
   about an order across members there, not even the difference (R3, R21).
4. **The pair's uncertainty counts each part once.** Comparing instants on two independently mapped clocks takes
   `sqrt(rA^2 + rB^2) + sA + sB`: their random parts in quadrature and their bounds added. An order is stated only when
   the instants are further apart than that; otherwise it is ambiguous. Two instants of one member share its mapping,
   whose uncertainty cancels, and are ordered exactly on its one clock.
5. **An alignment is an annotation, versioned.** Each is a revision of the workspace file, kept when a later one
   supersedes or withdraws it; none changes a session, a timestamp or a derivation (I9).
6. **A bound never reads smaller than it is.** A stated uncertainty is written rounded up at the precision written.
7. **What captures record aligns them too, and says what it cannot measure** (revision 256, ADR-040). Two captures that
   recorded one boot's token read one counter, so they align exactly through their epochs, with no drift. Two others
   align through their recorded wall-clock samples, anchored on the pair taken closest in wall-clock time: its bound
   adds the samples' acquisition, which they measured, the wall clocks' agreement, which no sample can measure and a
   person must state, and a stated drift over the time between the samples and away from the anchor.

## Consequences

- Revision 254 implements `icat workspace align` and `compare`, `workspace-v2`, and the §8.2 model in
  `InterCat.Domain` (`ClockMapping`, `UncertaintyContribution`, `TimeUncertainty`, `TimeComparison`).
- §8.2's pair formula is restated in the plan with each side's random and bound parts.
- Alignment from recorded wall clocks needed captures to record paired monotonic and wall-clock samples; revision 255
  records them (ADR-040), and revision 256 aligns by them and by one boot's counter (`workspace-v3`, decision 7).
  Alignment from shared markers needs cross-host correlation; it and a rate from two separated anchors follow. Revision
  261 draws the merged time in the Desktop: each session a lane on the investigation's axis where its alignment places
  it, its placement's uncertainty stated beside it.
