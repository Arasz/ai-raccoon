# review-tests — `rechunk-instrumentation` (re-chunk observability tests)

- **Task:** `air-254-vs-1022-retrieval-measurement-809` (issue #809), step `rechunk-instrumentation`
- **Date:** 2026-09-30
- **Verdict:** PASS-WITH-FINDINGS — **0 blocker, 3 major, 2 minor**

## Scope

The exact test surface the step changed (uncommitted working tree; no git command was run per
this worktree's house rules — the diff was read with `diff -u` against the main checkout at
`/Users/arasz/RiderProjects/ai-raccoon`):

| target | file:line | change |
|---|---|---|
| `Rebudget_PhaseElapsed_MatchesTheScriptedClock` | `tests/AiRaccoon.Tests/Integration/Storage/ChunkRebudgetTests.cs:478` | new |
| `Rebudget_Event448_ReadsEveryReportCount` | `ChunkRebudgetTests.cs:496` | updated |
| `RechunkFinished_Records305500msAndGroups` | `tests/AiRaccoon.Tests/Unit/Embedding/EmbedDrainReporterTests.cs:85` | new |
| `MigrationDrain_RecordsRechunkMeasurements_AfterDrain` | `tests/AiRaccoon.Tests/Integration/Embedding/ModelMigrationRechunksAndReembedsTests.cs:131` | new |
| `InternalSeriesPrefixes_CoversJobDrainWriteAndSearchQueryAndSearchFusion` | `tests/AiRaccoon.Tests/Unit/Metrics/MetricsConfigKeysTests.cs:94` | list updated |
| `SqliteMemoryStoreSearchTimingsTests` | `tests/AiRaccoon.Tests/Unit/Storage/SqliteMemoryStoreSearchTimingsTests.cs` | nested provider removed; uses the extracted one |
| `ScriptedTimeProvider` (extracted + extended) | `tests/AiRaccoon.Tests/ScriptedTimeProvider.cs` | new file: `ForSpan`, cursor-derived `GetUtcNow` |
| `TestData.CreateEntryEmbedder` | `tests/AiRaccoon.Tests/TestData.cs:108` | new optional `measurements` parameter |

Code under test: `ChunkBudgetReconciler` (the phase bracket at `:114` and ` in {Elapsed}` in the
event-448 template at `:404-407`), `EmbedDrainReporter.RechunkFinished`
(`src/AiRaccoon.Infrastructure/Embedding/EmbedDrainReporter.cs:66`), `EntryEmbedder`'s call site
(`:264`), `MetricsConfigKeys` (`chunk.rechunk.*` prefix).

Judged against the step's own statement of done (task-graph step `rechunk-instrumentation`):
**ac1** event 448 carries `Elapsed` via `ChunkRebudgetReport.Elapsed`; **ac2**
`chunk.rechunk.duration_ms` / `.groups` recorded through the reporter with the pinned prefix;
**ac3** the groups metric equals both the reconciler's counters and the fixture's known count
(PR-2.R12). The step's instruction also names this test's intended kill: *"Kills a stopwatch
starting after the scans or stopping before final repairs."*

**Out of scope, per dispatch:** the implementer's RED output (7 failed / 42 passed incl. 2
environmental) and the 3 reported mutation kills — accepted as reported at dispatch time, not
re-verified here. Environmental disclosure: gitignored `model_fp16.onnx_data` was restored from
the main checkout, sha matching the manifest; with that file present the two "environmental"
timings failures did not reproduce in the review copy (`SqliteMemoryStoreSearchTimingsTests`
4/4 green).

## Method and environment

- Runner: `dotnet test tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj -c Debug --filter
  "FullyQualifiedName~<class>"` (.NET 10.0.401, osx-arm64).
- Every mutation was applied to a clonefile copy of the worktree at
  `/tmp/qa-rechunk-review/repo`, rebuilt, run, and reverted **there**. Final `diff -q`
  copy-vs-worktree is identical for all three mutated production files; the worktree was never
  modified.
- Baselines in the copy, all green: `ChunkRebudgetTests` 15/15, `ModelMigrationRechunksAndReembedsTests`
  2/2, `EmbedDrainReporterTests` 4/4, `MetricsConfigKeysTests` 24/24,
  `SqliteMemoryStoreSearchTimingsTests` 4/4.
