# HTTP serve mode — idempotent attach & stdio→http protocol switch: feasibility report (research + design lane)

Date: 2026-08-06 · Branch: `task/switch-from-stdio-to-http` · Lane: research + design (subagent), input to owner-gate review

Status: **verdicts ready — not merged into the design doc.** This report evaluates the two questions the owner elevated to first-class (f: 2026-08-06). It does **not** modify `docs/work/2026-08-06-http-serve-design.md` or any code; if the owner approves, the design doc gains the new sections/acceptance rows proposed here.

## Verdicts (read this first)

- **Part A — idempotent attach: BUILD it** (one small component, high robustness value), **but it does NOT make `ai-raccoon serve` the right `.mcp.json` command default for Claude Code.** Stdio zero-arg `ai-raccoon` remains the right `.mcp.json` default; HTTP is a Hermes `config.yaml` `url:` story pointed at a `serve` instance.
- **Part B — banner handshake: DO NOT SHIP now.** Defer with this report as the stored design record. `url`-config + idempotent serve covers the real need; the handshake's one genuine win (ephemeral random-port self-discovery) does not justify a bespoke protocol plus a client-side fork of the SDK stdio transport.
- **ai-badger `.mcp.json` re-scope:** a `command` entry that prints a URL cannot serve Claude Code in *either* mode (started or attached) — see A.8. The "HTTP default" change should be re-scoped before it touches `features/common/stack-mcp.json` or the pinned test `test_common_ai_raccoon_mcp_server.py`.

## Evidence base (grades per evidence-first research)

| Finding | Grade | Evidence |
|---|---|---|
| `MapMcp` maps POST always; GET/DELETE only when `!Stateless` | READ | csharp-sdk `McpEndpointRouteBuilderExtensions.cs:82-103` (raw fetch, main, 2026-08-06) |
| POST `/mcp` without `Accept: application/json, text/event-stream` → **406** + JSON-RPC error body | READ | csharp-sdk `StreamableHttpHandler.cs:52-61` |
| POST `/mcp` with valid Accept but non-JSON body → **400** + JSON-RPC error body | READ | `StreamableHttpHandler.cs:64-80` |
| ai-raccoon HTTP host is `Stateless = true` | READ | worktree `src/AiRaccoon/Setup/McpServerSetup.cs:133-139` |
| Python SDK `stdio_client` spawns the process itself; `stdout_reader` JSON-parses every stdout line; non-JSON line → injected Exception | READ | `~/.hermes/hermes-agent/venv/lib/python3.11/site-packages/mcp/client/stdio/__init__.py:106-217` (esp. 139-164) |
| Hermes spawn site: `_run_stdio` wraps command with watchdog, then `stdio_client`, PID/pgid capture around spawn | READ | `~/.hermes/hermes-agent/tools/mcp_tool.py:2444-2552` |
| Claude Code `.mcp.json` command entries are stdio-protocol entries; `type: http` + `url` entries exist | READ/INFERRED | design doc §3.4; .mcp.json schema; no standard for URL-announcing commands exists (B.5) |
| MCP spec: stdio and Streamable HTTP are separate transports; no upgrade/handoff in any revision incl. 2026-07-28; no SDK implements one | READ (prior research, per task context — not redone) | spec transports page (transports §stdio, §streamable-http); SDK survey |
| Claude Code behavior on a command that never answers `initialize` (timeout → server disabled) | INFERRED | from its stdio client contract (JSON-RPC over stdin); not measured |
| Race-loser bind failure throws `AddressInUseException`/`IOException` | READ | design doc §3.2 (A4), Kestrel semantics |

---

# PART A — Idempotent "already serving → point at it" semantics

## A.1 Problem restated

Claude Code spawns `.mcp.json` command entries per client. If the command became `ai-raccoon serve` on a fixed port (7721), the second concurrent client's spawn collides with the first: under the current design the second process fails fast with `ExitCode.PortInUse = 3` and its client has a dead server. The fix under evaluation: before binding, `serve` detects an already-listening ai-raccoon server on the target port and switches to **attached mode** — print the existing server's URL (same stdout contract) and exit 0 without starting a host.

