# ADR-025: A store instance hashes each immutable dependency once

- Status: accepted for M1 and M2
- Date: 2026-09-23
- Decision owners: InterCat maintainers
- Relates to: §12 (ingest budget), §19.3 (live publication cadence), §20.1 (commit sequence, recovery), I15, I18,
  ADR-010, ADR-022, `contracts/store-v1.md`

## Context

`store-v1` re-measures every dependency before a generation names it, and again whenever a reader acquires a
generation. Re-measuring hashed every byte of every file each time. A generation also carries all of its
predecessor's dependencies, so a live recording that publishes a chunk every few seconds hashed its whole session at
every publication. It did so twice: once to check that the current generation had not changed under the writer, and
once more for the new manifest.

A benchmark measured the cost. It published 120 chunks of 20,000 records each through the public store API:

| Chunks | Files | Session | Commit | Writer's lease | Fresh reader's open |
|---:|---:|---:|---:|---:|---:|
| 1 | 3 | 6.2 MiB | 158 ms | 24 ms | 8 ms |
| 30 | 90 | 185.6 MiB | 556 ms | 267 ms | 248 ms |
| 120 | 360 | 742.2 MiB | 1,951 ms | 972 ms | 977 ms |

The commit cost grew with the whole session, not with the chunk being published. At §12's ingest target, a
5-second publication would spend longer hashing than recording within a minute or two. Every CLI read command also
hashed everything twice: once when opening the session and once when taking its lease.

## Decision

1. **Every dependency is still opened and its length checked** on every commit and every acquisition.
2. **Bytes are hashed unless this instance already hashed them.** The instance must have hashed the same file under
   the same name, length and digest, and the file's last-write time must be unchanged since. A published file is
   immutable, so any write moves its time and sends it back to be hashed.
3. **A fresh instance hashes everything once.** Opening a session creates a new instance, whether as a writer or a
   reader. That is where corruption that left a file's length and time alone is found, as are the segment and
   journal checksums a reader meets when it reads.

## Consequences

The same benchmark, after the change:

| Chunks | Files | Session | Commit | Writer's lease | Fresh reader's open |
|---:|---:|---:|---:|---:|---:|
| 1 | 3 | 6.2 MiB | 154 ms | 17 ms | 8 ms |
| 30 | 90 | 185.6 MiB | 100 ms | 12 ms | 263 ms |
| 120 | 360 | 742.2 MiB | 148 ms | 38 ms | 942 ms |

- A commit now costs about the chunk it publishes plus one open per file. A writer's repeated leases cost the opens.
  A CLI command hashes the session once instead of twice.
- A reader that follows a recording should keep one store instance and acquire leases from it. Opening afresh each
  time still hashes the whole session, correctly but at the old cost.
- One open per file per commit still grows with the session, and so does the number of segments a reader opens.
  §20.1's compaction targets remain necessary.
- Corruption that leaves a file's length and last-write time unchanged goes unnoticed by the instance that already
  measured the file, until a fresh instance opens the session. §20.1 already disclaims tamper resistance ("Checksums
  detect corruption; they are not a claim of hostile-tamper resistance"). Durability is unaffected: a file is read
  back when first published, and a crash ends the instance that measured it.

## Alternatives considered

- **Trust carried dependencies without opening them.** Rejected. One open and a length check per file is cheap, and
  it still catches a file that was removed, truncated or rewritten.
- **Store last-write times in the manifest.** Rejected. A last-write time is not evidence, and copying a session
  changes it, so a copy would read as corrupt.
- **Hash in the background under a budget.** Deferred. It needs a scheduler that the broker binding will provide.
