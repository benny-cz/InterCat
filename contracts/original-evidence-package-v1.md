# original-evidence-package-v1

Status: **implemented** (revision 154). This is the third of §11.3's sharing presets, and the explicitly unredacted
one. The metadata-only report is `intercat-share-report-v1`, and the reopenable redacted session is
`contracts/redacted-session-v1.md`.

## 1. What it is

An original evidence package is an exact copy of one session's current generation, in a directory of its own. Every
InterCat reader opens it as that session: the same session ID, generation and manifest digest (`contracts/store-v1.md`).
Nothing in it is rewritten, pseudonymized or left out of the generation it copies.

## 2. What it holds

- Every file the generation names, byte for byte: journal chunks, segments, dictionaries, the coverage ledger, the
  normalizer plan, the finalization marker, and a redaction policy when the session is itself a redacted package.
- The generation's manifest, byte for byte.
- `current-generation.json`, naming that manifest.
- An empty `session-evidence-lease.lock`. It is the guard a reader's lease takes, so a copy on a read-only share can
  still be read.

It holds nothing else:

- no last-known-good pointer, and no earlier generation to fall back to;
- no superseded manifest;
- no staging, orphan or unrelated file;
- no file the session was imported from, such as an ETL.

## 3. How it is made

1. The source generation is held by an evidence lease for as long as it is copied (`contracts/store-v1.md` §8).
2. Each file is hashed as it is copied. A file whose bytes are not the ones its generation recorded is refused before
   the rest is copied. This finds a file that changed in place after the source was opened, which a lease that trusts
   its earlier measurement would not.
3. The package is built in `<destination>.partial-<id>` beside the destination. A fresh store reopens it, checking the
   pointer, the manifest's digest and every file's length and digest. It must open at the copied generation and digest,
   with nothing rolled back and nothing unreferenced.
4. Only then is it renamed to the destination. A cancelled or refused package leaves nothing under that name and
   removes its partial directory.

The destination must not exist, and it must neither lie inside the source nor contain it.

## 4. What is stated before it is written

`icat package <session> --original --check` and the Desktop's confirmation both state the following before anything is
written:

- the generation it copies, its records and source fields, and every file with its kind and length;
- that it is unredacted: process and executable names and image paths; process, thread and session IDs; parent links,
  start and exit times; local and remote addresses and ports; resource names, kernel object values and RPC procedures;
  timestamps as recorded, with the capture's clock and host identities; and every admitted record;
- that InterCat records metadata only, so no message content is included;
- the host identity the journal names.

A session that is itself a redacted package (`contracts/redacted-session-v1.md`) is copied the same way, and its copy is
that package as it is. Revision 208 makes what is stated say so: in place of the unredacted contents and warning, the
copy is described as the package's pseudonyms and synthetic records, never the original values, under the redacted
package's own warning, and neither the command line nor the Desktop calls it original or unredacted. The Desktop offers
it as "Share this package…", and does not offer to build a redacted package from one, which §9 of that contract refuses.

The Desktop's confirmation starts on Cancel, and nothing is written until the user chooses where the new folder goes.

## 5. What a recipient sees

The same session. The journal is the admitted evidence, and re-derivation, the evidence list and the original-record
view work as they do in the source. A recipient's reader verifies the package as it verifies any session.
