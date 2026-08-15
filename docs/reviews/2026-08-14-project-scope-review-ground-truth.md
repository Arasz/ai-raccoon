# Ground truth — project-scope review campaign, 2026-08-14

Base commit: `1d1889d517baf840df0b839f547091bd7f46808b` (`main`, and `origin/main` at dispatch time).
Campaign branch: `campaign/project-scope-review-0814`.
Campaign worktree: `.ai-badger/worktrees/campaign-review`.

Everything in this file was **run or read at that commit** by the orchestrator. Lanes are told to
trust this over anything a plan, ADR or prior review says.

## Build

```
dotnet build   → Build succeeded. 0 Warning(s), 0 Error(s). 7.54 s (warm restore).
```

`Directory.Build.props` sets `TreatWarningsAsErrors=true`, `Nullable=enable`,
`EnableNETAnalyzers=true`, `AnalysisLevel=latest`, `EnforceCodeStyleInBuild=true`.
It also sets `<NoWarn>NU1901;NU1903</NoWarn>` — low and moderate severity **NuGet vulnerability
advisories are suppressed build-wide**. Recorded as a fact here; whether it is a defect is a lane
question.

## Test suite

```
dotnet test --no-build
  → Failed: 0, Passed: 2861, Skipped: 9, Total: 2870, Duration: 5 m 59 s
```

The suite is green. **The nine skips, enumerated** — a skip reports as green, so each one is a
place the suite measures nothing:

| Skipped test | Kind |
|---|---|
| `Integration.GateQueryVectorRegenerationTool.RegenerateGateQueryVectors` | fixture regeneration tool, skipped by design |
| `Integration.JsaaCorpusRegenerationTool.RegenerateJsaaMemoryDb` | fixture regeneration tool, skipped by design |
| `Integration.PlatformNumericsProbe.Probe_HostFingerprint_ReportsCpuAndEmbeddingAndNdcg` | host-fingerprint probe |
| `Integration.Memory.PromotionScoringRealDataTests.ScoresCorrelateWithHandLabeledUsefulness` | **the only real-data correlation check for promotion scoring** |
| BDD `All 17 tools are still listed` | `@ignore`d, documented as stale in the feature file |
| BDD `Project isolation is enforced on the cloud side via row-level security` | **a security property, unasserted** |
| BDD `The memory inspects itself through memory_inspect` | tool that does not exist |
| BDD `The server asks the agent which hashes to keep via MRTR` | unimplemented protocol |
| BDD `The store emits metrics and tracing for its own operations` | observability property, unasserted |

Prior-review baseline for comparison (at `b4581717`, 52 commits back): 2277 passed, 6 skipped,
5m34s. The suite has grown by 584 tests and 25 seconds; the skip count has grown from 6 to 9.

## Size

| Area | Files | Lines |
|---|---|---|
| `src/AiRaccoon.Core` | 106 | 6,676 |
| `src/AiRaccoon.Infrastructure` | 96 | 11,540 |
| `src/AiRaccoon` (host) | 99 | 8,326 |
| **Production total** | **301** | **26,542** |
| `tests/AiRaccoon.Tests` | 354 | 64,386 |
| `benchmarks/` | — | 2,597 |
| Python (`scripts/`, `integrations/`) | 46 files | — |

**Test-to-production ratio: 2.43 : 1.** Recorded before forming an opinion about it. The prior
review measured 2.5:1 at `b4581717`; production has grown 26,542 − 22,400 ≈ 4,100 lines and tests
have grown ≈ 8,300 lines in 52 commits.

## Relationship to the 2026-08-14 MoE review

`docs/reviews/2026-08-14-moe-codebase-review.md` was conducted at base `b4581717`, which is
**52 commits behind this base** (`git log --oneline b4581717..HEAD | wc -l` → 52; `git diff --stat
b4581717..HEAD` → 473 files, 29,252 insertions, 8,619 deletions). Its two blockers have both been
addressed since:

- **B1** (`memory_write` fabricates success for discarded content) — `WriteResult` at
  `src/AiRaccoon/Tools/MemoryTools.cs:342` is now
  `record WriteResult(string Hash, string Path, string Context, long CreatedAt, bool Stored = true,
  string? Reason = null)`. The `"noise_hash"` / `"noise_path"` sentinel returns **zero matches**
  across `src/`, `tests/` and `benchmarks/`.
- **B2** (no tool reads a memory by hash) — `TnMemoryGet` is now in the `[McpServerTool]`
  inventory.

**Lanes must not re-file either as a live finding.** They may file regressions or incomplete
follow-through, with evidence at `path:line` on *this* base.

## MCP tool surface

26 `[McpServerTool]` methods across 12 files in `src/AiRaccoon/Tools/`:
`memory_delete`, `memory_delete_context`, `memory_embed_pending`, `memory_get`,
`memory_ingest_directory`, `memory_ingest_file`, `memory_list`, `memory_promotion_discard`,
`memory_promotion_list`, `memory_search`, `memory_set_ttl`, `memory_share`, `memory_share_extract`,
`memory_stats`, `memory_sweep`, `memory_sync`, `memory_workspace_begin`,
`memory_workspace_consolidate`, `memory_workspace_discard`, `memory_workspace_status`,
`memory_write`, `record_follow_through`, `record_grade`, `watch_add`, `watch_remove`,
`watch_status`.

Three different counts for this one surface are live in the repo simultaneously:

- `tests/AiRaccoon.Tests/Unit/Mcp/ToolInventoryTests.cs:33` — method named
  `ToolsNamespace_ExposesAll24SpecTools`, **asserting `ShouldBe(26)`** at `:39`.
- `docs/work/features-native-memory/native-memory.feature:204` — `Scenario: All 17 tools are still
  listed`, `@ignore`d, with a comment claiming the real surface is **22**.

The assertion itself is correct (26). The method name and the feature comment are not.

## Layering

`src/AiRaccoon.Core/AiRaccoon.Core.csproj` has **no `ProjectReference`**. Its only package
references are `FluentValidation`, `CommunityToolkit.Diagnostics`, `Polly.Core`,
`System.Numerics.Tensors`. `grep -rn "using Microsoft.Extensions" src/AiRaccoon.Core/` returns
**nothing** — no hosting/DI/logging framework leakage into the domain layer.

## CI

Four workflows: `build.yml`, `labeler.yml`, `nightly.yml`, `publish.yml`.

- **Every `uses:` is pinned to a full 40-character commit SHA** — `grep -rn "uses:"
  .github/workflows/ | grep -v "@[0-9a-f]\{40\}"` returns nothing.
- `permissions:` declared at workflow level on `build.yml` (`contents: read`), `nightly.yml`
  (`contents: read`), `publish.yml` (`contents: read`, plus job-level `id-token: write` for OIDC),
  and job level on `labeler.yml` (`contents: read`, `pull-requests: write`).
- PR gate runs three filtered jobs: `--filter "Speed=Fast"` (`build.yml:60`),
  `--filter "Category=bdd"` (`:81`), `--filter "Speed=Slow"` (`:113`). `nightly.yml:42` runs the
  whole suite unfiltered. **Whether the three filters partition the suite is a lane question** —
  the prior review verified it at `b4581717` (1658 + 142 + 483 = 2283); it has not been re-verified
  at this base.

Trait declarations counted across `tests/`:
`Speed=Fast` 209, `Speed=Slow` 93 (302 total) · `Category=Unit` 174, `Category=Integration` 104,
`Category=Retrieval` 12, `Category=E2E` 12 (302 total). `Category=bdd` comes from feature-file tags,
not `TestCategories`.

## Version and release

`src/AiRaccoon/AiRaccoon.csproj:14` → `<PackageVersion>1.12.0</PackageVersion>`. Latest tag
`v1.12.0`. Bump the version **once** for this campaign, not per wave.

## Feature files are in `docs/`

Reqnroll feature files live at `docs/work/features-native-memory/native-memory.feature` and
`docs/work/features-agent-memory/agent-memory.feature` — the executable spec is in the docs tree,
not under `tests/`. `find tests -name "*.feature"` returns nothing.
