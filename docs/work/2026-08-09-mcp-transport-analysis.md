# MCP transport: what stdio actually costs, and what "always-on HTTP" would fix

Research record for task `mcp-server`, 2026-08-09. Four read-only lanes over `src/`, `docs/`,
the live process table and the live bank. Every claim below carries `file:line` or a measurement.

## The question as asked

> What happens when the MCP server for ai-raccoon is using `serve` and agents interact with the DB
> using stdio? Now that there is an MCP HTTP server, using stdio just skips the HTTP server — so we
> get no logs, no metrics, nothing.

## The answer, corrected

**stdio does not "skip" the HTTP server.** A stdio process is not a client of `serve`. It is a
second, independently composed, *complete* server that opens the same SQLite bank directly. Both
hosts call the identical `RegisterMemoryServices(...)` — `McpServerSetup.cs:53` (stdio),
`McpServerSetup.cs:80` (HTTP). There is no path by which a stdio call could ever have reached the
`serve` process. They are peers, not client and server.

That correction matters, because it changes what the problem *is*. It is not a routing bug. It is
**N full servers writing to one bank**, and the observability hole is the least of it.

Right now on this machine: `ai-raccoon serve` (uptime 1h09m) plus **four** stdio processes, all on
`~/.ai-raccoon/memory.db` (136 MB, 12,890 entries, 7 watch registrations). Aggregate RSS measured
at **484–620 MB**.

### Observability: what is actually lost

