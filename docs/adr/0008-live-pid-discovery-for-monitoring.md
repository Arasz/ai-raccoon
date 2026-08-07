# 0008 — Live PID discovery for monitoring

Date: 2026-08-07

Status: Accepted

## Context

`serve` mode tells users to watch a live server with `dotnet-counters` and
`dotnet-trace` (README.md, "Observability"), but both commands need the
server's OS process id, and the README has always made the user find and
substitute that id by hand:

```bash
dotnet-counters monitor -p <server-pid> --counters AiRaccoon.MemoryTools
dotnet-trace collect -p <server-pid> --providers AiRaccoon.MemoryTools
```

Task `add-pid-to-serve` adds `ai-raccoon serve observability
<counters|trace|otlp|pid>`, a CLI verb that prints a ready-to-run monitoring
command with the placeholder filled in. That verb needs a way to learn the PID
of the server it is talking about — and `serve` already has a live process
to ask, not a PID to remember: `serve` doesn't write one anywhere today.

`serve` does already solve a related problem without any persisted state.
`TryProbeAttachAsync` (`src/AiRaccoon/Setup/Serve/ServeRunner.cs`) decides
whether an ai-raccoon server is already listening on the target port by
dialing that port and reading the response, so a second `serve` invocation
can attach instead of racing to bind. This ADR extends that same
probe-the-port shape — dial the server, ask it to identify itself — rather
than introducing a new discovery mechanism.

## Decision

The running server exposes `GET /observability` on its existing 127.0.0.1
HTTP port, returning its own PID. The CLI dials it to fill in the
`<server-pid>` placeholder.

Response contract:

```json
{"name":"ai-raccoon","pid":12345,
 "otlp":{"enabled":true,"endpoint":"http://127.0.0.1:4317","protocol":"grpc"}}
```

`name` lets the client tell an ai-raccoon server from an unrelated process
that happens to answer on that path; the client trusts `pid` only when
`name == "ai-raccoon"`.

- **Correct under attach.** When a second `serve` attaches to an existing
  one (the `TryProbeAttachAsync` path above), `GET /observability` answers
  from the *owner* process, because the owner is the process that answers
  HTTP requests on the port. A PID file would need attach-aware write
  suppression to get this right — the attaching process must know not to
  overwrite the file with its own PID — and that suppression logic is exactly
  the kind of extra state this decision avoids.
- **Cannot go stale.** If the endpoint answers, the process behind it is
  alive by construction. There is no file to leave behind on a crash, and
  no cleanup step to forget.
- **Scoped to HTTP/serve mode.** The endpoint is mapped inside the existing
  HTTP-transport guard in `McpServerSetup.cs` (the same `if
  (transports.Contains(McpTransport.Http))` block that maps `/mcp`), so it
  exists only when `serve` is actually listening on HTTP, never in stdio
  mode.
- **Never resets the idle watchdog.** `McpActivityMiddleware` only signals
  activity for the literal path `/mcp`
  (`src/AiRaccoon/Setup/Serve/McpActivityMiddleware.cs`); `/observability`
  is a different path, so a monitoring poll against it cannot keep an
  otherwise-idle server alive forever. This is load-bearing behaviour and
  needs a dedicated regression test — a poll loop against `/observability`
  must not extend `serve --idle-timeout`.
- **No `meters[]`/`activitySources[]` list in the response.** The CLI is the
  same binary as the server, so it already knows those names
  (`AiRaccoon.MemoryTools`, `AiRaccoon.PromotionQueue`) as compile-time
  constants; the endpoint only needs to answer what the CLI cannot already
  know — the live PID and whether OTLP export is currently enabled.

## Consequences

- **Positive.** The `<server-pid>` placeholder in the README becomes
  something the CLI fills in automatically instead of something the user
  hunts for with `ps`/Task Manager; the `observability` verb works
  identically whether the querying process is the server's own owner or a
  second `serve` that attached to it.
- **Positive.** No new file-lifecycle concern: nothing to clean up on crash,
  no directory to create, no attach-aware suppression logic to get right.
- **Negative.** An unauthenticated local endpoint now discloses the server's
  OS process id to anything able to reach 127.0.0.1 on that port. The `/mcp`
  endpoint on the same port is already unauthenticated, and a PID is not a
  secret on its own — but this is still one more piece of information a
  local process didn't previously have to expose, and that new surface is
  worth naming rather than waving away.
- **Neutral.** Discovery only reaches processes on the same port `serve` is
  already bound to; it cannot enumerate other `ai-raccoon serve` processes
  running on other ports. A PID file (rejected below) would have made that
  possible, at the cost this ADR chose not to pay.

## Non-Goals (explicit)

- **No PID persistence anywhere** (file, registry key, or otherwise). The
  live endpoint is the only source of truth for "is a server running, and
  what is its PID."
- **No enumeration of all running `ai-raccoon serve` processes.** Discovery
  is scoped to the one port the caller already knows to ask about.
- **No authentication on `/observability`.** It carries the same trust
  boundary as `/mcp` on the same port: loopback-only, unauthenticated.

## Future evolution

If AiRaccoon ever needs to discover servers across ports it doesn't already
know about (e.g. "list every ai-raccoon server running on this machine"),
that is a different problem — enumeration, not liveness confirmation for a
known port — and belongs in its own ADR, informed by whichever of the
rejected alternatives below turns out to matter once that need is real.

## Alternatives considered

### A PID file (`~/.ai-raccoon/serve-<port>.pid`)

Rejected. A PID file goes stale the moment the process it names crashes
without cleaning up after itself, so every reader has to separately verify
the PID it names is still alive — which needs most of the same liveness
check this ADR already does over HTTP. Getting attach right would also
require write suppression: the second `serve` that attaches to an existing
one must detect that and *not* overwrite the file with its own PID, which is
exactly the kind of extra coordination state a live, always-current endpoint
avoids by construction. The one real advantage a PID file would have bought:
it could be enumerated on disk to list every `ai-raccoon serve` process on
the machine, across ports, without having to already know which ports to
probe. That capability is not needed today (see Future evolution above).

### Shell out to `lsof` / `netstat` / `Get-NetTCPConnection`

Rejected. Resolving "what process owns port 7721" from the outside needs a
different tool and a different parsing strategy per platform (`lsof` on
macOS/Linux, `netstat` or `Get-NetTCPConnection` on Windows), and depends on
tools that may not be installed or may need elevated permissions in a locked
down environment. Asking the process itself over the port it already has
open needs none of that.

### `DiagnosticsClient.GetPublishedProcesses` (diagnostics client library)

Rejected. This enumerates .NET processes system-wide via the diagnostics IPC
channel, which is a new NuGet dependency to discover something AiRaccoon
already knows how to reach directly: the one process listening on the port
the caller is asking about. Pulling in a diagnostics-tooling library to solve
a problem an HTTP round-trip already solves is a cost with no buyer.
