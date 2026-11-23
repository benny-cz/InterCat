# Remote agent contract, version 1

Status: M11, revision 1093 (ADR-117): enrollment, the pinned transport, hello and inventory; since revision 1094 the
agent its administrator runs with icat, and what each party keeps; since revision 1095, version 1.1, a capture on the
agent's host through its broker within a control lease; since revision 1097, version 1.2, its evidence fetched by the
controller that started it; since revision 1098, version 1.3, one capture across several agents, each agent's clock
read at every answer; since revision 1101, version 1.4, a controller's key rotated at its agents
Owner: `InterCat.Remote` (`RemoteAgentServer`, `AgentEnrollments`, `RemoteAgentConnection`, `RemoteFrameCodec`,
`AgentState`, `ControllerState`); `icat agent` and `icat remote`
Read by: a controller's `RemoteAgentConnection`; the agent's administrator, through its enrollments and audit

A remote agent is InterCat on a host whose administrator installed and enabled it to serve controllers - another
person's InterCat - that the administrator enrolled. Version 1.0 defines how the two know each other, how a controller
is enrolled, what an enrolled controller may ask, and how the agent refuses; version 1.1 adds a metadata capture on the
agent's host, version 1.2 the fetch of what it recorded, version 1.3 the agent's clock in every answer of a capture,
for one capture across several agents, and version 1.4 a controller's key's rotation. It defines no command: what a request may ask for is closed, and every later
request is added here before an agent answers it.

## 1. Parties and pins

Each party holds an ECDSA P-256 key in a self-signed certificate and is known by its **pin**: the SHA-256 of the key's
DER `SubjectPublicKeyInfo`, written as 64 lowercase hexadecimal digits. Neither party trusts a certificate authority,
a trust store, a certificate's names or its dates; no certificate is installed in any store. Two certificates of one key
have one pin; a new key is a new party.

## 2. Enrollment

1. The agent's administrator issues a **token** granting what one controller may ask (§5), lasting ten minutes unless
   the administrator says otherwise, and never more than an hour. The agent keeps the token's secret's SHA-256, its
   grants and when it lapses - never the secret.
2. The token's text is `icat-enroll-1.` and the unpadded base64url of: the version byte 1; the agent's pin, 32 bytes; a
   secret of 32 bytes from the system's random source; and a check, the first 4 bytes of the SHA-256 of the 65 bytes
   before it. It is copied whole, not typed; a token whose check does not match was cut short or altered.
3. The controller connects to the agent's endpoint expecting the token's pin (§3) and sends the secret in an `Enroll`
   request (§4.2) only once the agent's key is the one the pin names. A party between them never learns the secret.
4. The agent compares the secret's hash with every unexpired token's in full, and admits the controller's key - the
   pin of the certificate it presented - with that token's grants, consuming the token. A key already enrolled holds the
   new token's grants.
5. An unknown, used or expired secret is refused alike (`token-refused`), and a revoked key is refused whatever it
   presents (`revoked`); either refusal ends the connection.
6. The administrator revokes a key on the agent's host: its enrollment ends at once, its next request is refused, and
   no token enrolls it again. A controller whose key was revoked enrolls with a new key.
7. Since version 1.4 a controller's next key also enrolls by its current key's rotation (§4.8), no administrator's token
   needed.

## 3. Transport

TCP to the endpoint the agent's administrator configured, and TLS 1.2 or 1.3 over it. The agent presents its certificate
and requires the controller's. The controller goes on only where the agent's key has the pin it expects - the token's,
or the one it enrolled with - and otherwise stops in the handshake, before any request: a wrong host's identity is
rejected. The agent admits any key to the handshake, so a controller can enroll; what a key may then ask, its pin
decides (§4).

A frame is a 32-byte header - the magic `ICRA`, the protocol's major version 1 and minor version 4 (3 from revision
1098, whose agents rotate no key; 2 from revision 1097, whose agents answer a capture's state without their clock; 1 from revision 1095, whose agents serve no evidence;
0 before it, whose agents answer no capture), the message type,
its flags (request 1, response 2, error 4; a frame is a request or a response), the payload's length and a correlation
identifier - then the payload, at most 64 KiB, checked before anything is allocated for it. A payload is a run of
fields, each an identifier (uint16), a length (uint16, at most 60 KiB; 16 KiB before version 1.2) and its bytes; at
most 64 fields, none twice. A
reader passes over a field it does not know, so a later minor version may add fields. Integers are little-endian;
identifiers are 16 bytes, big-endian; text is UTF-8.

