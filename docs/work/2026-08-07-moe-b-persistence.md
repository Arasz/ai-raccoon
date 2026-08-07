# MoE panel — Expert B: persistence & the SQLite data layer

Date: 2026-08-07. Scope: `src/AiRaccoon.Infrastructure/Sqlite/**`, `Maintenance/`,
`Sync/`, and the `Embedding/`+`Chunking/` seams that feed the store. Read-only review.

## Verdict

The retrieval half of this data layer is the strongest part of the codebase: SQL is
parameterised everywhere that matters, the FTS5 expression builder is genuinely
injection-proof by construction, and the hybrid-search implementation matches ADR-0005
and ADR-0006 closely enough that the measured sweep numbers still describe the shipped
code. The durability half has not had the same attention. There is no schema-version
marker, so `MemorySchema` is a create-if-absent DDL blob plus four ad-hoc `ALTER TABLE`s
that can add columns but can never change a constraint — an old bank silently keeps a
different `entries` shape forever, and `architecture.md`'s claim that the workspace/scope
CHECK is "enforced at the schema level" is false for such a bank. That whole DDL blob,
plus two full `COUNT(*)` scans, runs on *every single connection open*, and every store
method opens a connection. Multi-statement writes are almost never transactional (three
raw `BEGIN IMMEDIATE` strings in the entire Infrastructure project; `BeginTransaction` is
never used), so delete-plus-tombstone and the sync merge can both tear. Two sync findings
are worse than the persistence ones: the settings table — which holds `sync.secretKey`,
`sync.connectionString` and `embedding.apiKey` — is uploaded to the object store
unstripped and is then *unconditionally* overwritten from the remote copy on every merge.
And the data layer has no logging at all: `SqliteMemoryStore` (1227 LOC) takes no
`ILogger`, a failed keyword modality is swallowed silently, and a destructive dedupe
migration fails into an empty `catch`. `SqliteMemoryStore` is a god class, but the useful
cut is not read-vs-write — it is settings, embedding orchestration and file ingest, three
concerns that are not persistence at all and that carry most of the class's tangle.

## Findings

| ID | file:line | severity | what's wrong | why it matters |
|---|---|---|---|---|
| B1 | `src/AiRaccoon.Infrastructure/Sync/SyncService.cs:62-73`, `:87`; keys at `Sync/SyncSettingsKeys.cs:17-27`; `Sqlite/SqliteMemoryStore.cs:36-37` | **Critical** | The pre-push strip is entries-only (`DELETE FROM entries WHERE workspace_id IS NOT NULL`). Nothing removes the `settings` table, which holds `sync.accessKey`, `sync.secretKey`, `sync.connectionString` (Azure) and `embedding.apiKey`. The whole bank is then uploaded. | On an **unencrypted** install the snapshot on the object store is plaintext SQLite containing the very credentials that grant access to that bucket, plus the OpenAI key. Encrypted installs are safe — `docs/work/2026-08-06-sqlite3mc-feature-surface.md` F9 measured that `VACUUM INTO` snapshots of a keyed bank are ciphertext — but encryption is optional (`NoneEncryptionKeyProvider`), so the safe case is a configuration, not a guarantee. |
| B2 | `Sync/SyncService.cs:275-284`; schema at `Sqlite/MemorySchema.cs:52-55` | **High** | Comment says "Merge settings: updated_at LWW", but `settings` has only `(key, value)` — there is no `updated_at`. The SQL is `ON CONFLICT(key) DO UPDATE SET value = excluded.value`: remote unconditionally wins. | Every sync overwrites local settings with whatever the remote snapshot last carried. A machine's own `sync.secretKey`, `embedding.apiKey` or `maintenance.*` values are silently replaced by another machine's — combined with B1 this actively propagates one install's credentials to every replica. This is the one merge step that is not idempotent and not recoverable by re-syncing. No test covers it. |
| B3 | `Sqlite/MemorySchema.cs:15-171` (DDL), `:186-227` (Migrate); no `user_version` anywhere in `src/` | **High** | There is no schema-version marker. Evolution is `CREATE … IF NOT EXISTS` plus four `if (!columns.Contains(x)) ALTER TABLE ADD COLUMN`. `IF NOT EXISTS` is a no-op on an existing table, so constraints, CHECKs, FKs and PK definitions can never change. | Concrete failure mode: a bank created before the `entries` CHECK at `:49` and the `workspaces` FK at `:48` were added keeps the old table shape permanently, and nothing detects it. `docs/explanation/architecture.md:79-81` states the CHECK "enforces the mutual exclusion at the schema level" — untrue for such a bank, and there is no way to ask a bank which shape it has. Any future change that is not a nullable column addition has no delivery mechanism at all. |
| B4 | `Sqlite/SqliteConnectionFactory.cs:52` → `MemorySchema.cs:173-179`, `:241-250`, `:322-331` | **High** | `MemorySchema.EnsureAsync` runs unconditionally on **every** `OpenBankAsync`: the full ~155-line DDL batch, a `pragma_table_info` query, a `sqlite_master` lookup, **two full `COUNT(*)` scans** (`entries_fts` and `entries`), and two more `sqlite_master` probes. Every public method of `SqliteMemoryStore`, `SqliteWorkspaceStore` and `SqlitePromotionQueueStore` opens a connection. | Per-open cost scales with corpus size, on a bank measured at ~10,572 `entries` rows (`docs/work/2026-08-06-sqlanalyze-usefulness.md` F2) and with ~7 processes holding it open concurrently. Magnitude **UNVERIFIED** — no benchmark exists (`benchmarks/` has only `EmbeddingLatencyBenchmark` and `SearchValuesVsHashSetBenchmark`); an `EXPLAIN QUERY PLAN` plus a per-open timing harness would settle it. The shape is certain and a `PRAGMA user_version` gate (which B3 needs anyway) removes it entirely. |
| B5 | `Sqlite/MemorySchema.cs:253-313` (rethrows at `:311`), contrast the stated invariant at `:320-321` | **High** | The FTS-rebuild branch opens `BEGIN IMMEDIATE` and **rethrows** on failure, so `OpenBankAsync` throws and the bank fails to open. The dedupe branch below it deliberately swallows, with the comment "a bank must never fail to open". Two branches, opposite policies, in the same method. | With multiple processes on one bank (memory records ~7), a concurrent open contends for the write lock; after `busy_timeout=5000` (`SqliteConnectionFactory.cs:142`) the `BEGIN IMMEDIATE` raises `SQLITE_BUSY`, which propagates and fails the open. Worse, the rebuild is gated on `ftsRows != entryRows` (`:241-250`): if those counts ever diverge for any reason, *every* connection open triggers a full DROP/CREATE/repopulate of the index under a write lock — a permanent cliff, not a one-off. |
| B6 | `Sqlite/MemorySchema.cs:346-373` (destructive `DELETE`), `:386-389` (empty `catch`) | **High** | A migration that permanently deletes duplicate `entries` rows fails into a completely empty `catch { }` with no logging, no counter, no marker. `MemorySchema` is a static class with no `ILogger`. | This is the project's "every automated multi-step action stays observable" invariant inverted: the one migration that destroys user data is the one whose failure is invisible. An operator cannot tell whether a bank is deduped, partially deduped, or has been failing this step on every open for weeks. |
| B7 | `Sqlite/SqliteMemoryStore.cs:19-24` (no `ILogger`), `:725-728`; `MemorySchema.cs`, `SqliteConnectionFactory.cs` (no logging at all) | **High** | The entire `Sqlite/` namespace has zero logging — no `ILogger`, no nested `Log` class, no `[LoggerMessage]`. `QueryFtsBatchAsync` catches **every** `SqliteException` and returns an empty list. | A corrupt FTS index, a `SQLITE_BUSY`, or a tokenizer blowup all present identically as "the keyword modality found nothing", degrading search quality with no trace. `BankMaintenanceHostedService.cs:257-282` shows the project knows the right pattern (nested `static partial class Log`, `[LoggerMessage]`, EventIds 510-516) — the data layer simply never got it. |
| B8 | `src/AiRaccoon.Core/Encryption/SshKeyDerivation.cs:19-28`, called from `Sqlite/Encryption/Providers/BitwardenEncryptionKeyProvider.cs:32-36` | **High** | `DeriveRawKey` hand-composes `SHA-256(Label ‖ seed)` and formats it as the SQLCipher raw key. This is application-authored key derivation, in the domain layer. | Direct hit on "No hand-rolled crypto or security orchestration — key derivation … delegate to an audited, platform-provided library … even when the primitives themselves are sound." Adversarially: the input is a full-entropy 32-byte ed25519 seed, so this behaves like HKDF-Expand and is not practically weak — the breach is categorical, and the BCL already ships the intended primitive (`HKDF.DeriveKey`). Recorded as a deliberate owner decision (`docs/work/2026-08-05-db-passphrase-options.md:287-295`), so it is an owner question, not a unilateral fix. **Changing it changes every derived key and invalidates every existing encrypted bank** — the file's own comment says so — so any fix needs a rekey migration. |
| B9 | `Embedding/EmbeddingService.cs:60-70`, `CreateOpenAi`; schema at `Sqlite/MemorySchema.cs:67`, `:72`; `Embedding/EmbeddingMath.cs:10` | **High** | `vec_entries`/`vec_structure` are hardcoded `vec0(embedding float[384])` and `EmbeddingMath.Dimension` is `const int = 384`, but `ConfigureEmbeddingAsync` accepts any `openai` model and any custom local ONNX path with **no dimension validation anywhere** (grep of `src/AiRaccoon.Core/` and `src/AiRaccoon/Setup/` finds none). | Configuring e.g. `text-embedding-3-small` (1536-d) makes `MarkEmbedded` write a 6144-byte blob; the `vec_entries_au` trigger (`MemorySchema.cs:99-104`) then rejects it and the `UPDATE` throws out through `EmbedIfConfiguredAsync` (`SqliteMemoryStore.cs:802-804`, no catch) — **every subsequent write fails**. And `ConfigureEmbeddingAsync` persists the new engine settings at `:468-479` *before* attempting the re-embed at `:486-489`, so the failure leaves the bank configured with an engine it cannot use. The schema comment at `MemorySchema.cs:65-66` ("the embedder owns the embedding dimension if the model is not all-MiniLM") describes a design that was never implemented. |
| B10 | `Sqlite/SqliteMemoryStore.cs:298-319`; `Sqlite/SqlitePromotionQueueStore.cs:17-37`; `SqliteMemoryStore.cs:846-858`, `:914-974`. Only 3 transactions exist in all of `src/` (`MemorySchema.cs:260`, `:337`, `SqliteMemoryStore.cs:357`); `BeginTransaction` is never used. | **High** | Multi-statement writes are not atomic. `DeleteAsync` does `SELECT scope` → `DELETE` → `INSERT tombstone` as three autocommits. `SqlitePromotionQueueStore.UpsertAsync` loops N rows as N autocommits. `EmbedBatchAsync` and `InsertChunksAsync` do the same per row/chunk. | A crash between the `DELETE` and the tombstone leaves the row locally gone but with no tombstone, so the next sync **resurrects it from the cloud** — a silent undelete, which is exactly the failure the tombstone design exists to prevent. Separately, N autocommits per batch is N WAL commits: this is the cheapest available write-throughput fix and the lowest-risk item in this report. Transactions are also written as raw `BEGIN IMMEDIATE`/`COMMIT` strings rather than `SqliteTransaction`, so the ADO layer never knows a transaction is open. |
| B11 | `Sqlite/SqliteMemoryStore.cs:972` (per-chunk embed) vs `:836-861` (batched path, unused here); `:789-791` re-reads settings per call | Medium | The live ingest path embeds **one chunk at a time**: `InsertChunksAsync` calls `EmbedIfConfiguredAsync` inside its `foreach`, and each call re-reads 4 settings rows and invokes `GenerateAsync` with a batch of one. The correctly batched `EmbedBatchAsync` (batch 32) is wired only to `EmbedPendingAsync`. | A 60-chunk file does 60 ONNX runs or 60 OpenAI round trips instead of two batches, plus ~240 redundant settings SELECTs. Latency impact **UNVERIFIED** — no timing exists in `docs/` or `benchmarks/` for either path. No test asserts request count on the ingest path, so a fix would also need the missing regression pin. |
| B12 | `Sqlite/SqliteMemoryStore.cs:980-1001`; `Sqlite/MemorySql.cs:274-284` | Medium | `BumpAccessAsync` runs one `SELECT` + one `UPDATE` per distinct result hash on the **search** path — 40 statements for a limit-20 search, none transactional. Two correctness wrinkles: `SelectRatingForBump` reads with `LIMIT 1` while `BumpAccess` updates `WHERE hash = @hash` with **no project scoping**, so one arbitrary row's `created_at`/`access_count` computes a rating written to every row sharing that hash across all projects. | Search is a write operation that takes the WAL write lock 20× per call; under multi-process contention searches serialise on each other. The unscoped update is narrow in practice (path-scoped hashes rarely collide across projects) but is a genuine cross-project data write from a read API. |
| B13 | `Sqlite/SqliteMemoryStore.cs:243-245` (`ShareAsync` propagates `source.SourceFile`) vs `docs/adr/0005-source-affinity-ranking.md:69` | Medium | ADR-0005 states "Shared-scope rows have no `source_file` and participate as singletons". They do have one: `ShareAsync` copies `source_file` and `section` onto the promoted shared row. | The `ChunkIndex`/`TotalChunks` window functions (`MemorySql.cs:114-117`, `:140-143`, `:159-162`) are computed **per context batch**, so the same `source_file` gets independent index sequences in the shared and project batches. `SourceAffinityRanker` then groups by `source_file` across the merged batches (`SourceAffinityRanker.cs:24-27`, `:77`), so a shared chunk at index 0 can register as an "adjacent sibling" of an unrelated project chunk at index 1 and boost or consolidate it. The ADR's stated premise no longer holds; the ranking consequence is **UNVERIFIED** (no test or sweep covers a shared row carrying `source_file`) and a single sweep re-run over a corpus with shared promotions would settle it. |
| B14 | `Sqlite/MemorySql.cs:110-129` | Medium | The FTS query's `candidates` CTE is `MATERIALIZED` and computes `ROW_NUMBER()`/`COUNT()` window functions over **every row matching the context filter**, before the FTS join and before `LIMIT @limit`. Run once per context, twice when the OR fallback fires. | The `MATERIALIZED` hint is the right fix for the O(n²) inlined-subquery problem the comment describes, but the resulting shape is still O(corpus) per search per context. Magnitude **UNVERIFIED** — no search-latency benchmark exists; `EXPLAIN QUERY PLAN` plus a timing harness at the measured ~10.5k-row corpus size would settle whether this matters today or only at 10×. |
| B15 | `Sqlite/SqliteMemoryStore.cs:486-489` (`SelectAllEmbedded`), `:254-257` + `MemorySql.cs:33-40` (no LIMIT), `:276-281`; `Sync/SyncService.cs:87`, `:113`, `:167` | Medium | Several paths materialise the whole corpus in memory: `ConfigureEmbeddingAsync` loads every embedded row's `value` bank-wide into a `List`; `ExtractCandidatesAsync` and `GetSharedIndexAsync` have no `LIMIT`; sync does `File.ReadAllBytesAsync` of the entire bank (29 MB measured) up to three times per cycle on the retry path. | Unbounded working set that grows with the bank. `ConfigureEmbeddingAsync` additionally runs the entire re-embed inline in one MCP call with no progress, no resumability and no transaction — and per B9 it can fail partway with settings already committed. |
| B16 | `Sync/SyncService.cs:255-347` | Medium | `MergeRemoteAsync` is seven sequential autocommitting statements with no wrapping transaction. | A crash mid-merge leaves a partially merged bank. Most steps are idempotent (`INSERT OR IGNORE`, watermark GC) and self-heal on re-sync — except the settings overwrite (B2), which is one-way. No test simulates a partial merge. |
| B17 | `Sync/CloudSyncConnection.cs`, `ICloudSyncConnection.cs`, `CloudSyncConnectionFactory.cs` (whole files) | Medium | Dead code: no DI registration in `Dependencies.cs`, no callers outside their own three files, no tests. A leftover from the abandoned `cloudsync_*` extension design superseded by `SyncService`'s row merge (`docs/work/2026-08-06-sqlite3-rsync-evaluation.md` F6). | Presents a second, non-existent sync mechanism to any reader tracing durability. |
| B18 | `src/AiRaccoon/Setup/Cli/Commands/EncryptionCommands.cs:20-21`, mirrored in 3 test files | Medium | Real production Bitwarden `projectId`/`secretId` GUIDs are hardcoded as prompt defaults in tracked source — confirmed as the owner's actual identifiers via `docs/work/2026-08-05-db-passphrase-options.md:290`. | Not a credential (a `BWS_ACCESS_TOKEN` is still required), so not a literal breach of "no hardcoded secrets", but it publishes exactly which vault item protects the bank key. Distinguish from the test token `"test-bws-token-0123"`, which is correctly fake. |
| B19 | `Sqlite/SqliteConnectionFactory.cs:16-21` | Medium | A static constructor mutates Dapper's process-global `DefaultTypeMap.MatchNamesWithUnderscores`, and only runs when the type is first touched. | Any Dapper use in the process before `SqliteConnectionFactory` is first referenced gets the wrong column mapping, and the mutation affects every other Dapper consumer including parallel test fixtures. It is invisible global state set from a place nobody reads. |
| B20 | `docs/explanation/architecture.md:169-170`, `:259-264`, `:457`, `:506` | Low | Every `file:line` evidence citation for `SqliteMemoryStore.cs` is stale: `:41-93` for write (actually 39-110), `:95-152` for search (actually 112-221), `:652-716` for chunking (actually 896-977), `:718-740` for bump (actually 980-1001). | The doc's whole trust model is "cite the source"; citations that point at the wrong code invert it. Cheap to regenerate, and worth pinning with a check since the invariant is "check the source, not your own reasoning". |
| B21 | `Sync/SyncService.cs:52` + `:58`, `:102` + `:109`, `:156` + `:163` | Low | `Path.GetTempFileName()` creates a zero-byte file, and `VACUUM INTO` then targets it. **Measured**: stock `sqlite3` 3.51.0 rejects this with `Error: stepping, output file already exists`; the app's bundled SQLite3MC accepts it (`dotnet test --filter SyncServiceTests` → 10/10 passed). | The whole sync path depends on the bundled build tolerating a pre-created target where the stock CLI does not. Mechanism of the divergence is **UNVERIFIED**. If a future SQLite3MC bump aligns with stock behaviour, every sync breaks at the first statement. A `File.Delete(localSnapshot)` before each `VACUUM INTO` removes the dependence in one line. |
| B22 | `Sqlite/SqliteMemoryStore.cs:1007-1045`, used via `.Replace("{filter}", filter)` at `:337`, `:598`, `:716`, `:742`, `:748` | Low | SQL is assembled by string replacement of a `{filter}` placeholder. **Verified safe today**: every branch of `FilterFor` returns constant fragments with only `{alias}` interpolated, `alias` is a literal at all three call sites (`"e."`, `""`), and every value goes through a Dapper parameter. | The safety is a comment (`:1004-1006`), not a type. `FilterFor` returns a bare `string`, so one future branch interpolating a value is a Critical injection with no compiler or reviewer signal. A `readonly record struct EntryFilter(string Sql, IReadOnlyDictionary<string,object?> Values)` with a private constructor makes the invariant structural at near-zero cost. |
| B23 | `Sync/SyncService.cs:58`, `:109`, `:163`, `:233`, `:240` | Low | `VACUUM INTO '{path}'` and `ATTACH DATABASE '{path}'` interpolate file paths into SQL unescaped. | Not exploitable — paths come from `Path.GetTempFileName()`, and SQLite does not accept bound parameters in those positions, so interpolation is unavoidable. Genuinely fragile only if `TMPDIR` ever contains a quote. Noted for completeness; the `KEY` fragment beside it is correctly built via SQLite's own `quote()` and is the right pattern. |
| B24 | `Sqlite/Encryption/EncryptionKeyResolver.cs:19`, `:34`; `Providers/NoneEncryptionKeyProvider.cs` | Low | `Resolve()` remaps source `"none"` → `"env"` before provider matching, so `NoneEncryptionKeyProvider.IsForSource` never sees `"none"` through the wired chain. The provider is registered, unreachable and has no direct test. | Dead branch in the key-resolution path — the one place where "which key source am I actually on?" must be unambiguous. |

