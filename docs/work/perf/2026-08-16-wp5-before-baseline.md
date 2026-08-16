# WP5 before-baseline: bank-open cost, unoptimised tree

Status: DONE (before only — no optimisation implemented)
Date: 2026-08-16
Branch: `perf/wp5-before-baseline`
Measured at: `639284b9` (main), verified functionally identical to the plan's named baseline
commit `7cfaefca` — `git diff --stat 7cfaefca..639284b9` touches only
`docs/work/checklist/2026-08-16-rerun-1-20-0.json` (docs-only, nothing under `src/`).

Companion to `docs/plans/2026-08-16-bank-open-cost-implementation.md` §8 (WP5). That plan file
does not exist on this branch's history — it lives on `perf/single-writer-integration` and
downstream `task/wp*` branches, which branched from a different point than `main`. This report is
written as a sibling file rather than an edit to that plan, so the owner can reconcile it during
integration without a cross-branch merge on a document three other lanes may also be touching.

## Headline

| Metric | Plan's claim | This measurement | Verdict |
|---|---|---|---|
| Bank opens / operation (mixed) | 3.60 (5652/1572, trace-derived) | **4.5** (exact count, steady state) | **Plan undercounts** |
| Ddl-block statements (unconditional, per open) | "~30" / "roughly thirty-two" | **39** (empirical, `sqlite3_trace`) | **Plan undercounts** |
| `EnsureAsync` total statements on an already-current bank | not stated as a total | **42** (39 Ddl + 1 version read + 2 repair probes) | new data point |
| Settings reads / operation | implied ~2 (query-guard, "typical Clean search") | **2 / write, 2 / search** (steady state) | confirms — and settings reads = settings-caused opens 1:1, as claimed |

**The plan's fast-path arithmetic is directionally right but numerically low.** Both of its two
concrete counts (the Ddl block size, and opens per operation) undercount what I measured. Neither
error changes WP1's case for existing — if anything a 39-statement block gated to 4 is a *better*
story than a 32-statement one — but the plan's own instruction was "I want the ~32 verified, not
assumed," and it is not 32.

## Method

### Tree and build

- Branch `perf/wp5-before-baseline` off `main` (`639284b9`), which this report treats as
  interchangeable with `7cfaefca` per the diff check above.
- `dotnet build -c Release` for the standalone HTTP-server measurements (wall-clock latency).
- The bank-open/settings-read/Ddl-statement counts were captured through `dotnet test` (Debug
  configuration) rather than a standalone Release process — see "Deviations from §8" below for why
  this doesn't bias those particular numbers.
- No server was already running against these scratch data roots; the owner's real `ai-raccoon
  serve --restart` (pid 8657, live production bank) was left untouched throughout and its bank's
  checksum was re-verified unchanged at the end of this session
  (`8086c535d13ffc1413bb88d341548af7`).

### Bank

Copied (never written to) from `~/ai-raccoon-backups/memory-20260816-133916.db` (202,543,104
bytes, `integrity_check ok` per the task brief). The header confirms it is unencrypted
(`SQLite format 3`) and already at `PRAGMA user_version = 10` (`CurrentVersion`) with 90
`sqlite_master` rows — a real, already-migrated production bank, not synthetic data. Every
measurement pass used a **fresh copy** in a scratch temp directory (never the original, never a
previously-mutated copy), so writes in one pass never contaminate the starting state of another.

### Instrumentation: a counting decorator, not `dotnet-trace`

I did not use `dotnet-trace`/EventPipe for the count-based metrics (bank opens, settings reads).
Instead: a test-only decorator around `ISqliteConnectionFactory`
(`sp.GetRequiredService<SqliteConnectionFactory>()` wrapped, then registered back over
`ISqliteConnectionFactory`/`ISettingsStore` via `WebApplicationFactory`'s
`configureAdditionalServices` seam — the same seam `McpServerFactory` already exposes for the
existing E2E suite) that increments an `Interlocked` counter on every `OpenBankAsync` call. This
gives an **exact, deterministic** count per MCP tool call — no EventPipe sampling, no inclusive/
exclusive-time ambiguity, no reconstruction from a speedscope call tree. It was **test-only**: the
decorator lived in a throwaway test file
(`tests/AiRaccoon.Tests/E2E/Wp5BeforeBaselineProbe.cs`, deleted after the numbers were captured —
it hardcoded a machine-local scratch path and was never meant to be permanent), and no production
file was touched. `SqliteSettingsStore` was given its own separate counting factory instance so
"opens caused by settings reads" and "opens caused by everything else" are exact, disjoint buckets
that sum to the total — there is no unattributed residue in this method, unlike the original
trace's ~29%. What I *didn't* further decompose is the "everything else" bucket itself (content
CRUD, `MemoryAccessGuard`'s `IMemoryStore`-based opens, and any `IMemoryStore`-based settings reads
for access-mode) — `IMemoryStore` has 26 members and a full passthrough decorator wasn't worth
building for this pass.

The Ddl-statement count used the same instrument at the SQLite-engine level instead of the
ADO/DI level: `SQLitePCL.raw.sqlite3_trace` hooked directly onto a real `SqliteConnection.Handle`,
which fires once per top-level statement and correctly treats a `CREATE TRIGGER ... BEGIN ... END`
body as one statement (verified against a synthetic 4-statement block with one embedded-semicolon
trigger before trusting it on the real `Ddl`). This is now a committed regression/characterization
test — see "What's committed" below.

Wall-clock latency used neither: a small standalone console client
(`ModelContextProtocol.Client.McpClient` over `HttpClientTransport`) drove a **real, separate OS
process** (`dotnet .../AiRaccoon.dll ... serve --port 0`, Release build) over real loopback HTTP,
timing each `CallToolAsync` round trip client-side with `Stopwatch`. This is the one number in this
report that is genuinely apples-to-apples with what a real MCP client experiences.

### Deviations from the plan's §8 harness, and why

- **Bank-open/settings-read counts came from a Debug `dotnet test` run (in-process
  `WebApplicationFactory`/`TestServer`), not a Release standalone process.** `MemorySchema.cs` and
  `SqliteConnectionFactory.cs` have no `#if DEBUG`/configuration-conditional branches — Debug vs.
  Release changes JIT optimisation and thus timing, not which statements execute or how many times
  `OpenBankAsync` is called. Counts are safe to trust from a Debug run; only timing numbers from
  that pass would not be. I did not use any timing number from that pass.
