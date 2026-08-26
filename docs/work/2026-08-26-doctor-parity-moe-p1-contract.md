# LANE P1 — Architect / output contract

MoE planning lane P1 of task `doctor-feature-match`, 2026-08-26. Written by a planning subagent
against the task worktree; reviewed in the same task's review round. Sibling lanes: P2 (implementation
shape), P3 (test design), P4 (runtime observability parity). Companion research record:
`2026-08-26-doctor-memory-embedding-research.md` — whose §3.3 this lane corrects.

---

`ai-raccoon doctor` reports the memory-embedding engine

Worktree read: `/Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/doctor-feature-match`. Every factual claim below carries a `file:line`. Two claims in the research record are **wrong** and both change the design — §0 first.

---

## 0. Corrections to the research record (load-bearing — the other two lanes need these)

### 0.1 The memory engine DOES have a "not configured" state. The record's §3.3 is wrong.

The record asserts (`docs/work/2026-08-26-doctor-memory-embedding-research.md:83-99`) that "there is no 'not configured' state for the memory engine … the memory engine is always resolved", citing `EntryEmbedder.StartMigrationAsync`'s `model ?? BundledModel` (`src/AiRaccoon.Infrastructure/Embedding/EntryEmbedder.cs:26,47,90`).

That cite proves something narrower than the claim. `model ?? "bundled"` is a **model** fallback *inside* an already-configured provider. The corpus's configured-ness is `embedding.provider`, and:

- Nothing seeds `embedding.provider`. The only writers are `EntryEmbedder.StartMigrationAsync` (`EntryEmbedder.cs:38,54`), reached only from `ai-raccoon model embedding set local` (`src/AiRaccoon/Setup/Cli/Commands/SettingsCommands.cs:119`) and `… set openai` (`SettingsCommands.cs:158`). `MemorySchema` seeds no settings row; the only INSERT into `settings` in the tree is the generic upsert at `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs:605`.
- With the row absent, memory embedding is **skipped**: `EntryEmbedder.EmbedIfConfiguredAsync` returns early (`EntryEmbedder.cs:159-164`), `EmbedQueryAsync` returns `QueryVector.Empty` (`:238-241`), and `FileIngestor` reads the same key to decide (`src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs:339`).
- The CLI already has a name for this state: `model embedding show` prints `provider: (none — FTS5-only search)` (`SettingsCommands.cs:314-317`, and again in `ModelShowAsync` `:360-363`), and `model reset` prints `embedding engine reset to default: no engine (FTS5-only search)` (`SettingsCommands.cs:273`).

**Consequence for this lane:** the memory corpus's degraded state is *structurally identical* to the code corpus's (`embedding.codeModel` absent → `code engine: not configured` at `src/AiRaccoon/Setup/Cli/Commands/DoctorCommands.cs:67-69`). Same predicate, different key. It is therefore a **parameter of the shared component**, not a divergence — which strengthens the owner's extraction ruling rather than complicating it. The record's §5 open question 4 ("symmetry vs honesty") largely dissolves; what remains of it is ruled in §5 below.

### 0.2 Event id 1002 is taken. The record's §4 is stale.

The record says "a new failure log would take 1002" (`research record:135`). `docs/reference/logging-event-ids.md:84` assigns **1002-1007 to `EmbedDrainService`**. `DoctorCommands` still owns only 1000-1001 (`logging-event-ids.md:83`, matching `DoctorCommands.cs:215,218`). This lane rules **no new log line** (§4), so the id question is moot — but the implementation lane must not reserve 1002 on the record's word.

### 0.3 The live bank is the specification

Owner-supplied read-only probe of `~/.ai-raccoon/memory.db`, 2026-08-26, taken as fact:

| Fact | Value |
|---|---|
| `model_migration` id=1 | provider `local`, engine `local:/…/Salesforce__SFR-Embedding-Code-400M_R#a3df8a4c…`, `started_at` 1787739481 → **2026-08-26T10:18:01Z**, `finished_at` **NULL (open)**, live lease |
| `entries` | 51,947 rows, **47,723 pending (91.9 %)** |
| settings | `embedding.provider=local`, `embedding.model` = the **code** model dir, `embedding.codeModel` = the same dir, `embedding.codeDimensions=1024`, **no `embedding.dimensions` row** |
| `ai-raccoon doctor` | printed the 8 lines in the brief, `status: HEALTHY`, **exit 0** — while `memory_search` was refused with `model-migration-in-progress` |

Two things fall straight out of this and drive §2/§3:

1. **`status: HEALTHY` / exit 0 on a bank whose memory search is dead is the defect.** Silence is only half the defect; the other half is the verdict.
2. **The operator pointed the memory engine at the code model.** `embedding.model` == `embedding.codeModel`. That is a config accident (a 1024-dim code model embedding prose) which no surface reports, and which the minimum two-line change *does* make visible — the memory and code engine lines print the same directory, side by side. This is the single strongest argument for keeping the engine lines **adjacent**.

---

## 1. THE SHARED COMPONENT (owner override — supersedes "mirror the code line")

### 1.1 What it is, what it's called, where it lives

**Ruling: `EngineDoctor`, an `internal static class` in `src/AiRaccoon.Infrastructure/Sqlite/EngineDoctor.cs`, beside `SchemaDoctor` — a type of its own, not a `DoctorCommands` private.**

Justification, all cited:

- **Precedent for the home.** Read-only diagnosis over an already-open `SqliteConnection`, returning a record for a caller to format, is exactly `SchemaDoctor`: `internal static class SchemaDoctor` … `DiagnoseAsync(SqliteConnection bank, …)` → `SchemaDoctorReport` (`src/AiRaccoon.Infrastructure/Sqlite/SchemaDoctor.cs:13-34`). `EngineDoctor` is the same shape for a different question. Screaming-architecture: it is a doctor for engines, filed next to the doctor for schemas.
- **Visibility is free.** `AiRaccoon.Infrastructure.csproj:35,37` grants `InternalsVisibleTo` to **both** `AiRaccoon.Tests` and `AiRaccoon`. So `internal` is directly callable from `DoctorCommands` (which already does exactly this with `SchemaDoctor` at `DoctorCommands.cs:5,54`) **and** directly unit-testable — no public API added.
- **Why not a `DoctorCommands` private.** Today's code-engine read is `private static` (`DoctorCommands.cs:108`), so its only test route is argv-driven end-to-end: every existing assertion is a substring match on captured stdout (`tests/AiRaccoon.Tests/Unit/Setup/DoctorCommandsTests.cs:86,104,129,148,162,178`). A component two corpora depend on should be provable against an in-memory bank the way `SchemaDoctor` is, not only through the CLI.
- **Layering.** It needs `MemorySql` and `EmbeddingSettingsKeys`, both Infrastructure. Core cannot host it (it would need `SqliteConnection` — violates clean layering). Infrastructure is the only correct home.

### 1.2 The parameterisation — and why it satisfies `derive-or-delete-the-list`

The descriptor is the single place per-corpus facts live, and **`ReportAsync` iterates it** rather than hand-writing a line per corpus:

```
internal sealed record CorpusEngine(
    string  Label,               // "memory" | "code"          → the line's subject
    string? ProviderKey,         // .Provider   | null           → configured-ness for memory
    string  ModelKey,            // .Model      | .CodeModel     → configured-ness for code; the value either way
    string? BaseUrlKey,          // .BaseUrl    | null
    string? ApiKeyKey,           // .ApiKey     | null
    string  PendingTable,        // "entries"   | "code_entries" → the TableExistsAsync guard
    string  PendingSql,          // MemorySql.CountPendingEmbed | .CountPendingCodeEmbed
    string  NotConfiguredRemedy) // the em-dash clause, verbatim from the owning surface
{
    public static readonly CorpusEngine Memory = …;
    public static readonly CorpusEngine Code   = …;
    public static readonly IReadOnlyList<CorpusEngine> All = [Memory, Code];   // ← the derived list
}
```

`.ai-badger/invariants/derive-or-delete-the-list.md:3` — "Compute the list from the thing it describes so the two cannot disagree." A hand-written memory twin would produce **four** copies of drifting text: two `"<x> engine: …"` format strings, two `"<x> rows pending: …"` format strings, plus two copies of the `TableExistsAsync` + `catch (SqliteException)` guard (`DoctorCommands.cs:114-127` and `:149-153`). Under `CorpusEngine.All`, there is one copy of each grammar and one copy of the guard; a third corpus is one descriptor, not four format strings.

Shared inside `EngineDoctor`: the guard pattern, the configured/not-configured/unreadable branch, the pending count, and both line grammars. Per-corpus: only the descriptor's field values.

### 1.3 The extraction also removes a latent misreport

Today, an unreadable or absent `settings` table makes `ReadCodeEngineStateAsync` return `CodeEngineState(null, null, …)` — via the `TableExistsAsync` false path (`DoctorCommands.cs:116-118`) or the `catch` (`:124-127`) — which `ReportAsync` renders as `code engine: not configured — run 'ai-raccoon model code set default'` (`:67-69`). That is a **false remedy**: the engine may be perfectly configured; doctor just could not read the row, on precisely the broken bank doctor exists for. The shared component must distinguish `not configured` (key positively absent, `settings` readable) from `unreadable` (guard tripped). This is the one intentional change to **existing** output; it is a correctness fix, not scope creep, and it is why the SCHEMA-LAST table has a distinct "state when unavailable" for the engine lines.

Second freebie: the local arm must split on `Directory.Exists(model)` before attempting a manifest load, because `embedding.model` may be an **onnx file path**, not a directory (`src/AiRaccoon.Infrastructure/Embedding/EmbeddingSettingsKeys.cs:10`; `BundledModel.cs:66-70`). `ModelNameFor` (`DoctorCommands.cs:157-168`) would render such a path as `model_qint8_arm64.onnx (manifest unreadable)` — technically true, diagnostically misleading. `EmbeddingService` already uses exactly this discriminator (`src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs:277`, and `:238`), so the shared component derives it rather than inventing it. Both corpora get the guard; whether the code path can reach it today is irrelevant to it being correct.

### 1.4 Where the migration outbox goes — OUTSIDE the component

**Ruling: the open-migration state is its own bank-wide line, produced outside `EngineDoctor`, not a per-corpus option of it.**

Citing `docs/adr/0087-code-drain-is-configure-transaction-invalidation-not-the-model-migration-outbox.md` (Accepted, 2026-08-21), whose Context rejects a shared outbox for three reasons: the outbox is **single-row** (`MemorySchema.cs:372-382`, one open migration at a time); `ModelMigrationJob`'s relay **hard-codes `entries`**; and `ToolGate` closes **ALL** tools while the row is open. Confirmed in schema: `MemorySql.SelectModelMigration` / `HasOpenModelMigration` read `model_migration WHERE id = 1` — no corpus column (`MemorySql.cs:470-475`).

So "is a migration open?" is not a per-corpus question; it is one bank-wide fact. Modelling it as a `CorpusEngine` field would encode "every corpus may have an outbox" — the exact shape ADR-0087 rejected — and would force the code descriptor to carry a permanently-null field that a future reader would try to fill in. `derive-or-delete-the-list` is satisfied *better* by keeping it out: there is no second copy, because the code corpus never asks.

The remote arm (`BaseUrlKey`, `ApiKeyKey`) **does** go inside, as nullable parameters that `Code` passes null for. Cost: one branch unreachable for one corpus. Benefit: one copy of the grammar. Accepted — the code engine is always `local` by construction (`src/AiRaccoon.Infrastructure/Sqlite/Code/SqliteCodeEngineStore.cs:51` fingerprints `"local"` unconditionally), so the branch is dead by decision, not by accident.

---

## 2. THE LINE SET AND WORDING

### 2.1 The grammar the existing lines actually use

Read from `DoctorCommands.cs:64-75`:

- `<subject>: <value>` bare, when there is no "where" or "why" — `code rows pending: 0` (`:73-74`).
- `<subject>: <value> (<qualifier>)`, where the qualifier answers *where* (`code engine: <model> (<directory>)`, `:69`), *why this value* (`embedding threads: 5 (halved-core default)`, `:71-72`), or *compared to what* (`user_version: 11 (this binary: 11)`, `:65`).
- Degraded arm: `<subject>: not configured — run '<command>' to <benefit>` — em dash, single quotes round the command, **no parentheses** (`:68`).

Every new line below obeys one of those three forms. No fourth form is invented.

### 2.2 Candidate rulings

| Candidate | Ruling | Why — for an operator staring at "search feels wrong" |
|---|---|---|
| **memory engine provider** | **DROP as its own line; folded into the engine line's value** | `openai:<model>` already encodes the provider (the rendering `model embedding set openai` itself prints, `SettingsCommands.cs:159`); a path or `bundled` implies `local`. A separate `memory provider: local` line restates line 4 → `derive-or-delete-the-list`. The operator needs *which weights*, not the enum. |
| **model — local path vs remote name vs `bundled`** | **KEEP — it *is* the engine line's value** | This is the whole diagnosis. On the live bank it prints the code-model directory next to the code engine's identical directory, exposing the config accident in §0.3 with no extra line. |
| **engine fingerprint** | **DROP** | `EngineFingerprint` renders `local:/absolute/path#<64 hex>` (`EmbeddingService.cs:274-288`) — a 100+ char line. Its only use is comparing two banks, and it is derivable from the model + manifest, so printing it is a second copy of line 4. `model embedding show` already prints it (`SettingsCommands.cs:323`) for anyone who needs it. |
| **declared dimensions** | **DROP — strongest drop** | `embedding.dimensions` exists **only** for remote providers (`EmbeddingSettingsKeys.cs:19-21`), and `model embedding set local` explicitly **deletes** it (`SettingsCommands.cs:118`). The live bank is `local` and has no such row, while the real dimension is 1024 from the manifest via `EmbeddingService.ResolveDimensions` (`:218-227`) — unreachable from the connection. The line would print `(unset)` on exactly the bank where the dimension mattered. A line that reads "unset" when the answer is 1024 is worse than no line. |
| **memory rows pending** | **KEEP** | `47,723 of 51,947` is the number that makes the dead search legible. `MemorySql.CountPendingEmbed` (`MemorySql.cs:362-363`) is the exact structural twin of the query doctor already uses (`CountPendingCodeEmbed`, `:419-420`). Note: unlike the code count, it needs **no poison-row caveat** — `entries` has no `embed_attempts` column (`MemorySchema.cs` `entries` DDL: `embed_state, embedding, heading_path, structure_embedding, chunk_index, total_chunks, source_id` — no attempts column), so there is no quarantined subset to explain (contrast `MemorySql.cs:415-420`). |
| **open-migration state** | **KEEP — the highest-value line in the change** | It is *the* reason the live bank's search is refused (ADR-0087 Context 3: `ToolGate` closes all tools while the row is open; observed live as `model-migration-in-progress`). It is also the same event as the pending count: the outbox row and `MarkAllEmbeddedPending` commit in one transaction (`EntryEmbedder.cs:50-78`, `MemorySql.cs:398-399`). One indexed read (`MemorySql.HasOpenModelMigration`, `:474-475`; `IModelMigrationStore` calls it "cheap: one indexed read", `src/AiRaccoon.Core/Memory/IModelMigrationStore.cs:20-24`). |
| **bundled-asset presence** | **DROP** | Not readable from the connection at all — `BundledModel.ResolveBundled` walks the filesystem upward from `AppContext.BaseDirectory` (`BundledModel.cs:141-160`). Needs a fourth dep (§4). Already warned at startup with its remedy (`src/AiRaccoon/Setup/Models/EmbeddingAvailability.cs:20-29,34`, event 40) and again at use time (`BundledModel.cs:87-88`). And it is only relevant in the `bundled` arm — i.e. never on a bank whose operator configured a model, the live bank included. |
| **API key set for a remote provider** | **KEEP — folded into the engine line's remote arm, not a new line** | A remote engine with no key genuinely kills memory search, so doctor must say it — that is doctor's stated justification (`DoctorCommands.cs:101-107`: "doctor is where someone looks when search feels wrong"). But it is meaningless on a `local` bank, so it does not earn a line of its own; it appends the remedy sentence **quoted verbatim** from the surface that owns it (`SettingsCommands.cs:144`), inventing nothing. Precedent for folding a caveat into a value: `(manifest unreadable)` at `DoctorCommands.cs:166`. |

### 2.3 The exact literal text

**Line 4 — `memory engine:`** — four arms, discriminated only by settings rows readable from the open connection:

