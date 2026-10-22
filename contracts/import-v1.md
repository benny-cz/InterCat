# InterCat canonical import v1

Status: **source identity, import identity, canonical record keys, equal-time multiplicity, the
bounded external-sort path, standalone ETL import into a published session, reuse of the folder that
already holds an import, and the catalogue of imports that finds one in another folder implemented and
measured**.

This contract freezes the first IC-013 boundary: how a source's bytes become an identity, how one
record becomes a canonical key, what ordering an import may claim, and what it must refuse. It is a
portable contract. Nothing here needs elevation, opens a privileged handle or decodes a payload; an
import runs at ordinary integrity by construction, which is what R16 requires of every parser.

## 1. Source identity

A source identity is computed from the bytes before anything is read from them:

```text
ImportSourceIdentity = (kind, length, sha256 of the content)
kind = JournalV1 | StandaloneEtl
```

Rendered as `<kind>:<length>:sha256:<64 lowercase hex>`. The digest covers the bytes only, so the same
file presented under two kinds has one digest and two identities. A stream overload hashes without
holding the source in memory, so a multi-gigabyte ETL is identified the same way a small journal is.

## 2. Import identity

```text
ImportIdentity = sha256(
    "InterCat.Import.Identity.v1",
    import contract version,
    source kind, source length, source content digest,
    retained-evidence policy,
    normalizer contract version)
```

The retained-evidence policy and both contract versions are inside the identity, so importing metadata
only and importing approved content are different derived sessions over the same bytes (§18.4). An
import that finds a matching completed identity may reuse it; an import that differs in any of these
inputs may not, and gets a different identity instead of quietly reusing an existing one. The capture
identity of an import's records, which is the identity of the session it publishes, is derived from this
digest, so a session of that identity holds this import (§8).

## 3. Identity basis

The kind decides what gives an imported record its persistent identity:

| Kind | Basis | Why |
|---|---|---|
| `JournalV1` | `PreservedRawRecordId` | The journal assigned stream, epoch and ordinal at acquisition, before parallel decode. They are reproducible, and replay preserves them. |
| `StandaloneEtl` | `CanonicalKeyWithOccurrence` | `ProcessTrace` callback order is not reproducible: equal-time records from different processors can arrive in any order. |

A journal import therefore keeps the source's own order and its record identities. A standalone ETL is
keyed, ordered canonically and counted.

## 3.1 Clock identity for imported evidence

A journal names its own clock. Evidence recorded outside InterCat carries readings but none of our
identities, and both identities it needs are derived from the evidence rather than minted or borrowed:

```text
clock ID = uuid-v8("InterCat.Import.DerivedClock.v1|<source identity>|<encoding>|<ticks per second>")
host ID  = uuid-v8("InterCat.Import.DerivedHost.v1|<source identity>")
```

Minting a fresh clock ID per import would be worse than untidy: the clock ID is inside the canonical
record key, so two imports of one file would not even agree on their records' identities. Claiming the
reading machine's host would be worse still - it would let imported readings be compared against local
captures as though they shared a clock.

The rate is the recording machine's fact, so it comes from the file. The adapter library does not
expose the recorded performance-counter frequency, so an import derives it from the file's own native
readings and their relative times, rounds it to whole ticks per second, and then **checks** it: every
sampled reading must reproduce the file's own relative time under the derived rate, within one
microsecond. A file whose readings span too little time to derive a rate, or that does not reproduce
under the derived one, is refused rather than read at an assumed rate. On the measured 25H2 build this
derives exactly 10,000,000 ticks per second and every sampled reading reproduces.

## 4. Canonical record key

```text
CanonicalRecordKey = sha256(
    "InterCat.Import.CanonicalRecordKey.v1",
    source identity,
    clock ID, timestamp encoding, native ticks,
    provider, event ID, version, channel, level, opcode, task, keyword, flags, event property,
    process ID, thread ID, activity ID, related activity ID,
    processor number, logger ID, pointer size,
    extended items (count, then each item's type, flags, original length and retained bytes),
    omitted extended-item count,
    body classification, disposition, original length and retained bytes)
```

