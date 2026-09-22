# Lane D — Data access, schema & persistence

Worktree: `/Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/psr-d-data-access` @ `5bca1900`
Scratch root: `docs/work/2026-09-22-project-scope-review/d-data-access/`; the product was only ever run as `ai-raccoon --data-root docs/work/2026-09-22-project-scope-review/d-data-access/<bank> …` (scratch banks `data`, `data-d3`, `data-d4`).
Harness: `docs/work/2026-09-22-project-scope-review/d-data-access/harness/` — a console app that project-references the worktree's `AiRaccoon.Infrastructure` and drives the **real** `SqliteConnectionFactory` + `SyncService` (a file-backed `ICloudStore` so separate processes share one "cloud"), plus a vec0-loaded sqlite probe. No tracked file was modified.

---

### F1 — `memory_delete_context` deletes committed content with no tombstone, so the next sync resurrects it [MEASURED]
**Severity:** HIGH
**Evidence:** live, on a scratch bank (`data-d4`) with my own server and the real MCP tools:
`memory_write {projectId: lane-d, content: "context-delete resurrection probe", context: ctxA}` → hash `b06dcfbd…`; `dotnet run -- sync <bank> lane-d <cloud>` → `sent=1 received=0` (row on the remote); `memory_delete_context {projectId: lane-d, context: ctxA}` → `{"deleted": 1}`; `sqlite3` → `SELECT count(*) FROM entries` = `0`, `SELECT count(*) FROM sync_tombstones` = `0`; `sync` again → `received=1`, and the row is back with the same hash/scope/label.
`SqliteMemoryStore.DeleteContextAsync` (src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.cs:302-321) is a bare `DELETE FROM entries WHERE {filter}` — it writes no `sync_tombstones` row, unlike its sibling `DeleteCoreAsync` (:744-785, which does). The merge's entries leg (SyncService.cs:440-455) re-inserts any remote row without a matching tombstone, so the documented "delete every entry under this context" (the tool's own description, and ADR-0052 says `Destructive` means "reaching committed memory" and names `memory_delete_context` explicitly) does not stick as soon as sync is configured.
Smallest fix: in the same transaction, `INSERT OR IGNORE INTO sync_tombstones SELECT project_id, hash, scope, @now FROM entries WHERE {filter}` before the delete, exactly as `ProjectIdsRepair.FoldEntriesAsync` does for its dropped ids (ProjectIdsRepair.cs:202-213: `INSERT OR IGNORE INTO sync_tombstones … SELECT … FROM entries WHERE project_id = @dropped …` then the DELETE).

---