- Pass 0 (gate): `commands.test` is bare `dotnet test` (no filter), so these classes sit in the
  default sweep; CI runs them through the `Speed=Fast` (`build.yml:159`) and `Speed=Slow`
  (`build.yml:314`) lanes via their existing traits. No new exclusion, no hand-typed list changed.
- Base-clock ground truth for Q4: a scratch program (`/tmp/tp-check`) with a bare `TimeProvider`
  subclass overriding only `GetTimestamp` returned the **real** clock
  (`base GetUtcNow = 2026-09-30T09:15:45.6599870+00:00`, inside the run window) — so the extracted
  provider's override is a real change from the old nested class, not decoration.

## What the tests already prove (positive controls — not findings)

| probe | mutation (applied to /tmp copy, reverted) | result |
|---|---|---|
| P1 | `EmbedDrainReporter.cs:70` — `elapsed.TotalMilliseconds` → `elapsed.TotalSeconds` | **RED**: `duration.Value should be 305500d but was 305.5d` at `EmbedDrainReporterTests.cs:94`; 1 failed / 3 passed |
| P2 | `ChunkBudgetReconciler.cs:405` — `terminal in {Elapsed}` → `terminal after {Elapsed}` | **RED**: 2 failed (`Rebudget_PhaseElapsed...:491`, `Rebudget_Event448...:505`), 13 passed |
| P3 | `EntryEmbedder.cs:264` — caller forces the groups count to 2 | **RED**: `groups.Value should be 1d but was 2d` at `ModelMigrationRechunksAndReembedsTests.cs:162`; 1 failed / 1 passed |

P1 shows the ms/s conversion pin bites; P2 shows the elapsed genuinely flows into the event;
P3 shows the groups oracle is a real expected value, not a mirror — the literal `1` reddens the
metric when the SUT's count moves.

## The five questions

**1. Is the groups assertion an independent oracle (PR-2.R12)? — Yes.**
`const int knownRechunkedGroups = 1;` (`ModelMigrationRechunksAndReembedsTests.cs:133`) is a
hand-written constant; the fixture is one over-budget note with no file rows, so `1` is known by
construction and is never derived through the reconciler. It is asserted directly against the
metric (`:162`) and the reconciler's own counters are separately pinned by the literal 448
substring (`:149-150`). P3 confirms the oracle is not decorative. PR-2.R12 satisfied.

**2. Is the 305500 value pin conversion-fragile? — No.**
`TimeSpan.FromSeconds(305.5)` is 305,500,000 ticks exactly, so `TotalMilliseconds` is exactly
305500. The fractional seconds are the discriminator: an `s` swap yields 305.5, a microsecond
swap 3.055 × 10⁸ — orders of magnitude outside any double tolerance. P1 reds with
`should be 305500d but was 305.5d`. The assertion message (`"TotalMilliseconds, never
TotalSeconds"`) documents the intent. No finding.

**3. The disclosed bracket-placement limitation — real, and it defeats a named kill.**
`ScriptedTimeProvider.ForSpan(span)` is `[span, 0]`: the first `GetTimestamp()` returns 0 and
advances the cursor by `span`; the second returns `span`. `RunAsync` has exactly two
`GetTimestamp` consumers — `startedAt` (`ChunkBudgetReconciler.cs:114`) and
`GetElapsedTime(startedAt)` (`:272`) — and `GetUtcNow` never advances the cursor
(`ScriptedTimeProvider.cs:44`). Moving `startedAt` to after `scanner.BudgetAsync` leaves the
two-call sequence unchanged, so the elapsed is still 305.5 s and the suite stays green (see F3). The test's comment — *"an elapsed measured anywhere but the real bracket reads the wrong
number"* (`ChunkRebudgetTests.cs:483-484`) — is therefore false for any two-call bracket, and the
step's intended kill (*"a stopwatch starting after the scans"*) is not delivered by this harness.
Graded **major** (F3): the *value* path (clock injected, carried to report and event) is genuinely
pinned; the *span* is not, so the operator-facing duration can silently exclude part of the phase.

