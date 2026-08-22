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

### The backstop's own cadence, split from the heavy pass's

`BankMaintenanceHostedService` ran one `PeriodicTimer` for everything: the WAL checkpoint, the
pending-embed retry sweep, the noise/retention purges, and — via `MaintenanceJobRunner.RunDueAsync` —
every job's due-ness check, cadence-based and on-demand alike. That timer's period was
`maintenance.checkpoint-interval-minutes` (default 60). Left as the only trigger, the backstop's real
latency would have been bounded by that hour, not by anything about the migration itself — a `model
set` that outran the startup-pass race (no restart happens to pick it up) could sit undrained for up
to an hour, which is not migration latency, it is an outage-shaped wait with the same cause.

The fix is not a second relay — that would be exactly the competing-mechanism trap this design
otherwise avoids — it is recognizing that "how often do we wake up to check what's due" and "how
often does the expensive stuff actually run" are two different questions the single timer had
conflated. They now have separate cadences from the same hosted service: the existing heavy-pass loop
is untouched (same `PeriodicTimer`, same period, same `RunOnceAsync`), and a second, independent loop
wakes every `OnDemandPollInterval` (15 seconds, hardcoded rather than a settings key — the tests this
task's own regression check demanded pin the number, and a setting nobody would tune during
development is not worth the surface) and calls the **same** `MaintenanceJobRunner.RunDueAsync` with
the **same** job list. This is safe for cadence-based jobs by construction: `IsDue` still gates them
on their own `Interval`, so checking fifteen seconds apart instead of an hour apart cannot make vacuum
or chunk-backfill run any more often — it just means the check itself (one indexed
`maintenance_jobs` read per job) now happens far more often, which is what "obviously cheap" describes.
`BankMaintenanceHostedServiceOnDemandPollTests` pins both halves of that claim directly: an on-demand
job is picked up on the very next 15-second poll, and an hourly-interval job's run count is unchanged
after forty of those polls (ten minutes) — the regression the owner asked to see caught if this
refactor got it wrong.

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
recycled PID cannot inherit a dead lease, stale-lease reclamation by expiry alone, renewal after every
batch — `DrainMigrationAsync` calls `TryRenewAsync` once per 32-row batch, the same cadence a
heartbeat would use).

**A real kill-mid-drain test caught a defect in this design's first draft, and the fix is recorded
here rather than smoothed over.** The first version set the TTL to 10 minutes on the reasoning that
re-embedding is the one operation expected to run long and "there is no mid-drain renewal" — both
half-true (re-embedding can run long) and half-wrong (renewal was already wired per-batch; the
comment describing the design had drifted from the code implementing it). The consequence was not
caught by any unit test, because none of them kill a real process mid-batch — it took
`AnInterruptedMigration_KilledMidDrain_IsFinishedByTheNextServersStartupPass` doing exactly that: a
relay killed while it holds the lease leaves `lease_expires_at` frozen at whatever the last renewal
set it to, and **every bank operation is refused for as long as that lease survives** (ToolGate, see
above) — so a stale 10-minute lease is not "wasted inference deferred a bit", it is a **10-minute
full-bank outage** on top of the crash that caused it. The test hung for over five minutes against the
10-minute TTL before this was diagnosed and fixed; the fix is the TTL alone, dropped to 60 seconds
(matching `SqliteWatchScanLease.LeaseTtl` exactly) — generous for one batch's real embedding latency,
and a bound the whole point of this ADR (a crash must not leave the bank silently or indefinitely
degraded) actually requires. Re-embedding an already-embedded row after a premature theft still only
wastes inference, never corrupts anything, which is the trade this TTL is allowed to make in the
theft-too-early direction; there is no equivalent safe direction for "outage lasts ten minutes".

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

**The backstop's latency is now bounded by 15 seconds, not an hour** — `BankMaintenanceHostedService`
gained a second, independent poll loop for exactly this (see above); the heavy checkpoint/vacuum/purge
pass keeps its own separate, configurable cadence untouched. The startup pass remains the correctness
guarantee regardless of either cadence — this only shortens how long the *backstop* trigger can take
when no restart happens to invoke the startup pass instead.

**The on-demand poll interval is a hardcoded constant, not a settings key.** `checkpoint-interval-minutes`
and `vacuum-interval-days` are configurable because an operator plausibly wants to trade their cost
against their benefit on a specific bank. Fifteen seconds of "read one indexed row per job" has no
comparable cost to trade — making it configurable would add a settings key, a parser, a default-value
test and a CLI verb for a number nothing in this task's evidence suggests anyone needs to change.

**`ReadCheckpointIntervalSafeAsync` keeps its name.** Splitting the poll from the cadence means that
method still does exactly what its name says — reads the checkpoint interval — and nothing more; it
would have been misnamed only under the design this task rejected (repurposing the same timer/method
to also govern the on-demand poll). The new poll loop reads its own constant, not this method.

## Amendment 2026-08-16 — ToolGate's migration check cost

A suspected regression, checked rather than assumed: ADR-0075's own "after" baseline (WP5, 3.5 bank
opens / 12-16 statements per operation steady state) was measured at `c2fd31f0` — before this ADR's
commits (`e2fc23ee`..`23281944`) landed on top of it. It never included `ToolGate.RequireAsync`'s
migration check, which runs before every MCP tool call. Reading the code confirmed why it would cost
real money: `IModelMigrationStore.HasOpenModelMigrationAsync` opened a second, full bank connection
(`SqliteConnectionFactory.OpenBankAsync`, paying `MemorySchema.EnsureAsync`'s whole digest-matches
path — 3 connection pragmas + 4 EnsureAsync statements) on top of its own 1-statement query, every
single call.

