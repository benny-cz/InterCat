# InterCat redacted session package v1

Contract: `intercat-redacted-normalized-session-v1` · Policy: `normalized-session-redaction-v1` · Plan §11.3 / I22

Status: **implemented and verified**: `RedactedSessionPackage` in `InterCat.Application`, `icat package --redacted`, and the
Desktop's "Share redacted session…" action. It is the second of §11.3's three export presets. The first, the
metadata-only report, is `intercat-share-report-v1` ([`SHARING-REPORT-REDACTION.md`](../docs/design/SHARING-REPORT-REDACTION.md)).
The third, an explicitly unredacted original evidence package, is not implemented.

## 1. What a package is

A package is a **new session directory** that every InterCat reader opens as an ordinary `store-v1` session: `icat
session`, `overview`, `evidence`, `raw`, `metric`, `processes`, `export`, `compact` and the Desktop. It is built from one
verified generation of a source session, and it reproduces what every analysis concludes from that source: the same
process instances, parents, bindings, relations, channels, groupings, totals, timeline, coverage and loss. Only the
values that name or locate something are replaced.

It is **not re-derivable**. Its journal holds synthetic records, not the source's evidence, and it carries no normalizer
plan. Its rows are the package's evidence; `icat rederive` refuses it and says why.

It is **pseudonymized, not anonymous**. Relative times, sizes, counts, status codes and the shape of the workload remain,
and can identify a system or its activity. The policy file, the CLI and the Desktop all say so before and after a
package is made.

## 2. Identity and files

A package publishes exactly one generation, generation 1, with:

- a new random session id, capture id, clock id and host id. None is derived from the source, so no package can be
  matched to its source, or to another package of the same source, by identity;
- `sourceIdentity` = `intercat-redacted-normalized-session-v1`;
- exactly these dependencies:

| Name | Kind | What it holds |
|---|---|---|
| `journal-0000000001.icatj` | `Journal` | One synthetic record per row (§5) |
| `seg-0000000001-NNNN.icats`, `dict-…` | `Segment`, `Dictionary` | The projected `observation-v1` rows (§3) |
| `fld-0000000001-NNNN.icats` | `Segment` | The projected `source-fields-v1` rows (§4), when the source had any |
| `coverage-0000000001.json` | `CoverageLedger` | The source's `coverage-v1` ledger under package pseudonyms (§6), when it had one |
| `redaction-policy-0000000001.json` | `RedactionPolicy` (code 8) | The policy and counts (§7) |

A normalizer plan, a capture-finalization marker, an index or any other kind is never published. The directory holds
nothing else: no README, no source file, no staging leftover.

## 3. Rows

Every member of a row is set by name, so a column a later version adds is left out until a policy revision admits it.

| Kept exactly | Replaced | Set to a constant |
|---|---|---|
| Mechanism, layer, kind, direction | Resource name (§3.1) | Raw stream and epoch: 1 |
| Session-relative time | Header PID and TID, owner PID (§3.2) | Raw ordinal: the row's position, 1-based |
| Byte value, domain, side, unit, availability | Addresses and ports (§3.2) | Journal index: ordinal - 1 |
| Status code and availability | Activity, related-activity, source identifier | Fact key: `redacted-normalized-observation-v1` |
| The four quality levels | Provider (§3.3), schema fingerprint | Processor number: 0 |
| Event id, descriptor version, opcode | Native reading (§3.4) | Markers other than name truncation: cleared |

The resource-name truncation marker is kept: a pseudonym of a truncated name is still a prefix's pseudonym. The
extended-data and body markers are cleared, because they describe evidence the package does not hold.

### 3.1 Names

