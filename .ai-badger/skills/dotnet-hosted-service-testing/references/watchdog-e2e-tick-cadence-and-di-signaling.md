# Watchdog E2E gates: tick cadence & DI signaling (verified 2026-08-06)

From the HTTP serve-mode idle-watchdog review. Two test-side traps:

## Real-time E2E gates vs the service's tick period

A gate like "server shuts down within ~5 s real time" (IdleTimeout = 2 s) is UNPASSABLE
when the service checks its condition only on a fixed 60 s `PeriodicTimer` tick — the
shutdown lands on the next minute tick (~62 s). FakeTimeProvider tests mask this (ticks
fire on demand when you Advance); only the real-time E2E exposes it.

- When writing/accepting a real-time gate, check the tick period, not just the timeout.
- Fix the SERVICE when the gate is the contract: `period = min(1 min, IdleTimeout / 2)`.
- Pin the derived period with a unit test.

## `AddHostedService<T>()` does not make T resolvable

`sp.GetRequiredService<T>()` throws — the registration is `IHostedService → T` only
(verified: `TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, T>())`).
"Signal the hosted service from middleware via DI" needs:

```csharp
services.AddSingleton<T>();
services.AddHostedService(sp => sp.GetRequiredService<T>());
services.AddSingleton<IFoo>(sp => sp.GetRequiredService<T>());
```

The naive `AddSingleton<IFoo, T>()` + `AddHostedService<T>()` creates TWO instances — a
unit test resolving only `IFoo` can pass while production signals the wrong instance.
Pin single-instance: resolve `IFoo` and the `IHostedService` entry from one provider and
assert reference equality (the host-shape test is the right home).

## Fake-clock tick-cadence observability (probed 2026-08-06, WP2 watchdog tests)

`FakeTimeProvider` + `PeriodicTimer` on .NET 10 (empirical, file-log probe):

- The fake fires a periodic timer **twice per `Advance`** when two schedule points fall
  inside the window (the timer's due advances by `period` and re-fires within the same
  Advance; the loop consumes the queued ticks sequentially).
- The loop's check runs asynchronously at the **post-advance clock**, never at the tick's
  due time. A single large `Advance` that crosses the deadline therefore fires the
  shutdown immediately — tick period is NOT observable through shutdown timing via one
  big advance (a first attempt asserting "no fire at 2.4s after `Advance(2.4)`" failed
  exactly this way).
- To pin `tick = timeout/4` (R2) via loop behavior: advance in steps ≤ period with real
  settles between, keep the clock below the deadline until the cadence is established,
  then cross. timeout=2s / tick=0.5s:
  - `Advance(1.0); settle; Advance(1.0); settle` → no fire (elapsed exactly 2.0s, strict `>`);
  - `Advance(0.4); settle` → no fire (next tick due at 2.5s);
  - `Advance(0.1); settle` → fire (2.5s > 2.0s).
  This pins tick ∈ (0.4, 0.5] and fails against the 60s-tick bug. Probe: 0.5s advances
  fire exactly at 2.5s (0,0,0,0,1).
- Parallel-load caveat: the same sequence passed alone and failed once under the full
  parallel suite; reruns after a clean rebuild passed consistently. REBUILD after test
  edits and re-run under suite load before declaring a fake-clock test flaky.

## `StopApplication` on a WebApplication host does NOT auto-stop the host (verified 2026-08-06)

`lifetime.StopApplication()` cancels `ApplicationStopping` and makes
`WaitForShutdownAsync` return — but the host does not stop itself; `ApplicationStopped`
fires only after the runner calls `host.StopAsync()`/dispose (`NotifyStopped`). The
foreground runner contract is: `StartAsync` → ... → `WaitForShutdownAsync` returns → stop
the host. Assertion shape for "the watchdog shut the host down" (A6-style host test):

```csharp
lifetime.ApplicationStopping.IsCancellationRequested.ShouldBeTrue();          // StopApplication was called
await host.WaitForShutdownAsync(ct);                                          // the runner's await completes
await host.StopAsync(ct);                                                     // runner stops the host
lifetime.ApplicationStopped.WaitHandle.WaitOne(TimeSpan.FromSeconds(5)).ShouldBeTrue();
```

Do NOT assert `ApplicationStopped` immediately after the advance — with no runner
stopping the host, it never fires and the test fails on a red herring.

## Value-type ctor params vs class-constrained DI overloads (verified 2026-08-06)

Type-based `AddSingleton<TService>()` requires every ctor param resolvable. Feeding a
`TimeSpan` timeout param (IdleWatchdog ctor) fails on BOTH generic overloads —
`AddSingleton<TService>(TService)` and `AddSingleton<TService>(Func<IServiceProvider, TService>)`
are `where TService : class` (CS0452). Register the struct via the unconstrained
non-generic overload, then the type-based triple resolves:

```csharp
services.AddSingleton(typeof(TimeSpan), config.IdleTimeout);
services.AddSingleton<IdleWatchdog>();
services.AddSingleton<IActivitySignaler>(sp => sp.GetRequiredService<IdleWatchdog>());
services.AddHostedService(sp => sp.GetRequiredService<IdleWatchdog>());
```
