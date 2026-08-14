# Lane report — runtime operations (hosted services, watch, resilience, observability)

Campaign: project-scope review, base `1d1889d517baf840df0b839f547091bd7f46808b`.
Model: sonnet · read-only. Lane verified the base SHA.

---

### F1 — Every hosted service but one turns its own graceful shutdown into a false "unhandled exception" crash log [MEASURED]
**Severity:** HIGH
**Evidence:** `src/AiRaccoon.Infrastructure/Maintenance/BankMaintenanceHostedService.cs:66`,
`src/AiRaccoon.Infrastructure/Degradation/SweepHostedService.cs:43`,
`src/AiRaccoon.Infrastructure/Extraction/ExtractionHostedService.cs:29`,
`src/AiRaccoon/Hosting/Watchdog/IdleWatchdog.cs:40` — each has
`while (await timer.WaitForNextTickAsync(stoppingToken))` sitting **outside every try/catch**; the
only catches in each method wrap the inner `RunOnceAsync`/`RunOnce` call, not this condition.
`PeriodicTimer.WaitForNextTickAsync` transitions to `OperationCanceledException` on token
cancellation — it returns `false` only when `Dispose()` is called, never on cancellation.
`grep -rn "BackgroundServiceExceptionBehavior" src` → zero hits, so `HostOptions` is at the .NET
default `StopHost`.

Live evidence from the installed `1.12.0+c5f3fa26` server (`~/.ai-raccoon/serve.log`): a
`serve --restart` produced `fail: … BackgroundService failed` with a stack ending at
`BankMaintenanceHostedService.ExecuteAsync`, followed by `crit: … BackgroundServiceExceptionBehavior
is configured to StopHost … IHost instance is stopping`.

**Failure scenario:** any graceful shutdown path (`serve --restart`'s `/shutdown`, `IdleWatchdog`
firing after the idle timeout, SIGTERM/Ctrl-C) cancels every hosted service's `stoppingToken` via
`IHostApplicationLifetime`. On their next iteration, Bank/Sweep/Extraction/IdleWatchdog are each
blocked on the timer await; all four throw simultaneously, all four escape `ExecuteAsync` uncaught,
and Host logs a `fail` + `crit` block for each — **up to four decoy "crashes" per ordinary
shutdown**.

**Why it matters:** this is exactly what happened live — a routine restart read as an unhandled
exception taking the host down. Anyone watching for `crit`-level logs will chase an incident that
never occurred, and it buries the actual benign shutdown reason. `WatchHostedService` is the one
hosted service **not** affected: it uses `while (!stoppingToken.IsCancellationRequested)` with an
explicit `try/catch (OperationCanceledException)` around its `Task.Delay`, and is the correct
template.

**Note on severity (orchestrator concurs):** `stoppingToken` is only cancelled when the host is
already stopping, so this does **not** cause a shutdown that would not otherwise happen. The defect
is the false diagnosis, not a crash.

**Fix:** wrap the post-startup loop in `try { … } catch (OperationCanceledException) when
(stoppingToken.IsCancellationRequested) { }` in the four affected services, matching
`WatchHostedService`'s shape.

---

### F2 — `BackgroundServiceExceptionBehavior` is left at its implicit .NET default (StopHost), making any one hosted service's escaped exception a whole-server outage [READ]
**Severity:** MEDIUM
**Evidence:** no call to `services.Configure<HostOptions>(…)` anywhere in `src`; every hosted
service's own pass already logs-and-continues on `Exception` (the `RunFailed`/`ProjectFailed`
patterns), showing the code was written with a "one bad pass never takes down the process" intent —
except for F1's uncaught path.

**Failure scenario:** a future bug anywhere in any of the five hosted services' loop bodies takes
down memory search, writes, and every MCP tool server-wide.

**Fix:** decide deliberately — set `BackgroundServiceExceptionBehavior.Ignore` to match the
log-and-continue posture used throughout, or keep `StopHost` and document it as intentional
fail-fast. Fixing F1 removes the only currently-reachable trigger, but the policy is an implicit
default worth pinning explicitly.

---

### F3 — Quiet-mode logging has no floor for framework noise, so `DefaultHttpClientFactory`'s 10-second heartbeat floods an explicitly-never-rotated log file forever [MEASURED]
**Severity:** MEDIUM
**Evidence:** `src/AiRaccoon/Setup/Logging/QuietLogging.cs:19` —
`loggingBuilder.SetMinimumLevel(LogLevel.Trace)`, with the file's own doc comment: "No rotation (D5,
owner-approved) — the file accumulates across the installation's lifetime."
`src/AiRaccoon/Setup/Logging/HostLogging.cs:18-19` filters only `Microsoft.AspNetCore` and
`ModelContextProtocol` to Warning, and only under HTTP transport — never `Microsoft.Extensions.Http`.
`src/AiRaccoon/Hosting/Node/NodeRegistration.cs:14,17,20` and
`src/AiRaccoon/Hosting/Proxy/ProxyRegistrations.cs:14` call `.RemoveAllLoggers()` on the
`ServerProbe`/`ObservabilityRunner`/`ServerRestart` named clients — that strips only the per-request
pipeline logger, **not** `DefaultHttpClientFactory`'s own cleanup-cycle logger (a separate category,
logged by the factory itself). `ProxyRegistrations.cs:17`'s `BackendSessionClient` has no
`.RemoveAllLoggers()` at all.

