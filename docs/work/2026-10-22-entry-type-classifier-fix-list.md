# Entry-type classifier: compiled fix list

**Date:** 2026-10-22
**Subject:** branch `task/air-entry-type-classifier-v2`, commits `d0ac1ff8`, `c682c450`, `451594c1`,
`6acc4b33` (worktree `.ai-badger/worktrees/air-entry-type-classifier`)
**Source:** independent code review of the session `01a162d9-4b46-75e6-882b-ecb858d48f0e`
**Verdict:** do not merge yet. Six code fixes (F1 to F6), one schema fix (F8) and one merge-order
gate (F7).

The branch adds the denormalised `entries.entry_type` column, the v20 ladder step and the write-path
declarations. The classification rule itself is sound: a read-only aggregate over the live bank
(`~/.ai-raccoon/memory.db`) puts 23,200 file rows with `path = source_file` (file), 661 rows whose
file source is a cited file (memory), 1,496 transcript rows (memory) and 215 manual rows (memory),
so no row is misclassified. The defects below are in the write coverage, the schema contract, the
composition around the rule, and the prose that describes it.

**Owner rulings this record now carries** (2026-10-22): an entry has exactly one type, and the
stored column is NOT NULL with the default `none`. Both are recorded in the plan's owner-decisions
line.

## The list

| # | Severity | Location | Defect | Fix | State |
|---|---|---|---|---|---|
| F1 | SHOULD | `src/AiRaccoon.Infrastructure/Sync/SyncService.cs:461` | the sync pull inserts rows without a type, so they land `none` | classify in the pull, or carry the type in the payload | **Fixed** |
| F2 | SHOULD | `src/AiRaccoon.Core/Memory/EntryTypeClassifier.cs:41-45`, `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:1425-1435` | the rule exists twice, in C# and in SQL | one rule, or a test that compares the two | **Fixed** |
| F3 | SHOULD | `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:1390-1404` | `MigrateToV19Async` lost its doc comment, `MigrateToV20Async` carries two | move the v18 to v19 block back | **Fixed** |
| F4 | SHOULD | `tests/AiRaccoon.Tests/Integration/EntryTypeMigrationTests.cs:10-15`, `tests/AiRaccoon.Tests/Unit/Memory/EntryTypeClassifierTests.cs:7-12` | both class summaries describe the rejected source-based rule | rewrite both summaries to the shape rule | **Fixed** |
| F5 | SHOULD (owner-ruled) | `src/AiRaccoon.Core/Memory/EntryType.cs:9-22`, `src/AiRaccoon.Core/Memory/EntryTypeClassifier.cs:46-62` | one enum does two jobs: the parameter wants a flag set, the stored column holds one kind, and the stored form swallows a set as null | the stored kind is single-valued with a `none` state; the flag set is a separate type | **Fixed** |
| F6 | SHOULD | `src/AiRaccoon.Core/Memory/EntryType.cs:19`, `src/AiRaccoon/Tools/EntryTools.cs:48,52,60-67` | a second "entry type" with different meaning; the wire names are duplicated | one source for the wire names | **Fixed** |
| F7 | SHOULD (gate) | `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:71`, `docs/plans/2026-10-21-mcp-tool-surface-rename.md:205-215` | the branch migrates user banks to v20, which the released 2.0.x cannot open, and nothing in the branch reads the column | merge only with the query parameter, or record the gate | **Recorded** |
| F8 | SHOULD (owner-ruled) | `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:193,1414-1435`, `src/AiRaccoon.Infrastructure/Ingestion/ChunkBackfill.cs:108-109`, `src/AiRaccoon.Infrastructure/Ingestion/ChunkBoundaryRepair.cs:380-381` | the column admits a missing kind and any string; the backfill selects on null; the shape fallback dies on a non-null column | `NOT NULL DEFAULT 'none'`, a checked value domain, and the fallback re-expressed on `none` | **Fixed, except the CHECK constraint (open)** |

### How the fixes landed (2026-10-22)

- **F8** — the column is `entry_type TEXT NOT NULL DEFAULT 'none'` in both the `CREATE TABLE` and
  the ladder's `ADD COLUMN`. The backfill reads both ways of saying "no kind" (`NULL` for a bank
  touched by the earlier nullable build, `'none'` for the shipped schema) and never clobbers an
  explicit kind. The two repair paths already re-express the fallback on the VALUE: they call
  `Resolve`, which treats `'none'` as unclassified and classifies the row by its shape. The
  stored form gained `'none'`. The value DOMAIN is still open — SQLite cannot add a CHECK to an
  existing table without a rebuild, and the rebuild is the owner's open question.
