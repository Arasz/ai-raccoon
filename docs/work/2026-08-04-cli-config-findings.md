# Research: CLI-as-single-config-channel for the AiRaccoon MCP server

**Date:** 2026-08-04
**Question:** What is the full configuration surface of the AiRaccoon MCP server (CLI args, env vars, settings-table rows, MCP config tools), which options have sensible defaults, and what verb-style CLI command tree replaces every config-change channel per the single-channel principle?

## Findings

### F1 — The launch surface is 9 CLI options + 13 env vars, merged in exactly one class with precedence CLI > env > default [READ]

`ServerConfig.Build` is the only place the launch surface is merged: every option falls back `cli → readEnv → built-in default` (`ServerConfig.cs:12-38`). The 9 options are declared in `CliArgs.cs:45-53` (`--transport`, `--data-root`, `--install-scope`, `--access-mode`, `--embedding-model`, `--sync-endpoint`, `--sync-bucket`, `--sync-region`, `--sync-object-key`) plus 3 hidden, never-consumed host flags (`CliArgs.cs:57-59`). Env vars read: `MCP_TRANSPORT`, `AIRACCOON_DATA_ROOT`, `AIRACCOON_INSTALL_SCOPE`, `AIRACCOON_EMBEDDING_MODEL`, `AIRACCOON_ACCESS_MODE`, `AIRACCOON_SYNC_ENDPOINT/BUCKET/REGION/OBJECT_KEY` (`ServerConfig.cs:14-33`). Four more vars are env-only secrets read outside `ServerConfig`: `AIRACCOON_SYNC_ACCESS_KEY`/`AIRACCOON_SYNC_SECRET_KEY` at the composition root (`Dependencies.cs:29-30`), `AIRACCOON_OPENAI_API_KEY` (`EmbeddingService.cs:20,93`), `AIRACCOON_DB_PASSPHRASE` (`EnvEncryptionKeyProvider.cs:9`). Secrets are deliberately never declared as CLI options — the unknown-option parse error is the defense (`CliArgs.cs:34-37`; ruling in `docs/plans/cli-args-parsing.md:47-52`).

**Evidence:** `src/AiRaccoon/Setup/ServerConfig.cs:12-38`, `src/AiRaccoon/Setup/CliArgs.cs:45-53,57-59`, `src/AiRaccoon/Setup/Dependencies.cs:29-30`, `src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs:20,93`, `src/AiRaccoon.Infrastructure/Sqlite/EnvEncryptionKeyProvider.cs:9`, `docs/plans/cli-args-parsing.md:31-55` (the 13-var inventory table).

### F2 — The runtime config store is the settings table in the single per-install `memory.db`; 9 keys live there today, 3 of them write-orphaned [READ]

Schema: `settings (key TEXT PRIMARY KEY, value TEXT NOT NULL)` (`MemorySchema.cs:195-198`). There is exactly one bank per install — user scope `memory.db` at the data root, project scope at `<dataRoot>/.ai-raccoon` (`SqliteConnectionFactory.cs:22-32`) — so settings are bank-global, not per-project. Keys in use: `access.mode.global` (`AccessModePolicy.cs:10`), `access.mode.project:<id>` (`AccessModePolicy.cs:12`), `embedding.provider/model/baseUrl/engine` (`EmbeddingSettingsKeys.cs:6-11`), `retrieval.structureAlpha` (`StructureFusion.cs:20`), `sweep.threshold`, `sweep.ttl_days` (`ForgettingPolicyService.cs:13-14`). **Write-orphaned:** `access.mode.project:<id>` is read by `MemoryAccessGuard` (`MemoryAccessGuard.cs:14-25`) but nothing in `src/` writes it; `sweep.threshold`/`sweep.ttl_days` have setters (`ForgettingPolicyService.cs:36-54`) but no MCP tool calls them (`MemoryTools.cs` exposes only `memory_sweep`, which *reads* the knobs, `MemoryTools.cs:660-661`). The generic `GetSettingAsync`/`SetSettingAsync` (`IMemoryStore.cs:58-62`, `SqliteMemoryStore.cs:620-637`) exist but are not exposed as MCP tools.

