# WP6 — ADR-0089 parts 1–3 implementation plan (6a / 6b / 6c)

Date: 2026-08-23. Lane: architect / Opus. Satisfies the WP6 precondition in §WP6 of
`docs/work/2026-08-23-post-delta-4-plan.md` — *"the architect's implementation plan defines the RED
test, the acceptance criteria and the exact gate command for each sub-PR, and no `6x` PR opens until
it does."* Parts 4–5 carry in `docs/work/2026-08-23-post-delta-5-plan.md` §WP1/§WP2. Source of truth:
`docs/adr/0089-the-project-id-is-a-guidv7-and-that-is-not-access-control.md` (Accepted, decisions
1–8); everything below cites it or the tree at `task/post-delta-4-plan-and-gate` + `origin/main`.

## Session todo list

1. Read §"Three deviations from the WP6 brief" — two of them change what 6a builds.
2. `6a` — `projects` table in the unconditional `Ddl` block, `ProjectId` canonicaliser, `IProjectRegistry`. Merge before 6b opens.
3. `6b` — `project_id_token_get` on a new `ProjectTools` class; six inventory/contract gates move with it. Merge before 6c opens.
4. `6c` — three commits: `ToolGate` required deps, canonical id to storage, then the refusal.
5. Owner: §"One amendment the owner may decline" needs a yes/no, but blocks nothing.

## Two things the owner ruled — do not re-ask

- **`ai-raccoon.ignore`, not `.gitignore`.** Verbatim on #448: *"Not gitignored - ai-raccoon.ignore -
  we dont want this file in memory"* (decision 8). Nothing in 6a/6b/6c writes either file — that is
  6e/pd5-WP2 — but no lane may propose a `.gitignore` line.
- **A non-guid unregistered id is refused too.** Verbatim: *"we also want to eliminate the case: any
  valid guid creates project"* → decision 3, *"A write to an id with no registry row is refused —
  guid or not"*. The refusal is about **registration**, never the string's shape.

## Three deviations from the WP6 brief — read before starting 6a

The brief says *"6a (schema v11 …) … migration that back-fills existing project ids from
`SelectProjectIds` as legacy-grandfathered rows"*. Both halves contradict the ratified ADR, and this
plan follows the ADR. A third deviation is from §Review (e), not the brief, and is flagged here so
it is not discovered under 6b.

1. **No `CurrentVersion` bump. It stays 10.** Decision 5: *"**No `CurrentVersion` bump.** The table
   belongs in the unconditional `Ddl` block"*; `MemorySchema.cs:54` is `CurrentVersion = 10`, and
   §Review (e) repeats it. ADR-0086's reason: `user_version` survives `VACUUM INTO` and the sync gate
   refuses a pull from a newer one, so a bump hard-fails every concurrent session and peer. The
   mechanism already exists — `SchemaDigest` (`:503-510`) is SHA-256 of the `Ddl` string in `PRAGMA
   application_id`, so adding the table changes the digest and `EnsureAsync` (`:578-582`) re-runs
   `Ddl` on the next open of every bank. **No ladder step, no v11.**
2. **No back-fill of legacy ids into `projects`.** `SelectProjectIds` (`MemorySql.cs:58-64`) is a
   **live oracle**, not a migration source — decision 3: *"the refusal test is 'no registry row
   **and** no existing rows'"*, repeated verbatim in the owner's #448 fold, and §Consequences names
   *"a legacy id with rows and no registration"* an **intended** state. A back-fill erases it, puts
   raw text in a column decision 5 calls *"canonical lowercase D-form guidv7"*, and — operationally —
   is a **one-time** act on a bank that keeps gaining ids from `memory_sync` pulls and `VACUUM INTO`
   copies, so a later restore would see its own legacy projects refused. Cost: a registry-only listing
   misses legacy projects, so it unions the two as §Membership prescribes. Wanting it anyway is an ADR
   amendment plus a `legacy` column — raise it before 6a opens.
