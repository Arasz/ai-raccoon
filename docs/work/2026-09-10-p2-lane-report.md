# P2 lane report — measurement rigor (air-full-hundred-query-parity-eval)

Date: 2026-09-10. Lane branch `task/air-full-hundred-query-parity-eval-lane-p2`, base
`8b45e5d4` (P1 close-out + round 2). Task-tracking plan not owned here; amendments proposed
at the end. Frozen state untouched: `docs/work/results-f1.json` (run C) +
`results-f1-run2.json` (run D), `/tmp/p1-full-store`, `/tmp/p1-live-copy.db` (read-only),
frozen contract (rrfK=60, weights 1/1, limit 8, floor 0.6, λ=0.1, threshold 0.1, Max,
Max3X100, structureAlpha=0.5), never port 7721, one heavy process at a time.

Commits (all on the lane branch):

| commit | AC | subject |
|---|---|---|
| `4074fc45` | AC3 | pure `anchor_verdict` + stale-anchors-always-recorded gate |
| `2c7d8463` | AC2 | window-level gap taxonomy + classified report table |
| `03bcac52` | AC1 | `--repeats` from a fresh quiesced-base copy + spread/stability report |
| `fa3e0603` | AC4 | memwatch module-run/default-cap/sampling-help gates + derived GB label |
| (docs commit) | — | repeats artifact + regenerated report + lane report + trace script |

## Status

| AC | status | gate |
|---|---|---|
| AC1 repeats report spread | DONE | `test_summarize_repeats_min_max`, degenerate N=1, unstable-fixture, main-level fresh-copy/stability/unstable/mode tests; live 3-repeat campaign reproduces the golden ordered-exact, `unstable = []` |
| AC2 gap taxonomy | DONE | `test_gap_columns_classification_table`, `test_gap_counts_sum_to_paired`, shared-scope oracle, frozen-golden oracle, window-not-top8 pin; unknown cap enforced in the eval gate and at report render |
| AC3 anchor verdict | DONE | `test_anchor_verdict_refuse_warn_clean`, stale-always-recorded (empty + non-empty), refusal writes nothing; stale count in the one-line summary; report repeats it |
| AC4 memory guard | DONE (verified, gaps filled) | `test_memwatch_runs_as_module_documents_cap_and_sampling`, `test_memwatch_default_cap_is_12gb`, live tree-kill, stdout passthrough; every gate broken on purpose once |

Test run (touched suites): `74 passed` —
`pytest scripts/tests/test_llamaindex_harness_evaluate.py test_llamaindex_harness_report.py
test_memwatch.py test_llamaindex_harness_cli.py test_make_quiesced_scratch.py`.
All harness-related suites: `168 passed` (evaluate, report, cli, fts, fusion, ingest,
retrieve, slice, memwatch, quiesced-scratch). Full `scripts/tests` once:
`573 passed, 11 skipped, 6 failed` — the 6 failures are PRE-EXISTING on base `8b45e5d4`
(verified in a throwaway worktree): `optuna` missing (2 report tests), `matplotlib`
undeclared (dependencies gate), and the eval-corpus tests' hardcoded
`/tmp/continue-testing-algorithm/datasets/memory-copy.db` copy (3 tests). Collection of
`test_retrieval_tuning_tune.py` also fails pre-existing on the missing `optuna`. No
regression from this lane; the failures were not needed to touch.

---

## AC1 — repeats report spread

**Design.** `--repeats N` with `--scratch-base <quiesced base.db>` + `--scratch-root <dir>`:
repeat *i* runs in `<root>/repeat-i`, whose `memory.db` is a byte copy of the base
(`fresh_scratch_copy` removes stale WAL/SHM first), with its own scratch server. The
read-only harness store is shared across repeats (one model load, bounded memory). Per
repeat the loop records the C11 row-stability snapshot pair, a peak-RSS sample
(`RssSampler`, reusing `memwatch.tree_rss_mb`), and checkpoints the artifact to `--out`
after the run. `summarize_repeats` aggregates per-metric mean/min/max (MCC null-through
with `nullCount`) and diffs per-query served **sets** across runs for both legs; a
non-empty unstable list fails the run nonzero *after* the checkpoint is written (a golden
must not tolerate a moved set; anchors are never tolerance-blessed).

**RED (pasted).**

```
$ pytest scripts/tests/test_llamaindex_harness_evaluate.py -q -k "summarize_repeats or fresh_scratch"
E  AttributeError: module 'llamaindex_harness.evaluate' has no attribute 'summarize_repeats'
E  AttributeError: module 'llamaindex_harness.evaluate' has no attribute 'fresh_scratch_copy'
4 failed, 30 deselected
```

