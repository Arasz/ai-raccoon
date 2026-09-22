# Project-scope review — Phase 0 VERIFIED GROUND TRUTH

Date: 2026-09-22. Repo: `/Users/arasz/RiderProjects/ai-raccoon`
Base commit: `5bca19000d127547ddbc56e2a464113c844334cc` (branch `main`, == `origin/main`, clean tree)
Product: AiRaccoon — .NET 10 MCP server + CLI for agent memory over SQLite. VERSION `1.42.5`.

Trust these over anything a prior document, plan, issue or lane brief says.

## Measurements taken this session

- **Build**: `dotnet build --nologo` (main checkout) → `Build succeeded. 0 Warning(s), 0 Error(s)`, 28.41s.
- **Test discovery**: `dotnet test --project tests/AiRaccoon.Tests --list-tests` → `Discovered 5210 tests.`
- **Full suite at base** (macOS arm64, `dotnet test --project tests/AiRaccoon.Tests --no-build`):
  **total 5233, failed 2, succeeded 5211, skipped 20, duration 25m28s** (exit 2). Machinery: a
  Rider/ReSharper test session ALSO ran on this machine throughout (CPU contention: counts are
  valid, wall-clock is not a clean-room number) and four review lanes were running near the end.
  The two failures are detailed below. The 20 skips (enumerated from the run log): 2×
  GraphPooledOutputParityTests (need `AIRACCOON_POOLING_PARITY_MODEL_DIR`), CaptureGoldenVectors,
  5 BDD `Ignored` scenarios (`The server asks the agent which hashes to keep via MRTR`; `Project
  isolation is enforced on the cloud side via row-level security`; `The store emits metrics and
  tracing for its own operations`; **`All 17 tools are still listed`**; `The memory inspects
  itself through memory_inspect`), GateQueryVectorRegenerationTool, 4× CodeEngineRealModelEmbedTests
  (need `AIRACCOON_CODE_MODEL_DIR`), **PromotionScoringRealDataTests.ScoresCorrelateWithHandLabeledUsefulness**
  (needs real data), PlatformNumericsProbe (env-gated), 3× CodeModelGraphWindowTests,
  DocsCorpusRegenerationTool, and the one static `[Fact(Skip=…)]` in
  `ModelMigrationCrashRecoveryE2ETests.cs:295`. Note the two skipped gates that look load-bearing:
  the tool-inventory BDD scenario and the promotion-scoring real-data correlation check.
  The CI-equivalent reference point: last real CI code run 2026-09-14 (#640), all lanes green.
- **Python harness** (the exact 14-file subset CI's `scripts-harness` runs):
  `191 passed, 7 skipped, 1 warning in 98.80s`.
- **Static trait attributes** (grep of `[Trait(...)]`, attribute occurrences — not test counts):
  Speed=Fast ×403, Speed=Slow ×160, Speed=Nightly ×36; Category=Unit ×308, Integration ×258,
  Retrieval ×19, E2E ×14; Performance=Benchmark ×3.
- **Size** (tracked `.cs` lines): AiRaccoon.Core 10,480 (187 files); AiRaccoon.Infrastructure
  24,447 (187); AiRaccoon host 12,396 (130); tests 124,405 (674); benchmarks 2,766 (20).
  Production total 47,323 lines; test:production ratio 2.63:1.
- **HEAD product code == the installed tool.** `git diff --stat 8a1f1dde..HEAD -- src` is empty
  (only `tests/Directory.Packages.props` differs). `8a1f1dde` is both `v1.42.5` and the build hash of
  the globally installed `ai-raccoon 1.42.5+8a1f1dde2e86b0…`. So `ai-raccoon` on PATH executes
  exactly the reviewed product code; no build needed to run it.
- **Delta since the last project-scope review** (base `155f281e`, 2026-08-21): **441 commits**
  (87 fix, 74 docs, 61 chore, 34 test, 29 task, 28 feat, 15 harness, 12 refactor). Main threads:
  project-ids, remove-stdio-full-server-mode (ADRs 0101–0104), embedding, sync, schema, watch, settings.
- **CI at this base**: `build.yml` ran for the two skills-only commits with `code=false` → every
  dotnet test job was skipped ("No code-relevant paths changed"). Last real CI code run:
  2026-09-14 (#640), all lanes green. GitHub runs checked via `gh run list` this session.
- **CI lanes** (`.github/workflows/build.yml`): `build-fast` = `Speed=Fast&Performance!=Benchmark`;
  `build-bdd` = `Category=bdd`; `build-slow` = `Speed=Slow&Performance!=Benchmark`;
  `build-nightly-gates` = `Speed=Nightly&Performance!=Benchmark` (opt-in label / dispatch, NOT required);
  `scripts-harness` = the 14 pytest files (191 passed baseline above). Actions are pinned to commit
  SHAs. `permissions: contents: read` at workflow level.
- **Known-flakes registry** (`known-flakes.json`): exactly one entry —
  `…BackendLauncherTests.Acquire_WhenAServerIsAlreadyListening_DoesNotSpawn`, issue #395, port/probe race.
- **Local full-suite failures at base** (osx-arm64) — 2 reds in the all-in run:
  1. `MiniLmGoldenVectorTests.BundledMiniLm_Embeddings_MatchCommittedGoldens` —
     `mismatchedBits should be 0 but was 384; entry E001: 384/384 float32 values differ bit-for-bit
     from the golden capture`. The committed golden's capture architecture is **Arm64** (captured
     2026-08-21T10:43Z, ORT-era 1.29.0), so the strict bit-identity branch ran
     (`tests/AiRaccoon.Tests/Integration/Embedding/MiniLmGoldenVectorTests.cs:79–160`).
     On x64 CI the strict branch never runs (cosine ≥ 0.95 + token-ids only). `CaptureGoldenVectors`
     self-skips unless `AIRACCOON_GOLDEN_CAPTURE=1`.
     **CONFIRMED CAUSE (measured this session):** the ORT bump in `0d46f2cb` (2026-09-22) from
     `Microsoft.ML.OnnxRuntime` 1.29.0 → 1.30.0 — bundled with a 44-line dependency update
     (DotNext 6.6.2→6.8.0, Dapper 2.1.79→2.1.86, OpenAI 2.12.0→2.14.0, OpenTelemetry 1.18.0→1.19.1,
     System.CommandLine 2.0.11→2.0.12, …) — under the commit message
     `chore(skills): drop retired ai-badger skill files`, which mentions none of it. CI ran
     (Directory.Packages.props matches CODE_REGEX) and passed on x64, where the strict branch is
     inert. **Experiment**: scratch worktree at HEAD with only ORT re-pinned to 1.29.0 →
     `dotnet test --filter "FullyQualifiedName~BundledMiniLm_Embeddings_MatchCommittedGoldens"`
     → `Passed! total 1, failed 0` (41.7s). So ORT 1.30.0 changes the arm64 float output
     bit-for-bit vs the 1.29.0-era capture; the tripwire fired correctly; goldens need a
     conscious recapture under 1.30.0 (or the bump reverted).
  2. `ParityGateTests.FusedSearch_P95LatencyWithinBudget` — p95 1249.3 ms vs 1000 ms over 612
     queries. This test carries `[Trait(Performance, Benchmark)]`, is **excluded from every CI lane**
     (`Performance!=Benchmark`) and is documented to run alone (`--filter "Performance=Benchmark"`).
     It ran inside the full suite while a Rider/ReSharper test session competed for CPU — an artifact
     of the run conditions. **Solo re-run verdict (measured this session): Passed — total 3, failed 0,
     succeeded 3, 47s.** The budget holds alone; the full-suite red is contention, not a defect.
- **Runtime skips observed in the baseline run** (enumerated from the run log): 2×
  `GraphPooledOutputParityTests` (need `AIRACCOON_POOLING_PARITY_MODEL_DIR`), `CaptureGoldenVectors`,
  `CodeEngineRealModelEmbedTests` (need `AIRACCOON_CODE_MODEL_DIR`), `AIRACCOON_PLATFORM_PROBE`
  probe, plus BDD "Ignored" scenarios. Only one *static* `[Fact(Skip=…)]` exists in the suite
  (`ModelMigrationCrashRecoveryE2ETests.cs:295`).

## Prior review artefacts — LEADS, NOT FACTS. Re-verify at path:line.

- `docs/reviews/2026-08-21-delta-review.md` (89 lines, integrated) and
  `docs/reviews/lanes/2026-08-21-delta-*.md` (6 lanes, 55 findings). 14/14 owner rulings APPROVE;
  canonical rulings: `docs/work/2026-08-21-delta-review-owner-rulings.md`.
- `docs/reviews/2026-08-14-project-scope-review.md` (555 lines) + `docs/reviews/lanes/2026-08-14-lane-*.md` (8 lanes).
- Proposed (check whether implemented — do not assume):
  `docs/plans/2026-09-03-bank-quality-audit-plan.md`, `docs/plans/2026-09-03-search-signal-preservation-plan.md`.
- `docs/adr/` — 104 ADRs (0101–0104 are the newest: repair verdicts / alias map / run-until-fixed / stdio removal).
- Delta review `## Still open` (carried): D3 dim-check-at-open perf question; whether the global-tool
  path hits H-A (auto-start dead on unpackaged invocation); whether any Nightly workflow run actually
  executed recently; mechanism of the full-suite seed-embed slowdown (QA F3); H1 ranking-field
  semantics; leave-one-family-out on RRF parameters.

## Isolation and safety rules — binding for every lane

1. **Read-only.** Do not modify any tracked file in your worktree. Do not commit. Mutations for
   gate-testing are allowed ONLY for the test-QA lane, inside its own worktree, and must be reverted.
2. **Never touch the user's live bank.** The default data root is `~/.ai-raccoon` and holds real data.
   Run the product only as `ai-raccoon --data-root docs/work/2026-09-22-project-scope-review/<lane>/data …` (the flag must
   precede the verb). Do **not** call `mcp_ai-raccoon_*` tools from your session — that MCP server
   points at the live bank. Start your own scratch server if you need MCP.
3. **Write only here**: `docs/work/2026-09-22-project-scope-review/<lane>/` (scratch banks, logs) and your final report at
   `docs/work/2026-09-22-project-scope-review/lanes/<lane>.md`. Nothing inside the repo.
4. **Output contract** (every finding):
   ```
   ### F<n> — <claim, present tense> [MEASURED|READ|INFERRED|UNVERIFIED]
   **Severity:** BLOCKER | HIGH | MEDIUM | LOW | NIT
   **Evidence:** <path:line, or the command you ran + its output>
   <2–6 lines: what is wrong, what it costs, smallest fix>
   ```
   Grade goes at the end of the claim line, from the closed set, describing how YOU know it.
   MEASURED/READ require Evidence. End with `## Still open` (what you did not resolve and why),
   a grade-mix line, and `## Owner questions` — one line per decision an owner can rule on.
5. **If a lead is wrong, proving it wrong is a first-class result** — worth more than confirming it.
   State withdrawn findings explicitly. Do not propose plans or estimate schedules.
6. **At least a few MEASURED findings** — run something (the CLI with a scratch data root, sqlite3 on
   a scratch bank, a targeted test, a script). A lane whose findings are all READ reviewed the code's
   description of itself.
7. Save your full report to `docs/work/2026-09-22-project-scope-review/lanes/<lane>.md` AND return it as your final message.

## Campaign operations notes (for the assembler, not the lanes)

- **Dispatch model override.** Persona pins `model: opus` / `model: sonnet` resolve in this
  environment to amazon-bedrock, which has no API key; nine first-wave lanes hung with
  `No API key found for amazon-bedrock` and were aborted (d-1405..d-1414). Recovery: every lane
  is dispatched with an explicit `model: openrouter/deepseek/deepseek-v4.1-flash` (the
  model-groups.json preferred pin for both high and medium tiers in this repo). Four lanes ran
  via `queue add` (d-1415 A, d-1417 C, d-1419 E, d-1421 G); the six cancelled lanes were
  re-dispatched via `delegate` after a temporary persona-pin edit that was reverted immediately
  after dispatch (the child argv is built at tool-call time and carried in the queued request).
- **Dispatch cap** is 4 concurrent; lanes beyond it queue and are admitted on release.
- **Scratch experiment worktree**: `.ai-badger/worktrees/psr-baseline-ort` exists solely for the
  ORT 1.29.0-vs-1.30.0 golden experiment (Directory.Packages.props locally re-pinned; not a
  review lane's worktree).
