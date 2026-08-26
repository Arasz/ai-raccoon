# LANE P2 — doctor implementation shape (extraction-first)

MoE planning lane P2 of task `doctor-feature-match`, 2026-08-26. Written by a planning subagent
against the task worktree; reviewed in the same task's review round. Sibling lanes: P1 (output contract), P3 (test design), P4 (runtime observability parity).
Companion research record: `2026-08-26-doctor-memory-embedding-research.md`.

---

`ai-raccoon doctor` reports the memory-embedding engine the way it reports the code engine.
Worktree read: `.ai-badger/worktrees/doctor-feature-match`, branch `task/doctor-feature-match`.
Every claim below was read from source in that worktree; line numbers are that worktree's.

This lane owns **how the code moves**. It does not own **which lines get printed** (lane P1) or
**the test list** (lane P3). Where a shape depends on a P1 ruling, both shapes are given and one is
recommended.

---

## 0. Three corrections to the research record, established from source

These change the design, so they come first.

**0.1 — The memory corpus *does* have a "not configured" state, and it is `embedding.provider`,
not `embedding.model`.** The research record (§3.3) says the memory engine is always resolved
because an unset `embedding.model` falls back to the bundled ONNX copy. The model fallback is real
(`BundledModel.ResolveModelPath` :70-77 → bundled copy beside the tool; `EmbeddingService.CreateLocal`
:343-345), but it is downstream of a gate the record missed:

- `PendingEmbedJob.HasWorkAsync` (`src/AiRaccoon.Infrastructure/Maintenance/PendingEmbedJob.cs:29-40`)
  returns `false` when `embedding.provider` is blank, with the comment *"no engine configured: a
  pending row here is legitimately unembeddable"* — and `Interval => null` (:27) means
  `HasWorkAsync` is the only gate.
- `EntryEmbedder.EmbedIfConfiguredAsync` :159-164 returns without embedding, `EmbedQueryAsync`
  :237-241 returns `QueryVector.Empty` (search silently degrades to keyword-only), and
  `ReconcileVecDimensionsAsync` :146-150 returns — all three on a blank provider.
- `EmbeddingService.CreateGenerator` :95-106 throws `ArgumentOutOfRangeException` for anything that
  is not `local`/`openai`, so a blank provider has no generator at all.

That is *exactly* the #422 state the code-engine line was invented for: rows ingest, sit `pending`
forever, and nothing anywhere says so. **The symmetry the owner's extraction wants is real** — both
corpora have `not configured` / `configured` / `unreadable`, and the memory side additionally has a
*fourth* state the code side does not (an open migration outbox).

**0.2 — `EventId` 1002 is NOT free.** `docs/reference/logging-event-ids.md:84` allocates **1002-1007**
to `EmbedDrainService.cs`, and the measurement agrees (highest id in `src/` is 1007, zero
duplicates). See §6 — this makes "add a log line to DoctorCommands" cost far more than it looks.

**0.3 — `MemorySql.SelectModelMigration` has zero callers.** `grep -rn "MemorySql.SelectModelMigration" src/`
→ 0 hits; the whole worktree only matches its own declaration (`MemorySql.cs:470-472`) and the
research record. `HasOpenModelMigration` *is* used (`EntryEmbedder.cs:109-110`). So doctor would be
`SelectModelMigration`'s **first ever consumer**: reuse is right, and it also means the statement has
never executed in production or in a test (see §3.4 and Risk R9).

---

## 1. WHAT CODE MOVES — the extraction

### 1.0 The owner constraint restated as an acceptance test

After the change there is exactly **one** copy of each of:

| Concern | Where it lives after the change |
|---|---|
| `TableExistsAsync`-guarded settings read | `DoctorCommands.ReadCorpusEngineStateAsync` (one call site) |
| pending-count read | `DoctorCommands.CountPendingRowsAsync(probe)` (one method, SQL passed in) |
| `catch (SqliteException)` → degraded state | `ReadCorpusEngineStateAsync`'s single `catch` |
| line-formatting grammar | `CorpusEngineLines.EngineLine` / `.PendingLine` |

Both corpora reach all four through a `CorpusEngineProbe` value. No `if (corpus == "memory")`
anywhere in the reader or the renderer.

### 1.1 (a) The extracted type and its shape

**Placement ruling: a new file, `src/AiRaccoon/Setup/Diagnostics/CorpusEngineReport.cs`, holding
`internal` types in the `AiRaccoon` assembly — not `Core`, not `Infrastructure`, not private nested
inside `DoctorCommands`.**

- **Not `AiRaccoon.Core`.** The probe's values *are* `EmbeddingSettingsKeys.*` and `MemorySql.*`,
  both in `AiRaccoon.Infrastructure`. Putting the type in Core would drag Infrastructure into Core
  and break clean layering. (Core keeps exactly what it has: `CodeEngineSetup.DefaultModelCommand`,
  a string — the remedy text is *consumed* by the probe, never the other way round.)
- **Not `AiRaccoon.Infrastructure`.** This is the CLI's output grammar. Infrastructure has no
  business owning how `doctor` words a line, even though `InternalsVisibleTo("AiRaccoon")`
  (`AiRaccoon.Infrastructure.csproj:34-37`) would make it mechanically possible.
- **Not private-nested.** `AiRaccoon.csproj:56-58` already has `InternalsVisibleTo("AiRaccoon.Tests")`,
  so an `internal` type is directly unit-testable. That matters: **every one of today's 11 doctor
  tests is `[Trait(Category, Integration)] [Trait(Speed, Slow)]` + `RetryFact`** with a real temp-dir
  bank (`DoctorCommandsTests.cs:23-24,41`). A pure-string renderer gives the byte-identity gate for
  WP1 and the wording gate for WP2 as `Unit`/`Fast` tests, instead of adding more slow ones.
- **I/O stays inside `DoctorCommands`.** The invariant `static-classes` allows static classes for
  extensions, constants and pure functions — *no I/O*. So the renderer (pure) is a static class in
  the new file; the **reader stays a `private static` method on `DoctorCommands`**, which already
  owns the connection and is itself an injectable component. That keeps "one copy" without inventing
  a type whose only state is a borrowed `SqliteConnection`.
  *Alternative, if lane P3 wants to drive the reader without argv:* a `sealed internal class
  CorpusEngineProbeReader(SqliteConnection connection)` with one instance method. Rejected as the
  default — it adds a seam nobody else needs and the connection's lifetime is `RunAsync`'s.