**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:195-198`, `src/AiRaccoon.Infrastructure/Sqlite/SqliteConnectionFactory.cs:22-32`, `src/AiRaccoon.Core/Access/AccessModePolicy.cs:10-12`, `src/AiRaccoon/Access/MemoryAccessGuard.cs:14-25`, `src/AiRaccoon/Access/ForgettingPolicyService.cs:13-14,36-54`, `src/AiRaccoon/Tools/MemoryTools.cs:642-673`; grep for `ProjectSettingKey|access.mode.project` over `src/` returns only the reader pair, no writer.

### F3 — Access modes: effective default rw; the global seed is written once and never overwrites an operator value; per-project override has no writer [READ]

`AccessModePolicy.Resolve = perProject ?? global ?? Rw` (`AccessModePolicy.cs:14`). The global row is seeded on first bank open from the merged `--access-mode`/`AIRACCOON_ACCESS_MODE` value with `ON CONFLICT(key) DO NOTHING` — an operator-set row is never overwritten (`SqliteConnectionFactory.cs:61-78`). Defaults: no global row → `rw`; per-project row → overrides global. Per-project override keys are readable but unwritable through any shipped surface (F2). The E2E suite seeds `AIRACCOON_ACCESS_MODE=full` in its factory (`McpServerFactory.cs:30-33`).

**Evidence:** `src/AiRaccoon.Core/Access/AccessModePolicy.cs:14,32-46`, `src/AiRaccoon.Infrastructure/Sqlite/SqliteConnectionFactory.cs:61-78`, `tests/AiRaccoon.Tests/E2E/McpServerFactory.cs:30-33`, `docs/reference/agent-memory-server.md:116-131`.

### F4 — Embedding config is bank-global despite `memory_configure`'s projectId param; default = no engine (FTS5-only search, deferred writes); bundled model is the default model for local [READ]

`memory_configure(projectId, provider, baseUrl?, model?, apiKey?)` (`MemoryTools.cs:380-444`) calls `ConfigureEmbeddingAsync` which writes the bank-global `embedding.*` rows; `projectId` is used only for the write-tier access check and the re-embed scope (`SqliteMemoryStore.cs:441-501`). Defaults: no provider row → search degrades to FTS5-only and writes embed deferred (`SqliteMemoryStore.cs:108-112`; `docs/reference/agent-memory-server.md:76-77,96-98`); `provider=local` with no model → bundled int8 all-MiniLM-L6-v2 ONNX (`BundledModel.cs:16-25`, spec `constraints.model`); `provider=openai` → model required, baseUrl default `https://api.openai.com/v1` (`EmbeddingService.cs:18,100`), key from arg or `AIRACCOON_OPENAI_API_KEY` (`EmbeddingService.cs:93`; key never persisted, `SqliteMemoryStore.cs:452-457`). The persisted `embedding.engine` fingerprint triggers a full re-embed on change (`SqliteMemoryStore.cs:459-498`). The launch flag `--embedding-model`/`AIRACCOON_EMBEDDING_MODEL` only feeds the merged `EmbeddingModelPath` used as the fallback for local (`ServerConfig.cs:20`, `EmbeddingService.cs:77-78`) — a second, launch-time channel for a runtime setting.

**Evidence:** `src/AiRaccoon/Tools/MemoryTools.cs:380-444`, `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs:441-501,108-112`, `src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs:18-20,53-60,75-104`, `src/AiRaccoon.Infrastructure/Embedding/BundledModel.cs:16-25,37-48`, `docs/work/features-native-memory/spec.json` → `constraints.model`.

### F5 — `retrieval.structureAlpha` (default 0.5) is the second MCP config tool's target: `memory_set_structure_alpha` [READ]

The tool validates alpha ∈ [0,1] and writes `retrieval.structureAlpha` via `SetSettingAsync` (`MemoryTools.cs:446-483`); searches read it per call with fallback `0.5` (`StructureFusion.cs:17-20`, `SqliteMemoryStore.cs:307`). Requires write-tier access. Default 0.5, absent/unparsable falls back silently.

**Evidence:** `src/AiRaccoon/Tools/MemoryTools.cs:446-483`, `src/AiRaccoon.Infrastructure/Embedding/StructureFusion.cs:17-20`, `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs:307`.

### F6 — Sweep knobs have defaults (threshold 0.3, TTL 30 days) but no change channel exists at all [READ]

`ForgettingPolicyService` reads `sweep.threshold`/`sweep.ttl_days` with defaults 0.3/30 (`ForgettingPolicyService.cs:16-34`) and offers setters gated to full mode (`:36-54`), but no MCP tool or CLI invokes the setters (F2). `memory_sweep(projectId, dryRun=true)` only reads them (`MemoryTools.cs:660-661`). So today these knobs are effectively constants — the first real writer will be the CLI.

