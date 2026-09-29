# InterCat coverage ledger v2

Status: **frozen, and implemented for imported and live-recorded sessions**. `icat record` publishes a `LiveCapture`
epoch with its last generation, when every required loss counter was readable; otherwise that generation is published
without a ledger, and its coverage stays unknown. The generations a recording publishes while it records carry no
ledger: the epoch is not over, and coverage stays unknown until it is.

Revision 275 made this `coverage-v2`: a live epoch may state the readings it recorded between (§2), and speaks for
every reading between them. A `coverage-v1` file, which every earlier generation publishes, is read as a `coverage-v2`
file whose epochs state none, and nothing else about it changed.

This contract is §7.1's `CoverageInterval` and the "capture configuration epochs and health/loss ledger" of §10: what
a capture's sources could observe, over which readings, and what they are known to have lost. R21 is why it exists.
Coverage is stated independently from data, so an absence of records is never an observed zero. A session without
the ledger cannot tell a quiet mechanism from one it never collected, or a complete capture from one whose file
reported lost events. `journal-v1` holds admitted evidence only, and its counters for policy omissions and source loss
never enter the journal (§18.1). This file is where they are kept. ADR-018 records the decisions.

It owns no meaning of a metric. How a result states the coverage of its scope is `contracts/metrics-v1.md`'s.

## 1. The file

Each generation that admits evidence publishes one immutable `coverage-<generation:D10>.json` dependency of kind
`CoverageLedger` (code 6, `contracts/store-v1.md`). It is evidence about the capture, not a derivation of the
journal. Nothing can rebuild it, so it is kept the way the normalizer plan is:

- a re-derivation carries it unchanged;
- ordinary derived-file retention cannot release it;
- a journal-prefix release keeps it, because the capture it describes has not changed.

A generation that publishes none is **legacy**, and every coverage it is asked about is `UnknownCoverage` (§4).

The file is UTF-8 JSON. Property names are camel-case and enumeration values are §23 names. The contract name is
`coverage-v2`, or `coverage-v1` for a file written before it. A reader refuses any of the following:

- an unknown member or an unknown enumeration name;
- another contract name, or recorded readings in a `coverage-v1` file;
- one recorded reading without the other, a recorded start after its stop, or recorded readings on an import epoch;
- a count below zero, or a descriptor whose outcomes do not add up to what it delivered (§3);
- a duplicate descriptor or loss layer;
- no epoch, or more than 64 epochs;
- more than 4,096 descriptors in an epoch;
- a file over 1 MiB.

The manifest records the file's length and SHA-256; its bytes are not a cross-runtime canonical identity.

## 2. Epochs

```text
CoverageLedgerV1 = (contract, epochs[1..64])
Epoch = (epoch, acquisition, firstDeliveredNativeTicks?, lastDeliveredNativeTicks?,
         recordedFromNativeTicks?, recordedToNativeTicks?, collected[], deliveries[], losses[])
```

An **epoch** is an interval over which the capture's configuration and admitted sources were unchanged (§22,
"coverage epoch"). A capture that changes a profile, enables a provider or starts sampling opens a new one (P13). An
import is one epoch.

`acquisition` is how the epoch's evidence was acquired: `EtlImport` for a standalone file replayed through admission,
or `LiveCapture` for an owned session. It decides what an epoch can know. A file does not record which providers its
session enabled or with which keywords. A live capture knows.

The two delivered readings are the first and last native readings of **every** record the sources delivered, admitted
or not, on the capture's clock. They bound what the epoch can speak for: before the first and after the last, coverage
is `UnknownCoverage`. An epoch whose sources delivered nothing has neither reading.

The two **recorded** readings are a live capture's: its capture epoch, which it reads once every source is enabled,
and the reading at which it asks its session to stop. A capture states them only when its session then stopped while
delivery ran and delivered what it still held, so that nothing its sources raised between the two readings went
undelivered without a loss counter counting it (revision 273). Between them the epoch speaks as it does between its
delivered readings, and an epoch speaks for the union of the two spans: a quiet capture's start and end are covered,
and a live epoch that delivered nothing still speaks for its recording. An import, a `coverage-v1` file, and a capture
whose delivery had to be ended before its session stopped state none; before revision 273 a capture's last records
could be lost that way, which is why an older ledger is never widened to its recording.

## 3. What each epoch records

`collected` lists what the admission plan admitted, whether or not it delivered anything:

```text
Collected = (providerId, providerName, eventId, version, opcode?, mechanism)
```

`deliveries` lists, for every descriptor that delivered at least one record, what became of its records:

```text
Delivery = (providerId, eventId, version, opcode?, delivered, admitted, omission?, omitted, undecodable{reason: records})
```

- `admitted + omitted + Σ undecodable = delivered`, exactly.
- A descriptor whose records were omitted names why, as an `EN-OmissionReason`: `UnrequestedProvider`,
  `DescriptorNotAdmitted` or `DescriptorDenied`. Every record of one descriptor meets one policy, so one reason
  covers them.
