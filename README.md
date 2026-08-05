# AiRaccoon

[![publish](https://github.com/Arasz/ai-raccoon/actions/workflows/publish.yml/badge.svg)](https://github.com/Arasz/ai-raccoon/actions/workflows/publish.yml)

An MCP server that gives AI agents persistent, project-scoped memory backed by a
managed .NET SQLite store: local-first by default, one memory bank per install scope,
hybrid FTS5+vec0 semantic search, workspace sandboxes, a curated shared tier, memory
degradation, and opt-in cloud sync (S3 or Azure Blob). Built on the
[ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) C# SDK 2.0.0
(net10.0).

> Domain: provides AI agents with persistent, project-scoped memory over MCP,
> backed by a managed SQLite store.
> Stacks: `dotnet`, `mcp`.

## What an agent gets

- **One memory bank per install scope.** A user-scope install (global tool) keeps a
  single bank under `~/.ai-raccoon` shared by every project; a project-scope install
  keeps its own bank under `<project>/.ai-raccoon`. Projects partition the bank via
  context (`project:<id>`).
- **Workspace sandboxes.** `memory_workspace_begin` mints a `workspace_id` whose
  context is isolated by design — notes written with it stay in the outbox, never in
  committed project memory, until consolidated.
- **Shared promotion tier.** Plain writes land in the project. `memory_share`
  promotes a hash into the flat `shared` context — cross-project, curated, and exempt
  from degradation sweeps.
- **Hybrid search.** `memory_search` combines FTS5 keyword ranking and vec0 semantic
  similarity via reciprocal rank fusion (RRF), scoped by `scope=all|project|shared`
  and optional workspace. Configurable weights per modality.
- **Rating and degradation.** Search hits raise an entry's on-row retrieval rating
  (half-life decay with access-count multiplier); sweeps remove old, low-rated
  project entries (`shared` is protected).
- **Cloud sync (optional).** `memory_sync` pushes/pulls VACUUM snapshots to a
  cloud object store (S3 or Azure Blob) with If-Match conflict detection. This is the
  correlation point between a user-scope install and any project-scope install.
- **Access modes.** `ro` (read-only), `rw` (read-write, default), `full` (includes
  destructive operations). Per-project settings override the global default.
- **Encryption at rest (optional).** Set `AIRACCOON_DB_PASSPHRASE` to encrypt the
  SQLite bank with AES-256-CBC via e_sqlite3mc (transparent page-level encryption —
  FTS5 and vec0 work unchanged). Without the passphrase the bank is plaintext.

The full tool contract (19 tools, 2 prompts, environment variables, error
shapes) is in [`docs/reference/agent-memory-server.md`](docs/reference/agent-memory-server.md).

## Transports

- **stdio** (default) — what MCP clients expect when launching a server as a subprocess.
- **Streamable HTTP** — opt-in via `--transport http`; serves the
  protocol at `/mcp` (launch profile `http`, `http://localhost:8080`).

Transport selection lives in one place: `ServerConfig` takes the resolved transport
from the `--transport` launch flag (anything other than `http` runs stdio). All
diagnostics go to stderr; stdout carries only MCP protocol messages.

## Environment variables

Only one environment variable is read:

| Variable | Purpose |
|---|---|
| `AIRACCOON_DB_PASSPHRASE` | SQLite encryption passphrase (AES-256-CBC, optional) |

All other configuration (access modes, embedding engine, retrieval alpha, sweep,
sync, watch) lives in the settings table of the install's `memory.db` and is changed
through the `ai-raccoon` verb commands — the CLI is the single config channel. Secrets
(OpenAI API key, S3 access/secret keys or the Azure Blob connection string) are stored in
the settings table (encrypted at rest when a passphrase is set), never in the environment
and never in tracked files.

## Command-line options

The server parses its own arguments (System.CommandLine 2.0.10) before the host
builds. Launch-identity flags (startup-scoped only):

| Option | Values | Default |
|---|---|---|
| `--transport` | `stdio`, `http`, `https` (https → warning) | `stdio` |
| `--data-root <path>` | any (`~` expanded) | `~/.ai-raccoon` |
| `--install-scope` | `user`, `project` | `user` |

Runtime configuration is not read from environment variables — it lives in the
settings table and is changed with the config verbs (one-shot processes against the
bank; the running server hot-reloads the rows):

```
ai-raccoon access default set {ro|rw|full}      ai-raccoon access default show
ai-raccoon access set {project-id|*} {ro|rw|full}
ai-raccoon access unset {project-id|*}          ai-raccoon access list
ai-raccoon model set local [path]               ai-raccoon model set openai {model-id} [base-url] [--api-key <key>]
ai-raccoon model reset                          ai-raccoon model show
ai-raccoon retrieval alpha set {0..1}           ai-raccoon retrieval alpha show
ai-raccoon sweep threshold set {0..1}           ai-raccoon sweep show
ai-raccoon sync add s3 {url} --bucket {name} [--region {name}] [--object-key {key}] [--cli]   # key prompts, or --cli = AWS credential chain
ai-raccoon sync add azure {container} [--object-key {key}] [--cli --account {name}]            # connection-string prompt, or --cli = az login
ai-raccoon sync remove                          ai-raccoon sync show
ai-raccoon watch enable|disable {project-id|*} {true|false}
ai-raccoon watch scope add|remove|list {project-id|*} {path}
ai-raccoon watch concurrency {project-id|*} {1..16}
ai-raccoon watch list                        ai-raccoon watch registered [{project-id}]
ai-raccoon watch remove {project-id|*}
ai-raccoon encryption bitwarden [-t <token>]
ai-raccoon encryption show                     ai-raccoon encryption unset
```

**Encryption key sources.** The bank's encryption key comes from `AIRACCOON_DB_PASSPHRASE`
(env, default) or — via `encryption bitwarden` — from a Bitwarden Secrets Manager secret
fetched with the `bws` CLI: the secret holds an unencrypted ed25519 SSH private key whose
seed is derived into the raw SQLCipher key (`SHA-256("ai-raccoon-db-key/v1" ‖ seed)`).
`encryption bitwarden` checks `bws` presence (install guidance when missing), collects the
project id + secret id (defaults: project `613165e6-7947-49e0-889b-b49d007c5b85`, secret
`f1d3c8e5-5391-4aef-8611-b49d007c8702`), accepts an optional `-t <token>` for runs without
`BWS_ACCESS_TOKEN` (used for that run only, never persisted), validates reachability, warns
that rotating the secret in the Bitwarden UI without `PRAGMA rekey` bricks the bank, then
rekeys the bank and persists the source. The server refuses to start loudly when the
configured source cannot produce the key (bws missing, network failure, wrong key).

Secrets (OpenAI API key via `model set openai --api-key`, S3 access/secret keys via
`sync add s3`, or the Azure Blob connection string via `sync add azure`) are persisted
in the settings table and are never launch flags — an
unknown-option parse error is the defense. `--help`/`--version` and parse errors
print to stderr (exit 0 / exit 1); stdout carries only MCP protocol frames. Generic
host flags (`--environment`, `--contentRoot`, `--applicationName`) are accepted
hidden and ignored.

### Configuration commands — usage

Every verb runs as a one-shot process against the install's bank (the running server
hot-reloads the rows). Targets take `{project-id|*}`: `*` matches all projects and a
project-specific row overrides the wildcard (more specific wins). **Quote the wildcard**
(`'*'`) — shells expand a bare `*` into the files of the current directory, and the CLI
then reports each file as an unrecognized argument. Run any command with
`--help` for its exact argument list.

**Access modes** — who may do what in a project's memory.

```
ai-raccoon access default set {ro|rw|full}   # global default (rw unless set)
ai-raccoon access default show
ai-raccoon access set {project-id|*} {ro|rw|full}   # per-project override
ai-raccoon access unset {project-id|*}              # drop the override
ai-raccoon access list
```

Tiers: `ro` = read only, `rw` = read + write (default), `full` = adds destructive
operations (deletion, forgetting knobs). The background file-watcher mirror runs
regardless of tier.

**Embedding model** — which engine embeds chunks.

```
ai-raccoon model set local [path]       # bundled all-MiniLM-L6-v2 (int8, ~21 MB); optional custom ONNX path
ai-raccoon model set openai {model-id} [base-url] [--api-key <key>]   # any OpenAI-compatible endpoint
ai-raccoon model reset                  # back to the bundled local default
ai-raccoon model show
```

The API key is stored in the settings table (encrypted at rest with a passphrase).
Changing provider/model/base-url re-embeds the whole bank with the new engine.

**Retrieval alpha** — the hybrid-search blend weight (structure signal vs lexical).

```
ai-raccoon retrieval alpha set {0..1}   # default: measured sweep optimum (ADR 0006)
ai-raccoon retrieval alpha show
```

**Sweep threshold** — the degradation cutoff for old, low-rated entries.

```
ai-raccoon sweep threshold set {0..1}
ai-raccoon sweep show
```

(The per-entry TTL knob was removed in the CLI-config refactor — degradation is
threshold-driven only.)

**Cloud sync** — snapshot sync to a cloud object store (S3 or Azure Blob; off until
configured).

```
ai-raccoon sync add s3 {url} --bucket {name} [--region {name}] [--object-key {key}] [--cli]
ai-raccoon sync add azure {container} [--object-key {key}] [--cli --account {name}]
ai-raccoon sync remove
ai-raccoon sync show                      # provider first; secrets redacted
```

The backend is selected by the `sync.provider` settings row (default `s3`): `sync add
s3` writes `provider=s3` and `sync add azure` writes `provider=azure`, each clearing the
other provider's rows. Credentials are prompted interactively — `sync add s3` asks for
the S3 access key and secret key, `sync add azure` for the connection string (prompt on
stderr, input from stdin; an empty answer aborts with exit 1 and persists nothing) — and
are never accepted on the command line. `sync remove` returns to sync-off; `sync show`
prints the provider and its fields with the secrets redacted.

**`--cli` credential modes** (no secret prompts — the machine's CLI login state is the
credential): `sync add azure <container> --cli --account <name>` uses
`DefaultAzureCredential` (az CLI login, or `AZURE_TENANT_ID`/`AZURE_CLIENT_ID`/
`AZURE_CLIENT_SECRET` env vars for headless use); `sync add s3 <url> --bucket <name>
--cli` uses the AWS default credential chain (`aws configure`, or `aws sso login` for
short-lived SSO tokens). Nothing long-lived is stored in the settings table — only the
non-secret account name / `s3Chain` marker — and the tokens are short-lived and
revocable. Auth failures surface as `sync-auth-failed:` with a "run `az login`" /
"run `aws configure` | `aws sso login`" hint.

> `sync add azure` does **not** create the container — create it first (e.g. `az storage
> container create --account-name <account> --name <container>`), or the first sync
> fails with `sync-network:`.

**Azure (az CLI mode) least privilege** — sign in once, then scope a role to the storage
account:

```bash
az login                                        # sign in once (Azure CLI)
az storage account show -g <rg> -n <account> --query id   # find the storage account resource id
az role assignment create --assignee "you@domain.com" --role "Storage Blob Data Contributor" \
  --scope "<storage-account-resource-id>"       # least privilege: scope to account or container
```

**AWS (chain mode) least privilege** — the sync only GETs and PUTs one object, so the
IAM policy can be scoped to its key prefix:

```bash
aws configure   # or: aws sso login (short-lived SSO tokens)
```

```json
{ "Version": "2012-10-17", "Statement": [ { "Effect": "Allow", "Action": ["s3:GetObject", "s3:PutObject"],
  "Resource": "arn:aws:s3:::<bucket>/<object-key-prefix>*" } ] }
```

Prefer SSO/short-lived credentials over static keys in `~/.aws/credentials`.

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

**File watching** — mirror a path into memory (opt-in, project-scoped).

```
ai-raccoon watch enable {project-id|*} {true|false}   # opt-in; false = disable
ai-raccoon watch scope add|remove|list {project-id|*} {path}   # allowlist entries; absolute paths cover dir + subdirs
ai-raccoon watch concurrency {project-id|*} {1..16}   # parallel digests (default 4)
ai-raccoon watch list                               # config per target (block format: target: <id>  enabled: ..  concurrency: ..  scope:, one path per line, (none) when empty)
ai-raccoon watch registered [{project-id}]          # persisted registrations (project, path, registered, lastChange) — live state is on memory_watch_status
ai-raccoon watch remove {project-id|*}              # deletes a target's config rows ('*' clears only the global config; remove each file-name row individually, e.g. one written by an unquoted *)
```

Watching is **disabled until enabled**; `memory_watch_add` only accepts paths inside an
allowed scope. `watch enable '*' true` with an empty allowlist prints a hint to add at
least one scope. The `watch` family CONFIGURES watching — registrations are created by
agents via `memory_watch_add`; `enabled: true` in `watch list` means watching is enabled
for that target, not that a watch is registered. Watch configuration persists across
restarts (the watcher re-registers and catches up on restart).

Zero-config `.mcp.json` entry (defaults: stdio, `~/.ai-raccoon`, user scope, rw):

```json
{
  "mcpServers": {
    "ai-raccoon": { "command": "ai-raccoon" }
  }
}
```

Encrypted-bank setups set `AIRACCOON_DB_PASSPHRASE` in the client's user-scoped
config, never in a shared/tracked file:

```json
{
  "mcpServers": {
    "ai-raccoon": {
      "command": "ai-raccoon",
      "env": {
        "AIRACCOON_DB_PASSPHRASE": "change-me"
      }
    }
  }
}
```

## Embeddings

Embeddings are configured per bank via the `ai-raccoon model` CLI verbs; two engines:

| Engine | `provider` | `model` | Setup |
|---|---|---|---|
| Local (ONNX, in-process) | `local` | Optional ONNX path | Bundled `all-MiniLM-L6-v2` (int8, ~21 MB, Apache-2.0). No network, ~9 ms/query. |
| Remote (OpenAI-compatible) | `openai` | Model id (e.g. `text-embedding-3-small`) | Any OpenAI-compatible `baseUrl`; key via `--api-key` (persisted in the settings table) |

The API key is stored in the settings table (encrypted at rest when a passphrase is
set). Changing the engine (`provider`/`model`/`baseUrl`) re-embeds the entire bank
with the new engine. Other OpenAI-compatible endpoints (LM Studio, Ollama) are
supported — pass their base-url to `ai-raccoon model set openai`.

## Metrics & observability

Every MCP tool invocation (all 19 tools) records OpenTelemetry-compatible metrics and
traces through the `ToolCallMetrics` meter:

| Instrument | Name | Tags |
|---|---|---|
| Counter | `ai_raccoon_tool_invocations` | `tool`, `result` (success/error), `error_type` |
| Histogram | `ai_raccoon_tool_duration_ms` | `tool`, `result`, `error_type` (buckets 1 ms – 30 s) |
| ActivitySource | `AiRaccoon.MemoryTools` | `tool`, `project_id`, `error_type` |

Meter and ActivitySource are both named **`AiRaccoon.MemoryTools`**.

### Watch metrics live (dotnet-counters)

While the server runs, monitor the meter over EventPipe (no code changes, no
restart):

```bash
dotnet-counters monitor -p <server-pid> --counters AiRaccoon.MemoryTools
```

This prints the invocation counter and the duration histogram (count/sum/percentiles)
per `tool`/`result` tag set, refreshing every second. Install `dotnet-counters` once
with `dotnet tool install -g dotnet-counters`; find the server pid with
`pgrep -f ai-raccoon` (or `dotnet run`'s own pid in dev).

### Collect traces (dotnet-trace)

```bash
dotnet-trace collect -p <server-pid> --providers AiRaccoon.MemoryTools
```

The resulting trace file can be opened in PerfView/VS/VS Code; each tool call is a
span with `tool`, `project_id` and `error_type` tags. (The ActivitySource only emits
while a listener is attached — `dotnet-trace` attaches one.)

### Notes

- No OTLP export is wired yet (Wave 0 is local-only; `project_id` is a plaintext tag —
  it may need hashing when OTLP export is added). Prometheus/Grafana integration comes
  with that work.
- For store-level operational state (entries, pending embeds, contexts) use the
  `memory_stats` MCP tool in-band.
- Instrumentation behavior itself is pinned by tests
  (`tests/AiRaccoon.Tests/Unit/Observability/`), which attach an `ActivityListener` and
  read the meter directly.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Build & test

```bash
dotnet build
dotnet test
```

The test project (`tests/AiRaccoon.Tests`, xunit.v3 + Shouldly, Dapper) covers the
domain, the store, the tools, the prompts, and the E2E suite — 185+ cases.
Integration tests exercise the real SQLite FTS5 and vec0 tables against in-memory
databases. Tests that need the ONNX embedding model use the bundled int8 model
path; see the test project's README for the full setup.

## Embedding benchmark

**Do you need a bigger embedding model, or is the smallest one good enough?
Measured answer: the smallest one is good enough for most uses.** We benchmarked
three options on 174 real documents (68 judged queries):

| Model | Size | Quality (MRR) | Speed (per query) |
|---|---:|---:|---:|
| all-MiniLM-L6-v2 (local, in-process) | **~21 MB** | 0.836 | **~9 ms** |
| EmbeddingGemma-300m (LM Studio, network) | ~334 MB | 0.858 | ~37 ms |
| Qwen3-Embedding-0.6b (LM Studio, network) | ~639 MB | 0.854 | ~90 ms |

The 21 MB local model finds the right memory first essentially as often as the
served models (MRR 0.836 vs 0.854–0.858), is **4–10× faster per query** (no
network round-trip), costs nothing to run, and works offline. The served models
only pull ahead on one metric — nDCG@10 (0.70 vs 0.61), i.e. how well the whole
top-10 is ordered — which matters only when the *ranking of lower hits*, not the
first hit, decides the outcome.

**Recommendation:** start with the local model (bundled, zero setup). Move to a
served model only if retrieval quality on your own corpus proves insufficient —
you trade 4–10× latency and 15–30× disk for a quality gain visible only in top-10
ordering.

Full numbers, metric definitions (R@5, R@10, MRR, nDCG@10, dim, latency),
methodology and the runnable harness: [`docs/reference/embedding-benchmark.md`](docs/reference/embedding-benchmark.md).

## How to start

### Install

The server packs as a .NET tool (package id `arasz.ai-raccoon`; the installed command is `ai-raccoon`):

```bash
dotnet tool install -g arasz.ai-raccoon    # from the NuGet feed
# or from the local feed after `dotnet pack -c Release` (DOTNET_ENV=local):
dotnet tool install -g arasz.ai-raccoon --add-source .nupkg-local
```

Or run from source (see below). After install, `ai-raccoon` on PATH is the whole
interface: launch flags to start the server, verb commands to configure the bank.

### Run the server

**stdio** (default — what MCP clients expect when launching a subprocess):

```bash
ai-raccoon                                    # or: dotnet run --project src/AiRaccoon
```

**Streamable HTTP** (opt-in):

```bash
ai-raccoon --transport http                  # serves the MCP protocol at /mcp
# with the launch profile (listens on http://localhost:8080):
dotnet run --project src/AiRaccoon --launch-profile http
```

Launch identity flags (startup-scoped only): `--transport stdio|http|https` (default
`stdio`), `--data-root <path>` (default `~/.ai-raccoon`), `--install-scope user|project`
(default `user`). All diagnostics go to stderr; stdout carries only MCP protocol frames.

### Connect a client

Zero-config entry for clients that launch the server themselves (Claude Desktop,
VS Code, etc.):

```json
{
  "mcpServers": {
    "ai-raccoon": { "command": "ai-raccoon" }
  }
}
```

When the client points `command` at the repo (e.g. VS Code's `.vscode/mcp.json`):

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

`--no-launch-profile` matters: `dotnet run` otherwise prints its launch-settings notice
to stdout, which corrupts the newline-delimited JSON-RPC stream strict MCP clients expect
on stdio. For an HTTP client point it at `http://localhost:8080/mcp` (or the port from
`--transport http`).

## Architecture

```
AiRaccoon/
  src/AiRaccoon/              # the MCP server (thin)
    Program.cs               # transport selection + DI + MCP wiring
    Setup/McpServerSetup.cs  # stdio / HTTP transport
    Tools/MemoryTools.cs     # 16 [McpServerTool] memory tools, 1:1 to the port
    Tools/WatchTools.cs      # 3 [McpServerTool] file-watcher tools
    Prompts/MemoryPrompts.cs # 2 agent usage guides
    Access/                  # MemoryAccessGuard, ForgettingPolicyService
    Setup/Dependencies.cs    # DI registration
  src/AiRaccoon.Core/        # pure domain (no infra deps)
    Memory/                  # records, SearchQuery, IMemoryStore port
    Rating/                  # RatingPolicy, IMemoryExtension + MemoryExtensionHost
    Degradation/             # DegradationPolicy, SweepCandidate
    Chunking/                # IChunker, MarkdownChunker, TokenCount
    Access/                  # AccessMode, AccessModePolicy, AccessRequirement
    Workspace/               # Workspace, WorkspaceStatus, IWorkspaceStore
  src/AiRaccoon.Infrastructure/  # SQLite adapter, embeddings, sync
    Sqlite/                  # SqliteMemoryStore (Dapper), MemorySchema, RRF
    Embedding/               # EmbeddingService (ONNX + remote), OnnxEmbeddingGenerator
    Chunking/                # TokenizerChunker (o200k_base)
    Degradation/             # SweepService
    Workspace/               # WorkspaceService
    Sync/                    # SyncService (S3/Azure, VACUUM INTO, ATTACH+merge)
    Rating/                  # RetrievalRatingExtension (no-op, P1 rewire)
  tests/AiRaccoon.Tests/     # xunit.v3 + Shouldly
  Directory.Build.props      # analyzers, warnings-as-errors
  Directory.Packages.props   # central package versions
  docs/                      # canonical documentation tree (see docs/README.md)
```

The server keeps the [MCP layer thin](CLAUDE.md): `Tools/` maps parameters and formats
results, with no business logic of its own. The domain layer is pure; the SQLite adapter
lives in Infrastructure. Warnings are errors (`TreatWarningsAsErrors`), analyzers are on,
and package versions are managed centrally.

For the system architecture — data model, write/search/sync flows, workspace lifecycle,
access modes, and algorithms — see [`docs/explanation/architecture.md`](docs/explanation/architecture.md).

The store is our own managed SQLite layer: `MemorySchema.EnsureAsync` creates the
tables, FTS5 and vec0 virtual tables, and triggers on first open — no native extension
provisioning needed. The bundled ONNX embedding model (`all-MiniLM-L6-v2`, int8
quantized, ~21 MB) runs in-process.

## Packaging & release

The server packs as a .NET tool (`PackAsTool`, package id `arasz.ai-raccoon`, type `McpServer`):

```bash
dotnet pack -c Release
```

To deploy to the local NuGet feed (`.nupkg-local/`), set `DOTNET_ENV=local` for the
directory (MSBuild env lookup is case-sensitive on macOS — `dotnet_env` will not
match) — the `DeployToLocalSource` build target pushes the freshly built package. The
package embeds `.mcp/server.json`, so MCP clients can discover inputs.

## Contributing

Read [`CLAUDE.md`](CLAUDE.md) first — it is the source of truth for this repo's rules:

- **TDD is mandatory** — a failing, behavior-focused test precedes any production change.
- **One task per PR** — every unit of work ends in a pull request; never push directly to
  `main`. The one exception is an explicit instruction from the person you work with.
- Keep the [non-negotiable invariants](CLAUDE.md) (clean layering, minimal comments,
  guarded nulls, no hardcoded secrets, …).

Architecture decisions are recorded as ADRs under
[`docs/adr/`](docs/adr/README.md) — none recorded yet.

## Security

Do not open a public issue for a security problem — report it privately; see
[`SECURITY.md`](SECURITY.md) for the reporting channel, supported-versions policy, and the
threat model.

## License

MIT — see [`LICENSE`](LICENSE). Copyright (c) 2026 Rafał Araszkiewicz.