## 4. Messages

| Type | Name | Who may ask | Request fields | Response fields |
|---|---|---|---|---|
| 1 | Hello | any key | 1 major, 2 minor | 1 major, 2 minor, 3 enrolled; and only to an enrolled key, 4 the agent's host identity, 5 its grants (§5) |
| 2 | Enroll | any key not revoked | 1 the token's secret, 32 bytes | 1 the agent's host identity, 2 the grants now held |
| 3 | Inventory | an enrolled key | none | 1 host identity, 2 machine name, 3 operating system, 4 InterCat's version, each at most 256 bytes, 5 whether it serves a capture |
| 4 | PrepareCapture | an enrolled key holding metadata capture | 1 profile, at most 64 bytes, 2 duration in seconds (uint32), 3 journal in MiB (uint32) | 1 the preparation's identity, 2 the profile the host will record, 3 its duration, 4 its journal, 5 what it collects in the host's broker's words, at most 4 KiB |
| 5 | StartCapture | the key that prepared it | 1 the preparation, 2 the control lease in seconds (uint16) | 1 the capture's identity, 2 when the agent acknowledged the start by its own clock, 3 when the lease lapses unless renewed |
| 6 | CaptureStatus | the key that started it | 1 the capture | the capture's state |
| 7 | RenewCapture | the key that started it | 1 the capture | the lease renewed for its length again; the capture's state |
| 8 | StopCapture | the key that started it | 1 the capture | the capture stopped; its state |
| 9 | ListCapture | the key that started the capture, holding download | 1 the capture, 2 the generation listed before, 0 for the current one, 3 the first file (uint32), 4 the most files, 0 for as many as a page holds (uint32) | 1 the capture, 2 the generation, 3 its pointer's bytes, at most 1 KiB, 4 how many files it names (uint32), 5 their bytes in all, 6 the first file this page holds, 7 the page's files - each its name's length (one byte), its name, its length (int64) and its SHA-256 (32 bytes) - at most 56 KiB |
| 10 | ReadCaptureFile | the key that started the capture, holding download | 1 the capture, 2 a listed file's name, 3 the offset (int64), 4 the most bytes (uint32) | 1 the offset, 2 the bytes, at most 60 KiB, fewer only at the file's end |
| 11 | Rotate | an enrolled key | none | 1 the rotation's secret, 32 bytes, 2 when it lapses (§4.8) |
| 255 | Error | the agent alone | - | 1 code (§6), 2 a message a person reads, at most 512 bytes |

A capture's state is 1 the capture, 2 where it stands (1 starting, 2 recording, 3 stopping, 4 closed), 3 the most records
the host's broker said it admitted, 4 the most it said were lost - a broker says nothing of a closed capture's counts, so
a closed capture keeps the highest it said - 5 when the lease lapses, 6 whether a closed capture finished whole, 7 why
it closed, where the agent or its broker closed it, at most 512 bytes, and, since version 1.3, 8 when the agent answered,
by its own clock. An agent before version 1.3 writes no field 8, and says nothing of its clock. Times are Unix
milliseconds (int64).

A key not enrolled learns from hello the protocol and nothing of the host. No message names a command, a program, a
path or an address to act on: there is no remote shell, and a request of a type the agent does not answer is refused
(`unsupported`) without acting on it.

### 4.4 Captures

An agent records one capture at a time, through its host's capture broker as icat capture does (`broker-v1`), in one of
the metadata profiles `explore`, `rpc-peers`, `unix-sockets`, `com-calls`, `sync-objects`, `window-messages`, `sections`
and `sdk`, recorded live; a focused or content capture is not served remotely at version 1.3. A capture belongs to the
key that prepared it: another key is refused it as if it were not there.

- A preparation not started within 30 seconds is let go.
- The controller keeps a control lease, 10 seconds to 5 minutes by default, renewing it before it lapses. While it holds,
  the agent keeps its host's own hold on the capture - its broker's owner lease. When it lapses, the agent stops the
  capture: a controller that goes, or whose network does, leaves nothing recording past its lease.
- Should the agent itself go, its host's hold lapses and the broker stops the capture on its own; an administrator
  stopping the agent stops the capture.
- A capture that reaches its duration closes on its own, and the agent takes the next.

