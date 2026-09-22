# InterCat broker protocol v1

Status: **prepare handoff implemented; transport, authentication, leases and live capture commands not yet enabled**.

This contract freezes the first IC-014 boundary: the privileged broker can turn a locally compiled,
startable effective capture plan into a deep-frozen prepared plan with a deterministic identity. The
current executable still refuses to run as a service and exposes no start command. Nothing in this
document makes the prepared-plan digest an authorization credential.

## 1. Trust boundary

The wire-level `PrepareCapture` request contains only the reviewed profile ID and bounded typed
overrides, quota and retention policy. It never contains provider names, GUIDs, levels, keywords,
event IDs, body offsets, schema fingerprints or a serialized `EffectiveCapturePlan`.

The broker performs capability discovery and profile compilation inside its own process using the
installed adapter. The `BrokerPrepareCompiler` consumes only that local result. Accepting an effective
plan deserialized from an ordinary-integrity client would let the client choose privileged provider and
body-admission settings; it is therefore outside protocol v1 and must remain rejected.

An ordinary client may compile a read-only preview before elevation. After broker preparation, it must
compare the returned effective summary/digest with the state the user reviewed. A difference blocks
start and returns to review; it is never accepted as a silent fallback. The prepared-plan digest proves
identity of bytes within the broker. It is not a MAC, bearer token, user identity, consent receipt or
proof that the plan came from the broker.

## 2. Implemented prepare handoff

Preparation currently accepts a local `EffectiveCapturePlan` plus the broker runtime identity and
returns exactly one of:

- a `PreparedCapturePlan` whose nested collections are deep-copied into immutable arrays; or
- a typed `BrokerPrepareRefusal`, with no partial plan.

The broker refuses preparation when any of these conditions holds:

- the plan is not startable, the build is outside the measured support matrix, or required plan data is
  absent;
- build ID, architecture or adapter version differs from the running broker;
- requested/effective profile or admission differs, or the effective settings no longer match the
  catalog profile;
- a required source is missing, an unlisted source appears, descriptor/body-policy structure is invalid,
  or the provider request differs from the adapter allowlist;
- source-level process scope, aggregate wider-capture state and recorded consent disagree;
- content or separately governed original evidence is requested; or
- the body policy is not the exact reviewed metadata-only policy.

The implementation intentionally validates the capture-affecting result again before freezing it. It
does not repair, downgrade, substitute or reorder semantics to make an invalid plan startable.

## 3. Prepared-plan digest

The digest is `sha256:` followed by 64 lowercase hexadecimal characters. Its input starts with the
UTF-8 domain separator `InterCat.Broker.PreparedCapturePlan` and protocol version `1`, then covers:

- compilation UTC ticks, build ID, architecture and adapter version;
- requested/effective profile and admission;
- body policy, retained-byte bound and extended-data allowlist;
- extended-data and stack settings;
- requested/effective mechanism, selected and initial-view PIDs, aggregate broader-capture state,
  consent and every per-source process scope;
- every admitted source, descriptor identity, schema fingerprint and bounded slot definition; and
- every provider GUID/name, level, keyword mask, event allow/deny list, process filter, capture-state and
  stack request.

Human summaries, diagnostics, field-capability explanations and disclosure prose are not digest input:
they do not change capture behavior and may be localized. Their semantic decisions are covered.

### 3.1 Canonical encoding

The digest preimage uses these rules so collection order and JSON formatting cannot change identity:

- signed integers and unsigned keyword masks use fixed-width little-endian binary encoding;
- a Boolean is one byte (`0` or `1`);
- a string is a little-endian signed 32-bit UTF-8 byte count followed by those bytes, without a BOM;
- a GUID is its 32 lowercase hexadecimal `N` string encoded by the string rule;
- nullable enums are a presence Boolean followed, when present, by a signed 32-bit enum value;
- UTC time is signed 64-bit .NET UTC ticks (100 ns since 0001-01-01T00:00:00Z);
- every list begins with a signed 32-bit element count;
- sources sort by source index then ordinal source ID; descriptors sort by event ID then version;
  providers and scope decisions sort by ordinal source ID; numeric ID lists sort ascending; and
- slot order is preserved because it is part of the compiled bounded projection.

Duplicate provider/event/process/extended-data IDs are rejected or normalized only after validation of
the locally compiled plan. Reordering a semantically unordered input list produces the same digest;
changing selected PIDs or any capture-affecting value produces a different digest.

## 4. Planned wire and ownership contract

The remaining v1 commands are:

```text
Hello(protocol range, client instance, negotiated features)
GetCapabilities()
PrepareCapture(profile ID, approved overrides, quota, retention policy)
StartCapture(prepared plan token, request ID)
GetStatus(capture ID)
StopCapture(capture ID, request ID)
RenewOwnerLease(capture ID)
```

They will use a bounded length-prefixed binary schema, not .NET object or unrestricted JSON
deserialization. The local named pipe will use first-instance semantics, an explicit DACL and remote
client rejection. Every connection will be authenticated from its OS token (SID, logon session,
integrity and elevation); a supplied PID or nonce is diagnostic only.

Prepared tokens will be unguessable, expiring broker records bound to the authenticated SID/logon
session and the prepared digest. A token is distinct from the digest. Start/stop request IDs will be
idempotent, and ownership will be durable before success is exposed. Owner leases stop orphaned
interactive captures by default; bounded headless captures use an explicit owner and duration/retention
policy. Disconnect alone is neither successful stop nor authorization to take ownership.

## 5. Threat model and current non-capabilities

Assets are the elevated provider/session controls, admitted event data, broker-owned output directory,
capture ownership and the requesting user's consent. Relevant attackers are another local user, a
same-user ordinary-integrity process, a remote pipe client, malformed imported data and a stale or
compromised client connection.

Protocol v1 must reject remote clients, identity/session mismatch, unsupported commands or versions,
oversized frames, expired/wrong-owner tokens, duplicate starts that would create another session,
foreign session stop requests, arbitrary paths and provider/body settings not produced by the broker's
allowlisted compiler. Imported archives never invoke this protocol merely by being opened.

The current slice has no named pipe, prepared token, start/stop operation, lease, broker-owned directory
or durable ownership record. `InterCat.CaptureBroker` returns a failure exit code when launched. Those
absences are deliberate: a live UI control must not appear until authentication, ownership,
idempotency, expiry and cleanup behavior are implemented and tested together.
