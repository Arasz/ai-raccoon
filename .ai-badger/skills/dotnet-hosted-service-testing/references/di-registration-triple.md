# AddHostedService DI registration triple (verified .NET 10, 2026-08-06)

## The trap

`services.AddHostedService<T>()` registers ONLY `IHostedService → T` — the concrete
type is NOT resolvable from the container. Decompiled Microsoft.Extensions.Hosting
10.0.10: `TryAddEnumerable(Singleton<IHostedService, T>)`.

Symptom: a DI component that resolves `T` directly — e.g. ASP.NET middleware calling
`context.RequestServices.GetRequiredService<IdleWatchdog>()` — throws
`InvalidOperationException: No service for type 'T' has been registered` on the FIRST
request. All unit tests still pass (they construct T by hand); the E2E/first-request
path is where it explodes. Verified live on an MCP server host: the watchdog was
registered only via `AddHostedService`, the activity middleware died on the first
tools/call.

## The fix: three registrations, one instance

```csharp
services.AddSingleton<IdleWatchdog>();                                   // concrete
services.AddSingleton<IActivitySignaler>(sp => sp.GetRequiredService<IdleWatchdog>()); // interface
services.AddHostedService(sp => sp.GetRequiredService<IdleWatchdog>());  // hosted service
```

All three resolve the SAME singleton instance.

## The test that pins it

The DI-smoke test must assert BOTH shapes, or a dropped registration still passes:

```csharp
var services = provider.GetServices<IHostedService>().OfType<IdleWatchdog>().ShouldHaveSingleItem();
provider.GetRequiredService<IActivitySignaler>().ShouldBeSameAs(services);
```

Gate condition: registering via `AddHostedService<T>()` only, then resolving `T`,
must FAIL this test.
