# ADR-017: `between(A,B)`, the records connecting two process sets

- Status: accepted for M1
- Date: 2026-09-23
- Decision owners: InterCat maintainers
- Relates to: §19.1 (process filters), §23 (`EN-BetweenDirection`, `EN-FilterDimension`), §10.5 (canonical form),
  §24 (version axes), R18, R22, P6, ADR-014, ADR-015, `contracts/metrics-v1.md`, `contracts/query-identity-v1.md`

## Context

§19.1 defines `between(A,B)` in one line: it "selects relationships connecting the selected participant sets under the
chosen direction policy". No direction policy existed in §23. The line also left three questions open: what connects
two sets, whether the filter combines with a process focus, and how the canonical form writes it.

`peer(P,Q)` answers one pair, and only relative to a focus. A user who asks for the traffic between a client and the
server it talks to can ask `participant(P)` narrowed to peer Q. Between all instances of one executable and a set of
servers, the user has no request at all. The obvious composition, `participant(A)` together with `participant(B)`,
does not answer it. For A = {X} and B = {X, Y}, both filters keep X's records to a third process Z, and nothing about
those records connects the two sets.

## Decision

1. **`between(A,B)` names two nonempty sets of process instances and a direction.** Instances are identities from the
   generation, never PIDs (R22). An empty set does not mean "any process": that would admit remote and unresolved ends
   without saying so. `participant(P)` already asks "P with anyone".
2. **A record connects the sets when one set holds its maker and the other its other end.** The maker is the record's
   own binding. The other end is the one `relations-v1` finds. Both bindings must be admitted by the request's
   evidence policy. The sets may overlap, so a process connected to itself is between any set that holds it and
   itself.
3. **`EN-BetweenDirection` is the direction policy: 1 `Either`, 2 `FirstToSecond`, 3 `SecondToFirst`.**
   - `Either` keeps every record that connects the sets, including their connects, accepts and disconnects.
   - `FirstToSecond` keeps the data that left a process of A and reached one of B. That is A's `Send` records whose
     other end is in B, and B's `Receive` records whose other end is in A.
   - `SecondToFirst` is the same with the sets exchanged.
   - A record with no data direction belongs to `Either` only. This follows §19.1's rule for `sender(P)` and
     `receiver(P)`: direction is a known data direction, never an assumed client or server role.
4. **The filter selects records, and the accounting still decides which end measures them.** `BytesSent` under
   `SendSide` with `FirstToSecond` is what A sent to B, measured where it left. Under `ReceiveSide` it is the same data
   measured where it arrived.
5. **It is a process filter of its own.** It names the processes at both ends, so a request that also names a focus or
   a peer is refused rather than silently intersected. A peer grouping is refused for the same reason: it is relative
   to one focus. A peer count needs a process to count from, so it takes a grouping by process or executable. A
   channel count needs none, and counts the connections between the sets.
6. **What it cannot decide is disclosed.** A process of either set may have made a record whose other end is unresolved
   or not admitted. Such a record could connect the sets, so it is counted by reason and never added (P6). A record
   made by neither set cannot connect them, whatever its other end.
7. **The canonical form writes one meaning one way.** `between` is a second form of the `ProcessInstance` term:
   `{"facet":"ProcessInstance","between":[[A…],[B…]],"direction":"<name>"}`.
   - Each set is sorted and holds each instance once.
   - `SecondToFirst` is written as `FirstToSecond` with the sets exchanged.
   - Under `Either`, the lower set is written first.

   No earlier specification could name the term, so no earlier identity changes. `query-identity-v1` §9 admits such an
   addition under version 1.
8. **An answer read through relations names the binding rule.** A relation's ends are held by process instances, so
   an answer that depends on a relation rule depends on the binding rule too.
   - `entityRevision` is now present whenever `correlationRevision` is.
   - `evidencePolicy` is present only where the policy decides which records are kept or where they are grouped.
   - Every answer that reads a binding derives instances from the start keys the session holds.

## Consequences

- On the measured `peak` capture, the conversation between the workload's two processes is 34,300,070 B either way,
  split into 34,168,998 B from the client to the server and 131,072 B back. The sets are connected by 8 channels. Every
  one of these figures matches the corresponding focus answer.
- Verification found the defect decision 8 fixes. Only a focus or a process grouping loaded the session's start keys,
  so a between filter on a session that has start keys derived other instance identities, and could not find the
  instances `icat processes` prints. A channel count over every process was affected the same way, and its identity
  named a relation rule but no binding rule. That request had been answered for one plan revision. It is the only
  identity that changes, and no line of the golden corpus changes. The corpus now holds both new forms.
- `icat metric` accepts `--between <ids> --and <ids> [--direction either|first-to-second|second-to-first]`, with each
  set a comma-separated list of instance ids.

## Alternatives considered

- **Compose `participant(A)` and `participant(B)`.** Rejected: an overlap of the sets keeps records that connect
  nothing (Context).
- **Allow a focus alongside `between`.** Rejected: two selections would intersect with no rule saying what the result
  means. `participant(P)` narrowed by `peer(P,Q)` already answers one pair relative to P.
- **Direct by client and server role.** Rejected: a role is inferred from who connected, not from the data, and
  §19.1 forbids an assumed role.
- **Let an empty set mean "any process".** Rejected (decision 1).