Main-level wiring RED (`RssSampler` missing + argparse rejecting base mode):

```
E  AttributeError: module 'llamaindex_harness.evaluate' has no attribute 'RssSampler'
E  argparse.ArgumentError: the following arguments are required: --scratch-data-root
5 failed, 34 deselected
```

Report RED (before the renderer existed): `assert 'Repeat-run spread' in ...` /
`assert 'Variance guarded' in ...` → `2 failed, 21 deselected`.

**Live campaign** (one process, memwatch 12288, quiesced base `aec21bf7…`, ≈11 min,
peak 7197 MB, exit 0):

```
$ python3 memwatch.py --cap-mb 12288 --log /tmp/p2-closeout/repeats3-mem.log -- \
    python3 -m llamaindex_harness.evaluate \
    --corpus corpora/project-corpus-100.json --store-dir /tmp/p1-full-store \
    --scratch-base /tmp/p1-eval-scratch-quiet-base.db --scratch-root /tmp/p2-repeats3 \
    --repeats 3 --out /tmp/p2-closeout/results-f1-repeats.json --offline
...
eval: n=99 paired=99 stale=1 repeats=3 harness hit-rate=0.677 f1=0.158 |
  ai-raccoon hit-rate=0.808 f1=0.187 | mcc=0.5954897352959921 cont={'a': 65, 'b': 2, 'c': 15, 'd': 17}
memwatch: peak=7197MB cap=12288MB result=exit(0)
```

Per-run peaks and stability from `docs/work/results-f1-repeats.json["repeats"]`:

| repeat | peak RSS | row stability (before == after) |
|---|---|---|
| 1 | 7207 MB | entries=56457, max_created=1788974080, max_updated=1788974080 |
| 2 | 6522 MB | identical |
| 3 | 6451 MB | identical |