3. **6b creates `ProjectTools.cs`; §Review (e) sizes it into `MemoryTools.cs`.** New `project_*`
   family (decision 4), `MemoryTools.cs` is 502 lines under `ToolMethodSizeTests`, and `mcp-thin`
   wants the mint tool holding nothing else. Described in full under 6b.

## Conventions every sub-PR inherits

- TDD: the RED test lands first and is **seen failing**. One that cannot fail behaviourally because
  its type does not exist yet is marked *compile-level RED* and paired with a behavioural RED.
- New test classes carry **class-level** `[Trait(TestCategories.Category, …)]` +
  `[Trait(TestCategories.Speed, …)]` or they fall outside every filter (`TestCategories.cs:13-33`).
- Guards via `ArgumentException.ThrowIfNullOrWhiteSpace`; nested static partial `Log` with
  `[LoggerMessage]` + explicit `EventId`; no optional Null-object ctor params; one PR per sub-part.

## 6a — the `projects` table, the canonicaliser, and the registration write path

**Scope.** The substrate. No tool surface, no refusal, no behaviour change to any existing call.

**Files.**

| File | Change |
|---|---|
| `src/AiRaccoon.Core/Projects/ProjectId.cs` | **new.** `Canonicalize(string)` / `TryCanonicalize(string, out string)` — `Guid.TryParse` in, `ToString("D")` lowercase out; a non-guid returns false and is passed through untouched. A small pure helper, not a component. |
| `src/AiRaccoon.Core/Projects/IProjectRegistry.cs` | **new.** `Task RegisterAsync(string projectId, string? name, CancellationToken)`, `Task<bool> IsRegisteredAsync(string projectId, CancellationToken)`, `Task<bool> HasRowsAsync(string projectId, CancellationToken)`. Split out of `IMemoryStore` exactly as `IModelMigrationStore` was (`src/AiRaccoon.Core/Memory/IModelMigrationStore.cs:1-25`). |
| `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs` | `projects` table added to the `Ddl` raw string (`:75`), beside the `metrics` precedent at `:344-346`. **`CurrentVersion` untouched.** |
| `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs` | `InsertProject` (`INSERT … ON CONFLICT(id) DO NOTHING`), `ProjectIsRegistered`, and `ProjectHasRows` — the last composing **`ProjectRows.Of()`** (`ProjectRows.cs:21` = `Scope() AND project_id = @projectId`), so ADR-0046's single definition is reused, never re-typed. |
| `src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.Projects.cs` | **new partial**, mirroring `SqliteMemoryStore.ModelMigration.cs` (open via `factory`, `MemorySchema.EnsureCheapAsync`, Dapper). |
| `src/AiRaccoon/Setup/AppRegistrations.cs` (`:304` block) | `services.AddSingleton<IProjectRegistry>(sp => sp.GetRequiredService<SqliteMemoryStore>())`, beside `IModelMigrationStore`. **Server graph only.** |

Table shape exactly as decision 5 (`id TEXT PRIMARY KEY`, `name TEXT`, `created_at INTEGER NOT NULL`);
`created_at` from the injected `TimeProvider`, never `DateTimeOffset.UtcNow`.

**Not in 6a: the CLI-side registration.** `AppRunner.cs:227`'s `AddSingleton<T>(lazyServerStore)`
lines are backed by `LazyServerSettingsStore`, which implements its eight interfaces by forwarding
through `InnerAsync` (probe-and-start the server, ADR-0075 §5.1) — adding `IProjectRegistry` is three
forwarding members plus an `AsProjectRegistry` cast helper, **not one line**. 6a ships no CLI verb
(decision 6 is 6d) and 6b resolves from the server graph, so the CLI graph does not need it yet.
**Carried to `docs/work/2026-08-23-post-delta-5-plan.md` §WP1**, to budget rather than discover.

