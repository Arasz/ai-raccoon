# REVIEW R1 — architecture & correctness review

Independent review lane of task `doctor-feature-match`, 2026-08-26, by a reviewer who
wrote none of the planning lanes. Companion docs: the research record and lanes P1-P4 in this directory.

---

Round: independent review of lanes P1 (output contract), P2 (implementation shape), P3 (tests), P4 (observability) for task `doctor-feature-match`. Plan-only; nothing was edited.

**Lane P1 exists** — `docs/work/2026-08-26-doctor-parity-moe-p1-contract.md` (316 lines) — and was read, so P2's implied contract is judged against it rather than substituted for it.

Method: every claim below was re-read from source in `.ai-badger/worktrees/doctor-feature-match`. Where a lane's number disagreed with the file, the file wins. Independently re-measured, not taken from any lane: 168 `[LoggerMessage]` declarations in `src/`, highest EventId 1007, zero duplicates (the registry's own procedure, `docs/reference/logging-event-ids.md:91-99`); 11 tests in `DoctorCommandsTests.cs`; 38 `new EntryEmbedder(` construction sites across 28 files.

---

## 0. Verdict

1. The extraction is warranted, but **not in P2's shape**: two of its four new types exist only to feed a `Func` field that re-spells the per-corpus branch P2 §1.0 claims to have removed. The smaller shape removes the same duplication for roughly the line count of the duplication itself.
2. **The four lanes do not agree on the output literals, the exit code, or the event ids.** Three independent collisions. Until they are frozen, the plan is not implementable — an implementer following P2 produces a report P1 forbids and P3 does not test.
3. **P4 is a different subsystem** with a materially under-counted cost (38 constructor sites, 3 new constructor parameters) and one item (§2 S3) that is not the liveness fix it claims to be. Split it into its own PR.
4. The review surfaced one defect no lane names, and it is the only item here that leaves a bank **permanently** unusable: `model reset` deletes `embedding.provider` while a `model_migration` row is open, and nothing ever closes that row.

---

## 1. Findings

### MUST

**M1 — Three lanes, three different literals for the migration line.**
Lane/section: P1 §2.3 / P2 §2 renderer / P3 fixture table (`MEM_MIGRATION`).
Source: P1 rules the subject `model migration:` on the grounds that the table is bank-wide and single-row; P2's `MigrationLine` emits `memory migration: open since {startedAt} — memory tools are refused until it drains`; P3's fixture token is `memory migration: `. The bank agrees with P1: `MemorySql.SelectModelMigration` reads `FROM model_migration WHERE id = 1` (`src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs:470-472`) and the DDL pins one row, `id INTEGER PRIMARY KEY CHECK (id = 1)` (`src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:399-409`).
Correction: adopt P1's subject `model migration:` and P1's qualifier `(all MCP tool calls are refused until it finishes)` — the gate refuses *every* tool, not memory tools (`src/AiRaccoon/Tools/ToolGate.cs:23-30`, called from `RequireAsync` at `:43`). Strike P2's `memory migration:` renderer and re-point P3's fixture token in the same edit.

**M2 — P2's renderer cannot print P1's contract. The one component would be born unable to express the agreed output.**
Lane/section: P2 §1.1 (`CorpusEngineLines`) against P1 §2.3 arms 2 and 3.
Source: P2's `EngineLine` has exactly two shapes — `{Label} engine: {Model} ({Detail})` or `{Label} engine: {NotConfigured}`. P1 arm 2 requires a **bare** value (`memory engine: bundled`, no parenthetical); P1 arm 3 requires an appended em-dash clause when `embedding.apiKey` is absent. P2 then invents values to fill the mandatory parenthetical: `memory engine: bundled (local)` and `text-embedding-3-small (https://...)` (P2 §1.3 table), the second also dropping P1's `openai:` prefix taken from `SettingsCommands.cs:159`.
Correction: freeze P1's literals first, then derive the renderer from them: `CorpusEngineState` carries `string Value`, `string? Detail`, `string? Suffix`; `EngineLine` appends ` (Detail)` only when Detail is non-null and ` — Suffix` only when Suffix is non-null. One grammar, all four arms, no invented qualifiers.

**M3 — The event-id collision: P2's fallback block move is now unimplementable and must be struck from the integrated plan.**
Lane/section: P2 §7 + WP7 (move `DoctorCommands` to 1010-1012) vs P4 §5 (claim 1008-1013).
Source: read the test, not the lanes. `LoggerMessageEventIdTests.EventIds_AreUniqueAcrossTheAssemblies` (`tests/AiRaccoon.Tests/Unit/Observability/LoggerMessageEventIdTests.cs:18-31`) forbids any duplicate id at all — P2's 1010-1012 and P4's 1010-1013 are the same three ids twice. `EventIdBlocks_DoNotInterleaveBetweenOwners` (`:35-50`) groups by `OwnerOf` = outermost declaring type (`:141-150`) and fails on `a.Min <= b.Max and b.Min <= a.Max` (`:45`).
Correction: see Ruling 3. Doctor keeps 1000-1001 and takes no new id; P2 §7/WP7 is deleted rather than left as a conditional, because a conditional an implementer can reach is a collision an implementer can ship.

**M4 — The exit-code ruling is contradicted across lanes, and P3 has already encoded the losing side into its fixtures.**
Lane/section: P1 §3 Decisions A-E (exit 24 + new status word) vs P2 §4 (report-only) vs P3 A8 and its `EXIT_ON_OPEN_MIGRATION = ExitCode.Success`.
Source: `ExitCode.cs` highest value is 23 (`src/AiRaccoon/ExitCode.cs:71`), so 24 is free; today's exit is a total function of `report.Status` (`src/AiRaccoon/Setup/Cli/Commands/DoctorCommands.cs:77-98`) derived from schema shape and version skew only. The manual-checklist row P3 drafted already writes `report-only: 0` into an evidence artefact.
Correction: see Ruling 4 — the status line and the exit code both change. P3's fixture constant must be flipped to the new `ExitCode` member and its checklist row re-worded before any test is written against it, or the suite pins the rejected ruling.

