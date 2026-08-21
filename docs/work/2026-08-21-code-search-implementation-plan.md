# Implementation plan — code corpus for AiRaccoon (code-daemon-embed-v1)

**Date:** 2026-08-21
**Task:** code-search-implementation-plan (**plan-only — this task finishes when the plan is
ready; no implementation in this task**)
**Status:** rev 1 (MoE lanes combined) → MoE review round → rev 2 (reviewed)

## 0. How to read this plan

This is the combined, reconciled plan from four parallel MoE lanes. The lane docs carry the
depth (every decision cites source `path:line`); this document is the authoritative contract:
scope, decisions, work packages, gates, and the TDD test mapping. Where lanes disagreed, the
disposition is recorded in §7 (join dispositions).

- Architecture lane: `docs/work/2026-08-21-code-search-moe-architecture.md` (D-A…D-M)
- Engineer lane: `docs/work/2026-08-21-code-search-moe-engineer.md` (D-E1…D-E12, §5 watch)
- QA lane: `docs/work/2026-08-21-code-search-moe-qa.md` (95 named test cases, WP1…WP8)
- Ops lane: `docs/work/2026-08-21-code-search-moe-ops.md` (phases P0…P5, §2-§3 surface/migration)

## 1. Scope and assumptions

**Feature:** semantic code retrieval in the same `memory.db` — a second, code-only corpus
(`code_entries` + `code_fts` + `vec_code float[768]`), fed by the existing watch/ingest
machinery, searched through `memory_search kind=memory|code|both` and `code_get`.

**Assumed already implemented** (engine-generalization plan, task
`support-for-other-embedding-models`, `docs/work/2026-08-21-arbitrary-embedding-models-plan.md`
rev 2): manifest-driven embedding engines (D1 — numeric special-token map, pooling modes incl.
`model-output`, tokenizer families), sentencepiece family (D5), transactional vec0 dimension
reconcile float[N] (D3), manifest chunk budget ctx−2 capped 510 (D6), fingerprint = manifest +
per-file sha256s (D7), `ai-raccoon model download <repo-id>` verb (D4/D8), `embedding.dimensions`
row (D2). This plan does not re-litigate those decisions; it builds on their contracts.

**Code model (verified by spike, exploration §1):** `faxenoff/code-daemon-embed-v1` — 768-dim,
INT8 QAT ONNX 187 MB, sentencepiece tokenizer (`<s>`=2 `</s>`=3 `<pad>`=0 `<unk>`=1), pooling +
L2 fused in the graph → `pooling.mode = model-output`, 128-token hard cap → chunk budget **126**
(ctx−2), symmetric (no query prefix), MIT. A/B candidate: `jinaai/jina-embeddings-v2-base-code`
(768-dim, Apache-2.0, ONNX int8 154 MB).

**Out of scope (v1):** remote code embedding (Voyage/OpenAI — deferred to v2, ops §2.2);
tree-sitter AST chunking (v2 lever, engineer D-E1); `codeRetrieval.*` tuning rows (named seam
only, architect D-J); code `search_quality` rows; chunk-index repair/backfill family for code
(re-derivable from disk, QA G10); `!` negation in `ai-raccoon.ignore` (additive later, §3.4);
workspace/shared scope for code.

## 2. Owner requirements (f:, binding)

1. **`ai-raccoon.ignore`** — a gitignore-style exclude file filters ingestion for BOTH
   pipelines. Contract: one file per tree root (`<watchRoot>/ai-raccoon.ignore`, also honored
   by `memory_ingest_directory`), gitignore-subset syntax (`*`, `**`, trailing `/` directory
   patterns, leading/anchored `/`, no `!` negation in v1), any-match-wins, case per host OS;
   ignored files are **never fingerprinted, never chunked**; digest on an ignored path deletes
   stale chunks (a file indexed before the ignore line was added must be cleaned) and updates
   `last_change_ts` only; the ignore file is never self-ignored; **editing it triggers a full
   re-scan of the watch** (via its own digest event + `IWatchScanInitiator`, single-flighted);
   reads: once per scan / per directory walk / per digest event, no cache. Pure `IgnoreRules`
   type in Core (static parse/match). New matcher is hand-rolled — verified: no glob machinery
   exists in `Watch/` today (engineer §5.2, grep 2026-08-21).
