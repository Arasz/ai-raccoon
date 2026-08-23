# Implementation plan — code corpus for AiRaccoon (code-daemon-embed-v1)

**Date:** 2026-08-21
**Task:** code-search-implementation-plan (**plan-only — this task finishes when the plan is
ready; no implementation in this task**)
**Status:** rev 3 — rev 2 (MoE + reviews folded) + **pre-implementation audit (§12)**: the
engine base assumed by §1 is unmerged (H1–H3 → re-sequenced into waves, §12.5); H4–H12 folded
into WP contracts. Where §12 contradicts earlier sections, §12 wins.

## 0. How to read this plan

This is the combined, reconciled plan from four parallel MoE lanes. The lane docs carry the
depth (every decision cites source `path:line`); this document is the authoritative contract:
scope, decisions, work packages, gates, and the TDD test mapping. Where lanes (or reviewers)
disagreed, the disposition is recorded in §7/§11. Where a lane doc contradicts this document,
**this document wins**; the QA catalog (`-moe-qa.md`) and the ops checklist (§2.4/§5 of
`-moe-ops.md`) have been amended to match.

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
~~INT8 QAT~~ **fp32** ONNX 187 MB **CORRECTED 2026-08-23** (fp32, not INT8 — see Amendments), sentencepiece tokenizer (`<s>`=2 `</s>`=3 `<pad>`=0 `<unk>`=1), pooling +
L2 fused in the graph → `pooling.mode = model-output`, 128-token hard cap → chunk budget **126**
(ctx−2), symmetric (no query prefix), MIT. A/B candidate: `jinaai/jina-embeddings-v2-base-code`
(768-dim, Apache-2.0, ONNX int8 154 MB; pooling shape unverified — parity probe gates its arm).

**Out of scope (v1):** remote code embedding (Voyage/OpenAI — v2, ops §2.2); tree-sitter AST
chunking (v2 lever); `codeRetrieval.*` tuning rows (named seam only); `!` negation in
`ai-raccoon.ignore` (additive later); non-768 code manifests (refused at configure time, §3.3);
workspace/shared scope for code; code `chunk_index` repair/backfill family (re-derivable);
vec0 dimension reconcile for the code corpus (D3 machinery documented as the extension point,
NOT exercised in v1 — §3.3); `model_migration` outbox changes (the code drain does NOT use the
outbox, §3.3).

**search_quality (RESOLVED, review F-21):** `kind=code`/`kind=both` searches are **excluded
from `RecordSearchSafeAsync`** (the recorder is corpus-agnostic today and its rows sync —
recording code queries would leak identifiers/paths off-machine, undercutting §3.7). A test
pins the exclusion (WP7). Memory searches record exactly as today.

## 2. Owner requirements (f:, binding)

1. **`ai-raccoon.ignore`** — gitignore-style exclude file filtering ingestion for BOTH
   pipelines. Contract: one file per tree root (`<watchRoot>/ai-raccoon.ignore`, honored by
   directory watches and `memory_ingest_directory`); gitignore-subset syntax (`*`, `**`,
   trailing `/` directory patterns, leading/anchored `/`, **no `!` negation in v1**,
   any-match-wins, case per host OS); ignored files are **never fingerprinted, never
   chunked**; a digest on an ignored path **deletes stale chunks** (a file indexed before the
   ignore line was added must be cleaned) and updates `last_change_ts` only; the ignore file
   is never self-ignored; **editing it triggers a full re-scan** of the watch — single-
   flighted, and when a scan is already in flight the trigger **queues a follow-up scan**
   (or re-checks the ignore file's mtime at scan end and re-scans if changed — one of the
   two, pinned by WP4-T20); reads: once per scan / per directory walk / per digest event, no
   cache. **Explicit `memory_ingest_file` of an ignored path returns 0 chunks (ignore wins —
   consistent with never-fingerprinted).** Pure `IgnoreRules` type in Core (static
   parse/match). New matcher is hand-rolled — verified: no glob machinery exists in `Watch/`
   today (engineer §5.2).
2. **No overlapping watches** — the watch whose scope contains the other wins; the included
   watch is pruned. Containment = `IngestPath.IsWithinScope(inner, outer)` (separator-aware
   real-path prefix — `/repo2` ⊄ `/repo`). Adding a broader watch prunes every contained
   watch (entries stay, fingerprints cascade-deleted; new watch registers with
   `lastChangeTs=0` → full catch-up); adding a narrower watch inside a broader one is
   **rejected** with `WatchOverlapException` naming the covering watch (nothing written);
   equal-path re-add stays an idempotent no-op (the only case that reports `absorbedBy`).
   **Atomicity (review codereviewer MUST-FIX 7 — UPGRADED from the rev-1 documented-gap
   resolution):** the prune + register are ONE `BEGIN IMMEDIATE` store transaction (a
   composite `PruneAndAddAsync` over the watch rows + `watch_files` cascade) — a crash
   leaves either the old watches or the new watch, never an unwatched path; the runtime
   `UnregisterWatch` calls run after commit (idempotent; a crash between commit and
   unregister leaves stale runtime state that the hosted service's registration poll
   reconciles, and digest ownership stays deterministic). Kill-9 test in the WP4-T26 family.
   **Tie-break (review F-15):** mutual containment (real-path-equivalent registrations via
   symlink/case spellings) keeps the **longest literal path; on equal length, the
   first-registered** — never prune a watch whose real path equals the survivor's; pinned by
   the WP4-T24 family. Ordering in `AddAsync`: reject-if-contained → prune+register (one tx).
