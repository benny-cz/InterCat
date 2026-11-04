# InterCat coverage ledger v3

Status: **frozen, and written by a capture that admitted through a gate** (ADR-074). `coverage-v3` is `coverage-v2`
(`contracts/coverage-v2.md`) with one member added: a delivery may count the records its source's gate did not admit.
Everything `coverage-v2` says holds of it unchanged. A capture whose sources admitted through no gate still writes
`coverage-v2`, and a reader takes all three contract names: a `coverage-v2` or `coverage-v1` file is read as one whose
deliveries count no gate's omissions.

## 1. What a delivery adds

```text
Delivery = (providerId, eventId, version, opcode?, delivered, admitted, omission?, omitted, undecodable{reason: records},
            gated{reason: records})
```

- `gated` counts, by `EN-GateOmission`, the records of an admitted descriptor that its source's gate read and did not
  admit:
  - `NotCounted`: of a resource the gate follows, and counting nothing of it, such as one of a call's other records;
  - `NoFollowedResource`: of no resource the gate followed, such as another kind's or one made before the capture;
  - `BeyondBound`: the record that began a resource the gate did not follow because it already held as many as it may.
- `admitted + omitted + Σ undecodable + Σ gated = delivered`, exactly.
- A gate reads one descriptor's records, so a whole-provider delivery counts none.
- Each reason it names counts at least one record, and an absent member counts none.

A reader refuses `gated` in a `coverage-v2` or `coverage-v1` file, on a whole-provider delivery, under a reason this
version does not define, or with a count below one, as well as everything `coverage-v2` §1 refuses.

## 2. Coverage state

A gate's omission is not a loss, and changes no coverage state, as a policy's omission does not. A `BeyondBound` record
is the exception: a resource the gate could not follow is a gap in what it admitted. A mechanism whose collected
descriptors count one is therefore `PartialGap`, saying how many resources its gate did not follow. Otherwise a covered
mechanism's reason says how many of its descriptors' records the gate admitted.

## 3. What it does not do

- It does not say which resource a record was of, nor locate a gate's omissions in time.
- It names no gate. The source a descriptor belongs to has one or none, as its catalog says (ADR-074).
