# Entry-type classifier: compiled fix list

**Date:** 2026-10-22
**Subject:** branch `task/air-entry-type-classifier-v2`, commits `d0ac1ff8`, `c682c450`, `451594c1`,
`6acc4b33` (worktree `.ai-badger/worktrees/air-entry-type-classifier`)
**Source:** independent code review of the session `01a162d9-4b46-75e6-882b-ecb858d48f0e`
**Verdict:** do not merge yet. Six code fixes (F1 to F6) and one merge-order gate (F7).

The branch adds the denormalised `entries.entry_type` column, the v20 ladder step and the write-path
declarations. The classification rule itself is sound: a read-only aggregate over the live bank
(`~/.ai-raccoon/memory.db`) puts 23,200 file rows with `path = source_file` (file), 661 rows whose
file source is a cited file (memory), 1,496 transcript rows (memory) and 215 manual rows (memory),
so no row is misclassified. The defects below are in the write coverage, the composition around the
rule, and the prose that describes it.

## The list

| # | Severity | Location | Defect | Fix |
|---|---|---|---|---|
| F1 | SHOULD | `src/AiRaccoon.Infrastructure/Sync/SyncService.cs:461` | the sync pull inserts rows without `entry_type` | add the column and derive it, or carry it in the payload |
| F2 | SHOULD | `src/AiRaccoon.Core/Memory/EntryTypeClassifier.cs:41-45`, `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:1425-1435` | the rule exists twice, in C# and in SQL | one rule, or a test that compares the two |
| F3 | SHOULD | `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:1390-1404` | `MigrateToV19Async` lost its doc comment, `MigrateToV20Async` carries two | move the v18 to v19 block back |
| F4 | SHOULD | `tests/AiRaccoon.Tests/Integration/EntryTypeMigrationTests.cs:10-15`, `tests/AiRaccoon.Tests/Unit/Memory/EntryTypeClassifierTests.cs:7-12` | both class summaries describe the rejected source-based rule | rewrite both summaries to the shape rule |
| F5 | SHOULD | `src/AiRaccoon.Core/Memory/EntryType.cs:9-22`, `src/AiRaccoon.Core/Memory/EntryTypeClassifier.cs:46-53` | one enum does two jobs: the parameter wants a flag set, the stored column holds a single kind, and the stored form swallows a set as NULL | split the two roles; make the stored form refuse a set |
| F6 | SHOULD | `src/AiRaccoon.Core/Memory/EntryType.cs:19`, `src/AiRaccoon/Tools/EntryTools.cs:48,52,60-67` | a second "entry type" with different meaning; the wire names are duplicated; `Code` is unreachable | one source for the wire names; say where a `code` row comes from |
| F7 | SHOULD (gate) | `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:71`, `docs/plans/2026-10-21-mcp-tool-surface-rename.md:205-215` | the branch migrates user banks to v20, which the released 2.0.x cannot open, and nothing in the branch reads the column | merge only with the query parameter, or record the gate |

## F1: the sync pull writes rows with no type

The merge statement's column list ends at `chunk_index`:

```sql
INSERT OR IGNORE INTO entries (hash, path, value, source_file, section, scope, project_id, context_label,
                               workspace_id, agent_id, created_at, updated_at,
                               access_count, last_accessed_at, rating, ttl_days,
                               embed_state, embedding, chunk_index)
```

`entry_type` is absent, so every row pulled from a remote bank lands with `entry_type = NULL`. The
branch updated the six other insert sites (`WriteChunks`, `SqliteMemoryStore.WriteAsync`,
`FileIngestor`, `ChunkBackfill`, `ChunkBoundaryRepair`, `ChunkBudgetReconciler` through
`MemorySql.InsertRebudgetEntry`); this is the seventh and the only one left.

**Why it matters:** the contract the branch states in `EntryTypeWriteTests.cs:10-14` is "a row
written without one is a row the query cannot place". The hole is permanent, because
`ON CONFLICT DO NOTHING` means a later local write of the same hash never repairs the type. When the
2.1.0 query parameter filters on `entry_type`, synced rows drop out of the result set without an
error. The lane brief said the same thing: "Every write path must set the type."

**Fix:** add `entry_type` to the column list and derive it from the remote row's shape
(`r.path = r.source_file` gives `file`, otherwise `memory`), or carry the type in the sync payload so
a 2.1.0 to 2.1.0 sync preserves it. Either way, add a test that pulls a row and asserts the type is
not NULL.

