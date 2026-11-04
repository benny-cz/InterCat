# ADR-074: A gated source admits through its gate, and a ledger counting its omissions is coverage-v3

- Status: accepted for M6's Unix-domain sockets work package; Winsock AFD admits through its gate, which the opt-in
  Unix sockets profile requests (revision 899), and is refused where its gate could not decide (revision 905)
- Date: 2026-11-04
- Decision owners: InterCat maintainers
- Relates to: ADR-018, ADR-072, ADR-073, plan M6, §4.2, §9.3, §18.1, §18.3, §20.6, R3, R9, R21, R22, P12, FX-UNIX-001,
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

## Addendum: the Unix sockets profile (revision 899)

An opt-in profile, `unix-sockets`, now requests Winsock AFD, beside Kernel-Process, both required. Without AFD it does
not start, rather than record lifecycle alone. `icat record --profile unix-sockets` records it elevated, and
`icat capture --profile unix-sockets` asks the broker for it as it asks for Explore, taking no focus.

- **What it enables.** AFD is requested for `AFD_KW_SOURCE_WINSOCK` (`0x4`), the keyword every call the gate counts
  carries, and each of the ten calls by its event id. A keyword mask of 0, which ETW reads as every keyword, would also
  have enabled what the transport indicates. ADR-072's cost was measured collecting both source keywords and every event,
  more than the profile requests, so the Moderate class it states bounds the profile's collection from above. The
  profile itself is not measured whole, so its cost is its sources', each stated with its own.
- **What it collects.** AFD's process filter does not hold, so the profile collects every socket call of the machine and
  admits, through the gate, the calls of each socket created AF_UNIX. Its summary says so, and Explore never admits AFD.
- **What its records become.** An admitted record derives a Unix-domain socket row owned by its socket's creator, with
  its socket's number as its connection, and with a transfer's length as completed I/O on its side. Its fact key is its
  family's own, `unix-socket-operation`: no capture had admitted AF_UNIX before, so no row is re-keyed.
- **An import's counts.** An import that admits through a gate counts what the gate omitted, and `icat import` says it
  when it omitted any. `icat import` itself admits lifecycle and Kernel-Network alone, so its imports omit none.

A fixture measured through the product is still the next step: FX-UNIX-001 recorded by `icat record --profile
unix-sockets`, which could give the mechanism a tier.

## Addendum: what the Unix sockets profile costs (revision 902)

The profile's cost was measured through the product, as RPC peers' was (ADR-035's fifth decision), in
`bench/results/unix-sockets-impact-20261104T110457Z/product-impact.json`. `InterCat.AfdProbe --product-impact` ran
FX-UNIX-001's exchange - eight connections at once, unpaced, 2,048 messages each - in nine rounds: with no capture,
under `icat record --profile unix-sockets`, and under `icat record --profile explore`, in an order rotating each round.

- The profile added a median 1.5 CPU percentage points over no capture, where Explore added 0.8. Its own process used
  3.9 points, against Explore's 0.8. The workload was 4.9% slower, and nothing was lost.
- The machine was 14.5% busy with no capture, so a single round's difference ranged from 25 points below to 9 above.
  The capture process's own share is a lower bound that background load cannot move, and it too is above the Low
  ceiling of 1 point.
- The profile states Moderate, its source's class, with this evidence, so `icat profiles unix-sockets` states its
  measured cost.

## Addendum: a gate that could not decide refuses its source (revision 905)

M6's exit asks each mechanism for a permission or schema failure case. A gate decides each record by fields of its
descriptor: AFD's gate by a call's phase, its socket's address and the slot its owner is written to, a send's by its
location as well, and a create's by its family and the process it was made for. The admission plan compiler leaves out
a field the machine's schema does not admit, saying so in its field report (§18.3), and a gate reading that field read
zero. A create of family zero is no AF_UNIX socket's, so on a build whose AFD create carried no family a capture would
have followed no socket and said it saw none, its coverage covered with nothing admitted: a value never observed (R3).

- **A gated source is refused when its gate could not decide.** When the plan a machine's schema compiles for a gated
  source has a descriptor without a field its gate decides that descriptor's records by, the source is refused. The
  refusal names the descriptor, the field and what the gate could not tell without it.
- **Where it shows.** A profile requiring the source, as the Unix sockets profile requires AFD, does not start, and
  `icat record` says why; `icat capabilities` states the source's schema unknown, for the same reason. The check runs
  once, as the plan compiles, never on the callback path.
- **No contract changes.** A refused source is not collected, so no capture states anything of its records.

Tests take each field the gate decides by out of one template of AFD's registered layout in turn - a create's family
and process, a close's phase and address, a send's location, an accept's owner slot - and hold the plan, the capture's
compilation, the profile and the capability report to the refusal, the whole layout admitted; eight mutations are
caught.
