# Transport gating: long-interval BackgroundServices in short-lived stdio hosts

Verified 2026-08-06 on the live AiRaccoon deployment (hermes gateway as MCP client).

## The environment fact: stdio MCP connections are recycled every ~5 minutes

The hermes gateway spawns stdio MCP servers per connection and recycles them on a strict
cadence: `~/.hermes/logs/agent.log` shows `tools.mcp_tool: MCP server 'X' (stdio): registered
N tool(s)` lines 5min03s apart all day (1048 ai-raccoon registrations in one day). Each
instance's lifecycle in `~/.hermes/logs/mcp-stderr.log`:

```
===== [2026-08-06 15:35:37] starting MCP server 'ai-raccoon' =====
Server (stream) (AiRaccoon) transport reading messages.
Application started.
... initialize + tools/list (the registration) ...
Server (stream) (AiRaccoon) transport completed reading messages.   <- stdin EOF
Application is shutting down...                                     <- clean stop
```

No crash trace, no exceptions: the client closed the pipe. Long-lived processes DO exist
(multi-hour session servers, the HTTP bridge on 5094) — they are the exception, not the rule.

## How to verify the recycle (when in doubt)

1. `grep "tools.mcp_tool" ~/.hermes/logs/agent.log | grep "ai-raccoon.*registered" | awk '{print $2}' | tail -25` — regular ~5-min spacing = recycle loop.
2. In mcp-stderr.log, find the segment between the server's banner and the next banner; look
   for "transport completed reading messages" + "Application is shutting down..." = clean
   client-initiated exit; an unhandled exception trace = crash (different problem).
3. `pgrep -fl ai-raccoon` + `ps -o lstart= -p <pid>` vs the dotnet-tool store mtime
   (`ls -la ~/.dotnet/tools/.store/<pkg>/`) tells you which running processes exec which build.

## Design consequence

A `BackgroundService` whose first `PeriodicTimer` tick is one full interval (30-60 min) never
fires in a recycled stdio process — the process dies at the next recycle (~5 min) first. In the
AiRaccoon case the extraction hosted service was configured (extract.enabled=true, promote) and
installed, yet zero passes ever ran: the recycled instances could not reach the 60-min first
tick, and no long-lived process ran the build containing the service.

Fix shape (shipped as PR #58): gate registration by host transport —

- `RegisterMemoryServices(options, bool registerExtractionHostedService = true)`; the
  stdio-only app host passes `false`; the HTTP host (including both-transports) keeps the
  default. Host dispatch in `McpServerSetup.CreateServerHost`: stdio-only → app host;
  anything with HTTP/S → web host.
- Host-shape tests pin the gate: build each host via the setup entry point and assert
  `host.Services.GetServices<IHostedService>()` ShouldNotContain / ShouldContain
  (`s => s is ExtractionHostedService`) — three tests: stdio-only absent, http present,
  both-transports present. (Add `using Microsoft.Extensions.Hosting;` for IHostedService.)
- CLI verbs never create a host: Program.cs routes a non-empty command path to
  `CliCommandRunner` BEFORE any host is built. So `ai-raccoon extract enable true` while an
  HTTP server runs is a separate short-lived process that (a) never binds the port, (b) writes
  the shared settings table, (c) the running server picks up live (settings re-read per pass /
  per loop iteration). CLI interactions do NOT kill the server.

## Evidence-vs-claim discipline

"Service is live" claims need: (a) a running process that execs the build containing the
service (process start time vs binary swap time), AND (b) the service's own log lines (a
pass that always logs at Information leaves lines; zero lines + due first tick = never ran).
Absence of either = "configured but not executing", not "running".
