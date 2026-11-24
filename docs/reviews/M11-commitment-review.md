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

Since revision 1094, as the product owner chose, the agent runs when its host's administrator starts it from an elevated
prompt and stops when done, not as an installed service: `icat agent` issues tokens, serves until Ctrl+C, and lists,
revokes and audits controllers, keeping its key, enrollments and audit in a folder only administrators may use; `icat
remote` enrolls a controller and lists and checks the agents it enrolled with. Of what is listed above as not built, the
agent's host process, what it keeps across runs and the command line for both parties are built; its installation and
enablement are not needed.

Since revision 1095 a controller records on an agent's host through its broker, one metadata capture at a time within
the agent's bounds, keeping a control lease the agent stops the capture on when it lapses, and keeps the start's
acknowledgment within half its round trip. Of what is listed above as not built, captures within control leases and
quotas stopping a capture are built; a real capture through an agent has not been run, the broker root on this
machine holding its owner's captures.

Since revision 1096 a real capture has run through an agent, on the development machine with its owner's consent: a 15-second explore capture through icat agent serve and icat remote capture on loopback, the machine in use: about 10,000 records counted while it recorded, closed at its duration with its evidence finalized - 8 journal chunks, 3.7 MiB, beside the owner's captures, no broker or trace session left - and the start acknowledged 237 ms from the controller's clock within ±242 ms, on one host whose clocks are one. It found a closed capture reading as nothing recorded, which the agent now answers with the highest
counts its host said.

Since revision 1097 the controller that started a capture fetches its evidence once the agent no longer holds it open - icat remote fetch - listed in pages and read in pieces of 60 KiB at most, each file checked against the SHA-256 its generation records, into a folder that takes its name only once every file is whole and it opens as the capture's evidence, going on where a fetch stopped; icat follow derives its session. Of what is listed above as not built, store-and-forward download
with resume, verified before an investigation takes it, is built; a real capture's evidence was fetched through an agent
on this machine: the 15-second capture revision 1096 recorded, read where its broker left it, nothing recorded anew and the machine in use - the agent's record of it written by the probe, the agent that started it having been a scratch one - came over loopback as 16 files, 3.7 MiB, in 473 ms, every file and the pointer equal to its source and the source unchanged by name, length, time and hash; a second fetch read nothing, and icat follow derived its session whole, 8 chunks and 15,302 records - more than the 10,000 or so its controller read while it recorded, the last read seconds before it closed. It found the audit reading a listing back as an error, the words it reads back kept apart from those it writes; it now reads them through the same words.

Since revision 1098 icat remote capture records on several agents at once: every agent prepared at once with its preview, every prepared agent started as one barrier - each start sent before any answer is awaited, each acknowledgment read with the controller's clock either side - each lease kept and a lost connection made again within it, and every capture still open stopped together; a host that cannot record stops only its own part and says why, and each agent's clock is read at the start and at its last answer, within the bound its round trip leaves. Of what is listed above as not built, the coordinated start and
stop with each host's acknowledgments and their uncertainty is built, and partial starts and stops are said host by
host; it is held on loopback by agents with clocks of their own, and no real capture across several hosts has run,
this machine being one host. Partial-host investigations are not built.

Since revision 1099 icat remote collect gathers a capture across several agents into an investigation in a folder of its own: each host's evidence fetched from its agent, its session derived and added as a member, and what became of every host's part kept in the investigation's file (workspace-v26 §9) - collected, not collected yet, or recorded nothing, with the clock readings its controller took - so a host that recorded nothing, or whose evidence could not be fetched yet, stays in view beside the hosts that did; run again, it goes on with what is not collected. Of what is listed above as not built, the partial-host
investigation is built; an alignment by the capture's clock readings, and the investigation window showing every
host's part, are not.

Since revision 1100 icat workspace align <workspace> <session> <reference> --barrier --drift-ppm <rate> aligns two hosts a capture across hosts collected by the clocks its controller read at its start (workspace-v27 §5): the member's host's clock read ahead of the reference's by the difference of the two readings, within both their bounds - a barrier, never proof that the clocks agree - and the anchor pairs the captures' calibration samples as a wall-clock alignment does, the member's wall clock read as the reference's, bounded too by the samples' acquisition and the stated drift from the start reading to the samples. Of what is listed above as not built, M4's alignment model used for the
per-host acknowledgments is built, from the start readings; a second anchor from the end readings is not.

