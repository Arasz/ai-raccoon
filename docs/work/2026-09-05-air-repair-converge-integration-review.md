# Integration review — air-run-once-repair-fully-converges (packages A–G)

Reviewer: session 8575f1fb (whole-stack joints). Date: 2026-09-05.
Scope: the cross-package joints of #614/#615/#616/#617/#618/#619/#620, all merged to main
(base `adede5b5`, head `origin/main` at review time). Plan:
`docs/work/air-run-once-repair-fully-converges-plan.md`.

Partial-PR review stayed with session 01a070f9; this pass looked only at what no single PR
could see — where two packages meet.

## Verdict

Five findings, four of them defects that a per-package review could not have caught because each
lives in the seam between two packages. All four are fixed here, each with a test that was
observed failing against the pre-fix code first. The fifth is a process gap I am reporting rather
than fixing, because fixing it honestly requires running mutations I did not run.

The stack's own D6 machinery is sound: the planner/applier predicate unity (D2's central claim)
holds on every surface I checked, the three Default reload legs exist and fire, and the
convergence E2E proves what it says it proves.

## Findings

### 1. The push snapshot fold aborts the whole sync on an ordinary bank shape — HIGH

`SyncService.FoldSnapshotProjectIdsAsync` (origin/main `SyncService.cs:703`) rewrote
`project_id` in place:

```sql
UPDATE entries SET project_id = CASE ... END WHERE project_id IN (losers)
```

with no dedup guard, against `uq_entries_committed_bucket`
(`(path, hash, project_id, scope, COALESCE(context_label,''))`, UNIQUE) and against
`sync_tombstones`'s `PRIMARY KEY (project_id, hash, scope)`. A bank holding **both** spellings of
one row — the ordinary shape once this replica has pulled a repaired peer's winner-keyed rows —
makes that rename collide, and `SQLITE_CONSTRAINT` propagates out of `MemorySyncAsync`: the push
fails outright, and it fails on exactly the replica E2 push symmetry exists to serve.

The repair's own applier already knows this: `FoldEntriesAsync` moves only rows with no winner
twin and then deletes the rest (no tombstone on a dedup collapse), and `FoldTombstonesAsync`
merges `deleted_at` before deleting. The snapshot fold skipped that discipline.

Observed failing before the fix:

```
MemorySync_PushFoldsLoserRows_WhenTheWinnerAlreadyHoldsTheSameBucket
  SqliteException : SQLite Error 19: 'UNIQUE constraint failed: index 'uq_entries_committed_bucket''
MemorySync_PushFoldsLoserTombstones_WhenTheWinnerAlreadyHoldsTheSameKey
  SqliteException : SQLite Error 19: 'UNIQUE constraint failed: sync_tombstones.project_id, ...'
```

The existing E2 push tests could not have caught it: their fixture bank
(`SyncServiceAliasMapTests.BankDdl`) declares `entries` without either bucket index, so the
constraint the fold has to respect was absent from the only place it was exercised.

**Fixed** by mirroring the applier per alias — dedup-then-move for entries, merge-dedup-then-move
for tombstones — and by adding the real unique indexes to the fixture, which the six pre-existing
tests in that class still pass with.

### 2. The push fold folded `shared` rows; the pull fold did not fold `custom` rows — HIGH

Three predicates that the plan says are one:

| Site | Domain before | Should be |
|---|---|---|
| repair applier (D1) | `scope IN ('project','custom')` | — |
| sync **pull** (`SyncService.cs:439`) | `scope = 'project'` | project + custom |
| sync **push** (`SyncService.cs:703`) | *no scope predicate at all* | project + custom |

The push arm therefore re-attributed **shared-scope** rows off-machine — the one thing D1
explicitly refuses ("shared rows are cross-project by design and are NOT folded"; the planner
pins `pinned-shared-only` rather than touch them).

