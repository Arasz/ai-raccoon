# P3 lane report — post-F1 retrieval-harness consolidation (dedup / simplify / compose)

Task: `air-full-hundred-query-parity-eval`, package P3. Branch:
`task/air-full-hundred-query-parity-eval-lane-p3`, base `12a72dfb`
(P1 close-out + round 2 + P2 + C14 all merged). Spec:
`docs/work/2026-09-09-air-full-hundred-query-parity-eval.md` §P3; merge inventory in
`docs/work/p3-merge-map.md`.

Frozen state preserved: harness 0.6768/0.1582, bank 0.8081/0.1874, MCC 0.5955,
cont 65/2/15/17, stale=[C035], corpus snapshot `e0434a72…`, contract
rrfK=60 / weights 1/1 / limit 8 / floor 0.6 / λ=0.1 / threshold 0.1 / Max /
Max3X100 / alpha=0.5.

| AC | status | gates |
|---|---|---|
| AC1 dedup (7 spellings) | DONE | `test_retrieval_tuning_scopes.py` (20 passed), shim identity gates, merge map reviewed against the 7-count |
| AC2 JSON owns constants | DONE | `test_no_hardcoded_knobs.py` (4 passed, key-derived list, watch-it-red), `rg` zero on the moved literals |
| AC3 refresh script | DONE | `test_refresh_corpora.py` (16 passed; exit 0/1/2/3 fixtures), pinned-copy byte reproduction for the project corpus, deterministic report |
| AC4 behavior preservation | DONE | two post-refactor eval runs diff CLEAN vs both golden runs; full suite same 6 pre-existing failures; CI lane simulated green; old-vs-new CLI parity 5/5 |

---

## AC1 — one scope/bucket builder

