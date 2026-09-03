# Research: promotion-queue improvements

**Date:** 2026-09-03
**Question:** How can we improve the promotion queue given scorer v2's good head, mushy middle, and full bucket?

## Findings

### F1 — The queue is full at 1000/1000 with a thin head and a fat middle [MEASURED]

Reproduced the quality-report shape against the live bank read-only: 1000 rows, avg 2.63, max 4.0, all scorer v2; buckets ≥3.5: 21, 3.0–3.5: 259, 2.5–3.0: 384, 2.0–2.5: 254, <2.0: 82. Project split is fragmented: deepseek-harness 157, jsaa 157, ai-badger 156, ai-raccoon 156, arasz-home-page 156, job-search-ai-assistant 89, hermes-default 68, remainder small. Zero queued values already exist verbatim in shared (no double-promotion waste); per-doc flood cap holds (top source_file count 3, 70 rows with null source_file).

**Evidence:** `sqlite3 ~/.ai-raccoon/memory.db "SELECT count(*), round(avg(score),2), max(score) FROM promotion_queue;"` → `1000|2.63|4.0`, plus bucket/project/scorer-version/overlap queries in the same session, on this machine (macOS, .NET 10.0.400), live bank read-only, no writes.

### F2 — Rule phrasing dominates the reasons table over durability signals [MEASURED]

Counted reason tags over all 1000 rows: rule-language 743, durable-rule-language 669, work-note 350, mid-sentence 295, verified-contract 295, organic-note 216, portability 188, measured-values 177, foreign-subject 152, durable-fact-language 142, adr 107, status-vocabulary 97. Anything phrased imperatively outranks observations, measurements and decisions by sheer frequency — the extractor ranks tone above durability.

**Evidence:** `python3` + `sqlite3` read of `SELECT reasons FROM promotion_queue` (1000 rows) with `collections.Counter` over the JSON tag arrays, same machine and bank as F1.

### F3 — At-cap shedding evicts the lowest score from the biggest occupier, so the queue is not frozen [READ]

The prior report's open question is settled in code: `ProposeAsync` upserts first, then loops `while (NeedsEviction(total, cap))` picking `EvictionTarget(perProject)` and deleting via `EvictVictim`, whose victim query is `ORDER BY score ASC, created_at ASC, id ASC LIMIT 1`. The policy is uniform fair-share — the project with the greatest queued count loses its weakest row. New candidates therefore displace the 82 sub-2.0 rows first; inserts are never refused at cap.

**Evidence:** `src/AiRaccoon.Infrastructure/Promotion/PromotionQueueService.cs:46-70` (upsert-then-evict loop), `src/AiRaccoon.Infrastructure/Sqlite/PromotionQueueSql.cs:110-125` (`EvictVictim` ordering), `src/AiRaccoon.Core/Memory/IEvictionPolicy.cs:19-33` (`UniformCountEvictionPolicy`: biggest occupier, ordinal tie-break), `src/AiRaccoon.Core/Memory/PromotionCapacityPolicy.cs:22` (`totalCount > totalCap`).

### F4 — The admission floor admits the whole mushy middle by design [READ]

`CandidateFloor` is 0.4 on the 0–4 scale — deliberately set in the gap between the hard-noise ceiling (0.35) and the weakest real channel (`plan` 0.70) — and every row at or above it is eligible (`if (score >= CandidateFloor)`). Each propose pass then takes up to `DefaultCandidateLimit = 20` new rows per project after refreshing already-queued rows regardless of rank, with `MaxQueuedPerSourceDocument = 3` capping per-document flood — except rows with null `source_file` (organic notes), which are explicitly exempt and always admitted.

**Evidence:** `src/AiRaccoon.Core/Memory/SharedExtractionService.cs:11` (limit 20), `:17` (floor 0.4), `:24` (per-doc cap 3), `:91` (floor check), `:115-150` (refresh-anything-queued + `Take(limit)` + `CapPerSourceDocument` null exemption); floor rationale in `docs/adr/0018-promotion-scoring-v2.md` v3 section ("kept at 0.4 … hard-noise ceiling 0.35").

### F5 — rule-language fires unconditionally while durability signals need conjunctions [READ]

In the doc-channel evidence, `rule-language` is added whenever `RuleDensity > 0` — any single match of a broad regex (must/never/always/prefer/invariant/trap/gotcha/contract/semantics/…) — with a centred bonus (`0.38 × density − 0.20`, capped 1.00, floored −0.20). The genuinely sharing-shaped signals all need more: `measured-values` needs a measure-word plus a number-with-unit (or ≥2 measure-words), `verified-contract` needs measure-words AND rule-density ≥ 0.8, `foreign-subject` needs another project's alias in the opening 250 chars, `portability` only applies to the doc family. A one-word imperative out-tags a measurement with units.

**Evidence:** `src/AiRaccoon.Core/Memory/PromotionContentEvidence.cs:55-110` (`Evaluate`: rule branch vs `MeasuredBonus`, `Portability`, `verified-contract` conjunction), `:246-260` (`Durability`/`Portability` centring constants), `src/AiRaccoon.Core/Memory/CandidateFeatures.cs` (`RuleLanguage()` broad alternation vs `MeasureWordsRegex()`+`NumberWithUnit()` conjunction and `ForeignSubjectHeadChars = 250`).

### F6 — The highest-leverage fixes are scorer rebalancing, admission tightening, and review ordering [INFERRED]

Reasoning from F1–F5. (a) Scorer: centre or threshold `rule-language` (require density ≥ ~0.5 before tagging, or cap its bonus below `durable-rule-language`/`measured-values`), and upweight the conjunction signals that actually mark shareability (`foreign-subject`, `verified-contract`, `dated-fact`, ADR-over-work-note prior already exists — extend it). (b) Admission: raise the effective floor when the queue is near cap (dynamic floor = current eviction victim's score, so a pass only churns when it beats what would be evicted), canonicalize the jsaa/job-search-ai-assistant project-id split so fair-share sees one occupier, and close or bound the organic null-source exemption (per-project per-pass cap instead of unlimited). (c) Review: review head-first (21 rows ≥3.5, then 3.0–3.5), age out sub-2.5 rows untouched for N days instead of keeping them as permanent eviction buffer, and surface per-reason score decomposition in `memory_promotion_list` so a reviewer sees why a 2.6 differs from a 3.6. None of these changes the public `Run`/`RankAll` contract — all live in scorer constants, `SharedExtractionRunner` admission, and queue policy.

### F7 — The test-runner gate for verifying any of this locally currently runs zero tests [UNVERIFIED]

`dotnet build` succeeds but `dotnet test` (solution and filtered `PromotionCapacityPolicy|PromotionQueueService`) exits 5 with "Zero tests ran" on this machine; whether that is a runner/TFM invocation mistake or a real discovery breakage was not chased, so no scored-behavior change proposed here has a witnessed green gate yet.

## Still open

- What reweight values actually move the needle: re-running the round-3 lane-A fixtures (`split_train/validation/holdout` via `docs/work/promotion-scoring-eval/score_round.py`) against a rebalanced prototype is what settles (a) — unmeasured here.
- Whether the dynamic-floor admission causes starvation for small projects under `UniformCountEvictionPolicy` (biggest-occupier eviction already favors them; a floor tied to the victim score could reintroduce bias) — needs a simulation, not reasoning.
- Whether the jsaa id split is a live dual-write or a historical rename residue — `SELECT min/max(created_at)` per id would settle it in one query; not run.
- The `dotnet test` zero-run from F7 — what the correct invocation is and whether the promotion suites are actually green.
