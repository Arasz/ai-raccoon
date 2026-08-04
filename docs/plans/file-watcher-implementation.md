# File Watcher — Implementation Plan (part 2 of native-memory)

**Task:** `file-watcher`
**Source design:** `docs/features/file-watcher/spec.json` (manifest, 10 ruled cards D1–D10) + `docs/features/file-watcher/file-watcher.feature` (behavioral contract, 20 rules / 60 scenarios)
**Project:** AiRaccoon — C# .NET 10 MCP server over sqlite-memory
**Worktree:** `.ai-badger/worktrees/file-watcher` (branch `task/file-watcher`) — all work happens here
**Date:** 2026-08-04

---

## 1. Overview

Implement the file-watcher feature: three MCP tools (`memory_watch_add` / `memory_watch_status` / `memory_watch_remove`), a persisted `watches` table, a background mirror pipeline (FileSystemWatcher events → single channel → per-path pending → 1s tick → per-path digests, concurrency limit 4, round-robin across watches), CLI-only watch configuration (`watch enable|disable` / `watch scope add|remove|list`), hash-skip (SHA-256 path+content), retry with exponential backoff (max 5 → `stopped`), and catch-up re-ingest on restart via a per-watch last-change timestamp.

**Pass condition (from the task skill):** the **non-deferred scenarios of `file-watcher.feature` are the acceptance criteria**. The feature is done only when every scenario passes in a Reqnroll suite wired exactly like the native-memory suite, plus the unit-level implementation tests the spec explicitly keeps (card D5: concurrency, watcher-loop containment, hash-skip internals).

### Deliverables map

| # | Deliverable | Lands in |
|---|---|---|
| 1 | Store primitives: delete-by-source-path port method + watches/watch_files tables + settings keys | Core + Infrastructure |
| 2 | Watch domain: path normalization, config resolution (enable/scope, more-specific-wins), status states, `IWatchService` port | `src/AiRaccoon.Core/Watch/` (new) |
| 3 | CLI `watch` subcommands (CLI-only config channel) | `src/AiRaccoon/Setup/` |
| 4 | Mirror pipeline core (channel, debounce, scheduler, hash-skip, retry, status) | `src/AiRaccoon.Infrastructure/Watch/` (new) |
| 5 | FileSystemWatcher adapter, catch-up scan, restart re-watch, hosted lifecycle | `src/AiRaccoon.Infrastructure/Watch/` (new) |
| 6 | Three MCP tools + DI + tool-inventory update | `src/AiRaccoon/Tools/`, `Setup/` |
| 7 | Reqnroll scenario suite for `file-watcher.feature` (the 60-scenario acceptance contract) | `tests/AiRaccoon.Tests/BDD/` |
| 8 | Full-suite gate + delete/rename propagation verification | — |

---

## 2. Design facts already ruled (cards — do NOT re-decide)

- **D1** catch-up: per-watch last-change timestamp; restart re-ingests targets changed since it. No "stale" state.
- **D2** rename onto existing path: overwrite — incoming content wins, target's previous chunks are replaced.
- **D3** path identity: `Path.GetFullPath` (absolute, separators and `..` resolved, trailing separator stripped); case comparison follows host OS.
- **D4** status states: `scanning / healthy / retrying / stopped`.
- **D5** concurrency/hash-skip/watcher-loop containment internals stay **implementation tests** (unit-level).
- **D6** project-scoped watches only; all three tools take `projectId`.
- **D7/D8** tiers: add/remove require rw+; status open to every tier; background mirroring untiered. Watches are NOT "settings that affect other agents".
- **D9** opt-in: disabled until enabled; scope allowlist (absolute path covers dir + subtree); outside scope or disabled → tool error.
- **D10** config channel: CLI-only, user-facing, no tier checks. `watch enable|disable {project-id|*} {true|false}`, `watch scope add|remove|list {project-id|*} {path}`; `*` = all projects, more specific wins; persisted.
- Deferred (D11/D12): legacy config-channel conversion, full CLI surface for other options — out of scope.

---

## 3. Codebase review findings the plan is grounded in

Read (not guessed): `MemoryTools.cs` (17→18 tools, `TN_*` consts, `RequireProjectId`/`RequireAsync`, per-tool `RecordInvocation`), `Setup/CliArgs.cs` (System.CommandLine 2.0.10, 12 `Option<` declarations — 9 functional + 3 hidden host-config flags, `Render` → stderr), `Program.cs`, `Setup/ServerConfig.cs`, `Setup/Dependencies.cs` (DI), `Setup/McpServerSetup.cs` (`WithTools<MemoryTools>()`), `Access/MemoryAccessGuard.cs` + `Core/Access/*`, `Infrastructure/Sqlite/{MemorySchema,MemorySql,SqliteMemoryStore,SqliteConnectionFactory}.cs`, `Core/Memory/{IMemoryStore,ContentHash}.cs`, `Core/Rating/{IMemoryExtension,MemoryExtensionHost}.cs`, `docs/work/2026-08-04-memory-model-gap-analysis.md`, tests (`ToolInventoryTests`, `MemoryStorePortTests`, `MemoryToolsAccessModeTests`, `CliArgsTests`, `CliOutputRoutingTests`, `NativeMemorySteps`, `MemoryFeatureContext`, `Hooks`, `reqnroll.json`, `AiRaccoon.Tests.csproj`, `TestCategories`).

Findings that shape the plan:

