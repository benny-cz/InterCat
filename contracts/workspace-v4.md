# Workspace contract, version 4

Status: M4, revision 260 (ADR-038 to ADR-041); versions 1, 2 and 3 were revisions 253, 254 and 256's
Owner: `InterCat.Application` (`InvestigationWorkspace`)
Produced by: `icat workspace new | add | relink | alias | align | join`
Read by: `icat workspace show | compare | correlate`

A workspace is an investigation over several separately valid sessions (§8.4). It is one JSON file, by convention named
`*.icat-workspace`, that references its members by identity and never changes them. Version 2 added its time: one
member's clock, to which a person aligns the others (§5). Version 3 aligns by what captures record too: one boot's
counter, exactly, or their wall clocks (§5). Version 4 keeps a person's decisions about candidate joins (§6). A
`workspace-v1` file - members and host names, no time - is read as one without alignments, a `workspace-v2` file as one
with manual alignments only, and a `workspace-v3` file as one without join decisions; each is written as version 4.

## 1. The file

| Field | Meaning |
|---|---|
| `contract` | `"workspace-v4"` (`"workspace-v1"`, `"workspace-v2"` and `"workspace-v3"` are read) |
| `workspaceId` | A random identity of this workspace |
| `createdUtc`, `updatedUtc` | When it was made and last written |
| `members` | Its sessions, in the order they were added (§2) |
| `hostAliases` | A person's names for its members' hosts: `{ hostId, alias }`, one name per host and one host per name, ignoring case |
| `timeReference` | The member whose session clock is the workspace's time; null while no member is aligned (§5) |
| `alignments` | Every alignment revision, in the order recorded (§5) |
| `joins` | Every join decision revision, in the order recorded (§6) |