```csharp
// src/AiRaccoon/Setup/Diagnostics/CorpusEngineReport.cs  (new file)
using System.Globalization;

namespace AiRaccoon.Setup.Diagnostics;

/// <summary>What doctor reads and how it words one corpus's engine; values only, no I/O.</summary>
internal sealed record CorpusEngineProbe(
    string Label,
    string ConfiguredKey,
    string? ModelKey,
    string? BaseUrlKey,
    string CorpusTable,
    string PendingCountSql,
    string NotConfigured,
    Func<EngineSettings, EngineDisplay> Describe);

/// <summary>The settings values one engine line is worded from; every member may be absent.</summary>
internal sealed record EngineSettings(string? Configured, string? Model, string? BaseUrl);

/// <summary>An engine line's two halves: the value and its parenthetical qualifier.</summary>
internal readonly record struct EngineDisplay(string Model, string Detail);

/// <summary>Null <paramref name="Engine" /> is "no engine configured"; null <paramref name="PendingRows" /> is "unreadable".</summary>
internal sealed record CorpusEngineState(EngineDisplay? Engine, long? PendingRows);

/// <summary>doctor's engine/pending line grammar — the single producer of both sentences.</summary>
internal static class CorpusEngineLines
{
    internal static string EngineLine(CorpusEngineProbe probe, CorpusEngineState state) =>
        state.Engine is { } engine
            ? $"{probe.Label} engine: {engine.Model} ({engine.Detail})"
            : $"{probe.Label} engine: {probe.NotConfigured}";

    internal static string PendingLine(CorpusEngineProbe probe, CorpusEngineState state) =>
        $"{probe.Label} rows pending: {state.PendingRows?.ToString(CultureInfo.InvariantCulture) ?? "unreadable"}";
}
```

The reader, once, on `DoctorCommands` — the guard discipline of `ReadCodeEngineStateAsync`
(:108-128) preserved verbatim, only parameterised:

```csharp
/// <summary>
///     One corpus's engine state (#422 for code, this task for memory). A shape-mismatched bank is
///     exactly the bank doctor exists to diagnose, so this extra read must never decide the exit
///     code: every table it touches may be missing or the wrong shape. Guarded by existence and by
///     catch, and the report says so.
/// </summary>
private static async Task<CorpusEngineState> ReadCorpusEngineStateAsync(SqliteConnection connection,
    CorpusEngineProbe probe, CancellationToken cancellationToken)
{
    try
    {
        var settingsExist = await TableExistsAsync(connection, "settings", cancellationToken);
        var configured = settingsExist
            ? await ReadSettingAsync(connection, probe.ConfiguredKey, cancellationToken)
            : null;
        var model = probe.ModelKey is null || !settingsExist
            ? configured
            : await ReadSettingAsync(connection, probe.ModelKey, cancellationToken);
        var baseUrl = probe.BaseUrlKey is null || !settingsExist
            ? null
            : await ReadSettingAsync(connection, probe.BaseUrlKey, cancellationToken);

        var pending = await CountPendingRowsAsync(connection, probe, cancellationToken);
        return string.IsNullOrWhiteSpace(configured)
            ? new CorpusEngineState(null, pending)
            : new CorpusEngineState(probe.Describe(new EngineSettings(configured, model, baseUrl)), pending);
    }
    catch (SqliteException)
    {
        return new CorpusEngineState(null, null);
    }
}

private static async Task<long?> CountPendingRowsAsync(SqliteConnection connection,
    CorpusEngineProbe probe, CancellationToken cancellationToken) =>
    await TableExistsAsync(connection, probe.CorpusTable, cancellationToken)
        ? await connection.ExecuteScalarAsync<long>(new CommandDefinition(
            probe.PendingCountSql, cancellationToken: cancellationToken))
        : 0;
```

Note `ModelKey is null → model = configured` and `BaseUrlKey is null → baseUrl = null`: with the
code probe passing `null` for both, the code path issues **exactly the same statements it issues
today** (one `sqlite_master` check + one `settings` SELECT + one `sqlite_master` check + one COUNT).
No extra reads on the code path — statement-for-statement identical, not just byte-identical.

### 1.2 (b) How the code-engine call site collapses onto it

`ReadCodeEngineStateAsync` (:108-128) and `CodeEngineState` (:208) are **deleted**;
`CountPendingCodeRowsAsync` (:170-180) is **deleted** (absorbed by `CountPendingRowsAsync`);
`ModelNameFor` (:157-168) **stays** and is called from the code probe's `Describe`.

```csharp
/// <summary>#422: the code corpus can be completely inert without anything saying so; doctor is where someone looks when search feels wrong.</summary>
private static readonly CorpusEngineProbe CodeProbe = new(
    Label: "code",
    ConfiguredKey: EmbeddingSettingsKeys.CodeModel,
    ModelKey: null,
    BaseUrlKey: null,
    CorpusTable: "code_entries",
    PendingCountSql: MemorySql.CountPendingCodeEmbed,
    NotConfigured: $"not configured — run '{CodeEngineSetup.DefaultModelCommand}' to enable semantic code search",
    Describe: static settings => new EngineDisplay(ModelNameFor(settings.Configured!), settings.Configured!));
```

`RunAsync` :55 becomes `var code = await ReadCorpusEngineStateAsync(connection, CodeProbe, cancellationToken);`
and `ReportAsync` :67-69 / :73-74 become:

```csharp
await streams.WriteOutputLineAsync(CorpusEngineLines.EngineLine(CodeProbe, code));
...
await streams.WriteOutputLineAsync(CorpusEngineLines.PendingLine(CodeProbe, code));
```

Byte-identity check against today's literals, character by character:

| Today (`DoctorCommands.cs`) | After |
|---|---|
| `code engine: not configured — run '{CodeEngineSetup.DefaultModelCommand}' to enable semantic code search` (:68) | `"code" + " engine: " + NotConfigured` → same string |
| `code engine: {code.Model} ({code.Directory})` (:69) | `"code engine: " + Model + " (" + Detail + ")"`, `Model = ModelNameFor(dir)`, `Detail = dir` → same |
| `code rows pending: {code.PendingRows?.ToString(InvariantCulture) ?? "unreadable"}` (:74) | identical expression, one copy |