1. **The ingest path is additive-only — mirror semantics require a new store primitive.** `InsertChunksAsync` (SqliteMemoryStore ~L746) dedups per chunk via `EntryExistsByPathAndHashInBucket` and **never removes stale chunks** for a path. `IngestFileAsync` therefore *accumulates*; re-ingesting a changed file leaves the old content searchable. The feature's mirror rules ("A delete event removes the deleted file's chunks", "Repeated saves … no intermediate save's content is ever searchable", "Renaming onto an existing path … target's previous chunks are replaced") demand **replace-by-path** digest semantics. There is no delete-by-source-file-path in `IMemoryStore` (only `DeleteByHashAndProject`, `DeleteContextAsync`). **S1 adds `DeleteSourcePathAsync(projectId, path)` to the port** (Core interface + `MemoryExtensionHost` pass-through + `MemorySql` const + `SqliteMemoryStore` impl + port test). FTS/vec cleanup is automatic via existing triggers.
2. **`ToolInventoryTests` hard-codes 18 tools in two assertions** (`MemoryTools_ExposesAll18SpecTools`, `McpToolNames_MatchConstStrings` — both count `typeof(MemoryTools)` methods). Adding the three watch tools **inside `MemoryTools`** forces edits to a 795-line file plus both count assertions. The plan instead puts the three tools in a **new `Tools/WatchTools.cs`** class and registers it with a second `.WithTools<WatchTools>()`; `MemoryTools` and its inventory tests stay untouched (a new small inventory test covers `WatchTools`). This also gives S6 exclusive file ownership.
3. **Port changes ripple:** `MemoryStorePortTests` drives `IMemoryStore` through a `RecordingStore` that implements the whole interface — S1 must update it in the same commit as the interface (same-file serialization, see §6).
4. **Schema is idempotent and migration-safe:** `MemorySchema.EnsureAsync` runs `CREATE TABLE IF NOT EXISTS` DDL + `MigrateAsync` on every bank open — the `watches` / `watch_files` tables and any settings keys slot in there with zero migration ceremony (new tables only; no ALTERs needed).
5. **Settings are a generic key/value table** (`settings(key, value)`), already used by access modes (`access.mode.global`, `access.mode.project:{id}` — same "more specific wins" pattern the spec demands for watch config) and forgetting knobs. Watch enable/scope follow the same shape (S2 defines keys; S3 CLI writes them; server reads them at startup).
6. **CLI output convention:** every byte of CLI text goes to the caller-supplied writer (stderr in `Program.cs`) — `CliOutputRoutingTests` guards stdout for the stdio protocol. Watch commands keep this convention.
7. **CLI parse is pure and tested:** `CliArgs.Parse` returns a `CliParseResult` and never writes; `Program.cs` is 20 lines and is the only dispatch point. Watch commands need a verb branch in `Program.cs` that runs a command and exits **without starting the MCP server**.
8. **Reqnroll wiring precedent:** feature files live outside the test project and are linked via `<ReqnrollFeatureFiles>` in `AiRaccoon.Tests.csproj` (see `docs/work/features-native-memory/native-memory.feature`). Steps bind per-scenario through `MemoryFeatureContext` registered in `BDD/Hooks.cs`. `reqnroll.json` maps tags `integration`/`slow`/`e2e` → non-parallelizable. The canonical feature to link is `docs/features/file-watcher/file-watcher.feature` (the `docs/work/features-file-watcher/` copy is the elicitation draft — differs only in its title line; leave it untouched as provenance).
9. **Time control for ticks:** `Microsoft.Extensions.TimeProvider.Testing` (`FakeTimeProvider`) is already referenced and used (`MemoryFeatureContext.FixedNow`) — the 1s tick is testable deterministically.
10. **`OnSourceChanged` does not exist** — the gap analysis (2026-08-04) proposed it as the watcher hook, but the spec's accepted design (card D10 + scope.in) routes the watcher straight to the existing ingest path, not through `IMemoryExtension`. **No extension-hook work is planned**; the digest executor calls `IMemoryStore` directly (through the same `MemoryExtensionHost`-decorated instance the MCP layer uses, so hooks still observe watcher writes — a free side effect, not a requirement).
11. **TreatWarningsAsErrors** is on via `Directory.Build.props`; bare `dotnet build` / `dotnet test` from the worktree root are the canonical gates.
12. **xunit v3 + Reqnroll:** `Console.WriteLine` is not captured in test output — use `ITestOutputHelper` for diagnostics (project convention, per task context).

---

## 4. Architecture map (where each piece lands)