| Signal | stdio | `serve` (HTTP) | Evidence |
|---|---|---|---|
| Structured logs | **Partially works.** stderr in both modes; Claude Code captures them per session to `~/Library/Caches/claude-cli-nodejs/<slug>/mcp-logs-ai-raccoon/*.jsonl` (verified on disk). Fragmented across 7 project dirs × N sessions × N processes, message structure flattened to text, client-owned cache. Two of four live processes run `--quiet` → floored to Warning. | Works; one process, one log. | `McpServerSetup.cs:85,88-89,153-161,192` |
| Tool-call metrics | **Recorded, unobservable.** Instruments fire identically — recording is transport-independent code in the tool bodies. But no `MeterListener` is ever attached, so every record is a no-op. Guarded by a test: `OtlpExportTests.cs:175-193`. | Works. | `Dependencies.cs:70`; `ToolCallMetrics.cs:18-34` |
| Promotion metrics | **Near-silent.** Same as above, *plus* `ExtractionHostedService` — the loop that generates propose-tier traffic — is registered only for HTTP. | Works. | `Dependencies.cs:113-119` |
| Traces | **Lost, not even created.** No `ActivityListener` → `StartActivity` returns `null`; the span is never allocated. | Works, since 2026-08-08 only (#183 sampler fix). | `ToolExecutionActivity.cs:35`; `OtlpExport.cs:36` |
| OTLP export | **Never wired, by design.** Test-guarded: `OtlpExportTests.cs:153-164`. `CreateAppHost` also clears configuration with no `OTEL_*` re-admission (`McpServerSetup.cs:50`), so the SDK's config channel is dead there too. | Opt-in on `OTEL_EXPORTER_OTLP_ENDPOINT`. | ADR-0009:226-233 |
| `/observability` (PID discovery) | **Lost.** Endpoint is behind the HTTP guard. `serve observability *` exits 4 against a stdio process. | Works. | `McpServerSetup.cs:126-130`; ADR-0008 |

The stdio OTLP exclusion is **an owner decision from 2026-08-07**, explicitly reversing an earlier
symmetric call (ADR-0009:226-233): a per-connection process that recycles every few minutes cannot
pay the exporter's 5 s batch delay plus 5 s per-provider shutdown grace.

So the loss is not *recording* — it is **aggregation and reach**. Instrumentation runs in every
process. What a stdio process lacks is a listener, a way to be found, and a durable home for what
it recorded.

### The part that is worse than observability

Collapsing to one server is not a monitoring nicety. It fixes real defects:

**H1 — SEVERE. N watchers digest the same file change; the delete-then-reingest window makes files
vanish from search.** Every process — stdio included — starts a `FileSystemWatcher` for *every*
registration, not just its own project: `Dependencies.cs:144` (unconditional), and `SelectWatches`
has no project predicate (`MemorySql.cs:287-292`). Today that is **5 processes × 7 registrations =
35 concurrent watchers on 7 directories.**

The scan lease (`watches.scan_owner`, 60 s TTL) guards only the *catch-up scan*, not live event
digestion. `WatchDigestExecutor.DigestAsync` (`WatchDigestExecutor.cs:39-53`) is an unguarded,
untransacted read-modify-write, and the content hash is written **last**:

```csharp
var previous = await watchStore.GetFileHashAsync(...);   // :41  all N read the same stale hash
if (previous == hash) return;                             // :42  so none of them skip
await DeletePathAsync(...);                               // :50  wipes ALL chunks for the path
await store.IngestFileAsync(...);                         // :51  re-chunks + re-embeds
await TouchAsync(..., hash, ...);                         // :53  hash written only now
```

P1 deletes and starts re-inserting; P2 arrives mid-way, deletes the chunks P1 just wrote, and
re-ingests. Between P2's delete and its re-ingest the file returns **nothing** from `memory_search`.
If P2 is killed in that window — and stdio processes are killed on client session end — the chunks
stay gone until the file's content changes again, because the hash row P1 wrote makes the next
digest hash-*skip*. Cost even when it converges: the same file chunked and ONNX-embedded N times.

**H2 — SEVERE (latent). Mixed-binary version lockout.** ADR-0019:68-77 records it: once a newer
binary stamps `user_version`, every older binary hard-fails on *every* bank open, with no
degraded read mode. The fan-out is the mechanism — `dotnet tool update` replaces the binary while
N processes keep their old assembly loaded. The longest-lived process (`serve`, 1h09m here) is the
worst case.

**H3 — HIGH. Concurrent migration.** `MemorySchema.EnsureAsync:224` reads `PRAGMA user_version`
outside any transaction and branches on it; `BEGIN IMMEDIATE` is taken *inside* each step. N
processes racing the first open after an upgrade all enter the same step. `MigrateToV2Async` →
`RebuildVecTableAsync:705-724` does `DROP TABLE vec_entries` + full rebuild over 12,890 rows.
`BEGIN IMMEDIATE` serializes them so the outcome converges, but the rebuild is repeated N times and
every other process's `busy_timeout=5000` counts down against it.

**H4 — MEDIUM. Every `memory_search` is a writer.** `SearchAsync` ends with an unconditional
`BumpAccessAsync` (`SqliteMemoryStore.cs:258`), a per-hash SELECT-compute-UPDATE with no
transaction (`:910-931`). The count increment is atomic; the **rating is not** — two concurrent
searches both compute the rating for `k+1` and the row lands at `k+2`. Rating and access count
silently diverge, and both are ranking/sweep inputs. The bigger consequence is contention: search
takes the write lock, once per result hash, N-way.

**H5 — MEDIUM. Promotion eviction is an unguarded read-modify-write** reachable from stdio via
`memory_share_extract` → `ProposeAsync` (`PromotionQueueService.cs:26-55`), racing `serve`'s own
background loop. Two processes both over cap both evict → over-eviction below the cap.

**Cost.** Each process loads its own ONNX `InferenceSession` (`OnnxEmbeddingGenerator.cs:19`), no
`SessionOptions`, so each also sizes an ORT thread pool for the whole machine. Measured RSS: 83 MB
idle, 158–193 MB after serving searches. At the 8 processes observed earlier: **~0.8–1.2 GB**,
against **~193 MB** for one shared server. Every process start also SHA-256s the 23 MB model
(`BundledModel.cs:38-39`, measured 50–70 ms warm) and walks 977 files across the watch trees.
Measured from `memory-operations.jsonl` (356 calls, 23 sessions): first call in a session has a
median of **254 ms** against **43 ms** for every later call.

`VACUUM` is also structurally dead: the 7-day clock is seeded per process (`BankMaintenance
HostedService.cs:120-125`), and no stdio process lives 7 days. An always-on server is what would
make that knob mean anything.

## Answering the three options

### "Will `ai-raccoon serve` handle concurrent startups?"

**Yes — already, and it is tested.** `ServeRunner` has three layers:

1. **Probe-attach before any work** (`ServeRunner.cs:47-50,148-178`): POSTs `/mcp`, recognizes an
   ai-raccoon server by status ∈ {400,405,406} + `jsonrpc` in the body → prints the owner's URL,
   **exit 0**, never touches the bank.
2. **`AddressInUseException` catch + one re-probe** (`:69-81,183-199`) for the genuine bind race.
3. **Clean exit 3 `PortInUse`** if a foreign process owns the port — no stack trace, no silent
   fallback to a random port.

Proof: `ServeRunnerTests.cs:135-183` starts two real processes released through a shared gate and
asserts exactly one binds, every exit is 0 or 3, no stderr carries a stack trace.

Two caveats: (i) **different ports = no guard at all** — `serve --port 7721` and `--port 7722` both
run against the same bank, by design (ADR-0008:93-105); (ii) **bare `--transport http` has no probe**
— `Program.cs:33` → `HostExtensions.RunAsync` has no `AddressInUseException` handling, so a second
one dies on an unhandled Kestrel exception.

### Option (b): put an HTTP entry in the MCP client config

Every client supports it, and the repo **already renders the exact JSON**:
`McpEntryRenderer.cs:6-10` emits `{"type":"http","url":"http://127.0.0.1:7721/mcp"}` for Claude and
`{"url":...}` for Hermes, via `ai-raccoon serve --mcp-entry`.

But **(b) alone cannot meet the requirement.** No MCP client launches anything for a `url` entry —
Claude Code's docs describe HTTP entries purely in connection terms and reserve process spawn for
stdio. Nothing starts the server, and if it is down when the client boots, the entry simply fails.
Secondary blocker: the ai-badger scaffolder that writes `.mcp.json` structurally cannot emit a
`type`/`url` entry (`mcp_tools.py:_render_entry` writes only command/args/cwd/env/tools;
`stack-mcp.schema.json` is `additionalProperties: false` with no `url`).

### Option (a): stdio process as a proxy — recommended

The three things that normally make MCP proxies lossy **do not exist here**, because
`McpServerSetup.cs:196` sets `options.Stateless = true`, and the 2026-07-28 protocol revision
(SEP-2567) removed `Mcp-Session-Id` entirely:

- no session header to forward,
- no resumability / `Last-Event-ID`,
- server→client requests (sampling, elicitation, roots) already unsupported — not a regression.

The SDK (`ModelContextProtocol` 2.1.0, `Directory.Packages.props:11-12`) has everything needed on
public API, verified by reflecting the shipped `ModelContextProtocol.Core.dll`:

- `McpClient.CreateAsync(IClientTransport, …)` + `HttpClientTransport` (client side),
- `StdioServerTransport` (server side),
- `McpSession.SendRequestAsync(JsonRpcRequest, ct)` — raw untyped forward,
- `McpMessageFilters.IncomingFilters` — intercepts **all** incoming messages; "if a message filter
  does not call the next handler, the default handlers will not be executed."

That last one is a verbatim generic-forward hook. The proxy does **not** have to enumerate methods —
which matters, because a hand-listed method table is exactly the "derive the list, or delete it"
failure: fake `tools/list` and the proxy's tool surface silently drifts from the server's on every
release. Forward `initialize` too, or the proxy advertises capabilities the backend does not have.

Confirmed by grep: this tool surface uses no progress, sampling, elicitation or roots
(`IProgress|ProgressToken|Sampling|Elicit|ListRoots|SendNotification` → zero hits in `src/`). It is
pure request/response, plus prompts (`McpServerSetup.cs:150`).

And "start it if not live" is literally `ai-raccoon serve` — the race is already solved and tested
(above). The proxy also skips the expensive part of today's stdio launch: key resolve, bank decrypt
probe, and the 23 MB ONNX load.

## What this reverses, and what it exposes

Two ratified decisions are being reopened, deliberately:

- `docs/work/archive/2026-08-06-http-serve-design.md:137-146` rejected self-spawning ("re-implementing
  it in-process — redirects, orphan reaping, SIGTERM forwarding — is fragile"), and R15
  (`docs/plans/2026-08-06-http-serve-mode-plan.md:33`) deferred the stdio→http protocol switch.
- ADR-0009's stdio exclusion stays *correct* and stays *in force* — a proxy has no instruments to
  export. It stops mattering because the proxy records nothing; the server it forwards to does.

**The security posture does change, and this is the one thing that must not ship unnoticed.**
There is no authentication on `/mcp` — grep for `RequireAuthorization|AddAuthentication|Bearer`
across `src/` returns zero hits. The only control is the loopback bind (`McpServerSetup.cs:95`).
`SECURITY.md:50-51` names the mitigation explicitly:

> "Keep the HTTP endpoint **opt-in** and loopback-only for the same reason: an unauthenticated
> `localhost` listener is reachable by any local process."

Always-on removes the "opt-in" half of that sentence. A permanent unauthenticated listener exposes
22 tools — including delete/sweep and the settings table holding cloud-sync credentials — to every
local process on the box. The `ro`/`rw`/`full` access modes are the only remaining brake and the
default is `rw`.

**Recommended follow-up, deliberately out of scope here:** a random token minted at first `serve`,
written 0600 into the data root, required as a header. The proxy reads the file itself, so no
client config changes. That is a shared secret compared in constant time — not key derivation or
token signing — so it does not trip the no-hand-rolled-crypto invariant.

## Corrections to recorded facts

- `.ai-badger/state.json` `stillTrue` claims *"MCP_TRANSPORT=http selects the Streamable HTTP
  transport at /mcp; anything else runs stdio"*. **This is false.** `MCP_TRANSPORT` does not exist
  anywhere in `src/`; `ServerConfig.cs:5-10` records that the env layer was removed by the
  single-channel ruling. Transport is `--transport stdio|http|https`, default stdio
  (`DefaultOptions.cs:8`). Stale mentions survive in `docs/plans/cli-args-parsing.md:39` and a doc
  comment at `tests/AiRaccoon.Tests/E2E/E2ETestCollection.cs:6`.
- `McpServerSetup.SelectTransports(string?)` (`:24-27`) — the string-parsing resolver whose doc
  comment still implies an env value — has **zero production callers**; its only reference is a test.

## Known unknowns

- Whether H1 has ever fired in production. There is no server log anywhere under `~/.ai-raccoon`;
  stdio stderr is discarded by clients and two of four processes run `--quiet`. The absence of
  evidence here is itself part of the case.
- Whether a stdio process receives a graceful SIGTERM or is hard-killed on session end.
- The 6–15 s latency tail in `memory-operations.jsonl` (5% of calls recorded as errors, max
  15,005 ms) is real but unattributed — that log carries no PID and no error text.
