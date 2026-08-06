# HTTP serve mode — implementation plan (post-MoE)

Date: 2026-08-06 · Branch: `task/switch-from-stdio-to-http` · Status: **approved for implementation**

Design: `docs/work/2026-08-06-http-serve-design.md` (amended with the rulings below).
MoE review: 4 parallel expert lanes, each an independent report:

| Lane | Report | Verdict |
|------|--------|---------|
| code-reviewer | `docs/work/2026-08-06-http-serve-moe-code-reviewer.md` | APPROVE-WITH-CHANGES (2 MUST-FIX) |
| test-engineer | `docs/work/2026-08-06-http-serve-moe-test-engineer.md` | conditional approval (4 MUST-FIX, all design-doc edits) |
| dotnet-engineer | `docs/work/2026-08-06-http-serve-moe-dotnet-engineer.md` | 2 MUST-FIX + verified corrections |
| protocol-switch research | `docs/work/2026-08-06-http-serve-moe-protocol-switch.md` | build idempotent attach; skip the banner handshake |

## 1. MoE rulings (finding → ruling)

| # | Finding | Ruling |
|---|---------|--------|
| R1 | `AddHostedService<T>` registers only `IHostedService→T` (decompiled 10.0.10: TryAddEnumerable); middleware `GetRequiredService<IdleWatchdog>()` would throw on the first request (probe-verified) | Watchdog DI = three registrations, one instance: `AddSingleton<IdleWatchdog>()`, `AddSingleton<IActivitySignaler>(sp => sp.GetRequiredService<IdleWatchdog>())`, `AddHostedService(sp => sp.GetRequiredService<IdleWatchdog>())` |
| R2 | 60s fixed tick contradicts A6's 2s-timeout/5s-shutdown gate | Tick = `min(60s, IdleTimeout/4)`; at 4h → 60s, at 2s → 0.5s |
| R3 | Q1: 4h watchdog on bare `--transport http` = behavior change with no opt-out; silent exit-0 death under supervisors | Watchdog default gated to `serve` ONLY. Program.cs zeroes `IdleTimeout` on the bare launch path; serve keeps 4h default + `--idle-timeout` (0 disables) |
| R4 | Q2: "dies 4h after real activity" dishonest — ping-keepalive clients reset forever; Stateless=true means no sessions to protect | Middleware counts any `/mcp` request (unchanged); guarantee restated as "zero HTTP traffic for 4h". Middleware branches on `request.Path == "/mcp"` so 404s on other paths don't count |
| R5 | Q3 recipe `> entry.json 2>&1 &` corrupts the JSON (stderr merged into stdout) | Recipe split-streams: `ai-raccoon serve --mcp-entry > entry.json 2> serve.log &` |
| R6 | Q4: channel shape over-engineered for loopback | Single BackgroundService + `Interlocked` timestamp confirmed |
| R7 | Q5: root `--port` must not be silently ignored; `--transport` warning matches the https→stdio precedent | serve's own `--port` wins; else root `--port`; else 7721. Non-http `--transport` → one stderr warning (EventId 602) |
| R8 | A5 vacuous via lost first `Advance` after StartAsync | IdleWatchdogTests: real-time settle BEFORE the first Advance and after each Advance; assert StopApplication-call counter; NotifyActivity-reset case asserts an invocation counter |
| R9 | A6 real-time half can pass with broken NotifyActivity wiring | A6 = deterministic fake-clock host test (test host registers FakeTimeProvider + short timeout) + generous real-time smoke + fake-clock `--idle-timeout 0` never-shuts-down |
| R10 | A8 "idle test with extraction enabled" vacuous (extraction interval floor 1 min > test window) | Shared-FakeTimeProvider two-service test: advance past extraction interval, extraction pass runs, watchdog NOT reset; watchdog still fires. Grep accepted as review gate for "only middleware calls NotifyActivity" |
| R11 | Program.cs routing: today `serve` dies at CliArgs.TryParse ("Unrecognized command"), not at ConfigCommands' catch-all | `serve` added to `Verbs`; Program.cs pre-branch for `["serve"]` BEFORE the `CommandPath.Length > 0` branch; `CliCommandTreeTests` verb-list update is WP1's first test |
| R12 | SCL 2.0.10 `AddParent` does not throw; name-based `GetResult("--port")` prefers the root option (DFS) when both given | Static `internal static readonly` serve-option instances on `CliCommandTree`; serve reads via `GetValueForOption(instance)` (never name-based) |
| R13 | SCL 2.0.10 HAS a TimeSpan converter (design said none); no `ParseArgument` in 2.0.10 | Keep `Option<string>` + pure `IdleTimeoutParser` for `4h` sugar; parse errors surface via `Option.Validators` |
| R14 | Idempotent attach (owner f:) — first-class serve behavior | See §2 (new design section). Probe `POST /mcp` with Accept; recognized iff status ∈ {400,405,406} AND body contains `"jsonrpc"`; 2 attempts, 1s timeout; probe BEFORE bank/key work; loser of a bind race re-probes once, attaches (exit 0) or `PortInUse` 3; attached never arms the watchdog and never touches the bank; new acceptance rows A14/A15 |
| R15 | Protocol switch (owner f:) — WebSocket-style stdio→http banner | DEFERRED: no standard exists (spec transports page); client side needs a ~110-line fork of the SDK `stdio_client` (JSON-parses every stdout line, no pre-spawned process); attach + url-config covers the need; zero-arg auto-banner rejected (breaks non-peeking clients). This plan document is the design record |
| R16 | ai-badger `.mcp.json` → `ai-raccoon serve` is TRANSPORT-BROKEN (scaffolder emits stdio-shaped entries; serve never answers JSON-RPC; Claude Code never reads the printed URL; attached mode exits before initialize) | `.mcp.json` stays stdio. ai-badger PR scope: Hermes url: guidance (server.md) + meta.json fixes (`package: "ai-raccoon"`, description) — see §5 |
| R17 | Verified-clean claims (audit): ExitCode values, EventId-2 duplication, stdout rule, Hermes url-only entries, ServerConfig zero-churn (1 construction site + 1 test helper), host-shape/FakeTimeProvider/E2E patterns exist, EventId inventory (600-series free), SDK pin `ModelContextProtocol.AspNetCore 2.1.0`, middleware-before-MapMcp works (.NET 10 WireSourcePipeline) | No action; rely on them |