Also in scope for WP1 (a one-line cleanup that removes a real duplicate): `ReadSettingAsync`
(:182-185) holds an **inline SQL literal** `"SELECT value FROM settings WHERE key = @key"` while
`MemorySql.SelectSetting` (`MemorySql.cs:609-612`) is the same statement plus `LIMIT 1`. Point the
helper at the const. (`QuerySingleOrDefaultAsync` on a `key`-primary-key table returns the same
result either way — `settings.key` is `TEXT PRIMARY KEY`, `MemorySchema.cs:113-116`.)

*Deliberately not done:* replacing the per-key reads with one `MemorySql.SelectSettingsByPrefix`
(`MemorySql.cs:626-629`) call at prefix `embedding.`. It would be one statement instead of three and
every key both corpora need shares that prefix — but it also pulls `embedding.apiKey`
(`EmbeddingSettingsKeys.cs:17`) into a dictionary inside a command whose whole job is printing
things. Not worth the footgun for two saved statements on an interactive command. Named here so the
reviewer sees it was considered.

### 1.3 (c) Memory as a second configuration of the same probe

```csharp
/// <summary>The memory corpus's engine: an unset `embedding.provider` leaves rows permanently unembeddable (PendingEmbedJob.HasWorkAsync).</summary>
private static readonly CorpusEngineProbe MemoryProbe = new(
    Label: "memory",
    ConfiguredKey: EmbeddingSettingsKeys.Provider,
    ModelKey: EmbeddingSettingsKeys.Model,
    BaseUrlKey: EmbeddingSettingsKeys.BaseUrl,
    CorpusTable: "entries",
    PendingCountSql: MemorySql.CountPendingEmbed,
    NotConfigured: $"not configured — run '{EmbeddingEngineSetup.DefaultModelCommand}' to enable semantic memory search",
    Describe: static settings => DescribeMemoryEngine(settings));

/// <summary>Local bundled, local manifest directory, legacy .onnx path, or a remote model id — the four shapes `embedding.model` can hold.</summary>
private static EngineDisplay DescribeMemoryEngine(EngineSettings settings)
{
    if (string.IsNullOrWhiteSpace(settings.Model))
    {
        return new EngineDisplay("bundled", settings.Configured!);
    }

    return Directory.Exists(settings.Model)
        ? new EngineDisplay(ModelNameFor(settings.Model), settings.Model)
        : new EngineDisplay(settings.Model, settings.BaseUrl ?? settings.Configured!);
}
```

Every branch is a state that exists in the product, not a hypothetical:

| `provider` | `model` | Renders | Source of the state |
|---|---|---|---|
| absent | — | `memory engine: not configured — run '…'` | `PendingEmbedJob.cs:32-35` |
| `local` | absent | `memory engine: bundled (local)` | `BundledModel.cs:70-77`, `EmbeddingService.cs:343-345` |
| `local` | manifest dir | `memory engine: SFR-Embedding-Code-400M_R (/Users/…/models/Salesforce__SFR-Embedding-Code-400M_R)` | owner's live bank; `EmbeddingService.cs:348-355` |
| `local` | `.onnx` file | `memory engine: /path/model.onnx (local)` | `EmbeddingService.cs:357-369` |
| `openai` | model id | `memory engine: text-embedding-3-small (https://…)` | `EmbeddingService.cs:418-436` |

**`EmbeddingEngineSetup.DefaultModelCommand` does not exist yet.** The remedy string the memory side
needs (`ai-raccoon model embedding set local`) is currently hard-coded in two message literals —
`EmbeddingService.cs:364` and `EmbeddingAvailability.cs` (per the research record §3.3). The code
side solved this with one Core constant quoted by six surfaces (`CodeEngineSetup.cs:9-15`,
enforced by `DefaultCodeModelCommandTests`). If P1 rules that the memory not-configured line carries
a remedy, **the honest shape is the same constant, in Core, next to `CodeEngineSetup`**, with
`EmbeddingService.cs:364` re-pointed at it (`derive-or-delete-the-list`: two literals of one command
string is exactly the duplication that drifted `ai-raccoon model set code default` →
`ai-raccoon model code set default` in the release checklists). That is a small extra work package
(WP2b) and it is the only part of this lane that touches `Core` or `Infrastructure`.
If P1 rules *no remedy* on the memory line, `NotConfigured` is simply `"not configured"` and WP2b
disappears.

### 1.4 What the memory line must **not** copy from the code line

The code path's `catch (SqliteException)` returns `CodeEngineState(null, null, null)` → the renderer
sees `Engine is null` → it prints **`code engine: not configured — run '…'`** for a read that
*failed*. That is a latent lie (the bank may have a perfectly good engine; the read threw). It is
preserved bit-for-bit in WP1 because WP1 is a refactor. Fixing it means a third state
(`CorpusEngineState.Unreadable`) and an `unreadable` branch in `EngineLine`, which **changes the code
line's output** in a path no current test covers — so it is a P1 ruling and a separate WP (WP5,
optional), never smuggled into WP1 or WP2.

---

## 2. The migration line — the state doctor is silent about

Separate reader, **separate `try`**, deliberately: if `model_migration` is broken, the 47,723-row
pending count is the more valuable number and must still print. One `try` around both would blank
both.

```csharp
private enum MigrationRead { None, Open, Unreadable }

/// <summary>The outbox's reportable state (ADR-0076); unix seconds as stored, never converted in SQL.</summary>
private sealed record MigrationState(MigrationRead Result, long? StartedAtUnix);

/// <summary>#(this task): an open outbox row makes the server refuse every memory tool call (ToolGate), and doctor said nothing. Guarded like the engine reads — never the exit code.</summary>
private static async Task<MigrationState> ReadModelMigrationStateAsync(SqliteConnection connection,
    CancellationToken cancellationToken)
{
    try
    {
        if (!await TableExistsAsync(connection, "model_migration", cancellationToken))
        {
            return new MigrationState(MigrationRead.None, null);
        }

        var row = await connection.QuerySingleOrDefaultAsync<ModelMigrationRow>(new CommandDefinition(
            MemorySql.SelectModelMigration, cancellationToken: cancellationToken));
        return row is null || row.FinishedAt is not null
            ? new MigrationState(MigrationRead.None, null)
            : new MigrationState(MigrationRead.Open, row.StartedAt);
    }
    catch (SqliteException)
    {
        return new MigrationState(MigrationRead.Unreadable, null);
    }
}

/// <summary>model_migration's INTEGER unix-seconds columns as they are stored — Dapper has no DateTimeOffset handler in this solution.</summary>
private sealed record ModelMigrationRow
{
    public long StartedAt { get; init; }

    public long? FinishedAt { get; init; }
}
```

