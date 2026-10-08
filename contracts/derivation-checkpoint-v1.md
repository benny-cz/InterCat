# InterCat derivation checkpoint v1

Status: **implemented** in plan revision 162. A generation can name one checkpoint: the state of its
`process-binding-v4` instances (`contracts/entities-v1.md`) and its `transport-endpoint-relation-v4` relations
(`contracts/relations-v1.md`), as derived from named segments. A reader that finds a checkpoint builds both
derivations from it, reads only the segments it does not cover, and gets exactly what a derivation from every segment
gives. Without one, opening a session reads every record's owner, endpoints and canonical position before the first
view, so reopening takes longer as the session grows. That breaks §12.1 S1. This contract gives the format, what it
covers, when a reader may use it, and who publishes it.

Format 1.1 (plan revision 166) adds each instance's own records under `process-activity-v1`, which rank the ranked
table's groups and processes. A checkpoint of format 1.0 holds none. Every one was written under
`process-binding-v2`, which revision 177 accepts only where counts show every record named its owner, so it is
derived again (§4). Format 1.2 (plan revision 173) lets an end be IPv6, its addresses in 16 bytes; a checkpoint of
format 1.0 or 1.1 holds IPv4 ends only and is still read (§4).

A checkpoint is a derived index. It holds no evidence and changes no observation (R1). It can always be rebuilt from
the segments it covers (R20). A missing, stale or unreadable checkpoint costs time and never changes an answer.

## 1. What a checkpoint holds

A checkpoint is the complete state both derivations kept after reading a set of published segments, the **covered
set**. That state is what they need to answer any query and to extend to further segments:

- **Instances.** The lifecycle records read, the process source fields keyed by the record they describe, which fields
  each record carried, the earliest record naming each PID, and how many lifecycle records named no owner. The
  instances are a function of these alone (entities-v1 §3), so a reader rebuilds them rather than trusting a stored
  list.
- **Relations.** Per connection or datagram-flow end:
  - the end's key;
  - the lifecycle cuts dividing it;
  - the canonical position of its latest record;
  - per incarnation:
    - how many records it holds, and how many of them have no session time;
    - the PID they name, or that they name two;
    - the instance its bound records bind to, or that they bind to two;
    - the weakest binding;
    - the first and last reading, and the first canonical position.

  It also holds how many related records of each mechanism name no end. Pairing and channel numbers are recomputed
  from these (relations-v1 §3a, §5a).
- **Activity** (format 1.1). How many records name no owner; for each PID any record names, the earliest and latest
  reading of those records and how many of them bind to no instance; and for each instance and mechanism, its
  lifecycle records, which bind directly, and its other records, which bind as strongly as the instance's place among
  its PID's instances allows (entities-v1 §4). What an evidence policy admits of each instance is a function of these,
  and the PID spans are what an extension checks (§4).
- **Identity.** The session, the generation the state was derived at, the clock and host, the capture and normalizer
  derivation every covered segment belongs to, and the rule identities: binding and relation in the header, and the
  count rule at the start of the activity.

The bytes are the same whatever order the covered segments were read in, and whether the state was derived at once or
extended generation by generation (I14). Everything is written in a canonical order (§3). An ambiguous incarnation
records the first holder it saw, and one naming two PIDs records the first PID. Both depend on reading order and
nothing reads them, so neither is written. A checkpoint is therefore a function of the records it covers.

## 2. Files and publication

The dependency kind is `Index` (code 4 in `contracts/store-v1.md`), so a reader built before this contract accepts a
generation that names one and ignores it. The file name is:

```text
derivation-checkpoint-<publishing generation:D10>.bin
```

A generation names at most one checkpoint. A checkpoint is published in a generation of its own. That generation
carries every dependency of the one it was derived from except an earlier index, keeps the same committed boundary,
and adds nothing else (store-v1 §5, `CommitIndex`). The source generation must still be current when it commits, or
the checkpoint is refused. It is never published while another writer, such as a live capture, may still publish: a
writer that finds the generation it opened superseded is refused (store-v1 §5).

Later publications treat it as an index over segments:

| Publication | The checkpoint |
|---|---|
| An additive generation, such as a live chunk | not carried: the generation has none until its writer publishes one |
| A compaction, or a retention that releases a segment | released with the segments, and listed in the retention record (§20.2: no stale index outlives what it describes) |
| A re-derivation | not carried: the replacement names new segments (store-v1 §5) |
| A retention of journal chunks or a journal prefix | carried: every segment stays named |

