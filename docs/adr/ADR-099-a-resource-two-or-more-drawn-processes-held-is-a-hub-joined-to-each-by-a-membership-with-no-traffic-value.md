# ADR-099: A resource two or more drawn processes held is a hub, joined to each by a membership with no traffic value

- Status: accepted for M6's graph and resource inspectors; the window's graph draws resource hubs (revision 1007), a chosen hub selects its own records (revision 1008) and the search finds one (revision 1009)
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

## Addendum: a hub's own records (revision 1008)

Choosing a hub selects its own records: those of its mechanism whose source field carries the number its gate gave it -
a section's views by the number they carry and its handles by theirs, never joined (ADR-092) - whichever process they
bind to. Its key, `resource:hub:{mapped-section|section|object}:{number}`, scopes them as an RPC channel's key scopes its
calls: the timeline highlights them, the card counts them as records of its views or its handles and never traffic, E
lists them, and an export of them names `icat evidence <directory> --channel <key>`, which lists the same records under
the same identity, its rule `resource-instances-v3`. A key of no hub's kind, or numbered 0 - the number a record carries
where its gate gave none - names no hub. Finding a hub by the graph's search is later work. 25 mutations of the path
are caught.

## Addendum: a hub found by the search (revision 1009)

The search finds a resource two or more processes held - every hub of the session, drawn or not - by its name's leaf,
its whole name with the object directory it lies in, or, where no record named it, what it is, "Mapped section 4". Its
hit says what it is, how many processes held it - one program's, where they were - and where its name lies, and counts
its records. A hub is no rung: going to it leads where its holders are drawn apart - their program's rung where one
program's processes held it, else the machine rung - and draws its hub there first, whatever the budget would draw
before it, its holders kept apart, and chooses it. Another rung shown lets it go, and a re-count of the drawing keeps
it. While a search is typed the graph marks a hub it finds and counts it, and a chosen resource hit rings the hub and
its holders' nodes. Where the rung draws its holders in one node no hub joins them, and none is chosen.
25 mutations of the path are caught.