## Refactor opportunities

Ordered by value. The **serialisation** field is the parallel-execution plan: items that
name the same file must not run concurrently.

**R1 — Schema versioning gate.** *(fixes B3, B4, B5, B6)*
Current: `EnsureAsync` runs the full DDL + 6 probe queries + 2 full counts on every open;
migrations are `if (!columns.Contains(x))` blocks; no bank can report its shape.
Proposed: `PRAGMA user_version` as the version marker; an ordered `IReadOnlyList<Migration>`
each with a version and a body; `EnsureAsync` reads `user_version`, runs only the pending
steps inside one `SqliteTransaction`, bumps the version. Constraint changes become
expressible via the standard rename-copy-drop recipe. Failures log through a nested `Log`
class and set a degraded marker instead of vanishing.
Blast radius: `MemorySchema.cs` (392 LOC, near-total rewrite), `SqliteConnectionFactory.cs:52`,
`tests/AiRaccoon.Tests/Unit/storage/SqliteMemoryStoreSchemaTests.cs` (11 existing facts, all
of which should keep passing — they are a good safety net). Effort: **L**. Risk: **High** —
this touches the open path for every existing bank; needs a fixture bank at each historical
shape before any code changes.
**Must serialise with R5** (both rewrite `MemorySchema.cs`). Independent of everything else.

