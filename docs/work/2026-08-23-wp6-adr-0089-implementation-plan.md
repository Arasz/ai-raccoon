# WP6 — ADR-0089 parts 1–3 implementation plan (6a / 6b / 6c)

Date: 2026-08-23. Lane: architect / Opus. Satisfies the WP6 precondition in
`docs/work/2026-08-23-post-delta-4-plan.md` §WP6 — *"the architect's implementation plan defines the
RED test, the acceptance criteria and the exact gate command for each sub-PR, and no `6x` PR opens
until it does."* Parts 4–5 are carried in `docs/work/2026-08-23-post-delta-5-plan.md` §WP1/§WP2.

Source of truth: `docs/adr/0089-the-project-id-is-a-guidv7-and-that-is-not-access-control.md`
(Accepted, decisions 1–8). Everything below cites it or the tree at `task/post-delta-4-plan-and-gate`
merged with `origin/main` @ `32a5946e`.

## Session todo list

1. Read §"Two deviations from the WP6 brief" — one of them changes what 6a builds.
2. `6a` — `projects` table in the unconditional `Ddl` block, `ProjectId` canonicaliser, `IProjectRegistry`. Merge before 6b opens.
3. `6b` — `project_id_token_get` on a new `ProjectTools` class; six inventory/contract gates move with it. Merge before 6c opens.
4. `6c` — canonical id carried out of `ToolGate.RequireAsync`, then the unregistered-project refusal.
5. Owner: answer the one open question in §Open design questions before 6c's second commit.

## Two things the owner ruled — do not re-ask

- **`ai-raccoon.ignore`, not `.gitignore`.** Verbatim on #448: *"Not gitignored - ai-raccoon.ignore -
  we dont want this file in memory"*. ADR-0089 decision 8 carries the mechanism. Nothing in 6a/6b/6c
  writes either file — this is 6e/pd5-WP2 — but no lane may propose a `.gitignore` line.
- **A non-guid unregistered id is refused too.** Verbatim: *"we also want to eliminate the case: any
  valid guid creates project"*, folded into decision 3 as *"A write to an id with no registry row is
  refused — guid or not"*. The refusal is about **registration**, never about the string's shape.

## Two deviations from the WP6 brief — read before starting 6a

The brief that commissioned this plan says *"6a (schema v11 …) … migration that back-fills existing
project ids from `SelectProjectIds` as legacy-grandfathered rows"*. Both halves contradict the
ratified ADR, and this plan follows the ADR.

1. **No `CurrentVersion` bump. It stays 10.** ADR-0089 decision 5: *"**No `CurrentVersion` bump.** The
   table belongs in the unconditional `Ddl` block"*; `MemorySchema.cs:54` is `CurrentVersion = 10`.
   ADR-0086's reason: `user_version` survives
   `VACUUM INTO` and the sync gate refuses a pull from a newer `user_version`, so a bump hard-fails
   every concurrent session and every peer. `docs/work/2026-08-23-post-delta-4-plan.md` §Review (e)
   repeats this. The delivery mechanism is already built: `MemorySchema.SchemaDigest`
   (`MemorySchema.cs:503-510`) is SHA-256 of the `Ddl` string in `PRAGMA application_id`, so adding
   the table changes the digest and `EnsureAsync` (`MemorySchema.cs:578-582`) re-runs `Ddl` on the
   next open of every existing bank. **There is no ladder step and no v11.**
2. **No back-fill of legacy ids into `projects`.** The ADR makes `SelectProjectIds`
   (`MemorySql.cs:58-64`) a **live oracle**, not a migration source — decision 3: *"the refusal test
   is 'no registry row **and** no existing rows'"*, and the owner's own fold on #448 repeats it
   verbatim. The ADR then names *"a legacy id with rows and no registration"* as an **intended**
   coexisting state (§Consequences). A back-fill would erase that state, would put raw-text ids into
   a column decision 5 documents as *"canonical lowercase D-form guidv7"*, and — the operational
   reason — is a **one-time** act on a bank that keeps gaining project ids from `memory_sync` pulls
   and `VACUUM INTO` copies, so a post-back-fill restore would see its own legacy projects refused.
   Cost of not back-filling: a listing surface reading only the registry misses legacy projects — it
   unions the two, which is what the ADR's §Membership section already prescribes.

If the owner wants the back-fill anyway, it is an ADR-0089 amendment plus a `legacy` discriminator
column, not a plan-level change — raise it before 6a opens.

