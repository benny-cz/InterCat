# Workspace contract, version 16

Status: M4, revision 388 (ADR-038 to ADR-042); versions 1 to 15 were revisions 253, 254, 256, 260, 264, 265, 266, 269,
270, 271, 280, 301, 339, 343 and 360's, and packages revision 263's
Owner: `InterCat.Application` (`InvestigationWorkspace`)
Produced by: `icat workspace new | add | relink | alias | align | join | same-host | translate | note | view | package`
Read by: `icat workspace show | compare | correlate`

A workspace is an investigation over several separately valid sessions (§8.4). It is one JSON file, by convention named
`*.icat-workspace`, that references its members by identity and never changes them. Version 2 added its time: one
member's clock, to which a person aligns the others (§5). Version 3 aligns by what captures record too: one boot's
counter, exactly, or their wall clocks (§5). Version 4 keeps a person's decisions about candidate joins (§6). Version 5
lets a person's alignment take a second instant, which measures the two clocks' rate (§5). Version 6 aligns a member to
any member placed in the workspace's time, not only to its reference (§5). Version 7 keeps a person's confirmations that
two host identities are one host (§4), version 8 their statements of known address translations (§6), version 9 their
notes (§7), version 10 their saved views (§7), version 11 how they laid out each session's graph (§7), version 12
what they rank each session's rows by (§7), version 13 how strongly a record must bind to a process to count as its
own there (§7), version 14 whether each of its timeline lanes is read against its own peak (§7), version 15 how they
left the window's two main panes (§7), and version 16 which process lanes they pinned at the top of each session's
timeline (§7). An earlier version's file is read as one without what later versions added - a `workspace-v1` file holds
members and host names and no time, a `workspace-v2` file manual alignments only, and a `workspace-v5` file members each
aligned to the reference itself - and each is written as version 16. A kind of fact is
refused only in a file of a version before the one that added it; until revision 279 each was refused in any version
before the newest, so a file an earlier version wrote with what that version had added stopped reading once a later
version appeared.

## 1. The file

| Field | Meaning |
|---|---|
| `contract` | `"workspace-v16"` (`"workspace-v1"` to `"workspace-v15"` are read) |
| `workspaceId` | A random identity of this workspace |
| `createdUtc`, `updatedUtc` | When it was made and last written |
| `members` | Its sessions, in the order they were added (§2) |
| `hostAliases` | A person's names for its members' hosts: `{ hostId, alias }`, one name per host and one host per name, ignoring case |
| `timeReference` | The member whose session clock is the workspace's time; null while no member is aligned (§5) |
| `alignments` | Every alignment revision, in the order recorded (§5) |
| `joins` | Every join decision revision, in the order recorded (§6) |
| `hostEquivalences` | Every revision of a person's confirmation that two host identities are one host, in the order recorded (§4) |
| `addressTranslations` | Every revision of a person's statement of a known address translation, in the order recorded (§6) |
| `notes` | Every revision of a person's notes, in the order written (§7) |
| `views` | Every revision of a person's saved views of the investigation's time, in the order saved (§7) |
| `layouts` | How a person laid out each member's graph and timeline, ranked its rows and counted its records: at most one per member, replaced as it changes (§7) |
| `panes` | How a person left the window's two main panes while showing its sessions: the graph's share of their height and the pane filling the column; null when it keeps none (§7) |

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

`icat workspace show --json` prints `workspace-resolution-v19`: the file's identity and times, each member's fields with
its `fullPath`, `state`, `currentGeneration` (null when no session is there), `reason` (null when present), `host` (its
name, when given), `alignment` (the revision in force, or null), `through` (the members it is aligned through to the
reference, nearest first) and, since revision 395, `demo` (whether the session found for it is InterCat's generated demo,
`contracts/demo-investigation-v1.md`, which the text then says), the hosts with their members and the identities confirmed one host with each, the
`timeReference`, every alignment, join decision, host confirmation, address translation, note and view revision, the
overlaps of captures of one host (§5) with, since revision 278, the snapshot vector they answer (I16) - each capture
read to place them, with its session, the one generation read and its manifest's digest - the layouts and the panes
(§7), and caveats. It exits 0 when every member is present and 1 otherwise.

## 4. Hosts

