# ADR-099: A resource two or more drawn processes held is a hub, joined to each by a membership with no traffic value

- Status: accepted for M6's graph and resource inspectors; the window's graph draws resource hubs (revision 1007)
- Date: 2026-11-07
- Decision owners: InterCat maintainers
- Relates to: ADR-092, ADR-097, ADR-098, plan §3.5, §6.3, M6, R11, P27

## Context

§6.3 asks for a process graph with resource hubs for shared channels - shared memory a section hub with membership
edges, neutral, thin, constant and headless, since membership is undirected and carries no traffic value - and §3.5 for
a section several processes mapped shown as a node with membership edges. The graph drew none: until sections were
admitted no session had a resource, and until ADR-097 nothing derived one. `resource-instances-v3` now derives named
synchronization objects, sections by their handles and by their views, and AF_UNIX sockets, each with its holders.

## Decision

1. **A hub is a resource two or more process instances held**: a section by its views, a section by its handles - two
   hubs where both are found, as no record joins them (ADR-092) - or a named synchronization object. Never an image's
   section, a module loaded rather than memory shared, nor an AF_UNIX socket, an endpoint whose other end no record names.
2. **Its resources are derived only where a process made a record of a mechanism that has them**, from the counts every
   overview already holds, so a session without such records - every Explore capture - opens no segment for them. They
   are derived once per generation and policy; no checkpoint holds them yet.
3. **It joins the drawn nodes holding it**, each once however many of its processes held it, by a membership: neutral
   ink, the thinnest constant stroke, solid and headless, counted in no total, and re-counted by no brush. A hub is
   drawn only where its holders are in two drawn nodes or more; a holder outside a focused rung's neighbourhood is left
   to its own rung, as its connections to other hosts are.
4. **A hub never folds a process.** Hubs are drawn after every process is placed and every host drawn, the most
   holders first, as many of the budget's eight as the node and edge budgets allow. The holders of those the budget
   could draw are kept out of No relationships, as a process reaching other hosts is, so the hub joins them apart.
5. **It is a diamond at a fixed size**, a resource rather than a process or several of them, and its card says what it
   is, who held it, a mapped section's capacity - the largest extent a view of it mapped - and that holding it proves
   shared access, never traffic (P27). A repaint drawing it allocates nothing (R11).

## Consequences

- §3.5's shared section is drawn: a node with membership edges, its capacity as capacity and its traffic as none.
- Choosing a hub says what it is and who held it, and selects no records of its own, as Other hosts does: E lists the
  rung's. Listing a hub's own records and finding one by a search are later work, as is keeping the resources in the
  derivation checkpoint, so a reopened resource capture opens its segments for them once.
- 22 mutations of the path are caught.
