# Configure and run the AiRaccoon server

Set server flags, manage database passphrases, run background daemons, and trigger zero-downtime updates.

---

## Configuration summary

AiRaccoon stores settings directly in the SQLite `memory.db` settings table. Environment variables are reserved for boot parameters and passphrases.

### Environment variables

| Variable | Purpose | Default |
|---|---|---|
| `AIRACCOON_DB_PASSPHRASE` | Passphrase for page-level SQLite3MC encryption | `(unset - plaintext)` |
| `OTEL_EXPORTER_OTLP_ENDPOINT` | Endpoint for OTLP metrics and trace export | `(unset - disabled)` |

### Launch flags

| Flag | Description | Allowed Values | Default |
|---|---|---|---|
| `--transport` | Communication transport | `proxy`, `stdio`, `http`, `https` | `proxy` |
| `--data-root <path>` | Directory for `memory.db` and state | Any valid directory path | `~/.ai-raccoon` |
| `--install-scope` | Scope partition for database storage | `user`, `project` | `user` |
| `--port <n>` | HTTP listen port for serve mode | `1-65535` (`0` for random free port) | `7721` |

---

## Manage database encryption

AiRaccoon uses **SQLite3MC** (ChaCha20/sqleet cipher) for page-level encryption at rest.

### Enable encryption

Set the passphrase variable before launching:

```bash
export AIRACCOON_DB_PASSPHRASE="your-secure-passphrase"
ai-raccoon
```

When encrypted, FTS5 and vec0 virtual tables stay encrypted on disk without breaking hybrid search.

---

## Serve mode lifecycle and authentication

In serve mode (or default proxy mode), the background server authenticates loopback calls with a local token.

```mermaid
sequenceDiagram
    autonumber
    participant Client as MCP Client / Proxy
    participant Server as ai-raccoon serve
    participant TokenFile as <data-root>/mcp-token
    
    Server->>TokenFile: Mint random token (0600 permissions)
    Client->>TokenFile: Read token from disk
    Client->>Server: HTTP POST /mcp<br/>Header: X-AiRaccoon-Token or Authorization: Bearer
    Server-->>Client: 200 OK (RPC Stream)
```

### Idle watchdog

By default, `ai-raccoon serve` shuts down after 4 hours without traffic to free memory:

```bash
# Custom idle timeout (e.g. 30 minutes)
ai-raccoon serve --idle-timeout 30m

# Disable idle watchdog (run indefinitely)
ai-raccoon serve --idle-timeout 0
```

---

## Zero-downtime server updates

Updating the global tool replaces the binary on disk, but the running server keeps using the old version until restarted. Run `serve --restart` for a clean, zero-downtime handoff:

```mermaid
sequenceDiagram
    autonumber
    participant Dev as Developer / Script
    participant NewServer as New Binary (ai-raccoon serve --restart)
    participant OldServer as Running Server Process
    
    Dev->>NewServer: Run `dotnet tool update -g ai-raccoon`
    Dev->>NewServer: Run `ai-raccoon serve --restart`
    NewServer->>OldServer: POST /shutdown (Authenticated via token)
    OldServer-->>NewServer: Drain in-flight calls (up to 10s)...
    OldServer->>OldServer: Exit 0
    NewServer->>NewServer: Bind Port 7721 & Resume Serving
```

Command sequence:

```bash
# 1. Update global tool binary
dotnet tool update -g ai-raccoon

# 2. Trigger graceful restart
ai-raccoon serve --restart > serve.log 2>&1 &

# 3. Verify new server PID and version
ai-raccoon serve observability pid
curl -s http://127.0.0.1:7721/observability
```

---

## Connecting over HTTP directly

If your agent environment cannot spawn processes directly, connect over HTTP:

1. Launch server: `ai-raccoon serve --port 7721`
2. Read loopback token from `~/.ai-raccoon/mcp-token`
3. Send HTTP requests directly:

```bash
curl -X POST http://127.0.0.1:7721/mcp \
  -H "X-AiRaccoon-Token: $(cat ~/.ai-raccoon/mcp-token)" \
  -H "Content-Type: application/json" \
  -d '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2024-11-05","capabilities":{},"clientInfo":{"name":"curl-test","version":"1.0.0"}}}'
```

---

## Who writes to the bank

**The server is the only process that writes to the bank** (ADR-0075). Every `ai-raccoon settings …`
command you type below runs in the CLI, but the write itself is performed by the server.

**This is invisible in normal use, with one thing worth knowing: a settings command starts the
server if it is not already running.** So the first `settings` command after a reboot takes a
moment longer than the rest, and it leaves a server running afterwards. That is deliberate — it is
the same launcher `serve` uses, with a 30-second budget.

Two commands are exceptions and still act directly:

| command | why |
|---|---|
| `serve` | it *is* the server |
| `encryption …` | it rekeys the bank file itself, and moving it is tracked separately |

`ai-raccoon model set` used to be a third exception. It is not any more — it routes through the
server like everything else, so `encryption` is now the only command that writes the bank directly.

### Changing the embedding model

`model set` is the one settings command that owes real work afterwards: changing the engine makes
every stored vector stale. It is handled as an **outbox** (ADR-0076), which is worth knowing about
because it is visible:

1. One transaction commits the new engine settings, a durable migration record, **and** marks every
   embedded row pending — so the old vectors leave the search index the moment it commits.
