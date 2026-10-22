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

## Review of `10108e39`: the entry-type parameter's contract (S2, "flags")

Verified on a clean checkout of that head: `dotnet build` — 8 projects, 0 warnings, 0 errors; the fast lane (`--filter "Speed=Fast&Performance!=Benchmark"`) — **5197 passed, 0 failed, 2 skipped**.

**What landed well.** The vocabulary has one home; the seven asking sets are enumerated rather than implied; every refusal is stated as a refusal rather than as a fallback; the messages stay out of the MCP vocabulary (Core throws `ArgumentException`, the tool layer translates); and both refusals were watched fail before they were watched pass, which is the right way to land a gate.

**Verdict: the shape is right.** Two gaps in the contract itself, one thing to decide before the tool wires it, and a wire form the SDK can actually carry.

### Blocking

**1. The default — the sum — is not "every row": a row whose kind is `none` is in no bit.**
`src/AiRaccoon.Core/Memory/EntryTypeWireNames.cs:29` and the doc on `src/AiRaccoon.Core/Memory/EntryTypeTests.cs:39`.

The plan says *"`query` defaults to **Memory | Code | File** (the sum), so the default answers exactly what today's default (`kind=both`) answers"*, and this chunk repeats it: *"the default answers exactly what today's default (`kind=both`) answers, and more"*. Today `kind=both` applies **no kind filter at all** — it returns every row of the memory corpus plus the code corpus. A default implemented as a filter over the three kinds returns the rows of kind `memory`, the rows of kind `file` and the code corpus, and **drops every row whose kind is `none`** — the unclassified rows the branch itself counts and reports (`SchemaDoctor`: *"entries without a kind: N"*). So for exactly the population the doctor watches, the new default answers *less* than today's default, which is the opposite of the claim, and the "and more" is only true in granularity (you can now ask for one kind).

This is the same failure mode the empty-set refusal is designed to avoid, applied to the default instead: a query that quietly returns fewer rows than it used to reads as a broken bank. Either the default means *no filter* (every row, unclassified included), or the sum carries an implicit unclassified bucket, or the claim is amended to "every **classified** row" and the tool's description says that an unclassified row is outside every set. Whichever it is, it is a contract statement, and it is cheaper to write it now than to discover it in the query path.

**2. `Single` does not refuse unknown bits.**
`src/AiRaccoon.Core/Memory/EntryTypeWireNames.cs:80`.

`Single` refuses the empty set and a composite, but it never calls `RefuseUnknownBits` — so `Single((EntryType)16)` returns that value unchanged: a lookup naming a kind this build has no corpus for is accepted. The guard you added for "the other door" (a set assembled elsewhere) is not on the door that matters most, because the lookup is where a set reaches a decision. One line: `Single` should call `RefuseUnknownBits(types)` before the single-bit test, and the test file should have a case for it.

### The wire form (Q2) — keep it a string, and the comma list is the right shape

I probed the repo's own SDK (2.2.0) with a `[Flags]` enum parameter:

```text
PROBE SCHEMA: {"type":"object","properties":{"types":{"type":"string"}, ...}}
PROBE COMPOSITE SERIALIZED: {"types":"Memory, Code"}
PROBE BIND "memory,code"  -> 3 (Memory, Code)
PROBE BIND "Memory, Code" -> 3 (Memory, Code)
PROBE BIND "0"            -> 0 (None)
PROBE BIND "None"         -> 0 (None)
PROBE BIND "bogus"        -> REFUSED: The JSON value could not be converted to AiRaccoon.Core.Memory.EntryType.
```

Three things follow, and they answer the question:

- **The comma-separated list is exactly the SDK's own flags encoding** — `memory,code` binds to `Memory | Code`. The choice is SDK-compatible; a JSON array is not (it would need a second parsing path, and the SDK's schema for an array parameter is a different shape entirely). The research record's *"SDK 2.2.0 accepts `Memory, File` and `All`"* has no `All` member in this enum: on the wire, "all" is the three names spelled out — which is the sum, and that is fine.
- **The SDK cannot refuse the empty set.** `"0"` and `"None"` both bind to `EntryType.None`. So the zero criterion has to live in the application, in the contract you just wrote — which is where it is.
- **The SDK's refusal is not the tool's vocabulary.** An unknown name is refused by the binder with *"The JSON value could not be converted to …"*, not with the tool layer's `invalid-params: Invalid kind '…': expected memory, code, or both.` (`src/AiRaccoon/Tools/MemoryTools.cs:251`). The curated message only reaches the client if the **tool parses the string itself**.

So: declare the tool's parameter as a `string` (as today's `kind` is) and have the tool layer call `EntryTypeWireNames.Parse`, translating the `ArgumentException` into `invalid-params: …`. Two reasons beyond the message: the MCP schema for a flags-enum parameter is a bare `{"type":"string"}` with **no enum list**, so the client learns the vocabulary from the description either way — and a string parameter lets that description quote *Core's* vocabulary (lowercase, one spelling) instead of the SDK's member casing (`"Memory, Code"`); and the repo's principle is one spelling per word, which the SDK's binder does not give you.

### The empty set (Q1) — refuse it, and pair the refusal with the default

Refusing the empty set is right, and the plan's zero criterion supports it. The reasoning also has a twin: the same argument (*"a query that quietly returns fewer rows than it used to reads as a broken bank"*) is what the default has to satisfy, and today the default does not — see blocking 1. Keep the two statements next to each other.

Against the alternative — `"none"` meaning "unfiltered" — I would keep that out: "everything" is the *absence* of the parameter (the default), and giving the value a second spelling of "everything" makes "the sum" ambiguous (is the sum everything, or the three kinds?). One spelling for everything: the default.

### Where the unknown-bit guard lives (Q3)

In Core, beside the other rules — the place is right, and the *reason* is the SDK probe above: the binder cannot refuse anything, so every refusal has to be application-side. What is missing is coverage: `Single` does not call it (blocking 2), so the guard is applied on one path and not the other. `RefuseUnknownBits` is also the right shape for a set assembled elsewhere (a stored flag, a computed mask) — keep it public.

### The lookup rule (Q4)

`Single(types)` is the right shape, and the plan's *"no default; the type is required"* is honoured by `Parse`'s null refusal. Two things to pin when S6 wires it:

- `Single` must be called on the **parsed** value, so a composite lookup is refused rather than resolved to an arbitrary corpus.
- `Default` belongs to the query. It sits next to `Single` in the same class, which invites using it as a lookup default — the lookup has none, and the class doc should say which rule owns which.

### Nits

- `src/AiRaccoon.Core/Memory/EntryTypeWireNames.cs:23` — the word `file` is spelled in **two** places (`EntryKindWireNames.cs:21` and here), while the chunk's note says "each word is spelled in one place". `memory` and `code` are shared through `SearchKindWireNames`; `file` is new to both vocabularies and is the one word that can drift. Derive one from the other, or pin both in the test — the current test pins `memory` only (`EntryTypeTests.cs:102`).
- `src/AiRaccoon.Core/Memory/EntryTypeWireNames.cs:32` — `KnownBits = Default` couples "the kinds this build knows" to "the set a query asks for by default". If the default ever stops being the union (a reason to leave `Code` out of it, say), the known-bit set moves with it. Name them separately.
- The class name says "names"; the class carries the parameter's **rules** (`Parse`, `Single`, `RefuseUnknownBits`). `SearchKindWireNames` carries one conversion (`ToWireString`), which is a naming story; a parser and two refusal rules are a contract. A name like `EntryTypeParameter` — or a sibling that holds the rules — says what the thing is.
- `src/AiRaccoon.Core/Memory/EntryTypeWireNames.cs:45` — `Parse` refuses null with *"The entry type is required"*, which is right for the lookup and wrong for the defaulted query. The tool layer must substitute the default before parsing, and the class doc says nothing about a default path.
- `tests/AiRaccoon.Tests/Unit/Memory/EntryTypeTests.cs:55` — `Parse_RefusesTheEmptySet` includes `"none"`, which is refused as an **unknown name**, not as the empty set. Two different rejections under one test name; split them, because if the vocabulary ever grows a `none` spelling the test would still pass while the meaning moved.
- The bits mix two axes: `Memory` and `File` are row kinds inside the memory corpus, `Code` is a corpus. So `Memory | File` is today's whole memory corpus, and `memory` alone is **not** today's `kind=memory` (which returns file rows too). A client migrating from `kind=memory` to `entryType=memory` loses file rows silently. The class doc should say what each bit selects.

## The 2.1.0 scope: every entry in this list ships in 2.1.0