Every variable-length field is length-prefixed, so no concatenation of a shorter body and a longer item
can hash as another record's fields. The key is 32 bytes and comparable, which makes a sort entry fixed
width.

What is deliberately **not** in the key:

- the delivery ordinal, and anything else about the run that read the file. For a standalone ETL these
  are not reproducible, and keying them would make two imports of the same bytes disagree;
- callback-local pointer values and consumer context, which belong to the reader rather than the record;
- file-local schema and policy reference numbers, which are table positions rather than identities. The
  table's own digest covers what those references mean.

The buffer context **is** in the key. Which processor produced a record and which logger carried it are
recorded content: two otherwise identical records from different processors are two records, not one
record seen twice.

## 5. Ordering and multiplicity

The canonical order is native ticks, then canonical key, then stream, epoch and source ordinal. The last
three make the order total so a run is repeatable; they are not a claim that one of two indistinguishable
records came first.

Records that share an instant and a canonical key are indistinguishable by everything the evidence
carries. They are preserved, not deduplicated: each gets an occurrence index `0..n-1` and every one of
them carries the multiplicity `n`. The occurrence index enumerates them; it is not an order, and a UI
must not present it as one. Each entry keeps the source ordinal it was read with as import metadata:
which ordinal is paired with which occurrence is a property of the run, not a reproducibility guarantee.
Repeating an import of the same bytes with the same reader yields the identical index; reading them in
another delivery order yields the same keys, the same instants and the same multiplicities.

## 6. Refusals

An import refuses rather than continuing in part:

- a record naming a clock or timestamp encoding the source does not describe. Reading it would mean
  interpreting a native reading against an assumed clock, which I8 forbids;
- a record whose schema or admission-policy reference the source's tables do not describe;
- more records than the declared record bound;
- more spilled runs than the declared run bound;
- more indistinguishable repeats of one instant and key than the declared multiplicity bound;
- an options value outside its declared range, refused before the source is read at all.

## 7. Bounded spill and cancellation

Index entries are fixed 64-byte records: canonical key, native ticks, source ordinal, stream, epoch,
occurrence index and multiplicity. An import fills an in-memory run up to its declared entry bound,
sorts it, writes it to a spill file and merges the runs k-way at the end. Occurrence indices and
multiplicity are assigned during that merge, one group of indistinguishable records at a time, which is
why the group size is bounded.

Cancellation is honoured per record and per merged entry. Every exit path — completion, refusal or
cancellation — removes every spilled run it created, and the merged index removes its own file when the
result is disposed. An import that spills produces exactly the index it would have produced in memory;
that equality is a test, not an assumption.

## 8. Reading a standalone ETL

`InterCat.Capture.Journal` composes the pieces: it hashes the file, derives and checks its clock,
replays it through the same admission adapter live capture uses, maps each admitted record into a
`journal-v1` envelope and hands that envelope to the import builder, which keeps its 64-byte index entry
and lets the envelope go immediately. Holding the envelopes would undo the bound the importer exists to
keep.

`icat import <source.etl>` runs it. The command needs no elevation, writes machine-readable data to
stdout and progress to stderr, refuses to overwrite a report without `--overwrite`, and refuses
`--retain-content` outright, because no source has the validated payload contract, pre-persistence
scope proof and payload-specific impact evidence that content retention requires. Without `--into` it
writes an import summary, not a session. With `--into <directory>` it publishes the admitted journal, its
retained descriptor plan and derived observation/source-field segments as one committed generation, into a
new folder only. What the folder holds is read, writing nothing there, before a schema is compiled or a
record read; the file is hashed for it only when the folder holds a session to compare it with:

| The folder holds | `icat import` |
|---|---|
| Nothing, or only a session's lock files, which a confirmed cleanup of staged files leaves | Publishes the session, unless the catalogue of imports lists another folder that holds this import (§8.1). |
| This import: the session whose identity the import derives (§2), every file its generation names verifying | Reuses it, reading the file no further and writing nothing into it (exit 0, or 1 where it reads its last-known-good generation), and records it in the catalogue of imports. |
| Another session, or an import of the same path made from other bytes or under another contract | Refused, naming what it was derived from. |
| A session no generation of which verifies | Refused as damaged input (exit 4), naming `icat recover`. |
| Files a publication staged and never published, which no writer holds | Refused, naming `icat staging`, whose confirmed cleanup leaves a folder the import publishes into. |
| Files a writer is staging now | Refused: something is publishing into it. |
| Anything else, or a file at that path | Refused, naming what is there. |

