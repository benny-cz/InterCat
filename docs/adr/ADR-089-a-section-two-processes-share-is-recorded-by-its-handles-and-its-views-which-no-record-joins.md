# ADR-089: A section two processes share is recorded by its handles and its views, which no record joins

- Status: accepted for M6's section discovery work package; no source is inventoried yet; since revision 971 the file view
  flag's collection cost is measured, Low in processor time but a view-heavy workload past the throughput target (addendum)
- Date: 2026-11-06
- Decision owners: InterCat maintainers
- Relates to: ADR-035, ADR-082, ADR-083, plan M6, §4.2, R22, P2, P3, P6, FX-SECT-001

## Context

Section discovery's metadata inspection (revision 969) found a section's handles declared by the kernel's object manager as
every object's, and a view's mapping and unmapping declared by the classic file I/O class at version 2, each with the view's
base and size, a file object and the process, but nothing naming the section a view maps. A layout on one machine is no
evidence of what the kernel emits. The plan asks shared memory for its sections' membership and their mappings' lifetimes
where supported; a mapping's size, committed pages and dirty pages are never transfer volume.

## Measurement

FX-SECT-001: a creator makes two named sections of the session's namespace under a name of the run's own, one backed by
the paging file and one by a file of its own, maps a view of each and writes seeded bytes into it; an opener opens each by
its name and maps and unmaps a view of it a number of times, reading the creator's bytes through each. Each process logs
every section it made or opened, every view it mapped or unmapped, with its size, and every handle it closed; a truth gains
two event kinds for views. `tools/InterCat.SectionProbe --measure` reads a run under a private system logger of its own,
machine-wide: process lifecycle and the kernel's file view flag, VAMAP, with the object manager's handle group added to the
session's masks (ADR-082). Three runs - 64 KiB views four times, 4 KiB sixteen times and 1 MiB twice - lost nothing:

- Each process's handle to each section was created and closed once, both processes' at one address, typed Section. A
  creation carried no name and the close carried the section's, as a named synchronization object's (ADR-082).
- Every view either process mapped or unmapped had its record raised in that process, at most 21 microseconds before the
  call it was logged after returned, with the size the truth logged; each unmapping carried its mapping's base.
- Every view of one section, in either process, carried one value in its file object field: a kernel address, never the
  section object's that its handles carry, and in no other record of the two processes. A section backed by the paging
  file had 0xC4 in its views' extra information's high byte, and one backed by a file 0x04.
- The records were raised at version 3, which no layout the machine registers describes: 44 bytes, eight of them - a
  page-aligned offset - between the size and the process, which is the process that raised the record.
- The opener mapped both sections' views at one base. Each process's runtime mapped about 730 other views, which name
  neither section's value. No record says a byte was read or written through a view, though the opener read the creator's
  bytes through every one of its views.

## Decision

1. **A section is one section by its object's address within its lifetime**, never by its name or its address alone (R22,
   P6), as a named synchronization object is: each process's handles to it are joined at that address, and their closes
   name it.
2. **A view is a process's mapping of a section, by the process that raised it.** The value its file object field carries
   joins it to every other view of the same section, in any process, but to no handle: a view says that a process maps a
   section, and which views map the same one - never which named section it is.
3. **A view's base and its value are addresses, reused:** the base of one process's view held another section's view next,
   and an address names a view or a section only within its life. A gate admitting them numbers them in the capture and
   keeps no address (R22), as the synchronization and window message gates do.
4. **A mapping moves no byte a record shows.** A section's views are membership, never traffic: neither a view's size nor
   its section's is a byte measurement or transfer volume, and none is drawn as one (P2, P3).
5. **The records are raised at a version no registered layout describes.** A capture that admits them reads them by the
   measured layout, stated, or refuses them, as it treats a duplication raised at version 3 (ADR-082's sixth addendum).
6. **No source is inventoried yet.** The file view flag's cost and a gate that numbers sections and views come first.

## Consequences

- A section's membership can be shown from its handles, and its views' lifetimes from their mappings and unmappings; which
  of a process's views map a named section is said only where a view's value is shared with a view whose section is known,
  or not at all.
- The probe writes counts, roles and ordinals alone - never a PID, a handle value or an address - and the first run's
  ledger and truth are committed under `fixtures/FX-SECT-001/evidence/`.
- Tests hold the ledger to its truth and the three runs to the layout the inspection read; fifteen alterations of that
  evidence are caught.

## Addendum: the file view flag's collection cost (revision 971)

`tools/InterCat.SectionProbe --measure --impact` measures what collecting the kernel's file view flag costs before any
product path exists for it, as the window message and synchronization probes measured theirs: FX-SECT-001's opener
mapping and unmapping each section's 4 KiB view many times, in nine pairs - once with no session and once while a private
system logger of the probe's own collects the flag machine-wide, its consumer only counting the mappings and unmappings -
in alternating order. Four series, nothing lost:

- Over trials of about four seconds, 100,000 rounds, the processor time the session added, the workload's and its
  consumer's, was a median 0.49 and 0.55 CPU pp, Low; over trials of about a second, 20,000 rounds,
  1.3 and 1.1, the consumer's settling weighing on a second's work. The machine's busy share stayed within the
  processor target in every series.
- The workload spent 1.9 to 9.2% more processor time of its own, as a view's records are raised in the thread that
  maps and unmaps it, and its throughput fell 9.5, 5.2, 5.3 and 0.2%, past §12's 5% target in three series of four.
- The workload raised 78,496 to 126,535 view records a second; the machine's other processes, which map views
  whatever runs, raised 63 to 1,408 in the second before a trial.

So the flag's collection is Low in the processor time it adds, but a view-heavy workload slows past the throughput target:
only an opt-in profile that states it may admit the flag, never Explore.
