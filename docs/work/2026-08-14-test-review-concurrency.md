# Concurrency and timing test review

Date: 2026-08-14. Reviewer: test-engineer (read-only). Subject: every timing/concurrency test in
`tests/AiRaccoon.Tests/` — 98 grep hits across 30 files.

**CI context sets the severity.** Per `.github/workflows/build.yml`, every PR runs three gating
jobs on 2-core `ubuntu-latest`: `Speed=Fast` (`:60`), `Category=bdd` (`:81`), `Speed=Slow` (`:113`).
Nothing here is nightly-only, so a fixed sleep tuned on an M-series Mac now gates merges on a
shared 2-core Linux runner using `inotify` rather than FSEvents.

**Verification honesty.** Claims are marked **[deductive]** where the conclusion follows
structurally from the code (a single-threaded test cannot contend a lock — no experiment changes
that) and **[unverified — static reasoning]** where a run is genuinely needed.

## Headline: the DotNext premise does not survive contact with the code

The task asked whether DotNext offers primitives worth adopting. Three facts reframe it:

1. `DotNext.Threading.dll` is **already on the test compile path** transitively via
   `ProjectReference` -> `src/AiRaccoon/AiRaccoon.csproj:41`. No csproj change is needed to use it.
2. **`DotNext.Threading` has zero production callers.** `grep -rn "DotNext" src/ --include='*.cs'`
   returns exactly one hit: `src/AiRaccoon/Setup/McpServerSetup.cs:11`,
   `using DotNext.Collections.Generic;` — from the *core* `DotNext` package, not `.Threading`.
3. Site by site, the BCL already provides the win.

**Net recommendation: zero adoptions, and one removal to consider** — `DotNext.Threading` at
`AiRaccoon.csproj:41` has no callers in `src/` and should probably be dropped, keeping only
`DotNext`.

### API verification against the shipped XML docs

Confirmed present in 6.6.1 (`~/.nuget/packages/dotnext.threading/6.6.1/lib/net10.0/DotNext.Threading.xml`):
`AsyncBarrier`, `AsyncCountdownEvent`, `AsyncManualResetEvent`, `AsyncAutoResetEvent`,
`AsyncExclusiveLock`, `AsyncTrigger` (incl. `SpinWaitAsync<T>`), `AsyncCounter`, `AsyncBridge`,
`AsyncLazy<T>`, `AsyncReaderWriterLock`, `AsyncSharedLock`, `AsyncCorrelationSource<K,V>`,
`Tasks.TaskCompletionPipe<T>`, `Tasks.ValueTaskCompletionSource<T>`,
`Collections.Concurrent.IndexPool`, `Leases.LeaseProvider<T>`.

**Not present — the orchestrator's brief was wrong on two counts, both excluded from the top-10:**

| claimed | status |
|---|---|
| `AtomicInt64` / `AtomicBoolean` / `AtomicReference` | **Do not exist.** Zero matches in either package's XML. In 6.x they were replaced by the `DotNext.Threading.Atomic` *static extension class* (`Read`, `Write`, `GetAndUpdate`, `AccumulateAndGet`, `TrueToFalse`). |
| `Continuation` | **Does not exist** in either package's public surface. |

### Site-by-site verdict — every one a reject

| site | current | candidate | verdict |
|---|---|---|---|
| `Unit/Storage/SqliteMemoryStoreTests.cs:816,855,880,906,930` | `new Barrier(2)` + `SignalAndWait()` | `AsyncBarrier` | **REJECT.** The wins would be not blocking a pool thread (immaterial at N=2) and a bounded wait — and the BCL already has `Barrier.SignalAndWait(TimeSpan)`. **Do the BCL fix instead:** all five sites currently leak an undisposed `Barrier` (it is `IDisposable`) and none has a timeout. |
| `Unit/Setup/Serve/McpTokenFileTests.cs:263` (`Barrier(16)` + 16 real `Thread`s) | dedicated OS threads | `AsyncBarrier` | **REJECT, emphatically.** The dedicated threads are the point — 16 simultaneous OS-level `FileMode.CreateNew` attempts. Moving to the thread pool makes the race *weaker*. Correct as written. |
| `Integration/WatchDigestConcurrencyTests.cs:211,238` | `ManualResetEventSlim.Wait(Patience)` | `AsyncManualResetEvent` | **REJECT.** `IMarkdownChunker.Chunk` is a synchronous interface (`:224`); the blocking wait is forced by the seam, not chosen. |
| `Unit/Watch/WatchScanGuardTests.cs:112`, `WatchSchedulerTests.cs:172-211` | `TaskCompletionSource` + `lock` counters | `AsyncCountdownEvent` | **REJECT at N=2.** Would pay off at N>=5 with a dynamic count; `GatedRunner`'s counter is clearer and also tracks `MaxConcurrent`, which no DotNext primitive gives you. |
| ~7 hand-rolled poll loops | `while (!cond) await Task.Delay(20)` | `AsyncTrigger.SpinWaitAsync` | **REJECT — wrong shape.** `AsyncTrigger` needs a *producer* to call `Signal()`. These poll external state nobody signals (file contents, a SQLite row, subprocess stdout, an inotify-delivered list). A plain bounded poll is correct. |
| `SqliteWatchScanLease` (production) | conditional `UPDATE` + `TimeProvider` | `Leases.LeaseProvider<T>` | **REJECT.** ~40 lines, atomic, correct. Swapping it for a framework abstraction is the definition of over-engineering. |