**4. Does `ScriptedTimeProvider.ForSpan`'s cursor-derived `GetUtcNow` stay honest (PR-3.R10)? — Yes for everything asserted; one documented residual.**
PR-3.R10 asks for cursor-derived rather than a fixed epoch, and that is what
`ScriptedTimeProvider.cs:44` does. The ground-truth check above shows the old nested class (no
override) fed real wall time into store stamps; the extracted one is monotonic in the scripted
cursor and consistent with `GetElapsedTime` (now = origin + cursor, elapsed = cursor delta /
frequency). The phase tests put only the reconciler on the scripted provider while the store keeps
`FakeTimeProvider` (`ChunkRebudgetTests.cs:56-64`), exactly as the rule states, and
`SqliteMemoryStoreSearchTimingsTests` — which runs the store itself on the scripted provider —
stays 4/4 green. Residual, not counted: `GetUtcNow` does not advance the cursor, so successive
reads return the same instant between `GetTimestamp` calls; no current test depends on them
differing (a future test gating on two successive `GetUtcNow` reads, e.g. the drain's 1013
progress loop, would never fire under this double). The class doc already owns that trade-off.

**5. Coverage gaps vs the step goal.** Covered: 448 elapsed reaches the report and the message
(P2); the two series are recorded with kind/unit/project (reporter unit test + migration drain
test); the prefix is discoverable (`InternalSeriesPrefixes` list + `MetricsReportService`'s derived
`LIKE` query); the duration conversion (P1) and the groups value against the independent oracle
(P3). Gaps: F1 (event counts), F2 (call-site metric values), F3 (bracket span); F4/F5 are
hygiene.

## Findings

| id | file:line | rule | severity | the mutation | run? | what it means |
|---|---|---|---|---|---|---|
| F1 | `src/AiRaccoon.Infrastructure/Ingestion/ChunkBudgetReconciler.cs:277-278` + `tests/AiRaccoon.Tests/Integration/Storage/ChunkRebudgetTests.cs:496-512` | `T1-SCO-02` (enabled by `T1-STR-02`) | major | swap `report.RetryableSkipped` / `report.UnprovableSkipped` in the `Log.RebudgetCompleted` call | applied+reverted on /tmp copy — **survived** (ChunkRebudgetTests 15/15 green) | the event-448 line an operator reads to tell a retrying migration from a terminal one can mislabel the two skip counts and every test stays green: every 448 fixture has all four skip counts at 0 |
| F2 | `src/AiRaccoon.Infrastructure/Embedding/EntryEmbedder.cs:264` + `tests/AiRaccoon.Tests/Integration/Embedding/ModelMigrationRechunksAndReembedsTests.cs:153-163` | `T1-ORC-03` | major | (a) `rechunk.Elapsed` → `TimeSpan.Zero`; (b) drop `+ rechunk.MirrorGroupsRechunked` | applied+reverted on /tmp copy — both **survived** (2/2 green each) | the drain call-site arguments are pinned only by *presence*: a zeroed duration (the metric reports 0 ms forever) or a note-only group sum (mirror re-chunks vanish from the count) ships green |
| F3 | `src/AiRaccoon.Infrastructure/Ingestion/ChunkBudgetReconciler.cs:114` + `tests/AiRaccoon.Tests/Integration/Storage/ChunkRebudgetTests.cs:478-492` | `T1-ORC-06` | major | move `var startedAt = timeProvider.GetTimestamp();` to after `scanner.BudgetAsync(...)` | applied+reverted on /tmp copy — **survived** (15/15 green) | the elapsed claim cannot observe where the phase bracket starts: a bracket starting after the budget scan (the kill the step names) leaves report and event at 305.5 s and the suite green |
| F4 | `src/AiRaccoon.Infrastructure/Ingestion/ChunkBudgetReconciler.cs:404` + `tests/AiRaccoon.Tests/Integration/Storage/ChunkRebudgetTests.cs:496-512` | `T1-SCO-07` | minor | `[LoggerMessage(EventId = 448, Level = LogLevel.Information,` → `LogLevel.Debug` | applied+reverted on /tmp copy — **survived** (ChunkRebudgetTests 15/15; LoggerMessageEventIdTests 5/5; OtlpNamesRegistryTests 2/2; BackgroundInstrumentationCoverageTests 3/3; BundledModelLoggingTests 1 passed/1 skipped) | the documented `Information` level of 448 is unpinned — a downgrade hides the line from default logs while the message-text assertions stay green |
| F5 | `tests/AiRaccoon.Tests/Unit/Metrics/MetricsConfigKeysTests.cs:94` | `T1-SCO-02` | minor | none — a name's truth is not mutation-testable | unverified (static reasoning) | the test name still enumerates five families (`...JobDrainWriteAndSearchQueryAndSearchFusion`) while the assertion at `:96-97` now pins six, including `chunk.rechunk.` |

