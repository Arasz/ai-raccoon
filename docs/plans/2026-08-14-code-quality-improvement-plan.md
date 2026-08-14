# Improvement plan — 2026-08-14 MoE codebase review

Source: `docs/reviews/2026-08-14-moe-codebase-review.md`. Base commit `b4581717`.
Baseline to preserve: build 0/0; `dotnet test` 2277 passed / 0 failed / 6 skipped / 5m34s.

## Sequencing rationale

Ordered by the task's rule — fast improvements and high-priority defects first — with one
override: **WP2 (deletion) runs before WP1 (honesty), even though WP1 holds the blocker.**
Making a subsystem report honestly and then deleting it is wasted work. Deleting first shrinks
what WP1 has to be honest about to a single deterministic policy, and it also removes 8 of the 9
files in QA-F5 and 6 of the 10 uncalibrated thresholds as a side effect.

Every package names its acceptance criteria and the gate that proves them. Per the project's
invariant, **each new gate is broken on purpose first and watched go red** before it is trusted.

## Urgency calibration (measured against the live bank)

The deployed bank (15,236 entries, build 1.11.0, which *does* contain both ADRs) shows
`noise_entries = 0` and `ttl_days` NULL on every row. **Neither blocker has fired in
production.** Both code paths are exactly as the review describes; the reason they have not bitten
is behavioural — the filter's own 2/12 recall, and real writes running 64–166 words.

Consequences for this plan:

- **This is not a hotfix.** Nothing is being lost right now, so the waves can be done properly
  with tests rather than rushed. The plan's ordering stands.
- **But the reaper is armed:** `access.mode.global=full`, `sweep.enabled.global=true`. The margin
  is luck, not design.
- **Hard constraint:** no work that increases the noise filter's recall may land before WP1's
  honest write outcome. Doing so would convert a dormant defect into an active one. WP2 deletes
  the filter, which removes the hazard rather than tuning it — a further argument for the
  chosen ordering.

## Wave 1 — The write path (blocker B1)

One coherent change to one surface. Ships as one PR.

### WP2 · Delete the unearned judgement layers — do this first
**Effort:** QUICK–SMALL · **Net:** ~400 production lines, 2 schema tables, 4 test files removed

Delete: `ZeroShotEmbeddingNoisePolicy`, `BundledNoiseVectorProvider`, `INoiseVectorProvider`,
`ZeroShotEmbeddingFilter`, `OnlineNoiseClusteringService`, `NoiseFeedbackCollector`,
`INoiseClusterStore`, `PromotionScorerTtlPolicy`, `IAutoTtlPolicy` (if no other implementer),
`SqliteNoiseStore` + `noise_entries`, and the four matching test files. Remove the
`noise_clusters` / `vec_noise` / `noise_entries` DDL from the fresh-bank path (leave existing
tables — dropping them needs a migration and they are empty).
Keep `HermesProcessNoisePolicy` — deterministic, free, and measured to do 50/50 of the work.

**Justification against the decision test:** simplifies (yes — 400 lines, 2 tables, 6
thresholds), improves performance (yes — measured ~6.9 ms off every valid write), improves
maintainability (yes — removes two subsystems with no reachable caller).

**Acceptance criteria**
1. `grep -rn -E 'ZeroShot|NoiseCluster|NoiseFeedback|PromotionScorerTtl' src/` returns nothing.
2. A `memory_write` of a Hermes background-process log is still rejected (the surviving policy).
3. A `memory_write` of ordinary short content (`"Push after every commit."`) is **stored, with
   no `ttl_days`** — this is the RED test today.
4. Full suite green; no schema version bump needed (DDL removal only affects fresh banks).

**Gate:** new test `ShortOrganicWrite_IsStoredWithoutTtl` — must be seen RED on current `main`
(today it gets `ttl_days = 3`) and GREEN after. Paste both.

**Supersedes:** ADR-0029 (whole), ADR-0030 (whole). **Restores:** ADR-0025 "Fact 1".

### WP1 · Make `memory_write` report the truth — **BLOCKER B1**
**Effort:** SMALL

