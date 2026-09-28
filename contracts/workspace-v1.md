# Workspace contract, version 1

Status: M4 foundation, revision 253 (ADR-038)
Owner: `InterCat.Application` (`InvestigationWorkspace`)
Produced by: `icat workspace new | add | relink | alias`; read by `icat workspace show`

A workspace is an investigation over several separately valid sessions (§8.4). It is one JSON file, by convention named
`*.icat-workspace`, that references its members by identity and never changes them.

## 1. The file

| Field | Meaning |
|---|---|
| `contract` | `"workspace-v1"` |
| `workspaceId` | A random identity of this workspace |
| `createdUtc`, `updatedUtc` | When it was made and last written |
| `members` | Its sessions, in the order they were added (§2) |
| `hostAliases` | A person's names for its members' hosts: `{ hostId, alias }`, one name per host and one host per name, ignoring case |

A workspace is written whole to a temporary file beside it and moved into place, so a reader sees the old file or the
new one, and only over the text it was read from: a change made meanwhile is refused, never written over. It is kept
beside its sessions, never in a session's directory. A file is refused whole when it has another contract, a field this
version does not define (so a later version's file is never rewritten without it), a member without its identities, two
members of one session or one capture, or a name that is empty, doubled or given to two hosts.

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

`icat workspace show --json` prints `workspace-resolution-v1`: the file's identity and times, each member's fields with
its `fullPath`, `state`, `currentGeneration` (null when no session is there), `reason` (null when present) and `host`
(its name, when given), the hosts with their members, and caveats. It exits 0 when every member is present and 1
otherwise.

## 4. Hosts

A member's host is its source clock's `hostId`: for a live capture, derived from its installation's machine GUID together
with its machine's name and build (before revision 253, from the name and build alone); for an import, from its file's
content; for a redacted package, minted. Members with equal `hostId`s are grouped as one host identity, which is evidence
of one host and never proof - an exact clone shares its original's - and equal names or addresses make no two identities
one (§8.3, R22, P6). A name is a person's, for people; it changes no identity, and one name never names two host
identities, which would read as one host. A host is named by its identity, a unique leading part of it, or its current
name.

## 5. Not defined at this version

- Clock mappings between hosts, and any cross-host order, latency or pairing: withheld, their uncertainty unknown.
- Confirming two host identities as one host; cross-host correlation revisions; pins, notes and saved views.
- A workspace in the Desktop, and packaging a workspace with its sessions.
- Flagging partial overlap between two captures of one host.
