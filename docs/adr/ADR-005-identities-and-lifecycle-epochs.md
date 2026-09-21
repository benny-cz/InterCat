# ADR-005: Identities and lifecycle epochs

- Status: accepted for the IC-007 foundation
- Date: 2026-09-21
- Decision owners: InterCat maintainers
- Relates to: ADR-002 (owned capture), `contracts/identity-v1.md`

## Context

Windows routinely reuses PIDs, handles, object addresses and ports. M0 already observed provider process
sequence numbers, but the domain still exposed random process IDs and observation fact indexes. That
made the prototype usable while leaving the production identity rules undefined.

## Decision

1. Live raw records use capture, stream, source epoch and acquisition ordinal. Normalized observations
   add a non-zero normalizer version and a deterministic 128-bit semantic fact key.
2. Process identity is host- and boot-scoped and requires a provider start key, observed creation time,
   or an explicit provisional inventory witness. PID alone has no identity constructor.
3. Reusable process and resource keys include lifecycle epochs. Exact provider start keys outrank event
   delivery time, so a late event can still bind to an earlier epoch.
4. A PID with several known epochs is unresolved without explicit lifecycle evidence. We prefer a
   visible unknown over a convincing merge.
5. Better evidence appends a versioned alias revision; it never replaces source observations or makes
   an earlier ID unaddressable.
6. Stable entity IDs are UUIDv8 values derived from SHA-256 of versioned, culture-invariant canonical
   forms. Names and executable paths are attributes, never identity inputs.

## Alternatives rejected

- PID plus nearest timestamp: time proximity is not identity and fails on reuse and late delivery.
- Random IDs persisted after normalization: these cannot be reproduced from the same evidence.
- Rename a provisional entity in place: this invalidates bookmarks and earlier snapshots.
- Use names or paths as identity: independent resources and processes routinely share them.

## Consequences and reversal cost

The model is intentionally conservative and can show unresolved records that a heuristic would attach.
Mechanism-specific correlators may later add documented evidence rules, but cannot weaken the key. The
M0 `{rawRecordId,factIndex}` reader remains for existing evidence; removing it requires an evidence
migration. Changing canonical bytes or hash construction requires identity-v2 and bookmark migration,
not a silent code change.
