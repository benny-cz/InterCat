# InterCat session store v1

Status: **the commit protocol, manifests, the current-generation pointer, recovery, the derived segments and
dictionaries a generation publishes, evidence leases and the retention of a dependency, a journal prefix or, since
revision 434, an interval with the identity evidence and the operations its retained rows rest on, journal
re-derivation, the removal of superseded manifests, and the publication of an index, are implemented and tested; a
rolling retention policy is not**. The segment and dictionary formats are frozen separately
in `contracts/segment-v1.md`; this contract owns how a generation publishes them.

This contract freezes the first IC-016 boundary: how a generation is published, what a manifest says,
what a reader acquires, and what recovery does with a publication that was interrupted. It owns nothing
inside `journal-v1`, which §18.1 freezes; it owns what is built on top of it.

## 1. The session root

A session lives in one owned directory. The privileged broker's validated root and an ordinary
unprivileged directory are the same interface: a single allow-listed name per file, no reparse point, no
composed path. The commit protocol therefore does not know or care which one it is writing into, which
is what lets a live capture and an offline import share it.

Names are ASCII letters, digits, `.`, `-` and `_`, at most 64 characters, no leading `.` or `-`, no
`..`, never a reserved Windows device stem.

## 2. Files

| Name | What it is |
|---|---|
| `manifest-<generation:D10>.json` | One immutable generation. Fixed width, so the names sort as the numbers do. |
| `current-generation.json` | The mutable pointer a reader acquires. |
| `previous-generation.json` | The retained last-known-good pointer. |
| `session-publication.lock` | Persistent root-wide writer serialization guard. |
| `session-evidence-lease.lock` | Persistent shared-reader/exclusive-deletion guard. |
| `recovery-pointer-<32 hex>.json` | Preserved bytes of a damaged current pointer after confirmed repair. |
| `stg-<32 hex>.tmp` | A file being staged. Unreferenced by construction. |
| `stg-<same 32 hex>.lease` | The staging owner's OS-held marker; never a published dependency. |
| anything else | A published dependency, named by the generation that published it. |

## 3. A manifest

```text
SessionManifestV1 = (
    format version, generation, session ID, committed UTC,
    source identity, previous generation,
    committed boundary,
    dependencies: [ (name, kind, length, sha256) ],
    retention?,
    earlier releases?: [ (generation, retention record) ],
    digest)
```

The digest is `sha256:` and 64 lowercase hexadecimal characters over a canonical text covering every
other field, in the dependency order the manifest records. A manifest carries its own digest **and** the
length and digest of every file it depends on. That is what makes it possible to tell a dependency that
survived a power failure from a name that was renamed into place and lost its contents: on Windows there
is no directory flush, so a rename proves nothing about the bytes behind the name.

A dependency kind is `Journal`, `Segment`, `Dictionary`, `Index` (code 4, `contracts/derivation-checkpoint-v1.md` since
revision 162), `DerivationPlan` (code 5,
`contracts/normalizer-plan-v1.md`), `CoverageLedger` (code 6, `contracts/coverage-v2.md`),
`CaptureFinalization` (code 7, `contracts/capture-finalization-v1.md`), `RedactionPolicy` (code 8,
`contracts/redacted-session-v1.md`), `Content` (code 9, `contracts/content-v1.md`, revision 234), `ClockCalibration`
(code 10, `contracts/clock-calibration-v1.md`, revision 255) or `CollectorIdentities` (code 11,
`contracts/collector-identities-v1.md`, revision 414). An unknown kind, an unreadable format version, a
generation outside `1..9,999,999,999`, a previous generation that is not earlier, a duplicate dependency, or a
dependency name that is not an owned file name are each refused — the manifest is not read at a guessed layout.

A session's size on disk is the sum of the lengths its current manifest records: every file the generation names,
measured before it was named, never estimated from a figure per record. Since revision 393 `icat session` (its `size`
in `--json`) and the window state it with the records the generation holds, the bytes each takes, and §12.1's tier the
size is in (S5).

A `RedactionPolicy` dependency, `redaction-policy-<generation:D10>.json`, is published only by a redacted session
package, at most once per generation. It is that package's provenance: it says the journal is synthetic and what was
pseudonymized. Retention refuses to release it, and every later generation carries it, so a package never comes to read
as an original capture. A reader built before code 8 refuses a package's manifest rather than reading it as one.

