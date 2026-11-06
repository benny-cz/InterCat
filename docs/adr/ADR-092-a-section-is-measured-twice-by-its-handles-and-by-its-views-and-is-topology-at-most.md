# ADR-092: A section is measured twice, by its handles and by its views, and is topology at most

- Status: accepted for M6's section discovery work package; FX-SECT-001 measured through the product, topology only on
  10.0.26220.0-x64 (addendum); FX-SECT-002, sections made and ended in generations, each its own (second addendum);
  FX-SECT-003, sections held across a capture's start, joined to no one section by their handles (third addendum)
- Date: 2026-11-06
- Decision owners: InterCat maintainers
- Relates to: ADR-084, ADR-088, ADR-089, ADR-090, plan M6, §14.2, R3, R18, R19, R22, P1, P3, P27, FX-SECT-001, FX-SECT-002, FX-SECT-003

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

## Second addendum: FX-SECT-002, sections made and ended in generations (revision 982)

FX-SECT-002 runs FX-SECT-001's two processes and two sections 64 generations one after another, each generation's
sections under names of their own, made only once the last generation's were unmapped and closed by both processes, the
opener mapping and unmapping each section's view twice. A capture keeps no address, so a private system logger of
`tools/InterCat.SectionProbe --product --generations` watches each run beside the product's capture: it reads each
section's object address from the creator's close that names it and the value its views carry from the records of the
views the truth mapped, and counts the sections of a later generation the kernel made at an address, or whose views it
gave a value, an earlier generation's section held - counts alone, never an address, a base or a value.

- In three runs through the product, machine-wide and loss-free - 440,285, 355,301, 259,938 records admitted, none
  dropped, none undecodable - every section was found twice and bound to exactly its two processes: 128 of 128 a run
  discovered with their lifetimes, under 128 handle numbers and 128 view section numbers, and 1,280 of 1,280 truth
  operations a run seen and bound to their section.
- The kernel gave the views of 30, 30 and 31 sections a value an earlier generation's section's views had carried, 88
  of the 91 backed by the paging file, and in the first run made 6 sections at an object address an earlier
  generation's section had held. The view gate numbers a section by its value only while a followed view holds it, and
  the handle gate by its address from the first handle made there to the last close, so each was a section of its own.
- The views' value was never a section's object address, and 373 of 384 mappings a run were made at a base an earlier
  generation's view had held in its process, each a view of its own.
- A test evaluates the first run's committed ledger against its truth and holds it to the probe's counts of the same run;
  the build's per-build coverage takes sections' tier, topology only, from FX-SECT-002, the mechanism's latest fixture.

## Third addendum: FX-SECT-003, sections held across a capture's start (revision 983)

FX-SECT-003 is the mechanism's ambiguity case. FX-SECT-001's creator makes its two sections and maps a view of each, and its
opener opens both and holds its handles while a capture is launched; inside it the opener maps and unmaps its views of them
four times, and both processes unmap and close. FX-SECT-001 itself, made and ended inside the same capture, is the control.
`tools/InterCat.SectionProbe --product --held-ms` launches `icat record` only once the workload says its opener holds both
sections, and ledgers each workload's records of the one session apart.

- In three runs, machine-wide and loss-free - 193,696, 226,079, 240,713 records admitted, none dropped, none undecodable -
  every handle to the held sections and the creator's views of them were made 1.3, 0.9 and 0.9 seconds before the
  session's first section record, and the opener's views and every close came after it.
- By its handles each held section is two numbers: the handle gate met each close of an address no followed section held
  and numbered it alone (ADR-090), so each of the four closes naming a held section carries a number no other record of
  the session does, and decision 1 finds no one number the section. None is discovered, and no two processes' handles
  to it are joined by its name - a name is never a correlation key by itself.
- By its views each is one number in both processes: the session's start rundown names the creator's view, mapped before
  it began, with the value its section's views carry, and the view gate begins that view and numbers its section by the
  value while it holds it, so the opener's views mapped inside the capture join it. The 16 of the two sections' 20 truth
  views the capture saw mapped and unmapped - the opener's - are each bound to their section, and both processes to each:
  memberships its views resolve, though the capture never saw a section made, nor the creator's view mapped but as the
  rundown's inventory, so no lifetime is.
- The control made inside the same capture was whole each time: two of two sections discovered with their lifetimes, 28
  of 28 operations seen and bound.
- No tier is assigned to the fixture: it measures what a capture refuses to join, not how much of the mechanism it observes.
