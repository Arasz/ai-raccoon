# Ops/Ecosystem — MoE section: code corpus for AiRaccoon (code-daemon-embed-v1)

**Date:** 2026-08-21
**Lane:** ops/ecosystem — operational facts, CLI/tool-surface contract, migration & compatibility, rollout and acceptance for the code-corpus feature
**Task:** code-search-implementation-plan (implementation plan; no production code in this task)
**Question:** How does a user activate the code engine, what is the exact CLI/tool surface, what breaks or must not break for existing banks, and in what order does the feature ship with what gates?
**Sibling records:** `docs/work/2026-08-21-code-search-exploration.md` (design; worktree `code-embedding-exploration`), `docs/work/2026-08-21-arbitrary-embedding-models-plan.md` rev 2 (engine generalization — **assumed fully implemented**, worktree `embedding-model-support`), `docs/work/2026-08-21-embedding-moe-ops.md` (engine-plan ops lane, format precedent).
**Grade mix:** 26 READ (code), 2 MEASURED (spike throughput/sizes, from the exploration), 6 INFERRED/HYPOTHESIS (labelled).

Path convention: all `src/…`/`docs/…` citations are relative to this worktree's root unless stated otherwise.

---

## 1. Verified surface facts (all READ from code, 2026-08-21)

### 1.1 CLI verb tree

| Fact | Evidence |
|---|---|
| Top-level verb families: `settings`, `model`, `watch`, `extract`, `noise`, `encryption`, `repair`, `serve`, `doctor` | `src/AiRaccoon/Setup/Cli/CliCommandTree.cs:62-76` |
| `model` family today = `model set local [path]` and `model set openai <model> [base-url] [--api-key]` — **no `model download` in this worktree** (engine plan D4/WP2, assumed implemented) | `CliCommandTree.cs:151-164` |
| `settings model` node = `reset` (aliases unset/remove) + `show` (aliases list) | `CliCommandTree.cs:166-177` |
| `settings` is the mandated home for new settings-backed subsystems (ADR-0076): "a new settings-backed subsystem is a node under `settings`, not a new top-level family" | `CliCommandTree.cs:10-12, 78-97` |
| `model set` is an OPERATION that re-embeds the whole bank via an outbox record drained by a server relay; it blocks all tool calls until the re-embed finishes | `CliCommandTree.cs:144-150`; ADR-0076 (`docs/adr/0076-model-set-is-an-outbox-drained-by-an-on-demand-relay.md`); CLAUDE.md invariant "The CLI asks; the server acts" |
| Command routing lives in `ConfigCommands.cs`: `["model","set","local"]` → `ModelSetLocalAsync`, `["model","set","openai"]` → `ModelSetOpenAiAsync`, `["settings","model","reset"]` → `ModelResetAsync`, `["settings","model","show"]` → `ModelShowAsync` | `src/AiRaccoon/Setup/Cli/Commands/ConfigCommands.cs:51-54` |
| `ModelSetLocalAsync` deletes `embedding.apiKey`, then `StartModelMigrationAsync("local", path, null)` (outbox), prints "re-embedding in the background" | `src/AiRaccoon/Setup/Cli/Commands/SettingsCommands.cs:95-108` |
| `ModelResetAsync` deletes exactly five keys: `embedding.provider/model/baseUrl/engine/apiKey` | `SettingsCommands.cs:134-148` |
| `ModelShowAsync` prints provider/model/baseUrl/engine/apiKey from the `embedding.%` settings prefix | `SettingsCommands.cs:150-168` |
| Watch config lives under `settings watch` (enable/disable/concurrency/remove/list); registrations are DATA → `watch registered` is top level | `CliCommandTree.cs:407-432` |
| Ingest scope allowlist: `settings ingest scope add/remove/list`; empty by default → a project ingests nothing until a scope is added | `CliCommandTree.cs:400-405` |
| `doctor` verifies schema shape vs the binary's DDL, never repairs (GH #357) | `CliCommandTree.cs:386-388` |

### 1.2 Settings rows

| Row | Home | Evidence |
|---|---|---|
| `embedding.provider`, `embedding.model`, `embedding.baseUrl`, `embedding.engine`, `embedding.apiKey` | current memory-engine family | `src/AiRaccoon.Infrastructure/Embedding/EmbeddingSettingsKeys.cs:9-17` |
| `embedding.dimensions` (remote dims) + `embedding.model` accepts a directory (local, manifest required) | engine plan D2/WP3 — **assumed implemented**; `model set local` deletes `embedding.dimensions`; `ModelResetAsync` deletes it alongside the five | engine plan §3 D2; not present in this worktree's `EmbeddingSettingsKeys.cs` |
| `retrieval.structureAlpha/rrfK/ftsWeight/vectorWeight/sourceLambda/consolidationThreshold/docScoreFormula/candidateWindow` | memory-search tuning (ADR-0083); **memory-only** | `CliCommandTree.cs:179-218`; `docs/adr/0083-search-parameters-unified-source.md` |
| `watch.*`, `ingest.scope.*` | watch/scope config | `WatchService.cs:79-94` (`WatchConfigKeys`, `IngestScopeKeys`) |

### 1.3 MCP tool surface