The start's acknowledgment is the agent's own clock reading, a control barrier the hosts' captures share: never proof
that the two hosts' clocks agree. A controller keeps its own readings either side of the start beside it, which bound
the agent's reading within half the round trip and half the millisecond the agent writes it in, for an investigation's
alignment (M4); so it does for every answer of a capture since version 1.3 (§4.6). What the host's broker recorded
stays in its root, where the key that started the capture fetches it (§4.5).

### 4.5 Fetching a capture's evidence

What a capture recorded stays in its host's broker root as the broker's evidence: a session's folder (`store-v1`) whose
pointer names a generation and whose manifest names every file the generation needs, with each one's length and
SHA-256. The agent records each capture it starts - its identity, the key that started it, where its broker keeps the
evidence, its profile and when it started (§9) - so it serves the evidence after it restarts; it remembers the newest
1,024. A broker that does not say where it records has its capture stopped at once.

The key that started a capture, holding `download`, fetches its evidence once the agent no longer holds the capture
open: stopped, closed at its duration or its lease, or left by an agent that stopped. A capture still recording is
refused as `busy`; another key's capture, or one the agent no longer remembers, is refused as `not-permitted`, as if it
were not there.

- ListCapture lists the evidence's current generation: its pointer's bytes, and every file the generation needs - its
  manifest first, then each file the manifest names - each with a name of at most 64 ASCII letters, digits, '.', '-' and
  '_' (the store's own rule for a file's name, beginning with neither '.' nor '-', ending without '.', with no '..' and no
  reserved Windows device name), its length and the SHA-256 its generation records; the manifest's is the SHA-256 of its
  bytes. A page holds as many files as fit 56 KiB, or as few as asked. A later page names the generation the first
  listed, and is refused as `busy` where the evidence names another since - its broker still finishing - so the
  controller lists it again. A fetch's first listing is audited; its pages and reads are not.
- ReadCaptureFile reads a listed file, at most 60 KiB from an offset within it; a name the listing does not hold is
  refused as `not-permitted`. The agent opens the evidence to view it and reads it, never writing it, and never says
  where on its host it is.

The controller fetches into a folder of its own that is not there or is empty. It writes each file into `<folder>.partial`
beside it, marked with the capture and the agent; counts a file only once its bytes hash to its listed SHA-256, fetching
one that differs once more and refusing it if it differs again; writes the pointer last; opens the folder as the
capture's evidence - every file of the generation its pointer names hashed against its manifest, the session the
capture's own - and only then gives it the folder's name, in one rename. So the folder holds the capture's evidence
whole, or is not there. Run again, the fetch goes on: a file held whole already, by its content, is kept and not read
again, and one read part way goes on from where it stopped; a `.partial` folder of another capture or agent is not
taken over. icat follow derives a session from the folder as from any capture's evidence.

### 4.6 One capture across several agents

A controller records on several agents at once as one capture across them: each agent's part is its own capture on its
own host (§4.4), and nothing on the wire changes but what a capture's state says since version 1.3. The controller
prepares every agent at once and shows each host's preview - each host's broker may record its own subset of the
profile - then starts every agent prepared as one barrier: each start is sent before any answer is awaited, and each
acknowledgment is read with the controller's clock either side. It keeps each capture's lease, renewing it every third
of its length; a connection lost is made again, with the same key, for as long as the lease the agent holds has not
lapsed, and the capture records on. When the controller stops - Ctrl+C - it stops every capture still open together,
reading each stop's answer as the start's.

A host that cannot be reached, refuses or fails stops only its own part: the others record on, and each host says what
became of its part - not reached, not prepared, not started, lost or not stopped - in the agent's or the connection's
own words. A start whose answer never came leaves the capture, where the agent made it, to its lease. A host whose agent
is not reached again before the lease lapses is lost, and its agent stops that capture on its own.

Each reading of an agent's clock - the start's acknowledgment, and the agent's last answer of the capture, its stop's
where the controller stopped it - is kept with the controller's two readings either side. The agent's clock reads ahead
of the controller's by its reading less the round trip's middle, within half the round trip and half the millisecond
the agent writes its reading in; a delay on the way widens that bound and never moves the reading outside it. One
host's clock against another's is the difference of their readings, within both bounds added: a barrier, never proof
that two hosts' clocks agree, kept for an investigation's alignment (M4), the start's and the end's two anchors apart:
an investigation aligns two hosts of a capture by its start readings (`workspace-v27` §5).