**R2 — Strip secrets from the sync snapshot, and fix settings merge.** *(fixes B1, B2)*
Current: the whole `settings` table is uploaded and then unconditionally overwritten from
remote. Proposed: (a) add `DELETE FROM settings WHERE key LIKE 'sync.%' OR key = 'embedding.apiKey'`
to the strip step at `SyncService.cs:62-73`, next to the existing workspace strip — same
place, same shape; (b) either give `settings` an `updated_at` column and make the merge a
real LWW, or make it explicitly local-wins and correct the comment. (b) depends on R1 for
the column.
Blast radius: `SyncService.cs` (~15 LOC changed), `MemorySchema.cs:52-55` if (b) takes the
column route, plus new tests in `tests/AiRaccoon.Tests/Unit/sync/`. Effort: **S** for (a),
**M** for (b). Risk: Low for (a) — it only removes rows from an outgoing copy.
**(a) is independently shippable and should go first, alone.** **(b) must serialise
with R1** (adds a column).

**R3 — Transactions on multi-statement writes.** *(fixes B10, B16)*
Current: 3 raw `BEGIN IMMEDIATE` strings; `BeginTransaction` never used; delete+tombstone,
batch upsert, batch embed and per-chunk ingest all autocommit per statement.
Proposed: a small `await using var tx = await connection.BeginTransactionAsync(ct)` around
`DeleteAsync`'s delete+tombstone, `SqlitePromotionQueueStore.UpsertAsync`'s loop,
`EmbedBatchAsync`'s mark loop, `InsertChunksAsync`'s chunk loop, and `MergeRemoteAsync`'s
seven statements; replace the three raw string transactions with the same API.
Blast radius: `SqliteMemoryStore.cs` (~5 sites), `SqlitePromotionQueueStore.cs` (1 site),
`SyncService.cs` (1 site), `MemorySchema.cs` (2 sites). Effort: **M**. Risk: Low-Medium —
the failure mode of getting it wrong is a held write lock, which the existing
`busy_timeout=5000` surfaces loudly rather than silently.
**Must serialise with R1 (`MemorySchema.cs`), R2b (`SyncService.cs`) and R4
(`SqliteMemoryStore.cs`).** This is the highest value-per-risk item in the report; if only
one thing ships, ship the `DeleteAsync` half of it.