**M5 — P4's constructor change costs 38 call sites in 28 files, none of which appear in its work-package table or its own minimum estimate.**
Lane/section: P4 §4.2 (call site 2) and §6 WP-P4-2, §7 minimum (`~40 lines of production change`).
Source: `EntryEmbedder` takes four parameters today (`src/AiRaccoon.Infrastructure/Embedding/EntryEmbedder.cs:17-21`). P4 adds three: `EmbedDrainReporter`, `ILogger<EntryEmbedder>`, `IOperationTelemetry`. Measured: 38 occurrences of `new EntryEmbedder(` across 28 files, including `tests/AiRaccoon.Tests/TestData.cs:78,137` and 26 other test files. P4's WP table lists only `EntryEmbedder.cs`, `EmbedDrainReporter.cs`, `AppRegistrations.cs`.
Correction: land a mechanical, behaviour-free first commit that routes every test construction through one factory (extend `TestData.cs:78,137` into `TestData.CreateEntryEmbedder(...)` and re-point the other 26 files), then change the constructor once. Add the 28 files to the WP table and make the gate the whole Fast lane, not a class filter.

**M6 — P4's progress stride does not compile.**
Lane/section: P4 §4.2 (`ProgressStride => SqliteModelMigrationLease.LeaseTtl`) and §6 WP-P4-5.
Source: `LeaseTtl` is a static property declared on the **interface** — `public static TimeSpan LeaseTtl { get; } = TimeSpan.FromSeconds(60);` at `src/AiRaccoon.Infrastructure/Embedding/IModelMigrationLease.cs:34`; `SqliteModelMigrationLease` is the implementing class at `:22` and declares no `LeaseTtl` of its own. C# does not surface a non-abstract interface static through an implementing class.
Correction: `IModelMigrationLease.LeaseTtl`. P4's prose cites the right file and its code sketch does not; the sketch is what an implementer copies.

**M7 — P2's WP1 acceptance criterion is red by construction, so WP1 has no valid gate.**
Lane/section: P2 §8 WP1 (`grep -c 'TableExistsAsync(connection, settings' DoctorCommands.cs` = 1).
Source: after the extraction there are still two guarded settings reads in the file — the shared corpus reader, and `ReadEmbeddingThreadsStateAsync`, which has its own copy at `src/AiRaccoon/Setup/Cli/Commands/DoctorCommands.cs:136`. That method is out of P2's extraction scope by P2's own §1 table.
Correction: either fold the threads read into the same guarded read (one settings-guard call taking a key list, threads resolved by `EmbeddingService.ResolveThreadCountForDisplay` afterwards) or restate the criterion as `exactly one guarded settings read per state object, two in the file`. `proof-of-done` requires the named gate to be able to pass.

**M8 — P4's S3 is misclassified: its fix is observability, not liveness, and the real defect is elsewhere and worse.**
Lane/section: P4 §2 S3, §5 (1012), §7 (`it is a liveness fix ... arguably belongs in a defect PR of its own`).
Source: a migration can never be *opened* with a blank provider — `ArgumentException.ThrowIfNullOrWhiteSpace(provider)` guards the entry point (`src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.ModelMigration.cs:28`). The only route into the state is `SettingsCommands.ModelResetAsync` (`src/AiRaccoon/Setup/Cli/Commands/SettingsCommands.cs:260-275`), which deletes `embedding.provider`, `.model`, `.baseUrl`, `.engine`, `.apiKey`, `.dimensions` directly and never inspects or closes an open `model_migration` row. After P4's change the row is still open forever, so `ToolGate.RequireBankAvailableAsync` still refuses every tool forever (`ToolGate.cs:25-29`). All 1012 does is turn a per-poll `526` (`MaintenanceJobRunner.cs:82-89`, deliberately unstamped so it retries) into a per-poll Warning.
Correction: (a) keep 1012 but label it as observability; (b) fix its Warning cadence — it fires on every 15s poll for as long as the row is open (`ModelMigrationJob.HasWorkAsync` is true while open, `ModelMigrationJob.cs:25-29`), the exact flood P4 rejected for S1 and S5 — so emit once per process per migration, or at Error once; (c) raise the actual defect as its own item: `model reset` must either refuse while a migration is open or close the row in the same transaction. That second option is a new state-machine edge and needs an ADR-0076 amendment (Ruling 6).

### SHOULD

**S1 — Both extractions must key on the corpus list the solution already has.**
Lane/section: P2 §1.1 (`string Label`) vs P4 (`EmbedCorpus`).
Source: `public enum EmbedCorpus { Memory, Code }` at `src/AiRaccoon.Infrastructure/Embedding/EmbedDrainRequest.cs:10-14`; the research record §7 already names it as the existing corpus dimension; `DoctorCommands.cs:3` already imports that namespace; `EmbedDrainService.cs:163` already derives a lowercase label from it as `corpus.ToString().ToLowerInvariant()`.
Correction: the doctor probe carries `EmbedCorpus Corpus` and derives its label with the same expression. One corpus list for both extractions — this is the single axis on which the two components could otherwise drift, and it costs nothing to remove.

