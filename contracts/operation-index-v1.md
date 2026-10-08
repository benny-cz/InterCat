# InterCat operation index v1

Status: **implemented** in plan revision 440; minor 1 in revision 441. A generation can name one operation index: its
RPC calls under `rpc-call-operation-v1` (`contracts/operations-v1.md` §3–§5) and their other ends under
`rpc-call-peer-v1` (§5c), and since minor 1 its HTTP exchanges under `http-exchange-v1`
(`contracts/http-exchanges-v1.md`), as derived from named segments. A reader that finds one covering the segments its generation names reads the calls and
exchanges from it, binds them to the generation's process instances, and gets exactly what pairing every call record
and grouping every buffer give. Without one, a reopened session's first call ranking, RPC listing or brush pairs every
call record of every segment first, because a call's request and its response can lie in different segments, and its
first HTTP listing groups every buffer, so the first operation view of a finished session takes longer as the session
grows. That breaks §12.1 S3 (P25). This contract gives the format, what it covers, when a
reader may use it, and who publishes it.

An operation index is a derived index. It holds no evidence and changes no observation (R1). It can always be rebuilt
from the segments it covers (R20). A missing, stale or unreadable index costs time and never changes an answer.

## 1. What an index holds

For the covered observation and source-field segments, what the two derivations made of them:

- **Calls.** How many call records were read, and how many RPC records are no call record (operations-v1 §2); the
  interfaces the calls' starts named, in the order the derivation first read them; the **origins** of the calls' first
  records - each distinct raw stream, epoch and fact key, which a capture's call records share a handful of - in the
  order the calls first name them; and every call: its first record's origin and raw ordinal, the PID its records
  belong to, its side and state, and of its start and its stop, those it has, the reading and the segment position and
  row. A start also holds the interface it named, and the procedure and protocol it carried as source fields; a stop
  holds its status. A completed call with a procedure and a status takes 71 bytes.
- **Other ends.** How many ALPC sends and receives were read and, when there were any, each call's state under
  `rpc-call-peer-v1` and the call at its other end.
- **Exchanges** (minor 1). How many HTTP records named no exchange; the origins of the exchanges' first buffers - each
  distinct raw stream and epoch; and every exchange: its number, the PID its buffers belong to, its first buffer's origin
  and raw ordinal, its first and last readings, the session times of its first and last buffers and of the buffer that
  ended its response, those it has, what was recorded of each of its four parts - buffers, bytes, and whether whole -
  and where each of its buffers is, by segment position and row, in the order the grouping took them.
- **Identity.** The session, the generation the calls were derived at, the clock and host, the covered files, and the
  rule identities: the pairing rule, the peer rule, and the `process-binding-v4` rule whose record owners give each
  call's PID (entities-v1 §2a).

Neither a call's nor an exchange's process binding is held. A reader binds each as the derivation does, by its PID and
its first record's reading, to the instances it reads the index with, and groups and orders them from those bindings.
The index therefore depends on no instance numbering, and each is bound exactly as a derivation with the same instances
binds it.

The calls are written in the canonical order of their first records (operations-v1 §3): native reading, then raw
locator, then fact key; the exchanges in the order of their first buffers: reading, then raw stream, epoch and ordinal.
The other ends name calls by their place in that order. The bytes are therefore the same whatever order the derivations
grouped them in, and a derivation read back writes the same bytes again (I14).

## 2. Files and publication

The dependency kind is `Index` (code 4 in `contracts/store-v1.md`), so a reader built before this contract accepts a
generation that names one and ignores it. The file name is:

```text
operation-index-<publishing generation:D10>.bin
```

A generation names at most one operation index. It is published with the derivation checkpoint and the persisted
overview, in the generation that publishes them (`contracts/derivation-checkpoint-v1.md` §2), by the same writers:
an import, the end of a live follow, a finished recording, a compaction, a re-derivation, and `icat checkpoint`. Later
publications treat it as the checkpoint is treated: an additive generation does not carry it, and a compaction or a
retention that releases a segment releases it with the segments, in the same retention record. A generation that
names a checkpoint and an overview but no operation index, as every one published before revision 440 does, is not
current: its writer's next publication, or `icat checkpoint`, publishes all three again.

An index is written even for a generation with no RPC or HTTP record: it then holds no call or exchange, and a
reopen's first call ranking or HTTP listing reads that rather than every segment's records. Calls that would take more
than 1 GiB are not kept, and exchanges that would not fit in what is left are not either; the index says which (§3),
and a reader derives what it does not keep from the segments, as without an index. An index of minor 0, as revision
440 wrote, keeps no exchanges: it is read for its calls, and a writer that finds one publishes the generation's indexes
again, as for a generation naming none.

## 3. Bytes

Little-endian throughout. `str8` is an unsigned byte count followed by that many UTF-8 bytes. `guid` is 16 bytes in
.NET's `Guid` byte order. An index is at most 1 GiB.

