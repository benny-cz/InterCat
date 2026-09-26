# live-follow-v1

`live-follow-v1` is a viewer's note of where a live session's evidence is, kept while it follows a capture, so that a
later launch can finish a follow that ended early (plan §3.1 step 6, ADR-027). It is not evidence and belongs to no
session. It names the broker-owned evidence session and the user's derived session, and it says until when the broker
may still be recording.

## 1. Location and lifetime

The ticket is the file `<session directory>.follow.json` beside the derived session directory, never inside it: a
session directory holds only the files its store names (`contracts/store-v1.md`).

- A follow writes its ticket when it knows both directories, before it mirrors the first chunk.
- The follow holds the ticket open for reading and writing and shares it only for reading. A process that can open a
  ticket for writing therefore knows no follow holds it. A running follow's ticket cannot be removed by another process.
- Each owner-lease renewal rewrites `ownerLeaseExpiresUtc` with the expiry the broker returned (broker-v1). A renewal
  whose rewrite fails leaves the earlier expiry, which can only make a later launch wait less.
- A follow that ends early releases its ticket and leaves it in place. That includes a crash, an interruption, a close
  whose finish timed out, and a stop that left published chunks unfollowed.
- The ticket is removed once the session holds everything the capture will publish, or when the user forgets it.
  "Everything" is the capture's finalization marker (`contracts/capture-finalization-v1.md`), or every chunk of a
  capture that settled unfinalized (§3). Forgetting removes only the ticket, never the session or the evidence.

## 2. JSON contract

The UTF-8 JSON object is at most 64 KiB:

```text
{
  "contract": "live-follow-v1",
  "captureId": <non-empty GUID>,
  "evidenceDirectory": <absolute path>,
  "sessionDirectory": <absolute path, without a trailing separator>,
  "startedUtc": <DateTimeOffset>,
  "ownerLeaseExpiresUtc": <DateTimeOffset>
}
```

A file is not a ticket, and is ignored rather than repaired, when it is empty or larger than the bound, does not
parse, has unknown members, names another contract, has an empty capture ID or a relative path, or names a session
directory other than the one its own file name is beside. The last rule means a ticket can only ever send a finish
into the session beside it.

## 3. What a later launch finds

A launch assesses each unheld ticket from what the session and the evidence hold, reading only. A capture is
*settled* once `ownerLeaseExpiresUtc` plus a finalization allowance of 60 s has passed: the broker stops a capture
within seconds of its lease lapsing and finalizes it within a few more, so a capture still unfinalized after that
never will be, as when the broker or the machine stopped first.

| The session | The evidence | Settled | State |
|---|---|---|---|
| holds the finalization marker | any | any | Complete |
| lacks it | cannot be read | any | EvidenceGone |
| lacks it | directory absent | no | StillRecording |
| holds chunks | directory absent | yes | EvidenceGone |
| holds none | directory absent | yes | Complete |
| lacks chunks | finalized | any | Finishable |
| lacks chunks | not finalized | no | StillRecording |
| lacks chunks | not finalized | yes | EndedUnfinalized |
| holds every chunk | not finalized | no | StillRecording |
| holds every chunk | any other | yes, or finalized | Complete |

An evidence directory does not exist until the capture's first publication, so an absent one is waited for until the
capture settles. A finalized capture the session holds every chunk of cannot be finished further, because finality
comes with a capture's last chunk.

## 4. Finishing

A finish takes the ticket as a follow does, so two launches cannot finish one capture at once. It follows the evidence
into the session with the ordinary follower, which resumes from what the session holds and refuses evidence that is not
the session's own or that released chunks the session needs (ADR-027). It removes the ticket when the session holds the
finalization marker, or when the capture has settled and the session holds every chunk it published. Otherwise the
ticket stays for the next launch. A cancelled or failed finish keeps what it derived.