1. provider set, `embedding.model` is an existing directory:<br>`memory engine: Salesforce/SFR-Embedding-Code-400M_R (/Users/arasz/.ai-raccoon/models/Salesforce__SFR-Embedding-Code-400M_R)`<br>— name from the manifest via `ModelNameFor` (`DoctorCommands.cs:157-168`), moved into the shared component. **This is the live bank's line.**
2. provider set, `embedding.model` unset:<br>`memory engine: bundled`<br>— bare form, no parenthetical, because there is no "where" to name without touching the filesystem. The literal `bundled` is not invented: it is the value `EntryEmbedder` itself returns in `EmbeddingConfig.Model` (`EntryEmbedder.cs:26,47,90`) and the identity `EngineFingerprint` renders as `local:bundled` (`EmbeddingService.cs:288`).
3. provider `openai`:<br>`memory engine: openai:text-embedding-3-small (https://api.example.com/v1)` — parenthetical is the endpoint when `embedding.baseUrl` is set; **bare** `memory engine: openai:text-embedding-3-small` when it is not (same rule as arm 2: no invented placeholder for an absent "where"). `openai:<model>` quotes `SettingsCommands.cs:159`.<br>When `embedding.apiKey` is absent, append the em-dash clause verbatim from `SettingsCommands.cs:144`:<br>`memory engine: openai:text-embedding-3-small — no API key set; run 'ai-raccoon model embedding set openai <model> --api-key <key>' or embeddings will fail`
4. `embedding.provider` absent (§0.1):<br>`memory engine: not configured — run 'ai-raccoon model embedding set local' to enable semantic memory search`<br>— exact mirror of `DoctorCommands.cs:68`'s grammar. The command string is the one `EmbeddingAvailability`'s warning already names (`EmbeddingAvailability.cs:34`).

> **Constant-source obligation, flagged for the implementation lane.** The code side keeps its command in one constant that every surface quotes — `CodeEngineSetup.DefaultModelCommand` (`src/AiRaccoon.Core/Memory/Code/CodeEngineSetup.cs:15`), quoted by doctor (`DoctorCommands.cs:68`), the search warning (`CodeSearchWarnings.cs:16`), the MCP instructions (`src/AiRaccoon/Setup/McpServerInstructions.cs:20`) and the tool description (`src/AiRaccoon/Tools/MemoryTools.cs:105`). The memory side has **no such constant**: `'ai-raccoon model embedding set local'` is spelled by hand in at least four places (`EmbeddingAvailability.cs:34,37`; `BundledModel.cs:88,98`). Per `derive-or-delete-the-list`, doctor must **not become the fifth hand-spelling**. Introduce the memory twin of `CodeEngineSetup` — a `Core/Memory` constants class holding the memory-engine setup command and the no-API-key remedy — and have doctor quote it. Refactoring the four existing call sites onto it is optional; adding a fifth copy is not acceptable.

**Line 7 — `memory rows pending:`** — byte-for-byte twin of `DoctorCommands.cs:73-74`:<br>`memory rows pending: 47723`, `memory rows pending: unreadable` when the guard trips.

**Line 9 — `model migration:`** — subject is `model migration` (not `memory migration`): the outbox is bank-wide and single-row, and the table is literally `model_migration` (`MemorySql.cs:470-475`).

- open: `model migration: open since 2026-08-26T10:18:01Z (all MCP tool calls are refused until it finishes)`
- closed or no row: `model migration: none open`
- guard tripped: `model migration: unreadable`

The qualifier says **all MCP tool calls**, not "memory search" — that is what ADR-0087 Context 3 states the gate does, and understating it would mislead. Timestamp format is **derived, not invented**: `yyyy-MM-dd'T'HH:mm:ss'Z'`, `CultureInfo.InvariantCulture`, `UtcDateTime` — the existing CLI convention at `src/AiRaccoon/Setup/Cli/Commands/WatchCommands.cs:185`. `started_at` is unix seconds (`EntryEmbedder.cs:65`), read via `MemorySql.SelectModelMigration` (`MemorySql.cs:470-472`). The one copy of that formatter lives with the new component; whether `WatchCommands` is refactored onto it is out of scope, but a **third** rendering is not acceptable.

The migration line deliberately does **not** repeat the target engine, even though `SelectModelMigration` returns it. The outbox row's engine and the settings' engine commit in the same transaction (`EntryEmbedder.cs:54-67`), so it is the same value line 4 already prints — a second copy, forbidden by `derive-or-delete-the-list`.

### 2.4 Order — and what is deliberately not touched

| # | Line | Status |
|---|---|---|
| 1 | `ai-raccoon doctor: <path>` | unchanged |
| 2 | `user_version: …` | unchanged |
| 3 | `application_id: …` | unchanged |
| 4 | `memory engine: …` | **new** |
| 5 | `code engine: …` | unchanged text, may gain the `unreadable` arm (§1.3) |
| 6 | `embedding threads: …` | unchanged |
| 7 | `memory rows pending: …` | **new** |
| 8 | `code rows pending: …` | unchanged |
| 9 | `model migration: …` | **new** |
| 10 | `doctor verifies schema shape only; it never repairs a bank` | unchanged |
| 11 | `status: …` | one new arm (§3) |

Three properties this order buys, each a reason not to reshuffle:

- **Every existing line keeps its relative position** (2,3,5,6,8,10,11 in order). The sample block in `docs/how-to/configure-ai-raccoon-server.md:330-337` and the release-checklist items (research record `:138`) survive as *incomplete*, not as *wrong* — a strictly smaller doc-drift bill. `ask-if-simpler`: reordering buys zero diagnostic value.
- **Engine lines adjacent (4,5) and pending lines adjacent (7,8).** This is what surfaces §0.3's config accident: two adjacent lines printing the same directory. Memory first because memory is the primary corpus and the code corpus is an explicitly re-derivable cache (ADR-0085, cited in ADR-0087 Context 2).
- **The migration line sits immediately above the disclaimer and the verdict.** It is the line that contradicts `HEALTHY`, so it belongs where the reader's eye already is when they read the exit condition.

No test breaks on insertion: every existing assertion is `ShouldContain`, not a full-output equality (`DoctorCommandsTests.cs:86,104,129,148,162,178`).

---

## 3. THE EXIT-CODE RULING

### 3.1 What is true today

- Exit code is a **total function of `report.Status`**: `ReportAsync`'s switch maps `VersionAheadOfBinary → SchemaNewerThanBinary`, `Healthy → Success`, default `→ SchemaVerificationFailed` (`DoctorCommands.cs:77-98`).
- `report.Status` is derived **only** from schema shape plus version skew (`SchemaDoctor.DiagnoseAsync`, `SchemaDoctor.cs:15-34`; three-value enum at `:137-142`).
- The extra reads carry an explicit written invariant: *"this extra read must never be what decides the exit code"* (`DoctorCommands.cs:111-113`), whose stated reason is that every table they touch may be missing or misshaped on the very banks doctor exists for.
- `ExitCode` (`src/AiRaccoon/ExitCode.cs`) defines 0-23 (8 retired, `:17-19`); doctor uses `Success` (`:73`), `FailedToResolveEncryptionKey=1` (`:5`), `FailedToOpenEncryptedBank=2` (`:6`), `SchemaVerificationFailed=19` (`:53-54`), `SchemaNewerThanBinary=20` (`:56-57`), `NoBank=22` (`:64-66`). **24 is free.**
- The documented contract is schema-shaped: "Exit code is `0` when healthy and non-zero on a mismatch" plus a six-row table (`docs/how-to/configure-ai-raccoon-server.md:355-364`).

### 3.2 Decision

**DECISION A — an open model migration earns a new distinct non-zero code: `ExitCode.ModelMigrationOpen = 24`.**

**DECISION B — a missing local model asset stays report-only, and is not reported at all (§2.2).**

**DECISION C — the schema verdict outranks it.** `SchemaDoctorStatus` is not extended and the switch stays a total function of `report.Status`; 24 is reachable **only** from the `Healthy` arm. A shape-mismatched bank still exits 19; a version-ahead bank still exits 20.

**DECISION D — 24 is emitted only on a positively-read open row.** Guard trips (`model_migration` absent, `SqliteException`) → line 9 prints `unreadable` and the arm exits `Success`. This preserves the *rationale* of `DoctorCommands.cs:111-113` (a broken bank must never get a wrong exit code) while narrowly amending its *letter*, which the implementation lane must record in the doc comment so the amendment is visible rather than looking like an oversight.

**DECISION E — the `Healthy` arm's status word must not contradict its own exit code.** With an open migration:<br>`status: MIGRATION IN PROGRESS (schema shape is healthy; memory search is refused until the re-embed finishes)`<br>Bare `status: HEALTHY` is retained for the closed/absent/unreadable cases, so existing tests and the how-to sample stay valid (a fresh test bank has no `model_migration` row → `HasOpenModelMigration` = 0 → exit 0).

### 3.3 Why 24 and not report-only

The precedent is inside `ExitCode` already. `SchemaNewerThanBinary = 20` is **not** corruption — it is a transient, self-clearing, actionable state meaning "this tool's normal verdict does not apply; do something and re-run" (`ExitCode.cs:56-57`; the remedy `— update ai-raccoon` is in the line itself, `DoctorCommands.cs:81`). An open migration is the same species of fact: not corruption, actionable, self-clearing, and — decisively — the window in which **every** MCP tool call is refused (ADR-0087 Context 3; observed live). doctor already has a non-zero code for "legitimate state, but don't trust the bank yet". This is a second instance of that category, not a new category.

Report-only was considered and rejected because it closes only half the observed defect. On the live bank, lines 7 and 9 would have printed `memory rows pending: 47723` and `model migration: open since …` — and doctor would still have exited **0** under `status: HEALTHY`. A human reading the output is served; the script that wraps `doctor` before trusting the bank is not, and that script is the documented use case (`configure-ai-raccoon-server.md:355`: "so it composes into a script").

### 3.4 Consequence, stated plainly and accepted

A script that runs `doctor` immediately after `ai-raccoon model embedding set …` now gets **24** for the duration of the re-embed, where it previously got 0. That is the intended behaviour change, not collateral: during that window the bank's tools are refused, and a script that proceeds is a script that queries a bank whose search is dead. Callers wanting the pure schema verdict still have it — 19 and 20 outrank 24, and 24 is emitted only when the shape is clean, so `exit == 24` is *itself* a positive statement that the schema is healthy.

Second consequence: the exit-code table at `configure-ai-raccoon-server.md:357-364` gains a row. That table is a hand-maintained mirror of `ExitCode.cs` and therefore already a `derive-or-delete-the-list` liability; adding to it is a doc obligation for the implementation lane, not a design question for this one.

### 3.5 Attack surface (stated so reviewers hit it head-on)

The strongest counter is: *"an open migration is the documented happy path — `model embedding set` prints `re-embedding in the background` (`SettingsCommands.cs:124,159`) — so exiting non-zero for it trains operators to ignore doctor."* The rebuttal is that the same is true of 20 (update the binary and it clears), and that the happy path in question is one in which the product's primary interface returns errors. If the review overturns Decision A, **Decisions C, D and E fall with it and lines 4/7/9 stand unchanged** — the line set does not depend on the exit code.