2. **No overlapping watches** — the watch whose scope contains the other wins; the included
   watch is pruned. Containment = `IngestPath.IsWithinScope(inner, outer)` (separator-aware
   real-path prefix — `/repo2` ⊄ `/repo`; already the digest-ownership predicate). Adding a
   broader watch prunes every contained watch (`RemoveWatchAsync` + `UnregisterWatch`, entries
   stay, fingerprints cascade-deleted, new watch starts with full catch-up); adding a narrower
   watch inside a broader one is **rejected** with `WatchOverlapException` naming the covering
   watch (nothing written); equal-path re-add stays an idempotent no-op; ordering in
   `AddAsync`: reject-if-contained → prune-contained → register.
3. **Repo watch by default** — registering a watch on a repo root applies rule 2 to every
   existing watch inside it, then catch-up scans the whole repo into the correct corpora.
4. **TDD test cases in this plan, authored by QA** — §6 + the QA lane catalog (95 named cases,
   each with behavior, RED witness, GREEN assertion, home, kind).

## 3. Design (reconciled)

### 3.1 Corpus schema (architect D-A/D-B)

New objects in the **unconditional digest-gated `Ddl` block** — purely additive `CREATE … IF
NOT EXISTS`, no ladder step, no `CurrentVersion` bump for the tables themselves (metrics-table
precedent, `MemorySchema.cs:335-342`; digest gate re-runs once per bank).

- `code_entries(id, hash, path, value, source_file, line_start, line_end, project_id,
  created_at, updated_at, embed_state, embedding BLOB, chunk_index, total_chunks)` +
  `uq_code_chunk UNIQUE(project_id, path, hash)` + indexes (project, hash, embed_state).
  Deliberately absent (with reasons): `scope`/`workspace_id`/`agent_id` (project-scoped only),
  `rating`/`ttl_days`/`access_count` (no degradation — code is a re-derivable cache),
  `heading_path`/`section`/`source_id` (no structure modality in v1).
- `code_fts` — FTS5 external-content over `(value, source_file)`, `content='code_entries'`,
  full trigger family (ai/ad/au) mirroring `entries_fts`.
- `vec_code` — vec0 `ctx TEXT, embedding float[768] distance_metric=cosine`, `ctx =
  project_id` (ADR-0068), full trigger family (au/pending/ad) mirroring `vec_entries`.
- Encryption-at-rest: inherited wholesale (bank-level).

### 3.2 Lifecycle boundaries (architect D-C)

Code never syncs (push strip — see §3.7 correction), never sweeps, no TTL, no promotion, no
workspaces. Watch removal deletes from BOTH corpora (entries stay — existing semantics).
Corpus is an explicit re-derivable cache: loss costs a re-ingest, not knowledge.

### 3.3 Settings and engine activation (architect D-D, ops §2.2, engineer §6)

- New rows: `embedding.codeModel` (manifest-activated local code engine) + `embedding.codeEngine`
  (fingerprint). Memory rows untouched; `settings model reset` never touches code rows;
  `settings model code reset` deletes only the code rows.
- Activation UX: `ai-raccoon model download faxenoff/code-daemon-embed-v1` (187 MB < 500 MB,
  no `--yes`; SHA-256 pinned) then **`ai-raccoon model set code local <dir>`** (new subcommand
  under the existing `model set` family, ADR-0076 outbox shape; deletes stale
  `embedding.codeEngine` before writing → fingerprint change → code re-embed).
- Two `InferenceSession`s in-process (23 MB MiniLM + ~50 MB code INT8). Code-engine change
  invalidates the code corpus only (QA G11); unloadable code engine degrades code search with
  an actionable error, memory unaffected (QA G12); **code re-embed drains per-corpus and never
  blocks memory tools** (ops §3.6 contract; drain while vectors pending → `kind=code` degrades
  to FTS5-only; H4: the existing outbox/lease machinery expresses a per-corpus drain).

### 3.4 Chunker and ingest (engineer D-E1/D-E2/D-E10)

- `CodeChunker`: line-range splitter — blank-line blocks with brace-balance boundary preference
  (cut at the last balance==0 boundary inside the budget, else budget wins), per-line hard-split
  floor via `TokenBudget.Trim`, exact joined-recount packing (ADR-0036), budget **126** (manifest
  ctx−2; the 510 cap never binds), overlay 0 (justified: line ranges, not prose continuity),
  emits `CodeChunk(Text, LineStart, LineEnd)` 1-based. No AST in v1.