- An unrequested provider is counted as one whole-provider omission with no event/version. Some ETL system records
  expose no provider GUID; their empty GUID is allowed only in this omitted bucket, never as a collected descriptor
  or admitted record. This keeps the delivered count without inventing a source identity.
- A classic kernel record is counted under the class its header carries, with event id 0, whether or not the plan
  admits the class (ADR-035). A class the plan does not admit is one unrequested provider of its own. A decoder's
  name for such a record's provider depends on which of its parsers the consumer registered, so it is never the
  identity.
- `opcode` names a classic descriptor with its class, event id 0 and version, because a class has one id for every
  event: ALPC's send and receive differ only by opcode. It is 0-255, present on every classic descriptor and delivery,
  and absent otherwise, including on a whole-provider delivery. A file without it is read as it always was.
- An undecodable record is delivered under an admitted descriptor and cannot be read, or has an unknown
  descriptor version under a requested provider. It is counted by
  `EN-UndecodableReason`: `BodyShorterThanSchema`, `UnknownDescriptorVersion`, `AdmissionReadFailed` or
  `PointerWidthMismatch`.

A delivery names its descriptor by provider, event and version, with a classic descriptor's opcode, and assigns no
mechanism to an omitted descriptor. A file carries no event names, and a name looked up on the importing machine is
that machine's fact, not the capture's. A policy omission is also not a loss, and never changes a coverage state. It
is disclosed beside the state: evidence the policy chose not to admit, which a wider policy could.

`losses` lists what a layer reported lost and could not attribute to a descriptor:

```text
Loss = (layer, records)        layer: EN-LossLayer
```

`SourceSession` is loss the recording session reported. For an import this is the file's own lost-event count: the
records were never in the file, and nothing says which provider or which readings they belonged to. `CallbackQueue`
and `Storage` are InterCat's own bounded queue and writer, which a live capture fills. An import writes a
`SourceSession` loss of zero when the file reports none, because a reported zero is a fact and an absent entry is not.
Every import epoch requires that counter. A live epoch requires explicit, readable counters for `SourceSession`,
`ConsumerBuffers`, `CallbackQueue` and `Storage`, including measured zeroes; when a required counter is unreadable,
the runtime must not publish a ledger claiming coverage for that epoch. A live capture measures `CallbackQueue` as the
records its full queue dropped after the policy admitted them, and `Storage` as admitted records the writer never
journaled. Since revision 273 it stops its session before it ends delivery, so what the session's buffers still hold
is delivered rather than discarded uncounted, and it takes `SourceSession` and `ConsumerBuffers` from the stop's
answer: the events the session lost, and at least the real-time buffers its consumer lost, which ETW reports only
there. A dropped record is counted in no descriptor's deliveries, so each descriptor's outcomes still add up to
what it delivered.
Undecodable records are the `Decode` layer's loss, and they are attributed to their descriptor in `deliveries`.

No loss in this version is located in time. A later version that locates one adds an interval to it and leaves the
unlocated form as it is.

## 4. Coverage state

A mechanism's `EN-CoverageState` over an epoch follows from the facts above, checked in this order:

1. **`NotCollected`** when nothing in `collected` maps to the mechanism. No admitted descriptor could have recorded it.
2. **`UnknownCoverage`** when no record of a collected descriptor of the mechanism was delivered, and the epoch
   cannot show that its source was enabled. That is every `EtlImport` epoch: a file records neither enablement nor
   keywords, so a quiet mechanism and an unrecorded one look the same.
3. **`PartialGap`** when a loss of the epoch, in any layer, counts at least one record, or a collected descriptor of
   the mechanism has undecodable records. An unknown-version record cannot be assigned to an admitted mechanism;
   like an unlocated loss, it may belong to any collected mechanism and applies to each over the whole epoch.
4. **`Covered`** otherwise.

`ReducedFidelity` is not produced by this version. It is reserved for a profile that deliberately admits part of a
mechanism's evidence, such as sampling.

Outside every epoch's delivered and recorded readings a mechanism is `UnknownCoverage`. Coverage over an interval,
or over several mechanisms, rolls up to the **worst** state of what it spans on the ordered lattice `Covered <
ReducedFidelity < PartialGap < NotCollected < UnknownCoverage` (§10.3). It never takes an average. A legacy
generation is `UnknownCoverage` everywhere, and says that it publishes no ledger.

`Covered` states only what this evidence supports. For an import it means a collected descriptor of the mechanism
delivered records and the file reported no loss. It does not prove the file's session was enabled before its first
record or after its last, which is why an epoch is bounded by its readings.

## 5. What it does not do

- It holds no records and no rows. A count or a byte total is still read from segments; the ledger only states
  whether their absence means anything.
- It does not locate a loss in time, and it does not correct a rate for one (§19.2, R21).
- It does not describe thread, resource or operation coverage, which need derivations IC-015 does not have yet.
