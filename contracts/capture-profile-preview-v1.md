# Capture profile preview contract, version 1

Status: M1 additive CLI contract for IC-012
Owner: `InterCat.Capture.Windows`
Produced by: `icat profiles <profile> --json`, including focused requests such as
`icat profiles focused-transport --mechanism tcp --pid 4242 --json`
and bounded request-only previews such as `icat profiles content <scope-and-budget-options> --json`.

`icat profiles --json`, with no profile named, prints the catalog instead: `capture-profile-catalog-v1`, `contract` and
`profiles`, each capture intent as the catalog describes it. Until plan revision 303 it printed the profiles alone, an
array that named no contract.

A profile preview is a read-only answer to “what would this exact request enable on this machine?” It
reads provider schemas but starts no session and enables no provider. Requested and effective settings
remain separate so an unavailable source or body mode cannot become a silent fallback.

## Top level

| Field | Meaning |
|---|---|
| `schemaVersion` | `"1"`. |
| `compiledAtUtc` | When schema and policy compilation completed. |
| `environment` | Operating system, exact build, architecture, support tier, elevation and machine scope. |
| `adapterVersion` | Version of the catalog, schema compiler and admission behavior that produced the preview. |
| `requestedProfileId` | The profile the caller chose. |
| `effectiveProfileId` | The profile that can start unchanged, or `null` when anything required is blocked. |
| `requestedAdmission` / `effectiveAdmission` | Requested body mode and the enforceable mode; effective is `null` on refusal. |
| `canStart` | Whether the exact request can start. It never means a capture was started. |
| `collectionStatement` | Plain-language statement of what is and is not collected. |
| `bodyPolicy` | Compiled policy identifier, body shape, byte bound, original-byte flag and permitted extended-data types. |
| `scope` | Requested/effective mechanism, requested PID focus, initial-view PIDs, broader-capture consent state, per-source process scope and a plain-language disclosure. |
| `content` | `null` except for a valid Content request. Records source/process/channel selectors, per-record/session limits, retention, separate inspection consent, fixed truncation/unknown-schema behavior, approved body contracts and exact availability blockers. |
| `sources[]` | Every requested source with `required`, `Included`/`Omitted`/`Blocking`, exact reason, overhead class and evidence. |
| `providers[]` | Exact level, keyword masks, descriptor filters, provider-enforced `processIdsToInclude`, rundown and stack settings that an owned session would request. An empty PID list means whole-machine provider delivery. |
| `originalEvidence` | Separate diagnostic-ETL request, availability, start decision, storage boundary and content warning. |
| `preserveExtendedData` / `requestCallStacks` | Capture-affecting settings, stated independently. |
| `diagnostics[]` | Non-silent schema degradations and refusal details. |

Keyword masks are fixed-width hexadecimal strings. Optional unmeasured sources may be omitted with a
reason. A required source failure, an unavailable profile, or an unsupported original-evidence request
sets `canStart` to false; the compiler never substitutes Explore or metadata-only capture for another
request. `--diagnostic-etl` describes original, potentially content-bearing evidence. It is never the
sanitized journal and is never labelled redacted.

## Focused transport scope

Focused transport requires `--mechanism`; TCP is the only measured mechanism currently accepted. Repeated
`--pid` values request an initial process-focused view, but do not by themselves promise capture-side
retention scope. `scope.sources[]` states one of `WholeMachineRequested`, `ProcessFiltered`,
`WholeMachineRequiredContext`, or `WholeMachineFilterUnavailable` for every included provider and records
the actually applied PID filter separately.

If any source must collect outside the selected PIDs, `capturesOutsideRequestedProcesses` and
`broaderCaptureNeedsConsent` are true. The exact request has `canStart: false` until
`--allow-broader-capture` is present; after acknowledgement, `broaderCaptureAccepted` becomes true while
`initialViewProcessIds` preserves the requested focus. The compiler never represents a view filter as a
capture-side filter. A recognized but unmeasured mechanism is refused with the requested mechanism retained
and no TCP fallback.

## Content request-only preview

Content requires an exact `--source` and `--mechanism`, one or more `--pid` and `--channel` selectors,
`--max-record-bytes`, `--max-session-bytes`, `--retention stop-at-limit`, and an explicit
`--inspection disabled|hex-text` choice. PID values select the processes running when the capture starts:
a capture holds each open while it runs, so no other process can be given its ID, and a request naming a
process that is not running is refused (revision 245). Channel values are start-time selectors a capture
must bind to the resources it observes. Inspection is separate
consent: `hex-text` lets a person see a record's bytes when they ask, bounded, as inert hex and declared
text; joining a part's buffers, copying and saving are each a deliberate action of theirs
(`contracts/content-v1.md` §4), and nothing searches, decodes, runs or actively renders the bytes.

A request compiles into a production policy only for a source with its own validated content contract,
which a reviewed admission policy covers. Since revision 239 that is WinINet's capture (ADR-037),
requested for its processes with the one channel selector `*`: `canStart` is true, `effectiveAdmission`
is `ScopedContent`, `bodyPolicy` is `scoped-content-request-v1`, `providers[]` enables the provider for
the requested `processIdsToInclude` alone, and `content.admissionPolicyAvailable` is true. Its
`collectionStatement` says what the source's bytes are before anything is recorded: for WinINet, that an
HTTPS exchange is kept as its plaintext, headers, cookies and authorization included (revision 243).
`icat record --profile content` starts such a request; the broker refuses it, since its evidence follower
does not mirror content.

For any other source the preview is request-only: `canStart` is false, `effectiveAdmission` and
`bodyPolicy` are null, `providers[]` and approved content events/fields/classifications are empty, and
`content.admissionPolicyAvailable` is false, with the exact blockers. Longer records would retain only a
bounded prefix with original length and truncation recorded; the session would stop before exceeding its
content-byte cap; unknown schemas and out-of-scope bodies would be omitted before persistence. For such a
source these are reviewable requested rules, not claims that the adapter can enforce them.

A source can become available only through its own validated content contract: approved descriptors,
fields and classifications; process/channel enforcement before persistence; and impact evidence measured
with the payload-producing settings. Metadata-only impact evidence is not reused for content.

The human rendering uses the same compiled object, places required sources first, and states that the
command starts no capture. JSON data goes to stdout and progress goes to stderr.