Renderer (same file as the other two lines, so the grammar stays in one place):

```csharp
internal static string MigrationLine(MigrationRead result, string? startedAt) => result switch
{
    MigrationRead.Open => $"memory migration: open since {startedAt} — memory tools are refused until it drains",
    MigrationRead.Unreadable => "memory migration: unreadable",
    _ => "memory migration: none"
};
```

`row is null || row.FinishedAt is not null → None` collapses "no row" and "closed row" into one
answer, which is right for an operator: both mean *nothing is owed*. `IsOpen ⇔ FinishedAt is null` is
the rule `ModelMigration.IsOpen` already encodes (`AiRaccoon.Core/Memory/ModelMigration.cs:17`); it
is re-derived here rather than by mapping onto that record — see §3.4.

**P1 options this lane can implement either way (recommendation: M1).**

- **M1 (pick).** One unconditional line, three states, as above. Unconditional because doctor's six
  existing lines are all unconditional, a line that only appears in trouble is the line nobody knows
  to grep for, and a stable line count is what protects anything parsing stdout.
- **M2.** Fold it into the engine line (`memory engine: … (migration open since …)`). Cheaper on line
  count; buries the one fact that explains why every tool call is being refused, inside a
  parenthetical about the model. Not recommended.
- **M3.** Add lease liveness (`draining now` vs `stalled`). Needs `lease_owner`/`lease_expires_at`
  **and** a clock — see §3.3 and §5. Recommended only if P1 explicitly asks; costs a new SQL const
  and either a fourth constructor dependency or a static clock read.

---

## 3. WHICH QUERIES

### 3.1 Reused, unchanged — no new const

| Need | Const | Evidence it fits |
|---|---|---|
| memory pending rows | `MemorySql.CountPendingEmbed` (`:362-363`) | `SELECT COUNT(*) FROM entries WHERE embed_state = 'pending'` — bank-wide, exact structural twin of `CountPendingCodeEmbed` (`:419-420`) that doctor already uses. Index-served: `idx_entries_embed_state ON entries(embed_state, project_id)` (`MemorySchema.cs:314`) |
| code pending rows | `MemorySql.CountPendingCodeEmbed` (`:419-420`) | already doctor's; now passed in via the probe |
| the outbox row | `MemorySql.SelectModelMigration` (`:470-472`) | projects `started_at`/`finished_at`, which is all M1 needs; unmapped columns are ignored by Dapper. **Zero callers today** (§0.3) — doctor becomes the first |
| settings values | `MemorySql.SelectSetting` (`:609-612`) | replaces DoctorCommands' inline literal (`:184-185`); same statement + `LIMIT 1` on a primary-key lookup |
| model name from a directory | `ModelNameFor` (`DoctorCommands.cs:157-168`) | already tolerant of an unreadable manifest; reused by both probes' `Describe` |

**Reuse beats adding, and here it wins outright: M1 needs zero new SQL.**

### 3.2 Deliberately NOT used

- `MemorySql.HasOpenModelMigration` (`:474-475`). It answers only "is it open" as a `count(*)`.
  `SelectModelMigration` answers that **and** "since when" in the same statement, so using both
  would be two reads for one fact. Skip it. (It stays the right query for `ToolGate`'s hot path,
  where the timestamp is not wanted — `EntryEmbedder.cs:109-110`.)
- `MemorySql.PendingCount` (`:350-351`). Project-scoped (`AND project_id = @projectId`); doctor has
  no project. Wrong query.
- `MemorySql.HasPendingEmbed` (`:358-359`). `EXISTS`, not a count.
- `MemorySql.UpsertSetting` (`:604-607`). A **write**. Named in the brief; doctor must never
  reference it. Its presence in the *tests* (`DoctorCommandsTests.cs:97,141,171`) is how a test
  arranges a bank before running doctor — that is the test's write, not doctor's.

### 3.3 Where a NEW const would be needed, and why the existing one does not fit

Only under P1 option **M3** (lease liveness):

```csharp
// MemorySql.cs, in the model_migration block
/// <summary>The outbox's lease as stored (unix seconds) — doctor's "is anything actually draining this" read.</summary>
public const string SelectModelMigrationLease =
    "SELECT lease_owner AS LeaseOwner, lease_expires_at AS LeaseExpiresAt FROM model_migration WHERE id = 1";
```

Reason `SelectModelMigration` does not fit: its projection has no `lease_owner`/`lease_expires_at`
(read at `:470-472`; the columns exist on the table, `MemorySchema.cs:407-408`). The alternative is
**widening `SelectModelMigration`** to include them — cheaper (no second const, no second statement)
and blast-radius-free *today precisely because it has no callers*, but it makes the canonical outbox
projection carry two columns only a report wants. Ruling: if M3 lands, **widen** and say so in the
const's own remark; if M3 does not land, touch `MemorySql.cs` not at all.

### 3.4 The sharpest trap in this change

`SelectModelMigration` aliases `started_at AS StartedAt, finished_at AS FinishedAt`, and the obvious
target is `AiRaccoon.Core.Memory.ModelMigration`, whose members are `DateTimeOffset StartedAt` /
`DateTimeOffset? FinishedAt` (`ModelMigration.cs:14-15`). **Do not map onto it.**

- The columns are `INTEGER` unix seconds (`MemorySchema.cs:405-406`; written as
  `now.ToUnixTimeSeconds()`, `EntryEmbedder.cs:65`).
- `grep -rn "AddTypeHandler\|TypeHandler<" src/` → **0 hits**. No Dapper type handler exists in this
  solution.
- Dapper falls back to `Convert.ChangeType`, and `DateTimeOffset` does not implement `IConvertible`
  → `InvalidCastException`, which is **not** a `SqliteException`, so the `catch (SqliteException)`
  guard would not swallow it and `doctor` would terminate on an unhandled exception instead of
  degrading.
- The codebase's own precedent is a `long` row record converted in C#:
  `SqliteMemoryStore.Rows.cs:96-98`, `MetricsReportService.cs:84`, `MaintenanceJobRunner.cs:173`, and
  the `init`-property row-record style of `EntryEmbedder.EmbedRow` (`:408-413`).

