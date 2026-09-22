# Lane A — Architecture & layering

Repo: `/Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/psr-a-architecture`

Base: `5bca1900` · Worktree HEAD `5bca1900` (clean; no tracked file modified, nothing committed).
Read-only throughout: `dotnet build`/`dotnet test --no-build`, `dotnet list … reference`, `git`,
`grep`, `strings` on a built dll, and the installed `ai-raccoon 1.42.5+8a1f1dde` against

`--data-root docs/work/2026-09-22-project-scope-review/a-architecture/data`. `~/.ai-raccoon` was never read or written and no
`mcp_ai-raccoon_*` tool was called. Land measured in this worktree: `Build succeeded. 0 Warning(s), 0 Error(s)`.

Method: reference graph (csproj + `dotnet list reference` + metadata of the built Core dll); delta
inventory (`git diff 155f281e..HEAD`, per-file line growth recomputed from both revisions);
port/interfac census by grep across `src/`; the two size ratchets read and run; targeted live CLI
probes for ADR-0104; dead-code candidates proved by exhaustive grep.

---

### F1 — The two size ratchets no longer bind, and the delta's growth landed entirely outside them [MEASURED]

**Severity:** MEDIUM
**Evidence:** `tests/AiRaccoon.Tests/Unit/Storage/SqliteMemoryStoreSizeRatchetTests.cs:80-81` pins `MaxLines = 1066`, `MaxMembers = 27`; measured `wc -l src/…/Memory/SqliteMemoryStore.cs` = **1046**, and the port's declared members = **26** (was 27 at `155f281e`; the delta removed `ConfigureEmbeddingAsync` per ruling D4). Both tests pass: `dotnet test --project tests/AiRaccoon.Tests --no-build --filter "FullyQualifiedName~SqliteMemoryStoreSizeRatchetTests"` → `Passed! total 2, failed 0`. Per-file line growth `155f281e..HEAD` (recomputed from both blobs): `MemorySchema.cs` 1446→**2216 (+770, now the largest source file in the repo)**, `SyncService.cs` 469→**916 (+447)**, `SettingsCommands.cs` +168→769, `CliCommandTree.cs` +63→679; the gated `SqliteMemoryStore.cs` grew +65 and stayed under its cap.

The file whose whole raise history is "LOWERED, not raised — five times" is now the file that grew
least, and the D4 removal freed exactly the member slot this gate exists to protect. `MemorySchema`
(DDL + 14 migration rungs + one-shot repairs + `AsyncLocal` test hooks in one static class) has no
cap at all, so the delta's +770 lines there were invisible to every gate. Smallest fix: lower
`MaxLines`/`MaxMembers` to today's measurements and add one cap for `MemorySchema.cs` and `SyncService.cs`.

### F2 — Project-id enforcement rides a mutable process-static global that Core's own key factories read [READ]

**Severity:** MEDIUM
**Evidence:** `src/AiRaccoon.Core/Projects/ProjectIdAliasMap.cs:33-60` (`static _default`, `Lock`, `Default`, `ReplaceDefault`); readers `src/AiRaccoon/Tools/ToolGate.cs:68-69`, `src/AiRaccoon.Infrastructure/Watch/WatchService.cs:122`, `WatchDigestExecutor.cs:39`, `Sync/SyncService.cs:29,62`, `src/AiRaccoon/Observability/ToolTelemetry.cs:116,125`, `src/AiRaccoon/Setup/Cli/Commands/WatchCommands.cs:149`, and three Core key factories: `Core/Access/AccessModePolicy.cs:15`, `Core/Watch/WatchConfigKeys.cs:15,19`, `Core/Ingestion/IngestScopeKeys.cs:24`; the sole writer is `Infrastructure/Sqlite/ProjectIdAliases.cs:125`, reached from three reload legs (repair job `:109`, startup warm, sync pull).

ADR-0102 rules the cached `Default` and even records that "three missing test teardowns bit once", so
this is a ruled trade — but the *shape* is an ambient global rather than a port: `AccessModePolicy.ProjectKey`,
`WatchConfigKeys.EnabledKey` and `IngestScopeKeys.ScopeKey` are documented as pure key factories and
are no longer pure, a unit test cannot substitute a map, and enforcement state is per-process rather
than per-bank. Smallest fix: take the map as a parameter in those three factories and in `ToolGate`,
leaving `Default` as the process cache behind a port.

### F3 — `ISearchParametersSettings` is a dead nine-member port in Core; its implementation never landed [MEASURED]