### 4.7 Collecting a capture across hosts into an investigation

A capture across several agents is named by an identity its controller gives it, `coordinationId` in what
`icat remote capture --json` writes. `icat remote collect` takes that file and a folder, and gathers the capture into
an investigation there - `investigation.icat-workspace` unless `--workspace` names another file - host by host:

- A host that recorded is collected: its evidence fetched from its agent (§4.5) into `<folder>/<host>-<capture>/evidence`,
  its session derived from it into `session` beside it, as `icat follow --once` derives one, and added as a member. Its
  agent is the one the file names, reached at the address and with the key this controller enrolled it by.
- A host whose agent cannot be reached, refuses, or whose evidence is not fetched whole is not collected yet; a host that
  recorded nothing stays so.
- What became of every host's part is recorded in the investigation (`workspace-v27` §9), in words that name no path or
  address, since an investigation may be shared: the reason the controller has for a host not collected is said in the
  command's own output.

Run again, it goes on: a host collected is not fetched again, a fetch goes on where it stopped, and a new revision is
recorded only where anything changed. It exits 0 when every host is collected, 1 when some are, and 3 when none is.

### 4.8 A controller's key rotated

An enrolled, unrevoked key asks its agent for its rotation, `Rotate`: the agent answers with a secret from the system's
random source, keeping its hash as it keeps a token's, with the asking key's own grants, lapsing in ten minutes and
consumed once. The controller's next key enrolls with that secret as with a token (§2), over its own connection - its
TLS handshake proving the key is held. As it admits the next key, the agent revokes the one the rotation was asked by:
that key's enrollment ends, and no token enrolls it again. The secret admits no key but another than the one it rotates,
and only while that one stands: revoked meanwhile, its rotation goes with it. The audit records the rotation asked, and
the next key's enrollment says the key it replaces.

`icat remote rotate` rotates the controller's key at every agent it enrolled with. Its next key is kept beside its
current one, `controller-identity-next-v1.bin`, until every agent knows the next; each agent kept records the key it
knows the controller by, and the controller reaches each agent with that key. An agent not reached keeps knowing the
current key; run again, the rotation goes on with it - and with an agent that admitted the next key before the
controller kept that, which says so to the next key's hello. Once every agent knows the next key, it becomes the
controller's key and the current one is gone. An agent's own key's rotation is not defined at this version.

## 5. Grants and local policy

A token grants, each apart: `metadata-capture` (prepare, start, follow and stop a capture of metadata), `contents` (a
capture that keeps contents, never implied by metadata capture), `download` (fetch the evidence of a capture it started, §4.5) and
`collectors` (install an optional collector). The agent's local policy is authoritative: a controller may do what it
was granted and the policy allows. By default the policy allows metadata capture and download; contents only where the
agent's administrator says so; collectors never, whatever is granted - installing one is an administrative workflow of
its own. Hello and enroll answer with the grants the policy leaves.

The agent bounds what it records: by default a capture of an hour and a journal of 4 GiB at most, which its
administrator may narrow or widen when the agent starts. A capture asked past them is refused, saying the bounds.

## 6. Refusals

| Code | Words | When | Then |
|---|---|---|---|
| 1 | `malformed` | a frame or a field version 1 would not write | the connection ends |
| 2 | `unsupported` | a message type or version the agent does not answer | the connection goes on |
| 3 | `not-enrolled` | a request from a key not enrolled | the connection goes on |
| 4 | `token-refused` | an unknown, used or expired secret | the connection ends |
| 5 | `revoked` | any request from a revoked key but hello | an enrollment's refusal ends the connection |
| 6 | `not-permitted` | a grant not held, one the local policy forbids, a capture past the agent's bounds, a preparation or capture of another key or none, or a file the listing does not name | the connection goes on |
| 7 | `busy` | the agent holds a capture already, a capture asked for is still recording, or its evidence names another generation than a page asked; a connection past its limit is still closed unanswered (§8) | the connection goes on |
| 8 | `failed` | the agent's host refused or failed what was asked - its broker, or the capture - in its own words | the connection goes on |

## 7. Audit