An additive generation could carry the checkpoint, and a reader would extend it by the added segments (§4). It does
not, so that a checkpoint is normally named only by the generation that published it. A damaged checkpoint then costs a
rollback to the generation before it, as any damaged file does (store-v1 §6). It never also fails every later
generation, or a live writer's next commit, which re-measures everything it carries. A writer publishes a new
checkpoint when it finishes.

A writer publishes a checkpoint, and since revision 163 the persisted overview with it (`contracts/overview-index-v1.md`)
and since revision 440 the operation index (`contracts/operation-index-v1.md`), when it has finished writing a session:

- an import (`icat import --into`);
- the end of a live follow: the Desktop's, its finish of an interrupted one, `icat follow` and `icat capture`;
- a finished recording (`icat record`);
- a compaction (`icat compact`);
- a re-derivation (`icat rederive`).

`icat checkpoint <session-directory>` publishes one for a session written before any of those did. A package publishes
none of its own. An original-evidence package copies the source generation's files, its checkpoint among them. A
redacted package is verified as exactly its first generation, and its recipient can run `icat checkpoint`. Failing to
publish a checkpoint fails nothing: the session stays as complete and as correct, and opening it derives from its
segments.

## 3. Bytes

Little-endian throughout. `str8` is an unsigned byte count followed by that many UTF-8 bytes. `str32` is a `u32`
byte count, at most 1 MiB, followed by the bytes. `guid` is 16 bytes in .NET's `Guid` byte order. A checkpoint is at
most 512 MiB.

```text
checkpoint  = header covered instances relations activity   ; activity from minor 1: a minor-0 one ends at relations
header      = "ICATDCKP" (8 ASCII bytes), major u16 = 1, minor u16 = 2,
              bindingRule str8, relationRule str8,
              session guid, derivedGeneration i64 (>= 1),
              clock guid, host guid,
              capture guid, normalizer u32          ; both zero when no segment is covered
covered     = count u32, file*                      ; strictly ascending by name, ordinal
file        = role u8 (1 observations, v1 or v2; 2 source-fields-v1), name str8, length i64 (>= 0), digest str8
instances   = withoutOwner i64,
              count u32, lifecycle*,                ; ascending by (pid, record)
              count u32, fields*,                   ; strictly ascending by address
              count u32, seen*,                     ; strictly ascending by (address, field)
              count u32, first*                     ; strictly ascending by pid
record      = ticks i64, stream u32, epoch u32, ordinal u64, factHigh u64, factLow u64
address     = stream u32, epoch u32, ordinal u64, factHigh u64, factLow u64
lifecycle   = pid i32, kind u8 (15 Create, 16 Exit, 17 Inventory), record,
              flags u8 (1 exit code, 2 name), [exitCode i64], [name str32]
fields      = address, present u8 (1 start sequence, 2 create time, 4 parent PID, 8 parent start sequence,
              16 session, 32 exit time; at least one), each present value in that order:
              u64, i64, i32, u64, u32, i64
seen        = address, field u16 (1..6, EN-SourceField)
first       = pid i32, record
relations   = count u32, (mechanism u16 (3 TCP, 4 UDP), records i64 (> 0))*   ; strictly ascending by mechanism
              count u32, end*                                               ; strictly ascending by key
end         = protocol u8 (3, 4), family u8 (4; 6 from minor 2), localAddress ip, localPort u16, remoteAddress ip,
              remotePort u16,
              count u32, cut*,                      ; ascending by position
              hasLast u8 (0, 1), [record]           ; the latest record's position
              incarnation * (cut count + 1)
ip          = u32 for family 4, as every minor writes it; 16 bytes in network order for family 6
cut         = record, afterClose u8 (0, 1)
incarnation = flags u8 (1 has records, 2 names one PID, 4 names two PIDs, 8 binds to two instances)
              when it has records: [pid i32 when 2], holder i32 (-1 when unbound or 8), weakest u8 (EN-RelationStrength),
              first i64, last i64, firstPosition record, records i64 (> 0), untimed i64 (0..records)
activity    = countRule str8 ("process-activity-v1"), withoutOwner i64 (>= 0),
              count u32, span*,                     ; strictly ascending by pid
              count u32, tally*                     ; strictly ascending by (instance, mechanism)
span        = pid i32, first i64, last i64 (>= first), unbound i64 (>= 0)
tally       = instance i32 (a position among the rebuilt instances), mechanism u16 (EN-Mechanism),
              direct i64 (>= 0), bound i64 (>= 0)   ; not both 0
```

