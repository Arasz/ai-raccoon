# Research: the `serve observability` CLI surface and the `dotnet-counters` command it prints

**Date:** 2026-10-19
**Question:** Does the AiRaccoon CLI have test coverage of the `serve observability` tools, and should the `dotnet-counters` command it prints carry a `--counters` flag — if so, with which value?

```chart:matrix
title: coverage of the serve observability surface
surface, unit, integration, process-level
command tree (subcommand + --port option), yes, no, no
argument validators (kind, --port 0), yes, no, no
renderer (three command strings), yes, no, no
runner against a live server, no, yes, no
printed command actually shows app metrics, no, no, no
```

## Findings

### F1 — The printed counters command carries no `--counters` flag, so it watches `System.Runtime` only [MEASURED]

`ai-raccoon serve observability counters` prints `dotnet-counters monitor -p <pid>` and nothing
else. dotnet-counters answers an unspecified `--counters` by defaulting to `System.Runtime`, so
the paste-ready command shows GC, CPU, working set and thread pool, and none of AiRaccoon's own
instruments, not even while the server is serving tool calls. The command's stated purpose in
`docs/reference/cli-reference.md:148` is "a ready-to-run monitoring command for the live server";
measured, it is a runtime-metrics command, not a server-metrics one.

**Evidence:** macOS (darwin), .NET SDK 10.0.402, `dotnet-counters` 10.0.750501. Live server:
`src/AiRaccoon/bin/Debug/net10.0/AiRaccoon --data-root /tmp/air-obs-Myvv serve --port 7799`
(pid 72317). `… AiRaccoon serve observability counters --port 7799` → `dotnet-counters monitor -p 72317`.
`dotnet-counters monitor -p 72317 --duration 00:00:18` printed
`--counters is unspecified. Monitoring System.Runtime counters by default.` and the only provider
header in the table was `[System.Runtime]`, while a concurrent caller made 26 MCP `tools/call`
requests against the same server (`/tmp/mcp-caller.py`, one call every 0.7 s, `calls: 26`).

### F2 — The same server's own instruments are visible to dotnet-counters when the meter is named [MEASURED]

Pointing the same tool at the same server with `--counters AiRaccoon.MemoryTools` shows
`ai_raccoon.tool.invocations` (broken out by `project_id`, `result`, `tool`) and the
`mcp.server.operation.duration` histogram. The instruments are there; the printed command simply
does not ask for them. This is the whole defect: a missing argument, not missing telemetry.

**Evidence:** `dotnet-counters monitor -p 72317 --counters AiRaccoon.MemoryTools --duration 00:00:18`,
with a concurrent MCP caller (`calls: 26`). Output table: `[AiRaccoon.MemoryTools]` →
`ai_raccoon.tool.invocations ({invocation})` with row `none / success / project_id_token_get`, and
`mcp.server.operation.duration (s)` with percentiles 50/95/99.

### F3 — The running server does produce those metrics, confirmed over OTLP, so the gap is the command, not the instrumentation [MEASURED]

With `OTEL_EXPORTER_OTLP_ENDPOINT` pointed at a local capture server, the live server exported a
`/v1/metrics` payload naming the `AiRaccoon.MemoryTools` meter, the `ai_raccoon.tool.invocations`
counter and the `mcp.server.operation.duration` measurement. The metric data exists in-process and
reaches an exporter; only the copy-paste command fails to name it.

**Evidence:** capture server `python3 /tmp/otlp-capture.py` on `127.0.0.1:4318`; second live
server `OTEL_EXPORTER_OTLP_ENDPOINT=http://127.0.0.1:4318 OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf
… AiRaccoon --data-root /tmp/air-obs2-HquI serve --port 7800` (pid 74289); three
`project_id_token_get` calls; `kill -TERM 74289` flushed the export. Captured `/v1/metrics` payload
(10,634 bytes) contains the strings `AiRaccoon.MemoryTools`, `ai_raccoon.tool.invocations`,
`mcp.server.operation.duration`, `AiRaccoon.Background`.

### F4 — dotnet-counters accepts a comma-separated list of meter names, and a provider with no data in the window is not an error [MEASURED]

The five-meter list the project already maintains works as a single `--counters` value: exit code 0,
three provider headers in the table, and every instrument of each present meter listed. Two of the
five (`System.Net.Http`, `AiRaccoon.PromotionQueue`) had no measurements in the window and simply
produced no rows, with no warning and no failure. So a list value is safe even when a member is quiet.