---

## 4. DEPENDENCY BUDGET

**Ruling: no fourth dependency. All three new lines are readable from the already-open read-only `SqliteConnection`. Doctor's constructor stays at `ISqliteConnectionFactory`, `IEncryptionKeyResolver`, `ILogger<DoctorCommands>` (`DoctorCommands.cs:17`).**

Every kept value's source, all connection-local:

| Value | Source | Cost |
|---|---|---|
| provider / model / baseUrl / apiKey presence | `settings` rows, via the existing `ReadSettingAsync` (`DoctorCommands.cs:182-185`) behind `TableExistsAsync` (`:149-153`) | 4 scalar reads on an indexed key |
| manifest model name | `ModelNameFor` (`:157-168`) — a filesystem read *inside a `try/catch`* that already exists and already ships | none new |
| memory rows pending | `MemorySql.CountPendingEmbed` (`MemorySql.cs:362-363`) behind a `TableExistsAsync("entries")` guard | one `COUNT(*)`; already deemed affordable for its existing caller (`MemorySql.cs:361`) |
| open migration + started_at | `MemorySql.SelectModelMigration` (`:470-472`) or `HasOpenModelMigration` (`:474-475`) behind `TableExistsAsync("model_migration")` | "one indexed read" (`IModelMigrationStore.cs:20-24`) |

The only candidate that *would* have needed a fourth dep is bundled-asset presence: `IBundledModel`'s locator walks the filesystem from `AppContext.BaseDirectory` (`BundledModel.cs:141-160`), and its `EnsureAsync` **downloads over HTTP** on a miss (`BundledModel.cs:37-49,55-63`) — an unacceptable side effect for a read-only diagnostic verb. Injecting `IBundledModel` and calling only the static locator would add a dep to use none of its interface. Dropping the line (§2.2) removes the question.

**What the connection-only rule costs, stated honestly:**

1. **No true dimension for local engines.** The real value comes from the manifest via `EmbeddingService.ResolveDimensions` (`:218-227`), which needs `IEmbeddingService`. Cost: doctor cannot report a memory/`vec_entries` dimension mismatch. Bounded: `IVecDimensionReconciler` reconciles `vec_entries` at drain time as the migration's **first** phase (`EntryEmbedder.cs:116,144-153`; ADR-0093 generalised it for `vec_code`), so a mismatch is transient by construction — the reason the record's own §3.5 hypothesis is not worth buying. **The record's §3.5 stays an unverified hypothesis and this lane rules it out of scope**, rather than promising a read it never traced.
2. **No asset-presence check.** Covered at startup (`EmbeddingAvailability.cs:20-29`) and at use time with its own remedy (`BundledModel.cs:87-88`).
3. **No live engine health probe** (can the ONNX session actually load, can the remote endpoint be reached). Out of scope for a read-only bank verb, and a network call in `doctor` would be a new hazard, not a new line.

Corollary, per `high-performance-logging` and §0.2: **no new log line, no new EventId.** Every new value is either printed or rendered `unreadable`; nothing fails silently, so nothing needs logging. `DoctorCommands` keeps exactly 1000-1001 (`DoctorCommands.cs:215,218`; `logging-event-ids.md:83`), and 1002-1007 stay with `EmbedDrainService` (`logging-event-ids.md:84`).

---

## 5. SYMMETRY VS HONESTY

The premise of this question is **false as stated**, and correcting it is the ruling.

**The memory engine has a `not configured` state and it is a true analogue of the code engine's** (§0.1): `embedding.provider` absent, nothing seeds it, only `model embedding set …` writes it (`SettingsCommands.cs:119,158`), and the CLI already names the state `(none — FTS5-only search)` (`SettingsCommands.cs:314-317`). The `model ?? "bundled"` fallback (`EntryEmbedder.cs:47,90`) is a **model** fallback *inside* a configured provider — a different axis. So:

**Ruling 5a — the degraded arm lives INSIDE the shared component as a parameter** (`ProviderKey` vs `ModelKey` for configured-ness, plus `NotConfiguredRemedy`). No divergence to document, because none exists. Both lines read `<label> engine: not configured — run '<command>' to <benefit>`.

**Ruling 5b — the real divergences are exactly three, and each is handled structurally, not by comment:**

1. **The memory engine has a provider axis; the code engine does not** (always `local`, `SqliteCodeEngineStore.cs:51`). Handled by `ProviderKey`/`BaseUrlKey`/`ApiKeyKey` being **nullable** in the descriptor. A future reader who tries to "fix the asymmetry" by filling them in for `Code` has to answer why, and the descriptor's own 1-3 line doc comment (`minimal-comments`) says: *"Null keys mean the corpus has no such axis — the code engine is always local (SqliteCodeEngineStore.cs:51)."*
2. **The memory corpus has a migration outbox; the code corpus does not.** Handled by the migration line living **outside** the component (§1.4). The divergence is already documented, by decision, in `docs/adr/0087-…md` — an Accepted ADR whose Context enumerates three reasons and records two rejected designs. **Nothing new needs writing**: the doc comment on the migration read cites ADR-0087, and a future reader who wants to "share the outbox" reads the ADR that already refused it twice. This is the correct answer to "how does the divergence get documented" — it is documented in the layer where decisions live, not in a code comment that will drift.
3. **The memory pending count needs no poison-row caveat; the code one does.** `entries` has no `embed_attempts` column (`MemorySchema.cs` `entries` DDL); `code_entries` does, with a `MaxEmbedAttempts` quarantine and an explicit remark that doctor deliberately reports the literal count including poison rows (`MemorySql.cs:407-420,445-446`). Handled by *not* adding a memory-side caveat — the asymmetry is in the schema, and the `PendingSql` parameter carries it. `derive-or-delete-the-list`: the caveat lives on the SQL constant that needs it, once (`MemorySql.cs:415-420`), not restated in doctor.