## Conventions every sub-PR inherits

- TDD: the RED test lands first and is **seen failing**. Where a test cannot fail behaviourally
  because the type does not exist yet, that is marked *compile-level RED* below and paired with at
  least one behavioural RED in the same PR.
- New test classes carry **class-level** `[Trait(TestCategories.Category, …)]` +
  `[Trait(TestCategories.Speed, …)]` or they fall outside every filter (`TestCategories.cs:13-33`).
- Guard clauses via `ArgumentException.ThrowIfNullOrWhiteSpace`; nested static partial `Log` with
  `[LoggerMessage]` + explicit `EventId`; no optional Null-object ctor params; one PR per sub-part.

---

## 6a — the `projects` table, the canonicaliser, and the registration write path

**Scope.** The substrate. No tool surface, no refusal, no behaviour change to any existing call.

**Files.**

| File | Change |
|---|---|
| `src/AiRaccoon.Core/Projects/ProjectId.cs` | **new.** `Canonicalize(string)` / `TryCanonicalize(string, out string)` — `Guid.TryParse` in, `ToString("D")` lowercase out; a non-guid returns false and is passed through untouched. A small pure helper, not a component. |
| `src/AiRaccoon.Core/Projects/IProjectRegistry.cs` | **new.** `Task RegisterAsync(string projectId, string? name, CancellationToken)`, `Task<bool> IsRegisteredAsync(string projectId, CancellationToken)`, `Task<bool> HasRowsAsync(string projectId, CancellationToken)`. Split out of `IMemoryStore` exactly as `IModelMigrationStore` was (`src/AiRaccoon.Core/Memory/IModelMigrationStore.cs:1-25`). |
| `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs` | `projects` table added to the `Ddl` raw string (`:75`), beside the `metrics` precedent at `:344-346`. **`CurrentVersion` untouched.** |
| `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs` | `InsertProject` (`INSERT … ON CONFLICT(id) DO NOTHING`), `ProjectIsRegistered`, and `ProjectHasRows` — the last composing `ProjectRows.Scope()` so ADR-0046's single definition is reused, never re-typed. |
| `src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.Projects.cs` | **new partial**, mirroring `SqliteMemoryStore.ModelMigration.cs` (open via `factory`, `MemorySchema.EnsureCheapAsync`, Dapper). |
| `src/AiRaccoon/Setup/AppRegistrations.cs` (`:304` block), `src/AiRaccoon/AppRunner.cs` (`:227` block) | `services.AddSingleton<IProjectRegistry>(…)` alongside `IModelMigrationStore`. |

Table shape exactly as decision 5 (`id TEXT PRIMARY KEY`, `name TEXT`, `created_at INTEGER NOT NULL`).
`created_at` comes from the injected `TimeProvider`, never `DateTimeOffset.UtcNow`.

**One performance question to settle, not assume.** `ProjectHasRows` runs on every gated write once
6c lands, so `SelectProjectIds` (a `SELECT DISTINCT … ORDER BY` full scan) must **not** be reused
for it. Run `EXPLAIN QUERY PLAN` on `ProjectHasRows` against a real bank; if it reports a SCAN of
`entries`, say so in the PR — the index decision belongs in 6a or nowhere.

**RED tests.**

- `tests/AiRaccoon.Tests/Integration/Storage/ProjectsTableDdlTests.cs` — class `ProjectsTableDdlTests`,
  `[Trait(Category, Integration)] [Trait(Speed, Fast)]`.
  - `OpeningALegacyBank_CreatesTheProjectsTable` — **behavioural RED, and the one that matters**:
    open a bank at the pre-change schema, re-open through `MemorySchema.EnsureAsync`, assert
    `sqlite_master` holds `projects`. Fails today for the right reason (no such table), and it is
    the digest-rerun path that ADR-0086 requires. Writable with today's types — no new member.
  - `CreatingTheProjectsTable_DoesNotChangeUserVersion` — reads `PRAGMA user_version` before and
    after and asserts both are `MemorySchema.CurrentVersion` and that it equals `10`. **This is the
    check that must be seen failing**: bump `CurrentVersion` to 11 locally, watch it go red, revert.
- `tests/AiRaccoon.Tests/Unit/Projects/ProjectIdCanonicalTests.cs` — class `ProjectIdCanonicalTests`,
  `[Trait(Category, Unit)] [Trait(Speed, Fast)]`. `TryCanonicalize_LowercasesAndStripsBraces`,
  `TryCanonicalize_LeavesARawTextIdUntouchedAndReturnsFalse`. Compile-level RED (new type).