**R4 — Carve three non-persistence concerns out of `SqliteMemoryStore`.** *(addresses the
god class; enables B11, B12, B15)*
Current: 1227 LOC, ~25 public methods, spanning entry CRUD, hybrid search, settings
key-value, embedding orchestration, file ingest, stats, TTL and share.
Proposed, in descending order of value — and note that the productive seam is **not**
read-vs-write:
1. `SqliteSettingsStore` — `GetSetting`/`SetSetting`/`GetSettingsByPrefix`/`DeleteSetting`
   (`:618-658`) plus `ConfigureEmbeddingAsync` (`:451-493`). Pure key-value, zero coupling
   to `entries`, and the thing four other subsystems (sync, maintenance, embedding,
   extraction) actually depend on. **Highest value, lowest risk.**
2. `EntryEmbedder` — `EmbedIfConfiguredAsync`/`EmbedRowsAsync`/`EmbedBatchAsync`/
   `ReadEmbeddingSettingsAsync`/`ChunkSizeForAsync` (`:686-894`), taking a connection.
   This is the concern tangled into *both* the write and search paths, and the place B9's
   dimension guard and B11's batching fix belong.
3. `SqliteSourceIngestor` — `IngestFileAsync`/`IngestDirectoryAsync`/`InsertChunksAsync`/
   `IsIndexableFile`/`IsHidden` (`:414-449`, `:896-977`, `:1089-1095`). A persistence class
   calling `File.ReadAllTextAsync` (`:425`) and `Directory.EnumerateFiles` (`:435`) is the
   clearest layering smell in the file.