- **Warm-up was 5 writes + 5 searches for the open-count pass, then bumped to inspect
  distribution stability, not the specified 50+50, and no wait for a `MetricsFlusher` tick.** The
  open-count pass ran 25 writes + 25 searches per type (60 total ops including warm-up); 22/25
  writes and 19/25 searches landed on the same exact count, so the steady state is well
  characterized despite the shorter warm-up. The wall-clock latency passes *did* use 50+50 warm-up
  (matching §8 criterion 3) but did not wait out a 30 s flush tick before collecting.
- **No `dotnet-trace`, so no speedscope call-tree analysis, so no "`EnsureAsync` inclusive share of
  `InitializeAsync`" secondary metric (§8 criterion 5).** That ratio is really a lever for judging
  whether WP1 *landed* later, not a "before" fact worth a trace pass on its own — it wasn't on my
  task's list, and I didn't attempt it.
- **No settings-command latency regression check (§8 criterion 6).** That criterion is about
  WP7's server-routed settings CLI, which doesn't exist yet on this tree; there's nothing to
  measure for a "before" baseline.
- **One A-A pair for wall-clock latency, not for the open-count/statement-count numbers.** The
  latter are deterministic given the code path (no branching on request content that would change
  which queries run), so the within-run distribution (22/25, 19/25 at the same value) already
  functions as the noise characterization; a second full pass would be measuring the same
  deterministic control flow again. Wall-clock timing is not deterministic, so it got the real A-A
  treatment.

## Results

### 1. Bank opens per operation (exact, decorator-counted; 25 writes + 25 searches, steady state)