**Use `Of()`, never `Scope()`.** `ProjectRows.Scope()` (`:18`) is `scope IN ('project','custom')` with
**no `project_id` in it**; `SelectProjectIds` may use it only because it is a `SELECT DISTINCT` across
all projects. A per-project `ProjectHasRows` on `Scope()` is true whenever the bank holds *any*
project- or custom-scoped row, so every unregistered id reads as "legacy with rows" and 6c's refusal
never fires. Hand-writing `AND project_id = @projectId` beside it is worse — it re-types the predicate
ADR-0046 keeps in one place. `ProjectHasRows` also runs on every gated write once 6c lands: run
`EXPLAIN QUERY PLAN` on it against a real bank and report a SCAN of `entries` in the PR.

**RED tests.**

- `tests/AiRaccoon.Tests/Integration/Storage/ProjectsTableDdlTests.cs` — class `ProjectsTableDdlTests`,
  `[Trait(Category, Integration)] [Trait(Speed, Fast)]`.
  - `OpeningALegacyBank_CreatesTheProjectsTable` — **behavioural RED, and the one that matters**:
    open a bank at the pre-change schema, re-open through `MemorySchema.EnsureAsync`, assert
    `sqlite_master` holds `projects`. Fails today for the right reason, exercises the digest-rerun
    path ADR-0086 requires, and needs no new member.
  - `CreatingTheProjectsTable_DoesNotChangeUserVersion` — reads `PRAGMA user_version` before and
    after and asserts the two equal **each other**. No hardcoded `10`: a literal beside
    `MemorySchema.CurrentVersion` is a duplicated list a later cleanup deletes, and
    `ShouldBe(CurrentVersion)` alone goes green on a bump that also stamps 11. **Seen failing**: add
    a v11 ladder step locally, watch it go red, revert.
- `tests/AiRaccoon.Tests/Unit/Projects/ProjectIdCanonicalTests.cs` — `[Trait(Category, Unit)]
  [Trait(Speed, Fast)]`: `TryCanonicalize_LowercasesAndStripsBraces`,
  `TryCanonicalize_LeavesARawTextIdUntouchedAndReturnsFalse`. Compile-level RED (new type).
- `tests/AiRaccoon.Tests/Integration/Storage/SqliteProjectRegistryTests.cs` —
  `[Trait(Category, Integration)] [Trait(Speed, Fast)]`.
  `RegisterAsync_MakesIsRegisteredTrue`, `RegisterAsync_IsIdempotentForTheSameId`,
  `RegisterAsync_StoresTheCanonicalLowercaseDForm`, `HasRowsAsync_IsFalseForARegisteredProjectWithNoEntries`,
  `HasRowsAsync_IsTrueForALegacyRawTextIdThatHasRows` — the last two pin the ADR's two intended
  disagreements. Plus `HasRowsAsync_IsFalseForOneProjectWhileTrueForAnother`, **the
  `Of()`-vs-`Scope()` gate**: seed two projects, `A` with project-scoped rows and `B` registered with
  none, and assert false for `B` while true for `A`. A `Scope()` implementation returns true for both
  and this is the only test that says so — write it against a `Scope()` draft first and watch it fail.

**Acceptance.** `projects` exists on a fresh bank **and** on a legacy bank re-opened once;
`PRAGMA user_version` is unchanged across the upgrade; `IProjectRegistry` registers idempotently and
stores the canonical form; `HasRowsAsync` answers from `ProjectRows.Of()` and no new `projects` join
appears in any `ProjectRows` call site (`ProjectRowsSingleDefinitionTests` stays green untouched); no
tool, CLI verb or refusal changes behaviour.

**Gate command.**

```
dotnet build && dotnet test --filter "FullyQualifiedName~ProjectsTableDdlTests|FullyQualifiedName~ProjectIdCanonicalTests|FullyQualifiedName~SqliteProjectRegistryTests|FullyQualifiedName~SqliteMemoryStoreSchemaTests|FullyQualifiedName~ProjectRowsSingleDefinitionTests" --nologo -v m
```

**EventIds.** None — registration has a return value. **Docs.** None: the table is invisible until 6b.