- Extension registry: one constant `CodeExtensions` set (Core) + `CodeFileTypeMatcher`
  (case-insensitive, `FileTypeMatcher` normalize shape); memory `FileTypeMatcher` untouched;
  registries disjoint-by-test; **memory-wins runtime priority** for any overlap (`.md` inside a
  code dir → memory). `memory_ingest_directory` on a mixed tree feeds both corpora.
- Unconfigured code ingest counts with the bundled code-daemon sentencepiece tokenizer
  (626 KB) — preserves the ADR-0036 invariant (engineer D-E7; alternative o200k proxy rejected
  as the ADR-0036 drift defect class).

### 3.5 Watch channeling (engineer §5 — the critical case)

One watch row, **no digest-level classification, no new watch flag**: the digest executor's
existing replace/delete transactions gain corpus-agnostic **delete-BOTH + re-ingest-BOTH**
legs; each ingestor self-filters by its own matcher (classification would duplicate matcher
truth). Digest flow per event: rename → delete both; missing → delete both; ignored (rules
from §3.4.1) → delete stale chunks + `last_change_ts`, no fingerprint; hash-skip unchanged;
replace → delete-both + ingest-both + fingerprint in one tx; pending-embed drain for BOTH
corpora; `ai-raccoon.ignore` edit → re-scan via `IWatchScanInitiator` (no cycle). Catch-up
scan of a mixed tree: enumeration skips ignored files; `ReconcileIgnoredAsync` cleans
fingerprinted-but-now-ignored. Edge cases: 15 listed in engineer §5.6 (rename with extension
change, symlinked trees, transient overlap after crash mid-prune, file watch = no ignore
rules, etc.).

### 3.6 Search surface (architect D-I, ops §3.5; envelope disposition §7-2)

- `memory_search` gains `kind: "memory" | "code" | "both"`, default `memory`, validated
  fail-fast (`invalid-params: Invalid kind 'x': expected memory, code, or both.`). Per-section
  `minRelativeScore` and `limit`. QueryGuard + QueryLengthGuard apply identically; the code
  section's query is trimmed to 126 tokens (manifest-aware `TrimQueryToWindow`), with a
  separate code-budget warning.
- Wire shape: `SearchResultList` gains an optional `Code` section, serialized **only** for
  `kind=code|both`; `kind=memory` stays byte-identical (regression-pinned). `kind=both` →
  `{ results: [...memory...], code: [...] }`, both keys always present. Code hits carry
  `lineStart`/`lineEnd`; no cross-corpus fusion (each section ranked by its own hybrid).
- `code_get(projectId, hash)` mirrors `memory_get`; unknown hash refused.
- `kind=code` with `scope=shared/workspace` → empty code section (documented; code is
  project-scoped only).

### 3.7 Sync correction (ops §3.3 — lane-found design bug)

The exploration's "code excluded by construction" is **false for the push direction**: the
snapshot is a whole-bank `VACUUM INTO`; only settings + workspace rows are stripped. Code
tables would ship source + 768-dim vectors to the cloud. **Required:** `StripNonSyncableAsync`
deletes `code_%` tables from every pushed snapshot (mirroring the settings strip); ADR-0014
becomes "settings and the code corpus never sync". Pull/merge unchanged (merge names only
`entries`/`sync_tombstones`/`memory_source`). Gate: pushed snapshot contains no `code_%` table.

### 3.8 Maintenance (engineer §7, ops §3.6)

New ledger row `code-reindex` (model fingerprint change → code corpus re-embed via
`embed_state` invalidation triggers). No sweep/TTL/promotion jobs for code. `memory_stats` /
`memory_list` / `memory_performance` stay memory-only (QA G4); code counts surface via the
`code-reindex` ledger + metrics.

### 3.9 Evaluation (architect D-L, ops §5.1)

Reuse the existing harness unchanged (`evaluate.py` + `scoring.py`, binary relevance via
expectedHash/expectedSource). Code eval-set: 2–3 real repos vendored at pinned commits incl.
**a mandatory Python repo** (blank-line heuristic risk, H5); QA lane assembles, owner approves
repos. Arms: code-daemon vs jina-code-v2 (both 768 → re-embed only; jina pooling shape needs a
parity probe first — `emb_pooler: mean` suggests in-process mean) + chunker arm (heuristic vs
plain token-window, scratch-only MiniLM reference is NOT a gate). Acceptance floor: **mean
nDCG@5 ≥ 0.50**, pinned from first measurement, then witnessed RED against a deliberately bad
arm (ADR-0079/0081 precedent). Report must contain: per-query regression table, model A/B,
re-embed time, wall-time vs spike's 56 texts/s (H3). **The plan never flips defaults** — the
owner decides the champion from the report (engine-plan G0 precedent).

