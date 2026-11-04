# ADR-078: A COM call is a mechanism of its own, never an activation

- Status: accepted for M6's COM/DCOM/WinRT work package; EN-Mechanism gains `ComCall`, which no source admits yet
- Date: 2026-11-04
- Decision owners: InterCat maintainers
- Relates to: ADR-030, ADR-037, ADR-077, plan §5.1, §23, M6, R5, P4, P7, FX-COM-001, FX-COM-002

## Context

ADR-077 measured that `Microsoft-Windows-COM-Perf` records each COM call at both ends - its interface and method, the
process at its other end - with its start joined to its stop. A session's process filter holds for it, and a call across
an apartment within one process is recorded as an out-of-process call is, naming its own process. EN-Mechanism names COM
only as `ComActivation`, and a call is not an activation: M6's exit says a COM activation event cannot become a call
count, and COM-Perf's activations carry no field. Admitting COM-Perf's calls needs a mechanism for them.

## Decision

1. **EN-Mechanism gains `ComCall`, code 22, named "COM call".** It is a call made through COM's channel from one apartment
   to another, as COM-Perf's client and server starts and stops record it, with its interface and method. `ComActivation`
   keeps COM's activations, which never count as calls. A COM call's fixtures keep the `COM` prefix.
2. **It is between processes only where its record names another.** A COM call's two ends are each a record of its own
   process naming the process at the other end. One naming its own process is that process's own work across an
   apartment, never IPC and never a relation (ADR-077's fifth decision).
3. **A family of its own.** Its rows' fact discriminator is `com-call`, and its hue is the remote-call family's, beside
   RPC's. No capture has admitted a COM call, so no row is re-keyed.
4. **What it does not decide.** Binding a COM call's record by the process that raised it, as RPC's and HTTP's are bound
   (ADR-030), was measured on FX-COM-001 and under a process filter. It needs a new binding rule identity, which comes
   with the slice that admits its records. A COM call carried by an RPC call that the RPC source records is one exchange
   (P4), to be measured before a profile admits both.

## Consequences

- The plan's §23 table and the domain's enumeration name the same mechanisms by the same codes, which a test now holds
  (R5).
- Coverage and every list of mechanisms name "COM call", not collected until a source admits it.