Counts: 0 blocker, 3 major, 2 minor. Only F5 is `unverified (static reasoning)`; F1–F4 were each
applied and reverted on the /tmp copy.

## Detail

### F1 — event 448's skip counts are only proven at zero

The `Rebudget_Event448_ReadsEveryReportCount` fixture is one over-budget note: the full-string
assertion (`ChunkRebudgetTests.cs:505-508`) pins notes=1 and mirror/unchanged/retryable/
unprovable/terminal all at 0. Every placeholder therefore exists in the template and in the
right textual order, but the argument *bindings* among the four zero-valued counts are
unfalsifiable. The report fields themselves are well covered
(`Rebudget_UnprovenGroup_...` asserts `report.RetryableSkipped == 1`,
`Rebudget_AnUnprovableGroup_...` asserts `report.UnprovableSkipped == 1`) — only the log binding
to them is not.

Probe (applied to `/tmp/qa-rechunk-review/repo`, then reverted):

```text
mutation: ChunkBudgetReconciler.cs:277
  report.GroupsUnchanged, report.RetryableSkipped, report.UnprovableSkipped, report.TerminalSkipped,
→ report.GroupsUnchanged, report.UnprovableSkipped, report.RetryableSkipped, report.TerminalSkipped,

$ dotnet test tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj --no-build \
    --filter "FullyQualifiedName~ChunkRebudgetTests"
Test run summary: Passed!  total: 15, failed: 0, succeeded: 15
```

Why it matters: event 448 exists precisely so a partial migration is distinguishable by its skip
class (`retryable` = will be retried, `unprovable` = terminal for this budget). An argument-order
mistake in that one call misreports the migration's state to the only audience that reads it, and
nothing reddens. Fix direction: assert the 448 line in a state where the counts differ — e.g. the
first pass of `Rebudget_AnUnprovableGroup_...` (retryable=1, rest 0) or a fixture that yields one
mirror terminal skip alongside a retryable note.

### F2 — the reporter call site is pinned for presence, not for values

`MigrationDrain_RecordsRechunkMeasurements_AfterDrain` asserts the duration series' kind, unit and
project (`:153-156`) but never its value; the groups series' value is asserted against the
independent oracle (good, see Q1). The reporter unit test pins `RechunkFinished`'s own conversion,
but nothing pins what `EntryEmbedder:264` passes into it, and the fixture is note-only with a
frozen clock — so both plausible call-site regressions survive.

Probes (each applied and reverted):

```text
(a) mutation: EntryEmbedder.cs:264
  reporter.RechunkFinished(rechunk.Elapsed, rechunk.NoteGroupsRechunked + rechunk.MirrorGroupsRechunked);
→ reporter.RechunkFinished(TimeSpan.Zero, rechunk.NoteGroupsRechunked + rechunk.MirrorGroupsRechunked);

(b) mutation: EntryEmbedder.cs:264
→ reporter.RechunkFinished(rechunk.Elapsed, rechunk.NoteGroupsRechunked);

$ dotnet test tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj --no-build \
    --filter "FullyQualifiedName~ModelMigrationRechunksAndReembedsTests"
Test run summary: Passed!  total: 2, failed: 0, succeeded: 2      # identical for (a) and (b)
```

Why it matters: (a) makes `chunk.rechunk.duration_ms` a permanent zero — the metric the step
exists to add, silently useless; (b) drops mirror re-chunks from `.groups`, under-counting every
file-path migration. The reporter unit test cannot see either because it calls the reporter
directly. Fix direction: drive the migration drain with a clock whose elapsed is a known,
non-zero span (the drain currently uses `FakeTimeProvider`, so elapsed is 0 and any value
assertion there would be vacuous), and include one mirror group in the fixture so the groups
assertion distinguishes `notes + mirrors` from `notes`.

### F3 — the elapsed bracket's span is unobservable (the disclosed limitation, confirmed)

