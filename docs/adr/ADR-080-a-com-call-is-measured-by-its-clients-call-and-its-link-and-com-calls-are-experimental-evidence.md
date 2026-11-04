# ADR-080: A COM call is measured by its client's call and its link, and COM calls are experimental evidence

- Status: accepted; FX-COM-001 measured through the product, and COM calls carry their tier on `10.0.26220.0-x64`
- Date: 2026-11-04
- Decision owners: InterCat maintainers
- Relates to: ADR-004, ADR-075, ADR-077, ADR-078, ADR-079, §14.2, R3, P1, P4, P7, P8, P16, P27, FX-COM-001,
  `CoverageTierCalculator`, `ComCallCoverageEvaluator`, `RpcCoverageEvaluator`, `tools/InterCat.ComProbe --session`

## Context

The COM calls profile records COM-Perf beside RPC (ADR-079's fourth addendum). `com-call-link-v1` derives a generation's
COM calls, the RPC call carrying each and the link between a call's two ends, and `rpc-call-peer-v2` serves a client's RPC
call through the COM call it carries. They had been measured on lab ledgers made sessions, never against a truth through
the product, so no build carried a tier for COM calls.

§14.2 computes a tier from fixture counters. Traffic visualization needs at least 95% of truth operations observed, 90%
bound to a resource instance, at most 1% false peer attributions, and 90% with a byte measurement. A COM call carries no
size. Its records name its interface and method, and COM-Perf's IPID and OID, which would name its object, are not
admitted (ADR-079's addendum). RPC's evaluator already counts a call as bound to the interface it names, with no lifetime of its
own, and its byte criterion as not measured (ADR-004).

## Decision

1. **A truth call is its client's COM call.** `ComCallCoverageEvaluator` matches each call the client logged issuing to a
   client COM call of the client's process on the interface and method the workload called, FX-COM-001's
   `IDispatch::Invoke`. It matches each call the server logged serving to a server COM call of the server's process on them.
   - Where an end's truth records are as many as its calls and every pair lies inside 500 ms, each record is the call of
     its rank, so the truth's wall clock decides nothing.
   - Otherwise each record is its end's nearest unmatched call inside the window, on the truth's time moved by the
     ranked pairs' median offset.
   - Calls of the workload the truth does not log are counted beyond the truth: a method's name looked up through
     IDispatch, and COM's own calls on the object's interfaces and references. Other processes' calls are scope.
2. **A call is bound to the interface it names, and its peer is the process its link reached.**
   - A matched call names its interface and method, never its object, so it is bound to the interface, as an RPC call is.
   - The interface has no create or destroy record, so resource discovery is not met. Its two ends are its
     memberships, each resolved where a call was observed at it.
   - A peer attribution is a matched client call linked under `com-call-link-v1`. The peer is the process that raised
     the server call the link reached, never the process the client's start names. An attribution is false when that
     process is not the truth's server.
   - No call carries a size, so the byte criterion is not measured, never a measured zero (R3, P1).
3. **Beside the counters, the evaluation states what the product holds of each call:**
   - whether its start and stop paired, with the HRESULT the client logged, read as the capture keeps one, its 32 bits
     unsigned;
   - whether one RPC call carries it;
   - whether its link reached the server's call of the same truth call;
   - whether the RPC call carrying it was served through it, under `rpc-call-peer-v2`, by an RPC call of the truth's
     server.
4. **Measured on `10.0.26220.0-x64`: experimental evidence.** Five runs of FX-COM-001 under `icat record --profile
   com-calls` were machine-wide and loss-free: paced twice, 64 calls 5 ms apart; 256 and 1,024 calls unpaced; and 512 in
   bursts of 128 a second apart. In all 1,920 truth calls:
   - each was its client's COM call on `IDispatch::Invoke`, its start and stop paired, with the HRESULT the client logged;
   - each was linked to the server's call of the same truth call;
   - each was carried by one RPC call served through it by the RPC call carrying the server's.
   - Every call the server logged serving was a server call of its process.
   - No call was attributed to another process.
   - Each end's records paired by rank in every run, and the capture's calibrated clock read 0.09 to 0.19 ms ahead of
     the truth's.
   - The gaps are the byte's and the object's: byte measurement is not measured, and resource discovery is 0%.
5. **Evidence.** The first paced run is committed under `fixtures/FX-COM-001/evidence/product/`: its truth logs and
   scenario, and the ledger that `InterCat.ComProbe --session` wrote of the workload's COM calls as the product derives
   them.
   - For each call, the ledger records:
     - its ordinal, end and state;
     - its process's role, and the role its start names;
     - its interface, method and HRESULT;
     - its start and stop on the capture's calibrated wall clock;
     - its carrier and link states;
     - its other end's ordinal and role;
     - what its RPC call reached.
   - It holds no PID, thread, activity id or other process's call (P16).
   - The fixture's measurement states each run, and a test reproduces the committed run's counters and tier from them.

## Consequences

- COM calls carry ExperimentalEvidence on their build, and `icat capabilities` says so; COM-Perf's catalog notes state
  the measurement.
- COM calls reach traffic visualization only where a call's bytes are measured. Neither COM-Perf nor the RPC call carrying
  it records them, and the tier counts calls.
- M6's exit still asks the COM package for:
  - a second instance and lifetime fixture;
  - an ambiguity case;
  - a schema-failure case;
  - exact source drill-down.

## Alternatives considered

- **Bound to nothing, as no object is named.** It would state the same tier: the byte criterion keeps COM calls below
  traffic visualization either way. RPC's calls are bound to the interface they name, so COM's, carried by them, are as
  well. The missing object stays stated as resource discovery's gap.
- **A truth call counted at both ends.** The truth logs one call at each end, but it is one call. Counting the server's
  end as a second operation would count it twice, which ADR-079's first decision refuses for COM and RPC. The server's
  end is stated beside the counters, and the link between the two ends is the peer.
- **The peer read from the client's start.** The process a client's start names is the record's word, which the link
  checks against the server's (ADR-079's third decision). The link reached a call that process raised, so the peer is
  the process the link proved.
