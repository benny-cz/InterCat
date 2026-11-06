# ADR-090: A section gate follows a section by its handles, and a view by its process and base

- Status: accepted for M6's section discovery work package; no record admits through the gates yet
- Date: 2026-11-06
- Decision owners: InterCat maintainers
- Relates to: ADR-083, ADR-087, ADR-089, plan M6, §4.2, R22, P12, P20, FX-SECT-001

## Context

FX-SECT-001 found a section two processes share recorded by two kinds of records that no field joins (ADR-089). The object
manager's handle records name the section object's kernel address, typed Section - 49 on this build (its third addendum).
The file view flag's records name each view by its process and base and carry, in their file object field, one kernel
address that every view of the section carries in any process and no handle does. Every one of those addresses is reused
once what it names is gone: the opener's one base held both sections' views in turn. A capture decides each record as it
arrives, on its callback, so a gate must number sections and views as their records arrive, keeping no address (R22) and
allocating nothing per record (P12), as the synchronization handle gate (ADR-083) and the window message gate (ADR-087) do.

## Decision

1. **The handle gates share one algorithm.** `ObjectHandleGate` follows the objects of the types it is given from the
   first handle a capture sees made at an object's address to the close of the last, numbering each in the capture: a
   handle made before the capture is numbered alone at its close or the stop's rundown, and a kernel handle is never
   followed (ADR-083). The synchronization handle gate gives it Event, Mutant and Semaphore, and `SectionHandleGate` gives
   it Section, each by the numbers measured for the build, which a capture holds to its own type rundown; a build none
   measured has no gate. Both keep their own surface, and both return an `ObjectIncarnation`.
2. **A view is followed by its process and base from its mapping, or the start's rundown, to its unmapping.**
   `SectionViewGate` numbers each view in the capture. A mapping at a base its process's followed view holds ends that
   view, its unmapping unseen: the base's next view is another, though it maps the same section.
3. **A section is followed by the value its views carry, from the first followed view carrying it to the unmapping of the
   last.** While a followed view holds the value, the section cannot be gone and the value is its alone, so every view
   carrying it is the section's; once none does, the value's next view begins another section. A view's section is a
   number in the capture, never the value, and never the handle gate's number: no record joins a view to a handle
   (ADR-089).
4. **A view the gate never saw begin is numbered alone**, at its unmapping or in the stop's rundown. It joins the section
   its value names only while a followed view holds that value; otherwise its section is numbered alone too, never joined to
   another by a value nothing held between them (R22). A view whose value is zero names no section and joins none.
5. **Both gates are bounded and allocate nothing per record.** The view gate follows at most 262,144 views, and so as many
   sections, four times the handle gate's bound: the start's rundown alone began 61,016 to 61,146 views machine-wide in
   FX-SECT-001's runs. A view that would begin beyond the bound is not followed. Both gates' tables grow only as far as
   their bounds: growing to hold 262,144 views, each of a section of its own, the view gate's allocated 57.3 MiB, the
   arrays they outgrew included, which a test holds to 64 MiB, and once grown, views mapped and unmapped in turn allocate
   nothing.
6. **No record admits through the gates yet.** Reading the view records by their measured layout or refusing them
   (ADR-089's fifth decision), binding them to the process that raised them, and a profile collecting them come next.

## Consequences

- The synchronization handle gate's behavior, tests and callers are unchanged; the incarnation it returns is renamed
  `ObjectIncarnation`, as both handle gates return it.
- FX-SECT-001's committed ledger, its ordinals standing for the addresses, bases and values they number, reads through the
  gates as its two sections: each section's handles in both processes one number, every view of a section in both
  processes one section number, and each of the opener's mappings at its one base a view of its own.
- The callback walk reaches both section gates' reads with no lock, nothing that blocks and no allocation, and the
  capture's own walk reaches the shared algorithm through the synchronization handle gate.
- Tests hold the gates on synthetic records and on the ledger. Twenty-one mutations of the gates are caught: twelve of the
  view gate's, three of the section handle gate's, five of the algorithm the handle gates share and one of the
  synchronization handle gate's.
