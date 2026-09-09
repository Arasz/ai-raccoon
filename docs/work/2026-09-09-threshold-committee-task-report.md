# Task report — threshold (τ=0.95) evaluation

**Task:** `air-threshold-filter-corpus-committee-eval` · **Branch:** `task/air-threshold-filter-corpus-committee-eval` · **Draft PR:** [#626](https://github.com/Arasz/ai-raccoon/pull/626) · **Date:** 2026-09-09

## 1. The question we tested

When `memory_search` answers a query with 8 chunks, similar-looking chunks can crowd out
different ones. We tested a simple guard: before showing results, drop any chunk that is
nearly identical (cosine > 0.95, "τ=0.95") to one already shown, and let the next-best
chunk take its slot. A fancier variant (MMR re-ranking) was already ruled out in earlier
sessions; this task tested only the simple drop rule, on a 100-query benchmark built from
a snapshot of the real bank (11 projects, every query run under its own project id).

## 2. Results

| What | Result |
|---|---|
| Queries benchmarked | 100 (11 projects; 484 candidates held out for future tuning) |
| Top-8 identical between off/threshold | **85 / 100** (mean set overlap 0.976; mean RBO 0.5034 vs 0.5126 ceiling) |
| Queries where the filter changed anything | **15** |
| Chunks dropped ↔ slots backfilled | **19 ↔ 19** (strictly 1:1 — it never reorders, only substitutes) |
| What got dropped | Instruction-file copies (CLAUDE.md ×4, HERMES.md), machine-generated files (coverage-final.json ×4), translated mirrors (README.zh.md ×2), archive copies |
| Automatic correctness check (is the query's own source file still served?) | **81/99 → 80/99** — one anchor lost (C009) |
| Quality grading by 3-model committee | **Started, not finished** — 3/3 graders validated, 6/32 slots graded (36 forms), then aborted by the owner (see §3.3) |
| 4th "observation" grader (mercury-2.5) | Aborted before running — zero cost |
| Engineering deliverables | Threshold code (branch-local, never merges), 5 Python tools + tests (**34 passed / 5 env-gated skips**), RAM fix for grader runs, draft PR #626 (docs/scripts only, zero `src/` diff — proven) |
| Cost | ≈3.32M subagent tokens (~$1.98 lane-side) + ≈50 grader calls (openrouter) |

## 3. Why we got these results (plain terms)

**3.1 The filter is a near no-op because the bank rarely serves real duplicates.** In 85 of
100 queries, the top-8 contained nothing similar enough to drop. That is *good news about
the bank*: the earlier fear (restatements crowding out answers) mostly does not happen at
the served top-8 with today's pre-fusion dedupe and affinity consolidation. The 15 times it
did fire, it found exactly the junk classes it was designed for — the three agent
instruction files are all generated from one source (dropping the 3rd copy is free),
`coverage-final.json` is a machine artifact with near-identical chunks, `README.zh.md`
mirrors the English README, and archive folders hold stale copies of live specs.

**3.2 Every substitution is a small gamble — and we saw both outcomes.** Dropping a chunk
always promotes the next-ranked chunk (19↔19). Mostly that trades a copy for fresh
information. But once (C009, *"Where is Managed by ai-badger documented?"*) the dropped
CLAUDE.md chunk **was the answer**, and the promoted chunk (bitwarden integration) was not —
that is the 81→80 anchor loss. This is the same lesson the 5-query PoC taught with
HERMES.md: instruction-file copies are near-duplicates of each other, so to the filter they
look redundant — yet sometimes the copy is precisely what was asked for. The filter cannot
tell "redundant copy" from "second copy that some query wants".

**3.3 The quality verdict is pending, not negative.** Mechanical checks can say the filter
is *quiet*; only grading can say whether the 15 changed result lists got *better or worse*.
The committee (3 models, form-gated, 3/3 unanimity, capped rounds) was built for that and
was working — H1 validated all three graders (including the owner's substitute
`openrouter/xiaomi/mimo-v2.5-pro` after the native-xiaomi key was missing), 6 slots were
graded with the state machine behaving exactly as designed (a mid-run replacement slot
accepted at chain round 5/9). It was aborted at ~10/32 slots because of the RAM problem
below — an *incomplete measurement*, not a failed one.

**3.4 The RAM problem and its fix.** Every headless grader session loaded the
memory-enrichment extension, which spawned an ai-raccoon embedding server (~8GB) per
session — several running concurrently, repeatedly. Fixed for all future runs: grader
sessions now set `PI_BADGER_MEM_RAG=0` (no server spawn, ~200MB sessions). Both lanes were
aborted, orphaned processes killed, and the machine returned to baseline (1 instance, ~1GB).

## 4. What's next (in order)

1. **Finish the committee** (~26 slots + the 16-query blind A/B; ≈150–250 grader calls,
   now RAM-flat). One small choice first: add slot-resume (skip the 6–10 graded slots,
   saves ~80 calls) or re-grade them fresh (simpler, wastes nothing but calls).
2. **Read the verdict.** If the committee says the changed lists got better or neutral —
   consider shipping the filter behind a setting. If worse on instruction-file queries
   (the C009 pattern), either exclude generated instruction files from *drop candidates* or
   keep τ=0.95 off by default.
3. **Corpus hygiene (independent win):** exclude generated artifacts
   (`coverage-final.json` etc.) from candidate pools — they caused 4 of the 15 changes and
   are noise in any retrieval benchmark.
4. **Decide the scoping semantics finding:** projects with ~1 row served cross-project
   top-8s (observed, labelled in the artifacts). Probably intended, but worth a sentence in
   the docs.
5. **Then, and only then, the ship decision.** Nothing here changes production search yet:
   the threshold code stays branch-local by policy (PR #626 carries docs/scripts/tests
   only), and the default stays off.

## 5. Where everything lives

- Eval report (mechanical, traceable to sha256'd artifacts): `docs/work/2026-09-09-threshold-committee-eval.md`
- Plan + research record: `docs/work/2026-09-09-threshold-committee-eval-plan.md`, `docs/work/2026-09-08-threshold-committee-eval-research.md`
- Earlier sessions this builds on: `docs/work/2026-09-08-mmr-postfusion-eval.md` (leg-MMR killed, post-fusion neutral), `docs/work/2026-09-08-mmr-no-go-why.md`
- Artifacts: `docs/work/threshold-committee-eval/` (arm JSONs, metrics, manifests, partial committee forms)
- QA session: `docs/qa-sessions/post-fusion-mmr-threshold-followup.md`
- Draft PR: #626 (empty `src/` diff proven); threshold C# port archived as `docs/work/poc-threshold-port.patch`
