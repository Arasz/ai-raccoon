# Research: validation of the threshold τ=0.95 committee eval

**Date:** 2026-09-10
**Question:** Were the threshold τ=0.95 committee eval's approach, implementation and results correct — verified independently against the raw artifacts, a frozen re-run, and the literature?

```chart:bars
title: committee rounds archived per started slot
C009__off: 2
C009__threshold: 5
C011__off: 1
C011__threshold: 1
C024__off: 5
C024__threshold: 2
C050__off: 2
C050__threshold: 1
C053__off: 3
C053__threshold: 4
```

```chart:bars
title: dropped chunks by class (19 total)
machine-generated: 6
instruction files: 5
translated mirrors: 3
live docs: 2
other / no sourceFile: 2
archive copy: 1
```

```chart:matrix
title: validation verdicts by area
, verdict, basis
mechanical numbers, pass, frozen re-run 15/15 both arms
port default-off parity, pass, 3/3 port gates on real bank
artifact traceability, pass, trace test + 173-file hash manifest
RBO metric, note, p-shifted variant, rank-preserving
committee accounting, stale, 6 slots/36 forms vs 9/77 archived
committee method, caution, identical controls + unaudited nudge
corpus query quality, caution, 25/100 markup debris
bank-copy hermeticity, caution, watch ingest during later re-run
```

## Findings

### F1 — Every headline mechanical number reproduces independently from the raw arm artifacts [MEASURED]

Reading `arm-off.json` / `arm-threshold.json` directly — not through the repo's runner — gives
mean top-8 set overlap **0.97625** (report: 0.976), mean RBO **0.5034027** (report: 0.5034),
**15** queries changed, **19** drops ↔ **19** backfills, **85/100** ordered lists identical, and
anchors **81/99 → 80/99**. All match `metrics.json` and the report to the printed precision.

**Evidence:** `python3` recomputation over `docs/work/threshold-committee-eval/arm-off.json`,
`arm-threshold.json`, `metrics.json` (hash-pinned by `artifact-manifest.json`), on Apple M4,
macOS, 2026-09-10; re-derivation written for this record, output checked per pair.

### F2 — A frozen-copy re-run of the 15 changed queries reproduces both arms' served lists byte-for-byte [MEASURED]

Built the branch-local port (`task/air-threshold-filter-corpus-committee-eval` @ `0ccb6c68`),
ran the **original stdio runner** from that branch over a fresh `.backup` of the archived bank
copy with the copy's watch registrations cleared (see F6). All **15/15** changed queries are
byte-identical in the memory hash lists of **both** arms, with the same drops, backfills, RBO
values and anchor outcomes; the two-arm run took 37 s. This is the strongest available
reproduction of the mechanical half: same code, same snapshot content, same lists.

**Evidence:** `uv run python scripts/retrieval_tuning/run_threshold_eval.py --dll
src/AiRaccoon/bin/Debug/net10.0/AiRaccoon.dll --corpus /tmp/threshold-eval-rerun2/corpus-15.json
--copy /tmp/threshold-eval-rerun2/memory-copy.db --output-dir /tmp/threshold-eval-rerun2/out` in
worktree `.ai-badger/worktrees/air-threshold-eval-rerun` (branch `verify/air-threshold-eval-rerun`
from the task branch), bank `.backup` of `/tmp/threshold-eval-p6/memory-copy.db` with
`watch.enabled.*` set false and `watches`/`watch_files` cleared; comparison script printed
`ALL 15 changed queries identical (both arms): True`.

### F3 — The port's gated-off parity and marker gates pass on the real fixture bank [MEASURED]

The three `test_poc_port_gates.py` gates run green with a real copy and the built port:
default-off replay is byte-identical to the pristine-base goldens (3 queries, all deterministic
fields), threshold mode emits ≥1 `[mmr-poc]` line, and `MMR_DISABLE=1` emits none. The ported
build therefore does not perturb the gated-off path, and the eval's `off` arm is the base
behaviour on the executable evidence, not only by code reading.

**Evidence:** `AI_RACCOON_EVAL_COPY=/tmp/continue-testing-algorithm/datasets/memory-copy-p1-parity.db
AI_RACCOON_EVAL_DLL=$PWD/src/AiRaccoon/bin/Debug/net10.0/AiRaccoon.dll uv run pytest
scripts/tests/test_poc_port_gates.py -v` in `.ai-badger/worktrees/air-threshold-eval-rerun` →
`3 passed in 75.52s`, 2026-09-10 (the gates are env-gated and skipped without those variables).

