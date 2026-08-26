# LANE P3 — test design and gates

MoE planning lane P3 of task `doctor-feature-match`, 2026-08-26. Written by a planning subagent
against the task worktree; reviewed in the same task's review round. Sibling lanes: P1 (output contract), P2 (implementation shape), P4 (runtime observability parity).
Companion research record: `2026-08-26-doctor-memory-embedding-research.md`.

---

`ai-raccoon doctor` reports the MEMORY-embedding engine state the way it already reports the CODE
engine, **via a common component extracted from the existing code path** (owner correction).

This lane designs the proof only. Wording belongs to the output-contract lane; production structure
belongs to the implementation lane. Every assertion below is bound either to a **named production
constant** or to an explicitly flagged **wording assumption**.

---

## 0. Evidence base — verified in this worktree, not assumed

| Fact | Source read |
|---|---|
| doctor's whole output surface is 7 lines (6 fixed + 1 status branch) | `src/AiRaccoon/Setup/Cli/Commands/DoctorCommands.cs:61-99` |
| the two "extra state" reads are each wrapped in `TableExistsAsync` + `catch (SqliteException)`, and the code says in words that they must never decide the exit code | `DoctorCommands.cs:108-147`, comment `:111-113` |
| `code rows pending: unreadable` is reachable **only** when `settings`/`entries` exist with a broken shape — `TableExistsAsync` never throws for a *missing* table, and `CountPendingCodeRowsAsync` returns `0` for one | `DoctorCommands.cs:114-128`, `:170-180` |
| memory pending query is `SELECT COUNT(*) FROM entries WHERE embed_state = 'pending'` (bank-wide, no attempts filter) | `MemorySql.cs:362-363` |
| code pending query is its structural twin over `code_entries` | `MemorySql.cs:419-420` |
| `entries` has `embed_state` and **no** `embed_attempts` → no poison subset, no quarantine caveat on the memory side | `MemorySchema.cs:85-111` |
| `model_migration` is a single `id = 1` row; `finished_at IS NULL` ⇔ open; `lease_owner`/`lease_expires_at` are on the row | `MemorySchema.cs:399-409`, `MemorySql.cs:470-475` |
| settings seeding statement tests already use | `MemorySql.cs:604-607` (`UpsertSetting`) |
| exit codes | `src/AiRaccoon/ExitCode.cs:54` `SchemaVerificationFailed = 19`, `:66` `NoBank = 22` |
| the suite being extended: 11 tests, argv-driven through `CliRun`, class-level `Integration` + `Slow`, `[RetryFact]` | `tests/AiRaccoon.Tests/Unit/Setup/DoctorCommandsTests.cs:23-24,49` |
| `SchemaDoctor` derives status from schema shape + version skew only — nothing in the report can move the exit code | `src/AiRaccoon.Infrastructure/Sqlite/SchemaDoctor.cs:15-34` |
| internals are visible to the test assembly in **all three** production projects, so an internal descriptor/label type is directly testable | `AiRaccoon.csproj:56-58`, `AiRaccoon.Core.csproj:17-19`, `AiRaccoon.Infrastructure.csproj:34-38` |
| **filter syntax, measured** — on the built runner (`xUnit.net v3 / Microsoft.Testing.Platform v2 / Runner 4.0.0`), `--filter "FullyQualifiedName~DoctorCommandsTests"` and `--filter-class "AiRaccoon.Tests.Unit.Setup.DoctorCommandsTests"` each resolve **11 tests**; both `--filter` (VSTest syntax) and `--filter-class`/`--filter-trait`/`--filter-query` are documented in the binary's own `--help` | live `--list-tests` runs against `tests/AiRaccoon.Tests/bin/Debug/net10.0/AiRaccoon.Tests` |
| **a zero-test filter does NOT exit 0 here** — a bogus filter printed `Zero tests ran` and exited **8** | live run |
| `--nologo` must never appear (MTP forwards it as an unmatched token → "Zero tests ran / exit 5") | `docs/work/2026-08-25-mtp-xunit4-migration.md:18-20,60-61`; `scripts/nightly-triage.py:159-162` |
| CI lane shape | `.github/workflows/build.yml:134,168,221,274` — `dotnet test --project tests/AiRaccoon.Tests --filter "<trait expr>" --no-build -v m` |
| **a gate that constrains how these tests are written**: any test file whose text contains `Speed, TestCategories.Slow` must not contain a bare `[Fact]`/`[Theory]` | `tests/AiRaccoon.Tests/Unit/RetrySurfaceGateTests.cs:17,59-69` |
| doc-gate precedent (derive a doc's claim from the code, don't hand-maintain it) | `tests/AiRaccoon.Tests/Unit/Mcp/ToolInventoryTests.cs:169-176` |

Incidental defect found while reading, out of scope but worth an issue: a committed merge-conflict
marker `>>>>>>> origin/main` sits at `docs/reference/agent-memory-server.md:196`.

---

## 1. Acceptance criteria this lane designs proof for

| # | Criterion |
|---|---|
| **A1** | After the extraction, the `code engine`, `embedding threads` and `code rows pending` lines are byte-identical to today's, in the same order, with the same total line count. |
| **A2** | The shared component is per-corpus parameterised such that swapping any one corpus's wiring (settings key, table, pending SQL, label) for the other's is detected. |
| **A3** | Fault isolation: one corpus's unreadable bank state must not blank the other corpus's line. |
| **A4** | The memory engine is **always** reported; `not configured` is a state that cannot exist on the memory side. |
| **A5** | The memory engine is reported for an explicit local path and for a remote provider — and never leaks `embedding.apiKey`. |
| **A6** | `memory rows pending` is a real count (moves 0 → N on a real insert) and degrades legibly when unreadable. |
| **A7** | The migration state is reported; absent ≡ closed, open is distinct, and an open row is what explains a refused `memory_*` tool call. |
| **A8** | No new line can change doctor's exit code, and no new read writes to the bank. |
| **A9** | No report line is printed at all when the bank is missing or the key fails. |

---

## 2. Wording assumptions and how the tests survive them

Every new assertion binds to a constant the production code must expose (recommended: internal
consts on the shared component, e.g. `DoctorReportLabels.MemoryEngine`). The single deliberate
exception is **R1**, the full-report contract pin, which uses literals — that test *is* the wording
contract, and it is expected to be edited in the same commit that changes the wording.