### F2 — `memory_delete` writes at most one tombstone for N deleted rows, and none when the first row is workspace-scoped [MEASURED]
**Severity:** HIGH
**Evidence:** live end-to-end on `data-d3` (my server, real tools): workspace write of content C → row id 5 (`scope NULL, workspace_id 01a0c93c…`); committed write of the same C → row id 6 (`scope project`), **same hash** `09b78d26…` (the write path's hash is `ContentHash.Of(path, value)` and both writes derive the same value-addressed path — `memory_write` accepts a workspace write and later a committed write of the same content, they are separate buckets); `memory_sync` → push (the workspace row is stripped from the snapshot, the committed twin reaches the remote); `memory_delete {projectId: lane-d, hash: 09b78d26…}` → `{"deleted": 1}`, `SELECT count(*) FROM entries WHERE hash='09b78d26…'` = `0` (both rows gone), `SELECT * FROM sync_tombstones` = **empty**; `memory_sync` again → `received=1` and `hash=09b78d26c3 scope=project` is back.
`DeleteCoreAsync` reads `rowScope` with `QueryFirstOrDefault` (`SELECT scope FROM entries … LIMIT 1`, :753) and then `DELETE FROM entries WHERE hash=@hash AND project_id=@projectId AND (@scope IS NULL OR scope IS @scope)` (:761) can delete many rows; only one tombstone is written, and only `if (deleted > 0 && rowScope is not null)` (:765-772). A workspace row's scope is NULL, so selecting it first writes zero tombstones for a committed row that was just deleted. With two *committed* rows deleted, the single tombstone carries one of the scopes, so the other scope's deletion is unrecorded (the merge's suppression compares `t.scope = COALESCE(r.scope,'workspace')`).
Smallest fix: derive the tombstone set from the rows actually deleted (`INSERT … SELECT DISTINCT project_id, hash, COALESCE(scope,'workspace'), @now FROM entries WHERE <same predicate>` before the DELETE), not from one arbitrarily-ordered probe.

---

### F3 — Re-creating deleted content with the same hash is silently deleted again by the next sync (D27, still live) [MEASURED]
**Severity:** HIGH
**Evidence:** full statement-level and end-to-end repro in `docs/work/2026-09-22-project-scope-review/d-data-access/harness` (real `SyncService`, `FakeCloudStore`): push a fact; delete it locally and write its tombstone (the shape `DeleteCoreAsync` produces); `memory_sync` (push the tombstone); re-insert the identical content (identical hash); `memory_sync` again → `local rows BEFORE sync 3: 1`, `local rows AFTER sync 3: 0`, `RESULT: the re-added fact was silently deleted by the sync merge`.
`SyncService.cs:518-523` — `DELETE FROM entries WHERE (hash, COALESCE(scope,'workspace'), project_id) IN (SELECT hash, scope, <folded project_id> FROM remote.sync_tombstones)` has no `created_at`/`deleted_at` comparison. This is `docs/work/2026-08-07-moe-d-tests.md` D27 (Medium) and item 1c of `docs/work/2026-08-07-moe-integrated-plan.md`; nothing changed it in the 441-commit delta (git log on that range shows only the project-fold edits), and `SyncServiceTests.MemorySync_TombstonePropagation_NoResurrection` (tests/…/Integration/Sync/SyncServiceTests.cs:235) never re-creates the tombstoned hash. Smallest fix: `AND entries.created_at <= (SELECT t.deleted_at …)`, or delete the tombstone when a write re-creates that (project, hash, scope).

---

### F4 — `doctor` reports HEALTHY for a bank whose bucket index lost its uniqueness, and nothing ever repairs it [MEASURED]
**Severity:** MEDIUM
**Evidence:** on a copy of a healthy scratch bank: `sqlite3 exp2/memory.db "DROP INDEX uq_entries_committed_bucket; CREATE INDEX uq_entries_committed_bucket ON entries(path,hash,project_id,scope,COALESCE(context_label,''));"` → `ai-raccoon --data-root …/exp2 doctor` → `status: HEALTHY`; two identical bucket inserts then both succeed (`SELECT count(*) … WHERE hash='dup'` → `2`), i.e. the uniqueness FR-NM-7 relies on is gone.
`SchemaDoctor` diffs object *names* and table *columns* (`SchemaDoctor.cs:70-125`) but never an index's definition/uniqueness/partial flag, and `EnsureAsync`/`MigrateToV1Async` only probe existence (`SELECT 1 FROM sqlite_master WHERE … name='uq_entries_shared_bucket'|'uq_entries_committed_bucket'`, MemorySchema.cs:1782-1790; `CREATE UNIQUE INDEX IF NOT EXISTS`, MemorySchema.cs:1822-1827). The same blindness covers a same-named index built over different columns — including the `COALESCE(context_label,'')` term whose absence re-admits duplicate NULL-label buckets. Smallest fix: compare `PRAGMA index_list` (`unique`, `partial`) and `PRAGMA index_xinfo` columns against the in-memory expected bank `SchemaDoctor` already builds, and rebuild a mismatched index in `EnsureAsync`.

---

### F5 — A tracked C# file contains literal NUL bytes, so git treats it as binary and its diffs are unreviewable [MEASURED]
**Severity:** LOW
**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/Encryption/EncryptionKeyResolver.cs` has `0x00` at offsets 2737/2754 (inside `Fingerprint`: `$"{data.Source}\x00{data.ProjectId}\x00{data.SecretId}"` written with raw NULs); `git show --stat 6750b041 -- <file>` → `Bin 4225 -> 4224 bytes`; `git grep -n Fingerprint -- <file>` → `Binary file … matches` with no line numbers. A `git ls-files -z` byte sweep of every tracked file found NULs in exactly four: this file plus the two model binaries and `tests/AiRaccoon.Tests/Resources/docs-memory.db`.
Every change to this key-resolution source since 2026-08-25 (`6750b041`) has been invisible as a diff in PR review and invisible to `git grep`/`git log -S`. Fix is mechanical: literal `"\0"` escapes produce the same string without making the file binary.

---

### F6 — The `entries` workspace-XOR CHECK admits a row with neither a scope nor a workspace [MEASURED]
**Severity:** LOW
**Evidence:** `INSERT INTO entries(hash,path,value,scope,project_id,workspace_id,created_at,updated_at) VALUES('h3','p3','v3',NULL,'proj',NULL,1,1);` succeeds; the both-set case (`scope='project', workspace_id='ws-x'`) fails with `CHECK constraint failed: (workspace_id IS NULL AND scope IN ('shared','project','custom')) OR (workspace_id IS NOT NULL AND scope IS NULL)`.
The DDL comment (MemorySchema.cs:126-128) claims the CHECK makes an entry "either workspace-scratch or one of shared/project/custom, never both" — but SQLite passes a CHECK that evaluates to NULL, so the *neither* case is legal. Such a row is in no tier (`ProjectRows` predicates never match it) yet counts in stats and re-keys as `'workspace'` in the sync tombstone comparison (SyncService.cs:451-453, 521-522), and it is exactly the shape the remote-merge `WHERE r.workspace_id IS NULL` guard admits (SyncService.cs:440-451). Fix: `CHECK ((workspace_id IS NULL) <> (scope IS NULL))`.

---

### F7 — The watcher's file-delete path shares F1's missing-tombstone shape, so a deleted file's chunks return on the next pull [READ]
**Severity:** MEDIUM
**Evidence:** `SqliteMemoryStore.DeleteSourcePathAsync` (:329-368) deletes `entries` rows for the path and its subtree (plus `code_entries` and `watch_files`) inside one transaction and writes **no** `sync_tombstones` rows — the only writers of `sync_tombstones` in `src/` are `DeleteCoreAsync`, `ProjectIdsRepair`, `MemorySchema`'s v11 ladder step and `SyncService` (verified by `grep -rn sync_tombstones src/`). Its only production caller is `WatchDigestExecutor.cs:138` (file removed → cascade delete).
Consequence, by the same merge predicate F1 measured live: the remote snapshot still holds those chunk rows, the entries leg re-inserts them (no tombstone suppresses), and the miss is then pushed. No test in `tests/` writes or asserts a tombstone for this path (grep: only `SyncServiceTests`' hand-seeded tombstones and the v11 schema tests). I did not drive the watcher end-to-end (needs an ingest-scope registration and an FS event), so this is graded READ while F1 — the same root cause, measured — is the evidence that an un-tombstoned delete is undone by sync.

---

### F8 — Remote tombstones are garbage-collected against the *local* pull watermark, so a fresh remote deletion can be dropped on arrival [READ]
**Severity:** LOW
**Evidence:** `SyncService.cs:501-539` — the merge inserts remote tombstones, then runs `DELETE FROM sync_tombstones WHERE deleted_at < @watermark` where `@watermark` is this bank's *previous* `last_pull_at`, while `deleted_at` is the deleting replica's clock; `last_pull_at` is only then advanced.
A remote tombstone whose `deleted_at` predates this bank's last pull is inserted and destroyed in the same pass, so it can never suppress the hash's re-arrival — the entries leg's `NOT EXISTS … sync_tombstones` check (SyncService.cs:448-455) already ran, and the next push from a replica that still holds the row re-inserts it. The native-memory plan mandates "GC below min(last_pull watermark)", so the *rule* is intended; what is unintended is comparing two machines' clocks: a remote clock behind ours turns a fresh deletion into an instantly-GC'd tombstone. I did not build the two-machine, skewed-clock experiment (I have no way to set the two processes' clocks), hence READ.

---

## Refuted / verified sound (leads that produced no finding)

- **Digest-gated migration ladder (the campaign's false-positive warning).** Re-derived by reading `MemorySchema.cs:609-800`: strictly sequential `if (healthy && storedVersion < N)` for v1…v14, both stamps (`user_version`, `application_id`) written only after `healthy` and only in that order; the three `ALTER TABLE ADD COLUMN` helpers re-probe `pragma_table_info`. No gap-jumping, no stamp-before-work. **No finding.**
- **vec0 KNN semantics with the production two-key ORDER BY.** Measured with the vec0-loaded probe: 50 seeded `vec_entries` rows, `k = 5` → the production statement (`ORDER BY v.distance, e.path`, MemorySql.cs:145-151) returns **5** rows, identical to the canonical `ORDER BY distance` shape; both plans report `SCAN v VIRTUAL TABLE INDEX 0:…` + `USE TEMP B-TREE FOR ORDER BY`. The `k` constraint is honoured; this is not the "fullscan returns everything" trap. **No finding.**
- **The vec0 `count(*)` chunk trap.** `grep -rn "count(\*)" src/` finds exactly one FTS count (`MemorySchema.cs:1703`, `entries_fts` vs `entries`) and no `count(*) FROM vec_*`; no chunk-count confusion. **No finding.**
- **LIKE-wildcard leakage in path cascades.** `LikePattern.Escape` escapes `\`, `%`, `_` in that order, and every `@pathPrefix` construction site (7 of them: SqliteMemoryStore.cs:342, Replace.cs:146/150/160, WatchStore.cs:71/123, MemorySchema.cs:1354) uses it. **No finding.**
- **Dropped SQL-parameter mismatches (delta F8 lead, "re-derive, don't trust").** I derived it: 125 SQL constants parsed from `*Sql*.cs`, `@token` sets compared with the bound anonymous/typed parameter object at each `CommandDefinition`/`Def` call site in `src/`. Six apparent misses were all extraction artefacts of multi-line object initialisers (e.g. `WriteChunks.cs:38`, `SqlitePromotionQueueStore.cs:55` bind every column — checked by reading them), and none of the real sites is missing a parameter. Limits of the method: `DynamicParameters` call sites and parameter objects built by a helper are not covered. **No finding.**
- **`ON CONFLICT` targets vs real constraints.** Each targeted clause matched a real constraint/index on a scratch bank: `promotion_queue(project_id,hash)` (inline `UNIQUE`), `watches`/`watch_files`/`watch_digest_claims` (`PRIMARY KEY (project_id, path)`), `sync_tombstones` (`PRIMARY KEY (project_id, hash, scope)`), `settings`/`sync_meta` (`key` PK), `search_quality.correlation_id` (`UNIQUE`), `project_id_aliases` PK, `memory_source` (`uq_memory_source`). Bare `ON CONFLICT DO NOTHING` (entries) is deliberate. **No finding.**
- **`PRAGMA foreign_keys = ON`** is set per connection on every open path (`SqliteConnectionFactory.OpenWithPragmasAsync`), so `entries.workspace_id`'s `ON DELETE RESTRICT` is live rather than inert. **Sound.**

## Still open

- **F7's live arm** — the watcher file-delete → sync resurrection was not driven end-to-end (needs an `ingest.scope` registration, a watch and an FS event); the sibling F1 measurement is the strongest available proof of the mechanism.
- **F8's experiment** — I could not construct the two-replica clock-skew case (no way to skew a process's clock here); the finding stays READ and low.
- **Encryption wrong-key / Bitwarden paths** — read-only: `SqliteConnectionFactory.DiagnoseAsync`/`LegacyKeyOpensHealthyBankAsync`/`RekeyBankAsync` were inspected (open → diagnose → refuse, never writes) but I did not create an encrypted scratch bank or exercise a wrong key or the Bitwarden provider. Lane J owns the security judgement.
- **Sync upload/merge atomicity** — `MergeRemoteAsync` (SyncService.cs:310-600) runs its whole merge as a sequence of autocommits with no surrounding transaction (aliases → entries → memory_source → tombstones → watermark → reindex → recompute). The protocol is monotone (INSERT OR IGNORE union + tombstone deletes) so a partial merge looks retry-safe, which is why I did not raise it; the exception is the alias-conflict probe, which deliberately runs first. Flagging it as unverified, not as sound.
- **`MemorySchema` v6's `noise_clusters`/`vec_noise`** are still created for legacy banks while fresh banks never get them; SchemaDoctor ignores extra objects, so this is inert — noted, not a finding.
- **`RepairCommands` chunk-index/reingest SQL** and `ChunkBackfill`'s re-chunk predicate were read but not exercised against a scratch DB (Lane C owns the lifecycle; #585's dimension reconciliation looked careful — `NeedsRecreateAsync` parses `float[N]` from `sqlite_master` and treats a missing table as needing recreation).

## Grade mix

MEASURED 6 · READ 2 · (8 findings). Commands: 8 sqlite3 probe sets, 3 live MCP sessions, 4 real-`SyncService` runs, 1 vec0 KNN probe, 2 static derivations.

## Owner questions

- **F1/F2/F7**: are un-tombstoned deletes (`memory_delete_context`, workspace-twin `memory_delete`, `DeleteSourcePathAsync`) a bug to fix uniformly, or is there a ruling that only hash-addressed `memory_delete` propagates through sync? If the latter, the tool description and ADR-0052's "Destructive = reaching committed memory" are misleading and should say the delete is local-only.
- **F3**: should re-created content survive its own tombstone (`created_at` guard — the fix D27 proposed in 2026-08-07), or is "delete wins forever for a given hash" the intended semantic? If the latter, resurrecting a fact is currently impossible without a new hash/path.
- **F4**: is `doctor` contracted as existence-only ("verifies schema shape", its own footer), or should it verify index definitions/uniqueness the way it verifies table columns? A HEALTHY verdict currently cannot be distinguished from "uniqueness silently gone".
- **F6**: should the entries CHECK be tightened to exactly-one (`(workspace_id IS NULL) <> (scope IS NULL)`), or is the neither-case a shape some legacy/remote data still uses?