**Severity:** LOW
**Evidence:** `src/AiRaccoon.Core/Memory/ISearchParametersSettings.cs:10` — exhaustive search,
`grep -rn "SearchParametersSettings" src tests benchmarks --include=*.cs` → **1 hit (the declaration)**;
`grep -rn "ISearchParametersSettings" .` → the file, `docs/adr/0083-search-parameters-unified-source.md:49`, `docs/work/2026-08-20-search-parameters-plan.md:107,129,182`.
The plan specifies `SqliteSearchParametersSettings : ISearchParametersSettings`; the interface's own
doc says the per-search path uses the store's batched snapshot "instead of this interface". So the
shipped seam is the plan's abandoned first draft: it compiles, it invites a future contributor to
wire to it, and nothing answers. Smallest fix: delete the file, or implement the plan's class.

### F4 — No gate bounds Core's third-party surface: the reference allowlist the repo's own invariant asks for does not exist [READ]

**Severity:** LOW
**Evidence:** `LayeringRulesTests.cs:39-49` (Rule 1) asserts only that the names of the two sibling *project* assemblies are absent; `:63-88` (Rule 2) filters dependency targets starting with `System.Net.`. The invariant `.ai-badger/invariants/clean-architecture-layering.md` states the preferred shape: "Prefer a **reference allowlist**: assert the domain assembly's `GetReferencedAssemblies()` is a subset of an approved set … it rejects the next infrastructure package nobody thought to deny". Measured current state (metadata of the built dll, `strings src/AiRaccoon.Core/bin/Debug/net10.0/AiRaccoon.Core.dll`): exactly three non-BCL references — `CommunityToolkit.Diagnostics`, `FluentValidation`, `System.Numerics.Tensors` (ADR-0001, ADR-0017) — plus BCL.

Core is clean today; the gap is that `PackageReference Include="Azure.Storage.Blobs"` in
`AiRaccoon.Core.csproj` would fail nothing (its dependency targets are `Azure.*`, not `System.Net.*`).
Smallest fix: one `[Fact]` asserting `CoreAssembly.GetReferencedAssemblies()` ⊆ the three approved
names plus a BCL prefix, with the empty-set positive control the file already uses elsewhere.

### F5 — `ProjectIdAliasMap.LoadFromFile` is production-dead, test-only, and the only file read in Core [MEASURED]

**Severity:** LOW
**Evidence:** `src/AiRaccoon.Core/Projects/ProjectIdAliasMap.cs:176-190` (`File.ReadAllText`); `grep -rn LoadFromFile src tests benchmarks` → declaration + 3 tests in `tests/…/Unit/Projects/ProjectIdAliasMapTests.cs:120,160,170`, **no production caller** (the CLI reads the map itself: `Setup/Cli/Commands/ProjectIdsRepairCommands.cs:90` `File.ReadAllText(mapPath)` then `FromJson`). Whole-Core filesystem census: `grep -rn "\bFile\.\|Directory\.Exists" src/AiRaccoon.Core` → 2 hits (this one, plus `Ingestion/IngestPath.cs:102` `File.ResolveLinkTarget`).

The domain assembly performs filesystem I/O for a method nobody calls, and three tests exist to keep
that dead path covered — the D4-style fix is deletion, after which Core's remaining filesystem touch
is the symlink check the ingest containment rule genuinely needs.

### F6 — A test double is shipped in the production Infrastructure assembly, and the tests don't use it as the single fake [MEASURED]

**Severity:** LOW
**Evidence:** `src/AiRaccoon.Infrastructure/Sync/FakeCloudStore.cs:7` — `public sealed class FakeCloudStore : ICloudStore`, the **only** `Fake*` type under `src/`; no production caller (`SyncCloudStoreFactory` resolves S3/Azure/Null), consumers are tests. Four test files nevertheless define their own private copies (`Unit/Mcp/MemoryToolsAccessModeTests.cs:365`, `Unit/Mcp/MemoryToolsTests.cs:1087`, `Unit/Observability/MemoryToolsInstrumentationTests.cs:304`, `McpExceptionPathInstrumentationTests.cs:240`), while the shipped one is used by the sync merge tests.

Cost: the packed `ai-raccoon` tool publishes a store whose purpose is to lie about the real one, and
because four private fakes coexist, an `ICloudStore` contract change can leave the shipped fake
silently wrong with no production caller to notice. Smallest fix: move it to the tests project and
fold the four private copies into it.

### F7 — `IMemoryStore` still carries the four duplicated settings members, and its effective surface is 28 methods on a ten-dependency class [MEASURED]