An instance is named by its position in the rebuilt instances, which are ordered by PID and lifecycle epoch
(entities-v1 §3), so the counts are as canonical as the instances.

A `record` is a canonical position (entities-v1 §3). Every covered segment shares one capture and one normalizer
derivation, so the header states them once. A covered file's role says which derivation read it: the relations read
observation segments only, and the instances read both.

## 4. Reading

A reader refuses a checkpoint whose bytes do not hash to the digest its generation records. The store does not hash an
`Index` before a viewer's first view, because this reader checks what it interprets (store-v1 §6). It then refuses the
checkpoint when:

- the magic is wrong, the major version is not 1, or the minor is above 2: a checkpoint is rebuildable, so there is no
  partial read of a newer one. A minor-0 checkpoint of this build's rules would be read without counts: a reader
  counts them from every segment, and a writer that finds one replaces it (§2's publishers, `icat checkpoint`), since a
  reopen would otherwise read every segment. Every one revisions 162 to 165 wrote is under `process-binding-v2`,
  which the next item refuses without counts;
- a rule identity is not this build's: a checkpoint derived under another rule describes other instances, relations or
  counts (§24). Two exceptions are exact. A checkpoint of format 1.0 or 1.1 derived under
  `transport-endpoint-relation-v3` is read as `v4`'s when it counts no related record without an end. `v4` differs
  from `v3` only in giving an IPv6 record the end `v3` left it without, so where no record went without one the two
  derivations are one state. One that counts any is refused and derived again. No checkpoint of format 1.2 was written
  under `v3`. A checkpoint of format 1.1 or 1.2 derived under `process-binding-v2` or `process-binding-v3` is read
  as `v4`'s when its counts name no record without an owner. Each differs from `v4` only in binding fewer records
  whose payload names no owner to the process that raised them - `v2` none, `v3` an RPC record, `v4` an RPC or HTTP
  record (entities-v1 §2a) - so where every record named its owner the derivations are one state. One that counts any
  record without an owner is refused and derived again, because the count does not say whether one was such a
  record. A checkpoint of format 1.0 holds no counts and was written only under `v2`, so it is refused;
- a checkpoint before format 1.2 claims an IPv6 end, which it cannot hold;
- its session, clock or host is not the generation's;
- a count exceeds what the remaining bytes can hold, a string is not valid UTF-8, a code is outside its set, an order
  above is broken, or bytes remain after the last end;
- the state contradicts itself:
  - an incarnation's untimed count exceeds its records;
  - its first position is not at its first reading;
  - two PIDs are named with one of them, or without the incarnation being ambiguous;
  - an ambiguous incarnation names a holder;
  - an end's latest position is missing although an incarnation holds records, or present although none does;
  - a holder is not an instance of the rebuilt instances;
  - a tally names no rebuilt instance or an undefined mechanism, or a PID's first reading is after its last.

A refused checkpoint is not used, and the overview says why in one caveat, because a
slower open should be explainable. The answer is the same.

A reader uses a checkpoint for a generation only when every covered file is named by that generation with the same
name, length and digest:

- **Covers exactly.** Every observation and source-field segment the generation names is covered. The derivations are
  the checkpoint's.
- **Covers a prefix.** The generation names further segments, as after later live chunks. The derivations are
  extended by reading only those (I14). An extension that would change a decision already made declines, as it does
  between live generations: a late cut among records already read, or an instance that no longer binds alike. The
  generation is then derived in full. The counts extend when every PID's readings, from its earliest to its latest
  counted record, bind alike under the extended instances: each instance's counts move to the instance its records
  now bind to, and only the added segments are counted. Otherwise they are counted in full.
- **Stale.** A covered file is no longer named, or is named with another length or digest. The checkpoint describes
  other evidence and is not used, silently. §2 keeps writers from publishing one.

A derivation already in memory for an earlier generation of the same session is extended in preference to reading a
checkpoint. Both give the same result.

## 5. What is not defined at this version

- The overview's counts, which the persisted overview publishes beside the checkpoint (`contracts/overview-index-v1.md`,
  revision 163). When both cover exactly the segments a generation names, a reopen opens no segment before its first
  view. The checkpoint is then used as it stands, without a segment reader to compare it with: its covered files are
  compared with the manifest's.
- Metric queries (`icat metric`) derive their own instances and do not read a checkpoint.
- The boundary checkpoint of §20.2 (IC-016a). It keeps still-live identities across a retention, which this
  checkpoint does not attempt: a retention that releases a segment releases the checkpoint with it.