## 2. Design amendment — idempotent attach (R14, new §3.7 in the design doc)

`serve` on a busy port that already hosts an ai-raccoon server **attaches** instead of failing:

1. Probe (before bank/key work): `POST http://127.0.0.1:<port>/mcp` with `Accept: application/json, text/event-stream` and a non-JSON body. Recognized iff `status ∈ {400, 405, 406}` AND body contains `"jsonrpc"`. 2 attempts, 1s timeout each.
2. Probe miss → bind (Kestrel). Bind throws `AddressInUseException` (race with a concurrent instance) → re-probe once → attach, or `ExitCode.PortInUse` (3).
3. Attached mode: print the same URL line to stdout, provenance line to stderr (EventId 605), exit 0. Never arms the watchdog, never touches the bank. The owning process owns the watchdog.
4. `PortInUse` (3) remains reachable only for foreign/non-HTTP listeners (TCP-open but unrecognized body, or probe timeout).
5. `--idle-timeout` in attached mode is ignored (the owner decides); document it.

New acceptance rows:

| # | Point | Acceptance | Gate |
|---|-------|-----------|------|
| A14 | attach on existing server | probe-recognized listener → `serve` exits 0, stdout = the URL, stderr has the attach provenance, NO second host bound (port still owned by the first), watchdog count in the first process unchanged | `ServeRunnerTests` attach cases (in-process: bind a real probe-responding stub or a first ServeRunner instance, run a second) |
| A15 | bind-race recovery | two ServeRunner instances started together on the same port: exactly one owns the port; the other attaches (exit 0) or returns `PortInUse` if the probe is unrecognized; no crash, no stack trace | `ServeRunnerTests` race case (barrier-synchronized double start) |
| A16 | foreign listener → PortInUse | a `TcpListener` (non-HTTP) on the port → `serve` exits 3 with the "in use" stderr line + `--port 0` hint | `ServeRunnerTests` (TcpListener held OPEN — no port race) |