Live: `~/.ai-raccoon/quiet.log` is **10 MB**, mostly "Starting/Ending HttpMessageHandler cleanup
cycle" at Debug, emitted every 10 s — roughly 8,640 lines/day of pure heartbeat, unbounded.

**Why it matters:** the D5 "no rotation" ruling almost certainly assumed sparse real events, not a
10-second framework heartbeat. The current floor turns a deliberate policy into an
unbounded-disk-growth bug.

**Fix:** `loggingBuilder.AddFilter("Microsoft.Extensions.Http", LogLevel.Warning)` in
`QuietLogging.Configure` (or unconditionally in `HostLogging.Configure`).

---

### F4 — A FileSystemWatcher buffer overflow is logged and forgotten: no rescan, no status change, silent permanent loss until restart [INFERRED]
**Severity:** MEDIUM
**Evidence:** `src/AiRaccoon.Infrastructure/Watch/WatchEventSource.cs` — no `InternalBufferSize` set
anywhere (default OS ~8 KB applies); `watcher.Error += (_, e) => HandleError(…)` (`:86`) →
`ReportError` (`:204-216`) only logs. `src/AiRaccoon/Setup/AppRegistrations.cs:226-228` wires
`onError` to nothing but a log line.
`src/AiRaccoon.Infrastructure/Watch/WatchHostedService.cs:163-172` —
`_catchUp.EnqueueInitialScan`/`EnqueueChangedSince` only fire when a watch key transitions
inactive→active (`if (!started) continue;`), i.e. only at first registration or after a process
restart, never in response to a runtime `Error` event.

**Failure scenario:** a burst of filesystem changes (git checkout, IDE "reformat all", rsync) exceeds
the default buffer; `InternalBufferOverflowException` is caught, logged, and nothing else — the files
changed during that window are never re-enqueued, and `memory_watch_status` still reports "Healthy"
since nothing updates `WatchPipeline._runtime`.

**Why it matters:** precisely the class of defect
`docs/plans/2026-08-07-watch-scan-runaway-fix.md` closed for scans, but that fix never touched the
`Error` path — silent, invisible ingestion loss with no operator signal.

**Fix:** on `WatchEventError`, raise `InternalBufferSize` (e.g. 64 KB) at watcher creation and/or
enqueue a fresh `EnqueueChangedSince` catch-up scan for that watch, and flip its runtime state so
status tools surface it.

---

### F5 — The promotion queue only drains when an operator flips a single global mode switch, explaining the live 38-hour-old queue [MEASURED]
**Severity:** MEDIUM
**Evidence:** `src/AiRaccoon.Core/Memory/ExtractionConfigKeys.cs` — `ParseEnabled(null) == false`
(disabled by default), `ParseMode(null) == ExtractMode.Propose` (not Promote).
`src/AiRaccoon.Infrastructure/Extraction/ExtractionHostedService.cs:129` — one pass does either
propose or promote, gated by a single bank-global setting, never both. Live
`memory_promotion_list(projectId=ai-raccoon)` returned exactly 3 rows (watched `docs/work/*` files),
`oldestWaitSeconds: 139013` (~38.6 h).

**Why it matters:** nothing auto-drains the queue unless someone flips `extract.mode.global` to
`promote` or calls the tool by hand, and the live bank shows nobody has, for over a day and a half.
A bank left in propose-only mode accumulates candidates forever.

**Fix (if unintended):** alternate propose/promote automatically each pass, or add a queue-age alert
paired with the existing 5-minute stale-claim reclaim.

---

### F6 — A write-performance benchmark measures per-thread allocation across `await` boundaries [INFERRED, test-only]
**Severity:** LOW
**Evidence:** `tests/AiRaccoon.Tests/Integration/WritePerformanceBenchmarkTests.cs:46,56` —
`GC.GetAllocatedBytesForCurrentThread()` read before and after a loop of 50 `await
memoryStore.WriteAsync(…)` calls, each with internal awaits that can resume on a different ThreadPool
thread. Three live runs produced 71.62 KB/write and did not surface a negative value — the mechanism
is confirmed, the failure mode was not reproduced.

**Fix:** use `GC.GetTotalAllocatedBytes()` (process-wide) for any span crossing an `await`.

---

### F7 — One direct `logger.LogWarning(...)` call bypasses the mandatory `[LoggerMessage]` convention [READ]
**Severity:** LOW
**Evidence:** `src/AiRaccoon/Tools/MemoryTools.cs:152`. Repo-wide: **111 `[LoggerMessage]` methods
vs this 1 direct call.**
**Fix:** add `Log.SearchQualityRecordFailed` and call that.

