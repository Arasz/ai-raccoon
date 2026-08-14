# Lane report — architecture & layering

Campaign: project-scope review, base `1d1889d517baf840df0b839f547091bd7f46808b`.
Model: opus · persona: architect · read-only, own worktree.
Lane verified `git rev-parse HEAD` matched the stated base.

---

### F1 — Core launders a dependency on an Infrastructure exception type through string matching on the type's name [READ]
**Severity:** HIGH
**Evidence:**
- `src/AiRaccoon.Core/Resilience/ResiliencePipelineFactory.cs:62` — `.Handle<Exception>(ex => ex.GetType().Name == "EmptyDownloadException")`
- `src/AiRaccoon.Infrastructure/Assets/AssetDownloader.cs:9` — `public sealed class EmptyDownloadException(string message) : Exception(message);` — the type being matched, defined in Infrastructure.
- `src/AiRaccoon.Core/Resilience/ResiliencePipelineFactory.cs:6` — `namespace AiRaccoon.Resilience;` — the only namespace in Core that is not `AiRaccoon.Core.*`.
- Same file lines 1-2, 32-35, 44-63, 72-75 — `System.Net.Sockets`, `HttpRequestException`, `SocketException`, `HttpResponseMessage`, `HttpStatusCode`, 5xx/408/429 handling.
- Callers: `src/AiRaccoon.Infrastructure/Assets/AssetDownloader.cs:30` and `src/AiRaccoon/Hosting/Common/ServerProbe.cs:25,60`. **Zero Core callers.**

**Why it matters:** the clean-layering violation made literal. Core cannot reference
`EmptyDownloadException`, so the code reaches back up into Infrastructure by reflecting on a type
name — a dependency the compiler cannot see, no ArchUnit rule could catch, and that silently stops
working if anyone renames the class. The file is otherwise pure HTTP/socket transport policy with
no domain content and no domain consumer, and its own namespace declines to claim membership in
Core. `Polly.Core` is in `AiRaccoon.Core.csproj:13` solely for this file.

**Fix:** move `ResiliencePipelineFactory.cs` to `src/AiRaccoon.Infrastructure/Resilience/`, restore
`.Handle<EmptyDownloadException>()` as a real typed handler, drop `Polly.Core` from
`AiRaccoon.Core.csproj`.

---

### F2 — The DI helper registers every implementation under both its concrete type and its interface, dissolving the port boundary project-wide [MEASURED]
**Severity:** HIGH
**Evidence:**
- `src/AiRaccoon/Setup/AppRegistrations.cs:263-268` — `AddRequiredSingleton<TService,TImplementation>(factory)` body is `serviceCollection.AddSingleton<TImplementation>(); serviceCollection.AddSingleton<TService, TImplementation>(implementationFactory);`
- 43 call sites across `AppRegistrations.cs`, `Hosting/Proxy/ProxyRegistrations.cs`, `Hosting/Watchdog/WatchdogRegistrations.cs`, `Hosting/Node/NodeRegistration.cs`.
- Consequence, measured: **8 of 8** tool classes inject the concrete `ToolGate`, never `IToolGate` — `src/AiRaccoon/Tools/SweepTools.cs:17`, `SyncTools.cs:15`, `ShareTools.cs:15`, `QualityTools.cs:11`, `WatchTools.cs:11`, `WorkspaceTools.cs:15`, `PromotionTools.cs:15`, `MemoryTools.cs`.
- Further drift: `SweepTools.cs:15-16` injects concrete `SweepService` and `ForgettingPolicyService`; `SyncTools.cs:14` injects concrete `SyncCloudStoreFactory`; `ShareTools.cs:16` injects concrete `SharedExtractionRunner` — while `ISweepService`, `IForgettingPolicyService`, `ISyncCloudStoreFactory`, `ISharedExtractionRunner` all exist and are registered.

**Why it matters:** because both registrations resolve, injecting the concrete Infrastructure class
is exactly as easy as injecting the port, and nothing — not the compiler, not DI validation, not a
test — reports the difference. This is the mechanism behind most other boundary findings here: the
ports are declared but the container makes them optional, so consumers drift to concrete types one
at a time and the drift is invisible.

