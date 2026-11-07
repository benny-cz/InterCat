# ADR-101: The discovery demo generates what driverless discovery finds, never a measurement

- Status: accepted for M6's synthetic investigations; named synchronization objects and sections (revision 1020), AF_UNIX
  sockets and window messages (revision 1021), COM calls (revision 1023); the window offers it (revision 1024)
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
- The window's Investigation menu offers it since revision 1024, beside the demo; `icat demo --discovery` writes it too.
- 13 mutations of the path are caught.

## Addendum: AF_UNIX sockets and window messages (revision 1021)

The editor connects an AF_UNIX socket to one the indexer accepted beside the indexer's listening socket, which carries
nothing and is idle; it sends five requests and the indexer answers each, the last answer's size unmeasured, every call
naming the socket's owner and the number its gate gave the socket. It sends three messages to the preview's window, each
at both ends with its start naming the preview, and the preview posts two back, each queued and retrieved, every end by
the message's number. The sockets are listed with their traffic, never hubs; each process counts its sockets' calls and
its window messages; and the resource-topology basis answers over ten resources. COM calls join it next.
9 mutations of the path are caught.

## Second addendum: COM calls (revision 1023)

A fourth process, `comhost.exe`, serves four COM calls the editor makes, each carried by an RPC call at both ends as
COM-Perf records them beside RPC (ADR-079): the editor's COM call and the RPC call of its activity on one thread, and
the host's RPC call around the host's COM call on its thread, the host's carrying the editor's activity, each start
naming the process at its other end. `com-call-link-v1` links each call's ends, `rpc-call-peer-v2` serves each RPC call
through them with no ALPC record, and the graph draws the editor's RPC edge to the host as revision 1022 draws what COM
calls link. With it the demo holds each mechanism M6 promoted.
5 mutations of the path are caught.

## Third addendum: the window offers it (revision 1024)

The window's Investigation menu offers Explore the discovery demo beside Explore the demo. The first time it makes the
discovery demo in a folder of InterCat's own beside the demo's - `discovery`, or `discovery-2` and on where one holds
anything else, which is left as it is - and later it opens the same one; it opens its investigation beside the window and
says, where the capture card states what happened, that InterCat generated it. While it is being made a second click
starts nothing.
7 mutations of the path are caught.
