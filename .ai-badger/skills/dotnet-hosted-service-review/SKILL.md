---
name: dotnet-hosted-service-review
description: "Use when reviewing a PR that adds or modifies a .NET BackgroundService/IHostedService — background extraction loops, watchers, sync, sweep, or any poll loop. Checklist: ExecuteAsync try/catch coverage (StopHost kills the process), cancellation filtering, PeriodicTimer semantics, store-level idempotency vs TOCTOU, settings-channel parsing, LoggerMessage invariants. Produces numbered findings + severity + file:line."
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [dotnet, hosted-services, review, backgroundservice]
    related_skills: [dotnet-hosted-service-testing, code-review-checklist]
---

# dotnet-hosted-service-review

Reviewing a PR that adds or modifies a .NET `BackgroundService` / `IHostedService` (poll loops: background extraction, watchers, sync, sweep). Focus: robustness, cancellation, idempotency, registration, and logging invariants. Produces numbered findings with severity + file:line evidence and a claims-vs-code verdict.

## Checklist

1. **ExecuteAsync try/catch coverage** — every await except the cancellation wait must be inside a try/catch (or have its own). The classic miss: the interval re-read / config reload AFTER the run body (`timer.Period = await ReadIntervalAsync(...)`) and the startup config read sit outside the try. An unhandled non-OCE exception there faults ExecuteAsync → default `BackgroundServiceExceptionBehavior.StopHost` stops the WHOLE host, contradicting any "best-effort" claim in the class doc. As of .NET 11 Preview 3 the cost is louder: `RunAsync`/`StopAsync`/`WaitForShutdownAsync` (and their sync forms) now fault instead of completing, so the process exits non-zero — a single failure rethrows, several combine into an `AggregateException` ([Microsoft Learn](https://learn.microsoft.com/dotnet/core/compatibility/extensions/11/ihost-runasync-stopasync-throw-backgroundservice-failure)). Evidence: read the loop body and check what is outside the try block, not just that a try exists.

2. **Compare with the sibling hosted service in the same repo** — same-repo precedent is the strongest review signal. If an existing service try/catches both its work body AND its delay, a new service that guards less is a finding. Cite the divergence explicitly, with file:line for both sides.

3. **Cancellation handling in per-item catches** — `catch (Exception ex)` per project/item swallows `OperationCanceledException`: on shutdown each in-flight item logs a spurious Warning and the top-level `catch (OCE) when (IsCancellationRequested) break` never fires from the run body. Inner catches should filter OCE (`when (!cancellationToken.IsCancellationRequested)`) or rethrow.

4. **Timer semantics** — `PeriodicTimer` first tick = one full interval: "enable" produces no work until the first tick (UX surprise; check for a run-now verb or a doc note). Re-reading the interval after each run is correct (config change without restart) but must be inside the try (item 1). `timer.Period` has a setter — mutation is fine.

5. **Idempotency claims vs store semantics** — "idempotent" claims must be checked at the store layer: SELECT-then-INSERT dedup (path-keyed) with NO unique constraint on the table = TOCTOU across processes. `PRAGMA busy_timeout` serializes commits, not read windows. Multi-process topology: each stdio MCP client spawns its own server process → N background loops over the same store when the feature is enabled. If the same exposure pre-exists on the synchronous tool path, rate it SHOULD-FIX (low), not MUST-FIX, and offer a partial UNIQUE index as the fix.

6. **Settings-channel semantics** — strict parsers (`value == "true"`), null/missing key → safe default (verify `GetSettingAsync` returns null for unset keys), unknown values → fail-safe mode. Grep `SetSettingAsync` callers to confirm the CLI is the only writer of the new keys. Case-sensitive parsing with fail-safe fallback is a NIT, not a bug.

7. **Logging invariant** — nested static partial `Log`, `[LoggerMessage]`, explicit EventIds. Check collisions against whatever per-class EventId ranges the repo already reserves — read them out of the existing `Log` classes rather than assuming a numbering. EventIds are scoped per logger category, so cross-class reuse is benign — note only same-category collisions.

8. **Safety claims → claims-vs-code table** — turn every PR-body claim into a verdict row with file:line evidence (off-by-default / never-mutates / propose-never-shares / explicit-gate-warns / idempotent / no-delete-path). Grep for the forbidden operation (e.g. `Delete*` calls) to prove "no delete path" rather than trusting the diff. State verdict per claim: HOLDS / HOLDS-with-caveat / FAILS.

9. **Test fidelity for loops** — fakes that don't model store idempotency (fake ShareAsync appends unconditionally, shared index never updates after a share) make loop tests pass even if the loop re-does work every tick; the real cross-run idempotency (index re-read + store dedup) then has no test. Flag as NIT with the fix: mutate the fake's index on share.

## Worked example

When reviewing, keep the repo's own past review records handy: finding shapes, evidence
lines and the safety-claim table are easier to match than to invent, and citing a
divergence from precedent lands better than citing a preference.

## Gotchas
- Don't rate a pre-existing TOCTOU as MUST-FIX when the identical pattern already exists on the synchronous tool path — SHOULD-FIX (low) + owner question is the honest severity.
- `BackgroundServiceExceptionBehavior.StopHost` is the .NET default — verify the host config before claiming a faulted ExecuteAsync is contained. On .NET 11+ the host also surfaces the failure through `RunAsync`/`StopAsync`/`WaitForShutdownAsync`, so a faulted service turns a zero exit code into a non-zero one; a `try`/`catch` around the `await host.RunAsync()` is what restores the old silent exit.
- One host per process (the startup path picks a stdio host OR a web host, not both) means `AddHostedService` runs once per process — the multi-loop question is about processes sharing one store, not hosts within a process.
- Access-mode / auth guards usually gate only the MCP tool layer — a background service calling the store directly bypasses them. Raise as an owner question (intent), not an automatic finding.