**Fix:** make `AddRequiredSingleton` register only via the interface, then fix the resulting
compile errors — each one is a consumer that was bypassing a port. Where a concrete type genuinely
must be resolvable (e.g. `SqliteConnectionFactory` at `AppRegistrations.cs:162-165`), register it
explicitly and deliberately.

---

### F3 — `IToolGate` and `ISyncCloudStoreFactory` are declared, implemented and registered, but injected nowhere [READ]
**Severity:** MEDIUM
**Evidence:**
- `src/AiRaccoon/Tools/IToolGate.cs:5`, `src/AiRaccoon/Tools/ToolGate.cs:13`, `src/AiRaccoon/Setup/AppRegistrations.cs:121`. A repo-wide search for `IToolGate` returns exactly these three hits — no injection site, no test.
- `src/AiRaccoon.Infrastructure/Sync/ISyncCloudStoreFactory.cs:5`, `SyncCloudStoreFactory.cs:12`, `AppRegistrations.cs:72`. The only other reference is `AppRegistrations.cs:74`, which resolves the **concrete** type.

**Why it matters:** extension points with one implementation, no second one planned, and no
consumer. They look maintained because they are registered and compile. `IToolGate` is the telling
case: written to be the seam every tool depends on, and every tool ignores it.

**Fix:** delete both, or (preferred for `IToolGate`, a genuine testing seam) change all 8 tool
constructors to take it — which F2's fix forces anyway.

---

### F4 — `WorkspaceService` and its port are pure domain logic living in Infrastructure, forcing a type alias to escape their own namespace [READ]
**Severity:** HIGH
**Evidence:**
- `src/AiRaccoon.Infrastructure/Workspace/WorkspaceService.cs:11` — `public sealed class WorkspaceService(IMemoryStore store, IWorkspaceStore workspaceStore, TimeProvider timeProvider) : IWorkspaceService`. All three dependencies are Core ports or BCL. The file's only usings (`:1-2`) are `AiRaccoon.Core.Isolation` and `AiRaccoon.Core.Memory` — no SQL, no logging, no framework.
- `src/AiRaccoon.Infrastructure/Workspace/IWorkspaceService.cs:3` — the port is also in Infrastructure.
- `src/AiRaccoon.Infrastructure/Workspace/WorkspaceService.cs:3` — `using WorkspaceRecord = AiRaccoon.Core.Isolation.Workspace;` — an alias needed only because the Infrastructure **namespace** collides with the Core **type** it exists to serve.

**Why it matters:** a domain service and its port sitting in the adapter layer with nothing
adapter-shaped about them — not even the `ILogger` that explains other Infrastructure residents.
Consumers in the host must reference `AiRaccoon.Infrastructure` to talk about workspaces at all.
The alias at line 3 is the codebase telling you the folder is in the wrong place.

**Fix:** move both files into `src/AiRaccoon.Core/Isolation/`. The alias disappears with them.

---

### F5 — The search-ranking domain lives in the folder named after the database driver, and is `internal` there [MEASURED]
**Severity:** MEDIUM
**Evidence:** six types in `src/AiRaccoon.Infrastructure/Sqlite/` with **zero** persistence
dependencies — their only usings are `AiRaccoon.Core.Memory` or BCL text/crypto:
`ReciprocalRankFusion.cs:9` (`internal static`, 57 lines, RRF fusion, FR-NM-4);
`SourceAffinityRanker.cs:9` (`internal static`, 128 lines, ADR-0005 adjacent-chunk boost);
`SearchResultMerger.cs` (35); `SnippetFallback.cs` (75); `EntryBucket.cs` (52);
`ModalityCandidates.cs` (56). 403 lines total. By contrast only 13 of the 37 files in `Sqlite/`
reference `Microsoft.Data.Sqlite`, `Dapper` or `IDbConnection`.

**Why it matters:** hybrid retrieval ranking is the product's core value — an ADR was written about
it — and it is filed under the storage engine's brand name, the textbook technical bucket the
Screaming Architecture invariant forbids. `internal` compounds it: Core cannot reuse these, and the
ranking policy cannot be exercised or swapped independently of SQLite. `Core/Embedding/` meanwhile
holds one file.

