# ADR-006: Native and local clock representation

- Status: accepted for the IC-007 foundation
- Date: 2026-09-21
- Decision owners: InterCat maintainers
- Relates to: ADR-005 (identity), `contracts/identity-v1.md`

## Context

The M0 projection retained QPC and converted UTC ticks, but did not identify the source clock or state
whether conversion had already occurred. Treating those values as one generic timestamp would enable
double conversion, cross-host QPC comparisons and silent viewport distortion from corrupted events.

## Decision

1. Native encoding, source clock identity, session-relative nanoseconds, wall-clock estimate and
   workspace-aligned time are distinct values. Alignment is a derived revision, not a source field.
2. Every source clock records host, kind, encoding, frequency, native capture epoch, explicit rounding
   and a maximum plausible session distance. Calibration retains uncertainty.
3. Local delta conversion uses checked `Int128` arithmetic and nearest-even rounding. Native ticks stay
   available for local interval work; nanosecond representation does not claim nanosecond accuracy.
4. Clock/encoding mismatches, overflow and implausible distance are quarantine outcomes. They cannot
   advance a watermark or stretch a viewport.
5. QPC from different hosts is incomparable until a versioned alignment with uncertainty exists.

## Alternatives rejected

- Store only UTC `DateTime`: loses monotonic source evidence and hides conversion policy.
- Store only QPC: prevents useful wall display and is meaningless across hosts.
- Floating-point seconds: loses deterministic boundary behavior for large values.
- Clamp invalid timestamps: rewrites source evidence and can disguise corruption.

## Consequences and reversal cost

The journal must persist clock descriptors and native timestamps rather than only the M0 converted field.
Queries carry alignment revisions separately. A different internal unit or rounding policy requires a
clock contract version and re-derived indexes; native evidence remains sufficient to rebuild them.
