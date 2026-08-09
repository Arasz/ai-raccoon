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

## Tools (22)

Every tool requires `projectId` (camelCase — all parameters are camelCase), except
`memory_promotion_list` where it is optional. Writes land in `project:<id>` by
default; naming a `workspaceId` routes them into that workspace's isolated context.

9 memory tools, 4 workspace tools, 3 watch tools, 2 promotion tools, 2 share tools,
1 sweep tool, 1 sync tool. `memory_configure` and `memory_set_structure_alpha` were
removed by the CLI-config refactor: configuration is no longer an MCP tool — the CLI
verbs are the single config channel (see [Command-line options](#command-line-options)).

| Tool                           | Parameters                                                                                                                                                  | Returns                                                                                            |
|--------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------|----------------------------------------------------------------------------------------------------|
| `memory_write`                 | `projectId`, `content`, `workspaceId?`, `agentId?`, `context?`, `sourceFile?`, `section?`                                                                   | `{hash, path, context, createdAt}`                                                                 |
| `memory_search`                | `projectId`, `query`, `scope=all\|project\|shared`, `workspaceId?`, `limit=20`, `minScore=0.7`, `rrfK=60`, `ftsWeight=1`, `vectorWeight=1`, `contextLabel?` | `{results:[{hash, seq, ranking, path, snippet, sourceFile?, chunkIndex, totalChunks}], projectId}` |
| `memory_list`                  | `projectId`                                                                                                                                                 | `{files: <json tree>}`                                                                             |
| `memory_stats`                 | `projectId`                                                                                                                                                 | `{entries, pending, contexts}`                                                                     |
| `memory_share`                 | `projectId`, `hash`                                                                                                                                         | `{shared: true, context: "shared"}`                                                                |
| `memory_share_extract`         | `projectIds[]`, `mode=propose\|promote`, `limit=20`, `includeTtlRows=false`, `autoPromote=false`, `confirm=false`                                            | `{candidates: [...], promotedHashes: [...], skippedDuplicates, failures: [...]}`                    |
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
| `memory_promotion_list`        | `projectId?`, `limit=50`                                                                                                                                    | `{rows: [PromotionQueueRow]}`                                                                       |
| `memory_promotion_discard`     | `projectId`, `hash?`                                                                                                                                        | `{discarded: n}`                                                                                   |

### Notes on the less obvious tools

- **`scope` values:** `scope=all` (default) searches `shared` + `project:<id>` (+ workspace
  when named); `scope=project` searches `project:<id>` only; `scope=shared` searches the
  `shared` promotion tier only. Workspace scratch is never included in `scope=all` — it is
  only visible to a search that names that `workspaceId`.
- **`memory_share`:** promotes the entry whose `hash` you pass (from a `memory_write`
  or `memory_search` result) into `shared`. It is additive — the source project row
  stays. There is no un-share; `memory_delete` on the shared row's hash removes it from
  `shared`.
- **`memory_promotion_list` / `memory_promotion_discard`:** the propose tier
  (ADR-0007) — `memory_share_extract` in `mode=propose` fills a persisted
  per-project queue (`promotion_queue`) ranked by score; `memory_promotion_list`
  reads it (omit `projectId` to see every project's queue); `memory_promotion_discard`
  drops one row (`hash`) or, with `hash` omitted, the whole project's queue.
  `memory_share_extract` in `mode=promote` drains the top queued candidates into
  `shared`. Every response carries `waitingPromotionsCount`/`promotionsWaitTimeSeconds`
  in `meta`, scoped to the project the call named; once that project holds queued rows,
  `meta.capacity` also carries its `reserved`/`used`/`borrowing` share of the cap
  (ADR-0007's fair-share promise, made observable) — see [`capacity`
  semantics](#capacity-semantics) below for what `reserved` and `borrowing`
  actually mean. The two tools that do not name a single project — `memory_promotion_list`
  with `projectId` omitted, and `memory_share_extract` over several ids — report a
  bank-wide count with `capacity` absent. No response names another project.
- **`memory_share_extract(mode=promote)` result shape:** `candidates` is always `[]` in promote
  mode (it is only populated by `propose`); `promotedHashes` are the hashes actually shared.
  `skippedDuplicates` counts queued candidates that matched something already in `shared` by value
  or path and were dropped without an error. `failures` is a list of `{projectId, hash, reason}`
  for candidates claimed off the queue but never shared, where `reason` is a bounded token —
  `stale-hash` (the queued hash no longer resolves in the entries table) or `share-failed` (any
  other per-candidate error) — see `PromoteFailure`/`ShareExtractResult`
  (`src/AiRaccoon.Core/Memory/PromotionQueue.cs`, `SharedExtraction.cs`). This exists so a caller
  can tell "everything queued was already shared" (`skippedDuplicates` > 0, `failures` empty) apart
  from "everything failed" (`failures` covers the whole batch), and can see partial success instead
  of a single pass/fail verdict for the batch.
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
  allowlist (`ingest scope add|remove|list`) and a concurrency cap (`watch concurrency
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

### Unknown-id rule

An id that a tool cannot act on is handled one of two ways, and which way depends on what kind of
tool it is (see [ADR-0024](../adr/0024-unknown-id-contract.md)):

- **A removal verb is idempotent and reports a count.** An unknown id is a no-op, not an error —
  `memory_delete`, `memory_delete_context`, and `memory_promotion_discard` return `0` for the
  count they would otherwise report; `memory_watch_remove` treats a non-existent watch the same
  way. Calling a removal verb twice on the same id is safe by construction.
- **A state transition refuses an id it cannot act on.** `memory_share` and the workspace family
  (`memory_write` with `workspaceId`, `memory_workspace_status`, `memory_workspace_consolidate`,
  `memory_workspace_discard`) return a typed refusal (`unknown-hash` / `unknown-workspace`, see
  [Error shapes](#error-shapes)) instead of silently doing nothing — there is no well-defined
  "already done" state for promoting a hash that was never written or writing into a workspace
  that was never begun.

### `capacity` semantics

`reserved` is not a fixed entitlement — it's `cap ÷ (number of projects currently
holding at least one queued row)`, recomputed fresh on every meta read
(`PromotionQueueService.GetMetaAsync`, `PromotionCapacityPolicy.CapacityFor`). The
denominator moves: it shrinks as unrelated projects' rows drain out of the queue and
grows the moment another project proposes its first row, so `reserved` for a project
that hasn't changed its own usage can still go up or down between two calls.

`borrowing: true` means "using more than the current fair share," not "at risk of
eviction." Eviction is a wholly separate rule — `PromotionCapacityPolicy.NeedsEviction`
fires only when the queue's total row count exceeds the total cap, regardless of which
projects are borrowing. A project can sit at `borrowing: true` indefinitely as long as
nobody pushes the total over cap.

**Worked example** (cap = 1000):

1. One project (`p1`) has proposed 400 rows. It is the only occupant, so
   `projectCount = 1`, `reserved = 1000 / 1 = 1000`. `p1` shows `reserved: 1000, used:
   400, borrowing: false` (400 ≤ 1000).
2. Four unrelated projects each propose one row. The queue now has 5 occupying
   projects and 404 total rows (well under the 1000 cap, so no eviction fires).
   `projectCount = 5`, `reserved = 1000 / 5 = 200`. Without `p1` proposing or
   discarding anything, its next meta read shows `reserved: 200, used: 400,
   borrowing: true` (400 > 200) — the same 400 rows, a smaller fair share, because
   four other projects showed up.

> **Evidence:** `src/AiRaccoon.Core/Memory/PromotionCapacityPolicy.cs:12-35`
> (`ReservationFor`, `NeedsEviction`, `CapacityInfo`),
> `src/AiRaccoon.Infrastructure/Promotion/PromotionQueueService.cs:138-150`
> (`GetMetaAsync`, `projectCount` = occupying projects)

## Prompts (2)

| Prompt | Purpose |
|---|---|
| `memory-usage-guide` | Protocol: always pass `project_id`; **search memory first** (2-3 query formulations) and escalate to web/code search only by result, writing findings back; watch setup (`ai-raccoon ingest scope add` + `watch enable`, then `memory_watch_add`/`status`/`remove`); workspace isolation, promotion via `memory_share`, search scopes, degradation, bulk ingest. |
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

| Variable                  | Purpose                                                                                                                             |
|---------------------------|-------------------------------------------------------------------------------------------------------------------------------------|
| `AIRACCOON_DB_PASSPHRASE` | SQLite encryption passphrase (page-level via SQLite3MC, SQLite3MC.PCLRaw bundle, default cipher chacha20/sqleet; unset = plaintext) |

Beyond that, the only other environment variables read are the `OTEL_*` ones the
OpenTelemetry SDK itself reads for OTLP export (serve/HTTP mode only, opt-in) —
notably `OTEL_EXPORTER_OTLP_ENDPOINT`, `OTEL_EXPORTER_OTLP_PROTOCOL`,
`OTEL_METRIC_EXPORT_INTERVAL`, and `OTEL_METRIC_EXPORT_TIMEOUT` —
see [OTLP export](#serve-mode) below and [ADR 0009](../adr/0009-otlp-export.md) for the
current set and behavior rather than treating this list as exhaustive.
`OTEL_SERVICE_NAME` is read by the SDK but has no effect here: `service.name` is a
fixed product identity (`ai-raccoon`) that this codebase's own resource registration
always wins over.

All other configuration (access modes, embedding engine, retrieval alpha, sweep,
sync, watch) lives in the settings table and is changed with the CLI verbs below —
environment variables are not read for that runtime configuration (single-channel
ruling). Secrets (OpenAI API key, S3 access/secret keys or the Azure Blob connection
string) are stored in the settings table (encrypted at rest when a passphrase is set),
never in the environment and never in tracked files.

## Command-line options

The server parses its own arguments (System.CommandLine 2.0.10) before the host
builds. Launch-identity flags are CLI-only; a verb runs a one-shot config command
against the bank (results to stdout), bare `ai-raccoon` (with optional launch flags)
runs the server.

| Option | Values | Default |
|---|---|---|
| `--transport` | `proxy`, `stdio`, `http`, `https` (https → warning) | `proxy` |
| `--data-root <path>` | any (`~` expanded) | `~/.ai-raccoon` |
| `--install-scope` | `user`, `project` | `user` |
| `--port <n>` | `1`-`65535`; `0` (random free port) is `serve`-only — the proxy has to dial a port it knows | `7721` |
| `--quiet` | flag | off |

`proxy` is the default and the zero-config path
([ADR 0020](../adr/0020-always-on-http-stdio-proxy.md)): bare `ai-raccoon`
opens no bank, resolves no encryption key, and loads no embedding model — it
probes `http://127.0.0.1:<port>/mcp`, spawns `ai-raccoon serve` when nothing
answers, and forwards every JSON-RPC message to it, restoring the client's
own request id on the response. No tool method is named in the proxy, so a
new tool needs no proxy change. If the backend can neither be reached nor
started within its budget, the process exits `ExitCode.ProxyBackendUnavailable`
(6) with one stderr line naming the URL, the `serve` exit code, and the
`--transport stdio` escape hatch — there is no in-process fallback.
`--transport stdio` is that escape hatch: a complete in-process server, no
proxy, no autostart — exactly how the server behaved before `proxy` became
the default.

`--quiet` sends every log level of a *server host* — the in-process `--transport stdio`
server, `--transport http`, and `serve` — to a file beside the bank instead of
stdout/stderr: `~/.ai-raccoon/quiet.log` at the default user scope, or
`<data-root>/.ai-raccoon/quiet.log` at project scope, the same directory `memory.db` lives
in (`HostLogging.Configure`, `QuietLogging.LogFilePath`,
`SqliteConnectionFactory.BankPathFor`). Nothing from those hosts, not even a warning,
reaches stdout or stderr in this mode, so a `--quiet` server that fails to start or
misbehaves (e.g. an invalid `OTEL_EXPORTER_OTLP_ENDPOINT`) leaves no trace on the
console — check `quiet.log` first. The file is append-only and never rotated; it
accumulates for the life of the installation.

The proxy is deliberately exempt. It builds its own logger factory
(`ProxyRunner.CreateLoggerFactory`) with no file destination, so under `--quiet` it still
logs to stderr at `Warning` and above; and the one line that says the backend could neither
be reached nor started is *written* to stderr rather than logged, so `--quiet` cannot
silence it. A proxy that cannot get a backend says so on the console either way. The
`serve` backend it spawns does inherit `--quiet`, so the backend's own logs land in
`quiet.log`.

### Serve mode

Since ADR-0020, `serve` is not only a manual verb — it is autostarted by the
default `proxy` transport, at proxy startup, whenever nothing already answers on
the port. A client that connects and never calls a tool still leaves a backend
running. This section describes `serve` itself, whether started by the proxy or
run by hand.

`ai-raccoon serve` is the HTTP mode as a first-class verb: it forces the http
transport, applies a 4h idle watchdog (`--idle-timeout 90s|30m|4h|1d`, `0`
disables), prints the bound URL to stdout, and stays in the foreground —
background it with `ai-raccoon serve > serve.log 2>&1 &` (POSIX). If the port
already hosts an ai-raccoon server, `serve` attaches to it and exits 0; the
owning process keeps the watchdog, and the attached run never touches the bank.
A busy port held by a foreign listener fails fast with exit code 3 and a
`--port 0` hint.

`serve --restart` cycles that server instead of attaching to it (ADR-0022).
Attaching is wrong on exactly one path — an update: `dotnet tool update`
replaces the binary while the always-on backend keeps the old assembly loaded,
so every later client attaches to the stale one. `--restart` asks the running
server to stop over `POST /shutdown` (token-guarded, POST-only), waits for the
port to free, then serves in its place; with nothing listening it is a plain
`serve`. The stop gets 10s in total — the host's stated `ShutdownTimeout`,
shared by in-flight calls and every background service, not a per-call
guarantee — after which what is left is aborted and the proxy's documented
at-least-once retry re-issues it against the new backend. The port is then
given 20s to free.

`--restart` kills no process and never falls back to attaching. Every way the
cycle can fail exits `8` with a line naming the port and the manual escape:
the server refuses our token (it serves another data root), it has no
`/shutdown` (too old to be cycled — the first update *onto* this version still
needs the old process stopped by hand), our data root holds no token to
present (nothing is asked to stop), the port is still held after the bound, or
another start won the port while this one was binding. A listener that does
not identify as an ai-raccoon over `/observability` is never sent a shutdown:
it is refused before the bind is attempted, with the unchanged exit code 3 and
a line saying the port is held by something that is not an ai-raccoon.

`/mcp` and `/shutdown` require the `X-AiRaccoon-Token` header: before binding,
`serve` mints a random token into `<data-root>/mcp-token` (0600, exclusive
create, reused across restarts), and every request to either must present it —
the proxy reads the file after a successful probe and sends it automatically.
Both answer an unauthorised call with the same 401 body, whether the header is
absent, the wrong length or simply wrong. `/observability` stays
unauthenticated by design (it returns a PID, the binary version and OTLP
on/off state, nothing that touches the bank). A direct `ai-raccoon
--transport http` launch (no `serve` verb) is **not** gated, and gets no
`/shutdown` at all — see [SECURITY.md](../../SECURITY.md) for the reasoning
and the known gaps.

`serve --mcp-entry [--format hermes|claude|all]` prints the client config entry
for the actually-bound URL — for Hermes (`hermes mcp add ai-raccoon --url
http://127.0.0.1:7721/mcp`) or Claude Code (`.mcp.json` `type: http` entry).
The printed entry carries the URL only, not the token, so a client connecting
this way (bypassing the proxy) must add the `X-AiRaccoon-Token` header itself,
read from `<data-root>/mcp-token`. Keep stderr out of the entry file:
`ai-raccoon serve --mcp-entry > entry.json 2> serve.log &`. One long-lived
HTTP server avoids the ~5-minute stdio recycle of per-connection processes and
lets the background extraction and bank-maintenance hosted services actually
fire.

`serve observability <counters|trace|otlp|pid> [--port <n>]` prints a ready-to-run
diagnostic command for the **running** server, with its process id filled in. It
does not start or touch a server: it reads the PID from `GET /observability` on
the loopback port (default `7721`), so the value cannot go stale, and it returns
the owning process's PID even when the server it dials was itself started by an
attached `serve`. The verb never opens the bank, resolves the encryption key, or
loads the embedding engine.

| Kind | stdout |
|---|---|
| `counters` | `dotnet-counters monitor -p <pid>` — `System.Runtime` only (GC, CPU, working set, thread pool); append `--counters AiRaccoon.MemoryTools` for the tool metrics, broken out by `project_id` on the invocation counter |
| `trace` | `dotnet-trace collect -p <pid> --providers AiRaccoon.MemoryTools` |
| `otlp` | the OTLP endpoint the server exports to; the protocol goes to stderr |
| `pid` | the bare process id, for composing with other tools |

Exit codes: `0` success; `4` nothing listening on the port (or the server predates
the endpoint); `3` the port is held by a foreign listener; `5` `otlp` was asked for
but the server has no OTLP export configured. `--port 0` is a parse error — unlike
`serve --port 0`, there is no "any free port" to dial. Failures write nothing to
stdout, so command substitution yields an empty string rather than an error message.

OTLP export is **serve/HTTP mode only** — a stdio server is a per-connection
process on a ~5-minute recycle, too short-lived for a batch exporter to be worth
its schedule delay and shutdown grace. Since ADR-0020 that scope covers nearly
all traffic: the default `proxy` transport forwards every call to a `serve`
backend, so instrumentation now reaches whatever a client does, not only
callers who opt into `serve` directly. The proxy itself wires no exporter and
propagates no `traceparent` — it records nothing, so there is nothing of its
own to export, and the server it forwards to stays the trace root. It is
opt-in and configured only through the standard `OTEL_EXPORTER_OTLP_ENDPOINT` /
`OTEL_EXPORTER_OTLP_PROTOCOL` variables, read at host-build time; unset means
no exporter is constructed. Exported: the
`AiRaccoon.MemoryTools` meter and ActivitySource, the `AiRaccoon.PromotionQueue`
meter, and the built-in `System.Runtime` meter. See
[ADR 0008](../adr/0008-live-pid-discovery-for-monitoring.md),
[ADR 0009](../adr/0009-otlp-export.md), and
[ADR 0020](../adr/0020-always-on-http-stdio-proxy.md).

Config verbs (each writes settings rows in the bank's settings table; the running
server hot-reloads them):

```bash
# access: who may do what per project
ai-raccoon access default set {ro|rw|full}
ai-raccoon access default show
ai-raccoon access set {project-id|*} {ro|rw|full}
ai-raccoon access unset {project-id|*}
ai-raccoon access list

# model: embedding engine
ai-raccoon model set local [path]
ai-raccoon model set openai {model-id} [base-url] [--api-key <key>]
ai-raccoon model reset
ai-raccoon model show

# retrieval: hybrid-search blend weight
ai-raccoon retrieval alpha set {0..1}
ai-raccoon retrieval alpha show

# sweep: degradation cutoff
ai-raccoon sweep threshold set {0..1}
ai-raccoon sweep show

# sync: cloud snapshot sync
ai-raccoon sync add s3 {url} --bucket {name} [--region {name}] [--object-key {key}] [--cli]
ai-raccoon sync add azure {container} [--object-key {key}] [--cli --account {name}]
ai-raccoon sync remove
ai-raccoon sync show

# watch: file-watcher configuration (registers happen via memory_watch_add)
ai-raccoon watch enable {project-id|*} {true|false}
ai-raccoon watch disable {project-id|*} {true|false}
ai-raccoon ingest scope add {project-id|*} {path}
ai-raccoon watch scope remove {project-id|*} {path}
ai-raccoon watch scope list {project-id|*}
ai-raccoon watch concurrency {project-id|*} {1..16}
ai-raccoon watch list
ai-raccoon watch registered [{project-id}]
ai-raccoon watch remove {project-id|*}

# encryption: bank key source
ai-raccoon encryption bitwarden [-t <token>]
ai-raccoon encryption show
ai-raccoon encryption unset
ai-raccoon encryption migrate

# extract: background shared-extraction (HTTP/S hosts only — a stdio process is
# per-connection and recycled before the loop can fire; default interval 30 min;
# config changes apply live, no server restart needed; propose logs the ranked
# candidates — path, preview, reasons — to the server log; prune reports/removes
# promotion_queue rows orphaned by a deleted or re-chunked entries row (ADR-0022) —
# read-only by default, --apply removes, idempotent)
ai-raccoon extract enable {true|false}
ai-raccoon extract mode {propose|promote}
ai-raccoon extract interval {minutes}
ai-raccoon extract capacity {capacity}
ai-raccoon extract exclude add {prefix}
ai-raccoon extract exclude remove {prefix}
ai-raccoon extract exclude list
ai-raccoon extract list
ai-raccoon extract prune [--apply]

# maintenance: bank housekeeping (every process checkpoints the WAL at startup
# and shutdown — stdio included; the periodic timer runs on HTTP/S hosts,
# default 60 min — and VACUUM + ANALYZE on the vacuum cadence, default 7 days;
# config changes apply live, no server restart needed)
ai-raccoon maintenance interval {minutes}
ai-raccoon maintenance vacuum-interval {days}
ai-raccoon maintenance list
```

**Encryption key sources.** Default: `AIRACCOON_DB_PASSPHRASE` (env). Alternative:
`encryption bitwarden` fetches an unencrypted ed25519 SSH private key from a Bitwarden
Secrets Manager secret via the `bws` CLI and derives the raw SQLCipher key with
`HKDF-SHA-256` (`System.Security.Cryptography.HKDF`, seed as IKM, no salt,
`"ai-raccoon-db-key/v1"` as `info`) → `x'<64hex>'` — see
[ADR 0012](../adr/0012-ssh-key-derivation-hkdf-replacement.md). The command checks
`bws` presence (install guidance when missing), collects project id + secret id
(default: an obviously fake placeholder, unless `AIRACCOON_BITWARDEN_PROJECT_ID` /
`AIRACCOON_BITWARDEN_SECRET_ID` is set — no default may identify a real vault entry),
accepts a per-run-only `-t <token>`, warns that rotating the secret in the Bitwarden UI
without `PRAGMA rekey` bricks the bank, then rekeys + persists. Server startup refuses
loudly when the configured source cannot produce the key.

**`encryption migrate`** rekeys a bank still encrypted under the pre-ADR-0012
`SHA-256(label ‖ seed)` derivation to the current HKDF key. It affects only the
Bitwarden/SSH key source — the env-var passphrase path never went through
`SshKeyDerivation` and is unaffected. It needs exclusive access to the bank (run it
with the MCP server stopped); one of three outcomes follows: the bank is rekeyed, the
bank is already on the current derivation (no-op), or the command refuses (wrong
secret or a damaged bank) and leaves the file byte-identical, so it is safe to retry.
See [how to rekey an encrypted bank](../how-to/rekey-an-encrypted-bank.md).

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
mode's rows — settings never leave the local machine (ADR 0014), but a stale secret row
left behind on this one is still a needless liability once its mode is no longer in use.

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

Encrypted-bank setups set `AIRACCOON_DB_PASSPHRASE` in the client's user-scoped
config, never in a shared or tracked file:

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

## Local embedding model

Local embeddings run in-process on ONNX Runtime over the small int8
all-MiniLM-L6-v2 model (dimension 384, mean-pool + L2-normalize) **bundled inside
the tool package** — `ai-raccoon model set local` needs no sidecar, server
process or download. The binary is gitignored and fetched once by the pinned script
(SHA-256 verified); the tests FAIL (never skip) when it is missing:

```bash
scripts/download-embedding-model.py          # -> src/AiRaccoon/Models/model_qint8_arm64.onnx + vocab.txt
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

A known, expected refusal — an invalid argument, a disabled feature, a path outside
scope — comes back as a normal MCP tool error (`CallToolResult.IsError = true`) rather
than an escaping exception, and is logged at `Information`, not `Error` (issue #151,
fixed by the `ToolRefusals` CallToolFilter in PR #163). Its message always starts with
one of the wire prefixes below — that mapping lives in
`ToolRefusals.RefusalPrefixes` (`src/AiRaccoon/Tools/ToolRefusals.cs`), which is the
source of truth; a test cross-checks this table against it.

| Prefix | Condition | Example message |
|---|---|---|
| `path-outside-scope` | Ingest/watch path falls outside the project's declared ingest scope | `path-outside-scope: Path '<path>' is outside the ingest scope.` |
| `path-not-found` | Ingest/watch path does not exist | `path-not-found: Path '<path>' does not exist.` |
| `unknown-workspace` | `workspaceId` does not exist, or is not active, for the project | `unknown-workspace: Workspace '<id>' does not exist for project '<project>'.` |
| `unknown-hash` | `hash` (e.g. passed to `memory_share`) does not exist in the project's scope | `unknown-hash: No entry with hash '<hash>' in project '<project>'.` |
| `schema-version-unsupported` | The bank's stored schema version is newer than this binary supports (issue #200) | `schema-version-unsupported: bank schema v<n> is newer than this binary supports (v<m>); update ai-raccoon` |
| `watching-disabled` | Watching is disabled for the project | `watching-disabled: Watching is disabled for project '<project>'.` |
| `sync-not-configured` | No sync credentials configured | `sync-not-configured: Memory sync is not configured or its connection string is invalid. Run 'ai-raccoon sync add s3 <url> --bucket <name>' or 'ai-raccoon sync add azure <container>' and enter the credentials when prompted.` |
| `sync-auth-failed` | Sync credentials missing/invalid, or a 401/403 from the cloud provider | `sync-auth-failed: Azure auth failed — run 'az login' (or set AZURE_TENANT_ID/AZURE_CLIENT_ID/AZURE_CLIENT_SECRET for headless use).` (Azure) / `sync-auth-failed: AWS auth failed — run 'aws configure' or 'aws sso login', or verify the keys with 'ai-raccoon sync show'.` (S3) |
| `sync-conflict` | Remote snapshot kept changing mid-merge, past the 3 re-pull/re-merge/re-push retries | `sync-conflict: <detail>` |
| `sync-network` | Network-level failure during sync push/pull. A missing bucket/container (404) on **push** also lands here; on **pull** a 404 means "no remote snapshot yet" and returns null instead — it is not a refusal | `sync-network: <detail>` |
| `sync-corrupt-file` | `PRAGMA quick_check` failed on the pulled remote snapshot — the local DB is not replaced | `sync-corrupt-file: <detail>` |
| `access-denied` | The resolved access mode (`ro`/`rw`/`full`) does not permit the attempted operation | `access-denied: <detail>` |
| `invalid-params` | FluentValidation rejected the request (missing/blank `projectId`, invalid `scope`, out-of-range `limit`, etc.) | `invalid-params: project_id is required` |
| `invalid-argument` | A call's JSON argument shape doesn't match the tool's declared parameter type (e.g. a scalar where an array is declared), a required parameter is missing, a present-but-blank value fails a guard clause, or a value is out of the range a guard clause enforces — caught at argument-binding time or by a guard clause at the top of the tool method, before its logic runs. `ToolRefusals.PrefixFor` walks the exception's base-type chain, so this one table entry covers the whole `ArgumentException` family (`ArgumentException`, `ArgumentNullException`, `ArgumentOutOfRangeException`) as well as the SDK's own `JsonException` | `invalid-argument: The JSON value could not be converted to System.String[]. Path: $ \| LineNumber: 0 \| BytePositionInLine: 5.` |
| `confirm-required` | `memory_share_extract` called with `autoPromote=true` but `confirm` not set to `true` — an explicit enable gate for a promotion that shares data across all listed projects | `confirm-required: autoPromote shares candidates with ALL projects — pass confirm=true to enable` |

Anything `ToolRefusals` does not recognize — a remote embedding provider called without
a key, or any other unmapped exception — is a genuine failure, not a refusal, and its message
does **not** reach the caller. The MCP SDK's `CreateToolCallErrorResult` surfaces the exception
message only for `McpException`; for every other exception type it discards the message and
replaces it with the bare string `"An error occurred invoking '<tool>'."` (measured against the
live server; see `docs/adr/0019-forward-version-write-guard.md`). So a call that hits, say, the
embeddings service's plain `InvalidOperationException`
(`src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs` —
`"OpenAI-compatible embeddings require an API key: run 'ai-raccoon model set openai <model>
--api-key <key>'."`, thrown from whichever tool needed the embedding engine — `memory_write`,
`memory_search`, `memory_ingest_file/directory`, `memory_embed_pending`) does not get that text on
the wire at all: the caller sees only `"An error occurred invoking '<tool>'."`, logged at `Error`,
with no indication of what to fix. This is precisely why the `ArgumentException` family is mapped
to `invalid-argument` above instead of being left unmapped — an unmapped exception type's message
is unrecoverable information loss, not just an untyped prefix.

## Managed store

All tables, indexes, FTS5 virtual table, vec0 virtual table, and triggers live in
`memory.db` with no native extension dependencies. `MemorySchema.EnsureAsync` creates
the schema on first open with `IF NOT EXISTS` on every DDL statement — idempotent,
safe to run on every bank open. No download-on-first-run provisioning, no per-RID
extension binaries, no external SQLite modules.

**Encryption at rest.** When `AIRACCOON_DB_PASSPHRASE` is set, the connection opens with `Password` in the connection string, enabling transparent page-level encryption via the bundled SQLite3MC engine (default cipher chacha20, sqleet
ChaCha20-Poly1305 scheme; the scheme is stored per-database and auto-detected on open). FTS5, vec0, and all SQL operations
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
