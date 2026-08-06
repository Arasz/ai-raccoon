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
