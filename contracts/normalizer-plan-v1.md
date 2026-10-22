# InterCat retained normalizer plan v1

Status: implemented for newly imported sessions. A legacy generation without this dependency is not
silently reinterpreted from the viewing machine's current schemas.

`journal-v1` stores provider/event/version fingerprints and policy references, but a fingerprint is not
the field layout it identifies. Re-deriving its approved metadata projection requires the exact compiled
descriptor interpretation that admission used: slot roles and transforms, source-field meanings, layer,
mechanism, direction and body policy. Every new ETL-backed session therefore publishes one immutable
`normalizer-plan-<generation:D10>.json` dependency of `DerivationPlan` kind alongside its journal.

The file is UTF-8 JSON with `contract: "normalizer-plan-v1"` and a `descriptors` array. Each descriptor
is the complete `AdmittedEventPlan` value, ordered by source index, event id and version. Enum values are
numeric and property names camel-case. Unknown JSON members, an unknown contract name, duplicate stream
or provider/event/version keys, unsupported body policy, missing required members, more than 1,024
descriptors or a file over 1 MiB are refused. So, since revision 719, is a descriptor whose records could not be
written as rows: one carrying a mechanism, layer, kind, direction, role, slot kind, source field, availability, domain
or unit code §23 does not define; a byte slot without its domain or unit; a reported field said not to apply; or
both IPv4 and IPv6 endpoint addresses. The catalog compiles none of these. The manifest records the file's exact
length and SHA-256 digest; its bytes are not a cross-runtime canonical identity.

Before replay, the plan must match every schema and policy in the journal's tables. Each record's stream,
provider, event, version, schema reference and admission policy reference must match its selected plan.
The decoder then reads only the bounded approved projection; it does not use current TDH metadata,
reopen the original ETL, or treat a matching PID as a process identity. A differing fingerprint or
missing plan is a refusal, not a best-effort parse.

The plan is an admitted-evidence companion, not a rebuildable index. Ordinary derived-file retention
cannot drop it. An explicit journal-prefix release retains the plan with the suffix. A legacy session
without one needs a separate migration that proves an exact descriptor match; none is implemented.

`icat session` reports manifest-level re-derivation readiness and the exact refusal reason. “Can attempt”
means one committed journal and one bounded retained plan are named by the verified manifest; it is not a
promise that replay will succeed. Schema, policy, batch and record-count validation occurs during
`icat rederive`, before publication. An invalid replay leaves the current generation unchanged.

`icat rederive <directory> --check` is a read-only full replay. It checks the plan and journal SHA-256
again on the handles it reads, then validates each frame and record against the saved interpretation,
normalizes each observation and source field, and compares the total with the committed boundary. It
stages no files and publishes no generation. Passing means this evidence can be read and normalized by
the current implementation; it does not guarantee a later segment write or commit will succeed. Plain
`icat rederive` uses the same replay path before publishing a replacement generation.

## What a capture admits (revision 543)

A capture publishes its plan with its first chunk and its coverage ledger (`coverage-v2`) with its last. Until a
generation's ledger is published - while its capture records, or when it never finished - a reader takes what the
capture collects from the plan: the mechanisms of its descriptors. The window draws an RPC call to the process that
served it, counts the links under a brush and keeps them in a checkpoint when they include ALPC (`operations-v1`
§5c). Once a ledger is published it decides, whatever the plan admitted. A plan that names another contract, as a
later writer's may, says nothing of what it admitted, so its RPC edges wait for the ledger; one that is not JSON,
names no contract or, naming this one, lacks its descriptors' mechanisms is refused rather than read as admitting
nothing. A generation that names no plan says nothing of what it admitted, and one that names two is refused.

## Classic kernel descriptors (revision 221)

A classic kernel event, such as ALPC's send and receive (ADR-035), has no manifest. Its descriptor's `providerGuid`
is its event class, its `eventId` is 0 and its `version` is the class's, as its header has them, and an `opcode`
member names which of the class's events it is: 33 an ALPC send, 34 a receive. The opcode is part of the descriptor's
key, so the stream/event/version and provider/event/version keys above are unique with it, and a record's plan is
selected by its header's opcode too. A class's descriptors at one version share one journal schema entry, so they must
share one layout fingerprint, and a file where they do not is refused. A manifest descriptor has no `opcode` member,
so its plan file is exactly what it was before this revision.

A record that two descriptors both select - one with no `opcode` beside one naming the record's - is refused as an
interpretation the plan does not give, naming the record, as one that no descriptor selects is (revision 718). Before,
such a record failed with an error that named no record and that a follow or a rebuild did not treat as damaged
evidence.

## IPv6 address slots (revision 174)

A descriptor may admit up to two IPv6 endpoint addresses (ADR-029). Such a slot has kind 6, `Address128`, width 16, and
transform 4, `NetworkOrderIpv6Address`: the callback copies the address's 16 bytes whole, in network order, into one
of the record's two address slots, numbered in the order the plan lists its address slots. A plan whose descriptor
names more than two address slots is refused, here and by the broker, because a record holds two.

The approved metadata projection of a record holding an address is `IAP2`. It is `IAP1` with one addition after the
identifier: a mask byte of the addresses the record holds, then each one's 16 bytes in network order. Any other record
is projected as `IAP1`, byte for byte as before, so a journal of IPv4 records is unchanged. A decoder refuses an
`IAP2` mask that names no address or one a record cannot hold. A build before revision 174 refuses an `IAP2` record
as a projection with the wrong marker, rather than replaying it without its addresses.

## Pointer width (revision 180)

A user-mode provider raises a record at the pointer width of the process that raised it, so a 32-bit process's
records arrive at width 4 on a 64-bit system. A descriptor's `pointerWidthIndependent` member is true when every
admitted field lies before any pointer-sized field and is not one itself: its offsets are the same at either width.
Such a plan admits a record of either width, and the record's own width is its envelope's `pointerSize`; any other
plan admits only its own `pointerSize` and counts the rest as undecodable (`PointerWidthMismatch`). Replay accepts an
envelope's width by the same rule. A plan written before revision 180 has no member and reads as false, as it
admitted. A build before revision 180 refuses a plan that has it, as an unknown member.