Add `Stored: bool` and `Reason: string?` to the write result type and to the MCP `WriteResult`;
update the tool description, which currently says "Returns the stored entry" unconditionally.
Add a `noise.enabled` settings key mirroring the existing `sweep.enabled` convention (key, CLI
verb, default on). Delete the `"noise_hash"` / `"noise_path"` sentinels.

**Acceptance criteria**
1. A rejected write returns `stored=false` with a `reason` naming the policy, and **no fabricated
   hash**.
2. A stored write returns `stored=true` and a real hash that `memory_get` (WP3) resolves.
3. `noise.enabled=false` disables rejection entirely; verified end to end.
4. The tool description states that a write may be refused and how to tell.

**Gate:** `RejectedWrite_ReportsNotStored` — RED today (returns a success envelope with
`"noise_hash"`), GREEN after. Plus a CLI/live check that `memory_delete` on a rejected write's
response is not possible because no hash is offered.

## Wave 2 — The read path (blocker B2) + data integrity

### WP3 · Make retrieval usable and honestly scored — **BLOCKER B2**
**Effort:** SMALL–MEDIUM

1. **Add `memory_get(projectId, hash)`** returning the full entry value. (B2's whole fix.)
2. **Remove the degenerate second RRF pass.** `SearchResultMerger.Merge([fused], …)` re-fuses an
   already-fused single list, so `Ranking` collapses to `61/(60+rank)` — pure position. Pass
   `fused` straight to `SourceAffinityRanker.Rank` to restore real magnitudes.
3. **Fix the missing-modality bias.** `StructureFusion.Fused` treats a missing structure vector
   as `0.0`, capping heading-less rows at `α×contentSim`. Renormalise over observed modalities,
   or fuse the two vector legs by RRF as the FTS/vector legs already are (preferred — deletes
   `Fused` and the alpha setting outright).
4. **Make `min_score` mean something.** Carry the best absolute signal (max cosine, bm25) on the
   result and filter on that, so an agent can tell a decisive hit from a desperate one.
5. Make the vector-hit snippet query-relevant instead of hash-seeded; raise the FTS snippet to
   ~40 tokens with real `…` delimiters.
6. Drop the dead `Seq` field and the redundant hash-derived `Path` from search results.

**Acceptance criteria**
1. `memory_get` returns the full value for a hash from `memory_search`.
2. A high-content-similarity heading-less note ranks above a low-content-similarity
   heading-bearing chunk in a mixed bank. **This is the RED test.**
3. Two searches with genuinely different best-match quality return different top scores (today
   the top result is always exactly 1.0).
4. Snippet for a vector hit contains query-relevant text, verified on a fixture.

**Blocked on:** WP4 for criteria 2 — no corpus in the repo currently contains a structure vector.
Do WP4 first or build the mixed bank inside the test.

**Amends:** ADR-0004, ADR-0006.

### WP5a · Data-integrity quick wins
**Effort:** QUICK (all four)

- Wrap `DeleteCoreAsync`'s entry-delete + tombstone-insert in one `BEGIN IMMEDIATE`/`COMMIT`
  (the pattern two methods below already does this correctly). Prevents sync resurrecting
  deleted content.
- `PRAGMA synchronous=NORMAL` alongside the existing WAL pragma — the standard WAL pairing, and
  it multiplies every other round-trip fix.
- Wrap `RememberDiscardsAsync`'s per-hash loop in a transaction (its sibling already does).
- Delete the dead `EntryExistsByPathInBucket` SQL constant.

### WP10b · Test the two things that guard data loss
**Effort:** QUICK–SMALL · *Run alongside WP5a — same methods*

- A unit test for `LikePattern.Escape` (literal `%`, `_`, `\` round-tripped through a real
  `LIKE … ESCAPE` match). It currently has **zero** test references anywhere in the suite.
- An integration test where one path is a literal-wildcard substring of another (`note_1.md`
  vs `noteX1.md`) proving the cascade delete does not over-match.
- A forced-failure-mid-transaction test proving the rollback branch leaves the bank unchanged.

**Gate:** break `LikePattern.Escape` (drop the `_` escaping) and watch the new test go red.

## Wave 3 — Measured performance

### WP6 · Stop paying per-row costs on the hot paths
**Effort:** SMALL, high measured payoff

- Add `idx_entries_project_committed ON entries(project_id, workspace_id) WHERE workspace_id IS
  NULL` (+ migration ladder step). **Measured 2.3–2.8× on the `memory_write` dedup check and the
  watch replace-by-path delete at only 20K rows**, widening with bank size.
- Batch `BumpAccessAsync` into one `WHERE hash IN @hashes` + one transaction — today it issues a
  SELECT and an UPDATE *per result* on every search, and takes a write lock on a read.
- Make `ToolGate`'s promotion-queue meta opt-in instead of running on all 25 tools.
- Drop the redundant EXISTS pre-check in `FileIngestor` (the INSERT already has `ON CONFLICT DO
  NOTHING`) and wrap a file's chunks in one transaction.
- Route `EmbedContentAsync` through the bank's configured engine so it stops opening a **second
  23 MB ONNX session** (and stops loading the local model on `openai`-configured banks).
  *Largely moot after WP2 — verify and close.*
- Replace the O(n²) `Skip`/`Take` slicing in the bulk re-embed loop with range indexing.
- Replace `EmbeddingBlob`'s per-float loop with `MemoryMarshal.Cast`.

**Acceptance criteria:** a before/after measurement recorded for the index change and for
`BumpAccessAsync` round-trip count, using a real harness — not the current benchmark test, which
WP10 fixes first.

### WP7 · Fix chunking so content reaches the embedding
**Effort:** QUICK–SMALL for the first two

- **Count tokens with the tokenizer that will embed.** The budget counts `o200k`; the model
  tokenizes BERT WordPiece at a measured p95 ratio of 1.217, so **37.5% of chunks (1331/3552)
  silently exceed the 256-token window and are truncated**. Make `TokenCount` engine-aware, or
  set the local budget to 200 as a stopgap. Log a counter when truncation happens.
- **Cap fence atomicity.** One unbalanced ` ``` ` currently makes the document remainder a single
  atomic chunk — measured 5621 tokens of which **95% never reaches the embedding**. Split any
  unit exceeding `maxTokens`.
- JSON key-path flattening (MEDIUM) — defer to Wave 5 unless JSON ingestion is in active use.

## Wave 4 — Gates that can fail

### WP4 · Regenerate the retrieval gate corpus
**Effort:** MEDIUM · *Prerequisite for proving WP3 correct*

`jsaa-memory.db` has **0 of 761 rows with a structure vector** and `vec_structure` is empty;
`RealWorldCorpus` is heading-flattened and ingested without `sourceFile`. So neither the
structure modality (ADR-0004) nor source-affinity ranking (ADR-0005) is exercised by any gate.
Regenerate through the production `FileIngestor` so heading paths, structure vectors,
`source_file` and `chunk_index` all exist; re-pin the numbers.

Also: delete `GoldenFile_MatchesFreshReferenceRun` — it tests a vendored legacy extension, not
AiRaccoon's search path, and cannot fail on any retrieval change while occupying a Slow CI slot.
Reconcile the nDCG floor with ADR-0006 (ratcheted 0.722 → 0.674 with no recorded reason): either
recover the 0.048 or amend the ADR with the measurement that justifies it.

### WP10 · Make the remaining gates honest
**Effort:** SMALL

- Fix the write-performance benchmark: assert the 50 valid notes were **stored** (today their
  return values are discarded, so a filter rejecting 100% of input passes), assert *which policy*
  intercepted so removing one turns it red, stop writing into tracked `docs/`, and either fix the
  allocation measurement (`GC.GetTotalAllocatedBytes`, not the per-thread counter read across
  `await` — which is why the committed report says `-549,67 KB`) or delete the metric.
- Commit a small labelled fixture (10–20 paraphrased rows) so the promotion scorer has a gate
  with a hardcoded floor. Today the running gates derive their thresholds from the code's own
  output at test time; zeroing every prior leaves all six green.
- Resolve the 6 skipped tests: un-skip or delete. `All 17 tools are still listed` pins a count
  that is now 25 — derive it from the `[McpServerTool]` attributes or delete it.
- Correct `PromotionScoringRealDataTests`' stale prior table (its documented ordering is no
  longer monotone after the round-3 refit).
- Add the `NoiseFilteringService` clean-path / short-circuit / null-store tests (QA-F3) — *only
  if the service survives WP2*.

## Wave 5 — Structure, silent failure, and polish

Everything below is real but none of it loses data today. Order within the wave by effort.

- **WP9 (silent failure):** watch-embedding retry sweep; Bitwarden key caching + async (a
  blocking `bws` shell-out on **every bank open** today); wire SIGINT/SIGTERM to the CLI/proxy
  `CancellationTokenSource`; the one direct `logger.LogWarning` on the search hot path.
- **WP8 (structure):** extract `ISettingsStore` from the 25-member `IMemoryStore` (moves 8 of 25
  dependents off it); split `SqliteMemoryStore` along its six seams and move its ~150 lines of
  pure domain rules to Core; de-duplicate the access-mode resolver (two verbatim copies both
  gating destructive deletion); remove the `IMemorySourceStore` downcast; split `Core/Memory/`'s
  41-file bucket; delete the duplicate DI registrations; move `Core/Resilience` out of Core and
  replace its type-name string match.
  **Sequencing note:** at 2.5:1, this touches far more test code than production code. Size it
  before committing to it.
- **WP11 (CLI/agent surface):** exit codes; the triple-printed parse error; the raw errno leak;
  the logger category leak on non-quiet runs; `serve` silently ignoring `--data-root` when
  attaching; `--json`; the reference doc missing 2 of 25 tools; the broken mermaid block and the
  missing verify step in the tutorial; verb consistency; the two tool-merge candidates; ADR-0027
  and ADR-0029 name drift; the CLAUDE.md `project_id` casing (LOW — see O14).
- **WP10c (test hygiene):** `NodeRunnerTests`' 116s; the `FakeTimeProvider` conversions; the
  trait-convention sweep (one file after WP2); the `ConfigCommands` harness extraction.
  **Do not scope a test-shrinking effort** — the suite's size is earned breadth (see the review's
  disconfirmed-hypothesis section); only ~180–220 lines are justifiably deletable.