```text
index       = header covered held [calls peers] http       ; http from minor 1: a minor-0 index ends at its peers
header      = "ICATOPIX" (8 ASCII bytes), major u16 = 1, minor u16 = 1,
              operationRule str8 ("rpc-call-operation-v1"), peerRule str8 ("rpc-call-peer-v1"),
              bindingRule str8 ("process-binding-v4"),
              session guid, derivedGeneration i64 (>= 1),
              clock guid, host guid
covered     = count u32, file*                      ; observation segments first, in the generation's order; then
                                                    ; source-field segments, in the generation's order; each name once
file        = role u8 (1 observations, 2 source-fields-v1), name str8, length i64 (>= 0), digest str8
held        = u8 (1 when calls and peers follow; 0 when the calls would take more than the index may hold)
calls       = callRecords i64 (>= 0), otherRecords i64 (>= 0),
              count u32, interface guid*            ; each once
              count u32, origin*                    ; each once, in the order the calls first name them
              count u32, call*                      ; in the canonical order of their first records
origin      = stream u32, epoch u32, factHigh u64, factLow u64
call        = origin i32 (an origin's place), ordinal u64,   ; the first record's raw locator and fact key
              pid i32, side u8 (1 client, 2 server), state u8 (EN-RpcCallState, 1..5),
              flags u8 (1 start, 2 stop, 4 procedure, 8 protocol, 16 status),
              [start: interface i32 (-1 for none), ticks i64, segment i32, row i32],
              [stop: ticks i64, segment i32, row i32],
              [procedure i64], [protocol i64], [status i64]
peers       = alpcSends i64 (>= 0), alpcReceives i64 (>= 0),
              (state u8 (EN-RpcPeerState), other i32 (-1 for none))*   ; one per call, in the calls' order,
                                                                       ; only when sends and receives are not both 0
http        = groupingRule str8 ("http-exchange-v1"), held u8 (1 when exchanges follow), [exchanges]
exchanges   = withoutExchange i64 (>= 0),
              count u32, (stream u32, epoch u32)*   ; origins, each once, in the order the exchanges first name them
              count u32, exchange*                  ; in the order of their first buffers
exchange    = number i64, pid i32, origin i32, ordinal u64,         ; the first buffer's origin and ordinal
              firstTicks i64, lastTicks i64 (>= firstTicks),
              times u8 (1 first, 2 last, 4 response end), [firstNs i64], [lastNs i64], [responseEndNs i64],
              part * 4,                             ; request head, request body, response head, response body
              count u32 (> 0, the parts' buffers together), (segment i32, row i32)*
part        = buffers i32 (>= 0), bytes i64 (>= 0), whole u8 (0, 1; 1 only with buffers)
```

A segment is named by its position among the covered observation segments, which is the position the calls were
paired and the exchanges grouped from. With no ALPC record every call's other end is unresolved for that (`NoAlpcEvidence`), and nothing per call
is written.

## 4. Reading

A reader refuses an index whose bytes do not hash to the digest its generation records, before interpreting any of them
(store-v1 §6). It then refuses the index when:

- the magic is wrong, the major version is not 1, or the minor is above 1;
- a rule identity is not this build's, the exchanges' among them: an index derived under another rule describes other
  calls, other ends, other exchanges or other owners (§24), and is derived again;
- its session, clock or host is not the generation's, or it names no generation;
- a count exceeds what the remaining bytes can hold, a string is not valid UTF-8, a code is outside its set, a covered
  file is not a named, measured segment, a name is covered twice, an observation segment follows a source-field one, or
  bytes remain after the last field;
- the calls contradict the pairing rule:
  - a completed call lacks its start or its stop, or another state holds both or neither;
  - a call open at capture end has no start, or a call whose start was not observed has one;
  - a stop carries a procedure or a protocol, or a start a status;
  - an interface or a segment is not one the index holds, or a row is negative;
  - a stop is read before its start;
  - a call is out of the canonical order of its first record;
  - the calls hold another number of records than the index counts;
  - an interface or an origin is named twice, or a call names an origin the index does not hold;
- the other ends contradict the peer rule:
  - a state is undefined, or says no ALPC record was read while the index counts some;
  - a call names a call the index does not hold;
  - a served call does not name a call of the other side that names it back;
  - a client call is said not to be reached, or names a call without being served;
  - a client call's other end says it was not completed, and it was, or it was not, and the state says something else;
  - a server call is neither served by a client call that names it, nor not reached with no call named;
- the exchanges contradict the grouping:
  - an origin is named twice, or an exchange names one the index does not hold;
  - an exchange ends before it begins, or holds an undefined time;
  - a part holds fewer than no buffers or bytes, or is whole with no buffer;
  - an exchange holds no buffer, or another number than its parts, or one in no segment the index holds;
  - an exchange is out of the order of its first buffer;
- a call's or an exchange's PID is not one of the instances it is read with, which are then not the instances of the
  segments it covers.

A refused index is not used. The calls are paired from the segments, and the overview says why in one caveat once
they have been asked for, since a slower first RPC view should be explainable. The answer is the same.

A reader uses an index for a generation only when its covered files are exactly the generation's, file for file and
in the generation's order, with the same name, length and digest, and the segments a query hands over are those, in
that order: the calls name segments by position. An index covering other files is stale and is not used, silently;
§2 keeps writers from publishing one. The calls are bound to the instances already derived for the generation, or to
the checkpoint's when it covers the same segments, so a reopen reads no segment for them.

## 5. What is not defined at this version

- Extending the calls or exchanges from one live generation to the next: a live generation names no index, and its
  first RPC or HTTP view derives them from every record, once per generation (operations-v1 §8).
- An index that covers a prefix of a generation's segments, as the checkpoint can: a call still open in the covered
  segments may close in a later one, so the calls would be paired again from the first record anyway.