The pull arm has the opposite drift, and it is the one that breaks D6. D1 broadened the repair to
fold custom rows; the pull still folded project scope only, so every sync from an unrepaired peer
re-seeds loser-keyed custom rows under the retired id. A repaired bank can never hold the D6 (iii)
stability verdict while a peer pushes. Its comment still read *"the pull fold matches the repair's
domain (project-scope rows only) — custom/shared rows merge verbatim, exactly as the repair leaves
them"*: true before D1, false after it.

`ProjectIdsPullFoldTests.PullLeavesCustomAndSharedLoserRows_Verbatim` asserted the superseded
contract in its own message — *"custom-scope rows merge verbatim — the repair never folds them"*.
This is the same contract collision #619 already had to resolve once for `SingleProjectIdE2E`; the
sync copy of it was missed.

**Fixed**: both arms now go through `ProjectRows.Scope` — one predicate, three call sites. The
pull test is renamed to `PullFoldsCustomLoserRows_AndLeavesSharedVerbatim` and asserts the
converged truth on both halves. `ProjectRows.ScopeIsProject` and `SyncService.FoldLocalProjectId`
lost their last callers and are deleted rather than left as stale single-home helpers.

### 3. "P3 armed" was a constant, not a measurement — MEDIUM

`ProjectIdsRepairCommands.P3ArmedNote = "P3 armed"` (origin/main line 39) printed
unconditionally on every converged and pinned-only verdict. A dry run on a bank that never ran a
repair — empty durable map, no id folded through, none refused — still closed with
`converged: 0 fold, 0 drop, 0 retire, 0 unresolved, 0 pinned, P3 armed.`

D6 (iv) makes "P3 armed" a claim about the bank. Asserting it is the same invisible-bucket
dishonesty D2 removed from the planner, and it is the shape `prove-the-check-fails` warns about:
a value that can only come out one way. The tests around it were self-referential
(`expected = ProjectIdsRepairCommands.P3ArmedNote`), so they would have passed whatever the
constant said.

**Fixed**: the census carries the durable map it scanned (`ProjectIdCensusReport.DurableAliases` /
`DurableDropped`, read SELECT-only and skipped on a pre-v14 bank), and the verdict reads it:

- `P3 armed (2 alias, 1 dropped)` — counts a reader can check against `project_id_aliases`
- `P3 inert (durable alias map empty — no id folds through, none is refused)`

The G convergence E2E confirmed the wiring end to end: on the really-applied bank it now reports
`P3 armed (2 alias, 1 dropped)`, matching the fixture map's rows. The five self-referential
assertions became literal expected strings.

### 4. Two test classes mutate the process-wide alias cache outside the serializing collection — MEDIUM

`ProjectIdAliasDefaultCollection` states its membership rule in a doc comment. Prose is not a
gate, and the rule was already broken twice in this stack: first by classes that drove the repair
job without resetting `Default` (the CI red fixed in `e8052665`, which only Linux ordering
exposed), and still, at review time, by:

- `SingleProjectIdCensusTests` — runs the real `ProjectIdsRepairJob`, so E1's job leg replaces
  `Default`; not in the collection, no reset.
- `SingleProjectIdSteps` (BDD) — same, and a Reqnroll binding cannot join an xUnit collection.
  CI runs BDD as a separate job so it is isolated there, but an unfiltered local lane runs it
  beside everything else.

A third, larger source turned out to be a product bug rather than a test one. Since E2, the sync
pull called `ProjectIdAliases.LoadAndCacheAsync` on **every** pull, including pulls that merged no
map rows — so every `SyncService*Tests` class silently replaced the process-wide cache with an
empty map. E1's contract is "reload on map **change**"; the code reloaded on every pull.