3. **Repo watch by default** — registering a watch on a repo root applies rule 2 to every
   existing watch inside it, then catch-up scans the whole repo into the correct corpora.
   **Hidden directories and dependency trees (review, arch-10):** enumeration skips hidden
   directories (extend `IsHidden` to path segments) AND a small built-in deny set for
   repo-root watches: `node_modules`, `bin`, `obj`, `.git`, `.venv`, `__pycache__`, `dist`,
   `build`, `target` (v1 default; the ignore file is the extension surface; owner sign-off
   item OQ8). Without this, repo-watch-by-default indexes dependency trees (56 texts/s ×
   100k+ files).
4. **TDD test cases in this plan, authored by QA** — §6 + the QA lane catalog (95 named
   cases, each with behavior, RED witness, GREEN assertion, home, kind; amended per review,
   §11).

## 3. Design (reconciled)

### 3.1 Corpus schema (architect D-A/D-B + review F-19)

New objects in the **digest-gated additive `Ddl` block** — purely additive `CREATE … IF NOT
EXISTS`, no ladder step, no `CurrentVersion` bump for the tables themselves (metrics-table
precedent, `MemorySchema.cs:335-342`; the digest gate re-runs the block once per bank).

- `code_entries(id, hash, path, value, source_file, line_start, line_end, project_id,
  created_at, updated_at, embed_state, embedding BLOB, chunk_index, total_chunks)` +
  `uq_code_chunk UNIQUE(project_id, path, hash)` + indexes: `(project_id)`, `(hash)`,
  `(embed_state, project_id)`, and **`idx_code_entries_path (project_id, path)`** (the delete
  legs run per digest event over a project's whole chunk set; code chunk counts per project
  are an order of magnitude larger than notes).
  Deliberately absent (with reasons): `scope`/`workspace_id`/`agent_id` (project-scoped only),
  `rating`/`ttl_days`/`access_count` (no degradation — code is a re-derivable cache),
  `heading_path`/`section`/`source_id` (no structure modality in v1).
- `code_fts` — FTS5 external-content over `(value, source_file)`, `content='code_entries'`,
  full trigger family (ai/ad/au) mirroring `entries_fts`.
- `vec_code` — vec0 `ctx TEXT, embedding float[768] distance_metric=cosine`, `ctx =
  project_id` (ADR-0068), full trigger family (au/pending/ad) mirroring `vec_entries`.
- Encryption-at-rest: inherited wholesale (bank-level).

### 3.2 Lifecycle boundaries (architect D-C)

Code never syncs (push **DROP**-strip, §3.7), never sweeps, no TTL, no promotion, no
workspaces. Watch removal deletes from BOTH corpora (entries stay — existing semantics).
Corpus is an explicit re-derivable cache: loss costs a re-ingest, not knowledge.

### 3.3 Settings, engine activation, and the drain mechanism (RESOLVED — review arch-1/F-08)

- New rows: `embedding.codeModel` (manifest-activated local code engine) + `embedding.codeEngine`
  (fingerprint). Memory rows untouched; `settings model reset` never touches code rows;
  `settings model code reset` deletes only the code rows.
- Activation UX: `ai-raccoon model download faxenoff/code-daemon-embed-v1` (187 MB < 500 MB,
  no `--yes`; SHA-256 pinned) then **`ai-raccoon model set code local <dir>`** (new subcommand
  under the existing `model set` family, ADR-0076 outbox shape for the *settings write* only).
- **Non-768 manifests are refused at configure time** (`model set code local` rejects with an
  actionable error; witness-test). The D3 dimension-reconcile machinery is documented as the
  extension point, NOT exercised in v1 (review F-18 / external round-1 item 3).
- **Drain mechanism — ONE design (engineer D-E9 adopted; H4 and v11b deleted):** the code
  re-embed does **NOT use the `model_migration` outbox** — that outbox is single-row
  (`MemorySchema.cs:372-382`), its relay hard-codes the memory query, and its ToolGate closes
  ALL tools during a migration (ADR-0076) — three facts that make "shared outbox per corpus"
  impossible without new machinery and a gate carve-out. Instead: `model set code local`
  writes the settings rows AND invalidates `embed_state='pending'` for the code corpus **in
  one transaction** (the `vec_code_pending` trigger empties `vec_code` at commit — no
  stale-vector window); the new `code-reindex` maintenance job drains pending code rows; **no
  ToolGate interaction — memory tools are never blocked**; while code vectors drain,
  `kind=code` search naturally degrades to FTS5-only (code_fts rows exist from ingest time).
  Drain rate: the maintenance poll (4×32 rows/run) is ~8.5 texts/s effective — acceptable for
  incremental fingerprint-change re-embeds of typical corpora; a large-repo re-embed wall
  time is stated in the how-to (H3 measures it; a batch-size lever is the documented
  extension point).
- **Unconfigured code engine (RESOLVED — review F-11):** always-ingest — with no
  `embedding.codeModel`, code files are still chunked (bundled code-daemon sentencepiece
  counting tokenizer, 626 KB) and stored `pending`; `kind=code` returns FTS5-only results with
  a warning; a **configured-but-unloadable** engine (missing manifest/files/dims mismatch)
  returns an actionable error for code searches only — memory unaffected.

### 3.4 Chunker and ingest (engineer D-E1/D-E2/D-E10 + review arch-11)

- `CodeChunker`: line-range splitter — blank-line blocks with brace-balance boundary preference
  (cut at the last balance==0 boundary inside the budget, else budget wins), per-line hard-split
  floor via `TokenBudget.Trim`, exact joined-recount packing (ADR-0036), budget **126** (manifest
  ctx−2), overlay 0 (line ranges, not prose continuity), emits `CodeChunk(Text, LineStart,
  LineEnd)` 1-based. No AST in v1.
