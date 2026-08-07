---
name: cron-watchdog-authoring
description: Use when scheduling a Hermes cron job or watchdog script.
---

# Cron / watchdog job authoring

Class of work: recurring automation that acts when a condition flips (tool version released, PR changed, disk threshold crossed) and stays silent otherwise. Proven shapes for this user's deployments (ai-raccoon tool rollouts, PR monitors).

## Pitfall: schedule strings — "30m" is ONE-SHOT

`cronjob(action=create, schedule="30m")` produces **"once in 30m", repeat=once** — a single run. Recurring requires the literal prefix: `schedule="every 30m"`, `"every 2h"`, or a cron expression (`"0 9 * * *"`). After creating a job, CHECK
the returned `schedule`/`repeat` fields and fix with `cronjob(action=update)` when they say `once`. (Measured 2026-08-07: created "30m", got one-shot; updated to "every 30m".)

## The watchdog pattern (no_agent + script)

For deterministic jobs, skip the LLM: `no_agent=true` with `script=<name>` (resolves under `~/.hermes/scripts/`; `.sh`/`.bash` run via bash, everything else via Python).

- **Silent unless action**: empty stdout = silent tick (no delivery); non-empty stdout is delivered verbatim; non-zero exit sends an error alert. Design the script to print ONLY when it took real action; write diagnostics to a log file
  (`~/.ai-raccoon/rollout.log`-style) instead of stdout.
- **Exit 0 on transient failures** (offline, not-yet-published): log to the file, stay silent — otherwise a laptop offline for a day spams one alert per tick. Reserve non-zero for genuinely broken states.
- **Idempotent by construction**: every tick must be a no-op when the end state already holds.
- **Delivery caveat**: CLI/TUI sessions have NO live-delivery channel — output is saved and viewable via `cronjob(action=list)`. If the user wants a ping, `deliver` must name a gateway-connected platform (`telegram`, `all`).

## Version-gated rollout watchdog (measured recipe, ai-raccoon 1.1.0 rollout)

Sequence per tick: read installed version → below target: run the update command (`dotnet tool update --all --no-http-cache -g`), re-read, still below → silent; at/above target → perform the one-time rollout:

1. **Marker file for one-time side effects** (`~/.ai-raccoon/rollout-<ver>-done.marker`): the kill+restart+settings-arming happens only on the FIRST tick at target version. Later ticks only ensure the service is still up (self-healing).
   Without the marker, every tick re-arms settings and fights later manual changes.
2. **Surgical replacement of the old server**: `lsof -ti :<port>` → SIGTERM only the port holder — never `pkill -f "<binary> serve"` (matches scratch servers on other ports and can kill the user's other data-root instances). Then start the
   new binary detached (`subprocess.Popen(..., start_new_session=True)` in Python, nohup-style), log to a file, poll the port up to ~20s.
3. **Settings arming once**: run the config verbs inside the marker branch only.
4. Version parse: `tool --version` prints SemVer+commit hash — regex the dotted triple; compare tuples. Verify the parse against the real binary before trusting the script.
5. Python compatibility: scripts may run under the macOS system python3 (3.9.x) — add `from __future__ import annotations` FIRST (before imports) or avoid `X | None` annotations entirely; `tuple[int,int,int] | None` raises TypeError at
   definition time on 3.9.

## Pitfall: verify CLI verb paths BEFORE the rollout branch can execute them

Draft scripts often assume a `config` parent verb (`ai-raccoon config extract enable true`). Verb families are frequently ROOT-level (`ai-raccoon extract enable true`) — there is no `config` command at all, and the mistake only surfaces
when the rollout branch actually runs ("Unrecognized command or argument 'config'"). Caught this way 2026-08-07: the settings-arming verbs were wrong from the first commit and the error stayed invisible through smoke tests that never
reached the rollout branch. Fix: during the foreground verification pass, run EVERY command the script will execute (or its `--help`) against the real binary — a wrong verb path fails silently in a log-only watchdog.

## Pitfall: the scheduler may never tick — run the script manually when the trigger condition is met

A job can sit with `next_run_at` in the past and `last_run_at: null` after creation/update (observed 2026-08-07 on an 'every 30m' job; the update also left `repeat: once`). Treat the cron as a self-healing layer, NOT the execution
guarantee: when the user reports the trigger condition (e.g. "we updated to 1.1.0"), run the script in the foreground instead of waiting for the tick.

## Verification before scheduling

Run the script once in the foreground first. Expect the "nothing to report" path to be silent + logged; the action path can be exercised with a lowered TARGET only if the real side effects are safe (port kill is NOT safe to dry-run against
a live server — verify the rollout branch by review instead).

## Related

- `memory-bank-audit` references/maintenance-fix-shape.md — the bank-maintenance design this rollout supports.
- Existing instance: `ai-raccoon-1.1.0-rollout` (job ba03a53e0268, every 30m) + `pr32-online-review-monitor` (30m, local-deliver).
