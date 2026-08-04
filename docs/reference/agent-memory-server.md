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
  age exceeds 30 days. `shared` entries are never swept.
- **`memory_configure`:** `provider` is `local` (bundled int8 ONNX model in-process)
  or `openai` (any OpenAI-compatible endpoint). For `openai`, `model` and an API key
  (`apiKey` arg or `AIRACCOON_OPENAI_API_KEY`) are required — an explicit `apiKey`
  parameter takes precedence over the environment variable. Until an engine is
  configured, writes are stored deferred (`memory_stats.pending > 0`) and only become
  searchable after `memory_embed_pending`. Changing the engine re-embeds the bank.

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
| `AIRACCOON_SQLITECLOUD_DB_ID` | SQLite Cloud managed database id (sync) |
| `AIRACCOON_SQLITECLOUD_API_KEY` | SQLite Cloud API key (sync) |
| `AIRACCOON_OPENAI_API_KEY` | API key for `provider=openai` embeddings |
| `AIRACCOON_EMBEDDING_MODEL` | Custom ONNX model path for `provider=local` (default: the bundled model) |

Credentials are read from the environment only.

## Local embedding model

Local embeddings run in-process on ONNX Runtime over the small int8
all-MiniLM-L6-v2 model (dimension 384, mean-pool + L2-normalize) **bundled inside
the tool package** — `memory_configure(provider="local")` needs no sidecar, server
process or download. The binary is gitignored and fetched once by the pinned script
(SHA-256 verified); the tests FAIL (never skip) when it is missing:

```bash
scripts/download-embedding-model.sh          # -> src/AiRaccoon/Models/model_qint8_arm64.onnx + vocab.txt
```

A custom ONNX model path overrides the bundled model via
`memory_configure(provider="local", model="/path/to/model.onnx")` or
`AIRACCOON_EMBEDDING_MODEL`.

## Embedding configuration matrix

`memory_configure` resolves exactly two engines:

| Engine | `provider` | `model` | `baseUrl` | Key | Notes |
|---|---|---|---|---|---|
| Local (bundled ONNX) | `local` | optional ONNX path (default: bundled model) | ignored | none | In-process, offline, no API cost |
| OpenAI-compatible | `openai` | model id (required), e.g. `nomic-embed-text` | optional endpoint (default `https://api.openai.com/v1`) | `apiKey` arg or `AIRACCOON_OPENAI_API_KEY` | Any OpenAI-compatible `/embeddings` backend (LM Studio, Ollama, self-hosted, OpenAI) |

Changing the engine (provider, model or baseUrl) re-embeds the bank with the new
engine.

## Error shapes

Tool errors are returned as MCP tool errors (`CallToolResult.IsError`):

| Condition | Message prefix |
|---|---|
| Missing/blank `projectId` | `invalid-params: project_id is required` |
| Invalid `scope` | `invalid-params: Invalid scope '<x>'` |
| Remote embedding provider without a key | `embedding-api-key-missing: set AIRACCOON_OPENAI_API_KEY or pass api_key for provider 'openai'` |
| Sync without credentials | `sync-not-configured: set AIRACCOON_SQLITECLOUD_DB_ID and AIRACCOON_SQLITECLOUD_API_KEY` — both are required |

## Native extensions

sqlite-memory 1.3.5, sqlite-vector 1.0.0, sqlite-sync 1.1.2 are pinned and provisioned
per RID into `<data-root>/extensions/<rid>/` (e.g. `~/.ai-raccoon/extensions/osx-arm64/`),
SHA-256 verified. `linux-musl-x64` has no sqlite-memory release binary — provisioning
refuses with a clear `ExtensionProvisioningException` naming what is missing.

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
