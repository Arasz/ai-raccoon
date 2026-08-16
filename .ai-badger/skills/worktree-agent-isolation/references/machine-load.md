# Machine Load: QoS and Worker Budget

Two independent, independently-disableable layers that keep a machine usable while several
agents run in parallel worktrees. Read this when the machine feels "almost unusable" under
parallel agents, when a timing-sensitive test suite starts flaking after QoS is turned on, or
when deciding whether a third layer (an admission queue) is worth building — it measured out
as not worth it; see `docs/adr/0020-background-qos-and-worker-budget.md`.

Both layers are implemented by `scripts/run_suite.py`:

```
python3 run_suite.py [--agents N] [--reserve M] [--no-qos] [--slots K] -- <command...>
```

## The three layers, and when each applies

| Layer | What it does | Applies to | Cost | Ships? |
|---|---|---|---|---|
| 1. Background QoS | `taskpolicy -b` prefix on macOS | The test/build runner process only | 2.11x slower | Yes |
| 2. Parallelism budget | Caps per-tool worker count via `AI_BADGER_TEST_WORKERS` | Any tool whose config reads that var | ~free | Yes |
| 3. Admission queue | Shared-state slot allocator across agents | N/A | Needs shared state, a daemon or lockfile | **No — designed, not built** |

Layer 1 alone solves the stated complaint ("machine almost unusable") completely and
independently of how many agents are running, because it changes how the OS scheduler treats
the process, not how many of them exist. Layer 2 is nearly free and exists to defend test
timing against layer 1's own side effect (see "The risk" below). Layer 3's only remaining
justification is memory pressure, and it barely addresses that — the dominant memory consumer
on a loaded machine is resident agent sessions themselves (~20 sessions at 0.5-0.7 GiB each),
and no scheduling mechanism evicts those; a queue only staggers *when* processes start, not how
much RAM they hold once running.

## Layer 1 — background QoS

### Measurements (M4, 20 concurrent saturating processes)

| Condition | Foreground work vs idle |
|---|---|
| No QoS | 4.8x slower |
| `nice` (highest niceness) | 4.4x slower |
| `taskpolicy -b` | 1.07x slower |
| `taskpolicy -b`, birth-applied | 1.49x slower |
| `taskpolicy -b`, applied late to a running PID | 1.45x slower |
| `taskpolicy -b`, applied to children (inherited) | 2.65x slower |
| Foreground work while `-b` background load runs | 1.0x of idle |
| Cost to the `-b`'d work itself | 2.11x slower |

`nice` is **not** a substitute for `-b` — measured 4.4x vs `-b`'s 1.07x under the same load.
`nice` only affects CPU scheduling priority; `taskpolicy -b` puts the whole process into
macOS's background QoS class, which also throttles I/O and timer coalescing — that is the
difference between 4.4x and 1.07x.

Children inherit `-b` (2.65x measured), and it can be applied to an already-running PID, not
just at birth (1.45x vs 1.49x from birth — close enough that late application is a legitimate
**recovery tool**, not just a launch-time flag).

### How it is applied

`run_suite.py` prefixes the wrapped command with `taskpolicy -b` when all of the following
hold:

- `sys.platform == "darwin"` (macOS only — `taskpolicy` does not exist elsewhere)
- `--no-qos` was not passed
- `AI_BADGER_QOS` is not set to `off` (case-insensitive, whitespace-trimmed)

Any one of those disables it. `taskpolicy` missing from `PATH` is a warning on stderr, not a
crash — the command still runs, just without the prefix.

### Do NOT apply `-b` to long-lived infrastructure

Because children inherit `-b`, wrapping a process tree that includes a long-lived dev
server, AppHost, or emulator also throttles *that* process for its entire lifetime — not just
the one test run. **Wrap only the test/build runner invocation, never the infrastructure the
tests block on.** This has to be an explicit exclusion in how `run_suite.py` is used, not an
omission: start the AppHost/emulator/dev server normally, then run the test command (and only
the test command) through `run_suite.py`.

