# Plan — Retrieval parameter tuning: adjustment matrix, evaluation datasets, ML tuning harness

Date: 2026-08-21
Task: `continue-testing-algorithm` (branch `task/continue-testing-algorithm-u1`, base `main` @ `1851a4d2`)
Status: plan (ready for owner/MoE review — review record convention per `docs/work/README.md`)

This plan is executable by implementation agents who know nothing beyond this document plus the
referenced files. Every fact below was re-verified against the live sources on 2026-08-21; where a
fact came from an earlier record, the record is cited.

---

## 0. Goal and non-negotiables

### 0.1 Goal

1. **Adjustment matrix** — with all defaults as baseline, tune ONE parameter at a time and
   document the LEARNED influence of each of the 9 retrieval parameters on the result, together
   with a logic-flow diagram of the full retrieval algorithm (the diagram is in §2 and must be
   reproduced in the matrix document).
2. **Two evaluation datasets** — (a) the existing sextant artificial bank, (b) a curated + copied
   project memory bank: a read-only `.backup` copy of the live bank + a 10-query test set graded
   good / could-be-improved / just-wrong + a 100-query eval set (ADR-file-targeted at 3 queries
   per file + non-file-based memory queries).
3. **ML-driven tuning** — the parameter search MUST use a researched, selected Python ML
   hyperparameter-tuning framework (not manual tuning). Decision: **Optuna 4.x (TPE sampler)**,
   see §6.
4. **Quality = 2 numbers** — the test set (10 graded queries) and the eval set (100 queries
   against the memory-db COPY, disjoint from the test set).

### 0.2 Non-negotiable constraints

| # | Constraint | Enforcement |
|---|---|---|
| C1 | Never write to the live bank `~/.ai-raccoon/memory.db`; never touch the live server (port 7721, PID 5537). The ONLY permitted access to the live bank is a read-only connection: `sqlite3 "file:/Users/arasz/.ai-raccoon/memory.db?mode=ro"` (or `query_only=1`). | Harness safety asserts (§9.6); code review; no code path may open the live path for write |
| C2 | Scratch servers must use `--data-root <scratch>` + `--port 0` and the bound port must be read back from the log. Port 7721 must never be bound. | `server.py` asserts bound port != 7721 before any search |
| C3 | No credentials/secrets in tracked files (none are needed — the bank is plaintext). | review + repo invariant |
| C4 | TDD is mandatory for every code change. Python code + tests live under `scripts/src/` and `scripts/tests/` (pytest, `pythonpath = scripts/src`). Prefer Python + existing surfaces (MCP + CLI); C# changes only if genuinely required (this plan needs none). | per-WP gates |
| C5 | One PR per task at the end; all new files in THIS worktree only. | §13 |
| C6 | Runtime scratch (bank copies, study DBs, logs) lives OUTSIDE the repo, under `/tmp/continue-testing-algorithm/` (checklist precedent: `/tmp/checklist-1-27-2/`). Only deliverables (scripts, corpora, docs) are committed. | §9.1 |

### 0.3 Source-of-truth files (read before implementing)

- `docs/adr/0083-search-parameters-unified-source.md` — SearchParameters design (precedence: query > settings > constants).
- `.ai-badger/skills/learned/uncategorized/ai-raccoon-retrieval-analysis/references/parameter-topology-and-probing.md` — 9-knob table, probing recipes, measured constraints (weight ceiling, sibling-boost trap, RRF corpus fragility, zero-overlap design).
- `docs/work/2026-08-20-hybrid-retrieval-fusion-investigation.md` — measured ranking behavior on the sextant bank; corpus appendix; fusion A/B.
- `docs/adr/0056-a-retrieval-gate-measured-off-its-tuning-set.md` — the gate discipline this plan extends (held-out tiers, mean nDCG@5 gate, per-query pins, discrimination proofs).
- `scripts/baseline-queries.json`, `scripts/table-corpus-queries.json` — the corpus JSON model to extend (schema in §5.3).
- `scripts/src/mcp_client.py` — MCP HTTP client pattern (SSE parsing) to reuse.
- Pipeline sources (§2): `src/AiRaccoon.Infrastructure/Sqlite/Memory/{SqliteMemoryStore.cs, SqliteMemoryStore.Search.cs, SearchResultMerger.cs, SourceAffinityRanker.cs, ReciprocalRankFusion.cs, ModalityCandidates.cs, SearchQueryExtensions.cs}`, `src/AiRaccoon.Core/Memory/{SearchParameters.cs, SearchParameterSettingsKeys.cs, SearchQuery.cs}`.

---

## 1. Verified current state (2026-08-21)

