# ADR-092: A section is measured twice, by its handles and by its views, and is topology at most

- Status: accepted for M6's section discovery work package; FX-SECT-001 measured through the product, topology only on
  10.0.26220.0-x64 (addendum)
- Date: 2026-11-06
- Decision owners: InterCat maintainers
- Relates to: ADR-084, ADR-088, ADR-089, ADR-090, plan M6, §14.2, R3, R18, R19, R22, P1, P3, P27, FX-SECT-001

## Context

The sections profile collects a section's handles and its views (ADR-090). A handle is numbered by its section's address
within the section's lifetime. A view is numbered by its process and base, and its section by the value every view of the
section carries. No record joins the two, as a view names its section by a value no handle carries (ADR-089).

A handle record names its section by name at a close, as a named synchronization object's does (ADR-084). No view record
names anything a truth can recognize, and every process maps hundreds of views of its own - its runtime's data files and
its images - beside the ones a fixture logs. A measurement against a truth must say which records are the truth's without
reading what the capture could not.

## Decision

1. **A section is found by its handles as a named object is.** Its handles' number is the one its closes' names carry
   (ADR-084), with the creator's creation and every close the truth logged seen under it, each in the process that logged
   it.
2. **And by its views, matched by time.** A truth mapping is the record of its process mapping a view of its size, no
   image's, nearest its log line within 5 ms on the wall clock the capture's calibration reads, where that line is the
   record's nearest mapping too. A record lost thus leaves its own mapping unmatched rather than taking its neighbour's.
   The mapping's unmapping is the record unmapping that view's number in the process. The section's views are the one
   number every matched view carries.
3. **Its two numbers are never joined.** A section is discovered with its lifetime where both findings are whole:
   - one handle number from its creation to every close;
   - every view mapped and unmapped seen under one view section number, each mapping a view of its own;
   - neither number another truth section's;
   - the processes the two numbers' records bind to exactly the truth's.
4. **Topology at most.** Its handle opens and closes and its views' mappings and unmappings are its operations. No record
   says a byte moved through a view, and a view's size is the extent mapped, so no operation is byte eligible. The
   mechanism claims neither bytes nor operations, and its tier is topology at most (P3, P27).
5. **What this does not decide.** No capture has been measured through the product. The view records' times come from a
   session's calibration, which a measuring tool reads, and the fixture's run through the sections profile comes next.

## Consequences

- `SectionCoverageEvaluator` is pure and portable: the same evaluation runs in a test on committed evidence.
- Tests hold it on FX-SECT-001's run as a capture would hold it, where both sections are found twice and the mechanism is
  topology only. They hold it beside views nearer the truth's lines than the truth's own records - images, the other truth
  process's views, other processes' views, views of another size - and views a second away. They hold a lost mapping left
  unmatched without its neighbour taken; two sections' views under one number, neither's; two mappings seen as one view; a
  process the truth never names mapping a section; a section whose closes name it by no number or by two, whose handles'
  number is another section's too, or one of whose closes went unseen; and a lone mapping whose only record lies beyond the
  window. Twelve mutations are caught.

## Addendum: FX-SECT-001 through the product (revision 981)

`tools/InterCat.SectionProbe --product` records FX-SECT-001 by the product's own `icat record` under the opt-in sections
profile, the workload started once the session runs, and reads the session it published as the product holds it: each
section record by the process it binds to under `process-binding-v8`, its numbers its source fields, and its time on the
capture's calibrated wall clock. It writes the ledger of the workload's two processes' records - their runtimes' own views
and images among them - and of other processes' on the run's numbers, by role, never a PID, a handle value or an address,
evaluates it as decided here, and deletes the session, which holds every process's views and section handles.

- In three runs, machine-wide and loss-free - 164,165, 305,910, 246,298 records admitted, none dropped, none undecodable, the
  view records read by the layout measured for the build - each of the run's two sections was found twice: by its
  handles' one number from the creator's creation to every close, and by every view both processes mapped and unmapped,
  each matched among hundreds of the processes' own views of other sizes, images among them.
- Each section's two numbers were bound to exactly its two processes, and no other process's record carried either; 28
  of 28 truth operations a run were seen in their own processes.
- Sections are topology only on 10.0.26220.0-x64: the build's per-build coverage carries the tier from FX-SECT-001, and a
  test reproduces it from the first run's committed ledger.