A member's host is its source clock's `hostId`: for a live capture, derived from its installation's machine GUID together
with its machine's name and build (before revision 253, from the name and build alone); for an import, from its file's
content; for a redacted package, minted. Members with equal `hostId`s are grouped as one host identity, which is evidence
of one host and never proof - an exact clone shares its original's - and equal names or addresses make no two identities
one (§8.3, R22, P6). A name is a person's, for people; it changes no identity, and one name never names two host
identities, which would read as one host. A host is named by its identity, a unique leading part of it, or its current
name.

Two identities are one host only by a person's word - a machine renamed or reinstalled, or a file imported from it -
which `icat workspace same-host` records as a revision (§8.3):

| Host confirmation field | Meaning |
|---|---|
| `revision` | A positive number, unique among host confirmations and increasing in the order recorded |
| `decision` | `Confirmed`: the two are one host; `Withdrawn`: from this revision they are two again |
| `first`, `second` | Two different host identities, each a member's |
| `note`, `recordedUtc` | The person's words, and when |

A pair's latest revision, in either order, is in force, and confirmations in force join identities transitively: two
identities each confirmed one host with a third are one host too. Captures of identities one host by confirmation are
compared as one host's - for sessions that ran at once (§5), and for loopback connections between them (§6) - and each
statement that rests on a confirmation says so. A confirmation changes no session and no identity: it is never evidence
of its own. A pair is confirmed once and withdrawn only while confirmed; a file whose confirmations name a host no member
was recorded on, join an identity to itself, or appear in a file before version 7 is refused.

## 5. Time

The workspace's time is its time reference's session time, in nanoseconds: that member's instants are its own, exactly.
Another member has workspace time only through its alignment in force - its latest revision, when that is not a
withdrawal - to the time reference, or to another member that has workspace time, and never through itself (ADR-039).

| Alignment field | Meaning |
|---|---|
| `revision` | A positive number, unique in the file and increasing in the order recorded |
| `sessionId` | The member aligned |
| `mode` | `Manual`: a person's statement; `SameBoot`: one boot's counter; `WallClock`: the captures' recorded wall clocks; `Withdrawn`: the member is not aligned from this revision |
| `referenceSessionId` | The member aligned to: the time reference, or a member placed in its time, while the revision is in force; null for a withdrawal |
| `sessionNanoseconds`, `referenceNanoseconds` | The anchor: the member's instant, and the same instant in the reference's session time |
| `withinNanoseconds` | The bound on the anchor, a half-width: the person's, the rounding of one counter, or the sum of the wall-clock bounds |
| `driftPartsPerMillion` | The person's bound on how fast the two clocks drift apart - with a second anchor, on how far their rate may wander from the one the anchors measure; null when not stated; 0 for one boot's counter |
| `secondSessionNanoseconds`, `secondReferenceNanoseconds` | `Manual` only, optional: a second anchor, well apart from the first, which measures the clocks' rate; null with one anchor |
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

With a second anchor `(t2, r2)` well apart from the first `(t1, r1)`, a manual alignment measures the clocks' rate
`s = (r2 - r1) / (t2 - t1)` and maps `t` to `r1 + s * (t - t1)`, rounded to the nanosecond. Its uncertainty adds
`withinNanoseconds`, which holds between the anchors; beyond them, that bound carried along the rate,
`2 * withinNanoseconds / |t2 - t1|` for every nanosecond past the nearer; `2 * driftPartsPerMillion * d / 10^6`, `d`
being the distance from the nearer anchor - a rate that may wander by w from a constant moves an instant by up to 2w
times that distance; and 1 ns of rounding (§8.2). With no drift stated the wander is unknown, and so is every
uncertainty but at the anchors themselves. A second anchor at the first's instant in either session, or measuring a rate
more than 1,000 ppm from 1 - which no working clock runs at, so an instant was misread - is refused. A member's span is
as uncertain as its widest instant, which lies at an end of it or midway between its anchors.

A member aligned to another aligned member is placed through both alignments in turn, and on through that member's to
the reference: each adds its own uncertainty, taken at the widest instant what was carried so far allows, and carries
the rest at its rate. Two members compared meet in the nearest member both are placed through, or the reference: below
it each side counts as independent, and the alignments above it, which both share, move both instants alike, so they add
only their growth - a drift, or the slope a two-anchor line may have - over the time the two instants may lie apart, and
each instant's own roundings, which no shared alignment cancels: the rounding of a measured rate, and one boot's
counter's bound. Aligning a member to one with no place, or to one placed through the member itself, is refused, and so
is withdrawing an alignment another member is aligned through.

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