| | Steady-state value (mode/median) | Distribution |
|---|---|---|
| `memory_write` | **4** (2 general + 2 settings) | 22/25 samples exactly 4; 3 outliers up to 19 |
| `memory_search` | **5** (3 general + 2 settings) | 19/25 samples exactly 5; 6 outliers up to 20 |
| Combined average (mixed workload, matching the plan's methodology) | **(4+5)/2 = 4.5** | vs. plan's 3.60 |

The outliers are not attributable to the call itself — they show elevated *settings-only* opens (up
to 16 in one sample) while the general bucket stays flat at 2-4, which points at a background
hosted service (`BankMaintenanceHostedService`, `IdleWatchdog`, or the deferred-embedding pipeline)
reading several settings keys on its own timer and landing inside whichever call happened to be
in flight. I did not chase down which one; the steady-state median is the number to trust for "what
does one `memory_write`/`memory_search` cost," and it's what the table above reports.

**Per-open-multiplied-out cost**: at 4 opens/write × 42 statements/open (see §2) = **168 SQL
statements** just for schema-ensure overhead per write, before any content SQL runs; **210** per
search.

### 2. `EnsureAsync` statement count on an already-current bank (empirical, `sqlite3_trace`)

| | Count |
|---|---|
| `PRAGMA user_version` read | 1 |
| Ddl block (unconditional `ExecuteNonQueryAsync`) | **39** |
| Legacy `watch.scope.*` probe | 1 |
| Trigger scope-guard probe | 1 |
| **Total** | **42** |

39 matches a manual recount of the top-level `CREATE TABLE`/`CREATE VIRTUAL TABLE`/`CREATE
TRIGGER`/`CREATE INDEX` statements in the `Ddl` string in `MemorySchema.cs` — the plan's "~30"/
"roughly thirty-two" undercounts by about a fifth. Committed as
`tests/AiRaccoon.Tests/Integration/MemorySchemaDdlStatementCountTests.cs`.

### 3. Settings reads per operation

Confirmed 1:1 with settings-caused opens (as the plan claims): every `SqliteSettingsStore` method
opens its own bank connection, so "settings reads" and "settings-caused opens" are the same number
today. Steady state: **2 settings reads on `memory_write`**, **2 settings reads on
`memory_search`** (see table in §1 — this is the "settings" half of the general/settings split).

### 4. Wall-clock latency (real out-of-process HTTP MCP server, Release build, real 193 MB bank)

Two independent runs (A, then an A-A repeat) — fresh bank copy each, 50+50 warm-up (not measured),
then 300 writes + 300 searches sequentially through one client, single connection, no concurrency.

| Run | Op | n | median (ms) | p95 (ms) | min (ms) | max (ms) |
|---|---|---|---|---|---|---|
| A | write | 300 | 56.50 | 92.05 | 47.91 | 323.58 |
| A | search | 300 | 41.99 | 68.47 | 34.92 | 149.72 |
| A-A | write | 300 | 60.26 | 83.50 | 51.88 | 173.37 |
| A-A | search | 300 | 44.04 | 61.16 | 34.37 | 74.46 |

**Noise floor (before-vs-before, identical conditions):** write median moved 3.76 ms (6.7%), write
p95 moved 8.55 ms (9.3%); search median moved 2.05 ms (4.9%), search p95 moved 7.31 ms (10.7%). Any
future before/after delta smaller than roughly this band is not a measured change.

No EventPipe/`dotnet-trace` involvement in these numbers — they are the actual client-observed
round trip, real loopback HTTP, real JSON-RPC framing, real process scheduling on a machine that
had other agent processes running concurrently (a real-world confound, not simulated).

## What's committed on this branch

- `tests/AiRaccoon.Tests/Integration/MemorySchemaDdlStatementCountTests.cs` — a durable
  characterization test pinning the 39/42 statement counts, using `sqlite3_trace` on a synthetic
  in-memory bank (no dependency on the real backup, safe for CI). This is a genuine regression net
  for WP1's later digest-gating work, in the same spirit as the plan's own WP1-T1.
- This report.
- Nothing else. The bank-open/settings-read counting decorator and the wall-clock driver were
  throwaway (a test-only harness deleted after use, and a standalone scratch console client) —
  neither touched production code, and neither is reproducible without the machine-local backup
  path, so neither belongs in the tree.

## Contradictions with the plan, stated plainly

1. **"Roughly thirty-two" Ddl statements is wrong — it's 39.** Verified empirically twice (SQLite
   engine trace, and independent manual recount of the DDL source). This doesn't change WP1's
   recommendation, but the plan explicitly asked for this number to be checked rather than assumed,
   and it needed correcting.
2. **3.60 opens/operation undercounts a clean, exact measurement (4.5).** The two numbers aren't
   measuring quite the same thing (mine is an isolated per-call exact count on a fresh bank via
   in-process hosting; the original is a live-server 28 s trace under EventPipe sampling with
   ~29% unattributed), so this isn't a strict apples-to-apples refutation — but it's evidence in
   the same direction as finding #1: this plan's arithmetic runs low.
3. **Settings reads are exactly 2 per write, not just "up to 2 opens per write" from
   `MemoryAccessGuard`** as the plan's Amplifier 2 describes — that 2-per-write settings cost is a
   *separate* thing from `MemoryAccessGuard`'s (which the plan says is `IMemoryStore`-based, not
   `ISettingsStore`-based, and therefore lands in my undecomposed "general" bucket, not in the
   settings bucket at all). I did not identify which settings keys are read on the write path — the
   plan's own settings-read analysis (§1, Amplifier 2) is scoped to the search path
   (`QueryGuardService`) and doesn't name a write-side settings consumer, so this is worth a look
   before WP2's batching lands, in case there's a write-path settings reader the plan's trace
   reading missed entirely.

## Branch / commits

- `perf/wp5-before-baseline`, pushed to `origin/perf/wp5-before-baseline`.
- Not merged to main; integration is the owner's.