## 4. The committed boundary

Every generation carries the boundary of ADR-010: which journal it derives from, how many bytes and
records of that journal were durable when it was published, and that prefix's digest. A generation
therefore names the admitted evidence behind it rather than implying the whole file. A generation that
declares no boundary is distinct from one that declares zero.

## 5. The commit sequence

1. The caller makes its admitted journal durable and states the boundary.
2. Each file is staged under a unique `stg-` name, written, and **completed**: flushed to the device,
   then read back to measure its length and digest. A staged file that was not completed is never
   published, because its contents are not durable.
3. The staged files are renamed onto their published names. Until the manifest exists none of them is
   referenced, so an interruption here leaves unreferenced files rather than a generation missing a
   dependency. A name an earlier generation already published is refused: a published file is immutable.
4. The manifest is built, and **every dependency is read back and re-measured before it is named**. A
   generation that cannot verify a dependency is not published at all.
5. The manifest is written through a staging name and a replacing rename. The existing pointer is copied
   to `previous-generation.json`, and the new pointer is written the same way.

**What re-measuring does (ADR-025).**

- Every dependency's presence and length are checked each time. A file this store instance already hashed is
  confirmed by one listing of the directory: listed, not a reparse point, and with the length and last-write time it
  was measured at. Any other file, and every file of a directory that cannot list its files, is opened and checked
  from its handle (revision 129).
- Its bytes are hashed unless the same store instance already hashed that file, under the same name, length and
  digest, and the file's last-write time has not moved since. A published file is immutable, so any write sends it
  back to be hashed.
- Opening a session creates a fresh instance, which hashes everything once.

Without this rule a commit hashed the whole session twice. On a 120-chunk, 742 MiB recording, a commit took 1.95 s
and a writer's lease 0.97 s; with it, they take 148 ms and 38 ms. Corruption that leaves a file's length and time
alone is found by the next fresh instance, and by the checksums a reader meets when it reads.

Publication and retention take an exclusive root-owned `session-publication.lock` file handle. Under that
lock, a writer re-reads and verifies the on-disk current pointer and manifest against the generation it
opened; a stale writer, a pointer that needs last-known-good rollback, an existing dependency target or
an existing target manifest is refused before any immutable name is replaced. The lock is a persistent
control file, not evidence or an orphan. It serializes independently opened writer processes as well as
threads within one store instance. A reader does not need that lock.

A normal additive generation includes its predecessor's dependencies, except an index (revision 162, below). A
journal re-derivation is the exception: it carries every journal - one, or a live recording's chunks - and the
content kept beside them, with the retained descriptor plan, the
capture coverage ledger if published, the capture-finalization marker if present, and a redaction policy if present
(a redacted package itself is refused before replay, because it has no plan); it stages a replacement set of
segments and dictionaries and publishes it as the next generation. Carrying the earlier segments would count the same capture twice. The earlier manifest and
dependencies remain last-known-good; publication still follows the same staged-file and pointer sequence. The
replacement is refused if the source generation changed during replay. The replay itself refuses two cases:

- journals that are not one capture's chunks in order (§7), because another capture's rows would be derived as this
  capture's;
- a generation whose rows derive from records its journals no longer hold (§8), because rows no replay can rebuild
  would be dropped without a word.

**An index publication** (`CommitIndex`, revision 162) publishes an index of the current generation as the next
generation. It carries every dependency but the earlier indexes and keeps the committed boundary. It adds only the
staged `Index` files, such as a derivation checkpoint (`contracts/derivation-checkpoint-v1.md`). An earlier index
describes the files of an earlier generation, so it is not carried. Like a re-derivation, it names the generation it
was derived from, and it is refused when a writer has published since. A re-derivation carries no index, because its
segments are new. Neither does an additive generation. An index is then named only by the generation that published
it, so a damaged one costs a rollback to the generation before, and never fails a live writer's next commit, which
re-measures everything it carries. A dropped index stays with the last-known-good generation and is then an ordinary
orphan (§6).