4. Optionally `SqliteHybridSearch` — `SearchAsync` + the two batch queries + `FilterFor` +
   `CandidateWindowFor` (`:112-221`, `:705-783`, `:1007-1045`). Large and ADR-governed, but
   it is already cohesive and already delegates its algorithms to five small pure classes,
   so this buys less than 1-3.
**Explicitly not worth doing:** a generic read/write (CQRS-shaped) split of the entry
store, or per-table repositories. The row types are shared, the read and write paths use
the same filters and the same bucket logic, and the split would add indirection without
removing a single tangle. That is structure for its own sake.
Blast radius: `SqliteMemoryStore.cs` (1227 → ~450 LOC), 3-4 new files, `Dependencies.cs`
DI wiring, and the `IMemoryStore` port in `AiRaccoon.Core` if the interface splits (Expert A
owns that boundary call — **do not change the port without coordinating**). Effort: **L**
for all four, **M** for items 1-2 alone. Risk: Medium — mechanical, well covered by
`SqliteMemoryStoreTests`/`…HybridSearchTests`/`…ChunkingTests`.
**Must serialise with R3, R6, R7, R8 (all touch `SqliteMemoryStore.cs`).** Recommend
running R4 items 1-2 *after* R3 lands, so the transaction scopes move with their code.

**R5 — Logging for the data layer.** *(fixes B6, B7)*
Current: zero logging in `Sqlite/`. Proposed: nested `static partial class Log` with
`[LoggerMessage]` + explicit EventIds on `SqliteMemoryStore` and `SqliteConnectionFactory`
(copy the shape from `BankMaintenanceHostedService.cs:257-282`, EventIds 510-516 are taken);
`MemorySchema` becomes an injectable `MemorySchemaInitializer` or takes an `ILogger`
parameter, so the dedupe failure and the FTS rebuild are recorded. Narrow the
`catch (SqliteException)` at `:725` and log what it swallowed.
Blast radius: `SqliteMemoryStore.cs`, `SqliteConnectionFactory.cs`, `MemorySchema.cs`,
`Dependencies.cs`. Effort: **M**. Risk: Low.
**Must serialise with R1 and R4** (same three files). Cheapest to fold *into* R1 and R4
rather than run as its own pass.

**R6 — Embedding dimension guard.** *(fixes B9)*
Current: `vec0(float[384])` hardcoded, no validation, failure surfaces as an opaque vec0
error on every write after a bad `model set`. Proposed: `EmbeddingService` exposes the
engine's dimension; `ConfigureEmbeddingAsync` validates it against the bank's vec0 width
(readable from `sqlite_master`) *before* writing any settings row, and rejects a mismatch
with a message naming both numbers. Full multi-dimension support is a separate, larger
question — this is the guard, not the feature.
Blast radius: `EmbeddingService.cs`, `SqliteMemoryStore.cs:451-493`, one new test. Effort:
**S**. Risk: Low. **Must serialise with R4** (`SqliteMemoryStore.cs`); ideally lands as part
of R4 item 2.

**R7 — Batch the ingest embed path.** *(fixes B11)*
Current: per-chunk `EmbedIfConfiguredAsync` inside `InsertChunksAsync`'s loop. Proposed:
collect inserted chunk ids, call the existing `EmbedBatchAsync` once per 32. Add the
missing call-count assertion to `SqliteMemoryStoreChunkingTests` first — there is currently
no test that would catch a regression either way.
Blast radius: `SqliteMemoryStore.cs:896-977` only. Effort: **S**. Risk: Low.
**Must serialise with R4** (same file).

