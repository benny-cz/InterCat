# ADR-117: A remote agent is enrolled by its administrator's token and speaks only TLS pinned to each party's key

- Status: accepted for M11's remote capture coordination; enrollment, the pinned transport, hello and inventory
  (revision 1093); the agent run by its administrator with icat, keeping its key, enrollments and audit (its addendum,
  revision 1094); a capture on its host through its broker within a control lease (its second addendum, revision 1095)
- Date: 2026-11-23
- Decision owners: InterCat maintainers
- Relates to: ADR-038, ADR-052, ADR-115, plan §8.2, §11.1, §15 M11, §25.1, §26.1, `contracts/remote-agent-v1.md`,
  `docs/reviews/M11-commitment-review.md`, P16, R19

## Context

M11 asks for an explicitly installed, authenticated remote agent, with device enrollment and mutual transport
authentication, separate permissions to capture metadata, collect contents, download evidence and install collectors,
local policy authoritative and remote actions audited - and for no remote arbitrary shell, expired and revoked
credentials denied and a wrong host's identity rejected.

Version 1 has no network path at all. Evidence leaves the machine only in a file a person saves (§11.1), and a fitness
test (P16) holds that no product code opens a connection. The broker is reached through a pipe that rejects remote
clients and admits its owner's logon session alone (ADR-052): it is not, and should not become, a network service.
So M11 needs a party of its own, with a boundary of its own, before any capture is coordinated.

## Decision

1. **A project of its own.** `InterCat.Remote` holds the agent's protocol, its enrollments, its transport and a
   controller's connection to it, and references the domain alone. Two of its files alone open a connection - the agent's
   listener and a controller's connector - and the P16 fitness test names them and nothing else.
2. **Each party is known by its key.** An agent and a controller each hold an ECDSA P-256 key in a self-signed
   certificate and are known by its pin, the SHA-256 of the key's SubjectPublicKeyInfo. No certificate authority, no
   trust store and no certificate's names or dates are trusted, and no certificate is installed anywhere. SChannel cannot
   sign with a key held only in memory, so a certificate is loaded again from its own PKCS #12 bytes into a temporary key
   container that goes with it.
3. **Enrollment by a token the agent's administrator issues.** A token is the agent's pin and a 256-bit one-time secret,
   copied whole; it carries grants, and lapses within ten minutes unless its administrator says otherwise, never more than
   an hour. The agent keeps the secret's hash. A controller connects expecting the token's pin and sends the secret only
   once the agent's key is that one, so no party between them learns it. The agent admits the presented key with the
   token's grants and consumes the token. Unknown, used and expired secrets are refused alike, a refused enrollment ends
   the connection, and a revoked key is enrolled again by no token.
4. **Mutual authentication on every connection.** TLS 1.2 or 1.3 with the controller's certificate required: the
   controller goes on only where the agent's key has the pin it expects, and the agent admits a request only from a key
   it enrolled and did not revoke.
5. **Grants apart, local policy over them.** Metadata capture, contents, download and collectors are granted apart. The
   agent's policy is authoritative: by default it allows metadata capture and download, contents only by its
   administrator's choice, and collectors never - installing one is an administrative workflow of its own.
6. **A closed set of messages.** Version 1.0 answers hello, enroll and inventory. A frame has the broker's shape - a
   32-byte header with a magic of its own, a payload of at most 64 KiB checked before it is allocated, at most 64 fields
   of at most 16 KiB - and no message names a command, a program, a path or an address to act on. A frame version 1
   would not write ends the connection after the agent says why.
7. **Audited by pin.** The agent records each request by the controller's pin, the message and its outcome, never an
   address, a port, a path or a command line (P16).
8. **Bounded.** At most 8 connections at once, closed unanswered past that; a handshake within 10 seconds; 60 seconds idle.

## Consequences

- Revision 1093 builds the library and holds it over loopback: a token enrolling once and lapsing, a wrong agent stopped
  in the handshake before the secret leaves, unenrolled and revoked keys refused, malformed frames ending the connection,
  the audit naming pins alone. Nothing installs, listens or captures yet.
- M11's next parts follow this record: the agent's host process, installed and enabled only by an administrator, with its
  key, enrollments and audit kept across restarts; captures on the agent's host through its broker within control leases;
  coordinated start and stop recording each host's acknowledgments and their uncertainty for M4's alignment;
  store-and-forward download with resume, verified against each session's hashes before an investigation takes it; and
  the command line.
- Any party that reaches an agent's port can complete a TLS handshake with a key of its own and say hello. It learns the
  protocol version and nothing of the host.
- A host's administrator opens the agent's port in its firewall; InterCat changes no host's firewall on its own.

## Alternatives

- **Windows integrated authentication between domain members.** It needs a domain both hosts trust, and leaves a pair of
  hosts outside one with nothing.
- **A certificate authority of InterCat's own.** One more key to protect and a revocation list to distribute, for what a
  handful of pins does.
- **A short code a person types, proven by a keyed hash.** An attacker between the parties who sees one proof can find a
  short code offline. A password-authenticated key exchange would not have that weakness, but the platform offers none,
  and writing one is a cryptographic protocol of InterCat's own.