Retiring a dependency otherwise is retention (§8).

## 6. Acquiring and recovering

Opening a session reads the pointer, then verifies, in order: the pointer's own shape, that the manifest
it names exists and parses, that the manifest's contents match its own digest, that the manifest's digest
matches the one the pointer recorded, that the generation numbers agree, and that every dependency is
present with the recorded length and digest.

- All of that passes → that generation is current.
- Any of it fails → the retained last-known-good pointer is verified the same way, and a success is
  reported as a rollback with the reason the newer one failed.
- Both fail and either pointer exists → the session is refused. A store with a pointer that names
  nothing usable is not silently reset.
- Neither pointer exists → the session is empty, which is not a failure.

Since revision 314 a reason names the file that failed and what is wrong with it in a person's words:
"'seg-0000000001-0000.icats' does not match what generation 2 records (its SHA-256 begins d678662e9244, not
2d7f44f53556)", "(it is 2,831 bytes, not 2,832)", or "'…', which generation 2 records, is missing". A digest is given by
its first twelve digits, which tell two apart; the whole of the recorded one is in the manifest. The two generations
share most of their files, so a refusal states a file both need once: "The generation kept to fall back to needs the
same file." A last-known-good that fails for another reason gives that reason too, and a refusal ends by saying the
session's files are left as they are.

**A viewer opens without hashing what checks itself.** Hashing every dependency makes opening cost every byte of
the session, which §12.1's S1 forbids at scale: a million-row session is 309 MiB, and hashing it took 210 ms of a
fresh open. So a viewer opens a session the same way with one difference. A `Segment`, `Dictionary`, `Journal`,
`Index` or `Content` dependency present with its recorded length is taken from one directory listing, without being
hashed:

- a segment reader checks every byte it interprets against the segment's own checksums (`segment-v1` §9);
- a dictionary's decoder checks its own digest;
- a journal's frames and records carry their own checksums (`journal-v1`);
- a content chunk's header and every fragment carry theirs (`content-v1` §2), revision 234;
- an index is read whole and hashed against its recorded digest before a byte of it is interpreted
  (`derivation-checkpoint-v1` §4). Revision 162 added it: a checkpoint can be tens of megabytes for a session of many
  connections, and hashing it at open would read it twice.

Every other kind is small and carries no checksum of its own, so it is hashed at open as before. After its first
view, the viewer hashes each file it listed. A file whose digest disagrees is reported and forgotten, so the next
lease measures it again and fails its generation over to the last-known-good, exactly as opening would have. A
store that publishes, and every command-line reader, still hashes everything at open.

Opening **reports and keeps** every unreferenced file, including `stg-` files. A second process may have
completed a staged file and not yet committed it, so opening cannot infer that a staging name was
abandoned. Ordinary orphans are not removed automatically either: one may belong to a generation whose
publication was interrupted. `RemoveOrphans` removes non-staging orphans only when asked, under the
publication lock and after a fresh-pointer check. It skips both staging data and ownership markers.

A writer creates `stg-<id>.lease` before the matching `.tmp` and holds it without sharing until the
staged file is committed or abandoned. Completing the data file closes its content stream but does not
release ownership. A disposed staged file cannot later publish. After successful pointer publication,
the owner marker is closed and removed; an interruption can leave it for cleanup.

`icat staging <directory>` previews three distinct states without changing the root: marker-owned files
whose handle is no longer held (eligible abandoned staging), active locked owners (kept), and unmarked
legacy staging (kept because abandonment cannot be proved). A marker with no `.tmp` may be left by an
interruption after a file was renamed to its published name; only that marker is eligible, never the
published file. The preview returns a digest of eligible names and a copyable confirmation command.
`--confirm --expect-set <digest>` rechecks under the publication lock and removes only reviewed entries
whose marker can still be held; a changed candidate set is refused and newly abandoned entries are not
silently added. Ordinary open, orphan removal and pointer recovery never delete staging.

A session is never opened as another session: a manifest naming a different session ID is refused.

