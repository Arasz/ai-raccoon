# Auto-grading memory_search usefulness — coverage audit + algorithms catalog

Context: 2026-08-11 research (record: `docs/work/2026-08-11-auto-grading.md`). The 08-11
diagnostic's recommendation #4 ("raise grading coverage: the 12 % rate makes 'perceived
quality' a 20-sample opinion") prompted: check the metric, then find auto-grading algorithms.

## Coverage audit — three denominators, quote the right one

Measured 2026-08-11 (~16:15Z, full enumeration over both logs):

| number | formula | value |
|---|---|---|
| graded / logged | usefulness != null over memory-quality.jsonl lines | 21/172 = 12.2 % |
| logged / searched (hook capture) | hermes-host grade lines / `memory_search` ops in memory-operations.jsonl (08-06..08-11 window) | 168/438 = 38 % |
| graded / searched (true coverage) | graded lines / ops searches | 20/438 = 4.6 % |

- Capture varies per day: 40 / 24 / 0 / 22 / 41 / 71 % (08-06..08-11); 08-08 captured zero of
  26 searches. A "grading fatigue" reading of a coverage number is wrong when the log itself
  missed most searches — fix capture first (hook/plugin enablement per host, per session),
  then grading.
- 56 % of lines (97/172) carry no projectId (all have sessionId) — per-project correlation
  only possible on 44 % of lines. Check the null-projectId share before claiming per-project
  quality.
- pending.json (the grade-ask stash) held 2 asks vs 151 ungraded — the stash is NOT the
  bottleneck; ungraded lines were mostly never asked or explicitly skipped (the log cannot
  distinguish).
- The 21 grades are selection-biased: avg 4.29, {5:9, 4:10, 3:1, 2:1}, 20/21 hermes-host,
  0/3 claude-host. Voluntary grading skews to satisfaction; a judge validated against this
  set inherits the bias. Treat "perceived quality" as an upper bound.

Re-run with: `scripts/audit_coverage.py` (memory-quality-logging skill, no args).

## Algorithms catalog (all READ from primary sources, 2026-08-11)

### LLM-as-judge with rubric — G-Eval
arXiv:2303.16634. CoT + form-filling rubric; GPT-4 backbone reaches Spearman 0.514 vs humans
on summarization, outperforming all prior methods; authors flag a bias toward LLM-generated
text. Use: per-search relevance judge ("are these chunks useful for this query", 1-5).

### Open judge — Prometheus
arXiv:2310.08491. 13B evaluator fine-tuned on 1K score rubrics; Pearson 0.897 vs humans on
45 custom rubrics, on par with GPT-4 (0.882), ChatGPT 0.392. Use: local/cheap auto-grader —
fits the repo's `local:bundled` offline preference.

### Calibration pattern — ARES (the key one)
arXiv:2311.09476 (NAACL 2024). Synthetic-data-trained lightweight judges + prediction-powered
inference (PPI) corrected on "a few hundred human annotations" → statistically valid estimates
of context relevance / answer faithfulness / answer relevance, robust across domain shift.
Use: human grades become CALIBRATION, not enumeration. A small stratified human sample
corrects the judge's estimates with confidence intervals instead of every search being
graded by hand.

### RAGAS Context Precision
docs.ragas.io/en/stable/concepts/metrics/available_metrics/context_precision/. "Evaluates
whether retrieved contexts are useful for answering a question by comparing each context
against a reference answer"; a no-reference variant compares against the generated response.
Shape = exactly a usefulness grade over top-k chunks. Open question: what is the "reference
answer" for organic memory searches (candidate: the doc the agent read/wrote right after the
search).

### Implicit feedback (zero-prompt-burden option)
Joachims et al., ACM TOIS 2007, doi:10.1145/1229179.1229181. "Relative preferences derived
from clicks are reasonably accurate on average", with position-bias caveats (eyetracking).
memory_search analog = follow-through: did the agent subsequently read the result's
sourceFile / write memory citing it? Measures "used", not "useful". Whether follow-through
is recoverable from hermes state.db is UNVERIFIED (open probe).

### In-repo offline harness (the gate, not the grader)
RetrievalMetrics.cs:10-117 — nDCG@k, MRR, Recall@k, Kendall τ over ranked id lists +
relevance sets; GoldenFileComparisonTests pins golden corpora. Gates retrieval changes on
fixed fixtures; organic queries have no relevance sets, so it cannot grade live searches —
but its agreement functions (Kendall τ / Spearman-style) are the right tool for a
judge-vs-human calibration study.

## Design synthesis (INFERRED, 2026-08-11)

The three options from the diagnostic are complementary halves of one pipeline:
1. Prompt less — aggregate the grade ask to a small stratified per-day sample (per
   project/host/grade-band) instead of every search. Universal "grade in-session" rules fail
   (08-10: 1/37 graded).
2. Auto-grade the rest with an LLM judge, ARES/PPI-style, calibrated on that sample.
3. Keep the in-repo nDCG harness as the offline regression gate.

## Still open

- Judge-human agreement on THIS domain (14k sqlite rows, hermes session-transcript noise)
  is unmeasured — needs ~30-50 stratified organic queries double-graded; agreement via
  Kendall τ / Spearman (both already in RetrievalMetrics.cs).
- Reference-answer definition for organic queries (which RAGAS variant).
- Follow-through signal availability in hermes state.db (implicit-feedback feasibility).
- Why hook capture varies 0-71 % per day (08-08 = 0 %).
- 97 null-projectId lines: hook contract fix vs sessionId backfill.