**What moved.** `scripts/src/retrieval_tuning/scopes.py` is the one home:
`VALID_SCOPES` + `SCOPE_FALLBACK` + `DEFAULT_QUERY_SCOPE`/`DEFAULT_SERVER_SCOPE`,
`load_corpus`, `resolve_buckets` (P1's, moved verbatim), and the SQL/Chroma
emitters (`ingest_predicate`, `scope_predicate`, `slice_keep_clauses`,
`chroma_where`). `llamaindex_harness/scopes.py` is now an import-only shim
(bootstrap + re-export). The 7-spelling mapping is
`docs/work/p3-merge-map.md`; rows checked:

```
1 ingest.load_rows WHERE            -> scopes.ingest_predicate
2 ingest.verify_store id-set WHERE  -> scopes.ingest_predicate
3 ingest._scope_predicate           -> scopes.scope_predicate
4 fts_parity_probe legs             -> scopes.scope_predicate(prefix="e.")
5 retrieve._chroma_where            -> scopes.chroma_where
6 slice_copy keep-rule              -> scopes.slice_keep_clauses
7 corpus.py VALID_SCOPES/load_corpus-> scopes.CORPUS_SCOPES + scopes.load_corpus
```

**RED (before the module existed).**

```
$ python3 -m pytest scripts/tests/test_retrieval_tuning_scopes.py -q
E   ImportError: cannot import name 'scopes' from 'retrieval_tuning'
1 error in 0.06s
```

**GREEN.**

```
$ python3 -m pytest scripts/tests/test_retrieval_tuning_scopes.py -q
20 passed in 0.03s

$ python3 -m pytest scripts/tests/test_llamaindex_harness_slice.py \
    tests/test_llamaindex_harness_retrieve.py tests/test_llamaindex_harness_evaluate.py \
    tests/test_llamaindex_harness_ingest.py tests/test_llamaindex_harness_cli.py -q
… 141 passed (AC1 state)
```

Anti-duplication gate (identity, not equality):
`test_harness_shim_reexports_the_same_objects` asserts
`harness_scopes.resolve_buckets is scopes.resolve_buckets` and the same for the
table, loader and emitters. `test_ingest_predicate_is_the_old_load_rows_sql`
pins the byte-level SQL string against the 12a72dfb spelling.

**Old spellings gone (docstrings + the emitters' own clause builders only):**

```
$ grep -rn "scope IN ('project','custom')\|scope = 'shared'" scripts/retrieval_tuning scripts/src/retrieval_tuning
  … ingest.py:5,9 (docstring) / slice_copy.py:5 (docstring) / build_project_corpus.py:175 (docstring)
  … src/retrieval_tuning/scopes.py:82,87 (the two clause builders)
```

One scratch composition: `retrieval_tuning/scratch.py`
(`fresh_scratch_copy` + `scratch_server()`: fresh copy → pre-server checks →
started server), used by `evaluate`'s repeat loop (order preserved so the
mutating-start gate still fails loud). One anchor lib:
`retrieval_tuning/corpus_anchors.py`, shared by both generators and
`refresh_corpora.verify_anchors`.

**F6 correction.** `corpus.VALID_SCOPES` is now the derived
`CORPUS_SCOPES` (`VALID_SCOPES | {custom}`): the validator accepts `custom`
(a real corpus scope) and still rejects `galaxy`. Not on the full-100 eval path.

---

## AC2 — behavior constants live in `data/**/*.json`

New data files: `data/knobs.json` (PARAMS, knob defaults + verbs, eval and
threshold limits, model name/revision, bm25 weights, batch sizes),
`data/buckets.json` (slice defaults), `data/graders.json` (both trios + seeds),
`data/corpora/project-corpus-100.json` (seed/limits/frames),
`data/corpora/eval-set-100.json` (seed/allowlist/reserved files/limit). One
reader: `scripts/src/retrieval_tuning/repo_data.py`.

The gate derives its forbidden-literal list from the JSON keys (walk every
`data/**/*.json`, flatten to `(key, value)` leaves, scan the logic `.py` files
for `KEY = <literal>` / `"key": <literal>`; docstrings blanked with `ast`).
Adding a constant to JSON extends the gate automatically.

**RED (watch it fail).** `EVAL_LIMIT = 8` re-injected in `evaluate.py`:

```
E  scripts/retrieval_tuning/llamaindex_harness/evaluate.py:EVAL_LIMIT
   hardcodes knobs.json's value (EVAL_LIMIT = 8)
1 failed in 0.59s
```

**GREEN + `rg` zero on the moved literals:**

```
$ python3 -m pytest scripts/tests/test_no_hardcoded_knobs.py -q
4 passed in 0.70s

$ rg -n --glob '*.py' 'Salesforce/SFR-Embedding-Code-400M_R|cb950dc80d677c6fdc00f56c8ddd20ca2642c59e|"rrfK": 60|bm25\(docs_fts, 1\.0, 8\.0, 4\.0\)|bm25\(entries_fts, 1\.0, 8\.0, 4\.0\)|"structureAlpha": 0\.5|EVAL_LIMIT = 8|DEFAULT_CAP = 1500|DEFAULT_SEED = 20260909|TOTAL_QUERIES = 100|QUERY_CAP = 20|SEED = 42' \
    scripts/retrieval_tuning scripts/src/retrieval_tuning
(no output; exit 1 = zero hits)
```

Generator byte-identity after the move: `test_build_project_corpus.py` with
`AI_RACCOON_EVAL_COPY=/tmp/p1-live-copy.db` — `5 passed`, including the
committed-artifact equality half.

Deliberate non-moves: `candidateWindow` ladder points (`max3x100`/`max5x50` in
`matrix.py`/`tune.py` are tuning choices, not the default — the default comes
from JSON), `run_threshold_eval`'s ladder-like constants (its limit is sourced
from JSON), and `memwatch.DEFAULT_CAP_MB` (P2's own pinned guard).

---

## AC3 — `scripts/refresh-retrieval-corpora.py`

Thin wrapper; logic in `src/retrieval_tuning/refresh_corpora.py`. Injectable
registry (`CorpusSpec`) so the exit-code contract is fixture-testable. Exit
precedence: 2 snapshot mismatch > 1 anchor drift > 3 smoke regression > 0 clean.
Outputs the regenerated corpora plus a timestamp-free `refresh-report.json`.

**RED (two gates watched failing, then restored).**

```
# snapshot pin ignored -> mismatch test fails
FAILED tests/test_refresh_corpora.py::TestExitCodeContract::test_snapshot_mismatch_two_and_generator_not_run
# anchor verification disabled -> drift tests fail
FAILED tests/test_refresh_corpora.py::TestExitCodeContract::test_anchor_drift_one
FAILED tests/test_refresh_corpora.py::TestAnchorStyles::test_eval_style_reports_an_unresolved_source
```

**GREEN + pinned-copy run.**

```
$ python3 -m pytest scripts/tests/test_refresh_corpora.py -q
16 passed in 12.41s          # includes the pinned-copy gate

$ python3 scripts/refresh-retrieval-corpora.py --copy /tmp/p1-live-copy.db --out-dir /tmp/p3-refresh
OK: project-corpus-100.json (queries=100, committed-match=True)
SMOKE-REGRESSION: eval-set-100.json (queries=100, committed-match=False)
  - regenerated bytes differ from the committed artifact: the committed corpus is
    stale (or was generated from a different copy) — rerun against its pinned
    copy, or commit the regenerated artifact
refresh: smoke-regression (exit 3); report: /tmp/p3-refresh/refresh-report.json
WRAPPER EXIT=3

# sha evidence
project-corpus-100.json  regen=0a7ecb013364 committed=0a7ecb013364  matches
eval-set-100.json        regen=71390dff4d59 committed=15d715b9e193  differs
```

So the AC3 claim is exact for `project-corpus-100.json` (byte-for-byte on the
pinned copy, below the same gate as `test_build_project_corpus.py`) and the
report says why for `eval-set-100.json`: that committed artifact predates the
pinned copy and carries no `snapshotSha256` pin (bare list). See the amendment
proposal below.

Determinism: two runs on identical inputs produce byte-identical reports
(`test_report_is_byte_stable_for_the_same_inputs`), no timestamps anywhere.

---

## AC4 — behavior preservation

### Post-refactor eval, quiesced recipe (two runs)

Recipe per run: fresh `make_quiesced_scratch` base from `/tmp/p1-live-copy.db`
(both built to sha `eeb431a3…`, 56,457 rows), then from
`scripts/retrieval_tuning`:

```
python3 memwatch.py --cap-mb 12288 --log <run>/mem.log -- \
  python3 -m llamaindex_harness.evaluate --corpus corpora/project-corpus-100.json \
    --store-dir /tmp/p1-full-store --scratch-data-root <run> \
    --out <run>/results.json --offline
```

| run | harness hit/F1 | bank hit/F1 | MCC | contingency | stale | peak RSS |
|---|---|---|---|---|---|---|
| golden C (`results-f1.json`) | 0.6768 / 0.1582 | 0.8081 / 0.1874 | 0.5955 | 65/2/15/17 | [C035] | — |
| golden D (`results-f1-run2.json`) | 0.6768 / 0.1582 | 0.8081 / 0.1874 | 0.5955 | 65/2/15/17 | [C035] | — |
| post-refactor run 1 | 0.6768 / 0.1582 | 0.8081 / 0.1874 | 0.5955 | 65/2/15/17 | [C035] | 6865 MB |
| post-refactor run 2 | 0.6768 / 0.1582 | 0.8081 / 0.1874 | 0.5955 | 65/2/15/17 | [C035] | 6613 MB |

Diff (script in the transcript; exact on hashes/hits/contingency/MCC,
mean-F1 ±1e-9, `sessionId` + provenance pins allow-listed, `staleAnchors`
compared, `ingestedAt` absent):

```
run1 vs golden C : CLEAN: no differences under the P3 AC4 tolerance
run2 vs golden D : CLEAN: no differences under the P3 AC4 tolerance
run1 vs run2     : CLEAN
```

The provenance pins are not merely allow-listed — they are equal
(`modelRevision cb950dc8…`, `modelBytes 869254400`, `copyPath
/private/tmp/p1-live-copy.db`, `copySnapshotSha256 == corpusSnapshotSha256 ==
e0434a72…`, `excludedProjects`, `resolvedBuckets`); only `sessionId` differs
(golden `llamaindex-harness-dabcc31a29b7` vs run1 `…c19522b10be2`), which is
exactly what the allow-list exists for.

### Full `scripts/tests`

```
$ python3 -m pytest scripts/tests -q --continue-on-collection-errors
6 failed, 613 passed, 16 skipped, 1 warning, 1 error

# session-start baseline on the same tree (base 12a72dfb + docs only):
6 failed, 573 passed, 11 skipped, 1 warning, 1 error
```

The same 6 failures on both sides, with identical ids:

```
test_dependencies_declared.py::test_every_third_party_import_is_declared_in_pyproject
test_retrieval_tuning_eval_corpus.py::test_all_anchors_resolve_in_copy
test_retrieval_tuning_eval_corpus.py::test_expected_source_suffix_matches_at_least_one_source_file
test_retrieval_tuning_eval_corpus.py::test_generator_is_deterministic_and_matches_committed_corpus
test_retrieval_tuning_report.py::TestStudySummary::test_summary_reads_a_real_optuna_study
test_retrieval_tuning_report.py::TestStudySummary::test_unreadable_storage_yields_none
ERROR test_retrieval_tuning_tune.py            (optuna missing)
```

(+40 passed / +5 skipped vs baseline = the new AC gates, including the 5
opt-in parity tests skipped by default.) The 6 failures are pre-existing on
base `12a72dfb`: matplotlib undeclared, optuna missing, and the eval-corpus
tests pinned to the absent `/tmp/continue-testing-algorithm/…` copy.

### scripts-harness CI lane (simulated)

The lane installs only pytest+httpx and runs five files, so I ran the same five
with `chromadb`/`torch`/`llama_index`/`transformers` blocked at `sys.meta_path`
(`/tmp/p3-ci-lane-run.py`):

```
111 passed, 1 skipped in 0.32s
```

(the skip is the inherently-heavy leg-diagnostics test, guarded with
`pytest.importorskip("llama_index")`).

**Lane-repair note.** At the start of P3 the task branch's lane would have gone
RED: seven P2 evaluate tests imported `llamaindex_harness.ingest` for a
provenance monkeypatch, which needs chromadb/torch. Fixed in
`scripts/tests/test_llamaindex_harness_evaluate.py`:
`_run_main_with_fakes` now injects a fake `llamaindex_harness.ingest` module
(both the `sys.modules` entry and the parent-package attribute), so `main()`
finds the two provenance functions without the heavy import; the one test that
genuinely exercises real Chroma plumbing skips. Local dependency-full run:
`39 passed`. Verified against GitHub: the current `scripts-harness` job on
`main` is green (`gh run view 34463063286`: `scripts-harness: success`), and
this branch's version now runs the same five files green under the simulation.

The new stdlib-runnable gates (`test_retrieval_tuning_scopes.py`,
`test_no_hardcoded_knobs.py`, `test_refresh_corpora.py`) are **not** in the
workflow's five-file list; adding them is proposed as a plan amendment below
(`.github/` is outside this lane's file ownership).

### Old-vs-new CLI parity

`scripts/tests/test_p3_cli_parity.py` (opt-in `P3_CLI_PARITY=1`, base pinned):
extracts the 12a72dfb tree, runs the same arg matrix on both sides, diffs
exit/stdout/stderr and artifact bytes; expected values come from executing the
legacy code, never from reading it.

```
$ P3_CLI_PARITY=1 python3 -m pytest scripts/tests/test_p3_cli_parity.py -q
5 passed in 41.02s
```

Covered: ingest (`--verify-only` success, bad bucket, no-args), evaluate
(provenance refusal, repeat-mode usage error), report (same `--out` path,
markdown artifact bytes compared), slice_copy (output DB sha256 compared),
refresh-parity (legacy vs new `build_project_corpus`/`build_eval_corpus` bytes
on the pinned copy). RED proof — a deliberate stdout change in the new report
CLI:

```
E  AssertionError: report: stdout differs
E  - REPORT-PARITY-RED: … (6012 chars, n=1)
1 failed in 0.14s
```

---

## Duplication deliberately kept (with reasons)

| kept | where | reason |
|---|---|---|
| Python scope membership for the exclusion manifest | `build_project_corpus._embedded_scope_counts` | Classifies rows for a manifest count, not a predicate; routing it through SQL emitters would create a Python-scope table beside the SQL one. |
| bespoke probe spawn | `probe_sextant.start_server` | Different protocol (`--idle-timeout 0`, raw Popen + process-group kill); not on the eval path. |
| `memwatch.DEFAULT_CAP_MB` | `memwatch.py` | P2's own pinned guard parameter, not a retrieval knob. |
| two corpus generators | `build_project_corpus` / `build_eval_corpus` | Different contracts (seeded allocation + marker uniqueness vs ADR allowlist + section families); only anchors are shared. |
| threshold-eval runner | `run_threshold_eval.py` | Own newline-JSON-RPC arm protocol; only its uniform limit comes from `data/knobs.json`. |

## Plan amendments proposed

1. **`eval-set-100.json` provenance.** The refresh script cannot reproduce the
   committed eval corpus from the pinned copy: it predates the p1 copy and has
   no `snapshotSha256` pin, so the report exits 3 with the reason instead of
   pretending. Proposed: either regenerate/commit `eval-set-100.json` from the
   pinned copy in P4 (accepting threshold-plan baseline movement) or add a
   provenance header (snapshot sha) to the eval generator's artifact so the pin
   is explicit and refresh can distinguish "drifted source" from "stale
   artifact". Recommend the header; P4 decides.
2. **CI lane scope.** The new stdlib-runnable gates are not in the
   `scripts-harness` file list. Proposed: add
   `test_retrieval_tuning_scopes.py`, `test_no_hardcoded_knobs.py`,
   `test_refresh_corpora.py` to the job when `.github/` is next touched
   (cannot be done from this lane).
3. **Test-seam repair recorded.** The `_run_main_with_fakes` fake-module
   injection is a test-only change required to keep the CI lane green on the
   task branch; no production behavior moved.

## Evidence index

- Merge map: `docs/work/p3-merge-map.md`
- Eval runs: `/tmp/p3-eval-run1/{results.json,stdout.log,mem.log}`,
  `/tmp/p3-eval-run2/…`; golden diff script transcript in this session.
- Refresh output: `/tmp/p3-refresh/refresh-report.json`.
- Baseline suite log: `/tmp/p3-baseline-pytest.log`; final:
  `/tmp/p3-final2-pytest.log`.
