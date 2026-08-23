# kind-time

Applies to any test whose subject reads a clock, a timer, or an interval — a scheduled job, a
debounce, a watchdog, a periodic poll. The runner is whichever fake-clock mechanism the stack
provides (`FakeTimeProvider`, Bun/Vitest fake timers, Playwright's `page.clock`); these specialise
`T1-ISO-03`'s injected-and-pinned-source rule and `T1-ORC-04`/`T1-ORC-06`'s oracle rules for the
specific ways a clock-dependent assertion goes silently wrong. Stack-specific API names (which
provider, which call) live in the stack file, not here.

**`T2-TIME-01` — Specifically exercise midnight, month/day boundaries, DST transitions, and leap years with the fake clock.**
- *design:* add a case at each boundary explicitly; a suite that only ever runs mid-month, mid-year is not exercising the boundary at all.
- *review:* a clock-dependent suite green only on ordinary days is a production bug found late.
- *check:* auto-unless-listed — does the suite contain a boundary-instant case?
- **severity:** major · **evidence:** strong · **flag:** auto-unless-listed · **parent:** `T1-ISO-03`
- *cites:* MS Learn `FakeTimeProvider` docs; Fowler's "run just before and after midnight" heuristic.
- *meta:* pass=2 order=11

**`T2-TIME-02` — Never mix a real clock and a fake one in the same test.**
- *design:* pick one source per test; if a collaborator resolves its own real clock, seam it too or the test is not actually pinned.
- *review:* a test asserting against a fake clock while a dependency still reads the real one produces a result the test cannot explain when it fails.
- *check:* auto — real clock API calls inside a test that also constructs a fake clock.
- **severity:** major · **evidence:** strong · **flag:** auto · **parent:** `T1-ISO-03`
- *meta:* pass=2 order=11

**`T2-TIME-03` — For precise business-logic/timer tests, advance fake time explicitly and deliberately, per test, rather than relying on auto-advance.**
- *design:* call the explicit advance for each step the test cares about; let the intent stay legible in the test body.
- *review:* an auto-advancing clock in a business-logic test hides which advance produced which effect.
- *check:* does the test call the fake clock's explicit `Advance(...)` per step, rather than an auto-advancing/condition-flushing clock? Evidence: read the test — auto-advance used for a business-logic/timer assertion (not UI settle) is the violation.
- **severity:** major · **evidence:** strong · **flag:** argued · **parent:** `T1-ISO-03`
- *meta:* pass=2 order=11

**`T2-TIME-04` — For UI/debounce-style dependence, prefer a clock that auto-advances realistically and flush on a condition — never a tick to an arbitrary fixed value.**
- *design:* use condition-based flushing ("wait until everything that's supposed to happen has happened") rather than computing a specific advance amount.
- *review:* this is the deliberate counterpart to `T2-TIME-03`, not a contradiction of it (ruling C16) — the two are chosen by layer, and never combined in one test (`T2-TIME-02`).
- *check:* for a UI/debounce-dependent test, does the clock auto-advance with a condition-based wait, rather than ticking to a specific computed value? Evidence: read the advance call — a hardcoded tick chosen to land exactly on the debounce boundary is the violation.
- **severity:** major · **evidence:** strong · **flag:** argued · **parent:** `T1-ISO-02`
- *cites:* Angular blog; ruling C16.
- *meta:* pass=2 order=11

**`T2-TIME-05` — Dispose timers created against a fake clock in teardown, the same as any other resource.**
- *design:* register disposal for any timer the test itself creates, in the same shared teardown as other resources.
- *review:* a leaked timer stalls every timer-driven assertion in whichever later test runs in that worker — this is `T1-ISO-05`'s shared-mutable-state finding wearing a clock's clothes.
- *check:* auto — timer creations with no matching disposal.
- **severity:** major · **evidence:** strong · **flag:** auto · **parent:** `T1-ISO-05`
- *meta:* pass=2 order=11

**`T2-TIME-06` — Positively assert that time-gated behaviour does not happen before its trigger, not only that it does happen after.**
- *design:* add the pre-trigger assertion in the same test as the post-trigger one.
- *review:* this is `T1-ORC-04`'s positive/negative pairing applied to time: absence-before is meaningless without presence-after in the same test.
- *check:* does the test assert both that the behaviour has NOT happened before the trigger, and that it DOES happen after, in the same test? Evidence: read the assertions — only a post-trigger assertion with no pre-trigger check is the violation.
- **severity:** blocker · **evidence:** strong · **flag:** auto · **parent:** `T1-ORC-04`
- *meta:* pass=4 order=18

**`T2-TIME-07` — An advance issued immediately after starting an async host or loop is silently lost; sync on the subject's own emitted signal, never on a fixed delay.**
- *design:* wait for the subject's own first-pass signal (a log event, an emitted notification) before issuing the first `Advance`.
- *review:* a poll-loop test that passes via a lost advance is fragile in both directions — it fails, despite correct behaviour, the moment the timer ever registers in time.
- *check:* argued — grep for an advance call on the line immediately after a start call.
- **severity:** blocker · **evidence:** strong · **flag:** argued · **parent:** `T1-ISO-02`
- *meta:* pass=2 order=11

**`T2-TIME-08` — Derive a poll or watchdog tick from its own timeout, so the check granularity scales with it; pin the derivation with a short-timeout test.**
- *design:* express the tick as a fraction of the timeout (e.g. a quarter), never a fixed constant independent of it.
- *review:* a fixed tick makes a short-timeout gate unsatisfiable at small scales and vacuously true at large ones — the arrange never actually reaches the timing branch under test.
- *check:* is the watchdog/poll tick a fixed constant, or derived as a fraction of the configured timeout? Evidence: read the tick's definition; pair with a short-timeout test that must observe an early check to confirm.
- **severity:** major · **evidence:** strong · **flag:** argued · **parent:** `T1-ORC-06`
- *meta:* pass=1 order=6

**`T2-TIME-09` — Pin a periodic/cyclic timer's cadence with split advances; one large advance cannot observe the tick size.**
- *design:* advance in increments smaller than the deadline until the cadence is established, then cross the deadline.
- *review:* a single advance past the deadline fires regardless of tick size, so a test written that way asserts nothing about cadence — the arrange never reaches the branch it claims to.
- *check:* does the cadence test perform one large `Advance()` past the deadline, or several smaller advances that establish the tick size first? Evidence: read the `Advance()` call sequence.
- **severity:** major · **evidence:** strong · **flag:** argued · **parent:** `T1-ORC-06`
- *meta:* pass=1 order=6

**`T2-TIME-10` — A shutdown/stop assertion follows the runner's actual chain of state transitions, not the assumption that the first stop call is terminal.**
- *design:* name the exact terminal signal the runner's own lifecycle promises, and assert that one — not an earlier signal in the same chain.
- *review:* asserting an intermediate lifecycle signal as if it were the terminal one is a defect this level, and only this level, catches.
- *check:* does the shutdown assertion check the runner's actual terminal signal, or an earlier intermediate one? Evidence: trace the lifecycle chain the runner documents and compare against what the test asserts.
- **severity:** major · **evidence:** strong · **flag:** argued · **parent:** `T1-CST-02`
- *meta:* pass=7 order=29
