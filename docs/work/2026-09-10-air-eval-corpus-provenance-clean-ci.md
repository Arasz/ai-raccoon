# Plan: air-eval-corpus-provenance-clean-ci (low effort)

Task: packages **C** (`eval-set-100.json` provenance) and **E** (test hygiene) of
`docs/work/2026-09-10-harness-followups-definition.md:46-66`. Packages B/A1/D/F/G are out of
scope. One branch, one PR, C and E serialised in one lane (they share a test file).
Top-level gate: every `- [ ]` below checked with its named gate **run on the final branch
head**, RED output pasted before GREEN for every new or changed check. Frozen contract:
`docs/work/results-f1*.json` and harness behavior do not change.

## Research findings (file:line evidence)

**The artifact and the pin.**
- `scripts/retrieval_tuning/corpora/eval-set-100.json:1` is a bare JSON array — no
  `header`, so no `snapshotSha256`. `scripts/retrieval_tuning/corpora/project-corpus-100.json:645`
  carries the pin (`e0434a72…`) the refresh contract reads.
- The pinned copy `/tmp/p1-live-copy.db` hashes to `e0434a7214ac4caf1dbbef56147f582515bd0f5ebda665ad8cb06687296a55f6`
  (measured `shasum -a 256`); it is the default copy of the refresh gate
  (`scripts/tests/test_refresh_corpora.py:25`) and the P3 parity gate
  (`scripts/tests/test_p3_cli_parity.py:31`).
- `scripts/src/retrieval_tuning/refresh_corpora.py:53-61` (`snapshot_sha_of`) returns `None`
  for the eval corpus (bare list) → no pin check; `:162-170` byte-compares
  regenerated vs committed and reports `smoke-regression` (exit 3).
- **Measured** `python3 scripts/refresh-retrieval-corpora.py --copy /tmp/p1-live-copy.db --out-dir /tmp/refresh-probe`:
  `OK: project-corpus-100.json (queries=100, committed-match=True)` +
  `SMOKE-REGRESSION: eval-set-100.json (queries=100, committed-match=False)`, exit 3
  (same as `docs/work/2026-09-10-p3-lane-report.md:159-168`).
