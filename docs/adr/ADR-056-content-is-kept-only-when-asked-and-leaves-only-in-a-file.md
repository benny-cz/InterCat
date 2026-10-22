# ADR-056: Content is kept only when a capture asks for it, read only when a person asks, and leaves the machine only in a file a person saves

- Status: accepted; revision 728. It ties together what ADR-036 (content as restricted evidence), ADR-037 (WinINet's
  exchanges), ADR-049 (the broker's own-process rule), the sharing presets of revisions 104, 106 and 154 and the
  support bundle of revisions 391 and 392 decided under §11, and adds a test that no product code reaches the network
- Date: 2026-10-22
- Relates to: §11, §11.1-§11.3, §19.5, §20.6, I21, I22, R17, P2, P15, P16, P28, ADR-036, ADR-037, ADR-049,
  `contracts/content-v1.md`, `contracts/redacted-session-v1.md`, `contracts/original-evidence-package-v1.md`,
  `contracts/support-bundle-v1.md`, `docs/design/SHARING-REPORT-REDACTION.md`

## Context

A tool that watches which processes talk to each other sees what they say, and much of what it sees without the words
is sensitive too: endpoint addresses, pipe and process names, command lines, user names. §11 separated two choices -
whether a capture keeps content, and whether a person looks at what was kept - and asked for export presets, restricted
storage and no automatic upload. Each part was decided in its own record or contract as it was built; no record stated
the policy they make together, or what of §11 is not built.

## Decision

1. **Metadata by default; content only when a capture asks.** A capture keeps event bodies only for descriptors its
   reviewed policy names, and omits every other body with a stated reason (R17). Content is kept only under a content
   request reviewed before the capture starts - its sources, its processes, its limits per record and per session, its
   retention - and only from sources whose content semantics were measured: InterCat's own fixture provider and
   WinINet's capture provider (ADR-037). Through the broker, only the client's own processes may be named (ADR-049).
2. **Kept content is restricted evidence.** It lies beside the journal, one `content-v1` chunk per journal chunk, never
   in the metadata a query reads; each fragment states its classification, direction, kept and original lengths, how it
   was cut and the policy that kept it (I21); it is released with its chunk, and no byte stands in for one missing (P2).
3. **Reading it is a second choice.** A person opens a record's content on request: bounded hex and text, a part
   assembled only from whole buffers with its gaps named, and the fixture's decoder only when asked, its fields tied to
   the bytes they came from and never kept. Content never enters exports' JSON, search snippets, reports, logs or crash
   diagnostics (P16).
4. **Nothing leaves the machine but in a file a person saves.** No product code opens a connection, sends a request,
   resolves a name or fetches symbols, and product code is built only on reviewed packages; a test holds both. What a
   person can save, each stating what it holds before it is written:
   - a **metadata-only report** (`intercat-share-report-v1`): an allowlist of fields with random relationship tokens;
   - a **redacted session** (`redacted-session-v1`): a new session whose every name, identity and absolute reading is a
     pseudonym, verified to reproduce the source's analysis and searched for the source's names before it exists;
   - an **original evidence package** (`original-evidence-package-v1`): the evidence as captured, explicitly unredacted;
   - a **support bundle** (`support-bundle-v1`): versions, capability report and each session's facts and counters from
     an allowlist that holds no payload, address, command line, name or full path.
5. **Sessions live in the person's own space.** The window's sessions default to the person's local application data
   and take the access their folder grants - by default the person, SYSTEM and Administrators. A
   broker's evidence lies beneath its protected root, readable by the capturing user and written only by the broker
   (ADR-052).

## What §11 asks that is not built

- **Encryption at rest.** No session is encrypted; §11.3 allows it only with a reviewed library and OS-protected keys,
  and says encryption does not reduce exposure while content is being inspected.
- **A secure erase.** Releasing a chunk or deleting a session removes its files; on a solid-state disk that is not a
  guaranteed erase, which §11.3 says and the product does not promise otherwise.
- **Tightened session access.** InterCat does not narrow a session folder's access below what its parent grants; a
  session saved into a shared folder is as shared as the folder.
- **Name and symbol enrichment.** §19.5's optional enrichment is not built; were it built, it would be the first code to
  reach the network, behind an opt-in, and the test above would name it.

## Consequences

- An investigation of who talks to whom needs no content at all; content is there for the one exchange a person wants
  to read, under limits set before it was kept.
- A shared file is the only way anything leaves, and each says what it holds before it is written; the original
  evidence package is the one that holds everything, and says so.
- A package added to product code must be reviewed for what it sends before the test admits it.

## Alternatives considered

- **Keep content by default, filter it in views.** Rejected: a view filter is not retention (P15), and kept bytes are
  exposed however they are hidden.
- **Generic interception of any process's traffic.** Rejected: a source whose content semantics were not measured would
  claim bytes it cannot attribute or bound (§11).
- **An upload path for support.** Rejected: a support bundle is a file the person reads and sends themselves.
- **Encrypting sessions now.** Deferred: without a reviewed key-wrapping design for moving a session between machines,
  encryption would either break sharing or hold its key beside the data.
