# Hosted-service DI resolvability & tick-cadence gates (verified 2026-08-06)

Two review findings from the HTTP serve-mode design review (idle-watchdog BackgroundService
signaled from ASP.NET Core middleware), both verified by decompiling the shared framework
10.0.10 assemblies.

## AddHostedService<T> does NOT make T resolvable

`ServiceCollectionHostedServiceExtensions.AddHostedService<T>()` (decompiled from
Microsoft.Extensions.Hosting.Abstractions 10.0.10):

```csharp
services.TryAddEnumerable(ServiceDescriptor.Singleton<IHostedService, THostedService>());
```

- The descriptor's ServiceType is `IHostedService`; `THostedService` is only the
  implementation. `sp.GetRequiredService<T>()` THROWS ("No service for type … has been
  registered") — T is not resolvable by its own type.
- The design-doc belief "AddHostedService<IdleWatchdog>() already registers the class as a
  singleton" is the exact wrong belief. A factory
  `AddSingleton<IActivitySignaler>(sp => sp.GetRequiredService<IdleWatchdog>())` fails at
  first resolve (the first MCP request).
- The naive "fix" is worse: `AddSingleton<IFoo, T>()` + plain `AddHostedService<T>()`
  creates TWO singleton instances — the middleware/handler signals the wrong one and the
  watchdog never sees activity (silent, test-passing, production-broken).
- Correct single-instance pattern:

```csharp
services.AddSingleton<IdleWatchdog>();
services.AddHostedService(sp => sp.GetRequiredService<IdleWatchdog>());
services.AddSingleton<IActivitySignaler>(sp => sp.GetRequiredService<IdleWatchdog>());
```

- Pin it: host test resolving `IActivitySignaler` and `GetServices<IHostedService>()`'s
  `IdleWatchdog` from ONE provider and asserting reference equality.

## Tick cadence vs real-time acceptance gates

A service that evaluates its condition only on a fixed-period `PeriodicTimer` tick cannot
meet a real-time gate shorter than the tick. Verified arithmetic: 60 s tick + 2 s idle
timeout → shutdown fires on the NEXT minute tick, ~62 s after the last activity — a
"shuts down within ~5 s real time" acceptance criterion is unpassable as written.

- When acceptance criteria are real-time, the tick must derive from the timeout:
  `period = min(1 min, IdleTimeout / 2)` (2 s timeout → 1 s ticks → ~3 s shutdown).
- Review gate: check the tick period against the timeout in the gate, not just the
  timeout value. FakeTimeProvider unit tests hide this (they advance the clock and fire
  ticks on demand); only the real-time E2E exposes it.