## 6b — `project_id_token_get`

**Scope.** One MCP tool that mints a guidv7, registers it, and returns it plus the storage
instructions. Thin per `mcp-thin`: mint, register, return; it looks nothing up. The name is
family-first, verb-last, a **new `project_*` family** exactly as `code_get` opened `code_*` against
`memory_*` (decision 4). Its six consequences are mechanical and all named below.

**Files.**

| File | Change |
|---|---|
| `src/AiRaccoon/Tools/ProjectTools.cs` | **new** tool class. `private const string TnProjectIdTokenGet = "project_id_token_get";`, `[McpServerTool(Name = TnProjectIdTokenGet)]`, `[Description(...)]` on the method and on the one `string? name = null` parameter, returns `Task<ApiEnvelope<ProjectIdTokenResult>>` with a nested `sealed record`. Injects `IProjectRegistry`, `IToolGate` — interfaces only (`LayeringRulesTests.EveryToolClass_InjectsOnlyInterfaces`). Body ≤ 40 lines (`ToolMethodSizeTests`). |
| `src/AiRaccoon/Setup/McpServerSetup.cs` | `.WithTools<ProjectTools>()` in **both** chains — `CreateAppHost` (`:69-81`) and `ConfigureMcpServer` (`:163-178`). They are byte-identical today; keep them so. |
| `tests/AiRaccoon.Tests/Unit/Mcp/McpToolCompositionTests.cs` | `toolClasses.Count.ShouldBe(10)` at `:30` → `11`. |
| `tests/AiRaccoon.Tests/Unit/Mcp/McpToolContractTests.cs` | add the tool's wire-contract line to the hard-coded `ExpectedContract` (`:27-56`), in its **ordinal** slot. |
| `tests/AiRaccoon.Tests/Unit/Mcp/ToolInventoryTests.cs` | two edits, both principled — see below. |
| `docs/reference/agent-memory-server.md` | `## Tools (28)` → `(29)`; a table row; the per-family prose at `:25-28` gains the new family. |

**The two inventory-rule edits, and why neither is a fudge.**

1. `PackagedReadme_ToolsTable_ListsExactlyTheRegisteredTools` (`:135`) matches
   `^\|\s*`(memory_\w+|code_\w+)``; its own comment shows a **new family** was not anticipated.
   Extend to `(memory_\w+|code_\w+|project_\w+)` — one alternative, still derived.
2. `EveryTool_NamesTheProjectIdParameter` (`:103-114`) requires a `projectId`/`projectIds` parameter
   on every tool; `project_id_token_get` **mints** an id and has none, so the rule as written is
   false for it. No dead parameter, no exemption list (`derive-or-delete-the-list`). Re-derive it:
   **every tool whose body calls `gate.RequireAsync` must name the parameter** — read from source the
   way `ToolMethodSizeTests` already reads `src/AiRaccoon/Tools/*.cs`, equivalent today (28 tools, 28
   gated calls), and *stronger*: it also catches a tool that takes a `projectId` and never gates.

**The gate the tool cannot use.** `ToolGate.RequireAsync` (`ToolGate.cs:17-35`) refuses during a model
migration, rejects a blank project id, enforces the access mode. The mint tool keeps the **first** and
cannot have the other two — there is no project yet. Add `IToolGate.RequireBankAvailableAsync(string
toolName, CancellationToken)` with the migration check and have `RequireAsync` call it first: six
lines, no duplicated check, a clean discriminator for the re-derived rule. `WrapAsync(mintedId, …)` is
safe — `PromotionQueueService.cs:240-249` returns `PromotionMeta(0, …)` for a project with no rows.

**RED tests.** `tests/AiRaccoon.Tests/Unit/Mcp/ProjectTokenToolTests.cs` — class
`ProjectTokenToolTests`, `[Trait(Category, Unit)] [Trait(Speed, Fast)]`.