**S2 — Reject P1's placement of the shared component in Infrastructure.**
Lane/section: P1 §1.1 (`internal static class EngineDoctor` in `src/AiRaccoon.Infrastructure/Sqlite/`) vs P2 §1.1 (`src/AiRaccoon/Setup/Diagnostics/`).
Source: P1's justification — that Core cannot host it and Infrastructure is therefore the only home — skips the CLI project. The reads use `MemorySql` constants and Dapper, both already referenced from `DoctorCommands.cs:5,7`; and unit-testability, P1's other argument, is already granted in the CLI project by `src/AiRaccoon/AiRaccoon.csproj:57` (`InternalsVisibleTo AiRaccoon.Tests`).
Correction: keep the probe, the state and the renderer in `src/AiRaccoon`, namespace `AiRaccoon.Setup.Diagnostics` (P2's placement). Infrastructure must not own how a CLI verb words a line; `screaming-architecture` puts doctor's grammar next to doctor.

**S3 — Delete the `Describe` delegate: it is the per-corpus branch, moved into a field.**
Lane/section: P2 §1.1 / §1.3.
Source: P2 §1.0 promises `no if (corpus == memory) anywhere in the reader or the renderer`, then gives the descriptor a `Func<EngineSettings, EngineDisplay> Describe` whose two values are `settings => new EngineDisplay(ModelNameFor(...), ...)` for code and `DescribeMemoryEngine` for memory. That is the branch, one indirection later, and it drags in `EngineSettings` and `EngineDisplay` whose only purpose is to be its parameter and return type.
Correction: two types instead of four — a probe record of plain strings and a state record — with the arm selection inside the single renderer, switching on the state's own values. `ask-if-simpler`; it also deletes the two record declarations that exist only to serve the delegate.

**S4 — Collapse the third spelling of the open-migration predicate while the subsystem is open.**
Lane/section: not covered by any lane; touches P2 §3 and P4.
Source: `MemorySql.HasOpenModelMigration` is `SELECT count(*) FROM model_migration WHERE id = 1 AND finished_at IS NULL` (`MemorySql.cs:474-475`); `ModelMigrationJob.HasWorkAsync` inlines the identical statement as a literal (`src/AiRaccoon.Infrastructure/Maintenance/ModelMigrationJob.cs:26-29`). Doctor is about to become a third reader of the same fact.
Correction: point `ModelMigrationJob.HasWorkAsync` at the constant in the same PR — one line, no behaviour change. This is precisely the duplication the owner's constraint targets, sitting inside the subsystem being planned.

**S5 — Read the API key's presence, never its value.**
Lane/section: P1 §2.3 arm 3, §4 dependency table.
Source: `embedding.apiKey` is a persisted OpenAI key (`src/AiRaccoon.Infrastructure/Embedding/EmbeddingSettingsKeys.cs:17`). P1's dependency table lists it among `4 scalar reads` through `ReadSettingAsync` (`DoctorCommands.cs:182-185`), which returns the value.
Correction: read presence with an `EXISTS` form so the secret never enters the doctor process's heap or a `SqliteException` message. The report only ever needs the boolean.

**S6 — Land the `unreadable` arm, but as its own commit, with its test promoted from optional to required.**
Lane/section: P1 §1.3 and §5 Ruling 5c (fix now) vs P2 §1.4 and WP5 (conditional, last).
Source: today an unreadable or absent `settings` table renders as `code engine: not configured — run ...` via `DoctorCommands.cs:116-118` and `:124-127` — a false remedy on exactly the broken bank doctor exists for. No test reaches that branch: the 11 tests are substring assertions on stdout (`DoctorCommandsTests.cs`, class-level `Integration`/`Slow` at `:23-24`).
Correction: keep it a separate work package (P2 is right that it changes existing output and must not ride inside a refactor), but make it non-optional and make P3's shape-broken-bank row a required test, since it is the first coverage that branch has ever had.

**S7 — Keep the under-lease re-check when the pre-read is added.**
Lane/section: P4 §4.2 (call site 2 reorder).
Source: today the lease is acquired first (`EntryEmbedder.cs:102-105`) and the open-migration re-check happens **under** the lease (`:109-114`), which is what makes it race-free. P4's sketch reads state before acquiring and returns 1011 from the pre-read.
Correction: add the pre-read for the stale-lease fields only; keep `:109-114` where it is. Otherwise the reorder trades a documented race guard for a log line.

**S8 — Cut P2's WP6 (`UnixTime`).**
Lane/section: P2 §6, WP6.
Source: the formatter is one expression — `DateTimeOffset.FromUnixTimeSeconds(unixSeconds).UtcDateTime.ToString(...)` at `src/AiRaccoon/Setup/Cli/Commands/WatchCommands.cs:185`. P2 recommends extracting it because the lane's premise is not creating second copies.
Correction: the owner's constraint is about read/format/log/metric *logic*, not a `ToString` format. Take P2's option (a) — a second one-line private with a comment naming the twin — and drop WP6 with its `WatchCommands` test blast radius. `ask-if-simpler`.

**S9 — Prove `SelectModelMigration`'s aliases in a Fast test, not only through the Slow doctor lane.**
Lane/section: P2 §0.3, R9.
Source: confirmed — the only occurrence of `SelectModelMigration` in `src/` is its own declaration (`MemorySql.cs:470`). Doctor becomes its first executor ever, and the doctor suite is `Slow` (`DoctorCommandsTests.cs:23-24`), so the Fast lane would never touch it.
Correction: one Fast test executing the statement against an in-memory bank with a seeded row, asserting the `StartedAt`/`FinishedAt` mapping. A wrong alias otherwise surfaces first in the slow lane, or in production.

**S10 — If exit 24 lands, derive the how-to's exit-code table instead of editing it.**
Lane/section: P1 §3.4, P2 WP4.
Source: `docs/how-to/configure-ai-raccoon-server.md` carries a six-row hand-maintained mirror of `ExitCode.cs` (rows `0`, `1`, `2`, `19`, `20`, `22`) directly under the sentence `Exit code is 0 when healthy and non-zero on a mismatch, so it composes into a script`. P1 itself calls the table a `derive-or-delete-the-list` liability and then proposes adding a row by hand.
Correction: add a test that reads the table and compares it with the `ExitCode` members doctor can return — the repo already has this pattern for a quoted command (`tests/AiRaccoon.Tests/Unit/Setup/DefaultCodeModelCommandTests.cs`).

### NICE

**N1** — Fix the stale healthy sample in the same WP4 edit: it shows four lines at `user_version: 10 (this binary: 10)` and `application_id: -519479064` while the binary prints six at v11 (`docs/how-to/configure-ai-raccoon-server.md`, the block under `A healthy bank:`). Adding memory lines to a stale block is worse than the stale block.

**N2** — P4's reporter listing omits `SelfReSignalNotQueued` although its own call-site snippet calls it (P4 §4.2); id 1007 exists at `EmbedDrainService.cs:213-215`. Complete the surface so the move is provably total.

**N3** — `README.md:40` (`doctor shows the effective thread count`) stays true; the memory lines are a `What is new` row at release time (`traceable-releases`), which no lane's WP list carries.

**N4** — P3's dropped-`settings` expectation (`memory-engine line reports the bundled fallback`) is a *third* answer for that state, after P1's `unreadable` and P2's `not configured`. Reconcile to the M2/S6 ruling before the test is written.

**N5** — `DoctorCommandsTests.cs:69` asserts `outp.ShouldContain(HEALTHY)` — a fresh bank has no `model_migration` row, so it survives Ruling 4 untouched; but the new status arm must be asserted together with the new exit constant in one test, or the ruling is pinned in neither direction. P3 spotted the weak assertion; it did not spot that the pairing is what pins the decision.

---

## 2. Ruling 1 — the extraction, judged twice

**Two components, not one. The boundary is right; the corpus concept must be single.**

After the change these types exist, and no others:

| type | kind | project | namespace |
|---|---|---|---|
| `CorpusEngineProbe` | internal sealed record, plain string fields + `EmbedCorpus` | `src/AiRaccoon` | `AiRaccoon.Setup.Diagnostics` |
| `CorpusEngineState` | internal sealed record (`Value`, `Detail`, `Suffix`, `PendingRows`) | `src/AiRaccoon` | `AiRaccoon.Setup.Diagnostics` |
| `CorpusEngineLines` | internal static class, pure functions only | `src/AiRaccoon` | `AiRaccoon.Setup.Diagnostics` |
| `EmbedDrainReporter` | sealed partial class holding the whole moved `Log` block | `src/AiRaccoon.Infrastructure` | `AiRaccoon.Infrastructure.Embedding` |

The reader stays a `private static` method on `DoctorCommands` (`src/AiRaccoon/Setup/Cli/Commands/DoctorCommands.cs`), which already owns the connection — P2 is right that a type whose only state is a borrowed `SqliteConnection` is not worth inventing, and `static-classes` forbids I/O in the static renderer.

**Why they are not one component.** They share the word corpus and nothing else. Doctor's component answers `what does this bank's settings row say and how is that line worded`, over a read-only connection, producing stdout. The reporter answers `emit this pass's log line and its two measurements`, producing `ILogger` and `IMeasurementRecorder` calls (`EmbedDrainService.cs:161-169`). Merging them either puts CLI wording into Infrastructure (S2) or metrics recording into the CLI verb; either way it is one type with two unrelated reasons to change. The reporter is also injected into two background drainers and must never be reachable from a report path that must not write anything.

**Will they drift?** On exactly one axis — the corpus list — and that axis is removable today: `EmbedCorpus` already exists as a public enum in `Infrastructure.Embedding` (`EmbedDrainRequest.cs:10-14`), is already imported by `DoctorCommands.cs:3`, and already produces the lowercase label at `EmbedDrainService.cs:163`. P2's `string Label` is a second corpus list and is the finding (S1). With both keyed on `EmbedCorpus`, adding a third corpus touches one enum and two descriptor tables, and nothing silently disagrees.

**Layering, checked rather than asserted.** `AiRaccoon.Core` takes no Infrastructure dependency in either plan: the only Core addition proposed is `EmbeddingEngineSetup` (P2 WP2b), a const-only static class mirroring `CodeEngineSetup` (`src/AiRaccoon.Core/Memory/Code/CodeEngineSetup.cs:9-15`) — strings flow out of Core, never in. Doctor stays in the `AiRaccoon` (Setup) project. `EmbedDrainReporter` sits in the same namespace as both its call sites, so it adds no project edge. The one layering violation in the plan is P1's placement of doctor's grammar in `Infrastructure/Sqlite/` — rejected (S2).

---

## 3. Ruling 2 — is the extraction actually warranted

Counted from source, in `DoctorCommands.cs`, this is what a hand-written memory twin would duplicate:

| duplicated concern | lines today | count |
|---|---|---|
| guarded settings read + `catch (SqliteException)` | `:114-127` | 14 |
| pending-count method | `:170-180` | 11 |
| state record | `:207-208` | 2 |
| engine line format strings | `:67-69` | 3 |
| pending line format string | `:73-74` | 2 |
| **total second copy** | | **32** |

Against that, P2's machinery as drawn in its §1.1 and §1.3: four type declarations plus renderer, about 36 lines; two probe descriptors, about 18; `DescribeMemoryEngine`, about 12 — roughly **66 lines plus a `Func` field**, i.e. about twice the duplication it removes. Judged strictly on line count, `ask-if-simpler` bites and P2's shape loses.

It does not follow that the duplication is cheaper, because the 32-line figure is the *code* twin, not the *memory* twin: the memory corpus needs three settings keys, four arms, and (P1) a suffix clause, so a straight copy is nearer 45 lines and every later field lands twice.

**Verdict: extract, in the smaller shape.** Named precisely:

- One probe record of plain strings keyed by `EmbedCorpus` — label, configured key, model key, base-url key, corpus table, pending SQL, not-configured text. No `Func`.
- One state record with a nullable `Detail` and nullable `Suffix`.
- One reader (`private static` on `DoctorCommands`) and one renderer with the arm switch inside it.

That is two types instead of four, no delegate field, and it removes the same guard, the same catch, the same count and both format strings. It also lands at roughly the line count of the duplication rather than double it, which is the honest test the invariant asks for. The owner asked for extraction; this says the extraction is right and P2's parameterisation is one indirection too clever — the `Describe` delegate is the corpus branch with a lambda wrapped round it (S3).

---

## 4. Ruling 3 — event-id allocation

**Allocation, exactly:**

| owner | block | status |
|---|---|---|
| `DoctorCommands` | 1000-1001 | unchanged. **Doctor needs no new id at all.** |
| `EmbedDrainReporter` | 1002-1013 | 1002-1007 moved verbatim from `EmbedDrainService.cs:195-215`; 1008-1013 new |
| `EmbedDrainService`, `EntryEmbedder` | none | declare no `[LoggerMessage]` after the move |

**Does doctor need an id?** No. Both existing extra reads swallow `SqliteException` silently and let the report carry the signal (`DoctorCommands.cs:124-127`, `:142-146`); doctor's stdout *is* the artefact. P1 §4 and P2 §7 both land there independently, and I agree — but P2 then leaves a documented fallback (block move to 1010-1012, WP7) that now collides head-on with P4's 1010-1013. Both lanes computed `1008 is next free` correctly (measured: max 1007, zero duplicates, 168 declarations); only one of them may spend it, and doctor is not spending anything.

**What the block-interleave test actually forces** — read at `LoggerMessageEventIdTests.cs:35-50`, owner resolution at `:141-150`:

1. **Move all six or none.** Owner is the outermost declaring type, so leaving 1004 and 1006 on `EmbedDrainService` yields `[1004,1006]` nested inside `EmbedDrainReporter [1002,1013]`, and `:45` fails. P4's §4.2 reasoning is correct and is the binding constraint on the refactor.
2. **Doctor may not take 1008.** `[1000,1008]` versus `[1002,1007]` overlaps, so a doctor log line is impossible without a block move — which is why the answer is no log line, not a bigger commit.
3. **Reusing 1003 and 1005 from a second call site is legal and invisible to all four tests.** `Entries()` (`:129-138`) reflects over *declarations*, not call sites, so a second caller adds no entry, no duplicate and no range change. P4's reuse is sound.
4. **The registry edits are gated, and both directions go red.** `DocumentedCount_MatchesTheMeasuredCount` (`:68-75`) parses the bold count in `docs/reference/logging-event-ids.md:12` — re-measured today as **168**, so six new declarations means **174**, and the doc must move in the same commit as the code. `EveryEventIdInSource_FallsInsideADocumentedBlock` (`:78-92`, parser `:105-126`) expands a `1002-1013` block through its dash branch, so rewriting the single row at `:84` suffices; the row must stay anchored on a digit at line start or the parser skips it.
5. **Keep the `1000-1001` row.** Nothing in the suite notices a documented-but-unused id, so a doctor block move would leave a silent lie in the registry — one more reason it stays where it is.

---

## 5. Ruling 4 — the exit-code and status ruling

**The report does not stay report-only. The status word changes and one new advisory exit code appears.**

- `ExitCode.ModelMigrationOpen = 24`, free (`src/AiRaccoon/ExitCode.cs:71` is the highest at 23; 8 is retired at `:17-19`).
- Reachable **only** from the `Healthy` arm of the existing switch (`DoctorCommands.cs:84-86`); 19 (`ExitCode.cs:53-54`) and 20 (`:56-57`) keep precedence, so `exit == 24` is itself a positive statement that the schema shape is clean.
- Emitted **only** on a positively-read open row. Table absent, no row, `finished_at` set, or `SqliteException` all print the `unreadable`/`none open` arm and exit `Success`. This keeps the written rationale at `DoctorCommands.cs:111-113` intact where it matters — a shape-broken bank still cannot be given a wrong verdict by an extra read — while narrowing its letter for the one read that is a positive fact about the bank rather than an artefact of its damage. That narrowing is recorded in the doc comment, not left to look like an oversight.
- Status line: `status: MIGRATION IN PROGRESS (schema shape is healthy; MCP tool calls are refused until the re-embed finishes)`. `status: HEALTHY` must not print alongside a non-zero exit; a verdict that contradicts its own code is the defect restated.

**Why not report-only** (P2 §4, P3 A8). The trigger the owner gave is two facts joined: `prints status: HEALTHY` **and** `exit 0`, while every MCP call is refused. Printing three new lines closes the first and leaves the second exactly as it was. The bank in that window is not usable for tool traffic — ADR-0076 says so in its own words, `the server cannot proceed in a degraded state, only in a refusing one` (`docs/adr/0076-model-set-is-an-outbox-drained-by-an-on-demand-relay.md:123-131`) — so `0`, documented as `HEALTHY`, is a false statement about the bank, and it is the statement scripts consume.

**Precedent, not a new category.** `SchemaNewerThanBinary = 20` is already a non-zero code for a legitimate, transient, self-clearing state whose remedy is in the line itself (`ExitCode.cs:56-57`, `DoctorCommands.cs:79-82`). An open migration is the same species.

**Consequence for scripts, stated plainly.** Today `doctor; if [ $? -eq 0 ]` reads as `bank fine`. After this change that predicate returns false for the whole duration of a re-embed — on the owner's bank, 47,723 of 51,947 rows. That is the intended behaviour change: during that window the bank refuses every tool, so a script that proceeds is a script querying a bank whose search is dead. Callers wanting the pure schema verdict still have it, because 19 and 20 outrank 24 and 24 only fires on a clean shape. The documented contract at `docs/how-to/configure-ai-raccoon-server.md` — the sentence `Exit code is 0 when healthy and non-zero on a mismatch, so it composes into a script`, plus its six-row table — must gain the row and lose the word `mismatch` in the same commit, and per S10 that table should be derived rather than hand-edited. `README.md` gets its `What is new` row at release.

**If review overturns this**, P1 §3.5 is right that lines 4, 7 and 9 stand unchanged — but then P3's `EXIT_ON_OPEN_MIGRATION` must still be a named constant asserted in every migration test, so the ruling is pinned rather than assumed (M4).

---

## 6. Ruling 5 — scope: two tasks, three PRs

**Split. P4 is not this task.**

The owner's request — `doctor: add memory embedding features` — is **P1 + P2 + P3**: one CLI verb's report, one file plus one new file, plus docs. P4 is the drain relay: `EntryEmbedder.cs`, `EmbedDrainService.cs`, `AppRegistrations.cs`, `MemorySql.cs`, the logging registry, and 28 test files (M5). The two share **no** source file. Their only overlap was the event-id registry, and since doctor takes no new id (Ruling 3) even that disappears. Nothing in P4 is required for doctor to report the truth, and nothing in doctor is required for the relay to log.

- **PR 1 — doctor reports the memory engine** (P1 literals, P2 shape minus WP6/WP7, P3 tests). This is the owner's request and it alone closes the trigger.
- **PR 2 — the relay reports itself**: P4 WP-1, WP-2, WP-6 (extract the reporter, 1008 plus reuse of 1003/1005, registry), preceded by the mechanical test-construction commit from M5. 1009 and 1013 may ride along; 1010/1011 are Debug and can wait.
- **PR 3 — the permanent-lockout defect**: `model reset` versus an open migration (M8). This is neither observability nor a report; it is the only item that leaves a bank unusable forever, and it carries an ADR question (Ruling 6).

P4's own §7 minimum already suspects 1012 belongs in a defect PR of its own. The correction is that 1012 is not the fix — the fix is in `ModelResetAsync`/the relay's completion rule — so PR 3 is about that, and 1012 travels with PR 2 as the observability it actually is.

---

## 7. Ruling 6 — ADR

- **New ADR for the doctor report: no.** It reports state ADR-0076 already defines and opens the bank read-only exactly as ADR-0075 requires (`DoctorCommands.cs:187-205`, never `MemorySchema.EnsureAsync`).
- **Amendment to 0075: no.** ADR-0075 governs writes; every statement added is a `SELECT` and the connection is `SqliteOpenMode.ReadOnly` (`DoctorCommands.cs:190-194`).
- **Amendment to 0076 for the doctor lines: no.** Reading the outbox row is not a change to it; ADR-0076's `only supported way to observe or drive it is by reading/writing the model_migration row` (`0076:61-62`) explicitly blesses the read.
- **Amendment to 0076 for P4 Option B: no.** The outbox transaction (`EntryEmbedder.cs:50-88`), the `MarkAllEmbeddedPending` side effect (`MemorySql.cs:397-399`) and the finish write (`EntryEmbedder.cs:132-134`) are untouched. P4's Option A **would** have required one, and its §4.1.2 says so correctly — the pass predicate `drained >= rowsPerRun` is not `batch.Count == 0`, so completion would have to be redefined against `mark it finished only on completion` (`0076:24-25`).
- **Amendment to 0076 required for PR 3: yes, if the ruling closes the row.** ADR-0076 accepts the consequence in writing — `a permanently failing migration means a permanently refusing server ... the correct trade` (`0076:129-131`). A blank-provider migration has no `completion`, so any change that closes the row without embedding the backlog, or that lets `model reset` clear it, is a new state-machine edge and must be recorded as an amendment (`state-transitions-through-a-machine`).
- **ADR-0076 versus P4's progress heartbeat (1013): no conflict, but say so.** The ADR's `No progress reporting` is about the CLI not streaming or polling (`0076:19-20`); a server-side log line is not a CLI progress channel. Put that sentence in the WP so a later reviewer does not read 1013 as contradicting an Accepted ADR.
- **Exit code 24: no ADR.** `ExitCode` records rationale in its own doc comments citing issues and review rounds (`ExitCode.cs:53,56,64-66,68-71`). P1 §7 is right on this point.

---

## 8. Ruling 7 — what the lanes missed

From my own reading, ordered by whether they make the plan **wrong** rather than incomplete.

1. **`model reset` can strand an open migration forever, and no lane knows it.** `ModelResetAsync` (`SettingsCommands.cs:260-275`) deletes six `embedding.*` rows through `store.DeleteSettingAsync` with no migration check and no close of the outbox row; a migration can never be born in that state (`SqliteMemoryStore.ModelMigration.cs:28`). Result: `model embedding set local` then `model reset` leaves `finished_at NULL` forever, `ToolGate` refusing every tool forever (`ToolGate.cs:25-29`), the relay throwing every 15s (`EmbeddingService.cs:103-104` into `MaintenanceJobRunner.cs:82-89`). P4 found the symptom and mislabelled it; nobody found the trigger or the permanence. **This makes P4's plan wrong**, because its S3 fix removes the exception and leaves the outage.
2. **P4's stride does not compile** (M6) and **P4's constructor change is 38 sites in 28 files** (M5). Either one turns P4's `does not parallelise, sequential commits` plan into something quite different from what its WP table gates.
3. **P2's WP1 gate cannot pass** (M7): the second guarded settings read at `DoctorCommands.cs:136` is outside the extraction, so the criterion `grep -c ... = 1` is red by construction. A named gate that cannot pass is a `proof-of-done` failure, not a typo.
4. **`EmbedCorpus` was in front of both extraction lanes and neither used it** (S1), even though the research record §7 names it as the corpus dimension and `EmbedDrainService.cs:163` already derives the label from it. This is the one place the two extractions can drift, and it was avoidable for free.
5. **A fourth spelling of the open-migration predicate is being added while a third sits unfixed**: `ModelMigrationJob.cs:26-29` inlines what `MemorySql.cs:474-475` already declares (S4).
6. **P1 and P2 were written to different contracts, and P3 tested a third.** P2 hedges the wording, the remedy string, the migration subject and the exit code as `P1 rulings`, then commits code sketches with concrete different literals (M1, M2); P3 froze P2's side into fixture constants and a checklist artefact (M4, N4). The integration step must freeze P1's literals first and re-derive P2's renderer from them — otherwise the single component ships with a grammar that cannot print the agreed contract.
7. **The API key value would be read into a report process** (S5) — nobody flagged it, and `doctor` is the one verb an operator runs while pasting output into an issue.
8. **The only status assertion on doctor's happy path is a substring** (`DoctorCommandsTests.cs:69`), so the new status arm and the new exit code must be asserted **together** or the Ruling 4 decision is pinned in neither direction (N5).

---

## SCHEMA-LAST

### Table 1 — findings

| id | severity | lane/section | finding | correction |
|---|---|---|---|---|
| M1 | MUST | P1 §2.3 / P2 §2 / P3 fixtures | Three different literals for the migration line (`model migration:` vs `memory migration:`) and two different qualifiers | Adopt `model migration: open since <UTC> (all MCP tool calls are refused until it finishes)` per `MemorySql.cs:470-472`, `MemorySchema.cs:399-409`, `ToolGate.cs:23-30`; re-point P2's renderer and P3's token |
| M2 | MUST | P2 §1.1 / §1.3 vs P1 §2.3 | `CorpusEngineLines.EngineLine` can only emit `Model (Detail)` or the not-configured text — it cannot print P1's bare `bundled` arm or the API-key suffix, so P2 invents `bundled (local)` and drops `openai:` | Freeze P1's literals, then shape the state as `Value` + nullable `Detail` + nullable `Suffix`; renderer appends each only when present |
| M3 | MUST | P2 §7 + WP7 vs P4 §5 | Both lanes spend 1008+; P2's fallback block move to 1010-1012 duplicates P4's 1010-1013 → `EventIds_AreUniqueAcrossTheAssemblies` (`:18-31`) and the interleave test (`:35-50`) both red | Delete P2 §7/WP7. Doctor keeps 1000-1001 and takes no new id; reporter owns 1002-1013 |
| M4 | MUST | P1 §3 vs P2 §4 vs P3 A8 | Exit-code ruling contradicted across lanes, and P3 already encoded `EXIT_ON_OPEN_MIGRATION = Success` into fixtures and a checklist artefact | Adopt Ruling 4 (`ModelMigrationOpen = 24`, `ExitCode.cs:71` is 23); flip P3's constant and checklist row before any test is written |
| M5 | MUST | P4 §4.2, §6 WP-P4-2, §7 | 3 new ctor params on `EntryEmbedder` (`EntryEmbedder.cs:17-21`) = 38 `new EntryEmbedder(` sites in 28 files, absent from the WP table and the `~40 lines` estimate | Mechanical first commit routing tests through `TestData` (`TestData.cs:78,137`), then one ctor change; list the 28 files; gate on the whole Fast lane |
| M6 | MUST | P4 §4.2, WP-P4-5 | `SqliteModelMigrationLease.LeaseTtl` does not compile — `LeaseTtl` is an interface static (`IModelMigrationLease.cs:34`), impl at `:22` | Use `IModelMigrationLease.LeaseTtl` in the sketch an implementer copies |
| M7 | MUST | P2 §8 WP1 | Acceptance grep (`TableExistsAsync(..., settings)` = 1) is red by construction: `DoctorCommands.cs:136` holds a second copy outside the extraction | Fold the threads read into the shared guarded read, or restate the criterion as two guarded reads; a gate must be able to pass |
| M8 | MUST | P4 §2 S3, §5, §7 | 1012 is called a liveness fix; the row stays open and `ToolGate.cs:25-29` refuses everything forever. Also Warning-per-15s-poll (`ModelMigrationJob.cs:25-29`) — the flood P4 rejected for S1/S5 | Relabel 1012 as observability, emit once per process per migration, and raise the real defect (`SettingsCommands.cs:260-275`) as its own PR with an ADR-0076 amendment |
| S1 | SHOULD | P2 §1.1 vs P4 | `string Label` is a second corpus list beside `EmbedCorpus` (`EmbedDrainRequest.cs:10-14`), the axis on which the two extractions can drift | Key the probe on `EmbedCorpus`; derive the label with `corpus.ToString().ToLowerInvariant()` as `EmbedDrainService.cs:163` already does |
| S2 | SHOULD | P1 §1.1 vs P2 §1.1 | `EngineDoctor` in `Infrastructure/Sqlite/` puts CLI output grammar in Infrastructure; P1's `only correct home` argument skips the CLI project, which already has `InternalsVisibleTo` (`AiRaccoon.csproj:57`) | Keep probe/state/renderer in `src/AiRaccoon`, namespace `AiRaccoon.Setup.Diagnostics` |
| S3 | SHOULD | P2 §1.1, §1.3 | `Func<EngineSettings, EngineDisplay> Describe` is the per-corpus branch as a field, and drags in two types that exist only to serve it | Delete the delegate and both types; switch the four arms inside the one renderer |
| S4 | SHOULD | none (P2 §3, P4) | `ModelMigrationJob.cs:26-29` inlines `MemorySql.HasOpenModelMigration` (`:474-475`); doctor becomes a third reader | Point the job at the constant in the same PR — one line, no behaviour change |
| S5 | SHOULD | P1 §2.3 arm 3, §4 | The plan reads `embedding.apiKey`'s value (`EmbeddingSettingsKeys.cs:17`) through `ReadSettingAsync` (`DoctorCommands.cs:182-185`) for a presence test | Read presence with an `EXISTS` form; the secret never enters the report process |
| S6 | SHOULD | P1 §1.3/§5c vs P2 §1.4/WP5 | The false-remedy bug (`DoctorCommands.cs:116-118,124-127`) is a real fix but changes existing output, and no test reaches that branch today | Land it as its own commit after the memory lines; promote P3's shape-broken-bank row from optional to required |
| S7 | SHOULD | P4 §4.2 | The reorder replaces the under-lease open re-check (`EntryEmbedder.cs:109-114`) with a pre-lease read | Add the pre-read for lease fields only; keep the re-check under the lease |
| S8 | SHOULD | P2 §6, WP6 | A shared one-line timestamp formatter (`WatchCommands.cs:185`) is not the duplication the owner's constraint targets | Cut WP6; take a second one-line private naming the twin |
| S9 | SHOULD | P2 §0.3, R9 | `SelectModelMigration` has never executed (only `MemorySql.cs:470` matches), and the doctor suite is `Slow` (`DoctorCommandsTests.cs:23-24`) | One Fast test executing the statement against a seeded row to pin the aliases |
| S10 | SHOULD | P1 §3.4, P2 WP4 | The how-to's exit-code table is a hand-maintained mirror of `ExitCode.cs`; the plan adds a row by hand | Derive it in a test, following `DefaultCodeModelCommandTests` |
| N1 | NICE | P2 WP4, R5 | Healthy sample is 4 lines at `user_version: 10` / `application_id: -519479064` | Fix the drift in the same edit that adds the new lines |
| N2 | NICE | P4 §4.2 | Reporter listing omits `SelfReSignalNotQueued` (1007, `EmbedDrainService.cs:213-215`) though the call site uses it | Complete the surface so the block move is provably total |
| N3 | NICE | P2 WP4 / P4 | No lane carries the release note (`README.md:40` context) | Add the `What is new` row at release (`traceable-releases`) |
| N4 | NICE | P3 | Dropped-`settings` expectation is a third literal (`bundled fallback`) beside `unreadable` and `not configured` | Reconcile to the M2/S6 ruling before writing the test |
| N5 | NICE | P3, `DoctorCommandsTests.cs:69` | `ShouldContain(HEALTHY)` is the only happy-path status assertion | Assert the new status arm and the new exit constant in one test |

### Table 2 — recommended integrated work-package sequence

| step | what | why this order | gate |
|---|---|---|---|
| 0 | Freeze the output contract: P1's literals for lines 4, 7, 9 and 11, the `unreadable` wording, and Ruling 4's exit code, as one table in the task doc with an acceptance criterion per line | M1/M2/M4 are literal conflicts; every later WP is derived from these strings, and P2's renderer shape depends on them | Owner sign-off on the frozen table; `proof-of-done` — no line without its criterion |
| 1 | PR 1, commit A: RED unit test for `CorpusEngineLines` using today's three code-engine strings as literals; then extract probe + state + renderer (S1, S2, S3 shapes) and re-point the code path | The refactor must be pinned before the memory arms exist, and the 11 argv tests can only prove wording, not shape | `TESTINGPLATFORM_TELEMETRY_OPTOUT=1 dotnet test --project tests/AiRaccoon.Tests --filter 'FullyQualifiedName~CorpusEngineLinesTests'` then `--filter 'FullyQualifiedName~DoctorCommandsTests'` — 11 green with that file unmodified |
| 2 | PR 1, commit B: `EmbeddingEngineSetup` const in `AiRaccoon.Core/Memory/`, re-point `EmbeddingService.cs:364`; doctor quotes it | The not-configured arm must not become the fifth hand-spelling of `ai-raccoon model embedding set local` (`EmbeddingAvailability.cs:34,37`, `BundledModel.cs:88,98`, `EmbeddingService.cs:364`) | `--filter 'Category=Unit&Speed=Fast'` plus a constant-agreement test in the `DefaultCodeModelCommandTests` pattern |
| 3 | PR 1, commit C: `memory engine:` (four arms, presence-only API-key read per S5) and `memory rows pending:` | Second configuration of the frozen probe; no new SQL — `MemorySql.CountPendingEmbed` (`:362-363`) is index-served by `idx_entries_embed_state` | `--filter 'FullyQualifiedName~DoctorCommandsTests'` with seeded provider/model/pending rows |
| 4 | PR 1, commit D: `model migration:` line, unconditional, three states, separate `try`; collapse `ModelMigrationJob.HasWorkAsync` onto the constant (S4); add the Fast alias test (S9) | The line must exist before the exit code can be derived from it; the pending count must survive a broken `model_migration` read | `--filter 'FullyQualifiedName~DoctorCommandsTests'` plus the new Fast alias test |
| 5 | PR 1, commit E: `ExitCode.ModelMigrationOpen = 24`, the new status arm, the derived exit-code doc test (S10) | Depends on step 4's positively-read open row; must be asserted together with the status string (N5) | `--filter 'FullyQualifiedName~DoctorCommandsTests'` plus the exit-table derivation test |
| 6 | PR 1, commit F: the `unreadable` arm for both corpora (S6) | Changes existing code-engine output, so it must not ride inside the refactor or the memory commits | `--filter 'FullyQualifiedName~DoctorCommandsTests'` including the shape-broken-bank test |
| 7 | PR 1, commit G: docs — healthy sample rebuilt from real output at v11 (N1), exit-code row, embedding how-to and reference sentences if step 2 changed what they quote | Content depends on the final strings; the stale v10 block must not be extended | `dotnet run --project src/AiRaccoon -- --data-root <scratch> doctor` diffed against the block, then `--filter 'FullyQualifiedName~DefaultCodeModelCommandTests'` |
| 8 | PR 2, commit A: mechanical `TestData.CreateEntryEmbedder(...)` factory across the 28 files (M5) — no behaviour change | Makes the constructor change a one-file edit afterwards; keeps the reporter commit reviewable | `--filter 'Speed=Fast&Performance!=Benchmark'` green with no production change |
| 9 | PR 2, commit B: extract `EmbedDrainReporter`, moving all of 1002-1007 verbatim, plus the registry row and count 168 → 174 in the same commit | The interleave test forces all-or-nothing (`LoggerMessageEventIdTests.cs:35-50`), and the two registry tests go red in both directions if code and doc split | `--filter 'FullyQualifiedName~LoggerMessageEventIdTests'` then `--filter 'Speed=Fast&Performance!=Benchmark'`; assert log **category** is unchanged |
| 10 | PR 2, commit C: migration drain start/finish/failure — 1008 new, 1003 and 1005 reused, `embed.drain` span with `RecordRows` before `Succeeded` (`EmbedDrainService.cs:137-141`), `IModelMigrationLease.LeaseTtl` per M6, re-check kept under the lease per S7 | This is P4's actual deliverable and the smallest thing that makes `ran in 1 ms` decisive | `--filter 'Speed=Fast&Performance!=Benchmark'` |
| 11 | PR 2, commit D (optional): 1009 stale-lease reclaim reproducing the owner's row, then 1013 time-strided heartbeat; 1010/1011 last, Debug; 1012 as observability with a once-per-migration cadence (M8) | Each is independently valuable and each serialises on the same two files; 1009 is the line that would have named the owner's bank out loud | `--filter 'Speed=Fast&Performance!=Benchmark'`; the 1013 test asserts **zero** lines inside one stride |
| 12 | PR 3: the permanent-lockout defect — `model reset` versus an open `model_migration` row — with the ADR-0076 amendment if the ruling closes the row | Different subsystem, different decision, and the only item that leaves a bank unusable forever; must not ride inside a report PR | RED first: `model embedding set local` then `model reset` then a relay pass — assert the bank is usable again; plus the ADR amendment recorded in `docs/adr/0076-*.md` |

*Never pass `--nologo` to any of these commands: under Microsoft.Testing.Platform it is an unmatched token and produces `Zero tests ran` with a success-shaped exit.*
