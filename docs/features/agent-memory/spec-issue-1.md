# Issue #1 — Agent Memory Management MCP Server (AiRaccon)

> **Epic:** AiRaccon — agent memory management MCP server
> **Status:** Draft
> **Prerequisites:** none (first dossier in this repo; the random-number tools in `src/AiRaccon/Tools/RandomNumberTools.cs` are scaffold samples, superseded by this feature)
> **Dependencies (external):**
> - MCP C# SDK **2.0.0** (already pinned in `Directory.Packages.props`) — 2026-07-28 spec revision, stateless HTTP, MRTR, `[McpServerTool]` attribute discovery
> - [sqlite-memory](https://github.com/sqliteai/sqlite-memory) **1.3.5** (MIT) — markdown-aware memory extension, hybrid vector+FTS5 search, built-in llama.cpp local embedding engine, `memory_*` SQL functions
> - [sqlite-vector](https://github.com/sqliteai/sqlite-vector) **1.0.0** (Elastic 2.0 — see §11 licensing note) — vector search extension, required by sqlite-memory
> - [sqlite-sync](https://github.com/sqliteai/sqlite-sync) **1.1.2** (Elastic 2.0) — CRDT offline-first sync (`cloudsync` extension), SQLite Cloud / Postgres / Supabase backends
> - [vectors.space](https://vectors.space) — free remote embedding API (OpenAI-compatible endpoint, `POST /v1/embeddings`), used when no local GGUF model is configured
> - SQLite Cloud — free managed SQLite instance for sync (dashboard project already created: `memory.sqlite`)

## 1. Scope

### In scope

Turn AiRaccon from a scaffold MCP server into an **agent memory management MCP server**: a
thin MCP adapter over the sqlite-memory SQL surface, packaged as the existing `ai-raccon`
dotnet tool, that gives AI agents a persistent, project-scoped, searchable memory with:

- **Multi-project / multi-agent operation** — every memory operation is keyed by a mandatory
  `project_id`; any number of agents may work on any number of projects concurrently.
- **Workspace sandboxing** — an agent working in a worktree (ai-badger `worktree-agent-isolation`
  style) writes to an isolated workspace context, reads the project's committed memory, and
  decides what to keep when the workspace finishes (inbox/outbox + consolidation).
- **Local-first defaults** — local SQLite DB per project, local GGUF embedding model by
  default; remote embeddings (vectors.space) and cloud sync (SQLite Cloud via sqlite-sync)
  are opt-in configuration.
- **Extensibility layer** — a first-class extension pipeline designed in from day one, with
  two first-party extensions: **memory rating** (score by retrieval frequency) and **memory
  degradation** (time/rating-based removal of low-value memories).
- **Agent-facing usage guidance** — MCP prompts that teach the agent the protocol: provide
  `project_id`, track `isolated` state, use inbox/outbox, and when/how to consolidate.
- **ai-badger integration** — ship the server as the framework's opinionated, multiplatform
  memory system: an agent skill documenting the discipline, an mcp-index catalog entry, and
  scaffolding wiring for agent configs.

### Out of scope (V1)

- A REST/HTTP management API separate from MCP (the MCP server is the only client surface).
- Per-agent authentication/authorization beyond a provenance `agent_id` parameter.
- A hosted/multi-tenant cloud service (SQLite Cloud is used purely as a sync endpoint).
- Web UI, dashboards, or analytics.
- Non-SQLite storage backends.
- Cross-replica search (search is always local; sync merges content, embeddings stay local).

## 2. Requirements

Prefix `FR-MEM` (feature: memory). Tagged **[V1]** (this issue) or **[LATER]**.

| Id | Requirement | Acceptance sketch |
|---|---|---|
| FR-MEM-1.1 | The server exposes memory tools over MCP (stdio by default, HTTP opt-in per `MCP_TRANSPORT`), following the existing dual-transport wiring in `Program.cs`. | Both transports start; tools listed via MCP `tools/list`. |
| FR-MEM-1.2 | Every memory tool requires a `project_id` string parameter; no operation is performed without one. | Missing `project_id` → tool error; valid id → operation runs. |
| FR-MEM-1.3 | Each project owns a dedicated SQLite database file under the configured data root; project databases never share a file. | Two projects produce two distinct files; writes to one never appear in the other. |
| FR-MEM-1.4 | Memory is partitioned inside the project DB by **context**: committed project memory lives in context `project:<project-id>`; workspace scratch memory lives in context `workspace:<workspace-id>`. | Search with a context filter returns only matching rows (verified via `memory_search` context column). |
| FR-MEM-1.5 | An agent may begin a workspace (`workspace_begin`), which returns a `workspace_id`; while `isolated=true`, writes land in the workspace context and reads span project + workspace contexts. | Write with isolated=true; search returns both project and workspace rows; non-isolated search returns project rows only. |
| FR-MEM-1.6 | The agent can consolidate a finished workspace: promote a selected subset (or all) of workspace entries into the project context, then remove the workspace context. | After `workspace_consolidate`, promoted hashes are searchable in project context; workspace context is gone; discarded hashes are deleted. |
| FR-MEM-1.7 | The agent can discard a workspace without promoting anything; the workspace context and its rows are removed. | `workspace_discard` → workspace context empty; project memory unchanged. |
| FR-MEM-1.8 | Memory writes support both raw text and file/directory ingestion, mirroring `memory_add_text` / `memory_add_content` / `memory_add_directory`. | Text entry searchable; directory ingest indexes markdown files with relative paths. |
| FR-MEM-1.9 | Hybrid search (`memory_search`) exposes `query`, `limit`, `min_score`, and `context`/workspace scoping; results carry hash, path, snippet, ranking, seq. | Search returns ranked snippets above threshold; context filter narrows. |
| FR-MEM-1.10 | Deletion is available per-hash and per-context (`memory_delete`, `memory_delete_context`). | Deleting a hash removes its chunks/FTS rows; deleting a context removes all its entries. |
| FR-MEM-1.11 | Embedding configuration is per-project and persisted (`memory_set_model`); default provider is `local` with a user-configured GGUF model path; `vectors.space` remote is supported via API key. | `memory_configure` persists provider/model; new connections reuse it; remote requires API key per connection. |
| FR-MEM-1.12 | Local embeddings are the default when a GGUF model path is configured; without any model configured, writes use deferred embeddings (`defer_embeddings=1`) and report pending count. | Write with no model → entry stored, `indexed:false`, `memory_pending_count()` > 0; after model configured, `memory_embed_pending` indexes it. |
| FR-MEM-1.13 | A first-class extension pipeline (`IMemoryExtension`) wraps write/search/delete/consolidate/sweep with ordered hooks; extensions are DI-registered and config-driven. | Two registered extensions both run their hooks in registration order on a write. |
| FR-MEM-1.14 | A **rating extension** maintains retrieval-frequency metadata (access count, last-accessed, computed rating) in a local-only meta table keyed by content hash; search hits increment the counter. | Searching an entry twice raises its access count and rating; meta rows are not synced. |
| FR-MEM-1.15 | A **degradation extension** removes entries whose rating falls below a threshold and whose age exceeds a TTL; a sweep tool runs it on demand with a `dry_run` mode. | Sweep with dry_run lists candidates; sweep for real deletes them; young/highly-rated entries survive. |
| FR-MEM-1.16 | Sync is opt-in per project: with `AIRACCON_SQLITECLOUD_DB_ID` and `AIRACCON_SQLITECLOUD_API_KEY` set, `memory_sync` performs push/pull via sqlite-sync and reindexes merged content. | After sync, remote entries are searchable locally; workspace contexts are never synced. |
| FR-MEM-1.17 | MCP prompts (`memory-usage-guide`, `workspace-consolidation-guide`) describe the project-id / isolation / inbox-outbox / consolidation protocol to the calling agent. | Prompts listed via MCP `prompts/list`; content covers all five protocol points. |
| FR-MEM-1.18 | The ai-badger framework ships an `agent-memory` skill teaching the protocol and an mcp-index catalog entry for the server's tools. | Skill file exists in framework common skills; catalog `features/<stack>/mcp/ai-raccon/tools.json` tags every tool. |
| FR-MEM-1.19 | The server runs on win-x64, win-arm64, osx-arm64, linux-x64, linux-arm64, linux-musl-x64 (existing `RuntimeIdentifiers`); native extensions are provisioned per-RID at first run. | Server starts and serves tools on the supported host RIDs with extensions loaded. |
| FR-MEM-1.20 | No credentials in tracked files: sync/embedding keys come only from environment variables or runtime config. | Repo scan finds no secrets; docs use `AIRACCON_*` placeholders. |

## 3. User flows

Text interaction trees. "Agent" is the LLM calling the MCP server; "User" is the human.

### 3.1 Daily memory usage (non-isolated)

```
Agent (any task start, project P):
  1. Agent asks "what do we know about X?" → calls memory_search(query=X, project_id=P)
  2. Server: hybrid search over context 'project:P' → ranked snippets + hashes
  3. Agent: if result relevant, optionally memory_write(project_id=P, content=...)
  4. Server: memory_add_text(content, 'project:P') → dedup by content hash, embed, index
  5. Agent: calls memory_stats(project_id=P) to check counts when useful
```

### 3.2 Workspace sandbox lifecycle (ai-badger worktree)

```
Agent starts task in a worktree (isolated=true):
  1. memory_workspace_begin(project_id=P, agent_id=A) → workspace_id=W
  2. Agent works; every note goes to outbox:
       memory_write(project_id=P, workspace_id=W, isolated=true, content=...)
     Server: memory_add_text(content, 'workspace:W')  [NOT synced]
  3. Agent researches with full visibility:
       memory_search(query, project_id=P, workspace_id=W)
     Server: search over 'project:P' ∪ 'workspace:W'
  4. Workspace finishes. Agent reviews its outbox:
       memory_workspace_status(project_id=P, workspace_id=W)
       → list of workspace entries (hash, path, snippet, created)
  5. Agent decides: keep valuable entries, drop noise.
       memory_workspace_consolidate(project_id=P, workspace_id=W, keep=[h1,h2])
     Server: for each kept hash → memory_add_content(path, value, 'project:P')
             then memory_delete_context('workspace:W')
  6. Alternative: memory_workspace_discard(project_id=P, workspace_id=W)
       Server: memory_delete_context('workspace:W')
```

### 3.3 Degradation sweep (rating lifecycle)

```
Agent or scheduled caller:
  1. memory_sweep(project_id=P, dry_run=true) → [h1 (rating 0.12, age 40d), h2 (0.30, 21d)]
  2. Agent reviews candidates; runs memory_sweep(project_id=P, dry_run=false)
  3. Server: deletes entries with rating < threshold AND age > TTL;
             reports deleted hashes; young/rated entries survive
```

### 3.4 Cloud sync

```
User sets AIRACCON_SQLITECLOUD_DB_ID + AIRACCON_SQLITECLOUD_API_KEY.
Agent (or user) calls memory_sync(project_id=P):
  1. Server: memory_enable_sync('project:P')   [workspace contexts excluded]
  2. Server: cloudsync_network_init(dbId); cloudsync_network_set_apikey(key)
  3. Server: cloudsync_network_sync() twice (send + receive), then memory_reindex()
  4. Server: reports sent/received counts + reindexed rows
```

## 4. MCP tool and prompt contract

The server's "API surface" is the MCP tool set, mapped 1:1 onto the sqlite-memory SQL
functions through a `IMemoryStore` port (see §6). Wire shapes are illustrative JSON.

### 4.1 Tools

| Tool | Parameters | Returns | Backing sqlite-memory call |
|---|---|---|---|
| `memory_write` | `project_id` (req), `content` (req), `workspace_id`?, `isolated`? (bool), `agent_id`?, `context`? (custom label) | `{hash, path, context, created_at, indexed}` | `memory_add_text(content, ctx)` where `ctx = workspace:W if isolated else project:P` |
| `memory_search` | `project_id` (req), `query` (req), `workspace_id`?, `limit`? (default 20), `min_score`? (default 0.7) | `{results:[{hash, seq, ranking, path, snippet}], project_id}` | `SELECT ... FROM memory_search WHERE query=? [AND context=?]` |
| `memory_list` | `project_id` (req), `context`? | `{files: <JSON tree from memory_list_files()>}` | `memory_list_files()` |
| `memory_stats` | `project_id` (req) | `{entries, pending, contexts}` | `memory_pending_count()` + `SELECT count(*) FROM dbmem_content` |
| `memory_delete` | `project_id` (req), `hash` (req) | `{deleted: 0\|1}` | `memory_delete(hash)` |
| `memory_delete_context` | `project_id` (req), `context` (req) | `{deleted: n}` | `memory_delete_context(ctx)` |
| `memory_ingest_file` | `project_id` (req), `path` (req), `context`? | `{indexed: 1}` | `memory_add_file(path, ctx)` |
| `memory_ingest_directory` | `project_id` (req), `path` (req), `context`? | `{scanned: n}` | `memory_add_directory(path, ctx)` |
| `memory_configure` | `project_id` (req), `provider`? (`local`\|remote name), `model`? (GGUF path or remote model id), `api_key`? (env fallback) | `{provider, model, engine: "local"\|"remote"}` | `memory_set_model` / `memory_set_apikey` |
| `memory_embed_pending` | `project_id` (req), `limit`? | `{processed: n, pending: n}` | `memory_embed_pending(n)` |
| `memory_workspace_begin` | `project_id` (req), `agent_id`?, `name`? | `{workspace_id, context}` | allocates `workspace:<uuid>` |
| `memory_workspace_status` | `project_id` (req), `workspace_id` (req) | `{entries:[{hash, path, snippet, created_at}], count}` | query `dbmem_content WHERE context='workspace:W'` |
| `memory_workspace_consolidate` | `project_id` (req), `workspace_id` (req), `keep` (req: array of hashes or `"all"`) | `{promoted: n, discarded: n}` | `memory_add_content(path,value,'project:P')` per kept hash, then `memory_delete_context('workspace:W')` |
| `memory_workspace_discard` | `project_id` (req), `workspace_id` (req) | `{discarded: n}` | `memory_delete_context('workspace:W')` |
| `memory_sweep` | `project_id` (req), `dry_run`? (bool, default true) | `{candidates:[{hash, rating, age_days}], deleted:[...]}` | degradation extension policy |
| `memory_sync` | `project_id` (req) | `{sent, received, reindexed}` | `memory_enable_sync('project:P')` + `cloudsync_*` + `memory_reindex()` |

Notes:
- `project_id` on **every** tool — the server never guesses a project (FR-MEM-1.2). This follows
  the MCP v2 stateless guidance: the model threads an explicit handle across calls instead of
  the server keeping session state.
- `isolated` is the agent's declaration that it is working in a worktree; the server enforces
  write routing and search scoping from it. `workspace_id` is minted by `memory_workspace_begin`
  and echoed back by the agent (opaque handle, like `requestState` in MRTR).
- `agent_id` is provenance only (recorded in the local meta table, see §5.3). No auth.

### 4.2 Prompts

| Prompt | Arguments | Purpose |
|---|---|---|
| `memory-usage-guide` | `project_id`? | Protocol for the agent: always pass `project_id`; check `isolated` state (worktree) before writing; write durable knowledge, search before asking the user; never write raw chatter. |
| `workspace-consolidation-guide` | `workspace_id`?, `project_id`? | Ritual for finishing a workspace: list outbox entries, promote durable facts, drop noise, then discard/consolidate. Called by the agent before declaring a task done. |

Prompts are implemented with the SDK v2 prompt attribute API and registered in
`Program.cs` alongside tools.

## 5. Storage and data model

### 5.1 File layout

```
<data-root>/                    # default ~/.ai-raccon (local scope) or <project>/.ai-raccon (project scope)
  projects/
    <project-id>/               # one directory per project; project-id is URL-safe slug
      memory.db                 # sqlite-memory database (dbmem_content, dbmem_vault, dbmem_vault_fts, dbmem_settings)
      raccon_meta.db            # local-only metadata (rating, provenance) — see §5.3
  extensions/<rid>/             # provisioned native extensions per platform (vector, memory, cloudsync)
  models/                       # downloaded GGUF embedding model (optional, user-provided path allowed)
```

Data root resolution: `AIRACCON_DATA_ROOT` env var → local default `~/.ai-raccon` →
project-scoped `.ai-raccon/` when installed as a project tool. Each project DB is created
on first access with `memory_is_enabled()` check + schema init (sqlite-memory 1.0+ schema).

### 5.2 Context naming convention

| Context | Meaning | Synced? |
|---|---|---|
| `project:<project-id>` | committed, durable project memory (inbox) | yes |
| `workspace:<workspace-id>` | sandboxed workspace scratch memory (outbox) | never |
| custom (`memory_write(context=...)`) | user-defined labels, e.g. `docs:api` | yes (unless excluded) |

Sync scope is `memory_enable_sync('project:<project-id>')` — exactly the committed context;
workspace scratch stays local until consolidated (FR-MEM-1.16). `memory_search` supports the
`context` hidden filter column, which is how read scoping is implemented.

### 5.3 Local-only meta table (`raccon_meta.db`)

sqlite-memory's `dbmem_content` schema is fixed and is the CRDT-synced table — we must not
add columns to it (schema hash must match across replicas). All AiRaccon-owned metadata lives
in a separate local-only database:

```sql
CREATE TABLE entries (
  hash        TEXT PRIMARY KEY,     -- sqlite-memory content hash (16 hex)
  project_id  TEXT NOT NULL,
  context     TEXT NOT NULL,        -- project:<id> | workspace:<id> | custom
  agent_id    TEXT,                 -- provenance, from memory_write(agent_id)
  created_at  INTEGER NOT NULL,     -- unix epoch
  access_count INTEGER NOT NULL DEFAULT 0,
  last_accessed_at INTEGER,
  rating      REAL NOT NULL DEFAULT 0.5,
  ttl_days    INTEGER               -- per-entry override for degradation
);
CREATE INDEX idx_entries_project ON entries(project_id, context);
```

This table is the substrate for the rating and degradation extensions (§6.3) and for
`workspace_workspace_status` provenance. It is never synced (mirrors sqlite-memory's own
"embeddings and provenance are local-only" rule).

## 6. Architecture

### 6.1 Project layout (clean layering)

```
src/AiRaccon.Core/            # NEW class library — pure domain, zero infra deps
  Memory/MemoryEntry.cs        # record: hash, path, context, value, created_at
  Memory/MemorySearchResult.cs # record: hash, seq, ranking, path, snippet
  Memory/Workspace.cs          # record: id, project_id, context, status
  Memory/IMemoryStore.cs       # port: the sqlite-memory surface (thin, SQL-shaped)
  Rating/RatingPolicy.cs       # pure: rating = f(access_count, last_accessed_at, age)
  Rating/IMemoryExtension.cs   # extension contract + hook context records
  Degradation/DegradationPolicy.cs  # pure: candidate selection (rating < thr && age > ttl)
  Common/ContextNaming.cs      # pure: project/workspace context string builders
src/AiRaccon.Infrastructure/   # NEW class library — sqlite-memory adapter + provisioning
  Sqlite/SqliteMemoryStore.cs      # IMemoryStore over Microsoft.Data.Sqlite + LoadExtension
  Sqlite/MetaStore.cs              # raccon_meta.db CRUD
  Provisioning/ExtensionProvisioner.cs  # download/verify per-RID vector/memory/cloudsync
  Sync/SyncService.cs              # sqlite-sync orchestration (enable_sync, network_*, reindex)
src/AiRaccon/                  # MCP server — thin (existing project)
  Tools/MemoryTools.cs         # NEW: 16 tools, 1:1 to IMemoryStore / services
  Tools/WorkspaceTools.cs      # NEW: workspace_* tools (or merged into MemoryTools)
  Prompts/MemoryPrompts.cs     # NEW: memory-usage-guide, workspace-consolidation-guide
  Program.cs                   # MODIFIED: register Core/Infrastructure, WithToolsFromAssembly, WithPrompts
tests/AIRaccon.Tests/          # existing xunit.v3 + Shouldly project
  Domain/RatingPolicyTests.cs, DegradationPolicyTests.cs, ContextNamingTests.cs
  Store/SqliteMemoryStoreTests.cs   # integration, real extensions, temp DB
  Tools/MemoryToolsTests.cs         # tools against a fake IMemoryStore (existing pattern)
  Sync/SyncServiceTests.cs          # orchestration with stubbed network; real cloud = manual
```

MCP stays thin: `Tools/` classes only marshal parameters ↔ `IMemoryStore`/service calls and
format results. All policy (rating, degradation, consolidation selection) lives in `Core`;
all SQL and extension loading lives in `Infrastructure`. The MCP layer never writes SQL.

### 6.2 Extension pipeline (the extensibility layer)

```csharp
public interface IMemoryExtension
{
    string Name { get; }
    Task OnWriteAsync(WriteContext ctx, CancellationToken ct);
    Task OnSearchAsync(SearchContext ctx, CancellationToken ct);      // rating: bump hits
    Task OnDeleteAsync(DeleteContext ctx, CancellationToken ct);
    Task<IReadOnlyList<SweepCandidate>> OnSweepAsync(SweepContext ctx, CancellationToken ct);
    Task OnConsolidateAsync(ConsolidationContext ctx, CancellationToken ct);
}
```

- Registered in DI (ordered); `MemoryExtensionHost` invokes hooks around every store operation.
- Hooks are **async and non-blocking**: rating updates happen after the search result returns
  (fire-and-forget with a background queue so latency is unaffected).
- First-party extensions:
  - `RetrievalRatingExtension` (FR-MEM-1.14) — increments `access_count`/`last_accessed_at`
    for hashes present in search results, recomputes `rating` via `RatingPolicy`.
  - `DegradationExtension` (FR-MEM-1.15) — on `memory_sweep`, runs `DegradationPolicy`
    (rating < threshold && age > ttl, with per-entry `ttl_days` override), deletes via
    `memory_delete`, honors `dry_run`.
- Third-party extensions implement the interface and are picked up by assembly scanning
  (`WithExtensionsFromAssembly`), mirroring the SDK's `WithToolsFromAssembly`.

### 6.3 Concurrency

Multiple agents → multiple server processes → multiple SQLite connections on the same
project DB:

- `PRAGMA journal_mode=WAL`, `PRAGMA busy_timeout=5000` on every connection.
- sqlite-memory functions are SAVEPOINT-transactional and content-hash-deduplicated, so
  concurrent identical writes are safe no-ops.
- `raccon_meta.db` uses WAL too; rating bumps are `INSERT ... ON CONFLICT DO UPDATE`.
- Sync calls are serialized per project with a `SemaphoreSlim` (cloudsync is not
  concurrency-safe).

## 7. Error and edge cases

| Scenario | Detection | Behavior | Recovery |
|---|---|---|---|
| Missing `project_id` | tool input validation | error `invalid-params: project_id is required` | agent retries with id |
| Unknown project dir | first access | directory + empty DB created lazily | n/a |
| Unknown `workspace_id` | `workspace_status`/consolidate | error `workspace-not-found` | agent begins a new workspace |
| Embedding model not configured | write/search | writes stored deferred (`indexed:false`); search returns FTS-only results; `memory_stats` reports pending | `memory_configure` then `memory_embed_pending` |
| Remote embedding without API key | `memory_configure`/write | error `embedding-api-key-missing`; never writes the key to the DB | set `AIRACCON_VECTORSSPACE_API_KEY` or pass `api_key` |
| Sync without credentials | `memory_sync` | error `sync-not-configured` | set `AIRACCON_SQLITECLOUD_DB_ID`/`_API_KEY` |
| Native extension missing for RID | startup provisioning | provisioner downloads pinned versions + SHA-256 check; offline → clear error with expected path | rerun with network, or pre-provision into data root |
| sqlite-memory schema version mismatch | `memory_is_enabled()` | error with rebuild instruction (sqlite-memory 1.0+ schema) | rebuild DB |
| Concurrent agents writing same content | content-hash dedup | second write is a no-op, returns same hash | n/a |
| Consolidation with missing hash | `workspace_consolidate` | skipped hash reported in `discarded` | agent re-checks status |
| Degradation deleting needed entry | `dry_run` default | candidates listed, nothing deleted | agent reviews before real run; per-entry `ttl_days` override |
| Cloud sync merge | post-merge | `memory_reindex()` refreshes hashes/embeddings; stale local embeddings replaced | automatic |
| Extension load failure on Windows | `LoadExtension` | clear error incl. PATH/LD_LIBRARY_PATH note (Microsoft.Data.Sqlite caveat) | adjust PATH or provision bundled deps |

## 8. Testing strategy

| Layer | What to test | How | Where |
|---|---|---|---|
| Domain (pure) | `RatingPolicy` (access bumps raise rating; age decays), `DegradationPolicy` (threshold/TTL/dry-run selection), `ContextNaming` (project/workspace/custom strings), consolidation keep-selection | xunit.v3 + Shouldly, table-driven, no infra | `tests/Domain/*` |
| Store (integration) | `SqliteMemoryStore` against **real** sqlite-memory 1.3.5 + vector 1.0.0: write/dedup, search+context filter, delete, file/dir ingest, deferred embeddings, meta-table rating bumps | temp DB per test, extensions provisioned by the same provisioner; `[Trait("Category","Integration")]` | `tests/Store/*` |
| Tools (MCP layer) | Parameter validation (missing project_id), 1:1 mapping, result formatting; prompts content | fake `IMemoryStore` (existing `RandomNumberToolsTests` style) | `tests/Tools/*` |
| Sync | Orchestration sequence with stubbed network functions; real SQLite Cloud round-trip as a **manual** test (credentials) | unit + manual checklist | `tests/Sync/*` |
| Provisioning | Per-RID URL selection, checksum verification, offline failure message | unit with mocked HTTP + integration on host RID | `tests/Provisioning/*` |

Gates: `dotnet build` and `dotnet test` from repo root (existing commands); TreatWarningsAsErrors
is on — new code must be warning-free.

## 9. Acceptance criteria

Numbered, each traceable to a requirement.

1. **AC-1 (FR-MEM-1.1, 1.19)** — `dotnet run` starts the server on stdio; `MCP_TRANSPORT=http`
   starts the HTTP transport; `tools/list` shows all 16 tools and 2 prompts on both.
2. **AC-2 (FR-MEM-1.2, 1.3, 1.4)** — calling any tool without `project_id` errors; two projects
   write into two distinct DB files; `memory_search(context='project:A')` never returns rows
   from project B.
3. **AC-3 (FR-MEM-1.5, 1.6, 1.7)** — workspace flow end-to-end: begin → isolated writes →
   search spanning project+workspace → consolidate `keep=[h1]` → h1 searchable in project
   context, workspace context empty, h2 gone; discard variant removes everything.
4. **AC-4 (FR-MEM-1.8, 1.9, 1.10)** — text write + directory ingest both searchable; search
   honors limit/min_score/context; per-hash and per-context deletes remove chunks/FTS rows.
5. **AC-5 (FR-MEM-1.11, 1.12)** — with a GGUF path configured, writes embed locally; without
   any model, writes are deferred (`indexed:false`, pending>0) and `memory_embed_pending`
   indexes them after configuration.
6. **AC-6 (FR-MEM-1.13, 1.14)** — two registered extensions run hooks in order; searching an
   entry twice increments its meta access count and raises its rating.
7. **AC-7 (FR-MEM-1.15)** — `memory_sweep(dry_run=true)` lists low-rated/aged entries without
   deleting; real run deletes exactly those and preserves young/highly-rated ones.
8. **AC-8 (FR-MEM-1.16)** — with cloud env vars set, `memory_sync` sends/receives and reindexes;
   workspace contexts are absent from the sync payload; without env vars it errors cleanly.
9. **AC-9 (FR-MEM-1.17, 1.18)** — `prompts/list` exposes the two guides covering project-id,
   isolation, inbox/outbox, consolidation; the ai-badger skill + mcp-index catalog entry are
   present in the framework repo.
10. **AC-10 (FR-MEM-1.20)** — `grep -riE 'sqlitecloud.*key|vectorspace.*key' src tests` finds
    only env-var references; no secrets in tracked files.

## 10. Implementation notes

- **Packages to add** (central version management — `Directory.Packages.props` only, never
  per-project `Version`): `Microsoft.Data.Sqlite` (net10.0, supports `LoadExtension` with
  `EnableExtensions=true`), `SQLitePCLRaw.bundle_e_sqlite3` (contains FTS5 + extension
  loading; `bundle_winsqlite3` must be avoided on Windows — it cannot load extensions).
- **Extension provisioning**: sqlite-memory/vector/sync publish per-platform binaries
  (macos-arm64, linux-{x64,arm64,musl}, windows-x86_64 …). `ExtensionProvisioner` downloads
  pinned versions (memory 1.3.5, vector 1.0.0, sync 1.1.2) + SHA-256 manifest into
  `<data-root>/extensions/<rid>/` on first run. sqlite-memory builds come in
  `local`/`remote`/`full` flavors — pick **full** (or `local` for embedding-only deployments).
- **MCP SDK v2**: stateless HTTP is default (`HttpServerTransportOptions.Stateless`); tools
  use `[McpServerTool]` + `WithToolsFromAssembly()`; prompts via the prompt attribute API.
  MRTR is not required for V1 (consolidation is a plain tool call); the `requestState`
  pattern is the model for echoing `workspace_id`.
- **No hand-rolled security**: embedding/sync API keys are read from env/`IConfiguration`
  only, passed per-connection to `memory_set_apikey`/`cloudsync_network_set_apikey`
  (both connection-scoped by design — never persisted).
- **Logging**: nested static partial `Log` class with `[LoggerMessage]` methods
  (high-performance-logging invariant), stderr-only for stdio transport.
- **`InternalsVisibleTo`** already set for `AiRaccon.Tests`; Core/Infrastructure projects
  need the same for their own test access.
- **Packaging**: keep `PackAsTool` + `.mcp/server.json`; the server.json's
  `environmentVariables` array should declare `AIRACCON_*` so MCP clients surface them.

## 11. Open questions

| # | Question | Lean | Status |
|---|---|---|---|
| OQ-1 | sqlite-vector and sqlite-sync are **Elastic License 2.0** — free for OSS/non-production, commercial for production/managed. Is the ai-raccon distribution OSS-licensed? | Treat as OSS for now; add a LICENSE note + contact path in README | open |
| OQ-2 | Bundle extensions inside the NuGet tool package per-RID vs download-on-first-run? | Download-on-first-run (sqlmem CLI precedent, keeps tool package small); bundling as a later packaging option | open |
| OQ-3 | `linux-musl-x64` has no sqlite-memory release binary — drop the RID or build the extension for musl? | Keep RID, fail provisioning with a clear message; revisit when upstream ships musl | open |
| OQ-4 | Should consolidation be an MRTR round-trip (server asks the agent which hashes to keep) instead of a plain tool call? | Plain tool call for V1; MRTR is a natural later upgrade since `workspace_id` already threads like `requestState` | open |
| OQ-5 | Single memory bank across all agents (future goal): one shared SQLite Cloud DB with RLS per project, or per-project cloud DBs? | sqlite-sync RLS supports tenant-scoped rows in one DB — defer decision until sync volume demands it | open |
| OQ-6 | Default rating formula: linear decay vs half-life? | Half-life on `last_accessed_at` (`rating = base * 0.5^(age/half_life)`) + access-count multiplier — pure policy, adjustable without schema change | open |
| OQ-7 | Where does the GGUF model come from by default (download from HuggingFace on first run vs user-provided path)? | Download-on-first-run from a pinned HF repo (e.g. nomic-embed-text-v1.5.Q8_0.gguf) with a size warning (~137 MB) | open |

## 12. Files to create / modify

### New files

| File | Purpose |
|---|---|
| `src/AiRaccon.Core/AiRaccon.Core.csproj` | pure domain library (no external deps) |
| `src/AiRaccon.Core/Memory/*.cs` | MemoryEntry, MemorySearchResult, Workspace, IMemoryStore, SearchQuery |
| `src/AiRaccon.Core/Rating/*.cs` | RatingPolicy, IMemoryExtension, hook context records |
| `src/AiRaccon.Core/Degradation/*.cs` | DegradationPolicy, SweepCandidate, threshold/TTL options |
| `src/AiRaccon.Core/Common/ContextNaming.cs` | project/workspace/custom context string builders |
| `src/AiRaccon.Infrastructure/AiRaccon.Infrastructure.csproj` | sqlite adapter + provisioning + sync |
| `src/AiRaccon.Infrastructure/Sqlite/*.cs` | SqliteMemoryStore, MetaStore, connection factory (WAL, busy_timeout) |
| `src/AiRaccon.Infrastructure/Provisioning/*.cs` | ExtensionProvisioner, rid mapping, checksum manifest |
| `src/AiRaccon.Infrastructure/Sync/SyncService.cs` | sqlite-sync orchestration |
| `src/AiRaccon/Tools/MemoryTools.cs` | 16 `[McpServerTool]` methods |
| `src/AiRaccon/Prompts/MemoryPrompts.cs` | 2 `[McpServerPrompt]` guides |
| `tests/AIRaccon.Tests/Domain/*.cs` | rating/degradation/context/consolidation unit tests |
| `tests/AIRaccon.Tests/Store/*.cs` | integration tests against real extensions |
| `tests/AIRaccon.Tests/Tools/*.cs` | tool validation/mapping tests (fake IMemoryStore) |
| `tests/AIRaccon.Tests/Provisioning/*.cs` | provisioner unit tests |
| `docs/features/README.md` | feature table row for this dossier (establishes the dossier convention) |

### Modified files

| File | Specific changes |
|---|---|
| `src/AiRaccon/AiRaccon.csproj` | ProjectReferences to Core + Infrastructure; remove `RandomNumberTools` if replaced |
| `src/AiRaccon/Program.cs` | register Core/Infrastructure services, `WithToolsFromAssembly()`, `WithPrompts...()`, keep dual-transport selector |
| `src/AiRaccon/Tools/RandomNumberTools.cs` | delete (superseded scaffold sample) or keep for demo — decide in PR |
| `src/AiRaccon/.mcp/server.json` | real package id/description, `AIRACCON_*` environmentVariables |
| `src/AiRaccon/README.md` | memory server usage, env vars, extension provisioning, licensing note |
| `Directory.Packages.props` | `Microsoft.Data.Sqlite`, `SQLitePCLRaw.bundle_e_sqlite3` versions (central only) |
| `tests/AIRaccon.Tests/AiRaccon.Tests.csproj` | reference Core/Infrastructure; integration trait config |
| `.ai-badger/config.json` | project summary/domain updated to memory server |

### Not modified (with reason)

| File | Reason |
|---|---|
| `src/AiRaccon/McpTransportSelector.cs` | transport selection unchanged |
| `tests/AIRaccon.Tests/McpTransportSelectorTests.cs` | still valid |
| `tests/AIRaccon.Tests/RandomNumberToolsTests.cs` | deleted only with its tool |

## 13. ai-badger integration (framework side, separate repo)

The ai-badger framework (in `~/RiderProjects/ai-badger`) ships the opinionated memory
discipline around this server:

1. **`agent-memory` skill** (new, `features/common/skills/agent-memory/`) — teaches agents:
   - always pass `project_id` (from scaffolding/bl-project config);
   - detect worktree context (`isolated=true`) and keep writes in the workspace outbox;
   - search before asking, write durable facts, never log raw chatter;
   - consolidate at workspace end: promote durable facts, drop noise.
2. **mcp-index catalog entry** — `features/<stack>/mcp/ai-raccon/tools.json` tagging every
   tool (`[memory]`, `[workspace]`, `[sync]`, `[rating]`) so the `pre_llm_call` hook
   recommends the right tool per turn.
3. **Scaffolding wiring** — declare the `ai-raccon` stdio server in generated agent configs
   (mcp.json / `.mcp/server.json`) and default `AIRACCON_DATA_ROOT` to the framework's
   per-project data dir, so multiple ai-badger agents on one machine share project memory.
4. **Multi-platform promise** — the same dotnet tool + per-RID extension provisioning covers
   macOS, Windows, Linux; the skill is platform-agnostic.

This dossier is the server-side contract; the framework-side changes are tracked as separate
work items in the ai-badger repo once this spec is accepted.