| Placeholder used below | Assumed rendering | If P1 rules otherwise |
|---|---|---|
| `MEM_ENGINE` | `memory engine: ` | change one const; all rows survive |
| `MEM_PENDING` | `memory rows pending: ` | same |
| `MEM_MIGRATION` | `memory migration: ` | same; if migration is folded into the engine line (research §5 option 5), X-rows assert on the engine line instead and X2's "absent ≡ closed" claim still holds |
| `BUNDLED_QUALIFIER` | `bundled` appears in the memory engine line when `embedding.model` is unset | M2 fails if P1 instead prints an empty value — that is the point of M2 |
| `UNREADABLE` | `unreadable` (already produced today, `DoctorCommands.cs:74`) | reuse the existing token; do not invent a second one |
| `PENDING_WHEN_TABLE_MISSING` | `0` (mirrors `CountPendingCodeRowsAsync`'s guard) | if P1 rules `unreadable`, D2 flips its expected value — the test stays, its oracle changes |
| `EXIT_ON_OPEN_MIGRATION` | `ExitCode.Success` (report-only; everything in `DoctorCommands` says schema-shape-only) | X3 asserts the constant either way; the assertion must exist so a later drive-by change is caught |

---

## 3. THE TEST LIST

Ordering is TDD-significant. **Group R is written and run GREEN against today's binary before any
extraction** (characterisation is the one named exception to red-first; its realness is proven by
mutating a character and watching it go RED, per the `prove-the-check-fails` invariant). Groups
S/M/D/X are red-first.

### Group R — characterisation of the refactor (A1)

| Name | Asserts | Failure mode targeted | Mutation → RED | Why existing coverage misses it |
|---|---|---|---|---|
| `Doctor_HealthyBank_PrintsExactlyTheseLinesInThisOrder` | the full stdout of a healthy fresh bank equals an expected 7-line list built from `_factory.BankPath`, `MemorySchema.CurrentVersion`, `MemorySchema.SchemaDigest`, `CodeEngineSetup.DefaultModelCommand` and `Math.Max(1, ProcessorCount/2)` | the extraction silently drops, reorders, duplicates or re-words a line | delete the `user_version:` write, swap the `code engine` and `embedding threads` lines, or change `(` to `[` in the code-engine line | **nothing in the suite asserts `user_version:`, `application_id:` or the `ai-raccoon doctor: <path>` header line at all** (grep: those tokens appear only in schema tests, never in doctor's stdout), and nothing asserts line order or count — an extraction that deleted 3 of 7 lines would keep all 11 tests green |
| `Doctor_SharedThreadsLine_AppearsExactlyOnce` | `Regex.Matches(outp, "embedding threads: ").Count == 1` | the extracted per-corpus block prints the *shared* threads line once per corpus | make the shared component emit the threads line inside the per-corpus loop | all three existing threads assertions are `ShouldContain` — they pass on 1 *or* 2 occurrences |
| `Doctor_EveryReportLabel_AppearsExactlyOnce` | for each label the shared component exposes (derived from its own descriptor/label collection, not a hand-typed list), exactly one output line starts with it | a corpus block emitted twice, or one corpus block missing entirely | build the report from the code descriptor twice (the classic copy-paste-the-wrong-descriptor slip) | no existing test counts anything; `derive-or-delete-the-list` is otherwise unenforced on this surface |
| `Doctor_ConfiguredCodeEngine_NamesTheModelNameNotJustTheDirectory` | the code-engine line equals `code engine: {leaf} (manifest unreadable) ({dir})` for a directory with no manifest | `ModelNameFor`'s manifest fallback (`DoctorCommands.cs:157-168`) is dropped by the extraction | delete `ModelNameFor` and print the raw directory twice | the existing test asserts `ShouldContain("code engine:")` and `ShouldContain(modelDir)` **separately** — the model *name* is never asserted anywhere, so removing `ModelNameFor` entirely keeps the suite green |

### Group S — the shared component's per-corpus parameterisation (A2, A3)

**This is the group the owner correction demands, and the finding is: today, exactly one existing
assertion (`DoctorCommandsTests.cs:106`, `ShouldNotContain("code engine: not configured")`) would
survive a settings-key swap, and it survives by accident.** A key swap keeps `:104` green (the
prefix `code engine:` still prints, in the not-configured line) and keeps `:105` green (the seeded
directory still prints — on the memory line). One accidental assertion is not a gate.

| Name | Asserts | Failure mode targeted | Mutation → RED | Why existing coverage misses it |
|---|---|---|---|---|
| `EngineDescriptors_EachCorpusPairsItsOwnKeyTableAndQuery` (**new Fast/Unit class**, no bank) | for the code descriptor, all four fields (settings key, table, pending SQL, label) mention `code`; for the memory descriptor, none do; and every descriptor's pending SQL contains `FROM {descriptor.Table}` | a swapped or copy-pasted field in the descriptor table | swap `EmbeddingSettingsKeys.CodeModel` ↔ `Model`, or `MemorySql.CountPendingCodeEmbed` ↔ `CountPendingEmbed`, or the two table names, or the two labels — **any one** of the four | nothing exists: there is no shared component today, and this cheapest rung (reflection over the descriptor, Fast lane, no SQLite) is the only place a swap is caught in under a second |
| `Doctor_BothCorporaConfigured_EachLineNamesItsOwnModelDirectory` | with `embedding.codeModel = codeDir` and `embedding.model = memDir` (**two different directories**), the code-engine line names `codeDir` and the memory-engine line names `memDir` — asserted as whole lines, label and value together | the memory line rendered from the code corpus's settings (or vice versa) | swap the two settings keys in the descriptors | no memory line exists; and see the finding above — the existing pair `:104`+`:105` cannot see this |
| `Doctor_BothCorporaPending_ReportsAsymmetricCountsPerCorpus` | 2 pending `code_entries` rows and **3** pending `entries` rows → `code rows pending: 2` **and** `memory rows pending: 3` | the two pending queries swapped, or one corpus counting the other's table | swap `CountPendingCodeEmbed` ↔ `CountPendingEmbed`, or swap the `TableExistsAsync` table names | `Doctor_ReportsHowManyCodeRowsArePending` (`:115-130`) does catch the swap today (2 → 0, `entries` empty) — **but only because the counts differ.** A naively written memory twin seeding 2 and 2 would make the swap invisible in both directions. The asymmetry is the design requirement, stated here so it is not lost |
| `Doctor_MemoryStateUnreadable_StillReportsTheCodeEngine` | on a bank with a shape-broken `entries` **and** a healthy `settings` naming a code model dir: the code-engine line still names the model and `code rows pending: 0`, while `memory rows pending: unreadable` | the extraction collapses the two per-corpus `try`/`catch (SqliteException)` blocks into one shared try — so one corpus's exception nulls out both corpora | wrap both corpora's reads in a single `try` | no test today puts a *readable* corpus and an *unreadable* corpus in the same bank; the #357 repro (`:183-210`) has no `settings` table at all, so both reads degrade together and the collapse is invisible |
| `Doctor_NoBank_PrintsNoReportLinesAtAll` and `Doctor_KeyResolutionFails_PrintsNoReportLinesAtAll` (extend `:50` and `:234`) | stdout contains none of the report labels | the shared component invoked before the bank-exists / key-resolve guards | hoist the corpus reads above the `File.Exists` check | the existing tests assert only exit code + one stderr substring; a report printed before the guard would pass both |

### Group M — the new memory behaviour (A4, A5, A6)

| Name | Asserts | Failure mode targeted | Mutation → RED | Why existing coverage misses it |
|---|---|---|---|---|
| `Doctor_NoMemoryModelSetting_ReportsTheBundledFallback_NeverNotConfigured` | on a fresh bank the memory-engine line names the bundled fallback **and** `outp.ShouldNotContain("memory engine: not configured")` | the memory line written by analogy with `Doctor_NoCodeEngine_SaysNotConfigured` — reporting a state that cannot exist (`BundledModel.cs:67`, `EntryEmbedder.cs:26,47,90`) | implement the memory line as `Directory is null ? "memory engine: not configured …" : …` | this is the research record's central asymmetry (§3.3); no test can cover a line that does not exist yet, and the *negative* assertion is what keeps a future contributor from re-introducing the copied branch |
| `Doctor_ExplicitLocalMemoryModel_NamesTheResolvedModelAndItsPath` | with `embedding.model = <dir>` and `embedding.provider = local`, the memory-engine line, **as a whole line**, names the model and the path | a value printed without its label, or the wrong corpus's value | swap the settings keys; or print the path without the model name | new behaviour |
| `Doctor_RemoteMemoryProvider_NamesProviderModelAndBaseUrl` | with `provider = openai`, `model = text-embedding-3-small`, `baseUrl = https://example.invalid/v1`, the memory line names all three and does **not** print a filesystem path | remote engines rendered through the local-directory formatter (`ModelNameFor` would return `text-embedding-3-small (manifest unreadable)`) | route the remote case through `ModelNameFor` | new behaviour; `ModelNameFor` (`:157-168`) is a *directory* helper with a swallow-everything catch — reusing it for a remote model name silently appends `(manifest unreadable)` and no existing assertion would notice |
| `Doctor_RemoteMemoryProvider_NeverPrintsTheApiKey` | with `embedding.apiKey = sk-test-DO-NOT-PRINT` seeded, `(outp + err).ShouldNotContain("sk-test-DO-NOT-PRINT")` | a shared component that dumps every `embedding.*` row, or a "show the full config" convenience | make the shared component read and print all settings whose key starts with `embedding.` | nothing today reads `embedding.apiKey` in doctor; the key is persisted in the settings table (`EmbeddingSettingsKeys.cs:17`), so this becomes reachable the moment a generic settings reader is extracted |
| `Doctor_ReportsHowManyMemoryRowsArePending` | pending count is `0` on a fresh bank and `N` after inserting N real `entries` rows (two runs in one test, or two tests sharing the assertion helper) | a hardcoded literal, or a count read from the wrong corpus | return a constant; or swap the pending SQL | new behaviour; this is the shape the existing `Doctor_ReportsHowManyCodeRowsArePending` and the 1.33.0 checklist item `doctor-reports-code-engine-state` both use (`0` → genuine `N`), and it is the only shape that proves the number is alive |

### Group D — degraded paths (A3, A6, A8)

| Name | Asserts | Failure mode targeted | Mutation → RED | Why existing coverage misses it |
|---|---|---|---|---|
| `Doctor_MissingSettingsTable_StillReportsTheMemoryEngineAndPendingCount` | open a real bank, `DROP TABLE settings`, run doctor: memory-engine line reports the bundled fallback, `memory rows pending: 0`, exit is `SchemaVerificationFailed` (the *shape* decides, not the read) | the settings read performed without the `TableExistsAsync` guard → `SqliteException` → the whole memory state nulls and the pending count is lost | delete the `TableExistsAsync(connection, "settings", …)` guard for the memory descriptor | the #357 repro bank happens to have no `settings` table, but it also has no readable `entries`, so it cannot distinguish "guard missing" from "everything unreadable" |
| `Doctor_MissingEntriesTable_ReportsPendingAsPENDING_WHEN_TABLE_MISSING` | open a real bank, `DROP TABLE entries`, run doctor: `memory rows pending: 0` (assumption `PENDING_WHEN_TABLE_MISSING`) and every other line still prints | a bank predating a table read as a crash or a blank | delete the `TableExistsAsync(connection, "entries", …)` guard (→ `unreadable`), or return `null` instead of `0` | no test exercises a missing `entries` with an otherwise healthy bank; `SchemaDoctorTests` covers the *diagnosis* of missing tables, never the report's tolerance of them |
| `Doctor_ShapeBrokenBank_MemoryPendingIsUnreadable_AndExitIsShapeVerificationFailed` | on the exact #357 repro bank (`entries (only_one_column TEXT)`), `memory rows pending: unreadable`, and exit is still `ExitCode.SchemaVerificationFailed` | a new read that throws outside the guard and changes the exit code, or crashes the command | remove the `catch (SqliteException)` from the memory descriptor's read | the existing `:183-210` test asserts the exit code and three substrings; it says nothing about the new line, and `unreadable` is a branch **no test reaches today** for either corpus |
| `Doctor_MissingModelMigrationTable_ReportsTheMigrationAsAbsent` | `DROP TABLE model_migration`, run doctor: the migration line renders as absent/none, not `unreadable`, not a crash | `SelectModelMigration` issued without a table guard | drop the guard | new behaviour; the table exists on every v11 bank, so only a deliberate drop reaches this |
| `Doctor_NeverModifiesTheBank_WithBothCorporaAndAnOpenMigration` (a second SHA-256 witness) | file hash and `PRAGMA user_version`/`application_id` unchanged across a run on a bank carrying settings, both corpora's rows and an **open** migration row | a migration read that acquires or clears the lease (`AcquireModelMigrationLease`, `ReleaseModelMigrationLease` are one line away in the same const block, `MemorySql.cs:495-504`) | replace `SelectModelMigration` with `AcquireModelMigrationLease` | the existing `Doctor_NeverModifiesTheBank` (`:213-231`) runs against a **bank with no migration row and no settings**, so a lease-touching read has nothing to write and the hash never moves |

### Group X — the migration states (A7)

Live evidence (owner's real bank, read-only probe, 2026-08-26): `model_migration` id=1 **open**
(`finished_at NULL`), `lease_expires_at` already in the past — an open migration with **no live
drainer**; `entries` 51,947 rows / 47,723 pending; and `doctor` printed `status: HEALTHY`, exit 0,
**while `memory_search` was refusing with `model-migration-in-progress`**. That is the defect this
group exists to close.

Four candidate states, and what each earns:

| State | Verdict |
|---|---|
| no row at all | **earns a row**, folded with "closed" into one `[RetryTheory]` |
| closed row (`finished_at` set) | **earns a row** — but only as the second theory case, whose value is proving absent ≡ closed *deliberately* |
| open + live lease | **collapses** into the open case unless P1 rules that the report distinguishes a live drainer |
| open + expired lease | **earns a row** — it is the owner's reproducible state, and the operator-visible difference ("nothing is draining this") is the whole value; *but* distinguishing it requires a clock, and `DoctorCommands` has exactly three constructor deps (`:17`) with no `TimeProvider` — flagged to the implementation lane as a 4th-dependency decision |

| Name | Asserts | Failure mode targeted | Mutation → RED | Why existing coverage misses it |
|---|---|---|---|---|
| `Doctor_NoMigrationRowOrAClosedOne_ReportsTheSameSettledState` (`[RetryTheory]`, 2 cases: no row / `finished_at = 1`) | both cases produce the identical migration line | "absent" and "closed" drifting apart, so a settled bank reads as broken (or vice versa) | render `finished_at IS NOT NULL` with different text from "no row"; or use `SelectModelMigration`'s row-presence instead of `HasOpenModelMigration`'s predicate | new behaviour |
| `Doctor_OpenMigration_ReportsItAndSaysMemoryToolsAreRefused` | with an open row seeded and every embedded row marked pending: the migration line reports open, the pending line reports the real count, and `exit.ShouldBe(EXIT_ON_OPEN_MIGRATION)` | the exact live defect: `HEALTHY` + exit 0 while every `memory_*` call is refused | report the migration from a hardcoded `false`; or let the migration state change the status branch (which the exit-code assertion catches in the other direction) | **no test anywhere asserts doctor's behaviour on a bank with an open migration**; `ModelMigrationCrashRecoveryE2ETests` covers the relay, never the report |
| `Doctor_OpenMigrationWithAnExpiredLease_ReportsTheSameOpenStateAsALiveLease` (characterisation) **or** `…_SaysNoDrainerHoldsTheLease` (if P1 distinguishes) | whichever ruling P1 takes, asserted explicitly | an accidental dependence on `lease_owner`/`lease_expires_at` that makes the report flap between two banks that are equally stuck | seed `lease_owner = NULL` and watch the line change | new behaviour; and this row is what stops the collapse from being silent |
| `Doctor_OpenMigration_PendingCountAndMigrationLineAgree` (may be folded into the row above) | with 3 embedded rows + `MarkAllEmbeddedPending` + an open row: pending is 3 **and** migration is open, in the same output | the two lines derived from unrelated reads that can disagree, leaving the operator with `pending: 47723` and no explanation | report pending from `HasPendingEmbed` (an EXISTS, `MemorySql.cs:358-359`) instead of `CountPendingEmbed` — the count would collapse to `1` | new behaviour; the coupling (`EntryEmbedder.cs:50-91` commits settings + migration row + `MarkAllEmbeddedPending` together) is exactly what makes the pair meaningful |

---

## 4. HONESTY GATES — the existing invariants, and which assertions go ambiguous

### 4.1 Invariants the new lines must not break

| Existing test / line | Invariant pinned | How the new lines could break it | What must be added |
|---|---|---|---|
| `:194,201` `FileSha256(...).ShouldBe(beforeHash, "doctor must never write to a bank it is diagnosing, healthy or not")` — asserted **before** the exit-code checks, deliberately | read-only on a shape-broken bank | a memory read that opens its own read-write connection, or touches the migration lease | keep it; add the Group D lease witness (the existing bank has no migration row, so a lease write cannot show up there) |
| `:219,228` `afterHash.ShouldBe(beforeHash, …)` + `PRAGMA user_version`/`application_id` unchanged | read-only on a healthy bank | same | same |
| `:203-205` `exit.ShouldBe(ExitCode.SchemaVerificationFailed)` and two `ShouldNotBe`s | a shape mismatch keeps its own distinct code — the new reads must not hijack it | an unguarded memory read throwing out of `RunAsync` | add the Group D `unreadable` test that re-asserts this exit code *with* the new line present |
| `:54-56` `exit.ShouldBe(ExitCode.NoBank)` + path on stderr | a wrong `--data-root` never reads as healthy | a corpus read hoisted above the `File.Exists` guard | add `Doctor_NoBank_PrintsNoReportLinesAtAll` |
| `:244-246` `ExitCode.FailedToResolveEncryptionKey`, explicitly `ShouldNotBe(SchemaVerificationFailed)` | key failure is distinguishable | same | add the paired "no report lines" assertion |
| `RetrySurfaceGateTests` (`:17,68-69`) | the slow surface uses retry attributes | a new `[Fact]` in `DoctorCommandsTests.cs` | every new test in that file is `[RetryFact]`/`[RetryTheory]`; the new Fast descriptor class uses plain `[Fact]` and must not carry the `Slow` trait |
| `SpeedGateCoverageTests` / `CategoryGateCoverageTests` | every test carries `Category` **and** `Speed` | a new class without traits | the new Fast/Unit class carries `[Trait(Category, Unit)]` + `[Trait(Speed, Fast)]` |

### 4.2 Collision audit — every string assertion in `DoctorCommandsTests.cs`

| Line | Assertion | Verdict once memory lines exist |
|---|---|---|
| `:56` | `err.ShouldContain(_factory.BankPath)` | **safe** — the report is never printed on that path |
| `:69` | `outp.ShouldContain("HEALTHY")` | **safe but weak**: an unanchored token match. Tighten to `status: HEALTHY`; a memory line must never contain the word |
| `:86` | `outp.ShouldContain("code engine: not configured")` | **safe from the memory line**, but a hard constraint on P1: the literal prefix `code engine: ` must survive the extraction verbatim (a generalised label like `engine (code):` breaks this) |
| `:87` | `outp.ShouldContain(CodeEngineSetup.DefaultModelCommand)` | **already thin, and becomes ambiguous**: it does not assert the command is on the *code* line. If any memory line ever quotes the same command, `:86`+`:87` both pass while the code line has lost its remedy. Merge into one whole-line assertion |
| `:104` | `outp.ShouldContain("code engine:")` | **becomes ambiguous**: satisfied by the not-configured line too, so it survives a keys-swap |
| `:105` | `outp.ShouldContain(modelDir)` | **becomes ambiguous — the important one**: once a memory line can print a path, a keys-swap prints `modelDir` on the *memory* line and this still passes. Merge `:104`+`:105` into `outp.ShouldContain($"code engine: {expectedName} ({modelDir})")` |
| `:106` | `outp.ShouldNotContain("code engine: not configured")` | **the only assertion in the file that catches a keys-swap**, and it catches it by accident (the swap happens to produce the not-configured branch). Keep it, but do not rely on it |
| `:129` | `outp.ShouldContain("code rows pending: 2")` | **safe** (label-anchored) and **strong** — it catches a pending-SQL swap because `entries` is empty in that bank. The memory twin must therefore seed a *different* count |
| `:148,162,178` | the three `embedding threads: …` full-line assertions | **safe as text, ambiguous as to multiplicity**: all three still pass if the extraction prints the shared line once per corpus. Add the occurrence-count test (R2) |
| `:207` | `combined.ShouldContain("entries")` | **becomes ambiguous**: any new line containing the substring `entries` (`memory rows pending (entries)`, a `vec_entries` dimension line) satisfies it even if the finding vanished. Tighten to the finding's own bullet shape, `"  - entries: missing column"` |
| `:208` | `combined.ShouldContain("missing column")` | **safe** — the phrase only comes from `SchemaDoctor.CompareColumnsAsync` (`SchemaDoctor.cs:97`) |
| `:209` | `combined.ShouldContain("never repair")` | **already vacuous**: satisfied by the always-printed disclaimer `it never repairs a bank` (`DoctorCommands.cs:75`), so it passes on a HEALTHY run too and proves nothing about the mismatch path. Replace with `remedy: start the server` (`:96`), which is mismatch-only and currently asserted nowhere |
| `:246` | `err.ShouldContain("encryption key")` | **safe** |

---

## 5. WHAT MUST NOT BE TESTED BY STRING MATCH ALONE

| Assertion that would pass on a hardcoded literal | The shape that makes it honest |
|---|---|
| `memory rows pending: 0` on a fresh bank | worthless alone — `return 0;` passes. Pair it with the **same** assertion helper after a real insert: `0` → `N`, exactly as `Doctor_ReportsHowManyCodeRowsArePending` (`:115-130`) and the 1.33.0 checklist item `doctor-reports-code-engine-state` (`observed-result`: "moved from a genuine 0 … to a genuine 3 … not a static/always-0 report") already do |
| `memory rows pending: N` with N equal to the code count | a swapped query passes. **Seed asymmetric counts** (2 code / 3 memory) and assert both lines in the same run |
| `memory engine: <dir>` where `<dir>` also appears on the code line | seed **two different directories** and assert whole lines (label + value), never the value alone |
| `memory migration: open` on a bank whose row was seeded open | a hardcoded `true` passes. Assert the **transition**: same bank, closed row → settled line; then `UPDATE model_migration SET finished_at = NULL` → open line, in one test |
| `memory engine: bundled` on a fresh bank | a hardcoded string passes. Assert the transition: fresh → bundled; then seed `embedding.model` → the path; and add the negative `ShouldNotContain("memory engine: not configured")` |
| `embedding threads: 3 (setting)` | already honest (derived from a real setting) — but add the occurrence count, which no string match can supply |
| the whole report | `ShouldContain` can never prove a line was **not deleted**. Only R1's full-line-list equality can, and it is the only test that pins order |
| `EngineDescriptors_…` structural gate | a descriptor test that merely restates the mapping is a mirror. Its oracle must be **cross-field consistency** — the code descriptor's four fields all mention `code`, the memory descriptor's none do, and each pending SQL contains `FROM {its own table}`. Structural gates cannot see a mis-*used* correct descriptor, so it is paired with the behavioural asymmetric-count test, never a substitute for it |

---

## 6. TEST MECHANICS in this repo

- **Driving**: argv through `CliRun.RunAsync(args, ConfigCommands)` → the same entry point `Program.cs`
  uses. The fixture's private helpers already exist: `Run(doctor, ["doctor"])` (`:39`) and
  `CreateDoctor(resolver?)` (`:41`). No new harness is needed.
- **Bank**: a real temp-dir bank. `TestData.CreateTempRoot("doctor-cli")` +
  `TestData.CreateInfrastructureOptions(dataRoot)` + `new SqliteConnectionFactory(options,
  NullKeyProvider.Resolver(options))` (`:27-35`); `TestData.DeleteTempRoot` in `Dispose` (`:37`).
  `await using (await _factory.OpenBankAsync(ct)) { }` creates the full v11 schema.
- **Traits / retry**: class-level `[Trait(TestCategories.Category, TestCategories.Integration)]` +
  `[Trait(TestCategories.Speed, TestCategories.Slow)]` (`:23-24`) are inherited; each new method is
  `[RetryFact]` (xRetry.v3 1.0.0, 3 attempts) or `[RetryTheory]`. Never a bare `[Fact]` in this file.
- **Cancellation**: `TestContext.Current.CancellationToken` on every command (xunit 4.0.0's xUnit1069
  analyzer + `TreatWarningsAsErrors`).
- **Seeding a setting**, without the server: `MemorySql.UpsertSetting` with `new { key, value }`
  (`:97-99`, `:141-143`, `:171-173`).
- **Seeding pending rows**: `INSERT INTO entries(hash, value, project_id, scope, created_at,
  updated_at, embed_state) VALUES (@hash, @value, 'p', 'project', 0, 0, 'pending')` — the exact
  column set `Integration/Embedding/NonDefaultDimensionMigrationTests.cs:116-121` uses (the table's
  own CHECK requires a non-null `scope` when `workspace_id` is null, `MemorySchema.cs:110`).
- **Seeding a migration row**, without the server: the same file's `OpenMigrationAsync`
  (`:124-130`) — a direct `INSERT INTO model_migration(id, provider, model, base_url, engine,
  started_at, finished_at) VALUES (1, 'local', NULL, NULL, 'test-engine', 0, NULL)` with an
  `ON CONFLICT(id) DO UPDATE`. Note `MemorySql.StartModelMigration` is **not** usable to *reopen* a
  row: its `WHERE model_migration.finished_at IS NOT NULL` guard makes it a no-op against an
  already-open row by design (`MemorySql.cs:477-489`).
- **Shape-broken banks**: a raw `SqliteConnection` in `ReadWriteCreate` mode before doctor ever runs
  (`:185-192`), or `DROP TABLE <x>` on a real bank — both are writes the *test* performs, which is
  orthogonal to doctor's own read-only invariant.
- **The `unreadable` branch** is reached by a *shape-broken but present* table, never a missing one.

### Sketch 1 — R1, the full-report contract pin (the most delicate; write it first, GREEN, then mutate one line to witness RED)

```csharp
/// <summary>
///     The extraction's characterisation gate: the whole report, in order, line for line. Nothing
///     else in this suite asserts the header lines, the line count or the order — so an extraction
///     that dropped `user_version` would otherwise stay green. Literals on purpose: this test IS
///     the output contract, and it changes in the same commit the wording does.
/// </summary>
[RetryFact]
public async Task Doctor_HealthyBank_PrintsExactlyTheseLinesInThisOrder()
{
    await using (await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
    {
    }

    var (exit, outp, err) = await Run(CreateDoctor(), ["doctor"]);

    exit.ShouldBe(ExitCode.Success);
    err.ShouldBeEmpty();
    var threads = Math.Max(1, Environment.ProcessorCount / 2);
    Lines(outp).ShouldBe([
        $"ai-raccoon doctor: {_factory.BankPath}",
        $"user_version: {MemorySchema.CurrentVersion} (this binary: {MemorySchema.CurrentVersion})",
        $"application_id: {MemorySchema.SchemaDigest} (expected: {MemorySchema.SchemaDigest})",
        $"code engine: not configured — run '{CodeEngineSetup.DefaultModelCommand}' to enable semantic code search",
        $"embedding threads: {threads} (halved-core default)",
        "code rows pending: 0",
        // P1 owns where these two land; R3 (labels appear exactly once) is what keeps the set honest.
        $"{DoctorReportLabels.MemoryEngine}bundled (no embedding.model set)",
        $"{DoctorReportLabels.MemoryPending}0",
        $"{DoctorReportLabels.MemoryMigration}none",
        "doctor verifies schema shape only; it never repairs a bank",
        "status: HEALTHY"
    ]);
}

private static string[] Lines(string output) =>
    [.. output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r'))];
```

### Sketch 2 — S4, fault isolation: a readable corpus beside an unreadable one

```csharp
/// <summary>
///     The extraction's sharpest failure mode: one `try`/`catch (SqliteException)` around BOTH
///     corpora, so a broken `entries` blanks the code-engine line too. This bank has a healthy
///     `settings` (so the code side is genuinely readable) and a #357-shaped `entries` (so the
///     memory count genuinely throws) — the only combination that can tell the two apart.
/// </summary>
[RetryFact]
public async Task Doctor_MemoryStateUnreadable_StillReportsTheCodeEngine()
{
    var modelDir = Path.Combine(_dataRoot, "models", "faxenoff__code-daemon-embed-v1");
    Directory.CreateDirectory(modelDir);
    Directory.CreateDirectory(Path.GetDirectoryName(_factory.BankPath)!);
    await using (var raw = new SqliteConnection(new SqliteConnectionStringBuilder
                 { DataSource = _factory.BankPath, Mode = SqliteOpenMode.ReadWriteCreate }.ToString()))
    {
        await raw.OpenAsync(TestContext.Current.CancellationToken);
        await raw.ExecuteAsync(new CommandDefinition(
            """
            CREATE TABLE entries (only_one_column TEXT);
            CREATE TABLE settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
            """, cancellationToken: TestContext.Current.CancellationToken));
        await raw.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
            new { key = EmbeddingSettingsKeys.CodeModel, value = modelDir },
            cancellationToken: TestContext.Current.CancellationToken));
    }

    var beforeHash = FileSha256(_factory.BankPath);

    var (exit, outp, err) = await Run(CreateDoctor(), ["doctor"]);

    FileSha256(_factory.BankPath).ShouldBe(beforeHash, "doctor must never write to a bank it is diagnosing");
    exit.ShouldBe(ExitCode.SchemaVerificationFailed);          // the SHAPE decides, never these reads
    outp.ShouldContain($"code engine: {Path.GetFileName(modelDir)} (manifest unreadable) ({modelDir})");
    outp.ShouldContain("code rows pending: 0");                // code_entries is absent, not broken
    outp.ShouldContain($"{DoctorReportLabels.MemoryPending}unreadable");
    outp.ShouldNotContain($"{DoctorReportLabels.MemoryPending}0");
}
```

### Sketch 3 — S3, the anti-swap behavioural test (asymmetric counts, distinct directories)

```csharp
/// <summary>
///     The keys/query-swap killer. Two DIFFERENT model directories and TWO DIFFERENT pending counts
///     (2 code, 3 memory): swapping either the settings keys or the two COUNT queries in the shared
///     component's per-corpus descriptors reddens this test in both directions. Equal counts, or a
///     shared directory, would make the same swap invisible.
/// </summary>
[RetryFact]
public async Task Doctor_BothCorporaConfigured_ReportsEachCorpusFromItsOwnWiring()
{
    var codeDir = Path.Combine(_dataRoot, "models", "code-engine-dir");
    var memoryDir = Path.Combine(_dataRoot, "models", "memory-engine-dir");
    Directory.CreateDirectory(codeDir);
    Directory.CreateDirectory(memoryDir);

    await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
    {
        foreach (var (key, value) in new[]
                 {
                     (EmbeddingSettingsKeys.CodeModel, codeDir),
                     (EmbeddingSettingsKeys.Model, memoryDir),
                     (EmbeddingSettingsKeys.Provider, "local")
                 })
        {
            await connection.ExecuteAsync(new CommandDefinition(MemorySql.UpsertSetting,
                new { key, value }, cancellationToken: TestContext.Current.CancellationToken));
        }

        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO code_entries (hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
            VALUES ('c1', 'src/A.cs', 'a', 'src/A.cs', 1, 2, 'acme', 1, 1),
                   ('c2', 'src/B.cs', 'b', 'src/B.cs', 1, 2, 'acme', 1, 1)
            """, cancellationToken: TestContext.Current.CancellationToken));
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO entries (hash, value, project_id, scope, created_at, updated_at, embed_state)
            VALUES ('m1', 'one', 'acme', 'project', 0, 0, 'pending'),
                   ('m2', 'two', 'acme', 'project', 0, 0, 'pending'),
                   ('m3', 'three', 'acme', 'project', 0, 0, 'pending')
            """, cancellationToken: TestContext.Current.CancellationToken));
    }

    var (_, outp, _) = await Run(CreateDoctor(), ["doctor"]);

    // Whole lines, label and value together: asserting the directory alone would pass under a swap.
    outp.ShouldContain($"code engine: {Path.GetFileName(codeDir)} (manifest unreadable) ({codeDir})");
    outp.ShouldContain($"{DoctorReportLabels.MemoryEngine}local {memoryDir}");
    outp.ShouldContain("code rows pending: 2");
    outp.ShouldContain($"{DoctorReportLabels.MemoryPending}3");
}
```

### Helper — seeding the migration states (Group X)

```csharp
private async Task SeedMigrationAsync(long? finishedAt, string? leaseOwner = null, long? leaseExpiresAt = null)
{
    await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
    await connection.ExecuteAsync(new CommandDefinition(
        """
        INSERT INTO model_migration (id, provider, model, base_url, engine, started_at, finished_at, lease_owner, lease_expires_at)
        VALUES (1, 'local', NULL, NULL, 'test-engine', 1787739481, @finishedAt, @leaseOwner, @leaseExpiresAt)
        ON CONFLICT(id) DO UPDATE SET finished_at = @finishedAt, lease_owner = @leaseOwner, lease_expires_at = @leaseExpiresAt
        """, new { finishedAt, leaseOwner, leaseExpiresAt },
        cancellationToken: TestContext.Current.CancellationToken));
}
```

---

## 7. GATES — the commands that prove each criterion

All verified against this repo's own setup (MTP via `global.json` `test.runner`, `Directory.Build.props:22-24`), not assumed.

```bash
# 0. Build — TreatWarningsAsErrors is on, so this is also the analyzer gate.
dotnet build

# 1. This class only (A1, A3-A9). VERIFIED: resolves 11 tests today; a bogus filter exits 8, not 0.
dotnet test --project tests/AiRaccoon.Tests \
  --filter "FullyQualifiedName~DoctorCommandsTests" --no-build -v m
# Equivalent, exact-class form (also verified): --filter-class "AiRaccoon.Tests.Unit.Setup.DoctorCommandsTests"
# Never pass --nologo: MTP treats it as an unmatched token -> "Zero tests ran / exit 5".

# 2. One test, for the red-proof of a single mutation.
dotnet test --project tests/AiRaccoon.Tests \
  --filter "FullyQualifiedName~DoctorCommandsTests.Doctor_HealthyBank_PrintsExactlyTheseLinesInThisOrder" \
  --no-build -v m

# 3. The new Fast/Unit descriptor gate (A2) + the three meta-gates a new test file can break
#    (RetrySurfaceGateTests, SpeedGateCoverageTests, CategoryGateCoverageTests).
dotnet test --project tests/AiRaccoon.Tests \
  --filter "Speed=Fast&Performance!=Benchmark" --no-build -v m

# 4. The lane this class actually runs in on CI (build.yml:221).
dotnet test --project tests/AiRaccoon.Tests \
  --filter "Speed=Slow&Performance!=Benchmark" --no-build -v m
```

Per the `pipeline-runs-the-rest` invariant, 1-3 are the local gates; 4 is CI's. A count check is
built in — a filter that matches nothing exits 8 here — but `--minimum-expected-tests <n>` is
available on this runner and is worth adding to step 1 if the class-name filter is ever loosened.

### Manual live-bank verification — warranted, and for one reason

Two of the criteria cannot be proven on a synthetic bank: (a) the report's usefulness at real scale
(47,723 pending rows, not 3), and (b) the exact defect the owner hit — `doctor` saying `HEALTHY`
while `memory_search` refuses. The owner's bank is in that state **right now**, and `doctor` is
provably read-only (the two SHA-256 assertions), so the item is safe to run against it unchanged.
Rows shaped like `docs/work/checklist/*.json` (`observed-result`/`status`/`accepted` are filled at
run time, as in `2026-08-23-1.33.0-release.json:172-215`):

```json
[
  {
    "item": "doctor-reports-memory-engine-state",
    "expected-result": "On the live bank (~/.ai-raccoon/memory.db) `ai-raccoon doctor` prints a memory-engine line naming the resolved engine (never 'not configured' - unset embedding.model falls back to the bundled model, so that state cannot exist) and a memory-rows-pending line whose number matches an independent read-only COUNT of entries WHERE embed_state='pending'. On a scratch --data-root with a fresh bank the same two lines print with pending 0, and after ingesting one file the count moves 0 -> N: the number is alive, not a literal.",
    "anchor": "docs/work/2026-08-26-doctor-memory-embedding-research.md 3.1-3.3; MemorySql.cs:362; BundledModel.cs:67; src/AiRaccoon/Setup/Cli/Commands/DoctorCommands.cs",
    "command": "ai-raccoon doctor  # live bank, read-only. Cross-check: sqlite3 -readonly ~/.ai-raccoon/memory.db \"SELECT count(*) FROM entries WHERE embed_state='pending';\". Then: ai-raccoon --data-root root-mem doctor (fresh) ; memory_ingest_file one .md ; ai-raccoon --data-root root-mem doctor.",
    "evidence": "<paste both doctor blocks verbatim, the sqlite3 count, and the fresh-root 0 -> N pair>",
    "status": "pending"
  },
  {
    "item": "doctor-explains-a-refused-memory-tool-call",
    "expected-result": "With model_migration id=1 open (finished_at NULL) on the live bank - the state observed 2026-08-26, lease_expires_at already in the past, i.e. no live drainer - `ai-raccoon doctor` names the open migration in its own line and the pending count reflects MarkAllEmbeddedPending (~47,723 of 51,947), so an operator whose memory_* calls are being refused with 'model-migration-in-progress' learns why from doctor alone. The exit code stays whatever the ruling fixes it at (report-only: 0), and the bank is byte-identical before and after (doctor must not touch the lease).",
    "anchor": "ADR-0076; MemorySql.cs:470-475; src/AiRaccoon/Tools/ToolGate.cs; docs/work/2026-08-26-doctor-memory-embedding-research.md 3.4",
    "command": "shasum -a 256 ~/.ai-raccoon/memory.db ; ai-raccoon doctor ; echo \"exit=$?\" ; shasum -a 256 ~/.ai-raccoon/memory.db ; sqlite3 -readonly ~/.ai-raccoon/memory.db \"SELECT provider, finished_at, lease_owner, lease_expires_at FROM model_migration WHERE id=1;\"",
    "evidence": "<paste the doctor block, the exit code, the two identical sha256 sums, and the model_migration row>",
    "status": "pending"
  }
]
```

---

## 8. DOC GATES

| Doc | What must change | How it is verified |
|---|---|---|
| `docs/how-to/configure-ai-raccoon-server.md:330-337` | **already stale before this change**: the sample "A healthy bank" block shows 3 lines, `user_version: 10` and `application_id: -519479064`, while the binary prints 6 + status at v11 with digest `-1765263351`. Regenerate it from a real run in this PR, including the new memory lines. | Two options, in preference order: **(1) a derived gate** on the precedent of `ToolInventoryTests:169-176` — a test that reads the fenced block after `A healthy bank:` and asserts its line count and label prefixes match what the report actually emits (this is the `derive-or-delete-the-list` invariant applied to a doc sample, and it is the only thing that stops the block going stale a third time). **(2)** If P1/P2 decline the gate, the fallback is the manual checklist row above, which pastes the real block. R1 makes (1) cheap: both tests consume the same label set. |
| `README.md:40` | the existing bullet is the 1.33.0 thread-count entry and stays as history; a memory-engine report is a new "What's new" entry at the new `VERSION` (per the `whats-new-update` skill and the `traceable-releases` invariant). | `VersionContractTests` pins the version marker; no automated gate ties README prose to doctor's output — reviewer check, named explicitly so it is not assumed. |
| `docs/reference/logging-event-ids.md:83` | `DoctorCommands` owns **1000-1001**, and **1002-1007 is already `EmbedDrainService`** — so a new doctor warning cannot take 1002; it needs a fresh free block, plus the doc's prose count. | **The simplest correct answer is to add no log line at all**: the guarded reads are silent today (`DoctorCommands.cs:124-127,142-146`) and the report itself carries the `unreadable` signal, so the memory read should stay silent too. If a log is added anyway, four existing tests already gate it: `LoggerMessageEventIdTests.EventIds_AreUniqueAcrossTheAssemblies`, `EventIdBlocks_DoNotInterleaveBetweenOwners`, `DocumentedCount_MatchesTheMeasuredCount` (goes RED unless the prose count is updated in the same commit) and `EveryEventIdInSource_FallsInsideADocumentedBlock` (goes RED unless the table gains the block). |
| `docs/reference/agent-memory-server.md:201` | ties doctor's wording to `CodeEngineSetup.DefaultModelCommand`; needs the memory-side sentence if the memory line quotes a remedy command (`ai-raccoon model embedding set local`). | Reviewer check against the constant; no gate exists. (Also note the committed `>>>>>>> origin/main` marker at `:196` — fix or file it.) |
| `docs/how-to/configure-embedding-engines.md:145` | lists `doctor` among the surfaces quoting the code-engine remedy; extend to the memory engine if a remedy is added. | Reviewer check. |
| `docs/reference/logging-event-ids.md` count prose | only if a log line is added. | as above. |

---

## 9. What this lane deliberately does not design

- **The exit-code ruling** for an open migration (research §5 Q2). Every X-row asserts
  `EXIT_ON_OPEN_MIGRATION` so the ruling is pinned either way; the assertion must not be omitted.
- **The `vec_entries` dimension line** (research §3.5) — still a hypothesis with no traced read-only
  query. No test is designed for an unverified capability.
- **Bundled-asset presence** (needs `IBundledModel`, a 4th constructor dep). If P2 takes it, the
  matching row is a Group M twin: assets present → named; assets absent → the remedy command, with
  the mutation "always report present".
- **Whether an expired lease is distinguishable** — needs a `TimeProvider` seam doctor does not have.
  Flagged in Group X; the characterisation row covers the collapse either way.
- **Simpler shape** (`ask-if-simpler`): the minimum viable version is two memory lines with migration
  folded into the engine line. That cuts Group X from four rows to two and drops nothing from Groups
  R/S — the anti-swap and characterisation gates are independent of how many lines P1 chooses.

---

## SCHEMA-LAST

| test | acceptance criterion it proves | mutation that makes it RED | gate |
|---|---|---|---|
| `Doctor_HealthyBank_PrintsExactlyTheseLinesInThisOrder` | A1 | delete the `user_version:` line; reorder the code-engine and threads lines; change `(` to `[` in the code-engine line | class filter |
| `Doctor_SharedThreadsLine_AppearsExactlyOnce` | A1 | emit the shared threads line inside the per-corpus loop | class filter |
| `Doctor_EveryReportLabel_AppearsExactlyOnce` | A1, A2 | build the report from the code descriptor twice | class filter |
| `Doctor_ConfiguredCodeEngine_NamesTheModelNameNotJustTheDirectory` | A1 | delete `ModelNameFor` and print the directory twice | class filter |
| `EngineDescriptors_EachCorpusPairsItsOwnKeyTableAndQuery` | A2 | swap `CodeModel` ↔ `Model`; or `CountPendingCodeEmbed` ↔ `CountPendingEmbed`; or `code_entries` ↔ `entries`; or the two labels | `Speed=Fast` |
| `Doctor_BothCorporaConfigured_ReportsEachCorpusFromItsOwnWiring` | A2, A5, A6 | swap the two settings keys; swap the two COUNT queries | class filter |
| `Doctor_MemoryStateUnreadable_StillReportsTheCodeEngine` | A3, A8 | collapse the two per-corpus `try`/`catch (SqliteException)` blocks into one | class filter |
| `Doctor_NoBank_PrintsNoReportLinesAtAll` / `…KeyResolutionFails…` | A9 | hoist the corpus reads above the `File.Exists` / key-resolve guards | class filter |
| `Doctor_NoMemoryModelSetting_ReportsTheBundledFallback_NeverNotConfigured` | A4 | render the memory line with the code line's `Directory is null ? "not configured" : …` branch | class filter |
| `Doctor_ExplicitLocalMemoryModel_NamesTheResolvedModelAndItsPath` | A5 | print the path without its label; read the wrong settings key | class filter |
| `Doctor_RemoteMemoryProvider_NamesProviderModelAndBaseUrl` | A5 | route the remote model name through `ModelNameFor` (appends `(manifest unreadable)`) | class filter |
| `Doctor_RemoteMemoryProvider_NeverPrintsTheApiKey` | A5 | print every settings row whose key starts with `embedding.` | class filter |
| `Doctor_ReportsHowManyMemoryRowsArePending` | A6 | `return 0;` for the memory count; swap the pending SQL | class filter |
| `Doctor_MissingSettingsTable_StillReportsTheMemoryEngineAndPendingCount` | A3, A6 | drop the `TableExistsAsync("settings")` guard for the memory descriptor | class filter |
| `Doctor_MissingEntriesTable_ReportsPendingAsPENDING_WHEN_TABLE_MISSING` | A6 | drop the `TableExistsAsync("entries")` guard; or return `null` instead of `0` | class filter |
| `Doctor_ShapeBrokenBank_MemoryPendingIsUnreadable_AndExitIsShapeVerificationFailed` | A6, A8 | remove the `catch (SqliteException)` from the memory read | class filter |
| `Doctor_MissingModelMigrationTable_ReportsTheMigrationAsAbsent` | A7 | issue `SelectModelMigration` without a table guard | class filter |
| `Doctor_NeverModifiesTheBank_WithBothCorporaAndAnOpenMigration` | A8 | replace `SelectModelMigration` with `AcquireModelMigrationLease` | class filter |
| `Doctor_NoMigrationRowOrAClosedOne_ReportsTheSameSettledState` | A7 | render a closed row differently from an absent one | class filter |
| `Doctor_OpenMigration_ReportsItAndSaysMemoryToolsAreRefused` | A7, A8 | report the migration from a hardcoded `false`; let it change the status branch | class filter + manual `doctor-explains-a-refused-memory-tool-call` |
| `Doctor_OpenMigrationWithAnExpiredLease_ReportsTheSameOpenStateAsALiveLease` | A7 | make the line depend on `lease_owner`/`lease_expires_at` | class filter |
| `Doctor_OpenMigration_PendingCountAndMigrationLineAgree` | A6, A7 | count with `HasPendingEmbed` (EXISTS) instead of `CountPendingEmbed` | class filter + manual |
| doc sample gate (derived from the report's label set) | doc gate | edit the how-to's fenced healthy-bank block to drop a line | `Speed=Fast` |
| `LoggerMessageEventIdTests` (4 tests, only if a log line is added) | doc gate | take event id 1002 (owned by `EmbedDrainService`); add an id without a doc block | `Speed=Fast` |