- **F1** — the merge carries the kind: it takes the remote's own kind when the remote names one,
  and files a row from an older peer (which has no kind column) by its shape. Two tests cover the
  two peers, and both fail without the fix.
- **F2** — the shape rule has one SQL home (`MemorySql.EntryTypeFromShape`) beside the C#
  classifier, and `EntryTypeRuleParityTests` runs both over one fixture, so the two cannot drift.
- **F5** — the stored kind is its own single-valued type (`EntryKind`: none, memory, file) with no
  flag attribute and no `Code` member; `EntryType` stays the query parameter's flag set; the
  stored form refuses a set loudly.
- **F6** — the wire names live in `EntryKindWireNames` (the stored vocabulary) and
  `SearchKindWireNames` (the corpus vocabulary), and `EntryTools` reads the latter.
- **F7** — recorded in the plan: PR #913 ships as groundwork with no version bump, and it merges
  with the parameter and the TTL rule or not at all.

### The invariant is observable

`SchemaDoctor` now reports the rows that carry no kind (`entries without a kind: N`) and the rows
carrying a kind this build does not know. A write path that omits the column lands `'none'`
silently, so the count is how the omission shows up in a bank nobody has queried yet.


## F1: the sync pull writes rows with no type

The merge statement's column list ends at `chunk_index`:

```sql
INSERT OR IGNORE INTO entries (hash, path, value, source_file, section, scope, project_id, context_label,
                               workspace_id, agent_id, created_at, updated_at,
                               access_count, last_accessed_at, rating, ttl_days,
                               embed_state, embedding, chunk_index)
```

`entry_type` is absent, so every row pulled from a remote bank lands unclassified. The branch
updated the six other insert sites (`WriteChunks`, `SqliteMemoryStore.WriteAsync`, `FileIngestor`,
`ChunkBackfill`, `ChunkBoundaryRepair`, `ChunkBudgetReconciler` through
`MemorySql.InsertRebudgetEntry`); this is the seventh and the only one left.

**Why it matters:** the contract the branch states in `EntryTypeWriteTests.cs:10-14` is "a row
written without one is a row the query cannot place". The hole is permanent, because
`ON CONFLICT DO NOTHING` means a later local write of the same hash never repairs the type. When the
2.1.0 query parameter filters on the type, synced rows drop out of the result set without an
error. The lane brief said the same thing: "Every write path must set the type."

**Fix:** add the column to the statement and derive it from the remote row's shape
(`r.path = r.source_file` gives `file`, otherwise `memory`), or carry the type in the sync payload so
a 2.1.0 to 2.1.0 sync preserves it. Either way, add a test that pulls a row and asserts the type is
not `none`.

**Proves it:** `dotnet test --filter "FullyQualifiedName~SyncService"` plus a new test that pulls a
row and reads its type.

## F2: the classification rule is written twice

`EntryTypeClassifier.Classify` (`:41-45`) implements "file iff `path = source_file`". The migration
backfill implements the same rule a second time as SQL text (`MemorySchema.cs:1425-1435`):

```sql
UPDATE entries
SET entry_type = CASE
    WHEN entries.source_file IS NOT NULL AND entries.source_file <> ''
         AND entries.source_file = entries.path THEN 'file'
    ELSE 'memory'
END
WHERE entry_type IS NULL
```

The class doc calls itself "the one rule ... so there is one answer to 'is this row a memory or a
document' in the codebase", and the plan the branch cites says "The two rules now agree on one
classifier" (`docs/plans/2026-10-21-mcp-tool-surface-rename.md:98`). The lane brief said "no second
copy of the rule".

**Why it matters:** nothing compares the two. The migration test pins the SQL and the unit test pins
the C#, so a change to one, together with its own test, leaves the other silently wrong. The
backfill and the write path then classify the same row differently, which is the failure the column
was built to remove.

**Fix:** have the backfill call the C# rule over the unclassified rows, or generate the SQL
predicate from the same constants, and add a test that runs both over one fixture.

**Proves it:** break the SQL, watch the new comparison test go red, restore it, watch it go green.