| Fact | Value | Evidence |
|---|---|---|
| Worktree | `/Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/continue-testing-algorithm`, branch `task/continue-testing-algorithm-u1`, base `1851a4d2`, clean | `git status` |
| Sextant bank | `/tmp/checklist-1-27-2/bank/memory.db`, **9 entries** (astrolabe, invoice, signal-15, guide note, guide.md 0/2, guide.md 1/2, cross-project note, sextant, notes.md digest) | `SELECT count(*)` + investigation appendix |
| Probe client | `/tmp/checklist-1-27-2/mcp_call.py` (streamable-HTTP JSON-RPC, SSE parsing), `probe.py` (7 queries incl. Query A/B, invoice, alien-tokens) | read |
| Live bank | `/Users/arasz/.ai-raccoon/memory.db`, **230 MB**, `entries` = **22,509**, all `embed_state='embedded'`; **83 distinct ai-raccoon ADR files present** (937 chunks, e.g. 0056 = 7 chunks, 0083 = 7 chunks — multi-chunk, sibling boost applies); **1,367 hermes transcripts** under `project_id='hermes-default'` (chunk_index -1); **161 entries with NULL/empty source_file**; **229 shared-scope entries** | read-only sqlite |
| Live bank settings (leak risk!) | `retrieval.structureAlpha = 0.5` and **`fusion.noRegression.enabled.global = true`** — the fusion flag is ENABLED on the live bank (non-default). Any copy inherits these rows. | `SELECT key,value FROM settings WHERE key LIKE 'retrieval.%' OR key LIKE 'fusion.%'` |
| Server launch recipe | `ai-raccoon --data-root <scratch> serve --port 0` (backgrounded); bound port in `serve.log`; bearer token at `<data-root>/mcp-token`; MCP endpoint `http://127.0.0.1:<port>/mcp` | checklist 2026-08-15/1-27-2 records |
| Venv | `.venv` Python 3.11.15; has httpx 0.28.1, numpy, pandas, scikit-learn 1.9.0, scipy, click, typer, tqdm, joblib. **No Optuna/hyperopt/ray.** `pyproject.toml` declares `requires-python >=3.12` (venv predates the bump; `uv add` reconciles) | `pip list` |
| MCP tool surface | 27 tools; `memory_search` exposes 7 knobs (rrfK, ftsWeight, vectorWeight, sourceLambda, consolidationThreshold, docScoreFormula, candidateWindow); weights are INT-only; enums travel as strings with fail-fast validation | ADR-0083 + topology reference |
| Settings CLI surface | `ai-raccoon --data-root <root> settings retrieval rrfk\|fts-weight\|vector-weight\|source-lambda\|consolidation\|doc-formula\|window\|alpha set/show`, `... fusion enable/disable/show`, `... show-all` | topology reference + ADR-0083 |

---

## 2. The retrieval algorithm — logic-flow diagram (source-verified)

Chain verified by reading the pipeline sources on 2026-08-21
(`SqliteMemoryStore.cs` lines 170/608/628/659/672, `SearchResultMerger.cs`, `SourceAffinityRanker.cs`,
`ReciprocalRankFusion.cs`, `SqliteMemoryStore.Search.cs`, `ModalityCandidates.cs`).

```mermaid
flowchart TD
    Q[memory_search query\nprojectId, scope, query, limit, minScore] --> R[Resolve SearchParameters\nFromSources query > settings > constants\n2 batched settings SELECTs on the search connection]
    R --> W[Candidate window per modality\nCandidateWindowFor limit, mode\nmax3x100 default | max5x50]
    R --> A[QueryVector with Alpha = structureAlpha\ncontent embedding + structure arm]
    W --> F[FTS leg — FTS5 BM25\nskipped if no expression or ftsWeight=0]
    A --> V[Vector leg — cosine nearest neighbors\nskipped if empty query vector or vectorWeight=0]
    F --> FUS[Weighted RRF fusion per context batch\nscore = sum weight / (k + rank), normalized to max 1.0\nweights = ftsWeight, vectorWeight; k = rrfK]
    V --> FUS
    FUS --> NR{≥2 legs contributed\nAND fusion.noRegression.enabled.global?}
    NR -- yes --> RE[NoFusionRegression reorder\nreorder by best single-leg rank]
    NR -- no --> SK
    RE --> SK[Cross-context candidate merge\nModalityCandidates dedupe by content hash]
    SK --> U[Second RRF pass — unit weight, same k\ncommit 404ba926 'missing unit fusion']
    U --> SA[SourceAffinityRanker\n1 sibling boost: +lambda per adjacent chunk of same multi-chunk source\nchunkIndex >= 0 only, GH#371 guard\n2 doc-score aggregation per source: max | sum\n3 consolidation: drop weak adjacent siblings\nscore gap >= consolidationThreshold\n4 normalize by boosted max]
    SA --> FL[Filter Ranking >= minRelativeScore,\nTake limit]
    FL --> OUT[Ranked results\nhash, ranking, path, sourceFile, chunkIndex, snippet]
```

Stage → knob mapping (the 9 knobs in pipeline order):

