# ADR-028: The broker's recording path is a module of its own

- Status: accepted for M1 and M2
- Date: 2026-09-23
- Decision owners: InterCat maintainers
- Relates to: §9 (module boundaries), R16, R19, P18, IC-014, ADR-001, ADR-027

## Context

`InterCat.Capture.Journal` was introduced as the one bridge between the Windows capture adapter and the storage
formats, to be shared by the CLI, the comparison harness and the future broker. Since then it has also come to hold
work the broker must never do:

- `EtlCanonicalImport` parses standalone ETL files;
- `ObservationNormalizerV1` decodes admitted records into rows;
- `JournalRederivation` replays journals;
- `LiveSessionFollower` derives a followed session;
- `LiveSessionRecorder` compacts, which reads segments back.

The dependency map keeps the broker from referencing the module at all (R19), so the evidence-only path of ADR-027
had no home the broker could reach.

## Decision

1. **`InterCat.Capture.Recording` holds what a privileged recording runs:**
   - `LiveRecorder`: capture, one journal chunk per publication, the plan first and the ledger last;
   - the envelope mapper and the capture coverage tally;
   - the retained normalization plan.

   It references Domain, Storage and Capture.Windows, and derives nothing itself.
2. **A derivation is a hook.** `ILiveRecordingDerivation` adds each record's rows before the record is appended, and
   acts on every publication before the next chunk begins. `InterCat.Capture.Journal`'s `LiveSessionRecorder` supplies
   the normalizer and the compaction policy through it, which is what an elevated `icat record` runs. The broker will
   supply none.
3. **Everything that parses, decodes, replays or queries stays in `InterCat.Capture.Journal`**, which now references
   the recording module. The broker will reference `InterCat.Capture.Recording` and nothing that parses.

## Consequences

- Behaviour is unchanged: 681 tests pass, and a live 6-second recording published 4 chunks and one closing
  compaction, 785 records, nothing lost.
- The dependency map gains the module, and the architecture test holds both edges. The broker's reference arrives with
  its runtime, so that no edge exists before it is used.
- `JournalNormalizationPlanV1` moved with the recorder because the recorder stages it. Its decoder therefore sits in a
  module the broker will link. That is JSON the broker never reads; R16 is about what runs privileged, and the broker
  runs only what it stages.

## Alternatives considered

- **Let the broker reference `InterCat.Capture.Journal`.** Rejected. It would link the ETL parser and the normalizer
  into the privileged process, and the architecture test could no longer state P18.
- **Copy the recording path into the broker.** Rejected. Two recorders would drift, and `icat record` would stop
  exercising the broker's path.
