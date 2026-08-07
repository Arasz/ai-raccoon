# Security policy

## Reporting a vulnerability

**Do not open a public issue for a security problem.**

Report privately through GitHub's [private vulnerability reporting][pvr] (the
**Security -> Report a vulnerability** tab) once this repository is hosted there. If
private reporting is unavailable, email **araszkiewiczrafal@gmail.com** with
`ai-raccoon security` in the subject.

### What to include

- What an attacker can do, and what they need in order to do it (a malicious MCP client?
  a crafted tool argument? a hostile package in the local NuGet feed?).
- The affected file and, where possible, a failing test or a reproduction command.
- The version — the `PackageVersion` in `src/AiRaccoon/AiRaccoon.csproj`, or the commit.

### What to expect

This is a **one-maintainer project**. There is no on-call rotation and no guaranteed
response time. Realistically: best effort, typically an acknowledgement within a week,
and a fix released as a normal version bump.

## Supported versions

Only the **latest tagged release** (the version currently live on nuget.org) is
supported. Fixes ship forward; there are no backports to older tagged versions.

## What this project actually is, security-wise

AiRaccoon is a **local MCP server process**. There is no hosted service, no account, and no
network surface beyond an optional localhost HTTP endpoint. The honest threat model is:

| Surface                    | What it does                                                                                                                                                                            | Who controls the input                        |
|----------------------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|-----------------------------------------------|
| stdio transport (default)  | Reads MCP JSON-RPC from the client's stdin, writes protocol messages to stdout, logs to stderr                                                                                          | The MCP client that launched the process      |
| HTTP transport (opt-in)    | Serves MCP over Streamable HTTP at `/mcp` on `localhost`                                                                                                                                | Any process that can reach the listening port |
| `/observability` endpoint (HTTP mode) | Returns the server's PID and OTLP export state on the same loopback port as `/mcp`                                                                                          | Any process that can reach the listening port |
| OTLP export (opt-in)       | Exports metrics and traces to the collector named by `OTEL_EXPORTER_OTLP_ENDPOINT`; off entirely when that variable is unset                                                           | Whoever sets the environment variable for the server process |
| Memory tools (22 tools)    | Read/write/search/manage the SQLite memory bank; watch files/directories; begin/consolidate/discard workspaces; run degradation sweeps; sync to a cloud object store (S3 or Azure Blob) | The calling MCP client                        |
| NuGet package / local feed | Ships the built tool via `dotnet pack` and the local `.nupkg-local/` feed                                                                                                               | The pack/push commands and feed contents      |
| Embedded ONNX model        | Runs `all-MiniLM-L6-v2` inference in-process for local embeddings (~21 MB, bundled)                                                                                                     | The model file shipped with the binary        |
| Cloud sync (opt-in)        | Pushes/pulls VACUUM snapshots to/from a cloud object store (S3-compatible or Azure Blob)                                                                                                | Credentials from the bank's settings table    |
| SQLite encryption (opt-in) | Transparent page-level encryption via SQLite3MC (SQLite3MC.PCLRaw bundle, default cipher chacha20/sqleet); FTS5 and vec0 work unchanged                                              | Passphrase from `AIRACCOON_DB_PASSPHRASE`, or an ed25519 SSH key from Bitwarden Secrets Manager via `ai-raccoon encryption bitwarden` (HKDF-derived; ADR 0012) |

**The dangerous direction is the client that launches the process.** A stdio MCP server
inherits the privileges of whatever starts it and trusts the protocol messages it reads —
a malicious client can invoke tools, and anything a tool does runs with the server's
privileges. Keep the HTTP endpoint opt-in and loopback-only for the same reason: an
unauthenticated `localhost` listener is reachable by any local process.

**Access modes provide a defence-in-depth layer:** `ro` mode allows only reads; `rw`
(default) adds writes; `full` enables destructive operations (delete, sweep, forget).
Per-project modes override the global setting, stored in the bank's `settings` table.

