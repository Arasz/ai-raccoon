# 0076. `model set` is an outbox, drained by an on-demand relay

Date: 2026-08-16

Status: Accepted

Closes issue #358 (`model set`'s progress shape kept it a CLI writer, ADR-0075 §10.3). Amends
ADR-0070 (maintenance is a list of jobs with a ledger — a model migration is one such job) and
completes ADR-0075's write-exclusivity invariant, which was not fully reached while `model set`
stayed a CLI-direct write.

## Context

`model set` re-embeds the whole bank when the embedding engine changes (`EntryEmbedder.ConfigureAsync`).
ADR-0075 exempted it from routing through the settings server because three questions were unruled:
what the CLI shows while it runs, what happens if the CLI is interrupted, and what happens if two
callers race. The owner ruled all three:

- **No progress reporting.** The CLI does not stream or poll; the absence of a progress channel is a
  decision, not an omission still owed.
- **Lock all DB operations for the duration.** A bank whose rows are half old-model and half
  new-model vectors is not detectably broken — it just retrieves worse. Serving through a migration
  would produce exactly that, silently.
- **The re-embed must complete even if the operation dies mid-way**, via a durable record: write
  that the change started, run the migration, mark it finished only on completion.

The first draft of the third point was a two-step status record — write "started", then migrate, then
write "finished" — with the write and the migration as separate steps. The owner corrected this
mid-implementation: that shape leaves an ordering window (an instant where the engine changed but
nothing yet records the re-embed it owes) and reasons about "which model is current" that a stronger
shape doesn't need. **The correction was to the transactional outbox pattern**, and a second
correction — after the outbox was in place — removed an inline re-embed this ADR's first draft still
performed synchronously inside the request that started the migration, on the grounds that starting
and finishing a migration are two different responsibilities that had been collapsed into one.

## Decision

**In one transaction, `model set` writes the new engine settings and the work it owes; a separate
relay drains it.**

```
BEGIN
  UPDATE settings         SET embedding engine = X       -- the business change
  UPSERT model_migration  (open, engine = X)              -- the work it owes
  UPDATE entries           SET embed_state = 'pending'     -- every stale vector, at commit
COMMIT
-- the endpoint returns here — no re-embed in this request
-- ... a separate relay (ModelMigrationJob) drains the pending rows and marks the row finished
```

The property this buys: **there is no instant in which the engine changed but nothing records that a
re-embed is owed, and none in which a re-embed is owed but the engine did not change.** A crash
anywhere leaves both facts or neither.

**Why the pattern fits especially well here.** An outbox normally exists to solve a *dual-write*
problem — a service updates a database and publishes to a message broker, two systems with no shared
transaction. Here both writes land in the same SQLite file, so the transaction is real and costs
nothing; the pattern degenerates to "one transaction", which is the whole point. The consequence worth
stating for whoever touches this next: **moving the re-embed trigger to an in-memory queue, a
`Task.Run`, an event, or any other process-local signal reintroduces the dual-write problem the outbox
exists to remove.** The work record must live in the bank, and the only supported way to observe or
drive it is by reading/writing the `model_migration` row.

### The relay is an `IMaintenanceJob`, on-demand rather than clock-gated

ADR-0070 already established "maintenance is a list of jobs with a ledger", drained by
`MaintenanceJobRunner.RunDueAsync`. `ModelMigrationJob` reuses that mechanism rather than inventing a
second one. But `MaintenanceJobRunner` stamps `maintenance_jobs.last_run_at` on every success and
treats a job with `Interval = null` as **run-once** — exactly wrong for a migration that must fire
whenever work appears, not once per bank ever. `IMaintenanceJob` gained `HasWorkAsync`, an on-demand
due-ness check the runner consults independently of the clock: `ModelMigrationJob.HasWorkAsync` is one
indexed read of `model_migration`, true exactly while a row is open. Every existing cadence-based job
(vacuum, chunk-backfill, metrics retention) is unaffected — the default implementation returns false.

Three triggers can run the job; only two are the correctness guarantee. The startup pass
(`BankMaintenanceHostedService.ExecuteAsync` runs one pass before arming the periodic timer) is the
crash-recovery path and the important one — it runs before any traffic is served, regardless of
whether the process that started the migration is the one now starting up. The periodic tick is a
backstop. **There is no immediate "kick" after the outbox commits.** An early draft of this design
had the settings endpoint run the migration inline, which made the kick load-bearing for latency; once
that was removed (see below), the kick added nothing the periodic tick doesn't already provide, and a
process-local kick is precisely the dual-write fragility the outbox exists to avoid. `model set`
commits and returns; the relay picks the row up on its own schedule.

### No inline re-embed in the settings endpoint

The first version of this design ran the migration synchronously inside the HTTP request that started
it — commit the outbox, then drain it, then respond. That satisfied "the CLI waits" but conflated two
responsibilities the owner's steer separated: *starting* a migration (fast, transactional) and
*finishing* one (potentially slow, a background concern). Collapsing them meant every `model set`
call held an HTTP request open for as long as the bank took to re-embed, and made the outbox's
crash-recovery machinery exercise only the rare path (a request that died mid-drain) rather than the
normal one. `model set` now returns as soon as the transaction commits. The relay — not the request —
does the re-embedding, every time, so the crash-recovery path and the normal path are the same code.

### The lock and the completion guarantee are the same mechanism

