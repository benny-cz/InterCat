# InterCat entities v1

Status: **implemented** as `process-binding-v2`, with the v1 fallback for older generations that publish no
`source-fields-v1`. Thread, endpoint, resource and channel instances remain undefined (§8); which process is at a
record's other end is `contracts/relations-v1.md`'s, built on the bindings fixed here.

This contract fixes how the process instances of §7.1 are derived from one capture's published `observation-v1`
segments and how every record binds to one of them (§7.3's `EntityBindingRevision`). It owns no bytes: a derivation
is computed from the segments a generation names and changes none of them (R1, R20). The identity inputs it uses are
`contracts/identity-v1.md`'s; how a grouped total uses the bindings is `contracts/metrics-v1.md` §6. ADR-013 records
the decisions.

## 1. Scope

A derivation reads every segment of one generation. They must be of **one capture on one clock**: a PID means
nothing outside the host, boot and timeline it was observed in, and a generation holding several captures is refused
rather than merged. The clock is the one the generation's journal describes.

| Identity input | Where it comes from |
|---|---|
| Host | The clock descriptor's host. |
| Boot | Derived from the clock: one monotonic clock scopes one boot (`BootId.DeriveFromClock`). |
| PID | The owner a record's own payload names (`OwnerProcessId`). The event header's process is never used (§4.1). |
| Lifecycle epoch | Which instance of the PID this is, in reading order, from 1. |
| Evidence | A provider start sequence and reported creation time when available, otherwise an observed creation or the raw-record key of the witnessing record. |

## 2. Lifecycle records

A **lifecycle record** is a row whose mechanism is `ProcessLifecycle` and whose kind is `Create`, `Exit` or
`Inventory` (a capture-state rundown). Its owner is the process it is about. A lifecycle record with no owner
witnesses nothing and is counted.

## 3. Instances

Per PID, lifecycle records are read in canonical order — native reading, then raw locator, then fact key — and each
one does exactly one thing:

| Record | No instance is live | An instance is live |
|---|---|---|
| `Create` | opens a **created** instance at its reading | ends the live one at this reading, flagged `ExitNotWitnessed`, and opens a created instance |
| `Exit` | opens an **exit-only** instance ending at it; flagged `CreationNotWitnessed` when an earlier instance of the PID ended before | ends the live one |
| `Inventory` | opens a **rundown** instance; flagged `CreationNotWitnessed` when an earlier instance ended before | confirms the live one |

A PID that records name and no lifecycle record does has one **activity-only** instance, witnessed by the earliest
record that names it. It is provisional: the capture holds no evidence of its creation, and none is invented.

`process-binding-v2` joins lifecycle records to `source-fields-v1` by raw locator and fact key. A record carrying
a provider start sequence that contradicts the live instance closes it at that reading with `ExitNotWitnessed`
and opens another with `CreationNotWitnessed`, even if no explicit create or exit was captured. Each instance
retains the source's image path, exit basename, session, FILETIME creation/exit claims and named parent PID/key
without converting them into facts from another clock. A parent key identifies a parent instance directly when
that instance is in the capture; without a key, PID and the child's creation reading can make a correlated
parent link. A named but unobserved parent remains named and unlinked, never invented.

An instance's lifetime is half-open. It starts at its creation, or at the end of the PID's previous instance, or is
unbounded below. It ends one tick after its exit — an exit record is its instance's last reading — or at the next
instance's creation, or is unbounded above.

| Witness | Key |
|---|---|
| Any lifecycle instance with a provider start sequence | `FromProviderStart(host, boot, pid, epoch, sequence and reported creation FILETIME)` |
| Created without a provider start sequence | `FromObservedCreation(host, boot, pid, epoch, creation reading)` |
| Rundown, exit-only, activity-only without a provider start sequence | `FromInventoryWitness(host, boot, pid, epoch, witnessing record's raw-record key)` |

Instances are listed by PID and then by epoch. Their identities are functions of the evidence, so they do not depend
on how rows were split into segments or in what order they were delivered.

## 4. Binding

A record binds by its owner and its native reading:

| The record | Binds to | Strength |
|---|---|---|
| A lifecycle record | the exact instance it created, ended or confirmed, joined by its observation identity; same-tick reuse does not move it | `Direct` |
| Any other record inside the PID's **first** instance | that instance | `Correlated` |
| Any other record inside a **later** instance of a reused PID | that instance | `Candidate` |
| No owner in its payload | — | `Unresolved`, `NoOwner` |
| A reading before the PID's first instance | — | `Unresolved`, `BeforeFirstEvidence` |
| A reading between one instance's end and the next one's start | — | `Unresolved`, `BetweenInstances` |
| A reading after the PID's last instance ended | — | `Unresolved`, `AfterExit` |

Inside a later instance, a record could be a late record of the earlier one, and PID and time cannot tell them
apart; inside the first, only that instance or a holder the capture never witnessed could have made it, which is the
risk every PID that was never reused carries. A reading outside every lifetime is never moved to the nearest one
(P6).

## 5. Evidence policy

`EN-EvidencePolicy` decides which strengths a result admits:

| Policy | Admits |
|---|---|
| `DirectOnly` | `Direct` |
| `IncludeCorrelated` (default) | `Direct`, `Correlated` |
| `IncludeCandidates` | `Direct`, `Correlated`, `Candidate` |
| `AllIncludingConflicting` | every strength |

A binding a policy does not admit is reported as unattributed with the reason `NotAdmittedByPolicy`, never dropped.

## 6. Assumptions

For records without a start key the rule assumes the capture lost no lifecycle record that would divide their
lifetime. A lost exit together with the next creation could merge two such instances; a contradictory start key
can still split a witnessed lifecycle. No session publishes a coverage ledger yet, so a result states this as an
assumption rather than a verified condition.

## 7. Identity of a derivation

A derivation is identified by `process-binding-v2` and the generation it was derived from. A change to what binds,
how strongly, or how an instance is keyed is a new rule identity (§24 `entityRevision`); a result names the rule it
was grouped under.

## 8. Not defined at this version

- Persisted entity bindings and alias revisions reconciling a provisional instance with later evidence of its start.
- Thread, endpoint, resource and channel instances, and alias revisions reconciling a provisional instance with later
  evidence of its start.
- Persisting a derivation as a published table. It is computed from the segments on demand; IC-017 owns caching it.
