# InterCat broker protocol v1

Status: **prepare, ownership/recovery, bounded dispatch, the OS-authenticated pipe boundary and the broker-owned filesystem root implemented; ownership-log compaction and live capture commands not yet enabled**.

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
- maximum duration, journal-byte allowance, minimum free-space reserve and stop-at-limit retention;
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

## 4. Implemented ownership and lifecycle core

`PreparedPlanRegistry` issues a 256-bit cryptographically random, base64url token for a deep-frozen
prepared plan. Only a SHA-256 token fingerprint is retained. The grant expires after 30 seconds by
default (configurable only within 5 seconds to 5 minutes), binds to the authenticated user SID and
logon-session ID, and is single-use. Unknown, expired and wrong-owner grants return the same refusal so
the registry is not an identity oracle. Restart invalidates every grant by design.

`BrokerLifecycleCoordinator` provides the transport-independent command semantics:

- start and stop require non-empty request IDs scoped to the authenticated owner;
- the store commits a start/stop intent before the capture runtime is called;
- the store commits the result before success is returned;
- an exact duplicate request returns the stored result and never calls the runtime twice;
- reusing a request ID for another command or target returns a conflict;
- a prepared token cannot start a second capture, even under another request ID;
- status, stop and renewal are owner-only; another owner receives no status record;
- interactive leases default to 30 seconds and are constrained to 10 seconds through 10 minutes;
- renewal before expiry writes a new deadline, but an expired lease cannot be revived;
- the expiry sweep requests stop for orphaned captures; and
- stop reports `requested`, `providersStopped`, `callbacksDrained`, `journalFinalized` and
  `analysisFinalized` independently. Replaying one partial request returns the same partial result; a new
  request may retry incomplete finalization.

The runtime is still an interface and tests use a fake. A completion-write failure never exposes start
success and triggers best-effort compensating stop; the stored intent remains for recovery.

### 4.1 Durable ownership log

`FileBrokerLifecycleStore` is the production persistence primitive for this state. Its directory must
already exist with its final broker-owned ACL; the store refuses a missing root, a root marked as a
reparse point, or an existing ownership file marked as a reparse point. The fixed filename is
`broker-ownership-v1.log`; no client path or filename is accepted.

Each mutation appends a complete snapshot frame containing all capture ownership and request-ID state.
A frame carries version, monotonic sequence, bounded payload length, SHA-256 over version/sequence/
length/payload, and a repeated-length end marker. The payload is at most 4 MiB, the log at most 64 MiB,
and snapshots at most 4,096 captures and 16,384 request records. The frame is flushed to the physical
device before in-memory state changes or success is returned. Prepared token secrets are never written;
only their SHA-256 fingerprints appear as start-request targets.

On reopen, recovery scans in sequence and validates the checksum, end marker, strict internal schema,
owner/session identity, lifecycle values and referential integrity of every frame. It restores the last
complete valid snapshot and truncates only the rejected tail. A nonempty file with no valid frame is
refused and left unchanged instead of being reset. This avoids treating filesystem rename as a power-
failure guarantee.

Every ownership record persists a unique session name and a separate random ownership token. Restart
reconciliation passes both to the runtime: an interrupted start is conservatively stopped and completed
as failed, pending and partial stops resume, an expired recording stops, and only a recording with an
unexpired owner lease is preserved. A matching name or capture ID without the token is insufficient.
The in-memory store remains available only as a deterministic behavior fixture.

## 5. Implemented wire protocol and dispatcher

The v1 command set is:

```text
Hello(protocol range, client instance, negotiated features)
GetCapabilities()
PrepareCapture(profile ID, approved overrides, quota, retention policy)
StartCapture(prepared plan token, request ID)
GetStatus(capture ID)
StopCapture(capture ID, request ID)
RenewOwnerLease(capture ID)
```

They use a bounded length-prefixed binary schema, not .NET object or unrestricted JSON deserialization.
The local named-pipe boundary uses first-instance semantics, an explicit protected DACL and remote
client rejection. Every connection is authenticated from its OS token (SID, logon session, integrity
and elevation); a supplied PID is diagnostic only.

### 5.1 Implemented frame and request schema