```
src/AiRaccoon.Core/Watch/            (NEW — domain, infrastructure-free)
  WatchPath.cs            — D3 normalization (Path.GetFullPath; absolute; trailing sep stripped; host-OS case)
  WatchConfigKeys.cs      — settings keys (enable/scope, global + per-project)
  WatchConfig.cs          — resolution: project entry wins over global; enable default false; scope list
  WatchState.cs           — enum scanning/healthy/retrying/stopped + WatchStatus record (state, lastError, lastSync)
  IWatchService.cs        — AddAsync/RemoveAsync/StatusAsync(projectId) — the port the MCP tools call

src/AiRaccoon.Infrastructure/Watch/  (NEW — pipeline)
  WatchStore.cs           — watches + watch_files persistence (Dapper over memory.db)
  WatchEventSource.cs     — FileSystemWatcher adapter (created/changed/renamed/deleted → WatchEvent)
  WatchPipeline.cs        — single channel, per-path pending aggregation, 1s tick, digest executor
  WatchScheduler.cs       — concurrency limit (default 4) + round-robin across watches
  WatchDigestExecutor.cs  — replace-by-path digest (DeleteSourcePathAsync + IngestFileAsync), hash-skip
  WatchRetryPolicy.cs     — exponential backoff, max 5 → stopped (keeps registration + status)
  WatchService.cs         — implements IWatchService (S2 port): add/remove/status, enable+scope resolution, per-watch orchestration (OWNED BY S4; consumed by S6)
  WatchCatchUp.cs         — restart scan: mtime > last-change-ts → re-queue; updates watch.last_change_ts
  WatchHostedService.cs   — BackgroundService: start watchers on boot, dispose on stop

src/AiRaccoon.Infrastructure/Sqlite/ (TOUCHED by S1)
  MemorySchema.cs         — + watches, watch_files DDL (idempotent)
  MemorySql.cs            — + DeleteBySourcePath, watch CRUD consts
  SqliteMemoryStore.cs    — + DeleteSourcePathAsync

src/AiRaccoon.Core/Memory/IMemoryStore.cs + src/AiRaccoon.Core/Rating/MemoryExtensionHost.cs
                          — + DeleteSourcePathAsync (S1)

src/AiRaccoon/Setup/
  CliArgs.cs              — + `watch` verb (S3)
  WatchCommands.cs        — NEW: enable/disable/scope add/remove/list runners (S3)
  McpServerSetup.cs       — + .WithTools<WatchTools>() (S6)
  Dependencies.cs         — + WatchService/WatchStore/WatchHostedService registrations (S6)

src/AiRaccoon/Tools/WatchTools.cs    — NEW: memory_watch_add/status/remove (S6)

tests/AiRaccoon.Tests/
  Unit/Memory/MemoryStorePortTests.cs          — + DeleteSourcePathAsync port test (S1)
  Unit/Watch/…                                 — NEW: config/path/scheduler/hash-skip/retry tests (S2+S4)
  Unit/Setup/CliArgsTests.cs + WatchCommandsTests.cs (NEW) (S3)
  Integration/SqliteMemoryStoreIntegrationTests.cs — + delete-by-source-path scenario (S1)
  Integration/WatchIntegrationTests.cs         — NEW: real-FS adapter, catch-up, restart (S5)
  Unit/Mcp/WatchToolsTests.cs + WatchToolsAccessModeTests.cs (NEW) + ToolInventoryTests (S6)
  BDD/FileWatcherFeatureContext.cs + FileWatcherSteps.cs (NEW), BDD/Hooks.cs (S7)
  AiRaccoon.Tests.csproj                       — + ReqnrollFeatureFiles link (S7)
```

---

## 5. Settings keys and tables (fixed in S1/S2, used by S3)

**Settings** (shape follows `AccessModePolicy`):

| Key | Value | Notes |
|---|---|---|
| `watch.enabled.global` | `"true"`/`"false"` | default absent = false (opt-in) |
| `watch.enabled.project:{projectId}` | `"true"`/`"false"` | more specific wins |
| `watch.scope.global` | JSON array of absolute paths | empty/absent = empty allowlist |
| `watch.scope.project:{projectId}` | JSON array of absolute paths | more specific wins |

**Tables** (in `MemorySchema.Ddl`, idempotent):

```sql
CREATE TABLE IF NOT EXISTS watches (
    project_id      TEXT NOT NULL,
    path            TEXT NOT NULL,          -- normalized per D3
    created_at      INTEGER NOT NULL,
    last_change_ts  INTEGER NOT NULL,       -- catch-up watermark (D1)
    PRIMARY KEY (project_id, path)
);
CREATE TABLE IF NOT EXISTS watch_files (   -- per-path fingerprint for hash-skip
    project_id      TEXT NOT NULL,
    path            TEXT NOT NULL,          -- normalized
    file_hash       TEXT NOT NULL,          -- SHA-256(path + full content)
    updated_at      INTEGER NOT NULL,
    PRIMARY KEY (project_id, path)
);
CREATE INDEX IF NOT EXISTS idx_watches_project ON watches(project_id);
```

Status/retry state is **runtime-only** (in-memory per watch), not persisted — persisted state is registration + watermark; `scanning/healthy/retrying/stopped` are the live view (`stopped` re-derives on restart because catch-up replaces the "stale" concept, D1).

---

## 6. Section decomposition & parallelism

### Parallelism waves

```
Wave 1 (parallel):  S1  S2  S3        — S3 reads S2's key constants from the plan; no shared files
Wave 2 (parallel):  S4  S6            — both depend on S2's port only; S6 needs no S4 internals
Wave 3 (parallel):  S5  (S7 prep)     — S5 depends on S4; S7 step authoring can start against S5's API
Wave 4 (serial):    S7  (suite run)   — needs S1–S6 merged and building
Wave 5 (serial):    S8  (full gate)
```

**File-collision matrix (sections that share a file must NOT run concurrently):**