### F4 — The RBO number is a documented non-standard variant: exactly 0.9× canonical truncated RBO, rank-preserving on these lists [MEASURED]

The paper defines `RBO(S,T,p) = (1−p)·Σ_{d=1..∞} p^{d−1}·A_d` (eq. 7); the runner deliberately
implements `(1−p)·Σ p^d·A_d` and documents the deviation. Recomputation over all 100 pairs shows
the archived value is exactly `0.9 ×` the canonical truncated sum (for equal-length lists) and
that the ordering of the 100 pairs is identical either way. So "0.5034" supports the arm
comparison, but it is **not** a standard RBO figure: the identical-list ceiling is 0.5126 here,
0.5695 canonical-truncated, and 1.0 under extrapolated `rbo_ext`. Quoting 0.5034 against
published RBO values would be wrong.

**Evidence:** `python3` recomputation over the archived arms (repo value / canonical ratio
`0.8999999999999999` for every pair; rank order identical); Webber, Moffat & Zobel, *A Similarity
Measure for Indefinite Rankings*, ACM TOIS 2010, eq. (7), PDF
`http://www.williamwebber.com/research/papers/wmz10_tois.pdf` (line 740 of `pdftotext` output);
runner docstring `scripts/retrieval_tuning/run_threshold_eval.py` `rbo()`.

### F5 — The filter never reorders and every served list stays at 8; the "19 ↔ 19" equality is set algebra, not extra evidence [MEASURED]

Across all 200 served lists: every list has exactly 8 hashes, no list repeats a hash, and the
common hashes of the two arms appear in the same relative order in all 100 queries. The "it never
reorders, only substitutes" claim holds. But `|off\threshold| == |threshold\off|` follows from
both lists being length 8 — drop count equals backfill count for every query by construction — so
"strictly 1:1" is a restatement of equal list sizes, not a property the filter demonstrated.

**Evidence:** `python3` recomputation over `arm-off.json` / `arm-threshold.json`: 0 size
violations, 0 duplicate hashes, 0 reorder violations, 0 queries with `len(drop) != len(backfill)`.

### F6 — The bank copy is not hermetic: a later re-run ingested 3,085 filesystem entries through the watch subsystem [MEASURED]

A first re-run attempt copied the archived 53,792-entry snapshot as-is; within two minutes the
copy had grown to **56,474 entries** and a **1.5 GB WAL**, because the snapshot carries 12
enabled watches and 35 hours of watched-file changes were pending. The original eval is
unaffected — its copy was taken four minutes before the arms ran, the post-run `--verify-only`
note records 53,792 entries with 5/5 spot checks, and the archived copy still carries exactly the
corpus header's per-project counts (ai-badger 7528, jsaa 18298, …). But any future mechanical
re-run over a snapshot older than the last filesystem change will silently measure a different
bank unless the watches are cleared or disabled first. The planned "finish the committee" step is
grader-only (it reads `metrics.json`), so it is unaffected.

**Evidence:** `make_memory_copy.py --live /tmp/threshold-eval-p6/memory-copy.db --target …`;
`sqlite3 … "SELECT count(*) FROM entries"` → 53792 at 11:04, 56474 at 11:06; WAL 1,513,547,952
bytes; `SELECT count(*) FROM watches` → 12; new entries sampled ai-badger skill files. Original
claims: `run-metadata.json` `copyNote`, `docs/work/2026-09-09-threshold-committee-eval.md` §6.5.

### F7 — Committee artifacts are materially larger than both reports state: 10 slots started, 9 unanimous, 77 forms [MEASURED]

The task report's "**6/32 slots graded (36 forms)**" is exactly the state after the first three
sampled queries (C050, C011, C009 = 12 rounds × 3 forms). The committed artifact set continued to
the first five sampled queries: **10 slots started, 26 rounds, 77 grader submissions**, of which
**9 slots reached a 3/3-identical round** and one (`C024__threshold`) was aborted mid-round-2 with
2 of 3 grader forms written. The eval report's anchored committee/AB blocks stay `BLOCKED` either
way (no `committee-grades.json`), which is conservative and correct; the prose understates what
the archive actually holds.

