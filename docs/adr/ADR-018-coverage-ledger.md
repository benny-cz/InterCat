# ADR-018: Coverage is capture evidence, not a derived zero

- Status: accepted for imported sessions; live publication deferred to the owned runtime
- Date: 2026-09-23
- Decision owners: InterCat maintainers
- Relates to: §7.1 (`CoverageInterval`), §10.3, §18.1, §20.1, R21, IC-014, IC-015,
  `contracts/coverage-v1.md`, `contracts/store-v1.md`

## Context

The admitted journal cannot reconstruct what a source delivered but policy omitted, what could not be decoded, or
what the source reported losing before delivery. Without those facts, a query would mistake silence for a measured
zero and a re-derived session could appear more complete than its original capture. A standalone ETL also cannot
prove its recording session enabled a quiet provider, even when the import plan would have admitted its descriptors.

## Decision

1. Publish `coverage-v1` beside admitted evidence as a manifest dependency, not inside a derived segment. It follows
   a re-derivation unchanged and ordinary derived-file retention cannot release it. A generation without one is
   legacy and has unknown coverage.
2. Record one epoch for an import; an owned capture opens an epoch whenever its admission or source configuration
   changes. Each epoch records the plan's admitted descriptors, every delivered descriptor's admitted, omitted and
   undecodable outcomes, and explicit measured loss counters. Outcomes must add up exactly. An unreadable required
   loss counter is not zero and prevents a ledger that claims coverage for that epoch.
3. An imported file's quiet admitted mechanism is `UnknownCoverage`, not `Covered`: the file carries no provider
   enablement proof. A live owned session may report a quiet enabled mechanism as covered only with readable loss
   counters. An uncollected mechanism is `NotCollected`.
4. Reported loss is unlocated in v1. It may affect any collected mechanism over the epoch, so each is `PartialGap`.
   An unknown descriptor version cannot be assigned to an admitted mechanism and has the same conservative effect.
   Policy omission is disclosed but is not loss. Coverage is bounded by the first and last delivered native readings;
   nothing before or after is inferred from an import.
5. Keep coverage separate from a metric's eligibility and value. A rate is an observed rate and is never corrected by
   an estimated missing count. An empty eligible set remains unavailable under `metrics-v1`, even with a covered
   capture; it does not silently become zero.

## Consequences

- The ledger is a small, bounded immutable file with validation and a manifest digest. It is not reproducible from
  the journal, so losing it loses the capture's negative-evidence context.
- Imported sessions can now state the difference between not collected, quiet but uncertain, covered and partial-gap
  mechanisms. The desktop still needs to join this state to its leased snapshot; the CLI can disclose it now.
- Live capture must wire all required measured counters and epoch transitions before publishing this contract. A
  partially wired runtime leaves coverage unknown rather than claiming a healthy capture.
