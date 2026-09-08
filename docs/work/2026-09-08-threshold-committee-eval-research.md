# Research record — threshold τ=0.95 eval with committee grading (2026-09-08)

Task: `air-threshold-filter-corpus-committee-eval` (low effort). Every finding cites its
source; unverified claims are labelled hypotheses.

## What exists (verified this session, 2026-09-08)

1. **Post-fusion threshold PoC code, working but UNCOMMITTED** — lives only in worktree
   `.ai-badger/worktrees/air-mmr-postfusion-poc-eval-run` (branch `task/air-mmr-postfusion-poc`
   has none of it; `git ls-tree` shows 3 modified + 1 untracked):
   `MaximalMarginalRelevance.cs` (new), `SearchResultMerger.cs`, `SqliteMemoryStore.cs`,
   `MemorySql.cs`. Source: `git status --porcelain` in that worktree, this session.
2. **Sequential single-server MCP-stderr harness proven**: 13 runs tonight, pools 230–365,
   one batched sidecar blob fetch each, no model loads, flat RAM. Source:
   `/tmp/mmr-post-*.stderr`, `/tmp/mmr-jsaa-*.stderr`, eval report §RAM.
3. **Per-project scoping is load-bearing**: Q1/Q2 are jsaa-domain; running them under
   project `ai-raccoon` silently serves 0/8 overlap. Every generated query MUST carry its
   own projectId. Source: eval report §"Method note".
4. **Corpus generators to reuse**: `scripts/retrieval_tuning/build_eval_corpus.py`
   (copy-read-only, 75 file-targeted with `expectedHash` + 25 non-file, `RESERVED_TEST_FILES`
   holdout, SEED determinism contract); `scripts/src/benchmark_corpus.py` (doc-derived +
   cross-repo cluster queries, keyword-verified judgments). Source: file headers, read today.
5. **Rubric precedent**: A=1 iff the chunk answers the query; answer-chunks/8, docs-with-answer,
   unique-docs, facets; single grader, no blind. Source:
   `docs/work/2026-09-08-mmr-postfusion-eval.md` §Grades.
6. **Effect sizes for power math** (from the 40-slot eval): drop rate ~2/40 = 5%, quality
   delta 1/40, per-query deltas mostly 0 with σ ≈ 0.4 answer-chunks. Source: same report table.

## Decided by owner (this session)

- Arms: `off` + `threshold τ=0.95` only. MMR discarded.
- Committee: 3 graders, identical gated form (grade counts only with completed form:
  per-chunk {hash, grade A|0, reason} + overall). Consistency = 3/3 identical per-chunk
  vectors; majority never accepted. Inconsistent → nudge round ("argue against your own
  grades"), ≤3 rounds → replace query, ≤3 replacements → ≤9 attempts/slot; exhausted slot
  reported, never skipped.
- Grader models: #1 session default, #2 `openrouter/meta/muse-spark-1.3-contributor`,
  #3 `xiaomi/mio-v2.5-pro` (owner's explicit pick). A/B fresh trio = same composition.
- Blind A/B second pass on the sample: (query, A, B), randomized unlabeled order, 1 pass,
  comp_score = x/3 for threshold, reasons distilled to core sentences by the orchestrator.
- Sample size: 16 queries = 10 threshold-changed + 6 identical-arm controls (stratified on
  mechanical diff only, never on grades). Hypothesis→math: CI half-width on per-query answer
  delta ≈ 1.96·0.45/√16 ≈ 0.22 chunks; sign test detects 13/16 same-direction (two-sided
  p≈0.021); mechanical n=100 carries the primary inference.

## Known unknowns (hypotheses, to resolve in-plan)

- H1: `miro-v2.5-pro`/`muse-spark` accept the same brief shape headless (`pi -p`) — verify
  with one smoke grading call before the fleet runs.
- H2: per-project query generation quality — paraphrase templates may under-cover code-ish
  content (ADR-anchored precedent is doc-heavy). Mitigate: expectedHash auto-grade flags it.
- H3: DB-copy freshness — copy is a snapshot; bank drift between corpus build and runs is
  harmless (both arms run on the same copy) but the report must state the snapshot hash.
