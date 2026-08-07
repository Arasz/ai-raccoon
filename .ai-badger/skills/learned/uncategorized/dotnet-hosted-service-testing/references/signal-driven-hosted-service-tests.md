# Signal-driven hosted-service tests (owner-approved pattern, 2026-08-07)

Owner f: "can we use FakeTimeProvider for those tests, to not use a time based tests - or just implement some form of signaling using DotNext libs or custom TaskCompletionSource for tests"
and "use Lock for _gate -> new dotnet type for locks".

## Why: the flake that motivated it

`ExecuteAsync_IntervalReReadPerTick` (PeriodicTimer loop test) timed out 4/5 runs under load with single-`Advance` + fixed-`Task.Delay` synchronization. Root cause: the loop's `PeriodicTimer` is created AFTER the startup pass (startup
RunOnceAsync -> interval read -> constructor), so even after syncing on the startup pass's completion, a single `time.Advance(period)` can land before the timer arms (or before its first `WaitForNextTickAsync` deadline computation) and the
tick is silently lost. Load makes the window bigger (bank opens + schema ensure are real-time). The failure signature: 5s timeout in the "first tick" wait, passes in isolation, green full suites earlier.

## The TickSignal seam (custom TaskCompletionSource broadcast)

`DotNext.Threading.AsyncCounter` was inspected first: it is a COUNTING SEMAPHORE — `Increment()`
raises, `WaitAsync()` DECREMENTS and releases ONE waiter. Wrong semantics for "await until count reaches N" (no broadcast, no target). A small custom class is the right shape (~40 lines):

```csharp
internal sealed class TickSignal
{
    private readonly Lock _gate = new();              // System.Threading.Lock (owner f:)
    private readonly List<(long Target, TaskCompletionSource<bool> Completion)> _waiters = [];
    private long _count;

    public long Count { get { lock (_gate) { return _count; } } }

    public void Increment()
    {
        List<TaskCompletionSource<bool>> ready;
        lock (_gate)
        {
            _count++;
            ready = _waiters.Where(w => w.Target <= _count).Select(w => w.Completion).ToList();
            _waiters.RemoveAll(w => w.Target <= _count);
        }
        foreach (var completion in ready) completion.TrySetResult(true);
    }

    /// <summary>True when the count reached the target; false on timeout or cancellation.</summary>
    public async Task<bool> WaitAsync(long target, TimeSpan timeout, CancellationToken cancellationToken)
    {
        TaskCompletionSource<bool> completion;
        lock (_gate)
        {
            if (_count >= target) return true;
            completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _waiters.Add((target, completion));
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);
        await using var registration = cts.Token.Register(() => completion.TrySetResult(false));
        return await completion.Task;
    }
}
```

## Service seams (test-only surface in the production file)

```csharp
internal TickSignal Ticks { get; } = new();          // after EVERY pass (startup + each tick)
internal TickSignal TimerArmed { get; } = new();     // right after new PeriodicTimer(...)
internal TickSignal IntervalReReads { get; } = new(); // after each post-tick interval re-read
```

Increment `Ticks` in a `finally` around each `RunOnceAsync` (startup pass and loop body) so a failed pass still signals. `TimerArmed` goes immediately after the timer constructor (before the while loop).

## Test mechanics (no polling sleeps)

- Sync on the signal: `(await service.Ticks.WaitAsync(1, 5s, ct)).ShouldBeTrue();` — the startup pass.
- Advance-in-a-loop until the signal fires (robust against the arm race regardless of whether PeriodicTimer computes deadlines at construction or first await):
  ```csharp
  private async Task AdvanceUntilTicksAsync(long target, TimeSpan step)
  {
      var deadline = DateTime.UtcNow + SignalTimeout;
      while (DateTime.UtcNow < deadline)
      {
          _time.Advance(step);
          if (await _service.Ticks.WaitAsync(target, AbsenceWindow(150ms), ct)) return;
      }
      throw new TimeoutException($"Tick {target} did not fire within {SignalTimeout}");
  }
  ```
- Absence assertions ("no tick happened") are inherently bounded: use
  `(await _service.Ticks.WaitAsync(expected + 1, AbsenceWindow(150ms), ct)).ShouldBeFalse()` — the window is a wait-for-signal that returns early when signaled, not a sleep.
- The FakeTimeProvider still drives the clock (`_time.Advance`); only the synchronization is signal-based.
- Re-read semantics tests: sync on the tick signal, then on `IntervalReReads` (the seam) BEFORE changing the setting, so the period is known to be the old value; change; advance-until next tick; sync re-read #2; then the absence check
  proves the new period took effect.

## WAL-mode busy semantics (for "contended VACUUM/checkpoint" tests)

- WAL mode: a READ transaction does NOT block VACUUM (writers and readers coexist) — only the single WRITE lock does. Hold `BEGIN IMMEDIATE` on another connection to make VACUUM fail with SQLITE_BUSY;
  `BEGIN` + `SELECT` does nothing (first version of the test premise was wrong).
- `PRAGMA wal_checkpoint(TRUNCATE)` returns a `busy|log|checkpointed` ROW: read via `ExecuteReader`,
  `GetInt32(0)` = busy frames. `ExecuteNonQueryAsync` discards it.
- `busy_timeout` is per-connection and survives pool return: a connection that lowers it (250 ms for fast defer tests) must restore the factory value (5000) in a `finally` before disposal, or the next pooled borrower inherits the short
  timeout.

## Measured result

8/8 consecutive green runs, ~400-450 ms per run (vs 570-760 ms with polling) — faster AND stable; the previously-flaky test became deterministic.