**Evidence:** enumeration of `docs/work/threshold-committee-eval/forms/**` (77 `grader-*.json`,
77 payloads, 26 round directories, 10 slot directories) plus per-slot per-round 3/3 comparison;
`forms-manifest.json` and `artifact-manifest.json`; the rounds chart above.

### F8 — The unanimity gate failed to converge on 3 of 5 processed queries; nudges reached unanimity by movement, not by new evidence [MEASURED]

C009, C024 and C053 exhausted their first three-round version and were replaced by C033, C097 and
C034 respectively — so on the five changed queries the fleet reached, **three originals never
reached 3/3**. Round-1 pairwise chunk-grade agreement across the 10 slots averaged **0.883**
(only 3/10 perfect). The largest nudge flip: `C009__off` round-1 grader-1 graded every chunk `0`
while graders 2 and 3 graded three `A`s each; after "argue against your own grades" grader-1
graded three `A`s and the slot accepted. The acceptance rule selects rounds in which disagreement
has been eliminated — it does not show the surviving vector is the correct one.

**Evidence:** `python3` computation over the archived forms (pairwise agreement table; A-counts
per round); rounds chart; `committee_grade.py` `grade_slot()`/`_submit()` (nudge shows each
grader its own prior form).

### F9 — The nudge + unanimity step is an unvalidated self-correction, and the committee brief is not blind to the arm [READ]

The method literature: LLM judges carry position, verbosity and self-enhancement biases (MT-Bench
§1 — strong judges still reach only ~80% human agreement); position alone can flip quality
rankings (Fair Evaluators abstract); intrinsic self-correction without external feedback can
*degrade* performance (Huang et al. abstract); models demonstrably shift answers toward user
pressure (sycophancy). The committee's "argue against your own grades" round is exactly an
intrinsic self-correction with no external signal, and there is no gold subset against which the
conformed vectors were checked. Separately, the committee brief names the arm
(`Chunks retrieved for arm "threshold"`), so those grades are arm-aware; only the never-run P5
pass is blind.

**Evidence:** `committee_grade.py` `build_brief()` (arm name in the brief; `NUDGE_PHRASE`);
arXiv:2306.05685 (MT-Bench), arXiv:2305.17926 (Fair Evaluators), arXiv:2310.01798 (self-correction),
arXiv:2310.13548 (sycophancy).

### F10 — Half the committee sample cannot distinguish the arms: all 6 controls are byte-identical lists, and 0/100 queries reordered only [MEASURED]

The sample's 6 controls all have set overlap 1.0 *and* RBO 0.5126 — byte-identical ordered lists.
Across the whole run there were **zero** reorder-only queries, the case the plan's
"reorder-only queries are controls" rule was written for. So committee control slots grade the
same 8 chunks twice (label differing only by the arm name in the brief), and the blind A/B
payloads for those 6 queries force a choice between *identical* content — measuring position bias
rather than the filter. The seeded mapping gave 4 off-first and 2 threshold-first among controls,
so a pure first-pick bias swings those 6 comp-scores by up to ±0.33, i.e. ±0.125 on the 16-query
mean. The unchanged-query stratum adds no signal; it adds a measurable bias channel.

**Evidence:** `python3` comparison of `sample.json`, `metrics.json`, `arm-*.json`: 6/6 controls
byte-identical, 0 reorder-only queries in 100; `ab_compare.assign_positions(seed=20260909)` on the
bridged sample → control mapping `{'off': 4, 'threshold': 2}`; fake-runner P5 pass over the real
bridged artifacts (48 calls).

### F11 — A quarter of the corpus queries are markup debris, including sampled and replacement queries [MEASURED]

