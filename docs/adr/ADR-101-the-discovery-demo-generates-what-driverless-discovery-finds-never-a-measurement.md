# ADR-101: The discovery demo generates what driverless discovery finds, never a measurement

- Status: accepted for M6's synthetic investigations; named synchronization objects and sections (revision 1020)
- Date: 2026-11-07
- Decision owners: InterCat maintainers
- Relates to: ADR-084, ADR-092, ADR-097, ADR-098, ADR-099, `demo-investigation-v1`, plan M5, M6, R3, P27

## Context

M6 asks for synthetic investigations for each promoted capability. M5's demo investigation holds two hosts' TCP and UDP
and lists every other mechanism as not collected, and many readers and tests rest on exactly what it holds. The
mechanisms M6 promoted - named synchronization objects and sections, topology only; AF_UNIX sockets, window messages and
COM calls, experimental evidence - could be tried only on a capture of one's own, under an opt-in profile with a stated
cost.

## Decision

1. **A second generated investigation, never a change to the demo.** `icat demo <directory> --discovery` writes
   `discovery.icat-workspace` and one session, `demo-discovery`, of one host, the demo workstation; M5's demo is
   unchanged.
2. **It is generated, and says so as the demo does.** Its records come from the demo's provider, its session is
   published under the demo's source identity, which every view states, and it is the same, record for record, every
   time it is made. No value in it is evidence about Windows, and no tier rests on it (P27).
3. **Its records are shaped as a capture's are**, so each view derives from them as it would from a capture: handles
   to a named event the editor shares with the indexer and a semaphore and a section it shares with the preview, made,
   duplicated, closed and held at the stop; the section's views mapped and unmapped by both; a mutex the editor alone
   holds; a view mapped before the capture, as the start's rundown lists it; and a module's view, an image's. The graph
   draws four hubs, the inspector lists six resources and the resource-topology basis answers over seven.
4. **Its capture collected what it holds**, nothing lost, so its coverage states those mechanisms covered and every
   other one not collected.
5. **Each promoted mechanism joins it in a revision of its own**: AF_UNIX sockets, window messages and COM calls next.

## Consequences

- A person can try hubs, the resources list, a resource's records and the resource-topology basis with no capture.
- The window offers the discovery demo in a later revision; until then `icat demo --discovery` writes it.
- 13 mutations of the path are caught.