| # | Stage | Knob | Home | Type / range | Default |
|---|---|---|---|---|---|
| 1 | Params resolution | (precedence) | — | query > settings > constants | — |
| 2 | Query vector | `structureAlpha` | settings `retrieval.structureAlpha` | float 0..1 | 0.5 |
| 3 | Candidate window | `candidateWindow` | settings `retrieval.candidateWindow` | "max3x100" \| "max5x50" | max3x100 |
| 4 | Leg gating | `ftsWeight` | MCP `ftsWeight` / settings `retrieval.ftsWeight` | int ≥ 0 (0 = leg off) | 1 |
| 5 | Leg gating | `vectorWeight` | MCP `vectorWeight` / settings `retrieval.vectorWeight` | int ≥ 0 (0 = leg off) | 1 |
| 6 | Weighted RRF | `rrfK` | MCP `rrfK` / settings `retrieval.rrfK` | int ≥ 1 | 60 |
| 7 | Post-pass | fusion flag | settings `fusion.noRegression.enabled.global` | bool | false |
| 8 | Source affinity | `sourceLambda` | MCP `sourceLambda` / settings `retrieval.sourceLambda` | float 0..1 | 0.1 |
| 9 | Doc-score aggregation | `docScoreFormula` | MCP `docScoreFormula` / settings `retrieval.docScoreFormula` | "max" \| "sum" | max |
| 10 | Consolidation | `consolidationThreshold` | MCP `consolidationThreshold` / settings `retrieval.consolidationThreshold` | float ≥ 0 | 0.1 |

Measured constraints that bound every tuning decision (evidence: investigation doc + topology reference):

1. **Ranking is corpus-fragile** — RRF is rank-based; adding one entry flipped a target from rank 3 to rank 1. Every rank claim must state corpus + corpus size.
2. **The vector leg's similarity ordering is the binding constraint** — a target ranked 3rd-closest by the model can never reach rank 1 via any weight combination. The weights' ceiling is the pure-vector rank.
3. **The sibling boost is a second scoring layer RRF weights cannot counteract** — λ=0.1 makes adjacent chunks of the single multi-chunk source dominate small corpora.
4. **Fusion flag rescues only queries where one leg already had the right answer at rank 1** (gauges `search.fusion.top1_changed`, `top1_rank_delta`, `top5_moved` prove engagement).
5. **Weights are integer-only over MCP** (`0.25` rejected); enums fail fast on typos; malformed settings fall back to constants, never crash.
6. **MCP weights cannot express fine ratios** — for fine-grained fts/vector balance the harness must set settings (settings parse as int too — `retrieval.ftsWeight` is an int; same constraint; ladder points must be integers).

---

## 3. The adjustment matrix (deliverable 1)

### 3.1 Definition

- **Baseline** = the 9 defaults, **written explicitly to the scratch server's settings** (never "whatever the copy inherited" — see §1 settings-leak).
- For each knob: sweep a **ladder** of values around the default, holding every other knob at baseline, evaluate over BOTH datasets (sextant bank, memory-db copy). Record per-config: mean nDCG@5, MRR@5, hit@3, hit@1, per-query ranks, category breakdown (file-targeted / non-file).
- Ladders (per knob; default marked **bold**):

| Knob | Ladder |
|---|---|
| rrfK | 1, 5, 15, **60**, 120, 200 |
| ftsWeight | 0, 1, 2, 3, 5, 10 (with vectorWeight=1) |
| vectorWeight | 0, 1, 2, 3, 5, 10 (with ftsWeight=1) |
| sourceLambda | 0, 0.05, **0.1**, 0.2, 0.3, 0.5 |
| consolidationThreshold | 0, 0.05, **0.1**, 0.2, 0.5, 1.0 |
| docScoreFormula | **max**, sum |
| candidateWindow | **max3x100**, max5x50 |
| structureAlpha | 0, 0.25, **0.5**, 0.75, 1.0 |
| fusion flag | **false**, true |

Total ≈ 6+6+6+6+6+2+2+5+2 + 1 baseline = 42 configs × (7 sextant + 10 test + 100 eval) ≈ 4,900
searches ≈ 10–20 min wall on a scratch server. Fine.

### 3.2 Deliverables

- `docs/work/2026-08-21-parameter-tuning-matrix.md` — the matrix document: reproduces the §2
  diagram, the knob table, one influence subsection PER KNOB. **Every influence claim must cite the
  matrix rows it is inferred from** (evidence-first; a claim without a row reference is not
  allowed in the doc).
- `docs/work/2026-08-21-parameter-tuning-matrix.csv` (or a committed JSON alongside) — raw sweep
  data produced by the matrix script.
- `scripts/retrieval_tuning/matrix.py` — the driver (see §9.4).

### 3.3 Influence statement template (per knob)

> **knob** — swept over [ladder] with others at baseline on [corpus, size N]. Mean nDCG@5 moved
> [x → y]; MRR [..]. Rank structure: [..]. Category affected: [file-targeted / non-file / both].
> Verdict: [no effect / marginal / strong, monotone / non-monotone / harmful beyond default].
> Evidence: matrix rows [ids]. Caveats: [corpus fragility, sibling-boost interference, ...].

---

## 4. Dataset 1 — sextant artificial bank