(a single base-mode run before the campaign — the first launch without `--repeats 3` —
peaked 5763 MB; the campaign max is repeat 1's 7207 MB, under the 12 288 MB cap.)

**Unstable-id list (expect: empty):** `{"harness": [], "airaccoon": []}`.

**Golden reproduction (frozen gate).** `rows` are ordered-exact equal to
`docs/work/results-f1.json` on both legs; the only top-level difference is the additive
`summary.gaps.taxonomy` block plus the new `repeats` block:

```
summary keys equal: True
DIFF: gaps.taxonomy | new: {n_paired: 99, c_cell: 15, cells: {none: 84, fusion: 15,
      embedding: 0, unrecoverable: 0, unknown: 0}, unknownShare: 0.0} | golden: None
rows equal (ordered): True
harness hashes ordered-exact: True
bank hashes ordered-exact: True
staleAnchors: ['C035'] == golden ['C035']
base sha before == after: aec21bf79e7a87ac5c0c18e4c5d093c2b40d325249b891e721dd6e4ca97ef520 (base untouched)
run C vs run D equal: True
```

Spread (artifact): every metric `mean == min == max` (harness hit 0.6768, bank hit 0.8081,
harness F1 0.1582, bank F1 0.1874, MCC 0.5955 — bank mean F1's 3-value float mean differs
from the single value by 1 ulp: 0.18742985409652077 vs …74, well inside the ±1e-9 term).
What the repeats guard is stated in the report: weight drift (revision/bytes pinned),
server nondeterminism (fresh scratch + fresh server per repeat), bank drift (row-stability
snapshot per repeat; C11). Process-level harness determinism remains the P1 C7 double-run
gate — repeats reuse one read-only harness store, which is exactly what the plan's guard
list asks them to cover.

## AC2 — gap taxonomy

**Definition (pure, over the EXISTING `fts_hit`/`vector_hit` columns).** The columns are
computed in `build_harness_fn` from the FULL leg lists returned by `fts_leg`/`vector_leg`
at `candidate_window(8) = max(3*8,100) = 100` — **not** a top-8 slice. This was the plan's
open worry ("if the window cannot be reconstructed without a retrieval change, STOP"); it
does not apply at the frozen HEAD: the window is already what the columns measure. Pinned
by `test_leg_diagnostics_use_candidate_window_not_top8`, broken on purpose by slicing the
leg lists to `[:EVAL_LIMIT]`: `assert out["fts_hit"] == 1` → `assert 0 == 1` (RED), restored
green. A measured proof from the frozen store follows below.

| label | rule |
|---|---|
| `none` | not a c-cell deficit (harness hit, or the bank missed too — agreement is never misattributed as deficit) |
| `fusion` | a leg's candidate window held the anchor, the fused pipeline did not serve it (dedupe/RRF/relative floor/Take(8)) |
| `embedding` | no leg window held it, clean natural-language query |
| `unrecoverable` | no leg window held it, debris/tool-call artifact (C9 signature) |
| `unknown` | leg diagnostics absent (or an unpaired error row) |

Conservation: the five cells sum to `n_paired` (`none` is the c-cell complement);
`gap_taxonomy.unknownShare` is capped at 5% — enforced by `eval_gate_failures` (fails the
run loud) and by `report.render` (refuses to publish). Golden reading:
`cells = {none: 84, fusion: 15, embedding: 0, unrecoverable: 0, unknown: 0}`,
`unknownShare = 0.0%`, `c_cell = 15` == `summary.gaps.c_cell`.

**RED (pasted).** Four taxonomy functions missing:

```
FAILED test_gap_columns_classification_table
FAILED test_gap_columns_shared_scope_rows_are_fusion_drop
FAILED test_gap_counts_sum_to_paired
FAILED test_unknown_share_over_cap_fails_the_eval_gate
4 failed, 1 passed  (the window-not-top8 pin passed on the existing code, as expected)
```

Report replacement RED (`Classified gap taxonomy` absent; per-row label absent):

```
E  assert 'Classified gap taxonomy' in '# LlamaIndex fusion harness — eval report ...'
E  AssertionError: assert ('shared' in '| E002 | ai-raccoon/shared | h2 | 0 0.000 | ... 1/0 | |'
   and 'fusion' in '| E002 | ... | 1/0 | |')
2 failed, 16 deselected
```

Cap-raise mutation (after green): remove the `unknownShare > 0.05` raise → the
`test_report_rejects_unknown_share_above_cap` gate fails `DID NOT RAISE ValueError`; restored
→ green.

**Per-row labels for the c-cell (frozen golden).** From the frozen-golden oracle test
(`test_frozen_golden_c_cell_is_fusion_with_shared_oracle_rows`): all 15 c-cell rows =
`fusion`; the `targetScope=shared` rows (C019/C065/C081 — the C10 oracle, selected by scope
because ids shift on regeneration) are all `fusion`, never `embedding`. Measured
stage/rank table (script `docs/work/p2_c_cell_leg_positions.py`, run under memwatch 12288,
peak 2492 MB, exit 0; `!` = rank inside the 100-window but outside top-8):

```
c-cell rows: 15 of 99 scored (golden c_cell=15)
id    label         fts       vec   pre  post  floor stage          golden_legs label_check
C002  fusion        34!         -    68    66  False relative-floor 1/0         ok
C009  fusion        98!         -   183   183  False relative-floor 1/0         ok
C011  fusion        68!         -   126   126  False relative-floor 1/0         ok
C019  fusion          1         -    28    28   True Take(8)        1/0         ok
C026  fusion          5         -     9     9   True Take(8)        1/0         ok
C029  fusion        10!         -    18    18   True Take(8)        1/0         ok
C030  fusion          9!         -    15    16   True Take(8)        1/0         ok
C065  fusion          1         -    32    32   True Take(8)        1/0         ok
C081  fusion          1       95!    10    10   True Take(8)        1/1         ok
C092  fusion          4         -    32    32   True Take(8)        1/0         ok
C093  fusion          1         -    26    27  False relative-floor 1/0         ok
C094  fusion          1         -    27    30   True Take(8)        1/0         ok
C095  fusion          1         -    20    20   True Take(8)        1/0         ok
C099  fusion          1         -    13    13   True Take(8)        1/0         ok
C100  fusion          3         -    12    12   True Take(8)        1/0         ok

window hits beyond top-8: fts=5 vector=1
labels: {'fusion': 15}
```

Readings:
- **6 of 15 c-cell rows would be misclassified under a top-8 reading** (C002 r34, C009 r98,
  C011 r68, C029 r10, C030 r9 FTS; C081 vector r95). The plan's amendment was load-bearing,
  and the frozen columns already carry the right window semantics.
- All 15 have a leg window hit ⇒ all 15 are `fusion`. No c-cell row is an embedding-side
  miss on this corpus, so `embedding`/`unrecoverable` are 0 in the golden (both labels are
  still exercised by the classification-table test).
- Measured sub-stages inside `fusion`: **11 drop at Take(8), 4 at the relative floor**
  (C002/C009/C011/C093). The pure taxonomy cannot split those from the two columns alone;
  the label stays `fusion` (pipeline drop — honest at that resolution) and the split is
  recorded here as the trace evidence. C10's shared-row verdict (survived floor, dropped
  at Take(8)) is reproduced for C019/C065/C081.
