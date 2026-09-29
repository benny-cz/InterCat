# ADR-042: An investigation is shared as one folder with its sessions

- Status: accepted; revision 263
- Date: 2026-09-29
- Relates to: §8.4, §11.3, M4, I15, R22, ADR-038, ADR-039, ADR-041, `contracts/workspace-v8.md`,
  `contracts/original-evidence-package-v1.md`

## Context

An investigation references its sessions where they were last found (ADR-038): one file on one computer, naming each
session's folder by a path - relative when it lies under the file's folder, whole otherwise. Handing it to another
person means handing over its sessions too. §8.4 asks that an export "package selected captures plus the workspace",
and that "missing captures reopen as unresolved references with a relink workflow". One session is already shared
exactly by an original evidence package (§11.3), which reopens as the same session. Open were: what the investigation's
own file becomes in a package; what happens to a session that is not where it was last found, or has moved on since it
was selected; and what a package states before it is saved.

## Decision

1. **A package is one folder that opens as the same investigation.** It holds the investigation's file and, under
   `sessions/`, an original evidence package of each copied member; the file names each copy by a path relative to
   itself, so the folder can be moved or sent whole. The investigation keeps its identity, time reference, alignments,
   host names and join decisions unchanged: every copy is the same session, so nothing that names a session changes.
2. **Only a member whose capture is where it was last found is copied, and a person chooses which.** A member that is
   missing, different or unreadable cannot be copied, nor chosen. Every member not copied stays a reference by the whole
   path it was last found at - relative to the original folder, its path would name nothing beside the package, or
   something else - so on the computer that made the package it resolves as it did, and elsewhere it is missing, to
   relink.
3. **The selection is kept as it is.** A member whose session has published a newer generation since it was selected
   is copied as it is now, and the package's investigation still selects the generation it did, so the copy reads as
   advanced in the package, as it does at its source, until a person relinks it. A package never selects a generation
   for them.
4. **It is stated before it is saved.** What the copies hold - unredacted metadata, the message content any capture
   kept, or a redacted package's pseudonyms - their hosts, what the investigation's file carries, with names and notes
   as written and never pseudonymized, and which sessions are not copied, with the path each reference discloses, are
   said before anything is written. The warning is the unredacted one unless every copy is itself a redacted package.
5. **Nothing appears under its name until it verified.** It is built in a private folder beside its destination, which
   must be new and inside no session. Each copy is checked as it is copied and reopened as an original package is; then
   the investigation is reopened from its own file, and every copy must be found beside it as the session it is, at the
   generation copied, with nothing else under `sessions/`. A package that fails or is cancelled leaves nothing.

## Consequences

- Revision 263 implements it in `InterCat.Application` (`InvestigationPackage`), `icat workspace package`
  (`workspace-package-v1`, `--only`, `--check`) and the investigation window's Package…, whose confirmation lists each
  session to choose and states again what the choice exposes whenever it changes.
- A copy holds one generation, so a recipient cannot return to a generation its source selected but no longer holds; the
  package says so of each such member.
- A redacted package of a whole investigation is not defined: a redacted package's pseudonyms are consistent only within
  it, so host names, joins and alignments across its sessions would not carry.
