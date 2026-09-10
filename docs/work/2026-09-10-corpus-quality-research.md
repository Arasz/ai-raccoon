# Research record — corpus quality (harness follow-up Package B)

Date: 2026-09-10 · Task: `air-corpus-quality-clean-instrument` · Status: research complete,
planning dispatched.
Scope source: `docs/work/2026-09-10-harness-followups-definition.md` Package B:

> **Problem.** 23/99 queries are markup/JSON debris (e.g. `How is { "extends handled?`),
> because the generator takes the first clause of a chunk value. Relevance-flavoured
> readings are composition-sensitive: clean n=76 → harness 0.697 / bank 0.842; debris
> n=23 → 0.609 / 0.696. The parity (pipeline) reading stands; the relevance reading
> does not yet have a clean instrument.
>
> **Scope.** Improve the corpus generator's topic derivation (or add a paraphrase/quality
> filter), regenerate the corpus **with a snapshot pin**, re-run the pair on the clean
> subset, publish stratified + clean-subset numbers and the delta.
> **AC.** A documented generator change; a regenerated corpus whose debris share is
> materially reduced (target ≤5/99 or an explicit floor); a re-run artifact; no silent
> re-baseline of the frozen golden (a new golden version is explicitly named if the
> corpus changes).

Every claim below carries its evidence. Anything believed but not run is labelled
**hypothesis**.

## 1. The measurement rig (all verified this session unless noted)

| Fact | Evidence |
|---|---|
| Pinned copy `/tmp/p1-live-copy.db`, sha256 `e0434a7214ac…`, 56,457 entries, 9 watches enabled | `shasum -a 256`; sqlite count; matches the corpus header pin |
| Harness store `/tmp/p1-full-store` intact: `copySnapshotSha256=e0434a72…`, `counts.copyEntries=56457`, 24 buckets, `modelRevision=cb950dc8…`, `ingestedAt=2026-09-10T11:43` | `python3 -c json.load(params.json)` |
| HF cache holds exactly the pinned weights revision | `~/.cache/huggingface/.../snapshots/cb950dc8…`; `ingest.model_weights_info()` → same revision, 869,254,400 bytes |
| Installed bank binary is `ai-raccoon 1.42.0` (symlink dated Sep 9 17:20); repo `VERSION` is `1.42.1` | `ai-raccoon --version`; `ls -l ~/.dotnet/tools/ai-raccoon`. The golden was measured with this same installed binary — no reinstall since Sep 9 17:20. |
| Quiesced base rebuilt this session is byte-identical to the golden's base: sha `eeb431a3efd8…` | `make_quiesced_scratch.py --source /tmp/p1-live-copy.db --out …` → `base sha256: eeb431a3…`; matches the C11 record (`…p1-lane-report`) |
| Eval smoke on the FROZEN corpus reproduces the golden for the first 5 rows (harness 0.800 / bank 1.000; C001–C005 all match golden hits) | `evaluate --limit-queries 5` run this session |
| Eval cost under the CURRENT machine load: 5 queries = 1m14s wall (load avg ≈ 20, co-tenant `jsaa` vitest + Chromium + the live server at ~90% CPU). Extrapolated full 100 ≈ **~13 min/repeat**; the P2/P4 record is ~3 min/repeat on an idle box | `time` on the smoke run; `uptime`, `ps aux` |
| `--repeats N` requires `--scratch-base` + `--scratch-root`; `--limit-queries` exists; checkpoint written after each repeat | `evaluate.py:588-620, 700-737` |

**Consequence for the plan:** the campaign is affordable but not free; one
full-rig reproduction + one `--repeats 2` new-corpus run ≈ 40 min under load. Run
heavy steps one at a time (`memwatch --cap-mb 12288`, never port 7721).

## 2. The debris, measured

`evaluate.DEBRIS_SIGNATURE = re.compile(r'[{}\[\]|<>\\]|://|```|"line"|@[a-z0-9-]+/|\S{60,}')`,
plus `len(text) > 160`; `report.py` imports `debris_query` from it (one definition).

- Current corpus: **23/100 debris** (the P1 report's 23/99 scored = 23 + one stale row
  C035 filtered; C035 is not debris).
- All 23 are `file-targeted`. Sources are machine-generated/data files: `.ai-badger/mcp-tools.json`,
  `BenchmarkDotNet…-report-github.md`, `benchmarks/README.md` (table), `0002-opentelemetry…md`
  (blockquote), `arasz-home-page …/MEMORY.md` (link index), `deepseek-harness` `BENCHMARK.md`,
  `CLAUDE.md` (fenced listing), `README.md`/`README.zh.md`, `THIRD_PARTY_NOTICES.md` (table),
  `apps/cli/README*.md`, `composition.md` (mermaid), `package.json` ×2, `reference/README.md`,
  `tsconfig.json` ×2, `global.json`, `jsaa …/MEMORY.md`, vue-kanban `README.md` (table),
  `coverage/coverage-final.json`, `lighthouse-report/board.report.json`.
- **Root cause (read, verified against the data):** `_derive_topic()`
  (`build_project_corpus.py:96-112`) collapses whitespace and cuts at the first
  `[.;:!?](\s|$)` at index ≥ 8 — in JSON the first `:` is the key separator
  (`], "intent": "Close…` → topic `], "intent`), in a table it cuts after a cell, in a
  fenced listing it never finds a sentence. No markup sanitisation happens before the cut.