- Source: `/tmp/checklist-1-27-2/bank/memory.db` (9 entries, corpus appendix in the investigation doc §Appendix).
- Probe queries (reuse `/tmp/checklist-1-27-2/probe.py`): astrolabe-original, astrolabe-richer,
  sextant-zero-overlap, sextant-richer, invoice, widget-guide, alien-tokens.
- Known expected top-1 under defaults (from the investigation): astrolabe for Query A, guide-intro
  for Query B (sextant rank 4 fused / 3 pure-vector), invoice ranks low, alien-tokens tops guide
  siblings (sibling-boost trap).
- Use: fast diagnostic for the matrix + a "does this knob do what we think" sanity surface. The
  matrix script seeds `projectId=checklist-1-27-2` on a scratch server whose data-root is a copy
  of the sextant bank (copy the bank dir, never the original — the checklist run's files are
  scratch but treat them as read-only inputs).
- `scripts/retrieval_tuning/probe_sextant.py` — pinned probe runner; asserts the default-config
  top-5 hashes match the investigation doc's recorded table (guards against silent pipeline drift).

---

## 5. Dataset 2 — curated + copied project memory bank

### 5.1 The copy (read-only, verified)

Procedure (only permitted live-bank access is the `?mode=ro` connection):

```bash
mkdir -p /tmp/continue-testing-algorithm/datasets
sqlite3 "file:/Users/arasz/.ai-raccoon/memory.db?mode=ro" \
  ".backup /tmp/continue-testing-algorithm/datasets/memory-copy.db"
```

Verification gates (G1, §12):
- `PRAGMA integrity_check` = ok on the copy.
- Counts parity: `entries` total, `embed_state='embedded'`, `vec_entries_rowids` (or
  `SELECT count(*) FROM vec_entries`) equal between live (read-only) and copy.
- Copy opens and serves on a scratch server: `memory_stats` on the copy returns the same counts;
  one probe `memory_search` returns results.
- SHA-256 spot check: 3 random `(hash, value)` rows identical between live and copy.
- **Settings leak**: the copy inherits `fusion.noRegression.enabled.global=true` and
  `retrieval.structureAlpha=0.5` (§1). The harness therefore ALWAYS writes all 9 knobs explicitly
  per trial; the "baseline" config is an explicit write of all defaults, never the inherited state.
  `scripts/retrieval_tuning/make_memory_copy.py` wraps copy + verification + prints the inherited
  settings so the leak is visible in every run log.
- Maintenance risk: on server start the copy may run idempotent maintenance jobs (chunk-backfill,
  vec0-reclaim, vacuum — observed to be 2 ms no-ops on a consistent bank). Verify entry count and
  embedded count BEFORE and AFTER the first server start on the copy; if any job mutates the copy
  (counts change), investigate (check `ai-raccoon maintenance --help` for a disable/pause verb)
  before running any sweep. Corpus drift mid-study invalidates results.
- The copy is ~230 MB; disk is not a concern under /tmp. Do not commit the copy.

### 5.2 The curated test set (10 queries, 3-level grades)

File: `scripts/retrieval_tuning/corpora/test-set-10.json` (committed).

Composition (user requirement: md files WITH TABLES + exact queries + non-file memories):

| Bucket | Count | Content |
|---|---|---|
| Table-targeting ADR queries | 4 | Questions whose answer sits in a table of an ADR with tables (candidates: 0006, 0011, 0046, 0056, 0070, 0083 — pick 4) |
| Exact queries | 3 | Near-verbatim phrases from the target chunks (high BM25 overlap; tests the FTS leg) |
| Non-file-based memory queries | 3 | Topic queries against hermes transcripts / shared-tier entries |

Each query graded by a human (agent-assisted, but the grade + rationale is a recorded decision,
not an LLM output): **good** (target at rank 1–2, clean result) / **could-be-improved** (relevant
but misplaced or buried) / **just-wrong** (target missing from top-5 or results irrelevant).
Each entry carries the rationale. Schema: §5.3. Files chosen for the test set MUST be disjoint
from the 25 eval-set files (§5.4) — three disjoint document tiers: 10 test files? No — the test
set is query-level with per-query expectedSource; keep test-set target FILES disjoint from
eval-set target FILES (83 − 10 test − 25 eval = 48 untouched ADRs).

### 5.3 Corpus JSON schema (extends the baseline-queries.json model)

```json
{
  "id": "E042-D3",
  "category": "ADR (Decision)",                 // free label, grouped in reports
  "query": "...",
  "expectedSource": "docs:adr:0042-*.md#decision", // existing anchor style; resolved by
                                        // source_file suffix match in the copy
  "expectedHash": "abc123de",             // precise target chunk hash prefix (optional; for
                                        // non-file queries this is REQUIRED, expectedSource = null)
  "answerSpan": "short quote",            // for curation/verification, not scoring
  "targetProjectId": "ai-raccoon",        // resolved FROM THE COPY at build time (§5.5)
  "targetScope": "project",               // "project" | "shared" | "all" — resolved from the copy
  "searchLimit": 5,
  "relevanceGrade": 5,                    // 1-5; auto-set 5 for target chunk, 0 = negative test
  "negativeTest": false,
  "difficulty": "medium",                 // easy|medium|hard|very-hard
  "nonFileTarget": false                  // true for hermes/shared-tier targets
}
```