**Evidence:** `dotnet-counters monitor -p 72317 --counters System.Runtime,System.Net.Http,AiRaccoon.MemoryTools,AiRaccoon.PromotionQueue,AiRaccoon.Background --duration 00:00:20`
→ exit 0, provider headers `[AiRaccoon.Background]`, `[AiRaccoon.MemoryTools]`, `[System.Runtime]`;
instruments `ai_raccoon.tool.invocations`, `mcp.server.operation.duration`,
`ai_raccoon.background.passes`, `ai_raccoon.background.pass.duration`. Concurrent caller:
`calls: 29`.

### F5 — A first measurement that showed an empty table was a timing artifact of my own harness, not a server defect [MEASURED]

dotnet-counters reports only the measurements that occur while its session is attached; it does not
replay history. Three calls issued in a burst, finished before the session attached, produced an
empty table against a server that was recording them. Spreading the same calls across the window
(one every 0.7 s) produced the rows in F2. Recorded because the empty table was the first result
this investigation produced and would have been easy to report as "the counters are not exported".

**Evidence:** `dotnet-counters monitor -p 72317 --counters AiRaccoon.MemoryTools --duration 00:00:20`
with 3 then 15 calls issued back-to-back → table empty; the same command with the same server and
calls spread over the window → `[AiRaccoon.MemoryTools]` rows as in F2.

### F6 — The project's own documents already disagree with the tool's output [READ]

`docs/adr/0008-live-pid-discovery-for-monitoring.md` shows the command the verb is supposed to
print with the flag present (`dotnet-counters monitor -p <server-pid> --counters AiRaccoon.MemoryTools`),
while `docs/reference/agent-memory-server.md` documents the bare form and hands the flag to the
user as a manual step ("append `--counters AiRaccoon.MemoryTools` for the tool metrics"). The tool
follows the second document. Whichever value is chosen, one of these two documents is wrong
afterwards and must move with the code.

**Evidence:** `docs/adr/0008-live-pid-discovery-for-monitoring.md:15`;
`docs/reference/agent-memory-server.md:790` (the `counters` row of the kind → stdout table).

### F7 — The renderer's counters arm has no provider list to derive from, while the trace arm derives its list from `OtlpNames` [READ]

`RenderTrace` builds its `--providers` value from `OtlpNames.Sources`; `RenderCounters` has no list
at all. `OtlpNames` states that intent in its own doc comment (the renderer is supposed to derive
its meter/provider lists from it rather than repeat literals), so the counters arm is the one place
the file does not yet honour it. `OtlpNames.Meters` is the matching list for counters (meters vs
activity sources), and it is guard-tested against the real DI container: `OtlpNamesRegistryTests`
force-resolves every registered service and asserts each created `Meter` is in the list, so it
cannot silently miss a meter.

**Evidence:** `src/AiRaccoon/Observability/MonitoringCommandRenderer.cs:6` (`RenderCounters`, no
list) and `:8` (`RenderTrace`, `--providers` from `OtlpNames.Sources`);
`src/AiRaccoon/Observability/OtlpNames.cs:4-8` (the intent) and `:60-62` (`Meters`);
`tests/AiRaccoon.Tests/Unit/Observability/OtlpNamesRegistryTests.cs:28-36`
(`EveryCreatedMeterAndActivitySource_IsInTheRegistry`).

### F8 — The premise "no coverage in the CLI of the observability tools" is false for parsing, dispatch and rendering [READ]

The CLI surface of the verb is covered: the command tree exposes the subcommand and its `--port`
option; the argument validators are covered for the accepted kind, the rejected kind, `--port 0`,
`--port` before or after the kind, and the 7721 default; the renderer has a golden test per
command; the runner is covered against a live server for all four kinds and for the error paths
(nothing listening, foreign listener, old server, OTLP disabled, cancellation).

