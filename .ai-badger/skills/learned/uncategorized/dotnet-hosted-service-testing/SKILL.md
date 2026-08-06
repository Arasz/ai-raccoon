---
name: dotnet-hosted-service-testing
description: Write or review BackgroundService/FakeTimeProvider tests.
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

## Test-honesty checklist for hosted-service tests
- **Negative-only assertions are weak positives**: `store.Shared.ShouldBeEmpty()` cannot distinguish "worked, didn't share" from "did nothing at all" — a no-op `RunOnceAsync` passes. Add call counters to the fake.
- **A test name claiming two halves must assert both halves** (e.g. `..._ListsCandidates_WithoutSharing` must verify candidates were produced, not just that nothing was shared).
- **New SQL surface needs a real-DB test**: a port-contract test against a fake is an echo. Grep the method name across `Unit/storage/*` + `Integration/*`; if only the fake/port test references it, the SQL is untested (a wrong `WHERE scope=...` or ordering passes everything).
- **`AddHostedService<T>()` registration needs a DI smoke test**: if registration is dropped, the service silently never runs and every test still passes. Follow the repo's existing precedent (e.g. `WatchDependenciesSmokeTests`: `provider.GetServices<IHostedService>().OfType<T>().ShouldHaveSingleItem()`).
- **Re-pinned gates: check for vacuousness.** A gate is vacuous if it passes without exercising the claim: "outside top-10" *absence* assertions, self-comparisons, ties asserted as `>=` with no independent absolute floor. A `>= baseline - 0.001` gate is meaningful only if a >0.001 regression fails AND an absolute floor exists elsewhere. `is null or > N` violation checks (null = fail) are the honest shape.
- **Verify branch state before finalizing a review**: remote refs can disappear mid-review (branch merged + deleted). Compare the merged squash's file content against the branch head (`git show <squash>:<file>` vs `git show <head>:<file>`) — a squash may omit the branch's last commits (e.g. a "PeriodicTimer refactor" that never made it into the merged result). Report the tested state, not the branch head, and flag the divergence for the owner.

## Empirical probe pattern
When timing semantics are in doubt, run `scripts/probe-faketime.sh` (scratch console app outside the repo) — it prints thread ids and counters around `StartAsync`/`Advance` and settles inline-vs-threadpool and lost-advance questions in ~2 minutes. Details and measured outputs: `references/faketime-semantics.md`.
