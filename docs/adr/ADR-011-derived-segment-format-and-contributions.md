# ADR-011: The derived segment format, and where a metric contribution is decided

- Status: accepted for M1
- Date: 2026-09-22
- Decision owners: InterCat maintainers
- Relates to: §5 (measurement semantics), §5.3 (metric compatibility), §7.3 (observation schema), §10.2
  (indices), §20.1 (physical storage v0), ADR-010 (committed boundary), `contracts/segment-v1.md`

## Context

§20.1 specifies a physical store — fixed-width columns, null bitmaps, variable chunks, dictionaries, a
raw-record locator, time-block metadata — but the backlog of §14.1 assigned it to nobody: IC-015 was
written as "metric contributions and query basis semantics" and IC-016 as "durable commit/recovery and
boundary checkpoints". The commit protocol shipped treating a dependency as an opaque named file with a
length and a digest, which is exactly what §20.1 asks of it, and left the file formats unowned. This ADR
records the decisions taken while implementing them, and plan revision 26 adds IC-015a to own them.

Three questions had to be answered before any bytes could be written.

**Where does a measurement's meaning live?** §5.3 requires a byte metric to name exactly one byte domain
and one accounting side, and R2 requires every measurement to carry value-or-null, unit, domain, side,
source and quality. A store that keeps only the value and lets a query supply the labels cannot tell an
unknown apart from an inapplicable field, so it cannot produce the denominator §5 requires for
"measured on 63% of eligible contributions".

**What does a row know about identity?** R1 forbids resolved identities in observation columns. But a
derived row is exactly where a resolved process instance would be most convenient to put.

**How is a dictionary bounded?** §10.2 requires explicit dictionary budgets and fallback policies without
saying what the fallback is.

## Decision

### 1. The measurement slot is stored separately from the measurement

A descriptor that exposes a byte field labels its domain, accounting side and unit in every row, whether or
not that record supplied a value. A descriptor that exposes none carries no labels at all and reports
`NotApplicable`. So three states are distinguishable in the columns themselves:

| Value | Labels | Availability | Means |
|---|---|---|---|
| present | present | `Present` | a measurement; zero is an observed zero |
| null | present | a reason | an unknown **in a declared slot** — in the denominator |
| null | null | `NotApplicable` | the descriptor has no such field — in no denominator |

Any other combination is refused by the row's own validation before it can be written. This is the smallest
change that makes R3 checkable rather than aspirational: without it, "null" means both "the source did not
say" and "there was nothing to say", and every availability ratio computed above it is a guess.

The accounting side comes from the descriptor's kind: `Send` → send side, `Receive` → receive side, and
anything else → `EndpointActivity`, which §5.1 defines as counting both sides by design. That last case is
a labelled placeholder, not a resolution: which side owns a total is a correlation decision, and until a
correlator makes one the contribution is endpoint activity and says so.

### 2. A byte sum over a segment is part of the storage layer, not the query layer

`SegmentMeasurement.SumBytes` takes one domain, one side and an optional layer or mechanism projection, and
returns the total together with every contribution it did not take: unknown contributions with their
reasons, contributions in another domain, contributions on another side, rows with no declared slot, and
rows outside the projection. Accumulation is checked.

Putting this here rather than in the query scheduler is deliberate. IC-017 owns *when* a result is computed,
superseded and cached; if it also owned *what* a byte sum means, the semantics of §5.3 would be defined by a
scheduler and would have to be re-derived by every other caller — the CLI, an export, a test. The scheduler
composes something already correct instead.

### 3. A segment carries source facts only, and the layer is a source-contract fact

There is no column for a process instance, a channel, a relation or a derived metric. R1's failure mode is
"evidence rewritten behind a reader", and the way to prevent it is for the write to be impossible, not
forbidden. Resolved identities are separate derivations in their own dependencies, under their own table id.

The observation layer is added to the source catalog's descriptor intent and travels through the compiled
admission plan into the row. It is not derived from the mechanism, because one mechanism can carry evidence
at more than one layer, and guessing would let a transport metric absorb an application annotation — which
is the exact failure I11 names.

### 4. The dictionary budget's fallback is the variable chunk

A text column whose segment has more distinct values than a dictionary holds, or more bytes, is stored as
`(offset, length)` references into the segment's variable chunk instead. The fallback is per column and per
segment, so one segment can hold a dictionary-coded column beside a chunk-encoded one, and a column no row
has a value in is chunk-encoded with an empty chunk rather than given a dictionary that resolves nothing.

This is what makes the budget a bound rather than a ceiling on what can be imported. A refusal would turn a
machine with many distinct pipe names into a machine InterCat cannot derive a session for.

### 5. A segment's bytes are a function of its row set

Rows are sorted by native reading, then by the raw-record locator, then by the fact key; dictionary entries
are sorted by their UTF-8 bytes and unique; the segment id is derived from the capture, clock, derivation,
ordinal, row count and interval rather than minted. Two derivations over the same rows therefore produce
byte-identical segments and dictionaries.

Measured on the 852-record ETL corpus: importing the same file twice into two fresh directories produced
byte-identical segments and dictionaries. The journals differ in one field, the creation time in their
header, which is a fact about the run rather than about the evidence.

**What is not claimed** is that two imports partition the same rows into the same segments. Which rows land
in which segment follows acquisition order, and §18.4 already records that ETL delivery order is not
reproducible. The row set, every row's identity and every segment's internal bytes are reproducible; the
partition is a property of the run.

### 6. An import's capture identity is derived from the import, not minted

Implementing this found a defect in IC-013: the ETL importer called `CaptureId.New()` once per record. The
canonical record key excludes the capture id, so keying was unaffected and no test caught it — but the
capture id is inside `RawRecordId` and therefore inside every observation identity, so a session built from
those records would have had a different capture for every row.

The capture id is now derived from the import identity, which is itself derived from the source bytes. This
is the same rule plan revision 24 applied to an import's clock and host, and for the same reason: an
identity that is inside a record's identity cannot be minted per run without making two imports of one file
disagree about which records they hold.

## Consequences

- `contracts/segment-v1.md` is frozen and its refusals are tested. `observation-v1` has 39 columns; adding,
  removing or renumbering one is a format change needing a new version and an ADR.
- A generation published by an import now names its evidence: the admitted journal is written into the
  session, the segments derive from it, and ADR-010's committed boundary names the durable prefix and its
  digest. `icat import --into <dir>` produces something `icat session <dir>` can open.
- The status-domain enumeration §7.3 names is still owed. A status code is stored with its availability and
  nothing claims which domain it is in; inventing codes for §23 would have been a worse answer than
  recording the gap.
- The layer added to `AdmittedEventIntent` is a required member, so every catalog descriptor states one.
  Kernel network descriptors are transport, kernel process are lifecycle, RPC are application, and the
  kernel-file descriptors the pipe probe uses are resource.
- The kernel-network catalog declares one field set for all six TCP descriptors, so a connect, accept or
  disconnect record carries a `size` of zero in the transport-observed domain accounted to endpoint
  activity. It is an observed zero from a field the source populated, and it is reported rather than
  smoothed away — but what that field means on a non-transfer descriptor is not established by a truth
  workload, and that is a limitation of the catalog rather than of this format.
