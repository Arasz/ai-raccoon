# Integration Review: last changed points (PRs #24–#26)

**Reviewer:** Hermes agent (task `integration-review-and-docs-refresh`)
**Date:** 2026-08-05
**Base → HEAD:** `af13eb9` → `f919480` (main)
**Commits reviewed:**
- `1ad5625` fix(embedding): harden local model resolution — fail-fast, packaged model, self-heal (#24)
- `ecd3d15` release(version): bump to 1.0.5 (#25)
- `f919480` chore(ai-badger): refresh framework to 0.79.0 — ai-raccoon-memory skill, memory_search grade hook, MCP declaration (#26)
**Graph:** code-review-graph rebuilt at `f919480` (282 files, 3261 nodes, 15873 edges, `head_matches_build: true`)

---

## Method

Review based on code-review-graph `detect_changes` (risk-scored change detection, blast-radius
flows, test-gap warnings) plus a line-by-line read of the production diff, its new tests, and the
changed scripts/workflows. Gates run: `dotnet build` (0 warnings), fast unit set, integration set,
full suite.

## Graph risk summary (detect_changes vs af13eb9)

- Overall risk score: **0.65**
- 39 changed files, 30 changed functions/classes, 9 affected flows
- Risk concentrates in `BundledModel` (EnsureAsync / EnsureDownloadsAsync / IsVerifiedIn /
  DownloadAsync — risk 0.55–0.65), `EmbeddingService.CreateLocal` (0.6), new `EmbeddingBootstrap`
- Graph "test gap" warnings named `BundledModel`/`ResolveModelPath`/`EnsureAsync` — **verified
  false positives**: PR #24 added `BundledModelEnsureDownloadsTests` (4 facts), `EmbeddingServiceLocalGuardTests`
  (2 facts), `EmbeddingBootstrapTests` (3 facts) that pin exactly those paths (the graph's
  tests-for mapping does not resolve the new files).

## Findings by risk tier

### HIGH — EmbeddingService.CreateLocal fail-fast guard (PR #24)

**What changed:** a non-empty `settings.Model` that is not an existing file now throws
`InvalidOperationException` with an actionable message (resolved path, "may be a model name",
both remediation commands) instead of a cryptic ONNX `NoSuchFile` failure — the
embedding-model-resolution incident shape.

**Quality:** guard sits at the boundary (before any ONNX work); message is actionable; the
old `CreateGenerator_MissingSettingsModelPath_Throws` moved into
`EmbeddingServiceLocalGuardTests` and is strengthened (asserts message content, model-NAME
case pinned). `EmbeddingServiceConfiguredPathTests` still proves the real-path branch.

**Test status:** covered — `CreateGenerator_MissingSettingsModelPath_Throws`,
`CreateGenerator_ModelNameInSettings_ThrowsActionableError` (RED verified on merge-base by the
implementing session; both green here).

**Finding (minor):** none. Guard is a few lines, no layering impact.

### HIGH — BundledModel download/bootstrap paths (PR #24)

**What changed:** `EnsureAsync` split into `EnsureAsync` + internal `EnsureDownloadsAsync(http,
targetDir, ct)` for testability; `IsVerifiedIn` extracted; `DownloadAsync` now catches
`OperationCanceledException` (30 s bootstrap timeout); missing-asset messages re-pointed at
`ai-raccoon model set local` (the dead `AIRACCOON_EMBEDDING_MODEL` env guidance removed from
code, download script, and packaged README).

**Quality:** refactor is behavior-preserving (verified by tests + full suite); network-free
tests via `StubHandler`/`StuckHandler`; SHA mismatch and cancellation paths pinned.

**Test status:** covered — `BundledModelEnsureDownloadsTests` (failing HTTP → errors;
wrong sha → mismatch; cancellation → error not throw), `BundledModel_MissingModelMessage_RecommendsModelSetLocal`.

**Finding (minor):** the failure-mode error strings are duplicated between `MissingBundledModelMessage`
and `EmbeddingService.CreateLocal`'s inline message — acceptable (different audiences), noted only.

### MEDIUM — EmbeddingBootstrap startup self-heal (PR #24)

**What changed:** new `Setup/EmbeddingBootstrap.EnsureAtStartupAsync` — 30 s-bounded, never
throws, warns on stderr (incl. exception type) when the packaged ONNX is missing; wired in
`Program.cs` before `RunAsync`.

**Quality:** matches the "warn, never fail" contract; linked CTS guarantees the bound;
`catch (Exception)` is justified (best-effort startup check, documented).

**Test status:** covered — `EmbeddingBootstrapTests` (missing → warning, present → silent,
throws → warning).

### LOW — Version bump 1.0.5 (PR #25)

csproj `PackageVersion`/`InformationalVersion`/`AssemblyVersion`, `.mcp/server.json` versions,
`VersionContractTests.ExpectedVersion` — all consistent; the contract tests pin agreement.
No drift found (grep for `1.0.4` in live docs: none).

### LOW — Framework refresh 0.79.0 (PR #26)

`.ai-badger/`, CLAUDE.md/HERMES.md/copilot-instructions projections, `.mcp.json` ai-raccoon
declaration, memory-grade hook scripts. Reviewed: `memory_grade_hook.py` is advisory-only
(never blocks, never decides); `memory_grade.py` gates on `AI_BADGER_MEMORY_GRADE=1` (default
off: no reads/writes/injection), IO internally guarded. Projections consistent with sources
(diff = only the managed-header comment).

## Docs sync audit

Audited README.md (root), docs/reference/agent-memory-server.md, src/AiRaccoon/README.md
(packaged), framework projections, ADRs/features/how-to for stale claims vs code.

**In sync:** tool count (19 = 16 memory + 3 watch; 2 prompts), env-var contract (only
`AIRACCOON_DB_PASSPHRASE` read — verified against `GetEnvironmentVariable` in src), launch
flags (`--transport/--data-root/--install-scope`, https → warning), CLI verb tree vs
CliCommandTree, embedding engine matrix (`model set local/openai`), `memory_configure` /
`memory_set_structure_alpha` removal notes (accurate — commit ba7ec8d), no stale `1.0.4` or
`AIRACCOON_EMBEDDING_MODEL` in live docs (remaining refs are in dated plan/work records).

**Gap found + fixed:** `docs/reference/agent-memory-server.md` compact verb block omitted
`watch registered [{project-id}]` and `watch remove {project-id|*}` (present in code
CliCommandTree.cs:172,174 and in that doc's own prose). Both verbs added to the block.

## Changes landed in this task

1. **ConfigVerbRunner extraction (architect plan → dotnet-engineer, TDD):** the config-verbs
   one-shot composition moved out of Program.cs into `Setup/ConfigVerbRunner.cs` (internal
   static, single caller, no injection seams — abstraction with no buyer avoided). Program.cs
   is now a thin entry file. 6 new tests close the composition gap (parse→config→bank→store→
   verb→exit round trip; user/project scope bank placement; watch-store wiring; env-key
   resolver). `ConfigCommands.RunAsync` signature untouched; no new deps.
2. **Code-comments cleanup:** oversized doc comments trimmed to 1–3 contract lines across 9
   files (WatchCatchUp, SqliteConnectionFactory, EncryptionCommands, CliArgs, CliRendering,
   RetrievalRatingExtension, WatchHostedService, SqliteMemoryStore, BundledModel rewrapped);
   noise/restating comments removed; contract-bearing "why" comments kept.
3. **Docs fix:** reference verb block gap above.

## Gates

- `dotnet build` — 0 warnings, 0 errors
- New tests — 6/6 (ConfigVerbRunnerTests), RED observed first by implementing session
- Fast unit set — 743/743
- Integration set — 153/153
- Full suite (worktree, model provisioned) — see run below
- Main checkout baseline: 1086 passed / 0 failed / 43 skipped (before this task's changes)

## Verdict

**APPROVE.** No blockers or majors in PRs #24–#26. The embedding-hardening change is
well-tested, the extraction is minimal and matches the architect's approved plan, the comment
cleanup is conservative (contract comments kept), and docs are now synced. One follow-up
candidate: the failure-string duplication noted above — not worth a change now.