`icat recover <directory>` previews a rollback without writing: the verified manifest digest, the files no
verified generation names, and a copyable confirmation command (`confirmCommand` in `--json`). Only
`--confirm --expect-manifest <digest>` repairs a damaged or missing current pointer, under the exclusive
publication lock, after re-verifying that last-known-good still names the generation the user reviewed. A
confirmation whose digest no longer names that generation changes nothing and exits 2. If the damaged pointer exists, at most 1 MiB of its exact
bytes is copied and flushed to a unique `recovery-pointer-` file before current is replaced. A larger
pointer is refused until preserved manually; no source bytes are overwritten in that case. The repaired
pointer is re-read and its complete generation verified before success is reported. No manifest, dependency
or staging file is removed. A healthy current pointer makes repair a no-op.

An interrupted publication may already have used the next generation's immutable names. After rollback,
the next writer skips every generation number already present in a non-staging owned file name, rather than
overwriting an orphan or getting permanently stuck on that number. The new manifest names the actual
earlier verified generation as its predecessor; a gap is allowed. The abandoned generation remains
inspectable as an orphan until a separate explicit cleanup. If a file appears with the chosen generation
number after staging began, publication refuses the stale staged generation rather than silently giving
its already-named files a different manifest number.

## 7. What a derived generation publishes

A generation that derives data from an admitted journal publishes all of it at once, in the order §20.1's
sequence requires, under names that carry the generation:

| Name | Kind |
|---|---|
| `journal-<generation:D10>.icatj` | `Journal` — the admitted evidence, written and flushed first |
| `normalizer-plan-<generation:D10>.json` | `DerivationPlan` — the retained compiled interpretation of admitted descriptors |
| `coverage-<generation:D10>.json` | `CoverageLedger` — delivered, omitted, undecodable and reported-loss facts about the capture, not reconstructible from its admitted journal |
| `capture-finalization-<generation:D10>.json` | `CaptureFinalization` — last-publication and stop-milestone evidence, present only after a live capture stops |
| `content-<generation:D10>.icatc` | `Content` — the message content a capture kept of this generation's journal records, restricted evidence beside the journal, present only when it kept any (`contracts/content-v1.md`) |
| `dict-<generation:D10>-<dictionaryId:D4>.icatd` | `Dictionary` |
| `seg-<generation:D10>-<ordinal:D4>.icats` | `Segment` |

A segment references a dictionary by id, and the id is in the dictionary's file name, so a reader resolves a
segment's dictionaries from the manifest without a side index. A segment that references a dictionary the
generation does not name is refused rather than read with its codes shown as values.

The journal's pending batch is flushed to the device before any segment derived from it is staged, so a
generation can never reference evidence that was not durable when it was derived. The committed boundary of
§4 then names that journal, its durable length and record count, and its digest.

`icat rederive <directory>` replays the published journal up to that boundary with its retained
`normalizer-plan-v1` dependency and replaces the derived segments and dictionaries in a new generation that carries
every journal, the plan, the coverage ledger when present and the capture-finalization marker unchanged. A live recording's journal is a sequence of chunks, replayed
in name order, which is the order they were recorded (ADR-023). The replay validates each chunk's length, digest,
source clock, schema/policy table, every record and terminal frame. It checks that every chunk names one capture and
one clock, and that within each stream and epoch every chunk's ordinals pass those of the chunks before it, so no
record is replayed twice or out of order. It also checks that the boundary names the newest chunk and that the boundary
chunk's replayed records equal the boundary's count. A legacy session without a saved plan is refused rather than
guessed from current schemas, and so is a generation holding rows derived from records a retention released (§8). A
replay that fails any check publishes nothing.

A live recording (`icat record`, ADR-021, ADR-022) publishes into a session that had no generation. With a publication
interval it publishes as it records: each publication completes a **journal chunk**, a complete and immutable
`journal-v1` file of the capture, and publishes a generation that carries every earlier chunk and segment and adds the
new chunk with the segments derived from it. The normalizer plan is published with the first generation and carried
after it. The committed boundary names the newest chunk; record ordinals continue from one chunk to the next, and a
row's journal index counts its record across the capture's chunks in order, so a row derived while recording and the
same row re-derived agree. The coverage ledger, when its loss counters are readable, is published with the last
generation. Independently, `capture-finalization-v1` is always staged for a started capture's last generation after
the owned session stop returns; it is what lets restart recovery distinguish that last chunk from a complete
intermediate chunk. A recording interrupted between publications leaves the chunks already published and staging files for the rest.
Journal-prefix retention releases a recording's oldest chunks whole (§8).

