# 0075. Only the server writes to the bank

Date: 2026-08-16

Status: Accepted

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
- **The digest gate narrows ADR-0026, and this is the one consequence that needs the owner's
  ratification.** ADR-0026 put `promotion_discards` in "the unconditional `MemorySchema.Ddl`" so it
  would reach every existing bank with no schema-version bump — same precedent as `watches` /
  `watch_files`. **That requirement survives**: adding a table changes the `Ddl` string, which changes
  the digest, which forces the rerun, so delivery to existing banks is unaffected. What is lost is
  narrower and was never stated as a requirement — it was a free side effect of running `Ddl` every
  time: a bank whose digest **matches** but is missing an object no longer self-heals on open. That is
  manual surgery or corruption, not version skew. `PromotionQueueDiscardTests` now asserts **both**
  halves so the narrowing is a recorded decision rather than a silent regression. Restoring the old
  property would cost a per-open existence probe — precisely the cost this ADR exists to remove.

  **Ratified after enumerating how that state is actually reachable, verified against a scratch
  database rather than reasoned about.** The digest is stamped only *after* `Ddl` completes, so
  every automated path self-heals:

  | path | `application_id` after | outcome |
  |---|---|---|
  | `Ddl` fails part-way (statement *n* of 39) | stays `0` — the stamp never runs | next open reruns `Ddl` — **safe** |
  | `.dump` → restore | `0` — a dump emits no `application_id` | next open reruns `Ddl` — **safe** |
  | `VACUUM INTO` backup | preserved, *with* every object copied | consistent — **safe** |
  | a new `Ddl` adds an object | digest changes | rerun forced — **safe**, ADR-0026 intact |

  Only two routes reach digest-matches-object-missing:

  1. **Manual `DROP` after a successful stamp** — surgery, debugging, a stray script. This is the
     one property the gate genuinely gives up.
  2. **A 32-bit digest collision**: a bank stamped by `Ddl`-A opened by a binary whose `Ddl`-B
     truncates to the same 32 bits, so B's new objects are never created. This is the only *new*
     failure mode. `P(any collision)` across every `Ddl` version this project will ever ship is
     **~1e-8 at 10 versions, ~1e-6 at 100**. It is also self-limiting: the `user_version` ladder
     runs regardless of the digest, so a change that bumps `CurrentVersion` is immune. Bumping the
     version is therefore the escape hatch for any additive change important enough to want
     certainty — at the cost of the very thing ADR-0026 avoided.

  Eliminating the collision entirely would mean storing the full SHA-256 in a row rather than the
  32-bit `application_id` header slot, costing one extra read on the fast path (5 statements
  instead of 4, against 42 before). Not taken: a 1-in-a-million risk that only bites additive
  changes which deliberately skip the version ladder does not justify making the fast path's
  bootstrap depend on a table the fast path exists to avoid creating.

  Separately and **not** caused by this ADR: `CREATE TABLE IF NOT EXISTS` silently keeps a
  pre-existing table of the wrong shape (verified, issue #357). That hole was equally open when
  `Ddl` ran on every open — the digest neither widens nor narrows it.

- **The zero-digest hazard.** A fresh SQLite file reads `application_id = 0`. If `SHA-256(Ddl)`
  ever truncated to `0`, a fresh bank would falsely *match* and skip `Ddl` on its first open —
  creating no schema at all. It is a 1-in-4-billion property of whatever `Ddl` happens to say, so
  it is not a live concern; it is a tripwire for a future `Ddl` edit, and
  `MemorySchemaDigestTests.SchemaDigest_IsNotZero` is that tripwire.

- **Alternating binaries defeat the gate on a shared bank.** Two builds whose `Ddl` differs, opening
  the same bank in turn, each see a mismatch: each reruns the full block and restamps, so the bank
  pays *more* than before — every rerun is also a `PRAGMA schema_version` bump, which invalidates
  every other connection's prepared-statement cache. This is a developer-machine and partial-upgrade
  scenario, not a production one, but it is the case where this optimisation is a pessimisation, and
  it is worth recognising rather than debugging from scratch.

- **`application_id` is no longer a file-type magic number.** SQLite's convention is that
  `application_id` identifies what kind of file this is — `file(1)` and forensic tooling read it that
  way. It now holds a schema digest that changes with every `Ddl` edit. Nothing in this repo relied
  on it (verified: zero hits before it was claimed), but anything outside that identified a bank by
  its `application_id` no longer can.

- **Found only on the merged tree.** Each lane was green alone; the digest gate's interaction with two
  `Speed=Slow` schema tests appeared only after WP1 and WP6/WP7 were merged together, because the lane
  that wrote them ran `Slow` on a branch that predated WP1. Integration gates re-run on the merged
  result for exactly this reason.

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

**The plan was wrong in two places and this records that rather than quietly restating it.** The
statement count was underestimated by ~25% and opens per operation by 20%.

The third apparent contradiction — two settings reads on every `memory_write` that the plan's
write-path analysis did not account for — **resolved on inspection, and was terminological.**
Pre-WP3 `MemoryAccessGuard` is declared `MemoryAccessGuard(IMemoryStore store)` and makes exactly two
`GetSettingAsync` calls, per-project then global (`MemoryAccessGuard.cs:13,21` at `639284b9`). The
plan filed it as an `IMemoryStore` cost because of the constructor parameter; a settings read is a
settings read whichever port it arrives through. Those two calls are the two reads, and WP3 collapsed
them into one batched `GetSettingsByPrefixAsync`.

**After.** `MemorySchemaDdlStatementCountTests` pins both sides of the gate: **0 `Ddl` statements and
4 total when the digest matches, 39 when it is stale.** So the per-open cost goes 42 → 4 on every
install past its first run. `QueryGuardServiceTests` pins query-guard settings reads at 4 → 1 on the
structural path and 2 → 1 elsewhere, watched red first (`CallCount should be 1 but was 2/2/2/4`).

**The route-table guard, watched red then green (`37a67e53`).** `EndpointGuardTests` enumerates the
server's route table and calls every route unauthenticated: mapping a bare `/falsify-the-guard` with
no `McpTokenGate` entry made the test fail naming the route; reverting made it pass. `McpTokenGate`
flipped from a default-*open* allowlist (`GuardedPaths`) to default-*closed* (`OpenPaths`, holding
only `/observability`), so a forgotten entry now costs a 401 on the new endpoint instead of shipping
it unauthenticated. Still green after the CLI composition-root flip below, alongside every other
route the server maps, including `/settings`.

**The CLI composition-root flip landed, and the end-to-end path is now real.** `AppRunner.RunCliCommand`
binds `ISettingsStore` to a `LazyServerSettingsStore` for every command outside the two-entry write
opt-out (`CliWriteOptOuts`: `encryption`, and `model set` — held back from the server pending §10.3's
progress-shape ruling, not on principle). The store defers acquiring the backend
(`CliSettingsBackend.AcquireAsync`, reusing `BackendLauncher` exactly as the stdio proxy does) to its
first actual call, so a command that never touches settings — `serve` above all, which builds its own
separate server DI graph — never probes or auto-starts anything.

Proven against the real binary, not a fake: `CliContractTests` replays its recorded scenarios through
a real `ai-raccoon` process that cold-starts a real backend for the first settings command and reuses
it for the rest (`ai-raccoon: starting the backend on port {N}` appears exactly once, on the cold
scenario), and adds the two rows that were owed — a settings command against a server that refuses a
tampered token exits distinctly (`SettingsServerRefused`, 17) from one where nothing can be made to
listen within the acquire budget (`SettingsServerUnavailable`, 18). `SqliteSettingsStoreTests`'
cross-process liveness test (WP4-T3) is re-founded on this topology: the writer is still a distinct OS
process from the reader, it just delegates the write to the server now rather than opening the bank
itself — re-verified red-then-green by temporarily routing its reads through the existing
naively-caching decorator (failed `should be "false" but was null`), then reverting. `CliBankWriteTests`
needed a real fix, not a rewrite of its expectations: every settings command now auto-starting a full
server surfaced a genuine race with `BankMaintenanceHostedService`'s unrelated startup checkpoint pass,
which the fix absorbs with a warm-up before any test takes its baseline — not a weakened assertion.
Full `Speed=Fast` (2504), `Speed=Slow` (727) and `Category=bdd` (138) all green on the merged chain.

**After** (`docs/work/perf/2026-08-16-wp5-after-measurement.md`, same counting-decorator method,
same 193 MB backup bank, measured at `c2fd31f0`):

| | Before | After | Delta |
|---|---|---|---|
| bank opens / operation | 4.5 (4 write, 5 search) | **3.5** (3 write, 4 search) | -22% |
| settings reads / operation | 2 write, 2 search | **1 write, 1 search** | -50% |
| statement volume / operation, steady state | 168 write, 210 search | **12 write, 16 search** | **-92/-93%** |

**These "After" numbers describe `c2fd31f0` (WP1-WP7 merged), not the shipped 1.21.0.** ADR-0076
(the model-migration outbox and its `ToolGate` lock) landed on top of this commit and added a check
before every MCP tool call that this measurement never saw. As released, before a follow-up fix:
bank opens/operation are 4 write / 5 search (not 3/4 above) and statement volume is 20 write / 24
search (not 12/16 above); 17/21 after the fix. See this ADR's sibling,
`docs/adr/0076-model-set-is-an-outbox-drained-by-an-on-demand-relay.md` §"Amendment 2026-08-16 —
ToolGate's migration check cost", for the measured derivation. The table above is left as originally
recorded — it was correct for the tree it measured — but must not be read as the current product's
cost without that amendment.

The per-open statement count is unchanged where it should be unchanged (39 Ddl / 42 total on a
digest-stale first open) and exactly as claimed where the gate applies (0 Ddl / 4 total once the
digest matches, re-confirmed against the already-committed `MemorySchemaDdlStatementCountTests`).
Opens-per-operation moved a real but modest 22%, driven by WP3's settings-call batching; the
92-93% reduction in per-operation statement *volume* is where WP1's digest gate actually pays off —
opens got a little less frequent, and each one got an order of magnitude cheaper.

**Wall-clock could not be measured cleanly this session.** Three independent passes disagreed with
each other by 28-93% on write/search medians — a machine load average that climbed from 12.8 to
16.0 over the session, with 45 concurrent `dotnet`/`ai-raccoon` processes running (other lanes'
scratch servers, this worktree's own test runs, the owner's live install), swamped the signal. The
least-contended sample in each pass (the minimum) still lines up with before's minimums
(49-61 ms vs. before's 48-52 ms write; 34-44 ms vs. before's 34-35 ms search), which argues against
a regression, but a minimum is one sample, not a distribution — this is not a wall-clock win claim,
it is an honest "inconclusive, re-run on a quieter machine" finding.

**A new cost this ADR adds, and it was worth quantifying separately: settings CLI auto-start.**
~785 ms cold (no server running, `BackendLauncher` mints a token and binds one), ~245 ms warm
(server already running, CLI process start + one proxied HTTP round trip) — a floor the before tree
never paid at all, because settings commands opened the bank directly, in-process, before this ADR.

*The after-measurement rerun (§8) now exists. The mechanism claim (bank-open/statement cost) is
proven by exact, deterministic counts immune to machine contention; the wall-clock claim is not —
that half of §8's criteria stayed open pending a rerun under less contention.*

**Wall-clock, settled** (`docs/work/perf/2026-08-16-wp5-interleaved-ab.md`): instead of waiting for
a quiet machine — which never fully arrives, and didn't fix the problem above — this pass built
both trees and interleaved them (A B A B …, order alternated, 16 rounds, first round of each arm
discarded as warm-up), pairing each round's B−A difference so machine-load drift (this session's
own load climbed 9.6→20+ mid-run) cancels out of the *difference* even though it doesn't cancel out
of either arm's absolute number. Result is a split, not one number:

| Operation | Paired result (n=15 rounds) | Sign test |
|---|---|---|
| `memory_write` | B faster, median −2.4 ms / mean −11.9 ms, growing to 40-55 ms under this session's higher-load rounds | 12/15 favour B, **p = 0.035** |
| `memory_search` | No reliable difference | 8/15 vs 7/15, **p = 1.000** (a coin flip) |

Writes get a real, small, statistically significant win, consistent with the settled 168→12
statement-volume drop. **Search gets no measurable wall-clock difference despite paying the same
210→16 statement-volume drop** — search medians in this data run 130-300 ms against write's
15-85 ms, so search latency is evidently dominated by something the schema-open gate never touched
(embedding/vector-scoring cost is the visible candidate, not measured directly here). That is
reported as a genuine, useful finding about where search's cost actually lives, not as a weaker
version of the write result. §8's wall-clock criterion is closed by this measurement, not by a
quieter re-run.

## Amendment 2026-08-17 — `repair` was never on the route table, and wrote the bank directly

The owner's own manual audit found `repair reingest --apply` and `repair chunk-index --apply`
writing the bank directly from the CLI process — `CliWriteOptOuts` (§5.3's opt-out list) never named
`repair`, so `AppRunner.RunCliCommand` correctly routed it to the server-backed store for
`ISettingsStore`/`IModelMigrationStore`, but `IMemoryStore` was (and, for every command this
amendment does not touch, still is) bound unconditionally to the direct `SqliteMemoryStore`
(`AppRegistrations.cs`). `repair`'s command classes held that direct store and called its write
methods, so every `--apply` wrote the bank from the CLI process regardless of the route-table intent.
A prior commit (`6c38e663`) made this worse by documenting the gap as expected behaviour in three
user-facing places — the CLI help text, the runtime `--apply` output, and a doc comment — each saying
some version of "with no server running, nothing drains it, run `memory_embed_pending` by hand,"
enshrining the violation as a feature rather than flagging it as one.

**Fixed by giving `repair` the same shape `settings`/`model set` already have, generalised one step
further.** `model set` (ADR-0076) proved the outbox pattern for "the CLI records a request, a
maintenance job drains it" — but `model set` never had a read half worth moving, so that pattern
alone under-specifies what a command with a genuine read (a report) needs. `repair` has both:

- **`IRepairStore.ReportReingestAsync`/`ReportChunkIndexAsync`** — a synchronous, read-only,
  server-computed answer (mirrors `GET /settings`): the CLI asks a question, gets numbers back, never
  opens the bank itself, whether or not `--apply` was passed. The scan that used to run inside the
  CLI process now runs inside the HTTP request handler on the server.
- **`IRepairStore.RequestRepairAsync`** — an outbox row (`repair_requests`, keyed by kind, mirroring
  `model_migration`'s id=1 shape but keyed since two repair kinds are independent) the CLI commits
  through the server on `--apply`; `ReingestRepairJob`/`ChunkIndexRepairJob` — on-demand
  (`Interval` is null, exactly like `ModelMigrationJob`) — apply it within the maintenance loop's next
  ~15s poll.

Both halves go through the same acquired connection `ServerSettingsStore`/`LazyServerSettingsStore`
already hold for settings and model migration — a third interface on the same class, not a second
`BackendLauncher` acquire path. `/repair` is a new route on the same token-guarded host as `/settings`,
default-closed by `McpTokenGate` like every route that isn't explicitly opened.

**The "explicit-only, never unattended" guard survives, restated correctly.** `ReingestRepair`/
`ChunkIndexRepair` themselves still never implement `IMaintenanceJob` — `ReingestRepairJob`/
`ChunkIndexRepairJob` wrap them instead, on the maintenance job list but gated on `HasWorkAsync`
reading the outbox row, never on a clock. A job that only ever runs because a human explicitly
requested it via `--apply` is not "unattended" in the sense GH #371 cared about; the two
`*DoesNotAutoStartTests` suites now assert that restated invariant (`Interval` is null) rather than
"never appears on the job list," which was never actually the requirement.

**The three enshrined strings are fixed to say what is now true**: the CLI help text, the `--apply`
output, and the `ReingestRepair`/`ChunkIndexRepair` doc comments no longer describe a "no server
running, nothing drains it" branch — that branch is unreachable now, because `apply: true` is only
ever reached inside the server process (`ReingestRepairJob`, gated on the outbox row).

**No residual violation from `repair` remains.** `CliWriteOptOuts` still names only `encryption`
(the bootstrap path), and that is now accurate for `repair` too — the CLI process opens the bank for
`repair` never, not even to read.

**Two other CLI-writes-the-bank violations were found in the same audit and are deliberately not
fixed here** — queued as follow-up work, not silently left undocumented:

- `settings maintenance list` (`MaintenanceCommands.ReadStatsAsync`) opens the bank directly to read
  `page_size`/`page_count`/`freelist_count` and issues `PRAGMA wal_checkpoint(PASSIVE)`, unconditionally,
  on every invocation — no `--apply` gate. This is a pure "ask a question, get numbers back" case, the
  same shape `IRepairStore`'s report half already demonstrates; the fix is expected to add a small
  `IMaintenanceStatsStore` alongside `IRepairStore` on the same acquired connection, not a new
  transport.
- `extract prune --apply` deletes via `IPromotionQueueStore` (`SqlitePromotionQueueStore.cs:262`),
  the same never-swapped-for-the-CLI-graph pattern `repair` had.

Until those land, `CliWriteOptOuts`'s doc comment claim that `encryption` is the *only* exception
should be read as "the only exception among the commands this amendment and ADR-0076 have addressed,"
not as a claim that every CLI command is clean — the gate this ADR's Consequences section describes
(a route-table guard asserting zero bank writes from the CLI process) does not yet cover
`maintenance list` or `extract prune`.

**Both landed** — see "Amendment 2026-08-17 (continued)" below; `CliWriteOptOuts`'s claim is now
true without qualification.

**Evidence**: `RepairEndpointTests` (route-table + auth), `SqliteRepairStoreTests`/
`ReingestRepairJobTests`/`ChunkIndexRepairJobTests` (server-side read and apply, red-then-green),
`AppRunnerSettingsRoutingTests.ARepairCommand_UsesTheInjectedAcquireFunction` (watched red by
temporarily removing the `IRepairStore` DI override — reproduces the original defect exactly: the
fake acquire function is never called, `acquireCalls` stays 0), `CliWriteOptOutsTests` (repair is
`WritesDirectly: false`), and `RepairCommandsTests` (the CLI's own text no longer contains
`memory_embed_pending`/"no server running", watched red against the pre-fix string).

## Amendment 2026-08-17 (continued) — `extract prune` and `settings maintenance list` close the two remaining violations

The two follow-ups the previous amendment queued and deliberately left unfixed are fixed now, the
same shape as `repair`:

- **`extract prune --apply`** routes through a new `IPromotionQueuePruneStore`
  (`ReportPruneOrphansAsync`/`RequestPruneOrphansAsync`), split out of `IPromotionQueueStore` the
  same way `IRepairStore` was split out of `IMemoryStore` — the CLI needs only the report/request
  shape, not the whole promotion-queue surface most of which stays server-internal (eviction,
  claims, discards). The write is a new singleton outbox row, `promotion_queue_prune_requests`
  (`id = 1`, mirroring `model_migration`'s shape rather than `repair_requests`'s per-kind shape,
  since there is only one prune operation, not independent kinds) — drained by a new on-demand
  `PromotionQueuePruneJob` (`Interval` is null, `HasWorkAsync` reads the outbox row, same as
  `ReingestRepairJob`/`ChunkIndexRepairJob`).
- **`settings maintenance list`** routes through a new `IMaintenanceStatsStore.GetStatsAsync`,
  exactly the shape §"What was rejected" ruled out for reads in general and this ADR's own
  Consequences section explicitly still permits for a genuinely local question — except this read
  was never local: it opened the bank via `MemorySchema.EnsureAsync`, which can write on a
  digest-stale bank, so the fix moves it server-side rather than leaving it as a CLI-side read. No
  outbox: the read has no `--apply` gate to begin with, so there is nothing to defer.

Both new server-side stores are additional interfaces on `ServerSettingsStore`/
`LazyServerSettingsStore`, over the same acquired connection every other control-plane resource
already shares — no second `BackendLauncher`, no new credential.

**`CliWriteOptOuts`'s doc comment claim is now literally true, not just true for the commands
addressed so far**: `encryption` is the only CLI command that opens the bank directly. The two
routes named as open in the previous amendment are closed.

**Watched red before the fix, not just asserted**: `CliBankWriteTests.ApplyCommand_OnlyCommitsAnOutboxRequest_NeverTheDomainTableDirectly`,
run against `ExtractCommands` reverted to its pre-fix direct-store shape with a seeded orphaned
`promotion_queue` row, failed with `changed` = `["promotion_queue"]` instead of the expected
`["promotion_queue_prune_requests"]` — the exact violation this amendment closes, reproduced by a
real out-of-process `ai-raccoon` invocation rather than inferred. Reverted to the fixed shape
afterward; the same theory row is green there. `repair chunk-index --apply`/`repair reingest
--apply` were added to the same theory as coverage completion, not a red/green pair, since they
were already fixed by the previous amendment.

**The gate that let `repair` slip through in the first place is now a gate that can fail again.**
`CliBankWriteTests.ReadCommands()` previously listed `extract prune`/`repair chunk-index`/`repair
reingest` bare-only — dry run never writes by construction, so those rows could only ever pass, and
nothing forced a matching `--apply` row to exist. `SettingsCommandTreeTests.ApplyLeaves_MatchCliBankWriteTestsCoverage`
now derives every `--apply` leaf from `CliCommandTree.BuildFullRootCommand()` and asserts it against
`CliBankWriteTests.ApplyCommandPaths`, so a new `--apply` leaf added without a matching write-mode
row fails immediately rather than silently inheriting the same blind spot.

**Evidence**: `PromotionQueuePruneEndpointTests`/`MaintenanceStatsEndpointTests` (route-table +
auth), `SqlitePromotionQueueStoreOutboxTests`/`SqliteMaintenanceStatsStoreTests`/
`PromotionQueuePruneJobTests` (server-side read/apply), `ExtractPruneCommandTests` (the CLI's own
text — "queued for the server to remove", "request committed", "maintenance poll" — mirroring
`RepairCommandsTests`), and the red-then-green `CliBankWriteTests` run described above.
