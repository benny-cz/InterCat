# Capability report contract, version 2

Status: M0 contract for `capabilities/<build>/<adapter>.json` (plan §21.2 item 1)
Owner: `InterCat.Capture.Windows`
Produced by: `icat capabilities`, and by `icat measure` when a run supplies runtime evidence

A capability report states what a source *could* supply on one machine and what has actually been
proven there. It is evidence, not marketing: every field an adapter cannot prove is present with a
reason rather than absent (R21, P1).

## Top level

| Field | Meaning |
|---|---|
| `reportVersion` | `"2"`. A reader that does not know the version refuses the artifact (§23). |
| `probedAtUtc` | When the probe ran. |
| `environment` | `operatingSystem`, `buildId`, `architecture`, `isSupportedBuild` (§1.3 matrix), `isElevated`, `machineScope`. |
| `adapterVersion` | Bumps when enablement, field mapping or admission behaviour changes (§24 `adapterVersion`). |
| `probeOnly` | `true` when no capture ran. A `probeOnly` report can never carry observed health. |
| `sources` | One `SourceCapability` per catalog entry (§4.3). |
| `mechanisms` | Per-mechanism roll-up with the computed tier (§14.2). |
| `diagnostics` | Probe-level notes, including registration breadth. |

## The four separate checks

§18.2 requires registration, enablement, observed health and validated semantic coverage to stay
distinct. Every source carries all four as `checks[]`, each with an outcome of `NotAttempted`,
`Passed`, `Failed` or `Inconclusive`, a detail string, and an `unavailableReason` when it failed.
`NotAttempted` is never rendered, exported or summed as a failure or as a zero.

## State mapping

| `state` | When |
|---|---|
| `Available` | Registered, schema read, admission plan compiled, and a capture observed matching records. |
| `Experimental` | Registered and schema read, but no capture has demonstrated emission or field semantics. |
| `SchemaUnknown` | Registered, but TDH metadata could not be read or interpreted here. |
| `Unsupported` | No registered source backs the mechanism on this machine. |
| `PermissionDenied` | Enablement was refused for a permission reason. |
| `ProviderFailed` | Enablement was refused for another reason. |
| `DisabledByProfile` | A registered source could serve it, but the active plan does not request it. |

## Fields and descriptors

`events[]` lists only the descriptors the profile admits. Each field carries its
`EN-FieldAvailability` value, its role, its schema input type, and, where it backs a measurement, its
unit and byte domain (R2). A field is `Present` only when a bounded admission read can resolve its
offset; a field behind a variable-length field is `SchemaUnknown` with the blocking field named, and
a field the schema does not declare is `NotExposed` (§18.3). Non-admitted descriptors are counted in
`schema.declaredEventCount` but their fields are not expanded, which keeps the artifact bounded (R8).

## Tiers

`mechanisms[].tier` is computed by `CoverageTierCalculator` from `measurement` counters only. Without
a measurement the tier is `Unsupported` and `coverage` is `UnknownCoverage`, meaning *not yet
measured* rather than *no traffic*. A measurement taken on a build outside the §1.3 matrix cannot
promote a mechanism beyond `ExperimentalEvidence`, and the cap is stated in `tierAssessment.gaps`
(P27). Every threshold appears in `tierAssessment.criteria` with its numerator and denominator, so a
reader can recompute the tier; a criterion with a zero denominator is `not measured`, never a pass (R3).

## Compatibility

`reportVersion` bumps when a field is removed or its meaning changes; additive fields do not bump it.
Consumers preserve unknown fields rather than dropping them (§26.3).