**Evidence:** `src/AiRaccoon/Access/ForgettingPolicyService.cs:13-54`, `src/AiRaccoon/Tools/MemoryTools.cs:642-673`; grep over `src/` for `SetSweepThresholdAsync|SetEntryTtlAsync` finds no callers outside the service itself.

### F7 — Sync: 4 non-secret fields are launch-time today, 2 secrets env-only, default = off; the cloud store is resolved once at startup [READ]

`SyncOptions` fields endpoint/bucket/accessKey/secretKey/region/objectKey; `IsConfigured` requires endpoint+bucket+access+secret (`SyncOptions.cs:6-17`). The four non-secrets come from CLI/env at launch (`ServerConfig.cs:30-33`); the two secrets from env at composition root (`Dependencies.cs:29-30`). `Dependencies.cs:51-61` constructs `NullCloudStore` (sync off) or `S3CloudStore` **once**, from the startup `SyncOptions`. Object-key default `memory-<projectId>.db` is applied per sync call (`MemoryTools.cs:703`). `memory_sync` is an operation tool (per-project, write tier) that errors `sync-not-configured` when unset (`MemoryTools.cs:675-701`). The spec pins WAL + busy_timeout on every connection and VACUUM-INTO/ integrity-check sync semantics (`spec.json` nonFunctional).

**Evidence:** `src/AiRaccoon.Infrastructure/Options/SyncOptions.cs:6-17`, `src/AiRaccoon/Setup/ServerConfig.cs:30-33`, `src/AiRaccoon/Setup/Dependencies.cs:25-32,51-61`, `src/AiRaccoon/Tools/MemoryTools.cs:675-762`, `src/AiRaccoon.Infrastructure/Sqlite/SqliteConnectionFactory.cs:88-94`, `docs/work/features-native-memory/spec.json` → `nonFunctional`.

### F8 — The file-watcher spec already rules the pattern to copy: CLI-only config, `{project-id|*}` targets, more specific entry wins, user commands bypass access tiers, persisted [READ]

`docs/work/features-file-watcher/file-watcher.feature:44-68` (2026-08-04 ruling f): the CLI is the ONLY channel for watch config — no MCP tools, no env/args, no config-file edits. Format: `watch enable|disable {project-id|*} {true|false}` and `watch scope add|remove|list {project-id|*} {path}`; `*` matches all projects and the more specific entry wins; `watch enable * true` returns a message to add at least one scope; config survives restart. User-run commands get no access-tier checks (`file-watcher.feature:46`). Watch registrations (add/status/remove) remain MCP tools in the same feature (baseline `:17-18`, tier rules `:392-421`) — the split is config-vs-operation: the CLI owns enable/scope, the tools own registration.

**Evidence:** `docs/work/features-file-watcher/file-watcher.feature:16-19,44-68,392-421`.

### F9 — The only two runtime config-change MCP tools are `memory_configure` and `memory_set_structure_alpha`; everything else in the 17-tool surface is an operation [READ]

The 17 tools (`docs/reference/agent-memory-server.md:28-47`) split cleanly: 15 operate on memory data (write/search/list/stats/share/delete/delete_context/ingest_file/ingest_directory/embed_pending/workspace_*4/sweep/sync); exactly 2 mutate configuration: `memory_configure` (embedding engine, F4) and `memory_set_structure_alpha` (fusion alpha, F5). `memory_embed_pending` is a one-shot operation on the deferred queue, not config. So the single-channel removal set on the MCP surface is precisely those 2 tools (→ 15 tools), plus the planned watch-registration trio stays as operations.

**Evidence:** `docs/reference/agent-memory-server.md:28-47`, `src/AiRaccoon/Tools/MemoryTools.cs:32-49` (tool-name constants).

### F10 — `appsettings.json` is a dormant channel: the host loads it but nothing reads `IConfiguration`; the registry manifest declares 11 env vars with empty `packageArguments` [READ]

`WebApplication.CreateBuilder([])` still loads `appsettings.json` + env by default, but the only `IConfiguration` mention in `src/` is a doc comment on `InfrastructureOptions` (`InfrastructureOptions.cs:15`); every value flows through `ServerConfig.Build`/`Dependencies` explicitly. The MCP registry manifest declares `packageArguments: []` and the 11 non-secret/non-transport env vars (`src/AiRaccoon/.mcp/server.json:14-27`) — notably it omits `AIRACCOON_DB_PASSPHRASE` (and `MCP_TRANSPORT`). Env vars are honored E2E (`McpServerEnvHonoredE2ETests.cs:51-52`).

