# 0070. Maintenance is a list of jobs with a ledger in the bank

Date: 2026-08-15

Status: Accepted

Amends ADR-0010, which introduced the vacuum cadence. The cadence stands; where its clock lives does not.

## Context

WP5 shipped ladder step v9, which rebuilds both vec0 tables with `ctx` demoted to a metadata column
(ADR-0068). The owner asked the question that turned out to matter: *"we will reclaim size on this
machine, but what for other users?"*

**Measured on a copy of a real 183 MB bank, and the answer was nobody — including us.**

| | |
|---|---|
| v9 frees | **42.0 MB** into SQLite's free list |
| file size after the migration | **unchanged — 183,099,392 bytes** |
| after a `VACUUM` | **129,892,352 (−53.2 MB, −29%)** |
| `VACUUM` cost | **0.6 s** |

The rebuild frees pages; the *file* does not shrink until something vacuums it. And nothing reliably
did. `BankMaintenanceHostedService` held `_lastVacuumUtc`, an in-memory field **seeded on the first
pass** — its own comment said *"short-lived processes never vacuum (the clock resets per process)"*.
Every ai-raccoon MCP server is a short-lived process. A bank only ever opened by them would have
carried those 42 MB forever.

The same reasoning applies to the over-window rows WP3 found: **6,240 of 17,219 on the live bank**,
377,431 tokens outside their own embedding window. `ChunkBackfill` fixed that on one machine, by
hand. Every other user's bank has the same defect and no way to heal.

## Decision

**Maintenance is a list of jobs, and the schedule lives in the bank.**

```
CREATE TABLE maintenance_jobs (name TEXT PRIMARY KEY, last_run_at INTEGER NOT NULL, run_count INTEGER NOT NULL DEFAULT 0);
```

A job declares a name, a display name and an interval; `MaintenanceJobRunner` decides what is due,
runs it and stamps the ledger. **An interval of `null` means once per bank, ever** — which is why the
ledger stores a timestamp rather than a boolean: the same column answers "when last" and "at all".

| Job | Cadence | What it does |
|---|---|---|
| `vacuum` | 2 hours | VACUUM + ANALYZE. Converted from the hosted service's own timer. |
| `vec0-reclaim` | once | Reclaims the pages v9 freed, without waiting for the vacuum cadence. |
| `chunk-backfill` | once | Splits rows larger than the embedding window (WP3 step 4). |

**The default cadence moves 7 days → 2 hours.** The old number was chosen to keep an expensive
operation rare. The measurement above says it is not expensive: 0.6 s on a 183 MB bank. An
explicitly configured `maintenance.vacuum-interval-days` still wins — that CLI verb did not stop
meaning anything; only the default changed.

**A failed job is not stamped.** It retries on the next pass, which is what a transient lock wants,
and it is the difference between a run-once job that deferred and one that is retired having never
run. One job's failure does not stop the ones after it.

## Consequences

**Every user's bank now self-heals**, which is the whole point. A short-lived process that lives long
enough for one maintenance pass runs whatever is due, and the ledger it stamps is read by the next
process rather than discarded with it.

**Two tests were adjudicated rather than ported.** Three vacuum tests moved to `VacuumJobTests`
because their assertions were the contract — VACUUM+ANALYZE runs, a contended bank defers, a
configured interval is honoured. `RunOnce_VacuumNotDue_OnFirstTick_SeedsTheClock` was **deleted**: it
asserted that the first pass deliberately does nothing, which is the defect this record removes, not
a contract. A test that pins a defect in place is the specification encoding the bug.

**A latent bug surfaced while proving a job ran.** `connection.ExecuteAsync(new CommandDefinition("VACUUM"))`
never executed: **Dapper infers `CommandType.StoredProcedure` for a SQL string containing no
whitespace**, so it failed with *"The CommandType 'StoredProcedure' is not supported"*. The trailing
semicolon in `"VACUUM;"` is load-bearing. This was found only because a test asserted the job's
outcome carried no error rather than asserting a side effect — a side-effect assertion would have
been satisfied by a bank that happened to have nothing to reclaim.

**The event-id uniqueness gate earned its keep.** The first draft used 530-531, which
`SweepHostedService` already owns; the gate named both collisions. Moved to 525-526.

**Amendment, same day.** A job now reports whether it *created work*, and the pass sweeps pending
embeds again only when one did. The first version swept after any job that ran, which was wrong twice
over: vacuum and reclaim never make work, and the needless sweep doubled the window in which a
background embed races an in-flight write — `Embeddings_ConfigureOpenAi_RoutesThroughTheConfiguredEndpoint`
went red on CI while passing locally, asserting one embedding request and seeing two. Only
`chunk-backfill` returns true, and only when it actually replaced rows.

**What this does not do:** it does not bound how long a job may take. `chunk-backfill` on a bank the
size of the one measured here rewrites 6,240 rows and leaves 13,578 rows pending embedding. It runs
once, in the background, but a user watching a maintenance pass will see it work.