**R8 — Batch/scope `BumpAccessAsync`.** *(fixes B12)*
Current: 2 statements per result hash, unscoped update. Proposed: one `UPDATE … WHERE hash
IN (…)` computing the rating in SQL, or at minimum one transaction; add the project scope
to `MemorySql.BumpAccess`.
Blast radius: `SqliteMemoryStore.cs:980-1001`, `MemorySql.cs:274-284`. Effort: **S**. Risk:
Low-Medium (the rating formula must move to SQL or the batch must pre-read).
**Must serialise with R4 and R3** (same file); independent of `MemorySql.cs` consumers.

**R9 — Delete the dead sync path and stale docs.** *(fixes B17, B20, B24)*
Blast radius: 3 files deleted in `Sync/`, `EncryptionKeyResolver.cs:19` simplified,
`architecture.md` citations regenerated. Effort: **S**. Risk: Very low.
**Independently shippable** — touches no file any other item touches.

**R10 — Make the `{filter}` seam a type.** *(hardens B22)*
Blast radius: `SqliteMemoryStore.cs:1007-1045` + 5 call sites, `MemorySql.cs` unchanged.
Effort: **S**. Risk: Low. **Must serialise with R4** (same file). Low urgency — this is
prophylaxis for a currently-safe construct, not a fix.

**Parallel plan.** Three lanes can run at once with no file overlap:
`R9` (dead code + docs) ‖ `R2a` (sync strip) ‖ `R1+R5-schema-half` (`MemorySchema.cs`,
`SqliteConnectionFactory.cs`). Then a second wave, serialised on `SqliteMemoryStore.cs`:
`R3` → `R4` (items 1-2) → `R6`, `R7`, `R8`, `R10`.

## What is already good

- **FTS5 injection surface is closed by construction, twice over.**
  `FtsQueryNormalizer.BuildPlan` builds terms only from `[\p{L}\p{N}_]+` matches with
  reserved words filtered (`FtsQueryNormalizer.cs:41-46`), `SourcePathQuery` does the same
  with `[\w]+` and quotes reserved tokens (`SourcePathQuery.cs:34-46`), and the resulting
  expression is then passed as a **bound parameter** anyway
  (`SqliteMemoryStore.cs:198`). Belt and braces, and the belt alone would suffice.
- **Every value in every query is parameterised.** Reviewed all of `MemorySql.cs` (339 LOC)
  and all five `{filter}` call sites: no user-controlled value ever reaches SQL text.
- **The retrieval stack matches its ADRs.** `CandidateWindowFor` (`SqliteMemoryStore.cs:677-680`)
  implements ADR-0006's chosen point exactly; `SourceAffinityRanker` implements all three
  ADR-0005 mechanisms including the sibling-visibility floor that the ADR records as
  load-bearing. The two-stage RRF (per-context then across-context) is the pipeline
  ADR-0006 actually swept — not a divergence. B13 is the one premise that has drifted.
- **The algorithms are already decomposed.** `ReciprocalRankFusion`, `SearchResultMerger`,
  `SourceAffinityRanker`, `FtsQueryNormalizer`, `SourcePathQuery`, `SnippetFallback`,
  `LikePattern` are all small, pure, single-purpose and unit-tested. The god-class problem
  is orchestration, not algorithms — which is why R4 is tractable.
- **Concurrent-insert races are handled correctly and deliberately.** `ON CONFLICT DO NOTHING`
  plus a bucket-key re-read instead of `last_insert_rowid` (`SqliteMemoryStore.cs:85-102`,
  `:953-966`) is the right answer for pooled connections, and the reasoning is recorded
  where a maintainer will find it.
- **Embedding is *not* awaited under a write lock.** Verified independently: no explicit
  transaction exists on the write path, so `GenerateAsync` (`:799`) sits between two
  already-committed statements. The most likely hazard in this design is genuinely absent.
- **`BankMaintenanceHostedService` is exemplary** and matches
  `docs/work/2026-08-07-bank-maintenance-design.md` point for point: `TimeProvider`-based,
  its own connection, defer-fast `busy_timeout=250` restored via `finally` on every exit
  path, log-and-continue with distinct EventIds per failure mode, and real seam-based tests.
  It is the template the rest of the data layer should copy.
- **Sync's optimistic concurrency and integrity checks are right.** If-Match CAS with a
  bounded 3-attempt re-pull/re-merge loop, `PRAGMA quick_check` on both the outgoing
  snapshot and every remote snapshot before merge, a typed `SyncCorruptFileException` that
  never replaces the local bank, and tombstone-based delete propagation — all covered by
  named tests.
- **Workspace isolation is enforced structurally**, not by convention: workspace rows are
  stripped from every outgoing snapshot and excluded from every merge query, with dedicated
  tests. (The same discipline applied to `settings` would close B1.)
- **The encryption plumbing itself is correct.** No key material in any log or exception
  (exhaustively grepped), `ProcessStartInfo.ArgumentList` rather than a concatenated
  argument string in `BitwardenCliSecretManager.cs:36-45`, the sidecar persists only a
  non-secret source descriptor at 0600 and is untracked, and wrong-key failure is
  two-staged into distinguishable exit codes. B8 is about *where the key comes from*, not
  about how it is handled.