## 4. Migration (ladder; join disposition §7-1)

One feature version bump, **`CurrentVersion` 10 → 11**, one ladder step with two guarded
sub-migrations, both server-side:

1. **v11a — overlapping-watch prune** (ops §3.4): per project, keep the outermost watches,
   delete nested watch rows + cascade their `watch_files` (same transaction pattern as
   `WatchStore.RemoveWatchAsync`); **reported, not silent** — one Information log line per
   pruned watch (`watch overlap migration: removed <path> (covered by <path>)`) + pruned
   count. Stamped only on success. Same code path as the runtime rule (one implementation,
   two call sites).
2. **v11b — `model_migration` corpus column** (architect D-D): the migration outbox gains a
   `corpus TEXT` column ('memory'|'code') so code-engine changes drain per-corpus without
   touching memory rows. The separate `code_migration` table is rejected (derive-or-delete).

## 5. Work packages, gates, and TDD tests

Canonical WP numbering = QA lane's (test contract). Every WP: TDD, RED witnessed, gate named.

| WP | Deliverable (lane owner) | Gate | QA tests |
|---|---|---|---|
| WP1 | Corpus schema in `Ddl` + triggers + indexes; `embedding.codeModel` rows; `model set code local`; settings show/reset; ladder v11a+v11b (fixture bank with nested watches prunes, RED→GREEN) | existing-bank copy opens; `doctor` reports new tables; code rows round-trip; memory tables byte-identical; v11 migration RED→GREEN | WP1-T01…T08 |
| WP2 | `CodeChunker` (line-range, 126 budget, brace-balance, hard-split, no overlay) | chunker fixtures: budget ≤126, contiguous covering line ranges, edge cases; real-sentencepiece cap check | WP2-T01…T11 |
| WP3 | Extension registry + `CodeIngestor` + mixed-tree directory ingest; bundled sentencepiece counting | `.cs` → code rows with line_start<line_end + FTS + vec0; routing code/docs/neither; hidden files; scope refusal; pending→embedded; dedup | WP3-T01…T10 |
| WP4 | Watch channeling + `ai-raccoon.ignore` + overlap prune/reject + repo-watch-by-default; `WatchAddResult` gains `pruned`/`absorbedBy` (additive); ignore-change re-scan | watch fixtures: root-add prunes nested (result lists them, status shows winners); add-inside → `WatchOverlapException`; `.md` edit → memory only; `.cs` edit → code only; deletion removes from both in one tx; crash-mid-delete rolls back; ignored file → no rows in EITHER corpus; ignore edit → re-scan; `/repo2` not pruned by `/repo` | WP4-T01…T27 |
| WP5 | Code search: `CodeSearchService` (FTS5 + vec0 + RRF, project scope) + code-engine query embed + 126 trim + per-section limit/minRelativeScore | hybrid present; no shared/workspace leakage; weight-flip fusion; empty corpus; per-call tuning args apply to code section | WP5-T01…T10 |
| WP6 | `memory_search kind` + `code_get` + envelope | kind=memory byte-identical (regression guard); invalid kind refused; both keys present for both; code_get unknown-hash refused; QueryGuard applies; code-budget warning | WP6-T01…T12 |
| WP7 | Maintenance + non-interference: `code-reindex` job, per-corpus drain (memory never blocked), sync strip, sweep/promotion untouched | code-engine fingerprint change re-embeds code only; pushed snapshot has no `code_%` tables; sweep never sees code rows | WP7-T01…T08 |
| WP8 | Eval + provenance + BDD + docs: eval-set (2-3 repos incl. Python), registry pin, A/B arms, `code-corpus.feature`, ADR-0084, docs drift | floor nDCG@5 ≥ 0.50 pinned then witnessed RED vs bad arm; parity probe for jina; feature scenarios green; tool-count gate 28 | WP8-T01…T06 |

Regression guards (QA §3): memory behavior unchanged by default — every existing retrieval gate
and wire-shape test stays green; `kind=memory` byte-identical.

## 6. TDD test catalog (QA lane)