**Evidence:** `tests/AiRaccoon.Tests/Unit/Setup/Cli/CliCommandTreeTests.cs` →
`ServeCommand_ExposesObservabilitySubcommand`; `tests/AiRaccoon.Tests/Unit/Setup/CliArgsTests.cs:594-651`
(`Parse_ServeObservability_ReadsPortAfterTheKind`, `…ReadsPortBeforeTheKind`, `…DefaultsToPort7721`,
`…UnknownKind_IsAParseError`, `…PortZero_IsAParseError`);
`tests/AiRaccoon.Tests/Unit/Observability/MonitoringCommandRendererTests.cs`;
`tests/AiRaccoon.Tests/Integration/Observability/ObservabilityRunnerTests.cs`.

### F9 — What is missing is coverage of the contract, and the existing golden test pins the wrong contract [READ]

The renderer test asserts the bare command, by name and by value
(`RenderCounters_IsTheBareMonitorCommand` → `dotnet-counters monitor -p 12345`), and the
integration test asserts the same string on stdout. A string-equality golden cannot tell a
"ready-to-run monitoring command" from one that watches the wrong metrics. F1 is exactly the
failure it cannot see, because the string is stable and the behaviour behind it is not. Two
smaller gaps in the same area: the kind validator's *message* is never asserted (only "there is an
error"), and the `--port` range validator is only asserted for 0.

**Evidence:** `tests/AiRaccoon.Tests/Unit/Observability/MonitoringCommandRendererTests.cs:13-15`;
`tests/AiRaccoon.Tests/Integration/Observability/ObservabilityRunnerTests.cs:36-50` →
`LiveServer_PrintsTheCountersCommand_WithTheServerPid` (asserts
`dotnet-counters monitor -p {Environment.ProcessId}`).

### F10 — The verb is never exercised as a process, only in-process [INFERRED]

The runner tests call `ObservabilityRunner.RunAsync` directly and the server harness is
documented as an in-process `serve` run, so the process-level path (argv → `CliArgs` →
`Program` routing → `ServeCommands.Observability` → stdout) has no automated coverage; the
project compensates with a manual checklist skill. This is inferred from the harness
documentation and the test inventory, not measured.

**Evidence:** `tests/AiRaccoon.Tests/TestHelpers/ServeHarness.cs:7-9` ("One in-process `serve`
run"); `tests/AiRaccoon.Tests/Integration/Observability/ObservabilityRunnerTests.cs` →
`RunObservabilityAsync` calls `TestData.CreateObservabilityRunner().RunAsync(...)` in-process.

### F11 — The "correct" `--counters` value is a decision, and two defensible values exist [INFERRED]

Reasoning from F4, F6 and F7: the narrow value `AiRaccoon.MemoryTools` is what ADR-0008 documents
and what the reference doc tells users to append, and it is the one-meter answer to "show me the
tool metrics". The complete value is `OtlpNames.Meters` (all five, in registry order) — the
symmetric counterpart of the trace arm's `OtlpNames.Sources`, complete by construction against the
DI guard, and it keeps the runtime metrics the bare command already gave the user instead of
dropping them. Both are measured to work as a `--counters` value. The choice between them is a
product call, not a fact this investigation can settle; the "must change" part is that the flag
appears at all, and that its value comes from `OtlpNames` rather than a literal.

**Evidence:** F4 (the list works), F6 (the two documents), F7 (`OtlpNames.Meters` and its guard).

## Still open

- Which value the product wants: the narrow `AiRaccoon.MemoryTools` (ADR-0008's shape) or the
  complete `OtlpNames.Meters`. Both are measured to work; the choice changes what
  `docs/adr/0008` and `docs/reference/agent-memory-server.md` must then say.
- Whether the `counters` kind should keep the runtime meters at all once the flag is present —
  dotnet-counters' default is `System.Runtime`, so the bare command's current value is not
  accidental, and dropping it is a deliberate narrowing of what the paste shows.
- Windows behaviour: every measurement here is macOS. `dotnet-counters` is a cross-platform
  global tool, but nothing in this record speaks for Windows.
- Whether the dotnet-counters versions a user is likely to have (8.x/9.x) accept a custom meter
  name in `--counters`; only 10.0.750501 was exercised.
- The process-level path (argv → routing → stdout) was not exercised in this investigation at all;
  it was driven by hand from the built binary, and no automated test covers it (F10).
- Whether a test can assert "the printed command shows the app's metrics" without pinning
  dotnet-counters' output format — the honest options are a golden string plus a guard that the
  string names every meter in `OtlpNames.Meters`, or a real dotnet-counters run (slow, environment
  dependent) — the design of that check is not settled.