**25/100** query texts carry a structural-debris signature (`{}[]|<>\\`, `://`, backticks,
`"line"`, `@scope/`, or >160 chars): e.g. C053 `How is ```mermaid
plugin_dsh_base_tool_result_pruner["tool-result-pruner<br/>@deepseek-ai/dsh-compaction-to
handled?`, C050 an npm-table fragment, C097 a `"loc":{"start":{"line":27…` JSON fragment. **5 of
the 16 sampled** queries are affected, and the accepted replacement for C024 was C097 — the
largest-churn query in the run and itself debris. The generator's `_derive_topic()` takes the
first clause of the chunk value, so code/JSON/markdown targets yield non-questions. H2 was
"flagged" in the plan; this sizes it. It limits the committee's external validity on the changed
stratum; it does not break the mechanical n=100 comparison, which is query-text-agnostic.

**Evidence:** `python3` regex census over `corpus-regenerated.json` (25/100 flagged; 5/16 sampled)
and the drops/rounds tables; `build_project_corpus.py` `_derive_topic()` / `FRAMES`.

### F12 — The eval report contradicts itself on H1, and the canonical budget block under-counts grader activity [MEASURED]

Within one committed file: line 12 ratifies the grader-3 substitute and the 3/3 re-smoke, while
line 348 says "**No substitution was made.**" `run-metadata.json` still records `h1.outcome =
FAILED`, `committee: null`, `ab: null`. The anchored budget block derives `H1 smoke grader calls =
3` from a manifest in which grader-3 has both an `invalid` entry and a valid `form` for the same
round — at least 4 submissions, while `h1Calls` counts unique payload paths. The committee's 77
submissions are reported as `n/a` because `committee-grades.json` was never written. The task
report's "≈50 grader calls (openrouter)" is well supported: graders 2+3 produced **51** of the 77
forms.

**Evidence:** `grep -n` on `docs/work/2026-09-09-threshold-committee-eval.md` lines 12–16 vs 348;
`h1-smoke/forms-manifest.json` grader-3 kinds `['form','invalid','payload']`;
`run-metadata.json`; per-grader form counts `{1: 26, 2: 26, 3: 25}`.

### F13 — Traceability is strong, with one gap in the committee's own manifest [MEASURED]

`pytest -k trace` passes: every report anchor block is byte-equal to a recomputation, every
manifest sha256 matches disk, and the 173-entry `artifact-manifest.json` covers **every** archived
file. The committee-local `forms-manifest.json` has 144 entries and misses exactly the **10 files
of the aborted `C024__threshold` slot** (its per-slot writer runs only at slot completion, which
the abort prevented). The outer manifest is what preserves traceability; a future consumer
trusting only `forms-manifest.json` would silently lose the aborted slot's evidence.

**Evidence:** `uv run pytest scripts/tests/test_threshold_eval_integration.py -k trace -v` → 1
passed; `python3` coverage comparison (`forms` on disk 154 files vs 144 manifest entries; missing
list = `forms/C024__threshold/{1,2}/*`).

### F14 — P5 is runnable but has never run; only one of its four bridge inputs was kept [MEASURED]

P5's loader refuses the raw P4 `sample.json` (graders carry `index`/`label`, not `name`) — the P6
bridge exists to fix exactly that. Re-running the bridge on the archived artifacts regenerates all
four files (`metrics-committee.json`, `sample-ab.json`, `arm-off.ab.json`,
`arm-threshold.ab.json`), and a fake-runner P5 pass over them completes 16 queries × 3 graders =
48 calls with the seeded mapping. But the committed set contains only `metrics-committee.json`;
`ab-results.json` and the ab bridge inputs were never committed, so the blind A/B has been
exercised only against fixtures, never on the real artifacts.

**Evidence:** `uv run python scripts/tests/test_threshold_eval_integration.py bridge --artifact-dir
/tmp/threshold-eval-bridge` → 4 files; `ab_compare.load_sample(sample.json)` → `ValueError … name:
None`; fake-runner `run_ab(...)` → 16 queries, 48 builder calls, results written.

### F15 — The implementation matches the algorithms and makes conservative choices [READ]

The threshold filter is standard greedy near-duplicate suppression over the ranked pool (keep a
row unless cosine > τ to any kept row), cosine is the textbook formula with zero-length/absent
vectors scoring 0.0 (never drop on ignorance), and the second `Merge` call site is idempotent
because its input is a subset of an already-diversified list — observed as one marker per search
in the run (the `AdjustMergedResults` re-merge early-returns when the no-regression flag is off).
The MMR term matches Carbonell & Goldstein's formulation with ρ = (1−λ)/λ; MMR was discarded
anyway. τ = 0.95 is a conservative near-duplicate bar: semantic-dedup practice tunes cosine
thresholds per dataset, with aggressive removal around cosine 0.95–0.97 (SemDeDup's 50% point is
ε = 0.03, i.e. cosine 0.97), and the bank's own served max-pair cosine of 0.885 predicted a
near-no-op — which is what the mechanical run measured.

**Evidence:** `docs/work/poc-threshold-port.patch` (`ThresholdFilter`, `Cosine`, `PocDiversifierFor`,
`FetchPocVectorsAsync`); `SqliteMemoryStore.cs` `AdjustMergedResults` early return
(`legs.Count(<2) || !FusionNoRegressionEnabled`); Carbonell & Goldstein SIGIR 1998 MMR paper
(`https://www.cs.cmu.edu/~jgc/publication/The_Use_MMR_Diversity_Based_LTMIR_1998.pdf`);
Abbasi et al. SemDeDup, arXiv:2303.09540 §4.2/§6.5; `docs/work/2026-09-08-mmr-no-go-why.md` F3.

### F16 — The C009 anchor loss and the drop classes are as described, with three qualifications [MEASURED]

The C009 dropped chunk **is** its `expectedHash` (`ai-badger/CLAUDE.md`) and the backfill is an
unrelated skill doc — the 81→80 anchor loss is real and tracing it to the data confirms it. The
19 drops split into 6 machine-generated, 5 instruction-file, 3 translated-mirror, 1 archive,
2 live-doc and 2 other; but three changed queries swap chunks of the **same file**
(C023 HERMES.md→HERMES.md, C056 config-catalog.md→config-catalog.md, C097 coverage-final.json→its
own other chunks), so not every change is whole-file junk removal — some are intra-file chunk
swaps that leave the file represented.

**Evidence:** `python3` extraction from `metrics.json` (drop/backfill `sourceFile` census,
expectedHash membership per arm); the drop-class chart above.

### F17 — The nudge's unanimity is more plausibly conformity than correction [INFERRED]

Reasoning from F8 (movement under a self-critique prompt, no external signal, outliers converging
on the majority in the observed flips) and F9 (sycophancy and intrinsic-self-correction evidence):
the accepted vectors in nudge rounds should be treated as "the graders agreed after being asked to
reconsider", not as "the graders converged on the truth". This does not invalidate the state
machine's behaviour — it invalidates using its unanimity as a quality ground truth. A gold subset
of human-graded slots, or a majority vote with reported agreement statistics, would distinguish
the two.

### F18 — The quality verdict is genuinely absent; the 9 unanimous vectors give two same-query comparisons, 0 and −1 answer chunks [MEASURED]

There is no `committee-grades.json` and no `ab-results.json`, so no quality verdict exists in the
archive — the report's BLOCKED status is honest. Reconstructing the accepted vectors from the
forms (the report never did): of the 9 accepted slots, only C011 and C050 accepted on their
**original** query under both arms, giving answer-chunk counts C011 off=2 / threshold=1 and C050
off=1 / threshold=1. That is n=2, changed-stratum only, and consistent with the report's "pending,
not negative" — it is not evidence for or against shipping the filter either way.

**Evidence:** `python3` reconstruction of accepted 3/3 vectors per slot from
`forms/**/grader-*.json`; the accepted-vector table (versions, rounds, A-counts) printed above.

### F19 — Cost and substitute provenance are not verifiable from the repository [UNVERIFIED]

The "≈3.32M subagent tokens (~$1.98 lane-side)" figure has no token ledger or session record in
the repo, and the valid H1 grader-3 form records no model id, so the claim that
`openrouter/xiaomi/mimo-v2.5-pro` produced it rests on the narrative. Settleable with session
token records and by recording the provider/model on each archived form.

## Still open

- **Would the sample have been informative with a better control rule?** The plan's control
  stratum assumed reorder-only queries exist; none did. Re-stratifying controls on
  `RBO < ceiling` (or excluding identical pairs from the A/B comp_score) is an untested repair.
- **Does the nudge improve accuracy or only agreement?** A small human-graded gold subset (the
  report's own "human-grade a random 20–30" idea) would separate the two; no gold labels exist now.
- **What did the original run's bank actually contain during the arms?** The post-run
  `--verify-only` note and the unchanged archived copy support content stability, but the
  in-flight check was not re-runnable; the watch-ingest mechanism (F6) means "same copy, same
  content" is only true for a copy younger than the last watched-file change.
- **Whether the report's prose gets corrected or the artifacts re-synced.** This record does not
  edit the eval reports or `run-metadata.json`; the stale "6 slots / 36 forms", the H1
  contradiction and the `committee: null` block remain in the committed documents.
- **Whether C097's four coverage-final.json drops would survive corpus hygiene.** Excluding
  generated artifacts from candidate pools (the report's own §4.3) would remove the run's largest
  change; no follow-up measured what remains.
