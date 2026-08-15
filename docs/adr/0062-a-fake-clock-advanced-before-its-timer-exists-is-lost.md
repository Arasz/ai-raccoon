# 0062. A fake clock advanced before its timer exists is lost

Date: 2026-08-15

Status: Accepted

## Context

WP19 half 2: find the race behind the intermittent reds, reproduce it deterministically — *by
injecting the timing rather than by looping the test* — and only then fix it.

The plan named four suspects and one hypothesis: `ToolRefusalsTests`, `BackendLauncherTests`,
`ServeRestartTests` and `IdleWatchdogTests` all fail intermittently, and the first three bind real
loopback ports through `LoopbackPort.BindWithRetryAsync`, so the shared cause was assumed to be port
contention.

**The hypothesis does not survive contact with the fourth.** `IdleWatchdogTests` binds no port at all,
and `ToolRefusalsTests` contains **zero** `Task.Delay` calls while `IdleWatchdogTests` contains
**eighteen**. These are two different defects wearing the same symptom.

This ADR is about the one that was root-caused.

## The race

Every async test in `IdleWatchdogTests` had this shape:

```csharp
var run = watchdog.StartAsync(cts.Token);
await Task.Delay(100, ...);        // timer registers
time.Advance(TimeSpan.FromHours(4));
await Task.Delay(100, ...);
lifetime.StopCalls.ShouldBe(1);
```

`StartAsync` returns **before** `ExecuteAsync` has constructed its `PeriodicTimer`. The comment
`// timer registers` names the assumption exactly, and 100 ms of real wall-clock is the guess that
backs it.

When that guess is wrong the advance is **not merely observed late — it is not observed at all**: the
timer is created *after* the clock moved, so it schedules against the new now, and the loop then waits
for a tick that a frozen fake clock will never deliver. The test hangs on its assertion and reports
the pre-advance state.

That is a far worse failure mode than a slow assertion, and it explains the signature the review
recorded: a varying subset failing per run, all of them passing in isolation, all passing on re-run.

## Reproduced by injecting the timing

Setting every `Task.Delay(100, …)` to `Task.Delay(0, …)` — one variable, no load, no looping:

```
run 1: Failed: 3, Passed: 5
run 2: Failed: 3, Passed: 5
run 3: Failed: 2, Passed: 6
```

Reliable failure, and the varying count reproduces the flake's own signature. The timing is the only
thing that changed.

## Decision

**Wait for the observable, never for the clock.**

Three pieces, all in the test file:

| | |
|---|---|
| `ObservableTime` | a `FakeTimeProvider` that counts `CreateTimer` calls, so a test can wait until the service under test has **registered its timer** before advancing |
| `TickCounter` | an `IOperationTelemetry` that counts idle checks, so a test can wait until the loop has **observed** an advance |
| `WaitUntilAsync` | polls a condition to a 15 s ceiling — the ceiling bounds a *failure*, not a pass; a healthy run exits on the first poll |

`AdvanceAsync` deliberately waits for *at least one* further check rather than an exact count:
`PeriodicTimer` does not queue missed ticks, so one advance spanning several periods may surface as a
single tick. Pinning an exact count would have made the test about `PeriodicTimer`'s coalescing rather
than about the watchdog.

The one advance that is due **no** tick is asserted directly — `ticks.Count.ShouldBe(before)` — rather
than slept through. Counting the checks turned that step from "wait and hope nothing happened" into a
claim.

**`ExecuteAsync_ExtractionPasses_DoNotResetTheWatchdog` needed the same fix twice**: two services
register timers against the one clock, and waiting for the watchdog's alone left the extraction
service's advance to be lost exactly as before.

## Consequences

- No fixed sleep remains in the file. It runs in **90 ms instead of 15 s** — a side effect, not the
  goal, but it is where 15 s of every `Speed=Fast` run was going.
- The negative assertions got stronger, not just faster. `StopCalls.ShouldBe(0)` after a fixed sleep
  could only ever **pass falsely** if the loop had not run; now the loop is proven to have looked.
- **The same latent race is in five more files** — `ExtractionHostedServiceTests`,
  `WatchIntegrationTests`, `McpServerSetupHostTests`, `ObservabilityEndpointTests`,
  `WatchHostedServiceTests` — each combining `StartAsync`, `Advance` and `Task.Delay`. None has been
  observed failing, so none is changed here; the pattern above is what to apply when one does.

## What is NOT fixed

**`ToolRefusalsTests`.** Its race is a different defect and remains open:

- It has no `Task.Delay` and no fake clock, so nothing above applies to it.
- It failed once during this work — `IngestFile_OutsideScope_ReturnsRefusal_WithoutAnSdkErrorLog`, the
  same case that reddened PR #291 — and the message was not captured.
- It then survived **three clean full-suite runs**, and a fourth run under **ten busy loops on ten
  cores**. So *CPU contention is not its trigger*, which disconfirms the remaining half of the plan's
  hypothesis.

ADR-0061 armed the diagnostic that will name its exception type on the next occurrence. Until that
occurrence, anything said about its cause would be a guess.

## The fix reintroduced the bug once, and CI caught it

`ObservableTime.CreateTimer` first incremented its counter and **then** delegated to
`base.CreateTimer`. So `StartAndArmAsync` could report "armed" while the timer was not yet in the
provider's queue — an advance racing that window is lost, which is the exact ordering bug this whole
ADR is about, reintroduced inside its own fix.

It survived **three consecutive local runs of the file and four full local `Speed=Fast` runs**, one of
them under ten busy loops on ten cores. Only CI's timing exposed it:

```
System.TimeoutException : timed out after 15s waiting for a check after advancing 00:00:01
                          (saw 0, wanted more than 0)
```

The increment now happens **after** the base call returns, when the timer really is registered. That
this matters is measured, not argued: putting the increment back in front and widening the window with
a 50 ms sleep fails **5 of 8 every run**, where the corrected order passes 8/8.

The general lesson is the one the ADR already states, aimed at itself: passing locally is not evidence
about a race, however many times it passes.

## Evidence

`tests/AiRaccoon.Tests/Unit/Setup/Serve/IdleWatchdogTests.cs`.

- **Reproduced:** delays to zero → 2-3 of 8 fail every run (above).
- **Fixed:** 8/8 pass on three consecutive runs, in 90 ms.
- **Still a check:** commenting out `_lifetime.StopApplication()` turns **5 of 8 red**, including all
  four rewritten tests — they still demand the behaviour they claim to.
- **In its habitat:** `Speed=Fast` green on four full runs, one of them under saturated CPU.