**Evidence:** `src/AiRaccoon/Program.cs:11` (`CreateBuilder([])`), `src/AiRaccoon.Infrastructure/Options/InfrastructureOptions.cs:15`, `src/AiRaccoon/.mcp/server.json:14-27`, `tests/AiRaccoon.Tests/E2E/McpServerEnvHonoredE2ETests.cs:17,51-52`.

### F11 — Classification: transport/data-root/install-scope are genuinely launch-time; access-mode seed, embedding-model and sync-* are runtime values mis-homed as launch args [INFERRED]

Reasoned from F1/F3/F4/F7: `--transport` selects which protocol binding the process serves (stdio child vs HTTP listener) — meaningless to change at runtime; `--data-root`/`--install-scope` select *which bank* the process serves, bound once at startup (`SqliteConnectionFactory.BankPath` is a startup property) — also meaningless at runtime. These three keep flag+env as launch channels (both are launch channels for one launch concept; `.mcp.json` `args` is the documented client surface, `docs/plans/cli-args-parsing.md:13-16`). By contrast `--access-mode` is a *seed for a runtime setting* (F3), `--embedding-model` overrides a runtime default (F4), and the four `--sync-*` are runtime values (F7) — they must become CLI commands, and their flags+env reads removed. Notably `--access-mode`/env has a one-shot effect only (first bank open), so the seed mechanism itself is replaced by a command that writes the row directly.

### F12 — The 4 secret env vars conflict with strict single-channel; the repo's secrets invariant wins — secrets stay env-only, env remains a channel only for secrets and launch identity [INFERRED]

