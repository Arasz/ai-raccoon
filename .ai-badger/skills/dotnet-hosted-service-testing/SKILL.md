---
name: dotnet-hosted-service-testing
description: "Use when writing or reviewing .NET BackgroundService tests with FakeTimeProvider/TimeProvider: lost-first-Advance semantics, inline-vs-threadpool timer callbacks, poll-loop test honesty (invocation counters, not side-effect counts), tick derivation from timeouts, DI registration smoke tests, vacuous-gate detection. Verified on .NET 10; includes an empirical probe script."
description: Write or review BackgroundService/FakeTimeProvider tests.
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [dotnet, testing, faketime, hosted-services]
    related_skills: [dotnet-hosted-service-review, dotnet-logger-message-design]
---

# dotnet-hosted-service-testing

## Trigger
- Writing tests for a `BackgroundService` (ExecuteAsync loop, interval polling, periodic jobs).
- Reviewing tests that use `FakeTimeProvider` / `TimeProvider` (e.g. `Microsoft.Extensions.Time.Testing`).
- Judging whether hosted-service tests honestly cover the loop, interval, dedup, error-tolerance, and DI-registration paths.

## FakeTimeProvider + BackgroundService semantics (empirically verified on .NET 10)
1. **`StartAsync` does NOT run `ExecuteAsync` on the caller thread.** The loop body starts on a threadpool thread; `StartAsync` returns before the first pass runs. Consequence: `time.Advance(...)` immediately after `StartAsync` is reliably **LOST** — the `Task.Delay`/`PeriodicTimer` timer isn't registered yet. The first advance silently does nothing.
2. **Timer callbacks fire inline on the `Advance` caller thread**, but the `Task.Delay` continuation is queued to the threadpool (`RunContinuationsAsynchronously`). After each `Advance`, give the continuation real time to land (`await Task.Delay(100, TestContext.Current.CancellationToken)`), then assert.
3. **`Task.Delay(interval, timeProvider, token)` registers its timer synchronously inside the call** — but only when the loop reaches it. With fully-synchronous fakes (`Task.FromResult` everywhere), the first pass completes synchronously inside `ExecuteAsync`; the first real yield is the delay.
4. **A poll-loop test that passes via a lost first advance is fragile in both directions**: it passes when the advance is lost, and FAILS (despite correct behavior) if the timer ever registers in time. Assert on an **invocation counter in the fake** (e.g. RunOnceAsync calls), not on side-effect counts.
5. **Side-effect counts encode fake artifacts.** If the fake's shared index is never updated by `ShareAsync`, a second loop pass re-shares — expecting `count == 2` documents fake behavior that can never happen in production (a real store would dedup the second pass). Test loop-iteration count; test dedup separately with a pre-populated index.
6. A `RunOnceAsync` internal seam (called by `ExecuteAsync`, directly testable) is the right shape — test the pass logic through the seam, and the interval through the loop test.
7. **PeriodicTimer observables (verified .NET 10):** the fake fires a periodic timer TWICE per `Advance` when two schedule points fall inside the window, and the loop's check runs asynchronously at the POST-advance clock — never at the tick's due time. A single large Advance past the deadline fires immediately regardless of tick size, so the tick period is unobservable via one big advance. Pin a tick period by split advances: keep the clock below the deadline until the cadence is established, then cross (recipe + numbers: `references/watchdog-e2e-tick-cadence-and-di-signaling.md`).
8. **`StopApplication` on a WebApplication host does NOT auto-stop the host (verified):** it cancels `ApplicationStopping` and makes `WaitForShutdownAsync` return (the runner contract); `ApplicationStopped` fires only after the runner calls `host.StopAsync()`/dispose. For "watchdog shut the host down" assertions, assert the runner-shaped chain — `ApplicationStopping` token, then `WaitForShutdownAsync` completes, then `StopAsync` — not `ApplicationStopped` directly.
9. Timing-sensitive fake-clock tests: re-run under the full-suite filter (parallel load can starve the 100ms settle) and REBUILD after test edits before judging flakiness — `--no-build` against a stale assembly reports phantom failures.
10. **Watchdog-style deadline services: derive the poll tick from the timeout** (`tick = min(60s, timeout/4)`), not a fixed 60s — a fixed tick makes short-timeout gates unsatisfiable (a 2s-timeout host shuts down up to 62s late, so a "within ~5s" assertion can never pass) AND makes early-shutdown assertions vacuously true. The deadline-check granularity must be a fraction of the timeout at every scale; pin the derivation with a short-timeout test.
11. **Sync on the service's own log emissions, not wall-clock delays.** When the loop's first action emits a `[LoggerMessage]` event (e.g. a startup-pass checkpoint), poll the `FakeLogger` collector snapshot instead of `Task.Delay(50)`
    before probing and `Task.Delay(100)` before asserting: `WaitForLogAsync(eventId, count)` — poll every ~10 ms up to a 5 s timeout, throw `TimeoutException` naming the expected event/count. Fixed delays race BOTH directions: a slow box
    misses the startup pass (the probe write lands before it, then the startup truncation breaks the assertion), and a fast box asserts before the tick's continuation lands. The service's own log emission is the deterministic sync point —
    the startup event doubles as the "timer registered" guarantee. (Applied 2026-08-07 to bank-maintenance lifecycle tests after review flagged the fixed-delay flake risk; the polling rewrite ran 4x stable.)