| File | Owned by |
|---|---|
| `Core/Memory/IMemoryStore.cs`, `Core/Rating/MemoryExtensionHost.cs`, `Infrastructure/Sqlite/MemorySchema.cs`, `MemorySql.cs`, `SqliteMemoryStore.cs`, `Unit/Memory/MemoryStorePortTests.cs`, `Integration/SqliteMemoryStoreIntegrationTests.cs` | **S1 only** |
| `Core/Watch/*` (new) | **S2 only** |
| `Setup/CliArgs.cs`, `Setup/WatchCommands.cs` (new), `Program.cs`, `Unit/Setup/CliArgsTests.cs`, `Unit/Setup/WatchCommandsTests.cs` (new) | **S3 only** |
| `Infrastructure/Watch/*` (new) | **S4 + S5** — S5 adds files; S4's files are read-only for S5. S4 defines the pipeline classes **incl. `WatchService`** (the `IWatchService` impl); S5 adds `WatchEventSource` + `WatchCatchUp` + `WatchHostedService` as *new* files and only consumes S4's public surface. Safe to run S5 after S4 merges (Wave 3), or same-wave with explicit file split. |
| `Tools/WatchTools.cs` (new), `Setup/McpServerSetup.cs`, `Unit/Mcp/WatchTools*Tests.cs` (new) | **S6 only** |
| `Setup/Dependencies.cs` | **S5 + S6** — S6 (Wave 2) registers watch services + `IWatchService`; S5 (Wave 3) appends the hosted-service + catch-up/event-source registrations. Waves are sequential, so the two edits never overlap. |
| `Unit/Mcp/ToolInventoryTests.cs` | **S6 gate-run only (READ-ONLY)** — must stay green; the existing 18-count assertions are NOT edited (WatchTools is a separate class with its own new inventory test). |
| `AiRaccoon.Tests.csproj`, `BDD/*` (Hooks, new feature context/steps) | **S7 only** |

No two waves touch the same file. `Program.cs` (S3) and `Dependencies.cs` (S6) are different files; both compile against the same solution, so **builds/tests are serialized by the orchestrator regardless** (see §7 worktree etiquette).

### Worktree etiquette (single shared worktree)

- Code edits may happen in parallel; **`dotnet build` / `dotnet test` never run concurrently** — the orchestrator runs every gate serially (shared `obj/`/`bin/`). Agents report "code done, gate pending" rather than racing the build.
- Commits are per-path: `git add <specific files>` — **never `git add -A` / `git add .`** — so one agent's uncommitted scratch never leaks into another's commit.
- Gates run from the worktree root with **targeted filters** (exact commands in each section). The full `dotnet build` + `dotnet test` runs only in S8 (and once after each wave merge, run by the orchestrator).
- Every agent's TDD order is enforced: failing test committed/visible **before** production code for that behavior.

---

## Section S1 — Store primitives (schema + delete-by-source-path)

**Wave 1. Parallel with S2, S3. Serializes: nothing else touches its files.**