- Extension registry: one constant `CodeExtensions` set (Core) + `CodeFileTypeMatcher`
  (case-insensitive, `FileTypeMatcher` normalize shape); memory `FileTypeMatcher` untouched;
  registries disjoint-by-test; **memory-wins runtime priority** (`.md` inside a code dir →
  memory). `memory_ingest_directory` on a mixed tree feeds both corpora.
- **CodeIngestor dedup (review arch-11):** the insert is `INSERT … ON CONFLICT DO NOTHING`
  followed by the bucket re-read `SELECT id FROM code_entries WHERE project_id=@p AND
  path=@path AND hash=@hash` — exactly the memory path's post-conflict re-read
  (`FileIngestor.cs:167-184`); under a concurrent same-file ingest the loser re-reads and
  refreshes positions. Pinned by WP3-T07 (concurrent double-ingest RED).

### 3.5 Watch channeling (engineer §5)

One watch row, **no digest-level classification, no new watch flag**: the digest executor's
existing replace/delete transactions gain corpus-agnostic **delete-BOTH + re-ingest-BOTH**
legs; each ingestor self-filters by its own matcher. Digest flow per event: rename → delete
both; missing → delete both; ignored (rules from §2.1) → delete stale chunks +
`last_change_ts`, no fingerprint; hash-skip unchanged; replace → delete-both + ingest-both +
fingerprint in one tx; pending-embed drain for BOTH corpora (memory outbox + code-reindex
job); `ai-raccoon.ignore` edit → re-scan (queued follow-up when one is in flight). Catch-up
scan of a mixed tree: enumeration skips ignored files + hidden directories + deny-set paths
(§2.3); `ReconcileIgnoredAsync` cleans fingerprinted-but-now-ignored. Edge cases: the 15 in
engineer §5.6, plus the tie-break and transaction-boundary semantics of §2.2.

### 3.6 Search surface (architect D-I, ops §3.5; envelope RESOLVED — review arch-2/3, F-02/03/12/13)

- `memory_search` gains `kind: "memory" | "code" | "both"`, default `memory`, validated
  fail-fast (`invalid-params: Invalid kind 'x': expected memory, code, or both.`). Per-section
  `minRelativeScore` and `limit`. QueryGuard + QueryLengthGuard apply identically; the code
  section's query is trimmed to 126 tokens (manifest-aware `TrimQueryToWindow`), with a
  separate code-budget warning.
- **Wire shape (pinned):** keys are **`results`** (the existing key, `MemoryTools.cs:346`) and
  **`code`**. `kind=memory` serializes exactly the legacy envelope — **no `code` key at all**
  — via a nullable `Code` property with `[JsonIgnore(Condition = WhenWritingNull)]` (the
  mechanism; a custom converter is the fallback if the SDK serializes nulls differently).
  `kind=code` → `{ results: [], code: [...] }`; `kind=both` → `{ results: [...memory...],
  code: [...] }` (both keys always present). Code hits carry `lineStart`/`lineEnd`; no
  cross-corpus fusion (each section ranked by its own hybrid).
- **The compat promise (reworded per review F-02):** "semantically identical envelope — same
  keys, order, values; `Meta.CorrelationId` excluded" (two live responses are never
  byte-identical: the correlation id is per-call random). A **golden response is captured and
  committed in WP1** (pre-feature) and WP6-T01 compares exact JSON modulo the correlation id.
- **WP6-T01 RED (two-phase, review F-03):** (1) golden captured pre-feature; (2) witnessed
  failing against a deliberately broken intermediate (a stub that serializes the `code` key
  for `kind=memory`, or defaults `kind` to `both`); (3) green after the real change. Both runs
  recorded in the PR.
- `code_get(projectId, hash)` mirrors `memory_get`; unknown hash refused.
- `kind=code` with `scope=shared/workspace` → empty code section (documented; code is
  project-scoped only).
- **search_quality:** code/both searches are excluded from recording (§1; test in WP7).

### 3.7 Sync (RESOLVED — external blocking 1, review F-22)

The exploration's "code excluded by construction" is **false for the push direction**: the
snapshot is a whole-bank `VACUUM INTO`; only settings + workspace rows are stripped. **The
fix: `StripNonSyncableAsync` DROPs the code tables from every pushed snapshot** — `DROP TABLE
code_entries`, `code_fts`, `vec_code` (their FTS5/vec0 shadow tables and triggers drop with
them; the strip already runs with vec0 loaded). Row-deletion is NOT the mechanism: the gate
asserts table ABSENCE, and `DELETE` would leave empty tables + fire trigger families. The
strip runs on **both push paths** (local snapshot + merged snapshot, `SyncService.cs:70-74,
101-107`); ADR-0014 becomes "settings and the code corpus never sync". Pull/merge unchanged
(merge names only `entries`/`sync_tombstones`/`memory_source`). **Gate:** pushed snapshot
contains no `code_entries`/`code_fts`/`vec_code` (and no `vec_code_%` shadow) tables — tested
on both push paths (WP7-T04 rewritten per review F-06: RED seeds code rows via WP3 machinery
and pushes BEFORE the strip change → snapshot contains the tables → assertion fails; GREEN
after the DROP-strip lands).

### 3.8 Maintenance (engineer §7 — D-E9 shape)

New ledger row `code-reindex` (model fingerprint change → `embed_state` invalidation in the
configure transaction + drain on the maintenance poll). No sweep/TTL/promotion jobs for code.
`memory_stats` / `memory_list` / `memory_performance` stay memory-only; code counts surface
via the `code-reindex` ledger + metrics.

### 3.9 Evaluation (architect D-L + review F-01/F-04, arch-7)