## Test-honesty checklist for hosted-service tests
- **Negative-only assertions are weak positives**: `store.Shared.ShouldBeEmpty()` cannot distinguish "worked, didn't share" from "did nothing at all" — a no-op `RunOnceAsync` passes. Add call counters to the fake.
- **A test name claiming two halves must assert both halves** (e.g. `..._ListsCandidates_WithoutSharing` must verify candidates were produced, not just that nothing was shared).
- **New SQL surface needs a real-DB test**: a port-contract test against a fake is an echo. Grep the method name across `Unit/storage/*` + `Integration/*`; if only the fake/port test references it, the SQL is untested (a wrong `WHERE scope=...` or ordering passes everything).
- **`AddHostedService<T>()` registration needs a DI smoke test**: if registration is dropped, the service silently never runs and every test still passes. Follow the repo's existing precedent (e.g. `WatchDependenciesSmokeTests`: `provider.GetServices<IHostedService>().OfType<T>().ShouldHaveSingleItem()`).
- **Re-pinned gates: check for vacuousness.** A gate is vacuous if it passes without exercising the claim: "outside top-10" *absence* assertions, self-comparisons, ties asserted as `>=` with no independent absolute floor. A `>= baseline - 0.001` gate is meaningful only if a >0.001 regression fails AND an absolute floor exists elsewhere. `is null or > N` violation checks (null = fail) are the honest shape.
- **Verify branch state before finalizing a review**: remote refs can disappear mid-review (branch merged + deleted). Compare the merged squash's file content against the branch head (`git show <squash>:<file>` vs `git show <head>:<file>`) — a squash may omit the branch's last commits (e.g. a "PeriodicTimer refactor" that never made it into the merged result). Report the tested state, not the branch head, and flag the divergence for the owner.

## Empirical probe pattern
When timing semantics are in doubt, run `scripts/probe-faketime.sh` (scratch console app outside the repo) — it prints thread ids and counters around `StartAsync`/`Advance` and settles inline-vs-threadpool and lost-advance questions in ~2 minutes. Details and measured outputs: `references/faketime-semantics.md`.

## Gotchas

- FakeTimeProvider's Advance is lost-first: the first elapsed timer after Advance fires with the pre-advance time.
- Count invocations, not side effects: a poll-loop test that counts log lines can pass while the loop body never ran.
## References

- `references/faketime-semantics.md` — measured FakeTimeProvider semantics (lost-first Advance, inline-vs-threadpool); read when asserting FakeTimeProvider advance semantics.
- `references/di-registration-triple.md` — DI registration smoke tests: the three registrations a hosted service needs (service, options, hosted) and how to pin them; read when smoke-testing DI registrations.
- `references/real-time-smoke-deadline-trap.md` — the latent flake mode in real-time smoke tests for deadline services (idle watchdog case); read when a real-time smoke test flakes.
- `references/watchdog-e2e-tick-cadence-and-di-signaling.md` — PeriodicTimer tick cadence pinning + DI signaling in watchdog E2E tests; read when pinning tick cadence.
