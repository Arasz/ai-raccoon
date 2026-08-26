# PLAN — integrated implementation plan (task `doctor-feature-match-cont`)

Planning lane output, 2026-08-26, written against the task worktree. Will be reviewed by two
independent lanes (architecture/correctness, QA/test-design) before implementation.

---

---

# Integrated implementation plan — task `doctor-feature-match-cont` (three follow-ups, one PR, 1.36.1)

Worktree: `.ai-badger/worktrees/doctor-feature-match-cont` (branch `task/doctor-feature-match-cont`). Every ruling cites source read in this worktree. Nothing was edited.

## 1. Integrated rulings

### Ruling A — follow-up 1: where the guard lives, what it prints, which exit code

**A1. The guard lives in `SettingsEndpoint`'s per-key DELETE handler — not in `SqliteSettingsStore.DeleteSettingAsync`.**

Verified seam (write path): CLI verb `settings model reset` (ConfigCommands.cs:62) **and** `settings model embedding reset` (ConfigCommands.cs:58) dispatch to the **same** `SettingsCommands.ModelResetAsync` (SettingsCommands.cs:260-275) — one guard covers both verbs. It `foreach`s `{Provider, Model, BaseUrl, Engine, ApiKey, Dimensions}` (Provider **first**, :263-268) calling `store.DeleteSettingAsync`. The CLI's `store` is `LazyServerSettingsStore` → `ServerSettingsStore` (HTTP client; AppRunner.cs:223-236); `ServerSettingsStore.HasOpenModelMigrationAsync` **throws `NotSupportedException` by design** for CLI callers (ServerSettingsStore.cs:106-109), so the CLI cannot pre-check — the guard must be server-side. Server-side, the DELETE route is `SettingsEndpoint.MapDelete` (SettingsEndpoint.cs:54-65) → `ISettingsStore` = **`SqliteSettingsStore`** (AppRegistrations.cs:335), which does **not** implement `IModelMigrationStore` — that interface resolves to `SqliteMemoryStore` (AppRegistrations.cs:327), whose `HasOpenModelMigrationAsync` (SqliteMemoryStore.ModelMigration.cs:15-22) is the **same check ToolGate runs** (ToolGate.cs:25-29).

Why the endpoint, not the store class:
1. **One predicate implementation.** The endpoint composes the existing `IModelMigrationStore.HasOpenModelMigrationAsync` — the ToolGate's own check, with its cheap `EnsureCheapAsync` cost profile. A store-level guard would force `SqliteSettingsStore` (a dumb KV store, per-call `OpenBankAsync`) to run its own copy of the open-migration predicate — a fourth spelling of a fact R1 S4 already flagged at three (MemorySql.cs:474-475, ModelMigrationJob.cs:26-29, ToolGate). The endpoint approach adds zero new predicate code.
2. **The endpoint is the only write route (ADR-0075).** The server is the only process that writes the bank; every CLI deleter crosses `MapDelete`. Verified: the **only** deleter of `EmbeddingSettingsKeys.Provider` ("embedding.provider", EmbeddingSettingsKeys.cs:9) in `src/` is `ModelResetAsync`'s foreach (grep of all `DeleteSettingAsync` call sites: SettingsCommands.cs:79,117-118,270,302-304; EncryptionCommands.cs:249-270 — encryption keys; WatchCommands; SyncCommands; ExtractCommands — none touch Provider). A class-level guard would protect zero additional current callers.
3. **The endpoint already owns this exact refusal mapping.** `MapPost` catches `ModelMigrationInProgressException` → `409 Conflict` (SettingsEndpoint.cs:84-87). The DELETE handler mirrors it. `ModelMigrationInProgressException` is `AiRaccoon.Core.Memory` — already used by the endpoint and thrown by `EntryEmbedder` (EntryEmbedder.cs:81-82).
4. **Minimal surface (owner: "just block reset").** `SqliteSettingsStore` stays a plain key/value store; the migration invariant stays in the migration domain.