- `memory_watch_add(projectId, path)` — **only two parameters; no corpus/kind/ignore flag**; write-gated; returns `WatchAddResult(ProjectId, Path)`; description promises "Already-watched paths are a no-op" | `src/AiRaccoon/Tools/WatchTools.cs:17-33, 64`
- `memory_watch_status(projectId)` → `{watches: [{projectId, path, state, lastError?, lastSync?}]}`; `memory_watch_remove(projectId, path)` | `WatchTools.cs:35-62`
- `memory_search(projectId, query, scope, workspaceId, limit, minRelativeScore, rrfK, ftsWeight, vectorWeight, sourceLambda, consolidationThreshold, docScoreFormula, candidateWindow, contextLabel)` → `SearchResultList {results, warning}` — **no `kind` parameter today**; QueryGuard refuse path at `MemoryTools.cs:167-170`; QueryLengthGuard always on at `:180-183` | `src/AiRaccoon/Tools/MemoryTools.cs:98-187`
- Tools reference table: **27 tools** today | `docs/reference/agent-memory-server.md:19, 35-51` (memory_search row :37, memory_watch_add row :49)
- Tool-count is a gated surface: the feature file asserts the live tool listing ("All 17 tools are still listed" — FR-NM-9; count grew since) | `docs/work/features-agent-memory/agent-memory.feature:12-14`

### 1.4 Schema open / migration machinery

- `CurrentVersion = 10`; ladder `MigrateToV1…V10` (ADR-0011); ladder is for **guarded one-time work**, unconditional re-runnable DDL belongs in `Ddl` (ADR-0023 amendment) | `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:38-49`
- `Ddl` is all `CREATE … IF NOT EXISTS`: `entries` :81-108, `settings` :110-113, `maintenance_jobs` :119-123, `entries_fts` :125-131, `vec_entries float[384]` :137, `vec_structure float[384]` :141, `watches` :249-257 (project_id, path, created_at, last_change_ts, scan_lease_expires_at — **no corpus/kind column**), `watch_files` :259-263+ (one SHA-256 fingerprint per path) | `MemorySchema.cs`
- **Digest gate (ADR-0075):** `SchemaDigest = first 32 bits of SHA-256(Ddl)`; `EnsureAsync` runs `Ddl` only when the stored digest differs, on the read-write open path (`SqliteConnectionFactory.InitializeAsync`) | `MemorySchema.cs:421-426, 428-460`
- "a `Ddl` edit changes [the digest] and forces the rerun, which is why additive DDL still reaches existing banks with **no version bump** (ADR-0026)" | `docs/explanation/architecture.md:175-180`
- Forward-version guard: a bank stamped newer than `CurrentVersion` refuses the open (ADR-0019) | `MemorySchema.cs:435-439`
- Two data-shaped repairs run on every open, outside the digest gate | `MemorySchema.cs:462-469`

### 1.5 Watch / ingest / degradation machinery

- `WatchDigestExecutor.DigestAsync`: hash-skip → `ReplaceIfFileChangedAsync` (delete + re-ingest + fingerprint in ONE transaction) → best-effort embed-pending | `src/AiRaccoon.Infrastructure/Watch/WatchDigestExecutor.cs:20-61, 68-78`
- `FileTypeMatcher`: extension→handler map, **duplicate extension registration throws**; `TryGetHandler`/`IsSupported` | `src/AiRaccoon.Infrastructure/Ingestion/FileTypeMatcher.cs:19-47`
- Unhandled extensions are **skipped today** (return 0, no error): a watched `.cs` file is currently ignored | `src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs:30-38`
- `WatchService.AddAsync`: enabled-check → scope-check → existence-check → `InsertWatchIfAbsent` (idempotent by PK `(project_id, path)`) → pipeline register. **No overlap handling exists.** | `src/AiRaccoon.Infrastructure/Watch/WatchService.cs:15-40`; `WatchStore.cs:36-44`
- `WatchStore.RemoveWatchAsync` deletes `watch_files` cascade + watch row in one `BEGIN IMMEDIATE` transaction, path-prefix `LIKE` pattern | `WatchStore.cs:46-75`
- Sweep/degradation operates through `IMemoryStore.ListContextAsync` (committed memory entries only) — a `code_entries` table is out of sweep's reach **by construction** | `src/AiRaccoon.Infrastructure/Degradation/SweepService.cs:16-19`

### 1.6 Sync (the one correction to the design)

- Sync pushes a **whole-file snapshot**: `VACUUM INTO` of the bank → `StripNonSyncableAsync` (deletes workspace entries + ALL settings rows, then VACUUM) → push | `src/AiRaccoon.Infrastructure/Sync/SyncService.cs:63-79, 424-440`
- The pull/merge side ATTACHes the remote and merges **only** `entries`, `sync_tombstones`, `memory_source` | `SyncService.cs:261-343`
- **Correction to exploration §4.4/§Q2:** "sync ignores the code corpus by construction" is true for the **pull** direction only. The push direction ships the whole bank file, so `code_entries`/`code_fts`/`vec_code` **would leave the machine** unless `StripNonSyncableAsync` explicitly deletes them. The feature must extend the strip (see §3.3). ADR-0014 ("settings never sync") is the precedent: push strips by explicit deletion, gated by tests.
- Encryption: the bank file is encrypted wholesale (SQLCipher); the snapshot is itself encrypted and opened with the bank key | `SyncService.cs:427-429`; `docs/adr/0012-ssh-key-derivation-hkdf-replacement.md`

### 1.7 Versioning / docs conventions