**Ruled by the owner on 2026-10-22: all of it lands in 2.1.0.** The schema, the parameter, the
TTL rule and the findings that make them honest arrive together — nothing on this list waits for
a later release. Several PRs may carry the work; the release is one, and the 2.1.0 changelog
entry names every PR that ships a part of it, PR #913 among them. The plan records the ruling in
`docs/plans/2026-10-21-mcp-tool-surface-rename.md`, under "The 2.1.0 scope".

The list is written against the head this branch carried when the PR description was last
rewritten. Everything here is open unless it says otherwise.

### Done in this round

- The PR description was stale — it was written for `4c71eba8` and still claimed "F1–F7 are fixed
  at this head", quoted the old surface numbers (3280 passed of 3291), and omitted the parameter's
  contract entirely. It now describes the head, quotes the current lanes (fast 5197 / slow 1599,
  both 0 failed), documents the parameter contract as *here but unwired*, and lists the open items
  below instead of the old F-list claim.
- The description's claim that "SQLite cannot add a constraint to an existing table — it needs a
  rebuild" was wrong for the ladder's own path: the value-domain `CHECK` rides on
  `ALTER TABLE ... ADD COLUMN` without a rebuild, verified against a scratch bank. Only the banks
  that already carry the column need one.
- The description now names the release: the "not here" table carries a "lands in" column, every
  known gap is marked `→ 2.1.0`, and the scope paragraph states the owner's ruling that the whole
  list ships in 2.1.0. The plan gained the same ruling in "The 2.1.0 scope".

### A. The merge gate (F7) — the PR cannot merge without these

- **A1. The parameter's wiring (the plan's S6).** `query` takes the entry-type parameter with the
  sum as its default; `get_entry` takes it with no default; the tool layer parses the string with
  `EntryTypeWireNames.Parse` and turns the `ArgumentException` into `invalid-params: …`. The wire
  form is a string, not a JSON array — the comma-separated list is the SDK's own flags encoding.
- **A2. The Memory-only TTL rule**, on the one classifier: a transcript is Memory and may carry a
  TTL, a File entry may not.

### B. Blocking findings from the review of `abb21046`

- **B1. The nullable-v20 population.** `CurrentVersion` was 20 and the ladder returns early for a
  bank already at 20, so a bank that went through the first v20 build keeps a nullable column and
  its NULL rows, and the backfill never runs on it. A v21 normalization step — or an explicit
  "that population is unsupported" ruling — is needed.
  *This became load-bearing with the in-flight v21 work:* the typed vec0 rebuild reads the row's
  `entry_type` to choose the partition, so a NULL kind would file a vector under a NULL partition.
  The peer's `EnsureEntryTypeColumnAsync` does not close this: it returns early when the column
  already exists, so a nullable column stays nullable.
  **RESOLVED FOR THE ROWS at `8656fddd`:** the v21 step classifies every row before it rebuilds
  the vectors, so no vector is filed under a NULL partition.
  **THE POPULATION IS DEVELOPMENT-ONLY** (ruled in the review of `4cdfeb8f`): v2.0.0 and v2.0.1
  both ship `CurrentVersion = 19`, the nullable `ADD COLUMN entry_type TEXT` only ever existed in
  branch commits (`3171c07f`, `db94682b`), and no release ever shipped a v20 bank — so for a user
  the ladder runs the v20 step, which adds the column as `NOT NULL DEFAULT 'none'`. The column
  stays nullable only in the dev banks that ran this branch's earlier builds. Those banks are
  disposable: re-create them, do not migrate them. The rebuild that would make the column NOT NULL
  is therefore **not a 2.1.0 user requirement** — it is the owner's question, framed in E1.
- **B2. The doctor cannot count a NULL row.** Its unclassified predicate is `entry_type = 'none'`
  and a NULL row satisfies neither it nor the unknown predicate, so it is counted as neither.
  A NULL-row test belongs with the fix.
- **B3. The sync merge's declared-kind branch is a second SQL home** for the kind domain
  (`SyncService.cs:469` spells `'memory', 'file'` out). It must derive the domain from the one
  vocabulary, and the parity coverage must reach it: a declared value, a `none` value, an unknown
  value.
- **B4. N3 — the repair paths normalise an unknown stored value away**, so the doctor's unknown
  count can never report one. Unknown, undeclared and declared are three states; only the
  undeclared one may be resolved from shape.

### C. Blocking findings from the review of `10108e39`