A privileged recording can publish an **evidence session** instead (ADR-027). It holds the same journal chunks, plan,
finalization marker and optional ledger, with no rows and no segments, so nothing privileged derives, reads back or compacts. An ordinary process
follows it into a session directory of its own:

- each committed chunk is copied byte for byte, its length and digest checked against the evidence manifest;
- its rows are derived there, with the capture-wide journal index an in-process recording uses;
- the plan comes with the first chunk; the finalization marker and any coverage ledger come with the last.

The derived session takes the evidence session's identity and is an ordinary session. A follower resumes from what the
derived session holds. It refuses evidence whose chunks are not the ones it mirrored, a session that already has its
rows, and an evidence session that has published nothing.

The formats themselves are `contracts/segment-v1.md`. This contract does not read inside them: to it a
segment is a named file with a length and a digest, which is what lets a future format arrive without
changing the commit sequence.

## 8. Leases and retention

### A lease

A reader acquires the current generation **and every dependency it names** as one lease, which is the unit
§20.1's fifth step describes and the thing I18 makes inviolable: while a lease is live, nothing this store
does removes a file the lease holds.

A lease has a kind and an expiry. An `Interactive` lease is short and reserves nothing. A `Pinned` lease
declares the disk allowance it reserves, because §10.1 forbids promising an indefinite pin inside a circular
quota without one; a pin that reserves less than the evidence it would pin is refused. An expired lease holds
nothing and is not revivable — a hold that can come back from expiry is not a bound on anything.

Cross-process readers hold a shared read handle on the root-owned `session-evidence-lease.lock` before
verifying the pointer and its dependencies. When the pointer still names the generation the store instance already
verified, that manifest is reused rather than read and digested again; its dependencies are still re-measured. The session writer creates this guard before publishing a new
generation. A reader needs only read access to it; a legacy session without the guard can be upgraded by a
writable reader, but a read-only viewer must refuse that legacy session until its owner has established the
guard. A reader refreshes a stale store's snapshot from the verified pointer, but that refresh never makes a
stale *writer* eligible to publish: publication keeps its own baseline until that store is reopened.

Retention and explicit orphan removal take the exclusive guard under the publication lock before deleting
anything. If any reader in any process holds the shared guard, physical removal is deferred and the released
files remain as reported orphans. This is intentionally conservative: one reader may defer deletion of an
unrelated file, but no reader loses a dependency. Disposal, expiry, or process exit releases the OS handle;
expiry has a timer so an idle client does not hold cleanup indefinitely. A lease is not a durable promise
across process restart, and the guard is not a cross-process quota reservation for pins.

A lease on a session with no published generation is refused. An empty session is not a generation with no
data. Since revision 310 a reader establishes a missing guard only in a folder that holds a session pointer, current or
last-known-good: in one that holds none - not a session's folder, or a capture's before its first publication - it is
refused, and writes nothing there. The refusal says so in a person's words, the same from every reader. Since revision 312
the store raises it as `NoSessionException`, derived from the `InvalidOperationException` it raised before, so whatever
handles a reader's refusals handles it. A command line answers it as an invalid invocation, the code for naming a folder
that does not exist, never as a capability failure or a partial answer (§20.4).

A lease carries the **manifest** of the generation it holds, and a reader reads that manifest rather than the
store's current one. A commit that lands between acquiring a lease and asking the store for its current
generation would otherwise hand the reader a newer generation than the one its lease protects — a reader
holding generation 1 and reading generation 2's dependency list reads files its lease does not hold (I16,
I18). A lease never moves to a later generation, and neither does what it names.

### Retention

Retention publishes a new generation that no longer names what it released, together with a **retention
record** saying what went and why:

```text
RetentionRecord = (kind, released UTC, reason, released files, released bytes, released records, source digest)
```

