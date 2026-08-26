# Research record — `doctor` reports the memory-embedding engine (task `doctor-feature-match`)

Date: 2026-08-26. Binary version at time of writing: `VERSION` = 1.35.0, schema `user_version` 11.

Every finding below cites the file it was read from. Anything not read from source is labelled
**hypothesis** and is a question for the plan, not an input to it.

---

## 1. The request

`ai-raccoon doctor` today reports the **code** engine and the shared thread count; it says nothing
about the **memory** engine. Observed output supplied by the owner:

```
ai-raccoon doctor: /Users/arasz/.ai-raccoon/memory.db
user_version: 11 (this binary: 11)
application_id: -1765263351 (expected: -1765263351)
code engine: Salesforce/SFR-Embedding-Code-400M_R (/Users/arasz/.ai-raccoon/models/Salesforce__SFR-Embedding-Code-400M_R)
embedding threads: 5 (halved-core default)
code rows pending: 0
doctor verifies schema shape only; it never repairs a bank
status: HEALTHY
```

Goal: the memory corpus gets the same class of report. Deliverable of this task is a **plan**,
MoE-reviewed and integrated — no implementation.

## 2. What doctor is, mechanically

- `src/AiRaccoon/Setup/Cli/Commands/DoctorCommands.cs:19-59` — `RunAsync`: bank-exists check →
  key resolve → **read-only** open (`OpenBankReadOnlyAsync`, `:188-205`, mirrors
  `AppRegistrations.OpenSnapshotReadOnly`; never `MemorySchema.EnsureAsync`) → `SchemaDoctor.DiagnoseAsync`
  → two extra state reads → `ReportAsync`.
- `:61-99` `ReportAsync` — the whole output surface: six fixed lines, then a status line whose branch
  decides the exit code (`SchemaNewerThanBinary` / `Success` / `SchemaVerificationFailed`).
- `src/AiRaccoon.Infrastructure/Sqlite/SchemaDoctor.cs:15-34` — shape diff against the real `Ddl`
  applied to a throwaway in-memory bank. Status is only ever derived from **schema shape** plus
  version skew; nothing else in the report can change the exit code.
- Constructor deps are exactly three: `ISqliteConnectionFactory`, `IEncryptionKeyResolver`,
  `ILogger<DoctorCommands>` (`:17`).

### The two existing "extra state" reads — the pattern to mirror

| Read | Source | Guarding |
|---|---|---|
| code engine | `ReadCodeEngineStateAsync` `:108-128` — `settings[embedding.codeModel]`, model name from the model dir's manifest (`ModelNameFor` `:157-168`), pending rows (`CountPendingCodeRowsAsync` `:170-180`) | `TableExistsAsync` before every table touch (`:149-153`) **and** `catch (SqliteException)` → all-null state. Explicit rationale at `:111-113`: "this extra read must never be what decides the exit code" |
| threads | `ReadEmbeddingThreadsStateAsync` `:131-147` — `settings[embedding.threads]` through `EmbeddingService.ResolveThreadCountForDisplay` | same shape; falls back to the unset resolution on `SqliteException` |

Two facts follow for any new line: (a) it reads settings/counts through the same
`TableExistsAsync` + `catch` guard, and (b) it must not touch the exit code.

## 3. The memory side, as it actually exists

### 3.1 Settings keys (`src/AiRaccoon.Infrastructure/Embedding/EmbeddingSettingsKeys.cs`)

| Key | Const | Line | Meaning |
|---|---|---|---|
| `embedding.provider` | `Provider` | 9 | `local` / OpenAI-compatible |
| `embedding.model` | `Model` | 10 | local model **directory or onnx path**, or a remote model name |
| `embedding.baseUrl` | `BaseUrl` | 11 | remote endpoint |
| `embedding.engine` | `Engine` | 14 | engine **fingerprint**; a change is what triggers re-embedding |
| `embedding.apiKey` | `ApiKey` | 17 | persisted OpenAI key (single-channel ruling) |
| `embedding.dimensions` | `Dimensions` | 21 | remote engines' declared output dim (sqlite-vec infers none) |
| `embedding.threads` | `Threads` | 35 | ORT intra-op cap — **shared by both corpora**, already reported once |

Code-side twins for contrast: `CodeModel` (25), `CodeEngine` (28), `CodeDimensions` (31).

### 3.2 Pending rows

