# Research: Auto-grading memory_search usefulness

**Date:** 2026-08-11
**Question:** Which option for raising usefulness-grading coverage — prompting less
(per-day aggregation), grading in-session (dogfood rule), or auto-grading with retrieval-harness
metrics — is feasible for `memory_search`, and which published algorithms support auto-grading?

```chart:line
title: per-day capture and grading of hermes memory_search (08-06..08-11)
captured by log %: 40,24,0,22,41,71
graded % of captured: 19,14,0,19,3,11
```

```chart:bars
title: usefulness grades of the 21 graded searches
1: 0
2: 1
3: 1
4: 10
5: 9
```

## Findings

### F1 — The 12 % figure understates the gap: 20 of 438 searches are graded (4.6 %) [MEASURED]

"12 %" is graded/logged (21/172). Against the real search count the rate is 4.6 %: the ops log
records 438 `memory_search` calls on the hermes provider channel in 08-06..08-11, of which only
20 carry a usefulness grade. The "20-sample opinion" the diagnostic warned about is thus a
20-grade opinion over a ≥438-search population — and bridge (Claude/HTTP) searches are not in
either log, so the true denominator is larger still.

**Evidence:** `python3` enumeration over `~/.ai-badger/memory-grade/memory-quality.jsonl` (172
lines, 21 graded, ts 08-05..08-11) joined with `~/.ai-raccoon/memory-operations.jsonl` (438
`memory_search` ops, ts 08-06..08-11), run on this machine 2026-08-11 ~16:15Z. Full-enumeration
counts, not samples.

### F2 — The grade log itself captures only ~38 % of hermes searches — an instrumentation gap on top of the grading gap [MEASURED]

168 hermes-host grade lines fall inside the ops-log window vs 438 ops searches. Per-day capture
varies wildly: 40 / 24 / **0** / 22 / 41 / 71 % for 08-06..08-11 — 08-08 recorded zero of 26
searches. Fixing the grading habit alone cannot raise coverage while 62 % of searches never
reach the log. This is the known hook-coverage failure mode (memory-quality-logging skill:
"empty log ≠ no usage") at line granularity.

**Evidence:** per-day join of the two files above (same run as F1). Full enumeration.

### F3 — The 21 grades are selection-biased: avg 4.29, {5:9, 4:10, 3:1, 2:1}, 20/21 from hermes, 0/3 from the claude host [MEASURED]

Voluntary grading skews toward satisfaction: 19 of 21 grades are 4-5, and the two sub-4 grades
both came from structured diagnostic sessions, not routine use. The only claude-host lines
(3) were never graded. A 4.29 "perceived quality" is an upper-bound estimate, not a point
estimate — the auto-grader this record explores must not be validated against it.

**Evidence:** grade distribution, host breakdown, and notes read from the JSONL (same run as F1).

### F4 — 97 of 172 logged lines (56 %) carry no projectId; all carry sessionId [MEASURED]

The log was built to correlate quality per project and workspace, but 56 % of lines cannot be
correlated per project. The null-projectId lines span every day (08-06..08-11), so this is a
hook contract gap (projectId not always passed), not a legacy-format artifact. sessionId is
present on all 97, so backfill is possible in principle.

**Evidence:** key-level count over the JSONL; per-date and per-host distribution (same run).

### F5 — The pending-ask stash is not the bottleneck: 2 unanswered asks vs 151 ungraded lines [MEASURED]

`pending.json` holds exactly two grade asks (08-10T14:41Z, 08-11T14:10Z). The remaining
ungraded lines were either never asked or explicitly skipped — the log cannot distinguish the
two. A "grade in-session" rule without an enforcement loop already failed for a week
(08-10: 1/37 graded).

**Evidence:** `pending.json` read in full; per-day graded counts from the JSONL.

### F6 — The repo owns an offline retrieval harness (nDCG@5, MRR, Recall@k, Kendall τ) that cannot grade organic queries [READ]

`RetrievalMetrics.cs` implements nDCG@k, MRR, Recall@k, and Kendall τ as pure functions over
ranked id lists + relevance sets; `GoldenFileComparisonTests` gates retrieval changes against
pinned golden corpora. The harness measures the retrieval function on fixed fixtures — it has
zero coverage of live organic queries, because organic queries have no relevance sets. It is
the right **offline regression gate**, not an online grader.

**Evidence:** `tests/AiRaccoon.Tests/Unit/Retrieval/RetrievalMetrics.cs:10-117` (NdcgAtK, Mrr,
RecallAtK, KendallTau); `tests/AiRaccoon.Tests/Unit/Retrieval/GoldenFileComparisonTests.cs`;
`Directory.Packages.props:21,34` ("re-run the retrieval harness on upgrade" pins).

### F7 — G-Eval: rubric-based LLM-as-judge with chain-of-thought; GPT-4 reaches Spearman 0.514 with humans, with a bias caveat [READ]

