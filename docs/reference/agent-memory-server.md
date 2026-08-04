# Agent memory server — reference

The ai-raccoon MCP server's complete agent-facing contract: tools, prompts,
environment variables, contexts, and error shapes. Consult this mid-task when
integrating or debugging; see `docs/features/agent-memory/spec-issue-1.md` for the
design rationale and `docs/features/native-memory/spec.json` for the native-store
scope.

The server runs a single SQLite bank (`memory.db`) with a native .NET store:
no sqlite-memory/sqlite-vector/sqlite-sync extensions, no download-on-first-run
provisioning, and no `raccoon_meta.db`. All tables — entries, workspaces, settings,
FTS5, vec0, sync_meta, and sync_tombstones — live in `memory.db` (FR-NM-1).

**Fresh-start note (P11):** this release drops existing-bank migration — the bank
starts clean with the new native schema. A re-hash + re-embed migration path is
deferred to a deployment that needs it (D11).

## Tools (17)

Every tool requires `projectId` (camelCase — all parameters are camelCase). Writes
land in `project:<id>` by default; naming a `workspaceId` routes them into that
workspace's isolated context.

All 17 tools are unchanged in name from the prior release. `memory_configure`
gained a `baseUrl` parameter for any OpenAI-compatible endpoint.

| Tool | Parameters | Returns |
|---|---|---|
| `memory_write` | `projectId`, `content`, `workspaceId?`, `agentId?`, `context?` | `{hash, path, context, createdAt}` |
| `memory_search` | `projectId`, `query`, `scope=all\|project\|shared`, `workspaceId?`, `limit=20`, `minScore=0.7`, `rrfK=60`, `ftsWeight=1`, `vectorWeight=1` | `{results:[{hash, seq, ranking, path, snippet}], projectId}` |
| `memory_list` | `projectId` | `{files: <json tree>}` |
| `memory_stats` | `projectId` | `{entries, pending, contexts}` |
| `memory_share` | `projectId`, `hash` | `{shared: true, context: "shared"}` |
| `memory_delete` | `projectId`, `hash` | `{deleted: 0\|1}` |
| `memory_delete_context` | `projectId`, `context` | `{deleted: n}` |
| `memory_ingest_file` | `projectId`, `path`, `context?` | `{indexed: 0\|1}` |
| `memory_ingest_directory` | `projectId`, `path`, `context?` | `{scanned: n}` |
| `memory_configure` | `projectId`, `provider`, `baseUrl?`, `model?`, `apiKey?` | `{provider, model, engine}` |
| `memory_embed_pending` | `projectId`, `limit?` | `{processed, pending}` |
| `memory_workspace_begin` | `projectId`, `agentId?`, `name?` | `{workspaceId, context}` |
| `memory_workspace_status` | `projectId`, `workspaceId` | `{entries, count}` |
| `memory_workspace_consolidate` | `projectId`, `workspaceId`, `keep` | `{promoted, discarded}` |
| `memory_workspace_discard` | `projectId`, `workspaceId` | `{discarded}` |
| `memory_sweep` | `projectId`, `dryRun=true` | `{candidates, deleted}` |
| `memory_sync` | `projectId` | `{sent, received, reindexed}` |

### Notes on the less obvious tools

- **`scope` values:** `scope=all` (default) searches `shared` + `project:<id>` (+ workspace
  when named); `scope=project` searches `project:<id>` only; `scope=shared` searches the
  `shared` promotion tier only. Workspace scratch is never included in `scope=all` — it is
  only visible to a search that names that `workspaceId`.
- **`memory_share`:** promotes the entry whose `hash` you pass (from a `memory_write`
  or `memory_search` result) into `shared`. It is additive — the source project row
  stays. There is no un-share; `memory_delete` on the shared row's hash removes it from
  `shared`.
- **`memory_configure`:** `provider` is `local` (bundled int8 ONNX all-MiniLM-L6-v2
  in-process, ~23 MB, Apache-2.0, SHA-256 pinned) or `openai` (any OpenAI-compatible
  `baseUrl`, default `https://api.openai.com/v1`). `model` is the model id for openai
  or a custom ONNX path for local; it defaults to the bundled model for local, is
  required for openai. `baseUrl` overrides the endpoint for openai providers.
  `apiKey` takes precedence over `AIRACCOON_OPENAI_API_KEY`; keys are never persisted.
  Changing the engine re-embeds the bank. The `engine` field in the result is the
  stable fingerprint (`local:bundled`, `openai:text-embedding-3-small@<baseUrl>`,
  etc.) — a change triggers the re-embed.
- **`memory_search`:** hybrid fusion from two modalities: FTS5 (keyword) and vec0
  (semantic, when an embedding engine is configured). The two ranked lists are fused
  with Reciprocal Rank Fusion (RRF): each result's score = Σ weight / (k + rank) per
  modality, then normalized so the top result is 1.0 (range 0..1). `rrfK=60` (default),
  `ftsWeight=1`, `vectorWeight=1` (default 1:1). When no engine is configured, search
  degrades to FTS5-only — never crashes.
- **`memory_workspace_consolidate`:** `keep` is an array of hashes to promote, or
  `["all"]` to promote every entry in the workspace. It then deletes the workspace
  context entirely — entries not kept are gone.
- **Workspace lifecycle record:** `memory_workspace_begin` inserts an `Active` row into
  the `workspaces` table inside `memory.db` (no separate meta DB); consolidate and
  discard mark it `Closed` with `closed_at`. A workspace begun but never finished stays
  traceable after a crash.
- **`memory_sweep`:** `dryRun=true` (default) only lists candidates; pass `dryRun=false`
  to delete. An entry is a candidate when its retrieval rating falls below 0.3 and its
  age exceeds 30 days. `shared` entries are never swept. Requires `full` access mode
  when `dryRun=false` (`rw` is sufficient for `dryRun=true`).
- **`memory_sync`:** pushes the committed contexts (`shared` + `project:<id>`) as a
  single `memory-<id>.db` snapshot to S3-compatible object storage (R2, S3, MinIO).
  Uses VACUUM INTO + `PRAGMA quick_check` + If-Match conditional PUT + row merge
  (updated_at last-writer-wins + tombstones). Workspace rows are stripped before
  upload. Requires `AIRACCOON_SYNC_ENDPOINT`, `AIRACCOON_SYNC_BUCKET`,
  `AIRACCOON_SYNC_ACCESS_KEY`, and `AIRACCOON_SYNC_SECRET_KEY`.

## Prompts (2)

| Prompt | Purpose |
|---|---|
| `memory-usage-guide` | Protocol: always pass `project_id`; when a workspace is active, write with its `workspace_id`; writes land in the project by default; promote cross-project knowledge via `memory_share`; search `scope=all` sees shared + project. |
| `workspace-consolidation-guide` | Ritual: list the outbox, promote durable facts, drop noise. |

## Contexts

| Context | Meaning | Synced? | Swept? |
|---|---|---|---|
| `shared` | curated cross-project knowledge — only via `memory_share` | yes | exempt |
| `project:<project-id>` | committed, durable project memory | yes | yes |
| `workspace:<workspace-id>` | sandboxed workspace scratch (outbox) | never | no |
| custom | user-defined labels (`docs:api`, …) | yes | project sweep only |

## Access modes

Three-tier access control (FR-NM-2), enforced at the tool boundary:

| Mode | Reads | Writes | Destructive (delete, sweep, consolidate) |
|---|---|---|---|
| `ro` | ✓ | ✗ | ✗ |
| `rw` (default) | ✓ | ✓ | ✗ |
| `full` | ✓ | ✓ | ✓ |

- The **global default** is `rw`.
- Set `AIRACCOON_ACCESS_MODE=ro|rw|full` to override the global default. The env
  value is seeded into the settings table on first bank open and never overwrites
  an operator-set value.
- A **per-project override** is stored in the settings table under
  `access.mode.project:<id>` — it takes precedence over the global setting.

## Environment variables

| Variable | Purpose |
|---|---|
| `AIRACCOON_DATA_ROOT` | Bank data root (default `~/.ai-raccoon`) |
| `AIRACCOON_INSTALL_SCOPE` | `user` (default) or `project` |
| `AIRACCOON_ACCESS_MODE` | Global access mode seed: `ro`, `rw` (default), or `full` |
| `AIRACCOON_OPENAI_API_KEY` | API key for `provider=openai` embeddings |
| `AIRACCOON_EMBEDDING_MODEL` | Custom ONNX model path overriding the bundled all-MiniLM-L6-v2 |
| `AIRACCOON_SYNC_ENDPOINT` | S3-compatible endpoint URL (sync) |
| `AIRACCOON_SYNC_BUCKET` | S3 bucket name (sync) |
| `AIRACCOON_SYNC_ACCESS_KEY` | S3 access key (sync) |
| `AIRACCOON_SYNC_SECRET_KEY` | S3 secret key (sync) |
| `AIRACCOON_SYNC_REGION` | S3 region (optional) |
| `AIRACCOON_SYNC_OBJECT_KEY` | Custom object key (default `memory-<projectId>.db`) |

Credentials are read from the environment only — never from tracked files.

## Embedding configuration matrix

`memory_configure` accepts two providers with the `IEmbeddingGenerator` abstraction:

| Engine | `provider` | `model` | `baseUrl` | Key | Notes |
|---|---|---|---|---|---|
| Local (ONNX) | `local` | ONNX model path (optional) | — | none | Bundled int8 all-MiniLM-L6-v2 (~23 MB, 384 dims, SHA-256 pinned); `model` overrides it. |
| Remote (OpenAI) | `openai` | model id (required) | any OpenAI-compatible endpoint | `AIRACCOON_OPENAI_API_KEY` or `apiKey` arg | Defaults to `https://api.openai.com/v1`; any compatible endpoint works. |

The local engine runs in-process with ONNX Runtime; no sidecar, no download on first
run, no GGUF/llama.cpp. The model and BERT vocab are bundled inside the tool package
under `Models/`. The `AIRACCOON_EMBEDDING_MODEL` env var overrides the bundled model
path; the bundled model is SHA-256 verified on first use (gate test catches drift).

Changing the engine (new provider, model, or baseUrl) triggers a full re-embed of the
bank's committed entries; the pending queue is left for `memory_embed_pending`.

Writes without a configured engine are stored `embed_state=pending` and become
searchable only after `memory_embed_pending`.

## Hybrid search (FTS5 + vec0 + RRF)

Search combines two retrieval modalities fused with Reciprocal Rank Fusion:

- **FTS5**: external-content index over `entries(value)` uses normalized FTS5
  expressions; a pathological query that trips FTS5 tokenizer limits degrades
  gracefully to the vector list only.
- **vec0**: KNN over the `vec_entries` virtual table (384-dimensional float vectors);
  only active when an embedding engine is configured. Query text is embedded with the
  configured engine at search time.
- **RRF fusion**: each modality produces a ranked candidate list with window
  `max(limit × 3, 100)`; RRF scores are `weight / (k + rank)` summed across
  modalities, then normalized so the top result is 1.0. Defaults: `k=60`,
  `ftsWeight=1`, `vectorWeight=1`. Results are filtered by `minScore` and truncated
  to `limit` after fusion.
- Per-context fusion: each in-scope context produces its own fused batch; batches
  are merged with the `SearchResultMerger` (max fusion score per hash, then
  re-normalize, filter, and truncate).

## Workspaces

Workspaces are first-class entities in `memory.db` with structural isolation
(FR-NM-8):