**Path-reading tools are contained by the declared scope.** `memory_ingest_file`,
`memory_ingest_directory` and `memory_watch_add` all take a caller-supplied path, and the
server reads it with its own privileges — without containment, a malicious client could
read any file the process can and turn it into searchable memory that cloud sync may then
push. All three refuse a path outside `watch.scope.<project>` (falling back to
`watch.scope.global`), and the scope is **empty by default**, so a project refuses every
path until an operator declares one with `ai-raccoon watch scope add`. Containment is
enforced in the store, not in the tool layer, so the CLI and the file watcher are bound by
it too.

### What leaves the process when OTLP export is on

Spans carry `project_id` in **plaintext**, alongside `tool`, `result`, `error_type`,
and duration (`src/AiRaccoon/Observability/ToolExecutionActivity.cs`).

Metrics differ by meter, and the difference matters:

| Meter | Carries `project_id`? |
|---|---|
| `AiRaccoon.MemoryTools` (tool calls) | No — only `tool`, `result`, `error_type` (`ToolCallMetrics.RecordInvocation`) |
| `AiRaccoon.PromotionQueue` | **Yes**, on four of its seven instruments — the queued/evicted/promoted/discarded counters (`PromotionQueueMetrics.RecordQueued`/`RecordEviction`/`RecordPromoted`/`RecordDiscarded`). The wait-seconds histogram and the capacity gauge carry no project tag |
| `System.Runtime` (built-in) | No — process-level GC/CPU/memory only |

So project names reach a collector through two channels, not one: trace spans and
the promotion-queue metrics. There is a second, non-privacy cost to the metric
channel — `project_id` is unbounded, so each distinct project becomes its own time
series. That is free over EventPipe locally, but a hosted collector generally bills
per series.

**Memory content never leaves.** No entry text, no search queries, no file contents,
no embeddings — only the scope name (`project_id`) and call-shape telemetry (which
tool, how long, success or failure). If your first worry on reading this is "is my
memory bank being shipped to a collector" — it is not.

The exposure this creates is: whoever can read your collector learns your **project
names** and your usage pattern. Do not use a project id that is itself sensitive (a
client name, an unreleased codename) if you point `OTEL_EXPORTER_OTLP_ENDPOINT` at a
shared team or third-party vendor collector.

For completeness: `project_id` already appears in plaintext in the server's own
stderr log (`serve.log`, when redirected per the README's
`ai-raccoon serve > serve.log 2>&1 &` pattern) regardless of whether OTLP export is
on — e.g. `PromotionQueueService.Log.Proposed`
(`src/AiRaccoon.Infrastructure/Promotion/PromotionQueueService.cs:146`,
`"Propose for {ProjectId}: ..."`) and `ExtractionHostedService.Log.Pass`
(`src/AiRaccoon.Infrastructure/Extraction/ExtractionHostedService.cs:179`,
`"Extraction pass for {ProjectId} ({Mode}): ..."`). OTLP export is not a new class of
disclosure for this value — this is precisely why no hashing is applied before export
(ADR 0009).

The `/observability` endpoint discloses the server's PID, unauthenticated, on
loopback. A PID is not a secret, and the same port already serves `/mcp`
unauthenticated — this widens an existing surface rather than opening a new one, but
it is still one more thing that port answers.

## What is deliberately not here yet

State plainly, so nobody assumes coverage that does not exist:

- **No automated security scanning.** CI (`.github/workflows/`) builds and runs the
  fast test suite on every PR, and the full suite nightly — but none of that is a
  security scan: no CodeQL, no Dependabot, no gitleaks. Secrets are kept out by review
  and by the "no hardcoded secrets" invariant in [`CLAUDE.md`](CLAUDE.md) — verify with
  a manual scan (`grep -riE 'api[_-]?key|secret|password' src tests`) before any push.
- **No release automation.** Versions are set by hand in the csproj; releases are
  traceable per the "releases are traceable" invariant, nothing more.

## Out of scope

- Vulnerabilities in the [ModelContextProtocol C# SDK](https://www.nuget.org/packages/ModelContextProtocol)
  or the .NET runtime — report those to their maintainers.
- Vulnerabilities in the MCP clients (VS Code, Copilot, Claude, …) — report upstream.
- Anything requiring you to already have write access to this repository or to the
  machine it runs on.

[pvr]: https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability
