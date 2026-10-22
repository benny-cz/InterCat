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
format 1.0 or 1.1 holds IPv4 ends only and is still read (§4). Format 1.3 (plan revision 573) says which record made
each cut - a connect, an accept or a disconnect - so the relations say how each connection opened (relations-v1 §3); an
earlier format said only whether a cut closed an incarnation, and one holding a cut that opened one is derived again
(§4). Format 1.4 (plan revision 736) adds, after every other section, what each incarnation's records sent and
received (relations-v1 §5b), so a connection's summary over the whole session reads no segment. A checkpoint of an
earlier format is read without them, its connections' summaries reading their rows, and a writer that finds one
replaces it (§4). Format 1.5 (plan revision 738) keeps, after the transfers, every record by the owner it binds to -
each incarnation's and each instance's (relations-v1 §5, entities-v1 §4a) - so a generation opened from the
checkpoint ranks by bytes and by peers without reading its rows. An earlier format is read without them, and replaced
the same way.

Since plan revision 710 a live follow names a checkpoint in every generation it publishes after its first: the state of
the generation that one extends, which a reader of the new generation extends over the chunk it adds (§2, §4's prefix).
Opening a session while it is still being followed then reads only its newest chunk's rows to derive (§12.1 S1).

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
    - the first and last reading, and the first canonical position;
    - since format 1.4, its sends, the bytes they state and how many state none, the same of its receives, and
      whether either sum passed what one holds;
    - since format 1.5, its records by the owner each binds to and how strongly, or none, with the transport bytes
      they measured, and the PIDs they name when they name more than one.

  It also holds how many related records of each mechanism name no end. Pairing and channel numbers are recomputed
  from these (relations-v1 §3a, §5a).
- **Activity** (format 1.1). How many records name no owner; for each PID any record names, the earliest and latest
  reading of those records and how many of them bind to no instance; and for each instance and mechanism, its
  lifecycle records, which bind directly, and its other records, which bind as strongly as the instance's place among
  its PID's instances allows (entities-v1 §4). What an evidence policy admits of each instance is a function of these,
  and the PID spans are what an extension checks (§4). Since format 1.5, every record by the instance it binds to and
  how strongly, or none, with the transport bytes it measured (entities-v1 §4a).
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

The exception is the live writer's own (plan revision 710). A live follow - the window's, `icat capture`'s and
`icat follow`'s - stages into each generation it publishes, beside the chunk that generation adds, a checkpoint of the
generation it extends: written afresh, named for the generation that publishes it, and covering every segment the
generation before named, so the new generation's reader extends it over the added chunk alone (§4's prefix). It is
made from the follow's own derivation of the generation before, extended chunk by chunk as the window's is, and in the
window it is that derivation. A follow's first generation extends nothing and names none; nor does the compaction that
coalesces a follow's small chunks, which replaces the segments any checkpoint described; nor a recording's own writer
(`icat record`, the broker), since deriving would put analysis on the writer that holds the capture, nor the finish of
an interrupted follow, which publishes a checkpoint of everything once it is done. A checkpoint that cannot be made is
not staged, and the generation is published without it.

Later publications treat it as an index over segments:

| Publication | The checkpoint |
|---|---|
| An additive generation, such as a live chunk | not carried: a live follow stages one of the generation before it into the generation (revision 710); another writer's has none until it publishes one |
| A compaction, or a retention that releases a segment | released with the segments, and listed in the retention record (§20.2: no stale index outlives what it describes) |
| A re-derivation | not carried: the replacement names new segments (store-v1 §5) |
| A retention of journal chunks or a journal prefix | carried: every segment stays named |

An additive generation could carry the checkpoint, and a reader would extend it by the added segments (§4). It does
not, so that a checkpoint is normally named only by the generation that published it. A damaged checkpoint then costs a
rollback to the generation before it, as any damaged file does (store-v1 §6). It never also fails every later
generation, or a live writer's next commit, which re-measures everything it carries. A writer publishes a new
checkpoint when it finishes.

A writer publishes a checkpoint, and since revision 163 the persisted overview with it (`contracts/overview-index-v1.md`),
since revision 440 the operation index (`contracts/operation-index-v1.md`) and since revision 456 the tile index
(`contracts/tile-index-v1.md`), when it has finished writing a session:

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
checkpoint  = header covered instances relations activity transfers owners
                                                    ; activity from minor 1: a minor-0 one ends at relations;
                                                    ; transfers from minor 4: an earlier one ends at activity;
                                                    ; owners from minor 5: an earlier one ends at transfers
header      = "ICATDCKP" (8 ASCII bytes), major u16 = 1, minor u16 = 5,
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
cut         = record, kind u8 (0 connect, 1 disconnect, 2 accept)
                                                    ; before 1.3: afterClose u8 (1 disconnect, 0 connect or accept)
incarnation = flags u8 (1 has records, 2 names one PID, 4 names two PIDs, 8 binds to two instances)
              when it has records: [pid i32 when 2], holder i32 (-1 when unbound or 8), weakest u8 (EN-RelationStrength),
              first i64, last i64, firstPosition record, records i64 (> 0), untimed i64 (0..records)
activity    = countRule str8 ("process-activity-v1"), withoutOwner i64 (>= 0),
              count u32, span*,                     ; strictly ascending by pid
              count u32, tally*                     ; strictly ascending by (instance, mechanism)
span        = pid i32, first i64, last i64 (>= first), unbound i64 (>= 0)
tally       = instance i32 (a position among the rebuilt instances), mechanism u16 (EN-Mechanism),
              direct i64 (>= 0), bound i64 (>= 0)   ; not both 0