Reuse the existing harness (`evaluate.py` + `scoring.py`). Code eval-set: 2–3 real repos
vendored at pinned commits incl. **a mandatory Python repo** (H5); QA assembles, owner
approves. Arms: code-daemon vs jina-code-v2 (both 768 → re-embed only) + chunker arm
(**token-window baseline**, not whole-file/MiniLM — review arch-7; MiniLM-on-same-chunks is a
scratch-only reference, NOT a gate).

- **Floor (rewritten per review F-01):** fixed **mean nDCG@5 ≥ 0.50**, owner-set, **never
  re-baselined from measurement**; the champion arm measured on the committed, hash-anchored
  eval set + query list; **two named negative controls in the same report, same set, same
  queries** — (a) hybrid with the vector leg replaced by seeded random unit vectors, and
  (b) FTS-only (vector leg disabled) — **both must score < 0.50**; the eval set contains ≥ 1
  `negativeTest` per category. Precedent cited: the engine plan's G5 parity-gate pattern
  (fixed bar + negative control + never re-baselined), NOT ADR-0079/0081.
- **Chunker-arm anchoring (review arch-7):** chunk boundaries differ across arms → chunk
  hashes differ → `expectedHash` anchoring breaks. WP8 adds a small scoring extension:
  **span-overlap relevance** — a chunk is relevant iff its line range intersects the graded
  span; graded queries carry answer spans (line ranges); per-arm re-anchoring regenerates the
  spans' hashes against that arm's chunks. This answers H2 (chunk-boundary quality) directly.
- **jina parity probe (review F-04):** fixed cosine ≥ 0.999 bar against a reference embedding
  of the same texts, negative control (deliberately wrong pooling) must fail, threshold never
  re-baselined (verbatim engine-plan G5 wording); the jina arm runs only after the probe
  passes (its pooling shape is unverified — `emb_pooler: mean` suggests in-process mean).
- Report contents: per-query regression table, model A/B, chunker arm, re-embed time,
  wall-time vs spike's 56 texts/s (H3). **The plan never flips defaults** — the owner decides
  the champion from the report (engine-plan G0 precedent).

## 4. Migration (ladder; RESOLVED — review arch-1/F-08/F-17)

**One version bump, `CurrentVersion` 10 → 11, ONE ladder step: the overlapping-watch prune.**
The `model_migration` corpus column (v11b) is **dropped** — the adopted drain (D-E9, §3.3)
does not use the outbox, so the column is dead weight (the separate-code-migration-table
rejection applies to it).

- **v11 — overlapping-watch prune** (ops §3.4 + review F-15): per project, keep the outermost
  watches, delete nested watch rows + cascade their `watch_files` (same transaction pattern
  as `WatchStore.RemoveWatchAsync`); **tie-break: keep the longest literal path; on equal
  length, the first-registered — never prune a watch whose real path equals the survivor's**;
  **reported, not silent, WITH a channel (review arch-8):** `MigrateToV11Async` returns the
  pruned `(path, coveredBy)` list + count; the one caller that owns a logger
  (`SqliteConnectionFactory.InitializeAsync`) logs one Information line per pruned watch
  (`watch overlap migration: removed <path> (covered by <path>)`) + the count; WP1's gate
  asserts the log line (RED: migration runs, no log line). Stamped only on success. Same code
  path as the runtime rule (one implementation, two call sites). Known, accepted: the
  surviving watch's fingerprint set is incomplete until its next scheduled scan (re-digest is
  idempotent + hash-skip cheap — INFO F-20).
- Grandfathering claim deleted (arch D-G corrected): v11 IS the v1 reconcile.

## 5. Work packages, gates, and TDD tests

Canonical WP numbering = QA lane's (test contract, amended per §11). Every WP: TDD, RED
witnessed, gate named.