### Scope
1. `IMemoryStore.DeleteSourcePathAsync(string projectId, string path, CancellationToken)` — deletes every committed entry whose `source_file = path` for the project (workspace rows excluded, matching ingest's committed-bucket shape). New `MemorySql.DeleteBySourcePath` const. Pass-through in `MemoryExtensionHost` (with `OnDeleteAsync` hook fired with a `DeleteContext` — consistent with existing delete hooks).
2. `watches` + `watch_files` DDL in `MemorySchema` (per §5) — idempotent, no migration needed.
3. Update `MemoryStorePortTests`'s `RecordingStore` for the new port method.

### TDD order (failing tests first)
1. Port test: `DeleteSourcePathAsync_IsPartOfThePort_AndCarriesProjectAndPath` (add to `MemoryStorePortTests`).
2. Integration test (`Integration/SqliteMemoryStoreIntegrationTests.cs`): ingest a file → `DeleteSourcePathAsync` → search no longer returns its content; FTS/vec rows gone (assert via `entries` count + search).
3. Schema test: open a fresh bank → `watches` and `watch_files` tables exist; opening an existing bank adds them without disturbing `entries`/FTS (idempotency: run `EnsureAsync` twice).

### Acceptance criteria
- Deleting by source path removes all chunks of that file for that project only; other projects' rows for the same path survive.
- Delete fires the extension `OnDeleteAsync` hook.
- `watches`/`watch_files` exist in every freshly opened bank and in banks created before this feature.

### Quality gate
```
dotnet test --filter "FullyQualifiedName~MemoryStorePortTests|FullyQualifiedName~SqliteMemoryStoreIntegrationTests"
```
(plus orchestrator `dotnet build` after merge)

---

## Section S2 — Watch domain (config, path rules, states, service port)

**Wave 1. Parallel with S1, S3. New files only.**

### Scope
- `WatchPath.Normalize(string)` per D3: `Path.GetFullPath`, trailing separator stripped (except root), host-OS case comparison; `IsWithinScope(watchPath, scopeEntry)` — absolute entry covers the directory and all subdirectories; a file watch is inside scope when its full path is under an entry.
- `WatchConfigKeys`: `EnabledGlobal = "watch.enabled.global"`, `EnabledProject(id)`, `ScopeGlobal = "watch.scope.global"`, `ScopeProject(id)`; JSON-array serialization helpers.
- `WatchConfig.Resolve(settings)`: enable = project entry ?? global entry ?? **false**; scope = project list ?? global list ?? empty. More specific wins (mirrors `AccessModePolicy.Resolve`).
- `WatchState` enum + `WatchStatus(projectId, path, state, lastError, lastSync)`.
- `IWatchService` port: `AddAsync(projectId, path, ct)`, `RemoveAsync(projectId, path, ct)`, `StatusAsync(projectId, ct)`, `IsEnabledAsync(projectId, ct)`, `IsPathAllowedAsync(projectId, path, ct)` — the surface S4/S6 consume. Errors: `WatchDisabledException` (→ tool error `watching-disabled`), `PathOutsideScopeException` (`path-outside-scope`), `PathNotFound` (`path-not-found`), `MissingProject` (tool-level, S6).

### TDD order
1. `Unit/Watch/WatchPathTests`: normalization table (relative → absolute, `..` resolved, trailing `/` stripped, root path, case behavior via `StringComparer` per host OS); scope containment (dir covers subtree; sibling outside; file inside; `/repo` vs `/repo-other`).
2. `Unit/Watch/WatchConfigTests`: absent → disabled + empty scope; global true + no scope; project override beats `*`; scope list resolution; JSON round-trip.
3. `Unit/Watch/WatchStateTests`: state enum serialization; status record.

### Acceptance criteria
- All path/scope/config rulings of D3/D9/D10 are pure functions with unit coverage.
- The port compiles against nothing but Core types (infrastructure-free).

### Quality gate
```
dotnet test --filter "FullyQualifiedName~AiRaccoon.Tests.Unit.Watch"
```

---

## Section S3 — CLI watch commands

**Wave 1. Parallel with S1, S2 (reads S2's key constants, which the plan fixes). Owns `CliArgs.cs`, `Program.cs`.**

### Scope
1. `CliArgs.BuildRootCommand` gains a `watch` subcommand tree: `watch enable|disable {project-id|*} {true|false}`, `watch scope add|remove|list {project-id|*} {path}`. Parse result gains a discriminated `WatchCommandRequest?` (verb, scopeKey, project-or-*, path-or-flag). Root options (`--data-root`, `--install-scope`, …) still apply so the command opens the right bank.
2. New `Setup/WatchCommands.cs`: runners that resolve `ServerConfig.Build`-style options, open the bank via `SqliteConnectionFactory`, and upsert/read settings through `SqliteMemoryStore.GetSettingAsync`/`SetSettingAsync`. `scope add` appends a normalized absolute path (dedup + re-sort); `scope remove` removes; `scope list` prints entries. `watch enable * true` with an empty allowlist prints the "add at least one scope" message (per feature scenario). All output through the caller-supplied writer (stderr convention).
3. `Program.cs` dispatch: if the parse produced a watch command → run it, return exit code, **never start the MCP server**.

### TDD order
1. `CliArgsTests`: parse `watch enable * true`, `watch disable proj-a`, `watch scope add * /docs`, `watch scope remove proj-a /docs`, `watch scope list *`, unknown watch verb → error; root options still parse.
2. `WatchCommandsTests` (new, temp bank + FakeTimeProvider): enable persists to settings; `*` vs project precedence round-trip; scope add/remove/list mutate the JSON list; enable-`*`-with-no-scope returns the message; command idempotency (add twice → one entry).
3. `CliOutputRoutingTests` addition (or same-pattern test): watch command text goes only to the injected writer, never stdout.

### Acceptance criteria
- The four feature scenarios of rule "Watch configuration is CLI-only and user-facing" pass at the unit level (survives restart = settings persisted in memory.db, verified by reopening the bank).
- No env/args/config-file channel exists for watch config (parse-level test: `--watch-enable` is an unknown option error).

### Quality gate
```
dotnet test --filter "FullyQualifiedName~CliArgsTests|FullyQualifiedName~WatchCommandsTests|FullyQualifiedName~CliOutputRoutingTests"
```

---

## Section S4 — Mirror pipeline core

**Wave 2. Parallel with S6. Depends on S2's port. New files only (`Infrastructure/Watch/`).**

### Scope
- `WatchPipeline`: single `Channel<WatchEvent>`; per-path pending aggregation (a path with ≥1 event is pending once); 1s tick (injected `TimeProvider`); each tick drains pending paths to the scheduler; events arriving during a digest keep the path pending → next tick re-digests (feature: "modified during its own digest").
- `WatchScheduler`: global concurrency limit (constant default 4, `WatchOptions.ConcurrencyLimit`), round-robin across watches so one watch's flood cannot starve others (feature rule 12).
- `WatchDigestExecutor`: **replace-by-path digest** — if the file no longer exists → `DeleteSourcePathAsync` (delete event); else compute file hash `SHA-256(path + full content)`, compare `watch_files.file_hash`; equal → skip (hash-skip, metadata-only touch); different → `DeleteSourcePathAsync` + `IngestFileAsync` (re-digest via the existing ingest path), upsert fingerprint, update `watch.last_change_ts`. Rename event → remove old-path chunks + digest new path (overwrite on collision per D2). Delete of a never-ingested file → silent no-op.
- `WatchRetryPolicy`: per-watch consecutive-failure counter; exponential backoff between attempts; after 5 consecutive failures → state `stopped`, stop checking, keep registration + status; success resets counter (feature rule 14).
- Status transitions: `scanning` (initial scan in flight) → `healthy`; failures → `retrying`; 5th failure → `stopped`; success → `healthy`. `WatchStatus` carries last error + last sync.
- Containment: every loop iteration is try/caught; no exception escapes to the MCP server (feature rule 13) — errors land in status.

### TDD order (all unit, `FakeTimeProvider`, fake event source + fake store implementing the used port slice)
1. Hash-skip: touch without content change → no digest call, fingerprint unchanged; real change → digest runs (the "never skip a real change" risk, spec risks).
2. Debounce: N events for one path in a tick → one digest; burst of create/change/rename within one tick → final content only.
3. Scheduler: 10 pending across 3 watches → ≤4 concurrent digests, all complete; flood + single-change watch → small watch processed promptly (round-robin order asserted).
4. Delete-during-digest / delete-before-digest: deterministic resolution — file gone at digest time ⇒ chunks removed, no resurrect (feature rule 8).
5. Retry: 4 failures → `retrying` + backoff schedule (asserted via fake time advance); 5th → `stopped`, registration intact; success resets (feature rule 14).
6. Loop containment: executor throws → status shows error, pipeline keeps ticking, next tick succeeds and clears the error (feature rule 13).

### Acceptance criteria
- All D5 internals (concurrency, round-robin, hash-skip, containment) pinned by unit tests.
- No real `FileSystemWatcher` in this section — the event source is an injected seam.

### Quality gate
```
dotnet test --filter "FullyQualifiedName~AiRaccoon.Tests.Unit.Watch"
```

---

## Section S5 — FileSystemWatcher adapter, catch-up, restart

**Wave 3. Depends on S4. New files only (plus `Integration/WatchIntegrationTests.cs`).**

### Scope
- `WatchEventSource`: `FileSystemWatcher` wrapper (IncludeSubdirectories, all four event types → `WatchEvent`, path normalized per D3); never throws on its own — adapter-level try/catch feeds a synthetic error event to status. Per-OS event coalescing is accepted; the catch-up watermark is the safety net (spec risk).
- `WatchCatchUp` (D1): on startup, for each registered watch: if the watch's `last_change_ts` is 0 (never synced) → full initial scan (status `scanning`); else enumerate subtree, queue files with `mtime > last_change_ts` for digest; initial scan is async — `memory_watch_add` returns immediately (feature rule 4). `last_change_ts` advances as digests complete.
- `WatchHostedService` (`BackgroundService`): on start — read enable config; load `watches`; **if watching is disabled, keep registrations but start no checking** (decision, see §10); re-watch + catch-up otherwise. On stop — dispose all `FileSystemWatcher`s.
- Integration tests (real temp dirs, real `FileSystemWatcher`, `FakeTimeProvider` for ticks, bounded polling ≤5s for OS event delivery):
  - created file becomes searchable; delete removes chunks; rename moves content and leaves nothing under the old path; rename onto existing path leaves only the incoming content (D2).
  - restart re-watch + catch-up: file changed while "down" (fingerprint/watermark behind) is re-digested; unchanged files are skipped.
  - unreadable path → status error, server (pipeline) keeps running.

### TDD order
1. Adapter unit-ish test: event translation + normalization (drive the handler directly).
2. Catch-up unit test with fake timestamps: watermark logic (never-synced → full scan; mtime > watermark → queued; equal/older → skipped).
3. Async-add test: `memory_watch_add` on a large directory returns immediately while status reports `scanning` (R4 mitigation — was S7-only; now pinned here).
4. DI smoke: hosted service + catch-up/event-source registrations resolvable (the S6-forwarded half of the DI smoke).
5. Integration scenarios above (each starts with a failing assertion).

### Acceptance criteria
- Real-FS mirror behavior for create/change/delete/rename proven on this host (macOS), including rename-overwrite.
- Restart re-watch + catch-up proven end-to-end against a real bank (persisted watches table).

### Quality gate
```
dotnet test --filter "FullyQualifiedName~WatchIntegrationTests"
```
(serialized by the orchestrator; `@slow`-class runtime expected)

---

## Section S6 — MCP tools + DI + inventory

**Wave 2. Parallel with S4. Depends on S2's `IWatchService`. Owns `Tools/WatchTools.cs`, `Setup/McpServerSetup.cs`, `Setup/Dependencies.cs`, `Unit/Mcp/*` (new + `ToolInventoryTests`).**

### Scope
1. `Tools/WatchTools.cs`: `memory_watch_add(projectId, path)` — `RequireProjectId`, access `Write` (rw+), then `IWatchService.AddAsync`; errors mapped to `McpException` codes: `watching-disabled`, `path-outside-scope`, `path-not-found`, `access-denied` (guard), `missing-project` (invalid-params). `memory_watch_status(projectId)` — access `Read` (every tier), returns watch states (state, last error, last sync), empty list when none. `memory_watch_remove(projectId, path)` — access `Write` (rw+), no-op on non-existent watch. Add on an already-watched (projectId, path) → no-op (D-note scope: per-(projectId,path) identity; normalization per D3 means `/repo` and `/repo/` are the same watch). All three follow the existing pattern: `McpServerTool` + `Description`, activity tags, `ToolCallMetrics.RecordInvocation`.
2. `McpServerSetup.ConfigureMcpServer`: add `.WithTools<WatchTools>()`.
3. `Dependencies.RegisterMemoryServices`: register `WatchStore`, `WatchPipeline`, `WatchScheduler`, `WatchDigestExecutor`, `WatchRetryPolicy`, `IWatchService` → `WatchService`. **NOT here: `WatchCatchUp`/`WatchEventSource` registrations and `AddHostedService<WatchHostedService>()` belong to S5 (Wave 3) — those types do not exist until S5.** Watch services resolve the **same** `IMemoryStore` (the `MemoryExtensionHost`-decorated instance) so hooks observe watcher writes.
4. `Unit/Mcp/WatchToolsTests` (fake `IWatchService`): param validation, result shapes, no-op semantics; `WatchToolsAccessModeTests`: ro → add/remove rejected with `access-denied`, status allowed (mirror `MemoryToolsAccessModeTests` setup). New `WatchToolsInventoryTests`: exactly 3 tools, names match `TN_*` consts. Update `ToolInventoryTests` only if tools were added to `MemoryTools` — with the separate class this is a *new* test file instead; the existing 18-count tests stay untouched.

### TDD order
1. Access-mode tests first (ro denied add/remove; status OK).
2. Tool behavior tests (errors, no-ops, result shapes).
3. Inventory test (3 tools, consts match).
4. DI smoke: `McpServerSetupTests`-style registration test — `IWatchService` resolvable (the hosted-service half lives in S5).

### Acceptance criteria
- Feature rules 1 (opt-in errors), 3 (project-scoped, missing-project), 16 (remove no-op), 17 (add no-op), 19 (rw+ add/remove), 20 (status every tier) pass at the tool-unit level.
- `dotnet build` green with the new DI graph.

### Quality gate
```
dotnet test --filter "FullyQualifiedName~WatchTools|FullyQualifiedName~ToolInventoryTests|FullyQualifiedName~McpServerSetupTests"
```

---

## Section S7 — Reqnroll scenario suite (the acceptance contract)

**Wave 4. Serial — needs S1–S6 merged. Owns `AiRaccoon.Tests.csproj`, `BDD/Hooks.cs`, new `BDD/FileWatcherFeatureContext.cs` + `BDD/FileWatcherSteps.cs`.**

### Scope
1. csproj: `<ReqnrollFeatureFiles Include="..\..\docs\features\file-watcher\file-watcher.feature"><Link>BDD\features-file-watcher\file-watcher.feature</Link></ReqnrollFeatureFiles>` (precedent: native-memory link).
2. `FileWatcherFeatureContext` (extends `MemoryFeatureContext`): real temp dirs under `DataRoot`, real `SqliteMemoryStore`, `FakeTimeProvider`, real `WatchService` stack (same composition as DI), helper to run one tick deterministically (`Advance(1s)`) and bounded polling (≤5s) for OS-event delivery.
3. `FileWatcherSteps`: bind all 60 scenarios — watch add/status/remove calls, enable/scope CLI equivalents (drive `WatchCommands` with a writer), file create/edit/delete/rename helpers, search assertions. Feature tagged `@integration @slow` (reqnroll.json already maps those to non-parallelizable).
4. **Delete/rename propagation scenarios get dedicated, belt-and-braces assertions** (highest-risk surface, spec risks): delete removes chunks (search + raw `entries` count), delete-during-digest deterministic, delete-never-ingested no-op, rename moves content, rename-before-scan-ends lands under final path, rename-onto-existing → only incoming content searchable. See §9 for the exact scenario list.
5. No `Console.WriteLine` in steps — diagnostics via `ITestOutputHelper` (xunit v3).

### TDD order
The feature file IS the failing test list: link it, generate, watch the suite fail red (missing bindings → then failing assertions), then implement steps one rule at a time. This is the TDD spine of the whole feature — the 60 scenarios are the acceptance criteria.

### Acceptance criteria
- All 60 scenarios green; zero scenarios skipped or marked pending.
- Scenario semantics honored: "within one second" via tick advance; hash-skip scenario asserts the memory entry is unchanged (same search results, no new rows); "no intermediate save's content is ever searchable" via replace-by-path.

### Quality gate
```
dotnet test --filter "FullyQualifiedName~FileWatcher"
```
(run serially by the orchestrator)

---

## Section S8 — Integration gate + risk verification

**Wave 5. Serial.**

### Scope
1. Full `dotnet build` + full `dotnet test` from the worktree root (the configured gates), run by the orchestrator alone.
2. Re-run the delete/rename propagation cluster plus retry/containment clusters under the full suite (no filter) to catch cross-suite interference (shared banks, port changes).
3. Walk the spec's `nonFunctional` list: containment, concurrency, debounce, retry, persistence, TDD evidence, TreatWarningsAsErrors, CLI-only secrets policy.
4. Verify no stray config channel: `--watch-enable`/`--watch-scope` parse as unknown options (S3 already tests; re-assert here).
5. Update docs per repo convention (CLAUDE.md tool list if it enumerates tools; README tool table if present) — small, mechanical, after the code is proven.

### Acceptance criteria
- `dotnet build` exit 0; `dotnet test` exit 0 (full suite).
- Every non-deferred feature scenario has a passing test that was red before its implementation (git history shows test-first commits).
- Owner-gate review offered with the diff + gate output (spec `gate.ownerGateReview`).

### Quality gate
```
dotnet build && dotnet test
```

---

## 7. Worktree etiquette (all sections)

- **One build/test at a time.** The orchestrator runs every gate serially; agents never run `dotnet build`/`dotnet test` concurrently (shared `obj/`/`bin/` under a single worktree). Agents commit code and report "gate pending".
- **Per-path `git add`** (`git add src/AiRaccoon.Core/Watch/... tests/...`) — never `git add -A` or `git add .`.
- Targeted `--filter` gates per section (above); the full suite only in S8 and after each wave merge.
- TDD enforced per section: the failing test lands first, then the implementation that turns it green.
- `docs/work/features-file-watcher/` (elicitation draft) is provenance — never edited.

---

## 8. Risk callouts

| # | Risk (spec + review) | Mitigation |
|---|---|---|
| R1 | **Delete/rename propagation is destructive and irreversible — highest-risk merge surface** | Dedicated scenario tests (S7) + store-level tests (S1) + real-FS integration tests (S5): delete removes chunks, delete-during-digest, rename moves identity, rename-overwrite leaves exactly one content set. Assert both via search AND raw `entries` rows. |
| R2 | **Additive ingest violates mirror semantics** (review finding: `InsertChunksAsync` never removes stale chunks) | S1 `DeleteSourcePathAsync` + S4 replace-by-path digest; feature scenario "no intermediate save ever searchable" pins it. |
| R3 | **Filesystem event loss/coalescing differs per OS** — catch-up is the safety net | Watermark updated on every processed batch (D1); S5 verifies on macOS; spec requires linux/macOS/windows verification — flag OS matrix as a follow-up run after merge (this host is macOS). |
| R4 | **Initial scan of a large directory is unbounded work** | Async by contract (add returns immediately), status `scanning`, errors surface in status; S5 test: large dir returns immediately + status scanning. |
| R5 | **Hash-skip correctness — a real change must never be skipped** | File-level SHA-256(path + full content) vs persisted fingerprint; unit test for content-change-never-skipped + metadata-touch-skipped (S4). |
| R6 | **Concurrency limit + round-robin fairness are not black-box observable** | Unit-level scheduler tests with fake executor (S4), per D5. |
| R7 | **FileSystemWatcher latency on macOS test host** | Bounded polling (≤5s) in integration steps; `@slow` tag keeps the suite honest; tick determinism via `FakeTimeProvider` so timing flake is confined to OS event delivery. |
| R8 | **Tool-count tests pin 18 tools** | Separate `WatchTools` class → existing inventory tests untouched; new inventory test for the 3 watch tools (S6). |
| R9 | **Restart with watching disabled but watches registered** (spec silent) | Decision (see §10): registrations kept, checking paused, status visible; enabling resumes checking. Flag for owner confirmation at the gate. |

---

## 9. Dedicated delete/rename propagation test map (R1)

| Feature scenario | Pinned by |
|---|---|
| Deleting a file removes its chunks from search | S1 store test + S5 integration + S7 scenario |
| Deleting a file while its digest is in flight resolves deterministically | S4 unit (fake executor) + S7 scenario |
| Deleting a never-ingested file is a silent no-op | S4 unit + S7 scenario |
| Renaming a file moves its memory to the new path | S5 integration + S7 scenario |
| Renaming before the initial scan finishes resolves deterministically | S5 integration (scan in flight) + S7 scenario |
| Renaming onto an existing path resolves deterministically (overwrite, D2) | S4 unit (executor order) + S5 integration + S7 scenario |
| Mirror removal in ro project (untiered background mirror) | S7 scenario (tier rule) |

---

## 10. Plan-blocking unknowns (none found) and decisions made at review

No plan-blocking unknowns. Five decisions the review forced (all with a recommended default; owner confirmation at the S8 gate):

1. **Replace-by-path digest requires a new port method** `DeleteSourcePathAsync` (the spec says "re-digest via existing ingest" but ingest is additive-only — mirror semantics are impossible without delete-by-source-path). **Default: add to port** (S1).
2. **"Exactly one memory entry holds that path"** (rename-overwrite scenario) vs chunking (one file = N chunk rows). **Default: interpret as "exactly one content set" — assert old content absent, new content present, no orphan chunks; not a row-count-of-1 assertion.** Step author note (S7).
3. **Restart with watching disabled:** spec rules only add-time errors. **Default: registrations persist; checking pauses while disabled; `watch enable` resumes checking.** Consistent with "stop checking but keep registration/status".
4. **Concurrency limit configurability:** spec says "configured concurrency limit (default 4)" but the only config channel is CLI enable/scope. **Default: `WatchOptions.ConcurrencyLimit = 4` as a code constant; CLI surface deferred** (consistent with D11/D12 single-channel principle).
5. **No `IMemoryExtension` work:** the gap analysis's `OnSourceChanged` hook is superseded by the spec's direct ingest-path design; watcher writes flow through the `MemoryExtensionHost`-decorated store anyway, so existing hooks observe them for free.

### Recorded deviations

- **OS matrix deferred:** the spec risk "verify event semantics on linux/macOS/windows" is scoped to a post-merge follow-up run — this host is macOS and the plan proves the real-FS mirror behavior there only (S5 acceptance). The catch-up watermark is the cross-OS safety net; the linux/Windows verification is tracked as a follow-up, not silently dropped.

---

## 11. Definition of done

- S1–S7 merged on `task/file-watcher` with per-section gates green; S8 full `dotnet build` + `dotnet test` green.
- All 60 feature scenarios passing (the acceptance contract), each demonstrably red-before-implementation.
- Unit-level implementation tests present per D5 (concurrency, round-robin, hash-skip, containment, retry).
- No MCP server failure path introduced: containment scenarios green.
- Docs drift fixed (tool inventory in README/CLAUDE.md if they enumerate tools).
- Owner-gate review completed per spec `gate.ownerGateReview` (formally deferred during elicitation — offered with the final diff).
