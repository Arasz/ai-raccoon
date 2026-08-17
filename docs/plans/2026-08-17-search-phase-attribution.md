# Search phase attribution: close the books on `SearchAsync`

Status: PLAN — not yet implemented. Revision 2, after adversarial review against the source.
Date: 2026-08-17
Issue: [#382](https://github.com/arasz/ai-raccoon/issues/382)
Branch: `task/add-missing-metrics`
Baseline: `6fea1f42`, VERSION 1.24.0

> **Revision 2 changed five things.** S2's derived-list watch-red was void as written, and its
> section is merged into S1. The closure gate's detection floor was never computed and the ratio
> form hid it — the assertion is now an **absolute ms budget** (§S2, and §8). The region-B sizing
> quoted a stale bank-open number that current code refutes (§1). The version bump was not a
> one-file edit — and on the owner's ruling it now **becomes** one, by making `server.json` derive
> from `VERSION` (§S4), which reverses a prior ruling and must answer what that ruling was right
> about. §S4 is not part of issue #382 and should ship as its own PR.

---

## 1. What the issue asks, and what it can actually get

`SearchTimings` records six phases. Measured on the live bank on **2026-08-17** via
`memory_performance` (24-hour window, **n=12** `memory_search` calls): the tool average is
**287.03 ms**, the six phase averages sum to **169.12 ms** — **41% unattributed**. The worst single
recorded search is still **1,799.91 ms**. The issue's direction: add the missing spans, then gate
that the phases sum to the total.

**The gate as stated cannot be built, and the reason is structural.** The `memory_search` total is
recorded by `ToolExecutionActivity` (`src/AiRaccoon/Observability/ToolExecutionActivity.cs:108`)
from a `Stopwatch` started in `ToolTelemetry.RecordAsync`
(`src/AiRaccoon/Observability/ToolTelemetry.cs:65`) around `next(request)` — the MCP SDK's whole
dispatch. `SearchTimings` phases live inside `SqliteMemoryStore.SearchAsync`. Between the two sit
work items that no phase can ever reach (`src/AiRaccoon/Tools/MemoryTools.cs:131-174`):

| Step | Site | Shape |
|---|---|---|
| `gate.RequireAsync` | `:131` | access-mode resolve |
| scope parse + `SearchQueryValidator.ValidateAndThrowAsync` | `:133-145` | pure, trivial |
| `queryGuard.EvaluateAsync` | `:147` | **default-ON**; one prefix read (see below) |
| `qualityService.RecordSearchSafeAsync` | `:162` | **a bank write**, after the search returns |
| `RecordSearchMeasurements` | `:164` | in-memory buffer append |
| `gate.WrapAsync` + envelope | `:171-173` | envelope shaping |
| SDK argument binding and result serialization | inside `next` | outside `MemoryTools` entirely |

So the unaccounted 41% is **two disjoint regions**, not one:

- **Region A — inside `SearchAsync`, between the phases.** Closable. This is what the plan closes.
- **Region B — inside the tool total, outside `SearchAsync`.** Not closable by any change to
  `SearchTimings`, at any granularity, ever.

**Region B is unsized, deliberately.** An earlier draft put the query guard at "~2 bank opens per
search" on ADR-0075's trace. That is refuted by current code: `QueryGuardService.cs:36` now calls
`GetSettingsByPrefixAsync("queryGuard.")` — a **single prefix read, one open**
(`docs/plans/2026-08-16-bank-open-cost-implementation.md:150`, "One prefix read replaces 2 opens
per search"; WP2 landed in `c2fd31f0`). Default-ON is still correct. Quoting pre-digest-gate trace
numbers as current is the exact error this plan warns against elsewhere, so region B carries **no
number** until `search.total` exists to measure it against. That is the follow-up issue's own
point (§7).

The honest consequence: **`SearchAsync` gets its own measured total** (`search.total`), the phases
are gated to close against *that*, and the gap between `search.total` and `memory_search` is named
in the docs as region B rather than pretended away.

---

## 2. Rulings

### 2.1 Which spans

**Two new phases, one measured total, no residual phase.**

- `search.open` — brackets `factory.OpenBankAsync` (`SqliteMemoryStore.cs:163`).
- `search.embed` — brackets `_embedder.EmbedQueryAsync` (`:165`).
- `Total` — brackets the whole `SearchAsync` body, recorded as `search.total`.

*The simpler shape, stated as the invariant requires.* The simplest version is **`search.embed`
alone** — one span, the issue's headline. Rejected: it leaves the books open. Bank open is the
other uninstrumented step whose cost does not scale with bank size, and one span would have named
the smaller of two suspects while leaving #382's analysis still impossible to redo. `search.total`
is likewise not gold-plating: without a recorded total the next reader repeats exactly the
arithmetic that produced this issue — phases against `memory_search` — and gets the wrong answer
again, because of §1.

*Rejected: a residual/`other` phase.* It can never be wrong, and that is its problem — it is
`Total - sum(phases)`, derivable by any reader from two recorded series, so storing it is a second
copy of a number the data already holds (`derive-or-delete-the-list`). It would also absorb a new
untimed step **silently**, which is the exact failure mode #382 exists to prevent.

*Rejected: granular spans for the remaining untimed steps.* Verified untimed inside `SearchAsync`
and deliberately left in the residual: `FtsQueryNormalizer.BuildPlan` + `SourcePathQuery.TryBuild`
(pure), `ReadStructureAlphaAsync`, `SearchContexts.ResolveAsync`, `NoFusionRegressionEnabledAsync`
(three reads on the already-open connection; the last two helpers live in the
`SqliteMemoryStore.Search.cs` partial). Sub-ms each. **If the residual ever grows, instrument them
then** — the closure gate in S2 is what will say so, and S2's step 1 measures the number.

*Naming note the docs must carry.* `search.embed` is named for the neural step but brackets
`EmbedQueryAsync` in full (`EntryEmbedder.cs:250-266`): four settings reads (`ReadSettingsAsync`),
`CreateGenerator` — which builds the ONNX session on first use and caches it per fingerprint in a
`ConcurrentDictionary` (`EmbeddingService.cs:31-46`) — `TrimQueryToWindow` (WordPiece, ADR-0071),
then the forward pass. The first-call session build being inside this span is a feature: it is the
mechanism the issue's warm-up hypothesis needs, and the series will show it as a first-search
outlier instead of leaving it invisible. On a bank with **no embedding engine configured**,
`EmbedQueryAsync` returns null right after `ReadSettingsAsync` (`EntryEmbedder.cs:253-257`), so
`search.embed` is four settings reads and near zero — not a broken metric.

### 2.2 What the total is

**`SearchTimings` gains a measured `Total`. It is not a phase name.** The doc comment at
`SearchResults.cs:28-34` records ruling F11: `PhaseNames` is declared, not reflected, precisely so
a computed `Total` cannot be silently minted as a series. Adding `search.total` to `PhaseNames`
would honour the letter (deliberate, not silent) and break the meaning — `PhaseNames` is the
decomposition, and a consumer that ever sums it would double-count.

```
PhaseNames  = [search.open, search.embed, search.fts, search.vector,
               search.fusion, search.affinity, search.snippets, search.bump]   // eight
TotalName   = "search.total"
SeriesNames = [..PhaseNames, TotalName]                                        // derived, one place
Phases()       -> the eight name/value pairs
Measurements() -> Phases() + (TotalName, Total)     // mirrors FusionDiff.Measurements()
```

`SeriesNames` exists so the two downstream consumers stay derived rather than gaining a second
hand-kept list (`derive-or-delete-the-list`): `MetricsReportService.cs:44-46` and
`SqliteMetricsStore.cs:37-39` both move from `PhaseNames` to `SeriesNames`.

### 2.3 The gates

Two gates, two mechanisms, because **one mechanism cannot prove both**.

- **Attribution** uses the `ScriptedTimeProvider` — deterministic, no wall clock.
- **Closure** uses a **real clock** (`TimeProvider.System`) with a delaying embedder stub, asserting
  an **absolute residual budget in ms**.

Closure *cannot* use the scripted clock: a newly-introduced untimed `await` does not advance a
scripted clock at all, so the closure assertion would stay green against the very defect it exists
to catch — a check that can only produce success (`prove-the-check-fails`). It equally cannot use
`FakeTimeProvider` un-advanced (every elapsed is zero, so any `sum >= k * Total` passes vacuously;
ADR-0062 is the same trap seen from the other side).

A **structural** gate — failing at compile time when a new untimed `await` appears ahead of the
phases — is **judged infeasible and is not attempted**. It needs a Roslyn analyzer reasoning about
one method body's await sequence: a subsystem, against a focused instrumentation change. The
runtime closure gate catches the same defect with a number attached. Stated so the absence is a
decision, not an oversight.

### 2.4 Bank open

**A phase on it is the right place; nothing is duplicated.** ADR-0075 is *"Only the server writes
to the bank"* — it contains the bank-open profiling, but its measurement protocol
(`docs/plans/2026-08-16-bank-open-cost-implementation.md` §8) is a **one-off `dotnet-trace` run**
whose primary metric is deliberately **a count** (opens per operation) with ms an **explicit
non-target**. It created **no live series**, and its numbers predate the digest gate that landed in
`c2fd31f0`. `search.open` is the first persistent measurement of the cost.

Two caveats the docs must carry: `search.open` covers **only `SearchAsync`'s own single open**, not
the query guard's (region B, §1); and connection dispose / return-to-pool happens at method exit,
after the `Total` bracket closes, so it is in neither.

---

## 3. Sections and their order

**Five sections — and the fifth is the tell.** S1-S3 and S5 are issue #382. **S4 is not**: it is an
owner-directed fix to the version markers that shares no file and no reasoning with the rest, and
the plan's own "more than ~5 sections means over-engineered" heuristic is firing on a section that
arrived from outside the task rather than from decomposing it. That is the argument for shipping S4
as its own PR (§S4); the count is a symptom, not the problem.

S1 and the old S2 are merged: the old S2 was three one-line production edits, the names S1 defines
are inert until something records them, and the split is what created the sequencing trap described
in §S1's watch-red note.

```
  S1 (Core + store + host + reporting + shared test harness)
        │
        ├──> S2 (the closure gate)
        └──> S3 (docs + ADR-0080)
                                        S4 (server.json derives from VERSION)  ── independent
                                              │
                                              └──> S5 (the bump, last commit)
```

- **S1 lands first.** Everything in the #382 lane consumes the names, the `Total`, and the shared
  test harness.
- **S2 and S3 run in parallel after S1** — they share no file with each other.
- **S2 depends on S1's files**, not merely its names: S1 extracts the embedder stub and store
  factory into a shared harness that S2 consumes. An earlier draft claimed S2 shared no file with
  S1; that was wrong once the duplication was removed.
- **S4 is independent of S1-S3** and can run at any time, including in a separate PR — which is the
  recommendation.
- **S5 is last, alone, and ordered after S4**: only once `server.json` derives is the bump the
  one-file edit the owner is asking for. Its commit must not reach `main` ahead of the merge.

---

## 4. The sections

### S1 — Instrumentation, recording, reporting

**Scope.** Extend `SearchTimings` per §2.2; bracket `OpenBankAsync`, `EmbedQueryAsync` and the whole
body in `SearchAsync`; record the total alongside the phases; move both downstream consumers to
`SeriesNames`; extract the shared test harness.

**Files — production.**
- `src/AiRaccoon.Core/Memory/SearchResults.cs` (record params, `Empty`, `PhaseNames`, `TotalName`,
  `SeriesNames`, `Phases()`, `Measurements()`; the `:33` "six" comment)
- `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs` (`SearchAsync`, `:158` onward)
- `src/AiRaccoon/Tools/MemoryTools.cs` (`:330-350` iterate `Measurements()`; the `:324` comment)
- `src/AiRaccoon.Infrastructure/Metrics/MetricsReportService.cs` (`:44-46` → `SeriesNames`)
- `src/AiRaccoon.Infrastructure/Metrics/SqliteMetricsStore.cs` (`:37-39` → `SeriesNames`; its
  doc comment says "SearchTimings' own **phase** suffixes" and `total` is deliberately not a phase,
  so the word becomes **series**)

**Files — tests.**
- `tests/AiRaccoon.Tests/Unit/Storage/SearchTimingsHarness.cs` **(new)** — hosts the store factory
  and a `VectorEmbedderStub(TimeSpan delay = default)` promoted from the `private sealed
  FixedVectorEmbedder` at `SqliteMemoryStoreSearchTimingsTests.cs:35-42`. Copying ~30 lines of
  `IEntryEmbedder` implementation into a second file guarantees drift the next time the interface
  gains a member.
- `tests/AiRaccoon.Tests/Unit/Memory/SearchResultsTests.cs` (`:45-52` pins the names)
- `tests/AiRaccoon.Tests/Unit/Storage/SqliteMemoryStoreSearchTimingsTests.cs` (`:15` "six")
- `tests/AiRaccoon.Tests/Unit/Mcp/MemoryToolsTests.cs` (`:305`, `:348`, and the **method names**
  `Search_RecordsSixPhaseMeasurements_...` at `:307` and `:369`)
- `tests/AiRaccoon.Tests/Integration/Metrics/MetricsReportServiceTests.cs` (`:55-57`, `:65`, `:86`,
  `:88`, `:96` — see the watch-red note)
- `tests/AiRaccoon.Tests/Integration/Metrics/SearchMetricsIsolationTests.cs` (`:26`, `:109`,
  `:115` **method name**, `:131`, and `:207` — whose assertion message reads "capacity 1 against
  six phase measurements"; the buffer test still holds at nine, but the message must move with it)
- `tests/AiRaccoon.Tests/Integration/Metrics/SqliteMetricsStoreTests.cs`

**Implementation notes the author will otherwise hit.**

1. `Total`'s `GetTimestamp()` is the **first** statement after `ArgumentNullException.ThrowIfNull`,
   before `await using var connection = ...`. There is no early `return` before the `SearchTimings`
   construction today; if one is ever added, `Total` is lost.
2. **`ScriptedTimeProvider` must be redesigned for nesting.** Its current logic
   (`SqliteMemoryStoreSearchTimingsTests.cs:126-150`) toggles `_pendingElapsed` on alternating calls
   and assumes strictly non-overlapping pairs. `Total` nests around every phase, so the alternation
   desynchronises on the first inner bracket and every scripted value lands on the wrong phase.
   Replace with: a monotonic cursor that **every** `GetTimestamp()` returns and then advances by the
   next scripted delta. `TimeProvider.GetElapsedTime(long)` calls `GetTimestamp()` internally, so a
   simple bracket's elapsed is its own delta, the deltas between brackets are the residual, and
   `Total` is their sum — exact integer arithmetic, no wall clock.
3. `SqliteMemoryStore.cs` is under a line ratchet:
   `SqliteMemoryStoreSizeRatchetTests.cs:80` caps it at **1066** (the test counts that file only);
   it is at **989**. The change is ~8 lines and fits. **Do not raise the cap** — the file's own
   comment block records six lowerings and names the partial-file seam
   (`SqliteMemoryStore.Search.cs`) as the escape valve if it is ever hit.
4. Three `new SearchTimings(...)` construction sites exist; all three take the new parameters.

**Acceptance criteria.**
1. `PhaseNames` holds the eight names in measurement order; `SeriesNames` is derived from
   `PhaseNames` + `TotalName` in one place; `SqliteMetricsStore` and `MetricsReportService` both
   consume `SeriesNames`.
2. A search that opens a bank and embeds a query reports `Open > 0` and `Embed > 0`.
3. Each of the eight phases reports the elapsed time of its own bracket and no other's.
4. One `memory_search` call records **nine** histogram rows — eight phases plus `search.total` —
   each tagged with query hash and correlation id, none carrying query text.
5. `memory_performance` returns a series for every name in `SeriesNames`, including ones at count
   zero.
6. A measurement tagged `phase=open`, `phase=embed` or `phase=total` survives the save-time
   allowlist; an invented suffix still does not.
7. Recording stays best-effort: a throwing recorder does not fail or slow the search.

> **Dropped from this list:** `Total >= sum(phases)`. It holds by construction under both the
> redesigned `ScriptedTimeProvider` and `FakeTimeProvider`, so no defect can redden it in S1's
> harness. It is asserted in S2, against a real clock, where it can.

**Gate.**
```
dotnet test --filter "FullyQualifiedName~SqliteMemoryStoreSearchTimingsTests|FullyQualifiedName~SearchResultsTests|FullyQualifiedName~SqliteMemoryStoreSizeRatchetTests|FullyQualifiedName~MemoryToolsTests|FullyQualifiedName~MetricsReportServiceTests|FullyQualifiedName~SearchMetricsIsolationTests|FullyQualifiedName~SqliteMetricsStoreTests"
```

**Watched failing against — three defects.**
1. **Attribution:** swap the `search.open` and `search.embed` assignments in `SearchAsync`. The
   scripted-clock test must name **both** wrong phases.
2. **Pinned list:** drop a name from `PhaseNames` while leaving it in `Phases()`;
   `SearchResultsTests` fails on the list.
3. **Derived series:** leave `MetricsReportService` on `PhaseNames`, so `search.total` is written to
   the `metrics` table and filtered out of every report. **This watch-red is only valid after the
   test edits.** `MetricsReportServiceTests.cs:65` (`2 + SearchTimings.PhaseNames.Count`) and `:96`
   (`ShouldBe(SearchTimings.PhaseNames)`) currently derive from `PhaseNames` — the very list the
   defect leaves in place — so both sides of the comparison move together and the assertions pass
   against the defect they exist to catch. Revision 1 called this "already derived, good"; it is
   derived from the **wrong** list. Move `:65` and `:96` to `SeriesNames` **first**, then perform
   the watch-red.

**The ratchet gate carries no watch-red, by exception.** It has already been seen to fail for real:
its own comment block (`SqliteMemoryStoreSizeRatchetTests.cs:70-79`) records it catching ADR-0078's
change at 1144 against the then-1112 cap, which is what forced the search seam out into a partial
file instead of a raise.

---

### S2 — The closure gate

**Scope.** The gate #382 actually asks for: the phases account for `Total` within a stated budget,
and it goes red the day another untimed step is introduced ahead of them.

**Files.**
- `tests/AiRaccoon.Tests/Unit/Storage/SearchPhaseClosureTests.cs` **(new)**
- consumes `SearchTimingsHarness.cs` from S1

**Design.** Real `TimeProvider.System`. `VectorEmbedderStub(TimeSpan.FromMilliseconds(250))` — the
sleep's job is to make watch-red #1 observable, nothing else. Traits `[Category=Integration]`,
`[Speed=Slow]`, matching the sibling file.

```
residual = timings.Total - Σ timings.Phases()

residual        >= TimeSpan.Zero      // brackets are disjoint sub-spans of Total
residual        <= ResidualBudget     // the closure assertion
timings.Embed   >= 200 ms             // the 250 ms landed in the named phase
```

**Why an absolute budget and not a ratio — a deliberate departure, see §8.** A ratio floor
`Σ >= r·Total` is algebraically `residual <= Total·(1−r)`, and because the stub's sleep sits
**inside** a timed phase it inflates numerator and denominator together. So the real threshold is a
function of the harness's own stub delay: at `r = 0.75` with a 250 ms stub the budget is ~86 ms —
a third of an entire real search — and nobody reading the test would know. At `r = 0.90` it is
~28 ms. Raising the stub delay *loosens* the gate. Expressing the same number directly as
`ResidualBudget` in ms removes that coupling: the threshold is visible, and it stops moving when
someone tunes the stub.

**Step 1 of this section is a measurement, and it gates the constant.** The residual is
`BuildPlan` + `TryBuild` + `ReadStructureAlphaAsync` + `SearchContexts.ResolveAsync` +
`NoFusionRegressionEnabledAsync` — three reads on an already-open connection plus pure string work,
so **1–3 ms expected**. That is reasoning, not measurement: the residual cannot be measured before
S1 exists, because `Total` does not exist. So S2 begins by printing the real residual over ~20
searches with the stub in place, records the number **in this document and in the test comment**,
and sets `ResidualBudget` at ~10× the observed p95. **Provisional value: 28 ms**, adopted from the
coordinator's ruling and consistent with the 1–3 ms estimate. If the measurement lands materially
elsewhere, the constant follows the measurement and the change is recorded here.

**Acceptance criteria.**
1. The measured residual over ~20 searches is recorded in this document, and `ResidualBudget` is
   ~10× its p95.
2. `residual <= ResidualBudget` and `residual >= TimeSpan.Zero` with the delaying stub.
3. The 250 ms lands in `search.embed`.
4. **Repeat-run standard, adopting ADR-0062's**: passing locally is not evidence about a race,
   however many times it passes. Required before the section is done — **3 runs of the file alone,
   plus 4 full `Speed=Slow` sweeps, one of them under CPU saturation** (a parallel build or a busy
   loop per core). `SearchPhaseClosureTests` carries no `[Collection]`, so it runs parallel with the
   rest of the sweep; at a 28 ms budget the contention risk is small but real, which is the whole
   argument for measuring rather than asserting.

**Gate.**
```
dotnet test --filter "FullyQualifiedName~SearchPhaseClosureTests"
```

**Watched failing against — two defects, both must be seen red, and the boundary observed.**
1. **Remove the `search.embed` bracket** (keep the call, drop the timestamps, leave `Embed` at
   `TimeSpan.Zero`). The 250 ms leaves `Σ phases` and stays inside `Total`; residual ≈ 250 ms
   against a 28 ms budget. This is the #382 defect itself.
2. **Insert an untimed delay between the `Total` bracket's start and the first phase** — the future
   regression the gate exists for. Size it **just above** the budget: `Task.Delay(40)` against 28 ms
   goes red. Then **record the corresponding green just below it** — `Task.Delay(15)` must pass —
   so the detection floor is a measured boundary rather than an asserted one. `Task.Delay` only
   ever overshoots, so the red side has margin and the green side is the one worth watching.

**Stated limitation, now with a number.** A new untimed step under ~28 ms will not trip this gate.
That is the price of any threshold, and it is now legible instead of buried in a ratio. What the
gate guarantees: no untimed step larger than the budget can hide, and the budget is ~10× the work
the residual is *supposed* to contain.

---

### S3 — Docs and ADR-0080

**Scope.** Fix every hardcoded "six", document what the new series mean and do not mean, record the
decision.

**Files.**
- `docs/adr/0080-the-phases-close-against-search-total-not-the-tool-total.md` **(new)** — 0079 is
  the next free number; 0078 is the highest on disk
- `docs/adr/README.md` (index row, matching the existing dense-summary style)
- `docs/explanation/architecture.md` (`:400`, `:696`)
- `docs/how-to/read-performance-metrics.md` (`:63`, plus the `search.*` reader guidance)

**ADR-0080 is deliberately narrow.** It earns its place for **§1's structural finding only**: the
phases can never close against `memory_search`, because the tool total brackets the SDK dispatch and
region B sits between them — therefore `search.total` is the denominator, and `search.open` /
`search.embed` are what make that denominator decomposable. The rejected-alternatives inventory
(residual phase, granular spans, compile-time analyzer) stays in this plan and out of the ADR.

**One thing ADR-0080 must argue explicitly**, or the next reader closes S2 as a regression:
**ADR-0062's relationship to S2's `Task.Delay`.** 0062's headline is *"Wait for the observable,
never for the clock"*, and its Consequences record *"No fixed sleep remains in the file"* (live echo
at `MetricsFlusherTests.cs:194`). S2 is compatible and the ADR must say why: 0062 is about a **fake
clock advanced before its timer registers**, and about sleeping as a **synchronisation guess** in
place of waiting on an observable. S2's sleep is neither — it is **the subject under measurement**,
a known quantity deliberately injected so the instrumentation can be checked for finding it, and
nothing in the test waits on it to synchronise anything.

**`read-performance-metrics.md` gains four reader rules:**
1. **Mixed windows.** `PerformanceReportBuilder` presents every derived-inventory series at
   `count: 0` when it has no samples, so a window spanning this change shows
   `search.open`/`search.embed`/`search.total` at a **lower count** than `search.fts`.
   **Sum-versus-total arithmetic is valid only where `search.total`'s count equals `search.fts`'s.**
   No backfill is possible — old rows do not know their own total. The count mismatch is the honest
   signal.
2. **`search.total` is not `memory_search`.** The difference is region B (§1), and it is expected,
   not a defect.
3. **The two `search.open` caveats** from §2.4.
4. **`search.embed` on an engine-less bank** is four settings reads and near zero (§2.1).

**One line in `architecture.md`'s metrics section on buffer pressure (N4).** Phase rows per search
go 6 → 9. Against `DefaultBufferCapacity = 1000` and a 30 s flush
(`MetricsConfigKeys.cs:8,18`) the drop threshold moves from ~142 to ~100 searches per flush window.
Not a risk at any realistic rate — recorded so a future `metrics.dropped` uptick is attributable.

**Acceptance criteria.**
1. No occurrence of "six" describing the phase list survives in `src/`, `docs/explanation/`,
   `docs/how-to/`, or `tests/` (the `tests/` occurrences are fixed in S1, whose scope already opens
   every one of those files; this criterion is where they are checked).
2. ADR-0080 follows the Nygard shape used by 0071-0078 and is linked from `docs/adr/README.md`.
3. The four reader rules and the buffer-pressure line are present.

**Gate.**
```
grep -rniE "\bsix\b|SixPhase" src/ docs/explanation docs/how-to tests/ \
  --exclude=HermesProcessNoisePolicyTests.cs | grep -iE "phase|search" ; test $? -eq 1
dotnet test --filter "FullyQualifiedName~AdrIndexTests"
```
One derived expression rather than a list of literal patterns. Revision 1's four literals missed
`MemoryTools.cs:324` ("the six search phases", no hyphen); revision 2's first attempt
(`grep -rn "six"`, case-sensitive) missed the four **method names** `Search_RecordsSixPhase...` —
the same drift, twice, which is why the pattern above was run against the tree before being
written down. **Verified on 2026-08-17: 19 hits, all genuine, no false positives.** Each element
earns its place: `\bsix\b` excludes `SqliteMemoryStoreSizeRatchetTests.cs:75` ("the sixth time",
historical and correct); `|SixPhase` recovers the CamelCase method names the word boundary drops;
`--exclude` names the one unrelated file (`HermesProcessNoisePolicyTests.cs:92`, six log
occurrences) rather than weakening the pattern for it. `docs/plans/` is excluded on purpose: the
historical `2026-08-15-*.md` legitimately says "six".
`AdrIndexTests` (`tests/AiRaccoon.Tests/Unit/Docs/AdrIndexTests.cs`) already pins that the index
lists every ADR on disk, links none that is gone, and has no unrecorded numbering gaps.

**Watched failing against:** revert one "six" — `architecture.md:400` — and confirm the grep exits
zero (i.e. the gate reports it). A grep gate that has only ever returned empty is
indistinguishable from a mistyped pattern.

---

### S4 — Make `server.json` derive from `VERSION`

> **This section is not part of issue #382.** It is an owner-directed drive-by fix to the version
> markers. It shares no file with S1-S3 and no reasoning with them. **Recommendation: ship it as its
> own PR, ahead of this one** — `one-PR-per-task` is an invariant, the release note for a merged PR
> is its title, and a title cannot honestly name both an instrumentation change and a packaging
> change. If it must ride along, it lands as its own commits and the PR title names the
> instrumentation, with this called out in the body.

**Scope.** `VERSION` becomes the only hand-written version marker; `.mcp/server.json`'s two version
slots are filled at build time from `$(Version)`.

**Why this reverses a prior ruling, and what that ruling was right about.**
`docs/plans/2026-08-15-performance-metrics-implementation.md` §R1 considered exactly this and
**rejected it**: *"Generating it at pack time is cleanest by derive-or-delete-the-list but changes
packaging, and a manifest correct in the repo but wrong in the package is a defect nobody sees until
a registry rejects it — loses on risk."* It chose instead to keep the literals and compare them to
the assembly version, which is what `McpServerJson_Versions_MatchTheBuiltAssemblyVersion` does
today. The owner is reversing that call. R1's objection is still valid and the new design must
answer it rather than ignore it: **the gate inspects the packed `.nupkg`, not the generated
intermediate file.** Asserting against `obj/` would leave R1's exact failure mode — repo right,
package wrong — undetected, which is the whole reason R1 refused this shape.

R1's doc comment already claims the end state this section builds (`VersionContractTests.cs:11-14`:
*"the built assembly and server.json must derive from it, with no literal duplicate"*). That comment
is false today. Making it true is the work.

**Files.**
- `src/AiRaccoon/.mcp/server.json` — both version slots become the token `__VERSION__`
- `src/AiRaccoon/AiRaccoon.csproj` — the generating target; `:26`'s `Pack="true"` moves to the
  generated copy
- `scripts/version-bump.py` — see the ruling below
- `.ai-badger/skills/learned/software-development/version-bump/SKILL.md` — documents the bump
  procedure as "run `scripts/version-bump.py`"; it must move with whatever the script becomes, or it
  is a third copy of the procedure drifting from the two real ones
- `tests/AiRaccoon.Tests/Unit/Setup/VersionContractTests.cs`

**The token is `__VERSION__`, and the choice is forced.** It must not be `$(Version)`: the target
substitutes via an MSBuild property function, and `'$(Version)'` written as a `.Replace()` argument
is expanded by MSBuild *before* the function runs, so the target would search for the version it is
trying to insert. `__VERSION__` contains no `$(`, no digits, and cannot be mistaken for a version.
**Verified against the registry schema** (fetched 2026-08-17,
`static.modelcontextprotocol.io/schemas/2025-10-17/server.schema.json`): the top-level `version` is
`{type: string, maxLength: 255}` and `packages[].version` is
`{type: string, minLength: 1, not: {const: "latest"}}` — **neither carries a `pattern` or `format`
constraint**, so the tracked file stays schema-valid with a token in it and editors show no error.
The single forbidden literal is `"latest"`, which `__VERSION__` is not.

**Implementation note that will otherwise eat an afternoon.** `WriteLinesToFile` escapes `%` and
treats `;` as an item separator. The current `server.json` contains neither, so the straightforward
read-replace-write works — but the implementer must **diff the generated file against the tracked
one** (token substituted) as the first thing they do, rather than assuming byte-for-byte fidelity.
`File.WriteAllText` is not available as an MSBuild property function, so `WriteLinesToFile` is the
route.

**Ruling on `scripts/version-bump.py` — it should be deleted, and this reverses R1 too.** R1 kept it
because *"its `replace_version(path, old, new, expected)` count guard is a real check"* and
*"deleting the script would make bumping an unvalidated hand-edit"*. Both justifications expire
here. With `server.json` derived, the guard's remaining job is "the `VERSION` file contains the
version" — tautological. And the bump is no longer unvalidated: `ValidateVersionFile`
(`Directory.Build.props:19`) and `VersionContractTests` validate the **result**, whatever produced
it, which is strictly stronger than validating the procedure. What remains is ~50 lines of Python
that increment an integer. *Counter-argument, stated because this is a judgment call:* the script
prevents a well-formed-but-wrong bump (`1.2.50` for `1.25.0`) that no gate catches. Against that: it
is a one-line diff in a one-line file, read by a human in the PR. **Recommendation: delete it and
rewrite the learned skill to name the one-line edit plus the gate command.** The owner's call —
if it survives, it loses its `SERVER_JSON` handling (`:13`, `:45`, and the `expected=2` argument)
and the skill still needs no change.

**Acceptance criteria.**
1. The tracked `.mcp/server.json` contains no literal semver anywhere.
2. The `.mcp/server.json` **inside the packed nupkg** carries the version from `VERSION` in both
   slots.
3. `McpServerJson_ConformsToRegistrySchemaConstraints` and
   `PackageId_MatchesServerIdentifier_CommandUnchanged` keep passing against the **tracked** file —
   they assert authored content (description length, env vars, repository url, package identifier),
   none of which is generated.
4. `VersionContractTests.cs:11-14`'s doc comment is true as written; it is reworded only if it is
   still inaccurate afterwards.
5. The learned skill and the script agree with reality.

**Gate.**
```
dotnet pack src/AiRaccoon/AiRaccoon.csproj -o /tmp/raccoon-pack
dotnet test --filter "FullyQualifiedName~VersionContractTests"
```
The new `PackedMcpServerJson_CarriesTheVersionFileVersion` opens the `.nupkg` (a zip) and reads
`/.mcp/server.json` out of it — this is the test that answers R1's objection, and it is why the gate
runs `dotnet pack` first. `TrackedMcpServerJson_HoldsNoLiteralVersion` asserts
`\d+\.\d+\.\d+` does not appear in the tracked file; **verified 2026-08-17** that with the two
slots tokenised the file has no other semver-shaped literal (the `$schema` URL's `2025-10-17` and
`repository.id`'s `1320945078` do not match), so the regex needs no exclusions.

**Watched failing against — two defects.**
1. **Single-marker gate:** put `1.24.0` back into the tracked `server.json`, watch
   `TrackedMcpServerJson_HoldsNoLiteralVersion` go red, take it out, watch green. This is the gate
   that did not exist before and is the point of the section.
2. **R1's own failure mode:** make the generating target a no-op (or point `Pack="true"` back at the
   tracked file) so the repo is right and the package ships `__VERSION__`. Watch
   `PackedMcpServerJson_CarriesTheVersionFileVersion` go red. A test that read the intermediate file
   instead of the nupkg would stay green here — which is exactly why it reads the nupkg.

---

### S5 — The bump

**Scope.** One line. `VERSION`: `1.24.0` → **1.25.0**. Minor: new observable behaviour on the
`memory_performance` surface (three new series), no breaking change to any existing series or tool
signature.

**This is a separate section from S4 on purpose.** The derivation work is a real change that gets
gated like any other; only once it has landed is the bump genuinely a one-file edit, which is the
state the owner is asking for. Ordering is not optional: bumping before S4 lands means editing two
markers again.

**Files.** `VERSION`

**Hazard.** `.github/workflows/release.yml` triggers on `push` to `main` filtered to
`paths: ["VERSION"]` and cuts a tag plus a GitHub release (`--generate-notes`). The bump lands **in
this branch**, as the **final commit** of the PR, and reaches `main` only through the squash-merge —
at which point the release cuts deliberately, with the PR title as the release note. Never push a
VERSION change to `main` directly; never bump early.

**Acceptance criteria.**
1. `VERSION` reads `1.25.0` and is the only file changed by this section.
2. The PR title reads as a release note (`--generate-notes` builds the changelog from merged PR
   titles; there is no hand-maintained CHANGELOG).

**Gate.**
```
dotnet build
dotnet test --filter "FullyQualifiedName~VersionContractTests"
git diff --name-only main -- . | grep -c . # this section touches VERSION and nothing else
git rev-parse --abbrev-ref HEAD           # not main
```
**Watched failing against:** write `1.25.0-rc1` and confirm **two local enforcers** refuse it —
`Directory.Build.props:19` (`ValidateVersionFile`, so `dotnet build` fails) and
`VersionContractTests.VersionFile_IsABareSemverWithNoPrereleaseSuffix` (`:21-25`). The workflow's own
regex (`release.yml:54-57`) is unwatchable from a task branch and is not the gate.

---

## 5. What the phases will say once this lands

Worth stating before anyone optimises anything. On the 2026-08-17 measurement (n=12, 24 h window)
the **two legs are essentially all of the accounted cost**: `search.fusion` averages **1.30 ms** and
`search.affinity` **0.78 ms** — **~2 ms combined**, against a 287.03 ms tool average. Optimising
fusion or affinity is optimising noise. What this change does is put `search.open` and
`search.embed` on the same footing so the next person can see whether *they* are the target — which
the data at n=12 cannot say, and this plan does not claim.

---

## 6. Gate summary

| Gate | Section | Watch-red move |
|---|---|---|
| Phase attribution (8 phases) | S1 | swap the `open` and `embed` assignments |
| Pinned name list | S1 | drop a name from `PhaseNames`, keep it in `Phases()` |
| Derived series list | S1 | after moving the tests to `SeriesNames`, leave `MetricsReportService` on `PhaseNames` |
| Line ratchet | S1 | *(exempt — already seen red at 1144 vs 1112, ADR-0078)* |
| Residual budget | S2 | remove the `search.embed` bracket |
| Residual budget (boundary) | S2 | `Task.Delay(40)` red **and** `Task.Delay(15)` green |
| No stale "six" | S3 | revert `architecture.md:400`; grep must exit zero |
| Single version marker | S4 | put `1.24.0` back into the tracked `server.json` |
| Packed manifest correct | S4 | no-op the generating target; the nupkg ships `__VERSION__` |
| Version format | S5 | write `1.25.0-rc1`; build and contract test both refuse |

---

## 7. Explicitly out of scope

- **Region B (§1).** Instrumenting the query guard, the access gate, the `search_quality` write and
  the envelope shaping. It is host-layer work under `mcp-thin`, needing its own decision about
  whether an MCP tool method may hold timing at all. File it as a follow-up **once `search.total`
  exists**, because only then is region B a measured number rather than a guess — which is exactly
  why this plan refuses to size it now.
- **Granular spans** for the three settings reads — deferred until S2's measured residual says they
  matter (§2.1).
- **A structural compile-time gate** on untimed awaits — infeasible at proportionate cost (§2.3).
- **Backfilling `search.total`** — impossible; S3's mixed-window reader rule is the answer.

---

## 8. Where this revision departs from the review

The review directed the closure gate to keep its ratio form (`0.90`) and additionally state the
budget in ms. This plan **inverts that**: the ms budget is the assertion, and the ratio is dropped
rather than documented alongside. Reason — the review's own finding, carried one step further. The
ratio's threshold is `Total·(1−r)`, and the stub delay is inside `Total`, so the ratio's real
meaning depends on a harness constant that a future author may tune for unrelated reasons (a slower
CI box, a longer sleep to stabilise something else). Keeping both forms means keeping a number that
can silently disagree with itself. The chosen value is identical to the review's — **28 ms**,
pending S2's step-1 measurement — so no threshold moves; only its expression does.

**Three further departures, all in S4, all flagged rather than done quietly:**

1. **The review directed `python3 scripts/version-bump.py minor`.** The owner's later ruling
   supersedes it, and this plan goes one step further by recommending the script be **deleted**
   rather than reduced. The reasoning is R1's own: it kept the script for a count guard that becomes
   tautological and for an "unvalidated hand-edit" risk that the result-validating gates already
   cover. Presented as a recommendation, with the counter-argument stated, because the owner asked
   whether it should survive rather than telling me it should not.
2. **The review's item 4 said "assert against the generated artifact".** This plan asserts against
   the **packed nupkg** instead. Asserting against the `obj/` copy would leave untouched the exact
   failure mode — repo right, package wrong — that made the prior ruling reject pack-time generation
   in the first place.
3. **S4 is recommended as a separate PR.** It is not part of #382, and `one-PR-per-task` plus
   PR-title-as-release-note both argue against mixing it in. Stated as a recommendation; the section
   is written so it works either way.

Everything else in the review is adopted as directed. Its four blocking findings, and the owner's
five S4 points, were each re-verified against the source before amendment — including the registry
schema, fetched rather than recalled.