G-Eval scores outputs by having an LLM follow a CoT + form-filling rubric. On summarization it
outperformed all prior methods (Spearman 0.514 vs humans), and the authors flag a bias toward
LLM-generated text. Direct template for a "relevance of these chunks to this query" judge.

**Evidence:** arXiv:2303.16634 abstract (fetched 2026-08-11).

### F8 — Prometheus: an open 13B judge fine-tuned on rubrics reaches Pearson 0.897 with humans, on par with GPT-4's 0.882 [READ]

Prometheus takes a custom score rubric as input and matches GPT-4 as an evaluator (0.897 vs
0.882 Pearson on 45 rubrics; ChatGPT scores 0.392). Relevant if auto-grading must run on a
local/cheap model rather than a proprietary one — the repo's `local:bundled` embedding engine
suggests an offline preference.

**Evidence:** arXiv:2310.08491 abstract (fetched 2026-08-11).

### F9 — ARES: automated RAG evaluation calibrated on a few hundred human labels via prediction-powered inference [READ]

ARES fine-tunes lightweight LLM judges on synthetic data and corrects their errors with
prediction-powered inference (PPI) using "a few hundred human annotations", yielding
statistically valid estimates of context relevance / answer faithfulness / answer relevance
that survive domain shift. This is the pattern the 21-grade sample points at: human grades
become **calibration**, not enumeration — a small stratified sample corrects the judge's
estimates with confidence intervals instead of every search being graded by hand.

**Evidence:** arXiv:2311.09476 abstract (fetched 2026-08-11); NAACL 2024 long paper.

### F10 — RAGAS Context Precision: "evaluates whether retrieved contexts are useful for answering a question by comparing each context against a reference answer"; a no-reference variant exists [READ]

The metric's shape is exactly a usefulness grade over top-k retrieved chunks. The reference
variant needs a reference answer; the no-reference variant compares contexts against the
generated response. For `memory_search` the missing piece is what plays the role of
"reference answer" for organic queries — the agent's subsequent action is the candidate.

**Evidence:** https://docs.ragas.io/en/stable/concepts/metrics/available_metrics/context_precision/
(fetched 2026-08-11; page text: "The ContextPrecision metric evaluates whether retrieved
contexts are useful for answering a question by comparing each context against a reference
answer").

### F11 — Implicit feedback: "relative preferences derived from clicks are reasonably accurate on average", with position-bias caveats [READ]

Joachims et al. validated clickthrough/reformulations as implicit relevance judgments against
manual ratings using eyetracking; preferences are accurate on average but exhibit position
bias. The `memory_search` analog is follow-through — did the agent subsequently read the
result's `sourceFile`, or write memory citing it? This is the only option that produces a
grade with **zero added prompt burden**, at the cost of measuring "used" rather than
"useful".

**Evidence:** Joachims, Granka, Pan, Hembrooke, Gay, "Evaluating the accuracy of implicit
feedback from clicks and query reformulations in Web search", ACM TOIS 2007,
https://dl.acm.org/doi/10.1145/1229179.1229181 (abstract read; PDF text not read).

### F12 — The three options are complementary halves of one pipeline, not alternatives [INFERRED]

Reasoning from F5-F11: the nDCG harness (F6) needs per-query relevance sets that only exist
for organic queries once labels do; LLM judges (F7-F10) produce labels cheaply but need
calibration; the 21 human grades (F3) are the calibration source. So the coherent shape is:
(1) prompt less — aggregate the ask to a small stratified per-day sample (per project/host,
and per grade-band) instead of per search; (2) auto-grade the remaining searches with an
LLM judge using the ARES/PPI calibration pattern against that sample (F9); (3) keep the
in-repo nDCG harness as the offline regression gate (F6). The "grade in-session dogfood
rule" survives only as the stratified-sample enforcer — as a universal rule it already
failed (F5). Implicit follow-through (F11) is the cheapest supplementary signal where the
session store can supply it; its data availability is unverified (Still open).

## Still open

- **Judge-human agreement on this domain is unmeasured.** Whether an LLM judge's 1-5
  usefulness grades correlate with human grades on memory search over ~14k sqlite rows with
  hermes session-transcript noise needs a calibration run: ~30-50 stratified organic queries
  double-graded, agreement via Kendall τ / Spearman — both already implemented in
  `RetrievalMetrics.cs`.
- **What plays the "reference answer" for organic memory searches** (F10): the document the
  agent read or wrote right after the search is the candidate, but no data-availability probe
  has been run against the hermes session store.
- **Follow-through signals (F11) are unverified**: whether post-search `read_file` of a
  result's `sourceFile` is recoverable from hermes `state.db` is unknown.
- **Why hook capture varies 0-71 % per day** (F2): 08-08 captured nothing; the per-session /
  per-host plugin enablement mechanics were not diagnosed here.
- **The 97 null-projectId lines** (F4): fix the hook contract or backfill via sessionId —
  undecided, and it gates any per-project quality reporting.