- **C1. The default's meaning is undecided.** The plan and the chunk both claim the sum "answers
  exactly what today's default (`kind=both`) answers", but `kind=both` applies no kind filter
  while the sum is a filter over three bits — so a row whose kind is `none` is in no bit and the
  new default answers *less*. The contract has to say which it is: no filter, the three kinds, or
  every *classified* row — and the plan's claim is amended to match.
- **C2. `Single` does not refuse unknown bits.** `Single((EntryType)16)` returns the value
  unchanged. One line (`Single` calls `RefuseUnknownBits` before the single-bit test) plus a case.

### D. Nits, from both reviews

- **D1.** The word `file` is spelled in two places (`EntryKindWireNames.cs:21`,
  `EntryTypeWireNames.cs:23`); the test pins `memory` only. Derive one from the other, or pin both.
- **D2.** `KnownBits = Default` couples "the kinds this build knows" to "the set a query asks for
  by default". Name them separately.
- **D3.** The class says "names" but carries the parameter's rules (parse, two refusals). A name
  like `EntryTypeParameter` says what the thing is.
- **D4.** `Parse(null)` refuses with "required" — right for the lookup, wrong for the defaulted
  query. The doc says nothing about a default path.
- **D5.** `Parse_RefusesTheEmptySet` includes `"none"`, which is refused as an unknown name, not
  as the empty set. Two rejections under one test name.
- **D6.** The bits mix two axes (`Memory` and `File` are row kinds inside the memory corpus,
  `Code` is a corpus), so `memory` alone is not today's `kind=memory`. The plan's move #1 already
  flags the old rows' meaning; the class doc should say what each bit selects.
- **D7.** The parity fixture omits case sensitivity, a null path with a non-null source, and both
  null; and it does not compare the sync "declared kind wins" half with `Resolve`.

### E. The value domain — a decision, now framed correctly

- **E1.** Put `CHECK (entry_type IN ('none','memory','file'))` on the DDL and on the ladder's
  `ADD COLUMN` (both verified possible without a rebuild); the normalization step in B1 carries it
  for the banks that already have the column. The reason it is worth doing is B4: while the repair
  paths normalise unknown values away, the doctor can never report one.
  **The owner's question, as sharpened in the review of `4cdfeb8f`:** it is no longer "CHECK or no
  CHECK" — it is **"one rebuild of `entries` carrying `NOT NULL DEFAULT 'none'` and the domain
  `CHECK`, or no rebuild at all"**. The rebuild is not a 2.1.0 user requirement (the nullable
  population is dev-only, see B1), and doing a NOT NULL-only rebuild now would make the owner's
  later "yes" a *second* rebuild of `entries` — two rebuilds, two migration steps, for a schema
  that ships once. One rebuild, after the ruling. The cost of "no rebuild" is known: the domain
  stays unenforced, and the doctor's unknown count is the only thing that sees an out-of-domain
  value — which the tri-state read now makes reachable.
  **The interaction the rebuild brings** (raised by the peer, and it is real): under the `CHECK`, a
  repair that rewrites a row carrying an unknown value fails the constraint. The two changes have to
  land together, and the interaction is best handled one step earlier — the repair path asks
  `ReadStoredKind` *before* it rewrites and skips an unknown-kind row (reporting it), so the
  constraint never fires and the batch needs no partial-failure path.

### F. The steps beyond this PR

- **F1. The typed query** — the search filters on the stored kind, with the scope composition the
  plan fixes: a `shared` query with type `Code` is empty by construction, and the refusal text
  must not pretend otherwise.
- **F2. The production typed vector partitions** — in flight in the branch worktree (v21,
  ADR-0141: `type` as the second vec0 partition key). When it lands, the PR description's
  "not here" table changes: native typed partitions move from *not here* to *here*.

### G. Release chores, after the gate is met

- **G1.** Bump `VERSION` to 2.1.0 (the release split: 2.0.0 is the rename alone, the entry types
  ride in 2.1.0), add `docs/changelog/2.1.0-*.md`, and update the changelog index. The changelog
  convention is that the entry is written in the PR that bumps `VERSION` and that it names every
  PR shipping a part of the release — so the 2.1.0 entry names PR #913 and each follow-up PR,
  and the scope it describes is the list above, in full.
