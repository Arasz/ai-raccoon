# 0075. Only the server writes to the bank

Date: 2026-08-16

Status: Proposed

## Context

A profiling pass over a live 193 MB bank asked why `memory_search` cost what it did. The trace named
three things, and the third turned out to be the interesting one:

1. **Schema-ensure per open.** `MemorySchema.EnsureAsync` ran the whole `Ddl` block on every bank
   open, before the `storedVersion >= CurrentVersion` early return at `MemorySchema.cs:414` could
   help. The early return was there; it was just downstream of the work. **Measured: 39 statements
   in the block, 42 for the whole call** — the plan estimated "~30", and estimating is why this
   number is now pinned by a test that traces the real connection handle rather than splitting the
   `Ddl` string (which misparses the trigger bodies' embedded semicolons).
2. **`ISettingsStore` opens a bank per read.** `SqliteSettingsStore.GetSettingAsync`
   (`SqliteSettingsStore.cs:9-17`) opens a fresh bank for a single row, so one search paid the DDL cost
   several times over.
3. **`QueryGuardService` at ~43% of search.** Two settings reads typical, four worst case;
   `EvaluateStructuralAsync` read `StructuralEnabledGlobal` on every Clean query only to learn it was
   off.

The initial hypothesis — *nothing pools, connections are the cost* — was **wrong**. Pooling was never
broken. `Microsoft.Data.Sqlite` pools by default, `BuildConnectionString` never disables it, and the
`efcore#28774` pooling defect was fixed in 6.0.11 against our pinned 10.0.11. What made opens expensive
was what happened *on* each open, not the open itself. Recording that here because the wrong hypothesis
is the one a future reader is most likely to re-derive from the same trace.

Fixing (1)-(3) is arithmetic. But it left a structural question standing: **why is a CLI process opening
the bank for a settings write at all?** Two processes writing the same SQLite file is a design choice
nobody made deliberately — it accumulated one command family at a time.

The owner's steer settled it, and the framing matters more than the performance win: *move all settings
operations to the server; we don't want to implement one part on CLI and one on server.* The split is
not read-versus-write. It is **CLI-only things stay on the CLI, everything else goes through the
server** — with the CLI permitted to *read* the bank directly if it must, but never to write.

## Decision

**The MCP server is the only writer to the bank.**

```
CLI process                          server process
  settings <anything>  ──HTTP──▶       reads AND writes the bank
  encryption <...>     ──HTTP──▶       (WP9: logic moves here too)
  serve                                 — CLI-only, it *is* the server
```

Three consequences fall out of that sentence:

**The CLI surface does not shrink.** The owner was explicit: *the CLI command will stay, just the
logic.* Every command a user types today still exists and still means the same thing. What changes is
which process performs the work. No aliases, no deprecation shims — the release that introduced the
current surface is hours old, so there is no installed base to keep compatible, and surface churn is
free exactly once.

**The many settings command families become one `settings` command.** `queryguard`, `noise`,
`maintenance`, `performance`, `retrieval`, `extract`, `watch` and the rest each grew their own
enable/disable/set verbs against the same settings table. One command, one transport, one place where
a settings write can happen.

**The server auto-starts.** A CLI invocation that needs the server starts it if it is not running,
reusing `BackendLauncher` (30 s budget, 250 ms poll, existing concurrent-launch tolerance) rather than
growing a second launcher. Reuse, not rewrite.

**`encryption` moves too (WP9), and that closes a real race.** `encryption` looked like the obvious
CLI-only holdout — it rekeys the file, so surely it must own the file. It is the opposite.
`RekeyBankAsync` calls `SqliteConnection.ClearPool` (`SqliteConnectionFactory.cs:114`), and
**`ClearPool` is process-local**: a CLI process clearing its own pool does nothing about the pooled
connections the *server* is holding against the bank it just rekeyed. Moving the logic server-side is
what makes the clear-pool meaningful. The performance work and the correctness fix are the same change.

## What was rejected

**"Writes go through the server, reads stay direct."** My own first framing, and the owner corrected
it. It splits one concept across two processes and leaves every future settings operation needing a
judgment call about which half it belongs to. "All settings operations go through the server" needs no
such call.

**A settings cache to make reads cheap (WP8).** Deliberately *not* taken as part of this decision. A
cache is the obvious answer to "settings reads are expensive" and it is gated behind the cross-process
liveness tests WP4 landed first — because a cache that goes stale across processes is worse than the
cost it removes, and the only way to know it does not is a test that can observe staleness. That test
was proven able to discriminate by building a naive caching decorator and watching it go stale.

**Refusing to let the CLI read the bank at all.** Stronger and simpler to state, but it buys nothing:
a read cannot corrupt, and forbidding it would force a server round-trip on genuinely local questions.
The invariant that carries the weight is *zero bank **writes** from the CLI process*, and that is what
the gate asserts.

## Consequences

- The gate is a **route-table guard**: zero bank writes originate from the CLI process. It is the check
  the whole single-writer claim rests on, so it is the one that must be watched failing.
- Two processes no longer contend for the same SQLite writer lock, which removes a class of
  intermittent failure we have been paying for without naming.
- `EncryptionCommands.cs` currently owns `EventId` 800-807 (`docs/reference/logging-event-ids.md`).
  If WP9 moves that logic server-side, the allocations move with it and that table must move too — it
  is a measurement, not a hand-maintained list, so regenerate rather than hand-edit.
- **Open, and deliberately not decided here:** whether `sync` moves now or after the route-table guard
  is green. `sync` over HTTPS would solve the secret-passing problem, and accepting sync only via
  existing credentials would mean no secret is passed at all — but that is a separate decision from
  this one.

## Evidence

**Measured before, on the unoptimised tree** (`docs/work/perf/2026-08-16-wp5-before-baseline.md`).
Counts come from a test-only decorator over `ISqliteConnectionFactory`/`ISettingsStore`, not from
`dotnet-trace` — exact counts, no EventPipe sampling overhead to discount:

| | Plan estimated | Measured |
|---|---|---|
| `Ddl` block statements | ~30 | **39** |
| `EnsureAsync` on an already-current bank | — | **42** |
| Bank opens per operation | 3.60 | **4.5** (4 write, 5 search) |
| Settings reads per operation | ~2 | 2 write, 2 search |

Wall clock, real out-of-process Release server over loopback: write median 56-60 ms / p95 92 ms;
search median 42-44 ms / p95 68 ms. A-A noise floor 5-10%, so treat anything under 10% as no change.

**The plan was wrong in three places and this records that rather than quietly restating it.** The
statement count was underestimated by ~25%, opens per operation by 20%, and the two settings reads
observed on every `memory_write` are not explained by the plan's own write-path analysis, which names
only `MemoryAccessGuard` — an `IMemoryStore` cost, not an `ISettingsStore` one. **That gap is open.**

**After.** `MemorySchemaDdlStatementCountTests` pins both sides of the gate: **0 `Ddl` statements and
4 total when the digest matches, 39 when it is stale.** So the per-open cost goes 42 → 4 on every
install past its first run. `QueryGuardServiceTests` pins query-guard settings reads at 4 → 1 on the
structural path and 2 → 1 elsewhere, watched red first (`CallCount should be 1 but was 2/2/2/4`).

*Still pending: the route-table guard watched red, and the after-measurement rerun. The status stays
Proposed until both exist — an ADR whose evidence is a promise has not earned Accepted.*