**Severity:** LOW
**Evidence:** `src/AiRaccoon.Core/Memory/IMemoryStore.cs:77,80,103,107` (the four settings members), each delegating at `Infrastructure/Sqlite/Memory/SqliteMemoryStore.cs:572-580`; `ISettingsStore.cs`'s own doc still says "`IMemoryStore`, which still exposes the same four members and delegates here". Declared members 26 + 2 inherited from `IModelMigrationStore` = **28**. `SqliteMemoryStore.cs:23-33` is a 10-parameter primary constructor (plus a 12-parameter overload at `:40` that chains to it); the partial family is 2539 lines over 21 files, with 24 of the 26 declared members still implemented in the 1046-line main partial.

The 0821 lane's F7 is unchanged and its "CLI never writes the bank through `IMemoryStore`" guarantee
is still convention-only — `Settings/SettingsEndpoint.cs:72` documents the bypass it would create.
Removing the four members costs ~8 lines at 572-580 plus call sites that reach settings through the
store — `Access/MemoryAccessGuard.cs:11` is the sharpest (`IMemoryStore store` whose only use in the
class is `GetSettingsByPrefixAsync`, so `ISettingsStore` drops in unchanged); it is also the seam
that would let the port fall back under a tightened cap.

### F8 — Two classes keep a narrow constructor whose hidden behaviour is "does nothing" [MEASURED]

**Severity:** NIT
**Evidence:** `Infrastructure/Maintenance/BankMaintenanceHostedService.cs:25-54` — the primary constructor leaves `_jobRunner = null` / `_jobs = []`; the 9-arg overload at `:40` chains in (`6` test constructions). `Infrastructure/Sqlite/Memory/SqliteMemoryStore.cs:23-58` — the 10-arg primary substitutes `NoOpNoiseEntryStore.Instance` / `NoOpNoiseShadowObserver.Instance`; the 12-arg overload adds the real ports (`12` test constructions). Production takes the wide path: `Setup/AppRegistrations.cs:335` `AddRequiredSingleton<IMemoryStore, SqliteMemoryStore>()`, `:218` `AddHostedService<BankMaintenanceHostedService>()`.

The delta documented the maintenance service's version as an intentional test seam; the store's is
undocumented, and nothing in `LayeringRulesTests` covers either. A test built narrowly exercises a
graph that silently has no noise filtering (store) or zero jobs (maintenance) — the exact way a
green suite can describe a product that does not exist. Smallest fix: document the store's narrow
constructor like the maintenance one, or make the no-op ports explicit arguments.

### F9 — ADR-0104's removal contract is implemented as written and holds live; the residue is exactly what the ADR accepted [MEASURED]

