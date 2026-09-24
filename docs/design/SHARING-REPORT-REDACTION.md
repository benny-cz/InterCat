# Metadata-only sharing report redaction v1

Contract: `intercat-share-report-v1` · Policy: `share-report-redaction-v1` · Plan §11.3 / I22

This is a derived **report**, not a reopenable session or evidence package. Its sole output is a JSON or CSV file.
No original ETL, journal, segment, dictionary, body, extended-data bytes, source-to-token mapping, raw locator or
reference to an original source is attached. The ordinary `intercat-export-v1` JSON/CSV is a detailed inspection
export and is **not** a sharing format. §11.3's reopenable redacted normalized session is a separate preset with its own
contract, [`redacted-session-v1`](../../contracts/redacted-session-v1.md); choose it when the recipient should explore the
session rather than read one view of it.

## Allowlist and transformations

| Subject | Retained | Omitted or transformed |
|---|---|---|
| Report context | New random report ID, rung, half-open session-relative interval, export time, completeness, count of applied filters | Original session/capture ID, generation, breadcrumb, scope prose, filter names/values/reasons, source caveats |
| Ranked rows | Count, known byte total, mechanism, coverage, accounting side, descent level | Key/label/detail replaced by one random `entity-*` token keyed to row identity within this report |
| Evidence records | Relative presentation ticks, mechanism, layer, kind, direction, numeric byte/status values with domains/availability, quality and owner-binding classifications | Raw record ID/ordinal, journal/segment coordinate, native clock, provider/schema IDs, header/owner PID, machine/user/executable/resource names, endpoint address and port, original files |
| Relationships | Random `record-*`, `process-*`, `executable-*`, `endpoint-*`, `resource-*`, `identifier-*`, `activity-*` tokens | Original identifiers and any mapping back to them; only policy-admitted process instances receive owner tokens |

Tokens use 96 cryptographically random bits, collision-checked within a report, and are generated afresh for every
export. Equal values of the same kind map to one token *within* that report; mirrored endpoints and related
activities therefore remain linkable there. Tokens are not intended to be stable across reports. No raw value is
serialized as a token seed or label. CSV repeats the policy, included/omitted disclosures and warning on every row;
an empty scope writes one context-only row with `row_present=false` so the policy is still present.

The renderer uses dedicated allowlisted DTOs and never serializes an `ObservationRowV1`, `SessionEvidenceRecord`,
`LadderRow` or `ExportContext` directly. Any newly added source field is omitted until a deliberate policy revision
and leak test admit it. The CLI flag is `icat export --share-redacted`; the Desktop uses a separate button and
confirmation before the save picker. Both save complete UTF-8 files through adjacent staging.

## Limits and reviewer checklist

Pseudonymization is **not anonymity**. Counts, sizes, precise relative timing, mechanism mix, status codes and graph
shape can fingerprint a workload. An interval and the export time may reveal when it ran. Review the report before
sharing it, especially when the audience should not learn workload behavior. The policy does not promise resistance
to external auxiliary data or multiple reports of the same capture.

Before widening this contract: prove every added field cannot contain names, IDs, addresses, content or a locator;
extend adversarial JSON/CSV leak tests; confirm CSV is still safe to open in a spreadsheet; preserve truthful
completeness and byte-domain labels; and review the disclosure text in both CLI and Desktop. This report's safety claim does not cover the redacted session
package, and the package's does not cover this report: each has its own allowlist, tests and verification
(`redacted-session-v1` §8).