**Measured, steady state (digest already matches — every install past its first open):**

| | before ADR-0076 (WP5's baseline) | ADR-0076 as shipped (1.21.0) | after this amendment |
|---|---|---|---|
| bank opens / operation | 3 write, 4 search | **4 write, 5 search** (+33%/+25%) | 4 write, 5 search (unchanged) |
| migration check's own statement cost | n/a | **8** (3 pragmas + 4 EnsureAsync + 1 query) | **5** (3 pragmas + 1 digest read + 1 query) |
| statement volume / operation, steady state | 12 write, 16 search | **20 write, 24 search** (+67%/+50%) | **17 write, 21 search** (+42%/+31%) |

The migration check's own statement cost is traced statement-for-statement against a real connection
handle (`ModelMigrationCheckStatementCountTests`, the same `sqlite3_trace` technique
`MemorySchemaDdlStatementCountTests` already uses), watched red (asserting the post-fix cost) against
the pre-fix code before the fix landed. The opens/volume rows combine that exact number with WP5's own
measured opens-per-operation arithmetically — the same way WP5's own report derived its statement
volume (opens × per-open cost), because the factory's own connection pragmas run before an external
trace can attach to the connection it returns.

**The fix: `MemorySchema.EnsureCheapAsync`.** `HasOpenModelMigrationAsync` now opens via a new
`OpenBankSkippingEnsureAsync` (same key resolution and pragmas; skips `EnsureAsync`'s DDL/version
dance) and calls `EnsureCheapAsync` on the returned connection: one `PRAGMA application_id` read,
falling back to the full `EnsureAsync` only when the digest does not match — the same rare,
self-healing path every other bank open already takes. A digest match proves `model_migration`
already exists, because its `CREATE TABLE` lives in the same digest-gated `Ddl` block ADR-0075
already gates on this exact check. No new risk: a digest match/mismatch is the identical signal
`EnsureAsync` itself already trusts (ADR-0075 §"the digest-matches-object-missing gap"), read one
call earlier for a check that does not need the rest of `EnsureAsync`'s work.

**What this does not fix, and why.** Opens/operation stays +1 (write 3→4, search 4→5): the check
still opens a connection separate from whatever the guarded tool operation opens next. Eliminating
that would mean sharing one connection between `ToolGate.RequireAsync` and the ~30 `RequireAsync` call
sites across every guarded tool class (`MemoryTools`, `SweepTools`, `WatchTools`, `PerformanceTools`,
`ShareTools`, `PromotionTools`, `QualityTools`, `SyncTools`, `WorkspaceTools` — nine today, not the
seven this ADR's own "one choke point" line above counted; that number has drifted and is corrected
here rather than silently) and every store method underneath them — a connection-threading refactor
disproportionate to a patch release, not attempted here.

**Correctness is unweakened.** The check still reads live `model_migration` state on every call — no
caching, no staleness, identical to the pre-amendment guarantee: a migration open in this or any other
process is visible to the very next check, not eventually. `ModelMigrationCrashRecoveryE2ETests` and
`ToolGateTests` are unchanged and still green.

**Rejected: an in-process cached flag.** ADR-0020 keeps `--transport stdio` as "the escape hatch" — a
complete, independently-composed second server process against the same bank, tested
(`ServeRunnerTests`), reachable by anyone whose client config names it — not a theoretical concern. A
migration started by that process's own `model set` would be invisible to a cached flag in this one:
a stale `false` would serve a search or write against a half-migrated bank, precisely the failure this
ADR exists to prevent. No staleness window, bounded or not, was judged acceptable without the owner's
explicit sign-off, so this amendment introduces none.

## Correction 2026-08-16 — `finished_at` recorded the wrong duration until 1.21.1

`DrainMigrationAsync` took its `now` as a parameter captured by the caller before the drain loop ran,
then stamped `finished_at` with that stale value — so `finished_at - started_at` measured "time to
enter the method", not "time to drain the bank". Invisible at small scale (a 200-entry bank drains in
~13s, so a 6s stamp did not look wrong); a real 25,917-entry bank exposed it: 357s real wall-clock
drain recorded as 6s, a ~60x understatement. `started_at` was unaffected — it is captured immediately
before the short outbox transaction that writes it, not before a long-running loop. Fixed by giving
`EntryEmbedder` its own `TimeProvider` and reading `finished_at` from it after the drain loop
completes, not from a caller-supplied value.

## Amendment 2026-08-22 — the on-demand-relay pattern extends to `PendingEmbedJob`/`CodeReindexJob`, not `ModelMigrationJob`

ADR-0091 (`0091-the-event-pump-never-blocks-a-producer.md`) amends this ADR's general pattern — an
on-demand `IMaintenanceJob` (`HasWorkAsync` gated on a row, not a clock) relaying a durable pending
flag — for `embed_state = 'pending'`'s other producers: `PendingEmbedJob` and `CodeReindexJob` keep
this ADR's on-demand shape but no longer drain inline themselves; they signal
`AiRaccoon.Core.EventPump<EmbedDrainRequest>`, and `EmbedDrainService`, the pump's single consumer,
performs the drain. `ModelMigrationJob` — this ADR's own named relay for `model set` — is untouched
and does not go through the pump; it still drains `model_migration` directly under its lease.