Since revision 1101 icat remote rotate replaces a controller's key at every agent it enrolled with, no administrator's token needed: at each agent its current key asks for a rotation's secret, its next key enrolls with it - taking the current key's grants - and the agent revokes the current key as it admits the next; an agent not reached keeps knowing the current key, which the controller keeps beside the next and reaches it with, until every agent knows the next and the old key is retired. Of what is listed above as not built, credential rotation is
built for a controller's key; an agent's own key's rotation is not.

Since revision 1102 icat agent rotate rotates an agent's own key, which every controller pins: a next key is kept beside the agent's, and while it serves the agent tells each enrolled controller that key in its hello, over the channel its current key secures; a controller goes on where the told key answers - and no other - and takes it as the agent's, every verb that reaches an agent following it; --finish makes the next key the agent's from its next start, naming the controllers never told, which enroll again with a token. Of what is listed above as not built, credential rotation is
built for both parties' keys.

Since revision 1103 the investigation window says every host's part of a capture across hosts above its pages, in the words icat workspace show says them (R18) - collected as which session, not collected yet, or recorded nothing, each with where it stopped short and why - and its Align dialog aligns a session by the clocks a capture across their hosts read at its start, as icat workspace align --barrier does, opening on that alignment again. Of what is listed above as not built, the investigation window
showing every host's part and offering the alignment by the start readings is built.

Since revision 1104 icat remote preview asks each enrolled agent what a capture request would record on its host before anything is prepared: the agent reads the request's focus or content options and refuses them in icat profiles' words, compiles the host's own preview (capture-profile-preview-v1) from its provider schemas - starting nothing - and says whether it would prepare the request as asked, and why not; a content request is refused for a key not granted contents, then by an agent whose policy keeps none, then because its host's broker keeps content only of its own client's processes - never recorded as anything it did not ask for. Of what is listed above as not built, the remote content scope
preview is built, and so is a metadata-only policy no controller's setting upgrades: the agent's policy and the key's
grants decide, and a request names nothing that changes them. A focused or content capture recorded remotely is not.

Since revision 1105 M11's overhead is measured on this machine - 24 logical processors, Windows build 26320, in use by its owner - with icat built in Release, through InterCat.RemoteQualification (bench/results/remote-overhead-20261123T220532Z, bench/results/remote-renewals-20261123T220501Z): an agent idle five minutes, enrolled and listening, used 0.33 s of processor time - 0.11% of one core - and held a 49 MiB working set, 12 MiB of it private, 501 handles and 13 threads; a control lease's renewal took 153 µs over loopback TLS at its median and 353 µs at its 99th percentile, its two ends 258 µs of processor time together, beside the host's broker's own hold, which a stand-in replaced; and a capture's evidence - 268 files, 437 MiB, read where its broker left it and left unchanged - came over loopback at 26.5 MiB/s, the median of three fetches, the controller spending 16 ms and the agent 19 ms of processor time a MiB. A fetch is bound by its processor, not its network: each read of 60 KiB is a request of its own. Of what is listed above as not built, measured overhead is
built for an idle agent, a renewal and a fetch on one machine; across a real network it is not measured, and a fetch
that pipelines its reads would move faster than this one.

Since revision 1106 a fetch costs its agent less: each request reads the agent's enrollments once rather than up to three times, and the agent keeps the file a fetch reads open across its reads rather than opening it for every 60 KiB. Measured against revision 1105's icat in turns, under the same load, three runs each of two fetches of the same 437 MiB (bench/results/remote-fetch-before-after-20261123T231230Z), the agent's processor time fell from 23.4 to 9.3 ms a MiB and a fetch rose from 21.2 to 31.0 MiB/s, the controller's 20 ms a MiB unchanged - the machine busier than for revision 1105's own figures. A fetch's controller is still bound by its processor.

Since revision 1107 icat remote follow follows the evidence of a capture its controller started while the capture records: round after round its newest published generation is listed - an agent lists a capture still recording only to a listing that says it follows - only what the generation adds is fetched and checked against its SHA-256, and its pointer is replaced last, so the folder holds one whole generation at every round, which icat follow derives as it goes; once a listing says the capture closed, its last generation is held, what it released is let go and the folder is checked as the capture's evidence. The plan's live following, after store-and-forward, is built a
generation at a time; records streamed one by one as they are recorded are not.

Since revision 1108 M11's [release review](M11-release-review.md) finds its exit gate met for its measured scope.