- `Get_ReturnsAGuidV7_InCanonicalLowercaseDForm` (version nibble 7,
  `Guid.Parse(r).ToString("D") == r`); `Get_RegistersTheMintedId` — the whole point of decision 4,
  and a fake `IProjectRegistry` makes it a real behavioural RED, not a compile stub;
  `Get_HonoursAnOptionalName`, `Get_WithNoName_RegistersANullName`,
  `Get_RefusesWhileAModelMigrationIsOpen` (the one gate half the tool keeps).
- Then let the six existing inventory gates go red on the un-edited test files **before** editing
  them and paste that output in the PR — the seen-failing evidence for the doc and contract edits.

**Acceptance.** `tools/list` shows 29 tools; the doc's heading, table, family prose and `:21-22`
sentence agree with reflection; `McpToolContractTests` describes the real wire shape; the tool mints,
registers and returns in one call and looks nothing up; the payload carries the storage instructions
in prose (where the id lives, not a secret, not access control); no `projectId` parameter invented.

**Gate command.**

```
dotnet build && dotnet test --filter "FullyQualifiedName~ProjectTokenToolTests|FullyQualifiedName~ToolInventoryTests|FullyQualifiedName~McpToolInventoryTests|FullyQualifiedName~McpToolCompositionTests|FullyQualifiedName~McpToolContractTests|FullyQualifiedName~ToolMethodSizeTests|FullyQualifiedName~LayeringRulesTests|FullyQualifiedName~McpServerSetupHostTests" --nologo -v m
```

**EventIds.** None.

**Docs.** `docs/reference/agent-memory-server.md` — heading (`:19`), tool table row, family prose
(`:25-28`), and **`:21-22`** (*"Every tool requires `projectId` … except `memory_promotion_list`"*):
`project_id_token_get` is a second exception, and that sentence is the prose twin of the
`EveryTool_NamesTheProjectIdParameter` rule this PR re-derives — they move together or the doc
contradicts the gate. Only the heading is test-derived; fix all four. No ADR amendment needed.

## 6c — the unregistered-project refusal

**Scope.** The point of the ADR. Three commits, in order — commit 0 is mechanical and reviewable on
its own, because commit 2 is the one a reviewer most needs to read cleanly.

**Commit 0 — `ToolGate`'s required dependencies.** `IModelMigrationStore? migrations = null`
(`ToolGate.cs:14`) violates *No optional Null-object ctor params* and 6c adds a required guard beside
it, so fix it first: all **40** `new ToolGate(...)` constructions across **19** test files pass every
dependency explicitly (**39** are two-argument; one already passes three). **No behaviour change, no
new test** — the existing suite is the gate. WP9/#519 does not reach this (its acceptance grep
`?? Null[A-Za-z]*\.Instance` does not match the optional-plus-null-check shape), so 6c is the only
lane that catches it. Same PR is fine; the same commit is not.

**Commit 1 — carry the canonical id (decision 2).** `IToolGate.RequireAsync` returns `Task<string>`,
the canonical project id, and every one of the **28 call sites across 10 tool classes**
(`MemoryTools` 10, `WorkspaceTools` 4, `WatchTools` 3, `Promotion`/`Quality`/`Share`/`Sweep` 2 each,
`Code`/`Performance`/`Sync` 1 each) assigns it and passes **that** downstream. Decision 2 binds:
*"stored/compared as the lowercase `D` form … two spellings of one guid are two different projects:
`project_id` and the vec0 `ctx` column are compared as strings, never as guids."*

**Two of the 28 sites cannot be fixed by assigning the result. Name them in the PR.**

- **`ShareTools.cs:57-61`** builds `ShareExtractRequest` from the raw `projectIds` at `:57`, **before**
  the per-id `RequireAsync` loop at `:60`, and `shareExtract.RunAsync(request, …)` then reads the raw
  ids — assigning the loop's result changes nothing. The loop reads `request.ProjectIds` *and*
  `request.Promotes`, so the request cannot simply be built after it. **Fix:** leave the construction
  where it is, collect the canonical ids in the loop, then
  `request = request with { ProjectIds = [.. canonicalIds] };` — it is a `sealed record`
  (`ShareExtract.cs:10`) whose `MetaProjectId` (`:28`) derives from `ProjectIds`, so
  `WrapAsync(request.MetaProjectId, …)` at `:67` carries the canonical form for free.