## 1. Test honesty

| site | problem | fix | sev |
|---|---|---|---|
| `Integration/WatchStoreCascadeTests.cs:132,135,137,141` | **The assertion cannot fail.** The measurement window at `:141` encloses the test's own 3 s sleep at `:135`, so elapsed is >=3 s unconditionally, contention or not. The comment at `:139-140` ("a remove that slipped in before the UPDATE took the lock would return in milliseconds") describes a check the code does not perform — the remove's own duration is never measured. **[deductive]** | Delete the stopwatch. Assert `remove.IsCompleted.ShouldBeFalse()` on the line before `await held.CommitAsync(...)` at `:136` — that is the actual claim, and it goes red the instant the cascade stops taking the write lock. | **Critical** |
| `Unit/Watch/WatchScanGuardTests.cs:33,36` | Both `Run` calls execute **on the test thread**, separated by an `await`. `WatchScanGuard._gate` (`WatchScanGuard.cs:13`) and the `lock` at `:28` are never contended — swapping the `Dictionary`+`Lock` for a bare unlocked `Dictionary` passes. The docstring says "concurrent Run calls ... join the in-flight scan" (`:8`); that word is not exercised. **[deductive]** | Two `Task.Run` bodies released by a shared TCS, both calling `Run` for the same key; assert `StartedScans == 1 && SkippedScans == 1` and both tasks reference-equal. Also exposes that `StartedScans`/`SkippedScans` (`:15,17`) are incremented under the lock but **read without it**. | **High** |
| `Integration/SqliteWatchScanLeaseTests.cs` (all 15 tests) | The docstring promises "only one owner may scan a watch at a time" (`:10`) but **not one test creates two concurrent acquirers** — zero hits in the concurrency grep. The SQL (`MemorySql.cs:272-276`) *is* one atomic conditional UPDATE, so WHERE-clause semantics are pinned; what is not pinned is that two racing acquirers on **separate connections** yield exactly one `true`, and that the loser returns `false` rather than surfacing `SQLITE_BUSY` through `busy_timeout=5000` (`SqliteConnectionFactory.cs:293`). | Two lease instances over two factories, both released from a `Barrier(2)`; assert `results.Count(x => x) == 1` **and** `Should.NotThrow`. Harness to copy already exists: `WatchDigestConcurrencyTests.Process` (`:174-200`). | **High** |
| `Unit/Sync/SyncServiceTests.cs` (1463 lines) | No `Task.Run`/`Barrier`/`WhenAll` anywhere; the only "concurrent" is a string literal at `:1457`. `SyncService._gate` (`SyncService.cs:19`) is never contended — deleting the semaphore is silent. **[unverified — needs a mutation run]** | Two `MemorySyncAsync` calls from a barrier against `FakeCloudStore`; assert one upload, no lost update. | **High** |
| `Unit/Setup/Serve/McpTokenFileTests.cs:48-64` | Genuinely concurrent (16 threads on a barrier) — **but it races `TryMintAsync`, which never touches `McpTokenFile.Gate`** (`McpTokenFile.cs:33`; only `EnsureAsync`/`AcquireAsync` at `:58` do). Correctness rests entirely on OS `FileMode.CreateNew` atomicity, so the *named* primitive is exercised only by `:222-238`, and only probabilistically. | Keep both; add one that holds the debris-delete open at a seam so the `Gate` window is forced, not hoped for. | Medium |
| `Unit/Embedding/EmbeddingServiceTests.cs` | `ConcurrentDictionary.GetOrAdd` (`EmbeddingService.cs:29`) never raced. | Barrier(2), two `CreateGenerator` with the same fingerprint, assert `ReferenceEquals`. | Medium |
| `TickSignal` (`src/.../Maintenance/TickSignal.cs:9,17,27,44`) | **No `TickSignalTests.cs` exists at all.** Four `lock` regions, only indirect coverage. | A direct test racing `Signal` against `WaitAsync`. | Medium |