The agent records each request: when, the controller's pin, the message and its outcome. A capture's status and
renewals, which come every few seconds, are not recorded; its preparation, start and stop are, and so is a stop the agent
makes itself, when the lease lapses or the agent stops. A fetch's first listing is recorded - the capture, its files and
their bytes - and its later pages and its reads are not. A key's rotation is recorded as asked, and the next key's
enrollment names the key it replaced, revoked. An entry never holds an address, a port, a path or a command
line (P16); a controller is named by its pin alone.

## 8. Limits

At most 8 connections at once - a connection past them is closed unanswered, before TLS - a handshake within 10
seconds, and a connection idle 60 seconds closed. One request is answered at a time on a connection.

## 9. What each party keeps

An agent keeps four things in its folder: `remote-agent` in InterCat's root under the machine's program data, or the
folder its administrator names.

- Its key, `agent-identity-v1.bin`: the PKCS #12 of its certificate and key, protected by the system's data protection
  for the machine, with InterCat's own entropy.
- Its enrollments, `enrollments-v1.json` (`remote-agent-enrollments-v1`): each unconsumed token's secret's SHA-256 in
  hexadecimal digits, with its grants and when it lapses - and, for a rotation, `rotatesFrom`, the key it rotates; each
  enrolled controller's pin, with its grants and when it enrolled - and `rotatedFrom`, the key it replaced, where it
  enrolled by a rotation; and each revoked pin. Every process holds `enrollments-v1.lock` while it reads or writes them, so a token
  issued in one process is admitted by an agent serving in another, and a revocation reaches it at its next request. The
  file is replaced whole, never edited in place. One that does not read as InterCat wrote it is refused, not reset: a
  reset would forget whom the administrator revoked.
- Its audit, `audit-v1.log`: a JSON line a request - `utc`, `controller`, `action`, `outcome` (§7). Past 4 MiB it
  becomes `audit-v1.previous.log`, replacing the one before.
- The captures it started, `captures-v1.json` (`remote-agent-captures-v1`): each capture's identity, the pin of the key
  that started it, the folder where its broker keeps the evidence, its profile and when it started; the newest 1,024,
  read and written under `captures-v1.lock` and replaced whole, as its enrollments are. One that does not read as
  InterCat wrote it is refused, not reset: the agent then starts no capture - one it could not record is stopped at once
  - and serves no evidence, until its administrator restores or removes the file.

The agent's folder admits SYSTEM and the machine's administrators alone, is owned by the administrators and inherits
nothing from its parent. Where it does not exist, it is made so; its parent must exist, as InterCat's installation makes
it. A folder that admits another account, inherits its parent's access or has another owner is refused, not repaired:
someone else could have written what it holds. Only a process running as an administrator opens it.

A controller keeps two things in its folder: `remote` in InterCat's folder under its user's local application data, or
the folder its user names.

- Its key, `controller-identity-v1.bin`, protected for its user, and while a rotation is under way its next key,
  `controller-identity-next-v1.bin`, protected alike (§4.8).
- The agents it enrolled with, `agents-v1.json` (`remote-controller-agents-v1`): each agent's endpoint, pin, host
  identity, machine name, grants and when it enrolled, one entry per key, and `controller`, the pin of the controller's
  key that agent knows where a rotation moved it - absent for the controller's current key.

The controller's folder admits SYSTEM and its user alone.

## 10. icat