- `VERSION` = **1.28.1** (single hand-written marker; everything derives from it; `scripts/version-bump.py <patch|minor|major>`) | `VERSION`; `scripts/version-bump.py:1-7`
- README "What's new" format: `- **<headline>** (X.Y.Z) [ADR-NNNN](docs/adr/…md)` — reverse chronological, newest first | `README.md:32-39`
- ADRs are immutable, numbered in date order; highest committed = **0083** in both this worktree and `embedding-model-support` (the engine plan's own ADR is plan-only, not yet numbered) | `docs/adr/README.md:1-6`
- Feature files: Gherkin `@bdd`, FR-* rules, `docs/work/features-<name>/<name>.feature` + `spec.json` | `docs/work/features-agent-memory/agent-memory.feature:1-14`

---

## 2. CLI + tool surface

### 2.1 `memory_watch_add`: NO new flag — verified, and correct

The design's recommendation (extension-based dispatch, no new parameter) matches the code's seams:

- The `watches` row has no corpus/kind column and the design shares **one fingerprint per path** between corpora (`MemorySchema.cs:249-263`; exploration §4.2) — a flag would force a schema change to express what extension dispatch already expresses.
- The dispatch seam already exists: `FileTypeMatcher` throws on duplicate extensions (`FileTypeMatcher.cs:27`), so a `CodeFileTypeHandler` registering `.cs/.py/.ts/…` **cannot** collide with the memory path's markdown/json handlers; unhandled extensions are silently skipped today (`FileIngestor.cs:35-38`), which is exactly the "no code engine → code files skipped" behavior we want.
- `memory_watch_add`'s description must change (text only, not parameters): "mirrors the path into the project's memory **and, when a code engine is configured, indexes code files into the project's code corpus**" (`WatchTools.cs:18-19`). Same for `memory_ingest_directory`/`memory_ingest_file` descriptions — the dispatch is in the shared ingest path, so one description update per tool that can reach code files.

**Rejected:** `--corpus code` / `--kind` flag on `memory_watch_add`. It adds wire surface, needs a `watches.kind` column, forces users to know corpus internals, and buys nothing — the extension IS the corpus selector. Simpler shape wins (invariant: ask-if-simpler).

### 2.2 Code engine activation — exact commands a user runs

V1 code engine = **local-only** (the downloaded ONNX model). Remote code embedding (Voyage/OpenAI via the `openai` provider) is a documented v2, because the dims-probe machinery (engine plan D10) would need a second `embedding.codeDimensions` row for no v1 user.

```bash
# 1. Download the code model (engine plan D4/WP2 verb, assumed implemented).
#    187,490,530 B INT8 < 500 MB → no --yes needed. SHA-256 pinned from HF LFS oids;
#    manifest written to <data-root>/models/code-daemon-embed-v1/manifest.json.
ai-raccoon model download faxenoff/code-daemon-embed-v1

# 2. Activate the code engine for the bank. NEW subcommand under the existing
#    `model set` family (an operation: outbox record + server relay, ADR-0076 shape).
ai-raccoon model set code local <data-root>/models/code-daemon-embed-v1

# 3. Verify.
ai-raccoon settings model code show
#    provider: local
#    model: /Users/<you>/.ai-raccoon/models/code-daemon-embed-v1
#    engine: local:<path>#code-daemon-embed-v1@768   (fingerprint; change → code re-embed)

# 4. Deactivate.
ai-raccoon settings model code reset    # deletes the code rows; code search degrades to FTS5-only
```

Settings rows **added**: `embedding.codeModel` (directory path), `embedding.codeEngine` (fingerprint — change triggers a code-corpus re-embed, engine plan D7 shape). **No** `embedding.codeProvider` in v1 (always local; remote is v2) and **no** `embedding.codeDimensions` (the manifest carries dims — code tables are created at `float[768]` from the manifest at first code-embed).

Settings rows **removed/changed**: none from the memory family. `settings model reset` (memory) must NOT touch the code rows and vice versa — the two engines are independent; only `settings model code reset` deletes `embedding.codeModel/codeEngine`. `settings model show` is extended to print the code rows under a `code:` block (today it prints named rows only, `SettingsCommands.cs:150-168` — the `embedding.%` prefix dump alone would not show them).

**Where the verb goes:** `model set code local` is a new subcommand under `ModelCommand()` (`CliCommandTree.cs:151-164`) — an operation, like `model set local` (ADR-0076: operations top level). Config lives under `settings model` (`code show`/`code reset`), mirroring `settings model show/reset`. Routing additions in `ConfigCommands.cs:51-54` pattern. The re-embed enqueue reuses `IModelMigrationStore` (outbox + relay) with a **corpus discriminator** so the code drain never re-embeds memory rows (architect/engineer lanes own the mechanism; ops states the contract in §3.6).

### 2.3 NEW: `ai-raccoon.ignore` (owner requirement)

**Name:** `ai-raccoon.ignore` (plain name; matches the tool's own naming, unambiguous next to `.gitignore`).

**Placement (v1):** the **watched root** — the directory passed to `memory_watch_add` (or `memory_ingest_directory`). One file per watched directory; patterns are matched against paths **relative to that watched root**. v1 does not walk up to ancestors and does not support nested ignore files inside the tree (a v2 lever if users ask). The ignore file itself is never ingested (`.ignore` is not in either matcher's extension map).

**Format:** gitignore-style line format — `#` comments, blank lines, trailing `/` = directories only, leading `/` = anchored to the watched root, `*`/`**` globs. **No `!` negation in v1** (review F-09: re-include semantics interact badly with directory pruning; additive later — engineer §5.2, combined §2.1). The matcher is hand-rolled, kept minimal by design (verified: no glob machinery exists in `Watch/`; no `FileSystemGlobbing` in the package graph).

**What it excludes:** **both pipelines** — a matched file is skipped by the memory ingest AND the code ingest; a matched directory is pruned from directory scans (watch initial scan/catch-up and `memory_ingest_directory`). **A previously-indexed file that becomes ignored is CLEANED**: the digest on an ignored path deletes its stale chunks from both corpora (`DeleteSourcePathAsync` — deletion is not gated by ignore) and updates `last_change_ts` without reading/fingerprinting; the catch-up scan's `ReconcileIgnoredAsync` clears fingerprinted-but-now-ignored paths.

**Scope of effect:** scans and watch digests only. **`memory_ingest_file` on an explicitly-named ignored path returns 0 chunks (ignore wins — RESOLVED, combined §2.1; matches "ignored files are never fingerprinted, never chunked")**; `memory_watch_add` on an explicitly-named file applies no ignore rules (a file watch has no tree).

**When changes take effect:** the file is read per scan/digest — no caching, no restart. **Editing it triggers an immediate full re-scan of the watch root** (single-flighted; when a scan is already in flight the edit queues a follow-up scan or the scan re-checks the file's mtime at end — review F-16); the ignore file itself is never self-ignored and is fingerprinted like any non-indexable file so its edit is detected.

**`memory_watch_add` parameter:** **none in v1** — the file just works. Agreed with the owner's recommendation; arguments: (a) a per-watch `ignore` param needs persistence + wire surface + docs to express what a versionable, repo-committed file expresses better; (b) the file travels with the repo, so every machine watching the repo gets the same exclusions; (c) it is discoverable by the user, invisible to the agent — the agent should not need to re-state exclusions per watch call. A `--ignore` override param is listed as a rejected alternative (R3).

**Docs entries:** reference (new "Ignoring files" subsection in `docs/reference/agent-memory-server.md` + one line in the `memory_watch_add`/`memory_ingest_directory` notes) and a how-to (`docs/how-to/ignore-files.md`, new — `docs/how-to/` exists, cf. `docs/how-to/read-performance-metrics.md`). See §6.

### 2.4 NEW: no overlapping watches + repo-watch-by-default (owner requirement)

**Rule statement (for the reference docs):** *"A project holds at most one watch per path: a watch may not be nested inside another watch of the same project. Registering a directory watch prunes every watch it contains. Registering a watch inside an existing directory watch is **rejected** with an error naming the covering watch. `memory_watch_status` lists only the surviving (outermost) watches."*

**Behavior on `memory_watch_add` (review F-10/F-14 + codereviewer MUST-FIX 7 — ONE `BEGIN IMMEDIATE` store transaction `PruneAndAddAsync`):**
1. If the new path already has a watch (identical path) → no-op, `absorbedBy = path`.
2. If the new path is inside an existing watch → **rejected**: `WatchOverlapException(projectId, newPath, coveringPath)` naming the covering watch; nothing is written; `absorbedBy` is never set on a rejection.
3. Else (the new path contains zero or more existing watches) → **one transaction**: delete every contained watch row + cascade its `watch_files` (the `RemoveWatchAsync` pattern, `WatchStore.cs:46-75`) AND insert the new watch (`lastChangeTs=0` → full catch-up), `pruned = [<nested paths>]`. A kill-9 anywhere in the step leaves EITHER the old watches OR the new watch — never an unwatched path. Runtime `UnregisterWatch` runs after commit (idempotent; a crash between commit and unregister leaves stale runtime state that the hosted service's registration poll reconciles; digest ownership stays deterministic).
4. **Tie-break (review F-15):** mutual containment (real-path-equivalent registrations via symlink/case spellings) keeps the **longest literal path; on equal length, the first-registered** — never prune a watch whose real path equals the survivor's.

**Result shape:** `WatchAddResult(ProjectId, Path)` (`WatchTools.cs:64`) gains two **additive** fields:

```
{projectId, path, pruned: ["/repo/src/legacy", …], absorbedBy: null | "/repo"}
```

- `pruned` — paths of watches removed because the new watch contains them (empty when none).
- `absorbedBy` — the covering watch path ONLY for the identical-path re-add no-op; null otherwise (a rejected add throws — no result).
- Additive = existing clients unaffected.

**`memory_watch_status`:** reflects the winners only — pruned paths disappear from the list (no tombstone; the add response already reported them). No status-shape change.

**CLI:** `ai-raccoon watch registered` shows the same winners (it reads the `watches` table; `CliCommandTree.cs:408-414`). No new CLI verb.

**Why prune rather than coexist:** overlapping watches double-fingerprint the same files and, with the code corpus, would double-ingest into two corpora paths and make `watch_files` ownership ambiguous. One owner per path is the invariant that keeps the shared-fingerprint design (exploration §4.2) sound. The repo-watch-by-default UX: agents watch the repo root once; per-directory watches agents registered earlier are cleaned up by the first root watch add — the common case the owner wants to work.

### 2.5 Reference-doc changes (`docs/reference/agent-memory-server.md`)

| Change | Location |
|---|---|
| Tools count 27 → **28** | `:19` |
| `memory_search` row: add `kind=memory\|code\|both` (default `memory`); result `{results, warning}` unchanged for `memory` (no `code` key — review F-12), `{results: [...], code: [...]}` for `code`/`both`; code hits carry `path, lineStart, lineEnd` | `:37` + new "kind" note |
| New tool row `code_get`: `projectId`, `hash` → `{hash, value, path, lineStart, lineEnd}` | tools table |
| `memory_watch_add` row + note: mirrors both corpora; prunes nested watches (`pruned`); a nested add is REJECTED naming the covering watch (`absorbedBy` only for identical-path re-add) | `:49` |
| Embedding-engine section: `ai-raccoon model download <repo-id>`, `model set code local <dir>`, `settings model code show/reset` | `:116-124` |
| New "Ignoring files" subsection: `ai-raccoon.ignore` contract (§2.3) | new |
| New "Watches" rule statement (§2.4) | new |

---

## 3. Migration & compatibility

### 3.1 New tables — digest-gated `Ddl` extension, no ladder step

`code_entries`, `code_fts` (FTS5 external-content), `vec_code` (vec0 `float[768] distance_metric=cosine` — the exploration §4.2 shape, minus `scope/workspace/rating/ttl/heading_path`) are added to the `Ddl` constant as plain `CREATE … IF NOT EXISTS` statements. The mechanism is **verified, not hypothesized**:

- `Ddl` is digest-gated on itself (ADR-0075): editing `Ddl` changes `SchemaDigest` (`MemorySchema.cs:421-426`), so on the next read-write open of **every existing bank**, `EnsureAsync` runs the block once and creates the missing tables (`MemorySchema.cs:453-460`); architecture.md:175-180 states this exact property ("additive DDL still reaches existing banks with no version bump").
- No `CurrentVersion` bump for the tables themselves (ladder is for guarded one-time work, `MemorySchema.cs:44-47`). A ladder step **is** needed for the overlapping-watch data migration — see §3.4.
- The 384/768 dimension split is safe: the two corpora never share a vec table, and the memory DDL stays constant at `float[384]` (engine plan D3).

### 3.2 What does NOT change (explicit)

- `entries`, `entries_fts`, `vec_entries`, `vec_structure` DDL and every memory tool's wire shape — `memory_search` gains `kind` **defaulting to `memory`** (`MemoryTools.cs:98-187` keeps all current params; additive optional parameter). Retrieval gates and existing clients stay green.
- `retrieval.*` tuning settings apply to **memory only**; code search reuses the same RRF constants in v1 (exploration §4.4), no new tuning rows.
- Sweep/degradation: `SweepService` reaches the bank only through `IMemoryStore` (entries) — code rows are unreachable (`SweepService.cs:16-19`). No TTL, no rating, no promotion, no workspace for code.
- Encryption-at-rest: the bank is encrypted wholesale; new tables inherit it with zero per-table work.
- Watch registration surface: no `watches`/`watch_files` column changes; one fingerprint per path serves both corpora.
- `settings model reset` semantics for the memory engine are untouched (`SettingsCommands.cs:134-148`); `embedding.dimensions` deletion rule per engine plan D2 unchanged.

### 3.3 Sync — the push direction must be closed (design correction)

The exploration claimed sync excludes the code corpus "by construction". **Verified false for the push direction:** the snapshot is `VACUUM INTO` of the whole bank (`SyncService.cs:70, 101`) and only settings + workspace entries are stripped (`SyncService.cs:425-440`). A code corpus would ship source code + 768-dim vectors to the cloud object — a privacy leak (source leaves the machine) and a size regression (3072 bytes/chunk of vectors alone).

**Required change (review F-22 + external round-1 B1 — DROP, not row-delete):** `StripNonSyncableAsync` **DROPs** `code_entries`, `code_fts`, `vec_code` (their FTS5/vec0 shadow tables and triggers drop with them; the strip already runs with vec0 loaded, `SyncService.cs:427-429`) from every pushed snapshot on **both push paths** (local + merged, `SyncService.cs:70-74,101-107`); ADR-0014 ("settings never sync") becomes "settings and the code corpus never sync". Row-deletion is NOT the mechanism — the gate asserts table ABSENCE (`sqlite_master`), and `DELETE` would fire the trigger families + leave empty tables. Pull/merge needs no change (merge names only `entries`/`sync_tombstones`/`memory_source`, `SyncService.cs:261-343`). Gate: a sync test asserting the pushed snapshot contains no `code_entries`/`code_fts`/`vec_code` (nor `vec_code_%` shadow) tables on both push paths — RED seeds code rows and pushes before the strip change (review F-06).

### 3.4 Overlapping watches — first-run migration (owner requirement)

Existing banks can hold nested watches (old versions allowed them — no overlap logic exists anywhere in `WatchService`/`WatchStore` today). On the first run with the new rule they must be pruned. **Proposal: a ladder step (v11, `CurrentVersion` 10 → 11), reported, not silent.**

- **Mechanism:** `MigrateToV11Async` runs once per bank, server-side (ADR-0075: only the server writes): per project, keep the outermost watches, delete nested watch rows + cascade their `watch_files` (same transaction pattern as `WatchStore.RemoveWatchAsync:46-75`). **Tie-break (review F-15): keep the longest literal path; on equal length, the first-registered — never prune a watch whose real path equals the survivor's.** Stamped only on success (ladder rule, `MemorySchema.cs:490-494`).
- **Reporting — with a channel (review arch-8):** the ladder has no logger (`MemorySchema` is static and logger-less), so `MigrateToV11Async` **returns** the pruned `(path, coveredBy)` list + count, and the one caller that owns a logger (`SqliteConnectionFactory.InitializeAsync`) logs one Information line per pruned watch (`watch overlap migration: removed <path> (covered by <path>)`) plus the count. WP1's gate asserts the log line (RED: migration runs, no log line). `memory_watch_status` and `watch registered` show only winners afterwards — the migration's report is the log, not a persistent surface. Rationale: silent pruning would confuse an agent that "knows" its old watch exists; a persistent tombstone list would be a hand-maintained list (derive-or-delete invariant) nobody reads.
- Same pruning code path as the runtime rule (§2.4) — one implementation, two call sites.

### 3.5 Wire-shape backward compatibility

- `memory_search` + `kind` (default `memory`): existing JSON-RPC calls behave identically; the envelope for `kind=memory` is the current `SearchResultList` — semantically identical modulo `Meta.CorrelationId` (per-call random; review F-02), pinned by the WP1 golden.
- `WatchAddResult` + `pruned`/`absorbedBy`: additive fields; old clients ignore them.
- `code_get` is a new tool: tool-count tests and `docs/reference/agent-memory-server.md:19` (27→28) update together — the feature file's live tool-listing assertion (`agent-memory.feature:12-14`) is the gate.
- `model set code local` writes new `embedding.code*` rows; a bank opened by an older binary simply ignores unknown settings rows (settings is a key-value table, `MemorySchema.cs:110-113`) — no forward-compat hazard. The forward-version guard (`MemorySchema.cs:435-439`) already protects older binaries from newer banks via the version bump in §3.4.

### 3.6 Code re-embed must not block memory tools (RESOLVED — review arch-1/F-08; engineer D-E9 adopted)

`model set` for the memory engine blocks all tool calls until the re-embed finishes (`CliCommandTree.cs:144-150`). The code corpus must NOT inherit that — and the shared-outbox idea is impossible (single-row outbox `MemorySchema.cs:372-382`, hard-coded relay query `ModelMigrationJob.cs:36-43`, ADR-0076 all-tools gate). **Adopted: the code drain is outbox-free** — `model set code local` writes settings + invalidates `embed_state` in ONE transaction (the `vec_code_pending` trigger empties `vec_code` at commit — no stale-vector window); the `code-reindex` maintenance job drains pending code rows; **no ToolGate interaction — memory tools never blocked**; while code vectors drain, `kind=code` search degrades to FTS5-only (code_fts rows exist from ingest time). H4 deleted; no `model_migration` corpus column (ladder v11 = overlap prune only).

---

## 4. Risks & mitigations

| # | Risk | Evidence / mitigation | Grade |
|---|---|---|---|
| 1 | **Model provenance** — single author, 66 downloads, 2 weeks old, MIT | Registry pin: committed SHA-256 pin for `faxenoff/code-daemon-embed-v1` (engine plan D8 pattern; `BundledResource.IsVerified`); first download TOFU pin re-verified on every load; **eval before any default flip**; A/B vs `jinaai/jina-embeddings-v2-base-code` (154 MB int8, 768-dim, Apache-2.0, 396k downloads — the established fallback). ~~Never PTQ the INT8 QAT artifact~~ — the artifact we run is **fp32**, so PTQ is permitted; the card's hit@1 .200→.133 is the cost to weigh, not a prohibition **CORRECTED 2026-08-23** (fp32, not INT8 — see Amendments) | READ + MEASURED (exploration §1/§1.1/§6.1) |
| 2 | **Chunker quality without AST** | v1 line-range heuristics approximate function boundaries; eval settles it — the eval set must include a Python repo (indentation-based splitting is the weak case, exploration §6.2); tree-sitter symbol extraction is the v2 lever if nDCG fails | READ (exploration §6.2) |
| 3 | **Throughput** — 56 texts/s on M4 CPU (spike) | 10k-unit repo ≈ 3 min initial index; watch deltas in seconds. Initial index of a large repo is the only real wait; document expected wall time in the how-to; 700k-unit repos are the GPU/OpenVINO story, not local default | MEASURED (spike, exploration §1/§6.3) |
| 4 | **187 MB download, ~50 MB resident** | Under the 500 MB `--yes` threshold; disk-space check exists in the download verb (engine plan D4); two sessions in-process (23 MB MiniLM + ~50 MB code) is fine | READ (engine plan D4) |
| 5 | **Sync push leakage (NEW)** | Code tables ship in whole-file snapshots unless `StripNonSyncableAsync` deletes them — §3.3; test-gated like ADR-0014 | READ (this lane §1.6) |
| 6 | **Search surface stability** | `kind` additive, default `memory` — retrieval gates and clients untouched; QueryGuard + QueryLengthGuard apply to code queries identically (`MemoryTools.cs:167-183` — code is still a search over project data) | READ |
| 7 | **Settings hygiene** | `settings model reset` (memory) must not delete code rows; `settings model code reset` must not touch memory rows; `model set code local` deletes any stale `embedding.codeEngine` before writing the new one (fingerprint change → re-embed, engine plan D7) | INFERRED (from `SettingsCommands.cs:134-148` shape) |
| 8 | **Unloadable code engine** | Missing manifest/files/dims mismatch must fail loudly at activation (`model set code local` refuses, engine-plan manifest validation D1/D5) and at server start, never silently at search time (engine plan D12 principle) | READ (engine plan D1/D5/D12) |
| 9 | **Binary/garbage files with code extensions** | A `.cs` that is actually binary would be read as text and chunked into noise; v1 accepts this (matches today's memory-path behavior for markdown-ish files); the code matcher must not register extensions the memory path owns (duplicate-ext throw, `FileTypeMatcher.cs:27`) | INFERRED |
| 10 | **Re-embed blocks memory** | §3.6 contract — code drain is per-corpus; memory tools never blocked | HYPOTHESIS H4 |
| 11 | **Overlapping watches before the rule** | §3.4 ladder v11 migration, reported in logs | READ |

---

## 5. Rollout sequence (phased; owners; decision points)

The engine generalization (WP1–WP4 of the arbitrary-models plan) is **assumed fully implemented** — this sequence starts after it.

| Phase | Lane owner | Deliverable | Gate |
|---|---|---|---|
| P0 | architect+ops | **Owner review of this plan** (combined MoE doc) — activation UX (§2.2), ignore contract (§2.3), no-overlap rule (§2.4), sync-strip correction (§3.3), migration report (§3.4), eval contents (§5) | G0: owner approves in Rider |
| P1 | architect+eng | **Corpus schema + settings + CLI**: `code_entries/code_fts/vec_code` in `Ddl` (digest gate); `embedding.codeModel/codeEngine` rows; `model set code local`; `settings model code show/reset`; `settings model show` code block; ladder v11 (overlapping-watch prune, reported); **no code indexing yet** | G1: existing-bank copy opens, `doctor` reports the new tables, `code` rows round-trip through show/reset, v11 migration prunes a fixture bank with nested watches (RED→GREEN), memory tables byte-identical |
| P2 | eng | **Chunker + ingest**: `CodeChunker` (line-range, 126-token budget = 128 ctx − 2, exploration §4.3/D6), `CodeFileTypeHandler` + extension map, `CodeIngestor`; unhandled-extension skip unchanged (`FileIngestor.cs:35-38`); `ai-raccoon.ignore` matcher in the scan path (both pipelines) | G2: ingest fixtures — `.cs` file → `code_entries` rows with `line_start < line_end`, FTS + vec0 rows; ignored file → no rows in EITHER corpus; memory corpus row-for-row unchanged |
| P3 | eng | **Watch channeling**: digest dispatch by extension (memory and/or code), deletion from both corpora in one transaction; overlap prune-on-add (§2.4) with `pruned`; nested add rejected (`WatchOverlapException`); `WatchAddResult` additive fields; `memory_watch_add` description | G3: watch tests — add root watch prunes nested (result lists them, status shows winners); add-inside-existing → rejection naming the covering watch; removal deletes from both corpora |
| P4 | eng | **Search + tools**: `kind` param (`memory` default), `CombinedSearchResultList` (`results`/`code` keys, `WhenWritingNull` omission), `code_get`, `CodeSearchService` (FTS5 + vec0 + RRF, project scope); outbox-free code drain (§3.6); sync DROP-strip (§3.3) | G4: memory wire-shape compat tests green (default `kind=memory` semantically identical to the WP1 golden, no `code` key); code search returns path+line hits; pushed snapshot has NO code tables (both push paths); tool-count gate 28 |
| P5 | ops+eng+eval | **Eval A/B on a bank copy** (WP5 harness of the engine plan): code-daemon-embed-v1 vs jina-code-v2 (after the parity probe) on the same chunks; heuristic-chunker vs **token-window** arm (span-overlap scoring); MiniLM-on-same-chunks = scratch-only reference, NOT a gate arm (review F-26); include a Python repo; timed index + query; **negative controls: random-vector leg AND FTS-only both < floor** (review F-01) | G5: **owner decision point** — eval report (§5.1) reviewed; default code model blessed (registry pin) or v1 ships activation-only with no default; **default memory model unchanged** |
| P6 | arch+ops | **Docs + ADR + release**: ADR (0084+), architecture.md, reference doc, how-to, feature file, README What's-new, version bump | G6: docs drift audit clean; ADR reviewed; one squash-merge PR |

### 5.1 What the eval report must contain for the owner to approve (G5)

1. **nDCG@5 per arm** on the eval set: code-daemon-embed-v1, jina-embeddings-v2-base-code (after the ≥ 0.999 parity probe — review F-04); MiniLM-on-same-chunks is a scratch-only reference (answers "is a code model worth it" informally, NOT a gate arm — review F-26).
2. **Per-query regression table**: every eval query × arm → rank of the relevant chunk (the engine plan's regression-table shape, WP5).
3. **Chunker A/B**: heuristic line-range chunks vs token-window baseline, per model, scored by **span overlap** (per-arm re-anchoring — review arch-7; settles risk #2).
4. **Negative controls (review F-01):** the same set + queries with (a) the vector leg replaced by seeded random unit vectors and (b) FTS-only — both must score below the fixed 0.50 floor.
5. **Throughput/wall time** per arm: index time + p50/p95 query latency (56 texts/s M4 reference point).
6. **Recommendation**: which model becomes the registry-pinned default (if any), with the provenance note (risk #1).

---

## 6. Docs checklist

| Doc | Change |
|---|---|
| `docs/explanation/architecture.md` | §Data model: add `code_entries`/`code_fts`/`vec_code` to the ER diagram + a "code corpus" note (separate tables, no sweep/TTL/promotion/sync); §Write path: code-ingest branch + `ai-raccoon.ignore`; §Query flow: `kind`; §Sync cycle: "code corpus never syncs" (strip); §Schema versioning: cite the new tables as the digest-gate additive-DDL instance + v11 step |
| `docs/reference/agent-memory-server.md` | §2.5 table — every row |
| `docs/how-to/ignore-files.md` (NEW) | `ai-raccoon.ignore` placement/format/scope, **edits trigger an immediate re-scan**, ignore wins for explicit file ingest, example file |
| `docs/adr/0084-…md` (NEW; number = **next free after the engine-generalization ADR** — both plans are in flight; highest committed today is 0083, so this is 0084 or 0085 — check `docs/adr/README.md` at write time) | Records: code is a second corpus (own tables, 768-dim, no memory semantics); **code corpus never syncs** (push strip + pull-ignores-by-construction); `kind` additive default `memory`; no-overlap watch rule + v11 migration; `ai-raccoon.ignore` contract; code re-embed is per-corpus and never blocks memory |
| `docs/work/features-code-search/code-search.feature` + `spec.json` (NEW) | Gherkin `@bdd` per `features-agent-memory` convention; FR-* rules: activation (`model set code`), extension dispatch, ignore file, overlap pruning + migration, `kind` default, `code_get`, no-sync, code re-embed non-blocking |
| `README.md` What's-new | One bullet, existing format: `- **<headline>** (X.Y.Z) [ADR-NNNN](docs/adr/…md)` (`README.md:32-39`) |
| `VERSION` | Bump via `scripts/version-bump.py` (minor — new feature) at release time |

---

## 7. Owner acceptance checklist (verifiable, commands + expected output)

Run against a **bank copy** first, then a real repo:

1. `ai-raccoon model download faxenoff/code-daemon-embed-v1` → files land in `<data-root>/models/code-daemon-embed-v1/` incl. `manifest.json` with per-file SHA-256s; a second run verifies and skips (no re-download).
2. `ai-raccoon settings model code show` → before activation: `provider: (none …)`; after §2.2 step 2: `provider: local`, `model: <dir>`, `engine: local:<dir>#code-daemon-embed-v1@768`.
3. `ai-raccoon settings model show` → prints the memory block AND a `code:` block.
4. `ai-raccoon doctor` → reports the schema shape including `code_entries`, `code_fts`, `vec_code` on an **existing** bank (digest-gate Ddl rerun) — no version error.
5. On an old bank with nested watches: first open logs `watch overlap migration: removed … (covered by …)`; `ai-raccoon watch registered` lists only the outermost watch.
6. Watch a repo: `settings ingest scope add <pid> <repo>`, `settings watch enable <pid> true`, `memory_watch_add(pid, <repo>)` → result `{projectId, path, pruned: […], absorbedBy: null}`; `memory_watch_status` lists only the repo watch. A second `memory_watch_add` of a subdirectory → **rejected with `WatchOverlapException` naming `<repo>`** (not `absorbedBy` — review F-10).
7. `sqlite3 memory.db "SELECT count(*), min(line_start), max(line_end) FROM code_entries"` → count > 0, ranges sane; `memory_search(kind=code)` returns chunks with `path`+`lineStart`/`lineEnd`; `memory_search()` (no kind) returns the **same envelope as before the feature** (compat).
8. `code_get(hash)` returns the full chunk source with its line range.
9. Add `ai-raccoon.ignore` at the repo root ignoring `**/generated/**` → re-scan: no `code_entries` rows and no `entries` rows for those files (stale chunks cleaned); explicit `memory_ingest_file` of an ignored file returns 0 chunks (ignore wins — combined §2.1).
10. With sync configured: push → download the remote object → no `code_%` tables present; local bank untouched.
11. `ai-raccoon settings model code reset` → code search degrades to FTS5-only (or empty), memory search and memory vectors untouched; `embedding.code*` rows gone.
12. Eval report (§5.1) exists with all six items; owner's G5 decision recorded.

---

## 8. Open questions / hypotheses

- **O1 (owner):** should `model set code local` also accept a bare `openai` remote in v1? (Lane proposal: no — remote code embedding is v2; dims probe machinery is the cost.)
- **O2 (owner):** `ai-raccoon.ignore` vs explicit `memory_ingest_file`/file-watch — explicit wins (lane proposal) or git-style ignore-wins?
- **O3 (owner):** should the eval's losing arm (likely jina-code-v2) still be downloadable and selectable via `model set code local <dir>`? (Lane proposal: yes — the download verb is model-agnostic; the registry pin is per-model.)
- **O4 (engineer):** gitignore subset matcher vs a matcher library for `ai-raccoon.ignore` parity.
- **H1 (HYPOTHESIS):** code-daemon-embed-v1 beats jina-code-v2 on agent-style behavioral queries — settled only by P5 eval.
- **H2 (HYPOTHESIS):** line-range heuristic chunks are good enough without AST — settled by the P5 chunker arm.
- **H3 (HYPOTHESIS):** initial-index wall time on user repos tracks the spike's 56 texts/s — real repos vary; P5 measures on the eval repos.
- **H4 (HYPOTHESIS):** the existing outbox/lease machinery expresses a per-corpus code drain without new machinery — engineer lane verifies in P1/P4.
- **H5 (HYPOTHESIS):** the digest-gate Ddl rerun is safe on banks with in-flight sync state — the rerun is additive-only CREATE IF NOT EXISTS, but a sync test on a mid-cycle bank copy is in G1.

## Amendments

### 2026-08-23 — the shipped code model is fp32, not INT8 QAT (WP7 desk half, PR #536)

**What was wrong:** this document records `faxenoff/code-daemon-embed-v1` as an INT8
quantization-aware-trained artifact in its §4 risk table, where it is carried forward as a live ops constraint. The file AiRaccoon downloads and runs is **fp32**.

**Measured 2026-08-23** by loading the artifact that
`model download faxenoff/code-daemon-embed-v1` places on disk — 187,286,767 B, sha256
`57bcfc6aed11ea239d01f2b124f2f948456f2284ad6e2c4744452509c9c25ca9`, the value pinned in that
directory's own `ai-raccoon.manifest.json`:

| | Recorded here | Measured |
|---|---|---|
| Weights | INT8, QAT, Q/DQ nodes carry trained scales | **fp32** — 70 initializers, **all `FLOAT`**, 46,801,920 elements = 187,207,680 raw bytes |
| Quantized ops | implied throughout | **zero** `QuantizeLinear`, `DequantizeLinear`, `MatMulInteger` or `QGemm` in 373 nodes |
| Why 187 MB reads as int8 | — | it does not: 46.8M parameters x 4 bytes **is** 187 MB. A 46.8M-parameter int8 graph would be ~47 MB — which is exactly what quantizing this one produces |

Reproduced independently during review of PR #536.

**What it changes:** the model card's *"never PTQ the INT8 QAT artifact"* warning refers to a
**different file** (`model_int8qdt.onnx`) than the one we run, so it does not forbid quantizing the
fp32 graph we actually have. It remains a live warning about what quantization costs this model
family's retrieval — hit@1 .200 -> .133 — and WP7's desk half measured a fp32-vs-int8 cosine of
**0.964** (against a 0.9999 negative control), which points the same way. **Nothing shipped
changes:** the engine has always been running this fp32 graph, so every throughput and
resident-size figure taken against it stands; only the label was wrong.

**Not rewritten:** figures elsewhere in this document that merely *label* the model INT8 while
reporting something else measured correctly are historical and read as such.

**Full record:** `docs/work/2026-08-23-code-engine-inference-research.md` §2.