| WP | Deliverable (lane owner) | Gate | QA tests |
|---|---|---|---|
| WP1 | Corpus schema in `Ddl` + triggers + indexes (incl. `idx_code_entries_path`); `embedding.codeModel` rows; `model set code local` (incl. non-768 refusal); settings show/reset; ladder v11 (fixture bank with nested watches prunes — incl. symlink tie-break — RED→GREEN; log line asserted); **golden `kind=memory` response captured pre-feature** | existing-bank copy opens; `doctor` reports new tables; code rows round-trip; memory tables byte-identical; v11 migration RED→GREEN + log assertion; non-768 manifest refused (witnessed) | WP1-T01…T08 + golden capture |
| WP2 | `CodeChunker` (line-range, 126 budget, brace-balance, hard-split, no overlay) | chunker fixtures: budget ≤126; **union of ranges covers lines 1..N with no gaps; ranges disjoint except hard-split lines (T03 restated per review F-05)**; real-sentencepiece cap check | WP2-T01…T11 (T03/T06 reconciled) |
| WP3 | Extension registry + `CodeIngestor` (+ post-conflict re-read) + mixed-tree directory ingest + hidden-dir/deny-set skip; bundled sentencepiece counting | `.cs` → code rows with line_start<line_end + FTS + vec0; routing code/docs/neither; hidden files AND hidden dirs + deny set skipped; scope refusal; pending→embedded; dedup incl. concurrent double-ingest RED (re-read) | WP3-T01…T10 (T03 pins hidden-dir/deny; T07 concurrent) |
| WP4 | Watch channeling + `ai-raccoon.ignore` + overlap prune/reject + repo-watch-by-default + tie-break + mid-scan ignore re-scan + **prune+register one-transaction atomicity (kill-9)**; `WatchAddResult` gains `pruned`/`absorbedBy` (additive); ignore-change re-scan | watch fixtures: root-add prunes nested (result lists them); add-inside → `WatchOverlapException` (NOT absorbedBy); `.md` edit → memory only; `.cs` edit → code only; deletion removes from both in one tx; crash-mid-delete rolls back; **kill-9 between prune and register → either old watches or new watch, never unwatched**; ignored file → no rows in EITHER corpus; ignore edit → re-scan incl. mid-scan follow-up; symlink-equivalent pair → exactly one watch survives; `/repo2` not pruned by `/repo`; `EXPLAIN QUERY PLAN` on the delete leg shows the path index | WP4-T01…T29 |
| WP5 | Code search: `CodeSearchService` (FTS5 + vec0 + RRF, project scope) + code-engine query embed + 126 trim + per-section limit/minRelativeScore | hybrid present; no shared/workspace leakage; weight-flip fusion; empty corpus; per-call tuning args apply to code section; unconfigured engine → FTS5-only + warning | WP5-T01…T10 |
| WP6 | `memory_search kind` + `code_get` + envelope (`results`/`code` keys; `WhenWritingNull` omission) | kind=memory semantically identical (golden, modulo correlation id; NO `code` key); invalid kind refused; both keys for both/code; code_get unknown-hash refused; QueryGuard applies; code-budget warning; **two-phase RED for WP6-T01 recorded** | WP6-T01…T12 (keys amended) |
| WP7 | Maintenance + non-interference: `code-reindex` job (D-E9 drain, no outbox), sync **DROP**-strip on both push paths, sweep/promotion untouched, search_quality exclusion for code searches | code-engine fingerprint change re-embeds code only + memory tools never blocked (no ToolGate close); pushed snapshot has NO code tables (both push paths; RED seeds rows pre-strip); sweep never sees code rows; `kind=code/both` writes no search_quality rows | WP7-T01…T08 (T04 rewritten) |
| WP8 | Eval + provenance + BDD + docs: eval-set (2-3 repos incl. Python), registry pin, arms (code-daemon vs jina after parity probe; token-window chunker arm), span-overlap scoring extension, `code-corpus.feature`, ADRs 0084-0087, docs drift | **floor nDCG@5 ≥ 0.50 (fixed, never re-baselined) + both negative controls < 0.50 (random-vector leg AND FTS-only) witnessed**; jina parity probe ≥ 0.999 + negative control; tool-count gate 28 + feature-file assertion updated; ADR set reviewed | WP8-T01…T06 + negative controls + probe |

Regression guards (QA §3): memory behavior unchanged by default — every existing retrieval
gate and wire-shape test stays green; `kind=memory` semantically identical (golden modulo
correlation id).

## 6. TDD test catalog (QA lane)

95 named test cases in `docs/work/2026-08-21-code-search-moe-qa.md` (amended per review:
WP2-T03/T06 coverage property, WP6 keys `results`/`code`, WP6-T01 two-phase RED, WP7-T04 sync
RED, WP8-T03 arm + anchoring, WP3-T03 hidden-dir/deny, WP4-T20 mid-scan, WP4-T24 tie-break,
WP7-T08 unconfigured mode), mapped 1:1 to WP1–WP8. Each case: behavior pinned, RED
precondition, GREEN assertion, home, kind. RED-witness logistics: QA §5.

## 7. Join dispositions (lane conflicts resolved; review round in §11)

1. **Ladder v11 ownership** — architect claimed v11 for the outbox corpus column, ops for the
   overlap prune. **Resolved:** v11 = overlap prune ONLY; the corpus column is dropped with
   the outbox drain (see 2).
2. **Drain mechanism (review arch-1/F-08)** — three contradictory designs. **Resolved:**
   engineer D-E9 (configure-transaction invalidation + `code-reindex` drain, no outbox, no
   ToolGate, no v11b). H4 deleted. Single-row outbox, hard-coded relay query, and ADR-0076
   gate semantics are the reasons; the stale-vector window and drain rate are closed by the
   chosen design.
3. **Envelope shape** — `results` + `code` keys; `kind=memory` has NO `code` key, via
   `WhenWritingNull`; compat promise = semantic identity modulo correlation id, pinned by a
   golden captured in WP1. (QA/ops docs amended to `results`.)
4. **`ai-raccoon.ignore` negation** — no negation in v1 (engineer wins over QA G17).
5. **Narrower-inside-broader** — reject with `WatchOverlapException` (ops doc + checklist
   amended; `absorbedBy` only for identical-path re-add).
6. **Repo-watch-by-default** — always prune.
7. **Ignore vs explicit `memory_ingest_file`** — ignore wins (0 chunks); recorded here and in
   §2.1 (was unrecorded).
8. **Hidden directories + deny set** — v1 skips hidden dirs + built-in deny set for repo-root
   watches (owner OQ8).
9. **search_quality** — code/both searches excluded from recording (privacy: rows sync).
10. **Unconfigured code engine** — always-ingest + pending + FTS5-only + warning; actionable
    error only for configured-but-unloadable.
11. **Sync strip mechanism** — DROP the code tables (gate asserts absence; both push paths).
12. **Chunker A/B arm** — token-window baseline; span-overlap relevance extension for
    cross-arm anchoring.
13. **Per-call tuning args for the code section** (QA G6): accepted per-call, same defaults.
14. **Kind + scope interaction** (QA G15): `kind=code` + shared/workspace → empty code
    section, documented.

## 8. Risks (condensed; full table in ops §4)

Model provenance (registry pin + eval-before-default + jina A/B; ~~never PTQ the INT8 QAT
artifact~~ — the artifact we run is **fp32**, so PTQ is permitted; the card's hit@1 .200 → .133
figure stands as the cost to weigh, not a prohibition **CORRECTED 2026-08-23** (fp32, not INT8 — see Amendments)), chunker quality without AST (Python repo in eval-set + span-overlap measure;
tree-sitter v2 lever), throughput 56 texts/s (incremental watch deltas; initial index of a
large repo is the only wait; dependency trees excluded by deny set), sync push leakage (fixed
by DROP-strip, both-paths test), re-embed blocking memory (impossible by design — no outbox,
no gate), binary files with code extensions (v1 accepts, matches memory path), first-run
overlap prune (reported via the v11 log channel), code re-embed drain rate on very large
corpora (documented; batch-size lever).