**Severity:** NIT (positive result)
**Evidence:** installed tool `1.42.5+8a1f1dde` (== HEAD product code) against `--data-root docs/work/2026-09-22-project-scope-review/a-architecture/data`:
`--transport stdio` → exit **9**, stderr `Hint: --transport stdio was removed; run bare 'ai-raccoon' for the proxy or 'ai-raccoon serve' for HTTP.` + `Cannot parse argument 'stdio' as --transport: expected proxy|http.`; `--transport https` → exit **9** with the same rejection; `serve --transport stdio` → exit **15** `Unrecognized command or argument '--transport'.`; `--help` → `--transport <proxy|http>`. `--transport proxy serve --port 8912 --idle-timeout 5s` → `ai-raccoon: serve ignoring --transport Proxy; serve always uses http` (EventId 602, twice: stream + logger), then a clean exit 0.
Code matches: `Setup/McpTransport.cs:3-9` keeps `Stdio`/`Https` as parse-rejected members (the ADR's keep-enum mechanics constraint), `Setup/McpServerSetup.cs:43-47` throws for them defensively, `Setup/Cli/CliArgs.cs:67-99` carries hint and rejection.

The residual `ServerConfig.Transport` plumbing is 3 read sites and one warning; that is ADR-0104 Q3's
documented long-term exception, not drift. Nothing to fix — recording it as the lane's live check
that the removal landed rather than as a defect.

### F10 — The 0821 port-placement lead is refuted by measurement; what remains is the missing generic rule [MEASURED]

**Severity:** NIT (withdrawn lead)
**Evidence:** both ports the 0821 architecture lane flagged have moved — `IPromotionQueuePruneStore` → `src/AiRaccoon.Core/Memory/`, `IWatchRegisteredStore` → `src/AiRaccoon.Core/Watch/` (the **only** file deleted in the entire `src/` delta). Tool-layer dependency census: **15** Core-declared ports reach `src/AiRaccoon/Tools` (`IMemoryStore`, `IPromotionQueue`, `IWatchService`, `ICodeSearchService`, `ISearchDispatcher`, …) against **3** Infrastructure-declared interfaces (`ISyncService`, `ISyncCloudStoreFactory`, `ISweepService`, all pre-existing); of the **6** interfaces the delta added to Infrastructure (`ICodeEmbedder`, `ICodeTokenizer`, `ICodeIngestor`, `IWatchScanInitiator`, `ISyncBlobAuthenticator`, `IReportsOutstandingRows`) **zero** reach the tool layer, while the delta added 8 Core ports. `LayeringRulesTests.cs:151-160` (Rule 4) still pins only `IMetricsReportService` by name.

So F1 of the 0821 lane is fixed and its successor pattern did not recur: new cross-layer contracts
went to Core. The gap is that the outcome is unguarded — Rule 4 remains a single-name pin, and the
owner's 0821 question ("add a generic port-placement rule instead of per-name pins") still has no
answer because the delta's ruling set (D1-D6, S1-S3, Q1-Q2, C1-C3) never included it.

---

## Still open

- **`WarnOnNonHttpTransport`'s reachable surface.** I measured it firing via `--transport proxy serve`; since `serve` rejects `--transport` after the verb and stdio/https die at parse, that is the *only* live spelling. Whether any launcher uses it was not determined.
- **`MemorySchema.cs` decomposition** — I read its 40-member inventory (DDL, 14 rungs, one-shot column/trigger repairs, `TestOnly*` `AsyncLocal` hooks) and the +770 delta, but did not evaluate which of its concerns could be split, so F1 names the size, not a split.
- **Two reds quoted from GROUND-TRUTH** (arm64 MiniLm golden, `ParityGateTests` p95) were not re-run here — retrieval and test lanes own them.
- **`StructuralNoiseModel.Trees.g.cs` (1881 lines)** excluded from the growth analysis: generated, and I did not verify the generator is checked in and reproducible, so it may be a source-of-truth question rather than a growth one.
- **Maintenance pass log** ("… job 'vacuum' ran in 7 ms" for all 7 jobs on a fresh bank, including `vec0-reclaim` and `vacuum`) — I did not establish whether `ran` means work happened or only that the guard let the job run; that changes how the log reads, and it belongs to the data/QA lanes.
- **Tool-count arithmetic is not reconciled**: `ToolMethodSizeTests` asserts the scan finds ≥26 tool bodies, `McpToolInventory.Names()` derives from reflection, the `@ignore`d BDD scenario says "17 tools" and is annotated in-file as stale against a "22-tool surface", and I counted 30 `[McpServerTool` occurrences. Three or four numbers, one surface — Lane F's to close.
- **ADR-0102's "watch boundaries refuse retired ids"** is looser than the code: watch boundaries *fold*, the refusal lives in `ToolGate`, and the repair deletes dropped ids' watch rows (`ProjectIdsRepair.cs:458`). I refuted the resurrection hazard I suspected (`WatchDigestExecutor` folding a dropped id) by reading `FoldWatchesAsync`, so this is wording, not behaviour — reported nowhere because I could not show harm.
- **`IMaintenanceJob.HasWorkAsync` DIM default `false`** (M4 lead): all 8 on-demand jobs override it and the 4 cadence jobs correctly rely on the default — no defect found, so no finding; the shape is still the "silent default" class and would break if an on-demand job forgot.
- **Isolation note:** the `dotnet test` filter needed `--no-build`; without it discovery reported "Zero tests ran" in this worktree. I did not chase it (not my lane) but every test measurement here used `--no-build`.

**Grade mix:** MEASURED 8 (F1, F3, F5, F6, F7, F8, F9, F10) · READ 2 (F2, F4) · INFERRED 0 · UNVERIFIED 0.

## Owner questions

- Lower `MaxLines`/`MaxMembers` to today's measurements (1046/26), and add a cap for `MemorySchema.cs` and `SyncService.cs`, or accept the slack?
- Should the alias map be injected into `ToolGate` and Core's three key factories instead of read as `ProjectIdAliasMap.Default`?
- Delete `ISearchParametersSettings`, or implement the plan's `SqliteSearchParametersSettings`?
- Adopt the clean-layering invariant's reference allowlist as a layering rule for `AiRaccoon.Core`?
- Delete `ProjectIdAliasMap.LoadFromFile` and its three tests?
- Move `FakeCloudStore` into the tests project (and fold the four private copies into it)?
- Drop the four settings members from `IMemoryStore` now that D4 has been paid down?
- Answer the 0821 question that is still open: one generic port-placement rule instead of Rule 4's single-name pin?
