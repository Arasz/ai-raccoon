### F63 — CORRECTED
**Method:** `curl` flat-container 404/200 probes; python set-diff over `git tag -l 'v*'`, `gh release list --limit 200` and both `ai-raccoon`/`arasz.ai-raccoon` index.json; `gh api .../runs/34868224773` + jobs; 110-run publish history with sha→VERSION (`git show`); read `.github/workflows/release.yml` and `publish.yml` in full.
**Result:** Mechanism re-derived: `release.yml` tags+releases on a VERSION push; `publish.yml` is a separate manual dispatch packing the default-branch tip with `--skip-duplicate`; run 34868224773 (VERSION 1.42.4, `f0ca4907`) had pack(linux-x64) cancelled, publish skipped, `run_attempt=1`, and the next dispatch packed 1.42.5 (`8a1f1dde`) — 1.42.4 was never retried and neither workflow checks the other's half.
Replacement claim for "four": 21 of 101 GitHub releases have no `ai-raccoon` package on nuget.org (of which 15 have no package under either package id; six 1.0.x versions live under the former `arasz.ai-raccoon`); the lane's own `tags.txt` holds only the last 20 tags, which is exactly how it derived `v1.42.4, v1.41.1, v1.38.0, v1.34.4`.
Falsifying evidence for "a later dispatch under the same VERSION would have silently no-op'd": the observed next dispatch (34869842192, 16:38:13Z) packed main's then-current 1.42.5 and succeeded — the 1.42.4 pack was abandoned, not no-op'd. Four missing versions had a cancelled dispatch (1.42.4, 1.34.4, 1.33.6, 1.31.2), all never retried; the other missing versions have no publish run at all. Severity MEDIUM stands.