**Fix:** move the six types to `src/AiRaccoon.Core/Retrieval/` and make them `public`. They compile
there unchanged.

---

### F6 — `IMemoryStore` is a 26-method god port that mixes persistence, file ingestion, embedding orchestration and settings [MEASURED]
**Severity:** HIGH
**Evidence:** `src/AiRaccoon.Core/Memory/IMemoryStore.cs`, 117 lines, 26 members. Its own doc at
line 3 calls it "thin and SQL-shaped", but it declares filesystem ingestion (`IngestFileAsync :53`,
`IngestDirectoryAsync :57`), embedding orchestration (`ConfigureEmbeddingAsync :65`,
`EmbedPendingAsync :69`), settings key-value (`GetSettingAsync :88`, `SetSettingAsync :91`,
`GetSettingsByPrefixAsync :105`, `DeleteSettingAsync :109`), and promotion/extraction
(`ShareAsync :37`, `ExtractCandidatesAsync :40`, `GetSharedIndexAsync :44`). Exactly one production
implementation: `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs:29` (1291 lines), plus
`tests/AiRaccoon.Tests/TestHelpers/FakeMemoryStore.cs:13`.

**Why it matters:** the codebase's central hub, so its shape sets everyone's coupling. Anything
needing one setting takes a dependency on file ingestion and embedding too. `IFileIngestor` already
exists at `src/AiRaccoon.Infrastructure/Ingestion/IFileIngestor.cs`, so ingestion is modelled twice.

**Fix:** complete the segregation already started — remove the four settings members (F8) and the
two ingestion members in favour of `IFileIngestor`.

---

### F7 — Three port members carry default implementations whose fallback behaviour is wrong [READ]
**Severity:** MEDIUM
**Evidence:**
- `src/AiRaccoon.Core/Memory/IMemoryStore.cs:17-18` — `GetAsync(...) => Task.FromResult<MemoryEntry?>(null);` with the comment "Defaults to 'not found' so an implementation predating this read path needs no change."
- `src/AiRaccoon.Core/Memory/IMemoryStore.cs:28-30` — `DeleteInScopeAsync(...) => DeleteAsync(projectId, hash, cancellationToken);` — the default **widens a scoped delete into an unscoped one**, the exact cross-scope reach the member exists to prevent.
- `src/AiRaccoon.Infrastructure/Sqlite/IPromotionQueueStore.cs:64-66` — `ClaimAsync(...)` defaults to `DiscardAsync(...)`, i.e. **deletes the row** instead of claiming it.
- Only one production implementor exists for each (F6).

**Why it matters:** the stated reason — sparing implementations that predate the member — protects a
single test fake. What it buys instead is three silent wrong answers: a `memory_get` that reports
"not found", a scoped sweep that deletes a sibling scope's row, and a promotion claim that destroys
the candidate. A compile error is the correct way to tell an implementor it is missing a member.

**Fix:** make all three abstract and implement them explicitly. The compiler will list the work.

---

### F8 — The `ISettingsStore` extraction was never finished: the old members remain, and a second instance is hand-constructed beside the registered one [READ]
**Severity:** MEDIUM
**Evidence:**
- `src/AiRaccoon.Core/Memory/ISettingsStore.cs:4-5` — doc states it was "Split out of `IMemoryStore`, which still exposes the same four members and delegates here (WP8…)".
- `src/AiRaccoon.Core/Memory/IMemoryStore.cs:88,91,105,109` — the four members are indeed still there.
- `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs:33` — `private readonly ISettingsStore _settings = new SqliteSettingsStore(factory);` — direct construction inside a DI-registered singleton.
- `src/AiRaccoon/Setup/AppRegistrations.cs:184` — `AddRequiredSingleton<ISettingsStore, SqliteSettingsStore>()` registers a *separate* instance.
- `SqliteMemoryStore.cs:720-724` — the delegating forwarders.