95 named test cases in `docs/work/2026-08-21-code-search-moe-qa.md`, mapped 1:1 to WP1–WP8
(§7 acceptance matrix). Each case: behavior pinned (one sentence), RED precondition (how the
failure is witnessed before the feature), GREEN assertion (observable), home (test class /
.feature scenario), kind (unit / integration / BDD). Key clusters: chunker budget + line
accounting (WP2-T01…T11), repo-wide watch channeling + ignore + pruning (WP4-T01…T27),
kind/both envelope + regression (WP6-T01…T12), non-interference (WP7), eval + provenance
(WP8). RED-witness logistics: §5 of the QA doc (which tests can run before their WP, which
need the feature; the intentionally-bad-arm RED for the eval floor).

## 7. Join dispositions (lane conflicts resolved in this combined plan)

1. **Ladder v11 ownership** — architect claimed v11 for the outbox corpus column, ops claimed
   v11 for the overlap prune. **Resolved:** one step, two guarded sub-migrations (v11a prune,
   v11b column) — both lanes' "only one bump" holds; the feature bumps 10 → 11.
2. **Envelope shape** — architect proposed an always-present `code` key; ops demanded
   byte-identical `kind=memory`. **Resolved:** optional `Code` section serialized only for
   `kind=code|both`; `kind=memory` byte-identical (ops wins, architect's strict-client
   fallback OQ1 adopted as the primary).
3. **`ai-raccoon.ignore` negation** — QA G17 recommended `!` negation in v1; engineer rejected
   it (re-include vs directory-pruning interaction). **Resolved:** no negation in v1
   (engineer wins; additive later, documented).
4. **Narrower-inside-broader** — QA G18 recommended reject; engineer designed reject.
   **Resolved:** reject with `WatchOverlapException` naming the covering watch.
5. **Repo-watch-by-default** — QA G19 recommended always-prune. **Resolved:** always prune.
6. **Per-call tuning args for the code section** (QA G6): accepted per-call, same defaults.
7. **Kind + scope interaction** (QA G15): `kind=code` + shared/workspace scope → empty code
   section, documented.

## 8. Risks (condensed; full table in ops §4)

Model provenance (registry pin + eval-before-default + jina A/B; never PTQ the INT8 QAT
artifact), chunker quality without AST (Python repo in eval-set; tree-sitter v2 lever),
throughput 56 texts/s (incremental watch deltas; initial index of a large repo is the only
wait), sync push leakage (fixed by strip, test-gated), re-embed blocking memory (per-corpus
drain contract, H4), binary files with code extensions (v1 accepts, matches memory path),
first-run overlap prune (reported, not silent).

## 9. Open questions for the owner (lane OQs consolidated)

1. Approve this plan (G0) — then implementation starts as a separate task.
2. Approve the code extension list (engineer OQ1, §4.1) — v1 languages.
3. Accept bundling the code-daemon sentencepiece tokenizer (626 KB) as the unconfigured
   counting tokenizer (engineer OQ4).
4. Confirm `memory_ingest_file` stays memory-only in v1 (engineer OQ2; watch + directory
   paths cover code onboarding) — or route code files there too (QA G5 recommends yes).
5. Approve the code eval-set repos (QA G14; owner picks the repos).
6. Whether `model set code` ships as a new subcommand vs a `--corpus` flag (engineer/architect
   OQ; combined plan defaults to the subcommand).
7. Champion code model default flip: decided by the owner from the WP8 eval report, never by
   this plan (architect OQ6).

## 10. Evidence

- Exploration: `docs/work/2026-08-21-code-search-exploration.md` (merged #401; spike facts §1,
  model landscape §1.1, refactor proposition §4)
- Lane docs: the four `docs/work/2026-08-21-code-search-moe-*.md` files (this task's worktree)
- Engine plan (assumed implemented): `docs/work/2026-08-21-arbitrary-embedding-models-plan.md`
- Source anchors: `MemorySchema.cs` (Ddl digest gate 421-460, triggers 143-201, metrics
  precedent 335-342), `WatchStore.cs:46-75` (remove cascade), `WatchPipeline.cs:100-126,262-280`
  (unregister choke point, digest ownership), `IngestPath.cs:47-60` (containment predicate),
  `WatchCatchUp.cs:22-23,38-58,65-132` (scan machinery), `FileIngestor.cs:35-38,298-302`
  (unhandled skip, hidden check), `SyncService.cs:70,101,425-440` (snapshot + strip),
  `MemoryTools.cs:98-187` (search surface), `FileTypeMatcher.cs:27` (duplicate-ext throw)