## A.2 Detection — cheap and safe probe (design)

**Verified SDK routing facts that decide the probe shape:**

1. **GET cannot be the positive probe.** In Stateless mode the GET endpoint is *not mapped at all* (`McpEndpointRouteBuilderExtensions`: "The GET endpoint is not mapped in Stateless mode"), so `GET /mcp` on ai-raccoon is a plain 404. The task's "probably 405/406" guess is correct only for stateful servers and for the 2026-07-28 per-request-metadata protocol (which 405s GET); it does not hold for this server. A 404 is indistinguishable from "no route" — useless as a fingerprint.
2. **POST is the fingerprint.** `HandlePostRequestAsync` checks the Accept header *before* parsing the body: missing either `application/json` or `text/event-stream` → **406 Not Acceptable** with a JSON-RPC error body; a valid Accept with a non-JSON body → **400 Bad Request** with a JSON-RPC error body (`id: null`).

**Probe specification** (new component `Setup/Serve/ServerProbe.cs`, injectable for tests):

- One HTTP POST to `http://127.0.0.1:{port}/mcp`: `Content-Type: application/json`, `Accept: application/json, text/event-stream`, body `x` (deliberately non-JSON), **total timeout 1 s** (an HttpClient with `Timeout = 1s` on a raw loopback URL; no DNS).
- **Recognized iff:** HTTP status ∈ {400, 405, 406} **and** the response body contains `"jsonrpc"` (the SDK's `WriteJsonRpcErrorAsync` JSON-RPC envelope; a 404-HTML or 200-HTML from a foreign server fails the body check).
- **Not recognized:** connection refused / timeout (nothing there, or a non-HTTP listener that accepts TCP but never answers — e.g. the A4 test's `TcpListener`), or any other status/body.
- Probe **retries**: 2 attempts, 250 ms apart, before concluding "absent". This covers the race loser re-probing while the winner is mid-`StartAsync` (Kestrel begins accepting at the start of `StartAsync`; the request pipeline is built before start, so an early probe is served — INFERRED from the Kestrel lifecycle).

**Misidentification analysis.** A TCP connect alone cannot distinguish an ai-raccoon server from any other listener — agreed, and that is exactly why the probe is TCP + HTTP fingerprint, not TCP alone. The residual risk is a *foreign MCP server* (another MCP tool) squatting 7721: it answers the probe with the same 400/406 JSON-RPC shape, so we attach and hand the client that server's tool set. On loopback with a dedicated, ai-raccoon-chosen port this is the same risk class as any fixed-port collision and is **acceptable**; two mitigations are available at increasing cost: (a) log the probe outcome on stderr (EventId 604) so the failure is diagnosable; (b) Tier-2 identity probe (full `initialize` round trip, assert `serverInfo.name` starts with "AiRaccoon") — **not in v1** (adds protocol-version negotiation complexity for a scenario that has not occurred), documented as a follow-up if it ever bites. Decision: v1 ships Tier 1 + stderr diagnostics.

**Probe ordering matters.** The probe must run **first**, before key resolution, bank open, and `EnsureEmbeddingAvailabilityAsync` (attached mode must never touch the bank or trigger a model download). This short-circuits the design doc's §3.2 ServeRunner sequence: probe → (attached? print URL, exit 0) → full boot.

## A.3 Race window — retry-once-on-bind-failure

Two instances start simultaneously: both probe (miss), both boot, both call `StartAsync`; one binds, the other throws `AddressInUseException`/`IOException` (Kestrel). The loser must **not** return `PortInUse`:

1. Catch around `StartAsync` (the design doc's existing busy-port catch — same catch site).
2. Re-run the probe (with its 2-attempt retry).
3. Recognized → attached mode: print URL to stdout, exit `Success`.
4. Not recognized → `ExitCode.PortInUse = 3` with the (updated, see A.6) message.

This is a single re-probe, not a loop: after one failed re-probe the port is held by something we cannot identify, and failing honestly beats spinning. The winner needs no change — it bound first, it owns the port, it prints the URL and serves.

## A.4 Exit code and the stdout contract (marker question)

- **Exit code: 0 (`Success`).** The command's contract in attached mode is "ensure an HTTP ai-raccoon server is reachable at the URL" — fulfilled. Same semantics as `systemctl start` on an already-running unit. Scripts that loop `ai-raccoon serve` until they see the URL line terminate correctly.
- **No marker on the URL line.** Attached mode prints the *identical* single line `http://127.0.0.1:{port}/mcp` (and the identical `--mcp-entry` JSON — the URL is true, the entry is correct). Provenance belongs on stderr: new EventId 605, info `ai-raccoon: attached to existing server at {Url}`. A distinguishing prefix (`attached: …`) would break every existing parser for zero benefit; a script that needs provenance can read stderr. Decision: stdout identical, stderr distinguishes.

## A.5 `--idle-timeout` in attached mode

The attached instance owns nothing and exits immediately; it **never arms the watchdog**. The owning process's watchdog governs the server's lifetime. Consequences, stated as contract:

- `--idle-timeout` on an attaching invocation is parsed normally but not acted on (one stderr info line; no error).
- If the owner runs with `--idle-timeout 0` (watchdog disabled), the server persists until killed — identical to today's bare `--transport http` behavior; not a new risk.
- The attached instance must not, on exit, signal or touch the owner in any way (it does not hold the port, the process, or the bank).

## A.6 Impact on design-doc A4 (busy port) — PortInUse is still reachable

With attach in front, `PortInUse = 3` remains reachable in exactly two cases:

1. **Race loser whose re-probe fails** (winner not yet recognizable, or winner died mid-startup) — rare, mitigated by probe retries.
2. **A non-ai-raccoon listener on the port** — foreign HTTP server (probe status/body mismatch), or non-HTTP listener (probe timeout; the A4 `TcpListener` test case).

So A4's gate stays green and honest (the `TcpListener` still yields `PortInUse = 3`), but the **error message should change** because the old message ("port is in use — pass --port 0") is now wrong in the common case where a *real* ai-raccoon holds the port: EventId 603 becomes `ai-raccoon: port {Port} is in use by a service that is not an ai-raccoon server — pass --port 0 for a random port, or free the port`. Proposed new acceptance rows for the design doc (not added there yet):

- **A14 — attach**: with a live ai-raccoon server on the port, a second `serve` prints the URL, logs 605, exits 0, starts no host, and the bank is never touched (assert: no key-resolution path runs — e.g. fake resolver that throws if called).
- **A15 — race**: two concurrent `ServeRunner.RunAsync` on the same free port → exactly one host binds, the other attaches (exit 0); with a `TcpListener` on the port → both paths return `PortInUse` 3 (loser re-probe fails).

## A.7 Failure modes (summary)

| Mode | Probe result | Outcome |
|---|---|---|
| Nothing on port | refused/timeout | boot (normal) |
| ai-raccoon on port (any version with `MapMcp("/mcp")` stateless — old `--transport http` included) | recognized | attach, exit 0 |
| Foreign HTTP server on port | unrecognized | `PortInUse` 3 |
| Non-HTTP TCP listener (A4 test) | timeout | `PortInUse` 3 |
| Foreign **MCP** server on port | recognized (fingerprint matches) | attach → wrong tool set; accepted residual risk, stderr diagnostic, Tier-2 noted |
| Owner mid-shutdown (port bound but dying) | recognized briefly | client may connect to a dying server; transient, client-level retry; owner restart rebinds |
| Different `--data-root` owner | recognized | attach points client at the *other* bank — **documented** (fixed port is the contract; use `--port 0` for an isolated bank) |
| Race: both probe miss | — | retry-once path (A.3) |

## A.8 Part A verdict — one recommendation

**Recommendation: build idempotent attach, but keep stdio as the `.mcp.json` default for Claude Code; HTTP only for Hermes (`config.yaml` `url:` form).**

Reasoning chain:

1. **Claude Code command entries are stdio-protocol entries.** `.mcp.json` `command` entries mean "spawn this process and speak newline-delimited JSON-RPC over its stdin/stdout". There is no standard, and Claude Code implements none, in which a command announces a URL and the client switches transports (B.5).
2. **Started mode never answers JSON-RPC.** `serve` forces http-only transport; it never reads stdin. The first client's `initialize` hangs until timeout → Claude Code disables the server, leaving the process running (holding the port until the watchdog reclaims it). Attach does not change this: the first spawn was already the problem.
3. **Attached mode exits immediately.** The second client's spawn prints one line and exits 0 — the client sees EOF before `initialize` completes → server disabled. Attach converts "second client's server is dead" into "second client's server exits immediately": the *collision* is solved, the *protocol mismatch* is untouched.
4. **Therefore** `ai-raccoon serve` as a `.mcp.json` command cannot work for Claude Code in either mode, and no amount of idempotence fixes that. The per-client-spawn model only works with a stdio-speaking command.
5. **What should the ai-badger change be, then?** Either keep `command: ai-raccoon` (zero-arg stdio; the pinned test `{'command': 'ai-raccoon', 'tools': ['*']}` stays), or — if the owner insists on an HTTP default — the *renderer* must emit a `{"type": "http", "url": "http://127.0.0.1:7721/mcp"}` entry, which requires an out-of-band already-running server (bootstrap + 4 h-watchdog interplay — an open problem, see Still open). The middle option, `command: ai-raccoon serve`, is the worst of both worlds and should not land.
6. **Idempotent attach still ships** — it is ~one small component, it makes `serve` safe to run twice by humans and scripts, it is the load-bearing piece of the Hermes `url:` path (double-start protection), and it is the enabling primitive if Hermes ever gains an "ensure the URL is up" spawn mode. It is also cheap insurance for the ai-badger http-url entry once the bootstrap question is answered.
7. **The only shape that would make per-client command spawns + one shared HTTP server work for Claude Code** is a built-in stdio→HTTP bridge in the binary (the spawned stdio process proxies JSON-RPC to the shared HTTP server; first spawn creates it, later spawns attach) — i.e. mcp-remote in reverse, embedded. It is a real feature with real session-mapping complexity; out of scope today (see B.6).

---

# PART B — WebSocket-style protocol switch (stdio → http on a spawned process)

## B.1 Protocol shape and backward compatibility

**Design:** `ai-raccoon serve --stdio-upgrade` prints exactly one banner line as its first stdout byte — `MCP-UPGRADE: http://127.0.0.1:{port}/mcp\n` — *after* binding (the URL must be real; `--port 0` works), then keeps running as the HTTP server. In upgrade mode the banner **is** the stdout contract (it replaces the plain URL line; nothing else ever goes to stdout in serve mode, so "first line" stays unambiguous).

**Distinguishability:** JSON-RPC frames start with `{`; the banner starts with `M`. No plausible server output collides. The client's rule is: first line has the exact prefix `MCP-UPGRADE: ` + a parseable `http://127.0.0.1:<port>/mcp` URL → upgrade; anything else (including `{`) → stdio.

**Backward compatibility — the banner must be opt-in, never default.** Old servers print JSON-RPC starting with `{`; a client that doesn't peek would hand a banner line to the SDK's stdio parser, which fails the JSON parse and injects a parse exception into the read stream (SDK `stdio_client`, lines 154-159) — the session dies. So a *default* banner breaks every existing stdio client (Claude Code, Cursor, any SDK-based tool). The zero-arg `ai-raccoon` auto-banner variant (".mcp.json could keep a zero-arg command if the DEFAULT bare launch gained an auto-upgrade banner") is therefore **rejected**: it breaks all non-peeking clients by construction, and an opt-in flag in `.mcp.json` costs exactly as much configuration as a `url:` entry — the variant buys nothing. Banner only under an explicit flag, only for clients that declare they can peek.

**The pipe question:** in upgrade mode, the client should **keep the pipe open as a liveness channel** (close stdin — the server is HTTP-only and never reads it — but keep stdout; the child writes nothing more, and a closed stdout would EPIPE the watchdog relay). Liveness = a `process.wait()` watcher; on death, the HTTP transport fails and the existing reconnect machinery respawns.

## B.2 Client integration point (grounded in code)

**Today's spawn path** (`tools/mcp_tool.py`, `_run_stdio`, ~2444-2552): command is wrapped by `_wrap_command_with_watchdog` (line 2442, the parent-death supervisor) → `StdioServerParameters` built → `async with stdio_client(server_params, errlog=_errlog)` → `ClientSession(read_stream, write_stream)` → `initialize` (bounded by `connect_timeout`) → tool discovery. PID/pgid capture happens around the spawn (2474-2514) for the kill-sweep.

**The SDK owns the spawn and the parse.** `mcp/client/stdio/__init__.py:106-217`: `stdio_client` spawns via `anyio.open_process(..., start_new_session=True)` and immediately starts `stdout_reader`, which splits stdout on newlines and `model_validate_json`'s each line; a non-JSON line is logged and injected into the read stream as an Exception. **Consequence:** the peek cannot ride on top of `stdio_client` — the banner would be consumed and rejected by its reader — and `stdio_client` accepts neither a pre-spawned process nor a pre-read line. The fallback path (no banner) therefore forces a fork of the spawn loop.

**Minimal change sketch** — a new `_upgrade_aware_stdio_client(server_params, errlog, peek_timeout=15.0)` in `mcp_tool.py`:

1. Spawn exactly as the SDK does (watchdog wrapper already applied upstream; `anyio.open_process`, `start_new_session=True`, `stderr=errlog`).
2. `await asyncio.wait_for(_read_first_line(process.stdout), peek_timeout)` — bytes until `\n`, capped at 4 KiB, decoded utf-8/`replace`.
3. **Banner** → parse URL → close stdin → start a `process.wait()` liveness watcher → yield the stream pair from `streamablehttp_client(url)`. The `ClientSession` machinery (initialize, discovery, sampling hooks) is transport-agnostic and unchanged; the existing reconnect/backoff loop keeps working.
4. **Otherwise** → run a forked copy of the SDK's reader/writer (~110 lines: `TextReceiveStream` decode, newline split, `JSONRPCMessage` validation, Exception injection, `SessionMessage` wrapping, stdin writer, close-stdin → wait 2 s → killpg termination) with the buffered first line pre-fed → yield stdio streams.

**Risks:** (a) the fork drifts from SDK behavior on upgrades — mitigate by pinning the SDK version and a small unit test over the fork with a fake process; (b) peek timeout vs cold start — ai-raccoon boot includes bank open and embedding-availability checks; a short timeout falls back to stdio and the banner arrives late → parse exception → broken session. So `peek_timeout` must be generous (default 15 s, reuse the existing `connect_timeout` config, `readline`-style early return on `{`); (c) server prints nothing → timeout → stdio fallback with an empty buffer — safe (the SDK's parse-failure injection is non-fatal for one line, and `initialize` is itself bounded); (d) partial first line → the bounded read-until-`\n` handles it; (e) pipe ownership — we spawned, we own; the banner branch's closed stdin is harmless to an HTTP-only child.

## B.3 What the handshake actually buys

The config-only path (Hermes `config.yaml` `url:` entry + `serve` run once) already reaches the same end state with zero protocol invention. The banner's **one genuine win is random-port self-discovery**: `serve --port 0 --stdio-upgrade` — no fixed port (collision impossible by construction), no config edit, URL delivered in-band. Everything else it offers is already had.

Why that win is smaller than it looks:

- **Idempotent attach neutralizes the fixed-port problem** (Part A): the default 7721 never collides, so "no fixed port" is aesthetic unless two different banks/instances must run simultaneously.
- **Per-connection spawns don't want long-lived servers anyway.** Hermes recycles stdio servers ~every 5 min; an upgrade-spawned HTTP server gets recreated per connection, so the long-lived benefits (warm embeddings, extraction hosted service) never materialize — and the parent-death watchdog wrapper kills the process when Hermes dies regardless. The banner only pays off in a "spawn once, keep forever" client model — which is the config-`url` model minus the config line.
- **The zero-arg auto-banner variant is actively harmful** (B.1) and an opt-in flag in `.mcp.json` is as much config as a `url:` entry.

## B.4 Effort estimate

- **Server (ai-raccoon):** trivial. `--stdio-upgrade` flag, banner print after bind, ~30-40 LOC + 2 tests (unit: banner is the single stdout line, printed post-bind; E2E: banner + live `/mcp`).
- **Client (Hermes):** moderate and disproportionately risky. ~120-150 LOC fork of the SDK spawn/read/write loop + tests, including an interop test against a *fake* banner server (a Python script that prints the banner and serves Streamable HTTP — no ai-raccoon binary dependency in the Hermes suite). The risk is not the lines, it is owning a private copy of SDK transport behavior (decode, termination, task-group cleanup) that upstream can change under it.
- **Total:** ~2-3 days including review, for a feature with one consumer and no standard.

## B.5 Is there any standard? No.

Confirmed by the completed research (not redone): the MCP spec transports page defines stdio and Streamable HTTP as **separate** transports with no upgrade/handoff mechanism in any revision through 2026-07-28; no SDK (TypeScript, Python, .NET) implements a stdio→http switch; the only prior art in either direction is mcp-remote-style wrappers exposing an HTTP server as local stdio — the **reverse** direction. A bespoke banner would be protocol invention with exactly two adopters.

## B.6 Part B verdict — one recommendation

**Recommendation: skip the handshake now; ship `url`-config + idempotent serve. Do not build the banner in any variant.**

Reasoning chain:

1. No standard, no second client demanding it → a two-adopter bespoke protocol is a liability, not an asset.
2. The config-`url` + idempotent-serve pair covers today's need end-to-end with zero protocol invention and a smaller surface than the flag itself.
3. The sole genuine win (random-port discovery) is neutralized by Part A's attach semantics for the fixed default port.
4. The client-side cost is a fork of SDK transport code — the most maintenance-prone change in this whole feature, for a marginal benefit.
5. Revisit triggers, explicitly recorded: (a) the MCP spec grows a transport-upgrade mechanism (the transports section is actively evolving — watch it; that is the correct home for this idea); (b) a concrete second client that refuses `url:` configs. The zero-arg auto-banner variant is rejected unconditionally — it breaks every non-peeking client and cannot be made safe.

---

## Interaction between the parts

Attach and the banner are **alternative answers to the same problem**: attach makes the fixed URL honest (and the second spawn harmless); the banner would make the fixed URL unnecessary. They are not complements. Pick the cheap one — attach — and keep the banner design on file for the day a standard or a real client appears.

## Still open

1. Who starts the server for a Claude Code `{"type":"http","url":...}` entry, if the owner later wants that default? (Bootstrap + 4 h-watchdog interplay; the attach feature does not answer it.)
2. Tier-2 identity probe (`initialize` → `serverInfo`): build only if a foreign-MCP-server squatting incident actually occurs.
3. Attached mode and `--data-root`: currently documented as "fixed port is the contract" — is that acceptable to the owner, or should attach verify bank identity?
4. `peek_timeout` default of 15 s is UNVERIFIED — needs a measured ai-raccoon cold start (bank open + embedding availability) before any future banner work starts.
5. `--mcp-entry` in attached mode prints the same JSON as started mode — confirm that is the desired script contract (it is true either way).

## Grade mix

Findings: 3 READ (SDK HTTP routing/status codes), 1 READ (ai-raccoon stateless config), 2 READ (Python SDK stdio client internals, Hermes spawn site), 1 INFERRED (Claude Code failure behavior), 1 READ (spec/survey, prior research). Nothing MEASURED — no code was run; every status code claim is read from the SDK source, not probed live.
