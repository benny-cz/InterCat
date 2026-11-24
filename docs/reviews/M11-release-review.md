# M11 release review — remote capture coordination

Date: 2026-11-24, at plan revision 1108
Scope reviewed: M11 as revisions 1093 to 1107 built it: a remote agent enrolled by its administrator's token and speaking
only TLS pinned to each party's key (`contracts/remote-agent-v1.md`, version 1.7, ADR-117), `icat agent` and
`icat remote`, and an investigation's captures across hosts (`contracts/workspace-v27.md` §9)
Method: M11's [commitment review](M11-commitment-review.md) answered §15.2's questions for what M11 is to ship. This
review checks the plan's M11 deliverables and exit tests against what is built and the tests that hold it; what is not
built is listed as not built.

## Deliverables

| Deliverable | Built | Since |
|---|---|---|
| Host inventory with capability and health | Hello, inventory and grants (§4); `icat remote hosts --check` asks each agent what it allows now | Revisions 1093, 1094 |
| Coordinated start and stop | `icat remote capture` across several agents: every agent prepared, every one prepared started as one barrier, each acknowledgment read with the controller's clock either side, every capture still open stopped together (§4.6) | Revision 1098 |
| Reliable session download and resume | `icat remote fetch`: listed in pages, read in pieces, every file checked against its SHA-256, the folder published whole or not at all, going on where it stopped (§4.5); `icat remote follow`, the evidence followed a generation at a time while the capture records (§4.11) | Revisions 1097, 1107 |
| Partial-host workspace | `icat remote collect`: every host's part kept in the investigation - collected, not collected yet, or recorded nothing - and the investigation window saying each (`workspace-v27` §9) | Revisions 1099, 1103 |
| Short-lived control leases | A capture held by its controller's lease, renewed every third of it; the agent stops it when the lease lapses (§4.4) | Revision 1095 |
| Credential rotation and revocation | A key revoked on the agent's host; a controller's key rotated at its agents; an agent's own key rotated, its controllers following it (§2, §4.8, §4.9) | Revisions 1093, 1101, 1102 |
| Remote content scope preview | `icat remote preview`: each host's own preview of a request, compiled there and starting nothing, and why the agent would not record it as asked (§4.10) | Revision 1104 |
| Command-line automation | Every `icat agent` and `icat remote` verb prints a contract of its own with `--json` (§10) | Revisions 1094 to 1107 |

The plan keeps a remote driver's installation out of M11, as an administrative workflow of its own: none is built.

## M11's exit tests

| Exit test | Verdict | Evidence |
|---|---|---|
| Expired and revoked credentials denied | Met | "§15: a token enrolls one controller once and lapses after its lifetime - a second use, an expired token and a secret never issued are refused alike, and a refused enrollment ends the connection"; "§15: a key not enrolled is refused all but hello and enrollment, and a revoked key everything - a new token included - while hello tells it nothing of the host"; "§15: a rotation's secret enrolls one key once, within ten minutes, never the key it rotates, never after that key was revoked, and a key not enrolled has no rotation" |
| Wrong host identity rejected | Met | "§15: a controller that reaches another agent than its token names stops in the handshake, so the token's secret never leaves it and the token stays unused"; "§15: a controller reaches an agent under the next key it was told, in its known key's place, and any other key answering there is refused" |
| No remote arbitrary shell | Met | The message set is closed and names no command, program, path or address (§4); "§15: an agent refuses a message it does not answer and keeps the connection, and a frame not version 1's - another magic, a payload past the limit, a field cut short - it refuses saying why, then ends the connection" |
| A capture survives its controller's disconnection only within configured policy | Met | "§15: while its controller's lease holds the agent renews its host's hold on the capture, and once the lease lapses it stops the capture and says why"; "§15: a controller whose connections are lost makes them again while each capture's lease holds, so its captures record on, and stopping stops every host together, each stop's answer read across it"; "§15: an agent the controller cannot reach again before its lease lapses is said to be lost, and the agent stops that capture on its own" |
| A quota stops a capture safely | Met | "§15: an agent refuses a capture without the grant, past its bounds, while it holds another, or that its host refuses - each saying why"; "§15: a capture that ends on its own frees the agent for the next, and an agent that stops stops the capture it holds"; the host's broker stops at its journal's allowance, as M5 holds it |
| A partial host failure does not erase other captures | Met | "§15: a host that cannot be reached or refuses keeps no other host from recording, and each says what became of its part"; `RemoteCaptureCommandTests.IcatRemoteCollectGathersAnInvestigation`: every host's part recorded, a host that recorded nothing kept in view |
| A transferred package matches its source | Met | `RemoteFetchTests.AControllerFetchesTheEvidenceOfItsCapture`; "§15: a fetch takes no name a listing should not hold, publishes nothing whose bytes differ from its listing or that its caller's check refuses, and writes over no folder holding something else"; `RemoteCaptureCommandTests.IcatRemoteFetchBringsTheEvidenceToDeriveItsSession`; revision 1097 fetched a real capture through an agent byte for byte |
| A clock offset is not mistaken for network latency | Met on one host | `RemoteCoordinationTests.OneStartAcrossSeveralAgentsReadsEachClock`: agents whose clocks read 5 s ahead and 3 s behind are read where they are, within the bound their round trips leave; revision 1100 aligns two hosts' sessions by those readings, the offset corrected for. Two agents on one host stand for two hosts. |
| A remote metadata-only policy cannot be upgraded to contents by a local viewer setting | Met | An agent records metadata profiles alone (§4.4), and a request carries nothing that widens its policy: "§15: a content capture previewed is refused for a key not granted contents, then by an agent whose policy keeps none, then in its host's own words - each with the host's preview, and none prepared" |

## Supported environments, overhead and deployment

Hosts on the Windows builds InterCat supports, each reachable from its controller over TCP, each running `icat agent
serve` from an elevated prompt - its administrator's own choice at revision 1094, rather than a service - with TLS 1.2 or
1.3 through the system's SChannel; no domain, certificate authority or cloud service. M11's overhead was measured on one
machine in use (revisions 1105 and 1106, `bench/results/remote-overhead-20261123T220532Z`,
`remote-renewals-20261123T220501Z`, `remote-fetch-before-after-20261123T231230Z`): an agent idle five minutes used 0.11%
of one core and a 49 MiB working set; a lease's renewal took 153 µs over loopback; and a fetch, since revision 1106,
costs its agent 9.3 ms of processor time a MiB, bound by its processor rather than its network. On each host an
administrator runs the agent, opens its port and issues each controller a token once; a controller needs nothing beyond
InterCat.

## Limits

- One host cannot split an exchange between two hosts (M4's open item): a real capture across a second machine has not
  run, and a clock offset is held synthetically, by agents with clocks of their own. The overhead across a real network
  is not measured.
- A focused or content capture is previewed remotely, never recorded: its host's broker keeps content only of its own
  client's processes, and recording another host's processes' content is a decision of its own (`remote-agent-v1`
  §4.10, ADR-117's ninth addendum).
- A capture's evidence is followed a generation at a time, never record by record (§4.11).
- Two hosts align by the clocks a capture read at its start; a second anchor from its end readings, which they are kept
  for, is not built (`workspace-v27` §10).

## Verdict

M11's exit gate is met for its measured scope. Every deliverable of plan M11 is built on the command line, and the
investigation window shows a capture across hosts and aligns by it; every exit test holds by the tests named above, the
clock offset on one host whose agents keep clocks of their own. What a second machine would add - an exchange split
across two hosts, and the overhead across a real network - stays open with M4's two-host exchange. The other limits are
limits of what M11 claims.
