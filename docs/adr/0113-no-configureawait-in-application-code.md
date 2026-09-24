# 0113 — No `ConfigureAwait` in application code

Date: 2026-09-25

Status: Accepted

## Context

About 1,570 `await`s in `src/` and `tests/` ended in `.ConfigureAwait(false)`. That is the rule
for **libraries**: a library can't know whether its caller has a `SynchronizationContext` (a
WinForms/WPF UI thread, or classic ASP.NET on .NET Framework). If the caller blocks on the task,
resuming on that context deadlocks.

AiRaccoon is an **application**, not a library. It ships as a global tool: a generic-host
console app plus an ASP.NET Core server. It publishes no NuGet package for anyone else to call.

- ASP.NET Core and generic-host console apps install no `SynchronizationContext`. Microsoft's
  CA2007 docs say that in ASP.NET Core "a `ConfigureAwait` wouldn't actually change any behavior"
  and that it is "generally appropriate to suppress the warning entirely for projects that
  represent application code". CA2007 is not enabled by default in .NET 10.
  (https://learn.microsoft.com/dotnet/fundamentals/code-analysis/quality-rules/ca2007)
- The Azure Functions isolated-worker guide makes the same point about generic-host apps:
  with `SynchronizationContext.Current == null`, `ConfigureAwait(false)` "has no practical
  effect".
  (https://learn.microsoft.com/azure/azure-functions/dotnet-isolated-process-guide#async-programming)
- Nothing in `src/` sets a `SynchronizationContext` or a custom `TaskScheduler`.
  `SingleThreadExecutor` (ADR-0110) pins MLX calls to one thread with a `BlockingCollection`
  queue. It installs no context, so an `await` never posts back to it.
- The sync-over-async calls in `src/` (`SingleThreadExecutor.Run`, the `EmbeddingService`
  settings reads) are therefore safe with or without `ConfigureAwait`, because there is no
  context for a continuation to wait on.
- The 2026-08-07 cross-cutting review had already reached this conclusion ("Missing
  `ConfigureAwait`. Generic-host console app, no `SynchronizationContext`. Not a bug."). The
  code went the other way anyway, and reviewers kept asking for the calls to be kept consistent.

.NET 8 added `ConfigureAwaitOptions` (`SuppressThrowing`, `ForceYielding`) for `Task`. Those
options change behaviour; they aren't about context capture. This decision doesn't cover them.

## Decision

Application code (`src/`, `tests/`, `benchmarks/`) awaits directly and never calls
`ConfigureAwait`. The existing calls are removed. A source-scan test,
`NoConfigureAwaitTests.ConfigureAwait_IsNotCalledInSourceOrTests`, fails if a
`.ConfigureAwait(` call shows up in `src/` or `tests/` again. CA2007 stays off.

If AiRaccoon ever ships a reusable library package, that package follows the library rule
instead, and its project is exempted from the scan.

## Consequences

- Less noise on every `await`. `await using`/`await foreach` go back to plain types
  (no `ConfiguredAsyncDisposable` or `ConfiguredCancelableAsyncEnumerable`).
- Code that adds a `SynchronizationContext` later (a UI host, an xUnit sync context that tests
  block on) takes on context capture. That code has to avoid blocking on async work. It can't rely
  on callers having written `ConfigureAwait(false)`.
- The per-`await` saving `ConfigureAwait(false)` gives with no context present (skipping one
  `SynchronizationContext.Current` / `TaskScheduler.Current` check) is negligible.