- `tests/AiRaccoon.Tests/Integration/Storage/SqliteProjectRegistryTests.cs` — class
  `SqliteProjectRegistryTests`, `[Trait(Category, Integration)] [Trait(Speed, Fast)]`.
  `RegisterAsync_MakesIsRegisteredTrue`, `RegisterAsync_IsIdempotentForTheSameId`,
  `RegisterAsync_StoresTheCanonicalLowercaseDForm`, `HasRowsAsync_IsFalseForARegisteredProjectWithNoEntries`,
  `HasRowsAsync_IsTrueForALegacyRawTextIdThatHasRows`. Compile-level RED (new interface); the last
  two are the pair that pins the ADR's two intended disagreements.

**Acceptance.** `projects` exists on a fresh bank **and** on a legacy bank re-opened once;
`PRAGMA user_version` is 10 before and after; `IProjectRegistry` registers idempotently and stores
the canonical form; `HasRowsAsync` answers from `ProjectRows.Scope()` and no new `projects` join
appears in any `ProjectRows` call site (`ProjectRowsSingleDefinitionTests` stays green untouched);
no tool, CLI verb or refusal changes behaviour.

**Gate command.**

```
dotnet build
dotnet test --filter "FullyQualifiedName~ProjectsTableDdlTests|FullyQualifiedName~ProjectIdCanonicalTests|FullyQualifiedName~SqliteProjectRegistryTests|FullyQualifiedName~SqliteMemoryStoreSchemaTests|FullyQualifiedName~ProjectRowsSingleDefinitionTests" --nologo -v m
```

**EventIds.** None — registration has a return value. **Docs.** None: the table is invisible until 6b.

---

## 6b — `project_id_token_get`

**Scope.** One MCP tool that mints a guidv7, registers it, and returns it plus the storage
instructions. Thin per `mcp-thin`: mint, register, return. It looks nothing up (decision 4).

**Naming.** `project_id_token_get` — family first, verb last, a **new `project_*` family** exactly as
`code_get` opened `code_*` against `memory_*` (decision 4). Consequences are mechanical and all six
are named below.

**Files.**

| File | Change |
|---|---|
| `src/AiRaccoon/Tools/ProjectTools.cs` | **new** tool class. `private const string TnProjectIdTokenGet = "project_id_token_get";`, `[McpServerTool(Name = TnProjectIdTokenGet)]`, `[Description(...)]` on the method and on the one `string? name = null` parameter, returns `Task<ApiEnvelope<ProjectIdTokenResult>>` with a nested `sealed record`. Injects `IProjectRegistry`, `IToolGate` — interfaces only (`LayeringRulesTests.EveryToolClass_InjectsOnlyInterfaces`). Body ≤ 40 lines (`ToolMethodSizeTests`). |
| `src/AiRaccoon/Setup/McpServerSetup.cs` | `.WithTools<ProjectTools>()` in **both** chains — `CreateAppHost` (`:67-80`) and `ConfigureMcpServer` (`:163-178`). They are byte-identical today; keep them so. |
| `tests/AiRaccoon.Tests/Unit/Mcp/McpToolCompositionTests.cs` | `toolClasses.Count.ShouldBe(10)` at `:30` → `11`. |
| `tests/AiRaccoon.Tests/Unit/Mcp/McpToolContractTests.cs` | add the tool's wire-contract line to the hard-coded `ExpectedContract` (`:27-56`), in its **ordinal** slot. |
| `tests/AiRaccoon.Tests/Unit/Mcp/ToolInventoryTests.cs` | two edits, both principled — see below. |
| `docs/reference/agent-memory-server.md` | `## Tools (28)` → `(29)`; a table row; the per-family prose at `:25-28` gains the new family. |

**The two inventory-rule edits, and why neither is a fudge.**

1. `PackagedReadme_ToolsTable_ListsExactlyTheRegisteredTools` (`:135`) matches
   `^\|\s*`(memory_\w+|code_\w+)``; its own comment shows a **new family** was not anticipated.
   Extend to `(memory_\w+|code_\w+|project_\w+)` — one alternative, still derived.