**Proves it:** `dotnet test --filter "FullyQualifiedName~SyncService"` plus a new test that pulls a
row and reads `entry_type`.

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

**Fix:** have the backfill call the C# rule over the NULL rows, or generate the SQL predicate from
the same constants, and add a test that runs both over one fixture.

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

**Correction to an earlier draft of this record:** the flag attribute is deliberate and correct for
the parameter. The plan states it: "The parameter is a flag enum, so a caller can ask for Memory
alone, Code alone, File alone, or any sum. It replaces `kind` (`memory|code|both`)"
(`docs/plans/2026-10-21-mcp-tool-surface-rename.md:121-122`). Dropping `[Flags]` would break the
parameter the plan settles, so the fix is not to remove the flag.

What the flag does create is an overload. The same enum is used for two things with different
domains:

- **As the query parameter** it is a set: `Memory | Code | File` is the default, and `Code` names
  the `code_entries` corpus (the plan's table at `:126-131`).
- **As the stored kind of a row** (`entries.entry_type`) it is a single value, and `Code` is
  unreachable: no `entries` row is a code chunk, because code chunks live in their own table.

`ToStoredValue` (`EntryTypeClassifier.cs:46-53`) is where the two meet. It switches on the three
single values and returns null for anything else, so a set handed to it becomes
`entry_type = NULL`:

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
has to decide what NULL means, and the only rule the code offers is `FromStoredValue`, which maps
it to `EntryType.None` (an unclassified row). `SearchKind`
(`src/AiRaccoon.Core/Memory/SearchKind.cs:4-9`) is already the corpus-level vocabulary in the
codebase, and its doc comment sets the house rule: "a rename must update this class, not three
string literals". The new flag enum and `SearchKind` will have to be reconciled by whoever wires
the parameter, and the plan says the parameter replaces `kind`.

**Fix:** split the two roles rather than blurring them.

1. Keep a flag enum for the filter, and give the stored column a single-valued kind type with no
   `Code` member. If the filter enum replaces `SearchKind`, say so in one place.
2. Make `ToStoredValue` refuse a set loudly (a guard that throws) instead of returning null, so
   the parameter lane cannot hand the filter type to the write path by mistake.
3. Say what NULL means in the column: an unclassified row (None), or a row written by a path that
   did not declare its kind (F1's sync pull).

**Proves it:** a test that `ToStoredValue(Memory | File)` refuses rather than returning null, and
a test that the stored kind has no `Code` member. Break the guard, watch it go red, restore it,
watch it go green.

## F6: a second "entry type", and one unreachable value

The tool layer already returns a type string on `GetEntryResult` (`EntryTools.cs:60-67`), with
`"memory"` and `"code"` hardcoded at `:48` and `:52`. The new classifier defines `MemoryName` and
`CodeName` for the same two wire names. `EntryType.Code` can never appear on an `entries` row:
code chunks live in `code_entries`, and the research the branch cites says so (F5, "Code is already
a separate corpus, so only the Memory and File split needs a new lever").

**Why it matters:** two types named `EntryType` with different meanings, different representations
(enum and string) and different value sets (three and two), with the wire names written twice. A
caller of the future `get_entry` parameter has to know which "code" is meant.

**Fix:** one source for the wire names, used by `EntryTools` as well. Either drop `Code` from the
entries taxonomy or state exactly where a row of type `code` comes from. F5 carries the second half
of this: the `Code` member is right for the query parameter and wrong for the stored column.

## F7: the merge-order gate

The branch sets `CurrentVersion = 20` (`MemorySchema.cs:71`) and the ladder step runs on the first
read-write open, but nothing in the branch reads `entry_type`: the consumer is the query parameter,
a later step. The branch's own plan text records the consequence
(`docs/plans/2026-10-21-mcp-tool-surface-rename.md:205-215`): "a v20 bank refuses to open: `bank
schema v20 is newer than this binary supports (v19)` ... 2.1.0 therefore ships the schema v20, the
entry-type parameter and the TTL rule together."

**Fix:** merge only when the parameter lands. If the branch has to land first, record the gate in
the commit and in the plan, so the order survives the session that wrote it.

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

1. F1, with its test. It is the only fix whose absence loses data.
2. F2, with the comparison test, so the two copies cannot part company again.
3. F3, F4, F5, F6, each a small local edit.
4. F7 at merge time, not before.

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
