# ADR-004: RPC coverage scope after the local-call measurement

- Status: accepted for M0; revisit when the fixture runs on a supported build
- Date: 2026-09-21
- Decision owners: InterCat maintainers
- Relates to: ADR-002 (session strategy), ADR-003 (named-pipe scope)

## Context

M0 requires an explicit tier for every proposed initial adapter, computed from §14.2 rather than argued.
`Microsoft-Windows-RPC` was the candidate for the RPC mechanism: §4.2 recorded its schema, and its start
descriptors carry an interface UUID, a procedure number and a protocol before the variable-length endpoint
strings.

## Measurement

Fixture `FX-RPC-001` issues a known number of local RPC calls to the Windows service control manager
through the documented service API, logging every call itself. The fixture ran twice inside one owned
session on build `10.0.26220.0-x64`, with descriptors 5, 6, 7 and 8 admitted and 10 and 11 denied.

| Quantity | Result |
|---|---|
| Truth calls with an admitted observation | 8 of 8 |
| Calls bound to the expected interface | 8 of 8 |
| Completions paired through the activity id | 8 of 8 |
| Server-side call records observed | 164 |
| Client-to-server peers paired | 0 |
| Byte measurement | none exists on any descriptor |
| Reproduced in a second run inside the same capture | yes |
| Computed tier | `ExperimentalEvidence` |

## Findings

1. **RPC yields operations, never volume.** No descriptor of this source carries a size. The byte
   criterion is therefore reported as *not measured*, which is deliberately different from a measured
   zero (R3, P1). An RPC annotation placed over a transport observation adds no bytes to it (I11, P4).
2. **An interface is not a lifetime.** The source has no create or destroy record for an interface, so
   the §14.2 resource-discovery criterion cannot be satisfied from it alone. A resource projection for
   RPC needs a different source or a different definition of the resource.
3. **Both sides are visible, but not linked.** Server-side records arrive in the same capture, yet no
   client call shared an activity id with a server call. InterCat therefore leaves the peer unresolved
   rather than pairing on timing or on interface similarity (P7, P8). Pairing local RPC endpoints is an
   open problem, not a solved one.
4. **An allow list and a deny list cannot coexist in one ETW event-id filter.** Supplying both makes the
   provider refuse enablement outright. The allow list already excludes every other descriptor, so it is
   the one sent, while the deny list stays in the recorded plan and in the callback as a second refusal.
   Payload-bearing descriptors are still never enabled (P28).

## Decision

1. RPC is reported at tier `ExperimentalEvidence` with the two gaps stated field by field: no byte domain
   and no interface lifetime.
2. **No RPC volume claim may be made anywhere in the product.** RPC counts operations, and any byte figure
   shown beside an RPC call must come from the transport beneath it and be labelled as such.
3. Peer pairing for local RPC stays unimplemented until a correlation contract exists that does not rely
   on timing. IC-006's remaining work is that contract, not a heuristic.
4. ALPC stays unmeasured. It is a kernel flag group with no registered manifest provider, and ADR-002
   deliberately excludes the system logger that would be needed, so no tier is claimed for it at all.

## Consequences

- Release material lists RPC with tier `ExperimentalEvidence` and the build it was measured on.
- The §5.1 layering rule now has measured backing: RPC operations and transport bytes come from different
  sources with different domains, so summing them was never possible by accident.
- The capability report's RPC entry carries this measurement, so a reader sees the gaps rather than a
  provider name.

## Addendum: RPC over TCP (revision 894)

M3's exit review left RPC over TCP unmeasured. Fixture `FX-RPC-002`'s client asks the endpoint mapper on 127.0.0.1,
port 135, where the service control manager listens over TCP, through the documented `RpcEpResolveBinding` and with no
credentials: each resolution is one call to the endpoint mapper's map procedure over `ncacn_ip_tcp`. The client logs
each call and, from the system's TCP table, the connections beneath them. `icat record` recorded it five times on build
`10.0.26220.0-x64`, under Explore and RPC peers, 64 calls from one client and 256 from eight clients at once
(`bench/results/rpc-tcp-*-20261104T*`):

| Quantity | Result |
|---|---|
| Truth calls observed as a client call to the endpoint mapper, procedure 3 | every one: 64 of 64, 256 of 256 |
| The protocol its start carried | 1 on every one, where FX-RPC-001's local calls carry 3 |
| Completions paired through the activity id | every one, none failed |
| TCP connections beneath, to the endpoint mapper's host on port 135 | one a client: 1, and 8 from eight clients |
| Bytes | on TCP records alone, alike at both ends: 10,912 and 11,116 of 64 calls, 44,288 and 44,728 of 256 |
| The endpoint mapper's records of the calls it served | a start and a stop each, none carrying an activity id |
| Other ends found through ALPC, under RPC peers | none: no send on any call's thread, 320 of 320 |

1. **RPC over TCP adds no volume.** A call's request and answer are its TCP connection's sends and receives - one each,
   and a bind's on each connection - and every byte is theirs; the call records state none. Over TCP as over ALPC, RPC
   layered over its transport adds no volume (I11, P4).
2. **A call's protocol says its transport.** 1 is a call over TCP and 3 a local one; any other value is unmeasured.
3. **A server's records over TCP carry no activity id.** Each of the endpoint mapper's starts and stops is a call of its
   own that pairs with nothing, so a call served over TCP reads as two unpaired records. Each start was followed on its
   thread by its stop, on as many as seven worker threads eight at once, but a stop names no interface or procedure, so
   a pair made so could not be checked, and a server answering on another thread would join two calls' records (P8).
4. **No ALPC message carries a call over TCP**, so ADR-034's chain finds no other end for one, as it should. The TCP
   connection beneath joins the client to the endpoint mapper's host, but no RPC record names its connection.

Decision: a reader names a call's protocol - TCP or LRPC, any other by its number - and says of a call over TCP whose
other end is unresolved that it went over TCP, which ALPC does not carry (`contracts/operations-v1.md` §4, §5c). Pairing
and the peer rule are unchanged, and the server records left unpaired are stated as such (§6). RPC stays at
`ExperimentalEvidence`: the gaps above are still the source's. RPC over named pipes or HTTP, and a call to another host,
remain unmeasured.
