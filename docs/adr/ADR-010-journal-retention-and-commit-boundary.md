# ADR-010: What a session keeps of its admitted journal, and how a generation says so

- Status: accepted for M1
- Date: 2026-09-22
- Decision owners: InterCat maintainers
- Relates to: ADR-008 (authoritative journal), §12 budgets, §18.1 (`journal-v1`), §20.1 (commit protocol), §20.2 (retention checkpoints)

## Context

§20.1 states that a journal's lifetime is a decision it owes and that IC-016 makes it, records it here,
and publishes the retention boundary it implies. Neither behaviour may be arrived at by default.

The two options are not symmetric, and the numbers are measured rather than assumed. `journal-v1` is
append-only and carries no dictionary: the IC-011 series measured **231 bytes per admitted record**
against the **128 bytes per normalized observation** §12's retention arithmetic assumes. So:

- **Keep every batch for the life of a session.** Storage rises by roughly 1.8× on top of the derived
  store — nearly triple a session that would otherwise be segments alone.
- **Discard a batch once its segment is durably published.** Storage stays near the derived size, and
  the ability to rebuild that segment from admitted evidence after a normalizer revision is gone.

The second cost is not a storage cost. ADR-008 made the admitted journal InterCat's authoritative live
source; §18.4 lets a normalizer upgrade produce a new derivation generation with links back to raw
record identities. A session that discarded its journal cannot produce that new generation. It can show
what the old normalizer concluded and cannot show what the evidence says.

## Decision

**A session retains its admitted journal by default. Releasing any part of it is an explicit retention
action that publishes the boundary it released, and a generation states the boundary it derives from.**

Three parts:

1. **Every published generation carries a committed boundary**: the journal it derives from, how many
   bytes and records of that journal were durable when the generation was published, and that prefix's
   digest. A generation therefore names the admitted evidence behind it rather than implying the whole
   file. `CommittedBoundary.None` is a generation that declares none, which is not the same as a
   generation that declares zero.

2. **Nothing in the commit protocol deletes a journal.** The protocol publishes immutable files, writes
   a manifest that references only verified dependencies, and replaces the pointer. Removing evidence is
   not one of its steps. Unreferenced files are reported on open and removed only when a caller asks;
   the single exception is a staging file, which is unreferenced by construction.

3. **A release is a retention checkpoint, not a cleanup.** When §20.2's retention arrives, releasing
   journal bytes publishes a checkpoint stating the released extent, so a later reader can tell exactly
   which records can no longer be rebuilt. A release that cannot publish its boundary does not happen.

## Why this way round

The default that loses information should be the one a user chooses, not the one they get. A session
that silently dropped its journal would look identical to one that kept it until the day someone needed
to re-derive a fact, and then the difference would be unrecoverable and undocumented.

The measured cost of keeping it is a storage multiple, which is visible, bounded and budgetable. The
cost of discarding it is an unbounded loss of the ability to answer a later question from evidence,
which is the property ADR-008 was accepted for.

This also keeps the two decisions separable. A deployment that genuinely cannot afford 1.8× can release
journal extents through a retention checkpoint and will have a published record of what it gave up.

## What this costs, stated plainly

- A session's storage is roughly 1.8× the derived store's, plus the store itself, until a retention
  checkpoint releases part of it.
- §12's retention arithmetic must count the journal at 231 bytes per admitted record, which it now does.
- Retention checkpoints and evidence leases (I18) are not implemented. Until they are, a session keeps
  its whole journal and there is no supported way to release part of it. That is the safe half of this
  decision, and it is the half that exists.

## Consequences if this is revisited

Changing the default to discard-on-publish requires: a retention checkpoint that publishes every
released extent, a reader that reports a fact it cannot re-derive as such rather than as absent, and a
new ADR with the measurement that motivated the change. A silent change would make two sessions with the
same identity answer the same question differently, which is the failure this decision exists to prevent.
