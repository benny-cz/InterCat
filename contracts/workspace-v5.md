# Workspace contract, version 5

Status: M4, revision 264 (ADR-038 to ADR-042); versions 1, 2, 3 and 4 were revisions 253, 254, 256 and 260's, and
packages revision 263's
Owner: `InterCat.Application` (`InvestigationWorkspace`)
Produced by: `icat workspace new | add | relink | alias | align | join | package`
Read by: `icat workspace show | compare | correlate`

A workspace is an investigation over several separately valid sessions (§8.4). It is one JSON file, by convention named
`*.icat-workspace`, that references its members by identity and never changes them. Version 2 added its time: one
member's clock, to which a person aligns the others (§5). Version 3 aligns by what captures record too: one boot's
counter, exactly, or their wall clocks (§5). Version 4 keeps a person's decisions about candidate joins (§6). Version 5
lets a person's alignment take a second instant, which measures the two clocks' rate (§5). A `workspace-v1` file -
members and host names, no time - is read as one without alignments, a `workspace-v2` file as one with manual alignments
only, a `workspace-v3` file as one without join decisions, and a `workspace-v4` file as one whose alignments each have
one anchor; each is written as version 5.

## 1. The file

| Field | Meaning |
|---|---|
| `contract` | `"workspace-v5"` (`"workspace-v1"` to `"workspace-v4"` are read) |
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

`icat workspace show --json` prints `workspace-resolution-v6`: the file's identity and times, each member's fields with
its `fullPath`, `state`, `currentGeneration` (null when no session is there), `reason` (null when present), `host` (its
name, when given) and `alignment` (the revision in force, or null), the hosts with their members, the `timeReference`
every alignment and join decision revision, the overlaps of captures of one host (§5), and caveats. It exits 0 when every
member is present and 1 otherwise.

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
`DriftUnknown`) and distance from its nearer anchor; the `order`; the difference; the pair's half-width; and a
statement. Two instants of one member are ordered exactly, on one clock. Otherwise the pair's uncertainty is
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
revision states any of these. A second anchor is stated only by a manual revision, whole, at other instants than the
first, and measuring a rate within 1,000 ppm of 1; a file before version 5 states none.

Two captures of one host identity may have recorded the same events (§8.4), so every such pair is compared, by its
record extents placed in the investigation's time: they **ran at once** when each reaches past the other's start by more
than the pair's uncertainty - records of one event may be in both, so no count across them is summed, and nothing is
deduplicated by time; they **may have** when they are nearer than that uncertainty; two captures that recorded two
different boots and seem to run at once **contradict** each other, which two boots cannot, so one of their alignments is
wrong; and a pair of which not both have a place with a known uncertainty is **unknown**. Two hosts' captures are never
compared: their records are two machines' events. `show` and the Desktop state each overlap in words.

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

## 7. A package

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
- Everything else is kept: `workspaceId`, `createdUtc`, `updatedUtc`, `hostAliases`, `timeReference`, `alignments` and
  `joins`. The file is written as `workspace-v5`.
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

## 8. Not defined at this version

- Aligning through another aligned member, and alignment from shared markers (§8.2's third mode).
- Confirming two host identities as one host; pins, notes and saved views.
- Comparing two instants in the Desktop, whose investigation window lists, relinks, adds and opens sessions (revision
  257), aligns and withdraws them and lists candidate joins (revision 259), decides them (revision 260) and draws each
  session as a lane on the investigation's time (revision 261); zooming that timeline and opening a column's records.
- Deduplicating two captures' records of one event: an overlap is flagged, and nothing is merged.
- A redacted package of a whole investigation: a redacted package's pseudonyms hold only within it (ADR-042).