## 3. Feasibility prototype (measured, throwaway)

Prototype at `/tmp/corpus-quality-probe/probe.py` (not committed): markup-aware topic
derivation = strip fenced/table/quote/heading/list markers, unwrap Markdown links/images
and HTML tags, pick the first span of ≥3 word tokens containing no `{}[]<>\\`|~^*_`,
else a keyword-span fallback; cap at 100 chars.

- Repaired topics only, same frames, same targets: **2/100 debris** (C050 and C096 —
  both trip `@[a-z0-9-]+/` through `@scope/pkg` strings). Stripping the `@` of scoped
  package names in the sanitizer removes both → **0/100 in the prototype**.
- Repair does **not** change target selection or frames except where collisions resolve
  differently; the corpus keeps its 100 anchors, projects and scopes.

**Hypothesis (not yet measured):** the repaired queries change per-query hit rates; the
direction is unknown until the re-run. The report's stratification recomputes from the
scored rows, so the new debris share is an independent measurement of the generator fix.

## 4. Reach (consumers and gates that a regenerated corpus touches)

| Consumer / gate | What it does | What B must do |
|---|---|---|
| `scripts/src/retrieval_tuning/refresh_corpora.py` | regenerates both corpora, byte-compares to committed, exit 0 = clean, 3 = smoke regression | regenerate the committed artifact; `refresh --copy /tmp/p1-live-copy.db` must exit 0 for both corpora |
| `scripts/tests/test_build_project_corpus.py` | determinism + regeneration equality vs the committed artifact (copy-gated), shape, anchors, coverage/holdout | stays green unmodified (it derives from the artifact); committed-artifact half needs `AI_RACCOON_EVAL_COPY=/tmp/p1-live-copy.db` |
| `scripts/tests/test_refresh_corpora.py` | pinned-copy exit-0 test (`:275`), registry, pin shapes | stays green |
| `scripts/tests/test_p3_cli_parity.py::test_generators_reproduce_the_legacy_payload` | **opt-in heavy** (`P3_CLI_PARITY=1`); asserts byte-identity between the base `12a72dfb` generator output and the current one for the project corpus; the eval corpus arm compares query payloads | **will fail by design** after B — the case must be updated to the new contract (or retired with a record); the eval-corpus arm is unaffected |
| `.github/workflows/build.yml:272` scripts-harness lane | runs the stdlib-runnable harness tests incl. `test_refresh_corpora.py`, `test_llamaindex_harness_report.py`, `test_diff_golden.py`; **not** `test_build_project_corpus.py` (copy-gated) | keep all listed tests stdlib-importable; CI is the gate |
| `scripts/retrieval_tuning/llamaindex_harness/report.py` | recomputes the debris/clean strata from `rows[].query`; renders `mean [min–max]` from a repeats block | no code change needed; the re-run report carries the new split |
| `scripts/retrieval_tuning/collect_ac_evidence.py` | derives expectations from the corpus + store; contract literals frozen | must still run clean against the new corpus |
| `scripts/retrieval_tuning/run_threshold_eval.py`, `mmr_transfer_checks.py`, `docs/work/c10_shared_leg_trace.py` | consume the corpus path / recompute strata offline | no artifact pin; check no hardcoded id lists |
| Frozen goldens `docs/work/results-f1*.json` | the numeric reference for the OLD corpus | must not be rewritten; the new run is a NEW named golden |
| `scripts/tests/test_diff_golden.py`, `test_llamaindex_harness_report.py:361` | read `results-f1.json` as fixtures | unaffected |

**Provenance gap (verified):** `results.json` records `corpusSnapshotSha256` — the COPY
sha, unchanged by B — and `params.json` records only the corpus file NAME. Nothing
records the corpus file's own bytes, so old and new goldens are distinguishable only by
their row contents. Adding a corpus-file hash is optional; naming the new artifacts is
mandatory.

## 5. Golden naming and the re-run artifacts

Existing names: `results-f1.json`, `results-f1-run2.json`, `results-f1-repeats.json`,
`results-f1-merged-repeats.json`; report `docs/work/2026-09-10-p1-full-100-eval-report.md`.
The frozen files are never edited. B names its own generation explicitly (proposal:
`results-f2.json` + `results-f2-run2.json`, report
`docs/work/2026-09-10-corpus-quality-eval-report.md`; plan decides exact names and records
them in the follow-up definition + state).

## 6. Open decisions the plan must settle

1. Scope of the generator change: repair-only (recommended: no target churn, a clean
   single-variable experiment) vs repair + clean-candidate preference (better topics for
   machine files, but changes which anchors are targeted).
2. Whether the sanitizer shares the `DEBRIS_SIGNATURE` definition with
   `evaluate.py`/`report.py` (one home in `scripts/src/retrieval_tuning/`) and how to keep
   the report's measurement independent of the generator's predicate (avoid a
   self-certifying metric).
3. The exact new artifact names and where the "new golden version" is recorded (definition
   doc + state + report header).
4. Whether to add a corpus-file sha to `results.json`/`params.json` provenance (optional).
5. How `test_p3_cli_parity.py`'s project-generator arm changes (new contract vs retirement).
6. Campaign shape: full-rig reproduction on the frozen corpus (diff vs golden) before the new
   run, and `--repeats 2` vs 3 for the new golden.