**Fixed**: the pull reloads only when `INSERT OR IGNORE` actually inserted rows (red-proved by
relaxing the guard to `>= 0` and watching the new test fail); `SingleProjectIdCensusTests` joins
the collection and resets; the BDD binding resets in `[AfterScenario]`. A new gate,
`ProjectIdAliasDefaultCollectionGateTests`, derives the rule instead of listing it — any test file
holding a `Default`-replacing call site must serialize and reset. Red-proved: removing the
`[Collection]` from `SingleProjectIdCensusTests` reddens it with that file named.

### 5. Red-proof ledgers are missing from roughly 25 new tests — reported, not fixed

The repo's convention is an in-file `Ledger — <name> : <filter> : <mutation>` line recording the
mutation that was observed turning each assertion red. Parts of this stack follow it closely
(`ProjectIdsFoldPlanTests` 29/30, `ProjectIdsRepairFoldCommittedTests` 7/7,
`ProjectIdsPullFoldTests` 6/6). These new files carry none:

| File | Tests | Ledgers |
|---|---|---|
| `ProjectIdsRepairLoopTests` | 8 | 0 |
| `ProjectIdAliasesDurableTests` | 5 | 0 |
| `ProjectIdAliasMapDefaultTests` | 4 | 0 |
| `ProjectIdsConvergenceTests` | 3 | 0 |
| `WatchToolsRetiredIdTests` | 3 | 0 |
| `ProjectIdAliasCacheReloadTests` | 2 | 0 |
| `ToolGateRetiredIdTests` | 4 | 1 |

G's PR body records its ledger in the lane report rather than the tree, and the others may have
been proved without being written down. I did not write ledger lines for tests whose mutations I
did not run — an invented ledger is worse than an absent one, because it reads as evidence. This
needs either the lane authors' records moved into the files, or a follow-up that actually runs
the mutations.

## Joints checked and found sound

- **Planner/applier predicate unity (D2's core claim).** Every surface `OwnsMoveableContent`
  counts is one the applier actually moves for a fold, including the non-obvious ones: a
  tombstone-only id still reports moves (the loser-row delete counts even when every rewrite
  collides), and so do watches-only and entries-only ids whose rows all dedup-collapse. No shape
  I could construct plans a fold that executes as zero moves.
- **The retire/pin/telemetry boundary.** A registered id owning only telemetry retires, then
  reappears once as unregistered and pins telemetry-only — one extra pass, still convergent, and
  never a silent skip.
- **All three Default reload legs** exist and are reachable: job-side after
  `PersistAppliedAsync`, `ProjectIdAliasCacheHostedService` at startup (fail-open, warning on
  failure), sync pull after a merge (now correctly gated on an actual change).
- **The v14 migration** declares `project_id_aliases` once in `ProjectIdAliases.TableDdl` and
  executes it from both the fresh-bank DDL block and the ladder step — one definition, two call
  sites.
- **`ToolGate` refuse-dropped / fold-through-alias** sits behind the migration gate, so an
  unmigrated bank behaves exactly as before, and reads are never refused.

## Notes, no action taken

- `ProjectIdsRepairJob` persists the applied map even when `plan.IsEmpty`, and outside a
  transaction. Both are harmless — `INSERT OR IGNORE` is idempotent and an empty plan means there
  were no loser rows to move — but `PersistAppliedAsync`'s summary says "written only by the
  repair job on successful apply", which reads narrower than what happens.
- The id-embedding settings-key prefixes are listed twice: `ProjectIdCensus.TryAttributeSetting`
  (which decides whether a settings key makes an id look foldable) and
  `ProjectIdsRepair.SettingsKeysFor` (which renames them). They agree today. They are the settings
  leg of the same planner/applier unity D2 exists to protect, and nothing compares them.
- `ProjectIdAliases.LoadAndCacheAsync` runs its `SELECT` twice — once to count null-winner rows
  for the warning, once inside `LoadAsync`.

## Gate

Full unfiltered Fast lane (`Speed=Fast&Performance!=Benchmark`) plus the BDD lane
(`Category=bdd`), run against the merged stack with these fixes applied. Results recorded on the
PR.
