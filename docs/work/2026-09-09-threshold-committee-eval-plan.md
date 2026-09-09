# Implementation plan — `air-threshold-filter-corpus-committee-eval` (2026-09-09)

Task: low-effort. Branch: `task/air-threshold-filter-corpus-committee-eval`, worktree
`.ai-badger/worktrees/air-threshold-filter-corpus-committee-eval` (base `ae7a240a`).
Sources of truth: research record `docs/work/2026-09-08-threshold-committee-eval-research.md`
(owner-ratified; not relitigated here), eval precedent
`docs/work/2026-09-08-mmr-postfusion-eval.md`. This plan adds implementation shape only.

**Merge policy (load-bearing, verified this session).** The PoC resolves
`MMR_MODE ?? "mmr"` — with the env unset the diversifier is **active by default**
(verified in the `SearchResultMerger.cs` diff, `PocDiversifierFor` + `FetchPocVectorsAsync`).
"Default-off" therefore means the `MMR_DISABLE=1` path only. Consequence: the P1 C# port
is **branch-local forever** — the eventual PR merges scripts/tests/docs only (P2–P6 +
plan + report); the 4 C# files are excluded from the merge. The PoC header says NOT
SHIPPABLE; this plan enforces it mechanically.

## Ground truth verified this session (commands on record)

- PoC delta = 4 files, "3 files changed, 141 insertions(+), 11 deletions(-)" + 110-line new
  `MaximalMarginalRelevance.cs` (`git diff --stat` in worktree `air-mmr-postfusion-poc-eval-run`).
  3 of 4 live under `src/AiRaccoon.Infrastructure/Sqlite/Memory/`; **`MemorySql.cs` lives in
  `src/AiRaccoon.Infrastructure/Sqlite/`** (brief said otherwise — corrected).
- Live bank DOES have a `projects` table (5 registered rows) but it is a **strict subset** of
  `entries.project_id` (13 distinct ids — review-verified; enumerating `projects` would silently
  drop `deepseek-harness` at 12,890 rows and 7 others). Project scoping is `entries.project_id`;
  enumeration uses `SELECT DISTINCT project_id FROM entries WHERE project_id IS NOT NULL`.
  Project sizes (rows, all embedded): jsaa 18299,
  deepseek-harness 12890, ai-raccoon 8997, ai-badger 7528, hermes-default 2702,
  arasz-home-page 1726, vue-kanban 1184, pi-badger-integration 322, job-search-ai-assistant 116,
  dotnet-ignore 25, interview-tasks/aib/ai-sheepdog 1 each.
