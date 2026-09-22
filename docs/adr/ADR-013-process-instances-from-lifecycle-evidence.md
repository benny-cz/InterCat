# ADR-013: Process instances from lifecycle evidence, and how a record binds to one

- Status: accepted for M1
- Date: 2026-09-22
- Decision owners: InterCat maintainers
- Relates to: §7.1 (`ProcessInstance`), §7.2 (stable identifiers), §7.3 (`EntityBindingRevision`), §7.4
  (relation strengths), §19.1 (process filters), §21.1 scenario 5, §23 (`EN-RelationStrength`,
  `EN-EvidencePolicy`, `EN-Grouping`), ADR-005 (identities and epochs), `contracts/identity-v1.md`,
  `contracts/entities-v1.md`, `contracts/metrics-v1.md`

## Context

`metrics-v1` answers totals over a whole session, and the first question after "how much" is "which process".
The observation rows deliberately carry no resolved identity (R1), so the answer needs a derivation: process
instances, and a rule that binds each record to one. Building it met four facts the plan did not settle.

**The provider start key is not in the derived store.** `ProcessSequenceNumber` and `CreateTime` are admitted into
the journal, but `observation-v1` has no column for §7.3's source correlation fields, so a derivation over segments
cannot see them. Reading them back needs the compiled admission plan, which lives in the platform adapter and which
the portable analysis module may not reference (R19).

**Most traffic belongs to processes the capture never saw start.** On the measured 555-record corpus, 14 of the 55
PIDs that appear have no lifecycle record at all, and they carry almost all of the transport bytes. A binding that
requires creation evidence would leave the busiest processes unattributed.

**identity-v1 is conservative about reuse, for a reason.** It resolves a PID-only record through a single witnessed
lifecycle, and leaves a PID with several epochs unresolved, because a record timestamped inside a later lifetime can
be a late record of the earlier one. Its own fixture is exactly that case. Read literally, though, the rule also
leaves unresolved every record inside the *first* lifetime of a reused PID, which carries no more risk than a record
of a PID that was never reused.

**There is no boot identity for an import.** A process key needs host and boot. The host is derived from the
evidence already; nothing derived the boot.

## Decision

### 1. Instances are derived from `observation-v1` alone

Per PID, in the order of native readings, a creation opens an instance, an exit ends the live one, and a
capture-state rundown confirms it or, when none is live, opens one that was running before the capture. A record that
contradicts the one before it opens an instance flagged with the gap — `ExitNotWitnessed` when a creation arrives
while an instance is live, `CreationNotWitnessed` when an exit or a rundown arrives after one ended — rather than being
dropped or merged. A PID that records name but no lifecycle record does gets one provisional instance, witnessed by
the earliest record that names it: evidence that it existed, never an invented start (§7.2).

The instance key is identity-v1's: an observed creation keys a created instance, and the witnessing record's
raw-record key keys every other one. Lifetimes are half-open, and an exit record is its instance's last reading.

### 2. A record binds by where its reading falls, and how strongly depends on what the capture witnessed

| The record | Binds | Strength |
|---|---|---|
| A lifecycle record | to the instance it creates, ends or confirms | `Direct` |
| Any other record, inside the PID's first instance | to that instance | `Correlated` |
| Any other record, inside a later instance of a reused PID | to that instance | `Candidate` |
| A record with no owner in its payload | nowhere | `Unresolved: NoOwner` |
| A reading no lifetime holds | nowhere | `Unresolved`: before the first evidence, between two instances, or after the last exit |

The owner is the one the record's own payload names; the event header's process is context and is never promoted
(§4.1). A reading near a lifetime is never moved into it: proximity does not choose an instance (P6).

The evidence policy decides which strengths a grouped total admits. The default, `IncludeCorrelated`, does not admit
candidates, so a record of a reused PID's later instance is unattributed by default — identity-v1's stance — and is
attributed, labelled, when candidates are asked for. identity-v1 is amended to say which records of a reused PID it
resolves: those in the earliest lifetime, which no witnessed epoch precedes. `ProcessEpochResolver` implements the
same rule as `EarliestLifecycle`, so the domain contract and the derivation agree.

### 3. The boot is derived from the capture's clock

A monotonic source clock does not survive a restart, so one clock scopes one boot. `BootId.DeriveFromClock` makes
every key of one capture share a boot and keys on two clocks differ, without claiming which boot of the machine it
was. A live capture that can read the operating system's boot identity may key by it later; that is a new rule
version, not a silent change.

### 4. The start key, parents and image names are the next derivation, not a guess now

Carrying §7.3's source correlation fields needs a place in the derived store — a second segment table, or a
successor to `observation-v1` — and parents and image names come with it. Until then an instance keyed by an observed
creation is stated as such, and a grouping that needs a name, such as `Executable`, is reported unavailable.

## Consequences

- `ProcessInstanceIndex` and `process-binding-v1` live in `InterCat.Analysis`, rebuildable from published segments
  alone (R20). `icat processes` lists instances with their witness, lifetime, records and transport bytes, and `icat
  metric --group-by process` ranks any total by instance with an exact remainder and the unattributed contributions
  by reason.
- On the 76,092-record session of the IC-009 `peak` level, 482 of 538 instances were witnessed by the capture-state
  rundown and 56 were created during it. The two workload processes were created and exited in the capture and are
  each other's peers: 34,168,998 B sent by one is 34,168,998 B received by the other. Four transport records name a
  PID after its only instance exited and are reported as such rather than attributed.
- The rule assumes no lifecycle record was lost. A lost exit and creation pair merges two instances, and no session
  publishes the coverage ledger that would say one was lost; every process-grouped result says so.
- Grouping `BytesSent` under receiver accounting, or `BytesReceived` under sender accounting, by process is
  unavailable: the receive record names its receiver, and attributing it to the sender needs a proven transfer
  association.