The stream frame is implemented independently of any pipe host. Its fixed 32-byte header contains
`ICBP`, protocol major/minor, message type, direction/error attributes, unsigned 32-bit payload length
and an RFC/network-order correlation GUID. Payload length is rejected above 64 KiB before allocation.
A clean EOF before a frame returns no frame; EOF after any header/payload byte is a truncation error.
Reads are correct under arbitrary stream fragmentation.

Payloads use bounded TLV fields: unsigned 16-bit field ID, type, required bit, signed 32-bit byte length
and value. A payload may contain at most 64 fields and each field at most 16 KiB. IDs are nonzero and
unique; lengths, fixed widths, Booleans, strict UTF-8, list counts and item lengths are validated before
use. An unknown optional field is skipped and still counts against the field limit; an unknown required
field is refused. No runtime/domain object graph is deserialized.

Typed request codecs exist for Hello, GetCapabilities, PrepareCapture, StartCapture, GetStatus,
StopCapture and RenewOwnerLease. Prepare accepts only a canonical profile ID, typed Focused/Content
settings, stop-at-limit retention, a 1-second-to-24-hour duration, a 1-MiB-to-1-TiB journal limit and a
smaller 16-MiB-to-1-TiB free-space reserve. Focused and Content groups are mutually exclusive and
Content's eight fields are all-or-none. Start tokens and request/capture IDs are shape-checked before
dispatch.

Hello uses protocol range plus requested/required feature bits. Unknown optional feature bits are
ignored; unknown required bits or no common major version are refused. The currently negotiable bits
are prepared-plan digest, owner leases, separate stop milestones and durable recovery.

### 5.2 Implemented response and dispatch schema

Every command has a bounded typed response. Hello returns the selected protocol/features and server
instance. Capabilities returns build/support/elevation identity plus parallel, count-checked profile and
mechanism summaries. Prepare returns either one complete owner-bound grant or one typed refusal, never a
partial combination, together with the exact effective profile/admission/scope disclosure the client
must compare with its reviewed preview. Per-source scope includes the capture-side process mode, bounded
applied PID group, wider-capture flag and reason. The prepared digest now binds the requested duration,
journal limit, free-space reserve and retention as well as provider/body/scope semantics.

Start, status, stop and renewal responses preserve the lifecycle operation code, lease and independent
stop milestones. Status is owner-only and never exposes the broker session name, session ownership token
or owner identity. Protocol errors use a separate error frame with an enum code, a control-free message
of at most 512 UTF-8 bytes and an explicit retry hint. Unknown optional response fields are skipped and
unknown required fields are refused under the same limits as requests.

`BrokerConnectionDispatcher` is per authenticated connection. It requires one successful Hello before
any command, permits a corrected Hello after negotiation refusal, refuses renegotiation after success,
preserves correlation IDs and rejects a duplicate ID while its first command is in flight. Unexpected
service exceptions produce one generic typed failure without exposing exception text. A local-only
preparation coordinator serializes metadata access, probes capabilities, compiles the effective profile
inside the broker, revalidates/freezes it, and only then issues the owner-bound prepared secret.

The dispatcher is transport-independent and currently exercised only in process with a fake
OS-authenticated connection and fake capture runtime.

### 5.3 Implemented Windows pipe authentication

The broker creates one byte-mode pipe named from a broker-generated instance GUID through
`CreateNamedPipeW`. Creation combines `FILE_FLAG_FIRST_PIPE_INSTANCE`, overlapped I/O and
`PIPE_REJECT_REMOTE_CLIENTS`; any pre-existing instance makes creation fail instead of attaching to an
attacker-controlled endpoint. Its protected DACL grants generic read/write only to Local System and the
configured capturing-user SID. The security descriptor is applied during native object creation, not
after a permissive pipe exists.

After connection, the broker calls `ImpersonateNamedPipeClient`, opens the thread token and derives the
canonical user SID, authentication LUID/logon-session ID, mandatory integrity level and elevation bit.
The SID and logon session must both match the configured owner. Anonymous impersonation is refused.
`RevertToSelf` is mandatory on every path; inability to revert after an authentication failure terminates
the process rather than continuing under the client token. `GetNamedPipeClientProcessId` is recorded only
as diagnostic data and never participates in authorization.

The authenticated connection processor reads only the bounded v1 frame codec, writes one correlated
response at a time and closes a connection after at most 4,096 commands. Windows fixtures exercise the
real token/pipe APIs, first-instance squatting, a machine-name/redirector connection refusal, wrong-logon
refusal and a complete Hello exchange. The broker executable remains fail-closed: it does not start this
listener until bounded ownership-log compaction and the real runtime cleanup boundary are also composed.