Mechanics as in Q3. Probe (applied to `/tmp/qa-rechunk-review/repo`, then reverted):

```text
mutation: ChunkBudgetReconciler.cs:114
        var startedAt = timeProvider.GetTimestamp();
        var scanner = new ChunkPositionScanner(fileTypeMatcher, embeddingService);
        var budget = await scanner.BudgetAsync(connection, cancellationToken);
→       var scanner = new ChunkPositionScanner(fileTypeMatcher, embeddingService);
        var budget = await scanner.BudgetAsync(connection, cancellationToken);
        var startedAt = timeProvider.GetTimestamp();

$ dotnet test tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj --no-build \
    --filter "FullyQualifiedName~ChunkRebudgetTests"
Test run summary: Passed!  total: 15, failed: 0, succeeded: 15
```

Why it matters: the step record names this exact mutation class as the test's kill ("a stopwatch
starting after the scans"). The real-`Stopwatch` variant would redden (the value would not be
305.5 s), so the test does guard the clock-injection claim; what it cannot guard is the *span*.
The elapsed an operator reads can silently exclude the budget scan (and any work before the
moved bracket) while report and event agree on a wrong number. Fix direction: give `RunAsync` a
second `GetTimestamp` consumer whose placement the test scripts — e.g. assert through
`EntryEmbedder.DrainMigrationAsync` with a `ForPhases`-style script, where the drain's own
start bracket and the phase bracket interleave — rather than trying to observe placement with a
two-call provider.

### F4 — the 448 level is not asserted

`docs/reference/logging-event-ids.md:170` records 448 as "at Information", and `FakeLogger`
captures every level, but no test asserts `record.Level` for 448. Probe (applied and reverted):

```text
mutation: ChunkBudgetReconciler.cs:404
  [LoggerMessage(EventId = 448, Level = LogLevel.Information,
→ [LoggerMessage(EventId = 448, Level = LogLevel.Debug,

$ dotnet test ... --filter "FullyQualifiedName~ChunkRebudgetTests"      => Passed! 15/15
$ dotnet test ... --filter "FullyQualifiedName~LoggerMessageEventIdTests" => Passed! 5/5
$ dotnet test ... --filter "FullyQualifiedName~OtlpNamesRegistryTests"    => Passed! 2/2
$ dotnet test ... --filter "FullyQualifiedName~BackgroundInstrumentationCoverageTests" => Passed! 3/3
$ dotnet test ... --filter "FullyQualifiedName~BundledModelLoggingTests"  => passed, 1 skipped
```

Fix direction: add `record.Level.ShouldBe(LogLevel.Information)` where the 448 record is read
(the `Single(record => record.Id.Id == 448)` in the migration test and the `LoggedRebudgetMessages`
callers).

### F5 — the prefix test's name no longer matches its list

`InternalSeriesPrefixes_CoversJobDrainWriteAndSearchQueryAndSearchFusion` (`:94`) asserts a list
that now ends `[..., "search.fusion.", "chunk.rechunk."]` (`:96-97`). The body is correct and the
list is the contract; the name enumerates five of the six families. **unverified (static
reasoning):** the premise is the quoted name vs the quoted assertion — a rename has no mutation
that can redden, so there is no run to show. Fix direction: rename to
`..._CoversJobDrainWriteSearchQuerySearchFusionAndRechunk` (or drop the enumeration from the name).

## Verdict

**PASS-WITH-FINDINGS — 0 blocker, 3 major, 2 minor.**

The step's core claims are genuinely pinned: event 448 carries the injected elapsed (P2), the
duration conversion is exact and pinned at ms (P1), the two series are recorded on the real drain
path with kind/unit/project, the prefix stays discoverable through the derived list, and the
groups metric is checked against an independent fixture-known count (P3, PR-2.R12). The gaps are
all at seams the tests stop one step short of: the event's zero-valued count bindings (F1), the
values the drain passes into the reporter (F2), the span the elapsed bracket actually covers (F3),
plus the level (F4) and the stale name (F5). None makes an existing check vacuous; each is
closable with a small fixture or assertion change, and F3's fix needs a second scripted
`GetTimestamp` consumer rather than a two-call provider.

Findings go back to the implementation persona (`dotnet-engineer` per the project routing).