**Ruling 5c — the `unreadable` arm is shared and must exist for both** (§1.3), because otherwise the extraction would propagate the code line's current false-remedy bug (`DoctorCommands.cs:116-118,124-127`) to a second corpus. That would be symmetry bought with honesty — the exact trade this section exists to refuse.

---

## 6. SIMPLER SHAPE (`ask-if-simpler`)

### 6.1 The minimum viable version — named explicitly

**Two lines, no exit code, no shared component:** a second `private static Task<MemoryEngineState> ReadMemoryEngineStateAsync` beside the code one, emitting `memory engine: …` (arms 1-4) and `memory rows pending: N`, with the open migration folded into the engine line as a suffix. Cost: ~40 lines, no new type, no new `ExitCode`, no doc-table change.

### 6.2 What the fuller version buys — and why the minimum is NOT the right answer here

Measured against the live bank (§0.3), the minimum would have printed `memory engine: /…/Salesforce__SFR-Embedding-Code-400M_R` and `memory rows pending: 47723` — and then `status: HEALTHY`, **exit 0**, exactly as today. The specific defect the task exists to close survives the minimum. So:

| Increment over the two-line minimum | What it buys | Verdict |
|---|---|---|
| **Line 9 as its own line** (not a suffix) | The state and its consequence are legible without decoding a 130-char engine line; it sits adjacent to the verdict it contradicts; and the `none open` case is *positively* reported, so silence never has to be interpreted | **Buy** |
| **`ExitCode.ModelMigrationOpen = 24`** | The only increment that closes the defect for a script, which is the documented use case (`configure-ai-raccoon-server.md:355`) | **Buy** (see §3.5 for the counter-case) |
| **`EngineDoctor` extraction** | Removes four future copies of two grammars and one guard (`derive-or-delete-the-list`); makes the read unit-testable rather than argv-only; fixes the false-remedy bug (§1.3) and the onnx-path misreport in **both** corpora at once. `ask-if-simpler` bars abstractions "added before a real caller needs it" — **two real callers exist today**, so the bar is met, not dodged | **Buy** (owner-ruled) |
| Fingerprint line | Nothing an operator can act on; a second copy of line 4 | **Don't buy** |
| Dimensions line | Prints `unset` on the bank where it matters (§2.2) — actively misleading | **Don't buy** |
| Asset-presence line | Needs a fourth dep incl. an HTTP-capable interface; duplicates two existing warnings | **Don't buy** |
| API-key **line** | Meaningless on a local bank; folded into line 4's remote arm instead | **Don't buy** |

**Verdict: the two-line minimum is the right answer for the *candidate list* — five of eight candidates are dropped or folded — and the wrong answer for the *defect*.** Final shape: **three new lines, one new exit code, one extracted component, zero new dependencies, zero new log ids.**

---

## 7. ADR

**No new ADR.** The change adds no state, no write path and no lifecycle rule: it reports state ADR-0076 defined (the outbox) and ADR-0087 already ruled memory-only, over a connection ADR-0075 already ruled read-only for the CLI. The one genuinely new decision is `ExitCode.ModelMigrationOpen = 24`, and `ExitCode` additions follow the file's own convention of citing a GH issue or review round in the doc comment, never an ADR (`ExitCode.cs:53,56,59-62,64-66,68-71`) — so record it there, in this plan, and in the how-to's exit-code table (`configure-ai-raccoon-server.md:357-364`).

---

## SCHEMA-LAST — the complete `doctor` output surface after the change

Eleven lines; line 11 has four arms. `Guard` below means `TableExistsAsync(<table>)` (`DoctorCommands.cs:149-153`) plus the enclosing `catch (SqliteException)` (`:124-127`), both moved into `EngineDoctor`. All literal text is invariant-culture.