- The `workspaces` table holds lifecycle state: `id`, `project_id`, `agent_id`,
  `name`, `status` (`Active`/`Closed`), `created_at`, `closed_at`.
- `entries.workspace_id` is an FK to `workspaces.id` with a CHECK constraint
  enforcing XOR: a row must have either a workspace-scoped context (workspace_id
  NOT NULL, scope NULL) or a committed context (workspace_id NULL, scope IN
  ('shared','project','custom')).
- Consolidate promotes kept entries via `memory_add_content` into the project's
  committed context (preserving each entry's logical path), then deletes the
  workspace context atomically via `memory_delete_context`.
- Workspace rows (entries with `workspace_id IS NOT NULL`) are stripped from sync
  snapshots — they never leave the local bank.

## Sync

Own S3-compatible sync replacing the former SQLite Cloud path:

- **Push**: VACUUM INTO a temp snapshot → strip workspace rows → `PRAGMA quick_check`
  → upload with If-Match (CAS) using the last known ETag → record the new ETag in
  `sync_meta`.
- **Pull**: download the remote snapshot → integrity check → ATTACH and row-merge
  (entries via content hash `INSERT OR IGNORE`, settings via LWW on `updated_at`,
  tombstones applied and GC'd below the last pull watermark).
- **Conflict**: 412 on push triggers a re-pull + re-merge + re-push cycle (up to 3
  retries). Exhaustion is a `sync-conflict` error.
- **Tombstones**: `sync_tombstones` records deleted (hash, scope, deleted_at);
  tombstone GC only removes rows below the minimum of both sides' last_pull
  watermark.
- Sync is serialized per project with a `SemaphoreSlim`.
- Workspace rows are never synced.

## Error shapes

Tool errors are returned as MCP tool errors (`CallToolResult.IsError`):

| Condition | Message prefix |
|---|---|
| Missing/blank `projectId` | `invalid-params: project_id is required` |
| Invalid `scope` | `invalid-params: Invalid scope '<x>'` |
| Invalid `provider` | `invalid-params: provider must be 'local' or 'openai', got '<x>'` |
| Openai without model | `invalid-params: model is required for provider 'openai'` |
| Remote embedding provider without a key | `embedding-api-key-missing: set AIRACCOON_OPENAI_API_KEY or pass api_key for provider 'openai'` |
| Sync without credentials | `sync-not-configured: set AIRACCOON_SYNC_ENDPOINT, AIRACCOON_SYNC_BUCKET, AIRACCOON_SYNC_ACCESS_KEY and AIRACCOON_SYNC_SECRET_KEY` |
| Sync auth failure | `sync-auth-failed: verify AIRACCOON_SYNC_ACCESS_KEY and AIRACCOON_SYNC_SECRET_KEY` |
| Sync conflict exhausted | `sync-conflict: remote changed during merge — retry the sync` |
| Sync network error | `sync-network: <message>` |
| Sync corrupt file | `sync-corrupt-file: <message>` |
| Access denied | `access-denied: <tool> requires mode <required> (current <current>)` |

## Deletion and sync semantics

- Deletes are permanent — there is no trash or recovery.
- `memory_delete` targets one hash wherever it lives, including a `shared` row;
  `memory_delete_context` deletes every entry under a context label. Nothing forbids
  targeting `shared` — use it deliberately.
- Deleting a synced context (`shared`, `project:<id>`, custom) removes rows locally;
  the deletion is pushed as a tombstone on the next `memory_sync`, so the removal
  propagates to the cloud copy.
- Workspace contexts are never synced, so `memory_workspace_discard` and consolidation's
  discard have no cloud counterpart.

## Known limitations

- There is no tool to list active workspaces: `memory_workspace_status` needs a
  `workspaceId` you must already hold (keep the value returned by `memory_workspace_begin`).
- No un-share tool exists; see `memory_share` notes above.
- No existing-bank migration (P11): a fresh bank is created; migrating an older
  sqlite-memory format bank is deferred (D11).
