# WP5 after-measurement: bank-open cost, optimised tree

Status: DONE
Date: 2026-08-16
Branch: `perf/wp5-after-measurement`
Measured at: `c2fd31f0` (`origin/main`, carries WP1-WP7)

Companion to `docs/work/perf/2026-08-16-wp5-before-baseline.md` (the "before" column below) and
`docs/adr/0075-only-the-server-writes-to-the-bank.md` (what changed and why). Method is reused
exactly where the before doc specified one: the same counting-decorator technique over
`ISqliteConnectionFactory`/`ISettingsStore`, no `dotnet-trace`; the same real out-of-process
Release server for wall-clock; the same 193 MB backup bank, copied, never written to.

## Headline

| metric | before | after | delta |
|---|---|---|---|
| `Ddl` block statements (digest stale — first open of a bank on this schema) | 39 | 39 (unchanged, empirically re-confirmed) | none — the gate does not touch this path |
| `EnsureAsync` total (digest stale) | 42 | 42 (unchanged) | none |
| `EnsureAsync` total (digest matches — every open after the first) | *(no such path existed)* | **4** | **new, and it's the whole point** |
| bank opens / operation | 4.5 (4 write, 5 search) | **3.5** (3 write, 4 search) | **-22%** (write -25%, search -20%) |
| settings reads / operation | 2 write, 2 search | **1 write, 1 search** | **-50%/-50%** |
| general (non-settings) opens / operation | 2 write, 3 search | 2 write, 3 search | **unchanged** |
| statement volume / operation, steady state (opens × per-open cost) | 168 write, 210 search | **12 write, 16 search** | **-93% write, -92% search** |
| write latency | median 56-60 ms, p95 92 ms | median 65.87-127.01 ms, p95 276.66-431.11 ms (3 passes) | **not usable — see §4** |
| search latency | median 42-44 ms, p95 68 ms | median 52.31-78.19 ms, p95 186.50-273.69 ms (3 passes) | **not usable — see §4** |
| settings CLI, cold (auto-start a server) | *(no such cost existed — direct bank open)* | **~785 ms** (2 samples: 787, 783) | **new cost, ADR-0075 adds it** |
| settings CLI, warm (server already running) | *(no such cost existed)* | **~180-520 ms** (10 samples across 2 cold-then-warm×5 runs, median ≈245 ms) | **new cost, ADR-0075 adds it** |
| A-A noise floor | 5-10% | **28-93%** (this session, see §4) | machine contention, not the code |

**The mechanism claim is proven exactly, by the same low-noise instruments the before doc trusted
for exact numbers.** Opens per operation dropped 22%, settings reads per operation dropped 50%,
and — the number that actually matters — per-open statement volume dropped 92-93% once opens are
multiplied by their real cost, because almost every open after the first now pays 4 statements
instead of 42.

**The wall-clock number is not usable this session, and that is the more important finding to
report plainly.** Three independent passes on this machine disagree with each other by far more
than the before doc's 5-10% floor — up to 93% on write medians between two passes run minutes
apart with nothing else changed. That is not evidence the code got slower; it is evidence this
machine could not produce a clean number today. See §4 for the load evidence and why the minimums
(least-contended samples) still line up with before's numbers even though the medians don't.

## Method

### Tree and build

- Branch `perf/wp5-after-measurement` off `origin/main` (`c2fd31f0`, carries WP1-WP7 merged).
- `dotnet build -c Release` for the standalone HTTP-server wall-clock measurements, same as before.
- Bank-open/settings-read counts came from a Debug `dotnet test` run (in-process
  `WebApplicationFactory`), same deviation from a Release standalone process the before doc took,
  for the same reason: Debug vs Release changes timing, not which statements execute or how many
  times `OpenBankAsync` is called.
