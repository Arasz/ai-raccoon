# 0088. Code search surface — `kind`, the `results`/`code` envelope, no cross-corpus fusion

Date: 2026-08-21

Status: Accepted

Plan: `docs/work/2026-08-21-code-search-implementation-plan.md` (rev 3, §3.3, §3.6, §7 join
dispositions 3/9/13/14, §11 R1).

## Context

`memory_search` needed a way to reach the code corpus (ADR-0085) without breaking the promise
every existing caller already depends on: that `memory_search` with no new arguments behaves
exactly as it did before this feature existed. Three surface questions needed settling: what
selects which corpus gets searched, what the response looks like when a section is empty or
absent, and whether a code hit's score can be compared against a memory hit's — i.e. whether
the two legs ever fuse into one ranked list.

## Decision

1. **`kind: "memory" | "code" | "both"`, default `"memory"`.** Normalized like `scope`
   (lowercased, rejected fail-fast): `invalid-params: Invalid kind 'x': expected memory, code,
   or both.` `QueryGuard` and `QueryLengthGuard` apply identically regardless of `kind`; the
   code section's query is additionally trimmed to the manifest window (510 tokens — see Amendments)
   (`TrimQueryToWindow`), with its own code-budget warning distinct from the memory 254-token
   warning.
2. **Wire shape — `results` (the existing key) and `code` (new).** `kind=memory` serializes the
   **exact legacy envelope** — no `code` key at all, via a nullable `Code` property with
   `[JsonIgnore(Condition = WhenWritingNull)]`. `kind=code` → `{ results: [], code: [...] }`;
   `kind=both` → `{ results: [...], code: [...] }` — both keys always present for both/code,
   never partially omitted. Code hits carry `lineStart`/`lineEnd` in place of memory's
   `chunkIndex`/`totalChunks`. The compatibility promise is **semantic identity, not byte
   identity**: same keys, same order, same values, with `Meta.CorrelationId` excluded (two live
   calls are never byte-identical — the correlation id is per-call random). A golden
   `kind=memory` response is captured pre-feature and diffed against modulo the correlation id.
3. **No cross-corpus fusion.** Each section is ranked entirely by its own hybrid (FTS5 + vec0 +
   RRF) — a code hit's score and a memory hit's score are never merged into one ordered list,
   never compared to each other, and the two sections' `limit`/`minRelativeScore` resolve
   independently. This is a scope decision, not an oversight: fusing across corpora with
   different embedding models, different chunk shapes (line ranges vs. prose), and no shared
   relevance definition has no principled ranking to fall back on.
4. **`kind=code` + `scope=shared`/`workspace`** returns an **empty code section**, documented in
   the tool description — not a refusal. Code is always project-scoped; this asymmetry with
   memory (which supports all three scopes) is deliberate, not a bug the caller needs to work
   around.
5. **Per-call tuning (`rrfK`/`ftsWeight`/`vectorWeight`/…) applies to the code section too**,
   same defaults as memory — accepted per-call, no separate `codeRetrieval.*` settings
   namespace in v1. `retrieval.structureAlpha` is honored by the resolution path but **inert**
   for code (no structure/heading modality exists to weight).
6. **`code_get(projectId, hash)` mirrors `memory_get`**; an unknown hash is refused.
7. **Non-768 code manifests are refused at `model set code local` configure time** — the only
   dimension gate this corpus has. `vec_code` is fixed `float[768]`; unlike the memory bank's
   `model set local`, there is no post-hoc dimension-reconcile phase for code (ADR-0085's
   schema is fixed at creation, not migrated). The D3 dimension-reconcile machinery the engine
   plan built for memory is documented as the extension point for a future code-side reconcile,
   but is not exercised by this decision.
8. **`kind=code`/`kind=both` searches are excluded from `search_quality` recording** — the
   recorder is corpus-agnostic today and its rows sync off-machine; recording a code query would
   leak source identifiers/paths the same way an unstripped sync push would (ADR-0085's
   never-syncs rule, applied to the metrics side of the same concern). Memory searches
   (`kind=memory`) record exactly as before. Performance metrics still record for `kind=both`'s
   memory leg — the query-content hash is what gets omitted, not the whole measurement, since
   timing telemetry carries no code content. `Meta.CorrelationId` is likewise omitted from
   `code`/`both` envelopes: a later `memory_record_grade`/`memory_record_followthrough` call
   keyed on it would silently no-op against a `search_quality` row that was, by (8), never
   written — an unusable correlation id is a worse promise than none.

## Consequences

- **Positive**: every existing `memory_search` caller is unaffected — no new key appears, no
  reordering, no behavior change — because `kind` defaults to the value that reproduces today's
  contract exactly.
- **Positive**: the envelope shape answers "is a section present" unambiguously (`results`
  always present; `code` present iff `kind` asked for it), rather than callers having to
  distinguish "empty list" from "not searched" by inference.
- **Negative**: no unified ranked view across memory and code exists — a caller who wants "the
  single most relevant thing regardless of corpus" gets two separately-ranked lists and has to
  decide how to read them together. Accepted rather than solved (no cross-corpus fusion, above).
- **Negative**: the non-768 refusal is the *only* protection for `vec_code`'s dimension; a
  manifest that is wrong in some other way (bad tokenizer, wrong pooling) is not caught by this
  gate and surfaces as a runtime failure instead.
- **Not addressed**: this ADR does not cover corpus schema (ADR-0085), watch/ignore semantics
  (ADR-0086), or the drain mechanism (ADR-0087).

Extends ADR-0006 (parameter provenance pattern) and depends on ADR-0085 (the corpus this search
surface reaches) and ADR-0087 (the drain that determines when code vectors are actually
searchable versus FTS5-only).

## Amendments

### 2026-08-22 — the code window in Decision 1 is 510, not 126 (issue #422, PR #453)

Decision 1 above said "the 126-token manifest window". That number came from an exploration note
claiming code-daemon-embed-v1's ONNX graph caps at 128 tokens; the graph accepts 512 and fails at
513 (measurement on issue #422). The trim is 510 — `min(510, ctx − reservation)` for the model's
real window. **Scope of this amendment: Decision 1's number only.** Everything else in this ADR —
the `kind` parameter, the `results`/`code` envelope, the no-fusion ruling, the empty-section
behaviour for `scope=shared` — is unchanged and still in force.
