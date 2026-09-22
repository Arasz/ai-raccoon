# Join review — P2.1/P2.2 data-correctness fix (`task/psr-w2-p2`, `01003aa2` on `5bca1900`)

**Verdict: the lane's claims are CONFIRMED.** The tombstone derivation mirrors each delete predicate through
a shared constant (no copied SQL), the mandatory `scope IN ('project','custom','shared')` filter and the
refreshing upsert are both present and load-bearing (each was watched fail when removed), the five gate rounds
pass on HEAD and are all red on base, and every attack I could construct against the fix either confirmed the
claim or produced a residual the plan already accepted. One **test-coverage correction**: the P2.2 gate the plan
says "adds the second replica holding the tombstone as its own case" was not implemented; the tests are silent
on multi-replica convergence (they neither pin it nor overclaim it).

Read-only posture: the worktree was never modified (`git status --porcelain` empty before and after; no commit).
All mutation happened in throwaway `docs/work/2026-09-22-project-scope-review/verify/join-*` clones; every test bank was a scratch temp
root (`TestData.CreateTempRoot` → `$TMPDIR/…`, `tests/AiRaccoon.Tests/TestData.cs:230-236`). `~/.ai-raccoon`
was never read or written and no `mcp_ai-raccoon_*` tool was called.

---

## 1. Derivation vs the actual delete predicates — CONFIRMED

The tombstone is not a copy of the delete SQL; it composes the *same* predicate constant/string the delete uses.

| delete path | delete predicate | tombstone predicate |
|---|---|---|
| `DeleteCoreAsync` | `"DELETE FROM entries WHERE " + DeleteByHashAndProjectPredicate` (`MemorySql.cs:167-170`) | `TombstoneFromPredicate(DeleteByHashAndProjectPredicate)` (`SqliteMemoryStore.cs:751-753`) — same constant |
| `DeleteSourcePathAsync` | `"DELETE FROM entries WHERE " + DeleteBySourcePathPredicate` (`MemorySql.cs:201-206`) | `TombstoneFromPredicate(DeleteBySourcePathPredicate)` (`SqliteMemoryStore.cs:350-352`) — same constant, subtree `LIKE @pathPrefix ESCAPE '\'` clause included |
| `DeleteContextAsync` | `$"DELETE FROM entries WHERE {filter}"` (`SqliteMemoryStore.cs:324`) | `TombstoneFromPredicate(filter)` (`SqliteMemoryStore.cs:320`) — same `filter` string |

- **Operator precedence is safe.** `ContextFilterProvider.For` returns AND-chains only (`ContextFilterProvider.cs:19-50`):
  shared `scope = 'shared'`; project `scope = 'project' AND project_id = @projectId` (`ProjectRows.ProjectScope`,
  `ProjectRows.cs:29-30`); label/default `scope = 'custom' AND context_label = @contextLabel AND project_id = @projectId`;
  workspace `workspace_id = @workspaceId AND project_id = @projectId`. Appending `AND scope IN (…)` cannot re-bind
  through an OR in any of them.
- **Upsert refreshes `deleted_at`** — `ON CONFLICT(project_id, hash, scope) DO UPDATE SET deleted_at = excluded.deleted_at`
  (`MemorySql.cs:181`), and `(project_id, hash, scope)` is exactly the `sync_tombstones` PK (`MemorySchema.cs:287-292`).
  Variant 3 below (OR IGNORE) goes red, proving the refresh is load-bearing.
- **Scope filter cannot admit a workspace row** — the entries CHECK makes a workspace row's scope NULL
  (`MemorySchema.cs:154`: `(workspace_id IS NULL AND scope IN ('shared','project','custom')) OR (workspace_id IS NOT NULL AND scope IS NULL)`),
  and the filter is `scope IN ('project','custom','shared')` (`MemorySql.cs:180`), so NULL can never pass.
- **Scope filter omits no syncable tier** — the three committed scopes are exactly what `StripNonSyncableAsync`
  leaves in the pushed snapshot (it deletes only `workspace_id IS NOT NULL`, `SyncService.cs:629`). The one
  syncable-by-accident shape is the F34 "neither scope nor workspace" row (scope NULL, workspace_id NULL), which
  is outside every tier and not tombstoned — see Residual risk.
