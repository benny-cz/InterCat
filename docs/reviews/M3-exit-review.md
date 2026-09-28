# M3 exit review — Windows IPC breadth and content

Date: 2026-09-28, at plan revision 252
Build reviewed: `main` at revision 251 (`695690b`), on Windows 11 25H2 x64, build 10.0.26220.0 (Primary)
Method: each item of §14's M3 exit gate is checked against evidence already in the repository - fixtures with truth logs
(`fixtures/index.json`), named tests, ADRs and bench results - and against live passes of the Release window and CLI over
fresh captures recorded into scratch. A gate item is met only by evidence that names it; an unavailable feature is
reported as unavailable, as the gate requires, and is not called implemented.

## The exit gate

| Gate item | Verdict | Evidence |
|---|---|---|
| RPC layered over transport without duplicate volume | Met for RPC over ALPC, the local transport; RPC over TCP is not measured | An RPC call carries no byte value, so it adds none: "P2: an RPC call never reports a byte value, and the byte criterion stays not measured". Calls are counted from the calls, never from the records beneath them ("P4: started, completed and failed calls are counted from the calls…"). With ALPC collected, the graph joins a caller and the process that served it by an RPC edge counting the linked calls' records and never ALPC's ("§7.4: the overview joins a caller and the process that served it by an RPC edge of the linked calls' records"; revisions 225–229, ADR-034, ADR-035). FX-RPC-001 on this build. |
| Pipe instance ambiguity without invented peers | Unavailable, explicit | Named pipes measured `Unsupported`: FX-PIPE-001 observed 0 of 25 truth operations through Kernel-File (ADR-003). No pipe record is admitted, so no pipe peer is drawn or guessed; a pairing is checked against truth rather than assumed ("P7: a pipe peer pairing is checked against the truth log rather than assumed"). The capability report and `docs/PER-BUILD-COVERAGE.md` state the tier. |
| Shared-resource membership without invented traffic | Unavailable, explicit | No shared-section source is measured: the Kernel-Memory candidate is registered and compiled but no fixture proves membership, so no section topology is drawn and no traffic is invented for one. Its mechanism has no tier on any build. |
| Content truncation and encryption states | Met | Truncation: FX-CONTENT-001 kept 2,151 messages whole, cut 3,015 at the record limit and omitted 2,834 at the session limit, each with its length ("I21: a recording keeps each fixture message's bytes beside its journal chunk, and stops at its content limit"); the viewer states what was cut and never pads it (P2). Encryption: FX-HTTP-002 measured that WinINet's capture holds an HTTPS exchange's plaintext and no record says whether it was encrypted (ADR-037 decision 6); a content request says so before it records ("R17: a WinINet content request says before it records that an HTTPS exchange is kept as its plaintext"), and the viewer states an application payload's encryption fact. |
| Publish per-build coverage | Met | `docs/PER-BUILD-COVERAGE.md`, projected from the fixture index's structured tiers and checked against it, and the same coverage embedded in every build, which `icat capabilities` states for its own build only (revision 251; "P27: the coverage a build carries, and the page that publishes it, are the index's latest evidence…"). |

## The implement list

| Item | State |
|---|---|
| Validated RPC/ALPC adapters and correlators | RPC admitted to Explore (ExperimentalEvidence); calls paired by activity id (ADR-031, `operations-v1`); an RPC call's other end resolved through ALPC in the opt-in RPC peers profile (ADR-034, ADR-035). |
| Pipe adapter at proven coverage | None proven: `Unsupported` (FX-PIPE-001). |
| Shared-section resource topology at proven coverage | None proven. |
| Application/transport projections | Byte domains and layers kept apart (`metrics-v1`): transport bytes and HTTP message bytes never sum or rank together (P3), in metrics, rows and rankings. |
| Unresolved-resource UX | RPC calls whose other end is unresolved say why (revision 225); a process's connections whose other end no record holds are rows of its rung (revision 248); an HTTP part or exchange not recorded whole names what it lacks (revisions 242, 244). |
| Optional timing profile | Unavailable: thread scheduling and stack settings still need a measured overhead preview and a bounded retention policy. |
| Content opt-in flow, raw/hex/text viewer, a validated content-capable source | WinINet's capture (ADR-037), admitted under a bounded Content request through `icat record`, scoped to the named processes themselves (revisions 239, 245); the viewer and `icat content` (revision 236); parts and exchanges (revisions 242, 247); FX-HTTP-001 to 003 and FX-CONTENT-001. |

## Verdict

M3's exit gate is met for its measured scope. The two features the gate names that are not implemented - pipe instance
topology and shared-section membership - remain explicitly unavailable, which the gate itself requires of an unavailable
feature, and move with their measured gaps to M7 (cooperative instrumentation and shared-memory semantics) and M9
(optional kernel collectors), as §14's fallback allows. RPC over TCP transport, HTTP/2, compressed responses and
asynchronous WinINet are unmeasured, and the timing profile is unavailable; each is stated where it applies, and none is
claimed.

M4 - multi-machine investigation - is next in dependency order; M5 depends on it and on M3.
