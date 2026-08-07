# Watch catch-up runaway — analysis and fix plan

Task: `airaccoon-mem-check`. Branch: `task/airaccoon-mem-check-watch-loop`.

## The incident

A directory watch was registered under the wrong project id, then removed while its initial
catch-up scan was still running. The watcher kept re-chunking the same docs (~7 files/sec,
100-150% CPU), producing ~5,600 duplicate memory entries. Deleting the `watches` row directly
in SQLite did not stop it. Only killing the stdio MCP server processes did.

## Root cause — verified against the code, not inferred

1. **The reconcile stale branch never unregisters from the pipeline.**
   `WatchHostedService.ReconcileAsync` (`WatchHostedService.cs:120-124`) calls `_eventSource.Stop()`
   and `_active.Remove()` but never `_pipeline.UnregisterWatch()`. That method has exactly one
   caller in the codebase: `WatchService.RemoveAsync` (`WatchService.cs:39-44`). Any removal that
   does not go through `WatchService` — a hand-run SQL `DELETE`, as happened here — leaves
   `WatchPipeline._runtime` and `_watchPathsByProject` populated, so `FindContainingWatch`
   (`WatchPipeline.cs:213-230`) keeps resolving that path for the life of the process.

   Second defect in the same loop: it sweeps `_active`, which only ever holds *enabled* watches
   (`:104`). A watch registered while its project is disabled is registered with the pipeline at
   `:92` but never enters `_active`, so its registration can never be swept at all.

2. **The catch-up scan cannot be cancelled.** `WatchCatchUp.EnqueueInitialScan` /
   `EnqueueChangedSince` (`WatchCatchUp.cs:19,21`) are bare `Task.Run` with no `CancellationToken`;
   `ScanCoreAsync` (`:50`) takes none. `ReconcileAsync` has the host `stoppingToken` in scope and
   drops it (`:112,116`). `StopAsync` (`:73-78`) stops the event source only. A scan in flight runs
   to completion regardless of removal *or* host shutdown.

3. **No single-flight guard of any kind.** `_active` is an in-memory `HashSet` local to one process,
   rebuilt empty on every start. The stdio MCP servers recycle roughly every 5 minutes and several
   attach to the same bank at once — 29 `ai-raccoon` processes were live on this machine while the
   incident was being analysed. Each can independently decide a watch needs an initial scan and walk
   the whole tree.

4. **Fingerprints survive a remove.** `WatchStore.RemoveWatchAsync` (`:44-51`) runs only
   `MemorySql.DeleteWatch`. A constant `MemorySql.DeleteWatchFilesByProjectPathCascade`
   (`MemorySql.cs:192-196`) exists and is wired to nothing.

A hypothesis that was **refuted**: that the remove wiped fingerprints and so defeated hash-skip.
It does not — the cascade was never wired. The duplicate entries came from repeated full-tree
scans under the same wrong project id, not from lost fingerprints.

## Scope (decided by the repo owner)

| WP | Change |
|---|---|
| WP1 | Reconcile's stale sweep unregisters from the pipeline, and sweeps registrations rather than `_active` |
| WP2 | Catch-up scan is cancellable; removal and host shutdown both cancel it |
| WP3 | In-process per-`(projectId, path)` single-flight guard |
| WP4 | Cross-process lease so only one process scans a watch at a time |
| WP5 | Removing a watch cascades to its `watch_files` fingerprints |

## Design decisions

**D-1 — one removal choke point.** Scan cancellation hangs off `WatchPipeline.UnregisterWatch`,
which already means "this watch is no longer live in this process". `WatchService.RemoveAsync`
and the fixed stale sweep both inherit it. The rejected alternative — a `CancelScan` call added at
each removal site — reproduces the exact defect that caused this incident: a removal path that
forgot one of two calls. Because `WatchCatchUp` already depends on `WatchPipeline`, the scan
registry is extracted into `WatchScanGuard` to avoid a cycle.

**D-2 — the lease lives on the `watches` row, not a new table.** Two nullable columns
(`scan_owner`, `scan_lease_expires_at`). The lease's lifetime *is* the registration's lifetime, so
`DELETE FROM watches` is lease release and an orphaned lease cannot be represented — which matters
precisely because the operation at the centre of this incident was a hand-run row delete.

**D-3 — no heartbeat task.** Renewal happens inline in the scan's own loop when the injected
`TimeProvider` says the interval elapsed. No extra background task, no extra shutdown path, and a
wedged scan correctly stops renewing and loses the lease to a live process.

**Rejected simpler shape.** Stamping `watches.last_change_ts = now` when a scan *starts* would use
an existing column and no new API, but two processes starting in the same second still both
full-scan, and a process that dies mid-scan leaves the watermark advanced — so the un-scanned tail
is silently never ingested. That trades a data-loss failure mode for a code-size saving. The TTL
lease gives mutual exclusion *and* fails safe.

## The interaction worth stating

WP5 makes remove-then-re-add a **full re-ingest**: the row is gone (watermark restarts at 0) and
the fingerprints are gone (nothing hash-skips). That is the incident's exact shape, so it is pinned
directly:

- `RemoveThenReAdd_WhileTheFirstScanIsRunning_CancelsItAndRunsExactlyOneNewScan`
- `RemoveThenReAdd_ReIngestsEveryFile_WithoutDuplicateEntries`

After the change set, a remove cancels the in-flight scan, unregisters the path so queued events
drop at the next tick, frees the guard entry, releases the lease (and the row deletion would take
it anyway), and deletes the fingerprints. A re-add starts exactly one scan, which must win the
lease before enqueueing anything.

## Build order

