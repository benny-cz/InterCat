# InterCat session store v1

Status: **the commit protocol, manifests, the current-generation pointer, recovery, and the derived segments
and dictionaries a generation publishes are implemented and tested; retention checkpoints and evidence leases
are not**. The segment and dictionary formats are frozen separately in `contracts/segment-v1.md`; this
contract owns how a generation publishes them.

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
| `current-generation.json` | The pointer a reader acquires. The one mutable file in a session. |
| `previous-generation.json` | The retained last-known-good pointer. |
| `stg-<32 hex>.tmp` | A file being staged. Unreferenced by construction. |
| anything else | A published dependency, named by the generation that published it. |

## 3. A manifest

```text
SessionManifestV1 = (
    format version, generation, session ID, committed UTC,
    source identity, previous generation,
    committed boundary,
    dependencies: [ (name, kind, length, sha256) ],
    digest)
```

The digest is `sha256:` and 64 lowercase hexadecimal characters over a canonical text covering every
other field, in the dependency order the manifest records. A manifest carries its own digest **and** the
length and digest of every file it depends on. That is what makes it possible to tell a dependency that
survived a power failure from a name that was renamed into place and lost its contents: on Windows there
is no directory flush, so a rename proves nothing about the bytes behind the name.

A dependency kind is `Journal`, `Segment`, `Dictionary` or `Index`. An unknown kind, an unreadable
format version, a generation outside `1..9,999,999,999`, a previous generation that is not earlier, a
duplicate dependency, or a dependency name that is not an owned file name are each refused — the
manifest is not read at a guessed layout.

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

A generation's dependencies include its predecessor's. Retiring one is retention, which §20.2 owns and
this contract does not yet implement.

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

Opening also removes every `stg-` file, because a staging file is unreferenced by construction, and
**reports** every other unreferenced file as an orphan with its size. Orphans are not removed
automatically: a file that is unreferenced now may be a dependency of a generation whose publication was
interrupted, and deleting it would turn a recoverable interruption into lost evidence. `RemoveOrphans`
removes them when a caller asks for that.

A session is never opened as another session: a manifest naming a different session ID is refused.

## 7. What a derived generation publishes

A generation that derives data from an admitted journal publishes all of it at once, in the order §20.1's
sequence requires, under names that carry the generation:

| Name | Kind |
|---|---|
| `journal-<generation:D10>.icatj` | `Journal` — the admitted evidence, written and flushed first |
| `dict-<generation:D10>-<dictionaryId:D4>.icatd` | `Dictionary` |
| `seg-<generation:D10>-<ordinal:D4>.icats` | `Segment` |

A segment references a dictionary by id, and the id is in the dictionary's file name, so a reader resolves a
segment's dictionaries from the manifest without a side index. A segment that references a dictionary the
generation does not name is refused rather than read with its codes shown as values.

The journal's pending batch is flushed to the device before any segment derived from it is staged, so a
generation can never reference evidence that was not durable when it was derived. The committed boundary of
§4 then names that journal, its durable length and record count, and its digest.

The formats themselves are `contracts/segment-v1.md`. This contract does not read inside them: to it a
segment is a named file with a length and a digest, which is what lets a future format arrive without
changing the commit sequence.

## 8. Not yet implemented

- Evidence leases (I18). A reader acquires a generation by reading the current manifest; nothing yet
  pins it against a concurrent retention or compaction, because neither exists.
- Retention checkpoints (§20.2) and the journal release ADR-010 describes.
- Open-operation censoring at a capture or retention boundary (I20).
- Binding to the broker's validated root for live capture. The interface is shared; the composition
  belongs with the live runtime.