`ToolGate.RequireAsync` (the one choke point all seven MCP tool classes already share) refuses every
call with `model-migration-in-progress` while `model_migration` has an open row. This is not a
separate watchdog bolted onto the migration — it is the direct consequence of the row's own state, so
completion is not optional: the server cannot proceed in a degraded state, only in a refusing one. The
honest corollary: **a permanently failing migration means a permanently refusing server.** That is the
correct trade against a silently degraded bank, and it is loud rather than silent — every refusal
names the reason, and the migration retries every relay pass rather than being abandoned once.

### The lease

Two server processes could both observe an open migration and both try to drain it — briefly during a
`serve --restart` swap, or if two processes are pointed at the same bank file by mistake.
`SqliteModelMigrationLease` guards the drain with the same shape as `SqliteWatchScanLease`
(`lease_owner`/`lease_expires_at` columns on the row itself, a per-process GUID identity so a
recycled PID cannot inherit a dead lease, stale-lease reclamation by expiry alone). The TTL (10
minutes) is generous and is not renewed mid-drain by a heartbeat, unlike the watch lease's 20-second
renewal: re-embedding the whole bank is the one operation in this codebase expected to run long, and a
second pass that out-waits the TTL simply re-acquires and continues from wherever pending rows remain
— re-embedding an already-embedded row wastes inference, it does not corrupt anything. Measure only
when the measurement pays: a heartbeat would need its own test and its own failure mode, for a
scenario (a single migration exceeding ten minutes on hardware fast enough to run this codebase's own
test suite) nothing so far has shown reason to expect.

### Per-row progress is the existing pending flag, not a new mechanism

The outbox commits `UPDATE entries SET embed_state = 'pending' WHERE embed_state = 'embedded'` in the
same transaction as the settings change. This is not new machinery: `embed_state` already drove
`memory_embed_pending`'s retry semantics (ADR predates this one), and the schema's own triggers
(`vec_entries_pending`, `vec_structure_pending`) already remove a row from the searchable vec0 index
the instant it moves to `pending`. Reusing it here means the outbox's own payoff — no window where a
stale vector is searchable under the new engine's name — falls out of a mechanism the bank already
had, rather than a parallel one this task would otherwise have had to invent and prove correct
separately. `ModelMigrationJob` drains the SAME rows via a bank-wide (not project-scoped) batch loop,
sharing `EntryEmbedder`'s existing `EmbedAsync` primitive.

## What was rejected

**A two-step status record** (write started, migrate, write finished as three separate operations) —
superseded by the outbox transaction above; see Context.

**An inline re-embed in the settings endpoint** — superseded by the on-demand relay above; see
Context. Latency from `model set` to "fully re-embedded" is now bounded by the maintenance loop's poll
cadence rather than being zero, which the owner judged the correct trade once "the CLI waits" was
understood to mean "waits for the outbox to commit", not "waits for the whole bank to finish".

**Gaming `IMaintenanceJob.Interval` with a short non-null value** so the migration job would be due
"often enough" — rejected in favor of `HasWorkAsync`, a first-class on-demand shape, because a
clock-gated interval is answering the wrong question (when should this run) for a job whose real
question is whether it has anything to do.

**A second relay mechanism** parallel to `MaintenanceJobRunner` — rejected; `ModelMigrationJob` is an
ordinary `IMaintenanceJob` in the same list the vacuum/backfill/retention jobs already live in.

## Consequences

**ADR-0075's write-exclusivity invariant is reachable for the first time.** `CliWriteOptOuts` reduces
to `encryption` alone — the one remaining, deliberate, understood holdout (the bootstrap path that
creates and keys the bank before a server exists to resolve a key against it).

**A started-but-not-finished record is the only thing standing between a crash and a silently
degraded bank**, which is why it is the part this task's own test suite kills for real: a live
`ai-raccoon serve` process, `model set` run against it, the process killed mid-drain (not a mocked
exception), and a fresh `serve` shown completing the migration with no kick ever having fired — plus
the negative, with the resume logic removed, showing the same interruption leaves the bank
half-migrated and nothing notices. See the PR's test plan for both directions.

**`model set`'s own CLI output changed.** It used to report the engine as already active; it now
reports the migration as started and re-embedding in the background, because that is what the command
now does. There is deliberately no way to ask the CLI "is it done yet" — the owner ruled against a
progress channel, and a status-polling command would have been exactly that under a different name.

**A permanently failing migration is a permanently refusing server**, by design (see "The lock and
the completion guarantee are the same mechanism" above) — a diagnosable, loud failure mode rather than
a silent one, but an operational one an administrator has to be able to recognize from the refusal
message and the server's own logs (`ModelMigrationStarted`/the job runner's `JobFailed` at Warning,
retried every pass) rather than from any dashboard, since none exists.

**Not addressed here, and worth naming so nobody assumes it was considered and rejected:**
`BankMaintenanceHostedService`'s single `PeriodicTimer` still paces the on-demand job's poll at the
same cadence as the heavy checkpoint/vacuum/purge pass (`maintenance.checkpoint-interval-minutes`,
default 60). Separating "how often do we check for on-demand work" from "how often do we run the
expensive stuff" — so a migration is picked up within seconds of a restart rather than within an hour
— is a real improvement this task did not make, to keep the blast radius on an already large change
bounded to one well-tested loop. The startup pass is unaffected by this and remains the correctness
guarantee regardless of poll cadence.
