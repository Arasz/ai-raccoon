# Lane report: air-eval-corpus-provenance-clean-ci (packages C + E)

- Branch: `task/air-eval-corpus-provenance-clean-ci-lane-impl`
- Base: `3127051b` (`git merge-base HEAD origin/main`)
- Code-final head tested by the integration gates: `e8e86ae4`
- Final head differs from that tested head by this report and the plan checkboxes only
- Plan: `docs/work/2026-09-10-air-eval-corpus-provenance-clean-ci.md`

No PR is opened from this lane; the task brief reserves the merge for the
orchestrator. This report and the commit history stand in for the plan's I4 PR
description.

## AC status

| AC | Status | Gate (exact command) | Result |
|---|---|---|---|
| C1 | PASS | `AI_RACCOON_EVAL_COPY=/tmp/p1-live-copy.db python3 -m pytest scripts/tests/test_retrieval_tuning_eval_corpus.py -q` | 11 passed, 0 skipped |
| C2 | PASS | `python3 scripts/refresh-retrieval-corpora.py --copy /tmp/p1-live-copy.db --out-dir "$(mktemp -d)"` + `AI_RACCOON_EVAL_COPY=… python3 -m pytest scripts/tests/test_refresh_corpora.py -q` | exit 0, both `OK … committed-match=True`; 18 passed |
| C3 | PASS | `test_committed_corpora_carry_snapshot_pins`, `test_committed_pins_refuse_a_different_copy` (new, copy-independent) | 18 passed; wrong copy refuses with exit 2 |
| C4 | PASS | `python3 -m pytest scripts/tests/test_retrieval_tuning_eval_corpus.py -q` (no env) + `python3 -m pytest scripts/tests/test_llamaindex_harness_retrieve.py -q` | 8 passed, 3 skipped with reasons; 14 passed |
| C5 | PASS | base-vs-new artifact diff + frozen-golden diff + `test_diff_golden.py` | header added, exactly the 6 named entries moved; goldens byte-identical; 15 passed |
| C6 | PASS | `dotnet build` + `dotnet test --filter "FullyQualifiedName~MiniLmGoldenVectorReaderTests"` | build 0 warnings/0 errors; 3 passed (RED pasted below) |
| E1 | PASS | copy-absent, pinned and wrong-copy probes | skips carry the missing/mismatch reason; pinned run has 0 skips |
| E2 | PASS | `python3 -m pytest scripts/tests/test_dependencies_declared.py -q` | 2 passed |
| E3 | PASS | `python3 -m pytest scripts/tests/test_retrieval_tuning_report.py scripts/tests/test_retrieval_tuning_tune.py -q -rs` | 24 passed, 3 skipped with reasons; with optuna 39 passed |
| E4 | PASS | `python3 -m pytest scripts/tests -q -rs` | 629 passed, 25 skipped, 0 failed; every skip line names a reason |
| E5 | PASS (local) | exact `build.yml` lane command | 180 passed, 3 skipped; CI cannot be observed from this lane |
| I1 | PASS | `python3 -m pytest scripts/tests -q` | 629 passed, 25 skipped, 0 failed in 79.66 s |
| I2 | PASS | refresh CLI + corpus contract tests | exit 0; 29 passed |
| I3 | PASS | golden diff, `test_diff_golden.py`, `test_collect_ac_evidence.py`, name-only diff | golden diff empty; 15 passed; no `src/` or `scripts/src/retrieval_tuning/` change |
| I4 | PASS (adapted) | commit history + this report | no PR per brief |

## The six moved anchors (C5)

Both `expectedHash` and `answerSpan` moved on each row. Nothing else in the
artifact changed.

| id | old `expectedHash` | new `expectedHash` |
|---|---|---|
| E040 | `4b4c9327962cdf1670362dac1f47d1e89d008b317411aceb5c21a09c91950e2e` | `5bf7f0c913c3ee30d8365a50505293c1fec8b8bc8c24c12d872d11ba83866f19` |
| E041 | `a8dc353473e76f527d20a9d626695762041cfceedcbb5bc8c0877542772ecd49` | `ed214b8bf91b539059c52226fc77454ef9d3d478b2ecad305f000139e1df8714` |
| E042 | `024480c019dddb2841142659197dce92ece61f986533bf7315f50157110eb847` | `79141fdc52622fa73b2e6510fef946388a45e7e3b2155394b5a8d5a2322a953d` |
| E045 | `e3b89183bc7c41af8ce9a6c7b617583b9ee97cfbc113da80b30915b102b19ef5` | `3bbd47d7cbe50b55f24628c5f5fe9b201c26278c7888256c3a39dc99089a2576` |
| E063 | `d91dfe96c5057d1d36b77137d6df2d9c46c4e92f178770814c819bb347eb75af` | `8580b2d6968eb13d47a79589852408c40db8f3160a33036655c49ecaa1a8f3e8` |
| E065 | `07ea05a5f58f7c4f828c4bd3c1cb7b83a2bb79a589c8ccd183a206cdbc1e9dbd` | `de2eb16eb9b2d48da0dddbe684eaee9e13e9754b4b1e78a575e9907a50a3e847` |

Base-vs-new checker output:

```
header: {"generator": "build_eval_corpus.py", "queryCount": 100, "seed": 42, "snapshotSha256": "e0434a7214ac4caf1dbbef56147f582515bd0f5ebda665ad8cb06687296a55f6"}
base-vs-new: header added; exactly 6 entries differ; fields = [('answerSpan', 'expectedHash')]
ids: ['E040', 'E041', 'E042', 'E045', 'E063', 'E065']
```

## RED evidence

Header pin gate, before the artifact carried a header:

```
FAILED scripts/tests/test_retrieval_tuning_eval_corpus.py::test_committed_corpus_carries_a_snapshot_pin
E       AssertionError: committed eval-set-100.json must carry a header
E       assert None is not None
1 failed, 7 passed, 3 skipped
```

Refresh contract, before regeneration:

```
SMOKE-REGRESSION: eval-set-100.json (queries=100, committed-match=False)
refresh: smoke-regression (exit 3)
FAILED ...TestRegistry::test_committed_corpora_carry_snapshot_pins
FAILED ...TestRegistry::test_committed_pins_refuse_a_different_copy
FAILED ...TestRegistry::test_pinned_copy_reproduces_every_committed_corpus
3 failed, 15 passed
```

Python raw reader, header shape present:

```
E       KeyError: slice(None, 20, None)
scripts/tests/test_llamaindex_harness_retrieve.py:130: KeyError
1 failed
```

C# raw reader, header shape present:

```
System.InvalidOperationException : The requested operation requires an element of type 'Array', but the target element has type 'Object'.
   at System.Text.Json.JsonElement.EnumerateArray()
   at MiniLmGoldenVectorReader.ReadEvalSetQueries(String json) ...:13
failed ...MiniLmGoldenVectorReaderTests.ReadEvalSetQueries_ReadsTheHeaderShape
total: 3, failed: 2, succeeded: 1
```

matplotlib declaration:

```
E       AssertionError: third-party imports missing from [project.dependencies] in pyproject.toml: matplotlib (used in scripts/generate-embedding-benchmark-report.py)
1 failed, 1 passed
```

optuna gates without the guard:

```
ERROR scripts/tests/test_retrieval_tuning_tune.py:13: import optuna -> ModuleNotFoundError
FAILED ...TestStudySummary::test_summary_reads_a_real_optuna_study
FAILED ...TestStudySummary::test_unreadable_storage_yields_none
2 failed, 24 passed
```

Committee fixture, before the extraction:

```
FAILED scripts/tests/test_committee_grade.py::test_sampling - FileNotFoundError: [Errno 2] No such file or directory: '…/threshold-committee-eval/fixtures/metrics-fixture.json'
1 failed
```

## Guard proofs (the checks can fail, and can run)

- Wrong-copy probe: `AI_RACCOON_EVAL_COPY=/tmp/wrong-copy.db` gives three
  skips reading `copy at /tmp/wrong-copy.db is not the snapshot the committed artifact pins`.
- optuna present, in a throwaway venv with `optuna 5.0.0`:
  `39 passed in 1.08s` for `test_retrieval_tuning_report.py test_retrieval_tuning_tune.py`.
  Absent, the same command reports the three reasoned skips.
- Committee fixture restored from `af1c2482^`:
  `test_committed_metrics_fixture_contract` passes; with the file removed again
  it skips with the missing path in the reason.

## Frozen contract

```
git diff --exit-code 3127051b -- docs/work/results-f1.json docs/work/results-f1-run2.json docs/work/results-f1-repeats.json docs/work/results-f1-merged-repeats.json
(no output)
```

`test_diff_golden.py` and `test_collect_ac_evidence.py`: 15 passed.
`test_threshold_eval_integration.py::test_merge_hygiene_pr_branch_has_empty_src_diff`:
1 passed. The branch changes no file under top-level `src/` or
`scripts/src/retrieval_tuning/`.

## Full suite on the code-final head

```
$ python3 -m pytest scripts/tests -q
629 passed, 25 skipped, 1 warning in 79.66s (0:01:19)

$ python3 -m pytest scripts/tests -q -rs
629 passed, 25 skipped, 1 warning in 45.35s
$ grep -c "^SKIPPED"  # 25, every line names a reason
```

Baseline for comparison: 8 failed, 623 passed, 18 skipped plus one collection
error (optuna).

## Timings

- `build_eval_corpus.py` regeneration: 6.1 s (1.4 GB copy hash included).
- Refresh run: 15 to 20 s.
- Full `scripts/tests`: 79.66 s cold, 45.35 s warm.
- `dotnet build`: 32.33 s cold, 13.40 s incremental.
- New C# reader tests: 1.9 s.
- `test_llamaindex_harness_retrieve.py`: 4.71 s.
- Local CI lane command: 34.09 s.

## Deviations

1. No PR. The task brief hands the merge to the orchestrator, so the plan's I4
   PR link is replaced by this report.
2. E4's fixture guard is a fourth E item. The deleted
   `metrics-fixture.json` is a pre-existing main failure that blocks E4's
   "no failures" acceptance criterion; the plan flags it in E4 itself.
3. `test_committed_pins_refuse_a_different_copy` is a new test beyond the
   plan's table. The plan's C3 gate calls the CLI probe optional; a pytest test
   using a placeholder copy and an explicit `copy_sha` proves the same thing on
   a runner with no bank copy.
4. CI green for E5 cannot be observed from this lane because no PR exists. The
   exact lane command passes locally (180 passed, 3 skipped).