A workspace is written whole to a temporary file beside it and moved into place, so a reader sees the old file or the
new one, and only over the text it was read from: a change made meanwhile is refused, never written over. It is kept
beside its sessions, never in a session's directory. A file is refused whole when it has another contract, a field this
version does not define (so a later version's file is never rewritten without it), a member without its identities, two
members of one session or one capture, a name that is empty, doubled or given to two hosts, or a time that contradicts
itself (§5).

## 2. A member

| Field | Meaning |
|---|---|
| `sessionId` | The session's id (`store-v1`) |
| `captureId` | The capture its journal records (`journal-v1`'s header): what makes two sessions one capture |
| `path` | Where it was last found: relative to the workspace's folder when under it, else absolute; `/` separates |
| `generation`, `manifestDigest` | The generation selected, and that manifest's digest |
| `hostId`, `clockId`, `captureEpochNativeTicks` | Its source clock's host, clock and capture epoch |
| `addedUtc` | When it was added |

Adding a session whose `captureId` or `sessionId` a member already has is refused (ADR-038 decision 3). A store's source
identity is not a capture identity - a broker capture's is its plan's digest, shared by every capture under that plan -
so it is not recorded. An import's capture identity derives from the file's content, so a file imported twice is one
capture; a redacted package mints its own, so it is not recognized as its original's.

## 3. Resolution

Showing a workspace resolves each member against its path, opening the session there as a viewer does - its pointer,
its manifest's digest, every dependency's presence and length, and its journal's header - and writing nothing:

| State | Meaning | Holds its capture? |
|---|---|---|
| `Present` | The path holds the member's session, at the selected generation | yes |
| `Advanced` | The member's session has published a newer generation since it was selected | yes, at another generation |
| `Replaced` | The member's session is there at an older generation, or one derived apart from the selected one | yes, at another generation |
| `Missing` | Nothing is at the path | no: an unresolved reference, kept and shown |
| `Different` | Another session, or another capture, is at the path | no |
| `Unreadable` | What is at the path cannot be opened as a session; the reason is stated | no |

A session keeps no older generation to read, so an `Advanced` or `Replaced` member is said to be one, and its generation
is selected only by a relink. `relink` points a member at a path only when the session there has the member's
`sessionId` and `captureId`, and selects the generation found there; relinking to the member's own path selects what is
there. A member is named by its `sessionId` or a unique leading part of it.

`icat workspace show --json` prints `workspace-resolution-v4`: the file's identity and times, each member's fields with
its `fullPath`, `state`, `currentGeneration` (null when no session is there), `reason` (null when present), `host` (its
name, when given) and `alignment` (the revision in force, or null), the hosts with their members, the `timeReference`
and every alignment and join decision revision, and caveats. It exits 0 when every member is present and 1 otherwise.

## 4. Hosts

A member's host is its source clock's `hostId`: for a live capture, derived from its installation's machine GUID together
with its machine's name and build (before revision 253, from the name and build alone); for an import, from its file's
content; for a redacted package, minted. Members with equal `hostId`s are grouped as one host identity, which is evidence
of one host and never proof - an exact clone shares its original's - and equal names or addresses make no two identities
one (§8.3, R22, P6). A name is a person's, for people; it changes no identity, and one name never names two host
identities, which would read as one host. A host is named by its identity, a unique leading part of it, or its current
name.

## 5. Time

The workspace's time is its time reference's session time, in nanoseconds: that member's instants are its own, exactly.
Another member has workspace time only through its alignment in force - its latest revision, when that is not a
withdrawal - and every alignment in force is to the time reference (ADR-039).

| Alignment field | Meaning |
|---|---|
| `revision` | A positive number, unique in the file and increasing in the order recorded |
| `sessionId` | The member aligned |
| `mode` | `Manual`: a person's statement; `SameBoot`: one boot's counter; `WallClock`: the captures' recorded wall clocks; `Withdrawn`: the member is not aligned from this revision |
| `referenceSessionId` | The member aligned to: the time reference while the revision is in force; null for a withdrawal |
| `sessionNanoseconds`, `referenceNanoseconds` | The anchor: the member's instant, and the same instant in the reference's session time |
| `withinNanoseconds` | The bound on the anchor, a half-width: the person's, the rounding of one counter, or the sum of the wall-clock bounds |
| `driftPartsPerMillion` | The person's bound on how fast the two clocks drift apart; null when not stated; 0 for one boot's counter |
| `bootToken` | `SameBoot` only: the boot both captures recorded |
| `synchronizationNanoseconds` | `WallClock` only: the person's bound on how far apart the two wall clocks read at one instant |
| `acquisitionNanoseconds` | `WallClock` only: the two anchoring samples' acquisition bounds, added |
| `gapNanoseconds` | `WallClock` only: how far apart in wall-clock time the two anchoring samples were taken |
| `note` | The person's words, when given |
| `recordedUtc` | When it was recorded |

A manual alignment maps the member's instant `t` to `t + referenceNanoseconds - sessionNanoseconds`, with the uncertainty
`withinNanoseconds + driftPartsPerMillion * |t - sessionNanoseconds| / 10^6`: both are bounds, so they add (§8.2). With
no drift stated, the drift is unknown, and so is every uncertainty away from the anchor itself. The first alignment makes
its reference the workspace's time; withdrawing the last alignment in force leaves the workspace without one, so the
next may choose another. Every revision is kept.

A same-boot alignment is made only when both captures recorded one `bootToken` in their clock calibrations
(`contracts/clock-calibration-v1.md`), with one host, encoding and rate: they read one counter, so the member's instant 0
is the reference's `(memberEpoch - referenceEpoch) * 10^9 / rate`, with no drift, exactly when a tick is a whole number
of nanoseconds and otherwise within 2 ns of rounding. A capture with no calibration or no boot token, or of another
boot, is refused.

A wall-clock alignment is anchored on the pair of samples, one of each capture's calibration, taken closest in wall-clock
time: the member's sample instant is the reference's sample instant plus the difference of their wall-clock readings.
Its bound adds the wall clocks' agreement the person states - no sample can measure it - the two samples' acquisition,
and the stated drift over the time between them; away from the anchor the drift grows as a manual alignment's does.
Both a synchronization bound and a drift bound are required.

`icat workspace compare` places two instants, each written `<session>@<seconds>` in its own session time, and prints
`workspace-comparison-v1`: each instant's workspace time and half-width, or its `gap` (`NoTimeReference`, `NotAligned`,
`DriftUnknown`) and distance from its anchor; the `order`; the difference; the pair's half-width; and a statement. Two
instants of one member are ordered exactly, on one clock. Otherwise the pair's uncertainty is
`sqrt(rA^2 + rB^2) + sA + sB` over each side's random part `r` and bound part `s`, and the order is `Before` or `After`
only when the instants are further apart than it, `Ambiguous` when they are not, and `Unknown` - its difference withheld
- when either instant has no workspace time or an unknown uncertainty (§8.2, R3, R21). A stated bound is written rounded
up at the precision written, so it never reads smaller than it is.

A file's time contradicts itself when a `workspace-v1` file holds any; its reference is no member; a revision is not a
unique positive number; a manual revision names no member, aligns a member to itself or to no member, or lacks its
anchor or a non-negative bound, or states a drift that is no non-negative rate; a withdrawal states an anchor; or an
alignment in force is to a member other than the reference, or aligns the reference itself. A `workspace-v2` file holds
only manual alignments and withdrawals. A same-boot revision names a boot and states a drift of 0; a wall-clock revision
states its agreement, acquisition, gap and drift, and a bound no narrower than its agreement and acquisition; no other
revision states any of these.

## 6. Candidate joins

`icat workspace correlate` proposes candidate joins between members (§8.3, ADR-041), under the rule
`cross-capture-connection-candidate-v1`. Each member that holds its capture is read for its one-sided connections - the
TCP connections and UDP flows whose other end its own capture holds no record of (`relations-v1` §5b) - and a connection
of one member is a candidate with a connection of another when:

- they are of one protocol, and the one's local endpoint is the other's remote endpoint and the other way round;
- neither endpoint is a loopback address, unless both members were recorded on one host identity;
- their lifetimes - each from its first record to its last - overlap once each end is widened by its uncertainty in
  the investigation's time (§5), or cannot be compared because an end has no workspace time or no known uncertainty.

A mirrored pair whose lifetimes lie apart beyond their uncertainty is not proposed, and is counted. A candidate is never
an established join: it states its evidence - the mirrored endpoints, whether the lifetimes overlap or cannot be
compared and why, and the bytes each side measured of each direction, the same or not - and how many other candidates
either connection has, so a connection with two is said to be ambiguous. `--json` prints `workspace-correlation-v2`: the
rule, each candidate's two ends (session, key, protocol, endpoints, process, lifetime and bytes), its timing, its
alternatives, the decision in force and its evidence; the mirrored pairs not proposed and the loopback pairs of two
hosts, counted; the members not compared and why; the decisions in force whose pair is no candidate now, and why; and
caveats. Nothing is joined by time alone, by an address alone or by a name.

A person decides a candidate with `icat workspace join <n> --accept | --reject | --withdraw`, `<n>` its number in
`correlate`'s list. Each decision is a revision of the file, kept when a later one replaces or withdraws it:

| Join field | Meaning |
|---|---|
| `revision` | A positive number, unique among joins and increasing in the order recorded |
| `decision` | `Accepted`: one connection, by a person; `Rejected`: not one; `Withdrawn`: undecided from this revision |
| `first`, `second` | The two ends: `{ sessionId, key }`, each a member's one-sided connection's stable key |
| `decidedUnder` | The alignment revision in force for each of the two sessions when decided, 0 for none; empty for a withdrawal |
| `note`, `recordedUtc` | The person's words, and when |

A pair's decision in force is its latest revision, in either order of its ends, unless it withdraws. An accepted join is
a person's and is never evidence: a candidate says it was accepted or rejected by a person, and, when either session's
alignment has changed since the decision - revised, withdrawn or made - that the decision was made under alignments
since changed, to review (§8.3). A decision in force whose pair is no candidate now is said, never dropped. A file whose
joins name no member or connection, join one session's own connections, or state `decidedUnder` other than two entries
for their own two sessions exactly when they decide, is refused, as is a join in an earlier version's file.

## 7. Not defined at this version

- Aligning through another aligned member, a rate other than 1 from two separated anchors, and alignment from shared
  markers (§8.2's third mode).
- Confirming two host identities as one host; pins, notes and saved views.
- Comparing two instants in the Desktop, whose investigation window lists, relinks, adds and opens sessions (revision
  257), aligns and withdraws them and lists candidate joins (revision 259), decides them (revision 260) and draws each
  session as a lane on the investigation's time (revision 261); zooming that timeline and opening a column's records;
  packaging a workspace with its sessions.
- Flagging partial overlap between two captures of one host.
