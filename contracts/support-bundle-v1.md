# Support bundle contract, version 1

Status: M5, revisions 391 and 392 (§20.6, P16)
Owner: `InterCat.Application` (`SupportBundle`), with the capability report from `InterCat.Capture.Windows`
Produced by: `icat support [<session> ...] (--output <path> [--overwrite] | --json | --check)`, and the window's
"Save support bundle…" for the session it shows
Read by: a person, or the people supporting them

A support bundle is what §20.6 says support needs to see why a capture or a view went wrong: InterCat's version, the
machine's runtime, its capability report, and for each session named its manifest's facts, row counts, coverage and loss
counters, clock and recording. It is one JSON document. Every fact in it is chosen from an allowlist, so nothing a record
holds can reach it: no payload, endpoint string, command line or raw event (P16), and no name of a process, executable,
pipe, resource, machine, host or user, and no full path. Its contents are listed before anything is read or written.

## 1. The listing

Before it reads a session or writes a byte, `icat support` says what the bundle holds and what it leaves out, in the words
the bundle then records in `holds` and `leftOut`. With `--check` it says so and does nothing else. With `--json`, where
the bundle is the answer on stdout, the listing goes to stderr beside it. The window says the same words in a question
before a file is chosen, and adds that a bundle made there holds no capability report, which `icat support` adds.

It always leaves out: message content and payload bytes; endpoint addresses and ports; command lines; raw records and
their source fields; process, executable, pipe and resource names; machine, host and user names; full paths, which each
session's folder name stands for; and the identity of the source a session was made from.

## 2. The document

| Field | Meaning |
|---|---|
| `contract` | `"support-bundle-v1"` |
| `createdUtc` | When it was made |
| `version` | InterCat's version, as its build stamped it |
| `runtime` | `operatingSystem`, `operatingSystemArchitecture`, `processArchitecture`, `framework` and `processors`: the machine as support needs it, with no machine or user name |
| `capabilities` | The machine's capability report, as `icat capabilities --json` writes it (`capability-report-v1`): each candidate source's state and why it is not available. It names the operating system and its build, never the machine. Null when the window made the bundle: it runs no probe, and its `holds` then lists no report |
| `sessions` | One entry per session named, in the order named (§3) |
| `holds`, `leftOut` | What the listing said the bundle holds and leaves out |

## 3. A session

| Field | Meaning |
|---|---|
| `folder` | The name of the session's folder, which stands for its path |
| `problem` | Why the session could not be read in full, in its reader's words with its path said as its folder; null when it was read. A folder that holds no session, or one no generation of which verifies, is said to be rather than refused |
| `sessionId`, `generation`, `previousGeneration`, `committedUtc` | The generation read |
| `rolledBackToLastKnownGood` | Whether the newest generation did not verify and the retained one was read |
| `orphanFiles` | How many files the folder holds that no generation names; never their names |
| `redacted` | Whether it is a redacted package (`redacted-session-v1`) |
| `dependencies` | Each file the generation names: `kind`, `name` and `lengthBytes` |
| `rows`, `fieldRows` | The observation rows its segments hold, and the source-field rows beside them |
| `mechanisms` | Every mechanism, in its order: `mechanism` (its name, as `MechanismText` says it), `rows`, `coverage` (`CoverageStateText`'s value) and `reason`, the fact that decided it, as `icat session` states them. A mechanism with no rows and unknown coverage is listed, so it is never read as quiet (R21) |
| `ledger` | The coverage ledger the generation publishes (`coverage-v2`): epochs, acquisitions, deliveries, omissions, undecodable records and losses, by provider; null when it publishes none |
| `clock` | `kind`, `encoding` and `ticksPerSecond` of its source clock; nothing of its host |
| `recordingSeconds` | The capture's recording in seconds of its clock; null when it recorded no stop |
| `journalChunks`, `journalBytes` | Its journal's chunks and their size |
| `contentFiles`, `contentBytes` | Its kept-content files and their size, never a byte of what they keep (ADR-036) |

`icat support` exits 0 when every session was read, and 1 when one could not be, which the bundle then says.

## 4. Not defined at this version

- Timings of a live capture's publication, which no session records once its capture has ended.
- Diagnostic logs: InterCat keeps counters rather than logs on its capture, decode, aggregate and paint paths (§20.6, R11),
  and those counters are in each session's ledger.