- **Transaction safety** — tombstone-then-delete runs inside `InTransactionAsync` (BEGIN IMMEDIATE / COMMIT /
  ROLLBACK, `SqliteMemoryStore.cs:1017-1037`) in every path: `DeleteContextAsync` (`:318-327`), `DeleteSourcePathAsync`
  (`:349-366`, the hand-rolled BEGIN/COMMIT replaced by the helper), `DeleteCoreAsync` (`:751-775`). The tombstone
  SELECT therefore reads the pre-delete rows on the same connection. `DeleteContextAsync` had **no** transaction
  before this change; it now does.
- The removed `UpsertTombstone`/`SelectScopeByHashAndProject` constants have no dangling references (`grep` clean;
  the project builds).

## 2. Gates on HEAD and against base — CONFIRMED

### 2.1 HEAD (filters only, no `--nologo`)

```
$ dotnet test tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj --filter "FullyQualifiedName~DeleteTombstoneSyncTests"
Running tests from .../AiRaccoon.Tests.dll (net10.0|arm64)
... passed (1s 891ms)

Test run summary: Passed!
  total: 6
  failed: 0
  succeeded: 6
  skipped: 0
```

Per-test evidence (`./tests/AiRaccoon.Tests/bin/Debug/net10.0/AiRaccoon.Tests --filter-class
AiRaccoon.Tests.Integration.Sync.DeleteTombstoneSyncTests --output Detailed`):

```
passed ...DeleteTombstoneSyncTests.Delete_OfAWorkspaceTwin_DoesNotPushAWorkspaceTombstone_OrDeleteAPeersWorkspaceRow (451ms)
passed ...DeleteTombstoneSyncTests.WatcherDelete_ThenSync_KeepsTheFilesChunksDeleted (95ms)
passed ...DeleteTombstoneSyncTests.DeleteContext_ThenSync_KeepsTheContextDeleted (76ms)
passed ...DeleteTombstoneSyncTests.Delete_WhenTheWorkspaceRowSortsFirst_StillRecordsTheCommittedScope (64ms)
passed ...DeleteTombstoneSyncTests.Delete_WorkspaceAndCommittedTwins_RecordsEveryCommittedScopeAndNoWorkspaceScope (62ms)
passed ...DeleteTombstoneSyncTests.ReCreatedContent_ThenSync_SurvivesItsOwnTombstone (148ms)
total: 6 failed: 0 succeeded: 6
```

The two modified existing tests also pass: `MemorySync_TombstonePropagation_NoResurrection` +
`MemorySync_TombstoneFromRemote_DeletesAGroupMember…` → `total: 2 failed: 0 succeeded: 2`. The
`SyncServiceTests.cs` fixture change (`deleted_at` 1 → 100, `SyncServiceTests.cs:326`) is the correct
consequence of the age guard: the fixture's row has `created_at = 2`, so the tombstone must be ≥ it.

Modified-surface regression runs on HEAD: `Integration/Sync` namespace → **79/79 passed**;
`Integration/Storage` namespace → **370/370 passed**.

### 2.2 Base discrimination (`5bca1900` + only the new tests restored)

Throwaway clone `docs/work/2026-09-22-project-scope-review/verify/join-base` (`git clone` + `git checkout 5bca1900`); the new test file
copied in and verified byte-identical (`diff` → IDENTICAL); `git status` shows only that untracked file.

```
$ ./tests/AiRaccoon.Tests/bin/Debug/net10.0/AiRaccoon.Tests \
    --filter-class AiRaccoon.Tests.Integration.Sync.DeleteTombstoneSyncTests --output Detailed
failed ...WatcherDelete_ThenSync_KeepsTheFilesChunksDeleted
failed ...Delete_WhenTheWorkspaceRowSortsFirst_StillRecordsTheCommittedScope
failed ...DeleteContext_ThenSync_KeepsTheContextDeleted
failed ...Delete_OfAWorkspaceTwin_DoesNotPushAWorkspaceTombstone_OrDeleteAPeersWorkspaceRow
failed ...ReCreatedContent_ThenSync_SurvivesItsOwnTombstone
failed ...Delete_WorkspaceAndCommittedTwins_RecordsEveryCommittedScopeAndNoWorkspaceScope

Test run summary: Failed!
  total: 6
  failed: 6
  succeeded: 0
```