`icat workspace compare`, and the investigation window's Compare instants…, place two instants, each read in its own
session time - written `<session>@<seconds>` on the command line - and `compare` prints
`workspace-comparison-v1`: each instant's workspace time and half-width, or its `gap` (`NoTimeReference`, `NotAligned`,
`DriftUnknown`) and distance from its nearer anchor; the `order`; the difference; the pair's half-width; and a
statement. Two instants of one member are ordered exactly, on one clock. Otherwise the pair's uncertainty is
`sqrt(rA^2 + rB^2) + sA + sB` over each side's random part `r` and bound part `s`, and the order is `Before` or `After`
only when the instants are further apart than it, `Ambiguous` when they are not, and `Unknown` - its difference withheld
- when either instant has no workspace time or an unknown uncertainty (§8.2, R3, R21). A stated bound is written rounded
up at the precision written, so it never reads smaller than it is.

A file's time contradicts itself when a `workspace-v1` file holds any; its reference is no member; a revision is not a
unique positive number; a manual revision names no member, aligns a member to itself or to no member, or lacks its
anchor or a non-negative bound, or states a drift that is no non-negative rate; a withdrawal states an anchor; or an
alignment in force leaves its member with no place - aligned to a member with none, or through itself - or aligns the
reference itself. A file before version 6 aligns every member to the reference itself. A `workspace-v2` file holds
only manual alignments and withdrawals. A same-boot revision names a boot and states a drift of 0; a wall-clock revision
states its agreement, acquisition, gap and drift, and a bound no narrower than its agreement and acquisition; no other
revision states any of these. A second anchor is stated only by a manual revision, whole, at other instants than the
first, and measuring a rate within 1,000 ppm of 1; a file before version 5 states none.

Two captures of one host - one identity, or identities a person confirmed are one (§4) - may have recorded the same
events (§8.4), so every such pair is compared, by its
record extents placed in the investigation's time: they **ran at once** when each reaches past the other's start by more
than the pair's uncertainty - records of one event may be in both, so no count across them is summed, and nothing is
deduplicated by time; they **may have** when they are nearer than that uncertainty; two captures that recorded two
different boots and seem to run at once **contradict** each other, which two boots cannot, so one of their alignments is
wrong; and a pair of which not both have a place with a known uncertainty is **unknown**. Two hosts' captures are never
compared: their records are two machines' events. `show` and the Desktop state each overlap in words.

The merged time draws each member as a lane over one uniform grid of the investigation's time - its whole, from the
earliest placed record to the latest, or an interval of it - read through the member's alignment in its own session
time, so a lane's columns are its own records where the alignment puts them (I9). The investigation window draws it, and,
since revision 384, `icat workspace timeline` does, in the same words (R18): each lane's place and records, what its
capture covered over its columns (`coverage-v2`), or why it has no place; the overlaps; and the notes in force. Its
`--json` prints `workspace-timeline-v1`: the interval in 100-nanosecond ticks of the investigation's time, or none when
no member has a place, and the columns; each lane's session, whether it is placed, its extent, the half-width of its
placement's uncertainty (null when unknown), its time gap, why it was not read, its records, its sentence, and every
column's interval in the investigation's time and in its session's own, its records, and its coverage - a column of none
that its capture did not cover is not an observed zero (R21); the overlaps' and notes' statements; and the snapshot
vector it answers (I16).

## 6. Candidate joins

`icat workspace correlate` proposes candidate joins between members (§8.3, ADR-041), under the rule
`cross-capture-connection-candidate-v2`. Each member that holds its capture is read for its one-sided connections - the
TCP connections and UDP flows whose other end its own capture holds no record of (`relations-v1` §5b) - and a connection
of one member is a candidate with a connection of another when:

- they are of one protocol, and the one's local endpoint is the other's remote endpoint and the other way round - as
  each capture sees them, or through a known address translation a person stated (below), which the evidence then says;
- neither endpoint is a loopback address, unless both members were recorded on one host: one identity, or two a person
  confirmed are one, which the candidate's evidence then says (§4);
- their lifetimes - each from its first record to its last - overlap once each end is widened by its uncertainty in
  the investigation's time (§5), or cannot be compared because an end has no workspace time or no known uncertainty.