---

### F8 — The BDD scenario for the store's own metrics/tracing is an unimplemented stub, so nothing asserts the telemetry wiring end to end [READ]
**Severity:** LOW
**Evidence:** `docs/work/features-native-memory/native-memory.feature:70-72` — `@ignore` scenario with
zero Given/When/Then steps, only a comment. The underlying pieces are unit-tested individually
(`tests/AiRaccoon.Tests/Unit/Observability/ToolCallMetricsTests.cs`, `ToolExecutionActivityTests.cs`,
`Integration/Observability/OtlpExportTests.cs`).

**Failure scenario:** a DI/middleware regression that drops the metrics/tracing wiring for real MCP
tool calls would pass every existing unit test.

**Fix:** write the scenario's steps (or an equivalent integration test) driving a real `memory_write`
through the host and asserting on the emitted `Activity`/metric, then remove `@ignore`.

---

## Still open

- Whether any other reachable path beyond the four hosted-service loops can escape to `StopHost` — loop bodies were checked, not every callee exhaustively.
- Provenance oddity: the shipped 1.12.0 binary's stack frames carry paths under `.ai-badger/worktrees/tar2/` — flagged for the CI/docs lane as a release-traceability question.
- Whether `serve.log`'s growth/rotation (distinct from `quiet.log`) has an analogous issue — likely an external supervisor's stdout redirect, outside this codebase.
- Reproducing F6's negative-allocation case under real CI contention.
- F8: no live `memory_write` was driven through the host with OTel output inspected; verified via code plus existing unit coverage only.

## Grade mix

MEASURED 3 (F1, F3, F5 — each backed by a live artefact plus code) · READ 3 (F2, F7, F8) ·
INFERRED 2 (F4, F6 — mechanism confirmed, failure not reproduced) · UNVERIFIED 0.

## Owner questions

1. Is the implicit `BackgroundServiceExceptionBehavior.StopHost` (F2) the intended fail-fast posture, or should it be `Ignore` to match the log-and-continue design used inside every hosted service's own pass?
2. Is the promotion queue (F5) meant to require manual `extract.mode.global=promote` / `memory_share_extract`, or should propose/promote alternate automatically?
3. Was the quiet-log "no rotation" ruling (D5, F3) made assuming sparse real events, or does it need a filter for framework heartbeat categories like `Microsoft.Extensions.Http`?
4. Should the FileSystemWatcher buffer-overflow path (F4) trigger an automatic rescan, or is "self-heals on next restart" acceptable given the TTL-based degradation model elsewhere?
5. Why does the shipped 1.12.0 binary's stack trace point at `.ai-badger/worktrees/tar2/` rather than a clean-checkout path — does that affect the "releases are traceable" invariant?

## Healthy

- **`WatchHostedService`/`WatchPipeline`/`WatchCatchUp`/`WatchScanGuard`/`SqliteWatchScanLease`:** correct shutdown ordering (`CancelAllScans()` before `StopAll()`), single-flight per-watch scan guard, cross-process lease with TTL + heartbeat renewal, 5-strike retry backoff — faithfully reflects the runaway-fix plan and is the one hosted service immune to F1.
- **`ExitCode.cs`:** one code per restart-failure reason, no reused values (reflection-based uniqueness test), 8 deliberately retired and test-enforced never to return. Every `RestartOutcome` is exhaustively matched in `NodeRunner`.
- **Restart integration tests exercise real argv strings** (`Start(["--data-root", …, "serve", "--port", …, "--restart"])`), not just internal handler calls.
- **`IdleWatchdog`:** lock-free `Interlocked` activity tracking, tick sized to `min(timeout/4, 60s)`.
- **No overlapping-tick risk anywhere:** every one of the five hosted-service loops fully awaits its pass before waiting for the next tick.
- **Logging discipline:** 111/112 log call sites use `[LoggerMessage]`; no entry content or secret appears in any log message or telemetry tag (explicitly documented in `SweepHostedService`'s deletion logging).
- **`BankMaintenanceHostedService`'s VACUUM/ANALYZE/checkpoint ordering and busy-timeout handling** (250 ms defer during the pass, restored to 5000 ms before the connection returns to the pool) is careful and correctly sequenced.

## Disconfirmed

- **"A circuit breaker's state is scoped wrong."** No circuit breaker exists anywhere in the codebase — `ResiliencePipelineFactory` offers only Retry pipelines with jitter, and ADR-0031's title never claims one. There is nothing to mis-scope.
- **"There is async-correctness rot."** Sweep found zero `async void` outside handlers and zero `.Result`/`.Wait()`/`GetAwaiter().GetResult()` in `src`. The `Task.Run` sites (`WatchScheduler`, `OnnxEmbeddingGenerator`, `WatchScanGuard`) all wrap genuinely CPU-bound or must-run-off-caller work, not already-async work wrapped needlessly.