- **`PromotionTools.cs:39-45`** gates inside `if (projectId is not null)` while
  `queue.ListAsync(projectId, …)` (`:49`) and `gate.WrapAsync(projectId, …)` sit **outside** it, so a
  local declared in the branch cannot reach them. **Fix:** declare the canonical local before the
  `if`, initialised to `projectId`, and reassign inside the branch.

**The gate must be behavioural, not syntactic.** A source-grep rule ("the result is assigned") goes
green on `var canonical = await gate.RequireAsync(…);` never used again, and both sites above would
pass it while still writing the raw string. Instead:
`tests/AiRaccoon.Tests/Integration/Projects/CanonicalProjectIdReachesStorageTests.cs` — class
`CanonicalProjectIdReachesStorageTests`, `[Trait(Category, Integration)] [Trait(Speed, Fast)]`.
Register one guidv7; through each **write-capable** tool in turn, write under a *re-spelled* form
(upper-case, `{braced}`). Assert the bank holds **one** project not two, that `entries.project_id`
carries the lowercase `D` form, and that a `memory_search` with the re-spelled id finds those rows.
**Behavioural RED today.** `memory_share_extract` and `memory_promotion_list` are named cases.

**The vec0 assertion needs care — `ctx` is not the project id.** For the memory corpus `ctx` is
`ContextKeyExpression`'s *composed* key (only `vec_code` stores the raw id, `MemorySchema.cs:487`),
and a `vec_entries` row exists only once the entry is **embedded**. So drain first — the fake
embedder / `memory_embed_pending` path the existing integration tests use — then assert a **non-zero**
vec row count and `ctx == MemorySql.ContextKeyFor(ContextNaming.ProjectContext(canonical), canonical)`,
the shape `MemorySchemaVersionTests.cs:238` already asserts. Copy it rather than inventing one:
skipping the drain gives zero rows and a test that passes by finding nothing.

**Commit 2 — the refusal.**

| File | Change |
|---|---|
| `src/AiRaccoon.Core/Projects/UnregisteredProjectException.cs` | **new.** Message names the id and says how to get one (`project_id_token_get`). |
| `src/AiRaccoon/Tools/ToolRefusals.cs` | one row in `RefusalPrefixes` (`:25-64`): `[typeof(UnregisteredProjectException)] = "project-not-registered"`. Leave `WarningPrefixes` alone — a refusal is user input, Information is right. |
| `src/AiRaccoon/Projects/ProjectRegistrationGuard.cs` | **new**, beside `Access/MemoryAccessGuard.cs`. Injectable, `IProjectRegistry` only. |
| `src/AiRaccoon/Tools/ToolGate.cs` + `IToolGate.cs` | inject the guard as a **required** ctor param and call it after the blank check. |
| `docs/reference/agent-memory-server.md` | §Error shapes row; §Unknown-id rule (`:315`) gains the project-id case. |

**The rule, exactly.**

- **Which calls.** Keyed off the existing `AccessRequirement` enum, so there is no second list:
  `Write`/`Destructive` checked, `Read` passes through. The ADR decides this — decision 3 refuses *"a
  write"*, §Consequences says *"the failure lands on `memory_write`"*, §"Can each project still
  enumerate the DB" says every read path still returns everything, `AccessModePolicy.cs:20` already
  allows reads in every mode, and refusing a read of a ghost project turns "no results" into an error
  while preventing no accident. **One non-uniform site:** `ShareTools.cs:60-61` gates
  `request.Promotes ? Write : Read`, so for `memory_share_extract` the refusal is **per call, not per
  tool** — an unregistered id in `projectIds` is refused only on the promoting path.
