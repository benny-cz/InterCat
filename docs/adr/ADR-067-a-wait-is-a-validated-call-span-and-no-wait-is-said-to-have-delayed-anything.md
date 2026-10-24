# ADR-067: A wait is a validated call span, and no wait is said to have delayed anything

- Status: accepted; revision 767. It records `icat waits`, M12's bottleneck views
- Date: 2026-10-24
- Relates to: plan M12, R3, R18, R21, P8, ADR-031, ADR-034, ADR-066; `contracts/waits-v1.md`

## Context

M12 asks for bottleneck views built on validated local call and wait spans, with no unsupported critical-path or
deadlock claims. The capture records each RPC call's start and stop on the process that made it, paired by activity id
(`operations-v1` §3, ADR-031), and links a client call to the server call that served it through its one ALPC message
(§5c, ADR-034). It records no thread's waits on synchronization objects, and no nesting of one call inside another.

## Decision

1. **A wait is a validated span.** A client call's start and stop, paired on the caller, are its wait; a call without
   both has no time said for it, and is counted by its state.
2. **Waits are grouped by caller, interface and server**, the server being the process whose server calls served the
   caller's through ALPC, where the evidence policy admits it; calls no server is named for are grouped apart, each
   with why.
3. **The server's time is measured over linked calls alone**: the server call's own span beside the caller's, where
   both completed. The rest of the caller's span is not attributed.
4. **No wait is said to have blocked or delayed anything.** Calls overlap and can return before their work is done, so
   waits are summed per call and said to add up to more than the time that passed; no path through calls is called
   critical, and no wait a deadlock.
5. **Command line first.** `icat waits` lists them; the window reads the same reading when it shows them.

## Why

- **A span is what was measured** (R3): a call missing its start or stop has no duration to sum, so it is counted,
  never estimated.
- **Comparing only linked spans keeps the share honest**: a server's time beside a caller's means something only where
  the two calls are the same call's two ends.
- **The capture cannot see blocking.** Without a thread's waits or call nesting, a long call may have run beside other
  work; saying it delayed something would be a finding no record supports.

## Consequences

- `icat waits`, with `--process`, `--limit`, `--evidence-policy` and `--json` (`call-waits-v1`).
- Not built: the window's waits; HTTP, pipe and ALPC spans; thread waits; nesting; waits over an interval or across
  captures.

## Alternatives considered

- **The union of a caller's spans as wall-clock time.** Rejected at this version: it hides how much each interface and
  server took; the per-call sums are said to overlap instead.
- **A critical path through calls.** Rejected: thread nesting is undefined (`operations-v1` §8), so no call is known
  to have waited on another.
- **Thread blocking from context switches.** Not available: no capture here records synchronization waits.
