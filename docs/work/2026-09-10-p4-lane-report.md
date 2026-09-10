# P4 lane report — integration + review-fix round (air-full-hundred-query-parity-eval)

Base `352fbfa0` (P1 close-out + round 2 + P2 + C14 + P3 merged). Branch
`task/air-full-hundred-query-parity-eval-lane-p4`. Orchestrator finished AC2/AC3
in-session after the lane's architect was aborted mid-AC2 (false-stall on the
repeat campaign); the four fix commits below are the lane's.

## Review findings closed (commits)

| Finding | Commit | Evidence |
|---|---|---|
| **F1** — `PARAMS` unpinned: a `data/knobs.json` edit silently moved the contract | `91ca20e5` | collector now holds the frozen contract as literals and compares store params against them; `test_collect_ac_evidence` pins repo `PARAMS`, the eight-key `KNOB_DEFAULTS` agreement, and the collector literals to one independent literal. Injected `PARAMS.rrfK` edit now fails a gate (RED in the commit's evidence). |
| **F2** — last C14 carriers still stated the retracted pooling mechanism | `776f1958` | `2026-09-09-air-p1-lane-report.md:409,501` and `c10_shared_leg_trace.py:23` replaced with the measured ONNX-vs-HF seam (0.5934; bank-ONNX self-consistency 0.9826; harness CLS cos 1.00000) or a retraction pointer. |
| **F3** — P3 AC4 evidence lived only in `/tmp` | `35361d40` | `scripts/retrieval_tuning/diff_golden.py` + `test_diff_golden.py` in-tree (exact rows/hits/contingency/MCC, mean-F1 ±1e-9, allow-list = sessionId + provenance pins, `staleAnchors` compared, `summary.gaps.taxonomy` additive — F5's rule stated in the module docstring). |
| **F4** — CI lane missed the new gates | `9591f895` | `.github/workflows/build.yml` scripts-harness list now includes `test_retrieval_tuning_scopes.py`, `test_no_hardcoded_knobs.py`, `test_refresh_corpora.py`, `test_collect_ac_evidence.py`, `test_diff_golden.py`; heavy files documented as the local gate. |
| **F5** — tolerance wording omitted the additive key | `35361d40` | stated in `diff_golden.py` ("`summary.gaps.taxonomy` is additive inside `summary` and not compared"). |
| **F7** — no-hardcode scan's fail-capability only in a transcript | `91ca20e5` | scan extracted; in-tree test proves the scan goes red on a re-hardcoded constant. |
| **F6** — plan checkbox drift | orchestrator | C12 checked; C14 re-scoped with its remaining carriers; C13 kept open as the documented owner decision. |

## P4 AC1 — cross-package joins on the merged tree

- Touched suites on `352fbfa0`: harness + retrieval_tuning + the new P3 gates —
  **70 passed, 5 skipped** (this transcript) and the F1/F3/collector trio
  **20 passed**. Nothing in P2/P3 changed P1's behavior (P3's post-refactor runs
  diff CLEAN; P2's repeats artifact rows are ordered-exact equal to the golden).
- Frozen contract values verified in `data/knobs.json` == the pre-refactor
  literals (rrfK 60, weights 1/1, limit 8, floor 0.6, λ 0.1, threshold 0.1,
  Max, Max3X100, alpha 0.5).

## P4 AC2 — post-integration eval with `--repeats` on the merged tree

Command (quiesced base `eec…`→ fresh base per repeat, sha checked by the tool):

```
cd scripts/retrieval_tuning
python3 memwatch.py --cap-mb 12288 --log /tmp/p4-integration/repeats3-mem.log -- \
  python3 -m llamaindex_harness.evaluate --corpus corpora/project-corpus-100.json \
  --store-dir /tmp/p1-full-store --scratch-base /tmp/p4-integration/base-quiet.db \
  --scratch-root /tmp/p4-integration/scratch --repeats 3 \
  --out /tmp/p4-integration/results-f1-merged-repeats.json --offline
```

RESULT (campaign complete, artifact committed as
`docs/work/results-f1-merged-repeats.json`):

| artifact | harness | bank | MCC | cont | stale | unstable | peak |
|---|---|---|---|---|---|---|---|
| merged repeats (n=3) | 0.6768 / 0.1582 | 0.8081 / 0.1874 | 0.5955 | 65/2/15/17 | [C035] | `{harness: [], airaccoon: []}` | **7137 MB** (exit 0) |

Diff vs goldens (in-tree `diff_golden.py`):

```
diff_golden.py docs/work/results-f1.json      /tmp/p4-integration/results-f1-merged-repeats.json  -> CLEAN (exit 0)
diff_golden.py docs/work/results-f1-run2.json /tmp/p4-integration/results-f1-merged-repeats.json  -> CLEAN (exit 0)
corpusSnapshotSha256: golden=e0434a7214ac cand=e0434a7214ac
staleAnchors: golden=['C035'] cand=['C035']
```

Run log: `WARNING: repeat N: scratch copy sha eeb431a3… != store copy sha
e0434a72… (row count matches: access-bump mutation from a prior run)` — the
documented settings/bookkeeping-only delta, expected and row-matched (same
warning class as the P2 campaign).

## P4 AC3 — fresh-bank end-to-end (fixture-scoped)

Volume test double, stated up front: the full path re-embeds 56k rows (~13 h —
already proven end-to-end in P1); the fixture keeps the same scripted chain with
615 rows so a wiring failure is cheap to see. Every heavy step under memwatch.

1. **Copy step** — `python3 scripts/retrieval_tuning/make_memory_copy.py --live
   ~/.ai-raccoon/memory.db --target /tmp/p4-e2e/live-copy.db --sample-size 3`
   → exit 0, `VERIFIED: copy is healthy and matches the live snapshot`
   (1,465,483,264 bytes; inherited `retrieval.*` settings printed).
2. **Refresh step** — `python3 scripts/refresh-retrieval-corpora.py --copy
   /tmp/p4-e2e/live-copy.db --out-dir /tmp/p4-e2e/refreshed` → **exit 2
   (snapshot-mismatch)**: the fresh copy is `2e6ea1e5…` while
   `project-corpus-100.json` pins `e0434a72…` — the guard working as designed.
   Against the pinned copy: `project-corpus-100.json OK (committed-match=True)`
   and `eval-set-100.json SMOKE-REGRESSION` (the known stale-artifact
   amendment) → **exit 3** with both reasons in `refresh-report.json`.
3. **Fixture build (test double)** — copy the fresh live copy; delete every row
   except `project_id='pi-badger-integration' OR scope='shared'` (615 rows;
   delete triggers keep FTS/vec consistent); quiesce via
   `make_quiesced_scratch.py` (`3886d829…`, 12 settings disabled, parity verified).
   Fixture corpus = the 3 pbi queries + 3 shared queries from the frozen corpus
   (all six anchors verified present in the kept rows).
4. **Ingest** — `python3 -m llamaindex_harness.ingest --copy /tmp/p4-e2e/fixture.db
   --store-dir /tmp/p4-e2e/store --corpus /tmp/p4-e2e/corpus-small.json --offline`
   → **exit 0**, `VERIFIED: store is chunk-faithful to the copy; FTS parity probe
   clean`, peak 3985 MB.
5. **Evaluate** — `... evaluate --corpus /tmp/p4-e2e/corpus-small.json --store-dir
   /tmp/p4-e2e/store --scratch-base /tmp/p4-e2e/fixture-quiet.db --scratch-root
   /tmp/p4-e2e/scratch --out /tmp/p4-e2e/results.json --offline` → **exit 0**,
   `n=6 paired=6 stale=0`; harness 0.000 / bank 0.833 (a WIRING double, not a
   measurement — on 615 rows the queries' near-duplicates crowd the fused
   top-8; the leg pattern `fts=1, vec=0` → final miss reproduces the real
   c-cell mechanism faithfully, and scope routing verified: shared queries serve
   the global shared tier, the pbi project query serves pbi project rows, all
   six anchors present in the store).
6. **Report** — `... report --results /tmp/p4-e2e/results.json --out
   /tmp/p4-e2e/report.md --context <fixture context>` → **exit 0**, 8234 chars,
   5 section headers (the required section set).

Full-path (real volume) command for an overnight run, for the record:
`make_memory_copy.py` → `refresh-retrieval-corpora.py --copy <fresh copy>`
(expect exit 2 snapshot-mismatch until the corpus provenance is re-pinned) →
`ingest --copy <fresh copy> --corpus corpora/project-corpus-100.json`
(~13 h) → the same evaluate/report as above.

## PR readiness

- Frozen goldens untouched; merged-tree behavior == golden under the tolerance
  (P3 evidence) and the repeats campaign (AC2, above).
- Full-suite failures: the same pre-existing set (optuna/matplotlib deps +
  `eval-set-100.json` staleness) — cited in the PR description so they are not
  read as new.
- `eval-set-100.json` provenance amendment (P3 lane): still open by design; the
  refresh script reports it honestly (exit 3 with the reason).
