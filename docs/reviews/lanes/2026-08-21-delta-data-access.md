# Lane report — Data-access/SQLite (2026-08-21 delta campaign)

Lane: data-access · Base: `155f281e` · Read-only · 10 findings (1 MEASURED, 7 READ, 1 INFERRED,
1 MEASURED-disproof; 3 MEDIUM, 4 LOW, 3 NIT). No BLOCKER/HIGH. Two briefed leads disproven (F4, F9).

### F1 — EnsureAsync stamps the DDL digest before the ladder runs; a crash in that window leaves a bank the ToolGate cheap-path trusts at a stale schema version [READ]
**Severity:** MEDIUM
**Evidence:** `MemorySchema.cs:456-459` (Ddl → `StampSchemaDigestAsync`) vs `:551-558` (ladder → `StampAsync(CurrentVersion)`); `EnsureCheapAsync` (`:830-840`) checks only `application_id` and is called on every tool call (`SqliteConnectionFactory.cs:142-153`, `SqliteMemoryStore.ModelMigration.cs:15-22`). Crash between digest and version stamp ⇒ cheap path skips the ladder entirely. Self-heals on a full `OpenBankAsync`, but until then maintenance tables may be absent on the hot path (feeding F2). Fix: stamp digest last.

### F2 — "One job's failure must not stop the rest" only guards RunAsync; HasWorkAsync and the ledger read sit outside the try/catch [READ]
**Severity:** LOW
**Evidence:** `MaintenanceJobRunner.cs:38-46` (lastRun SELECT + HasWorkAsync) vs try at `:56`. A throw from any job's HasWorkAsync (e.g. `maintenance_jobs`/`model_migration` missing in F1's window; `ModelMigrationJob.cs:25-29` unguarded) aborts the whole pass, skipping vacuum/retention/pending-embed behind it.

### F3 — Between `model set` commit and the first drain's reconcile, inline-embed writes fail on vec0 dimension mismatch and searches run vector-less [INFERRED]
**Severity:** MEDIUM
**Evidence:** `EntryEmbedder.cs:108` empties vec tables via pending triggers; `ReconcileAsync` runs only inside `DrainMigrationAsync` (`EntryEmbedder.cs:152`), driven by the 15s poll. In the window, write-path inline embeds produce new-dim blobs into the still-old-dim vec0 table → SqliteException, uncaught in write or search path (row stays pending — no loss, but the tool call errors; ~15s window with a server up, unbounded if `model set` ran with no server running). The reconcile-to-drain-completion half of the ordering claim holds — no join-shaped corruption found.

### F4 — The reconciler's BEGIN IMMEDIATE is real but implicit, pinned only by Microsoft.Data.Sqlite's default isolation [MEASURED]
**Severity:** NIT — `VecDimensionReconciler.cs:35-36` passes no isolation level; Serializable→IMMEDIATE today, but nothing states it (contrast explicit `BEGIN IMMEDIATE` at `MemorySchema.cs:692`).

### F5 — FinishModelMigration is not lease-guarded, unlike Renew [READ]
**Severity:** LOW — `MemorySql.cs:384-386` has no `lease_owner` predicate (contrast `:392-394`). An expired-lease relay can still close the outbox row; consequences mild (idempotent drains), but the state machine is laxer than its lease.

### F6 — Metrics retention purge is wired and cadence-gated but unbounded per pass [READ]
**Severity:** LOW — registered (`AppRegistrations.cs:163`), 2h interval, indexed, ledger-stamped on success; but `DELETE FROM metrics WHERE recorded_at < @cutoff` (`MaintenanceJobs.cs:151-154`) has no LIMIT.

### F7 — SchemaDoctor's diff is not snapshot-consistent against concurrent DDL [READ]
**Severity:** LOW — `SchemaDoctor.cs:17-31` reads pragmas + `sqlite_master` with no transaction; transient false `ShapeMismatch` (exit 20) possible during a v9 rebuild/reconcile. Read-only, no corruption. Nit: unquoted `pragma_table_info('{table}')` interpolation at `:126` (catalog-sourced today).

### F8 — Parameter sweep of the delta's new SQL: zero dropped-parameter mismatches [MEASURED — confirmation]
~28 new statements cross-checked (`MemorySql.cs:374-432`, `SqliteMetricsStore.cs`, `MaintenanceJobs.cs`, `MaintenanceJobRunner.cs`, `VecDimensionReconciler.cs`, `EntryEmbedder.cs`); the prior campaign's clean result extends to the delta.

### F9 — MeasurementBuffer reserve-before-enqueue: no lost update [READ — lead disproven]
`MeasurementBuffer.cs:21-48` — reject-path Decrement is transient and converges; `DrainAll` decrements per dequeued item; no double-count or stranding.

### F10 — Migration ladder is append-only in steps; historical step bodies were edited, safely [READ]
v9/v10 added, none renamed/deleted; `MigrateToV2Async` body changed but only runs pre-v2 and the end state matches the old composition. Residual: v8-era banks keep `chunk_index DEFAULT 0` vs -1 on fresh banks — only reachable if an insert omits the column (write paths set it).

## Still open
- Whether vec0 actually raises dimension-mismatch on insert/query against an empty old-dim table (F3 is mechanical inference, not executed).
- Whether the search pipeline surfaces or swallows vec errors mid-migration.
- The `storedVersion > CurrentVersion` early-return at `MemorySchema.cs:478-482` — appears to stamp `CurrentVersion`, which would be wrong for a newer bank; needs a look.
- Speed=Nightly CI coverage — resolved separately by orchestrator (nightly.yml unfiltered).

## Owner questions
- F1: Move `StampSchemaDigestAsync` after `StampAsync(CurrentVersion)`?
- F3: Should reconcile also run at open/ToolGate when engine dim ≠ vec dim?
- F2: Wrap `HasWorkAsync` + ledger read in the per-job guard?
- F6: Cap the retention DELETE per pass (LIMIT + loop)?
