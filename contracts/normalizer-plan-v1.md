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
descriptors or a file over 1 MiB are refused. The manifest records the file's exact length and SHA-256
digest; its bytes are not a cross-runtime canonical identity.

Before replay, the plan must match every schema and policy in the journal's tables. Each record's stream,
provider, event, version, schema reference and admission policy reference must match its selected plan.
The decoder then reads only the bounded approved projection; it does not use current TDH metadata,
reopen the original ETL, or treat a matching PID as a process identity. A differing fingerprint or
missing plan is a refusal, not a best-effort parse.

The plan is an admitted-evidence companion, not a rebuildable index. Ordinary derived-file retention
cannot drop it. An explicit journal-prefix release retains the plan with the suffix. A legacy session
without one needs a separate migration that proves an exact descriptor match; none is implemented.