A mirrored pair whose lifetimes lie apart beyond their uncertainty is not proposed, and is counted. A candidate is never
an established join: it states its evidence - the mirrored endpoints, whether the lifetimes overlap or cannot be
compared and why, and the bytes each side measured of each direction, the same or not - and how many other candidates
either connection has, so a connection with two is said to be ambiguous. `--json` prints `workspace-correlation-v6`: the
rule, each candidate's two ends (session, key, protocol, endpoints, process and lifetime, and each direction's transfers,
how many of them stated no size and the bytes the others measured - null where none measured one, which is not a
transfer of none, since revision 382, where version 4 wrote 0), its timing, its
alternatives, the decision in force, the translations it mirrors through, and its evidence; the mirrored pairs not
proposed and the loopback pairs of two hosts, counted; the members not compared and why; the decisions in force whose
pair is no candidate now, and why; caveats; and, since revision 277, the snapshot vector it answers (I16): each compared
capture with its session, the one generation read and that generation's manifest digest, by capture; and, since revision
383, what each compared capture covered of TCP and UDP in that generation - each mechanism's coverage state and the fact
behind it, from its ledger (`coverage-v2`) - since a capture that lost or never collected a connection's records holds no
mirror of it, so no candidate is no proof there was no other end. A session that records on can hold more at the next
comparison. Nothing is joined by time alone, by an address alone or by a name.

A person decides a candidate with `icat workspace join <n> --accept | --reject | --withdraw`, `<n>` its number in
`correlate`'s list. Each decision is a revision of the file, kept when a later one replaces or withdraws it:

| Join field | Meaning |
|---|---|
| `revision` | A positive number, unique among joins and increasing in the order recorded |
| `decision` | `Accepted`: one connection, by a person; `Rejected`: not one; `Withdrawn`: undecided from this revision |
| `first`, `second` | The two ends: `{ sessionId, key }`, each a member's one-sided connection's stable key |
| `decidedUnder` | The alignment revision in force when decided, 0 for none, for each of the two sessions and every member either was aligned through; empty for a withdrawal |
| `note`, `recordedUtc` | The person's words, and when |

A pair's decision in force is its latest revision, in either order of its ends, unless it withdraws. An accepted join is
a person's and is never evidence: a candidate says it was accepted or rejected by a person, and, when an alignment it was
decided under has changed since the decision - revised, withdrawn or made - that the decision was made under alignments
since changed, to review (§8.3). A decision in force whose pair is no candidate now is said, never dropped. A file whose
joins name no member or connection, join one session's own connections, or state `decidedUnder` other than one entry per
member, their own two sessions among them, exactly when they decide, is refused, as is a join in a version 1 to 3 file,
or one with more than its own two entries in a file before version 6.

A known address translation is a person's statement, recorded by `icat workspace translate <seen> <endpoint>`, that an
endpoint one capture sees - a port forward's, a NAT's or a proxy's - is an endpoint the other capture holds (§8.3):

| Translation field | Meaning |
|---|---|
| `revision` | A positive number, unique among translations and increasing in the order recorded |
| `decision` | `Stated`, or `Withdrawn`: from this revision the two are two endpoints again |
| `seen`, `is` | Two different endpoints, as InterCat writes them: an address and a port - `203.0.113.7:8443`, `[2001:db8::7]:443` - or both an address alone, whose ports then pass through unchanged |
| `note`, `recordedUtc` | The person's words, and when |

A pair's latest revision, in either order, is in force; a translation applies either way, one hop, and never to a
loopback address, which names its own host. A candidate that mirrors only through one lists it and says it rests on the
person's statement; it is never evidence of its own. A file whose translations name no endpoint as InterCat writes one,
relate a loopback address, an endpoint to itself or an endpoint to an address alone, or appear in a file before version
8, is refused.

## 7. Notes, saved views, layouts and panes

A note is a person's words on the investigation (§8.4), about all of it or pinned at an instant of a member's session,
which the investigation's time places as it places any other instant - the merged timeline marks it on that session's
lane:

| Note field | Meaning |
|---|---|
| `revision` | A positive number, unique among notes and increasing in the order written |
| `noteId` | The note a revision is of: every revision of one note shares it |
| `text` | Its words, at most 4,000 characters; null when the revision removes the note |
| `at` | Where it is pinned: `{ sessionId, nanoseconds }`, an instant of a member's session in its own time; null for a note about the whole investigation |
| `recordedUtc` | When it was written |

