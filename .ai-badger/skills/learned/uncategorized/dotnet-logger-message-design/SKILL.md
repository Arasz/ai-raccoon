---
name: dotnet-logger-message-design
description: Use when designing [LoggerMessage] log lines or log tests.
version: 1.0.0
---

# dotnet-logger-message-design

Design and test `[LoggerMessage]` logging that stays inside the high-performance invariant (nested static partial `Log`, explicit EventIds, no string interpolation at call sites) while actually delivering what the help text/docs promise.

## Trigger
- Adding a new log line to a service that uses the nested `Log` class pattern.
- A finding like "feature promises to log X but logs only counts" — designing the detail-logging fix.
- Writing tests that assert log records (EventId, level, message contents).

## Design rules

1. **`[LoggerMessage]` templates cannot format collections.** An `IReadOnlyList<string>` parameter renders as its type name. Pre-join at the call site: `string.Join(", ", reasons)` — that is a parameter build, NOT interpolation, so it satisfies the no-interpolation invariant. One small allocation, fine off the hot path.
2. **EventIds**: allocate from the class's next free id in its own range (event ids are scoped per logger category — cross-class reuse is benign, same-category collisions are not). One EventId per (message shape, level) pair; if the same shape is logged at two levels, write two `[LoggerMessage]` methods.
3. **Per-item detail logs: one structured message per item**, not one giant aggregated string — each item becomes filterable/greppable in structured sinks. Gate detail logging by mode/level when the ops line should stay counts-only; volume math decides the level (e.g. a 30-min interval loop can afford Information; a per-request loop cannot).
4. **Don't widen records to feed logs.** Before designing "log the score", check the record exposes it. Adding a field to a record an MCP tool / API serializes directly ripples into the wire contract (additive JSON, but still a contract change needing tool tests + docs). Prefer logging the existing explanatory data (e.g. a reasons list encodes the score breakdown) and the rank position.
5. **Reuse existing truncation.** If the domain layer already truncates a preview (e.g. 300 chars) for the API response, log that field as-is — don't add a second, smaller truncation constant just for logs.
6. **Promise surfaces decide implement-vs-correct-docs.** When help text / README / class doc comments all promise behavior X but the code logs counts only, count the promising surfaces: with N surfaces promising X and one missing implementation, implementing X is usually cheaper and more honest than correcting N surfaces. Re-verify every cited file:line in the finding first — findings mis-cite (a finding may cite a docs line that never promised the behavior; the promise often lives in CLI help, README, and the class doc comment instead).

## Testing log output

- **`Microsoft.Extensions.Logging.Testing.FakeLogger`** (package `Microsoft.Extensions.Logging.Testing`, NOT `Logging.Abstractions`). Non-generic `FakeLogger` implements `ILogger` directly; `FakeLogger<T>` also exists. Assert via `logger.Collector.LatestRecord` / `Collector.AllRecords`, then `record.Id.Id`, `record.Level`, `record.Message`.
- **Generic vs non-generic is a COMPILE contract, not a preference.** `FakeLogger` (non-generic) is NOT `ILogger<T>` — passing it to a ctor that takes `ILogger<MyService>` is CS1503 (`cannot convert from 'FakeLogger' to 'ILogger<MyService>'`; proven with a scratch console app against the pinned package). Use `FakeLogger<T>` for `ILogger<T>` ctors; the non-generic form only plugs into plain-`ILogger` LoggerMessage methods. Repo precedent: non-generic for `McpServerSetup.Log.HttpsTransportNotSupported(ILogger)`, generic `FakeLogger<EmbeddingAvailability>` for `ILogger<T>` ctors. A plan that says "non-generic FakeLogger implements ILogger, so it plugs straight into the ctor" of a class with an `ILogger<T>` parameter does not compile.
- **Default ctor allocates a FRESH collector per instance** — package XML: "If this is null then a fresh collector is allocated automatically." So `new FakeLogger<T>()` instances are isolated from each other and negative assertions (`ShouldNotContain(r => r.Level == LogLevel.Warning)` / on an EventId) are safe under xunit parallelism; no need to thread a custom `FakeLogCollector`.
- **`FakeLogRecord.Message` is the fully formatter-rendered template** — `[LoggerMessage]` source-gen passes the generated formatter to `ILogger.Log`, so parameter substitutions ARE applied. `record.Message.ShouldContain("#1")` on a template like `"candidate #{Rank} for {ProjectId}"` works; assertions on message contents are sound.
- Hosted-service/loop tests often default to `NullLogger` — when a test must assert log behavior, thread a `FakeLogger` through the stack helper as an overload rather than replacing the NullLogger default everywhere.
- **RED-first for a new EventId**: the failing test asserts records with the new EventId exist (level + message contents); the `[LoggerMessage]` method is the implementation that turns it green.
- **Guard-test honesty**: a "no records with EventId X in this mode" assertion passes vacuously BEFORE the feature exists — pair it with a positive assertion (e.g. the counts record IS present) so the test proves the pass ran.

## Worked example

`references/extraction-candidate-detail-logging.md` — ai-raccoon S4 plan: propose-mode ranked-candidate logging (EventId 507 design, RED test names, docs-sync findings, dependencies/risks).