**Why it matters:** the refactor added a port without removing what it replaced, so callers have two
equally valid ways to read a setting and no signal about which is intended — and everything still
uses the `IMemoryStore` path. Two `SqliteSettingsStore` instances exist at runtime for no reason.

**Fix:** delete the four members and their forwarders, inject `ISettingsStore` where settings are
read, remove the hand-rolled construction at line 33.

---

### F9 — The MCP layer holds real business logic: a consent gate, a mode decision, two pipelines, and a whole query-guard policy engine [MEASURED]
**Severity:** HIGH
**Evidence:** `src/AiRaccoon/Tools/ShareTools.cs:43-118` is the fattest of the 26 tool methods at
**62 body lines** (next: `MemoryTools.Search` 43; median 9).
- `ShareTools.cs:75-79` — `if (autoPromote && !confirm) throw new McpException("confirm-required: …")` — a consent policy for cross-project data sharing decided in the adapter.
- `ShareTools.cs:84` — `var promotes = extractMode == ExtractMode.Promote || autoPromote;` — collapses two inputs into the domain decision that drives the access level (`:86-88`).
- `ShareTools.cs:93-104` — the entire promote pipeline, inline, with its own result shape and early return.
- `ShareTools.cs:106-113` — the propose pipeline: fetch the shared dedup index once, then loop `extraction.ProposeAsync` per project.
- `src/AiRaccoon/Tools/MemoryTools.cs:170-222` — `EvaluateQueryGuardAsync`/`EvaluateStructuralQueryGuardAsync`: private methods on the tool class that read raw flags via `store.GetSettingAsync`, run `QueryGuardPolicy.Evaluate` and `StructuralQueryGuardPolicy.Evaluate`, and implement shadow-vs-live branching.
- `src/AiRaccoon/Tools/MemoryTools.cs:141-153` — a derived projection plus a hand-rolled swallow-and-log fallback around quality recording.

**Why it matters:** the project invariant is that MCP tools map 1:1 onto services and hold no logic.
`memory_share_extract` is effectively a service implemented in the adapter — the promote/propose
decision, the safety gate and both orchestrations exist nowhere else, so they cannot be
unit-tested, reused by the CLI, or reached by the background extraction loop.

**Fix:** extract a `ShareExtractService` in Core owning the mode/consent decision and both
pipelines, and a `QueryGuardService` owning the settings reads and shadow-mode branching.

---

### F10 — Use-case ports sit in Infrastructure, so several "port" interfaces guard an Infrastructure→Infrastructure boundary [READ]
**Severity:** MEDIUM
**Evidence:** ports declared in Infrastructure rather than Core —
`Sync/ISyncService.cs`, `Sync/ICloudStore.cs`, `Sync/ISyncCloudStoreFactory.cs`,
`Workspace/IWorkspaceService.cs`, `Degradation/ISweepService.cs`,
`Embedding/IEmbeddingService.cs`, `Embedding/IEntryEmbedder.cs`, `Embedding/IBundledModel.cs`,
`Ingestion/IFileIngestor.cs`, `Watch/IWatchScheduler.cs`, `Watch/IWatchDigestExecutor.cs`,
`Watch/IWatchRetryPolicy.cs`, `Watch/IWatchScanGuard.cs`, and — persistence ports under the
driver's folder name — `Sqlite/IMemorySourceStore.cs`, `Sqlite/IPromotionQueueStore.cs`.
Against the opposite convention, correctly applied, in Core: `IMemoryStore`, `IWorkspaceStore`,
`IPromotionQueue`, `ISettingsStore`, `IChunker`, `IWatchService`, `ISearchQualityService`,
`INoiseDetector`.

**Why it matters:** two port conventions coexist with no rule distinguishing them, so a reader
cannot tell from a file's location whether an interface is a domain contract or an internal seam.
Where the implementation is also in Infrastructure, the interface inverts nothing.

**Fix:** pick one rule and state it in an ADR — suggested: any interface a Core or host type
depends on moves to Core; anything else loses the interface (F3).

---