### F64 — CORRECTED
**Method:** `gh run list --workflow=build.yml --limit 500` + per-run jobs API scan (all 500 fetched); job log of run 34351895815; `gh pr view 627/629`; `git merge-base --is-ancestor 759fa2da HEAD`; `grep -rn "schedule\|cron" .github/workflows/`; `git log --diff-filter=D -- .github/workflows/nightly.yml`; read `.github/labeler.yml`.
**Result:** Core re-derived: the only Nightly runner is the opt-in label/dispatch job and the labeler never applies `run-nightly-gates`; last non-skipped Nightly is 2026-09-09T12:35:22Z (run 34351895815, `failure`, the run's only red job) with the failing test string exactly as quoted; no non-skipped Nightly since (scan reaches 2026-09-22T10:12Z); no `schedule:`/cron in any of the 4 workflows; `nightly.yml` deleted by `7e141c59`; `Performance=Benchmark` has no backstop (`build.yml:138`).
Correction 1: today's last-500 scan yields **14** non-skipped (5 success / 2 failure / 7 cancelled), not 12 — the two extra are 2026-08-23T05:43Z (dispatch, failure) and 07:44Z (PR, cancelled).
Correction 2: the fix was not "20 minutes after the nightly run" — the red run tested PR #627's head `61dd8d53`; #627 merged 14:46:22Z and fix PR #629 (`759fa2da`) merged 15:09:08Z, i.e. ~2h34m after the red run and ~23 min after #627. Severity MEDIUM stands.

### F65 — CONFIRMED
**Method:** extracted `CODE_REGEX` from `.github/workflows/build.yml:75` and ran `grep -qE` over single-path diffs; `git log -2 -- global.json` with `git show --name-only`; `git ls-files benchmarks`; read `AiRaccoon.slnx` and `benchmarks/.../AiRaccoon.Benchmarks.csproj`.
**Result:** `global.json → code=false`; `benchmarks/AiRaccoon.Benchmarks/Program.cs → code=false` (all 21 non-build benchmark .cs files are equally unmatched; Program.cs is a real path); controls `src/AiRaccoon/Program.cs`, `SpeedGateCoverageTests.cs`, `VERSION`, `nuget.config → code=true`; `changes.code` is what gates build-fast/bdd/slow/nightly, so all dotnet lanes skip.
`global.json`'s only two commits (`dfd9c0a5` #589, `7bf58f8c` #342) both also touched CODE_REGEX-matched paths, and benchmark .cs changes are compiled by no lane without them.
Nuance (not load-bearing): the benchmark `.csproj` itself matches `\.csproj$`, so the exact unguarded class is benchmark *source* files, not the csproj. Severity MEDIUM stands.

### F66 — CORRECTED
**Method:** parsed `.github/workflows/build.yml:275` (14 files) vs `ls scripts/tests/*.py` (51); venv via `python3 -m venv` + `pip install pytest httpx` (pytest 9.1.1 / httpx 0.28.1) running the 37 excluded files with `--continue-on-collection-errors`; full suite with system python (chromadb 1.5.9) on branch main; read the `scripts-harness` comment and `test_threshold_eval_integration.py:1036-1054`.
**Result:** Core re-derived: 14 of 51 test files are named in CI; the 37 excluded files run under exactly the CI dependency set and produce **exactly 401 passed** with **exactly 2 chromadb collection errors** (ingest, retrieve); the named guards are green — verify_history_scrubbed 5, package_verify 17, tool_shell 15, download 7, coredump 5, nightly_triage 14 passes.
Corrections: 20 skipped (not 19) and 1 test fails under that set — `test_llamaindex_harness_cli.py::test_ingest_runs_as_module` (also chromadb) — that file is one of the 4 the CI comment already documents, so "35 that ran green" is 34 fully green of 35 collected; files excluded and named by no gate = **33** (51−14−4), not 31.
Repro note: pytest 9.1.1 aborts the whole run on the 2 collection errors unless `--continue-on-collection-errors` is passed; the full-suite half corroborates the lane artifact (637 passed / 27 skipped / 0 failed on main, and the merge-hygiene test skips on main per :1036-1039). Severity MEDIUM stands.

### F67 — CONFIRMED
**Method:** read `.github/workflows/build.yml:92-146` (build-fast), `:219-243` (build-slow), `:148-163` (build-bdd); `grep -n "upload-artifact\|dumps"` across workflows; rebuilt a crashing `net10.0` console app in `/tmp/g6-f67` (`throw new InvalidOperationException`) and ran it under `DOTNET_DbgEnableMiniDump=1 DOTNET_DbgMiniDumpType=2 DOTNET_DbgMiniDumpName=…` with a missing and an existing directory.
**Result:** build-fast names `${{ github.workspace }}/dumps/coredump.%p.dmp` (`build.yml:103`) with no `mkdir -p dumps` step and no upload/triage step; `upload-artifact` exists only in build-slow (`build.yml:240`) and publish.yml:81; build-bdd has no dump config at all.
Missing-dir run: `[createdump] Could not create output file '…/nodir/coredump.40928.dmp': No such file or directory (2)`, exit 134, no file written and the directory is not created; existing-dir control: `Dump successfully written`, 284 MB (their 314 MB / 644 ms — size and timing vary by run).
No step in build-fast creates `dumps/`, so a fast-lane host crash still leaves no evidence. Severity MEDIUM stands.

## Verdict tally
- CONFIRMED: 2 (F65, F67)
- CORRECTED: 3 (F63, F64, F66)
- REFUTED: 0

## Still open
- **F63:** the reason the non-cancelled missing versions have no dispatch (never attempted vs. attempted from another ref) is not visible in run metadata; two archived publish runs could not have their VERSION sha-resolved locally (one predates the `VERSION` file, one API-resolved to 1.34.1, which is present on nuget), but neither can cover a missing post-`VERSION` release.
- **F64:** why the lane's own last-500 scan counted 12 rather than 14 is unexplained; I could not reproduce a 12 count from the same API window (the two extra runs are 2026-08-23 dispatch/PR runs).
- **F66:** full-suite timing/skips are environment-dependent (22s / 27 skips here vs. 53.03s / 26 skips in the lane); I corroborated the detached-worktree failure only indirectly (skip-on-main branch condition), not by re-running off a detached HEAD.
- **F67:** measured on darwin-arm64 (this machine); the CI target (ubuntu-latest) was not re-measured, though the failing `createdump` logic (output directory must pre-exist) is runtime-level and the observed message is the process's own.
