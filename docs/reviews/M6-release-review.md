# M6 release review — broader driverless Windows discovery

Date: 2026-11-18, at plan revision 1062
Scope reviewed: M6 as built from revision 895 to 1024 and refined since: the Unix-domain sockets, COM calls, named
synchronization objects, window messages and sections packages (ADR-072 to ADR-092), mailslots measured unsupported
(ADR-085), processes named by package, service and terminal session (ADR-093 to ADR-096), resources by namespace and
lifetime (ADR-097, ADR-098), the graph's and inspector's resources (ADR-099), the capability catalog (ADR-100) and the
discovery demo (ADR-101); the inventory adapter's version (revision 1060), a second build's constants (revision 1056)
and the sections profile measured through the product (revision 1061).
Method: §15.2 asks, before a post-v1 milestone ships, for the user problem, the exact new evidence obtainable, the
supported environments, the expected overhead, the deployment burden and the exit fixtures. Each is answered below for
what revision 1062 ships, M6's exit tests and deliverables are checked against named fixtures, tests and measurements,
and what is not built is listed as not built.

## The user problem

Explore sees processes, TCP and UDP, RPC and named pipes. A machine's other IPC - AF_UNIX sockets, COM calls between
processes, named synchronization objects, window messages, shared sections - left no edge, so a person could not tell
what talked to what through them, nor that InterCat could not see it.

## The new evidence obtainable

Each opt-in profile's, measured against a truth, each claim no wider than its source:

- AF_UNIX calls with their lengths, by socket incarnation, never a path or a peer (ADR-072 to ADR-076).
- A COM call at both ends, linked to the RPC call carrying it, never an activation as a call (ADR-077 to ADR-081).
- A named object's handles, by its one number in the capture, named at their close, never a signal or a wait (ADR-082 to
  ADR-084).
- A window message's sending and retrieval between processes, never which message (ADR-086 to ADR-088).
- A section's handles and views, topology only, holding proving shared access, never traffic (ADR-089 to ADR-092).
- Processes named by package, service and terminal session; resources listed by namespace and lifetime, idle ones apart.

Mailslots were measured unsupported - no driverless source names a mailslot's traffic (ADR-085) - and are routed to M7,
M9a and M10.

## Supported environments

Windows 11 25H2 x64 - 26200 and the 26220 pre-release branch, where every tier was measured - and, since revision 1056,
26H2's 26320 pre-release branch as a candidate, its per-build constants measured. Each profile is opt-in, recorded by
`icat record` elevated or through the broker, and stated before it starts.

## Expected overhead

Each profile measured through the product against no capture, in rotating rounds over a workload heavy in its mechanism:

| Profile | Measured | Evidence |
|---|---|---|
| `unix-sockets` | Moderate: a median 1.5 CPU pp | `bench/results/unix-sockets-impact-20261104T110457Z` (revision 902) |
| `com-calls` | Moderate; a COM-heavy workload about 35% slower, nothing dropped | `com-calls-impact-*` (revisions 920, 921) |
| `sync-objects` | High on a handle-heavy workload: 11.8 pp, 18.4% slower, nothing dropped | `sync-objects-impact-*` (revision 943) |
| `window-messages` | Moderate; the workload 8.2% and 10.8% slower | `window-messages-impact-*` (revision 968) |
| `sections` | Moderate on a view-heavy workload: 1.9 pp, 17.6% slower, past the throughput target, nothing dropped | `sections-impact-*` (revision 1061) |

## Deployment burden

None beyond Explore's: no driver and no hook. Each profile is a capture a person chooses, its cost said before it
starts, and Explore admits none of M6's sources.

## Exit tests