- **`MemorySql` as a constants holder is the right call.** A query-builder abstraction here
  would be over-engineering with no caller asking for it.

## Durable project facts

- **There is no schema version marker anywhere in the codebase.** `PRAGMA user_version` is
  never read or written (`grep user_version src/` → nothing). Bank evolution is
  `CREATE … IF NOT EXISTS` plus four `ALTER TABLE ADD COLUMN` probes in
  `MemorySchema.MigrateAsync` (`src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:186-227`).
  Consequence: constraints, CHECKs and FKs can never be changed on an existing bank, and no
  bank can report which shape it has.
- **`MemorySchema.EnsureAsync` runs on every single connection open**
  (`src/AiRaccoon.Infrastructure/Sqlite/SqliteConnectionFactory.cs:52`), and every store
  method opens a connection. That includes two full `COUNT(*)` scans used as an FTS
  drift check (`MemorySchema.cs:241-250`).
- **Only three transactions exist in all of `src/`** — raw `BEGIN IMMEDIATE` strings at
  `MemorySchema.cs:260`, `MemorySchema.cs:337` and `SqliteMemoryStore.cs:357`.
  `SqliteConnection.BeginTransaction` is never called. Everything else autocommits per
  statement.
- **The `settings` table is the project's only runtime config channel** and holds secrets:
  `sync.accessKey`, `sync.secretKey`, `sync.connectionString`, `embedding.apiKey`
  (`src/AiRaccoon.Infrastructure/Sync/SyncSettingsKeys.cs:17-27`;
  `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs:36-37`). It is synced wholesale
  and merged remote-wins.
- **There are two different embedding code paths with different batching shapes in the same
  class**: `EmbedIfConfiguredAsync` (per-row, used by `WriteAsync` and `InsertChunksAsync`)
  and `EmbedBatchAsync` (batch-of-32, used only by `EmbedPendingAsync`). Any "why is ingest
  slow" investigation starts at `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs:972`
  versus `:849`.
- **The vec0 tables are hardcoded to 384 dimensions with no validation anywhere**
  (`MemorySchema.cs:67`, `:72`; `Embedding/EmbeddingMath.cs:10`), while
  `ConfigureEmbeddingAsync` accepts any OpenAI model or custom ONNX path. The schema comment
  claiming the embedder owns the dimension describes an unimplemented design.
- **`SqliteMemoryStore`, `MemorySchema` and `SqliteConnectionFactory` contain no logging at
  all** — no `ILogger`, no `[LoggerMessage]`. `BankMaintenanceHostedService.cs:257-282` is
  the only place in the persistence area that follows the project's logging invariant, and
  is the reference implementation. EventIds 100, 200-205 and 510-516 are taken.
- **Search is a write operation.** `SearchAsync` ends in `BumpAccessAsync`
  (`SqliteMemoryStore.cs:219`, `:980-1001`), which issues two statements per distinct result
  hash, untransacted, on the WAL write lock.
- **`ShareAsync` copies `source_file`/`section` onto the promoted shared row**
  (`SqliteMemoryStore.cs:243-245`), contradicting `docs/adr/0005-source-affinity-ranking.md:69`
  ("Shared-scope rows have no `source_file`"). `ChunkIndex` is computed per context batch, so
  the same document has independent index sequences in the shared and project batches.
- **`VACUUM INTO` in the sync path targets a file `Path.GetTempFileName()` already created.**
  Measured 2026-08-07: stock `sqlite3` 3.51.0 rejects that with "output file already exists";
  the bundled SQLite3MC accepts it (`dotnet test --filter SyncServiceTests` → 10/10 pass).
  The mechanism of the divergence is unverified; the dependence is removable with one
  `File.Delete`.
- **Encrypted-bank sync snapshots are ciphertext** — measured in
  `docs/work/2026-08-06-sqlite3mc-feature-surface.md` F9 ("snapshot WITHOUT key: FAIL as
  expected → SQLite Error 26"). This is what keeps B1 from being a universal secret leak;
  it protects only encrypted installs.
- **No search-path benchmark exists.** `benchmarks/AiRaccoon.Benchmarks/` contains only
  `EmbeddingLatencyBenchmark` and `SearchValuesVsHashSetBenchmark`. Every performance claim
  about search or bank-open cost in this report is marked UNVERIFIED for that reason; the
  only measured bank numbers in the repo are in
  `docs/work/2026-08-06-sqlanalyze-usefulness.md` (10,572 `entries` rows, 29.5 MB bank,
  47.9% freelist, 431 MB WAL before the maintenance service existed).
- **`SqliteConnectionFactory`'s static constructor mutates Dapper's process-global
  `DefaultTypeMap.MatchNamesWithUnderscores`** (`SqliteConnectionFactory.cs:16-21`) — a
  global side effect that only fires when the type is first touched.
