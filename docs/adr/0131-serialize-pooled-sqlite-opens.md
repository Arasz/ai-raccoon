# 0131 — Pooled SQLite opens are serialized until Microsoft.Data.Sqlite ships the pool fix

Date: 2026-10-13

Status: Accepted; the gate was removed in 1.58.1, when `Microsoft.Data.Sqlite.Core` moved to 10.0.13,
which carries dotnet/efcore#39012. The dispose-on-failure in `OpenWithPragmasAsync` stays.

## Context

A running 1.57.5 server logged `Watch re-watch reconcile pass failed` with
`ObjectDisposedException: Cannot access a disposed object. Object name: 'SQLitePCL.sqlite3'`.
The stack runs `WatchHostedService.ReconcilePassAsync` → `WatchStore.ListWatchesAsync` →
`SqliteConnectionFactory.OpenBankAsync` → `OpenConnectionAsync` → `OpenWithPragmasAsync`, and
fails on the first `PRAGMA foreign_keys = ON`. `OpenAsync` had already succeeded. A pooled
`OpenAsync` only pops a native connection off the pool and runs nothing on it, so the native
handle was disposed while it sat in the pool, before this open ever touched it.

The cause is in Microsoft.Data.Sqlite, not in our code. In 10.0.12 (the version this repo pins),
`SqliteConnectionInternal.Activate` sets `_active = true` before it publishes the owning
`SqliteConnection`. A connection that is active with no owner is what
`SqliteConnectionPool.ReclaimLeakedConnections` treats as leaked. A concurrent checkout that hits
that window reclaims the connection and lends it to a second live `SqliteConnection`. When both
owners close, `Return` pushes the same internal connection onto the idle stack twice, because it
does not check for duplicates. The pool's 2 to 4 minute prune later disposes one copy while the other is
still idle or rented, and the next open that pops it gets a dead handle. The same race can also put
two writers on one native connection, which upstream reports as
`cannot start a transaction within a transaction`.

Upstream tracks this as dotnet/efcore#39008. The fix (dotnet/efcore#39012, swap the two lines) merged
into `release/10.0` on 2026-09-18, after the `v10.0.12` tag. As of this record no 10.0.13 is on
NuGet. Upstream marks it "not a regression", so every version we have shipped carries the race. It
surfaces now because the server opens connections concurrently all the time: the embed drain, the
watch digest (four at a time), maintenance and the MCP tools.

A second, smaller defect sat on the same path. When a pragma after `OpenAsync` threw (a
cancellation, a busy bank), `OpenWithPragmasAsync` left the connection open and checked out. Only
the pool's leak recovery, after a GC, ever got it back.

## Decision

**Every pooled `SqliteConnection` open in the process takes its pool checkout under one
process-wide gate.** `SqliteConnectionFactory.PoolCheckoutGate` is a `SemaphoreSlim(1, 1)`, and
`SqliteConnectionFactory.OpenPooledAsync` wraps `connection.OpenAsync` in it. The bank open
(`OpenWithPragmasAsync`), the sync snapshot opens in `AppRegistrations` and the doctor's read-only
bank open all go through it. Two opens can then never be inside `GetConnection` and `Activate` at
the same time, which is the only place the race lives.

The gate covers the checkout only. Pragmas, vec0 loading, the schema pass and all statement work
run outside it.

Opens with `Pooling = false` (the rekey connection and the legacy-key probe) and `:memory:` opens
skip the pool entirely, so they stay ungated.

**`OpenWithPragmasAsync` disposes the connection and rethrows when any step fails**, the same
contract `InitializeAsync` already keeps, so a failed open returns its native connection to the
pool at once.

**Removal trigger.** When `Microsoft.Data.Sqlite.Core` moves to a release that contains
dotnet/efcore#39012 (10.0.13 or later on the 10.0 line), delete `PoolCheckoutGate` and make
`OpenPooledAsync` a plain `OpenAsync` (or inline it). Keep the dispose-on-failure.

**Rejected alternatives.**

- *`Pooling = false` on the bank connection string.* It closes the race, but every open would pay
  the cipher key setup and a full `MemorySchema.EnsureAsync`. The per-handle cache that skips the
  schema pass is keyed by native handle and would never hit. That cost is what the cache was added
  to remove from the idle reconcile loop.
- *Wait for 10.0.13.* The release date is unknown, and until then a pass can fail or two writers
  can share a connection.
- *Pin a nightly or the 11.0 release candidate.* That trades one known two-line bug for an
  unreleased provider across the whole data layer.
- *Detect a dead handle after open and retry.* That treats one symptom. The shared-connection case
  (two owners, one transaction state) would still happen silently.

## Consequences

- **Positive.** The pool can no longer hand one native connection to two owners, so it can no
  longer hold a duplicate that pruning disposes under a live user. A failed pragma no longer leaves
  a connection checked out.
- **Cost.** Pool checkouts are serialized. A warm checkout is a stack pop, so the gate is held for
  microseconds. A cold checkout (a new native connection) holds it through `sqlite3_open` and the
  cipher key setup, so concurrent cold opens queue behind each other. Cold opens happen at startup
  and after the pool prunes idle connections.
- **Neutral.** No tool, schema, setting or log-event change.

## Verification

`tests/AiRaccoon.Tests/Unit/Sqlite/PooledOpenTests.cs`:

- `Open_WaitsWhileTheCheckoutGateIsHeld` holds the gate and asserts an open stays pending with the
  connection `Closed`, then completes once the gate is released. It goes red with the gate's wait
  and release removed.
- `FailedOpen_ReleasesTheGate` fails an `OpenAsync` inside the gate (a missing directory) and
  asserts the next open completes. It goes red when the release moves out of `finally`.
- `PragmaFailure_ClosesTheConnection` cancels the token at the `Open` state change, so the first
  pragma throws, and asserts the connection ends `Closed`. It goes red without the dispose.

The race itself is a two-line window inside the provider and is not reproduced deterministically
here. Upstream's fix carries its own regression test.