Test-set entries additionally carry `"grade": "good"|"could-be-improved"|"just-wrong"` and
`"gradeRationale": "..."`.

### 5.4 The eval set (100 queries)

File: `scripts/retrieval_tuning/corpora/eval-set-100.json` (committed).

**Split decision (made here, justification follows): N = 25 files × 3 ADR queries = 75, plus 25
non-file queries → exactly 100.**

- 83 ADRs exist; 3×83 = 249 > 100, so a subset is mandatory. N=25 samples ~30% of ADRs across the
  numbering range, favoring files with tables (0004, 0006, 0011, 0013, 0014, 0035, 0046, 0056,
  0060, 0067, 0068, 0070, 0072, 0078, 0083, ...) and multi-chunk files (sibling-boost realism).
- 3 queries per file (one per ADR section family: context / decision / consequences, or decision +
  2 content queries) gives per-file consistency checks and enough volume for per-file statistics.
- The 25 non-file queries target hermes transcripts (project `hermes-default`, chunk_index −1,
  vector-only-ish path, no sibling boost) and shared-tier entries — the file-targeted queries
  cannot cover these, and they are the bank's real-world "agent memory" population.
- The remaining 58 ADR files are NEVER named by any eval/test query: document-level holdout
  discipline mirrors ADR-0056 — the eval set stays an honest sample, and the untouched files are
  the natural extension set later.
- Deterministic construction: `scripts/retrieval_tuning/build_eval_corpus.py` reads the copy +
  `docs/adr/` listing, selects the 25 files (fixed seed + explicit allowlist in the script),
  generates the 75 ADR queries (each query's expectedSource/expectedHash resolved from the copy's
  entries), and the 25 non-file queries from hermes/shared entries (target hash = entry hash).
  Generated once, reviewed, committed as static JSON. Queries must be phrased as a user would
  actually search (paraphrase the answer, do not quote it) — except the 3 exact-query test items
  which intentionally quote.

### 5.5 projectId/scope resolution (why it is in the JSON)

Searches on the copy must hit the right bucket: ADR entries live under `project_id='ai-raccoon'`,
hermes transcripts under `project_id='hermes-default'`, shared-tier entries have `scope='shared'`.
The builder derives `targetProjectId`/`targetScope` from the target entry's row in the copy so the
harness never guesses (searches the same way Hermes does: `memory_search(projectId, scope)`).

---

## 6. Framework research and selection (deliverable 3)

### 6.1 Decision

**Optuna 4.x — TPE sampler.** Add `optuna` to the venv: `uv add optuna` in the repo root
(`pyproject.toml` gains the dependency; uv reconciles the py3.11 venv — Optuna supports 3.8+).
Optuna pulls numpy/packaging/tqdm/alembic+sqlalchemy (already satisfied or small); no other
changes.

### 6.2 Why (evidence)

| Criterion | Optuna | hyperopt | scikit-optimize | sklearn RandomizedSearchCV | Ray Tune |
|---|---|---|---|---|---|
| Maintenance (2026) | **Active** — 4.8.0 (2026-03), docs at 4.9.0 | Stale — no release since 0.2.7 (2021) | **Archived** (repo archived; community fork uncertain) | Active (but it is a sampler, not a tuner) | Active — but heavyweight infra |
| Sample efficiency for expensive objectives (each trial = 100 MCP queries) | **TPE** (+ GPSampler) — learns between trials | TPE (older implementation) | GP (weak categorical support) | None — random, no learning | TPE/others, but needs a Ray cluster or heavy local runtime |
| Heterogeneous space (int log, float, 2 categoricals, bool) | **define-by-run**: any mix, conditional params | OK | Weak categoricals | Grid/rand only | OK |
| Pruning / early stop | **Yes** (MedianPruner etc.) | No | No | No | Yes |
| Resume / storage | **SQLite study storage** — crash-safe resume, ideal for long runs | Mongo/Redis (awkward) | No | No | Yes (heavier) |
| Fit for 9 knobs, 1 box, ≤ a few hundred trials | **Yes** | Yes | Marginal | No (space too large to grid) | Overkill |

