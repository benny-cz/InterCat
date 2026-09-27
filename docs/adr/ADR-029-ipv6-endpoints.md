# ADR-029: IPv6 endpoints, stored, related and admitted by measurement

- Status: accepted for M1
- Date: 2026-09-27
- Decision owners: InterCat maintainers
- Relates to: §4.3 (source catalog), §7.2 and §7.3 (endpoints and the observation schema), §7.4 (network correlation),
  §11.3 (sharing), §13.1 scenarios 1 and 2, §18.2 and §18.3 (bounded admission against a saved schema), §24
  (`correlationRevision`), R1, R3, R22, P6, P27, ADR-011, ADR-014, ADR-019, ADR-020, `contracts/segment-v1.md`,
  `contracts/relations-v1.md`, `contracts/derivation-checkpoint-v1.md`, `contracts/redacted-session-v1.md`

## Context

The capture admitted the kernel network provider's IPv4 descriptors only: TCPv4 10-15 and UDPv4 42-43, under the
IPv4 keyword. `observation-v1` holds an endpoint address in 32 bits. On Windows many `localhost` services listen on
`::1`, so a whole class of local traffic was invisible. §13.1's first scenario and M2's "known loopback client/server"
both name IPv6 loopback.

Four things stood in the way:

- a row could not hold a 128-bit address;
- a relation end was keyed by 32-bit addresses;
- nothing had measured what the TCPv6 and UDPv6 descriptors name first;
- the admission schema could not reach any IPv6 field. TraceEvent rebuilds a registered provider's manifest from TDH,
  and it writes each 16-byte address as `win:Binary` with neither its length nor its `win:IPv6` out type. A bounded
  read then cannot know the offset of anything after the first address.

## Decision

1. **A table version, written only when used (revision 172).** `observation-v2` is `observation-v1`'s 39 columns and
   then two 16-byte address columns (`segment-v1` §5, table 3, type `Address128`). A segment is written as v2 only when
   one of its rows has an IPv6 address; any other is v1 byte for byte, and stages the same bytes, so a session of IPv4
   records publishes exactly the files it did. A digest measured with the previous build pins that. A dictionary-coded
   form was the alternative the plan named; it would have cost every row a code, and it would have put addresses in a
   dictionary budget meant for names.
2. **Every surface that reads an address reads both families in the same revision.** Redaction maps an IPv6 address
   into the documentation prefix 2001:db8::/32, keeps `::` and `::1`, and gives an IPv4-mapped address its IPv4 part's
   pseudonym. The share report tokenizes an IPv6 endpoint by its address. Text writes RFC 5952's form, bracketed before
   the port. No IPv6 row could leak through a path written only for 32 bits.
3. **Relations key an end by its family and 128-bit addresses (revision 173).** The family is part of the key, so the
   two families never meet. An IPv4-mapped address is kept as the source named it rather than equated with its IPv4
   host, because no capture has shown the source needs that (R22). The rule becomes
   `transport-endpoint-relation-v4`, as relating UDP made v3. A v3 checkpoint is read as v4's where the two are
   provably one state: where it counts no related record without an end, because v3 left every IPv6 record without
   one.
4. **The adapter restores what TDH knows (revision 174).** The manifest keeps TraceEvent's text, and each top-level
   binary field TDH itself reports as fixed-length is given back its `length` and out type before parsing
   (`TdhGetManifestEventInformation`). The schema fingerprint therefore covers them, and a manifest nothing is restored to
   keeps its text and its fingerprint. A binary is read as an address only when the manifest says 16 bytes of
   `win:IPv6` under an IPv6 intent. Anything else stays refused, never guessed (§18.3).
5. **An admitted record carries its addresses whole.** An `Address128` slot copies 16 bytes into one of a record's two
   address slots. The journal's metadata projection gains `IAP2`, written only for a record holding an address, so a
   journal of IPv4 records keeps its bytes. The broker and the retained plan both refuse a plan with a third address
   slot, which would write past the record.
6. **Admission by measurement.** TCPv6 26-31 and UDPv6 58-59 are admitted under both keywords (0x30) because
   FX-TCP-002 and FX-UDP-002 measured them on real ETW over `::1`. Both met every applicable §14.2 criterion at 100%,
   reproduced by a second run. Each descriptor names its endpoints as its IPv4 counterpart does: TCP and a UDP send
   name the owner first, a UDP receive the datagram's sender. Every UDPv6 receive's header PID differed from its payload
   owner, as over IPv4.

## Consequences

- A local IPv6 conversation is a peer, a channel and a graph edge. A recorded IPv6 session's UDP client had its 11,674 B
  attributed to its server, correlated on 16 of 16 records. Its journal replays through `icat rederive --check`.
- The kernel network provider's schema fingerprint changed once, because its manifest now carries the restored
  lengths. Admission fingerprints of its IPv4 descriptors changed with it. No stored plan is reinterpreted: a session
  re-derives through the plan it retained.
- Enabling the IPv6 keyword adds whatever IPv6 traffic a machine carries to a capture. Measured again with both keywords
  (`bench/results/capture-impact-20260927T115356Z`, seven pairs per source), the network source cost a median 0.00 CPU
  pp (observed -1.45), with no throughput regression, loss-free. The IPv4 keyword alone had measured 1.61 pp. The
  difference is within the machine's noise, so the source stays Moderate, the higher of the two runs.
- Link-local zone indices, IPv6 multicast, and two-host IPv6 traffic have no fixture yet. A zone index is not part of
  a TCPv6 or UDPv6 record, so two link-local interfaces' identical addresses are one address to a relation until a
  compartment or interface identity is carried (§7.2).

## Alternatives considered

- **A dictionary-coded address column in `observation-v1`.** Rejected: see decision 1.
- **Map IPv4-mapped IPv6 addresses to IPv4 ends.** Rejected for now: it pairs by an equivalence no capture has shown
  the source to need. A dual-stack socket's IPv4 traffic arrives through the IPv4 descriptors on the measured build.
- **Assume 16 bytes for an unsized binary under an IPv6 intent.** Rejected: that is an offset from a guess, which §18.3
  forbids. TDH states the length, so the adapter reads it.
- **Keep `transport-endpoint-relation-v3`.** Rejected: its own contract makes a change to when an end is left
  unresolved a new rule identity. The equivalence rule keeps existing checkpoints wherever it is provably exact.