transfers   = transfer*, count u32                  ; one per incarnation holding records, in the order relations
                                                    ; holds them; count is how many there are
transfer    = refused u8 (0, 1), sends i64, sentBytes i64, unsizedSends i64,
              receives i64, receivedBytes i64, unsizedReceives i64
                                                    ; 0 <= unsized <= its sends or receives; sends + receives
                                                    ; <= the incarnation's records; both bytes 0 when refused
owners      = held* ownedInstances length u32       ; held: one per incarnation holding records, in the order
                                                    ; relations holds them; length: the bytes before it in owners
held        = count u32, pid i32*, byOwner          ; the PIDs ascending, only of an incarnation naming two or
                                                    ; more; byOwner: exactly the incarnation's records
ownedInstances = count u32, (instance i32, byOwner)*, byOwner
                                                    ; strictly ascending by instance, each naming only itself;
                                                    ; the last: the records binding to no instance, naming none
byOwner     = refused u8 (0, 1), count u32, owner*, unownedRecords i64 (>= 0), hasBytes u8 (0, 1), [ownedBytes]
                                                    ; refused: a byte view refuses a record of them
owner       = instance i32, strength u8 (EN-RelationStrength), records i64 (>= 0), ownedBytes
                                                    ; strictly ascending by (instance, strength)
ownedBytes  = refused u8 (0, 1), (total i64, measured i64, unmeasured i64) * 3
                                                    ; sent, received and any other side; none below 0;
                                                    ; the totals 0 when refused
```

The transfers come last and end with their count, so a minor-3 checkpoint is a minor-4 one without them. A writer
whose relations count no transfers, because they were read from an earlier checkpoint, writes minor 3. A transfer is
refused when its sent or received bytes passed what a sum holds: its counts are kept and its bytes are not, and the
connection's summary reads its rows, which say why (metrics-v1).

The records by owner come last and end with how many bytes they take, so a minor-4 checkpoint is a minor-5 one
without them. A writer whose derivations keep no records by owner, because they were read from an earlier
checkpoint, writes minor 4. The records are all but a process's or a thread's lifecycle records; the bytes are every
record's, as a byte view measures them. Everything is written in canonical order - PIDs and owners ascending - so a
derivation made at once and one extended write the same bytes (§1).

An instance is named by its position in the rebuilt instances, which are ordered by PID and lifecycle epoch
(entities-v1 §3), so the counts are as canonical as the instances.

A `record` is a canonical position (entities-v1 §3). Every covered segment shares one capture and one normalizer
derivation, so the header states them once. A covered file's role says which derivation read it: the relations read
observation segments only, and the instances read both.

## 4. Reading

A reader refuses a checkpoint whose bytes do not hash to the digest its generation records. The store does not hash an
`Index` before a viewer's first view, because this reader checks what it interprets (store-v1 §6). It then refuses the
checkpoint when:

- the magic is wrong, the major version is not 1, or the minor is above 5: a checkpoint is rebuildable, so there is no
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
- a checkpoint before format 1.3 holds a cut that opened an incarnation: it cannot say whether a connect or an accept
  opened that connection, which the relations say, so it is derived again, and `icat checkpoint` writes one that says
  it. One whose cuts all closed incarnations holds what 1.3 would, and is read;
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
  - a tally names no rebuilt instance or an undefined mechanism, or a PID's first reading is after its last;
  - a transfer is negative, says more of its sends or receives stated no size than there are, counts more sends and
    receives than its incarnation's records, or states bytes beside a refused sum; or there is not one transfer per
    incarnation holding records;
  - an owner names no instance or an undefined strength, counts fewer records than none, or is out of order; an
    incarnation's records by owner are not its records, or its PIDs contradict whether its records name two; an
    instance's name another instance, or the records binding to none name one; transport bytes are below none or
    stated beside a refused sum; or the records by owner do not take the bytes they say they take.

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
checkpoint. Both give the same result. RPC calls and their other ends extend only from a derivation in memory, by
`contracts/operations-v1.md` §3a, and never from a checkpoint or an operation index.

Relations read from a checkpoint before format 1.4 count no incarnation's transfers, nor do they once extended, since
what the covered segments' records sent is not in it: a connection's summary over the whole session then reads its
rows. §2's publishers and `icat checkpoint` replace such a checkpoint with one that counts them.

Since format 1.5 the checkpoint keeps every record by owner (`relations-v1` §5, `entities-v1` §4a): derivations
read back from it, and extended, rank their generation by bytes and by peers from them. Derivations read back from
an earlier format keep none, nor once extended, and a whole-session byte or peer ranking of their generation reads
its rows; §2's publishers and `icat checkpoint` replace such a checkpoint. Since revision 737 whether a segment was
read is told by the identity its generation records for it, so an extension opens only the segments it adds.

## 5. What is not defined at this version

- The overview's counts, which the persisted overview publishes beside the checkpoint (`contracts/overview-index-v1.md`,
  revision 163). When both cover exactly the segments a generation names, a reopen opens no segment before its first
  view. The checkpoint is then used as it stands, without a segment reader to compare it with: its covered files are
  compared with the manifest's.
- Metric queries (`icat metric`) derive their own instances and do not read a checkpoint.
- The boundary checkpoint of §20.2 (IC-016a). It keeps still-live identities across a retention, which this
  checkpoint does not attempt: a retention that releases a segment releases the checkpoint with it.