A note's latest revision is in force unless it removes the note; rewording keeps where it is pinned, and every revision
is kept. `icat workspace note <text> [--at <session>@<seconds>]` adds one, `<note> --replace <text>` rewords it and
`<note> --remove` removes it, `<note>` its identity or a unique leading part; `show` lists those in force with their
place. A note changes no session and is never evidence. A file whose notes name no note, hold no words or too many,
are pinned to no member, or appear in a file before version 9, is refused.

A saved view is a named interval of the investigation's time, to show on the merged timeline again:

| View field | Meaning |
|---|---|
| `revision` | A positive number, unique among views and increasing in the order saved |
| `name` | The view's name, at most 100 characters, trimmed; one name, in any case, is one view |
| `startTicks`, `endTicks` | The interval, in 100 ns ticks of the time reference's clock, its end after its start by at most 2^63 - 1 ticks; null when the revision removes the view |
| `reference` | The time reference the interval is in; null when the revision removes the view |
| `recordedUtc` | When it was saved |

A name's latest revision is in force unless it removes the view, so saving under a name replaces the view of that name.
A view needs the investigation to have a time; one saved before the time reference changed is kept, said not to be of
the time now, and never shown on another clock. `icat workspace view <name> <from> <to>` saves one, in seconds of the
investigation's time, and `<name> --remove` removes it; `show` lists those in force. A file whose views are unnamed,
show no interval, are in the clock of no member, or appear in a file before version 10, is refused.

A layout is how a person laid out a member session's view (§26.3's workspace scope): the nodes they pinned on its
graph, where, the process lanes they pinned on its timeline, what its rows are ranked by, the evidence policy its records
are counted under, and the scale its timeline lanes are read against. It is a preference rather than a finding, so a member has one at most, replaced as it changes,
and none is kept as a revision:

| Layout field | Meaning |
|---|---|
| `sessionId` | The member whose graph it lays out |
| `pins` | The pinned nodes, by key: `{ key, x, y }`, the node's stable graph key - a process instance, a group or an aggregate - at most 256 characters, and where it was put, each of `x` and `y` from 0 to 1 across and down the graph; at most 1,024, each key once |
| `pinnedLanes` | The process instances whose lanes are pinned at the top of their group's lanes on the session's timeline (§6.2), by identity, in the order they were pinned, which is the order they are drawn in; at most 1,024, each once, none the empty identity. Empty when none is. Since version 16 |
| `rankBy` | What the session's rows are ranked by (§6.1), when not by their own records: `BytesSent`, `BytesReceived`, `EndpointBytes`, `RpcCallsMade`, `RpcCallsServed`, `RpcErrors`, `RpcCallTime`, `RpcServeTime` or `ActivePeers`; null ranks by records. Since version 12 |
| `perSecond` | Whether a count or sum it ranks by reads per second of the ranked interval. Since version 12 |
| `evidencePolicy` | How strongly a record must bind to a process to count as that process's, when not by correlated evidence, the default: `IncludeCandidates`, which counts a reused PID's later holder's records as its own too, as candidates; null counts correlated evidence. No other policy is kept, since no view offers one to put back. Since version 13 |
| `scalesEachLane` | Whether each of the session's timeline lanes is read against its own busiest bar rather than one scale every lane shares, the default (§6.2's normalization scope). Since version 14 |
| `updatedUtc` | When it last changed |

The Desktop keeps a session's pins, its pinned lanes, its ranking, its evidence policy and its lanes' scale in the
investigation it was opened from, and puts them back when it is opened from it again, its records counted under the
policy kept before its first view and its lanes pinned before any group's are drawn; a session opened on its own keeps
them only while it is open. A pinned lane of an instance a later generation does not draw stays pinned, for when it is
drawn. A layout that pins nothing, ranks by records, not per second, counts correlated evidence and reads every lane on
one scale keeps nothing and is removed. A file whose layouts are of no member, keep nothing, pin a node twice, outside
the graph or by no key, pin a lane twice, by the empty identity or more than 1,024 of them, rank by `Records` by name or
by no metric §6.1 offers, count under `IncludeCorrelated` by name or under any policy but `IncludeCandidates`, appear in
a file before version 11, rank in a file before version 12, name a policy in a file before version 13, read each lane on
its own scale in a file before version 14, or pin a lane in a file before version 16, is refused.