- Proven harness shape: `/tmp/mmr-poc-mcp-stderr.py` — newline-delimited JSON-RPC over
  `dotnet <dll> --transport stdio`, stderr redirected per run, one server per arm, batch of
  queries per process. RBO formula pinned from `/tmp/mmr-rbo.py`: finite truncated RBO,
  `s += p**d * |A_d ∩ B_d|/d` for d=1..min(len), result `×(1−p)`; identical 8-lists = 0.5126
  (the eval report's 0.513 ceiling). Test asserts against that constant.
- Headless graders: `pi -p --no-session --provider <p> --model <id>` exists (`pi --help`,
  `--model` accepts `provider/id`). Grader #2/#3 = `openrouter/meta/muse-spark-1.3-contributor`,
  `xiaomi/mio-v2.5-pro` (H1: one smoke call per model before the fleet runs).
- Test conventions: `pyproject.toml` → `testpaths=["scripts/tests"]`, `pythonpath=["scripts/src"]`;
  retrieval-tuning tests load generators via `importlib.util.spec_from_file_location`
  (see `test_retrieval_tuning_eval_corpus.py`), env-gate live artifacts with `pytest.skip`.
  Run tests as `uv run pytest scripts/tests/<file>.py`.
- Sanctioned DB copy: `scripts/retrieval_tuning/make_memory_copy.py` (read-only URI +
  `.backup` + verify) — P2/P6 reuse it, do not re-implement.

## Owner-decision → package map (each decision in exactly one package)

| Owner decision (research record) | Package |
|---|---|
| Arms = `off` + `threshold τ=0.95` only; MMR discarded (no MMR arm built) | P3 (P1 only ports the gated code) |
| Committee: 3 graders, gated form, 3/3 unanimity, majority never accepted | P4 |
| Nudge ≤3 rounds, replacement ≤3, ≤9 attempts/slot, exhausted reported never skipped | P4 |
| Grader models #1 default, #2 muse-spark, #3 mio-v2.5-pro (trio composition) | P4 (P5 reuses this config by reference; the decision lives here) |
| Sample 16 = 10 changed + 6 controls, stratified on mechanical diff only | P4 |
| Blind A/B: randomized unlabeled a/b, 1 pass, comp_score=x/3 | P5 |
| Reasons distilled to core sentences by the calling orchestrator (not an agent) | P5 |
| H1 smoke grading call (hypothesis) | P4 first gate |
| H2 expectedHash auto-grade mitigation | P2 (anchors) + P3 (auto-grade metric) |
| H3 snapshot hash recorded | P2 (header) + P6 (report) |

## P1 — port-poc (first, alone)

Owns (only these): `src/AiRaccoon.Infrastructure/Sqlite/Memory/MaximalMarginalRelevance.cs`
(new), `.../Memory/SearchResultMerger.cs`, `.../Memory/SqliteMemoryStore.cs`,
`src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs`, the P1 gate files
`scripts/retrieval_tuning/fixtures/poc-parity/` + `scripts/tests/test_poc_port_gates.py`
(review M3 — the gates are P1's own deliverables), and the port archive
`docs/work/poc-threshold-port.patch` (review C3: `git format-patch` of the port commit so the
branch-local port survives branch pruning — docs are merge-allowed). P1's parity test
embeds its own minimal JSON-RPC client; the reusable harness module is P3's (disjoint
ownership). The env-gated P1/P3 tests are branch-only: with `AI_RACCOON_EVAL_COPY` set on
main they would fail (no port), by design.

Subpackages:
- **1a port**: copy `MaximalMarginalRelevance.cs` verbatim; apply the three diffs verbatim
  (SearchResultMerger +92/−12 region, SqliteMemoryStore +53, MemorySql +7). One commit:
  `poc(port): post-fusion diversifier (MMR/threshold) from task/air-mmr-postfusion-poc, NOT SHIPPABLE`.
- **1b gates**: build green + default-off parity + threshold-marker smoke.

ACs (each with run + expected shape):
- **AC1.1 port delta is exactly the PoC delta.** RUN:
  `git diff --stat <base>..HEAD -- src/ && git status --porcelain` → EXPECTED: exactly the 4
  files; total "3 files changed, 141 insertions(+), 11 deletions(-)" plus the 110-line new
  file; porcelain empty after the commit.
- **AC1.2 build green.** RUN: `dotnet build` → EXPECTED: exit 0, 0 errors.
- **AC1.3 default-off parity with base.** Before the port commit: `dotnet build src/AiRaccoon`
  in this worktree (base = uncommitted-tree state), capture golden outputs for 3 queries
  (2 `ai-raccoon`, 1 `jsaa`) with `MMR_DISABLE=1`; the DB copy is made by P1 itself via
  `make_memory_copy.run_copy_and_verify` (no copy exists at P1 time). Store fixture JSON under
  `scripts/retrieval_tuning/fixtures/poc-parity/`. After porting, same run → EXPECTED:
  identical results on deterministic fields (hash/ranking/path/sourceFile/chunkIndex/snippet —
  timings stripped).
- **AC1.4 threshold path engages.** Same harness, `MMR_MODE=threshold MMR_TAU=0.95`, one
  non-path query → EXPECTED: stderr contains ≥1 `[mmr-poc] mode=threshold` line; with
  `MMR_DISABLE=1` → EXPECTED: zero `[mmr-poc]` lines. Up to TWO lines per search is expected
  (review C4: `SearchResultMerge` and `AdjustMergedResults` both call `Merge`; the threshold
  filter is idempotent — do not "fix" the apparent duplicate).

Tests (red-first where red is possible — review S2):
- `scripts/tests/test_poc_port_gates.py` — env-gated (`pytest.skip` without
  `AI_RACCOON_EVAL_COPY` + built dll), 3 tests: parity-vs-fixture (fails if the port
  perturbs the gated-off path), marker-present (fails on base: no marker exists),
  marker-absent-under-disable (fails if the gate leaks). P1 adds no C# tests — the port is
  verbatim PoC, eval-only, and the parity/marker gates are the failing checks. The marker
  test is the red check (fails on base); parity-vs-fixture and marker-absent-under-disable
  are green-on-base regression guards — they cannot fail on base by construction (review S2).

Gate to pass: AC1.1–1.4 all green before any parallel lane branches.

## P2 — corpus

Owns: `scripts/retrieval_tuning/build_project_corpus.py`,
`scripts/tests/test_build_project_corpus.py`, artifact
`scripts/retrieval_tuning/corpora/project-corpus-100.json`. Nothing else.

Subpackages:
- **2a copy + enumerate**: fresh read-only copy via `make_memory_copy.run_copy_and_verify`
  (reused, not rewritten); enumerate projects from `entries`; record sha256 of the copy in
  the corpus header (H3).
- **2b candidates + holdout**: per project, candidate pool = file-targeted (distinct
  `source_file` with ≥1 embedded chunk) + content-targeted (embedded rows without
  source_file, marker-derived). **Alias-fold exclusion (review M2):** candidates whose raw
  `entries.project_id` ≠ its gate-canonical fold (`aib→ai-badger`,
  `job-search-ai-assistant→jsaa` — 2 projects, 117 rows) are excluded and the exclusion
  documented: the search gate folds the projectId, so a raw-spelled anchor could never be
  served. **Holdout reserved up front at candidate level** (seeded ~10% per project; projects
  with <10 candidates reserve no holdout — review S5, else 1-row projects lose their only
  candidate): holdout targets are never turned into queries — same semantics as
  `RESERVED_TEST_FILES` in `build_eval_corpus.py`.
- **2c query generation**: exactly **100 eval queries** (all eval; holdout excluded from the
  count) so the mechanical table is n=100. Stratification: every project with ≥1 embedded row
  gets ≥1 query; sizes scale with √rows; cap 20 for the largest; allocation redistributes post-cap by **largest remainder** (pinned,
  no implementer judgment); deterministic stable sort +
  seeded tie-break. File-targeted queries carry `expectedHash` (resolved from the copy,
  asserted unique) + `targetProjectId` + `targetScope`; content-targeted carry
  `expectedHash=null` + a unique verbatim marker assertion. 3 paraphrase frames per target,
  rotated. Every query carries its own `projectId` (the scoping trap). Header carries
  `snapshotSha256`, `seed=SEED`.

ACs:
- **AC2.1 determinism.** RUN: `uv run pytest scripts/tests/test_build_project_corpus.py -k determinism`
  → EXPECTED: two `generate()` runs byte-identical AND equal to the committed artifact.
- **AC2.2 shape.** Same file, `-k shape` → EXPECTED: exactly 100 entries; unique ids
  `C001..C100`; required keys incl. `projectId`, `expectedHash|null`, `holdout:false`;
  no duplicate query text (exact + normalized).
- **AC2.3 anchors resolve.** `-k anchors` → EXPECTED: every `expectedHash` resolves to
  exactly 1 row in the copy with matching `project_id`/`scope`; content markers match
  exactly 1 row in their bucket.
- **AC2.4 coverage + holdout.** `-k coverage` → EXPECTED: every project whose raw id equals
  its gate-canonical fold appears ≥1× (fold-divergent projects excluded per 2b); holdout
  candidates appear in zero queries; per-project counts recorded in the header.

Tests: `test_build_project_corpus.py` mirrors the four ACs (determinism, shape,
anchor-resolution against the copy, coverage/holdout), each can fail (e.g. an unresolvable
anchor, a duplicate query, a holdout leak). Import via `spec_from_file_location`.

## P3 — runner

Owns: `scripts/retrieval_tuning/run_threshold_eval.py`,
`scripts/tests/test_run_threshold_eval.py`. Reads P2's corpus artifact (contract = its JSON
schema, tested via fixtures, not by importing P2's module).

Subpackages:
- **3a harness**: committed version of the proven pattern — one server per arm
  (`dotnet <dll> --transport stdio`), sequential arms (never concurrent), stderr →
  `docs/work/threshold-committee-eval/arm-<name>.stderr`, newline JSON-RPC, batch all queries
  through one process per arm. Arm env: `off` → `MMR_DISABLE=1`; `threshold` →
  `MMR_MODE=threshold MMR_TAU=0.95`. Every `memory_search` call passes that query's
  `projectId`.
- **3b metrics**: per query per arm — hash list, ranking, path, sourceFile, chunkIndex,
  snippet(≤600), **`queryText`** (review S6: P4/P5 grade and compare payloads need it; they
  read it from `metrics.json`, not the corpus); derived per pair — **top-8 SET overlap**/8
  (review C2: set overlap, not RBO — reorder-only queries are controls), RBO p=0.9 truncated
  (formula above, identical=0.5126), drop list (in off, not in threshold) with sourceFile,
  backfill list (in threshold, not in off), `expectedHashInTop8` auto-grade per arm (H2 flag).
  Output:
  `docs/work/threshold-committee-eval/arm-off.json`, `arm-threshold.json`,
  `metrics.json`. Hard failure when the threshold arm's stderr has zero `[mmr-poc]`
  markers (arm didn't engage) or the off arm has any.

ACs:
- **AC3.1 arm config + scoping (unit).** RUN: `uv run pytest scripts/tests/test_run_threshold_eval.py -k "arm_env or scoping"`
  → EXPECTED: env mapping exact; every built call carries the query's `projectId`; holdout
  ids refused; corpus contains no fold-divergent projectId (review M2).
- **AC3.2 metrics on fixtures (unit).** `-k metrics` → EXPECTED: known-overlap fixture gives
  exact overlap/drop/backfill; RBO identical lists ≈ 0.5126 (tol 1e-3); a known swap fixture
  gives the documented value.
- **AC3.3 marker discipline (unit).** `-k markers` → EXPECTED: fixture stderr with markers
  parses; threshold-with-zero-markers raises; off-with-markers raises.
- **AC3.4 live smoke (env-gated).** `-k smoke` with copy + dll → EXPECTED: 2 queries × 2
  arms produce 2 JSONs + metrics; off stderr 0 markers; threshold stderr ≥1 marker.

Tests: the four above; all fail-capable (wrong env mapping, wrong RBO constant, missed
marker, holdout leak).

## P4 — committee

Owns: `scripts/retrieval_tuning/committee_grade.py`,
`scripts/retrieval_tuning/committee_form.schema.json`,
`scripts/tests/test_committee_grade.py`, artifacts under
`docs/work/threshold-committee-eval/` (`sample.json`, `forms/<slot>/<attempt>/grader-<n>.json`,
`forms-manifest.json`). Reads P3's `metrics.json` (fixture-based contract test).

Subpackages:
- **4a sample**: from `metrics.json`, stratify on mechanical diff ONLY: changed = top-8 SET
  overlap<1.0; controls = overlap==1.0 (reorder-only queries are controls by this definition);
  seeded pick `min(10, |changed|)` changed, backfill controls to 16 (research record fixes
  10+6; if the mechanical run yields <10 changed the sample backfills and the report states
  the composition). `sample.json` header records the grader-trio config (review S3 — P5
  asserts equality with its own constants in a test). Persist `sample.json`.
- **4b form + schema**: form = `{slotId, queryId, arm, chunks:[{hash, grade:"A"|"0",
  reason}], overall}`; `committee_form.schema.json` is the contract; validation is a
  hand-rolled `_validate_form` (no new deps; schema file documents it, tests pin both).
  A grade counts ONLY with a completed (schema-valid) form.
- **4c state machine**: slot = (query, arm); 16 queries → 32 slots. Per slot: 3 graders
  (models per owner decision; grader #1 = session-default `pi -p`), submit one form each;
  consistency = 3/3 identical per-chunk grade vectors (majority never accepted).
  **Attempt semantics (review M4, pinned to the owner's 3×3=9 arithmetic): an attempt = one
  committee round = 3 parallel grader submissions.** Per query version: initial round + ≤2
  nudge rounds ("argue against your own grades") = ≤3 rounds. A version that exhausts 3
  rounds without unanimity is REPLACED by a fresh query from the same stratum, and the
  replacement INHERITS the chain-wide cap: ≤9 rounds per slot across all versions (=3
  versions × 3 rounds; a 4th version is unreachable by construction). **Malformed forms
  (review S7):** a grader returning a schema-invalid form is re-asked (same brief) up to 2
  re-asks — not round-consuming, logged; a still-invalid form is void, the round cannot
  reach 3/3, and the inconsistency path proceeds. Exhausted slots are REPORTED
  (`"exhausted": true` in the slot report), never skipped. Grader = headless subprocess `pi -p --no-session --provider P
  --model M` (fresh session per call), `--runner "claude -p"` flag switches the CLI.
  Every attempt archived to disk; `forms-manifest.json` carries sha256 of every archived
  form and payload. **H1 gate first**: one smoke call per grader model must return a
  schema-valid form before any fleet run.
- **4d outputs**: `committee-grades.json` — per slot per-chunk vectors, consistency
  verdicts, attempt/nudge/replacement counts, exhausted flags; per-query answer-chunks
  per arm (A=1 iff chunk answers the query — PoC rubric).

ACs:
- **AC4.1 state machine paths (fake runner).** RUN: `uv run pytest scripts/tests/test_committee_grade.py -k machine`
  → EXPECTED: accept-first-round, nudge-then-accept, replace-then-accept (version 1 exhausts
  3 inconsistent rounds, version 2 accepts — constructible under FakeRunner, review M4),
  chain-exhaustion-reported-not-skipped (version 3 exhausts the 9-round chain) all pass; each
  path asserts archived forms + manifest hashes exist.
- **AC4.2 gating.** `-k gating` → EXPECTED: malformed form triggers ≤2 re-asks (logged, not
  round-consuming), then void → inconsistency path; trio with 2/3 agreement is inconsistent
  (majority rejected); 3/3 vectors accepted.
- **AC4.3 sampling.** `-k sampling` → EXPECTED: 16 = changed+controls per the backfill rule;
  determinism (same metrics → same sample); stratification ignores grades entirely.
- **AC4.4 caps.** `-k caps` → EXPECTED: >3 rounds for one query version triggers
  replacement; >9 rounds in a slot's replacement chain is impossible by construction; the
  exhaustion path fires when the third version exhausts its rounds (no fresh-budget
  replacement — review M4).
- **AC4.5 H1 smoke (env-gated, blocks the fleet).** RUN: `uv run python scripts/retrieval_tuning/committee_grade.py --smoke-graders`
  → EXPECTED: 3 schema-valid forms (one per model) or a documented per-model failure report;
  exit non-zero on any failure.

Tests: the five above (FakeRunner injection; no network in unit tests). Grader-call
accounting is asserted in tests via the FakeRunner log.

## P5 — ab-pass

Owns: `scripts/retrieval_tuning/ab_compare.py`, `scripts/tests/test_ab_compare.py`,
artifact `docs/work/threshold-committee-eval/ab-forms/` + `ab-results.json`. Reads P4's
`sample.json` + P3's arm JSONs (fixture contracts).

Subpackages:
- **5a payloads**: per sampled query, payload = (query, listX, listY) — the two arms' 8
  chunks; order randomized by seed; positions labelled only `first`/`second`; the
  position→arm mapping lives only in the archived `ab-results.json`. Fresh trio = the three
  models recorded in P4's `sample.json` header (review S3: P5 asserts its constants equal
  that record in a test), fresh subprocess/session per call, forced choice (pick first|second
  — no tie option; `comp_score = x/(3−abstentions)` for threshold, x = graders whose pick
  mapped to threshold — review S7: a malformed grader output is re-asked once, then recorded
  as an abstention and reported, never silently scored against the threshold arm). 1 pass, no
  nudges.
- **5b distillation**: the calling orchestrator (= this script, deterministic Python)
  distils the 3×16 grader reasons into core sentences (first-sentence extraction, dedup,
  frequency-rank top-k). No LLM call in this step — asserted by test (runner log empty).

ACs:
- **AC5.1 no arm hint in payload.** RUN: `uv run pytest scripts/tests/test_ab_compare.py -k blind`
  → EXPECTED: for a fixture pair, the rendered instruction block + delimiters contain none
  of `threshold`, `off` (as standalone words), `MMR_`, `mmr-poc`; chunk text is excluded
  from this assertion (chunks may legitimately contain the word). The test fails if the
  template leaks.
- **AC5.2 seeded mapping.** `-k mapping` → EXPECTED: same seed → same order; mapping
  restored post-grading; comp_score arithmetic exact (x/3 on fixture picks; x/(3−a) on an
  abstention fixture — review S7).
- **AC5.3 fresh sessions.** `-k sessions` → EXPECTED: runner invoked with `--no-session`
  (or runner-equivalent), no resume/continue flags; 48 calls for 16 queries × 3 graders.
- **AC5.4 distillation deterministic.** `-k distill` → EXPECTED: fixture reasons → stable
  core-sentence output across two runs; zero runner invocations during distillation.

Tests: the four above, FakeRunner throughout.

## P6 — integration (last)

Owns: `scripts/tests/test_threshold_eval_integration.py`,
`docs/work/2026-09-09-threshold-committee-eval.md` (report), coordinates the artifact
directory. Touches no other package's files.

Subpackages:
- **6a cross-package contract tests (fixture chain, no live deps)**: tiny synthetic corpus
  JSON → P3 spec-builder → fake arm JSONs → P3 metrics → P4 sample+fake-runner grading →
  P5 fake-runner ab → report assembler → assert the report's numbers equal the fixtures'
  numbers. RUN: `uv run pytest scripts/tests/test_threshold_eval_integration.py` → EXPECTED:
  pass without any live bank/CLI.
- **6b end-to-end run**: fresh copy (`make_memory_copy.py`) → P2 corpus → P3 both arms
  (sequential, one server at a time, no extra model loads) → P4 committee (post-H1) →
  P5 ab → report. Report sections: mechanical table (n=100, both arms: overlap, RBO,
  drop/backfill, expectedHash auto-grade), committee grades (n=16 sample: per-query
  answer-chunks per arm, consistency stats, exhausted slots), comp_score table (x/3) +
  distilled core-sentence recommendations, snapshot sha256 (H3), budget spent (grader-call
  count + wall clock). Every number cites its archived JSON; `forms-manifest.json` +
  artifact manifest carry sha256s.
- **6c merge-prep**: PR = scripts/tests/docs + artifacts only; **P1's 4 C# files excluded**
  (merge policy above); `docs/work/poc-threshold-port.patch` IS included (review C3 — the
  port survives branch pruning). RUN: `git diff --stat <base>..PR-branch -- src/` → EXPECTED:
  empty.

ACs:
- **AC6.1 traceability.** RUN: `uv run pytest scripts/tests/test_threshold_eval_integration.py -k trace`
  against the real artifacts → EXPECTED: every number in the report resolves to an archived
  JSON whose sha256 matches the manifest; any orphan number fails.
- **AC6.2 fixture chain.** `-k chain` → EXPECTED: full cross-package pass on fixtures.
- **AC6.3 report completeness.** `-k report` → EXPECTED: all six sections present with
  snapshot hash and budget spent; mechanical n=100; committee n=16; comp_score x/3.
- **AC6.4 merge hygiene.** As in 6c → empty src/ diff. Enforced by a pytest (review C1):
  `-k merge_hygiene` asserts `git diff --stat <base>..HEAD -- src/` is empty when run on the
  task branch (branch-skip on main — there the assertion is trivially true and the gate
  meaningless).

## Parallelism + dispatch

| Package | Parallel-safe? | Boundary |
|---|---|---|
| P1 | first, ALONE | owns all 4 C# files; sets the base for lanes |
| P2 | yes, after P1 | owns only its generator + test + corpus JSON |
| P3 | yes, after P1 | owns only its runner + test; reads P2 artifact by contract |
| P4 | yes, after P1 | owns only committee_grade.py + schema + test + forms/ artifacts |
| P5 | yes, after P1 | owns only ab_compare.py + test + ab artifacts |
| P6 | last, after all | owns integration test + report; merges everything |

Shared-surface check (explicit): P2–P5 file sets are pairwise disjoint (verified by
listing above); no new shared module, no new dependency, `pyproject.toml` untouched.
**However, parallel lanes must NOT share the task worktree** — commits, `git add` and the
index would interleave. Per-lane worktrees: each of P2–P5 branches from P1's task-branch
head in its own worktree (worktree-agent-isolation pattern), lands as its own commit(s),
and is rebased/cherry-picked onto the task branch by the orchestrator (review S4: lanes
share P1's head, so later lanes cannot fast-forward; disjoint paths make conflicts
unexpected). P6 runs in the task worktree after all merges.

Dispatch: exactly 2 levels — L1 orchestrator (this task session), L2 = one implementation
lane per package (+ review/fix folds). Lanes never spawn sub-lanes. Grader CLI subprocesses
are OS processes inside scripts, not agent dispatches.

## Budget arithmetic (worst case)

- L2 delegations: P4 impl ≤2 (impl + fix) + 1 review; P5 same → ≤6 for the two lanes;
  all packages ≤18 worst case, ~8 expected.
- Grader subprocess calls (token-dominant, each one headless LLM session):
  P4 worst = 32 slots × 9 rounds × 3 graders = **864** (hard-capped: 3 versions × 3 rounds;
  review M4 — the first draft's 288 under an attempts-are-submissions reading made
  replacement unreachable); expected ≈ 32×3 = 96 plus a nudge/replacement tail (realistic
  ≤200). P5 = 16×3 = **48** (+ ≤16 re-asks). H1 smoke = **3**. Worst total **915**,
  expected ≈ 100–250. Payload per call ≈ 8 chunks × ≤600-char snippets — small.
- Mechanical runs: 2 server processes total (one per arm), sequential; P6 adds 2 more on
  the fresh copy.

## Deviations from the brief (with reasons)

1. **`projects` table is a strict subset** of `entries.project_id` (5 vs 13 ids — the first
   draft wrongly claimed the table absent; review corrected it) — enumerate from `entries`.
2. **`MemorySql.cs` path** is `Sqlite/`, not `Sqlite/Memory/` (verified).
3. **Holdout re-scoped to candidate level** so the corpus is exactly 100 eval queries and
   the P6 mechanical table is n=100 as the brief requires.
4. **Merge policy added**: C# port never merges to main (PoC is default-ON for MMR when env
   unset — verified in the diff).
5. **Attempt = one committee round (3 parallel submissions); the ≤9 cap spans the slot's
   whole replacement chain** (review M4 pin of the owner's 3×3=9: 3 versions × 3 rounds;
   the first draft's submissions-reading made replacement unreachable and 288 undercounted).
6. **Backfill rule** when changed<10 in the mechanical run (plan addition; composition
   reported).
7. **No new jsonschema dep** — hand-rolled validator, schema file as contract.
8. **P1 adds no C# unit tests** — verbatim PoC port; parity/marker pytest gates are the
   failing checks (keeps the port faithful and the merge exclusion simple).
9. **Alias-fold exclusion in P2** (review M2): candidates whose raw project_id folds to a
   different canonical id are excluded (2 projects, 117 rows) — the gate would fold the
   query's projectId and the raw-spelled anchor could never be served.

## Hypotheses (believed, not fully verified)

- H1 grader-model headless compatibility — resolved by AC4.5 before any fleet run.
- H2 paraphrase templates under-cover code-ish content — mitigated by AC2.3 anchors +
  AC3.2 auto-grade; flagged in the report.
- H3 bank drift between corpus build and runs — both arms share one copy; snapshot hash
  recorded (AC2.1 header, AC6.3).
- Attempts-vs-rounds reading of "≤9 attempts/slot" (deviation 5) — owner-visible; the
  state machine makes the cap explicit either way.
- Per-lane worktrees assumed available for P2–P5 (same mechanics as existing
  `.ai-badger/worktrees/` usage — observed, not re-tested in this session).