2. `EveryTool_NamesTheProjectIdParameter` (`:103-114`) requires every tool to declare a `projectId`
   or `projectIds` parameter. `project_id_token_get` **mints** an id, so it has none — the rule as
   written is false for it. Do not add a dead parameter and do not add an exemption list
   (`derive-or-delete-the-list`). Re-derive the predicate instead: **every tool whose body calls
   `gate.RequireAsync` must name the parameter**. That set is readable from source the same way
   `ToolMethodSizeTests` already reads `src/AiRaccoon/Tools/*.cs`, and it is a *stronger* rule than
   today's — it also catches a tool that takes a `projectId` and never gates on it.

**The gate the tool cannot use.** `ToolGate.RequireAsync` (`ToolGate.cs:17-35`) refuses during a
model migration, rejects a blank project id, and enforces the access mode. The mint tool must keep
the **first** and cannot have the other two — there is no project yet. Add
`IToolGate.RequireBankAvailableAsync(string toolName, CancellationToken)` carrying the migration
check, and have `RequireAsync` call it as its own first line. Six lines, no duplicated check, and it
gives the re-derived rule above a clean discriminator. `WrapAsync(mintedId, …)` still applies —
`IPromotionQueue.GetMetaAsync` on a brand-new project returns an empty meta.

**RED tests.** `tests/AiRaccoon.Tests/Unit/Mcp/ProjectTokenToolTests.cs` — class
`ProjectTokenToolTests`, `[Trait(Category, Unit)] [Trait(Speed, Fast)]`.

- `Get_ReturnsAGuidV7_InCanonicalLowercaseDForm` — behavioural once the class exists; assert version
  nibble 7 and `Guid.Parse(result).ToString("D") == result`.
- `Get_RegistersTheMintedId` — the whole point of decision 4: `IsRegisteredAsync` is true afterwards.
  A fake `IProjectRegistry` makes this a real behavioural RED, not a compile stub.
- `Get_HonoursAnOptionalName` and `Get_WithNoName_RegistersANullName`.
- `Get_RefusesWhileAModelMigrationIsOpen` — pins the one gate half the tool keeps.
- Then let the six existing inventory gates go red on the un-edited test files **before** editing
  them, and paste that red output in the PR. That is the seen-failing evidence for the doc and
  contract edits.

**Acceptance.** `tools/list` shows 29 tools; the reference doc's heading, table and family prose all
agree with reflection; `McpToolContractTests` describes the real wire shape; the tool mints,
registers and returns in one call and looks nothing up; the returned payload carries the storage
instructions in prose (where to put the id, that it is not a secret, that it is not access control);
no `projectId` parameter is invented.

**Gate command.**

```
dotnet build
dotnet test --filter "FullyQualifiedName~ProjectTokenToolTests|FullyQualifiedName~ToolInventoryTests|FullyQualifiedName~McpToolInventoryTests|FullyQualifiedName~McpToolCompositionTests|FullyQualifiedName~McpToolContractTests|FullyQualifiedName~ToolMethodSizeTests|FullyQualifiedName~LayeringRulesTests|FullyQualifiedName~McpServerSetupHostTests" --nologo -v m
```

**EventIds.** None.

**Docs.** `docs/reference/agent-memory-server.md` — tool count heading, tool table row, the
per-family sentence at `:25-28` (prose, not test-derived; fix it anyway). No ADR amendment: decision
4 already names the tool and sanctions the rename.

---

## 6c — the unregistered-project refusal

**Scope.** The point of the ADR. Two commits, in this order.

**Commit 1 — carry the canonical id (decision 2).** `IToolGate.RequireAsync` returns
`Task<string>`: the canonical project id. Every one of the **28 call sites across 10 tool classes**
(`MemoryTools` 10, `WorkspaceTools` 4, `WatchTools` 3, `PromotionTools`/`QualityTools`/`ShareTools`/
`SweepTools` 2 each, `CodeTools`/`PerformanceTools`/`SyncTools` 1 each) assigns the result and passes
**that** downstream. Decision 2 is binding: *"stored/compared as the lowercase
`D` form … Without this, two spellings of one guid are two different projects: `project_id` and the
vec0 `ctx` column are compared as strings, never as guids."* A changed return type does not break a
call site that ignores it, so this needs its own gate:

- `tests/AiRaccoon.Tests/Unit/Layering/ProjectIdCanonicalAtToolBoundaryTests.cs` — class
  `ProjectIdCanonicalAtToolBoundaryTests`, `[Trait(Category, Unit)] [Trait(Speed, Fast)]`.
  `EveryRequireAsyncCallSite_AssignsTheCanonicalId` reads `src/AiRaccoon/Tools/*.cs` and fails naming
  any `RequireAsync(` call whose result is discarded. Behavioural RED today — all 28 discard it.

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
  `Write` and `Destructive` are checked; `Read` passes through untouched. This is what the ADR
  decides — decision 3 refuses *"a write"*; §Consequences says *"the failure lands on
  `memory_write`"*; §"Can each project still enumerate the DB" states every read path still returns
  everything, and `AccessModePolicy.cs:20` already allows reads in every mode. Refusing a read of a
  ghost project converts "no results" into an error and prevents no accident.
- **The test.** *No registry row **AND** no rows under `ProjectRows.Scope()`* → refuse, guid or not.
- **Warn-but-work.** Passes the second half only (rows, no registration) → proceed, log once.
  Note the WP6 brief says *"old raw-text ids that ARE registered warn-but-work"*; the ADR and the
  owner's #448 fold both say **rows**, not registration. Follow the ADR.
- **Canonicalisation first.** `ProjectId.TryCanonicalize` runs before the lookup, so a re-spelled
  registered guid is accepted, not refused. A non-guid is passed through unchanged and reaches the
  rows half — which is exactly how a legacy `jsaa` keeps working.

**RED tests.**

- `tests/AiRaccoon.Tests/Unit/Projects/ProjectRegistrationGuardTests.cs` — class
  `ProjectRegistrationGuardTests`, `[Trait(Category, Unit)] [Trait(Speed, Fast)]`.
  `AnUnregisteredGuidV7_IsRefused`, `AnUnregisteredRawTextId_IsRefused`,
  `ALegacyRawTextIdWithRows_IsAllowedAndWarns`, `ARegisteredId_IsAllowedSilently`,
  `AReSpelledRegisteredGuid_IsAllowed`, `AReadRequirement_IsNeverRefused`. Compile-level RED.
- `tests/AiRaccoon.Tests/Integration/Mcp/UnregisteredProjectRefusalTests.cs` — class
  `UnregisteredProjectRefusalTests`, `[Trait(Category, Integration)] [Trait(Speed, Fast)]`.
  `MemoryWrite_ToAnUnregisteredGuid_ReturnsProjectNotRegistered` (asserts the wire prefix through
  `ToolRefusals.Filter`), `MemorySearch_OnAnUnregisteredId_StillAnswers`,
  `MemoryWrite_ToALegacyIdWithRows_StillWrites`. The first is **behavioural RED today** — it
  currently succeeds and founds a ghost project, which is the defect being removed.
- `ToolRefusalsTests.DocumentedPrefixes_MatchCodeExactlyInBothDirections` (`:507-527`) goes red the
  moment the prefix is added to code and not to the doc. Let it, then fix the doc.

**Acceptance.** The three ADR-fixed criteria from §WP6, all seen failing first: a legacy id the bank
knows works with a warning; a legacy id it does not know is refused; an unregistered guidv7 is
refused. Plus: reads are unaffected; a re-spelled registered guid is accepted; the prefix is
`project-not-registered` in both the code table and the doc table; no `projects` join entered any
`ProjectRows` call site; every `RequireAsync` result is assigned.

**Gate command.**

```
dotnet build
dotnet test --filter "FullyQualifiedName~ProjectRegistrationGuardTests|FullyQualifiedName~UnregisteredProjectRefusalTests|FullyQualifiedName~ProjectIdCanonicalAtToolBoundaryTests|FullyQualifiedName~ToolRefusalsTests|FullyQualifiedName~ToolGateTests" --nologo -v m
dotnet test --filter "Category=Integration&Speed=Fast" --nologo -v m
```

The second run is not optional: 6c changes the write path of **every** tool, so the Integration/Fast
lane *is* the affected scope — not a full-suite sweep.

**EventIds.** **One new id, 433**, owner `src/AiRaccoon/Projects/ProjectRegistrationGuard.cs`:
`LegacyProjectIdAccepted` at Warning — *"project id {ProjectId} is not registered; it works because
the bank already holds rows for it. Convert it with `project id convert`."* `429-432` are
`ManifestPoolingRepair`'s as of #497/#504, and `500` starts the next owner, so `433` is a clean gap
that does not interleave (`LoggerMessageEventIdTests.EventIdBlocks_DoNotInterleaveBetweenOwners`).
Add the row to `docs/reference/logging-event-ids.md` in the same PR or
`EveryEventIdInSource_FallsInsideADocumentedBlock` goes red. The refusal itself needs no id — it
already logs through `ToolRefusals.Log.ToolRefused` (EventId 910).

