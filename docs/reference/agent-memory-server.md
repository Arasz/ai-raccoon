# Agent memory server — reference

The ai-raccoon MCP server's complete agent-facing contract: tools, prompts,
environment variables, contexts, and error shapes. Consult this mid-task when
integrating or debugging; see `docs/work/features-agent-memory/spec-issue-1.md` for the
design rationale and `docs/work/features-native-memory/spec.json` for the native-store
scope.

The server runs a single SQLite bank (`memory.db`) with a native .NET store:
no sqlite-memory/sqlite-vector/sqlite-sync extensions, no download-on-first-run
provisioning, and no `raccoon_meta.db`. All tables — entries, workspaces, settings,
watches, watch_files, FTS5, vec0, sync_meta, and sync_tombstones — live in
`memory.db` (FR-NM-1).

**Fresh-start note (P11):** this release drops existing-bank migration — the bank
starts clean with the new native schema. A re-hash + re-embed migration path is
deferred to a deployment that needs it (D11).

## Tools (19)

Every tool requires `projectId` (camelCase — all parameters are camelCase). Writes
land in `project:<id>` by default; naming a `workspaceId` routes them into that
workspace's isolated context.

16 memory tools plus 3 file-watcher tools. `memory_configure` and
`memory_set_structure_alpha` were removed by the CLI-config refactor: configuration is
no longer an MCP tool — the CLI verbs are the single config channel (see
[Command-line options](#command-line-options)).

| Tool                           | Parameters                                                                                                                                                  | Returns                                                                                            |
|--------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------|
| `memory_write`                 | `projectId`, `content`, `workspaceId?`, `agentId?`, `context?`, `sourceFile?`, `section?`                                                                   | `{hash, path, context, createdAt}`                                                                 |
| `memory_search`                | `projectId`, `query`, `scope=all\|project\|shared`, `workspaceId?`, `limit=20`, `minScore=0.7`, `rrfK=60`, `ftsWeight=1`, `vectorWeight=1`, `contextLabel?` | `{results:[{hash, seq, ranking, path, snippet, sourceFile?, chunkIndex, totalChunks}], projectId}` |
| `memory_list`                  | `projectId`                                                                                                                                                 | `{files: <json tree>}`                                                                             |
| `memory_stats`                 | `projectId`                                                                                                                                                 | `{entries, pending, contexts}`                                                                     |
| `memory_share`                 | `projectId`, `hash`                                                                                                                                         | `{shared: true, context: "shared"}`                                                                |
| `memory_delete`                | `projectId`, `hash`                                                                                                                                         | `{deleted: 0\|1}`                                                                                  |
| `memory_delete_context`        | `projectId`, `context`                                                                                                                                      | `{deleted: n}`                                                                                     |
| `memory_ingest_file`           | `projectId`, `path`, `context?`                                                                                                                             | `{indexed: 0\|1}`                                                                                  |
| `memory_ingest_directory`      | `projectId`, `path`, `context?`                                                                                                                             | `{scanned: n}`                                                                                     |
| `memory_embed_pending`         | `projectId`, `limit?`                                                                                                                                       | `{processed, pending}`                                                                             |
| `memory_watch_add`             | `projectId`, `path`                                                                                                                                         | `{projectId, path}`                                                                                |
| `memory_watch_status`          | `projectId`                                                                                                                                                 | `{watches: [{projectId, path, state, lastError?, lastSync?}]}`                                     |
| `memory_watch_remove`          | `projectId`, `path`                                                                                                                                         | `{projectId, path}`                                                                                |
| `memory_workspace_begin`       | `projectId`, `agentId?`, `name?`                                                                                                                            | `{workspaceId, context}`                                                                           |
| `memory_workspace_status`      | `projectId`, `workspaceId`                                                                                                                                  | `{entries, count}`                                                                                 |
| `memory_workspace_consolidate` | `projectId`, `workspaceId`, `keep`                                                                                                                          | `{promoted, discarded}`                                                                            |
| `memory_workspace_discard`     | `projectId`, `workspaceId`                                                                                                                                  | `{discarded}`                                                                                      |
| `memory_sweep`                 | `projectId`, `dryRun=true`                                                                                                                                  | `{candidates, deleted}`                                                                            |
| `memory_sync`                  | `projectId`                                                                                                                                                 | `{sent, received, reindexed}`                                                                      |

### Notes on the less obvious tools

- **`scope` values:** `scope=all` (default) searches `shared` + `project:<id>` (+ workspace
  when named); `scope=project` searches `project:<id>` only; `scope=shared` searches the
  `shared` promotion tier only. Workspace scratch is never included in `scope=all` — it is
  only visible to a search that names that `workspaceId`.
- **`memory_share`:** promotes the entry whose `hash` you pass (from a `memory_write`
  or `memory_search` result) into `shared`. It is additive — the source project row
  stays. There is no un-share; `memory_delete` on the shared row's hash removes it from
  `shared`.
- **Embedding engine (CLI, not a tool):** `ai-raccoon model set local [path]` selects
  the bundled int8 ONNX all-MiniLM-L6-v2 (in-process, ~23 MB, Apache-2.0, SHA-256
  pinned); `ai-raccoon model set openai {model-id} [base-url] [--api-key <key>]`
  selects any OpenAI-compatible `baseUrl` (default `https://api.openai.com/v1`).
  `model` is the model id for openai or a custom ONNX path for local; it defaults to
  the bundled model for local, is required for openai. The API key is persisted in the
  settings table. Changing the engine re-embeds the bank. The `engine` field in the
  result is the stable fingerprint (`local:bundled`, `openai:text-embedding-3-small@<baseUrl>`,
  etc.) — a change triggers the re-embed.
- **Structure alpha (CLI, not a tool):** `ai-raccoon retrieval alpha set {0..1}`
  writes the dual-vector fusion alpha (`retrieval.structureAlpha`, 0..1; default 0.5)
  used by search as `score = alpha × content + (1 − alpha) × heading-path structure`.
  Applies to subsequent searches, no re-embedding.
- **`memory_search`:** hybrid fusion from two modalities: FTS5 (keyword) and vec0
  (semantic, when an embedding engine is configured). The two ranked lists are fused
  with Reciprocal Rank Fusion (RRF): each result's score = Σ weight / (k + rank) per
  modality, then normalized so the top result is 1.0 (range 0..1). `rrfK=60` (default),
  `ftsWeight=1`, `vectorWeight=1` (default 1:1). When no engine is configured, search
  degrades to FTS5-only — never crashes. The FTS5 MATCH expression is constructed per
  query (plan C Wave 1): stopwords are stripped and the remaining content tokens joined
  with AND when there are ≤4 (precision), with an OR fallback — all query tokens plus
  quoted adjacent-token bigram phrases — whenever the AND under-matches (zero rows,
  fewer rows than terms, or fewer than the requested limit); longer queries keep the
  plain OR join of all tokens. Punctuation never reaches the FTS5 grammar.
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
- **File watching:** watching is enabled per project (or `*`) with
  `ai-raccoon watch enable|disable {project-id|*} {true|false}`, restricted to a scope
  allowlist (`watch scope add|remove|list`) and a concurrency cap (`watch concurrency
  {project-id|*} {1..16}`, default 4) — all CLI-only. Quote the `*` wildcard in the
  shell (`'*'`); an unquoted `*` expands into the current directory's files and the CLI
  reports each as an unrecognized argument. The `watch` family CONFIGURES watching —
  registrations are created by agents via `memory_watch_add`; `watch list` prints the
  config per target in block format (`target: <id>  enabled: ..  concurrency: ..  scope:`,
  one path per line, `(none)` when empty — `enabled: true` means watching is enabled for
  that target, not that a watch is registered), `watch registered [{project-id}]` lists
  the persisted registrations (project, path, registered, lastChange; live state stays on
  `memory_watch_status`), and `watch remove {project-id|*}` deletes a target's config rows
  (`'*'` clears only the global config; a file-name ghost row — written by an unquoted `*` —
  is removed individually, e.g. `watch remove CLAUDE.md`). `memory_watch_add` registers a
  file or directory and returns immediately (the initial scan runs in the background —
  status reports `scanning`); already-watched paths are a no-op. `memory_watch_status`
  lists every registered watch with live state (`scanning`/`healthy`/`retrying`/`stopped`),
  last error and last sync; it is available in every access tier. `memory_watch_remove`
  stops and unregisters; a non-existent watch is a no-op. Registration failures surface
  as `watching-disabled:` / `path-outside-scope:` / `path-not-found:` tool errors;
  watch failures never fail the server.
- **Deferred writes:** until an engine is configured, writes are stored deferred
  (`memory_stats.pending > 0`) and only become searchable after `memory_embed_pending`.

## Prompts (2)

| Prompt | Purpose |
|---|---|
| `memory-usage-guide` | Protocol: always pass `project_id`; **search memory first** (2-3 query formulations) and escalate to web/code search only by result, writing findings back; watch setup (`ai-raccoon watch scope add` + `enable`, then `memory_watch_add`/`status`/`remove`); workspace isolation, promotion via `memory_share`, search scopes, degradation, bulk ingest. |
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
- The global default is set with `ai-raccoon access default set {ro|rw|full}`
  (row `access.mode.global` in the settings table; unset resolves to `rw`).
- A **per-project override** is stored in the settings table under
  `access.mode.project:<id>` — it takes precedence over the global setting.

## Environment variables

Only one environment variable is read:

| Variable | Purpose |
|---|---|
| `AIRACCOON_DB_PASSPHRASE` | SQLite encryption passphrase (AES-256-CBC page-level via e_sqlite3mc; unset = plaintext) |

All other configuration (access modes, embedding engine, retrieval alpha, sweep,
sync, watch) lives in the settings table and is changed with the CLI verbs below —
environment variables are not read for runtime configuration (single-channel ruling).
Secrets (OpenAI API key, S3 access/secret keys or the Azure Blob connection string) are
stored in the settings table (encrypted at rest when a passphrase is set), never in the
environment and never in tracked files.

## Command-line options

The server parses its own arguments (System.CommandLine 2.0.10) before the host
builds. Launch-identity flags are CLI-only; a verb runs a one-shot config command
against the bank (results to stdout), bare `ai-raccoon` (with optional launch flags)
runs the server.

| Option | Values | Default |
|---|---|---|
| `--transport` | `stdio`, `http`, `https` (https → warning) | `stdio` |
| `--data-root <path>` | any (`~` expanded) | `~/.ai-raccoon` |
| `--install-scope` | `user`, `project` | `user` |
| `--port <n>` | any port; `0` = random free port | `7721` |

Config verbs (each writes settings rows in the bank's settings table; the running
server hot-reloads them):

```bash
# access — who may do what per project
ai-raccoon access default set {ro|rw|full}
ai-raccoon access default show
ai-raccoon access set {project-id|*} {ro|rw|full}
ai-raccoon access unset {project-id|*}
ai-raccoon access list

# model — embedding engine
ai-raccoon model set local [path]
ai-raccoon model set openai {model-id} [base-url] [--api-key <key>]
ai-raccoon model reset
ai-raccoon model show

# retrieval — hybrid-search blend weight
ai-raccoon retrieval alpha set {0..1}
ai-raccoon retrieval alpha show

# sweep — degradation cutoff
ai-raccoon sweep threshold set {0..1}
ai-raccoon sweep show

# sync — cloud snapshot sync
ai-raccoon sync add s3 {url} --bucket {name} [--region {name}] [--object-key {key}] [--cli]
ai-raccoon sync add azure {container} [--object-key {key}] [--cli --account {name}]
ai-raccoon sync remove
ai-raccoon sync show

# watch — file-watcher configuration (registers happen via memory_watch_add)
ai-raccoon watch enable {project-id|*} {true|false}
ai-raccoon watch disable {project-id|*} {true|false}
ai-raccoon watch scope add {project-id|*} {path}
ai-raccoon watch scope remove {project-id|*} {path}
ai-raccoon watch scope list {project-id|*}
ai-raccoon watch concurrency {project-id|*} {1..16}
ai-raccoon watch list
ai-raccoon watch registered [{project-id}]
ai-raccoon watch remove {project-id|*}

# encryption — bank key source
ai-raccoon encryption bitwarden [-t <token>]
ai-raccoon encryption show
ai-raccoon encryption unset
```

**Encryption key sources.** Default: `AIRACCOON_DB_PASSPHRASE` (env). Alternative:
`encryption bitwarden` fetches an unencrypted ed25519 SSH private key from a Bitwarden
Secrets Manager secret via the `bws` CLI and derives the raw SQLCipher key
(`SHA-256("ai-raccoon-db-key/v1" ‖ seed)` → `x'<64hex>'`, no KDF). The command checks
`bws` presence (install guidance when missing), collects project id + secret id
(owner defaults: `613165e6-7947-49e0-889b-b49d007c5b85` / `f1d3c8e5-5391-4aef-8611-b49d007c8702`),
accepts a per-run-only `-t <token>`, warns that rotating the secret in the Bitwarden UI
without `PRAGMA rekey` bricks the bank, then rekeys + persists. Server startup refuses
loudly when the configured source cannot produce the key.

The backend is selected by the `sync.provider` settings row (default `s3`): `sync add
s3` writes `provider=s3`; `sync add azure` writes `provider=azure`. Each clears the
other provider's rows, so at most one backend is configured at a time. Provider secrets
are **prompted interactively** — the S3 access/secret keys on `sync add s3`, the Azure
connection string on `sync add azure` (prompt on stderr, input read
from stdin; an empty answer aborts with exit 1 and persists nothing) — never accepted on the
command line.

**`--cli` credential modes** skip the prompts and use the machine's CLI login state:
`sync add azure <container> --cli --account <name>` (account required — `--cli` without
`--account` is an error) uses `DefaultAzureCredential`; `sync add s3 <url> --bucket
<name> --cli` uses the AWS default credential chain. Only non-secret rows are stored
(`sync.azureAccount`, `sync.s3Chain`); switching modes clears the other mode's rows.
Auth failures map to `sync-auth-failed:` with a "run `az login`" / "run `aws configure` |
`aws sso login`" hint.

**Sync authentication methods** — four ways to authenticate, two per backend. Secrets are
never accepted on the command line; the prompt-based methods read from stdin (an empty
answer aborts with exit 1 and persists nothing). Only one provider is active at a time
(`sync add` clears the other provider's rows), and switching modes clears the other
mode's rows — a stale secret row must never survive to spread via the settings merge.

| Method | Configure with | Stored in the settings table | Auth at sync time | On failure |
|---|---|---|---|---|
| S3 access/secret keys | `sync add s3 {url} --bucket {name}` (keys prompted) | `endpoint`, `bucket`, `region`, `accessKey`, `secretKey`, `objectKey` | `BasicAWSCredentials` from the stored keys (long-lived; encrypted at rest when a passphrase is set) | 403 → `sync-auth-failed:` ("verify the keys with `sync show`"); network → `sync-network:` |
| S3 AWS chain | `sync add s3 {url} --bucket {name} --cli` | `endpoint`, `bucket`, `region`, `s3Chain`, `objectKey` (no secrets) | AWS default credential chain — env vars, `~/.aws/credentials`, SSO (`aws sso login`), container/IMDS — resolved lazily on the first call | no credentials → `sync-auth-failed:` ("run `aws configure` \| `aws sso login`"); 403 → `sync-auth-failed:`; network → `sync-network:` |
| Azure connection string | `sync add azure {container}` (string prompted) | `connectionString`, `container`, `objectKey` | `BlobServiceClient(connection string)` — account name + key in one string (long-lived; encrypted at rest when a passphrase is set) | malformed string → `sync-not-configured:`; 401/403 → `sync-auth-failed:`; missing container (404) → `sync-network:` — create the container first |
| Azure az CLI | `sync add azure {container} --cli --account {name}` | `azureAccount`, `container`, `objectKey` (no secrets) | `DefaultAzureCredential` chain — env (`AZURE_TENANT_ID`/`AZURE_CLIENT_ID`/`AZURE_CLIENT_SECRET`), workload identity, managed identity, VS/VS Code, az CLI login — endpoint built as `https://{account}.blob.core.windows.net` | no login → `sync-auth-failed:` ("run `az login`"); 401/403 → `sync-auth-failed:`; network → `sync-network:` |

**Which method when:**

- **`--cli` methods** suit developer machines that already log into az/aws — nothing
  long-lived is stored in the settings table, the tokens are short-lived and revocable,
  and auth failures are loud and fixable. Prefer SSO over static keys in
  `~/.aws/credentials`.
- **Prompted-secret methods** suit headless/CI environments (env-var credentials work
  through the same `--cli` chains) and non-AWS S3-compatible endpoints (MinIO, R2, …)
  where no CLI login exists. The secrets live in the settings table, encrypted at rest
  when a passphrase is set.
- If both modes' rows exist (manual settings edits), the stored secret wins the
  tie-break: connection string over az CLI, keys over chain.
- `sync show` prints the provider first, then the mode's fields, with secrets redacted
  (`set`/`unset`); `sync remove` deletes every `sync.*` row.

> `sync add azure` does **not** create the container — create it first (e.g. `az storage
> container create --account-name <account> --name <container>`), or the first sync
> fails with `sync-network:`.

**Azure (az CLI mode) setup — least privilege:**

```bash
az login                                        # sign in once (Azure CLI)
az storage account show -g <rg> -n <account> --query id   # find the storage account resource id
az role assignment create --assignee "you@domain.com" --role "Storage Blob Data Contributor" \
  --scope "<storage-account-resource-id>"       # least privilege: scope to account or container
```

`--cli` mode uses DefaultAzureCredential — az CLI login state, or the env vars
`AZURE_TENANT_ID`/`AZURE_CLIENT_ID`/`AZURE_CLIENT_SECRET` for headless use. Nothing
long-lived is stored in the settings table; the token is short-lived and revocable.

**AWS (chain mode) setup — least privilege** (the sync only GETs and PUTs one object):

```bash
aws configure   # or: aws sso login (short-lived SSO tokens)
```

```json
{ "Version": "2012-10-17", "Statement": [ { "Effect": "Allow", "Action": ["s3:GetObject", "s3:PutObject"],
  "Resource": "arn:aws:s3:::<bucket>/<object-key-prefix>*" } ] }
```

`--cli` mode uses the default credential chain (env, `~/.aws/credentials`, SSO, IMDS);
prefer SSO/short-lived credentials over static keys in `~/.aws/credentials`.

Secrets (OpenAI API key via `model set openai --api-key`, S3 access/secret keys via
`sync add s3`, or the Azure connection string via `sync add azure`) are persisted in the settings table and are never launch flags — the
parser's unknown-option error is the defense. `--help`/`--version` and parse errors
print to **stderr** (exit 0 / exit 1). Generic host flags (`--environment`,
`--contentRoot`, `--applicationName`) are accepted hidden and ignored. A zero-config
`.mcp.json` entry is just `{"mcpServers": {"ai-raccoon": {"command": "ai-raccoon"}}}`;
registry installs (`.mcp/server.json`) pass no args (`packageArguments: []`).

When a client points `command` at the repo instead of the installed tool (e.g. VS Code's
`.vscode/mcp.json`):

```json
{
  "servers": {
    "AiRaccoon": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "<PATH TO PROJECT DIRECTORY>", "--no-launch-profile"]
    }
  }
}
```

`--no-launch-profile` matters: without it `dotnet run` prints its launch-settings
notice to stdout, which corrupts the newline-delimited JSON-RPC stream strict MCP
clients expect on stdio.

## Local embedding model

Local embeddings run in-process on ONNX Runtime over the small int8
all-MiniLM-L6-v2 model (dimension 384, mean-pool + L2-normalize) **bundled inside
the tool package** — `ai-raccoon model set local` needs no sidecar, server
process or download. The binary is gitignored and fetched once by the pinned script
(SHA-256 verified); the tests FAIL (never skip) when it is missing:

```bash
scripts/download-embedding-model.sh          # -> src/AiRaccoon/Models/model_qint8_arm64.onnx + vocab.txt
```

A custom ONNX model path overrides the bundled model via
`ai-raccoon model set local /path/to/model.onnx`.

## Embedding configuration matrix

The embedding engine (configured via `ai-raccoon model …`) resolves exactly two engines:

| Engine | `provider` | `model` | `baseUrl` | Key | Notes |
|---|---|---|---|---|---|
| Local (bundled ONNX) | `local` | optional ONNX path (default: bundled model) | ignored | none | In-process, offline, no API cost |
| OpenAI-compatible | `openai` | model id (required), e.g. `nomic-embed-text` | optional endpoint (default `https://api.openai.com/v1`) | `--api-key` (persisted in settings table) | Any OpenAI-compatible `/embeddings` backend (LM Studio, Ollama, self-hosted, OpenAI) |

Changing the engine (provider, model or baseUrl) re-embeds the bank with the new
engine.

## Error shapes

Tool errors are returned as MCP tool errors (`CallToolResult.IsError`):

| Condition | Message prefix |
|---|---|
| Missing/blank `projectId` | `invalid-params: project_id is required` |
| Invalid `scope` | `invalid-params: Invalid scope '<x>'` |
| Remote embedding provider without a key | `OpenAI-compatible embeddings require an API key: run 'ai-raccoon model set openai <model> --api-key <key>'` |
| Watch registration failures | `watching-disabled:` / `path-outside-scope:` / `path-not-found:` |
| Sync without credentials | `sync-not-configured: run 'ai-raccoon sync add s3 <url> --bucket <name>' or 'ai-raccoon sync add azure <container>' and enter the credentials when prompted` |

## Managed store

All tables, indexes, FTS5 virtual table, vec0 virtual table, and triggers live in
`memory.db` with no native extension dependencies. `MemorySchema.EnsureAsync` creates
the schema on first open with `IF NOT EXISTS` on every DDL statement — idempotent,
safe to run on every bank open. No download-on-first-run provisioning, no per-RID
extension binaries, no external SQLite modules.

**Encryption at rest.** When `AIRACCOON_DB_PASSPHRASE` is set, the connection opens
with `Password` in the connection string, enabling transparent AES-256-CBC per-page
encryption via the bundled e_sqlite3mc engine. FTS5, vec0, and all SQL operations
work unchanged — encryption is at the page level, invisible to queries. Without
the passphrase the bank is plaintext (backward compatible).

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