### The prior review's finding, and its status

From `docs/work/2026-08-07-moe-d-tests.md:69`, finding **D21**:

> These five hold explicit concurrency control that no test ever contends — every test drives them
> sequentially. [...] A regression that removed or mis-scoped `SyncService`'s gate (double upload,
> lost update) would be completely silent. **Awaiting calls one after another is not a concurrency
> test.**

**Status: not fixed.** All five sites remain uncontended — `SyncService.cs:19`,
`WatchPipeline.cs:56-57`, `WatchEventSource.cs:24`, `TickSignal.cs:9`, `EmbeddingService.cs:29`.
`git log --since=2026-08-07` on `SyncServiceTests.cs` shows only `04c63906 chore: fix namespaces`.

That document's own **correction** also holds up on re-verification: `WatchScheduler` is *not* on
the untested list — `WatchSchedulerTests.cs:21-41` with `GatedRunner` at `:172-211` pins the
concurrency *limit* (`MaxConcurrent.ShouldBe(4)`), not merely completion.

### Attacked and could not break — do not re-derive

`Unit/Storage/SqliteMemoryStoreTests.cs:816-947` (five `Barrier(2)` tests against real SQLite
asserting convergence, not "no exception"); `Integration/WatchDigestConcurrencyTests.cs:206-248`
(`GateChunker` parks the digest *between its delete and its inserts*, forcing the interleaving at a
seam rather than timing it — **the best concurrency test in the repo and the template to copy**);
`Unit/Observability/PromotionQueueMetricsTests.cs:200-225` (honestly disclaims being a
deterministic repro at `:195-196`); `Unit/Setup/Serve/NodeRunnerTests.cs:157-205` (two real Kestrel
binds to one real port on a shared TCS gate); `Unit/Watch/WatchPipelineTests.cs:83,110` and
`WatchCatchUpTests.cs:376`; `Integration/WritePerformanceBenchmarkTests.cs:46,63` (measures latency
and explicitly **does not assert** on it — "a hard threshold here would only flake under CI load",
exactly right, and the counter-example to section 2).

## 2. Flakiness, ranked

