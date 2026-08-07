# DI host pitfalls that only a live boot (or E2E) catches

Verified 2026-08-06 on the project (net10.0, ModelContextProtocol 2.1.0): a refactor converted a static helper into a DI service and the server stopped booting. Three registration truths the unit tests could not see — the E2E factory
(WebApplicationFactory booting the REAL Program) and a live `dotnet run` smoke are the only layers that exercise Program-top-level DI.

## 1. `IHttpClientFactory` needs an explicit `AddHttpClient()` registration

`services.AddSingleton<IBundledModel, BundledModel>()` where `BundledModel` takes
`IHttpClientFactory` fails at FIRST RESOLUTION with:

```
Unable to resolve service for type 'System.Net.Http.IHttpClientFactory' while attempting
to activate 'the project.Infrastructure.Embedding.BundledModel'.
```

Adding the `Microsoft.Extensions.Http` PACKAGE reference is not enough — the package only ships the extension methods; nothing registers the factory until
`services.AddHttpClient()` (plain, no generic) is called. The failure appears on the first
`app.Services.GetRequiredService<...>()`/`Build()` that resolves the consumer — typically server startup, never at compile time. Pin the registration with a DI smoke test:
`provider.GetRequiredService<IHttpClientFactory>().ShouldNotBeNull()` after
`RegisterServices`.

## 2. The non-generic `ILogger` is NOT registered by default hosts

`Host.CreateApplicationBuilder()` and `WebApplication.CreateBuilder([])` register
`ILoggerFactory` + `ILogger<T>` (open generic) — but NOT the non-generic `ILogger`.
`app.Services.GetRequiredService<ILogger>()` throws at startup:

```
No service for type 'Microsoft.Extensions.Logging.ILogger' has been registered.
```

This is a compile-safe mistake: nothing complains until the process boots, and unit tests never run Program's top-level statements. Resolve the factory and name the category:

```csharp
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Program");
```

(Note: `CreateLogger<T>()` is also illegal for a STATIC class — use a category string there too.)

## 3. System.CommandLine `GetValueOrDefault<T>()` THROWS on invalid values

`parseResult.GetResult("--transport") is OptionResult { Tokens.Count: > 0 } opt
? opt.GetValueOrDefault<McpTransport>() : default` throws `InvalidOperationException`
("Cannot parse argument 'ftp' ...") when the token's value does not convert — even when the parse result ALREADY carries the error in `parseResult.Errors`. A parse-first facade that reads options on every call (including failed parses) must
wrap the reads in try/catch and fall back to defaults: the contract is "errors surface via the Errors list, never as an exception out of the parser". This bit after a refactor moved `ReadOptions`
from an errors-only path to always-run.

## 4. FakeLogger: `LatestRecord` throws on an empty collector

`Microsoft.Extensions.Logging.Testing.FakeLogCollector.LatestRecord` throws
`InvalidOperationException("No records logged")` when nothing was logged — you cannot assert "nothing was logged" with `LatestRecord.ShouldBeNull()`. Use
`logger.Collector.Count.ShouldBe(0)`.

## Diagnosis path that found all three

1. Full suite: 264 failures, all E2E/boot classes → one root cause each round.
2. `dotnet run --project src/X -- --transport http --port 0` (background, then poll the log) — the live boot shows the REAL first failure; the E2E factory stack trace pointed at Program.cs's `GetRequiredService` lines directly.
3. Fix one root cause → re-run the boot + E2E filter → next root cause surfaced (the errors cascade: IHttpClientFactory masked ILogger, which masked nothing further but the CLI facade threw next).