## 3. Final acceptance matrix (amended)

A1–A4, A7, A9–A12 as designed, with these amendments:
- **A5** — R8 mechanics (settle before/after each Advance, StopApplication counter, invocation counter).
- **A6** — R9 restructure (fake-clock host test + real-time smoke + `--idle-timeout 0`).
- **A8** — R10 shared-FakeTimeProvider two-service test; grep gate accepted.
- **A13** — R11 (pre-branch before the verb branch; routing pinned via `CliCommandRunnerTests` + A2).

## 4. Work packages (final)

| WP | Contents | Files | Depends on |
|----|----------|-------|-----------|
| WP1 | CLI surface + config: `serve` verb + static option fields, `IdleTimeoutParser` (+ `Option.Validators` hook), `ServerConfig.IdleTimeout` (default Zero — `ToServerConfig` never sets it, R3 by construction), `ExitCode.PortInUse=3`, help + split-stream recipe; verb-list test first | `~ CliCommandTree.cs`, `~ CliArgs.cs` (Verbs/ContainsVerb), `~ CliOptionsExtensions.cs`, `~ ServerConfig.cs`, `~ DefaultOptions.cs`, `~ ExitCode.cs`, `+ Setup/Serve/IdleTimeoutParser.cs` | — |
| **WP2 — watchdog** | `IActivitySignaler`, `IdleWatchdog` (tick = min(60s, timeout/4), StopApplication, R1 DI triple), `McpActivityMiddleware` (path-branched), registration in `CreateWebHost` iff `IdleTimeout > 0` (HTTP only); EventIds 610–612 | `+ Setup/Serve/IdleWatchdog.cs`, `+ Setup/Serve/McpActivityMiddleware.cs`, `~ McpServerSetup.cs` | WP1 |
| WP3 | Serve runtime: Program.cs `["serve"]` pre-branch (R11), `ServeRunner` (force http, probe-attach R14, bootstrap, stdout URL/JSON, `PortInUse` catch), `McpEntryRenderer`, root-`--port` fallback (R7); EventIds 601–605 | `~ Program.cs`, `+ Setup/Serve/ServeRunner.cs`, `+ Setup/Serve/McpEntryRenderer.cs` | WP1; parallel with WP2 (no shared files) |
| WP4 | Docs: README serve-mode section (split-stream recipe, watchdog semantics, attach semantics, Windows/POSIX note), feature doc pointer | `~ README.md` | WP1–WP3 |

Serialization: WP1 → {WP2 ∥ WP3} → WP4. Final gate: `dotnet build` (0 warnings) + full `dotnet test` + manual smoke (`serve --mcp-entry --format claude`, second `serve` attaches, `hermes mcp add ai-raccoon --url ...` round trip).

## 5. ai-badger PR scope (extension, second repo)

- **`.mcp.json` declaration unchanged** (`ai-raccoon`, zero args, stdio) — R16.
- `features/common/mcp/ai-raccoon/meta.json`: `package` `arasz.ai-raccoon` → `ai-raccoon` (drift from the 1.0.9 id migration); description drops "served over MCP stdio" → "served over MCP stdio by default; `serve` launches the HTTP mode".
- `features/common/mcp/ai-raccoon/server.md`: add the Hermes HTTP route — one-time `ai-raccoon serve` (or `--port 0` + attach semantics), then `hermes mcp add ai-raccoon --url http://127.0.0.1:7721/mcp`, with the stdio-recycle motivation (one long-lived process, background extraction actually fires).
- Tests: `test_common_ai_raccoon_mcp_server.py` command assertions untouched; add (if the suite has a pattern for it) a meta.json package-id contract test.
- Gate: ai-badger pytest via the main checkout's `.venv/bin/python3` (worktrees lack `.venv`).

## 6. Out of scope (confirmed)

Protocol-switch banner (R15, deferred — this doc is the record); detached daemon/pid files; HTTPS/authn; settings-table timeout; standalone `mcp-entry` render-and-exit verb; Windows backgrounding recipe (POSIX-only, documented).