- **The test.** *No registry row **AND** no rows under `ProjectRows.Of()`* → refuse, guid or not.
  `Of()`, never `Scope()` — see 6a for why `Scope()` makes this always-true.
- **Warn-but-work.** Passes the second half only (rows, no registration) → proceed, log once. The
  WP6 brief says *"old raw-text ids that ARE registered warn-but-work"*; the ADR and the owner's #448
  fold both say **rows**, not registration. Follow the ADR.
- **Canonicalisation first.** `ProjectId.TryCanonicalize` runs before the lookup, so a re-spelled
  registered guid is accepted. A non-guid passes through unchanged to the rows half — exactly how a
  legacy `jsaa` keeps working.

**RED tests.**

- `tests/AiRaccoon.Tests/Unit/Projects/ProjectRegistrationGuardTests.cs` — `[Trait(Category, Unit)]
  [Trait(Speed, Fast)]`: `AnUnregisteredGuidV7_IsRefused`, `AnUnregisteredRawTextId_IsRefused`,
  `ALegacyRawTextIdWithRows_IsAllowedAndWarns`, `ARegisteredId_IsAllowedSilently`,
  `AReSpelledRegisteredGuid_IsAllowed`, `AReadRequirement_IsNeverRefused`. Compile-level RED.
- `tests/AiRaccoon.Tests/Integration/Mcp/UnregisteredProjectRefusalTests.cs` —
  `[Trait(Category, Integration)] [Trait(Speed, Fast)]`:
  `MemoryWrite_ToAnUnregisteredGuid_ReturnsProjectNotRegistered` (wire prefix through
  `ToolRefusals.Filter`), `MemorySearch_OnAnUnregisteredId_StillAnswers`,
  `MemoryWrite_ToALegacyIdWithRows_StillWrites`. The first is **behavioural RED today**: it succeeds
  and founds a ghost project — the defect being removed.
- `ToolRefusalsTests.DocumentedPrefixes_MatchCodeExactlyInBothDirections` (`:507-527`) goes red the
  moment the prefix is in code and not the doc. Let it, then fix the doc.

**Acceptance.** The three ADR-fixed criteria from §WP6, all seen failing first: a legacy id the bank
knows works with a warning; one it does not know is refused; an unregistered guidv7 is refused. Plus:
reads unaffected; a re-spelled registered guid accepted; the prefix `project-not-registered` in both
the code and doc tables; no `projects` join in any `ProjectRows` call site; and the canonical id
reaching `entries.project_id` and the vec0 `ctx` through **every** write-capable tool.

**Gate command.**

```
dotnet build && dotnet test --filter "FullyQualifiedName~ProjectRegistrationGuardTests|FullyQualifiedName~UnregisteredProjectRefusalTests|FullyQualifiedName~CanonicalProjectIdReachesStorageTests|FullyQualifiedName~ToolRefusalsTests|FullyQualifiedName~ToolGateTests" --nologo -v m
dotnet test --filter "Category=Integration&Speed=Fast" --nologo -v m
```

The second run is not optional: 6c changes the write path of **every** tool, so the Integration/Fast
lane *is* the affected scope — not a full-suite sweep.

**EventIds.** **One new id, 433**, owner `src/AiRaccoon/Projects/ProjectRegistrationGuard.cs`:
`LegacyProjectIdAccepted` at Warning — *"project id {ProjectId} is not registered; it works because
the bank already holds rows for it. Convert it with `project id convert`."* `429-432` are
`ManifestPoolingRepair`'s (#497/#504) and `500` starts the next owner, so `433` is a clean
non-interleaving gap (`LoggerMessageEventIdTests.EventIdBlocks_DoNotInterleaveBetweenOwners`). Add the
row to `docs/reference/logging-event-ids.md` in the same PR or
`EveryEventIdInSource_FallsInsideADocumentedBlock` goes red. The refusal needs no id — it already logs
through `ToolRefusals.Log.ToolRefused` (910).