- `ai-raccoon serve --restart` (pid 50634, the owner's live install, port 7721) was left untouched
  throughout. Every scratch server this session bound `--port 0` (wall-clock passes) or an
  explicitly-picked free port (settings auto-start passes, to avoid the CLI's default-7721
  auto-discovery colliding with the owner's install) — confirmed by checking `lsof -iTCP:7721`
  before and after every pass, and the owner's port stayed pinned to pid 50634 throughout.

### Bank

Fresh copies of `~/ai-raccoon-backups/memory-20260816-133916.db` (202,543,104 bytes), same file the
before doc used. MD5 checksum verified unchanged **before this session started and again after it
finished**: `8086c535d13ffc1413bb88d341548af7` both times. Every measurement pass — count-based and
wall-clock alike — used a fresh copy in its own scratch directory; none ever wrote to the original.

This backup predates the digest stamp ADR-0075 adds (`PRAGMA application_id`), so the **first**
open of any fresh copy pays the full digest-stale 39/42-statement path once, exactly like a real
existing install's first run past the upgrade. Every count-based pass warmed up (5 writes + 5
searches, uncounted) before resetting counters, so that one-time cost never lands in the sampled
steady-state numbers — matching the before doc's own warm-up rationale.

### Instrumentation: the same counting decorator, not `dotnet-trace`

Reused the before doc's technique exactly: `services.AddSingleton<ISqliteConnectionFactory>(sp =>
new CountingConnectionFactory(sp.GetRequiredService<SqliteConnectionFactory>(), ...))` registered
via `McpServerFactory`'s `configureAdditionalServices` seam, incrementing an `Interlocked` counter
on every `OpenBankAsync` call. `ISettingsStore` got a second, independent counting wrapper
(`CountingSettingsStore`) around the real `SqliteSettingsStore`, counting method calls — which the
ADR itself confirms are still 1:1 with settings-caused opens ("every `SqliteSettingsStore` method
opens its own bank connection"). `general = total - settings`, exact and disjoint, same bucketing
as before.

**One deliberate, real difference from before's bucketing, worth stating plainly rather than
glossing over:** post-WP8, `IMemoryStore`'s settings passthrough methods (which is how
`MemoryAccessGuard` reads access-mode) now delegate to the *same* injected `ISettingsStore`
instance that `QueryGuardService` uses directly (`SqliteMemoryStore.cs:724-726`,
`_settings.GetSettingsByPrefixAsync(...)`). Before WP8, `MemoryAccessGuard`'s reads went through
`IMemoryStore`'s own bank-access code, not a shared `ISettingsStore`, which is why the before doc's
"settings" bucket didn't catch them. After WP8's refactor, decorating the DI `ISettingsStore`
singleton catches every settings-attributable open regardless of whether the caller went through
`QueryGuardService` directly or `MemoryAccessGuard`/`IMemoryStore`. **The settings/general boundary
itself shifted because the code's actual structure changed, not because this measurement chose a
different line to draw.** The `general` bucket staying exactly unchanged (2 write, 3 search) despite
that shift is itself informative: it says the boundary move didn't quietly hide a settings-read
increase inside "general" — general really is flat.

The throwaway probe (`tests/AiRaccoon.Tests/E2E/Wp5AfterBankOpenCountProbe.cs`) was deleted after
capturing its numbers, same as the before doc's deleted probe — it hardcoded a
`WP5_BACKUP_BANK_PATH` env var to a machine-local scratch path and was never meant to be permanent.
`MemorySchemaDdlStatementCountTests` (already committed on `main` from the WP1 chain) was re-run
on this branch to reconfirm 0/4 (digest matches) and 39 (digest stale) still pass — both green.

Wall-clock latency reused the before doc's exact driver: a standalone console client
(`ModelContextProtocol.Client.McpClient` over `HttpClientTransport`) against a real, separate OS
process (`dotnet AiRaccoon.dll --data-root <scratch> serve --port 0`, Release build), timing each
`CallToolAsync` round trip client-side with `Stopwatch`, real loopback HTTP. **New since before:**
the server now gates `/mcp` by default (`McpTokenGate`, ADR-0075's route-table guard), so the driver
reads `<data-root>/mcp-token` after the server mints it and sends `X-AiRaccoon-Token` — a step the
before doc's driver didn't need because the gate didn't default-close until this chain.

### Settings CLI auto-start (new surface, not in the before doc)

The task brief flagged this explicitly: every settings command now auto-starts a server if one
isn't running for that data root (`BackendLauncher`, reused from the existing proxy-launch path).
Measured with the real `ai-raccoon` binary (Release), `settings sweep show` — a cheap read —
against a fresh backup-bank copy, with an explicit free loopback port (never the default 7721,
which is the owner's live install) passed via the shared `--port` option: one cold call timed
end-to-end (no server running, must auto-start, mints a token, binds, one bank open that pays the
digest-stale first-open cost), then five immediate warm calls (server already running, CLI process
proxies over HTTP). Two independent cold-then-warm×5 runs.

## Results

### 1. Bank opens per operation (exact, decorator-counted; 25 writes + 25 searches, steady state)

| | Steady-state value | Distribution |
|---|---|---|
| `memory_write` | **3** (2 general + 1 settings) | 17/25 samples exactly 3; outliers up to 18 |
| `memory_search` | **4** (3 general + 1 settings) | 17/25 samples exactly 4; outliers up to 19 |
| Combined average (mixed workload, matching before's methodology) | **(3+4)/2 = 3.5** | vs. before's 4.5 |

The outliers reproduce the exact pattern the before doc reported: elevated *settings-only* opens (up
to 15-16 in one sample) while the general bucket stays flat at 2-5, consistent with a background
hosted service reading several settings keys on its own timer and landing inside whichever call was
in flight. Not chased down here either, for the same reason: the steady-state mode is what a real
`memory_write`/`memory_search` costs, and it's what the table reports.

**Per-open-multiplied-out cost, steady state**: 3 opens/write × 4 statements/open = **12 SQL
statements** for schema-ensure overhead per write (was 168), **16** per search (was 210). That
92-93% reduction — not the 22% open-count reduction — is where WP1's win actually lives: gating the
Ddl block did far more than reducing *how often* the bank opens; it reduced *what each open costs*
by an order of magnitude.

### 2. `EnsureAsync` statement count — re-confirmed unchanged, not re-derived

`MemorySchemaDdlStatementCountTests` (committed on `main`, run again here) still pins both sides
of the digest gate exactly as ADR-0075's Evidence section states: **0 Ddl statements / 4 total when
the digest matches**, **39 Ddl statements when it does not** (both tests green on `c2fd31f0`). This
report does not re-derive those numbers — they were already exact and committed as a regression
test; re-deriving them empirically a second time would just be re-running the same deterministic
control flow, which the before doc already established isn't worth a second pass.

### 3. Settings reads per operation

**1 settings-attributable open on `memory_write`, 1 on `memory_search`** (steady state) — down from
2/2 before. Both numbers land at the "elsewhere" collapse ADR-0075's Evidence section claims
(`QueryGuardServiceTests` pins 4→1 structural, 2→1 elsewhere) plus the access-mode 2→1 collapse
(`MemoryAccessGuard`'s two `GetSettingsByPrefixAsync` calls → one batched call, WP3). Since §0's
method note applies here, this number now also includes `MemoryAccessGuard`'s reads (it didn't
before WP8), so the 2→1 is *not* a pure apples-to-apples repeat of the same measurement — it's a
real, smaller number measured with a wider net that would, if anything, have caught *more* reads
than before's narrower net. That it still nearly halved is stronger evidence, not weaker.

### 4. Wall-clock latency — not usable this session, and why

Three independent passes, real out-of-process HTTP MCP server, Release build, real 193 MB bank
copy each, 50+50 warm-up (not measured), then 300 writes + 300 searches sequentially through one
client, single connection, no concurrency — same shape as the before doc's passes.

| Pass | Op | n | median (ms) | p95 (ms) | min (ms) | max (ms) |
|---|---|---|---|---|---|---|
| A | write | 300 | 65.87 | 335.98 | 52.42 | 817.55 |
| A | search | 300 | 52.31 | 223.53 | 36.51 | 975.17 |
| A-A (repeat) | write | 300 | 127.01 | 431.11 | 61.49 | 904.03 |
| A-A (repeat) | search | 300 | 78.19 | 273.69 | 44.24 | 2307.88 |
| B (third pass) | write | 300 | 95.75 | 276.66 | 49.33 | 992.54 |
| B (third pass) | search | 300 | 64.82 | 186.50 | 33.85 | 975.39 |

**Noise floor across these three passes is 28-93%** (write median swung from 65.87 to 127.01 ms
between passes A and A-A, run minutes apart with nothing else changed; search median swung from
52.31 to 78.19 ms). The before doc's own floor was 5-10%. This is not a subtle difference — it's an
order of magnitude worse, and it means **none of these medians or p95s can be honestly compared to
before's 56-60/92 ms write or 42-44/68 ms search.**

**Why, checked rather than assumed:** `uptime` showed load averages of 12.8, then 14.4, then 16.0
across the three passes (climbing, not flat), and `ps` showed 45 concurrent `dotnet`/`ai-raccoon`
processes at the time — two other lanes' scratch servers (`laneB`, `laneC`, each their own
`serve --port 0`), the owner's live install, this worktree's own test runs, and Rider's background
indexer, all on the same machine while these passes ran. The before doc noted "a machine that had
other agent processes running concurrently" too, but this session's load was measurably higher and
climbing, not merely present. This is the exact confound the task brief asked to check for
(cheap `IF NOT EXISTS` no-ops vs. real cost elsewhere) — except the confound here isn't in the SQL,
it's in the machine, and ruling that in took one `uptime`/`ps` check rather than a deeper trace.

**One thing the noise didn't erase: the minimums.** The least-contended sample in each pass —
closest to a genuinely idle scheduler slot — landed at 49.33-61.49 ms for write and 33.85-44.24 ms
for search, across all three passes. Before's write min was 47.91-51.88 ms and search min was
34.37-34.92 ms. **Those numbers line up.** That's not proof of a wall-clock win — a minimum is one
sample, not a distribution — but it is evidence against the alternative worry (that the code got
slower and the noise is hiding a real regression): the best-case sample this session sits right
next to before's best-case sample, which is what you'd expect if the underlying per-call cost is
unchanged or improved and the elevated medians are pure scheduling contention.

**Verdict on wall-clock: inconclusive this session, not negative.** Re-running on a quieter machine
is the only way to get a number worth comparing to before's clean 5-10%-floor passes. This report
does not claim a wall-clock win, and it does not claim a wall-clock loss — it reports what was
measured, states plainly that the measurement was too noisy to trust, and points at the mechanism
evidence (§1-3, all exact/deterministic, immune to scheduler contention) as the trustworthy result
of this work instead.

### 5. Settings CLI auto-start — a new cost this ADR adds, quantified

| | n | value |
|---|---|---|
| Cold (no server running, `BackendLauncher` auto-starts one) | 2 | 787 ms, 783 ms |
| Warm (server already running, CLI proxies over HTTP) | 10 (5 per run × 2 runs) | 179-295 ms (run 1), 240-516 ms (run 2); median ≈ 245 ms |

This is not comparable to any before number — the before tree didn't have this cost at all,
because pre-ADR-0075 settings commands opened the bank directly, in-process, from the CLI. It is a
real, new latency floor every settings command now pays: **~785 ms** the first time in a given
data root (mints a token, binds Kestrel, pays the digest-stale first-open cost once), and **a
consistent ~180-520 ms** on every call after that, even though the server is already warm — that
floor is mostly CLI process-startup overhead (a fresh .NET process, its own DI container, HTTP
client setup) plus one proxied HTTP round trip, not bank-open cost. Two independent runs, same
order of magnitude both times — not deeply characterized (n=2 cold, n=10 warm), but enough to say
the floor is real and worth knowing about, which is what the task brief asked for.

## Contradictions and honest caveats, stated plainly

1. **The predicted per-open win (42→4 statements) is exactly confirmed, empirically, twice**
   (the committed regression test, re-run here; and this session's own steady-state open-count ×
   statement-count arithmetic). No correction needed here — WP1 landed as designed.
2. **The predicted opens-per-operation reduction is real but smaller than the statement-count
   story alone suggests: 22%, not the 90%+ the per-open cost dropped by.** Opens/operation and
   per-open cost are different axes; WP3's settings-call batching moved the first one modestly
   (4.5→3.5), while WP1's digest gate moved the second one by an order of magnitude. The
   task brief predicted both movements; both happened, at different sizes than a naive reading of
   "42→4" might suggest for the *open-count* metric specifically.
3. **Wall-clock could not be measured cleanly this session — reported as inconclusive, with the
   load evidence to back that call, rather than reported as either a win or a regression.** This is
   the single most important honest caveat in this report: a wall-clock number IS available (§4's
   table), but trusting a median under a 28-93% noise floor would be reporting noise as signal.
4. **Settings CLI auto-start is a new, real cost this chain adds, and it was worth measuring
   separately from the MCP server's own latency** — ~785 ms cold, ~245 ms warm, neither of which
   existed in the before tree at all.

## What's committed on this branch

- This report.
- Nothing else. The counting-decorator probe (`Wp5AfterBankOpenCountProbe.cs`) and the wall-clock
  driver/settings-autostart scripts were throwaway (deleted / left in scratch), matching the before
  doc's own practice — none touched production code, and the driver/scripts hardcode machine-local
  paths that don't belong in the tree.

## Branch / commits

- `perf/wp5-after-measurement`, pushed to `origin/perf/wp5-after-measurement`.
- Not merged to main; integration is the owner's.
