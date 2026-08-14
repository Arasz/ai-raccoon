# E2E test review — lifetimes, waiting, shared components

Date: 2026-08-14. Reviewer: test-engineer (read-only). Subject: `tests/AiRaccoon.Tests/E2E/`.
Reference pattern: `E2E/McpTokenGateE2ETests.cs` (owner's fix to HTTP client scope + polling).

Measured, not reasoned: the `WaitByPooling` timing (300-run `FakeTimeProvider` harness) and the
`FreePort` rebind race. Unverified and marked as such below: whether xunit.v3 calls `DisposeAsync`
when `InitializeAsync` throws, and which processor `AddInMemoryExporter` installs by default.

## Two premises that changed on contact with the code

**`McpServerFactory` clients are in-memory.** `CreateClient()` (`McpServerFactory.cs:46`) returns a
`WebApplicationFactory` `TestServer` handler — no sockets, no DNS. Socket exhaustion and DNS
staleness are **not** risks for the seven files using it; the concern there is ownership and
cancellation. Real sockets appear only at `ServeRestartE2ETests.cs:26`,
`ProxySpawnedBackendE2ETests.cs:85`, `ProxyTokenRefusedE2ETests.cs:103`,
`McpTokenGateE2ETests.cs:74/130`, `TestData.cs:179`.

**All ten E2E classes already share one collection**, so they run serially against each other
regardless of the `DisableParallelization` flag. See section C — the flag and the split are two
different decisions that the current comment conflates.

## Measured: `WaitByPooling` timing

Harness: `FakeTimeProvider`, 300 runs, predicate always false; plus one run with predicate true.

```
CASE1 success  completed=True result=True firstEvalAt=10s
CASE2 evals=4 at=[10s, 36s, 74s, 118s] endedAt=120s outcome=THREW OperationCanceledException
300-run distribution: 3 evals x41, 4 evals x259
outcomes: THREW OperationCanceledException x300   (returned false x0)
```

1. **Minimum time for a successful wait is exactly 10 seconds.** The predicate is never evaluated
   before the first tick, and `FirstTick = 10s` (`WaitByPooling.cs:6`). A server that boots in
   300 ms costs 10 s.
2. **The predicate runs 3 or 4 times, never 5.** Ticks land at `10, 30+d1, 60+d1+d2, 100+d1+d2+d3`
   with each `di` in `[0,9]` (`:33`, `:43`). A 5th tick would land at >=150 s, past the 120 s
   deadline (`:7`). So the `iteration >= iterationLimit` guard at `:28` is **unreachable dead
   code** — 0/300 runs reached it.
3. **It never returns `false` on timeout — it throws.**
   `PeriodicTimer.WaitForNextTickAsync(token)` throws `OperationCanceledException` on cancellation
   and returns `false` only on timer disposal. So `return false` at `:41` is also unreachable.
   **Consequence in the reference file:** `McpTokenGateE2ETests.cs:166-169`, the
   `if (!success) throw new TimeoutException($"serve never reported a URL; stderr: {_stderr}")`,
   is dead. A boot failure surfaces as a bare `OperationCanceledException` and the stderr
   diagnostic is lost. Highest-value single fix in this report.

Wrong shape for a sub-second boot on three counts: a 10 s floor per wait; failure detectable at
only 4 sample points across 2 minutes; and `Drift()` adding **0-9 whole seconds** of absolute
jitter (`:43`) — thundering-herd protection sized for a distributed system, applied to loopback
probes in a serial collection.

### Recommended shape

```csharp
public static async ValueTask<bool> WaitForAsync<T>(
    T state, Func<T, ValueTask<bool>> predicate,
    TimeSpan firstTick, TimeSpan maxTick, TimeSpan waitDeadline,
    TimeProvider timeProvider, CancellationToken cancellationToken)
{
    using var deadlineCts = new CancellationTokenSource(waitDeadline, timeProvider);
    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadlineCts.Token);
    try
    {
        if (await predicate(state)) return true;          // fast path: zero latency when already true
        var period = firstTick;
        using var timer = new PeriodicTimer(period, timeProvider);
        while (await timer.WaitForNextTickAsync(linked.Token))
        {
            if (await predicate(state)) return true;      // evaluate BEFORE any give-up check
            period = Min(period * 1.6 + Jitter(period), maxTick);
            timer.Period = period;
        }
        return false;
    }
    catch (OperationCanceledException) when (deadlineCts.IsCancellationRequested)
    {
        return false;                                    // deadline = false, not an exception
    }
}
static TimeSpan Jitter(TimeSpan p) => p * (Random.Shared.NextDouble() * 0.2);
```

- `FirstTick`: **10 s -> 25 ms**.
- Add `MaxTick = 500 ms`; replace the linear `firstTick * (iteration+1)` ramp with a capped ~1.6x
  geometric one. Sequence 25, 40, 64, 103, 165, 264, 422, 500, 500... — ~40 evaluations in the
  first 10 s versus today's 1.
- `Drift()`: **0-9 s absolute -> +/-20% of the current period**.
- **Delete `IterationLimit`.** Unreachable today, and it duplicates `WaitDeadline` as a stop
  condition. Two controls that can disagree is one too many.
- `WaitDeadline`: keep 120 s default, let callers pass shorter ones —
  `ProxyTokenRefusedE2ETests` asserts `< 20 s` (`:22`) and should wait against a matching deadline.
- Rename `WaitByPooling` -> `WaitByPolling` (a typo, not a term of art).

**Prove the check fails first:** before changing the file, add a `FakeTimeProvider` test asserting
`WaitForAsync` **returns false** on deadline. It goes red today with `OperationCanceledException`
— confirmed 300/300.

## Per-file findings

### McpServerE2ETests.cs — 10 facts, `IAsyncLifetime` on the class (`:26`) => 10 server boots

| issue | file:line | sev | fix |
|---|---|---|---|
| Server booted per test | `:32-38` | HIGH | `IClassFixture`. Tests assert absolute counts (`"entries":1` `:58`, `"entries":0` `:80`) so give each a unique `projectId` as ToolSurface already does (`:30`); one boot then serves all 10. |
| `FakeEmbeddingEndpoint` booted for all 10, used by one | `:35` used only `:207` | MEDIUM | move into the one test or the fixture |
| `DisposeAsync` no try/finally: `_factory` temp dir and `_openAi` port both leak if `_client.DisposeAsync()` throws | `:40-45` | HIGH | wrap each disposal |
| `CancellationToken.None` on tool calls — a wedged server hangs the run | `:163`, `:242`, `:260` | MEDIUM | `TestContext.Current.CancellationToken` (sibling already does, ToolSurface `:199`) |
| Non-thread-safe `StringWriter` | `:251-252` | MEDIUM | shared `LockingWriter`. Whether `ConfigCommands` writes from a background thread is UNVERIFIED; the shared writer removes the question at zero cost. |
| Store-construction duplicated | `:253-258` | LOW | `TestData.CreateStoreFor` |

### McpServerLaunchArgsE2ETests.cs

| issue | file:line | sev | fix |
|---|---|---|---|
| `ExplicitStdio_...` pays a full factory boot + MCP handshake and **uses neither** — it spawns its own process | `:22-27` vs `:51-77` | HIGH | split the class, or make the factory a lazy fixture |
| `tools.Count.ShouldBe(25)` is a hand-maintained mirror of `ExpectedToolNames` (25 entries, ToolSurface `:32-59`). Add a tool and one file goes red while the other silently drifts | `:66` | HIGH | assert against `ExpectedToolNames.Length` — "derive the list, or delete it" |
| `TestData.CreateServerProbe()` -> `LoopbackHttpClientFactory` returns `new HttpClient()` **per Polly attempt**, up to 3, none disposed | `:70` -> `TestData.cs:177-180` | MEDIUM | `ServerProbe` has a single-client ctor (`ServerProbe.cs:26`); reuse one client. Same leak at `McpTokenGateE2ETests.cs:39`. |
| Server booted per test | `:17`, `:22-27` | MEDIUM | `IClassFixture` |
| `DisposeAsync` no try/finally | `:29-33` | MEDIUM | wrap |
| `Directory.Delete` unguarded in `finally` — masks the real assertion result | `:75` | MEDIUM | best-effort `TempRoot` |
| `FreePort()` TOCTOU | `:55` | MEDIUM | `PortLease` |

### McpServerToolSurfaceE2ETests.cs

| issue | file:line | sev | fix |
|---|---|---|---|
| `FakeEmbeddingEndpoint` started and disposed but **never used by either test** — a Kestrel host booted twice for nothing | declared `:63`, started `:68`, disposed `:78`, zero other refs | HIGH | delete the field |
| Server booted per test | `:28`, `:65-71` | MEDIUM | `IClassFixture` — both tests share `ProjectId` (`:30`) |
| `DisposeAsync` no try/finally | `:73-78` | MEDIUM | wrap |
| Name says "TwentyFour", array has 25, assertion checks 25 | `:81` vs `:32-59` | LOW | drop the count from the name so it cannot drift again |
| One 93-line test covering 13 tools — first failure hides the other 12 | `:90-183` | MEDIUM | split into separate facts once the fixture is class-level |
| `File.Delete` unguarded in `finally` (contrast `:172-179`, which is guarded) | `:114` | LOW | guard consistently |
| Correct already: `TestContext.Current.CancellationToken` | `:199` | — | make this the pattern others copy |

### OtlpMetricExportE2ETests.cs

| issue | file:line | sev | fix |
|---|---|---|---|
| **`EnvVarGate.WaitAsync()` with no CancellationToken.** If a holder ever fails to release, this blocks forever — no timeout, no cancellation. 14 call sites share this semaphore, so it can wedge the assembly | `:69` | CRITICAL | pass `TestContext.Current.CancellationToken`, as `McpTokenGateE2ETests.cs:138` and `ServeRestartE2ETests.cs:189` do |
| Gate release is the **last** statement of `DisposeAsync`, after two disposals that can throw. `_factory.DisposeAsync()` (`:41`) throwing => gate permanently held | `:39-44`, release `:83` | CRITICAL | `try { ... } finally { _env.Dispose(); }`, release itself in a `finally` |
| Gate acquired `:32` before construction; if construction throws, release depends on xunit calling `DisposeAsync` after a failed `InitializeAsync` — **UNVERIFIED that it does** | `:29-37` | HIGH | acquire-and-scope in one statement so release is structural |
| Hand-rolled busy-wait: `DateTime.UtcNow` deadline + `Task.Delay(50)` **with no CancellationToken**, returns silently on timeout | `:111-123` | HIGH | fixed `WaitByPooling`; fail with a message naming the path |
| `_acceptLoop` never awaited; `_cts.Cancel()` then `.Dispose()` while the loop may be inside `WaitAsync(_cts.Token)` => possible `ObjectDisposedException` on an unobserved task | `:97`, `:101`, `:145-151` | MEDIUM | `IAsyncDisposable`, await the loop before disposing the CTS |
| `CapturingCollector` duplicated with the trace twin (this copy is a strict superset) | `:88-161` | MEDIUM | extract one shared type |
| Private `FreePort` — 4th copy | `:153-160` | LOW | `PortLease` |

### OtlpTraceExportE2ETests.cs

All of the above applies (`:177` no-CT gate, `:42-47` dispose ordering, `:223-235` busy-wait,
`:255-262` `FreePort`, `:196-263` duplicated collector). Additionally:

| issue | file:line | sev | fix |
|---|---|---|---|
| `OTEL_TRACES_SAMPLER` restored to **null**, not to its captured original — the file snapshots the other two vars (`:178-179`) then clobbers this one | set `:142`, restored `:171` | HIGH | snapshot and restore it like the others |
| `_factory` built in `InitializeAsync` (`:39`) is **unused by 3 of 4 tests** — they build their own. 4 tests => 7 factory boots | `:32-40` vs `:78`,`:109`,`:146` | HIGH | make it lazy, or move the one test that needs it (`:50`) into its own class |
| `exportedItems` is a plain `List<Activity>` written by the exporter and read on the test thread; `ForceFlush()` is the only barrier. Whether `AddInMemoryExporter` batches on a background thread is **UNVERIFIED** | `:77-93`, `:108-121`, `:145-162` | MEDIUM | concurrent collection, or assert on a post-flush snapshot |
| Holds the env gate across 4 tests' work, serialising 14 other call sites | `:32-40` | MEDIUM | only `:50` and `:140` need the gate; `:75` and `:106` use an in-memory exporter and could run gate-free |

### ProxyLaunchE2ETests.cs

| issue | file:line | sev | fix |
|---|---|---|---|
| `using var process` — **`Process.Dispose()` does not kill**. If `WaitForExitAsync(...).WaitAsync(60s)` times out, the child `ai-raccoon` is orphaned, and it may have spawned a `serve` grandchild | `:137-144` | CRITICAL | `finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }` |
| Backend host booted per test (3 tests, 3 hosts, 6 temp roots) | `:23`, `:30-40` | HIGH | `IClassFixture` |
| `WhenTheBackendCannotStart_ItFailsLoudly` uses none of the fixture state — a full boot + two temp roots wasted | `:98-121` | HIGH | move to a fixture-free class, or make the fixture lazy |
| `DisposeAsync` no try/finally around `StopAsync` => both temp roots leak | `:43-52` | MEDIUM | wrap |
| Two `FreePort()` TOCTOU sites | `:32`, `:101` | MEDIUM | `PortLease` |
| `Delete(root)` helper — 5th copy | `:158-168` | LOW | `TempRoot` |
| `ConnectDirectlyAsync` re-implements the transport block (3rd copy) | `:146-156` | MEDIUM | `McpOverHttp.ConnectAsync` |
| Correct already: transport at `:147` owns its HttpClient, released by `await using var direct` (`:79`) | `:79`, `:147` | — | no change |

### ProxySpawnedBackendE2ETests.cs

| issue | file:line | sev | fix |
|---|---|---|---|
| **Teardown can leave a real `serve` daemon running.** It kills by PID read from `/observability`; if that request fails (still booting, port stolen by the TOCTOU race, 2 s timeout too tight for a cold spawn) the `catch` swallows it and the daemon survives the whole run holding `_dataRoot` | `:83-100`, catch `:95-99` | CRITICAL | retry the PID lookup via `WaitByPooling`; on failure fail teardown loudly. A stray daemon poisons every later test that picks a port. |
| No wait for the killed process to exit before `Directory.Delete(_dataRoot, true)` | `:39` then `:42` | HIGH | `WaitForExitAsync`, then delete |
| `FreePort()` TOCTOU — most exposed case, the port goes to a *spawned* process later | `:29` | HIGH | `PortLease` held until just before spawn |
| `Directory.Delete` failure swallowed, so the leak is silent | `:41-47` | MEDIUM | throw once the kill is reliable |
| Backend spawn per test; slowest path in the suite | `:20`, `:27-31` | MEDIUM | class fixture — both tests only read |
| No readiness wait before the first tool call; relies on the SDK's internal timeout | `:71-76` | MEDIUM | explicit wait so failure reads as "backend never came up" |

### ProxyTokenRefusedE2ETests.cs

| issue | file:line | sev | fix |
|---|---|---|---|
| Same orphan defect: `using var process` + `WaitAsync(150s)`, no kill on timeout | `:145-152` | CRITICAL | kill the tree in `finally` |
| `_backend` `Process` killed but **never disposed**, and no `WaitForExit` before `Delete(_backendRoot)` (`:51`) — the delete races the dying process. `Kill` can throw `Win32Exception`, which the filter at `:46` does not cover | `:26`, `:42-54` | HIGH | `Kill(true)` -> `WaitForExit` -> `Dispose()` -> delete |
| Hand-rolled readiness: 120 x 250 ms plus up to 120 x 1 s HTTP timeouts (worst case ~2.5 min), `new HttpClient` per call | `:100-124`, client `:103` | HIGH | shared readiness helper. **Tension:** today's `WaitByPooling` would make this *worse* (10 s floor vs ~300 ms). Fix `WaitByPooling` first. |
| `FreePort()` TOCTOU on a port handed to a spawned process | `:31` | HIGH | `PortLease` |
| `ProcessStartInfo` duplicated twice here, twice elsewhere | `:81-96`, `:129-143` | MEDIUM | `RaccoonProcess` |
| Correct already: `Promptly = 20 s` (`:22`) is the assertion, not a wait; the 150 s cap (`:149`) is a documented hang guard | `:22`, `:126`, `:149` | — | keep |

### ProxyWireE2ETests.cs

| issue | file:line | sev | fix |
|---|---|---|---|
| Kestrel backend booted per test (3 tests, 3 boots, 3 spawned proxies) | `:25`, `:33-62` | HIGH | class fixture; only blocker is per-test recording state, and `ForgetPostStatuses()` (`:153`) already proves reset is the intended mechanism |
| `_headerNames` has no reset, so a class fixture would leak handshake headers between tests | `:28` | MEDIUM | add `ForgetHeaders()` |
| `DisposeAsync` no try/finally => temp root leaks | `:64-76` | MEDIUM | wrap |
| `FreePort()` TOCTOU | `:35` | MEDIUM | `PortLease` |
| **Correct already, and the best example in the suite:** middleware writes `_headerNames`/`_postStatuses` from Kestrel threads under explicit `lock`, readers snapshot under the same lock | `:41-56`, `:137-151` | — | the pattern the `List<Activity>` above should adopt |

### ServeRestartE2ETests.cs

| issue | file:line | sev | fix |
|---|---|---|---|
| **`LockingWriter` is a verbatim 39-line duplicate** of `McpTokenGateE2ETests.cs:183-221` | `:206-244` | HIGH | promote one to `TestHelpers/`, delete both privates |
| `CancellationTokenSource(180 s)` in `StartRestartInProcess` **never disposed**. Test 1 cancels but never disposes; test 2 neither cancels nor disposes => after it returns, a 180 s timer and a live `serve` task keep running in the test host | `:132`, `ServeRun` `:195` | HIGH | make `ServeRun` `IAsyncDisposable`: cancel, await `Exit`, dispose the CTS |
| Two hand-rolled busy-waits (`DateTime.UtcNow` + `Task.Delay`), 90 s and 120 s deadlines | `:137-163`, `:165-185` | HIGH | shared readiness on the fixed `WaitByPooling`; preserve the early-exit on `run.Exit.IsCompleted` (`:176-179`) as a predicate-side abort |
| `_ = process.StandardOutput.ReadToEndAsync()` fire-and-forget, output discarded — a backend boot failure is invisible in diagnostics | `:121-122` | MEDIUM | capture into a `LockingWriter`, include in failure messages |
| `Directory.Delete` inside the same `try` as the kill, so a kill failure skips the delete | `:34-53` | MEDIUM | separate `try` blocks |
| `AcquireCleanEnvAsync` + `EnvRestore` — 3rd copy | `:187-204` | HIGH | `EnvScope` |
| Two `FreePort()` sites, both handed to spawned processes | `:59`, `:89` | HIGH | `PortLease` |
| **Correct already:** gate acquired with a CancellationToken (`:189`) and per-test, not per-class | `:58`, `:88`, `:189` | — | the right shape; the OTLP files should copy it |

### McpServerFactory.cs

| issue | file:line | sev | fix |
|---|---|---|---|
| `CreateClientAsync()` takes no CancellationToken — a hung handshake ignores test cancellation | `:40`, `:56` | HIGH | thread a token; every caller already has one |
| **Double ownership of the HttpClient:** `CreateClient()` returns a client the factory tracks and disposes, but `ownsHttpClient: true` also makes the `McpClient` dispose it | `:46`, `:55` | MEDIUM | `ownsHttpClient: false`. Harmless today (`Dispose` is idempotent), a live footgun if a caller reuses the client. |
| `SeedGlobalAccessModeAsync` seeds `Path.GetTempPath()` as a **global** ingest scope for every factory instance — every E2E server can ingest the whole OS temp dir, including other tests' fixtures | `:70-71` | MEDIUM | scope to `DataRoot` plus an explicit per-test dir (needs a small refactor in ToolSurface `:105`/`:117`) |
| Transport block duplicated in two other files | `:47-56` | MEDIUM | `McpOverHttp` |
| Cleanup catches only `IOException`; `UnauthorizedAccessException` escapes | `:103-109` | LOW | `TempRoot` fixes all 7 copies |
| No fixture-shaped entry point — which is *why* seven classes hand-roll per-test lifetimes | whole file | HIGH | add `McpServerFixture : IAsyncLifetime` |

### AiRaccoonProcess.cs

| issue | file:line | sev | fix |
|---|---|---|---|
| **`FreePort()` TOCTOU — measured.** Bound port 0, called `Stop()`, immediately rebound the same number from a different `TcpListener`: succeeded. The number is genuinely unreserved between `Stop()` and the server's bind. 9 call sites, several handing the port to a process that starts seconds later | `:31-38` | HIGH | `PortLease : IDisposable` keeping the listener bound, exposing `Port` + `ReleaseForBind()`. Cannot be eliminated on POSIX; can shrink from seconds to microseconds. |
| Missing `listener.Dispose()` | `:36-37` | LOW | `Stop()` does release the socket in modern .NET (proven by the successful rebind) — style, not a handle leak. Stated because the obvious conclusion is the wrong one. |
| Three private `FreePort` copies elsewhere | see above | MEDIUM | delete, call the shared one |
| `Executable` existence unchecked; a missing build output surfaces as an opaque `Win32Exception` from inside the MCP SDK | `:11-12` | MEDIUM | guard clause naming the expected path |
| No process-run helper, so four files hand-roll `ProcessStartInfo` + stream draining + `WaitForExit` + (missing) tree-kill | `:14-29` | HIGH | `RunAsync` / `StartServeAsync` with tree-kill in `finally` |

## A. Shared components to extract

Ordered by leverage. Together they delete roughly 500 lines and close both CRITICAL classes.

**A1 `TestHelpers/EnvScope.cs` — highest leverage, do first.**
`static ValueTask<EnvScope> AcquireAsync(CancellationToken ct, params (string Name, string? Value)[] set)`.
Takes `EnvVarGate` **with the token**, snapshots every named var, applies overrides, and on
`DisposeAsync` restores all snapshots and releases the gate — release in a `finally` so no restore
failure can strand it.
Replaces E2E: `McpTokenGateE2ETests.cs:138-140/154-155`, `OtlpMetricExportE2ETests.cs:67-85`,
`OtlpTraceExportE2ETests.cs:175-193` plus its unsnapshotted sampler at `:142/:171`,
`ServeRestartE2ETests.cs:187-204`.
Plus 10 unit sites that inherit the fix: `CliCommandRunnerTests.cs:33/58`,
`ConfigCommandsEncryptionTests.cs:86/102` and `:108/127`, `DependenciesEncryptionSmokeTests.cs:50/72`,
`NodeRunnerTests.cs:402/470`, `BackendLauncherTests.cs:261/272`, `ServeRestartTests.cs:237/301`,
`OtlpExportTests.cs:677/715`, `OtlpExportStateTests.cs:95/109`, `OtlpFlushOnExitTests.cs:99/113`,
`ObservabilityEndpointTests.cs:201/215`, `ObservabilityRunnerTests.cs:372/395`.
**14 sites, 2 with no CancellationToken.**

**A2 `TestHelpers/RaccoonProcess.cs`** (extend `AiRaccoonProcess`).
`RunAsync(args, hardCap, ct) -> ProcessRun` with tree-kill in `finally`;
`StartServeAsync(dataRoot, port, ct) -> SpawnedServe` with readiness + tree-kill on dispose.
Replaces `ProxyLaunchE2ETests.cs:123-144`, `ProxyTokenRefusedE2ETests.cs:79-96` and `:127-152`,
`ServeRestartE2ETests.cs:107-124`, `ProxySpawnedBackendE2ETests.cs:83-100`.
Closes both CRITICAL orphan defects and the stray-daemon defect.

**A3 `TestHelpers/LockingWriter.cs`** — promote `McpTokenGateE2ETests.cs:183-221`, delete the
verbatim twin at `ServeRestartE2ETests.cs:206-244`; also targets `McpServerE2ETests.cs:251-252`
and the discarded output at `ServeRestartE2ETests.cs:121-122`.

**A4 `TestHelpers/PortLease.cs`** — `sealed class PortLease : IDisposable { int Port; void ReleaseForBind(); }`.
Deletes three private copies (`McpTokenGateE2ETests.cs:172`, `OtlpMetricExportE2ETests.cs:153`,
`OtlpTraceExportE2ETests.cs:255`). Lease consumers: `ProxySpawnedBackendE2ETests.cs:29`,
`ProxyTokenRefusedE2ETests.cs:31`, `ProxyWireE2ETests.cs:35`, `ServeRestartE2ETests.cs:59/89`,
`ProxyLaunchE2ETests.cs:32/101`, `McpServerLaunchArgsE2ETests.cs:55`.

**A5 `TestHelpers/ServerReadiness.cs`** — `ObservabilityAnswersAsync`, `TokenFileExistsAsync`,
`StdoutUrlAsync`, all on the **fixed** `WaitByPooling`. Replaces
`ProxyTokenRefusedE2ETests.cs:100-124`, `ServeRestartE2ETests.cs:137-163` and `:165-185`,
`McpTokenGateE2ETests.cs:160-170`; gives `ProxySpawnedBackendE2ETests.cs:71` the check it lacks.
**Sequencing: fix `WaitByPooling` first** — migrating onto today's version would regress
`ProxyTokenRefusedE2ETests` from ~300 ms to a 10 s floor.

**A6 `TestHelpers/McpOverHttp.cs`** — `ConnectAsync(endpoint, client, name, ct)`. The transport +
`LoggerFactory.Create(Warning)` + `McpClient.CreateAsync` block, verbatim in `McpServerFactory.cs:47-56`,
`McpTokenGateE2ETests.cs:76-87`, `ProxyLaunchE2ETests.cs:147-156`. Threads a token (missing at
`McpServerFactory.cs:56`) and sets `ownsHttpClient: false`.

**A7 `TestHelpers/TempRoot.cs`** — `IDisposable` over `CreateTempRoot`, best-effort delete catching
`IOException` **and** `UnauthorizedAccessException`. Replaces seven copies plus the two *unguarded*
sites (`McpServerLaunchArgsE2ETests.cs:75`, `McpServerToolSurfaceE2ETests.cs:114`).

**A8 `E2E/McpServerFixture.cs`** — one factory + one client via `IClassFixture<>`. Users:
`McpServerE2ETests`, `McpServerToolSurfaceE2ETests`, `McpServerLaunchArgsE2ETests`,
`OtlpMetricExportE2ETests`, `OtlpTraceExportE2ETests`. Turns ~19 boots into ~5.

**A9 `TestHelpers/CapturingOtlpCollector.cs`** — take the metrics version (strict superset), make it
`IAsyncDisposable` awaiting its accept loop before disposing the CTS.

**A10 `TestData.CreateStoreFor(dataRoot, scope)`** — three copies:
`McpServerFactory.cs:61-65`, `McpServerE2ETests.cs:253-258`, `McpServerToolSurfaceE2ETests.cs:187-191`.

**Rejected: a `ServeFixture` base class.** Two classes want a class-scoped server
(`McpTokenGate`, `ProxyWire`); two genuinely need a fresh one per test (`ServeRestart` restarts the
server under test, `ProxySpawnedBackend` asserts on a cold spawn). A base class forces one lifetime
on both. Composable owned resources (`SpawnedServe`, `PortLease`, `EnvScope`) that either a class
fixture *or* a per-test `IAsyncLifetime` can hold is the simpler shape and covers both.

## C. Is `DisableParallelization = true` still needed?

Every E2E class declares `[Collection(E2ETestCollection.Name)]` — `McpServerE2ETests.cs:25`,
`McpServerLaunchArgsE2ETests.cs:16`, `McpServerToolSurfaceE2ETests.cs:27`,
`McpTokenGateE2ETests.cs:25`, `OtlpMetricExportE2ETests.cs:19`, `OtlpTraceExportE2ETests.cs:20`,
`ProxyLaunchE2ETests.cs:22`, `ProxySpawnedBackendE2ETests.cs:19`,
`ProxyTokenRefusedE2ETests.cs:17`, `ProxyWireE2ETests.cs:23`.

xunit parallelises at *collection* granularity, so the ten classes are **already serialized against
each other** by sharing a collection. The flag buys exactly one thing: it stops the E2E collection
from overlapping the assembly's other collections. Removing it does **not** unlock E2E-vs-E2E
parallelism; splitting the collection does. Two decisions, conflated by the comment at
`E2ETestCollection.cs:5-8`.

| class | needs serialization? | evidence |
|---|---|---|
| `OtlpTraceExportE2ETests` | **Yes** — process-global `OTEL_*` env *and* `Activity` listener leakage | `:33`->`:177`, `:142`; comment `:129-138` documents observed cross-collection contamination |
| `OtlpMetricExportE2ETests` | **Yes** — same env vars | `:32`->`:69`, `:34-35` |
| `ServeRestartE2ETests` | **Yes** — mutates `AIRACCOON_DB_PASSPHRASE`, spawns processes, binds ports | `:58`, `:88`, `:189-191` |
| `McpTokenGateE2ETests` | **Yes** — mutates the passphrase for the whole class | `:138-140`, `:154` |
| `McpServerE2ETests` | No env; in-memory `TestServer` only | `:32-38` |
| `McpServerToolSurfaceE2ETests` | No env; in-memory only | `:65-71` |
| `McpServerLaunchArgsE2ETests` | test 1 no; test 2 spawns + binds | `:36-44` vs `:51-77` |
| Proxy{Launch,Wire,SpawnedBackend,TokenRefused} | No env, but all spawn processes and bind ports | `:32`, `:35`, `:29`, `:31` |

**Recommendation, two steps.** (1) Keep the flag but move only the env/OTel-sensitive classes into
that collection: the two OTLP classes, `ServeRestart`, `McpTokenGate`. The env vars are already
protected across collections by `EnvVarGate`; what the flag *additionally* protects is **OTel global
listener state**, which the gate does not cover and which `OtlpTraceExportE2ETests.cs:129-138`
records as having actually bitten. That is the real justification and it applies to the OTLP pair
specifically. (2) Move the rest into a parallel-enabled collection — **but not before `PortLease`
lands.** Those seven bind 9+ real ports via the measured-racy `FreePort`; running them concurrently
multiplies a race that serialization currently masks.

**Honest expected payoff:** E2E cost is dominated by real process spawns and `serve` boots, not
scheduling. A8 (~19 factory boots -> ~5) and the `WaitByPooling` floor (10 s -> 25 ms across 6 wait
sites) each save more wall clock than the parallelism split. Do A1-A5 and A8 first; treat C as a
follow-up, not a headline.

## Implementation order

1. **`WaitByPooling` fix** (red test first: deadline must return `false`) — unblocks A5, removes the
   dead `TimeoutException` at `McpTokenGateE2ETests.cs:166-169`.
2. **A1 `EnvScope`** — closes both CRITICAL gate-deadlock paths and the sampler-restore bug, 14 sites.
3. **A2 `RaccoonProcess`** — closes both CRITICAL orphan paths and the stray-daemon path.
4. **A3, A7, A4, A6, A9, A10** — mechanical de-duplication.
5. **A8 `McpServerFixture`** + per-class fixtures; delete the three dead-weight boots
   (`McpServerToolSurfaceE2ETests.cs:63/68/78`, `McpServerLaunchArgsE2ETests.cs:22-27` for test 2,
   `ProxyLaunchE2ETests.cs:98-121`).
6. **Collection split** — last, and only after A4.

Most urgent single defects: `ProxyLaunchE2ETests.cs:137-144`, `ProxyTokenRefusedE2ETests.cs:145-152`,
`ProxySpawnedBackendE2ETests.cs:83-100`, `OtlpMetricExportE2ETests.cs:39-44/69`,
`OtlpTraceExportE2ETests.cs:42-47/171/177`.