The reason is required. A release with no stated reason is indistinguishable from data loss. The kind is
`DerivedFiles` — segments, dictionaries or indices, all rebuildable from the journal — `JournalPrefix`,
which ADR-010 makes the explicit action it is, `Content` (revision 306): every content chunk at once, which nothing
can rebuild (`contracts/content-v1.md` §2), or `Interval` (revision 434): a session's oldest interval, its journal units
with their rows and the derived files that held them (below). A `JournalPrefix`, `Content` or `Interval` record names the
digest of what it released, which is gone afterwards; a `DerivedFiles` record lists its files instead. A reader that
does not implement a kind refuses the record.

The record is appended to the manifest's canonical digest text **only when it is present**, so a generation
published before retention existed verifies with exactly the digest it always had.

**A release outlives the generation that made it** (revision 315). Every later generation carries the latest release of
each extent nothing can rebuild - a `JournalPrefix`, a `Content` and, since revision 434, an `Interval` release - with
the generation that published it, as
`earlierReleases: [ { generation, record } ]`. A generation that releases one itself carries no earlier one of that kind,
whose own record is then the latest, so a manifest carries at most one of each. A `DerivedFiles` release is not carried:
what it released can be rebuilt. Readers state a carried release as they state a generation's own: a record's content
says when and why its content went (`contracts/content-v1.md` §2), re-derivation is refused as the journal release itself
refused it - the rows of the released records are still held - and `icat session` names the generation that released
each. The list is written only when it holds a release, and appended to the canonical digest text then alone, each
entry as `earlier|<generation>|<record>`, so a manifest that carries none is the file, and the digest, it always was. A
list that is empty, that names a generation not earlier than its own or out of order, that carries a `DerivedFiles`
release, or two of one kind, or one of the kind its own record releases, is refused.

Releasing and removing are separate steps. The generation stops naming a file immediately, which is what
makes the retention visible; the bytes go when the last reader that acquired them lets go. A file a live
lease still holds is reported as awaiting release rather than removed behind the reader.

A retention that released anything re-aims the retained last-known-good pointer at the retention generation
itself. The earlier generation is missing a file, now or as soon as a lease lets go, and a pointer that named
it would promise a rollback that cannot be performed.

**A retention that releases a segment releases every index too**, in the same record (revision 162). An index
describes the segments of the generation it was derived from, and §20.2 removes stale index references atomically
with the new manifest. A compaction is such a retention. A release of journal chunks or of a journal prefix keeps
every segment, so it keeps the indexes.

### Releasing a journal prefix

A journal is append-only and its published file is immutable, so a release does not truncate it: it publishes
a new journal holding the retained suffix — same capture, same clock frame, same schema table, then the
retained batches and a terminal frame — names the released extent in the retention record, and lets the old
file go.

**A batch is the unit of release.** A frame's checksum covers its records, so releasing part of a batch would
mean publishing a frame whose digest describes records it no longer holds. A boundary that falls inside a
batch releases nothing and says so, naming the boundaries the journal does allow; a journal written as a
single batch has none, which is a fact about how it was written rather than a refusal. The granularity is the
journal's batch size, which a capture or an import declares.

A release that would leave no admitted evidence at all is refused. ADR-010 keeps a journal by default, and a
session with no journal cannot re-derive anything.

**Kept content goes only with its journal chunk** (`contracts/content-v1.md` §2, revision 234). A content chunk is
evidence, not a derived file, so releasing it by name is refused. Rewriting one journal's prefix is refused while any
content is kept, since it would leave content whose records the retained journal no longer holds.

**Kept content can be released on its own** (revision 306): every content chunk at once, by a `Content` retention that
keeps every journal, row and derived file. It is refused while the capture has not finished, since a recorder still
writing would find the session changed beneath it, and when the generation keeps no content. Afterwards a journal's
prefix may be rewritten as for any session without content.

**A live recording is released a chunk at a time** (ADR-024). A boundary counts records in stored order across the
chunks the generation names, and releases each chunk that ends at or before it. Only a leading run of chunks is
released, never the chunk the boundary names. Nothing is rewritten: the retention generation stops naming the
released chunks and keeps its committed boundary. Rewriting part of a chunk would publish it under the new
generation's name, which sorts after every chunk and would put its records out of order. A released chunk's kept
content, the content chunk of its generation, is released with it. The retention record lists every released chunk,
oldest first, then their content chunks. Its source digest is the digest of their dependency lines,
`name|length|digest` joined by a line feed, exactly as the superseded manifest held them.