- `label_check = ok` on every row: computed window ranks reproduce the golden's
  `fts_hit`/`vector_hit` exactly.

## AC3 — anchor verification never silent

`anchor_verdict(n_entries, stale_ids) -> "refuse" | "warn" | "clean"` (refuse iff every
anchor is stale — the wrong-store/copy shape; warn iff some). `main` uses it instead of the
inline `raise`; a refusal prints `FAIL: no corpus anchor resolves …` and exits 1. The
one-line stdout summary already carried `stale=N` and still does; `results.json` always
carries `staleAnchors` on the write path, empty list included.

Pinned choice for the gate-failure path: **failure paths never write results.json** (a
written artifact only ever exists for a run that passed its gates, so the key is present by
construction). The repeats path is the documented exception in kind: it checkpoints after
every repeat, so an interrupted campaign keeps the runs already completed, each carrying
`staleAnchors`.

RED pasted: `AttributeError: … has no attribute 'anchor_verdict'`. Invariant mutation:
remove `out["staleAnchors"] = stale_anchors` → both recording tests fail with
`KeyError: 'staleAnchors'`; restored → green. Main-level tests (all heavy seams faked): the
write path records `[]` and `["C035"]` with `stale=0`/`stale=1` in the summary line; the
refusal path returns 1 and the `--out` file does not exist.

## AC4 — memory guard (verified, gaps filled)

The P1-close-out implementation is real and was not rebuilt. Existing gates re-proven by
deliberate breaks:

| gate | break applied | RED |
|---|---|---|
| live tree-kill | `kill_tree` root-only (`for pid in []`) | `AssertionError: grandchild 96713 survived the tree kill` |
| stdout passthrough | (P1 evidence; unchanged) | P1 `RED-c5b-memwatch-log.txt` |
| module-run / cap / sampling help | `DEFAULT_CAP_MB = 2 * 1024` | `assert '12 GB' in …` and `assert 2048 == (12 * 1024)` both fail |

Gap filled: `test_memwatch_runs_as_module_documents_cap_and_sampling` (`python -m memwatch
--help` exits 0, shows `--cap-mb`, documents the periodic-sampling limitation, states the
default cap) and `test_memwatch_default_cap_is_12gb`. The break exposed a real drift — the
help string hardcoded `"= 12 GB"` beside a derived number — fixed to derive the GB label
from `DEFAULT_CAP_MB`, so the gate now catches a cap change. `memwatch --help` still
documents "a spike shorter than --interval can be missed — this is a backstop, not a
cgroup". Mutual exclusion unchanged: one heavy process at a time; the repeat campaign ran
serialized under a single 12 288 MB cap with peaks ≤ 7207 MB.

## Report integration

`docs/work/2026-09-10-p1-full-100-eval-report.md` regenerated from
`docs/work/results-f1-repeats.json` (whose rows/summary are ordered-exact equal to the
frozen golden; only additive `gaps.taxonomy` + `repeats`), because the frozen
`results-f1.json` predates the repeats block and AC1 requires the report to render the
spread. The frozen golden files are byte-untouched.

What changed vs the P1 report:
- Per-query table gains a `gap` label column (per-row evidence).
- The provisional `c_*` counts table is **deleted**; the single classified taxonomy table
  replaces it, with conservation and the 0.0% unknown share stated. The P1 test that
  asserted the provisional table was repurposed to assert the classified replacement (and
  that the `c_*` names do not survive as a second taxonomy).
- New `### Repeat-run spread` block: `mean [min–max]` per metric, `unstable=none`, and the
  variance statement.
- The embedding bullet is now an explicit C13 seam disclosure, CORRECTED by C14 (this
  lane's report predates the correction): both systems use CLS (llama-index
  `get_pooling_mode` defaults to 'cls'; harness output equals the model-card
  `last_hidden_state[:,0]` recipe at cos 1.00000), and the measured seam is ONNX-vs-HF
  model conversion (bank ONNX reproduces its stored vectors at 0.9826; ONNX-vs-HF is
  0.5934 on the same text; padding is a no-op). Labels are against measured leg
  positions; only both-windows-miss rows are `embedding`/`unrecoverable`.
- C9 composition stratification (debris 23 / clean 76, recomputed) and the C10 trace note
  are unchanged.

## Frozen-state conformance

- `results-f1.json` / `results-f1-run2.json`: unchanged on disk, rows reproduced.
- Contract knobs read from `params.json` (rrfK 60, weights 1/1, limit 8, floor 0.6, λ 0.1,
  threshold 0.1, Max, Max3X100, alpha 0.5): untouched.