### F11 — The same concepts are cut in two with mismatched folder names on either side [READ]
**Severity:** MEDIUM
**Evidence:**
- **Workspaces:** Core calls it `Isolation/`, Infrastructure calls it `Workspace/`, persistence is `Sqlite/SqliteWorkspaceStore.cs`. Every file in `Core/Isolation/` is named after `Workspace`; the folder is not.
- **Sync:** `src/AiRaccoon.Core/Sync/` contains **only exceptions** — `SyncExceptions.cs` (20 lines, 5 types) and `SyncNotConfiguredException.cs` (18 lines). All sync behaviour, the port and `SyncSettingsKeys` live in `Infrastructure/Sync/` (11 files).
- **Promotion:** split four ways — domain records and `IPromotionQueue` in `Core/Memory/`, the orchestrator in `Infrastructure/Promotion/`, the store port and SQL in `Infrastructure/Sqlite/`.

**Why it matters:** a reader looking for "workspaces" must know to try `Isolation`, and a reader
opening `Core/Sync` finds a namespace that models no sync at all — just the failure modes of code
that lives elsewhere.

**Fix:** rename `Core/Isolation/` → `Core/Workspaces/` (absorbing F4's move); create
`Core/Promotion/`; move sync's port and domain types into `Core/Sync/`.

---

### F12 — The architecture-enforcement library is pinned but never referenced, and no architecture test exists [READ]
**Severity:** HIGH
**Evidence:**
- `tests/Directory.Packages.props:18` — `<PackageVersion Include="TngTech.ArchUnitNET.xUnitV3" Version="0.13.3"/>`
- `tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj` — **no** `PackageReference` to it.
- No ArchUnitNET code anywhere in `tests/`.
- The only mechanical layering guard in the repo is the absence of a `ProjectReference` in `src/AiRaccoon.Core/AiRaccoon.Core.csproj:18-20`.

**Why it matters:** the clean-layering invariant is enforced by exactly one thing — a missing
project reference — and that catches only assembly-level leaks. It cannot see F1's string-matched
dependency, F4's domain service in Infrastructure, F5's ranking domain under `Sqlite/`, F10's
misplaced ports, or F2's concrete-type injections. Every finding in this report is invisible to CI,
which is why they accumulated while the build stayed at 0 warnings.

**Fix:** add the `PackageReference` and one `ArchitectureTests.cs` asserting three rules: Core
depends on no other project assembly; no type in `AiRaccoon.Core.*` references `System.Net.*`; every
`[McpServerTool]` class's constructor parameters are interfaces. Watch each fail first.

---

### F13 — A DI-registered runtime service writes downloaded assets into the repository source tree and hardcodes the repo layout [READ]
**Severity:** MEDIUM
**Evidence:** `src/AiRaccoon.Infrastructure/Embedding/BundledModel.cs:146-158` —
`RepoModelsDirectory()` walks up from `AppContext.BaseDirectory` probing for
`Path.Combine(dir.FullName, "src", "AiRaccoon", "Models")`; `:47` downloads into it; `:65-70`
`ResolveModelPath` doc says "…else the repo source copy during tests"; `:165` —
`new AssetDownloader(http)` constructed inside a DI singleton. Registered at
`src/AiRaccoon/Setup/AppRegistrations.cs:191`. `git ls-files` confirms
`src/AiRaccoon/Models/model_qint8_arm64.onnx` (23 MB) and `vocab.txt` (231 KB) are **tracked**, and
`src/AiRaccoon/AiRaccoon.csproj:34-35` packs them.

**Why it matters:** a build concern implemented inside a shipped runtime service that mutates the
developer's checkout, with the repository's directory layout compiled into a published dotnet tool
where that branch can never fire. The model has three sources of truth, and production code behaves
differently depending on whether a repo happens to be above it on disk.

**Fix:** split provisioning from resolution — keep `ResolveModelPath`, move `EnsureAsync` /
`EnsureDownloadsAsync` / `RepoModelsDirectory` into a build script or CLI command. Inject
`AssetDownloader` rather than constructing it.

---

### F14 — Only one of the five stateful domain objects routes transitions through a declared set; the promotion queue mutates state from eight independent SQL sites [MEASURED]
**Severity:** MEDIUM
**Evidence:**
- **Compliant:** `src/AiRaccoon.Core/Isolation/Workspace.cs:40-55` — `Consolidate()`/`Discard()` funnel through private `TransitionTo`, which throws unless `Status == Active`; enforced again by the conditional CAS in `src/AiRaccoon.Infrastructure/Sqlite/SqliteWorkspaceStore.cs:52-66`.
- **Promotion queue:** no status type at all — state is inferred from row existence plus a nullable `claimed_at`. Eight independent mutating statements in `src/AiRaccoon.Infrastructure/Sqlite/PromotionQueueSql.cs`: `Upsert` (:6-21), `Claim` (:122-129), `ReclaimStaleClaims` (:134-138), `Discard` (:58-64), `EvictVictim` (:100-111), `PruneRejected` (:33-41), `ClearStale` (:114-117), `DeleteOrphans` (:92-98).
- **Watch:** `src/AiRaccoon.Core/Watch/WatchState.cs:7-13` declares `Scanning/Healthy/Retrying/Stopped`, mutated from 4 sites in `src/AiRaccoon.Infrastructure/Watch/WatchPipeline.cs:87,136,239-240,256` with no guard reading the prior state — `MarkScanning` (:136) overwrites unconditionally, so a `Stopped` watch can be silently revived.
- **Not applicable:** sync and degradation have no persisted status.
- **`RestartTransition.cs` is not the model:** it is a `static` class of three pure functions over transient values, with no mutated field and no persisted state — a good exhaustive decision table, but not a state machine.

**Why it matters:** the invariant's value is that one place answers "can it go from here to there".
For the promotion queue that place does not exist. Today each site is individually race-safe; the
risk is the ninth statement, which has nothing to be consistent with.

**Fix:** give the promotion queue an explicit `PromotionStatus` column and a single guarded
transition method, following `Workspace.TransitionTo`; add a prior-state check to
`WatchPipeline.MarkScanning`.

---

### F15 — Dead and duplicated DI registrations, including one silently shadowed by a later conflicting registration [READ]
**Severity:** LOW
**Evidence:**
- `src/AiRaccoon/Hosting/Watchdog/WatchdogRegistrations.cs:17` — `AddSingleton<IIdleTimeoutProvider>(serverConfig)`. Repo-wide, `IIdleTimeoutProvider` has exactly three hits: declaration, implements-clause, this registration. **No consumer.** `IdleWatchdog` takes a bare `TimeSpan` instead (`src/AiRaccoon/Hosting/Watchdog/IdleWatchdog.cs:22`).
- Same file `:18` — `AddSingleton(typeof(TimeSpan), serverConfig.IdleTimeout)` registers a BCL value type as a service; any future singleton with a `TimeSpan` parameter silently receives the idle timeout.
- `src/AiRaccoon/Setup/Cli/Commands/CommandsRegistration.cs:15` and `:20` — `AddSingleton<ExtractCommands>()` twice, identically.
- Same file `:19` versus `:24-29` — `AddSingleton<EncryptionCommands>()` shadowed by an explicit factory for the same type; last-wins makes line 19 dead configuration.

**Fix:** delete the `IIdleTimeoutProvider` registration (or inject it into `IdleWatchdog` in place of
the bare `TimeSpan`, removing line 18); delete the duplicate and the shadowed lines.

---

### F16 — `IWatchPipeline.MarkScanning` is declared on the port, implemented, and called from nowhere [READ]
**Severity:** LOW
**Evidence:** `src/AiRaccoon.Infrastructure/Watch/WatchPipeline.cs:34` (interface member, doc citing
`docs/plans/file-watcher-implementation.md` S5) and `:129` (implementation). A repo-wide search
across `src/` and `tests/` returns only these two lines.

**Why it matters:** a port member with no caller is surface every implementor must satisfy for
nothing, and it is the member most likely to break the watch state model if wired up later (F14: it
overwrites state with no prior-state guard).

**Fix:** delete the member and its implementation; if the initial-scan marking it was written for is
genuinely missing, file that separately with a test.

---

### F17 — The access-control concept is split between Core and the executable, skipping Infrastructure entirely [READ]
**Severity:** LOW
**Evidence:** `src/AiRaccoon.Core/Access/` holds `AccessMode.cs`, `AccessRequirement.cs`,
`AccessModePolicy.cs` (50 lines of pure policy), `AccessDeniedException.cs`; `src/AiRaccoon/Access/`
holds `IMemoryAccessGuard.cs`, `MemoryAccessGuard.cs` (45 lines), `IForgettingPolicyService.cs`,
`ForgettingPolicyService.cs` (66 lines). `src/AiRaccoon/Access/MemoryAccessGuard.cs:7` —
`MemoryAccessGuard(IMemoryStore store)`; its only usings are `AiRaccoon.Core.Access` and
`AiRaccoon.Core.Memory`.

**Why it matters:** the enforcement half of access control cannot be reached by anything but the MCP
host — not the CLI commands, not background services.

**Fix:** move both services and their ports into `src/AiRaccoon.Core/Access/`.

---

### F18 — Core performs real filesystem I/O in path containment checking [READ]
**Severity:** LOW
**Evidence:** `src/AiRaccoon.Core/Ingestion/IngestPath.cs:66` — `var target =
File.ResolveLinkTarget(path, true);` inside `ResolveSegment`, wrapped in `catch (IOException)` /
`catch (UnauthorizedAccessException)` (`:69-76`). This and F1 are the **only** two I/O, process,
HTTP or environment concerns anywhere in Core.

**Why it matters:** symlink resolution is genuinely security-relevant — the function prevents an
ingest path escaping its declared scope — so the call does real work. But it makes the containment
rule untestable without a real filesystem and symlink privileges, precisely the case that most needs
adversarial tests.

**Fix:** accept it and record an ADR (as ADR-0017 did for `TensorPrimitives`), or take a narrow
`Func<string, string?>` link resolver defaulting to `File.ResolveLinkTarget`.

---

## Still open

- **Is `src/AiRaccoon/Models/` (23 MB ONNX, git-tracked) intended to stay in git?** F13 establishes three provisioning paths but not which is canonical. Settled by: whether `dotnet pack` must work offline from a clean clone.
- **Do `MayBind`/`AfterBindRefused` (`RestartTransition.cs`) have callers?** Exported but not invoked within `ServerRestart.cs`. Settled by: a call-site search across `Setup/Cli/Commands/ServeCommands.cs` and `Hosting/Node/NodeRunner.cs`. If absent, this is a second F16.
- **Is the full inventory of single-implementation/single-injection interfaces larger than F3's two?** Four more verified by reading constructors (`IBundledModel`, `IEmbeddingService`, `WatchEventSource`, `WatchCatchUp`); ~55 not read. Settled by: applying F2's fix, which turns every unused port into a compile error.
- **Are the `*ConfigKeys` classes (8 in Core, 2 in Infrastructure) a deliberate configuration strategy or drift?** Settled by: whether settings must be changeable without a restart.
- **Does `SyncService`'s deferred `sp.GetRequiredService` closure (`AppRegistrations.cs:73-79`) resolve inside a request or at construction?** Low stakes given zero scoped registrations exist.

## Grade mix

**MEASURED** 5 (F2, F5, F6, F9, F14) · **READ** 13 · **INFERRED** 0 · **UNVERIFIED** 0.

Every finding carries at least one `path:line` the lane opened. The knowledge graph (built at
`74bb2e1c`, 6 days behind HEAD) was used for orientation only; all structure was verified against
the filesystem.

## Owner questions

1. Should `AddRequiredSingleton` stop registering the concrete type, accepting the compile errors that exposes (F2) — yes or no?
2. Should the ranking algorithms (RRF, source affinity) move from `Infrastructure/Sqlite/` into Core and become public (F5) — yes or no?
3. Pick the port convention (F10): (a) every port a Core or host type consumes lives in Core, (b) ports live beside their implementation, or (c) status quo, documented in an ADR?
4. Should ArchUnitNET be wired up now with the three starter rules in F12, or deferred — now or defer?
5. Should the three semantically-wrong default interface methods become abstract (F7) — yes or no?
6. Is the 23 MB ONNX model staying in git (F13) — keep tracked, or move to release-asset download?
7. Should `memory_share_extract`'s consent gate and dual pipeline move into a Core service (F9) — yes or no?
8. Rename `Core/Isolation/` → `Core/Workspaces/` and create `Core/Promotion/` (F11) — yes or no?
9. Does the promotion queue get an explicit status column and single transition method (F14), or is per-statement CAS accepted — machine or accept?
10. `IngestPath`'s filesystem call (F18): accept with an ADR, or make the link resolver injectable?

## Healthy

- **Core's package and namespace purity exceeds the brief's claim.** No `ProjectReference`, and beyond `Microsoft.Extensions`, Core contains **zero** `using Microsoft.*` of any kind. Only two of 106 files hold any I/O, HTTP, process or environment concern (F1, F18).
- **`Workspace` is a textbook state machine and should be the template for F14's fix.** `Core/Isolation/Workspace.cs:40-55` declares both transitions, funnels them through one guard, refuses non-`Active` sources, returns a new immutable record; `SqliteWorkspaceStore.cs:52-66` enforces the same rule again as a conditional `UPDATE` so a lost race throws rather than double-consuming an outbox.
- **FluentValidation in Core is idiom, not leakage.** Declarative domain validation on domain records — `Core/Memory/SearchQuery.cs:1`, `Core/Memory/MemoryWriteRequest.cs:1`, `Core/Degradation/EntryTtl.cs:1` — with one 16-line process-wide config shim. The established ADR-0001 pattern; it should stay. Polly.Core, by contrast, is leakage (F1).
- **21 of 26 MCP tool methods are genuinely thin** — gate check, guard clauses, one service call, map, wrap — median body 9 lines. F9 is a concentrated failure in two files, not a diffuse one.
- **`AddRequiredSingleton` forwards rather than double-instantiating.** Its second registration is `AddSingleton<TService, TImplementation>(sp => sp.GetRequiredService<TImplementation>())`, so exactly one instance exists. F2 is about the boundary it dissolves, not a lifetime bug.
- **No captive-dependency class of bug is possible.** Not a single `AddScoped` or `AddTransient` anywhere in `src/`; every registration is Singleton or `AddHostedService`.
- **`RestartTransition.cs` is good code** — pure functions, exhaustive switch expressions, no hidden state.
- **`IPromotionQueue` is a well-designed port** — intention-revealing members, 1-3 line contract docs citing ADRs. The problems around it are placement (F10) and the default methods (F7).

## Disconfirmed

- **"Core is framework-free by package reference but leaks framework concerns throughout its shape."** False. A sweep for `System.IO`/`File.`/`Directory.`/`Process.`/`Environment.`/`HttpClient`/`Socket` across all 106 Core files returns hits in exactly **two** files. The leak is sharp and localised, not systemic — which makes F1 and F18 cheap to fix rather than a rewrite.
- **"Domain services are exiled to Infrastructure because clean layering forbids `Microsoft.Extensions.Logging` in Core, putting two invariants in tension."** False, and checked specifically. `WorkspaceService` (F4) has no logger at all, and two genuine domain services — `Core/Memory/Filtering/NoiseFilteringService.cs` and `Core/Memory/SharedExtractionService.cs` — stayed in Core. The project also built `IOperationTelemetry` (`Core/Observability/IOperationTelemetry.cs:8`) as a Core-side port precisely to avoid this tension. Placement is driven by where the first caller happened to live.
- **"`AddRequiredSingleton` creates two instances of each service."** False — it forwards via `GetRequiredService<TImplementation>()`.
- **"`RestartTransition.cs` is a new state-machine model the codebase should follow."** False. It is a stateless decision table over transient enums. The model to follow already exists and is older: `Workspace.TransitionTo`.
- **"The prior review's two blockers are still open."** Confirmed fixed at this base and not re-filed. What the lane filed instead is the incomplete follow-through beside them: `IMemoryStore.GetAsync` ships a default implementation that returns "not found" (F7), so the port `memory_get` sits on can silently answer null.