| rank | site | problem | replacement |
|---|---|---|---|
| **1** | `BDD/FileWatcherSteps.cs:223,231` | `await Task.Delay(150)` x5 is the **entire** guarantee that a real `inotify` event reached `WatchPipeline` before the following tick. Feeds `ThenStatusReportsStopped` (`:1072-1078`), a direct unpolled assert. One late event under-counts failures -> red. Runs in `build-bdd`. | `Ctx.StepUntilAsync` (`FileWatcherFeatureContext.cs:107`) — already exists, re-checks each iteration, prints which budget expired |
| **2** | `BDD/FileWatcherSteps.cs:889, 1089` | Same shape; `:1089` asserts `Retrying` three lines after the sleep with no re-check | same |
| **3** | `Integration/WatchStoreCascadeTests.cs:135` | 3 s sleep while a write txn is held, against `busy_timeout=5000` / `DefaultTimeout=5` — a **2-second margin** before the blocked `RemoveWatchAsync` throws `SQLITE_BUSY`. Costs 3 s of `build-slow` every run | fixed by the section-1 row 1 change; removes both flake and 3 s |
| **4** | `Unit/Watch/WatchEventSourceTests.cs:218` | `Thread.Sleep(300)` then a **negative** assertion against a real `FileSystemWatcher`. On Linux inotify under load, a sibling event at 301 ms is a **false pass**, not a red — the worse failure mode, since the filter at `WatchEventSource.cs:156-193` could break silently | write the sibling, then `WaitFor` a *sentinel* change on the target file (proving the watcher drained past the sibling write), then assert absence |
| **5** | `Unit/Setup/Serve/IdleWatchdogTests.cs` — 18 `Task.Delay(100)` sites across 5 tests | Papers over two races: reaching `PeriodicTimer.WaitForNextTickAsync` (`IdleWatchdog.cs:39-40`) so `Advance` has a timer, and the tick's *continuation* (`RunOnce()` -> `StopApplication()`). `FakeTimeProvider.Advance` fires the callback but does not await the resumed state machine. 100 ms bounds nothing. In the **Fast** gate, costing 1.8 s | **The seam already exists and is already used.** `IdleWatchdog.RunOnce()` is `internal`, documented "Test seam" (`:49-50`), and used by three tests in the same file (`:174,192,210`). Drive the decision tests through `Advance` + `RunOnce()` with zero delays; keep exactly one `StartAsync` test for the loop wiring |
| **6** | `Unit/Setup/McpServerSetupHostTests.cs:295,297,318,322`; `Unit/Observability/ObservabilityEndpointTests.cs:150-166` | Same pattern through a full ASP.NET host; both in the **Fast** gate | bounded poll on `lifetime.StopCalls`; the negative cases need a quiescence wait — one shared named helper, not nine open-coded sleeps |
| 7 | `McpTokenFileTests.cs:154,176,197` and `:229` | `McpTokenFile` **already takes `TimeProvider`** (`:81,83`) yet `:229` uses a real 200 ms heal window raced by 16 threads | fake-clock the first three; keep real threads at `:229` (the OS race is the point) but note the load sensitivity |
| 8 | `Integration/WatchIntegrationTests.cs:516` | wall-clock upper bound over 300 files on a shared runner — **and redundant**, since `:517-519` already assert `State == Scanning` and `search count == 0`, proving the same claim behaviourally | delete `:512` and `:516` |
| 9 | `BackendLauncherTests.cs:73,133,157`; `NodeRunnerTests.cs:321,335`; `E2E/ProxyTokenRefusedE2ETests.cs:76`; `E2E/ServeRestartE2ETests.cs:101` | "fast enough" bounds — a coin flip with a generous bias. `NodeRunnerTests` is Fast-gated yet holds a 2 s sleep | assert the observable where one exists (`ProxyTokenRefused` already asserts stderr at `:69-73`; the `<20s` adds only a hang detector, which is what `timeout-minutes` is for). Where a sleep proves an *absence*, that is the honest shape — extract it as a named `QuiesceAsync` |
| — | `TestHelpers/WaitByPooling.cs` | `FirstTick = 10s`, only **4** predicate checks ever run (the limit is tested *before* the predicate at `:28-31`), period grows by a **random 0-10 s** each iteration. Its one consumer (`E2E/McpTokenGateE2ETests.cs:162`) passes `TimeProvider.System`, so that fixture pays >=10 s per construction even when `serve` listens in 200 ms. The name is also misspelled ("Pooling" -> "Polling"), which is why nobody found it | **Not a flake — a 10 s tax.** 1 consumer against ~50 hand-rolled sleeps: not load-bearing. Replace with `Eventually.UntilAsync` and delete |
| low freq, **high confusion** | `BDD/FileWatcherFeatureContext.cs:46,205`; `Integration/WatchIntegrationTests.cs:650,681` | **Latent race in the harness itself.** `List<T>.Add` passed as the error sink to `WatchEventSource`, whose `ReportError` (`:204-208`) is reached from the `FileSystemWatcher` callback thread (`:86`). A concurrent grow can lose an entry or throw | `ConcurrentBag<WatchEventError>`, as the OTLP collectors already correctly do (`OtlpMetricExportE2ETests.cs:106`) |
| — | `Unit/Watch/WatchPipelineTests.cs:359-371` | `EventuallyAsync` is **dead code** — one occurrence, the definition | delete |

**Safe by construction — do not "fix" these:** `WatchIntegrationTests.cs:776-806`,
`FileWatcherFeatureContext.cs:107-137`, `WatchEventSourceTests.cs:293-306` (3 s deadline +
`XunitException` naming what it waited for), `ShutdownEndpointTests.cs:148`,
`NodeRunnerTests.cs:392`, `ExtractionHostedServiceTests.cs:50,76`, `ProxyForwardTests.cs:232`, the
three OTLP `WaitForRequestAsync` copies.

## 3. Missing coverage