The pair fails on base **for the intended mechanism**, not incidentally:

- ordering-forced variant: `TombstoneScopesAsync` should be `["project"]` but was `[]` — the workspace row
  won the single probe, exactly the old bug.
- set assertion: should be `["custom","project"]` but was `[]` — a single probe cannot produce two tombstones
  whatever it probes.
- re-create: `CountEntriesAsync` should be 1 but was 0 — the re-added fact was silently deleted.
- leak test (base shape): `CountEntriesAsync(peerFactory, …)` should be 0 but was 1 — no tombstone was pushed
  at all, so the peer's committed twin survived.

## 3. Attacks

### 3.1 The leak gate — CONFIRMED (and the naive design is red)

The lane's `Delete_OfAWorkspaceTwin_DoesNotPushAWorkspaceTombstone_OrDeleteAPeersWorkspaceRow`
(`DeleteTombstoneSyncTests.cs:142-174`) is a real gate, not a tautology. It asserts three things: the peer's
independently written same-hash workspace row survives, no `'workspace'` tombstone reaches the cloud snapshot,
and the peer's committed twin does die.

**Falsification run** (scratch clone, scope filter deleted from `MemorySql.cs`):

```
$ sed -i '' "s/WHERE {predicate} AND scope IN ('project', 'custom', 'shared') /WHERE {predicate} /" \
    src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs
$ dotnet build tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj   # 0 Error(s)
$ ./tests/AiRaccoon.Tests/bin/Debug/net10.0/AiRaccoon.Tests \
    --filter-method ...Delete_OfAWorkspaceTwin_DoesNotPushAWorkspaceTombstone_OrDeleteAPeersWorkspaceRow
failed ... (485ms)
  Shouldly.ShouldAssertException : await CountWorkspaceRowsAsync(peerFactory, local.Hash, ct)
  Additional Info: (the peer's scratch row is deleted)
total: 1 failed: 1 succeeded: 0
```

Without the filter the peer's workspace row is deleted — the leak the P2.1 correction exists to prevent. With
the filter it survives. The filter is load-bearing.

### 3.2 Order independence — CONFIRMED

The primary twin test asserts the **set** `["custom","project"]` (`DeleteTombstoneSyncTests.cs:96-99`), which the
single-probe design cannot satisfy whatever row it reads first (it writes at most one tombstone). The
ordering-forced variant (`:115-139`) inserts the workspace row first and asserts `["project"]`; on base that
exact test produced `[]`, proving the workspace row does sort first on the old probe path. On HEAD both pass
with no ordering clause anywhere in the derivation — the set comes from `SELECT DISTINCT … WHERE <predicate>`,
so it is order-independent by construction.

### 3.3 Mirroring — CONFIRMED, with one effective-reach divergence

Character-for-character on the shared predicate (see §1; `DeleteSourcePathPredicate` carries the subtree/LIKE
clause and is the same constant both sides). The only added clause is the scope filter, which narrows — it can
never widen what the delete reaches.

**The divergence that does exist is on the apply side, not the derivation side:** `sync_tombstones` has no
`context_label` column, and the apply matches only `(hash, scope, project)` (`SyncService.cs:524-533`). A
context-scoped delete is therefore label-scoped *locally* but hash+scope-wide *on peers*. Scratch proof
(two replicas; A writes label `ctxA`, B writes the same content under label `other`, A deletes `ctxA`):

```
failed ...ScratchJoinTests.ContextDelete_OfOneLabel_AlsoDeletesAPeersSameHashOtherLabel (294ms)
  Shouldly.ShouldAssertException : await CountLabelRowsAsync(peerFactory, b.Hash, "other", ct)
      should be 1 but was 0
```

