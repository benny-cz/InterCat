# ADR-075: A peer ceiling with no peer attributed is not measured, and AF_UNIX is experimental evidence

- Status: accepted; FX-UNIX-001 and FX-UNIX-002 measured through the product, and Unix-domain sockets carry their tier
  on `10.0.26220.0-x64`
- Date: 2026-11-04
- Decision owners: InterCat maintainers
- Relates to: ADR-072, ADR-073, ADR-074, §14.2, R3, R22, P16, P27, FX-UNIX-001, FX-UNIX-002,
  `CoverageTierCalculator`, `UnixSocketCoverageEvaluator`, `tools/InterCat.AfdProbe`

## Context

The Unix sockets profile records AF_UNIX through Winsock AFD's gate (ADR-074). Its rows had not been measured against a
truth through the product, and no build carried a tier for the mechanism. §14.2 computes a tier from fixture counters.
Traffic visualization needs every truth operation observed (at least 95%), bound to a resource instance (90%), carrying
a byte measurement (90%), and at most 1% false peer attributions.

AF_UNIX names no peer. Winsock AFD cuts a socket's path to 14 characters and names no other end (ADR-072), so a capture
attributes no peer at all. `CoverageTierCalculator` counted a peer ceiling with no attribution as met: no attribution, so
no false one. Under that reading a mechanism attributing no peer reaches traffic visualization, which would claim AF_UNIX
is shown as traffic while the product can draw no AF_UNIX link between two processes. R3 says an absent denominator is
unknown, never a pass, and `MechanismMeasurement` says the same. The calculator applied it to every floor but not to the
peer ceiling. It had never mattered: every fixture at traffic visualization, TCP's and UDP's, attributed its peers.

## Decision

1. **A ceiling with no denominator is not measured, never met.** A capture that attributes no peer has its false-peer
   criterion unmeasured. Its gap says that the capture attributed no peer, so none could be checked, and the mechanism
   stays below traffic visualization. No published tier changes.
2. **AF_UNIX is evaluated against its truth by its owner, kind and time.** `UnixSocketCoverageEvaluator` matches each
   truth send and receive to the nearest unmatched row of its owner and kind inside 500 ms. A row's owner is the process
   its socket's create named, never the thread that raised it. A matched row binds the operation to its socket, the
   capture's number for one incarnation, with the bytes the call completed. Rows a stream's reader took a message in, beyond
   the one matched, are counted apart, and every byte of the workload's rows is totalled beside the truth's. Each truth
   connection is a socket its process both created and closed in the capture. No peer is attributed, and each socket's
   peer is a membership left unresolved.
3. **Measured on `10.0.26220.0-x64`: experimental evidence.** Four runs of FX-UNIX-001 under `icat record --profile
   unix-sockets` were machine-wide and loss-free: one connection at a time, eight at once twice, and messages of up to
   256 KiB. Every truth send and receive was a row of its owner, bound to its socket with the bytes it moved, 1,920 of
   1,920; every byte sent and received was counted exactly; and every connection was a socket created and closed.
   - The event-ID filter held: no descriptor beyond the ten counted calls was delivered.
   - The gate admitted exactly the workload's rows.
   - The gaps are the peer's: false peer attribution is not measured, and membership resolution is 0%.
4. **Evidence.** The run eight at once is committed under `fixtures/FX-UNIX-001/evidence/product/`: its truth logs and
   scenario, and the ledger of its rows that `InterCat.AfdProbe --session` wrote from the session. The ledger records each
   row's ordinal, its time on the capture's calibrated wall clock, its owner's role, kind, socket number, bytes and status,
   and never a PID, a path, an address or another process's row (P16). The fixture's measurement states each run, and a
   test reproduces the committed run's counters and tier from them.

## Consequences

- Unix-domain sockets carry ExperimentalEvidence on their build, and `icat capabilities` says so. Their rows are answered
  by `icat metric` in the completed-I/O domain.
- `icat processes` said a process with no TCP or UDP relation made no transport record, which a Unix-domain socket's
  owner contradicts. It now names TCP and UDP. The views do not yet show a process's Unix-domain socket traffic beside its
  TCP and UDP traffic; that is the next step.
- AF_UNIX reaches traffic visualization only through a source that names a socket's peer, which AFD does not.

## Addendum: many short connections, one at a time (revision 903)

FX-UNIX-001's connections were few, one at a time or eight at once. M6's exit asks for a second instance and lifetime
fixture, and the kernel creates a socket at an address an earlier socket held (ADR-072), so FX-UNIX-002 churns them:
`InterCat.TestWorkloads unix-loopback --churn` runs FX-UNIX-001's exchange in 64 connections made one at a time, two
messages of up to 256 bytes each, unpaced. The lab probe, reading AFD in a session of its own, found 234 of the 277
sockets it saw created at an address an earlier socket held, in
`bench/results/afd-unix-feasibility-churn-20261104T114827Z/report.json`.

Matched by nearness alone, the churn's connections were not each found on a socket of its own. The time the workload
stamps on a truth record can read as much as 65 µs earlier than the record before it in its sequence, and a connection
lasts about a millisecond, so nearness on that clock can take a row of the wrong operation. The second decision is
amended:

- **By rank, where a kind's operations and rows agree.** Where a process's truth operations of one kind are as many as
  its rows of that kind, and, taken in order - the operations by their sequence, the rows by the capture's - every
  pair's bytes agree and every pair lies inside the window, each operation is the row of its rank. The truth's clock
  then decides nothing.
- **Otherwise by nearness, on the truth's time moved.** Every other operation is matched to its owner's nearest
  unmatched row of its kind inside the window, on its truth time moved by the median distance of the ranked pairs.
- **Each operation in its connection by the truth's sequence.** When a process's connections never overlap in its own
  sequence, an operation belongs to the connection between whose open and close it was recorded. A connection is a
  socket of its own when every matched operation of it landed on one socket that no other connection's operation is
  on. When connections overlap, as concurrent ones do, that count is not known, never a guess.

Three runs on `10.0.26220.0-x64` under `icat record --profile unix-sockets` were machine-wide and loss-free: two
unpaced, and one with 5 ms between messages.

- Every truth send and receive was a row of its owner with its bytes, 512 of 512 a run, and each of the 64 connections
  at each end was a socket of its own, created and closed.
- Each run's sends at both ends and the client's receives were paired by rank, 384 of 512. The server reads each message
  in two calls, so its receives were matched by nearness; the ranked pairs put the two clocks a few microseconds apart.
- The tier stays ExperimentalEvidence, and the build's coverage names FX-UNIX-002, its latest fixture.
- FX-UNIX-001's four runs, evaluated again, keep their counters. The connections of its runs one at a time are each a
  socket of its own; those of its runs eight at once overlap, so they are not counted.

The first run is committed under `fixtures/FX-UNIX-002/evidence/`: its truth logs and scenario, and the ledger
`InterCat.AfdProbe --session` wrote, whose evaluation now names its fixture from the truth's scenario and states the
operations paired by rank and the clocks' offset. A test reproduces the run's counters and tier from them.
