# ADR-027: A privileged recording publishes evidence only, and an ordinary process derives the session

- Status: accepted for M1 and M2
- Date: 2026-09-23
- Decision owners: InterCat maintainers
- Relates to: §9 (architecture, broker boundary), §18.1 (journal writer), §20.1, R16, P18, IC-014, ADR-021, ADR-022,
  ADR-023, ADR-026, `contracts/store-v1.md`

## Context

§9 draws live capture as a pipeline, and §18.1 asks for one journal writer per capture, owned by the broker:

- the privileged broker writes the authoritative admitted-event journal;
- *unprivileged* decode and normalize turns it into immutable observation segments;
- analysis reads committed batches.

R16 confines privileged work to capture, and P18 forbids decoding or querying inside the broker.

ADR-021's `LiveSessionRecorder` derives rows in the recording process. Since ADR-026 it also compacts, which reads
segments back. For `icat record`, run from a shell the user chose to elevate, that is acceptable. The broker must not
do it, and the dependency map already keeps the broker away from `InterCat.Capture.Journal`.

The plan also leaves a gap. The broker-owned directory carries a high-integrity no-write-up label (§9.2), so no
ordinary-integrity process can write derived segments there. §9 draws the derived store without saying where it lives.

## Decision

1. **A privileged recording publishes an evidence session.** It holds journal chunks, the normalizer plan and the
   coverage ledger, published through the ordinary commit protocol, with no rows and no segments. Nothing in it
   derives, reads back, queries or compacts. This is `LiveRecordingOutput.EvidenceOnly`, and
   `icat record --evidence-only`.
2. **An ordinary process derives the session into a directory of its own.** The follower (`icat follow`) takes each
   chunk the evidence session committed and:
   - mirrors it byte for byte, with its length and digest checked against the evidence manifest;
   - derives its rows there, with the same normalizer and capture-wide journal index an in-process recording uses;
   - copies the plan with the first chunk and the ledger with the last;
   - compacts as the recorder does.

   The derived session takes the evidence session's identity and is an ordinary session in every respect.
3. **The follower resumes and refuses.**
   - It continues from what the derived session already holds.
   - It refuses evidence whose chunks are not the ones it mirrored: another capture, changed files, or released
     evidence.
   - It refuses an ordinary session, which already has its rows.
   - It refuses an evidence session that has published nothing; `icat follow` waits for one.
4. **Plan correction:** the derived store of a broker-owned capture is the viewer's own session directory, not the
   broker's. The broker's evidence stays authoritative, and the derived session holds a verified copy of it.

## Consequences

- Measured live: an evidence-only Explore recording of 12 s, published every 3 s, produced 5 generations of 1,254
  records with nothing lost. `icat follow`, running beside it, mirrored each chunk as it was published: 607, 209, 265
  and 173 records, then the ledger's empty chunk. It then compacted and finished.
- The derived session's observations by mechanism (TCP 723, process lifecycle 453, UDP 78) equal the recorder's own
  coverage counts, and the session re-derives across its 5 chunks.
- Journal evidence is held twice while both sessions exist. The broker's copy is bounded by its quota. Releasing it
  once a follower has mirrored a finished capture is the broker's retention to design.
- The follower lives in `InterCat.Capture.Journal`, which the CLI references and the desktop does not. The desktop's
  follow-along needs it somewhere `InterCat.Application` can reach.
- Following across a release of the evidence's own chunks is refused rather than guessed. A broker that retains
  evidence while a viewer follows must coordinate the two.
- What remains of the broker binding:
  - a module the broker may reference, holding the evidence-only recording path; the dependency map keeps the broker
    from `Capture.Journal`, which also holds the ETL import parser;
  - per-capture directories in the broker root that the capturing user can read;
  - the `IBrokerCaptureRuntime` implementation;
  - the broker executable.

## Alternatives considered

- **The broker derives the session too, as `icat record` does.** Rejected by R16, P18 and §9's architecture.
- **Two-root sessions, whose derived store references the evidence root.** Rejected for now. Every reader, lease and
  command would need cross-root dependencies, and a derived session would stop being portable.
- **The broker writes derived files on the viewer's behalf.** Rejected. The broker would have to validate what it
  writes, which is parsing.