**Docs.** `docs/reference/agent-memory-server.md` §Error shapes + §Unknown-id rule;
`docs/reference/logging-event-ids.md` (433). **`docs/adr/0046-*`**: a one-line cross-reference —
ADR-0089's registry answers "which projects exist", `ProjectRows` keeps answering "which rows
belong". The ADR's §Membership argues it; ADR-0046 is where a reader looks first.
**`docs/adr/README.md`: no change** — an amendment is needed only if the owner orders the back-fill
(deviation 2) or relaxes decision 2 (§"One amendment the owner may decline").

## Collisions and ordering — strictly serial

`6a → 6b → 6c`. Each lane branches from `origin/main` **after** the previous sub-PR merges; run
`git fetch origin && git merge --no-edit origin/main` first (main moves under this repo constantly).

| | src files touched | test files touched | doc files touched |
|---|---|---|---|
| **6a** | `Core/Projects/*` (new), `Sqlite/MemorySchema.cs`, `Sqlite/MemorySql.cs`, `Sqlite/Memory/SqliteMemoryStore.Projects.cs` (new), `Setup/AppRegistrations.cs` | 3 new classes | none |
| **6b** | `Tools/ProjectTools.cs` (new), `Tools/IToolGate.cs`, `Tools/ToolGate.cs`, `Setup/McpServerSetup.cs` | 1 new class + `ToolInventoryTests`, `McpToolCompositionTests`, `McpToolContractTests` | `agent-memory-server.md` |
| **6c** | `Core/Projects/UnregisteredProjectException.cs` (new), `Projects/ProjectRegistrationGuard.cs` (new), `Tools/IToolGate.cs`, `Tools/ToolGate.cs`, **all 10 `Tools/*.cs`** | 3 new classes + `ToolRefusalsTests` | `agent-memory-server.md`, `logging-event-ids.md`, `0046-*.md` |

**Why serial, not the `6b ∥ 6c` the session plan allows.** Both open `Tools/IToolGate.cs` and
`Tools/ToolGate.cs` (6b adds `RequireBankAvailableAsync`; 6c changes `RequireAsync`'s return type and
constructor), and 6c's commit 1 rewrites all ten tool classes including the one 6b creates. The 40
`new ToolGate(...)` constructions across 19 test files are 6c's real bulk — commit 0, never folded
into the refusal commit.

**Wave-2 disjointness holds** — no wave-1 lane opens any file above (§Sequencing shared-file map).
**6c collides with WP9** (#519 rewrites 83 call sites across 73 files): WP9 is wave 4 and lands last,
so 6c merges before it starts or WP9 re-derives against a moved `RequireAsync` signature.

**Lane, all three:** dotnet-engineer / **Sonnet** implements; code-reviewer / **Opus** reviews, never
the implementer. Reviewer's standing focus for 6c: the three ADR-fixed criteria were **seen failing**
before the production code, and no `projects` join entered a `ProjectRows` call site.

## One amendment the owner may decline — not an open question

The ADR settles everything 6a/6b/6c needs; this is the one place a simpler implementation would need
ratified text changed, recorded so the owner can decline it rather than meet it in review. **It
blocks no commit.** Decision 2 says ids are *"accepted in any form `Guid.TryParse` accepts and
stored/compared as the lowercase `D` form"*. Honouring the *stored* half is what forces commit 1 — a
28-site change to the return type of the most-called method on the tool surface, inside the already
risk-bearing sub-PR. The simpler alternative is to **accept only the canonical spelling**, refusing a
re-spelled registered guid with a message naming the canonical form: nothing downstream changes, and
two spellings still cannot become two projects. That reaches decision 2's *goal* by a different
mechanism than its *letter*, so it is an amendment, not a plan-level call. **Recommendation:
implement decision 2 as written** unless the owner prefers the strict form — answer before commit 2,
where the two diverge. Nothing else is open: reads are not refused (§6c "Which calls"), the refusal
test is rows-not-registration (decision 3), the table is decision 5, the tool name is decision 4.
