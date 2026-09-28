# ADR-040: A capture records its clock against the wall clock, and its boot

- Status: accepted; revision 255
- Date: 2026-09-28
- Relates to: §8.1, §8.2, §8.3, M4, R3, R22, I22, ADR-039, `contracts/clock-calibration-v1.md`, `contracts/store-v1.md`

## Context

§8.1 asks captures to collect paired monotonic and wall-clock calibration samples with their acquisition uncertainty,
and §8.2 offers alignment from recorded wall clocks. No capture recorded any: a journal names its source clock, its
host and its capture epoch, so its readings are exact against one another, but nothing said what wall-clock time any of
them was, nor which boot of its machine it ran in. Two captures of one boot read one performance counter and so relate
exactly through their epochs; without the boot, that could not be told from two captures of two boots, or of two clones.

Windows counts its boots, but a count is no identity: a clone counts as its original did, so two clones booted equally
often would read as one boot, and claiming an exact alignment between two machines is the worst false claim §8.2 allows.

## Decision

1. **A live capture pairs its clock with the wall clock when it starts and when it stops.** Each sample reads the
   performance counter, the precise wall clock, and the counter again, keeping the tightest of sixteen tries; the pair's
   uncertainty is half the bracket widened by one counter tick, plus the wall clock's 100 ns resolution. It bounds how
   far apart the two readings were taken and nothing else: how right the wall clock was is a synchronization claim no
   sample makes (R3).
2. **A boot is named by a token the machine keeps only until it restarts.** The first capture of a boot mints a random
   token into a volatile registry key, `HKEY_LOCAL_MACHINE\SOFTWARE\InterCat.Boot`, which Windows deletes on restart;
   every later capture of that boot reads it. Captures naming one token ran in one boot of one machine; no clone, and
   no other boot, can name it. The key is created only by a process that may write the machine's registry - the
   elevated recorder and the broker - under a machine-wide mutex, and nothing else is ever written. Windows' boot count
   is kept beside it for people, never as an identity (R22).
3. **The calibration is evidence about the capture, kept like its coverage ledger.** It is a dependency of its own kind
   (code 10, `clock-calibration-v1`), published with the last chunk, carried by every later generation and by a
   follower, never released, and never in a redacted package, whose leak scan looks for its digest and boot token (I22).
4. **Nothing is assumed where nothing was recorded.** An import, a redacted package, a capture that ended before its
   last publication, and every capture before this revision carry no calibration, and a reader says so.

## Consequences

- Revision 255 records the calibration in `icat record` and the broker's captures and shows it in `icat session`, with
  the wall clock's rate against the counter over the capture.
- Revision 256 aligns a workspace's members by it: two captures of one boot exactly, others through their wall clocks
  under a synchronization bound a person states (ADR-039 decision 7).
- A capture that ends before its last publication records no calibration. Staging the start sample earlier, so a
  recovered capture keeps it, is a later refinement.