Hence `ModelMigrationRow` with `long`/`long?` in §2.

---

## 4. READ-ONLY SAFETY

**Every statement this lane adds is a pure read.** Verbatim, in full:

1. `SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = @table` — `TableExistsAsync`
   (`:151-153`), unchanged.
2. `SELECT value FROM settings WHERE key = @key LIMIT 1` — `MemorySql.SelectSetting`.
3. `SELECT COUNT(*) FROM entries WHERE embed_state = 'pending'` — `MemorySql.CountPendingEmbed`.
4. `SELECT provider …, started_at …, finished_at … FROM model_migration WHERE id = 1` —
   `MemorySql.SelectModelMigration`.

No `INSERT`/`UPDATE`/`DELETE`/`PRAGMA`/`CREATE`, no temp table, no `ATTACH`. The connection is
already `SqliteOpenMode.ReadOnly` (`OpenBankReadOnlyAsync`, `:188-205`) and never calls
`MemorySchema.EnsureAsync` — so even a write attempt would fail rather than mutate; the reads are
read-only by construction *and* by the connection mode. `DoctorCommandsTests` pins this twice with a
file SHA-256 before/after (`:194-201` on a hand-surgered bank, `:219-231` on a healthy one) plus
`user_version`/`application_id` equality — those two are the standing gate and they must stay green
without modification.

**Required `TableExistsAsync` guards** — one per table touched, no exceptions:

| Guard | Protects | Why it is genuinely reachable |
|---|---|---|
| `TableExistsAsync(connection, "settings")` | reads 2 (all of them) | the GH #357 repro bank has exactly one table, `entries (only_one_column TEXT)` (`DoctorCommandsTests.cs:190`) — no `settings` |
| `TableExistsAsync(connection, probe.CorpusTable)` → `"entries"` for memory | read 3 | same bank shape; and `code_entries` is absent on any pre-1.30 bank |
| `TableExistsAsync(connection, "model_migration")` | read 4 | `model_migration` is in the unconditional `Ddl` block (`MemorySchema.cs:399-409`), but a pre-ADR-0076 bank, a partial restore, or the one-table repro bank has none |

Guard **and** `catch (SqliteException)`, both, for the same reason the existing comment gives
(`:111-113`): a table can exist with the wrong *shape*, in which case existence passes and the
`SELECT` throws. Neither the guard nor the catch may reach the exit code — `ReportAsync`'s `switch`
stays keyed on `report.Status` alone (`:77-98`), which is derived only from schema shape and version
skew (`SchemaDoctor.DiagnoseAsync`).

**Exit-code ruling (P1's to make, this lane's to cost).** Keep it report-only. Everything in
`DoctorCommands` says schema-shape-only, and the live evidence (a bank whose tools are all being
refused printing `status: HEALTHY`, exit 0) argues for *making the line impossible to miss*, not for
inventing a code. If P1 rules otherwise, the shape is: a new `ExitCode` const — **next free value is
24** (`ExitCode.cs` uses 0-7, 9-23; 8 retired, `:17-19`) — returned from a new branch *before* the
`SchemaDoctorStatus.Healthy` arm, and the `SchemaVerificationFailed` arm keeps precedence. That
changes `Doctor_HealthyBank_ReportsHealthyAndExitsZero` (`:60-70`) from a fact to a
conditional, so it is a P1 ruling with a test-lane cost, not a free addition.

---

## 5. NULLABILITY / DEGRADED STATES

Precedent being followed: `code.PendingRows?.ToString(InvariantCulture) ?? "unreadable"` (`:74`).

| Field | Read | Table missing | Row/value absent | `SqliteException` | Printed when degraded |
|---|---|---|---|---|---|
| memory engine (`Configured` = `embedding.provider`) | `SelectSetting` | `settings` absent → treated as unset | unset → not-configured branch | whole state → `CorpusEngineState(null, null)` | `memory engine: not configured …` — **the known lie, §1.4**; `unreadable` only if WP5 lands |
| memory engine model (`embedding.model`) | `SelectSetting` | as above | absent → `bundled` | as above | subsumed by the line above |
| memory engine base url (`embedding.baseUrl`) | `SelectSetting` | as above | absent → falls back to `Configured` as the qualifier | as above | subsumed |
| memory rows pending | `CountPendingEmbed` | `entries` absent → **`0`** | n/a | `null` | `memory rows pending: unreadable` |
| code engine / code rows pending | unchanged | unchanged (`0`) | unchanged | unchanged | unchanged — **byte-identical** |
| migration | `SelectModelMigration` | `model_migration` absent → `None` | no row, or `finished_at` set → `None` | `Unreadable` | `memory migration: unreadable` |
| migration `started_at` | same row | — | only read when open (`finished_at IS NULL` ⇒ `started_at NOT NULL`, `MemorySchema.cs:405`) | — | never null on the `Open` branch |

Two notes the implementer must not "improve" silently:

- **`0` for a missing corpus table** is today's convention (`CountPendingCodeRowsAsync` `:173-176`
  returns `0`, not `null`) and it conflates "no rows" with "no table". It is kept for byte-identity.
  Changing it to `null`/`unreadable` would change the *code* line on the repro bank, so it belongs in
  WP5 with the §1.4 fix, under one P1 ruling about degraded wording, or nowhere.
- On the owner's live bank the memory pending count is **47,723 of 51,947** — a real number that
  today's report never shows. `COUNT(*)` there is index-served (`MemorySchema.cs:314`), so this is a
  millisecond read, not a scan; no `EXISTS` shortcut needed.

---

## 6. TIME AND FORMATTING

**Formatting.** Reuse the CLI's existing one-liner, verbatim in shape:

```csharp
/// <summary>Unix seconds as an absolute UTC instant — mirrors WatchCommands.FormatTimestamp (:185).</summary>
private static string FormatTimestamp(long unixSeconds) =>
    DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime
        .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
```

Source: `src/AiRaccoon/Setup/Cli/Commands/WatchCommands.cs:185`. `CultureInfo.InvariantCulture` is
already imported and used in `DoctorCommands` (`:1`, `:74`), so nothing new arrives. On the owner's
row, `started_at = 1787739481` → `2026-08-26T…Z`.