| production path | gap | what the missing test asserts | sev |
|---|---|---|---|
| `WatchScanLease.cs:38-45` + `MemorySql.cs:272-276` | no concurrent acquirer | two instances over two connections from a barrier -> exactly one `true`, zero throws (proves `busy_timeout` handles the loser) | **High** |
| `WatchScanGuard.cs:13,28-40,107-115` | `lock` never contended | two concurrent `Run` on one key -> `StartedScans == 1`, `SkippedScans == 1`, tasks reference-equal; plus `Run` racing `Complete` (entry removal `:111` vs re-insert `:38`) proving a watch is never left permanently unscannable | **High** |
| `SyncService.cs:19` | semaphore never contended | two concurrent `MemorySyncAsync` -> one upload, no lost update | **High** |
| `WatchPipeline.cs:56-57` (8 `lock` regions) | in production `WatchHostedService.ExecuteAsync` runs `RunAsync` concurrently with a reconcile loop calling `RegisterWatch`/`UnregisterWatch` (`:63,140,199`); no test drives both as unbounded concurrent tasks | `UnregisterWatch` during an in-flight tick -> no `KeyNotFoundException`, no orphaned `_pending`, `GetStatuses` consistent | **High** |
| `WatchEventSource.cs:24` | `Translate` runs on the watcher callback thread; every test is single-threaded | `Stop(project, path)` while real events fire -> nothing delivered after `Stop` returns, no callback-thread exception | Medium |
| `PromotionQueueService.cs:102-109` | the comment names the race ("a concurrent discard may have removed this row since the `ListAsync` snapshot"); nothing races it | concurrent `PromoteAsync` vs `DiscardAsync` on one row -> claimed exactly once | Medium |
| `WatchScheduler.cs:12,90-107` (`GateFor`) | only ever called from one thread | two concurrent `RunBatchAsync` for one project -> one semaphore instance, limit respected across both | Medium |
| `EmbeddingService.cs:29` | never raced | two `CreateGenerator`, same fingerprint -> one engine | Medium |
| `TickSignal.cs` | no test file | `Signal` racing `WaitAsync` -> no lost wakeup, no double-fire | Medium |
| `IdleWatchdog.cs:19,34,55` | `NotifyActivity()` never called concurrently with a tick; swapping `Interlocked` for a plain field passes **[unverified]** | hammer `NotifyActivity` while the tick loop runs -> `StopCalls` stays 0 | Low |

**Adequately covered — do not add here:** SQLite write contention, digest concurrency, scheduler
limit, extraction queue metrics, restart/port race, token-file mint race.

## 4. Shared components

The suite hand-rolls **one bounded-poll helper per file**, and the drift is already visible:

| copy | location | note |
|---|---|---|
| `Stack.StepUntilAsync` | `WatchIntegrationTests.cs:776-806` | default `maxFakeSeconds: 60` |
| `FileWatcherFeatureContext.StepUntilAsync` | `:107-137` | **near-verbatim copy**, default `maxFakeSeconds: 2` — the drift |
| `WaitForRequestAsync` | `OtlpMetricExportE2ETests.cs:111`, `OtlpTraceExportE2ETests.cs:223`, `OtlpFlushOnExitTests.cs:145` | **three verbatim copies** |
| `WaitUntilAsync` | `ProxyForwardTests.cs:229-235` | no deadline of its own |
| `WaitFor` (sync) | `WatchEventSourceTests.cs:293-306` | best diagnostic of the lot |
| `WaitForLineAsync`/`WaitForUrlAsync`/`WaitForStoppingAsync` | `NodeRunnerTests.cs:392`, `ServeRestartTests.cs:229`, `ShutdownEndpointTests.cs:148` | three more |
| raw inline loops | `WatchIntegrationTests.cs:62-67,76-83` | no helper at all |
| `WaitByPooling` | `TestHelpers/` | the one *shared* helper — 1 consumer, 10 s first tick |