The plan's corrected P2.1 prescribes exactly this SQL shape (scope-only tombstone), so this is a plan-accepted
model limitation, not an implementation deviation — but it is a data-loss reach the lane's tests do not name.

### 3.4 K3 durability (`deleted_at` refresh, and the `<=` boundary) — CONFIRMED

Two scratch gates, both watched red under the corresponding mutation:

- **Later delete still propagates** (`ScratchJoinTests.ReCreated_ThenDeletedAgain_RefreshPropagatesToPeersReCreate`):
  A deletes (tombstone T1), A re-creates, B independently re-creates later, A deletes again → the tombstone's
  `deleted_at` must move past B's re-created row or B's row survives A's second delete. Passes on HEAD.
  **Variant 3** — the upsert replaced by `INSERT OR IGNORE`:
  ```
  failed ...ReCreated_ThenDeletedAgain_RefreshPropagatesToPeersReCreate (365ms)
    Shouldly.ShouldAssertException : refreshedDeletedAt
        should be greater than ... but was ...
  ```
- **`<=` is load-bearing** (`SameSecondDelete_StillPropagatesToPeer`): a delete in the same wall-clock second
  as the row's creation (`created_at == deleted_at`) still deletes on the peer. **Variant 2** — `<=` → `<`:
  ```
  failed ...SameSecondDelete_StillPropagatesToPeer (288ms)
    Shouldly.ShouldAssertException : await CountEntriesAsync(peerFactory, entry.Hash, ct)
  ```
- **The cost of `<=`** (documented, expected, not a bug against K3): a re-create in the same second as its
  delete is deleted again (`ScratchJoinTests.ReCreateInTheSameSecondAsTheDelete_IsDeletedAgain` → passed,
  asserting the row is gone). The lane's own re-create test advances the fake clock 10 s
  (`DeleteTombstoneSyncTests.cs:214`), so it does not pin this boundary.

### 3.5 Convergence — CONFIRMED partial; the tests omit the plan's second-replica case (CORRECTED, coverage)

**The partial behaviour is real.** The merge's entries leg checks the *local* `sync_tombstones` with no age
comparison (`SyncService.cs:450-455`), so a replica already holding the tombstone never re-inserts the re-created
row; the apply step (remote tombstones, with the age guard) can't help because the row never arrives. Scratch
proof (`ReCreate_OnReplicaHoldingTheTombstone_IsSuppressedUntilLaterGc`): after A re-creates and both sync,
B still has 0 rows; only after B's tombstone is GC'd (`SyncService.cs:536-545`) and A re-pushes does B converge
to 1. This matches the plan's P2.2 text.

**Coverage correction:** P2.2 states "the gate adds the second replica holding the tombstone as its own case".
No such case exists — `ReCreatedContent_ThenSync_SurvivesItsOwnTombstone` (`DeleteTombstoneSyncTests.cs:208-224`)
is single-bank, and the only peer in the file serves the workspace-leak gate. The tests neither pin the partial
behaviour nor overclaim it; they are silent. This is the one place the shipped tests do not match the corrected plan.

### 3.6 Transaction safety — CONFIRMED

See §1. All three paths run `TombstoneFromPredicate` **before** the DELETE inside `InTransactionAsync`
(`SqliteMemoryStore.cs:1017-1037`), so the SELECT reads the rows the DELETE is about to remove. The
`DeleteCoreAsync` rewrite preserves the recompute-context read and the `deleted > 0` return; the
`DeleteSourcePathAsync` rewrite preserves the code-corpus and watch-file legs inside the same transaction.
No caller opens an outer transaction around these paths.

## 4. Tests vs the plan's corrected gates — CONFIRMED except P2.2