**Released records cannot be re-derived, and neither can their rows be re-derived away.** A release keeps every
derived row, including the released records' rows. A re-derivation replaces every row with rows derived from the
retained journals, so after a release it is refused:

- on the retention generation itself, whose record names the release, before any evidence is read;
- on any later generation, before anything is published, when a stream's rows go back further than its retained
  records do.

Carrying those rows across a replacement is future work.

### Compacting derived files

A compaction (§20.1, ADR-026) coalesces a session's small publications into bounded segments.

- **A publication unit** is the derived files one generation published: its observation and field segments, and its
  dictionaries. A segment resolves its dictionaries through its own generation, so a unit's dictionaries serve only
  its segments.
- **A unit is small** while its rows and its observation bytes are both below the output targets, 64,000 rows and
  8 MiB.
- **Runs of two or more consecutive small units are rewritten** into the new generation's segments, run by run, so no
  output spans a unit it did not coalesce.
- **Every row moves unchanged**, with its locator, journal index and values, so every observation keeps its identity.

The generation releases the replaced files with a `DerivedFiles` retention record whose reason says their rows were
coalesced and kept. The record also lists any index, which described the replaced segments. It carries the journals,
the plan, any coverage ledger, the capture-finalization marker and the boundary unchanged; only segments and
dictionaries can be replaced. A file a reader's lease holds stays until that reader lets go.

A live recording compacts itself. When 64 small publications have accumulated, the writer coalesces the oldest run
between chunks, at most one segment's worth of rows. When the capture stops, every run left is coalesced.
`icat compact` does the same for any session.

### Releasing an interval

An interval release (revision 434, ADR-043) gives up a session's oldest interval: records **and** their rows, which a
journal release keeps. It is asked for as a session time, and works in the journal's units.

- **The unit and the boundary.** A release before an instant gives up the longest leading run of units - a recording's
  chunks, or a single journal's batches - each of whose rows reads before it; a row with no session time holds no unit
  back. The newest unit holding records is kept, and with it the chunk the committed boundary names. The release states
  its own **boundary**, one nanosecond after the latest row it gave up: every record read at or after it is retained.
- **Which rows go.** Each row goes with its record, which its record's number places in a unit: a journal not stored in
  the order its records were numbered is refused. Rows whose records a journal release gave up earlier are older than
  every unit, and go first.
- **Which rows stay.** Every row of a retained record, and the **identity evidence** of what those rows name, kept
  unchanged with its source fields: every lifecycle record of each PID a staying row belongs to, or its first record when
  no lifecycle record names it, and the same of each parent its instances link to; and, at each connection end a staying
  row names and its mirror, every connect, accept and disconnect, each incarnation's first record, and the first record
  of each owner, bound instance and strength no staying row has. Since revision 435 an operation open across the
  boundary keeps its records too: every record of an RPC call or ambiguous run one of whose records stays, what a
  completed client call's other end is read from - the ALPC sends on its thread in its window, the receives of its one
  send's message id before it stopped and the first server call the receiving thread began after it - every client
  call that reached a server call that stays, every buffer of an HTTP exchange one of whose buffers stays, and the use
  before a use of a number opened by repeating one of its buffers. A kept row stays like a retained one, so this repeats
  until nothing more is kept. Every row that stays binds, pairs and keys, and belongs to the call or exchange, as it did
  before (`entities-v1` §3-§4, `relations-v1` §3-§5b, `operations-v1` §3-§5c, `http-exchanges-v1` §2).
- **Files.** Each publication holding a row that goes is replaced by one holding its other rows; a publication holding
  none is carried, and every index goes with a replaced segment. Released chunks go with their content; a single journal
  is replaced by the batches it keeps, as a journal prefix is, and is refused while content is kept beside it.
- **Record.** `Interval`: every file released - journal units first, then their content, the derived files replaced
  and the indexes - their bytes, the journal records given up, the digest of the files' dependency lines as a chunk
  release digests its chunks, and `interval: { boundaryNanoseconds, releasedRows, keptRows }`, which only this kind
  states. The interval is appended to the canonical text as `|interval|<boundary>|<released rows>|<kept rows>`.