- **G2.** The framework side that speaks the new names (ai-badger #650, the feed-badger change)
  must land before the release ships.
- **G3. The release work is untracked in the task graph.** The live plan
  (`air-rename-mcp-tools-entry-types`, revision 39) has no 2.1.0 release step: its
  `release-docs` step is the 2.0.0 one, and it is in progress for a release that already
  shipped. The plan cannot be replaced while steps are in progress, so whoever owns the plan
  adds a 2.1.0 release step (version, changelog entry, index line, the client-facing note) —
  otherwise the release is the one thing on this list with no step behind it. The same note
  applies to the `entry-type-classifier` step, which is marked complete: its ac4 ("no entry row
  is left with a NULL or unknown `entry_type` after the migration") passed against a gate that
  could not see the nullable-v20 population and the doctor's NULL blindness, so the findings
  that close them belong to 2.1.0, not to a closed step.

### H. PR hygiene

- **H1.** Done — the description is rewritten against this head.
- **H2.** GitHub refuses every inline review comment on this PR with "commit_id is not part of the
  pull request" (and "The commitOID is not part of the pull request"), for every commit in it, and
  the endpoint accepts neither `position` nor `subject_type`. The findings therefore live in the
  two review summaries and in this record. That is a GitHub-side question, not a code one.

## Review of `db94682b` + `8656fddd`: the typed vector layout (ADR-0141, v21) and the fixes

Verified on a clean checkout of `8656fddd`: `dotnet build` — 8 projects, 0 warnings, 0 errors; the fast
lane — **5198 passed, 0 failed, 2 skipped**; the slow lane — **1607 passed, 0 failed, 14 skipped**.

**What landed well.** The seven fixes are the fixes I asked for, and each carries a red proof: the
nullable-v20 bank is classified before the vectors are rebuilt (the ordering was the point), the
doctor counts a NULL kind, the sync merge names its kinds by the one vocabulary, `Single` refuses
an unknown bit, and the two words are spelled in one place. The typed layout is measured before it
is written down, and the two properties that matter are tested by name: a typed KNN reads only the
asked-for kind, and an unrestricted query still reads the whole context — the compatibility
property the ADR depends on, and the one that would have broken every existing search.

**Verdict: the layout is right, the fixes are right, and one piece of B1 is still open.** Below is
that piece, then my reading on the three questions you asked.

### Blocking

**1. The nullable-v20 column is healed, not fixed — the schema half of B1 is still open, and it is
E1's step rather than a separate one.**
`src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:1866` (`EnsureEntryTypeColumnAsync`), the v21
normalization at `:1503`.

The fix is right for the rows: a bank that went through the first v20 build has every row
classified before the vectors are rebuilt, so no vector is filed under a NULL partition. What is
still true is that **the column itself stays nullable, with no default, forever** —
`EnsureEntryTypeColumnAsync` returns early when the column exists, and the v21 step's `UPDATE`
classifies rows, it does not change the column's definition. So on exactly the population that
carries the defect:

- the schema still permits a row with no kind. Today's write paths all supply one
  (`MemorySql.InsertEntry`, `InsertRebudgetEntry`, the sync merge), so this is latent, not live —
  but it is enforced by the writers' discipline rather than by the schema, on the one population
  where the discipline is the only thing standing between a row and a NULL kind;
- and a NULL kind has a *silent* consequence the doctor cannot see: the typed trigger files the
  vector under a **NULL partition** (`INSERT INTO vec_entries(rowid, ctx, type, embedding) VALUES
  (NEW.id, …, NEW.entry_type, …)`), and a NULL partition matches no typed query
  (`ctx=@ctx AND type=@type`), so the row's vector is an orphan — the doctor's unclassified count
  sees the *row* (B2's fix ✓) and nothing reports the *vector*.

The fix is the rebuild you are already contemplating for E1, and it is worth doing as **one step
with three properties**: rebuild `entries` with `entry_type TEXT NOT NULL DEFAULT 'none' CHECK
(entry_type IN ('none','memory','file'))` for the banks that already carry the column. That gives
the NOT NULL contract on the nullable population, the value domain (E1), and — the reason it is
worth the risk — a CHECK that stops the unknown-value population from being written at all, which
is what makes B4's doctor count reachable. Two details for that rebuild: the v17 rebuild's own
`CREATE TABLE entries` (`:1630`) has **no `entry_type` column** and its copy (`:1671`) does not
carry one, so a v16→v21 bank loses the column at v17 and gets it back at v20 — a v21 rebuild has
to carry the column in both the DDL and the copy; and the rebuild has to re-install the typed
trigger bodies, because the rename re-parses them.

### My reading on your three questions

**B4 — the shape.** The defect is one line deep: `FromStoredValue` maps *any* value this build does
not know to `EntryKind.None`, which is also the value that means "undeclared". So "unknown" and
"undeclared" are one state, and `Resolve` then rewrites the row from its shape — the repair path
erases the evidence, and the doctor's unknown count can never be non-zero.

The shape I would take is a **tri-state read**, with three callers giving three different answers:

- the classifier gains a tri-state read — `ReadStoredKind(value) → Declared(kind) | Undeclared |
  Unknown(raw)` — where `Undeclared` is null or `'none'` and `Unknown` is anything outside the
  domain;
- the **repair path** branches: undeclared → write the shape's kind; unknown → **leave the row
  alone and report it**. Rewriting an unknown value is what destroys the evidence; not rewriting it
  is what makes the doctor's count reachable;
- the **query path** may treat an unknown kind as "in no set" (it is neither memory nor file) —
  but that is a decision to write down, not an accident of `FromStoredValue` returning `None`. The
  default (no kind filter) includes it ✓, consistent with C1;
- the **sync merge** has the same hole, one line over: `CASE WHEN r.entry_type IN (known) THEN
  r.entry_type ELSE <shape> END` (`SyncService.cs:475`) rewrites a remote's *unknown* kind by its
  shape. A remote's `'none'` legitimately falls back to the shape — it has not classified its rows
  — but a remote's *unknown* kind is data from another machine and should land as unclassified
  (`'none'`, visible) or be refused, not guessed.

And `Resolve` itself: either it refuses an unknown value (a typed exception the repair path
catches and reports) or it is renamed to say it only answers the declared/undeclared case. What it
must not do is fall back silently for a value it did not understand.

**E1 — the shape.** The rebuild, as in the blocking finding above: `entries` rebuilt with
`NOT NULL DEFAULT 'none'` and the domain `CHECK`, carrying the column in the DDL and the copy,
re-installing the typed bodies, and — the ordering you just got right in the v21 step — classifying
the rows **before** the copy, so the typed vec rebuild sees a kind for every row. The v17 machinery
is the right machinery: it already knows how to rebuild `entries` while preserving the rowids, the
FTS companion rows and the vec companion rows, and the FTS/vec keying is on the rowid.

**D3–D7 — which are load-bearing.** One of them, one more, and the rest are not:

- **D4 is load-bearing** (`Parse(null)` says "required" where a defaulted query wants a default
  path). The wiring is the next step and it will call `Parse`; with the current doc, "the entry
  type is required" reads as "the query parameter is required", and a legitimate default query
  fails. One line in the doc — "a parameter with a default substitutes the default before it is
  parsed" — before S6, not after.
- **D7 is worth doing and cheap.** The parity test is the only thing holding the C# rule and the
  SQL rule together, and they agree today on boundaries the fixture does not pin: the C# rule
  compares with `StringComparison.Ordinal` (case-sensitive) and SQLite's `=` on TEXT is BINARY
  (case-sensitive) ✓ — but nothing pins the agreement, so changing the C# side to
  `OrdinalIgnoreCase` leaves the fixture green while the rules diverge. The same for a null path
  with a non-null source, and for both null.
- **D3 and D5 are cosmetic.** The class name is a naming story, and "none" being refused as an
  unknown name is a message detail. Neither changes behaviour, and I would not spend a step on them
  before the wiring.
- **D6 is done** ✓ — the class summary says what each bit selects, and the "a named set excludes
  the rows that carry no kind" consequence is the price of "the default is the only unfiltered set".

### Nits

- **The ladder's intermediate states are internally inconsistent, and nothing says so.** The v17
  rebuild creates an `entries` table with **no** kind column and installs the **typed** trigger
  bodies on it; the v19 rebuild recreates the vec tables in the **untyped** shape while the typed
  bodies stay attached. Both states are transient — the ladder runs inside one open, the v21 step
  ends with the tables and the bodies matching ✓ — but a typed body is only valid on a typed table
  *and* on a table that has the kind column, and that invariant is neither written down nor tested.
  A probe on this SQLite confirms where the check actually happens: `CREATE TRIGGER` with a body
  naming a missing column is **created** (`CREATE: CREATED`) and the failure surfaces when the
  trigger fires or when a `RENAME` re-parses the schema — which is exactly why the v17 rebuild's
  rename happens *before* the typed body is installed ✓. The cheap way to remove the fragility: give
  the v17 rebuild's table the column (`entry_type TEXT NOT NULL DEFAULT 'none'`, with the copied
  rows taking the default and the v20 backfill classifying them) and keep the typed bodies valid
  from the moment they are installed.
- **No test covers a bank with no kind column at all** — the exact population
  `EnsureEntryTypeColumnAsync` exists for. The legacy fixtures restore the old *bodies* and the old
  *vec tables*, but every one of them keeps the current `entries` shape, so the column's own
  creation path is unexercised. `ALTER TABLE entries DROP COLUMN entry_type` is a cheap fixture.
- **The default's promise is not testable yet, and the doc should name the gate.** "The sum is NO
  KIND FILTER" is a promise the query path has to keep, and it is only observable once the query
  path exists — the task graph already carries the gate (`entry-type-parameter` ac1: "query with
  the default entry type returns the same result set today's default (kind=both) returns"), so
  naming it in the doc tells the wiring what it must not break. The same goes in the client-facing
  migration note: **a named set excludes the rows that carry no kind**, so those rows are reachable
  only by the default — a client that upgrades and starts asking for `memory,file` loses them,
  silently.
- **The `EntryTypeWireNames` class doc's "WHAT THE DEFAULT MEANS" paragraph is the contract, and
  it is currently the only place it exists.** When the wiring lands, that paragraph is what the
  implementation is checked against; it is worth a second reader at that point rather than now.

## Review of `4cdfeb8f` + `3e87cebe`: the tri-state read, the remote's unknown kind, and the ruling on the rebuild

Verified on a clean checkout of `3e87cebe`: `dotnet build` — 8 projects, 0 warnings, 0 errors; the fast
lane — **5201 passed, 0 failed, 2 skipped**; the slow lane — **1608 passed, 0 failed, 14 skipped**.

**The chunk is right.** The tri-state read is the shape I asked for, the merge's rule lives in one
place beside the shape rule, the parity test now compares both halves over one fixture, and the
plan says what the default means. One correction to my own review, and then the ruling you asked
for.

### The ruling on the rebuild: it should not land now, and my finding was over-scoped for the users

I went looking for the population my finding is about, and it is **development banks only**:

```
git show v2.0.0:src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs | grep CurrentVersion
71:    internal const int CurrentVersion = 19;          # 2.0.0 ships v19
git show v2.0.1:…                                       # 2.0.1 ships v19 too
git log -S"ADD COLUMN entry_type TEXT" -- …/MemorySchema.cs
3171c07f  (branch)   db94682b  (branch)                 # the nullable column only ever existed here
gh release list                                          # v2.0.1 is the latest; no 2.1.0 prerelease
```

So: **no released version ever shipped a v20 bank.** Every user's bank is v19, and for a user the
ladder runs the v20 step, which adds the column as `TEXT NOT NULL DEFAULT 'none'`
(`MemorySchema.cs:1415`) and then v21. The nullable column never reaches a user's bank — it can
only exist in the banks of whoever ran this branch's earlier builds (mine, yours, CI's).

That means **my blocking finding stands for the development population and does not stand for the
user population**, and my "one step with three properties" was over-scoped: of the three, the NOT
NULL contract is dev-bank hygiene, and the value domain is the owner's. I am lowering the severity
of that finding accordingly, and the conclusion follows:

**Do not do the rebuild now.** Two reasons, and the second is the decisive one:

1. It is not a 2.1.0 user requirement. The population is disposable — a dev bank can be re-created
   in a second, where migrating it means a rebuild of `entries` with its FTS and vector triggers.
2. **If the rebuild lands now with `NOT NULL` and no `CHECK`, and the owner later rules yes to the
   `CHECK`, that is a second rebuild of `entries`** — two rebuilds, two migration steps, two rounds
   of the riskiest thing in this branch, for a schema that ships once. One rebuild, after the
   ruling.

So the owner's question is not "CHECK or no CHECK" any more; it is the sharper **"one rebuild of
`entries` carrying `NOT NULL DEFAULT 'none'` and the domain `CHECK`, or no rebuild at all"** — and
it is worth deciding with that framing, because the cost of "no rebuild" is now known: the domain
stays unenforced, and the doctor's unknown count is the only thing that sees an out-of-domain
value (which is exactly what the tri-state read now makes reachable ✓).

What I want in the meantime, and it is small:

- the PR description must say **which population** the nullable column applies to — the branch's own
  dev banks — so the gap is not read as a user-facing hole;
- the plan's 2.1.0 scope list should carry the same sentence;
- and the dev banks from the earlier build should be declared **disposable** (re-created), not
  migrated. That is the cheap resolution of the whole question, and it costs one sentence.

### Your point 2 is right, and there is a better way to handle the interaction than catching the violation

Under `CHECK (entry_type IN ('none','memory','file'))`, a repair that rewrites a row carrying an
unknown value fails the constraint — so the repair must not rewrite it. Your reading (treat the
constraint violation as "leave the row alone and report it") is correct, and the two changes do
have to land together. I would put the interaction one step earlier, though: **the repair path
should ask the state before it rewrites, not catch the violation after it.** `ReadStoredKind`
already knows — an unknown kind is `StoredKindState.Unknown` — so a row in that state is *skipped
and reported*, and the `CHECK` never fires. Catching a constraint violation inside a batch repair
means a partial-failure path (the other rows in the batch still have to be written, and the
failure has to be told apart from a real defect); a pre-check is a skip, and the state is already
in hand. The behaviour is the same, the code is simpler, and the constraint stops being the thing
that notices.

### The chunk, in detail

- **The tri-state read** (`EntryKindClassifier.ReadStoredKind` → `StoredKind` with
  `Declared | Undeclared | Unknown` and the raw value kept) is exactly the contract: an undeclared
  row is null or `'none'`, an unknown value is anything outside the domain, and the raw value is
  carried so a caller can report *what* it did not understand.
- **`ResolveStoredValue`** is the right shape for the repair paths: an unknown value is carried
  through untouched, an undeclared row is resolved from its shape, a declared kind is kept. The two
  repair paths (`ChunkBackfill`, `ChunkBoundaryRepair`) ask the classifier rather than resolving
  and re-storing the kind themselves ✓ — that is how the three states stop being three different
  things in three places.
- **The merge** now lands a remote's unknown kind as `'none'` — where the doctor counts it —
  while a remote's `'none'` still falls back to the shape. The asymmetry is principled and I would
  keep it: the merge *creates* a row and must choose a kind this bank knows, while the repair
  *rewrites* a row and must not destroy the value already there.
- **One home for the merge's rule** (`MemorySql.EntryTypeFromDeclarationOrShape`), with the parity
  test comparing it against the classifier's read over one fixture of declared / undeclared /
  unknown ✓. The shape fixture now carries the cases it was missing: a null path, a null path and
  a null source, and a path differing only in case ✓ — the case one is the one that mattered, since
  the C# rule compares ordinally and SQLite's `=` is BINARY, and nothing pinned that they agree.
- **D3, D4, D5** are done: the class is `EntryTypeParameter` ✓, the default path is written down
  ("a QUERY that names no kind asks for `Default` — the tool layer applies the default before it
  parses. A LOOKUP names no default: it requires one, so it refuses an empty value rather than
  defaulting") ✓, and `'none'` is refused as an unknown name with its own test ✓.
- **The plan** now says what the default means, in the same words as the contract ✓ — that was the
  amendment I asked for, and it is in the right place.

### Nits

- **`Resolve` is still public on the interface, and it still guesses.** `FromStoredValue` maps an
  unknown value to the undeclared kind, so `Resolve` answers a shape for a value it did not
  understand. It has **no production call site any more** (the only caller left is
  `ResolveStoredValue`, which filters the unknown case first) — which is exactly why it is worth
  closing now rather than later: the guessing API is one call away from being used again, and the
  doc still says "a row with none is classified by its own shape", which no longer describes the
  whole truth. Either `Resolve` refuses an unknown value, or its doc says it answers the declared
  and undeclared cases only and points at `ReadStoredKind` for the question. A doc line, or one
  `if`.
- **The merge's "land it as `'none'`" survives only until the local backfill runs** — a `'none'`
  row is exactly what the v20/v21 backfill resolves from its shape. In practice it survives (the
  merge's INSERT names `entry_type`, so it needs a v20+ local bank, and the backfill has already
  run by then), but the next reader will wonder, and one sentence in the merge's comment settles it.
- **The query path's third caller still has no home.** "An unknown kind is in no set" is right and
  is consistent with the no-filter default ✓ — but it is currently only in this review. When the
  typed query lands, it belongs in the contract next to the default's meaning, not in the SQL.