| plan gate (P2.1/P2.2 corrected) | test | status |
|---|---|---|
| (b) assert the tombstone **set**, not the outcome, order-independent | `Delete_WorkspaceAndCommittedTwins…` asserts `["custom","project"]` (`:96-99`) | CONFIRMED |
| (b) ordering-forced variant | `Delete_WhenTheWorkspaceRowSortsFirst…` (`:115-139`) | CONFIRMED |
| new leak gate: peer workspace row survives + no `'workspace'` tombstone pushed | `Delete_OfAWorkspaceTwin…` (`:142-174`) | CONFIRMED |
| (d) digest/delete path, not a real `FileSystemWatcher` event | `WatcherDelete_ThenSync…` drives `WatchDigestExecutor.DigestAsync(…, WatchEventKind.Deleted, …)` (`:176-206`) | CONFIRMED |
| P2.2 second replica holding the tombstone as its own case | — | **MISSING (CORRECTED)** |

---

## Residual risk

1. **Context-delete label collateral (measured, MEDIUM).** A `memory_delete_context` of label `ctxA` tombstones
   `(project, hash, custom)` and deletes a peer's same-hash row under a *different* label on its next pull
   (§3.3). The plan's prescribed SQL accepts this; the tombstone schema has no label to be more precise.
2. **Same-second re-create is deleted again (measured, LOW, per K3's explicit `≤`).** Timestamps are whole
   seconds (`ToUnixTimeSeconds`), so a re-create within the delete's second carries `created_at == deleted_at`
   and dies. The alternative (`<`) breaks same-second delete propagation (§3.4) — the trade is deliberate, but
   neither the plan nor the tests pin which side wins at the boundary.
3. **Multi-replica convergence is partial (measured, stated in the plan).** A replica holding the tombstone
   suppresses the re-created row until tombstone GC in a later pull; in the scratch run convergence also needed
   A to re-push after B's GC-push had overwritten the cloud snapshot. No test pins this.
4. **Sibling tombstone-less deletes of the same class (code-read, out of this package's scope).**
   `SqliteMemoryStore.Replace.cs:153-154` (`PruneAsync` of a replace: `DeleteAllChunksForPath` /
   `DeleteChunksForPathExcept`) and `ChunkBackfill.cs:69` delete syncable `entries` rows without tombstones.
   They were not among F29/F30/F35, but a stale chunk removed by a watcher *change* can still return on the
   next pull by the same merge predicate F29 measured.
5. **F34 scope-NULL rows.** `DeleteSourcePathAsync`/`DeleteCoreAsync` delete rows with `scope IS NULL,
   workspace_id IS NULL` but the scope filter never tombstones them, so they can resurrect. They belong to no
   tier and are admitted only by the NULL-passing CHECK (`MemorySchema.cs:154`).
6. **Clock skew (F36 class).** The age guard compares the deleting replica's `deleted_at` with the re-creating
   replica's `created_at`; a replica clock behind by more than the re-create gap can still suppress a legitimately
   newer row (and a clock ahead can fail to suppress an older one).
7. **`[RetryFact]` on all six gates.** The red runs showed retries do not mask the base/variant failures, and
   HEAD passed first try in every run (60-450 ms), but a retrying gate can in principle go green on a flake.
8. **Cosmetic:** `COALESCE(scope,'workspace')` inside `TombstoneFromPredicate` is dead under the scope filter
   (scope can never be NULL there); harmless, but it invites the reading that workspace rows can pass.

## Still open

1. **P2.2's missing gate:** should the lane add the second-replica-holding-the-tombstone case (my scratch test
   is a ready template), or does the plan accept partial convergence with no test?
2. **Label-granularity ruling:** is hash+scope-wide propagation of a context delete acceptable, or does
   `sync_tombstones` need a `context_label` (or label-aware apply) so a context delete stays label-scoped
   end-to-end? The owner ruling K3 addressed re-creation, not label reach.
3. **Sibling delete paths:** do `PruneAsync`/`ChunkBackfill` need the same tombstone derivation (F29-class),
   or are they out of scope by a ruling I have not read?
4. **The `<=` boundary:** does the owner want same-second re-create to survive (needs a higher-resolution
   `created_at`, or an explicit re-create tombstone-clearing rule), or is "delete wins inside the second" the
   intended semantic?
5. **F36 clock-skew interaction** with the age guard — untested, as in the G1 record.
