# ADR-069: A note about a comparison names it as saved and a row by its key, never by rank

- Status: accepted; revision 771. It records notes about saved comparisons, M12's annotations
- Date: 2026-10-24
- Relates to: plan M12, §8.4, R22, ADR-038, ADR-061; `contracts/workspace-v23.md`, `contracts/comparison-v1.md`

## Context

M12's deliverables include annotations and bookmarks. An investigation keeps a person's notes, about all of it or
pinned at an instant of a session, and their saved views of its time (`workspace-v9`, `workspace-v10`), and since
revision 758 their saved comparisons (ADR-061); but nothing said why a comparison was saved, or what one of its rows
showed. A comparison's rows are ranked by how much they changed each time it runs, so a rank names another row once
the sessions grow; a row's key - its relationship's, a process's connections to an address, or an executable pair's -
names the same row while the sessions hold it, a process's identity being a stable hash of its instance's key.

## Decision

1. **A note can be about a saved comparison, or about one of its rows by the key the comparison gives it**, never by
   its rank. Such a note is pinned at no instant.
2. **It names the comparison as it is saved.** A comparison removed later keeps the notes about it, which say it is no
   longer saved; a row the comparison does not compare when it runs is said to be one it does not compare.
3. **A run of the comparison says the notes about it beneath its rows**, a row's by its rank and ends in that run, and
   its document carries each with its words, its row's key and that rank.
4. **A redacted report carries none**: a note is a person's own words, as a comparison's name is (ADR-061).
5. **Command line first.** `icat workspace note --about` writes them and `icat compare --saved` says them; the
   investigation window lists them with what they are about, and the Compare window says them where it shows the
   comparison.

## Why

- **A rank is the run's; a key is the row's.** A note pinned by rank would move to whatever row ranks there next.
- **A person's words are never discarded by another act**: notes, like every revision, are kept, so removing a
  comparison removes no note, and the note says what it was about.
- **A note is never evidence** (`workspace-v23` §7): it is said beside the rows, and no row is ranked, compared or
  judged by it.

## Consequences

- `workspace-v23`: a note's `about`, `{ comparison, row }`; a file before version 23 holds none.
- `icat workspace note <text> --about <comparison> [--row <key>]`; `icat workspace show` and the investigation window
  say what each is about; `icat compare --saved` says them, and its JSON carries them.
- Revision 772 builds the Compare window's: showing a comparison its investigation saved alike, it says the notes
  about it beneath its rows, marks each row a note is about, and adds one about the comparison or its chosen row.

## Alternatives considered

- **A note on the comparison's own revision.** Rejected: saving the comparison again would carry or drop it, and a
  note has revisions of its own.
- **A row named by its rank.** Rejected: ranks change from run to run (Why).
- **A note written into the session beside the relationship.** Rejected: an investigation never writes to a session
  (ADR-038).
