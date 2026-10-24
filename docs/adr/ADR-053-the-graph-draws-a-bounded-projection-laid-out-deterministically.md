# ADR-053: The graph draws a bounded projection of one published graph, laid out by a deterministic, anchored force-directed pass

- Status: accepted; revision 725. It records the projection §6.3 and the layout §19.4 describe, which revisions 88 to 109
  first built and revisions 641 to 647 measured on wide and hub-structured machines, and tests the determinism claim
  across worker counts as §13.6 requires
- Date: 2026-10-22
- Relates to: §3.2, §6.3, §6.8, §13.6, §19.4, R7, R8, R12, `src/InterCat.Application/GraphProjection.cs`,
  `src/InterCat.Application/GraphLayout.cs`, `src/InterCat.Application/GraphLayoutScheduler.cs`

## Context

The question the product exists to answer is which processes talk to which. On a heavily used machine that is hundreds
of processes and thousands of relationships, a few hubs among them; drawn whole, it is the unreadable cloud §6.3
forbids. The person also has to be able to come back to it: a graph that rearranges itself when a recording publishes,
when a group is opened and closed, or when it is drawn again tomorrow, loses the map they built of it. §6.3 and §19.4
set what is drawn and how it is placed, and the code has held to them through many measured passes, but no record said
why this projection and this layout, or what their determinism covers.

## Decision

1. **A process graph, bounded, with nothing omitted.** Processes are nodes and relationships edges, an edge per
   mechanism between two drawn nodes; other hosts are resources - one **Other hosts** node, or opened, the busiest each
   on its own. The drawing holds at most 100 nodes and 500 edges (TUNABLE): about as many discs as the design pane draws
   with none hiding another. A projection over the budget folds by a fixed order - processes with no relationship into
   one counted node; groups of more than 25 drawn members into their group, except one the person opened; processes
   reaching only other hosts, by program; then the least active groups; an opened group's low end into its **Other
   members**; and last the least active nodes into **Other processes** - and every process stays drawn or counted in a
   node that states its count. A focused process is never folded, and the ranked and relationship tables are never
   compacted.
2. **Membership is the published scope's.** What is folded is chosen from the whole published graph, not from a brush or
   a viewport, so re-counting a time range changes sizes, never which node a process is in.
3. **The layout runs in four stages, off the UI thread, in drawn units** - the pane's design aspect of 2.6 to 1, so a
   distance means the same both ways. A node with no drawn edge is parked in a footer slot that depends only on the
   parked set. A new node is seeded beside a placed neighbour, at an angle from a SHA-256 hash of the graph identity and
   its key; a component with nothing placed starts in a cell of a fixed grid, largest first. Relaxation is 120
   Fruchterman-Reingold steps - every pair in a component repels by k²/d and every relationship attracts by d²/k,
   separate components repel only within 2.2 k, and gravity gathers them - with a temperature that cools linearly.
   Separation then parts discs relaxation left overlapping.
4. **The person's map survives.** A pin is a hard constraint. A node drawn before starts where it was and is anchored
   there; a settled refresh - a newer publication of the drawing the person is looking at - moves it no more than a
   nudge, about 0.09 of the pane's height at most, however many nodes join. Opening a group or focusing a process loosens
   the anchors in proportion to how much of the drawing is new.
5. **Determinism.** The same graph identity, nodes, edges, earlier positions, pins and iteration cap give the same
   positions, bit for bit, whatever the order the inputs arrive in, the thread or the number of layouts running at once,
   and the time. Inputs are sorted by key, seeds come from hashes, and no random number generator or clock is read.
6. **One result per graph identity.** A layout applies only to the graph identity it was computed for; the scheduler
   refuses a late or superseded one (R7). Relaxation never runs during a gesture, and reduced motion draws each change
   where it ends. A graph past the layout's own bound of 512 nodes or 4,096 edges is refused, never partly drawn (R8).

## Why

- **Force-directed rather than hierarchical or stress-based.** Relationships between processes are mostly undirected in
  what they show, cyclic, and dominated by hubs; a layered layout presumes a direction the evidence often lacks, and
  stress majorization needs all-pairs distances. Fruchterman-Reingold draws a hub and its peers as a star, runs in a
  fixed number of bounded steps, and needs no dependency beyond arithmetic.
- **Hashes rather than a seeded generator.** A position follows from the graph identity and the node's key, so nothing
  needs storing to reproduce it, and a node keeps its seed when others join.
- **Anchoring rather than recomputing.** §6.3 asks that positions survive refreshes: a layout recomputed on every
  publication of a recording rearranges what the person is reading while they read it. Anchors trade a little of the
  layout's optimum for a stable map.
- **A separation stage.** Revision 641's wide machine - 253 processes drawn as 199 nodes - settled with 846 pairs of discs
  overlapping, because many relationships outpull the push between discs; parting them after relaxation is what makes
  every node reachable by the pointer.
- **A budget of 100.** Revision 643 measured that the design pane draws about 100 discs at §6.3's radii with none hiding
  another; past that, a node a person cannot point at is no better than one folded and counted.

## Consequences

- A test lays out the same graph, fresh and as a settled refresh with every node drawn before and one pinned, in
  repeated runs on one, two and eight threads at once, and requires every position and the energy bit for bit; others
  hold the order of inputs, pins, anchors, parking, seeding, separation and the scheduler's refusals.
- The determinism holds within one runtime on one processor architecture. Seeding and the parting of coincident nodes
  use the runtime's sine and cosine, whose last bits may differ between platforms, and 120 steps can carry that
  difference into a visibly different layout of unpinned nodes. An investigation carries its pins exactly
  (`workspace-v23`), not the positions of nodes nobody pinned.
- Hit testing scans the drawn nodes and edges, which at 100 nodes and 500 edges is measured to be fast enough; a spatial
  index is owed only when a larger projection needs one.
- What a drawing can show is bounded by the budget: a machine of thousands of processes is read through its folded
  nodes, the tables and the search, not through one drawing of everything.

## Alternatives considered

- **Draw every process.** Rejected: §6.3's unreadable cloud, and no disc a pointer can reach at the busiest scale.
- **A layered (Sugiyama) layout by who connected to whom.** Rejected as the default: direction is known only for some
  relationships, and the layers become as wide as the machine's busiest service.
- **A seeded random generator, the seed stored with the session.** Rejected: the hash gives the same reproducibility
  without storage, and a stored seed would still move every node when one joins.
- **Laying out on the UI thread.** Rejected: §12's frame budget, and a gesture must never wait for relaxation.