- Quiesced scratch recipe used for every eval run: `fresh_scratch_copy` from
  `/tmp/p1-eval-scratch-quiet-base.db` (`aec21bf7…`, 10 watch/sweep/extract kill switches
  off), row-stability asserted per repeat. The base SHA is identical before and after the
  campaign; `/tmp/p1-live-copy.db` was only ever opened read-only.
- No eval run on an un-quiesced scratch; no port 7721 (ephemeral binds 52349/52456/52565).
- Eval runtime: 3 repeats ≈ 11 min wall (inside the N≥3 ≈9 min bar plus model load), peaks
  7207/6522/6451 MB.

## Proposed plan amendments (task-tracking plan not owned here)

1. **AC2 note is stale, not the code.** The "leg diagnostics currently come from top-8 call
   shapes — if the window cannot be reconstructed without a retrieval change, STOP" caveat
   does not hold at the frozen base: `build_harness_fn` computes `fts_hit`/`vector_hit`
   over the full `candidate_window(8)=100` leg lists (P1-era behavior, pre-P2), and the
   measured trace finds 6/15 c-cell anchors at window rank 9–100. No retrieval change was
   needed; the plan can drop the fallback clause and record the window-level semantics as
   already in force (test `test_leg_diagnostics_use_candidate_window_not_top8` pins it).
2. **New measured split inside `fusion`.** Of the 15 c-cell rows, 11 drop at Take(8) and 4
   at the relative floor (C002/C009/C011/C093). With only the existing columns the
   taxonomy cannot separate them, so both stay `fusion`; the per-row stages are recorded in
   this report and reproducible via `docs/work/p2_c_cell_leg_positions.py`. Optional
   follow-up (owner's call): persist per-row leg ranks/scores in `results.json` to make the
   floor-vs-limit subclassification post-hoc; that is a schema addition, deliberately not
   done here.
3. **AC3 failure-path choice pinned**: write-only-on-success; repeat campaigns checkpoint
   completed runs (documented above). If the plan wants partial results on gate failure, it
   needs a new AC — the current claim is scoped, not silently weakened.
4. **AC4 help-string derivation**: the GB label now derives from `DEFAULT_CAP_MB` (was
   hardcoded `12 GB`); no behavioral change, but the AC4 gate now fails on a cap change.
5. **Report source**: the regenerated report is generated from
   `docs/work/results-f1-repeats.json` (golden-equivalent rows + repeat/taxonomy blocks),
   not from the frozen `results-f1.json` directly; P3/P4 should treat the repeats artifact
   as the report source and the golden files as the frozen numeric reference.

## Artefacts and commands

- Repeat artifact: `docs/work/results-f1-repeats.json` (3 runs; `repeats.n=3`,
  `unstable=[]`, per-run peaks/stability).
- Regenerated report: `docs/work/2026-09-10-p1-full-100-eval-report.md`.
- Evidence script: `docs/work/p2_c_cell_leg_positions.py` (frozen golden + store,
  stage-by-stage trace).
- Raw campaign logs: `/tmp/p2-closeout/repeats3.log`, `repeats3-mem.log`,
  `repeats-verification.txt`, `results-f1-repeats.json`; RED pastes under `/tmp/p2-red/`.
- Commands:
  - campaign: `cd scripts/retrieval_tuning && python3 memwatch.py --cap-mb 12288 --log
    /tmp/p2-closeout/repeats3-mem.log -- python3 -m llamaindex_harness.evaluate
    --corpus corpora/project-corpus-100.json --store-dir /tmp/p1-full-store
    --scratch-base /tmp/p1-eval-scratch-quiet-base.db --scratch-root /tmp/p2-repeats3
    --repeats 3 --out /tmp/p2-closeout/results-f1-repeats.json --offline`
  - trace: `cd scripts/retrieval_tuning && python3 memwatch.py --cap-mb 12288 --log
    /tmp/p2-c-cell-mem.log -- python3 ../../docs/work/p2_c_cell_leg_positions.py --offline`
  - report: `cd scripts/retrieval_tuning && python3 -m llamaindex_harness.report --results
    ../../docs/work/results-f1-repeats.json --out
    ../../docs/work/2026-09-10-p1-full-100-eval-report.md --context "$(cat
    /tmp/p1-closeout/report-context.json)"`
  - tests: `python3 -m pytest scripts/tests/test_llamaindex_harness_evaluate.py
    scripts/tests/test_llamaindex_harness_report.py scripts/tests/test_memwatch.py
    scripts/tests/test_llamaindex_harness_cli.py
    scripts/tests/test_make_quiesced_scratch.py -q`