2. The command returns. There is no progress output; re-embedding happens in the background.
3. **While a migration is open, every tool call is refused** with `model-migration-in-progress`.
   That is deliberate: the alternative is serving searches against a half-migrated bank.
4. A relay finishes the re-embed and marks the record complete. Only then does the bank serve again.

**If the server dies mid-migration, the next one finishes it** — the record survives the crash and
the startup pass picks it up. You do not need to re-run `model set`.

The reason the old vectors are dropped at commit rather than replaced one by one: a bank holding
half one model's vectors and half another's returns quietly worse results with no error anywhere.
Dropping them degrades search to keyword-only until the re-embed lands, which is visible and
recoverable.

If the server cannot be reached, a settings command **fails loudly rather than falling back to
writing directly**, with distinct exit codes for *refused* and *unreachable*. A write that could not
be delivered never reports success.

**Why it works this way.** Two processes writing one SQLite file is a lock-contention problem nobody
chose; it accumulated one command family at a time. Routing every settings write through the server
removes that contention, and it is what makes the encryption rekey's connection-pool clear
meaningful — clearing a pool only affects the process doing the clearing.

---

## Tune what gets stored and what gets searched

Four settings families gate the write path, the read path and the reaper. All are bank-global
and all are stored in the `settings` table, so they survive restarts and apply to every project.

### Write-path noise filtering

Rejects machine-generated content before it reaches the bank; the rejected text is kept in the
noise store rather than discarded ([ADR-0039](../adr/0039-noise-learning-substrate-and-shadow-mode.md)).

```bash
ai-raccoon settings noise show        # enabled: True
ai-raccoon settings noise disable     # accept every write, even ones a policy would reject
ai-raccoon settings noise enable      # the default
ai-raccoon noise entries              # summarize what has been rejected — reads noise_entries, not settings
```

### Read-path query guard

Refuses a `memory_search` query that is itself machine output, and annotates one that merely
contains log-like content ([ADR-0040](../adr/0040-read-path-query-guard.md)). Armed by default.

```bash
ai-raccoon settings queryguard show             # enabled: True  shadow: False  structural: False  threshold: 0.98939822280316
ai-raccoon settings queryguard disable          # every query runs untouched
ai-raccoon settings queryguard shadow enable    # record what the guard would have done, without acting on it
```

Shadow mode is the safe way to measure your own traffic before arming anything: verdicts are
logged and then discarded, so no caller sees a refusal or an annotation.

The structural detector ([ADR-0041](../adr/0041-structural-noise-detector.md)) is a third input
to the *warn* tier only — pure shape statistics, no embedding, and never able to refuse. It ships
off:

```bash
ai-raccoon settings queryguard structural enable
ai-raccoon settings queryguard structural threshold set 0.95   # 0..1; lower annotates more
ai-raccoon settings queryguard structural disable
```

### The sweep reaper

Deletes low-rated, aged project entries on a cadence ([ADR-0025](../adr/0025-the-sweep-reaper.md)).
On by default; shared-tier entries are exempt, and a project not in `full` access mode is skipped.

```bash
ai-raccoon settings sweep show                  # enabled: True  interval: 24 h  threshold: 0.3
ai-raccoon settings sweep disable               # the kill switch
ai-raccoon settings sweep interval-hours 168
ai-raccoon settings sweep threshold set 0.55
```

### Retrieval fusion

```bash
ai-raccoon settings retrieval alpha show        # 0.5
ai-raccoon settings retrieval alpha set 0.7     # 0..1; weights the structure vector against the content vector
```

### Self-instrumentation (metrics)

Controls the background writer for AiRaccoon's own performance measurements (see
[Read back performance metrics](read-performance-metrics.md)). Recording itself cannot be turned
off — these three settings only tune the writer, not whether it runs.

| Setting | Key | Default |
|---|---|---|
| Buffer capacity | `metrics.buffer-capacity.global` | `1000` measurements |
| Flush interval | `metrics.flush-interval-seconds.global` | `30` seconds |
| Hot-table retention | `metrics.retention-days.global` (best-effort — holding more is not a violation) | `28` days |

```bash
ai-raccoon settings performance list                  # buffer capacity, flush interval, retention — with take-effect timing
ai-raccoon settings performance buffer-capacity 2000   # takes effect on the next server restart
ai-raccoon settings performance flush-interval 10      # takes effect on the next flush tick
ai-raccoon settings performance retention 60           # takes effect on the next maintenance pass
```

The three knobs take effect on different cadences, and each command's own output states which:
`MetricsFlusher` re-reads the flush interval inside its periodic loop, so a change applies on the
next tick with no restart needed, but reads buffer capacity only once, at the top of its startup
loop — a change there needs the server restarted; `MetricsRetentionJob` re-reads the retention
window on every maintenance pass. `buffer-capacity` and `retention` are bounded
(`MetricsConfigKeys.MaxBufferCapacity`, `MaxRetentionDays`) — an unbounded buffer capacity is an
allocation an operator could otherwise set arbitrarily large, and an unbounded retention window
overflows the reaper's `DateTimeOffset.AddDays` arithmetic.

---

## Related documentation

- [ADR-0020: Always-on HTTP stdio proxy](../adr/0020-always-on-http-stdio-proxy.md)
- [ADR-0022: Authenticated loopback restart](../adr/0022-authenticated-loopback-restart.md)
- [Security threat model](../../SECURITY.md)