A 9-knob space (2 enums, 2 bounded floats, 3 ints, bool) cannot be gridded exhaustively (that is
exactly why a tuner is mandated), so RandomizedSearchCV is out on principle; scikit-optimize is
archived; hyperopt is stale and pruning-less. Optuna is the only candidate that is active,
sample-efficient, and matches the mixed space — and its study DB doubles as the audit trail
(every trial's config + metric).

### 6.3 Search space (per knob; bounds from validator + measured behavior)

| Knob | Distribution | Bounds | Default |
|---|---|---|---|
| rrfK | int log-uniform | [5, 200] | 60 |
| ftsWeight | int uniform | [0, 8] | 1 |
| vectorWeight | int uniform | [0, 8] | 1 |
| sourceLambda | float uniform | [0.0, 0.5] | 0.1 |
| consolidationThreshold | float uniform | [0.0, 0.5] | 0.1 |
| docScoreFormula | categorical | ["max", "sum"] | max |
| candidateWindow | categorical | ["max3x100", "max5x50"] | max3x100 |
| structureAlpha | float uniform | [0.0, 1.0] | 0.5 |
| fusion flag | categorical | [false, true] | false |

Notes: weights ≥ 1 dominate (0 = leg off is a legitimate trial point — keep it reachable);
lambda > 0.5 is unlikely to help and saturates the sibling trap (evidence: investigation §2);
weights beyond 8 add nothing measurable (RRF score compression at k=60). The **defaults config is
enqueued as trial 0** (`study.enqueue_trial`) so every run measures baseline in-study.

---

## 7. Metric design (deliverable 4)

### 7.1 Eval set scoring (auto, deterministic)

- Relevance: binary gain per result — gain 1 iff the result is the target chunk
  (`expectedHash` match, or `source_file` suffix matches `expectedSource` and, when the anchor has
  a `#section`, the result's section/heading matches). All other results gain 0.
- Primary objective: **mean nDCG@5** over the 100 queries (binary gains; log2 discount).
- Reported: mean MRR@5, hit@3, hit@1, per-category breakdown (file-targeted vs non-file), and
  per-query result rows (query, config, ranks, hits) — persisted per trial.
- Per-query pins: after the first full eval run at defaults, record each query's nDCG@5; the
  report lists queries where a tuned config REGRESSES vs defaults (regression table is a required
  report section, not an afterthought).
- **Discrimination proof (ADR-0056 discipline)**: the scoring suite must include a test that a
  REVERSED result list fails a floor the normal ranking passes (watched red before green, §12 G4).

### 7.2 Test set scoring (human, 3-level)

- The 10 curated queries are graded good / could-be-improved / just-wrong at default AND at the
  tuned config; the report shows the grade deltas. This is the second quality number the user
  asked for. 10 queries is too small to optimize on — it is the sanity check and the narrative.

### 7.3 Why not the LLM judge for the objective

`scripts/prometheus_grade.py` (LM Studio, m-prometheus-14b) exists, but its latency (~1 s+ per
call × 100 queries × hundreds of trials), nondeterminism, and unproven correlation make it wrong
for a deterministic optimizer objective. Keep it OUT of the objective; optionally run it once on
the final defaults-vs-tuned comparison as a third opinion (manual step, not a gate).

---

## 8. Harness architecture (deliverable 4)

New package `scripts/src/retrieval_tuning/` (importable as `retrieval_tuning` via the repo's
`pythonpath = scripts/src`), tests in `scripts/tests/test_retrieval_tuning_*.py`.

| Module | Responsibility |
|---|---|
| `mcp.py` | Minimal async MCP client (streamable HTTP, SSE parsing — pattern from `scripts/src/mcp_client.py` + `/tmp/checklist-1-27-2/mcp_call.py`). `memory_search(projectId, scope, query, limit, minScore)` WITHOUT tuning args (settings-driven); optional knobs for the verification path. Does NOT import `jsaa_config`. |
| `server.py` | Scratch server lifecycle: `ai-raccoon --data-root <root> serve --port 0` (background), parse bound port from serve.log, read token from `<root>/mcp-token`, health-check via `initialize`, teardown by PID (SIGTERM + wait). **Safety asserts: bound port != 7721, data-root != ~/.ai-raccoon.** One server per data-root; settings mutations need no restart (read per search). |
| `settings.py` | Apply a knob dict to the scratch server via `ai-raccoon --data-root <root> settings retrieval <verb> set <value>` subprocess calls; `reset_to_defaults()`; `show_all()` for the run log. All 9 knobs written explicitly per trial. |
| `corpus.py` | Load/validate corpus JSON (§5.3 schema): id uniqueness, counts, anchor resolution against the copy, disjointness checks (test vs eval), grade presence for the test set. |
| `scoring.py` | Relevance resolution (expectedHash / expectedSource suffix + section), nDCG@5, MRR@5, hit@3, hit@1, mean + per-category + per-query records. Pure functions — unit-tested with hand-computed values. |
| `evaluate.py` | `evaluate(server, settings_dict, corpus) -> Metrics`: apply settings, run queries (limit 5), score. Used by BOTH the matrix sweeps and the Optuna objective. |
| `matrix.py` | §3 ladder driver → matrix CSV/JSON + per-knob summary tables. Exits non-zero if any knob's sweep is empty or the baseline row is missing. |
| `tune.py` | Optuna study: TPESampler(seed=42), `study.enqueue_trial(defaults)`, storage `sqlite:///<scratch>/study.db`, objective = `-mean_nDCG5(eval_set)`; n_trials default 50 (flag-adjustable); per-trial logging; best-params → `docs/work/2026-08-21-tuned-parameters.json` (committed) + full study in scratch. |
| `report.py` | Final report `docs/work/2026-08-21-parameter-tuning-report.md`: defaults vs best on eval set (nDCG@5/MRR/hit@3 + category breakdown + per-query regression table) and test set (3-level grade deltas), plus the matrix-influence summary. |
| `make_memory_copy.py` | §5.1 copy + verification (counts, integrity, settings leak printout). |
| `build_eval_corpus.py` | §5.4 deterministic eval-set generator (reads copy + docs/adr listing). |
| `probe_sextant.py` | §4 pinned sextant probes with default-config hash assertions. |

### 8.1 Scratch layout

```
/tmp/continue-testing-algorithm/
  datasets/memory-copy.db          # the .backup copy (never committed)
  datasets/sextant-bank/           # copy of /tmp/checklist-1-27-2/bank
  runs/<date>/                     # per-run: serve.log, matrix.csv, study.db, trial logs
```

### 8.2 Determinism and drift guards

- Corpus frozen: the copy is never written by the harness (searches only). Settings writes go to
  the settings table (a write — but to the COPY's settings table, which is scratch by definition).
- Maintenance no-op check before any sweep (§5.1).
- Fixed Optuna seed; deterministic scoring; a full defaults run at the START and END of every
  tuning session must produce identical metrics (drift check, asserted by `tune.py`).

---

## 9. Work packages and parallelization

Single worktree, one PR; lanes are logical parallelism (separate agents can run lanes 1–3
concurrently; lane 4 unit work starts immediately; lane 5 needs lanes 2–4; lane 6 last).

```
Lane 1 (matrix)      ─┐
Lane 2 (copy+test)   ─┼─► Lane 4 (harness core + unit tests) ─► Lane 5 (optuna run) ─► Lane 6 (report/PR)
Lane 3 (eval corpus) ─┘        ▲ Lane 4 integration needs lanes 2–3 outputs
```

| WP | Lane | Files (all in worktree unless noted) | TDD / gate |
|---|---|---|---|
| WP0 | — | This plan reviewed; scratch root created | owner review (G0) |
| WP1 | 1 | `scripts/retrieval_tuning/matrix.py`, `probe_sextant.py`; diagram + knob table into the matrix doc skeleton | unit tests for ladder definition + CSV shape (G3); probe hash assertions (G1) |
| WP2 | 2 | `make_memory_copy.py`, `corpora/test-set-10.json` (curated, graded) | copy verification tests (G1); corpus schema tests (G2) |
| WP3 | 3 | `build_eval_corpus.py`, `corpora/eval-set-100.json` | deterministic generator tests + anchor-resolution tests (G2) |
| WP4 | 4 | `mcp.py`, `server.py`, `settings.py`, `corpus.py`, `scoring.py`, `evaluate.py` + tests | RED-first unit tests: hand-computed nDCG, port parsing, safety asserts, settings-verb strings (G4) |
| WP5 | 5 | `tune.py`, `report.py`; `uv add optuna` | discrimination test RED→GREEN (G4); drift check (G5) |
| WP6 | 6 | `docs/work/2026-08-21-parameter-tuning-matrix.md`, `...-report.md`, `2026-08-21-tuned-parameters.json` | evidence-first doc review (G3/G5); PR (G8) |

Recommended FIRST action: **WP2's copy step** (2 minutes, unblocks lanes 2–5, carries the hardest
safety constraint), in parallel with **WP1's sextant probe + matrix skeleton** (dependency-free).

---

## 10. Tuning run specification

- Objective: `mean nDCG@5` over eval-set-100 (maximize; Optuna minimizes → negate).
- Sampler: `TPESampler(seed=42, n_startup_trials=10)`; `study.enqueue_trial(defaults)`.
- Trials: default 50 (each ≈ 100 searches ≈ 1–3 min; 50 trials ≈ 1–2.5 h — run in background,
  `notify_on_complete`). Budget flag `--trials`.
- Storage: `sqlite:////tmp/continue-testing-algorithm/runs/<date>/study.db` (resumable).
- Success criterion: report shows best-vs-defaults on BOTH quality numbers (eval metrics + test
  3-level grades) with the per-query regression table; a config that improves the mean but
  regresses >5 eval queries on nDCG@5 vs defaults is flagged (no auto-ship without owner review
  of the regression table).
- Output: `docs/work/2026-08-21-tuned-parameters.json` (all 9 values + eval/test metrics +
  study id + run date).

---

## 11. Risks

| Risk | Mitigation |
|---|---|
| Copy drifts mid-study (maintenance re-chunk/re-embed) | §5.1 count-parity before/after server start; drift check reruns defaults at session end; investigate `maintenance` verb if needed |
| Inherited settings leak into "baseline" | all 9 knobs written explicitly per trial; baseline = explicit defaults; leak printed by `make_memory_copy.py` (§1 evidence: fusion flag IS true live) |
| Optimizer overfits the 100 eval queries | document-level holdout (58 untouched ADRs); regression table gate; test set as independent sanity check; report states the risk explicitly |
| Weights int-only flattens the fts/vector trade | acknowledged constraint (MCP+settings both int); ladder uses ints; if the matrix shows a real need for fractional weights, that is a FINDING for a future C# change (separate task, ADR) |
| Optuna deps vs py3.11 venv / pyproject ≥3.12 | `uv add optuna` reconciles (uv manages the venv); if uv insists on 3.12, that is a venv refresh, documented in the PR |
| Tuning run too long | 50 trials × 100 queries is the default; `--trials` and `--limit` (top-k eval subset) flags exist for dry runs; study DB resumes |
| Sextant bank claims are corpus artifacts | every matrix/report rank claim states corpus + size (RRF fragility discipline, §2) |
| Live server/port accidents | server.py asserts; `lsof -i :7721` before and after every run in the run log (G7) |

---

## 12. Acceptance criteria and gates

| Gate | Criterion | Exact proof |
|---|---|---|
| G0 | Plan reviewed (owner/MoE) | review record in `docs/work/` per convention |
| G1 | Datasets exist and are verified | copy: `sqlite3 /tmp/continue-testing-algorithm/datasets/memory-copy.db 'PRAGMA integrity_check;'` = ok; counts parity vs live read-only (22,509 entries / 22,509 embedded); spot-check hashes; sextant bank: 9 entries, probe top-5 hashes match investigation doc under defaults |
| G2 | Corpora valid and honest | `pytest scripts/tests -k corpus` green; eval-set-100 = 75 + 25 exactly; all anchors resolve in the copy; test-set 10 all graded 3-level with rationale; no query text shared between test and eval; test-target files disjoint from eval-target files; every non-file query has `expectedHash` |
| G3 | Matrix complete and evidence-backed | `python scripts/retrieval_tuning/matrix.py` runs end-to-end on both datasets; matrix.md contains diagram + per-knob influence statements each citing matrix rows; RED witness: matrix.py fails (non-zero) when a knob sweep is empty — watched red before trusted |
| G4 | Harness correct, TDD-proven | `pytest scripts/tests` green incl. new tests; RED witnesses: scoring test with hand-computed nDCG fails before implementation; discrimination test (reversed ranking fails the eval floor) fails before the floor exists |
| G5 | Tuning done by ML framework, reported honestly | optuna study (≥50 trials default) completes; `2026-08-21-tuned-parameters.json` committed with both quality numbers; report contains defaults-vs-best eval table + per-query regression table + test-set grade deltas; drift check (defaults metrics identical at session start and end) passed |
| G6 | No regression in repo gates | `dotnet build` + `dotnet test` green (no C# change expected — proves no drift); `uv lock` updated; python tests green in CI config |
| G7 | Safety | run logs show bound ports ≠ 7721 for every scratch server; `lsof -i :7721` before/after identical (PID 5537 untouched); no harness code path contains `~/.ai-raccoon` as a write target (grep gate); live bank accessed only via `?mode=ro` |
| G8 | One PR | branch `task/continue-testing-algorithm-u1` → single squash-merge PR via the user's GitHub automation; title per convention |

---

## 13. PR and delivery

- All committed files in the task worktree: `scripts/retrieval_tuning/*`, `scripts/tests/test_retrieval_tuning_*.py`, `scripts/retrieval_tuning/corpora/*.json`, `docs/work/2026-08-21-parameter-tuning-*.md` (+ `-tuned-parameters.json`), `pyproject.toml`/`uv.lock` delta (optuna).
- NOT committed: `/tmp/continue-testing-algorithm/**` (copy, study DB, logs).
- PR body summarizes: matrix headline findings, tuned params + both quality numbers, regression table link, safety evidence.

---

## 14. Open decisions made in this plan (for the reviewer)

1. **100-query split: 25 ADR files × 3 + 25 non-file = 100** (§5.4) — 30% ADR sample across the
   numbering range favoring table-bearing multi-chunk ADRs; 58 ADRs stay untouched (document-level
   holdout, ADR-0056 discipline); 25 non-file queries keep the vector/hermes path represented.
2. **Metric: mean nDCG@5 (binary gain) primary; MRR@5/hit@3/hit@1 reported; 3-level test set as the
   second quality number; LLM judge excluded from the objective** (§7).
3. **Knob search space** (§6.3) — bounds justified by validator + measured RRF behavior.
4. **Settings-driven trials** (all 9 knobs via `settings retrieval ... set`, not MCP args) —
   one mechanism, precedence-safe, covers settings-only knobs.
5. **Optuna/TPE** (§6) — active, sample-efficient, mixed-space fit; study DB = audit trail.
6. **Test-set files disjoint from eval-set files** (§5.2) — keeps the 10-query set a true
   independent check.

Review questions the owner may want to answer: is 50 trials the right budget (vs 100)? Should the
tuned parameters ship as NEW defaults (ADR-0006 amendment) in this PR or a follow-up? (This plan
delivers the numbers + recommendation; changing shipped defaults is a separate decision.)