**Docs.** `docs/reference/agent-memory-server.md` §Error shapes + §Unknown-id rule;
`docs/reference/logging-event-ids.md` (433). **`docs/adr/0046-*`**: a one-line cross-reference —
ADR-0089's registry answers "which projects exist", `ProjectRows` keeps answering "which rows
belong". The ADR's §Membership argues it; ADR-0046 is where a reader looks first.
**`docs/adr/README.md`: no change** — no amendment is needed for 6a/6b/6c as planned. It *is* needed
if the owner orders the back-fill (deviation 2) or relaxes decision 2 (open question 1).

---

## Collisions and ordering — strictly serial

`6a → 6b → 6c`. Each lane branches from `origin/main` **after** the previous sub-PR merges; run
`git fetch origin && git merge --no-edit origin/main` first (main moves under this repo constantly).

| | src files touched | test files touched | doc files touched |
|---|---|---|---|
| **6a** | `Core/Projects/*` (new), `Sqlite/MemorySchema.cs`, `Sqlite/MemorySql.cs`, `Sqlite/Memory/SqliteMemoryStore.Projects.cs` (new), `Setup/AppRegistrations.cs`, `AppRunner.cs` | 3 new classes | none |
| **6b** | `Tools/ProjectTools.cs` (new), `Tools/IToolGate.cs`, `Tools/ToolGate.cs`, `Setup/McpServerSetup.cs` | 1 new class + `ToolInventoryTests`, `McpToolCompositionTests`, `McpToolContractTests` | `agent-memory-server.md` |
| **6c** | `Core/Projects/UnregisteredProjectException.cs` (new), `Projects/ProjectRegistrationGuard.cs` (new), `Tools/IToolGate.cs`, `Tools/ToolGate.cs`, **all 10 `Tools/*.cs`** | 3 new classes + `ToolRefusalsTests` | `agent-memory-server.md`, `logging-event-ids.md`, `0046-*.md` |

**Why serial rather than the `6b ∥ 6c` the session plan allows.** Both open `Tools/IToolGate.cs`
and `Tools/ToolGate.cs` (6b adds `RequireBankAvailableAsync`; 6c changes `RequireAsync`'s return
type and constructor), and 6c's commit 1 rewrites all ten tool classes including the one 6b creates.

**Wave-2 disjointness holds** — no wave-1 lane opens any file above (§Sequencing shared-file map).
**6c collides with WP9**
(#519 rewrites 83 call sites across 73 files) — WP9 is wave 4 and lands last, so 6c must be merged
before it starts, or WP9 re-derives against a moved `RequireAsync` signature.

**Measured.** `ToolGate`'s existing `IModelMigrationStore? migrations = null` violates *No optional
Null-object ctor params*; 6c adds a **required** guard param beside it, breaking the **40**
two-argument `new ToolGate(...)` constructions across **19** test files. Fix them in 6c — do not add
a second optional param to dodge the work.

## Lane and model

| Sub-PR | Implementer | Reviewer |
|---|---|---|
| 6a | dotnet-engineer / **Sonnet** | code-reviewer / **Opus** |
| 6b | dotnet-engineer / **Sonnet** | code-reviewer / **Opus** |
| 6c | dotnet-engineer / **Sonnet** | code-reviewer / **Opus** — never the implementer |

Reviewer's standing focus for 6c: the three ADR-fixed criteria were **seen failing** before the production code, and no `projects` join entered a `ProjectRows` call site.

## Open design questions

**One.** Decision 2 says ids are *"accepted in any form `Guid.TryParse` accepts and stored/compared
as the lowercase `D` form"*. Honouring the *stored* half forces 6c's commit 1 — a 28-call-site
change to the return type of the most-called method on the tool surface, inside the sub-PR that is
already the risk-bearing one. The strictly simpler alternative is to **accept only
the canonical spelling** and refuse a re-spelled registered guid with a message naming the canonical
form: nothing downstream changes, and two spellings still cannot become two projects. That reaches
decision 2's *goal* by a different mechanism than its *letter*, so it is an ADR-0089 amendment, not
a plan-level call. **Recommendation: implement decision 2 as written** (commit 1 as planned) unless
the owner prefers the strict form — answer before 6c's commit 2, which is where they diverge.

Everything else the ADR settles: reads are not refused (§6c "Which calls"), the refusal test is
rows-not-registration (decision 3), the table is decision 5, the tool name is decision 4.