A name is pseudonymized a path component at a time. The text before the last `\` or `/` is a folder, the rest a file name,
and each maps to one token; the pseudonym joins them with the separator the source used. No separator, or one at the very
end, makes the whole value a file name; a separator first makes it a rooted name with no folder. This mirrors how an
image name is read from a path, so:

- the file name of a pseudonymized image path is the pseudonym of the source's file name, which is also what an exit
  record's bare image name maps to, and a node keeps its label;
- process image names and folders are keyed case-insensitively, as executable grouping is, so paths that grouped together
  still do; pipe and other resource names are keyed exactly.

Tokens are `executable-<8 hex>` (keeping only a `.exe`, `.com` or `.scr` extension), `pipe-<8 hex>`,
`resource-<8 hex>` and `folder-<8 hex>`, by the row's mechanism. Every token is random, unique in its package and unlike
any source name.

### 3.2 Numbers and endpoints

Each namespace is a bijection on the values the source holds: equal values share one pseudonym and distinct values get
distinct ones, so every relationship an analysis reads by equality survives. A pseudonym is never one of the source's own
values. Values that mean the same thing on every Windows machine are **fixed points**, kept exactly, because replacing
them would change a conclusion:

| Namespace | Fixed points | Pseudonyms |
|---|---|---|
| Process and thread ids, including parent and issuing-thread fields | 0, 4 (System), -1 | Multiples of 4 in [1,000, 100,000,000) |
| IPv4 addresses | 0.0.0.0, 127.0.0.0/8, 255.255.255.255 | 240.0.0.0/4, reserved and never routed |
| Ports | 0 | 1024-65535, never a well-known port |
| Activity, related-activity and source identifiers | the empty identifier | random version-4 identifiers |
| Start sequences, connection ids, request packets, file objects and keys | 0 | random, 16-byte aligned for kernel objects |

Address 0 and port 0 are fixed points because a relation treats them as an incomplete endpoint: a pseudonym would turn
an unpaired record into a complete endpoint and change its peer reason. Loopback stays loopback. A package refuses a
session whose distinct ports leave no room for distinct pseudonyms outside the ports it uses.

### 3.3 Providers and schemas

The public Microsoft providers InterCat's catalog admits keep their identity - Kernel-Network, Kernel-Process, RPC,
TCPIP, Kernel-File and Kernel-Memory - because it is the same on every machine and discloses nothing about one. Every
other provider gets a random identifier and is named `provider-<first 8 hex of it>`, the same name wherever it is shown.
Every schema fingerprint, which can identify a Windows build, becomes `redacted-schema-<16 hex>`; equal fingerprints
share one.

### 3.4 Clock

The package clock keeps the source's kind, encoding, tick rate, rounding and plausibility bound, under new clock and host
ids. Its capture epoch is 0 - or, when a timed reading precedes the source's epoch, the smallest whole number of seconds
that keeps every timed reading non-negative - and every native reading moves by the same amount. Session-relative time is
exact; the boot-relative or wall-clock reading that would date the capture is gone. A reading that cannot be moved is
refused rather than altered.

## 4. Source fields

A field row joins its observation by the package's locator. What happens to its value is an allowlist:

| Field | Value |
|---|---|
| Process terminal session, RPC procedure number, RPC protocol sequence, file byte offset | kept |
| Parent PID, issuing thread | process and thread id pseudonym |
| Process start sequence, parent start sequence | start-sequence pseudonym, one namespace for both |
| Connection id, request packet, file object, file key | kernel-object pseudonym, one namespace |
| Process create and exit time (wall-clock FILETIMEs), any text, any field this policy does not name | withheld, marked `Redacted` |

A field the source did not supply keeps its availability reason. A parent named by PID is the same pseudonym as that
process's own records, and a parent's start sequence the same as its start key, so the process tree survives.

## 5. The journal

Each row has one synthetic `journal-v1` record: the row's pseudonymous provider, event id, version and opcode, its
pseudonymous header PID, TID and activity ids, its moved native reading, no body (`NoBody`), no extended item, admission
policy `normalized-session-redaction-v1`, and zeroes for everything else. The schema table interns each pseudonymous
descriptor once. The original-record drill-down therefore resolves only into the package, and its readers call the entry
a synthetic record, never an original: `icat raw` and the Desktop's record window say so.

## 6. Coverage ledger

When the source published a ledger, the package publishes it again with every epoch, acquisition, count, omission reason,
undecodable reason and loss unchanged, providers pseudonymized as in §3.3, provider names taken from §3.3 rather than
copied, and delivered readings moved as in §3.4. Every coverage state and reason is the source's. A source without a
ledger gives a package without one, whose coverage is unknown as the source's is.

## 7. The policy file

`redaction-policy-0000000001.json` is built only from this contract's constants and counts, never from a source value.
It names the contract and policy, when the package was made, its provenance ("made from a verified InterCat session",
whose identity is deliberately not recorded), the warning, the pseudonym scope, `false` for original sources, raw
locators, payload bytes and re-derivability, the retained, pseudonymized, redacted, omitted and fixed-point lists, and
counts of rows, fields and pseudonyms issued per namespace. A reader refuses an unknown member, another contract or
policy, and any claim that original sources, locators or payload are included. Retention cannot release the policy,
and a replacement generation carries it (`store-v1`).

## 8. Verification before publication

A package is written into a `<name>.partial-<id>` directory beside the destination and renamed onto the destination only
after it passes all of these. A failure or a cancellation publishes nothing, removes the partial directory and leaves
the source untouched.

1. **Reopened as a recipient would**: the generation verifies, it is the package's own identity, and the directory holds
   exactly its inventory.
2. **Every reference**: one journal, one policy, the ledger only if the source had one, the clock the rows are on, every
   segment on the package capture, clock and derivation, every row's journal index and reading matching its record.
3. **Every value**: each record and row value is a kept value, a fixed point or a pseudonym this package issued; every
   dictionary entry is an issued name or a package schema entry; every source field obeys §4; the ledger names only
   package providers; the policy's counts are the package's.
4. **Reproduction**: the package reproduces the source's tallies - rows by classification, sums of every kept number,
   presence of every pseudonymized value, the exact fixed points per row, and how many distinct values each namespace
   holds. A mapping that merged or split values, or moved a fixed point, is caught here.
5. **Bytes**: every file except the policy (step 3 compares it byte for byte) is searched for the source's session,
   capture, clock, host and provider identities in binary and text forms, its manifest and file digests, its schema
   fingerprints and its source-identity text. Journal, segment and dictionary files are also searched for source names and
   path components: in UTF-16 when a name has at least six characters, and in UTF-8 when it holds a character generated
   text never does (a capital, a space, a colon, anything outside `a-z 0-9 - . | \ /`). A needle is searched only where it
   cannot occur in text the package generates, so a match is a leak and never a coincidence; a shorter or
   generated-looking name is left to step 3, which checks every text value the formats can hold.

## 9. Limits

- At most 1,000,000 rows. Pseudonym tables and the source-field join grow with a session's distinct values; a larger
  session is refused with the bound named, before anything is written.
- One capture on one clock. A session whose segments name more than one is refused.
- A package is not built from a package: the second would read as evidence of an unknown source.
- IPv6 addresses are not retained by `observation-v1` at all, so there is nothing to pseudonymize.
- A pseudonym is consistent only inside one package. Two packages of one session share none.

## 10. Where it is made

- `icat package <session> --redacted --output <new-directory> [--check] [--json] [--report <path>]`. `--check` measures
  and writes nothing. The destination must not exist and must not overlap the source.
- Desktop: "Share redacted session…" in the inspector, for a saved or stopped session. It states what is kept, replaced
  and left out, asks where to put the new folder (`intercat-redacted-session-<local time>`, numbered rather than reused),
  shows progress and can be cancelled from the same button, then offers to open the package to review what a recipient
  will see. An open package says so in the status card, the header, the health strip and the disclosure; the record
  action reads "Open synthetic record".