A reuse writes `import-reuse-v1` with `--json`, and to `--output`: the source's path, kind, length and
content digest, the import digest and the evidence it retained, the folder `--into` named (`into`), the
catalogue that listed the folder reused, or null where it is the folder `--into` named (`catalogue`),
and the session reused - its path, session identity, generation, commit time, manifest digest,
observation rows, and why it reads its last-known-good generation, or null. `--rows-per-segment` and
`--journal-batch-records` shape a new publication, so a reuse says it did not apply them, naming
`icat rederive --rows-per-segment` for the segments. `--overwrite` applies only to the report file; it
never lets an import into a folder that holds anything else. The import API also refuses a store with an
existing generation, because appending the same evidence would silently count one capture twice.
Re-derive an existing journal with `icat rederive` rather than re-importing it into that session.

## 8.1 The catalogue of imports

A session's identity is derived from its import's (§2), so the folder an import is told to publish into
is recognised as holding it; an import of the same file into another folder needs to know where the user
put the first. `icat import` keeps a catalogue of completed imports for that: `import-catalogue-v1.json`
in the user's own InterCat folder (`%LOCALAPPDATA%\InterCat` on Windows, beside the window's sessions),
or the file `--catalogue <file>` names. It is a hint, never evidence.

- An import that publishes a session, and every reuse, records one entry: the import digest, the session
  identity, the session's folder as a full path, the source's path and length, and when it was recorded.
  It replaces the entry of the same folder. The newest 1,000 entries are kept; an older import is
  imported again.
- Before an import publishes into a new folder, and unless `--again` is passed, it reads the catalogue.
  The file is hashed only when the catalogue lists an import of a file of the same length. This import's
  entries are tried newest first, and a folder is reused only when it holds this import and every file its
  generation names verifies, exactly as §8 reuses the folder `--into` names. Nothing is written into the
  folder `--into` named, which is not made where it was not there; the report names both folders (§8),
  and `--again` imports the file into the new folder all the same.
- A listed folder that no longer holds the import - removed, emptied, holding another session or other
  files, or the very folder `--into` names, which is new - is forgotten when the catalogue is next
  written. One whose session cannot be read is named in a warning and stays listed, since `icat recover`
  may yet recover it.
- A file that cannot be read as a catalogue of this contract - unreadable, not one, of another contract,
  or larger than 16 MiB - lists nothing and is never replaced: the import says so once and goes on. A
  catalogue that cannot be written is warned about, and the import stands.
- A writer holds `import-catalogue-v1.json.lock` beside it, waiting up to five seconds for another, reads
  the catalogue again and replaces it whole - written beside it and moved over it - so two imports
  recording at once both stay listed, and a reader meets the old catalogue or the new one, never half of
  either.

```json
{
  "contract": "import-catalogue-v1",
  "imports": [
    {
      "importDigest": "sha256:<64 hexadecimal digits>",
      "sessionId": "<32 hexadecimal digits>",
      "path": "C:\\Cases\\trace",
      "sourcePath": "C:\\Traces\\trace.etl",
      "sourceLength": 268435456,
      "recordedUtc": "2026-10-22T09:30:00+00:00"
    }
  ]
}
```

An entry without an import digest, a session identity of 32 hexadecimal digits, a fully qualified
folder, a source path or a length of zero or more is passed over, and dropped when the catalogue is next
written.

## 9. Not yet implemented

- Semantic normalizer upgrades and bookmark fallback to raw evidence when a fact is split or renamed
  (§18.4). Same-version replacement derivation generations exist; a new normalizer contract version
  requires a real mapping change and stable-raw-identity tests rather than an arbitrary version bump.
- Overlap disclosure between a journal and its companion ETL. Both identities exist; the comparison that
  discloses overlap belongs with the store layers of §20.1.