| Wave | Group | Files |
|---|---|---|
| 1 | A — WP2 + WP3 + shared test surface | `WatchScanGuard.cs` (new), `WatchCatchUp.cs`, `WatchPipeline.cs`, `Dependencies.cs`, `WatchScanGuardTests.cs` (new), `WatchCatchUpTests.cs`, `WatchTestFakes.cs` |
| 2 | B — WP1 | `WatchHostedService.cs`, `WatchHostedServiceTests.cs` |
| 2 | C — WP5 | `WatchStore.cs`, `WatchStoreCascadeTests.cs` (new), `WatchServiceTests.cs` |
| 2 | D — WP4 | `WatchScanLease.cs` (new), `MemorySql.cs`, `MemorySchema.cs`, `WatchCatchUp.cs`, `WatchScanLeaseTests.cs` (new) |
| 3 | E — interaction tests + full gate | cross-cutting |

B, C and D share no file. `WatchService.cs` is edited by nobody — that is D-1 working.

## Risks this fix set introduces

| # | Risk | Bound | Test |
|---|---|---|---|
| R1 | Lease never released ⇒ watch unscannable | `finally` with a non-cancellable token; TTL ≤60s; row delete frees it | `ScanCore_WhenTheScanIsCancelled_StillReleasesTheLease` |
| R2 | Guard entry leaked ⇒ watch unscannable for the process's whole life (no TTL rescues a dictionary) | removal in `finally` on success, fault and cancellation | `Run_AfterAFailedScan_StartsAgain` |
| R3 | Lease-denied skip + once-per-process gate ⇒ a skipper never retries if the holder dies | accepted; watermark advances per digested file, so the next process resumes cheaply | `Reconcile_AfterALeaseDeniedScan_ANewProcessScansFromTheWatermark` |
| R4 | "Fixing" R3 by scanning every poll ⇒ an empty directory never advances its watermark and full-scans once per second, forever — this incident in new clothes | keep `_active` as the once-per-process gate | `Reconcile_CalledTwice_EnqueuesOnlyOneScan` |
| R5 | Progress-driven renewal ⇒ a >60s pause between files loses the lease | damage capped at one scanner; the loser stops before enqueueing more | `ScanCore_WhenTheLeaseIsLostMidScan_StopsEnqueuing` |
| R6 | `BEGIN IMMEDIATE` on remove contends with live fingerprint writes ⇒ `SQLITE_BUSY` | the connection string's `Default Timeout` (30s), not the PRAGMA — see the note below | `RemoveWatchAsync_WhileFingerprintsAreBeingWritten_Succeeds` |
| R7 | Cancellation logged as `ScanError` ⇒ noise masking real failures | caught before the general handler, logged at Information | `EnqueueInitialScan_CancelledByRemoval_DoesNotLogAScanError` |
| R8 | `WatchScanGuard` registered transient ⇒ single-flight silently does nothing, all tests still pass | DI smoke test asserts identity, not just resolution | `RegisterMemoryServices_ResolvesTheScanGuardAndTheScanLease` |

> **R6, verified 2026-08-08.** The test holds a write transaction open while the remove runs.
> It still passes with `PRAGMA busy_timeout=0`, and fails with the connection string's
> `Default Timeout` at 1s — so what absorbs the contention is Microsoft.Data.Sqlite's
> command-level busy retry, bounded by the default 30s command timeout. `busy_timeout=5000`
> is belt-and-braces, not the mechanism.

## One assumption left for the implementer to verify

The incident produced ~5,600 duplicates *despite* the `watch_files` hash-skip. WP1 removes the
endless re-resolution that fed it, but if entry-level dedup is independently broken, this change
set is not the whole story. `RemoveThenReAdd_ReIngestsEveryFile_WithoutDuplicateEntries` asserting
exactly one entry per file is what would expose that. If it still fails once all five WPs land,
that is a separate defect and needs its own report — not a patch folded in here.

## Outcome

Shipped as four PRs, each verified green before merge:

| PR | Work package | Evidence at merge |
|---|---|---|
| #93 | WP1 — stale sweep unregisters from the pipeline | 297 Watch tests, 0 fail |
| #94 | WP5 — removal cascades to fingerprints | 302 Watch tests, 0 fail |
| #95 | WP2 + WP3 + lease schema/SQL | 318 Watch tests, 0 fail |
| #101 | WP4 — `SqliteWatchScanLease` + renewal wiring | 337 Watch tests, 0 fail |

### Two things the analysis missed

**The stale sweep iterated `_active`, not registrations.** `_active` only ever holds *enabled*
watches, so a watch registered while its project was disabled could never be swept at all — the
missing `UnregisterWatch` call was only half the defect. Caught while planning WP1, fixed in #93.

**`ScanCoreAsync` acquired and released the lease but never renewed it.** The blueprint specified
inline renewal; the implementation that landed in #95 dropped it. Any scan outliving the 60s TTL
would silently lose its lease to a live process and keep enqueueing — the runaway shape again, one
step removed. Caught while wiring WP4, fixed in #101 with the renewal check placed *before* each
enqueue so a lost lease adds nothing further.

### Still open

The assumption above is **not yet settled**: `RemoveThenReAdd_ReIngestsEveryFile_WithoutDuplicateEntries`
was specified in wave 3 and never written, because the waves were reorganised into per-PR branches
when concurrent agents collided in a shared worktree. Entry-level dedup across a remove→re-add
cycle therefore remains unverified. If duplicates recur after all five WPs, that test is what would
expose it, and it is a separate defect.

Wave 3's other cross-cutting test — `RemoveThenReAdd_WhileTheFirstScanIsRunning_CancelsItAndRunsExactlyOneNewScan`
— is likewise unwritten. The individual behaviours it composes are each pinned; their interaction
is not.
