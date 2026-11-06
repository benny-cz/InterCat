# ADR-094: A process's package is a second text of its record, kept in a rented buffer and written as IAP3

- Status: accepted for M6's process enrichment; the package is admitted into a session's source fields, not yet said of a
  process or measured through the product
- Date: 2026-11-06
- Decision owners: InterCat maintainers
- Relates to: ADR-029, ADR-036, ADR-093, plan M6, §18.1, §18.2, R3, R8, R9, R11, I21, FX-PROC-001

## Context

FX-PROC-001 measured each process's package full name in Kernel-Process's process start and rundown, which every capture
collects, read by the registered layout past the mandatory label's SID (ADR-093). A record holds one bounded name, its
image, copied into 160 characters inline; the package follows that image, its checksum and its time stamp, so its offset
is the image's end, which no fixed offset gives. A second inline buffer would hold every queued record's room for a text
only process starts carry: at the queue's 262,144 records (ADR-081), about 80 MiB more of §12's acquisition budget for a
few records a second. And a metadata-only capture keeps a record's approved projection, never its source bytes, so a
package not copied in the callback cannot be read later.

## Decision

1. **A second text is a slot kind of its own, `TextAfterResourceName` (8).** It follows a name past a SID - the image -
   by a run of fixed fields, which the compiler sums from the layout: its offset is the SID's, as the name's slot's is,
   and its width that run, eight bytes for the image's checksum and time stamp. The compiler plans it only for the first
   UTF-16 string after such a name with only fixed fields between, only after the name itself, and for a field the catalog
   gives a meaning; Kernel-Process's start and rundown admit `PackageFullName` as `ProcessPackageFullName` (32), their
   eighth and last slot.
2. **The callback reads it bounded (R9).** The SID's own count places the name, the name's terminator its end, and the
   slot's width the text; each scan is bounded by the record's length, a name that runs past it making the record
   undecodable. A text with characters is copied into a buffer rented from the shared pool, owned by the record until the
   writer detaches it, as a content field's bytes are (ADR-036); an empty one, which the source states, is known and none;
   one the record ends before, one with no terminator in it and one longer than 128 characters - which no package full
   name is, at 127 at most - are unknown, never cut (I21).
3. **The projection's third form, `IAP3`, carries it.** It is `IAP2`, its address mask written always and naming none
   where the record holds none, with the second text after the extended-data counts: its UTF-8 byte length, then its
   bytes. A record without one is written as `IAP1` or `IAP2`, byte for byte as before, so every journal not carrying one
   is unchanged; a decoder refuses a text longer than a record keeps, and a build before this one refuses `IAP3` as a
   projection with the wrong marker rather than replaying it without its text.
4. **A package is its source field's text, and none the field's value 0.** A field's text is never empty in a segment,
   so a process in no package - which the record states - is the value 0, an observed none (R3); a record that carried no
   package says so as an unknown, and a record at a version declaring none has no row.
5. **The broker and the retained plan hold it.** The broker refuses a second text without the name slot at its SID, a
   second one, or one past more than 64 bytes of fixed fields; the retained plan reads the kind as one §23 defines.

## Consequences

- A session's source fields carry each process's package from its start or its rundown; no queued record without one
  grows, and the projection of every record without one keeps its bytes.
- Kernel-Process's start and rundown now admit their maximum of eight fields: a further field of theirs - the token's
  elevation or integrity level - needs a wider record first.
- Tests hold the compiled slot, the callback's bounded read of a package, an empty one and each unknown one, the IAP3
  round trip beside IAP1's unchanged bytes, the field row's text, value 0 and unknown, and the broker's refusals.
