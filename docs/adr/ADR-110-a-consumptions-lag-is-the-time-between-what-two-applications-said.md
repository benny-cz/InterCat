# ADR-110: A consumption's lag is the time between what two applications said

- Status: accepted for M7's lag between a publication and its consumptions (revision 1051)
- Date: 2026-11-08
- Decision owners: InterCat maintainers
- Relates to: ADR-102, ADR-104, ADR-106, ADR-108, plan M7, `contracts/sdk-v1.md` §2 and §9,
  `contracts/cooperative-messages-v1.md`, R3, R18

## Context

M7's review lists the lag between a publication and its consumptions among what is not built
(`docs/reviews/M7-release-review.md`). The SDK says a publication after its commit and a consumption after the read
(`contracts/sdk-v1.md` §2): each record is the application's word, said after the moment it names. A reader may therefore
consume a message, and say so, before its publisher says the publication - ADR-108 refuses messages as markers for this
reason. The time between the two records is not the message's latency, and it may be negative.

## Decision

1. **A consumption's lag is how long after its message's publication was said it was said**, in session time - the
   capture's one clock for both records. It is said in those words, "10.0 µs after its publication was said", never as how
   long the message took.
2. **A negative lag is kept and said as such**: "3.0 µs before its publication was said". It is no error and no
   contradiction - the reader said its consumption first, which the schema allows.
3. **A lag needs one publication.** A message published twice conflicts, and one whose publication was not recorded has
   none to be said after; their consumptions have no lag, never one taken from a chosen record (R3). A record without a
   session time gives none either.
4. **An epoch's lag is a spread**: the least, the median and the most over its consumption records - a batch's consumption
   once, however its run splits (ADR-106) - with how many were said before their publication.

## Consequences

- Revision 1051 builds each consumption's lag and each epoch's spread (`CooperativeMessageIndex`), says them in
  `icat messages` - a median column, each reader's lag, each listed epoch's spread - and its document, and in the window's
  Logical messages… in the same words (R18); 14 mutations of the derivation, the words, the command and the window
  are caught.
