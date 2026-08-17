# 0080. The phases close against `search.total`, not the tool total

Date: 2026-08-17

Status: Accepted

Issue #382. Docs half of `docs/plans/2026-08-17-search-phase-attribution.md`; the instrumentation
(S1) and its closure gate (S2) are implemented separately and are not re-litigated here.

## Context

`SearchTimings` recorded six phases inside `SqliteMemoryStore.SearchAsync`. Measured on the live
bank on 2026-08-17 via `memory_performance` (24h window, n=12): `memory_search` averaged
287.03 ms, the six phases summed to 169.12 ms — 41% unattributed. The worst recorded search left
1,177.68 ms of 1,799.91 ms unattributed. Issue #382 asked for the missing spans plus a gate that
the phases sum to `memory_search`'s reported total.

**That gate cannot be built, and the reason is structural, not a missing span.** `memory_search`'s
reported total is a `Stopwatch` started in the `ToolExecutionActivity` constructor, from
`ToolTelemetry.RecordAsync` (`ToolTelemetry.cs:65`) around `next(request)` — the whole MCP SDK
dispatch — and read back where the invocation is recorded (`ToolExecutionActivity.cs:102,108`).
`SearchTimings`'s phases live entirely inside `SqliteMemoryStore.SearchAsync`, one call nested
inside that dispatch. Between the two sit steps no phase inside `SearchAsync` can ever reach
(`MemoryTools.cs:131-173`):

| Step | Site | Shape |
|---|---|---|
| `gate.RequireAsync` | `:131` | access-mode resolve |
| scope parse + `SearchQueryValidator.ValidateAndThrowAsync` | `:133-145` | pure, trivial |
| `queryGuard.EvaluateAsync` | `:147` | default-ON; **one** bank open — a single prefix read (`QueryGuardService.cs:36`, `GetSettingsByPrefixAsync("queryGuard.")`), not the ~2 an older trace reported |
| `qualityService.RecordSearchSafeAsync` | `:162` | a bank **write**, after the search has already returned its results |
| `RecordSearchMeasurements` | `:164` | in-memory buffer append |
| `gate.WrapAsync` + envelope | `:171-173` | envelope shaping |
| SDK argument binding and result serialization | inside `next` | outside `MemoryTools` entirely |

So the unaccounted time is **two disjoint regions**, not one:

- **Region A — inside `SearchAsync`, between the phases.** Closable: what S1/S2 close.
- **Region B — inside the tool total, outside `SearchAsync`** (the table above). Not closable by
  any change to `SearchTimings`, at any granularity, ever — no phase recorded inside `SearchAsync`
  can time work that happens before the method is entered or after it returns.

Region B carries no size here, deliberately. It is unmeasured until `search.total` exists to
measure it against; sizing it now would repeat the exact error this record corrects — an earlier
draft of the implementation plan put the query-guard cost at "~2 bank opens" from a pre-digest-gate
trace, and current code (`QueryGuardService.cs:36`) refutes it: one prefix read, one open.

## Decision

**`SearchTimings` gains a measured `Total`, recorded as `search.total`, and the phases close
against it — never against `memory_search`.** `search.total` brackets the whole of `SearchAsync`;
`search.open` and `search.embed` are what make it decomposable rather than a second unattributed
number, since without them closing against `search.total` would still leave Region A open, just
relabeled.

`search.total` is **not** added to `PhaseNames`. `PhaseNames` is declared, not reflected,
precisely so a computed value can never be silently minted as a series (ruling F11,
`SearchResults.cs:28-34`). Adding `search.total` to that list would honour the letter and break
the meaning it protects: `PhaseNames` is the decomposition, and a consumer that ever summed it
would double-count its own total. `search.total` is exposed instead through a separately-derived
`SeriesNames` (`PhaseNames` + the total name, in one place), which is what the two downstream
readers of the name list — the metrics-report series inventory and the save-time allowlist —
consume.

## Compatibility with ADR-0062

[ADR-0062](0062-a-fake-clock-advanced-before-its-timer-exists-is-lost.md)'s headline is *"Wait
for the observable, never for the clock,"* and its Consequences record *"No fixed sleep remains
in the file."* The closure gate introduces a deliberate `Task.Delay` inside a test — on its face,
exactly the pattern 0062 removed. It is not, because the sleep plays a different role in each.

0062's sleep was a **synchronisation guess**: a fixed wait standing in for "the timer has
registered," advanced against a fake clock that had not yet started ticking on anything the test
could observe, so the wait's only job was to happen to be long enough. That is what made it
fragile, and it is exactly what 0062 replaced — with a wait on an observable (timer registration,
tick count) instead of a guessed duration.

The closure gate's sleep waits for nothing and guesses at nothing. It is **the subject under
measurement**: a known delay deliberately injected into `search.embed`'s bracket through a stub,
so the test can check that the instrumentation attributes it to the phase it actually occurred in
and to no other. Nothing in the gate depends on the sleep to synchronise two racing pieces of
code — its assertions read the recorded durations, not the clock, and the real `TimeProvider.System`
runs throughout. Removing the sleep would not fix a race; it would remove the one thing the gate
exists to detect.

## Consequences

- Anyone checking `search.*` phases against a total must use `search.total`, not `memory_search` —
  the how-to and architecture docs say this explicitly, because the arithmetic silently fails
  otherwise (Region B is not zero, only unmeasured).
- Region B (the query guard, the post-search bank write, the envelope, SDK binding/serialization)
  stays uninstrumented. It is host-layer work under `mcp-thin`, needing its own decision about
  whether an MCP tool method may hold timing at all — filed as a follow-up once `search.total`
  exists to size it against, not before.
- `search.open` and `search.embed` are the first *persistent* measurement of bank-open cost inside
  a search. [ADR-0075](0075-only-the-server-writes-to-the-bank.md)'s bank-open profiling was a
  one-off `dotnet-trace` run with no live series, and its numbers predate the digest gate that
  landed in `c2fd31f0`.

## Evidence

`docs/plans/2026-08-17-search-phase-attribution.md` §1, §2.2, §2.3;
`tests/AiRaccoon.Tests/Unit/Storage/SearchPhaseClosureTests.cs`.