- **Afterwards.** Before the boundary a generation holds only the kept rows and records delivered with later ones, so an
  interval before it is released, not quiet. Since revision 436 its reader places the boundary on the coverage ledger,
  and coverage over any scope reaching before it is no better than a partial gap, saying why (`coverage-v2` §4); a
  session's overview says from when it keeps every record, and when, how many and why the records before went.
  Re-derivation is refused while kept rows remain: their records are gone.
- **From the command line** (revision 437). `icat retain <session> --release-before <moment> [--confirm --reason <text>]
  [--output <path>] [--overwrite] [--json]` places the moment as `icat evidence --from` does - a time of day on the wall
  clock the capture's machine read, or session time with its unit - and measures what a release before it gives up and
  keeps: the units, records and rows that go, the rows kept as evidence, the boundary it achieves and the derived files
  it rewrites. It performs the release only with `--confirm` and a stated reason, and only once no recorder can still
  be writing the session: its capture finished, it is a redacted package, or its coverage read only files. A capture that
  stopped without finishing cannot be told from one still recording, and a release published beneath a recorder fails
  the recorder's next publication (ADR-024). Its document, `release-interval` under `store-v1`, carries the moment as
  typed and where it fell, the preview, the result and the notes.

The release is planned under a lease, which is released before publication, so the files it replaces go at once unless
another reader holds them (I18). A generation that changed meanwhile refuses the publication.

### What a reader sees afterwards

A retention generation is a generation like any other: it reopens, its manifest verifies against its own
digest, and every dependency it names is re-measured. The generation it superseded becomes unreferenced, and its
manifest is removed as §9's superseded manifests are.

## 9. What the sweep treats as referenced

Opening a session reports every file no generation needs. Both lock guards and both pointers count, and so does the manifest and
dependency list of **each** generation a pointer names — including the retained last-known-good. A sweep that
treated the last-known-good as unreferenced would let `RemoveOrphans` delete the one thing a rollback needs,
and the next torn pointer would turn a recoverable interruption into a refused session.

**Superseded manifests.** A writer that publishes a generation, by commit or by retention, then removes the
manifests of generations that were current once and that no pointer names any more. Each lists every dependency
its generation named, so a live session keeping one per publication would hold manifest bytes growing with the
square of its length: 16 MB after the default 10-minute capture.

- **Which ones.** A generation was current once exactly when a later manifest names it as its previous generation.
  The writer follows that chain back from the current generation, and only the chain's manifests go. A manifest an
  interrupted publication left was never current, and no chain reaches it whatever its number, so it stays an orphan
  for recovery to judge. Nothing else is removed this way: no dependency, pointer, lock or staging file.
- **When.** Only while the writer can take the evidence guard exclusively, which no reader anywhere holds, and only
  for manifests no lease of the writer's names. Otherwise a later publication removes them all at once. Removal is
  cleanup: a failure is ignored, and the publication already succeeded.
- **Readers.** A writer holds the guard exclusively only for the removal, for milliseconds. A reader acquiring a
  lease meanwhile waits up to one second for it instead of failing, and reports the guard only after that.

A session written before this rule keeps its superseded manifests until its next publication walks the chain.

## 10. Not yet implemented

- The entity-state checkpoint of §20.2 is kept as evidence since revision 434: an interval release keeps the lifecycle
  and first records of every process and connection a retained row names, rather than a summary of them (ADR-043).
  Thread and resource identities are not derived, so none are kept.
- Open-operation censoring at a retention boundary (I20) is not needed while an interval release keeps an operation open
  across its boundary whole (revision 435); an operation whose start the capture itself never saw is stated as such.
- Rolling retention by time or size. Retention here is an explicit action on a named extent; the policy that
  decides when to take it is separate work, and must run inside the recorder, whose next publication a release
  published beneath it fails. S5's disclosure exists for the policy captures have, which stops at its
  limits: since revision 393 a recording says when its length, its journal or the disk's reserve stops it. A rolling
  policy will owe the point at which it begins evicting in the same place.
- Binding to the broker's validated root for live capture. The interface is shared; the composition
  belongs with the live runtime.
