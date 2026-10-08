# ADR-049: The broker keeps content only from its client's own processes, each named by its PID and start, held while it records

- Status: accepted; revision 451 (decisions 1 and 3 at prepare); start (decision 2), the content limit (decision 4) and
  `icat capture --profile content` follow
- Date: 2026-10-08
- Relates to: §9, §11, §11.1, R16, R22, P18, P19, ADR-036, ADR-037, ADR-047, `contracts/broker-v1.md` §2-§3,
  `contracts/content-v1.md`

## Context

A Content request names the processes whose content a capture keeps (ADR-037). `icat record --profile content` runs
elevated, so whoever runs it may already read any process it names. The broker runs elevated for clients that are
not: if it kept the content of any process a client named, an ordinary user could read through it what another user's
process, or their own elevated one, sends and receives - the escalation §9 keeps the broker from being. So the broker
refused every Content request it previewed (`UnsupportedContentCapture`), and ADR-047 has since made the follow mirror
the content an evidence capture keeps, which leaves that refusal the only step between the broker and content.

A request names a process by its ID, which Windows reuses (R22), and the broker cannot take the client's word for whose
a process is (P19).

## Decision

1. **A client names only processes it could read itself.** Each named process must run under the client's own user, in
   its own logon session - the owner every broker capture belongs to - at an integrity level no higher than the
   client's. The broker reads the user, logon session and integrity from the process's own token, through the handle
   that holds it open (ADR-037's holds, the one way the product opens a process), never from the request; a process that is not running, cannot be read, or is
   another user's, another logon session's or of higher integrity is refused before anything is prepared, naming it
   and why. Content kept through the broker is then content the client could read without it.
2. **A named process is its ID and its start.** Prepare reads each process's creation time with its owner and pins it
   in the plan, which the digest covers; Start opens each process again, refuses one whose ID now names a process that
   started at another time or no longer passes decision 1, and holds every named process open while the capture
   records, so no other process can be given its ID (ADR-037's holds).
3. **The plan digests the content policy.** The source, the mechanism, each named process with its start, the channel
   selectors, the per-record and session limits, the retention and the inspection consent are written after every
   other field, only for a Content plan, so every metadata-only plan keeps its digest; the summary states them for the
   review, and its collection statement says what is kept.
4. **What it keeps stops it.** Content is kept until the session limit the request allows (stop-at-limit, the only
   content retention); reaching it stops the capture and finalizes its evidence, saying so, as a journal limit does.
5. **Everything else stays where it was.** The broker parses and decodes no content (P18): it keeps the bytes the
   source raised, within the policy, as `icat record` does; a follow mirrors them (ADR-047), and inspection stays the
   viewer's, under the consent the request gave.

## Consequences

- An unelevated window or `icat capture` can keep the content of the person's own applications, never another
  user's or an elevated process's; an administrator who needs those runs `icat record`, elevated, as before.
- A process that exits between prepare and start, or whose ID is reused, refuses the start rather than recording a
  stranger.
- A Content plan's digest differs from every metadata-only plan's by construction, and a plan prepared before this
  revision keeps its digest.

## Alternatives considered

- **The client's word for the process's owner.** Rejected: P19.
- **Same user, any logon session.** A user may open their own processes in another logon session, but the broker's
  captures belong to a user in one logon session; one notion of the owner is easier to hold to.
- **Letting an elevated client name any process.** Rejected: an elevated client can run `icat record` itself; the broker
  exists for clients that are not.