Reasoned from F1 and `docs/plans/cli-args-parsing.md:47-52` (invariants "No hardcoded secrets", "credentials from environment only"): `AIRACCOON_SYNC_ACCESS_KEY`, `AIRACCOON_SYNC_SECRET_KEY`, `AIRACCOON_OPENAI_API_KEY`, `AIRACCOON_DB_PASSPHRASE` must not become argv (visible in process listings and shareable `.mcp.json`). The single-channel rule therefore applies to non-secret configuration; secrets keep their env channel, and the config commands that need them (`sync add s3`, `model set openai`) document the companion env var instead of taking the value as an argument. The `apiKey` parameter of `memory_configure` dies with the tool (F4's `_remoteApiKey` cache goes too).

### F13 — Moving sync config to runtime requires re-resolving the cloud store per sync call — today it is constructed once from startup options [INFERRED]

Reasoned from F7 (`Dependencies.cs:51-61`): with `sync add/remove` writing settings rows, `ICloudStore` can no longer be chosen once at composition root; it must be resolved per `memory_sync` call from the *current* settings (or re-resolved on a settings-change signal). The `NullCloudStore` ↔ `S3CloudStore` swap semantics, and whether `sync_meta` watermarks survive a remove/re-add cycle, are implementation decisions the CLI design must carry.

### F14 — Runtime settings already hot-reload: CLI-written settings take effect without a restart, except the sync store [INFERRED]

Reasoned from the read paths: access guard reads settings per call (`MemoryAccessGuard.cs:14-25`); embedding settings per embed/search batch (`SqliteMemoryStore.cs:108-112,223-231,731-741`); alpha per search (`:307`); sweep knobs per sweep (`MemoryTools.cs:660-661`). So access/model/alpha/sweep/watch commands apply immediately to a running server. The bank is WAL with `busy_timeout=5000` (`SqliteConnectionFactory.cs:88-94`), so a config CLI process writing settings rows concurrently with the server is safe — no restart protocol needed.

### F15 — Watch-config storage rows (enabled flag + scope allowlist with `*` wildcard entries) have no specified home yet [UNVERIFIED]

The feature baseline names a "persisted watches table" for *registrations* (`file-watcher.feature:17-18`) and requires the *config* to survive restart (`:65-68`), but does not say whether `watch.enabled:*`, `watch.enabled:<project>`, `watch.scope:*` live in the existing `settings` table (which already hosts wildcard-less key/value rows) or a new config table. Not checked because the feature is still a working draft and no watch code exists in `src/` (grep for watch/Watch in src matches only `Stopwatch` in `MemoryTools.cs`).

## Option inventory: channels, defaults, proposed command, removal plan

| Option | Current channels | Sensible default? | Proposed CLI command | Channel removal plan |
|---|---|---|---|---|
| transport | `--transport`, `MCP_TRANSPORT` | yes — `stdio` | **stays launch arg** (F11) | keep both launch channels |
| data-root | `--data-root`, `AIRACCOON_DATA_ROOT` | yes — `~/.ai-raccoon` (`InfrastructureOptions.cs:18,33-34`) | **stays launch arg** (F11) | keep both launch channels |
| install-scope | `--install-scope`, `AIRACCOON_INSTALL_SCOPE` | yes — `user` (`InfrastructureOptions.cs:22`) | **stays launch arg** (F11) | keep both launch channels |
| global access mode | `--access-mode`, `AIRACCOON_ACCESS_MODE` (seed, one-shot) | yes — effective `rw` (F3) | `access default set {ro\|rw\|full}` · `access default show` | delete flag + env read + `SeedGlobalAccessModeAsync`; command writes `access.mode.global` directly |
| per-project access mode | settings row (read-only today) | none — falls back to global `rw` | `access set {project-id} {ro\|rw\|full}` · `access unset {project-id}` · `access list` | first writer; CLI-only thereafter |
| embedding provider/model/baseUrl | `memory_configure` MCP tool; `--embedding-model`/`AIRACCOON_EMBEDDING_MODEL` as local fallback path | yes — no engine (FTS5-only); bundled model for local; openai endpoint default (F4) | `model set local [path]` · `model set openai {model-id} [base-url]` · `model reset` · `model show` | delete `memory_configure` (17→15 tools), delete `apiKey` param, delete `--embedding-model` flag + env read + manifest entry |
| openai API key | env only | none (required for openai) | **stays env** `AIRACCOON_OPENAI_API_KEY` (F12) | keep; documented as companion to `model set openai` |
| structure alpha | `memory_set_structure_alpha` MCP tool | yes — `0.5` (F5) | `retrieval alpha set {0..1}` · `retrieval alpha show` | delete the tool; CLI-only writer |
| sweep threshold | none (setter exists, no caller) | yes — `0.3` (F6) | `sweep threshold set {0..1}` · `sweep show` | nothing to remove — CLI is first writer |
| sweep TTL | none (setter exists, no caller) | yes — `30` days (F6) | `sweep ttl set {days}` · `sweep show` | nothing to remove — CLI is first writer |
| sync endpoint | `--sync-endpoint`, `AIRACCOON_SYNC_ENDPOINT` | yes — unset = off (F7) | `sync add s3 {url} --bucket {name} [--region {name}] [--object-key {key}]` · `sync remove` · `sync show` | delete 4 flags + 4 env reads + manifest entries; settings rows become the source (F13) |
| sync bucket/region/object-key | `--sync-*`, `AIRACCOON_SYNC_*` | yes — object key `memory-<projectId>.db`; region optional (F7) | same `sync add s3` family | same as endpoint |
| sync access/secret key | env only | none — sync off without them | **stays env** (F12) | keep; documented as companion to `sync add s3` |
| db passphrase | env only | yes — unset = plaintext (`EnvEncryptionKeyProvider.cs:4-5`) | **stays env** (F12) | keep; add to registry manifest (currently omitted, F10) |
| watch enabled flag | none (feature unimplemented) | yes — off (spec) | `watch enable {project-id\|*} {true\|false}` · `watch disable {project-id\|*}` | CLI-only from day one (F8) |
| watch scope allowlist | none | yes — empty allowlist | `watch scope add\|remove\|list {project-id\|*} {path}` · `watch list` | CLI-only from day one (F8) |
| appsettings.json | loaded by host, unread | n/a — inert | — | explicitly clear or document as inert (F10) |

## Proposed command tree (verb-style, `{project-id|*}` convention)

Config verbs run as one-shot processes against the bank; bare `ai-raccoon` (or with launch flags) still runs the server. Config commands inherit bank identity from `AIRACCOON_DATA_ROOT`/`AIRACCOON_INSTALL_SCOPE` (the surviving env channel, F11) and are exempt from access-tier checks (user-run, per the watch ruling F8).

```
ai-raccoon                                       # run the MCP server (launch flags: --transport, --data-root, --install-scope)

ai-raccoon access default set {ro|rw|full}       # global default (was --access-mode seed)
ai-raccoon access default show                   # effective default: row value, else rw
ai-raccoon access set {project-id} {ro|rw|full}  # per-project override (more specific wins)
ai-raccoon access unset {project-id}             # drop override → falls back to default
ai-raccoon access list                           # default + every override

ai-raccoon model set local [path]                # provider=local; path omitted = bundled ONNX
ai-raccoon model set openai {model-id} [base-url]# key via AIRACCOON_OPENAI_API_KEY (unchanged)
ai-raccoon model reset                           # back to default: no engine (FTS5-only)
ai-raccoon model show                            # provider/model/baseUrl/engine fingerprint

ai-raccoon retrieval alpha set {0..1}            # retrieval.structureAlpha, default 0.5
ai-raccoon retrieval alpha show

ai-raccoon sweep threshold set {0..1}            # default 0.3
ai-raccoon sweep ttl set {days}                  # default 30
ai-raccoon sweep show

ai-raccoon sync add s3 {url} --bucket {name} [--region {name}] [--object-key {key}]
ai-raccoon sync remove                           # back to default: sync off
ai-raccoon sync show

ai-raccoon watch enable {project-id|*} {true|false}
ai-raccoon watch disable {project-id|*}          # alias for enable … false (per feature scenario)
ai-raccoon watch scope add {project-id|*} {path}
ai-raccoon watch scope remove {project-id|*} {path}
ai-raccoon watch scope list {project-id|*}
ai-raccoon watch list                            # enabled flags + scopes, per project
```

**`{project-id|*}` semantics.** For watch (spec-ruled, F8): `*` is a stored wildcard row; a project-specific row overrides it. For access: `*` would be redundant — resolution is already `perProject ?? global ?? rw`, i.e. the global row *is* the wildcard, so `access set * …` is spelled `access default set …`. For sweep/alpha: the settings rows are bank-global today (F2/F6), so no project target is accepted; `*` is implicit.

**MCP surface after removal:** 17 → 15 tools (`memory_configure`, `memory_set_structure_alpha` deleted); `memory_sync`, `memory_sweep`, `memory_embed_pending` stay as operations; watch part-2 adds `memory_watch_add/status/remove` as operations while `watch enable/scope` stay CLI.

## Still open

- **How launch args bootstrap runtime config.** Config commands inherit `AIRACCOON_DATA_ROOT`/`AIRACCOON_INSTALL_SCOPE` to find the bank; should they also accept `--data-root`/`--install-scope` flags of their own (one code path for bank resolution, or a duplicated resolution in the config CLI)? And should bare `ai-raccoon` with no args keep launching the server (zero-config `.mcp.json` design, `docs/plans/cli-args-parsing.md:113-117`) while `ai-raccoon <verb>` routes to config — i.e. root command plus subcommands in one binary, with `--help` covering both?
- **What exactly happens to `memory_configure`.** Proposal: deleted, replaced by `model set`. Open: whether a transitional release keeps it as a deprecated no-op error pointing at the CLI, and whether `memory_embed_pending` should gain a CLI twin (`model reembed`?) for the deferred-queue case.
- **Secrets vs single-channel.** This report keeps the 4 env secrets (F12) as the one exception, per the repo invariant. Alternative worth debating: interactive secret entry (`sync add s3` prompting for keys) writing a 0600 file or OS keyring — strictly more single-channel, strictly more machinery.
- **Sync runtime re-resolution.** `ICloudStore` must become per-call (F13): re-read settings on each `memory_sync`; decide `NullCloudStore` ↔ `S3CloudStore` swap behavior and whether `sync remove` clears `sync_meta`/tombstones or keeps watermarks so re-add resumes cleanly.
- **Watch-config storage.** Where `watch.enabled:*` / `watch.scope:*` rows live (existing `settings` table vs new table — F15); the feature's "persisted watches table" phrasing covers registrations only.
- **Per-entry TTL** (`SetEntryTtlAsync`, hash-scoped) is data, not config — out of the CLI tree; whether it ever gets a channel (tool or `entry ttl set {hash}`) is a separate decision.
- **`appsettings.json`** (F10): explicitly clear the default sources (`builder.Configuration.Sources.Clear()`) or document the dormant file as inert.
- **Registry manifest** (F10): shrink `environmentVariables` to the survivors (data-root, install-scope, 4 secrets) and add the currently-missing `AIRACCOON_DB_PASSPHRASE`.
- **Migration of existing env-based setups**: env vars that stop being read (e.g. `AIRACCOON_ACCESS_MODE`) become silently inert — already-seeded settings rows survive, so no data loss; worth a release note so users don't assume the env var still works.