- `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs:362-363` — `CountPendingEmbed` =
  `SELECT COUNT(*) FROM entries WHERE embed_state = 'pending'` (bank-wide). Existing caller:
  `PendingEmbedJob.CountOutstandingRowsAsync` (#477). Exact structural twin of
  `CountPendingCodeEmbed` (`:419-420`), which `doctor` already uses.
- **Asymmetry, load-bearing:** `entries` has `embed_state` but **no `embed_attempts`**
  (`MemorySchema.cs:102` — column list ends at `structure_embedding`), whereas `code_entries`
  carries `embed_attempts` with a `MaxEmbedAttempts` quarantine (`MemorySql.cs:407-413`, `:445-446`).
  So a memory row that always fails to embed stays `pending` and retried forever; there is no
  poisoned-row subset to explain in the report. `CountPendingCodeEmbed`'s own remark
  (`MemorySql.cs:415-420`) says doctor deliberately reports the literal count including poison rows —
  the memory count needs no such caveat.

### 3.3 The memory engine's "not configured" state — CORRECTED 2026-08-26

> **Correction (grounded-feedback).** This section originally claimed *"there is no 'not configured'
> state for the memory engine — it is always resolved"*. That is **wrong**, and two planning lanes
> caught it independently (P1 §0.1, P2 §0.1). The `model ?? "bundled"` fallback cited below is a
> **model** fallback *inside* an already-configured provider; the corpus's configured-ness is
> `embedding.provider`, and **nothing seeds that row**. Its only writers are
> `EntryEmbedder.StartMigrationAsync` (`EntryEmbedder.cs:38,54`), reached only from
> `ai-raccoon model embedding set local|openai` (`SettingsCommands.cs:119,158`). With the row absent,
> memory embedding is skipped outright — `EmbedIfConfiguredAsync` returns early
> (`EntryEmbedder.cs:159-164`), `EmbedQueryAsync` returns `QueryVector.Empty` (`:238-241`),
> `FileIngestor` gates on the same key (`FileIngestor.cs:339`) — and the CLI already has a name for
> the state: `model embedding show` prints `provider: (none — FTS5-only search)`
> (`SettingsCommands.cs:314-317`).
>
> **Why it matters:** the memory corpus's degraded state is *structurally identical* to the code
> corpus's (absent key → "not configured"), same predicate, different settings key. So it is a
> **parameter of the shared component**, not a divergence — which strengthens the owner's extraction
> ruling instead of complicating it. The paragraphs below are kept as written, with the incorrect
> conclusion struck, so the mistake and its correction stay auditable.

The bundled-model facts below are accurate; only the conclusion drawn from them was wrong.

- `EntryEmbedder.StartMigrationAsync` returns `new EmbeddingConfig(provider, model ?? BundledModel, engine)`
  where `BundledModel = "bundled"` (`src/AiRaccoon.Infrastructure/Embedding/EntryEmbedder.cs:26,47,90`).
- `src/AiRaccoon.Infrastructure/Embedding/BundledModel.cs:67` — resolution order is the
  `embedding.model` settings path when set, **else the bundled copy next to the tool**.
- `src/AiRaccoon/Setup/Models/EmbeddingAvailability.cs:20-29,34-38` — startup best-effort check;
  when bundled assets are missing it logs a warning naming
  `ai-raccoon model embedding set local` and never fails boot.
- `EmbeddingService.cs:364,430` — the two runtime failure messages (missing local model; missing
  OpenAI key) name their own remedy commands.

~~Consequence: the code engine's headline state ("absent, and the corpus is inert but legitimate",
`DoctorCommands.cs:101-107`, #422) **has no memory analogue**. The memory engine is always resolved;
its real failure modes are *assets missing under a configured path*, *remote engine with no API key*,
and *a migration owed*. A memory line copied verbatim from the code line would report a state that
cannot exist.~~ **Struck — see the correction at the head of §3.3.** The code engine's "not configured"
state *does* have a memory analogue (`embedding.provider` absent → FTS5-only search); the additional
failure modes named here are real but are not the headline state.

### 3.4 The migration outbox — the state doctor is silent about, and it blocks everything

- `src/AiRaccoon.Core/Memory/ModelMigration.cs:9-18` — outbox record (ADR-0076); `IsOpen` ⇔
  `FinishedAt is null`.
- `MemorySql.cs:470-475` — `SelectModelMigration` (provider, model, base_url, engine, started_at,
  finished_at from `model_migration WHERE id = 1`) and `HasOpenModelMigration`.
- `MemorySql.cs:481-504` — start/finish/lease statements; `lease_owner`/`lease_expires_at` exist on
  the row (the relay's lease).
- `EntryEmbedder.StartMigrationAsync:50-91` — the outbox transaction: settings + migration row +
  `MarkAllEmbeddedPending` (`:398-399`) commit together, so **an open migration and a large pending
  count are the same event**.
- `src/AiRaccoon/Tools/ToolGate.cs`, `ToolRefusals.cs` — while open, MCP tool calls are refused.
  **Observed live in this session**: `memory_search` returned
  `model-migration-in-progress: ai-raccoon: a model migration is in progress; try again once it finishes`.
- `src/AiRaccoon.Infrastructure/Maintenance/ModelMigrationJob.cs` — the relay that drains it.

An operator whose memory searches are being refused gets nothing from `doctor` today. This is the
highest-value finding in this record.

### 3.5 Dimensions / vec reconciliation

`embedding.dimensions` (remote) and the `IVecDimensionReconciler` seam (injected into
`EntryEmbedder`, `EntryEmbedder.cs:21`; ADR-0093 generalized it for `vec_code`) mean the vec table's
dimension can be reconciled at drain time. **Hypothesis (unverified):** doctor could read the live
`vec_entries` dimension and compare it with the configured engine's. Not yet traced to a read-only
query — the plan must verify before promising it.

## 4. Surfaces that must move together

| Surface | Where | What it says today |
|---|---|---|
| how-to sample output | `docs/how-to/configure-ai-raccoon-server.md:318-337` | **already stale**: sample healthy block shows 3 lines and `user_version: 10`; the binary prints 6 lines at v11 |
| embedding how-to | `docs/how-to/configure-embedding-engines.md:145` | lists `doctor` among the surfaces quoting the code-engine remedy string |
| reference | `docs/reference/agent-memory-server.md:201` | ties `doctor`'s wording to `CodeEngineSetup.DefaultModelCommand` |
| event ids | `docs/reference/logging-event-ids.md:83` | `DoctorCommands` owns 1000-1001 — a new failure log would take 1002 |
| README | `README.md:40` | "`doctor` shows the effective thread count" (1.33.0) |
| tests | `tests/AiRaccoon.Tests/Unit/Setup/DoctorCommandsTests.cs` | 11 tests, argv-driven (`CliRun`), `Integration`+`Slow`, `RetryFact`, includes two byte-hash "never writes" assertions |
| release checklist convention | `docs/work/checklist/*.json` (e.g. `2026-08-23-1.33.0-release.json:172-190`) | each doctor claim is a manual live-bank item with `expected-result`/`evidence`/`observed-result` |

Note the checklist file quotes `ai-raccoon model set code default` while the code const is
`ai-raccoon model code set default` (`CodeEngineSetup.cs:15`) — stale checklist text, out of scope
here but a reminder that quoted command strings drift.

## 5. Decisions the plan must make (open questions)

1. **Line set and wording.** Which of {provider, model, engine fingerprint, dimensions, pending
   rows, migration state, asset presence, API-key presence} earn a line, and in what order, given the
   existing lines' grammar (`<subject>: <value> (<qualifier>)`).
2. **Exit code.** Does an open migration (or a missing local asset) stay report-only, or does it
   deserve a distinct non-zero code? Everything in `DoctorCommands` today says schema-shape only.
3. **Dependency budget.** Reporting bundled-asset presence needs `IBundledModel`; reporting the
   resolved model name for a local dir may reuse `ModelNameFor` (`:157-168`). Does doctor grow a
   fourth constructor dep, and is that worth it?
4. **Symmetry vs honesty.** Where the code line's shape does not fit (§3.3), does the memory line
   diverge deliberately, and is that divergence documented?
5. **Simpler shape** (invariant `ask-if-simpler`): is the minimum viable change two lines —
   `memory engine: …` and `memory rows pending: N` — with migration folded into the engine line?
6. **Doc drift** — fixing the stale sample block in §4 is in scope of whatever changes the output.

---

## 6. Live evidence from the owner's real bank (2026-08-26, read-only)

`ai-raccoon` on PATH is `/Users/arasz/.dotnet/tools/ai-raccoon`. Ran `ai-raccoon doctor` (safe: read-only)
and a `sqlite3 mode=ro` probe of `~/.ai-raccoon/memory.db`. Results:

```
doctor  -> the 8 lines quoted in §1, exit 0, "status: HEALTHY"

model_migration (id=1):
  provider      local
  model         /Users/arasz/.ai-raccoon/models/Salesforce__SFR-Embedding-Code-400M_R
  engine        local:/Users/arasz/.ai-raccoon/models/Salesforce__SFR-Embedding-Code-400M_R#a3df8a4c…
  started_at    1787739481
  finished_at   NULL            <- OPEN
  lease_owner   MacBook-Air-Arasz:92861:6792f8b0c93a47b3961c9a8e2688f7c6
  lease_expires_at 1787740592   <- in the past: no live drainer

entries: 51,947 total, 47,723 embed_state='pending'   (92%)
settings: embedding.provider=local, embedding.model=<that dir>,
          embedding.engine=local:…#a3df8a4c…, embedding.codeModel=<same dir>,
          embedding.codeEngine=local:…#a3df8a4c…, embedding.codeDimensions=1024
          (no embedding.dimensions row — normal for a local engine)
```

**The defect, stated exactly:** on a bank where 92% of the memory corpus is unembedded, a model
migration has been open since `started_at` with an expired lease, and MCP `memory_search` calls are
being refused with `model-migration-in-progress` (`ToolGate.cs:25-29`, observed live in this session),
`doctor` prints **`status: HEALTHY`** and exits **0**. Every fact needed to say otherwise is one
read-only query away in the same bank doctor already has open.

Also observed: `embedding.model` and `embedding.codeModel` point at the **same** directory
(`Salesforce__SFR-Embedding-Code-400M_R`), i.e. a code-tuned model is serving the memory corpus. A
report that prints both engines makes that visible; today nothing does.

## 7. Owner corrections that bind the plan

**f: (1) — no duplication; extract the common component.** The plan must not add a memory-shaped copy
of the code-engine read/report path. It must extract one corpus-parameterised component both corpora
flow through. This is `derive-or-delete-the-list` applied to code: two hand-maintained copies of the
same read+format logic drift the moment one side gains a field.

Precedent already in the codebase — the drain path is *already* built that way:

- `EmbedCorpus` (`Memory` | `Code`) is the existing corpus dimension; `EmbedDrainSignal.cs:14-15` maps
  Core's `CorpusKind` onto it.
- `EmbedDrainService.DrainOnceAsync` (`src/AiRaccoon.Infrastructure/Embedding/EmbedDrainService.cs:107-154`)
  is one pass implementation parameterised by corpus (`:118-120`), with corpus-tagged logs
  (`:195-215`: 1002 started, 1003 finished + `{Rows}`, 1005 failed) and corpus-tagged metrics
  (`RecordDrainMetrics`, `:161-169`).

So the extraction the owner is asking for has a working model inside the same solution; doctor should
adopt that shape rather than invent one.

**f: (2) — "there are embed logs for code path only?"** Owner-supplied server log:

```
EmbedDrainService[1003]      Embed drain pass finished for Code: 2 row(s)
MaintenanceJobRunner[525]    maintenance job 'finish an interrupted embedding-model migration' (model-migration) ran in 1 ms
MaintenanceJobRunner[525]    maintenance job 'embed rows left pending by a write, …' (pending-embed) ran in 0 ms
```

Traced to source:

- The Memory corpus **does** get 1002/1003/1005 — but only when its drain runs through
  `EmbedDrainService`, i.e. when signalled by `PendingEmbedJob.RunAsync:50` or `FileIngestor.cs:188`.
- The **migration relay does not use that path at all**. `ModelMigrationJob.RunAsync:36-43` calls
  `IEntryEmbedder.DrainMigrationAsync` and **discards its bool**. `EntryEmbedder.DrainMigrationAsync`
  (`:94-141`): if `migrationLease.TryAcquireAsync` fails it returns **silently** (`:102-105`); when it
  does acquire, it drains the entire backlog in an unbounded `while (true)` loop (`:118-130`) with **no
  start log, no progress log, no finish log, no row count, and no metrics**, then closes the outbox row.
- `MaintenanceJobRunner`'s `ran in 1 ms` is therefore the *only* trace — and 1 ms on a 47,723-row
  backlog means the pass did nothing (lease contention), which is precisely the case that logs nothing.

So the answer to the owner's question is **both**: the Memory lines exist but never fired, *and* the
migration path that actually owns those 47,723 rows has no logging of its own. Lane P4 owns this.

## 8. Facts for the integration step

- Next free `ExitCode` value is **24** (`src/AiRaccoon/ExitCode.cs`: 23 = `SettingsServerError`, and
  `Success = 0`; 8 is deliberately retired, 15/16 already reused out of order).
- `model_migration` DDL (`MemorySchema.cs:399-409`): `started_at INTEGER NOT NULL`,
  `finished_at INTEGER NULL`, `lease_owner TEXT NULL`, `lease_expires_at INTEGER NULL` — unix seconds.
- Other outbox tables exist with the same open/closed shape — `repair_requests` (`:417-421`) and
  `promotion_queue_prune_requests` (`:428-432`). Out of scope here, but a shared "open outbox" reporter
  would cover them; say so in the plan rather than building it.
- CI test lanes: `dotnet test --project tests/AiRaccoon.Tests --filter "Speed=Fast&Performance!=Benchmark"`,
  `--filter "Category=bdd"`, `--filter "Speed=Slow&Performance!=Benchmark"` (`.github/workflows/build.yml:134,168,221`).
  `DoctorCommandsTests` is `Integration`+`Slow`, so it runs in the slow lane.

