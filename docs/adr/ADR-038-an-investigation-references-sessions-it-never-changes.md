# ADR-038: An investigation references sessions it never changes

- Status: accepted; the workspace manifest, adding, showing, relinking and host names are revision 253
- Date: 2026-09-28
- Relates to: §8, §8.3, §8.4, M4, I9, R22, P6, ADR-039, `contracts/workspace-v2.md`, `contracts/store-v1.md`

## Context

M4 asks for multi-machine investigation: captures taken separately on several hosts inspected together, with host and
boot identity, clock alignment and cross-host correlation. §8.4 names its persistence: a manifest that stores capture
content identities, selected generations, host aliases, clock mappings, correlation revisions, pins, notes and views,
while the sources remain separately valid `.icat` sessions; re-adding the same capture must not duplicate it, and a
missing capture reopens as an unresolved reference with a relink workflow.

Everything else in M4 hangs on that manifest - an alignment is an annotation on it, a correlation revision is recorded in
it - so it comes first, and its rules are decided before any of them.

A session's store names a "source identity", but it is not a capture's identity: a broker capture's is its plan's digest,
which every capture made under that plan shares, an import's names the path of the file it was read from, and a redacted
package's is its contract's name. What does name a capture is its journal's capture identity: minted for a live capture,
derived from the file's content for an import (so importing one file twice yields one capture), and minted anew for a
redacted package, which is deliberately not linkable to its original.

## Decision

1. **A workspace is one file, `workspace-v1` (`workspace-v2` since revision 254), and changes no session.** A
   `.icat-workspace` JSON file names its members; it never writes to a session - reading one opens it as a viewer does
   and reads its journal's header - and a session never learns it belongs to one. A source's timestamps, records and
   derivations stay exactly its own (I9). The file is kept beside its sessions, never inside one, whose directory holds
   only its own files.
2. **A member is a session identity, not a path.** Each member records the session's id and the capture its journal
   records, the generation selected and that manifest's digest, and its source clock's host, clock and capture epoch. Its
   path is where it was last found: relative to the workspace's folder when the session lies under it, so a workspace and
   its sessions move together, and absolute otherwise.
3. **One capture is one member.** Adding a session whose capture a member already records is refused, whether it is the
   same session, a copy of it or another session of that capture: a capture added twice would count its records twice.
   Two different captures are never merged, by time or anything else, and a store's source identity makes no two
   captures one.
4. **A member resolves, or says why it does not.** Each member is `Present` (its path holds its session at the selected
   generation), `Advanced` (its session has published a newer generation since), `Replaced` (its session is there at an
   older generation, or one derived apart from the selected one), `Missing` (nothing at its path), `Different` (another
   session or capture is there) or `Unreadable` (what is there cannot be opened as a session). A session keeps no older
   generation to read, so an `Advanced` or `Replaced` member is said to be one, and selecting what is there is a person's
   relink, never silent. A `Missing`, `Different` or `Unreadable` member is an unresolved reference, kept and shown.
5. **Relinking checks identity.** A member is relinked to a path only when the session there is the member's session of
   the member's capture; anything else is refused, never adopted. Relinking a member to its own path selects the
   generation there.
6. **Hosts are the sources' own, until a person says otherwise.** A member's host is its source clock's host identity. A
   live capture's is derived from its installation's machine GUID together with its machine's name and build
   (`HostId.ForLocalMachine`), an import's from its file's content, and a redacted package's is minted. Members are
   grouped by equal identities, which are evidence of one host and never proof - an exact clone shares its original's -
   and different identities are never one host by name or address. A person may name a host, one name to one host
   identity, and later confirm two identities as one host as a versioned annotation of the workspace (§8.3, R22, P6).

## Consequences

- Revision 253 implements decisions 1 to 5 and the host names of 6: `icat workspace new`, `add`, `show`, `relink` and
  `alias`, with `show --json` printing `workspace-resolution-v1`.
- A live capture's host identity was derived from its machine's name and build alone, an identity from an equal name
  that P6 forbids: two machines of one name and build would have been one host. Since revision 253 it includes the
  installation's machine GUID. Sessions recorded before keep the identity they recorded, so one machine's older and newer
  captures read as two hosts - the safe direction - until a person confirms them one.
- A workspace file holding a field this version does not define is refused, so a file written by a later version is
  never rewritten without what it held; a write goes over only the text it read, so a change made meanwhile is kept.
- Clock mappings (§8.2's affine segments with their uncertainty), confirmed host equivalence, cross-host correlation,
  pins, notes, saved views and the Desktop's workspace are later slices; each is recorded in the manifest as an
  annotation, versioned, and none rewrites a source. Until a clock mapping exists, no cross-host order, latency or
  pairing is stated: the uncertainty is unknown, not zero (R3, R21).
- A redacted package is a capture of its own, so a workspace holding it and its original would hold both; the package's
  unlinkability is the reason, and a person adds one or the other.
- Partial overlap between different captures of one host is flagged when their members are compared, not deduplicated.