- **HTTP or gRPC.** More surface and a framework reference the product does not carry, for a closed set of messages the
  broker's framing already bounds.
- **The broker's pipe opened to remote clients.** It would widen the elevated process's boundary from its owner's logon
  session to the network (ADR-052).

## Reversal cost

Moderate. The protocol is versioned and closed, and the project stands apart: replacing the transport or the enrollment
changes this project, its contract and its tests, not the broker, the store or a session.

## Addendum: the agent runs when its administrator starts it (revision 1094)

The product owner chose how an agent runs: as a process its host's administrator starts from an elevated prompt and stops
when done, not as a service of its host. Nothing of the agent stays installed or running between uses. The broker it will
drive is an elevated process launched for each capture (ADR-052), and an elevated agent can start one without a prompt
nobody at the controller could answer.

So an agent keeps what it needs between runs, and a controller keeps what it needs between enrollments
(`remote-agent-v1` §9):

1. **Folders that admit their owners alone.** An agent's folder admits SYSTEM and the machine's administrators, is owned
   by the administrators and inherits nothing from its parent; a controller's admits SYSTEM and its user. A folder found
   otherwise is refused, not repaired, before anything in it is read. Only a process running as an administrator opens an
   agent's. The broker's root admits the machine's user to read captures (ADR-115), so an agent's folder takes no access
   from it. The agent's default folder is `remote-agent` in that root, and the agent does not make the root itself: the
   root is made by InterCat's installation.
2. **Keys protected at rest.** An agent's key is kept as PKCS #12, protected by the system's data protection for the
   machine, so any of its administrators may run it; a controller's is protected for its user. Both use InterCat's own
   entropy. The folder keeps the bytes from other accounts on the machine, and the protection keeps them off it.
3. **Enrollments every process shares.** Issuing a token, admitting a controller and revoking one each read the
   enrollments, change them and replace them whole, under one lock file every InterCat process holds. A token issued in
   one process is admitted by an agent serving in another, and a revocation reaches it at its next request. Enrollments
   that do not read as InterCat wrote them are refused, not reset: a reset would forget whom the administrator revoked.
4. **The audit outlives the agent.** One JSON line per request, by pin, rotated past 4 MiB.
5. **icat runs both parties.** `icat agent token`, `serve`, `controllers`, `revoke` and `audit` on the agent's host, from
   an elevated prompt; `icat remote enroll` and `hosts` on the controller's, `hosts --check` asking each agent what it
   allows now. An agent is given by its address, never by a name, and reports the host identity its captures record.

Tests hold these on loopback: a key kept protected and read back as its own; a token issued by one process and admitted
by another; a revocation reaching the serving agent; damaged enrollments refused and left as they were; folders
admitting another account, or inheriting their parent's access, refused; a process not an administrator refused an
agent's folder; and icat enrolling, listing, checking, revoking and auditing. Where the tests run elevated, `icat agent`
serves an enrollment until stopped. Otherwise it is refused, as it would be on any prompt that is not elevated.

## Second addendum: a capture on the agent's host, within a control lease (revision 1095)

A controller holding metadata capture asks an agent to record on its host (`remote-agent-v1` §4.4, version 1.1). The
agent drives its host's capture broker as icat capture does: it launches the broker for its first capture - an elevated
agent needs no prompt - and keeps it until it stops; it prepares one of the metadata profiles within the agent's bounds,
shows the controller what the host will record in the broker's words, starts it, and records it live. A focused or
content capture is not served remotely: the one takes processes the controller cannot see, the other the content review
a person makes on the host (ADR-049).

1. **One capture at a time, the starter's own.** A capture belongs to the key that prepared it; another key is refused
   it as if it were not there. A preparation lapses unstarted after 30 seconds. A capture that reaches its duration
   frees the agent for the next, and so does one its controller stops.
2. **A control lease, and the host's own hold beneath it.** The controller renews a lease of its choosing within the
   agent's bounds - 10 seconds to 5 minutes by default; icat renews 30 seconds every 10. While it holds, the agent
   renews the broker's owner lease for the capture. When it lapses, the agent stops the capture, so a controller that
   goes, or whose network does, leaves nothing recording past its lease. Should the agent itself go, the broker's owner
   lease lapses and the broker stops the capture on its own (ADR-052). An administrator stopping the agent stops it too.
3. **The agent's bounds.** An hour and 4 GiB by default, which its administrator sets when the agent starts; a capture
   asked past them is refused, saying them. The broker's own quotas still apply beneath.
4. **A barrier, not synchronized clocks.** The start's answer carries the agent's own clock reading when it
   acknowledged the start. The controller keeps its own readings either side beside it, which bound the agent's
   reading within half the round trip: input for an investigation's alignment (M4), never proof that the clocks agree.
5. **Audit what changes a capture.** Preparations, starts and stops are audited, and stops the agent makes itself;
   status and renewals, every few seconds, are not.
6. **The broker behind an interface.** The agent's capture runs through a driver: the host's broker in InterCat, a
   stand-in in tests. No test starts a broker or records into its root. The driver's questions to the broker are held
   against a broker answering from a script.

What the broker recorded stays in its root on the agent's host. Fetching it - store-and-forward, verified against its
hashes before an investigation takes it - is the next part, and one start and stop across several agents the part after.
