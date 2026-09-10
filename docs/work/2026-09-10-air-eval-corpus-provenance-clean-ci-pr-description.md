# PR: eval-set-100 provenance + clean-checkout test hygiene (harness follow-up C+E)

Task `air-eval-corpus-provenance-clean-ci` — packages **C + E** of
`docs/work/2026-09-10-harness-followups-definition.md` (the follow-up to the full-100 parity
eval, PR #633).

## C — `eval-set-100.json` provenance

- The corpus is now **header-shaped** (`generator`, `seed:42`, `queryCount:100`,
  `snapshotSha256: e0434a72…`) and **regenerated from the pinned bank copy**
  (`/tmp/p1-live-copy.db`); the 6 stale anchors now resolve (old→new table below — only
  `expectedHash` + `answerSpan` move; ids/texts/sources identical).
- `refresh-retrieval-corpora.py --copy /tmp/p1-live-copy.db --out-dir <tmp>` now exits **0**
  with both corpora `committed-match=True` (was exit 3). A wrong copy is still refused with
  **exit 2** (snapshot-mismatch), generator short-circuited.
- Both raw readers are header-aware: the harness test
  (`test_llamaindex_harness_retrieve.py`, which did `corpus[:20]`) and the C# golden-capture
  path (`MiniLmGoldenVectorTests.cs`, which did `doc.RootElement.EnumerateArray()`). The C#
  read is extracted into `MiniLmGoldenVectorReader` with its own two-shape test — pre-fix it
  throws `InvalidOperationException: … requires an element of type 'Array'` (RED pasted in
  the lane report).

| id | old `expectedHash` | new `expectedHash` |
|---|---|---|
| E040 | `4b4c9327962cdf16…` | `5bf7f0c913c3ee30…` |
| E041 | `a8dc353473e76f52…` | `ed214b8bf91b5390…` |
| E042 | `024480c019dddb28…` | `79141fdc52622fa7…` |
| E045 | `e3b89183bc7c41af…` | `3bbd47d7cbe50b55…` |
| E063 | `d91dfe96c5057d1d…` | `8580b2d6968eb13d…` |
| E065 | `07ea05a5f58f7c4f…` | `de2eb16eb9b2d48d…` |

Full 64-hex table + RED pastes: `docs/work/2026-09-10-air-eval-corpus-provenance-clean-ci-lane-report.md`.

## E — clean-checkout test hygiene

- `matplotlib` declared (it is a genuine AST-detected import of
  `scripts/generate-embedding-benchmark-report.py`); **optuna stays declared** — its tests now
  `importorskip` (env guard, not an extras reclassification) and skip with a reason.
- Copy-dependent corpus tests **skip with reasons** instead of failing (`AI_RACCOON_EVAL_COPY`
  absent or not the pinned sha); the deleted-fixture committee gate is extracted with a
  `skipif`.
- The `scripts-harness` CI lane gains `test_dependencies_declared.py` and
  `test_retrieval_tuning_eval_corpus.py` (both stdlib-runnable; copy-dependent tests skip).

## Evidence

- Full `scripts/tests`: **629 passed, 25 skipped, 0 failed** (before: 8 failed + 1 collection
  error); every skip line names a reason.
- `P3_CLI_PARITY=1 … test_p3_cli_parity.py` → **5 passed** (this PR also normalizes that
  gate to the query payload — byte identity was impossible once the eval corpus gained a
  header; project-corpus byte identity kept).
- `dotnet build` 0 warnings / 0 errors; the reader tests 3/3.
- All four frozen goldens byte-identical to base.

## Notes for reviewers

- **CI side effect**: editing `.github/workflows/build.yml` flips the workflow's `CODE_REGEX`
  (`code=true`), so the dotnet lanes (fast/bdd/slow) run on this PR despite no production C#
  change beyond the reader helper. Cost only.
- **Copy-gated byte equality**: the corpus byte-equality check skips on a runner with no
  pinned copy, so CI enforces the **header shape**; byte equality is a documented local gate:
  `python3 scripts/refresh-retrieval-corpora.py --copy /tmp/p1-live-copy.db --out-dir "$(mktemp -d)"` → exit 0.
- **The no-silent-rebaseline check is a manual gate**: base-vs-new diff
  (`git show "$(git merge-base HEAD origin/main)":scripts/retrieval_tuning/corpora/eval-set-100.json`
  vs the tree) — executed and pasted in the lane report; not automated.
- Pre-existing, not introduced here: `uv.lock` does not list `matplotlib` (nor the pinned
  harness deps), and no CI job runs `uv`.