Prepared tokens will be unguessable, expiring broker records bound to the authenticated SID/logon
session and the prepared digest. A token is distinct from the digest. Start/stop request IDs will be
idempotent, and ownership will be durable before success is exposed. Owner leases stop orphaned
interactive captures by default; bounded headless captures use an explicit owner and duration/retention
policy. Disconnect alone is neither successful stop nor authorization to take ownership.

### 5.4 Implemented broker-owned filesystem root

The broker writes only beneath one validated root. `WindowsBrokerRoot.Provision` creates it with
`CreateDirectoryW` and a security descriptor supplied at creation, so no permissive directory exists at
any instant. The production descriptor is
`D:P(A;OICI;0x1f01ff;;;S-1-5-18)(A;OICI;0x1f01ff;;;S-1-5-32-544)(A;OICI;0x1200a9;;;<capturing user>)S:(ML;OICI;0x1;;;S-1-16-12288)`:
full control for Local System and Administrators, `FILE_GENERIC_READ | FILE_TRAVERSE` for the capturing
user, and a high-integrity `no-write-up` mandatory label. A capturing user who is already a broker
principal keeps that single full-control entry; the label, not a second weaker ACE, is what refuses
their ordinary-integrity writes.

The root is validated from the open handle rather than from the path that was requested:

- the parent must exist, must not be a reparse point, must resolve to the configured path in its long
  form, and in production must be owned by Local System or Administrators;
- the root's final component must not be a reparse point, its volume must be the parent's, and
  `GetFinalPathNameByHandleW` must return the configured path, which also catches an ancestor junction;
- the security read back through `GetSecurityInfo` must be exactly the declared descriptor, compared as
  parsed facts rather than as text, because Windows may reorder inheritance flags and render a
  well-known SID as an alias without changing what is granted. An access abbreviation the parser does
  not resolve is unknown, never none.

The handle is then held for the broker's lifetime with `FILE_SHARE_READ | FILE_SHARE_WRITE` and no
`FILE_SHARE_DELETE`, so the validated directory cannot be renamed or deleted under later writes. A root
that already exists is adopted only when a trusted principal owns it; drifted security is re-applied
from the same declared descriptor and validated again, and an untrusted owner is refused with the owner
named rather than repaired. A broker below the requested label refuses to provision instead of labelling
lower, and production provisioning refuses an unelevated broker.

Every broker file is opened through the root by a single validated name: ASCII letters, digits, `.`,
`-` and `_` only, at most 64 characters, no leading `.` or `-`, no `..`, and never a reserved device
stem. Each open uses `FILE_FLAG_OPEN_REPARSE_POINT` and then refuses a reparse point, a directory, a
foreign volume or a final path other than the expected one. Append mode is refused because it hides
where a broker write landed. `FileBrokerLifecycleStore` now takes the validated root instead of a path,
so the ownership log cannot be pointed at a directory whose security was never checked.

Windows fixtures measure this against real objects: a real junction substituted for the root and for the
parent, a rename attempt against the held handle, a pre-existing directory with inherited security being
repaired and revalidated, and a token duplicated and lowered to prove that an ordinary-integrity caller
still reads the evidence and is refused every write.

## 6. Threat model and current non-capabilities

Assets are the elevated provider/session controls, admitted event data, broker-owned output directory,
capture ownership and the requesting user's consent. Relevant attackers are another local user, a
same-user ordinary-integrity process, a remote pipe client, malformed imported data and a stale or
compromised client connection.

Protocol v1 must reject remote clients, identity/session mismatch, unsupported commands or versions,
oversized frames, expired/wrong-owner tokens, duplicate starts that would create another session,
foreign session stop requests, arbitrary paths and provider/body settings not produced by the broker's
allowlisted compiler. Imported archives never invoke this protocol merely by being opened.

The current slice has no bounded ownership-log compaction and no ETW/journal runtime binding.
`InterCat.CaptureBroker` returns a failure exit code when launched. Decoded requests, prepared tokens and
durable lifecycle/recovery operations are connected through real authenticated pipe fixtures but still
use a fake capture runtime. Those
absences are deliberate: a live UI control must not appear until authentication, directory security and
real cleanup behavior are implemented and tested together.