- **Regeneration succeeds.** Fresh generation yields 100 queries; exactly **6** entries
  differ from the committed artifact — `expectedHash` and `answerSpan` only, all other
  fields (id, query text, expectedSource, scope, project) identical:
  E040, E041, E042 (0046-project-membership-has-one-definition.md context/decision/consequences),
  E045 (0048…#consequences), E063 (0071…#consequences), E065 (0072…#decision).
  Committed sha `15d715b9e193`, regenerated sha `71390dff4d59`. Those 6 committed hashes
  resolve to 0 rows in the copy; each regenerated hash resolves to exactly one row
  (the refresh run reported no anchor-drift).
- The 6 old→new hash pairs (for the PR description; recompute in the implementation run):
  E040 `4b4c9327…→5bf7f0c9…`, E041 `a8dc3534…→ed214b8b…`, E042 `024480c0…→79141fdc…`,
  E045 `e3b89183…→3bbd47d7…`, E063 `d91dfe96…→8580b2d6…`, E065 `07ea05a5…→de2eb16e…`.

**The frozen goldens are a different corpus.** `docs/work/results-f1.json` (and run2/repeats/
merged-repeats) carry `corpusSnapshotSha256: e0434a72…` = the *project* corpus pin
(`project-corpus-100.json:645`); every P2/P3/P4 eval command runs
`--corpus corpora/project-corpus-100.json` (`docs/work/2026-09-10-p4-lane-report.md:37`,
`docs/work/2026-09-10-p2-lane-report.md:82`, `docs/work/2026-09-10-p3-lane-report.md:192`).
Regenerating `eval-set-100.json` cannot move them.

**Generator.** `scripts/retrieval_tuning/build_eval_corpus.py:426` `generate()` returns a
`list[dict]`; `:498-501` writes it as a bare array; `:511` prints the count;
`:41` default copy. `build_project_corpus.py:519-530` is the header convention to mirror
(`generator` / `seed` / `queryCount` / `snapshotSha256`, `sort_keys`, no timestamps).

**Consumers already handle a header** — one loader, `scopes.load_corpus`
(`scripts/src/retrieval_tuning/scopes.py:57-73`), returns `(header, entries)` and accepts
both shapes; `corpus.load_corpus:29-34` wraps it; header-aware call sites:
`llamaindex_harness/evaluate.py:626-628`, `run_threshold_eval.py:108-121` (tolerated/ignored),
`ingest.py:615/667`, `slice_copy.py:36`, `tune.py:215`, `report.py:538` (`:535` is the
default eval path). The raw reads are TWO, not one (reviewer F1):
`scripts/tests/test_llamaindex_harness_retrieve.py:126-129` (`for entry in corpus[:20]` —
breaks on a dict) and `tests/AiRaccoon.Tests/Integration/Embedding/MiniLmGoldenVectorTests.cs:167-172`
(the env-gated capture path's `doc.RootElement.EnumerateArray()` — throws
`InvalidOperationException` on the header shape; CI stays green only because the capture test
skips without `AIRACCOON_GOLDEN_CAPTURE=1`). Both are in scope (C4/C6).

**Measured failures on this checkout.**
`python3 -m pytest scripts/tests -q --ignore=scripts/tests/test_retrieval_tuning_tune.py`
→ **8 failed, 623 passed, 18 skipped**; plus `test_retrieval_tuning_tune.py` collection error
(module-level `import optuna`, `:13`).
- `test_dependencies_declared.py::test_every_third_party_import_is_declared_in_pyproject`:
  `matplotlib` used at `scripts/generate-embedding-benchmark-report.py:17-20`, absent from
  `pyproject.toml:8-24` (optuna **is** declared, `pyproject.toml:13`).
- `test_retrieval_tuning_report.py::TestStudySummary::test_summary_reads_a_real_optuna_study:309`
  and `test_unreadable_storage_yields_none:332` fail because `report.study_summary` imports
  optuna at `scripts/src/retrieval_tuning/report.py:310` and the env lacks it; `tune.py:36`
  imports optuna at module scope (a genuine harness dependency, already declared).
- `test_retrieval_tuning_eval_corpus.py`: **3 failures** without `AI_RACCOON_EVAL_COPY`
  (2 anchor tests + the determinism test) — the file hardcodes the default copy path
  (`:27-32`) and `_copy_conn` asserts existence (`:80-84`); **2 failures** with the pinned copy
  (`test_all_anchors_resolve_in_copy`, `test_generator_is_deterministic_and_matches_committed_corpus`).
- `test_committee_grade.py::test_sampling:404` → `FileNotFoundError` for
  `docs/work/threshold-committee-eval/fixtures/metrics-fixture.json` (`:35-42`); the whole
  artifact tree was deleted from main by `af1c2482` (`git cat-file -e
  origin/main:docs/work/threshold-committee-eval/fixtures/metrics-fixture.json` → absent).
  Pre-existing main failure, not named in E but it blocks E's "no failures" AC.
- `test_threshold_eval_integration.py::test_merge_hygiene_pr_branch_has_empty_src_diff:1031`
  fails only while the branch has no non-src diff; it passes on the final head (our changes
  are `scripts/`, `data/`, `pyproject.toml`, `.github/`).

**Env-gating convention to copy.** `scripts/tests/test_build_project_corpus.py:44-49`
(COPY_PATH = `AI_RACCOON_EVAL_COPY` or default), `:106-121` `_snapshot_mismatch_reason()`,
`:315` / `:433` / `:549` `pytest.skip(f"… not checkable: {reason}")`. `pytest.importorskip`
idiom: `test_llamaindex_harness_evaluate.py:521`, `test_llamaindex_harness_slice.py:128,197`.

**CI lane.** `.github/workflows/build.yml:246-272` — scripts-harness installs only
`pytest httpx` (`:266-269`) and runs the stdlib subset incl. the P3/F1/F3 gates (`:272`), but
**not** `test_dependencies_declared.py` or `test_retrieval_tuning_eval_corpus.py`. Editing
`build.yml` itself matches `CODE_REGEX` (`:75`), so this PR flips `code=true` and the dotnet
lanes actually run (see risks).

## Decision C — regenerate from the pinned copy **and** commit a provenance header (not retire)

Chosen: the definition's own option 1 (`…definition.md:49-52`). Evidence:

1. **It makes the tests meaningful.** Regeneration is known-good: 100 queries, all anchors
   resolve, exactly 6 stale entries repaired. Retiring would delete the artifact the tests
   exist to check and force rewriting/deleting `test_retrieval_tuning_eval_corpus.py` (10
   tests), `test_refresh_corpora.py::TestRegistry`, and the default corpus paths in `tune.py`
   and `report.py`.
2. **The header is the only way the refresh contract can pass.** Without it the pin check
   (`refresh_corpora.py:53-61`) is dead for this corpus; with it, a wrong copy exits 2
   (deliberate provenance change) instead of silently regenerating an unreviewed baseline.
   The P3 lane report's own recommendation is the header (`…p3-lane-report.md:317-323`).
3. **The frozen goldens are untouched** (project-corpus based, see above), so C carries no
   frozen-contract risk.
4. **Package B depends on C.** B's scope is "Improve the corpus generator … regenerate the
   corpus with a snapshot pin" (`…definition.md:40-43`) — retiring kills B's instrument; the
   header is B's interface (B may extend header keys; consumers must use subset checks).

Header shape (mirror `build_project_corpus.py:519-526`, deterministic, no timestamps):
`{"header": {"generator": "build_eval_corpus.py", "seed": 42, "queryCount": 100,
"snapshotSha256": "<copy sha>"}, "queries": [...]}`, same `json.dump(indent=2,
sort_keys=True, ensure_ascii=False)` + trailing newline as today
(`build_eval_corpus.py:498-501`), so refresh byte-equality holds.

## C: what can change the corpus content — and the anti-silent-rebaseline guards

| What | This regeneration (measured) | Guard against silent drift |
|---|---|---|
| Query count | 100 → 100 | refresh `expected_count=100` (`refresh_corpora.py:202-206`); `test_corpus_exists_and_is_exactly_100`; header `queryCount` |
| Query text / ids | identical (0 differ) | `test_no_duplicate_query_text`, `test_split_is_75_adr_plus_25_non_file`, byte-equality test |
| expectedSource | identical | anchor-resolution tests |
| expectedHash / answerSpan | **6 change** (E040/E041/E042/E045/E063/E065) | header pin + `matchesCommitted` byte check (exit 3) + `test_generator_is_deterministic_and_matches_committed_corpus` + the PR must paste the old→new table |
| Copy identity | pin now `e0434a72…` | refresh exits 2 on any other copy; new `test_committed_corpora_carry_snapshot_pins` |

Prevention summary: (a) `snapshotSha256` makes the source copy a reviewable diff line;
(b) because the copy is unchanged at `e0434a72…`, *any* generator or artifact drift fails
the byte-equality check rather than silently re-baselining; (c) a future deliberate re-pin
must change the header + regenerate + pass `refresh` exit 0 — all visible in one diff.
Note for users: eval-set-100 metrics from before C are not comparable to after (6 anchors
move from unhittable to hittable); the frozen `results-f1*.json` numbers are unaffected.

## Files to touch

P1 (C):
1. `scripts/retrieval_tuning/build_eval_corpus.py` — emit + return the header dict; update
   `main` print; docstring one-liner. (No `refresh_corpora.py` change needed: it already
   accepts dict outputs via `_queries_of:63-64` and pins via `snapshot_sha_of:53-61`.)
2. `scripts/retrieval_tuning/corpora/eval-set-100.json` — regenerated artifact (header + 6
   repaired anchors), produced by the generator, not hand-edited.
3. `scripts/tests/test_retrieval_tuning_eval_corpus.py` — header-aware `_load_corpus`;
   new pin/header test; (E1's skip guard also here).
4. `scripts/tests/test_refresh_corpora.py` — pinned-copy test asserts **exit 0 for both**
   corpora; new committed-pin test.
5b. `tests/AiRaccoon.Tests/Integration/Embedding/MiniLmGoldenVectorTests.cs:167-172` — the
   capture path's raw reader enumerates the JSON root as an array; read shape-aware
   (`GetProperty("queries")` when the root is an object), mirroring `evaluate.py:626-628`;
   extract the JSON→array read into a small static helper so it is unit-testable.
5c. `tests/AiRaccoon.Tests/Integration/Embedding/MiniLmGoldenVectorReaderTests.cs` (new) —
   the helper's both-shapes test (array + `{header,queries}`).

P2 (E):
6. `pyproject.toml:13` — add `"matplotlib"` (floating, like its peer tooling deps; not a
   harness pin).
7. `scripts/tests/test_retrieval_tuning_tune.py:13` — `optuna = pytest.importorskip("optuna")`
   before the `TPESampler` import.
8. `scripts/tests/test_retrieval_tuning_report.py:309,332` — `pytest.importorskip("optuna")`
   in both `TestStudySummary` tests (optuna stays declared; the harness genuinely needs it at
   `tune.py:36` — the skip is an environment guard, not an extras reclassification).
9. `scripts/tests/test_committee_grade.py:497-505` — extract the committed-fixture block into
   a separate `test_committed_metrics_fixture_contract` with
   `@pytest.mark.skipif(not FIXTURE_METRICS.exists(), reason=…)` (fixture deleted on main).
10. `.github/workflows/build.yml:272` — append `scripts/tests/test_dependencies_declared.py`
    and `scripts/tests/test_retrieval_tuning_eval_corpus.py` to the lane command (both
    stdlib-runnable; copy-dependent tests skip in CI).

## TDD test list (designed before code; each row names the failure mode it targets)

| # | Test (file::name) | Failure mode | RED today (measured) | Mutation that proves it can fail |
|---|---|---|---|---|
| 1 | `test_retrieval_tuning_eval_corpus.py::test_committed_corpus_carries_a_snapshot_pin` (new) | committed artifact has no pin → refresh pin dead | header is `None` today | delete `header` from artifact → RED |
| 2 | `test_retrieval_tuning_eval_corpus.py::test_generator_is_deterministic_and_matches_committed_corpus` (gate updated) | committed artifact drifts from pinned-copy regeneration | with copy: `15d715b9e193 != 71390dff4d59` | edit any hash in artifact → RED |
| 3 | anchor tests `:192,:231` (gate updated) | stale anchors (6 ids) | with copy: 6 hashes resolve to 0 rows | revert artifact → RED |
| 4 | `test_refresh_corpora.py::TestRegistry::test_committed_corpora_carry_snapshot_pins` (new) | eval pin silently `None` again | `snapshot_sha_of(eval) is None` today | strip header → RED |
| 5 | `test_refresh_corpora.py::test_pinned_copy_reproduces_every_committed_corpus` (extended) | refresh not clean for every committed corpus | exit 3, eval `matchesCommitted=False` | revert artifact or generator → exit 3 |
| 6 | `test_retrieval_tuning_eval_corpus.py` copy tests (E1 gates) | fail (not skip) when copy absent/wrong | 3 failed with no env | point env at absent path → skip, not fail |
| 7 | `test_dependencies_declared.py` (existing gate) | undeclared third-party import | 1 failed (matplotlib) | remove matplotlib from pyproject → RED |
| 8 | `test_retrieval_tuning_tune.py` / `test_retrieval_tuning_report.py` skip gates | collection/test failure when optuna absent | 1 collection error + 2 failed | uninstall optuna → skip (reason), not error |
| 9 | `test_committee_grade.py::test_committed_metrics_fixture_contract` (extracted, skipif) | hard failure on deleted fixture | `FileNotFoundError` | restore fixture → test executes |
| 10 | `MiniLmGoldenVectorReaderTests` (new, C#) | the capture path throws on the header shape | the reader is array-only today | feed the object-shaped JSON → RED before the fix |

## Parallelism and serialisation

- **One lane, sequential packages.** P1 and E1 share `test_retrieval_tuning_eval_corpus.py`;
  E1's snapshot guard needs the header P1 adds. E2 files are disjoint from P1, but the
  branch/test-run surface is small enough that a second lane buys nothing.
- Implement in order: P1 generator+artifact+tests → E1 in the same test file → E2 files →
  workflow → P3 gates on the final head. Commit per coherent step (small commits).
- Do not run the full `scripts/tests` before the branch has commits: the merge-hygiene test
  is branch-state-dependent.
- Integration with later package B: B re-generates under the same contract; if B deliberately
  changes the copy, the header re-pin is the reviewable change and `refresh` must exit 0.
- Heavy local gates (`ingest`/`retrieve`/`cli`) are not touched by C/E.

---

**P1 `eval-set-100.json` provenance — regenerate from the pinned copy with a snapshot header (Package C)**

- [x] C1 — `build_eval_corpus.generate()` writes and returns the header-shaped corpus
  (`{header:{generator:build_eval_corpus.py, seed:42, queryCount:100, snapshotSha256:<copy sha>},
  queries:[…]}`); the committed `eval-set-100.json` is regenerated from `/tmp/p1-live-copy.db`
  and the 6 anchors E040/E041/E042/E045/E063/E065 now resolve in the copy.
  **Gate:** the CLI run is SETUP, not a check — it rewrites the artifact in place and exits 0
  before and after C (reviewer F4); run it only with `--output "$(mktemp -d)/eval.json"` when a
  comparison is intended. The content proof is test #2 (byte-equality vs a fresh pinned-copy
  generation) plus C2's `--out-dir` compare:
  `AI_RACCOON_EVAL_COPY=/tmp/p1-live-copy.db python3 -m pytest
  scripts/tests/test_retrieval_tuning_eval_corpus.py -q` → 0 failed, 0 skipped;
  header/query checks pinned by new test #1 (`test_committed_corpus_carries_a_snapshot_pin`).
- [x] C2 — `refresh-retrieval-corpora.py` exits **0 for every committed corpus** from the
  pinned copy; both records `committed-match=True`. **Gate:**
  `python3 scripts/refresh-retrieval-corpora.py --copy /tmp/p1-live-copy.db --out-dir "$(mktemp -d)"`
  → exit 0, both `OK … committed-match=True`; `AI_RACCOON_EVAL_COPY=/tmp/p1-live-copy.db
  python3 -m pytest scripts/tests/test_refresh_corpora.py -q` → all pass (updated test #5).
- [x] C3 — the eval pin is **active**, not decorative: a different copy sha is refused with
  exit 2 (snapshot-mismatch), never silently regenerated. The new pin tests are
  COPY-INDEPENDENT (reviewer F3): they assert presence + 64-hex `snapshotSha256` +
  `generator`/`seed`/`queryCount` shape on the committed artifact — copy-sha equality belongs
  to test #5 (pinned-copy); otherwise the tests they join the CI lane with would FAIL, not
  skip, on a runner with no copy. **Gate:** new test #4
  (`test_committed_corpora_carry_snapshot_pins`) plus the existing mismatch test; optional CLI
  probe `python3 -c "from retrieval_tuning import refresh_corpora as r; …copy_sha='0'*64…"`
  printing `exitCode == 2`.
- [x] C4 — the corpus tests pass **without the local-copy workaround**: with no
  `AI_RACCOON_EVAL_COPY`, the copy-dependent tests skip with a precise reason (E1), and the
  pure corpus gates run against the committed artifact. **Gate:**
  `python3 -m pytest scripts/tests/test_retrieval_tuning_eval_corpus.py -q` (env unset) →
  0 failed, 3 skipped with reasons; `test_llamaindex_harness_retrieve.py` corpus slice test
  green (header-aware read). **Gate:** `python3 -m pytest
  scripts/tests/test_llamaindex_harness_retrieve.py -q` (heavy local gate) → 0 failed.
- [x] C5 — no silent re-baseline and no frozen-contract change: generation moves query
  text/ids **not at all** and hashes/spans on exactly the 6 named ids; the PR description
  carries the old→new table. **Gate:** BASE-vs-NEW diff (reviewer F2 — the
  committed-vs-regenerated probe is vacuous post-C by construction):
  `git show "$(git merge-base HEAD origin/main)":scripts/retrieval_tuning/corpora/eval-set-100.json`
  vs the working tree → the header appears and the only entry fields that differ are
  `answerSpan`+`expectedHash`, exactly on `[E040,E041,E042,E045,E063,E065]` (RED-capable on the
  final head); `git diff --exit-code <base> -- docs/work/results-f1.json docs/work/results-f1-run2.json
  docs/work/results-f1-repeats.json docs/work/results-f1-merged-repeats.json` prints nothing;
  `python3 -m pytest scripts/tests/test_diff_golden.py -q` → pass.
- [x] C6 — the second raw consumer survives the header: `MiniLmGoldenVectorTests.cs`'s capture
  path reads the corpus shape-aware (array OR `{header,queries}`), mirroring
  `evaluate.py:626-628`; the read is extracted into a helper unit-tested on both shapes.
  **Gate:** `dotnet build` clean + the new helper test RED on the object-shaped input before
  the fix (paste the throw) and green after; the capture command itself stays env-gated
  (heavy — not run).

**P2 test hygiene — clean-checkout zero failures (Package E)**

- [x] E1 — copy-dependent tests skip, never fail, when the copy is absent or not the pinned
  snapshot; `_load_corpus` accepts the header shape (mirrors
  `test_build_project_corpus.py:106-121`/`:433`). **Gate:**
  `python3 -m pytest scripts/tests/test_retrieval_tuning_eval_corpus.py -q` (no env) →
  0 failed, 3 skipped; with `AI_RACCOON_EVAL_COPY=/tmp/p1-live-copy.db` → 0 failed, 0 skipped;
  point the env at a wrong-sha copy → skip with the `snapshotSha256` mismatch reason.
- [x] E2 — `matplotlib` is declared where the static gate demands it (a genuine import of
  committed code at `generate-embedding-benchmark-report.py:17-20`; gating cannot hide an AST
  import). **Gate:** `python3 -m pytest scripts/tests/test_dependencies_declared.py -q` →
  2 passed (RED today: 1 failed listing matplotlib).
- [x] E3 — optuna-dependent tests skip cleanly when optuna is absent; optuna stays a declared
  main dependency (`pyproject.toml:13`, used at `tune.py:36`). **Gate:**
  `python3 -m pytest scripts/tests/test_retrieval_tuning_report.py scripts/tests/test_retrieval_tuning_tune.py -q`
  → 0 failed, 2 tests + 1 module skipped with reasons (RED today: 2 failed + 1 collection
  error); with optuna installed → 26 + 13 tests run.
- [x] E4 — full `scripts/tests` on a clean checkout reports **no failures (skips only, with
  reasons)**; includes the `test_committee_grade::test_sampling` fixture guard (pre-existing
  main failure caused by the `af1c2482` artifact deletion; a 3-line test-only extraction —
  beyond E1/E2 but required by this AC, flagged in the report). **Gate:**
  `python3 -m pytest scripts/tests -q` on the branch head → 0 failed; every skip line names a
  reason (`no copy`, `optuna not installed`, `fixture missing`, …).
- [x] E5 — the CI lane mirrors the fix: `test_dependencies_declared.py` and
  `test_retrieval_tuning_eval_corpus.py` join the scripts-harness file list. **Gate:** local
  run of the exact lane command (`python3 -m pytest scripts/tests/test_llamaindex_harness_fts.py
  … scripts/tests/test_dependencies_declared.py scripts/tests/test_retrieval_tuning_eval_corpus.py -q`)
  → 0 failed (copy-dependent tests skip); CI `scripts-harness` green on the PR.

**P3 Integration (always last)**

- [x] I1 — full suite on the final branch head: `python3 -m pytest scripts/tests -q` →
  **0 failed**, skips only; the branch-state-dependent
  `test_threshold_eval_integration.py::test_merge_hygiene_pr_branch_has_empty_src_diff` passes
  because the branch changes `scripts/`, `data/`, `pyproject.toml`, `.github/` (top-level
  `src/` untouched). **Gate:** that command, output pasted.
- [x] I2 — refresh + corpus contract green together on the merged head:
  `python3 scripts/refresh-retrieval-corpora.py --copy /tmp/p1-live-copy.db --out-dir "$(mktemp -d)"`
  → exit 0 **and** `AI_RACCOON_EVAL_COPY=/tmp/p1-live-copy.db python3 -m pytest
  scripts/tests/test_refresh_corpora.py scripts/tests/test_retrieval_tuning_eval_corpus.py -q`
  → 0 failed. **Gate:** both commands.
- [x] I3 — frozen contract and harness behavior unchanged: goldens byte-identical to base,
  `test_diff_golden.py` + `test_collect_ac_evidence.py` green, no file under top-level `src/`
  or `scripts/src/retrieval_tuning/` changed beyond the generator's output shape (the one
  header-aware read fix is test-side). **Gate:**
  `git diff --name-only "$(git merge-base HEAD origin/main)"..HEAD` reviewed; goldens diff
  empty; the two test files green.
- [x] I4 — traceability: one PR from this branch, commits per package, PR description carries
  the 6-entry old→new hash table, the RED outputs for tests #1–#9, and the refresh exit-0
  output. **Gate:** PR link + description read-back. Note the side effect that editing
  `build.yml` flips the dotnet lanes to `code=true` for this PR (full build/test will run
  despite no C# change).