| line | literal text | source of the value | state when unavailable |
|---|---|---|---|
| 1 | `ai-raccoon doctor: <bankPath>` | `ISqliteConnectionFactory.BankPath` (`DoctorCommands.cs:21,64`) | not reachable — a missing file exits `NoBank` (22) before any line prints (`:22-26`) |
| 2 | `user_version: <stored> (this binary: <current>)` | `PRAGMA user_version` vs `MemorySchema.CurrentVersion` (`SchemaDoctor.cs:17-18,26-33`; printed `DoctorCommands.cs:65`) | not reachable — a bank that cannot be read exits 2 (`:45-50`) |
| 3 | `application_id: <stored> (expected: <expected>)` | `PRAGMA application_id` vs `MemorySchema.SchemaDigest` (`SchemaDoctor.cs:19-20,33`; printed `:66`) | as line 2 |
| 4 **new** | arm 1 `memory engine: <manifest name> (<dir>)` · arm 2 `memory engine: bundled` · arm 3 `memory engine: openai:<model> (<baseUrl>)` — bare when no baseUrl; `— no API key set; run 'ai-raccoon model embedding set openai <model> --api-key <key>' or embeddings will fail` appended when `embedding.apiKey` is absent · arm 4 `memory engine: not configured — run 'ai-raccoon model embedding set local' to enable semantic memory search` | `settings`: `embedding.provider` (`EmbeddingSettingsKeys.cs:9`), `.model` (`:10`), `.baseUrl` (`:11`), `.apiKey` presence (`:17`); local dir/file split per `EmbeddingService.cs:277`; name via `ModelNameFor` (`DoctorCommands.cs:157-168`); `openai:<model>` per `SettingsCommands.cs:159`; key remedy verbatim from `SettingsCommands.cs:144`; `bundled` per `EntryEmbedder.cs:26,47,90` | `memory engine: unreadable (settings table missing or unreadable)` — Guard on `settings`. Arm 4 is reserved for a *positively absent* provider row, never for a failed read (§1.3) |
| 5 | `code engine: <manifest name> (<dir>)` · `code engine: not configured — run 'ai-raccoon model code set default' to enable semantic code search` | `settings`: `embedding.codeModel` (`EmbeddingSettingsKeys.cs:23-25`); name via `ModelNameFor`; command from `CodeEngineSetup.DefaultModelCommand` (`CodeEngineSetup.cs:15`); today's text at `DoctorCommands.cs:67-69` | `code engine: unreadable (settings table missing or unreadable)` — **behaviour change**: today an unreadable `settings` renders as `not configured` with a false remedy (`DoctorCommands.cs:116-118,124-127`) |
| 6 | `embedding threads: <n\|ORT default> (<setting\|halved-core default>)` | `settings` `embedding.threads` (`EmbeddingSettingsKeys.cs:33-35`) through `EmbeddingService.ResolveThreadCountForDisplay` / `ThreadCountDisplay` (`EmbeddingService.cs:327,330`; printed `DoctorCommands.cs:71-72`) | falls back to the unset resolution — `ResolveThreadCountForDisplay(null)` (`DoctorCommands.cs:142-146`); never prints `unreadable` (the resolver has a total answer). Shared by both corpora, so it stays a single line |
| 7 **new** | `memory rows pending: <n>` | `MemorySql.CountPendingEmbed` (`MemorySql.cs:362-363`), Guard on `entries`. No poison-row caveat: `entries` has no `embed_attempts` column | `memory rows pending: unreadable` — same `?? "unreadable"` rendering as line 8 (`DoctorCommands.cs:73-74`) |
| 8 | `code rows pending: <n>` | `MemorySql.CountPendingCodeEmbed` (`MemorySql.cs:419-420`) via `CountPendingCodeRowsAsync` (`DoctorCommands.cs:170-180`); counts quarantined rows by design (`MemorySql.cs:415-420`) | `code rows pending: unreadable` (`DoctorCommands.cs:73-74`); `0` when `code_entries` is absent (`:173-176`) |
| 9 **new** | `model migration: open since <yyyy-MM-ddTHH:mm:ssZ> (all MCP tool calls are refused until it finishes)` · `model migration: none open` | `MemorySql.SelectModelMigration` (`MemorySql.cs:470-472`) / `HasOpenModelMigration` (`:474-475`), Guard on `model_migration`; `IsOpen ⇔ FinishedAt is null` (`ModelMigration.cs:9-18`); `started_at` unix seconds (`EntryEmbedder.cs:65`) formatted per `WatchCommands.cs:185`; consequence per ADR-0087 Context 3 | `model migration: unreadable` — and the exit code falls back to `Success`, so an unreadable row can never produce 24 (§3 Decision D) |
| 10 | `doctor verifies schema shape only; it never repairs a bank` | literal (`DoctorCommands.cs:75`) | n/a |
| 11 | `status: HEALTHY` → exit 0 | `SchemaDoctorStatus.Healthy` (`SchemaDoctor.cs:32`) with no open migration (`DoctorCommands.cs:84-86`) | n/a |
| 11 **new arm** | `status: MIGRATION IN PROGRESS (schema shape is healthy; memory search is refused until the re-embed finishes)` → exit `ExitCode.ModelMigrationOpen = 24` | `SchemaDoctorStatus.Healthy` **and** a positively-read open row (line 9 arm 1). Reachable only from the `Healthy` arm — 19/20 outrank it (§3 Decision C) | falls back to `status: HEALTHY` / exit 0 whenever line 9 is `unreadable` |
| 11 | `status: SCHEMA NEWER THAN THIS BINARY (bank is v<n>, this binary supports up to v<m>) — update ai-raccoon` → exit 20 | `SchemaDoctorStatus.VersionAheadOfBinary` (`SchemaDoctor.cs:24-28`; printed `DoctorCommands.cs:79-82`); `ExitCode.cs:56-57` | n/a — outranks line 9 entirely |
| 11 | `status: SHAPE MISMATCH (<k> finding(s))` + one `  - <object>: <detail>` per finding + `remedy: start the server (ai-raccoon serve) — it repairs the schema on every open` → exit 19 | `SchemaDoctorStatus.ShapeMismatch` and `report.Findings` (`SchemaDoctor.cs:32-33,144-145`; printed `DoctorCommands.cs:88-97`); `ExitCode.cs:53-54` | n/a — outranks line 9 entirely |

**Live-bank prediction (the acceptance target for the other two lanes).** Run against `~/.ai-raccoon/memory.db` in the state of §0.3, doctor must print `memory engine: Salesforce/SFR-Embedding-Code-400M_R (/Users/arasz/.ai-raccoon/models/Salesforce__SFR-Embedding-Code-400M_R)`, `memory rows pending: 47723`, `model migration: open since 2026-08-26T10:18:01Z (all MCP tool calls are refused until it finishes)`, `status: MIGRATION IN PROGRESS (…)`, and **exit 24** — where today it prints `status: HEALTHY` and exits 0.