The panes are how a person left the window's two main panes - the graph above the timeline - while showing the
investigation's sessions (§6.1's persistence): the graph's share of the height the two share, and the pane filling the
column by their command, if one does. They are the window's rather than a session's, so the investigation keeps them
once, for every session opened from it, replaced as they change, and none is kept as a revision:

| Panes field | Meaning |
|---|---|
| `graphShare` | The graph's share of the height it shares with the timeline, above 0 and below 1, kept to four decimal places; the timeline has the rest. 0.5, equal halves, is how a window first lays them out |
| `expanded` | The pane filling the column, the other one command away: `Graph` or `Timeline`; null when the two share it |
| `updatedUtc` | When they last changed |

The Desktop keeps the panes in the investigation the shown session was opened from whenever a person drags the split
between them, lets one fill the column or gives both their places back, and puts them back when one of its sessions is
opened from it again, its status listing them with what else was put back. An investigation that keeps none leaves the
panes as they are, as a session opened on its own does. A split gives neither pane less than its minimum height, so a
kept share shows both on any window; only letting one fill the column hides the other (§6.1's collapse floor). Equal
halves with both shown keep nothing, and are removed. A file whose panes keep nothing, give the graph a share that is
not above 0 and below 1, let a pane the window does not have fill the column, or appear in a file before version 15, is
refused.

## 8. A package

`icat workspace package <workspace> --output <new-folder>`, and the Desktop's investigation window, share an
investigation with its sessions as one folder (§8.4, ADR-042):

| Path | Holds |
|---|---|
| `<new-folder>/<file name>` | The investigation, under its source file's name, its members' paths rewritten |
| `<new-folder>/sessions/<first 8 hex of the session>-<folder>/` | An original evidence package of a copied member |

- A member chosen to be copied must hold its capture (`Present`, `Advanced` or `Replaced`). It is copied as an original
  evidence package of its current generation (`contracts/original-evidence-package-v1.md`), and its `path` becomes
  `sessions/<folder>`, relative to the file. Its `generation` and `manifestDigest` are kept, so a member that has moved
  on reads as `Advanced` or `Replaced` in the package, as it does at its source, until a person relinks it.
- Every other member - not chosen, or `Missing`, `Different` or `Unreadable` - keeps its fields, and its `path` becomes
  the whole path it was last found at. On the computer that made the package it resolves as it did; elsewhere it is
  `Missing`, to relink.
- Everything else is kept: `workspaceId`, `createdUtc`, `updatedUtc`, `hostAliases`, `timeReference`, `alignments`,
  `joins`, `hostEquivalences`, `addressTranslations`, `notes`, `views`, `layouts` and `panes`. The file is written as
  `workspace-v16`.
- The folder must not exist and must lie inside no session. It is built in a private folder beside it,
  `<new-folder>.partial-<32 hex>`, and moved into place only after every copy verified and the file, reopened, found each
  copy as the session it is at the generation copied, with nothing else under `sessions/`. A package that is refused or
  cancelled leaves nothing.

`--only <session>`, repeated, copies only the members named, each by its identity or a unique leading part; naming one
that cannot be copied is refused. `--check` measures the package and writes nothing. `--json` prints
`workspace-package-v1`: `performed`, `source`, `directory` and `workspace` (null for a check), each member's
`sessionId`, `fullPath`, `copied`, `packagedPath`, `generation`, `rows`, `files`, `bytes`, `redacted`, `state` (in the
package once written, where it was found for a check) and `note`, the totals, `unredacted`, the `disclosure` - what the
package holds and exposes, a paragraph each, as the Desktop states it before saving - the `warning` and the
`verification`. It exits 0 when every member is copied, or the ones copied were chosen with `--only`, and 1 when a
member could not be copied.

## 9. Not defined at this version

- Alignment from shared markers (§8.2's third mode).
- Graph pins: a note pinned at an instant is the investigation's pin, and each session's graph keeps its own.
- Lane grouping and column widths (§6.1's persistence): the window offers no control for them yet, so nothing of them is
  kept.
- Comparing two instants in the Desktop, whose investigation window lists, relinks, adds and opens sessions (revision
  257), aligns and withdraws them and lists candidate joins (revision 259), decides them (revision 260) and draws each
  session as a lane on the investigation's time (revision 261); zooming that timeline and opening a column's records.
- Deduplicating two captures' records of one event: an overlap is flagged, and nothing is merged.
- A redacted package of a whole investigation: a redacted package's pseudonyms hold only within it (ADR-042).