## ADRs to write

| ADR | Decision | Supersedes / amends |
|---|---|---|
| 0032 | A write outcome is truthful: `Stored` + `Reason` on the result and the tool contract | supersedes ADR-0029 §Decision 2 |
| 0033 | Remove the zero-shot noise filter and the noise-learning subsystem; keep the deterministic policy | supersedes ADR-0029 |
| 0034 | An explicit TTL is authoritative; no heuristic assigns one at write time | supersedes ADR-0030; restores ADR-0025 Fact 1 |
| 0035 | `memory_get`, and search scores that carry absolute signal | amends ADR-0004, ADR-0006 |

(0028 is absent from the sequence; 0032 is the next free number.)

## Risks

- **WP3's fusion change alters ranking for every existing bank.** It needs WP4's corpus to
  measure, or it is an unmeasured change to the product's core function. Do not ship it on
  reasoning alone.
- **WP2 deletes code with passing tests.** Those tests pass over unreachable code; deleting both
  together is correct, but the diff will look alarming. The ADR must record why.
- **WP6's index needs a migration ladder step** on non-empty banks.
- **WP8 is the one package that could exceed its estimate**, because of the test ratio.

## Explicitly not doing

- Shrinking the test suite (measured: earned breadth, not bloat).
- Rewriting the 319 long doc comments — they cite ADR paths and carry real rationale; an
  analyzer that flags *new* ones stops the drift without the churn.
- ANN / vector-index work. `memory_search` is 94 ms p50 at 174 docs, dominated by fixed overhead
  (WP6), not the scan. Revisit when a single project approaches ~50K entries.
- FTS5 project partitioning (RAG-F7) — accepted trade-off; record it in an ADR note instead.
