# Capture profile preview contract, version 1

Status: M1 additive CLI contract for IC-012
Owner: `InterCat.Capture.Windows`
Produced by: `icat profiles <profile> --json`

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
| `sources[]` | Every requested source with `required`, `Included`/`Omitted`/`Blocking`, exact reason, overhead class and evidence. |
| `providers[]` | Exact level, keyword masks, descriptor filters, rundown and stack settings that an owned session would request. |
| `originalEvidence` | Separate diagnostic-ETL request, availability, start decision, storage boundary and content warning. |
| `preserveExtendedData` / `requestCallStacks` | Capture-affecting settings, stated independently. |
| `diagnostics[]` | Non-silent schema degradations and refusal details. |

Keyword masks are fixed-width hexadecimal strings. Optional unmeasured sources may be omitted with a
reason. A required source failure, an unavailable profile, or an unsupported original-evidence request
sets `canStart` to false; the compiler never substitutes Explore or metadata-only capture for another
request. `--diagnostic-etl` describes original, potentially content-bearing evidence. It is never the
sanitized journal and is never labelled redacted.

The human rendering uses the same compiled object, places required sources first, and states that the
command starts no capture. JSON data goes to stdout and progress goes to stderr.