## 9. Open questions for the owner (lane OQs + review round)

1. Approve this plan (G0) — then implementation starts as a separate task.
2. Approve the code extension list (engineer OQ1, §4.1) — v1 languages.
3. Accept bundling the code-daemon sentencepiece tokenizer (626 KB) as the unconfigured
   counting tokenizer (engineer OQ4).
4. Confirm `memory_ingest_file` stays memory-only in v1 (engineer OQ2) — or route code files
   there too (QA G5 recommends yes; note: it honors ignore either way, §2.1).
5. Approve the code eval-set repos (QA G14; owner picks).
6. `model set code` as a new subcommand vs a `--corpus` flag (combined plan defaults to the
   subcommand).
7. Champion code model default flip: owner decides from the WP8 report, never this plan.
8. **Approve the built-in deny set** for repo-root watches (node_modules/bin/obj/.git/.venv/
   __pycache__/dist/build/target) as the v1 default (review arch-10).
9. **search_quality exclusion** (review F-21): confirm code/both searches are NOT recorded
   (recommended — privacy) rather than recorded with a corpus marker + sync strip.

## 10. Evidence

- Exploration: `docs/work/2026-08-21-code-search-exploration.md` (merged #401; spike facts §1,
  model landscape §1.1, refactor proposition §4)
