# M11 commitment review — remote capture coordination

Date: 2026-11-23, at plan revision 1093
Scope reviewed: M11 as the plan states it, and its first part, the remote agent's enrollment and pinned transport
(`contracts/remote-agent-v1.md`, ADR-117)
Method: §15.2 asks, before a post-v1 milestone is committed for shipment, for the user problem, the exact new evidence
obtainable, the supported environments, the expected overhead, the deployment burden and the exit fixtures. Each is
answered below for what M11 is to ship, and what revision 1093 builds of it; what is not built is listed as not built.
The product owner chose M11 as the next milestone on 2026-11-22.

## The user problem

An investigation across hosts needs a person at each host: to start a capture there at about the right moment, to stop
it, to package the session and to carry the folder over by hand before the investigation can take it (M4). Starting
several hosts together is a matter of timing by hand, a capture left running runs to its bound, and which host's
session is missing or late is found only when someone looks.

## The new evidence obtainable

Nothing a host records changes: each host's broker records what it records today. What M11 adds is the coordination
around it: captures started and stopped together under one control barrier, each host's acknowledgment of the barrier
with its own clock reading and the round trip that bounds it - kept for M4's alignment, a barrier never taken for
synchronized clocks - and each session brought to the investigation verified against its own hashes, a host that did
not start, stop or deliver shown as such.

## Supported environments

Hosts on the Windows builds InterCat supports, each reachable from the controller over TCP, each running InterCat's
remote agent installed and enabled by its own administrator; TLS 1.2 or 1.3 through the system's SChannel. No domain,
no certificate authority, no cloud service: a controller and its agents know each other by their keys' pins.

## Expected overhead

An idle agent holds a listening socket and does nothing else. A coordinated capture costs what the host's broker costs
for the same profile (IC-010a), and a lease renewal every few seconds. A download costs the session's bytes over the
network once; a resumed one, the bytes not yet verified. Each figure is to be measured and published, as M11's exit
tests ask.

## Deployment burden

On each host, an administrator installs and enables the agent, opens its port in the host's firewall, and enrolls each
controller once with a token. On the controller, nothing beyond InterCat. Remote installation of the agent, and of any
optional collector, is not part of M11: it is an administrative workflow of its own.

## Exit fixtures

Built in revision 1093, on loopback, each party's key made for the test and installed in no store:
- a token enrolls one controller once, and a second use, an expired token and a secret never issued are refused alike;
- a controller that reaches another agent than its token names stops in the handshake, so the secret never leaves it;
- a key not enrolled is refused all but hello and enrollment, and a revoked key everything, a new token included;
- a message the agent does not answer is refused, and a frame version 1 would not write ends the connection;
- the agent's audit names controllers by pin, never by address.

Not built: the agent's host process, its installation and enablement; keys, enrollments and the audit kept across
restarts; a key's rotation; captures on the agent's host through its broker within control leases, and a capture that
outlives its controller only within the agent's policy; quotas stopping a capture safely; coordinated start and stop
with each host's acknowledgments; store-and-forward download with resume, verified before an investigation takes it;
partial-host investigations; a remote content scope preview, and a metadata-only policy that no viewer setting upgrades;
the command line; measured overhead. One host cannot split an exchange between two hosts, so until a second machine is
available, two agents on one host stand for two hosts, and a clock offset is tested synthetically.
