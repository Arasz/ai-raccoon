# FakeTimeProvider + BackgroundService measured semantics (.NET 10, Microsoft.Extensions.Time.Testing 10.x)

Measured 2026-08-06 while reviewing a hosted-service PR in ai-raccoon. The probe program
(`scripts/probe-faketime.sh`) produced this transcript:

```
after StartAsync: count=0 thread=1        <- StartAsync returned BEFORE ExecuteAsync body ran
ExecuteAsync start thread=4               <- loop body dispatched to a threadpool thread
about to RunOnce count=0 thread=4
RunOnce body count=1 thread=4             <- first pass on threadpool thread
after RunOnce count=1 thread=4
after Advance#1: count=1 thread=1         <- Advance returned; delay continuation NOT yet run
delay elapsed count=1 thread=4            <- continuation landed asynchronously on threadpool
about to RunOnce count=1 thread=4
RunOnce body count=2 thread=4
after Advance#2: count=2 thread=4
```

## What the transcript proves

1. `BackgroundService.StartAsync` does NOT invoke `ExecuteAsync` synchronously on the caller
   thread (in .NET 10). The caller sees `count=0` right after `StartAsync`; the loop starts on a
   threadpool thread. Any `time.Advance()` issued before the loop registers its
   `Task.Delay(…, timeProvider, …)` timer is silently lost (the timer is created later, due
   `now+interval` at creation time, so the earlier advance doesn't count).
2. `FakeTimeProvider.Advance` fires due timer callbacks inline on the calling thread, but the
   `Task.Delay` continuation is scheduled to the threadpool (`RunContinuationsAsynchronously` —
   verified separately: a hand-made TCS with that flag completed on a threadpool thread while
   the callback itself ran on the Advance thread). So a test must sleep in real time after
   advancing before asserting side effects.
3. With fully-synchronous fakes (`Task.FromResult` for every store call), a `RunOnceAsync` pass
   completes synchronously inside `ExecuteAsync`; the only real yield is the interval delay.

## Why a "count == 1 after first advance" assertion is a no-op

In a run-then-delay loop the first pass already ran (count=1) before the first advance, and the
first advance is reliably lost (threadpool dispatch latency > the test thread's path from
`StartAsync` to `Advance`). So `count.ShouldBe(1)` passes trivially; only the second advance's
assertion exercises the interval. Worse, the test FAILS if the first advance ever lands (count
reaches 2 within the real-time wait) — an inverted microsecond race. Fix: count invocations
of the pass seam instead of side effects, and treat the first assertion as documentation.

## Fake-artifact trap

If the fake store's shared index is a plain property that `ShareAsync` never updates, pass 2 of
the loop re-promotes the same row — a `count == 2` expectation that can never occur in
production (a real store's shared index includes pass-1 promotions, so pass 2 dedups). The
loop-iteration proof should use an invocation counter, and dedup should be tested separately
with a pre-populated index.

## Related .NET facts used in the same review

- `Task.Delay(interval, timeProvider, token)` registers its timer synchronously inside the call
  (timer exists before the returned Task is awaited), but only when the loop reaches that line.
- A wait-first `PeriodicTimer` loop (`timer = new PeriodicTimer(period, timeProvider)`) changes
  first-run semantics (first pass after the first tick) — tests written for a run-first
  `Task.Delay` loop assert different counts than tests for a wait-first loop.
- When reviewing a merged squash vs a branch head: `git show <squash>:<path>` vs
  `git show <head>:<path>` reveals whether the squash dropped the branch's last commits
  (observed: a "PeriodicTimer refactor" commit existed on the branch but was absent from the
  merged content; the suite had tested the older loop shape).