| Exit test | AF_UNIX | COM calls | Sync objects | Window messages | Sections |
|---|---|---|---|---|---|
| Two instance and lifetime fixtures | FX-UNIX-001, FX-UNIX-002 | FX-COM-001, FX-COM-003 | FX-SYNC-001, FX-SYNC-003 | FX-WMSG-001, FX-WMSG-002 | FX-SECT-001, FX-SECT-002 |
| An ambiguity case | FX-UNIX-003 | FX-COM-005 | FX-SYNC-004 | FX-WMSG-003 | FX-SECT-003 |
| A permission or schema failure case | revision 905 | revision 923 | revision 949 | revisions 959, 962 | revision 984 |
| Exact source drill-down | revision 906 | revision 922 | revision 950 | revision 967 | revision 985 |
| Baseline overhead comparison | revision 902 | revision 920 | revision 943 | revision 968 | revision 1061 |

Each package's failure case is the one its own source can meet - a schema lacking a field its gate decides by, the
profile refused with its reason - as ADR-074's addendum reads the test. A permission failure is every profile's alike: a
capture attempted without elevation creates nothing and says why ("R16: a capture attempt without elevation creates
nothing and says why"), and through the broker only after Windows' approval.

The two guarantees hold:

- A COM activation never becomes a call count: `ComCall` is a mechanism of its own and an activation none, and only the
  four synchronous call events are admitted (ADR-077 decision 3, ADR-078, ADR-079's addendum).
- An AF_UNIX endpoint is never reported fully observed: no path is admitted and no peer drawn, its false-peer criterion
  never met, so it stays below traffic visualization (ADR-075 decision 1, ADR-097's addendum, ADR-099 decision 1).

## Deliverables

| Deliverable | Built | Evidence |
|---|---|---|
| Versioned adapters | Yes | each rule versioned - coverage-v3, process-binding-v5 to v8, com-call-link-v1, resource-instances-v1 to v3 - and the inventory adapter `windows-etw-inventory-0.9.0` since revision 1060, which 0.8.0 had stayed through M6 |
| Per-build coverage extensions | Yes | `measured-coverage.json`; measured classic layouts (ADR-091); 26320's constants (revision 1056) |
| Graph and resource inspectors | Yes | ADR-099 (revisions 1007 to 1009), ADR-097's addenda (1010 to 1013), ADR-079's seventh addendum (1022) |
| A searchable capability catalog explaining unsupported mechanisms | Yes | `icat capabilities --catalog --find` (ADR-100, revision 1019) |
| Synthetic investigations for each promoted capability | Yes | `icat demo --discovery` and the window's Explore the discovery demo (ADR-101, revisions 1020 to 1024) |
| Speculative names and handle-derived candidates kept apart | Yes | a resource named only as its records name it, two names both kept (ADR-097 decision 3); a candidate binding marked "· candidate" in the evidence key's candidate style; a holding a neutral membership counted in no total, its card saying it proves shared access, never traffic (ADR-099 decisions 3 and 5) |
| Package, service and terminal session enrichment | Yes | ADR-093 to ADR-096 (revisions 988 to 1001) |
| Namespace-aware browsing, resource lifetimes, idle resources apart | Yes | `icat resources` (ADR-097, revisions 1002 and 1003), the window (1010), ADR-098 |

## Not built, each a source's measured limit or a refinement

- What each source does not carry: an AF_UNIX socket's path or peer; a COM call's bytes or object, asynchronous calls,
  WinRT and cross-machine DCOM; a signal or a wait on a named object; which window message.
- The clipboard, which the window messages package names, declared but never measured; DDE not told apart from other
  messages.
- A process's token elevation and integrity admitted (measured, needing a wider record); a service's status changes
  during a capture; the catalog in the window; a derivation checkpoint keeping resources.
- A tier measured on 26320 for any of M6's mechanisms: their tiers rest on 26220's fixtures.

## Verdict

M6's exit gate is met. Every newly supported mechanism - AF_UNIX sockets, COM calls, named synchronization objects,
window messages and sections - holds two instance and lifetime fixtures, an ambiguity case, a schema failure refused with
its reason, exact drill-down to its source records and a baseline overhead comparison through the product; both of the
milestone's guarantees hold; every deliverable is built; and mailslots, measured unsupported, are routed on as its
fallback asks. What is listed above as not built is each a source's measured limit or a refinement, none an exit test.