| Command | Run by | What it does | Its JSON |
|---|---|---|---|
| `icat agent token [--grant <grants>] [--lifetime <minutes>]` | the agent's administrator, elevated | issues a token granting `metadata-capture,download` unless said otherwise, lapsing in 10 minutes unless said otherwise (1 to 60) | `remote-agent-token-v1`: `token`, `agent`, `grants`, `expiresUtc` |
| `icat agent serve --listen <address:port> [--allow-contents] [--max-minutes <minutes>] [--max-journal-mib <mib>] [--broker <path>]` | the agent's administrator, elevated | serves enrolled controllers until Ctrl+C; the policy allows metadata capture and download, and contents only with `--allow-contents`; captures through the host's broker - installed beside icat, or named in a development build - within 60 minutes and 4,096 MiB unless said otherwise | - |
| `icat agent controllers` | the agent's administrator, elevated | lists the enrolled and revoked keys and how many tokens are unused | `remote-agent-controllers-v1`: `controllers` (`pin`, `grants`, `enrolledUtc`), `revoked`, `pendingTokens` |
| `icat agent revoke <key>` | the agent's administrator, elevated | revokes the one enrolled key whose pin begins with the 16 or more digits given | - |
| `icat agent audit [--last <count>]` | the agent's administrator, elevated | lists the newest requests, 50 unless said otherwise | `remote-agent-audit-v1`: `entries` (`utc`, `controller`, `action`, `outcome`) |
| `icat remote enroll <address:port> <token>` | a controller's user | enrolls with the agent the token names, then keeps the agent | `remote-enrollment-v1`: `endpoint`, `agent`, `hostId`, `machineName`, `grants`, `controller` |
| `icat remote hosts [--check]` | a controller's user | lists the agents enrolled with; `--check` asks each what it allows now | `remote-hosts-v1`: `agents` (`endpoint`, `pin`, `hostId`, `machineName`, `grants`, `enrolledUtc`, `check`) |
| `icat remote capture <address:port>... [--profile <profile>] [--duration <seconds>] [--max-journal-mib <mib>] [--lease <seconds>]` | a controller's user | records on one enrolled agent's host or several at once (§4.6) - explore, 60 seconds and 1,024 MiB unless said otherwise - renewing a 30-second lease every third of it until the capture closes or Ctrl+C stops it | one agent: `remote-capture-v1`: `endpoint`, `agent`, `hostId`, `machineName`, `captureId`, `profile`, `seconds`, `journalMebibytes`, `start` (`sentUtc`, `acknowledgedUtc`, `receivedUtc`, `roundTripMilliseconds`, `agentAheadMilliseconds`, `withinMilliseconds`), `end` (the same, `answeredUtc` for `acknowledgedUtc`; null from an agent before 1.3), `state`, `finalized`, `records`, `lost`, `reason`, `reconnections`, `failure` (`stage`, `reason`); several: `remote-coordinated-capture-v1`: `coordinationId`, `profile`, `seconds`, `journalMebibytes`, `startedHosts`, `barrier` (`sentUtc`, `receivedUtc`), `reference`, `hosts` (`endpoint`, `agent`, `hostId`, `machineName`, `captureId`, `profile`, `start`, `end`, `clock` against the reference's - `aheadMilliseconds`, `withinMilliseconds`, `endAheadMilliseconds`, `endWithinMilliseconds` - `state`, `finalized`, `records`, `lost`, `reason`, `stoppedByController`, `reconnections`, `failure`) |
| `icat remote rotate` | a controller's user | rotates this controller's key at every agent it enrolled with (§4.8); run again, goes on with the agents not reached | `remote-rotation-v1`: `controller` (the next key's pin), `previous`, `retired`, `agents` (`endpoint`, `pin`, `machineName`, `rotated`, `reason`) |
| `icat remote collect <capture.json> <folder> [--workspace <file>]` | a controller's user | gathers a capture across several agents into an investigation in a folder (§4.7): each host's evidence fetched, its session derived and added, every host's part recorded; run again, goes on | `remote-collection-v1`: `workspace`, `coordinationId`, `revision` (null where nothing changed), `collectedHosts`, `hosts` (`machineName`, `hostId`, `captureId`, `sessionId`, `part`, `stage`, `reason`, `evidencePath`, `sessionPath`, `fetchedBytes`) |
| `icat remote fetch <address:port> <capture> <folder>` | a controller's user | fetches the evidence of a capture it started on an agent's host, once it closed, into a folder of its own (§4.5); run again, goes on where it stopped; `icat follow <folder> <session-dir> --once` derives its session | `remote-fetch-v1`: `endpoint`, `agent`, `hostId`, `machineName`, `captureId`, `evidencePath`, `generation`, `files`, `bytes`, `fetchedBytes`, `keptBytes`, `alreadyFetched`, `elapsedMilliseconds` |

Every `icat agent` command takes `--root <folder>` for the agent's folder, and every `icat remote` command takes
`--state <folder>` for the controller's. Each lister takes `--json`.

A check reads `enrolled: <grants>`, `not enrolled`, `revoked`, `refused: <words>`, `another key answers there` or
`unreachable`.

An agent is given by its address, never by a name: neither party looks a name up. The agent's host identity is the one
its captures record (M4), so a controller can tell which host's sessions an agent serves.

## 11. Not defined at this version

The agent as a service of its host; an agent's own key's rotation; a focused or content capture remotely; a capture's evidence
streamed while it records; a second anchor from a capture across hosts' end readings. Each is
added to this contract before an agent answers it.