| type | responsibility | replaces | verdict |
|---|---|---|---|
| **`TestHelpers.Eventually`** — `UntilAsync(Func<Task<bool>>, TimeSpan deadline, TimeSpan interval, string what)`, throwing `XunitException` naming `what` and the expired budget | one bounded poll *with a diagnostic* — the thing every copy reinvents and only `WatchEventSourceTests.WaitFor` gets right | the 3 `WaitForRequestAsync` copies, `WaitUntilAsync`, `AttachedToolCallMetricsAsync`, the 3 `WaitForXAsync`, the 2 raw loops, and **`WaitByPooling` outright** | **CONFIRM — highest leverage** |
| **`TestHelpers.SteppedClock.UntilAsync`** — advance N ms, pump a tick delegate, short real sleep, re-check; dual fake/real budget with a "which budget expired" diagnostic | the hybrid fake-clock + real-OS-event shape | merges `WatchIntegrationTests.cs:776-806` and `FileWatcherFeatureContext.cs:107-137`, killing the 60-vs-2 drift; then retires the ten `Task.Delay(150)` sites in `FileWatcherSteps.cs` | **CONFIRM — fixes flake ranks 1 and 2** |
| **`TestHelpers.RaceHarness`** — `RunTogetherAsync<T>(int n, Func<int, Task<T>>)` and `RaceOnThreads(int n, Action<int>)`, real barrier start, results **and** exceptions collected | "run N truly in parallel and collect everything" | `RaceOnThreads` already exists, correct, at `McpTokenFileTests.cs:260-281` — **promote it, do not reinvent it**. The async twin absorbs the five `SqliteMemoryStoreTests` barrier sites (fixing the undisposed `Barrier`s, adding timeouts) and is the vehicle for every section-3 gap | **CONFIRM (extend, don't design)** |
| **`TestHelpers.RecordingErrorSink`** — `ConcurrentBag`-backed `Action<WatchEventError>` with `WaitForAsync(predicate)` | closes the `List<T>.Add`-from-watcher-thread race | `FileWatcherFeatureContext.cs:46,205`; `WatchIntegrationTests.cs:650,681` | **CONFIRM** |
| a "deterministic clock fixture" | — | — | **REJECT.** `FakeTimeProvider` is already used correctly and consistently. The problem is never the clock — it is the *un-awaitable continuation after* `Advance`, which no fixture can fix. The fix is the `RunOnceAsync` seam the codebase already uses. |
| **`TestHelpers.TimerRegistrationProbe`** — `TimeProvider` decorator completing a TCS on first `CreateTimer` | replaces the *first* `Task.Delay(100) // timer registers` in each pair | `IdleWatchdogTests`, `BackendLauncherTests:147,193`, `McpTokenFileTests:154,176,197`, `McpServerSetupHostTests:295`, `ObservabilityEndpointTests:150` | **CONFIRM, second choice** — prefer the `RunOnce` seams; adopt only where a test must exercise the `BackgroundService` loop |

## Top 10 by value / effort

| # | change | why | effort |
|---|---|---|---|
| 1 | Fix the vacuous assertion at `WatchStoreCascadeTests.cs:141` — `remove.IsCompleted.ShouldBeFalse()` before the commit at `:136`, drop the 3 s sleep | The check has never been able to fail; violates *"A check you have not seen fail is not a check"*. Also removes flake rank 3 and 3 s of `build-slow` | **XS** |
| 2 | Extract `Eventually.UntilAsync`, delete `WaitByPooling` | One helper replaces ~8 loops; removes a >=10 s-per-fixture tax with randomised duration and an off-by-one capping it at 4 checks | **S** |
| 3 | Add the concurrent-acquire test to `SqliteWatchScanLeaseTests` | 15 tests promising mutual exclusion, zero creating two acquirers. Harness exists at `WatchDigestConcurrencyTests.cs:174-200` | **S** |
| 4 | Make `WatchScanGuardTests.cs:18-44` actually concurrent | Currently passes with `WatchScanGuard`'s `Lock` deleted. ~10 lines, and surfaces the unsynchronised counter reads | **S** |
| 5 | Convert `IdleWatchdogTests` to the existing `RunOnce()` seam | Removes 18 `Task.Delay(100)` from the **Fast** PR gate (−1.8 s, −5 races). The seam is documented and already used in the same file | **S–M** |
| 6 | Route `FileWatcherSteps.cs:223,231,889,1089` through `Ctx.StepUntilAsync` | The top two flake risks, in the `build-bdd` gate on a 2-core Linux runner. Helper already exists | **M** |
| 7 | Add the `SyncService._gate` contention test | Flagged as D21 on 2026-08-07, estimated highest expected value, warned it may go red. Still unfixed. `FakeCloudStore` is ready-made | **M** |
| 8 | Promote `RaceOnThreads` to `TestHelpers` + async twin | The correct harness already exists; promoting it is the vehicle for items 3, 4, 7 and every coverage gap. Also fixes five undisposed `Barrier`s | **S–M** |
| 9 | Fix the negative-after-sleep at `WatchEventSourceTests.cs:218` | Failure mode is a **false pass**, so the sibling filter could break silently. Worse than a flake | **S** |
| 10 | Delete `WatchIntegrationTests.cs:512,516` and `WatchPipelineTests.cs:359-371` | Pure subtraction — a redundant wall-clock bound and dead code, no coverage cost | **XS** |

**Deliberately excluded:** every DotNext.Threading adoption (the BCL wins at every site, and the
package has zero production callers), and anything resting on `AtomicInt64`/`AtomicBoolean`/
`AtomicReference`/`Continuation`, which do not exist in 6.6.1.