Shape: in `MapDelete`, when `key == EmbeddingSettingsKeys.Provider && await migrations.HasOpenModelMigrationAsync(ctx)`, throw `ModelMigrationInProgressException(<frozen message>)`; catch it and return `Results.Conflict(ex.Message)` — byte-for-byte the `MapPost` pattern. **Atomic by ordering**: the guard fires on Provider, the first key in the foreach, so no partial reset state is possible on refusal.

Residual risk, accepted and documented in the WP: check-then-delete is not transactional — two *concurrent* control-plane calls (reset in flight while `model embedding set` commits an outbox row in the same instant) could still strand a row. The sequential defect (the actual issue #592 scenario, minutes-to-hours of open row) is closed; the concurrent case requires an operator racing two verbs against each other. The atomic alternative (a `DELETE … WHERE NOT EXISTS (SELECT 1 FROM model_migration …)` in `SqliteSettingsStore`) was considered and rejected: it hard-codes the migration predicate into the KV store for a theoretical race (`ask-if-simpler`; R1 S4).

**A2. What the CLI prints.** Freeze this literal — it is thrown in the endpoint and travels verbatim through the 409 body to stderr:

```
ai-raccoon: model reset refused: a model migration is in progress — every MCP tool call is refused until it finishes; nothing was deleted
```

Mechanism (each hop mirrors an existing pattern): endpoint throw → `Results.Conflict(ex.Message)` (mirrors SettingsEndpoint.cs:84-87) → `ServerSettingsStore.DeleteSettingAsync` maps `409` → `ModelMigrationInProgressException(body)` (mirrors `StartModelMigrationAsync` at ServerSettingsStore.cs:96-99; today a 409 from DELETE falls through `Ensure` → `EnsureSuccessStatusCode` → raw `HttpRequestException` → generic catch → exit 15 with a useless message) → `ModelResetAsync` catches it **inside the method** (wrapping the foreach) and writes `ex.Message` to stderr **unreformatted** (the message already carries the `ai-raccoon: ` prefix — the same convention as the three `SettingsServer*` catches at ConfigCommands.cs:144-164; no double prefix, no `CliFailureFormatting`). The catch is deliberately inside `ModelResetAsync`, not in the `ConfigCommands` dispatcher: a dispatcher-level catch would also change `model embedding set` while-open from today's generic exit 15 to the new code — an unpinned behavior change to a verb outside this task's scope (verified: no test pins the embedding-set-while-open path).

**A3. Which exit code: a new `ExitCode.ModelResetRefused = 25` — not 23, not 24.**

- **23 reused: rejected.** Its doc comment is an explicit 5xx contract — "reached a server that answered but failed processing the request (5xx)… a server-side fault" (ExitCode.cs:68-71). A 409 domain refusal is not a server fault; reaching 23 would require either returning 500 for an expected refusal or lying in the exception message.
- **24 reused: rejected.** `ModelMigrationOpen`'s doc comment scopes it to `doctor` and to a composite verdict — "the schema shape is healthy but a model_migration outbox row is open… Reachable only from the Healthy arm" (ExitCode.cs:73-77). The reset refusal is a settings-verb refusal that neither knows nor cares about schema-shape health. Reusing 24 would force rewording doctor's contract and would entangle the follow-up-3 derive gate (which pins the doctor-reachable set) with a settings-command code.
- **25: adopted.** Verified free — 24 is the highest const (ExitCode.cs:78); next free is 25. The codebase's established pattern is exactly this: a named const with a doc comment citing the issue and the rationale (precedents 17/18/23 at :47-71, 24 at :73-77). Semantics stay distinct: a script can tell "reset refused" (25) from "doctor reports migration open" (24), both meaning "a migration is open" but on different verbs and surfaces.

Proposed const (doc comment follows the ExitCode.cs house style, cites #592):

```csharp
/// <summary>`settings model reset` / `settings model embedding reset` (#592): refused by the
/// settings server because a model_migration outbox row is open (ADR-0076) — deleting
/// embedding.provider would strand the outbox and ToolGate would refuse every tool forever.
/// Same species as <see cref="ModelMigrationOpen" /> (24), but a settings-verb refusal.</summary>
public const int ModelResetRefused = 25;
```

No event-id changes (research record: prefer none — the CLI error surface carries the message). `CliCommandsDoNotOpenTheBankTests` is untouched: the CLI still never opens the bank; the check is server-side.

### Ruling B — follow-up 2: checklist completion, and the blank-provider row (reword, don't drop)

The 7-row draft (`docs/work/checklist/2026-08-26-1.36.0-doctor-memory-engine.json`) is missing exactly the four R3 block-3 rows: `doctor-memory-rows-pending-is-alive`, `doctor-unreadable-settings-says-unreadable-not-a-false-remedy`, `doctor-unreadable-migration-falls-back-to-exit-0`, `blank-provider-migration-warns-once-and-stops-the-throw-loop` (verified against the draft's seven `item` ids: fresh-install, healthy-11-line, stuck-bank-24, settled-0, shape-mismatch-19, drain-logs, registry/drift-gates).

**Blank-provider row: reword, do not drop.** The state (open `model_migration` row, `embedding.provider` absent) becomes **unreachable through any supported surface** once the guard lands: a migration is born only via `StartModelMigrationAsync`, whose outbox transaction always writes the provider and whose guard refuses a blank provider (`ArgumentException.ThrowIfNullOrWhiteSpace(provider)`, SqliteMemoryStore.ModelMigration.cs:28); the only deleter of Provider — `ModelResetAsync` — is now refused while the row is open. But the row's purpose is not "reach the state": it is the manual verification of the 1012 once-per-process warning that **shipped in #591 and has never been run against the published tool** — the automated coverage (`EntryEmbedderMigrationDrainReportingTests`, Integration) exercises the component, not the installed binary, and R3 §7's division of labor reserves the published-tool leg for the checklist. Dropping the row deletes the only manual leg for a shipped observability behavior; rewording preserves it and records the unreachability in the artefact. Reword: (a) state the unreachability and its cause (reset guard + provider-required birth), (b) keep the raw-SQL seed fixture (the same route the automated test uses) and the 1012/no-526/`finished_at NULL` assertions, (c) fold the guard's live check into the command: `settings model reset` on the seeded bank must exit 25 — this doubles as the fix PR's manual gate on the blank-provider fixture.

Plus **one new row** (recommended; satisfies the fix PR's mandatory MANUAL live test gate from the research record's constraints): `model-reset-refused-while-migration-open` — realistic fixture (provider present + open row with an unexpired lease, raw-SQL seeded so the relay cannot drain it mid-check), both reset verbs exit 25 with the frozen message and delete nothing, then the lease expired/row closed → reset succeeds (exit 0, six keys gone). Total: 12 rows (7 + 4 + 1).

### Ruling C — follow-up 3: the exit-table derive gate

Precedents verified: `CorpusEngineLinesTests.HowToHealthyBankSample_MatchesTheReportContract` (regex-extracts a fenced block from the how-to, asserts line-by-line; CorpusEngineLinesTests.cs:149-179) and `ToolInventoryTests.PackagedReadme_ToolsHeading_MatchesActualToolCount` (regex-extracts a doc table, compares to a derived set; ToolInventoryTests.cs:167-176) are the in-repo templates; `DefaultCodeModelCommandTests` pins const↔surface agreement. The table (configure-ai-raccoon-server.md:370-378) has rows 0/1/2/19/20/22/24 with a Meaning column — exactly the doctor-reachable set {0 Success, 1 FailedToResolveEncryptionKey, 2 FailedToOpenEncryptedBank, 19 SchemaVerificationFailed, 20 SchemaNewerThanBinary, 22 NoBank, 24 ModelMigrationOpen} (ExitCode.cs). New Fast test class `HowToExitTableTests` (Unit/Fast, `Unit/Setup/Diagnostics/`, beside the sibling drift gate): (a) parse the table's backticked code cells, assert the code set equals a list built from the `ExitCode` members (compile-time rename protection, renumber → set inequality → RED); (b) per code, assert the Meaning cell contains a key phrase that is asserted against the const's doc comment in `ExitCode.cs` source (read via `TestData.RepoFile`, the CorpusEngineLinesTests pattern) — meaning drift or doc-comment drift goes RED, not just a missing row. Exception: `Success = 0` has no doc comment (ExitCode.cs:80) — its phrase ("HEALTHY") pins the table's own claim, noted in the test. RED witnesses: delete the 24 row; renumber 24→25 in the doc; reword a Meaning; rename the const.

## 2. Work packages

### WP1 — the reset guard (TDD, RED first)

**RED witnesses (both witnessed before any production change):**
- Integration: `ModelReset_WithAnOpenMigration_IsRefused_Exit25_DeletesNothing` — today reset **succeeds** (exit 0, Provider gone, row open): the defect.
- Fast: `ModelReset_StoreThrowsModelMigrationInProgress_Exits25_WithTheUnprefixedMessage` — today the generic catch exits 15 with a doubled prefix.

**Files touched:** `src/AiRaccoon/ExitCode.cs` (const 25), `src/AiRaccoon/Settings/SettingsEndpoint.cs` (MapDelete guard + catch), `src/AiRaccoon/Settings/ServerSettingsStore.cs` (DeleteSettingAsync 409 mapping), `src/AiRaccoon/Setup/Cli/Commands/SettingsCommands.cs` (catch inside ModelResetAsync). New tests: `tests/AiRaccoon.Tests/Unit/Setup/ModelResetGuardTests.cs` (Fast — dispatcher + `FakeMemoryStore` throwing `ModelMigrationInProgressException` on `DeleteSettingAsync`), `tests/AiRaccoon.Tests/Integration/Setup/ModelResetGuardEndpointTests.cs` (Slow, `[RetryFact]`, harness copied from `SettingsEndpointTests` — real `WebApplication` via `McpServerSetup.CreateWebHost`, real data root, real `ServerSettingsStore` over the app's `HttpClient`, driven through `TestData.CreateConfigCommands` + `CliRun` so the dispatcher→HTTP→endpoint→`SqliteSettingsStore`→bank seam is real; the CLI's `IMemoryStore` shell is a tiny adapter forwarding `DeleteSettingAsync` to the server store — this is the real CLI→server path in-process; no existing Unit/Integration harness does this today, the E2E process harness is Nightly-only).

**Fixture determinism (binding detail):** seed `embedding.provider`/`.model` rows and the open `model_migration` row by raw SQL (precedent: `NonDefaultDimensionMigrationTests.cs:124-130`) with `lease_owner='test-holder'` and `lease_expires_at = now + 1h` — the relay's `AcquireModelMigrationLease` WHERE `(lease_owner IS NULL OR lease_expires_at < @now)` (MemorySql.cs:495-497) then refuses acquisition, so the real 15s maintenance loop cannot drain/close the row mid-test. Seed inside the test method (`DELETE` + `INSERT`), since Integration is the retry surface and xRetry state persists across attempts (R2 J8).

**Tests (4):** `ModelReset_WithAnOpenMigration_IsRefused_Exit25_DeletesNothing` · `ModelReset_NoOpenMigration_StillResets_Exit0_ClearsAllSixKeys` · `Delete_NonProviderKey_WhileMigrationOpen_StillSucceeds` (direct endpoint DELETE of `embedding.model` → 204; pins that SyncCommands/`model code reset` etc. are unaffected) · Fast `ModelReset_StoreThrowsModelMigrationInProgress_Exits25_WithTheUnprefixedMessage`.

**Acceptance criteria:** (a) both reset verbs exit 25 with the frozen message and delete nothing while a migration is open; (b) both verbs still exit 0 and clear the six keys when no migration is open; (c) non-Provider deletes are unaffected; (d) both RED runs witnessed (exit 0-today, exit 15-today); (e) `CliCommandsDoNotOpenTheBankTests` stays green; (f) no event-id changes.

**Gate commands:**
```bash
TESTINGPLATFORM_TELEMETRY_OPTOUT=1 dotnet test --project tests/AiRaccoon.Tests --filter "FullyQualifiedName~ModelResetGuardTests|FullyQualifiedName~ModelResetGuardEndpointTests" -v m --minimum-expected-tests 4
TESTINGPLATFORM_TELEMETRY_OPTOUT=1 dotnet test --project tests/AiRaccoon.Tests --filter "Speed=Fast&Performance!=Benchmark" -v m
```

### WP2 — complete the checklist JSON

**Files touched:** `docs/work/checklist/2026-08-26-1.36.0-doctor-memory-engine.json` only.

**Rows added (12 total):** the four R3 rows (ids as in Ruling B; each carrying the repo's 7 fields `item`/`expected-result`/`anchor`/`command`/`evidence`/`observed-result`/`status`), the blank-provider row **reworded** per Ruling B (unreachability statement + raw-SQL seed + 1012/no-526/`finished_at NULL` + guard check `settings model reset` → EXIT=25 in the command), and the new `model-reset-refused-while-migration-open` row.

**Depends-on:** WP1 (the rows quote the frozen message, exit 25, and the unreachability rationale).

**Acceptance criterion:** the file parses as JSON, has exactly the 12 expected `item` ids, and every row has all 7 fields; the blank-provider row contains the unreachability statement.

**Gate command:**
```bash
python3 -c "import json;d=json.load(open('docs/work/checklist/2026-08-26-1.36.0-doctor-memory-engine.json'));ids=[i['item'] for i in d['items']];assert len(ids)==12,ids;print('\n'.join(ids))"
```

### WP3 — the exit-table derive gate

**Files touched:** new `tests/AiRaccoon.Tests/Unit/Setup/Diagnostics/HowToExitTableTests.cs` (`[Trait(Unit)] [Trait(Fast)]`). No production or doc edits — the table already matches post-#591.

**Tests (2):** `HowToExitTable_ListsExactlyTheDoctorReachableExitCodes` (code-set equality, built from `ExitCode` members) · `HowToExitTable_EachMeaning_MatchesTheConstsDocComment` (per-code phrase asserted in both the table row and the `ExitCode.cs` source).

**Depends-on:** none. **Acceptance criterion:** both facts green; RED witnessed by deleting the `24` row from the doc, then restored.

**Gate command:**
```bash
TESTINGPLATFORM_TELEMETRY_OPTOUT=1 dotnet test --project tests/AiRaccoon.Tests --filter "FullyQualifiedName~HowToExitTableTests" -v m --minimum-expected-tests 2
```
(Docs-only edits to `docs/how-to/**` still run the fast lane — `build.yml:71` CODE_REGEX includes it — so the gate fires on a docs-only PR.)

### WP4 — release mechanics (patch 1.36.1)

**Files touched:** `VERSION` (1.36.0 → 1.36.1 via `python3 scripts/version-bump.py patch` — verified script supports `patch`), `README.md` (one-line What's new entry, defect-fix voice per R3 §3.1 precedent: "`settings model reset` no longer strands an open model migration — refused with exit 25 while the ADR-0076 outbox is open"), PR title (it *is* the release note — `release.yml` generates notes from merged PR titles): `fix(settings): refuse model reset while a model migration is open (exit 25); complete the 1.36.0 checklist; derive the doctor exit table (#592)`.

**Depends-on:** WP1-WP3. **Acceptance criterion:** `VERSION` = `1.36.1`; `VersionContractTests` green; README entry present; PR title names the guard and exit 25.

**Gate commands:**
```bash
python3 scripts/version-bump.py patch
TESTINGPLATFORM_TELEMETRY_OPTOUT=1 dotnet test --project tests/AiRaccoon.Tests --filter "FullyQualifiedName~VersionContractTests" -v m
```

## 3. Risks

| Risk | Mechanism | Mitigation |
|---|---|---|
| Relay closes the seeded open row mid-test | real maintenance loop polls every 15s; an empty bank drains instantly | seed `lease_expires_at` in the future → `AcquireModelMigrationLease` refuses (MemorySql.cs:495-497) |
| Retry-surface state bleed | Integration/ is the retry surface; xRetry state persists across attempts (R2 J8) | `[RetryFact]`; seed idempotently inside the method (`DELETE` + `INSERT`) |
| Check-then-delete race | concurrent reset + `model embedding set` within one round trip could still strand a row | accepted — sequential defect (the issue) closed; atomic `NOT EXISTS` variant rejected (fourth predicate spelling in a KV store); noted in the guard's doc comment |
| `model embedding set` while open keeps exit 15 | unchanged behavior (generic catch) — deliberate scope boundary | verified nothing pins the current 15; documented as an out-of-scope NICE follow-up |
| Double-prefixed refusal message | `CliFailureFormatting.Format` prepends `ai-raccoon: ` | catch prints `ex.Message` verbatim (message already carries the prefix); Fast test pins the exact string |
| Derive-gate regex couples to table format | a table reformat (extra column, non-backticked codes) goes RED | deliberate — a format change must be a conscious edit, same as the sibling sample gate |
| Filter narrowing goes silent | `--filter` typos can run zero tests (R2 E4) | `--minimum-expected-tests` on every gate; never `--nologo` (MTP) |

## 4. Schema-last

### Table 1 — verified source facts the rulings rest on

| fact | source |
|---|---|
| Both reset verbs share one handler (`settings model reset` :62, `settings model embedding reset` :58 → `ModelResetAsync`) | ConfigCommands.cs:58,62; SettingsCommands.cs:260-275 |
| Reset deletes Provider first; one refusal aborts before any key is gone | SettingsCommands.cs:263-268 |
| Only deleter of `embedding.provider` in `src/` is ModelResetAsync | grep of all `DeleteSettingAsync` call sites |
| CLI's `HasOpenModelMigrationAsync` throws by design | ServerSettingsStore.cs:106-109 |
| Server graph: ISettingsStore→SqliteSettingsStore; IModelMigrationStore→SqliteMemoryStore (same instance as ToolGate's) | AppRegistrations.cs:327,335 |
| Endpoint already maps migration-open refusals to 409 | SettingsEndpoint.cs:84-87 |
| Client already maps 409 → ModelMigrationInProgressException (POST) | ServerSettingsStore.cs:96-99 |
| CLI exception→exit mapping: 401→17, 5xx→23, transport→18, other→15 | ConfigCommands.cs:144-169 |
| 24 = ModelMigrationOpen, highest const; 25 free; 23 documented as 5xx-only; 24 documented as doctor-only | ExitCode.cs:47-78 |
| Migration cannot be born with a blank provider | SqliteMemoryStore.ModelMigration.cs:28 |
| Relay lease acquisition refuses a future `lease_expires_at` | MemorySql.cs:495-497 (R3 §6.1 quote) |
| How-to exit table rows: 0/1/2/19/20/22/24 | configure-ai-raccoon-server.md:370-378 |
| Draft checklist has 7 rows; missing = the 4 R3 rows | 2026-08-26-1.36.0-doctor-memory-engine.json; R3 block 3 |
| 1012 blank-provider behavior has automated coverage; manual leg does not exist yet | EntryEmbedderMigrationDrainReportingTests (Integration) |
| `model embedding set` while open exits 15 today; nothing pins it | grep of tests for `already in progress`/`ModelMigrationInProgress` (no CLI-path pin) |

### Table 2 — work-package sequence

| WP | what | depends-on | acceptance criterion | gate command |
|---|---|---|---|---|
| 1 | reset guard: ExitCode.cs, SettingsEndpoint.cs, ServerSettingsStore.cs, SettingsCommands.cs, 2 new test files | — | (a)-(f) in §2 WP1 | `--filter "FullyQualifiedName~ModelResetGuardTests\|FullyQualifiedName~ModelResetGuardEndpointTests" -v m --minimum-expected-tests 4`; then `--filter "Speed=Fast&Performance!=Benchmark" -v m` |
| 2 | checklist JSON: 4 R3 rows (blank-provider reworded) + 1 guard row | WP1 rulings | 12 rows, 7 fields each, JSON parses | `python3 -c "…assert len(d['items'])==12…"` |
| 3 | derive gate: new `HowToExitTableTests` | — | 2 facts green; RED witnessed on row delete | `--filter "FullyQualifiedName~HowToExitTableTests" -v m --minimum-expected-tests 2` |
| 4 | release: VERSION 1.36.1, README What's new, PR title | WP1-3 | `VersionContractTests` green; title names exit 25 | `python3 scripts/version-bump.py patch`; `--filter "FullyQualifiedName~VersionContractTests" -v m` |

All gates use `TESTINGPLATFORM_TELEMETRY_OPTOUT=1 dotnet test --project tests/AiRaccoon.Tests`; **never `--nologo`** (MTP: unmatched token → `Zero tests ran` with success-shaped exit).

---

## Summary

- **Deliverable:** this markdown plan only — no files edited.
- **Follow-up 1 rulings (source-justified):** guard in **`SettingsEndpoint.MapDelete`** (composes ToolGate's own `IModelMigrationStore` check; endpoint is the ADR-0075 write choke point; only `ModelResetAsync` deletes Provider; a store-level guard would add a fourth predicate spelling); CLI prints the frozen `ai-raccoon: model reset refused: …` message verbatim (409 body → `ModelMigrationInProgressException` → catch inside `ModelResetAsync`, no double prefix); exit code **new `ModelResetRefused = 25`** (23 is documented 5xx-only, 24 is documented doctor-only, 25 verified free). Both reset verbs covered by the one handler; atomic-by-ordering (Provider deleted first); no event ids; `CliCommandsDoNotOpenTheBankTests` untouched.
- **Follow-up 2:** the four missing R3 rows are exactly the 4-row gap vs the 7-row draft; blank-provider row **reworded, not dropped** (state becomes CLI-unreachable by design, but the row is the 1012 manual leg for the published tool, seedable by raw SQL — the same route its automated twin uses); plus one recommended new row for the guard's own manual gate (12 rows total).
- **Follow-up 3:** new Fast `HowToExitTableTests` pinning the how-to table's code set to the doctor-reachable `ExitCode` members and each Meaning to the consts' doc-comment phrases, following the `CorpusEngineLinesTests`/`ToolInventoryTests` regex-derive pattern.
- **Key findings:** no existing Unit/Integration harness drives the real CLI→server settings path (the E2E harness is Nightly/process-only) — the WP1 Integration tests build one in-process from the `SettingsEndpointTests` harness; the relay-interference hazard is solved by seeding an unexpired lease (future `lease_expires_at` blocks acquisition); `model embedding set`-while-open's exit-15 path is unpinned and deliberately left untouched.
- **Issues:** ai-raccoon memory server unreachable (same as R3 noted) — all claims verified by direct source reads, which is the stronger evidence per the repo's own invariant.