- Lane docs: the four `docs/work/2026-08-21-code-search-moe-*.md` files (this task's worktree)
- Review findings: `docs/work/2026-08-21-code-search-plan-review-{architect,codereviewer}.md`
  (this task's worktree; round 1 external feedback on PR #403)
- Engine plan (assumed implemented): `docs/work/2026-08-21-arbitrary-embedding-models-plan.md`
- Source anchors: `MemorySchema.cs` (Ddl digest gate 421-460, triggers 143-201, metrics
  precedent 335-342, model_migration single-row 372-382), `WatchStore.cs:46-75` (remove
  cascade), `WatchPipeline.cs:100-126,262-280` (unregister choke point, digest ownership),
  `IngestPath.cs:47-60` (containment predicate), `WatchCatchUp.cs:22-23,38-58,65-132` (scan
  machinery; no hidden-dir filter), `WatchScanGuard.cs:22-41` (join-not-queue),
  `FileIngestor.cs:35-38,167-184,298-302` (unhandled skip, post-conflict re-read, hidden
  check), `SyncService.cs:70-74,101-107,424-440` (snapshot paths + strip),
  `MemoryTools.cs:98-187,346` (search surface, result key), `EntryEmbedder.cs:96-107`
  (single-migration refusal, configure tx), `ModelMigrationJob.cs:27-43` (hard-coded relay
  query), `ToolGate.cs:23-25` (all-tools close), `FileTypeMatcher.cs:27` (duplicate-ext
  throw), `scoring.py:119-127` (hash/path relevance)

## 11. Review dispositions (round 1 — all folded)

External review round 1 (PR #403, @claude-review) — 2 blocking, both on the sync strip:
- B1 (strip misses `vec_code` + shadows) → §3.7 DROP-strip covers `code_entries`/`code_fts`/
  `vec_code` + shadows; gate asserts absence of all.
- B2 (WP7-T04 can't fail; pins pre-correction belief) → §3.7/§5: RED seeds rows, pushes before
  the strip, fails on table presence; GREEN after DROP-strip.
- R1 (vec_code dims vs any-manifest activation) → §3.3: non-768 refused at configure time;
  D3 documented as extension point.
- R2 (ADR numbering 0084 collision with #402) → §5 WP8: ADRs 0084-0087; whichever feature
  merges first takes 0084 (ADR text records the collision).
- R3 ("unconditional digest-gated Ddl" self-contradiction) → reworded "digest-gated additive
  Ddl block" (§3.1).

Internal MoE review (architect 3 MUST-FIX/8 SHOULD-FIX; code-reviewer 11 MUST-FIX/10
SHOULD-FIX/5 INFO): drain mechanism (arch-1/F-08 → §3.3, disposition 2), envelope keys
(arch-2/F-12 → §3.6, QA/ops amended), serialization mechanism + byte-identical definition
(arch-3/F-02/F-13 → §3.6, golden + WhenWritingNull), WP6-T01 RED (F-03 → two-phase),
eval floor + negative controls (F-01 → §3.9), jina probe bar (F-04 → §3.9), WP2-T03/T06
coverage (F-05 → QA amended), WP7-T04 RED (F-06 → rewritten), ops-ignore contradictions
(F-09 → ops §2.3 amended), ops absorbedBy (F-10 → ops §2.4 amended), unconfigured mode
(F-11 → §3.3), arch WP-D gate (F-13 → §3.6), prune/register atomicity (F-14 → §2.2; **upgraded to a single `BEGIN IMMEDIATE` `PruneAndAddAsync` per the reviewer's MUST-FIX 7** — kill-9 leaves old watches or the new watch, never an unwatched path),
tie-break (F-15 → §2.2/v11), mid-scan ignore re-scan (F-16 → §2.1/WP4-T20), grandfathering
(F-17 → §4), vec_code reconcile vacuity (F-18 → §3.3), idx_code_entries_path (F-19 → §3.1),
v11 log channel (arch-8 → §4), hidden dirs + deny set (arch-10 → §2.3/OQ8), dedup re-read
(arch-11 → §3.4), MiniLM arm (F-26 → §3.9 scratch-only), search_quality (F-21 → §1/§3.6),
strip DROP vs DELETE (F-22 → §3.7), feature-file path (arch-12 → QA/ops aligned on
`code-corpus.feature`), tool-count 28 + stale assertion (arch-13 → WP8 gate), INFO F-07/F-20/
F-23/F-24/F-25 recorded.

## 12. Pre-implementation audit (rev 3 — 2026-08-21, architect audit at implementation start)

Audited by the implementation task (`code-mem-implementation`) before any code. All 14 §10
anchors verify with zero drift; `CurrentVersion = 10` confirmed (`MemorySchema.cs:49`), 10→11
correct; ADRs top out at 0083 (§11 R2 moot); nothing merged to main since 571c751b. Twelve
holes found; dispositions below are **binding** and, where they contradict earlier sections,
win.

### 12.1 Blocking (engine base) — resolved by re-sequencing

- **H1 — §1's "assumed already implemented" engine base does not exist on main.** #402 merged
  plan docs only (its `feat:` title is false); PR #404 carries a tracker file, no code. The
  engine work lives on local unpushed branches `lane/wp1-manifest` (manifest records/validator)
  and `lane/wp3-engine` (tokenizer seam, sentencepiece), never integrated with each other
  (wp3 carries a provisional duplicate of wp1's manifest loader). → **WP0 (external
  dependency): the engine-integration packet — owned by the #404 task, not this one.** This
  task's engine-blocked WPs start only after WP0 lands on main.
- **H2 — D4/D8 `model download` verb and D2 `embedding.dimensions` row exist nowhere** (verb:
  infra only on lane/wp1, no CLI command; D2/D3: engine WP4 never started). → D2/D3 are SOFT
  for this plan (non-768 refused at configure time; D3 explicitly unexercised). The download
  verb is needed only for §3.3's UX: if WP0 lands without it, WP1 documents manual model
  placement + a hand-authored D1 manifest fixture, and the how-to is amended when the verb
  ships.
- **H3 — D6 contradiction.** The engine plan's text pins `MaxManifestChunkTokens = 510, NOT
  derived from ctx` (`2026-08-21-arbitrary-embedding-models-plan.md:90`), while this plan's
  126 assumes ctx−2; only the unmerged lane code implements `min(510, ctx − reservation)`.
  → Flagged to the engine lanes on PR #404: engine D6 must be amended to the min() rule that
  is actually implemented. **WP2 gate here asserts a 128-ctx manifest resolves to budget 126,
  not 510** — this gate also guards against a re-derivation of WP3 from the stale plan text.

### 12.2 Should-fix — folded into WP contracts

- **H4 — `manifest.json` name collision:** the D1 sidecar filename equals the file
  code-daemon-embed-v1 ships in its own repo. §3.3 gains: download/placement must keep the D1
  manifest authoritative (the resolution is pinned in WP0/engine docs; flagged on #404); WP1
  adds a fixture with both files present.
- **H5 — no WP owned the code embedder:** `ICodeEmbedder`/`CodeEmbedder` + the
  `EmbeddingService` second-engine resolution are now **named WP5 deliverables** (design per
  engineer lane §6.5, D-E8/E14/E15); WP3's pending→embedded gate depends on them; no
  `IModelMigrationLease` in the code path.
- **H6 — sync strip has THREE call sites**, not two: `SyncService.cs:74`, `:105`, and `:161`
  (retry-merged push). §3.7/§10 corrected; WP7-T04 covers all three; the strip uses
  `DROP TABLE IF EXISTS` (snapshots opened via `openSnapshot` never ran `EnsureAsync`).
- **H7 — settings-tier tuning params:** the code section resolves `SearchParameters.FromSources`
  like memory, but `retrieval.structureAlpha` is **inert** for code (no structure modality,
  §3.1). §3.6 gains the honored/inert list; WP5 test pins that `retrieval.structureAlpha`
  does not move code ranking.
- **H8 — WP8 eval is not CI-runnable:** the harness is Python (not a CI gate in this repo) and
  needs `kind=code` plumbing + a code fixture bank, which WP8 now names as deliverables. The
  eval gate is a **recorded manual run with a committed artifact**, not CI-enforced.
- **H12 — feature-file path:** WP8 pins `docs/features/code-corpus/code-corpus.feature` **+
  sibling `spec.json`** per `docs/features/README.md`; the QA catalog's bdd-kind reference to
  the legacy `docs/work/features-*` layout is repointed.

### 12.3 Notes (recorded decisions)

- **H9 — release:** WP8 gains "VERSION 1.28.1 → 1.29.0 via `scripts/version-bump.py`", landing
  in the final PR (release itself remains behind the repo's two human gates).
- **H10 — mid-scan ignore-edit semantics pinned:** mtime-recheck-at-scan-end (no new queue
  state in `WatchScanGuard`); WP4-T20 asserts that shape.
- **H11 — drain wording:** §3.5's "memory outbox" means `PendingEmbedJob`; the 8.5 texts/s
  figure holds only when the memory pending queue is idle (shared poll + `BatchSize = 32`).

### 12.4 Owner-question decisions (away-mode, registered)

- **OQ4:** `memory_ingest_file` **routes code files** (QA G5; one dispatch mechanism;
  ignore-wins already pinned by §2.1). WP3-T10 in scope.
- **OQ5:** eval repos picked provisionally at WP8 time (2–3 permissive-license repos, pinned
  commits, ≥1 Python), **marked provisional for owner sign-off** before the report is
  treated as authoritative. All other OQs take the plan's stated defaults.

### 12.5 Execution sequencing (supersedes any implied WP order)

- **Wave 1 (engine-independent, parallel):** (A) WP1-partial — corpus `Ddl` block + golden
  `kind=memory` capture; (B) WP4-core — `IgnoreRules`, overlap prune/reject/tie-break,
  one-transaction `PruneAndAddAsync` + kill-9, ladder v11 (both call sites), hidden-dir +
  deny-set enumeration skip.
- **Wave 2 (after A merges):** WP6 (kind/envelope/`code_get`), WP7-partial (DROP-strip ×3 +
  search_quality exclusion), WP3-partial (registry, `CodeIngestor`, routing, digest delete-both
  legs).
- **Wave 3 (after WP0 = engine base on main):** WP2, WP3-remainder (counting tokenizer),
  WP1-remainder (`model set code local`, non-768 refusal), WP5, WP7-remainder
  (`code-reindex` drain), then WP8.

### 12.6 Waves 1–2 integration-review dispositions (Opus gate, 2026-08-21)

Review found 2 blocking + 12 should-fix; all fixed in the same task except the following
recorded dispositions:

- **v11 REVERTED (S7/S8, orchestrator ruling — owner review requested):** the plan-§4
  `CurrentVersion 10→11` bump is replaced by an unconditional, ungated, idempotent
  overlap-prune at bank open (`MigrateIngestScopeKeysAsync` shape, soft per-row failure
  handling). Reason: the bump hard-fails concurrent sessions/peers on the older binary
  (user_version survives `VACUUM INTO`; sync refuses newer snapshots) — repo precedent
  ADR-0023, and this repo runs concurrent sessions as standard practice. `CurrentVersion`
  stays 10.
- **`idx_code_entries_path` DROPPED (S2):** proven inert — `uq_code_chunk` is the covering
  index; the EXPLAIN gate is re-pointed at it. §3.1's index list amended accordingly.
- **WP6 "code-budget warning" ownership (review note):** belongs to WP5 (the 126 trim), Wave
  3 — §5's WP6 gate row is corrected by this disposition; not a Wave-2 gap.
- **Per-call `limit`/`minRelativeScore` are shared across sections in Wave 2** (documented as
  provisional); per-section split is WP5. WP5 must also resolve the `relativeScore` semantics
  asymmetry: the FTS5-only code leg's score is positional (rank-derived), not
  relevance-relative — carrying the same field name with different semantics is a Wave-3
  design point, not a bug fix.
- **`WatchScanGuard` drop-window (review note):** an ignore-edit digest landing between a
  scan's final re-read and slot release is dropped, not queued — Wave-3 WP4-polish item.
- **`kind=both` metrics row** records a query hash while `kind=code` records nothing —
  asymmetry resolved in the S6 fix (see code); correlationId omitted for code/both envelopes.

## Amendments

### 2026-08-22 — chunk budget 126 → 510 (issue #422, PR #453)

**What was wrong:** §3.4/§12.1 H3 and the WP2/WP5 rows derive the code chunk budget from
code-daemon-embed-v1's "128-token hard cap", taken from
`docs/work/2026-08-21-code-search-exploration.md` §1. That cap is not real — the ONNX graph accepts
512 tokens and fails at 513, and it attends to content past token 128. Measurement and method are
on issue #422; the source document carries its own amendment.

**What it changed:** `CodeChunker.DefaultBudget` is 510 (`min(510, 512 − 2)`, derived from
`EmbeddingService.MaxManifestChunkTokens` rather than restated), the code-section query trim is 510,
and `SqliteCodeEngineStore.ActivateCodeEngineAsync` refuses a manifest *narrower* than the chunker's
budget instead of requiring exact equality with it. Every `126` and `128` in the body text above is
historical.

### 2026-08-23 — the shipped code model is fp32, not INT8 QAT (WP7 desk half, PR #536)

**What was wrong:** this document records `faxenoff/code-daemon-embed-v1` as an INT8
quantization-aware-trained artifact in its §1 model line and its condensed risk list. The file AiRaccoon downloads and runs is **fp32**.

**Measured 2026-08-23** by loading the artifact that
`model download faxenoff/code-daemon-embed-v1` places on disk — 187,286,767 B, sha256
`57bcfc6aed11ea239d01f2b124f2f948456f2284ad6e2c4744452509c9c25ca9`, the value pinned in that
directory's own `ai-raccoon.manifest.json`:

| | Recorded here | Measured |
|---|---|---|
| Weights | INT8, QAT, Q/DQ nodes carry trained scales | **fp32** — 70 initializers, **all `FLOAT`**, 46,801,920 elements = 187,207,680 raw bytes |
| Quantized ops | implied throughout | **zero** `QuantizeLinear`, `DequantizeLinear`, `MatMulInteger` or `QGemm` in 373 nodes |
| Why 187 MB reads as int8 | — | it does not: 46.8M parameters x 4 bytes **is** 187 MB. A 46.8M-parameter int8 graph would be ~47 MB — which is exactly what quantizing this one produces |

Reproduced independently during review of PR #536.

**What it changes:** the model card's *"never PTQ the INT8 QAT artifact"* warning refers to a
**different file** (`model_int8qdt.onnx`) than the one we run, so it does not forbid quantizing the
fp32 graph we actually have. It remains a live warning about what quantization costs this model
family's retrieval — hit@1 .200 -> .133 — and WP7's desk half measured a fp32-vs-int8 cosine of
**0.964** (against a 0.9999 negative control), which points the same way. **Nothing shipped
changes:** the engine has always been running this fp32 graph, so every throughput and
resident-size figure taken against it stands; only the label was wrong.

**Not rewritten:** figures elsewhere in this document that merely *label* the model INT8 while
reporting something else measured correctly are historical and read as such.

**Full record:** `docs/work/2026-08-23-code-engine-inference-research.md` §2.
