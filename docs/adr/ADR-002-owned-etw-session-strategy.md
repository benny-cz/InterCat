# ADR-002: Owned ETW session strategy for M0

- Status: accepted for M0, revisable when a supported build is measured
- Date: 2026-09-21
- Decision owners: InterCat maintainers
- Supersedes nothing; amends no part of ADR-001

## Context

§18.2 requires the capture to select and record a logger strategy per Windows build, and §9.2 requires
unique session names, explicit ownership and cleanup limited to what an attempt created. M0 had to
prove that a driverless, unprivileged-viewer design can observe a process and network vertical path at
all, without a broker existing yet (IC-014).

## Decision

1. **Named manifest-provider session, one per attempt.** The M0 experiment creates a real-time session
   named `InterCat-<purpose>-<pid>-<token8>` with `Create | NoRestartOnCreate`. It never uses a system
   logger, never touches `NT Kernel Logger`, and never restarts or adopts an existing session. A name
   collision is a refusal, not an adoption (P14). The ownership token is recorded with the run and is
   the only proof of ownership; a similar name is never treated as one (P19).
2. **Capture-side scope by keyword and event id.** Each provider is enabled at an explicit level with a
   recorded `MatchAnyKeyword`, and the profile's admitted descriptors are passed as an event-id filter.
   Payload-producing descriptors (`Microsoft-Windows-RPC` events 10 and 11) are denied by the catalog
   and are never enabled by default (P28).
3. **Bounded admission in the callback.** The callback resolves a precompiled descriptor plan, performs
   a body-length shape check, copies at most eight fixed-width fields into a pooled inline buffer, and
   enqueues into a bounded channel. A full queue increments an application-drop counter and returns
   immediately; it never blocks acquisition (R8, R9, §9.3).
4. **Offsets come from the machine's own schema.** Admission plans are compiled from the manifest TDH
   reports for the registered provider, not from constants in code. A field the saved schema cannot
   support is refused with a reason instead of being guessed (§18.3).
5. **Health counters stay independent.** Provider-reported loss, consumer buffer loss, application
   drops, policy omissions by reason, undecodable records by reason and quarantined timestamps are
   separate quantities that are never folded into one total (§20.6).
6. **Rundown is requested after delivery starts.** `Microsoft-Windows-Kernel-Process` state is captured
   after the pump is running, so short-lived processes are less likely missed; a refused rundown is a
   recorded degradation, not a silent gap (§18.5).

## Measured findings that this decision records

Measured on build `10.0.26220.0-x64`, which is **outside** the §1.3 support matrix, with fixture
`FX-TCP-001`:

- `Microsoft-Windows-Kernel-Network` TCPv4 descriptors 10–15 deliver `PID`, `size`, `saddr`, `sport`,
  `daddr`, `dport` and `connid` as fixed-width fields at computable offsets.
- Addresses and ports arrive in **network byte order**; the documented transform is applied when an
  observation is built, never to the admitted value (R1).
- The `saddr`/`sport` pair names the **owning process's own endpoint** on send and receive descriptors
  alike, so the flow is not re-oriented by direction. The message template's "from … to …" wording does
  not describe which side is local.
- With those semantics, all six §14.2 criteria were met at 100% with zero false peer attributions, and
  observed transport bytes equalled the truth log's completed bytes exactly.

## Consequences

- The tier for TCP stays `ExperimentalEvidence`: the thresholds were met, but not on a supported build.
  Promotion requires re-running the fixture on Windows 11 24H2 x64 (P27).
- The M0 capture path is an explicitly disposable spike under §21.2: it admits a bounded field
  projection rather than the `RecordEnvelopeV1` journal. IC-011 replaces it, and the replacement must
  keep the identity, ownership and counter behaviour asserted by the current tests.
- Elevation is required for the whole CLI in M0 because no broker exists. IC-014 moves the privileged
  surface behind the broker so the viewer and any parsing stay at ordinary integrity (R16, P18).
- ALPC stays unmeasured: it is a kernel flag group with no registered manifest provider, and this
  strategy deliberately does not create a system logger.
