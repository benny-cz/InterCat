# InterCat session store v1

Status: **the commit protocol, manifests, the current-generation pointer, recovery, the derived segments and
dictionaries a generation publishes, evidence leases and the retention of a dependency or a journal prefix
and journal re-derivation, and the removal of superseded manifests, are implemented and tested; the entity-state
checkpoint of §20.2 is not**. The segment and dictionary formats
are frozen separately in `contracts/segment-v1.md`; this contract owns how a generation publishes them.

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
    digest)
```

The digest is `sha256:` and 64 lowercase hexadecimal characters over a canonical text covering every
other field, in the dependency order the manifest records. A manifest carries its own digest **and** the
length and digest of every file it depends on. That is what makes it possible to tell a dependency that
survived a power failure from a name that was renamed into place and lost its contents: on Windows there
is no directory flush, so a rename proves nothing about the bytes behind the name.

A dependency kind is `Journal`, `Segment`, `Dictionary`, `Index`, `DerivationPlan` (code 5,
`contracts/normalizer-plan-v1.md`), `CoverageLedger` (code 6, `contracts/coverage-v1.md`),
`CaptureFinalization` (code 7, `contracts/capture-finalization-v1.md`) or `RedactionPolicy` (code 8,
`contracts/redacted-session-v1.md`). An unknown kind, an unreadable
format version, a generation outside `1..9,999,999,999`, a previous generation that is not earlier, a
duplicate dependency, or a dependency name that is not an owned file name are each refused — the
manifest is not read at a guessed layout.

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

A normal additive generation includes its predecessor's dependencies. A journal re-derivation is the
exception: it carries every journal - one, or a live recording's chunks - with the retained descriptor plan, the
capture coverage ledger if published, the capture-finalization marker if present, and a redaction policy if present
(a redacted package itself is refused before replay, because it has no plan); it stages a replacement set of
segments and dictionaries and publishes it as the next generation. Carrying the earlier segments would count the same capture twice. The earlier manifest and
dependencies remain last-known-good; publication still follows the same staged-file and pointer sequence. The
replacement is refused if the source generation changed during replay. The replay itself refuses two cases:

- journals that are not one capture's chunks in order (§7), because another capture's rows would be derived as this
  capture's;
- a generation whose rows derive from records its journals no longer hold (§8), because rows no replay can rebuild
  would be dropped without a word.

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
data.

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
`DerivedFiles` — segments, dictionaries or indices, all rebuildable from the journal — or `JournalPrefix`,
which ADR-010 makes the explicit action it is.

The record is appended to the manifest's canonical digest text **only when it is present**, so a generation
published before retention existed verifies with exactly the digest it always had.

Releasing and removing are separate steps. The generation stops naming a file immediately, which is what
makes the retention visible; the bytes go when the last reader that acquired them lets go. A file a live
lease still holds is reported as awaiting release rather than removed behind the reader.

A retention that released anything re-aims the retained last-known-good pointer at the retention generation
itself. The earlier generation is missing a file, now or as soon as a lease lets go, and a pointer that named
it would promise a rollback that cannot be performed.

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

**A live recording is released a chunk at a time** (ADR-024). A boundary counts records in stored order across the
chunks the generation names, and releases each chunk that ends at or before it. Only a leading run of chunks is
released, never the chunk the boundary names. Nothing is rewritten: the retention generation stops naming the
released chunks and keeps its committed boundary. Rewriting part of a chunk would publish it under the new
generation's name, which sorts after every chunk and would put its records out of order. The retention record lists
every released chunk, oldest first. Its source digest is the digest of their dependency lines, `name|length|digest`
joined by a line feed, exactly as the superseded manifest held them.

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
coalesced and kept. It carries the journals, the plan, any coverage ledger, the capture-finalization marker and the
boundary unchanged; only segments and dictionaries can be replaced. A file a reader's lease holds stays until that reader lets go.

A live recording compacts itself. When 64 small publications have accumulated, the writer coalesces the oldest run
between chunks, at most one segment's worth of rows. When the capture stops, every run left is coalesced.
`icat compact` does the same for any session.

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

- The entity-state checkpoint of §20.2. A rolling eviction must publish the still-live process, thread and
  resource identities, the known endpoint bindings, the continuity quality and the pending-operation
  summaries it carries across the boundary. Nothing derives those yet — they need the entity and operation
  revisions IC-015 owns — so the retention record deliberately carries what was released and no fields
  nothing can fill.
- Open-operation censoring at a capture or retention boundary (I20). It needs operations.
- Rolling retention by time or size. Retention here is an explicit action on a named extent; the policy that
  decides when to take it, and the disclosure S5 requires before it bites, are separate work.
- Binding to the broker's validated root for live capture. The interface is shared; the composition
  belongs with the live runtime.