Two placements, pick per taste: (a) a `private static` on `DoctorCommands` with a one-line comment
naming `WatchCommands` as the twin — one duplicated line across two command classes; (b) an
`internal static class UnixTime` in `Setup/Cli/` that both use — DRY, pure functions so the
`static-classes` invariant is satisfied, but it touches `WatchCommands.cs` and drags its tests into
the diff. Recommendation: **(b), as its own optional work package (WP6)**, because this lane's whole
premise is that a second copy of a shared rendering is what the owner asked to stop creating; take
(a) only if WP6 is cut for scope.

**Does doctor need a `TimeProvider`? No — and it should not take one.**

- An **absolute** timestamp needs no clock at all. M1 prints `open since <ts>`; zero clock reads.
- A **relative** phrase (`open for 3h`) or a **liveness verdict** (`stalled`) needs `now`. Ways to
  get it, ranked:
  1. **Don't** (M1). The operator reads the timestamp. Zero deps, zero clock semantics, honest.
  2. Print the lease facts instead of a verdict — `lease_owner` and its expiry timestamp — and let the
     reader compare. Still zero clock. Needs M3's SQL (§3.3). This is the best available answer to
     the owner's "draining now vs stalled without a TimeProvider" question: **a null `lease_owner`
     proves nothing is draining; a non-null one plus a printed expiry lets a human see it has
     passed.** Doctor states facts; it does not need to be the thing that subtracts them.
  3. `TimeProvider.System.GetUtcNow()` inline — a static clock read in a report. Works, untestable
     for the boundary case, inconsistent with `EntryEmbedder` taking an injected `TimeProvider`
     (`EntryEmbedder.cs:20`).
  4. A fourth constructor dependency. Real cost: `DoctorCommandsTests.CreateDoctor` constructs it
     with exactly three arguments (`:41`) for all 11 tests, the DI registration changes, and
     `CliCommandsDoNotOpenTheBankTests` walks doctor's constructor graph field-by-field for a live
     `ISqliteConnectionFactory` (`:25-52`, doctor is one of three allowlisted exceptions). Adding a
     dep is not free here.
  5. SQL `unixepoch()` — **rejected**: `grep -rn "unixepoch\|strftime(" src/` → 0 hits. Zero
     precedent, and clock-in-SQL under a bundled SQLCipher build is an unverified assumption.

Ruling: **M1 (no clock).** If P1 wants liveness, take option 2 (facts, not a verdict).

---

## 7. LOGGING

**Recommendation: add no `[LoggerMessage]` line at all.** Two reasons, one of which is a hard gate.

*Reason 1 — the pattern is already deliberate silence.* Both existing extra reads swallow
`SqliteException` with no log (`:124-127`, `:142-146`); the report itself is the channel, and the
`unreadable` token is the message. A `doctor` invocation is interactive and its stdout is the
artifact; a warning in a log the operator is not tailing adds nothing.

*Reason 2 — `DoctorCommands` cannot take a new id without a block move.* Measured, not assumed:

```
$ grep -rn "EventId = " src --include="*.cs" | sed -E 's/.*EventId = ([0-9]+).*/\1/' | sort -n | tail -3
1005  1006  1007
$ grep -rho "EventId = [0-9]\+" src | grep -oE "[0-9]+" | sort -n | uniq -d      # duplicates
(empty)
```

1000-1001 → `DoctorCommands.cs:215,218`. **1002-1007 → `EmbedDrainService.cs:195-213`**
(`docs/reference/logging-event-ids.md:84`). So the next unused id is **1008** — and
`LoggerMessageEventIdTests.EventIdBlocks_DoNotInterleaveBetweenOwners`
(`tests/AiRaccoon.Tests/Unit/Observability/LoggerMessageEventIdTests.cs:35-50`) asserts no two
owners' `[min,max]` ranges overlap. Giving `DoctorCommands` 1008 makes its range `[1000,1008]`,
which overlaps `EmbedDrainService`'s `[1002,1007]` → **that test goes red**. The brief's premise
("1000-1001 are taken" ⇒ 1002 is next) does not hold.

The only compliant way to add a doctor log line is therefore a **block move**, and its full cost is:

1. `DoctorCommands.Log` renumbered to a fresh contiguous block after the current maximum — e.g.
   **1010-1012** (1008/1009 left as the gap the doc's own convention likes for retired ids).
2. `docs/reference/logging-event-ids.md:83` row rewritten from `1000-1001` to `1010-1012`, with the
   retirement of 1000/1001 recorded the way `:17-19` of `ExitCode.cs` and the doc's other
   retirements are (retire, never silently reuse).
3. The prose count at `docs/reference/logging-event-ids.md:12` (**168**) incremented —
   `LoggerMessageEventIdTests.DocumentedCount_MatchesTheMeasuredCount` (`:68-75`) parses that exact
   number out of the sentence and compares it with reflection over the assemblies.
4. `EveryEventIdInSource_FallsInsideADocumentedBlock` (`:78-92`) then re-derives clean.
5. And 1000/1001 are ids that live logs, past checklists and operator habit already reference.

That is a disproportionate price for a line whose information is already in stdout. **If review
insists on a log line anyway, the plan above is the plan — steps 1-4 in one commit, or the test
lane's `LoggerMessageEventIdTests` run goes red for a reason unrelated to doctor.**

---

## 8. WORK-PACKAGE SPLIT

Small on purpose: this is a report surface. Three packages do the work; three are conditional on a
P1 ruling. Everything in `DoctorCommands.cs` **serialises** — that is one file and three of the
packages touch it.

**WP1 — extract the shared corpus reader + renderer (pure refactor, zero output change).**
*Files:* `src/AiRaccoon/Setup/Diagnostics/CorpusEngineReport.cs` (new),
`src/AiRaccoon/Setup/Cli/Commands/DoctorCommands.cs` (edit),
`tests/AiRaccoon.Tests/Unit/Setup/Diagnostics/CorpusEngineLinesTests.cs` (new).
*Shares a file with:* WP2, WP3, WP5 (all serialise behind it).
*RED first:* the new `Unit`/`Fast` renderer test is written against `CorpusEngineLines` before the
type exists — it cannot compile, then it passes. Its three cases are the three code-engine strings
today's binary prints, pasted as literals, so the refactor is pinned by construction.
*Acceptance:* `DoctorCommandsTests` (11 tests) green **with no edit to that file**, and the new
renderer test green; `ReadCodeEngineStateAsync`, `CodeEngineState` and `CountPendingCodeRowsAsync`
no longer exist; `grep -c "TableExistsAsync(connection, \"settings\"" DoctorCommands.cs` = 1.

**WP2 — the memory engine's two lines (second configuration of the probe).**
*Files:* `DoctorCommands.cs` (edit). *Serialises after WP1.*
*Acceptance:* on a bank with `embedding.provider=local` + `embedding.model=<manifest dir>`, doctor
prints `memory engine: <manifest name> (<dir>)`; with no `embedding.provider` row it prints the
not-configured wording; with N pending `entries` rows it prints `memory rows pending: N`; the four
existing code/threads lines are unchanged **byte for byte**.

**WP2b — `EmbeddingEngineSetup.DefaultModelCommand` (conditional on P1 ruling a remedy string).**
*Files:* `src/AiRaccoon.Core/Memory/EmbeddingEngineSetup.cs` (new),
`src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs:364` (re-point the literal),
`DoctorCommands.cs` (consume). *Parallel with WP1* (different files) but merges before WP2.
*Acceptance:* one constant; `EmbeddingService`'s message and doctor's line both quote it; a test
asserts the two agree (the `DefaultCodeModelCommandTests` pattern).

**WP3 — the migration line.**
*Files:* `DoctorCommands.cs` (edit), `CorpusEngineReport.cs` (the third renderer case).
*Serialises after WP2.*
*Acceptance:* an open outbox row (`finished_at IS NULL`) → `memory migration: open since <UTC ts>…`;
a closed row, no row, or no `model_migration` table → `memory migration: none`; exit code unchanged
in all three; a bank with only a one-column `entries` table still reaches `status:` (no crash).

**WP4 — docs.**
*Files:* `docs/how-to/configure-ai-raccoon-server.md:330-337` (the sample block is **already** stale —
3 lines at `user_version: 10` while the binary prints 6 at v11), plus the doctor sentence in
`docs/reference/agent-memory-server.md:199-207` and `docs/how-to/configure-embedding-engines.md:144-147`
**only if** WP2b changes what those quote. *Parallel with WP1-WP3 in principle; content depends on
their final strings, so land it last.*
*Acceptance:* the sample block equals real output from the built binary on a scratch bank, line for
line, at the current `user_version`.

**WP5 — degraded wording (conditional on a P1 ruling; changes existing code-engine output).**
*Files:* `CorpusEngineReport.cs`, `DoctorCommands.cs`. *Serialises after WP3.*
*Acceptance:* a read that throws prints `<label> engine: unreadable`, not the not-configured remedy;
`Doctor_NoCodeEngine_SaysNotConfigured_AndNamesTheInstallCommand` still green (it arranges a bank
with a readable, empty `settings` table, so it must keep hitting the not-configured branch).

**WP6 — `UnixTime` shared formatter (optional, DRY).**
*Files:* `src/AiRaccoon/Setup/Cli/UnixTime.cs` (new), `WatchCommands.cs:185` (edit),
`DoctorCommands.cs` (consume). *Serialises with WP3.*
*Acceptance:* one formatter; `WatchCommands`' existing timestamp tests green unchanged.

---

## 9. RISKS — everything this can break

**R1 — `DoctorCommandsTests` string assertions (the byte-identity gate).** 11 tests, argv-driven via
`CliRun`, `Integration`+`Slow`+`RetryFact`. The ones that protect the WP1 refactor:
`Doctor_NoCodeEngine_SaysNotConfigured_AndNamesTheInstallCommand` (`:78-88`, pins
`"code engine: not configured"` **and** `CodeEngineSetup.DefaultModelCommand`),
`Doctor_ConfiguredCodeEngine_NamesTheModelAndItsDirectory` (`:91-107`, pins `"code engine:"`, the
directory, and `ShouldNotContain("code engine: not configured")`),
`Doctor_ReportsHowManyCodeRowsArePending` (`:115-130`, pins `"code rows pending: 2"`), the three
threads tests (`:137-179`), `Doctor_HandSurgeredEntriesTable_…` (`:183-210`) and
`Doctor_NeverModifiesTheBank` (`:213-231`). **They must pass unmodified after WP1** — that is the
refactor's proof. They use `ShouldContain`, so they pin *wording*, not line order or line count:
appending memory lines cannot break them, and equally cannot catch a memory line printed in the
wrong place. That gap is lane P3's to close.

**R2 — the two SHA-256 "never writes" assertions** (`:194-201`, `:219-231`) plus
`user_version`/`application_id` equality. Any accidental write, `PRAGMA` with side effects, or a
switch to `OpenBankAsync` breaks them. §4 keeps every added statement a `SELECT`.

**R3 — `LoggerMessageEventIdTests`.** Red the moment a new `EventId` is added to `DoctorCommands`
without the block move (§7). Also red if the docs count sentence and the assemblies disagree.

**R4 — `CliCommandsDoNotOpenTheBankTests`.** `DoctorCommands` is one of three allowlisted
bank-capable commands (`:40-52`). A new constructor dependency gets its object graph walked
field-by-field for a live `ISqliteConnectionFactory`; a dep that transitively holds one would newly
trip this test. Another reason for zero new deps (§6). Its doc comment cites
`DoctorCommands.cs:85-102` for the read-only open, which is **already stale** (it is `:188-205`);
every WP here shifts those lines further. Cosmetic, ungated, worth one line of cleanup if the file is
open anyway.

**R5 — `docs/how-to/configure-ai-raccoon-server.md:330-337`.** The "healthy bank" sample is already
wrong (3 lines, `user_version: 10`, `application_id: -519479064`) against a binary that prints 6
lines at v11 with `-1765263351`. No test pins it. Whoever changes the output owns fixing it (WP4) —
and should fix the v10/v11 drift in the same edit rather than adding memory lines to a stale block.

**R6 — `docs/reference/agent-memory-server.md:199-207` and
`docs/how-to/configure-embedding-engines.md:144-147`.** Both say doctor quotes
`CodeEngineSetup.DefaultModelCommand`; still true after WP1/WP2 because the constant and the string
are untouched. `DefaultCodeModelCommandTests.TheHowTo_QuotesTheCommandVerbatim` (`:110-118`) pins
the how-to. **Separate landmine:** `agent-memory-server.md:196` contains a committed stray merge
marker `>>>>>>> origin/main`. A WP4 edit lands right next to it — fix it in its own commit with its
own note, or leave it; do not let it ride silently inside a docs-drift diff.

**R7 — `README.md:40`** ("`doctor` shows the effective thread count") stays true. A new memory line
is a "What's new" row at release time, not now.

**R8 — the release checklist.** `docs/work/checklist/*.json` are **dated historical records**, one
per run; there is no doctor row in the skill's `templates/checklist-template.json` (checked). So:
do **not** rewrite `2026-08-23-1.33.0-release.json:172-190` or `2026-08-22-1.32.0-check.json:184-224`
— their quoted expected output was true for that binary. The next release run adds a
`doctor-reports-memory-engine-state` item. (Those files already quote
`ai-raccoon model set code default` while the constant is `ai-raccoon model code set default` —
proof that quoted command strings drift, and the argument for WP2b's single constant.)

**R9 — `SelectModelMigration` has never executed** (§0.3). A wrong alias would surface for the first
time here. Mitigated by reading the DDL against it: `provider, model, base_url, engine, started_at,
finished_at` all match `MemorySchema.cs:399-409`. WP3's test executes it against a real bank, which
is the first real proof it works.

**R10 — the Dapper `DateTimeOffset` mapping (§3.4).** Highest-severity: it fails as
`InvalidCastException`, escapes the `catch (SqliteException)`, and turns a diagnostic into a crash on
exactly the bank the operator is diagnosing. Avoided by the `long` row record.

**R11 — anything parsing doctor's stdout.** No in-repo parser exists (searched: only tests, docs and
checklists match the line texts). External scripts are invisible to us, so: append lines rather than
reordering, keep every existing prefix byte-identical, and keep the line count stable across states
(M1's unconditional migration line, §2).

**R12 — line-count creep.** Six lines today; M1 + WP2 makes nine. That is a judgement call for P1,
not a defect — but it is why WP2's `memory rows pending` should sit next to `code rows pending` and
not somewhere else: two adjacent counts read as a pair, and a reader scanning for "what is stuck"
finds both in one place.

**R13 — the refactor tempts an in-flight behaviour change.** §1.4 and the `0`-for-missing-table
convention (§5) are both wrong-ish and both preserved. The risk is a well-meaning implementer
"fixing" them inside WP1 and quietly changing the code line. WP1's acceptance criterion is exactly
"no output change"; WP5 exists so the fix has somewhere legitimate to go.

---

## SCHEMA-LAST — work packages

| WP | files | depends on | acceptance criterion | gate command |
|---|---|---|---|---|
| WP1 | `src/AiRaccoon/Setup/Diagnostics/CorpusEngineReport.cs` (new), `src/AiRaccoon/Setup/Cli/Commands/DoctorCommands.cs`, `tests/AiRaccoon.Tests/Unit/Setup/Diagnostics/CorpusEngineLinesTests.cs` (new) | — | Output byte-identical: all 11 `DoctorCommandsTests` pass **unmodified**; `ReadCodeEngineStateAsync`/`CodeEngineState`/`CountPendingCodeRowsAsync` deleted; exactly one `TableExistsAsync(…, "settings")`, one `catch (SqliteException)`, one pending-count read, one line-grammar producer | `dotnet test tests/AiRaccoon.Tests --filter "FullyQualifiedName~CorpusEngineLinesTests\|FullyQualifiedName~DoctorCommandsTests"` |
| WP2 | `src/AiRaccoon/Setup/Cli/Commands/DoctorCommands.cs` | WP1 (same file), WP2b if a remedy string is ruled in | `memory engine:` renders all five real states (§1.3) and `memory rows pending: N` matches a seeded `entries` count; code/threads lines unchanged | `dotnet test tests/AiRaccoon.Tests --filter "FullyQualifiedName~DoctorCommandsTests"` |
| WP2b | `src/AiRaccoon.Core/Memory/EmbeddingEngineSetup.cs` (new), `src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs`, `DoctorCommands.cs` | P1 ruling: memory not-configured carries a remedy | One constant for `ai-raccoon model embedding set local`; `EmbeddingService`'s runtime message and doctor's line both quote it; no literal copy remains | `dotnet test tests/AiRaccoon.Tests --filter "Category=Unit&Speed=Fast"` |
| WP3 | `DoctorCommands.cs`, `CorpusEngineReport.cs` | WP1, WP2 (same files) | Open outbox row → `memory migration: open since <UTC ts>…`; closed/absent row/absent table → `none`; read throws → `unreadable`; exit code unchanged in every case | `dotnet test tests/AiRaccoon.Tests --filter "FullyQualifiedName~DoctorCommandsTests"` |
| WP4 | `docs/how-to/configure-ai-raccoon-server.md`, (conditionally) `docs/reference/agent-memory-server.md`, `docs/how-to/configure-embedding-engines.md` | WP2, WP3 (final strings) | Sample block equals real output line-for-line at the current `user_version`; the v10 sample drift is gone | `dotnet run --project src/AiRaccoon -- --data-root <scratch> doctor` diffed against the doc block, then `dotnet test tests/AiRaccoon.Tests --filter "FullyQualifiedName~DefaultCodeModelCommandTests"` |
| WP5 | `CorpusEngineReport.cs`, `DoctorCommands.cs` | P1 ruling on degraded wording; WP3 | A throwing read prints `<label> engine: unreadable` instead of the not-configured remedy; the not-configured tests still green | `dotnet test tests/AiRaccoon.Tests --filter "FullyQualifiedName~DoctorCommandsTests"` |
| WP6 | `src/AiRaccoon/Setup/Cli/UnixTime.cs` (new), `WatchCommands.cs`, `DoctorCommands.cs` | WP3 | One unix-seconds formatter; `WatchCommands`' timestamp tests green unmodified | `dotnet test tests/AiRaccoon.Tests --filter "FullyQualifiedName~Watch"` |
| WP7 *(only if review demands a log line — see §7)* | `DoctorCommands.cs`, `docs/reference/logging-event-ids.md` | §7's block move | `DoctorCommands` block moved to 1010-1012, 1000-1001 recorded as retired, doc count 168→169, no owner ranges interleave | `dotnet test tests/AiRaccoon.Tests --filter "FullyQualifiedName~LoggerMessageEventIdTests"` |
