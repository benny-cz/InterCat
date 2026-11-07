# ADR-098: The resource-topology basis counts each resource once over its known lifetime, never its records

- Status: accepted for M6's resource topology; resources, the processes sharing them and sections' capacity are counted
  (revision 1005), and grouped by a fact of the processes holding them (revision 1006)
- Date: 2026-11-07
- Decision owners: InterCat maintainers
- Relates to: ADR-012, ADR-032, ADR-092, ADR-097, plan §5.3, §21.1, `metrics-v1`, `query-identity-v1`, R3, R21, P1, P3, P27

## Context

§5.3's matrix has a resource-topology basis: its `Observations` count membership and lifecycle records, its
`ActiveChannels` and `ActivePeers` count resources and the processes sharing them, its `MappingCapacity` is a resource's
capacity, once, and its `Duration` is a mapping lifetime. §21.1 expects two processes mapping one 8 MiB section to be one
section, two memberships and 8 MiB of capacity, with no traffic. `metrics-v1` answered every request on the basis
`NoResourceTopology`, as nothing derived resources. Since ADR-097, `resource-instances-v3` derives them - named
synchronization objects, sections by their handles and by their views, AF_UNIX sockets - each with its known lifetime,
whether each end was witnessed, its holders and, for a section by its views, the largest extent one mapped.

## Decision

1. **A resource is counted once, by its known lifetime, never by its records.** It is in scope where its known lifetime
   meets the interval - from its first record to its last, open before the first where it was made before the capture
   and after the last where the stop's rundown held it - so a section mapped through an interval no record of it lies
   in is still in scope. A mechanism projection keeps its mechanism's resources, and a layer projection those whose
   records are of that layer, which each resource now carries: a socket's are the transport's.
2. **`ActiveChannels` counts resources**: every one in scope, or with a process filter those its process held - a
   focus, a focus and a peer both, or a process of each `between` set, either way.
3. **`ActivePeers` counts the processes sharing a resource with a focus**: the other process instances holding a
   resource it held, at any time in scope rather than at once, and never the focus itself.
4. **`MappingCapacity` is the largest extent each section's views mapped, once a section**, however many processes
   mapped it: a lower bound of what it holds, never bytes moved (P3). A section by its handles records no size and is
   an unknown contribution; a synchronization object or a socket maps nothing. An image's sections are said apart.
5. **What no record binds is disclosed, never guessed.** A resource a filter left out whose records bound to no process
   could be the filter's: it is disclosed by those records' reasons, and the count is a lower bound. A peer count beside
   records of the focus's resources bound to none is a lower bound, and unmeasured where they are all there is. A
   capacity no extent is known of is unmeasured, never zero (R21, P1).
6. **Not at this version**: a count of resources' records, a mapping lifetime, a grouped answer, and `sender`,
   `receiver` or a directional `between`, which select data flowing one way where holding a resource is undirected.
   Each is `NoResourceTopology`, saying what it needs.
7. **An answer names its resource rule** as its `correlationRevision`, as an operation answer names its operation rule,
   and the binding rule only where a process filter reads holders (`query-identity-v1` §4).

## Consequences

- §21.1's section scenario is answered: one resource, two memberships, 8 MiB of capacity, and no traffic metric.
- A section found by its handles and by its views counts twice, as ADR-092 measured it twice; no projection joins
  them, and every answer says so.
- `icat metric --basis resource-topology` answers in the words and the document the other bases use, its resources
  by mechanism and what their records establish beside the number.
- Grouped topology answers, a mapping lifetime's duration and a count of records wait for revisions of their own. 31
  mutations of the path are caught.

## Addendum: grouped by a fact of the processes holding them (revision 1006)

A topology answer groups by process, executable, terminal session, package or service container, each group's value
taken over the resources its processes held: how many, the other processes holding one of them, or their sections'
capacity. A resource the processes of several groups held counts in each, as a channel between two processes counts in
both of theirs, so the groups overlap and the total is the ungrouped answer - for peers, the distinct processes sharing a
resource with another - and a remainder answers over the union of the groups it merges. A membership no group can take -
a record bound to no process, or a process without the grouping's fact - is unattributed by why. A group whose processes
shared nothing has no peer where every record of what they held bound, and is unmeasured where a record bound to none
is all that could name one. A grouping by mechanism or peer is not defined yet. 13 mutations of the path are caught.
