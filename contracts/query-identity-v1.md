# InterCat query identity v1

Status: **frozen for the members `metrics-v1` implements, and implemented**. Graph projection, viewport and query
generation are defined by §10.4 and join this form when a UI publishes them (IC-017, IC-018); they never change
the bytes below.

This contract is §10.5's canonical analysis specification and query identity. A UI and the CLI must produce
byte-identical identities for the same request (R18), and every result names the snapshot, the revisions and the
specification it answers (I16), so the form is a contract rather than an implementation detail. It owns no
meaning: what a metric request means is `contracts/metrics-v1.md`'s. It owns how a request that means something is
written down, byte for byte, and hashed. ADR-015 records the decisions.

## 1. Identity

```text
QueryIdentity = (canonicalizationVersion, SHA-256(canonical specification bytes), requestedRows)
```

The canonicalization version is `1`. Its text form prefixes the hash - `v1:sha256:<64 lowercase hex>` - so a later
form can never collide with this one's cache entries or cursors. The hash is SHA-256 over the canonical
specification's UTF-8 bytes exactly, which any implementation can recompute from the printed form.

`requestedRows` is part of the identity and **not** of the hashed specification. It cuts a ranking of one aggregation:
the top 5 and the top 20 of the same total share every group, and a cache can serve both from one entry (§10.4).
Viewport and query generation are §10.4's other presentation components and are the UI's.

## 2. The canonical specification

UTF-8 JSON with no insignificant whitespace. Members appear in exactly this order, and an optional member that does
not apply is **absent**, never `null`:

| Member | Value | Present |
|---|---|---|
| `specVersion` | `1` | always |
| `snapshotVector` | the captures read, §3 | always |
| `versions` | the version axes the answer depends on, §4 | always |
| `basis` | `EN-Basis` name | always |
| `metric` | `EN-Metric` name | always |
| `rateNumerator` | `EN-Metric` name | a rate |
| `byteDomain` | `EN-ByteDomain` name | a byte metric, materialized |
| `accountingSide` | `EN-AccountingSide` name | a byte metric, materialized |
| `evidencePolicy` | `EN-EvidencePolicy` name | when any record is bound to a process, §5 |
| `timeScope` | `{"kind":"RetainedCapture"}`, or `{"kind":"AnalysisInterval","startTicks":"<n>","endTicks":"<n>"}` | always |
| `grouping` | `EN-Grouping` name | a grouped request |
| `filter` | `{"and":[terms]}`, §6 | when any term applies |

The request is **materialized** before it is written (`metrics-v1` §1): a metric's fixed domain and side and its
implied layer are written out, so leaving them implicit and writing them out give one identity. Names are §23's
enumeration names. Integers are decimal with no leading zeros; a native tick is written as a decimal string, because
a reading can exceed what a JSON number carries exactly; identities are lowercase hexadecimal without separators; no
floating-point value appears anywhere. Every string token is drawn from ASCII letters, digits, `:` and `-`, so the
form needs no escaping; a token outside that alphabet is a defect and is refused rather than escaped.

## 3. Snapshot vector

One entry per capture the answer reads, sorted by capture identity:

```text
{"captureId":"<32 hex>","generation":<n>,"manifest":"sha256:<64 hex>"}
```

The manifest digest pins the generation's exact bytes (`contracts/store-v1.md` §3). A capture and a generation
number alone do not: two sessions holding one capture can each reach generation 2 by different acts - a
re-derivation in one, a retention in the other - with different contents, and one identity would then name two
answers.

## 4. Version axes

`versions` names the §24 axes the answer depends on, in §24's order, and only those:

| Member | Value | Present |
|---|---|---|
| `normalizerContract` | the normalizer contract the segments were derived under, as its integer | always |
| `entityRevision` | the process binding rule, e.g. `process-binding-v2` | when any record is bound to a process |
| `correlationRevision` | the relation rule, e.g. `tcp-endpoint-relation-v1` | when the answer uses records' other ends |
| `metricsContract` | `metrics-v1` | always |

Entity and correlation revisions are named by their rule while derivations are computed on demand from one generation:
the generation, pinned by its manifest, and the rule decide the derivation completely. When revision tables are
persisted (IC-017), the revision they record replaces the rule name, under a new canonicalization version.

A total that binds no record to a process does not depend on the binding rule, and naming it would split one query
into two identities; the same holds for the relation rule. The answer depends on a relation rule when it has a
process focus other than a bare owner, a peer narrowing, a peer grouping, or a sent or received total grouped by
process or executable (`metrics-v1` §5, §6).

## 5. Terms that change nothing are dropped

An evidence policy decides which process bindings a result admits. With no process focus and no grouping by process,
executable or peer, no record is bound and every policy gives the same answer, so `evidencePolicy` is absent: a
mechanism grouping under `DirectOnly` and under the default is one query. Wherever a binding is used, the policy is
present and materialized.

## 6. Filter

The filter is a conjunction of terms sorted by `EN-FilterDimension` code (§23). This version writes three:

| Dimension | Code | Term |
|---|---|---|
| `ProcessInstance` | 2 | `{"facet":"ProcessInstance","<role>":["<instance>"]}`, with `"peer":["<instance>"]` after the role when narrowed |
| `Mechanism` | 4 | `{"facet":"Mechanism","include":["<name>"]}` |
| `Layer` | 5 | `{"facet":"Layer","include":["<name>"]}` |

The role is `owner`, `participant`, `sender` or `receiver`. A peer is a member of the process term, not a term of its
own: `peer(P,Q)` is relative to its focus and must never become an independent filter (§19.1). Time is the
specification's `timeScope`, not a filter term. Include values within a term are sorted by value code; this version
writes one value per term, because `metrics-v1` accepts one.

## 7. What a result carries

Every metric result that reads a generation carries its identity: a value, a grouping, or a reason it is unavailable,
because an unavailable answer is still an answer to one exact question over one exact snapshot. Only a generation
that publishes no derived segment has none, since it has no snapshot to name. A request `metrics-v1` refuses has no
identity: it means nothing. `icat metric` prints the identity with every answer, and `--print-canonical` prints the
canonical specification and identity without answering.

## 8. Golden corpus

`fixtures/FX-QUERY-001/golden/canonical-corpus.tsv` maps named specifications, over a fixed snapshot, to their
canonical bytes and identities, one line each: name, identity token, canonical specification. It covers every rule
above: a whole-capture count, an interval, an implied domain, an implied layer, an implied side, a rate, a grouping
that drops the policy, a grouping by process with rows outside the hash, an executable grouping with candidates, and
owner, participant-by-peer and sender-with-peer focus. The test recomputes each line and each hash; a change to this
file is a new canonicalization version or an ADR, never a test update.

## 9. Not defined at this version

- Graph projection, viewport and query generation (§10.4), which a UI publishes with IC-017 and IC-018.
- Multi-value include sets, negation, `is unknown` predicates and every other filter dimension. When one is added it
  follows §10.5's normalization - constant folding, no empty include set, negations at the leaves, sorted values and
  dimensions - under this version if the existing bytes do not change, and a new version if they would.
- Keyset cursors over a result's detail rows, whose validity is tied to this identity.
