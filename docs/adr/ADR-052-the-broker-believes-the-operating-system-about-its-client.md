# ADR-052: The broker believes the operating system about its client, and nothing its client says

- Status: accepted; revision 724. It records the trust boundary §20.3 set and `contracts/broker-v1.md` implements, which
  the broker has carried since IC-014 and was qualified with real ETW, and makes a failed revert end the broker on
  every path
- Date: 2026-10-22
- Relates to: §9, §20.3, R16, R19, R22, P18, P19, IC-014, ADR-027, ADR-028, ADR-049, `contracts/broker-v1.md` §1, §4,
  §5.3-§5.6, §6

## Context

Capturing the kernel and system providers needs administrative rights; reading what was captured does not. The broker
is the one elevated process. It prepares, starts and stops captures for an ordinary-integrity client - the window or
`icat capture` - and writes their evidence beneath a root of its own (ADR-027, ADR-028). Whatever a client can make it
do, that client can do with administrative rights, and the same goes for any other process that reaches it.

§20.3 set the rules and the contract implements them; Windows fixtures run them against real tokens, pipes and
directories, and the composed broker was qualified with real ETW. No record said why each rule is the one it is, or
what the boundary still lets through. Writing this one found two places where the plan and the contract said more
than the code: §20.3 had the broker verify the client's integrity level and elevation, where it compares its user and
logon session; and the contract required a revert on every path, where only a failed authentication ended the broker
when the revert itself failed - after a successful read, the failure was an exception, which would leave the thread
running as the client.

## Decision

1. **The client asks; the broker decides.** A request names a reviewed profile and bounded overrides, quota and
   retention - never a provider, keyword, event, body offset, schema or compiled plan (R19). The broker discovers
   capabilities and compiles the plan in its own process from its installed catalog; the client compares the effective
   summary with what the person reviewed, and a difference returns to review rather than starting.
2. **The operating system says who the client is.** The broker creates one pipe, named from a GUID of its own, as its
   first instance and refusing remote clients, with a DACL - supplied at creation - that grants only Local System and
   the capturing user. On each connection it impersonates the client and reads its token: the user and logon session
   must be the owner's, and an anonymous or identification-only token is refused. Integrity and elevation are read, not
   compared: a token below medium integrity cannot open the pipe for writing, and an elevated token of the same user is
   in another logon session. The integrity read bounds whose content the client may name (ADR-049). The broker reverts
   on every path, and a revert that fails ends the broker, after a failed read or a successful one, rather than letting
   a thread go on as the client. A PID or a nonce is never authentication: the client's PID is recorded for diagnosis and
   names the client in its capture, never trusted (R22).
3. **The client checks the broker too.** It launches the broker itself, elevated, keeping its process handle, and before
   Hello requires `GetNamedPipeServerProcessId` to name that process; a process that claimed the pipe's name first is
   refused. First-instance creation protects only the broker's side, and an elevated process's command line - which
   names the pipe - is readable at ordinary integrity.
4. **A prepared plan is held by a grant, not by its digest.** Prepare returns a 256-bit random token, of which the
   broker keeps only a SHA-256 fingerprint, bound to the authenticated user and logon session, used once and expiring
   after 30 seconds by default (5 seconds to 5 minutes). An unknown, expired or another owner's token gets one refusal,
   so the registry says nothing about who exists, and a restart invalidates every grant. The plan's digest identifies
   bytes; it is no credential.
5. **Ownership is durable and the owner's alone.** Start and stop carry request IDs scoped to the owner; the intent is
   written before the runtime is called and the result before success is returned; a replay returns the stored result,
   and an ID reused for another command is a conflict. Status, stop and renewal are the owner's. An interactive capture
   holds a lease, 30 seconds by default (10 seconds to 10 minutes), and one whose owner stops renewing is stopped.
   Disconnecting neither stops a capture nor hands it to anyone.
6. **The broker writes only beneath its root**, created with its descriptor - an Administrators owner, full control for
   Local System and Administrators, read and traverse for the capturing user, a high-integrity no-write-up label -
   validated from the open handle, held open without delete sharing, and every file opened through it by one validated
   name, refusing reparse points.
7. **Bounds come before allocation.** A frame's header is 32 bytes and its payload at most 64 KiB, with at most 64
   fields of at most 16 KiB each. A connection is held to a rate - 256 requests at once, 64 a second sustained - and a
   request past it is answered late, not refused: a runaway client costs a bounded rate, and a well-behaved one keeping a
   capture for a day is never cut off.
8. **A broker lives for its client.** Each launch names a fresh pipe, serves one client at a time, and by default exits
   ten seconds after it has no client and no capture recording.

## Consequences

- Another user, another logon session, a remote client and a process squatting on the pipe's name are refused by the
  operating system or by the checks above, before any command runs.
- A process of the capturing user, in the same logon session and at medium integrity, passes the same checks the
  window does. While a broker serves, such a process can ask for any capture the installed profiles allow, as the
  person's own window could; no check the operating system offers on a pipe tells a person's processes apart, and a
  PID is not identity. What bounds it: each broker starts from an elevation of its own, names a fresh pipe, serves one
  client at a time and lives seconds past its last capture; the profiles are the reviewed catalog; and content comes only
  from processes of the asking client's own user and logon session (ADR-049). The contract's threat model names this
  attacker.
- Ownership and request records have no retention rule beyond the store's count bounds.
- Every check that needs Windows runs only there: the fixtures exercise real pipes, tokens and directories on Windows,
  and none of them runs on another platform.

## Alternatives considered

- **A shared secret on the broker's command line, or a nonce the client presents.** Rejected: an elevated process's
  command line is readable at ordinary integrity, and a secret proves possession, not who holds it.
- **The client's PID, checked against the process that launched the broker.** Rejected: a PID is reused and names no
  identity (R22); the token is the operating system's statement of who connected.
- **A Windows service that runs whenever the machine does.** Not taken: a broker that lives for one client and one
  elevation is reachable for minutes rather than always, and needs no service installed. The checks above would hold for
  a service too, with its owner configured rather than passed at launch.
- **A loopback TCP or HTTP endpoint.** Rejected: it carries no OS identity of its caller, and other users' processes
  can reach it.
- **Comparing integrity and elevation as well.** Not needed for the boundary: mandatory integrity refuses a lower token
  the pipe, and elevation changes the logon session the broker already compares.
