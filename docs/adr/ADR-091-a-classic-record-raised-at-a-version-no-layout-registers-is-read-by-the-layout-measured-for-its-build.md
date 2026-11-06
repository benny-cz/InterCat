# ADR-091: A classic record raised at a version no layout registers is read by the layout measured for its build

- Status: accepted for M6's section discovery work package; the file view flag's four events are read by it on
  10.0.26220.0-x64, requested by no profile yet
- Date: 2026-11-06
- Decision owners: InterCat maintainers
- Amends: ADR-035 (its sixth decision: a classic event is admitted from the machine's schema)
- Relates to: ADR-082, ADR-089, ADR-090, plan M6, §4.2, §18.3, R3, R22, FX-SECT-001

## Context

ADR-035 admits a classic kernel record only by the layout the machine registers for its class. The capture reads that
layout from TDH before it starts, and refuses an opcode TDH cannot describe rather than admit a guessed field. This build
raises the file view flag's records - a view's mapping and unmapping, and the rundowns at a session's start and stop - at
version 3, 44 bytes, and registers only version 2's layout, five fields (ADR-089). Under ADR-035 a capture could admit none
of them. Sections' views, whose lifetimes the plan asks for, would then be unsupported on a build that records every one.
The kernel's duplication, raised at version 3 too (ADR-082's sixth addendum), left the same question open.

A measured layout is not a guessed one. FX-SECT-001 measured every field of version 3 against a truth: each view's base
was the same at its mapping and unmapping, its size was the truth's, its value was the one every view of its section
carries, and its process was the one that raised it. The probe's own views measured the rundowns alike, each record naming
a process alive then (ADR-089's fourth addendum).

## Decision

1. **A layout measured for a build reads a classic class's records at a version the machine registers none for.**
   `MeasuredClassicLayouts` carries each one, as the type numbers measured for a build are carried (ADR-082's fourth
   addendum): a class's opcodes at one version, every field by its name and type in the order the records carry them, the
   build that measured it, and the reports that measured it. It is never read from a delivered record, and a test holds it
   to its reports.
2. **The machine's registration always wins.** A measured layout is read only where TDH describes none of the class's
   opcodes at the version a source is planned for.
3. **It is stated.** A capture whose source is read by a measured layout says so among its plan's warnings, which the
   profile's preview shows. Its schema fingerprint is of a scheme no registered layout's is, over the layout's build, class,
   version and fields, so a journal tells a measured interpretation from a registered one.
4. **A build none measured is refused.** There the machine registers no layout at the version planned and none is
   measured for the build, so the source is refused, as before; no build's records are read by another build's
   measurement.
5. **What it reads now.** The file view flag's four events on 10.0.26220.0-x64, at version 3: a view's base, file object,
   extra information and size, and a rundown's process. The duplication at version 3 stays undecodable until a measurement
   through the product admits it.

## Consequences

- The file view flag admits a view's mapping and unmapping and both rundowns on its measured build, through the section
  view gate (ADR-090's addendum), and is refused on any other. No profile requests the flag yet, and no build carries a
  tier for sections.
- ADR-035's sixth decision holds wherever the machine registers a class's version. A classic record at a version neither
  registered nor measured is refused, as an opcode TDH cannot describe always was.
- Tests hold the measured layout to its six reports, its compilation to its offsets and its statement, a registered
  layout winning over it, an unmeasured build's refusal, and TDH describing the views at version 2 alone on this build.