```bash
# Right: infra starts un-throttled, only the test runner is QoS'd.
dotnet run --project src/AppHost &
python3 run_suite.py --agents 3 -- dotnet test --filter "RequiresInfra!=true"

# Wrong: the AppHost inherits -b for its whole lifetime, and every request it serves
# during the test run pays the 2.11x tax too.
python3 run_suite.py --agents 3 -- bash -c "dotnet run --project src/AppHost & dotnet test"
```

### The risk: `-b` tightens every fixed timeout by ~2.11x

This is not hypothetical. `apphost-lock.ts:9-12` in the job-search-ai-assistant repo records
two overlapping worktrees producing `TaskCanceledException` at `AspireInfraFixture.
InitializeAsync` — a **startup timeout with no assertion involved**. A suite that waits up to
30s for a dev dependency to come up can, under `-b`, take 63s and be killed by its own harness
before it ever gets to assert anything.

Mitigations:

- **Raise fixed timeouts on timing-sensitive suites by >=2.5x** when QoS is active — round up
  from the measured 2.11x for margin, the same way the AppHost/emulator startup wait should be,
  not the assertion-level timeouts inside already-running tests (those run at the same
  wall-clock rate as everything else once the process is up).
- **`AI_BADGER_QOS=off` exempts a lane entirely** — use it on a CI runner or a lane that owns
  the whole machine, where there is nothing else to protect and the 2.11x tax buys nothing.
- **Never QoS the infrastructure** — see above.

## Layer 2 — parallelism budget

### Formula

```
budget = max(1, (cores - reserve) // min(agents, slots or agents))
```

- `cores` — `os.process_cpu_count()` (respects a cgroup/`taskset` limit), falling back to
  `os.cpu_count()`. Never `os.sched_getaffinity` directly — it is Linux-only and raises
  `AttributeError` on macOS/Windows; `process_cpu_count` already reads it internally where
  it applies.
- `reserve` — cores left for the OS/orchestrator itself. Default **2**.
- `agents` — how many agents/worktrees are competing for the machine right now.
- `slots` — optional: how many of `agents` currently hold a run slot, when fewer than all of
  them are actually running work at once. Defaults to `agents` when unset.
- The result is exported as `AI_BADGER_TEST_WORKERS`, and never overwrites a value the caller
  already set.

`max(1, ...)` is load-bearing: at `agents=20, cores=10, reserve=2`, `(10-2)//20` is `0`, which
would starve the runner or crash tools that treat 0 workers as "no limit" (most do not — they
treat it as "use the CPU-detection default", silently discarding the budget entirely).

### Why this exists: percentage caps do not compose

Vitest's `test.maxWorkers: "50%"` and similarly-shaped percentage caps in other tools are **50%
of the whole machine, evaluated independently in every process**. Five worktrees each reading
`"50%"` do not divide the machine into five 20% shares — each one asks the OS for 50%, so five
worktrees collectively ask for **250%** of the machine. A percentage cap has no way to know how
many *other* percentage caps are active at the same time; only an external, explicitly-computed
number (this formula, fed by `--agents`) can.

### Per-tool cap table (verified against official docs)

| Tool | Flag / config | Env var | Default |
|---|---|---|---|
| `dotnet build` / MSBuild | `-m:N` | none | **1 (sequential)** — not a culprit |
| `dotnet test` (MTP) | `--max-parallel-test-modules N` | none | `Environment.ProcessorCount` |
| xUnit v3 | `--max-threads N` / `xunit.runner.json: maxParallelThreads` | none | CPU thread count |
| Vitest | `--maxWorkers=N` / `test.maxWorkers` | **none** | all cores |
| Playwright | `--workers=N` / `workers` | **none** | half the logical cores |
| `bun test` | `--parallel=N` | none | **sequential** — not a culprit |

`dotnet build` and `bun test` default to sequential execution and are not the source of
oversubscription — do not spend budget effort on them.

Because **neither Vitest nor Playwright has a worker env var**, their own config files must
read `AI_BADGER_TEST_WORKERS` explicitly — that read is the opt-in seam this layer exists to
create, in the same shape `playwright.config.ts` already uses for `CI`:

```ts
// playwright.config.ts
workers: process.env.AI_BADGER_TEST_WORKERS
  ? Number(process.env.AI_BADGER_TEST_WORKERS)
  : (process.env.CI ? 1 : undefined),
```

```ts
// vitest.config.ts
test: {
  maxWorkers: process.env.AI_BADGER_TEST_WORKERS
    ? Number(process.env.AI_BADGER_TEST_WORKERS)
    : undefined,
}
```

```json
// xunit.runner.json — read AI_BADGER_TEST_WORKERS in a build script or CI step and
// pass it through as --max-threads N / --max-parallel-test-modules N; xUnit v3 does not
// read the env var itself.
```

`dotnet test` and xUnit v3 have no env-var seam either — pass the value explicitly on the
command line when invoking them through `run_suite.py`:

```bash
python3 run_suite.py --agents 3 -- dotnet test -- \
  --max-parallel-test-modules "$AI_BADGER_TEST_WORKERS"
```

## Recovery: the machine is already wedged

`taskpolicy -b` can be applied to an already-running PID, not just at process birth (measured
1.45x vs 1.49x from birth — close enough to use live). If the machine is unresponsive right
now:

```bash
# Find the worst offenders first (see diagnosis one-liners below), then:
taskpolicy -b -p <pid>
```

This does not require killing or restarting the offending process — it re-classifies it into
the background QoS class in place. Apply it to every heavy PID competing for foreground
attention; foreground work returns to ~1.0x of idle without losing any of the background work's
progress.

## Diagnosis one-liners

```bash
# Load average vs core count — sustained load well above core count means real contention,
# not just a burst.
uptime

# Top CPU consumers, sorted, with QoS class and thread count visible.
ps -A -o pid,ppid,%cpu,nice,wq,command -r | head -20

# Swap pressure — this is the layer-3 argument's evidence: a machine can be CPU-idle and
# still unusable because it is swapping.
vm_stat | grep -E "Pageouts|Swapins|Swapouts"
sysctl vm.swapusage
```

Read `uptime`'s load average against `sysctl -n hw.ncpu` (or `os.cpu_count()`): a load average
several times the core count, with `ps` showing many resident agent processes each holding
0.5-0.7 GiB, and `vm.swapusage` reporting swap nearly full, is the machine-almost-unusable
state this whole document exists to fix — and the fix that actually reaches it is layer 1
(`taskpolicy -b` on the noisy foreground-competing processes), applied live per the recovery
recipe above.

## Verification Checklist

- [ ] `python3 -m pytest tests/test_run_suite.py -q` passes.
- [ ] `budget()` never returns 0 for any `(cores, agents, reserve, slots)` combination,
      including `agents=20, cores=10, reserve=2`.
- [ ] `budget(10, 5) != budget(64, 5)` — the budget is derived from real core/agent counts,
      not a hardcoded constant.
- [ ] The QoS prefix is absent when `sys.platform != "darwin"`, when `--no-qos` is passed, and
      when `AI_BADGER_QOS=off` is set — verified independently for each condition.
- [ ] A failure anywhere in `run_suite.py`'s own machinery (an unwritable state directory, an
      unresolvable QoS check) still runs the wrapped command and warns on stderr — it never
      silently skips the run.
- [ ] `run_suite.py` imports cleanly with `fcntl` blocked (it must never import it — the skill
      declares `platforms: [linux, macos, windows]`, and `fcntl` is POSIX-only).
- [ ] Any timing-sensitive suite run under QoS has its fixed timeouts raised >=2.5x, and the
      long-lived infrastructure it depends on (AppHost, dev server, emulator) is started
      *outside* `run_suite.py`, never as a child of the wrapped command.
- [ ] `AI_BADGER_TEST_WORKERS` is read by every tool in the per-tool cap table that has no
      native env var (Vitest, Playwright), and passed through explicitly for the ones that
      accept only a CLI flag (`dotnet test`, xUnit v3).