## F3: the v19 doc comment moved onto the v20 method

The new step was inserted between the v19 summary and `MigrateToV19Async`:

```csharp
/// <summary>
///     v18→v19 (ADR-0133): rebuilds vec_entries, vec_structure and vec_code with `ctx` as the vec0
///     partition key, so a KNN prunes every other context's chunks before reading them. ...
/// </summary>
/// <summary>
///     v19→v20: the entry type a row holds, denormalised onto <c>entries.entry_type</c> ...
/// </summary>
private static async Task MigrateToV20Async(...)
```

The v19 text now documents the v20 method, and `MigrateToV19Async` immediately below has no doc
comment at all. `dotnet build` reports 0 warnings, so no gate catches it.

**Why it matters:** the project convention is that a doc comment is the short contract an editor
shows on hover. Hovering the v20 step shows the vec0 partition text first, and the v19 step shows
nothing.

**Fix:** move the v18 to v19 block back above `MigrateToV19Async`, one `<summary>` per method.

## F4: two test files describe the opposite of the code they test

`EntryTypeMigrationTests.cs:10-15` says "The backfill classifies every row from its SOURCE, because
the row's own shape cannot say it", while the method twenty lines below says "The backfill
classifies every row by its SHAPE", and the implementation classifies by shape.
`EntryTypeClassifierTests.cs:7-12` says "the classification comes from the source's type", while
the rule it pins is the shape. Both are leftovers from the design that commit `c682c450` replaced.

**Why it matters:** a reader meets the wrong rule before the right one, and the drift is invisible
to the compiler and to the test run. This is the class of defect the documentation drift audit
exists to catch, and it is inside the change that introduces the concept.

**Fix:** rewrite both summaries to the shape rule, mentioning the source-based rule only as the
rejected alternative. The same check applies to the storage table in
`docs/plans/2026-10-21-mcp-tool-surface-rename.md:126-131`, which still describes the old
discriminator and calls the partition exact.

## F5: one enum does two jobs, and the stored form swallows the difference

**Settled by the owner on 2026-10-22:** an entry has exactly one type, and the stored column is
NOT NULL with the default `none`. The flag attribute is therefore correct for the parameter and
wrong for the stored column, and the plan agrees on the parameter side: "The parameter is a flag
enum, so a caller can ask for Memory alone, Code alone, File alone, or any sum. It replaces `kind`
(`memory|code|both`)" (`docs/plans/2026-10-21-mcp-tool-surface-rename.md:121-122`). Dropping
`[Flags]` would break the parameter the plan settles, so the fix is not to remove the flag.

What the flag does create is an overload. The same enum is used for two things with different
domains:

- **As the query parameter** it is a set: `Memory | Code | File` is the default, and `Code` names
  the `code_entries` corpus (the plan's table at `:126-131`).
- **As the stored kind of a row** (`entries.entry_type`) it is one value per row, and `Code` is
  unreachable: no `entries` row is a code chunk, because code chunks live in their own table. With
  the owner's ruling the stored kind is one value from `none`, `memory` or `file`.

`ToStoredValue` (`EntryTypeClassifier.cs:46-53`) is where the two meet. It switches on the three
single values and returns null for anything else, so a set handed to it becomes
`entry_type = NULL`, which a NOT NULL column rejects outright:

```csharp
public static string? ToStoredValue(EntryType type) => type switch
{
    EntryType.Memory => MemoryName,
    EntryType.Code => CodeName,
    EntryType.File => FileName,
    _ => null
};
```

No production caller ORs the enum today (the only combination in the tree is the test at
`EntryTypeClassifierTests.cs:62-70`), so nothing is broken yet. The trap is waiting for the
parameter lane, which is the next step and the one that will hold a set.

**Why it matters:** a set that reaches the write path produces a row the query cannot place, with
no error and no red test. The reverse direction is the same trap: a reader of the stored column
has to decide what a missing value means, and the only rule the code offers is `FromStoredValue`,
which maps an unknown one to the unclassified member. `SearchKind`
(`src/AiRaccoon.Core/Memory/SearchKind.cs:4-9`) is already the corpus-level vocabulary in the
codebase, and its doc comment sets the house rule: "a rename must update this class, not three
string literals". The new flag enum and `SearchKind` will have to be reconciled by whoever wires
the parameter, and the plan says the parameter replaces `kind`.

**Fix:**

1. Give the stored kind its own single-valued type with the members `none`, `memory` and `file`.
   No flag attribute, no `Code` member: the flag set belongs to the parameter, and `Code` names a
   corpus, not a row kind.
2. Let the parameter keep a flag enum. If it replaces `kind`, it should own that vocabulary the
   way `SearchKind` does today, so there is one class for it rather than two.
3. Make the stored form refuse a set loudly (a guard that throws) rather than returning null, so
   the parameter lane cannot hand the filter type to the write path by mistake.
4. `ToStoredValue` of the unclassified member must return the stored name `none`, not null: the
   column is NOT NULL, so a null would be rejected by the schema. `FromStoredValue` maps `none` back
   to the unclassified member.

**Proves it:** a test that the stored form refuses a set rather than returning null, a test that
the stored kind has no `Code` member, and a test that the stored names round-trip for every member
including `none`. Break the guard, watch it go red, restore it, watch it go green.

## F6: a second "entry type", and one unreachable value

The tool layer already returns a type string on `GetEntryResult` (`EntryTools.cs:60-67`), with
`"memory"` and `"code"` hardcoded at `:48` and `:52`. The new classifier defines `MemoryName` and
`CodeName` for the same two wire names. `EntryType.Code` can never appear on an `entries` row:
code chunks live in `code_entries`, and the research the branch cites says so (F5, "Code is already
a separate corpus, so only the Memory and File split needs a new lever").

**Why it matters:** two types named `EntryType` with different meanings, different representations
(enum and string) and different value sets, with the wire names written twice. A caller of the
future `get_entry` parameter has to know which "code" is meant.

**Fix:** one source for the wire names, used by `EntryTools` as well. `Code` is settled by the
owner: it names a corpus, not a row kind, so it has no place in the stored kind type (F5). The
duplicated wire names in `EntryTools` still need one source.

## F7: the merge-order gate

The branch sets `CurrentVersion = 20` (`MemorySchema.cs:71`) and the ladder step runs on the first
read-write open, but nothing in the branch reads the type: the consumer is the query parameter, a
later step. The branch's own plan text records the consequence
(`docs/plans/2026-10-21-mcp-tool-surface-rename.md:205-215`): "a v20 bank refuses to open: `bank
schema v20 is newer than this binary supports (v19)` ... 2.1.0 therefore ships the schema v20, the
entry-type parameter and the TTL rule together."

**Fix:** merge only when the parameter lands. If the branch has to land first, record the gate in
the commit and in the plan, so the order survives the session that wrote it.

## F8: the column has to carry exactly one kind, and say so when it carries none

**Settled by the owner on 2026-10-22:** an entry has exactly one type, and the column is NOT NULL
with the default `none`. The branch's column is `entry_type TEXT NULL` with no value check
(`src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:193` and the ladder's `ALTER` at `:1414`),
and the ruling changes four things in the branch.

**1. The DDL.** The column becomes `entry_type TEXT NOT NULL DEFAULT 'none'`, in the `CREATE TABLE`
for a new bank and in the ladder's `ADD COLUMN`. SQLite permits NOT NULL on `ADD COLUMN` when a
non-null default is given, and the operation does not rewrite the table, so the 25,000-row bank
pays only for the backfill. The reprobe pattern for the concurrent-open race stays.

**2. The backfill selects on the wrong value.** The migration's `WHERE entry_type IS NULL`
(`MemorySchema.cs:1433-1435`) can never match once the default is `none`: every legacy row
arrives as `none`, and the step would leave the whole bank unclassified. The condition becomes
`WHERE entry_type = 'none'`, and the "never clobber an explicit type" property still holds, since
`file` and `memory` are not `none`.

**3. The shape fallback dies silently.** `ChunkBackfill.cs:108-109` and
`ChunkBoundaryRepair.cs:380-381` read the row's type and fall back to the shape rule with `??`:

```csharp
entryType = row.EntryType
            ?? EntryTypeClassifier.ToStoredValue(EntryTypeClassifier.Classify(row.Path, row.SourceFile)),
```

On a NOT NULL column `row.EntryType` is never null, so the `??` never fires. A row that carries
`none` is then carried forward as `none` instead of being classified by its shape, and the two
repair paths quietly stop doing the classification they were written to do. The fallback has to be
re-expressed on the value: an unclassified row (`none`) is classified by its shape, an
already-classified row keeps its type.

**4. The stored form gains the fourth value.** `ToStoredValue` of the unclassified member must
return `none` rather than null (a null would be rejected by a NOT NULL column), and
`FromStoredValue` maps `none` back to the unclassified member. The test at
`EntryTypeClassifierTests.cs:59` (`ToStoredValue(EntryType.None).ShouldBeNull()`) changes with it.

**The value domain is still open.** NOT NULL settles the presence of a kind, not its value: the
column accepts any string, and `FromStoredValue` maps an unknown one to the unclassified member,
where it is indistinguishable from a row that was never classified. A check
(`CHECK (entry_type IN ('none','memory','file'))`) would settle it, but SQLite cannot add a
constraint to an existing table: it needs a table rebuild, the same machinery the v16 to v17 step
already uses (`MemorySchema.cs:1548-1592`). The cost is one rebuild of `entries` with its FTS and
vector triggers. Since the column is new, the rebuild can carry the classified value in the same
step, which also avoids the bare `ALTER` the file's own comment warns about.

**The invariant has to be observable.** With `none` as a legal stored value, "an entry must have
exactly one type" is a claim the code can keep breaking quietly: a write path that omits the
column lands `none`, not an error. `SchemaDoctor` (`src/AiRaccoon.Infrastructure/Sqlite/SchemaDoctor.cs:13`)
is the read-only surface that already reports the bank's shape; it should report the number of rows
carrying `none`, so the unclassified population is visible to `doctor` and countable.

**Proves it:**

- A migration test that a legacy bank ends with every row classified (no `none` left) and that a
  rerun does not clobber an explicit type.
- A migration test that the column is NOT NULL: an insert that omits the type stores `none`, and an
  insert with a null type is rejected.
- A test that the two repair paths classify a row carrying `none` by its shape.
- A doctor test that the unclassified count is reported.

**This is also F1's sharp edge.** The sync pull (`SyncService.cs:461`) omits the column, so with
the default it no longer produces a null: it produces `none`, silently, and a synced document row
is a row the query cannot place. F1 must be fixed for the same reason, and the `none` count is how
it would be noticed.

## Checks that came back clean

- The shape rule is correct on the live bank (read-only aggregate, 2026-10-22): 23,200 file rows
  with `path = source_file` are file; 661 rows whose file source is a cited file are memory, which
  is the 654 of 2,150 the branch's own doc cites; 1,496 transcript rows are memory; 215 manual rows
  are memory. No row is misclassified.
- The branch builds clean: `dotnet build` in the worktree, 0 warnings, 0 errors.
- The entry-type tests pass: `dotnet test --filter "FullyQualifiedName~EntryType"`, 23 passed.
- The backfill is idempotent and never clobbers an explicit type: covered by
  `EnsureAsync_PreservesAnExplicitType_WhenRerun` and
  `EnsureAsync_WithTheColumnPresentButTheBackfillHalfDone_FinishesIt`.
- The six write paths the branch does cover declare their kind.

## Order of work

1. F8, because the owner's ruling changes the column, the backfill's predicate, the two repair
   paths and the stored form together, and F1 and F5 both land inside it.
2. F1, with its test. It is the only fix whose absence loses data.
3. F2, with the comparison test, so the two copies cannot part company again.
4. F3, F4, F5, F6, each a small local edit.
5. F7 at merge time, not before.

## Related findings from the same review, outside this branch

- `src/AiRaccoon/Setup/Cli/CliCommandTree.cs:679,680,685-687`: the four observability kinds are
  written out three times (the argument `HelpName`, its description, the validator pattern and its
  error message). One constant array would serve all three.
- `src/AiRaccoon/Tools/UnknownToolGuidance.cs:11,27`: the tool count comes from
  `McpToolInventory.Names().Count` while the class already holds the same set as `KnownTools`.
  `KnownTools.Count` is the one source.
- `tests/AiRaccoon.Tests/Unit/Mcp/UnknownToolGuidanceTests.cs:112-145`: the changelog parser reads
  every two-column markdown table in the file, so any other such table with backticked names is
  parsed as a rename. Scope the parse to the table under its own heading.
- `src/AiRaccoon/Tools/RenamedToolNames.cs:20-49`: the rename map is guarded against the live
  surface and against the changelog, but nothing proves it covers every pre-2.0.0 name. A missing
  entry degrades to the generic message with no red test.

## PR #913: what the branch carries, and what is still open

Reviewed at `53c7b9b5` (the branch head after the two changes below).

### The F1–F8 list, against the branch

| Finding | State in PR #913 |
| --- | --- |
| F1 sync pull writes no type | **Still open.** `src/AiRaccoon.Infrastructure/Sync/SyncService.cs:461` names the merge insert's columns explicitly and `entry_type` is not among them, so a pulled row lands with a null type. The branch does not touch `SyncService.cs`. |
| F2 the rule is written twice | **Still open.** The write paths call the injected classifier, so the runtime path has one rule — but the backfill re-encodes the rule in SQL (`MemorySchema.cs:1428-1433`) and the classifier re-encodes it in C# (`EntryTypeClassifier.cs:49`). The comparison test that would catch a drift is still absent. |
| F3 the orphaned v19 doc comment | **Still open.** `MemorySchema.cs:1390-1404` still carries the v18→v19 summary directly above the v19→v20 summary, with `MigrateToV19Async` left without one. |
| F4 two test files describe the opposite | **Partly fixed.** The unit test's class summary now matches the code. The migration test's summary still says the backfill classifies "from its SOURCE … the row's own shape cannot say it" — the opposite of what the code does. |
| F5 one enum doing two jobs | **Still open, and the owner's ruling sharpens it.** `EntryType.Code` ("code") names a corpus, not a kind; the stored domain is `{none, memory, file}`. |
| F6 a second "entry type", and one unreachable value | **Still open.** `src/AiRaccoon/Tools/EntryTools.cs:45` writes the literal `"memory"`, and the classifier's wire names are `public const` on the concrete class (`EntryTypeClassifier.cs:19-25`) — an interface cannot hold constants, so a consumer holding `IEntryTypeClassifier` cannot reach them without a cast. `SearchKindWireNames` is the existing single-source pattern for the corpus vocabulary, and the two vocabularies already collide on the word "code". |
| F7 the merge-order gate | **Moot.** PR #912 is closed, so the branch is unblocked and the gate no longer applies. |
| F8 the column | **Not implemented.** `MemorySchema.cs:193` and `:1414` still declare `entry_type TEXT NULL`; the backfill's predicate is `WHERE entry_type IS NULL` (`:1433`); `ToStoredValue(None)` returns null (`IEntryTypeClassifier.cs:39`). |

### New findings

- **N1 — CI was red on a repo gate, and it was one line.** The `build-fast` job failed with
  exit code 2. Reproduced on a clean checkout of the branch head: with the bundled model
  weights present, the fast lane has exactly one failure —
  `RetrySurfaceGateTests.EveryRetrySurfaceFile_UsesRetryAttributes` — because the new probe
  file `tests/AiRaccoon.Tests/Integration/Retrieval/TypedVecPartitionProbe.cs:19` used a bare
  `[Fact]` on a file inside the retry surface. Fixed in `53c7b9b5` (`[RetryFact]`, plus
  `using xRetry.v3;`). The 61 other local failures were environmental: a fresh worktree has
  no bundled embedding weights.
- **N2 — the title overstates what ships.** The PR claims "Implement Memory/Code/File entry
  selection, native typed vector partitions, and Memory-only TTL". The diff contains no
  search-path or tool change (so no entry selection), no production typed partitioning (an
  opt-in probe and a measurement record only), and no TTL rule (the `ttl_days` column in the
  rebudget insert is pre-existing). Either the title says groundwork and probe, or the branch
  holds the rest.
- **N3 — `Resolve` cannot tell "no declaration" from "an unknown declaration".**
  `IEntryTypeClassifier.cs:29` takes the stored value as a string and falls back to the row's
  shape for anything it does not recognise, so a corrupt or foreign stored value is silently
  normalised — and the repair path then writes that normalised value back. With the domain
  settled to `{none, memory, file}`, an unknown value is a defect that wants to be visible
  (doctor, report), not hidden.
- **N4 — the probe cannot fail in CI, so only the gate ever looks at it.** It returns early
  unless `AIRACCOON_TYPED_VEC_PROBE=1`. That is the right shape for an opt-in measurement, but
  it means the file's correctness is a matter for review, not for the pipeline.

### Changes made during the review

- `6ecf9f1c` — the v19 rebuild spec is a named record, not a six-field tuple. The
  `(string Table, int Dimension, string Source, string Ctx, string Column, string Where)`
  tuple became a nested `private sealed record VecRebuild`; the property names, and so every
  use of them, are unchanged.
- `53c7b9b5` — the typed partition probe joins the retry surface (N1).

## Review of `abb21046`: the F8 → F1 → F2 → F5/F6 → F3/F4 chunk

Verified on a clean checkout of that head: `dotnet build` — 8 projects, 0 warnings, 0 errors;
the fast lane (`--filter "Speed=Fast&Performance!=Benchmark"`) — 5173 passed, 0 failed, 2 skipped.

**Verdict: hold.** The plan's own gate says this PR "merges with the parameter and the TTL rule
— or not at all", and neither is in the branch. Three findings below are blocking; the rest are
nits. The GitHub API refuses inline review comments on this PR ("commit_id is not part of the
pull request", for every commit in it), so the findings live here and in the PR's review summary.

### Blocking

1. **The ladder never revisits a bank the earlier build completed.**
   `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:1415` — the `ADD COLUMN` and the backfill
   live in `MigrateToV20Async`, and the ladder skips any step a bank has already passed
   (`if (storedVersion >= CurrentVersion) { … return; }`, `MemorySchema.cs:774`). A bank that ran
   the earlier build of this schema — the nullable column, stamped v20 because that build's
   ladder completed — is at v20, so this step never runs for it again: the column stays nullable
   and its NULL rows are never classified.

   Reproduced (probe test on that head, the earlier build's end state constructed by hand):

   ```text
   PROBE: column NOT NULL = 0; row kind = NULL
   ```

   So the comment above the statement — *"NULL is the marker a bank touched by an earlier build
   of this schema carries … the backfill has to read both"* — describes a defense that does not
   fire for the population it names. The dual read is reachable only when the bank is still at
   v19: the crash between the `ADD COLUMN` and the stamp.

   The test that claims the coverage stamps v19
   (`tests/AiRaccoon.Tests/Integration/EntryTypeMigrationTests.cs:116`), so it covers the crash
   case, not the population its doc names. Either stamp that fixture v20 and watch the assertion
   fail, or state plainly that a bank the earlier build completed is unsupported and a `v21` step
   will normalise it.

2. **The doctor's unclassified count misses NULL rows.**
   `src/AiRaccoon.Infrastructure/Sqlite/SchemaDoctor.cs:54` — `entry_type = 'none'` does not match
   a NULL, and `entry_type NOT IN (…)` is NULL for a NULL row, so a NULL row lands in neither
   count. On a scratch bank with `'none'`, `'memory'`, `'file'`, `NULL`, `'transcript'`:

   ```text
   unclassified  unknown        unclassified_fixed  unknown_fixed
              1        1                       2              1
   ```

   A NULL row is what a bank with a nullable column holds — the population of finding 1 — so on
   exactly those banks the doctor reports "entries without a kind: 0" while every row is
   unclassified, and the doc above the line says this count is "how the omission shows up".

   ```sql
   SUM(CASE WHEN entry_type IS NULL OR entry_type = 'none' THEN 1 ELSE 0 END),
   SUM(CASE WHEN entry_type IS NOT NULL AND entry_type NOT IN ('none','memory','file') THEN 1 ELSE 0 END)
   ```

   One line, plus a test with a NULL row beside the two that exist.

3. **The "a declared kind wins" half has a second home, with a hardcoded domain and no parity
   test.** `src/AiRaccoon.Infrastructure/Sync/SyncService.cs:469` — `Resolve` is "a declared kind
   wins, else the shape"; the merge's `CASE` is the SQL twin of the same rule, and the parity
   test covers only the shape half. Two consequences: the domain here is spelled
   `IN ('memory', 'file')`, so a kind added to `EntryKind` stops being taken on merge (the row is
   filed by its shape instead — a silent misfile), and the doctor's domain list
   (`SchemaDoctor.cs:55`) would report that new kind as "a kind this build does not know"; and
   nothing compares this SQL against `Resolve`. Derive the list from `EntryKindWireNames`
   (Infrastructure already references Core) and extend the parity net to the declared half.

### Answers to the three open questions

**The value domain does not need the rebuild.** The domain rides on the ladder's own
`ADD COLUMN`: `ALTER TABLE entries ADD COLUMN entry_type TEXT NOT NULL DEFAULT 'none' CHECK(entry_type IN ('none','memory','file'))` is accepted by the repo's own SQLite, keeps `NOT NULL`, defaults to `'none'`, and refuses a value outside the domain (probe test, green). The same CHECK is free in the `CREATE TABLE` for a new bank. So the domain is enforceable now for every bank that does not have the column yet — every released bank, since v20 is unreleased. A rebuild is needed only for the banks that already carry the column, which is exactly the population of finding 1: a `v21` step would land the CHECK, the `NOT NULL` and the backfill together. The owner's open question is smaller than it looks, and it lands where the blocking finding does.

**N3 — the unknown value.** The doctor's counter and the repair path fight each other: `ChunkBackfill` and `ChunkBoundaryRepair` write `ToStoredValue(Resolve(row.EntryType, …))`, and `Resolve` maps an unknown stored value onto the shape, so the repair job rewrites the unknown value into a kind and the doctor's "unknown" count drops to zero for every row the repair has touched. The line built to expose an unknown value is consumed by the thing it was meant to report. Three states, not two — declared / unclassified / unknown — where only the unclassified case is filled in by the shape, and an unknown value is left alone and reported. With the domain CHECK in place, an unknown value can only arrive from a foreign writer or a corruption, which is exactly the case worth surfacing rather than overwriting.

**One fixture is enough for what it claims, and one layer short of the real net.** The fixture is enough to catch a drift between the two homes of the *shape* rule. Three decisions it leaves open: case sensitivity (a path and a source file differing only in case — the C# rule is `Ordinal`, the SQL `=` is SQLite's `BINARY`, they agree today, but on a case-insensitive filesystem `Docs/Guide.md` and `docs/guide.md` are the same file, so the answer is a decision rather than an accident); a null path with a non-null source file (the interface doc says a null path reaches the resolver, and no fixture row has one); and both null. And the net stops one layer short — the declared half now has a SQL home (finding 3) with no test comparing it against `Resolve`.

### Nits

- `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs:20` — the kind vocabulary is single-sourced in
  C# and spelled in six SQL places: `MemorySchema.cs:193`, `:1415`, `:1434`, `MemorySql.cs:20`,
  `SchemaDoctor.cs:54-55`, `SyncService.cs:469`. `EntryKindWireNames` says "A rename must update
  this class, not the literals that spell it" — all six can interpolate it.
- `tests/AiRaccoon.Tests/Integration/Sync/SyncServiceTests.cs:1463` — the comment says "A file row
  (path = source_file)"; the row is `path = 'doc.md#0'`, `source_file = 'doc.md'`, which under the
  shape rule is a memory. The test still proves what it means to (the remote's own kind is taken),
  and the real ingest writes `path = source_file` for both (`FileIngestor.cs:356`), but a reader
  who takes the comment at its word will believe the fixture is file-shaped.
- `src/AiRaccoon.Infrastructure/Ingestion/ChunkBackfill.cs:28` — the parameter is still
  `entryTypeClassifier`; the type is `IEntryKindClassifier`. Same in `ChunkBoundaryRepair`,
  `ChunkBudgetReconciler`, `FileIngestor`, `SqliteMemoryStore`, `WriteChunks`,
  `ChunkBoundaryRepairJob` and `ChunkBackfillJob`, and in the `entry_type AS EntryType` aliases.
- `src/AiRaccoon.Core/Memory/EntryKindClassifier.cs:58` — the default arm's message describes a
  set, but the parameter is single-valued: the only value that reaches it is an out-of-range one.
- `tests/AiRaccoon.Tests/Integration/Storage/EntryTypeRuleParityTests.cs:21` — the three unpinned
  decisions above.
- `src/AiRaccoon.Core/Memory/IEntryKindClassifier.cs:19` — "215 of the 25,340 rows on the live
  bank" against the plan's table (File 23,200 + Memory 2,371 = 25,571) and the research record's
  pinned 25,340: two snapshots, one number each, and no way to tell which is which. Cite the
  snapshot with the number.
- `src/AiRaccoon.Core/Memory/EntryKindWireNames.cs:18` — `Memory = SearchKindWireNames.Memory`
  ties the *stored* word to the *protocol* word. One is data in the user's bank, the other is a
  wire value: a corpus rename would rewrite the stored kind through this constant. Pin the stored
  vocabulary on its own and let a test say they agree.
