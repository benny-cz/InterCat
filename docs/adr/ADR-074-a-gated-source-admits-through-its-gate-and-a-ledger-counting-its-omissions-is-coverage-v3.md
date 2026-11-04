# ADR-074: A gated source admits through its gate, and a ledger counting its omissions is coverage-v3

- Status: accepted for M6's Unix-domain sockets work package; Winsock AFD admits through its gate, and no profile
  requests it yet
- Date: 2026-11-04
- Decision owners: InterCat maintainers
- Relates to: ADR-018, ADR-072, ADR-073, plan M6, §4.2, §9.3, §18.1, §20.6, R9, R21, R22, P12, FX-UNIX-001,
  `contracts/coverage-v2.md`, `contracts/coverage-v3.md`

## Context

ADR-073's gate decides of each Winsock AFD record, as it arrives, which socket it is of and whether it counts one of the
socket's calls. Admission so far decided a record by its descriptor alone: a profile admits a descriptor, denies it, or
never asked for its provider, and the coverage ledger counts each descriptor's records as admitted, omitted for the one
reason that covers all of them, or undecodable (`coverage-v2` §3). AFD's records of one admitted descriptor are not
alike: a send raises four, and every family's sockets and those made before the capture raise them too. Admitting AFD
through its gate needs three things that model lacks:

- a place on the capture callback where the gate reads a record's slots before it is queued;
- the gate's values written into the record in place of the kernel addresses the source raised;
- a count of what the gate did not admit, which is neither a policy's omission nor a loss.

## Decision

1. **A source may have a gate.** `RecordGates` gives each source of a capture's admission table its gate; only Winsock
   AFD has one. The admitter calls it once a record's slots are read and before its extended data is copied. The gate
   admits the record, its gated slots rewritten, or omits it with an `EN-GateOmission`. It runs on the capture callback,
   one record at a time, with bounded state, and the walk of what the callback can reach (P12) reaches it and the tally's
   count of its omissions.
2. **AFD's admitted records carry the gate's values.** `AfdRecordGate` writes into an admitted record's `Endpoint` slot the
   socket's number in the capture, and into its `Process` slot the process its create named. AFD admits the ten calls the
   gate counts: create, close, send, receive, connect and ConnectEx, accept and AcceptEx, and bind with and without an
   address. Each carries its phase, location, process, endpoint and status; a create also carries its family, type and
   process, and a transfer its length. None carries a buffer's address or a socket's, the path being cut to 14 characters
   (ADR-072). An accept's new socket follows a variable-length address in its record, so none is read: an accepted socket
   is followed from its own create, as .NET's accept makes one ahead of the call.
3. **A gate's omissions are counted by reason, apart from the policy's.**
   - `NotCounted`: a record of a socket the gate follows that counts nothing of it.
   - `NoFollowedResource`: a record of no socket the gate followed, whether another family's or one made before the
     capture.
   - `BeyondBound`: a create beyond the 65,536 sockets the gate follows.

   The capture's health ledger counts them, the coverage tally counts them under their descriptor, and `icat record` says
   them beside the policy's.
4. **coverage-v3.** A ledger that counts a gate's omissions is `coverage-v3`: `coverage-v2` with a delivery's `gated`
   counts by reason. They join its outcomes, so that `admitted + omitted + Σ undecodable + Σ gated = delivered`, and never
   appear on a whole-provider delivery. Every other capture still writes `coverage-v2`, and a `coverage-v2` file is read
   as one whose deliveries count none. A redacted package keeps a gated ledger `coverage-v3`.
5. **Coverage.** A gate's omissions are no loss and leave a mechanism covered; its reason then says how many records the
   gate admitted. A create beyond the bound makes the mechanism a partial gap, since that socket's records are not
   admitted.

## Consequences

- No profile requests AFD yet. The next step is normalizing its admitted records as Unix-domain socket observations, an
  opt-in profile (collecting AFD costs Moderate), and a measurement of FX-UNIX-001 through `icat record`, which could
  carry a tier. An import's counts do not yet name a gate's omissions; the profile that makes AFD importable will.
- No test drives the admitter with an AFD record, because no ETL of AFD is committed: AFD records every socket of the
  machine. The walk holds the admitter to calling the gate, tests hold the gate on records built from AFD's registered
  layout, and the measurement through `icat record` will hold the whole path.
- The gate's state is bounded by the sockets it follows and the closed sockets it remembers (ADR-073). The dictionaries
  that hold them grow to that bound and no further.
