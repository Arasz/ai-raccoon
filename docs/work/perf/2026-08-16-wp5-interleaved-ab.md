# WP5 interleaved A/B: settling the wall-clock effect

Status: IN PROGRESS
Date: 2026-08-16
Branch: `perf/wp5-interleaved-ab`

Companion to `docs/work/perf/2026-08-16-wp5-before-baseline.md` and
`docs/work/perf/2026-08-16-wp5-after-measurement.md`. Both are merged and honest; neither settled
wall-clock. The mechanism (statement volume per bank open falling ~92%, `MemorySchemaDdlStatementCountTests`
pinning 42→4 statements per open) is **not** re-litigated here — it is settled. This report exists
to answer the one thing the prior two attempts could not: is there a measurable wall-clock effect,
once machine-load drift is cancelled out by pairing instead of chased by waiting for a quiet
machine.

## Why interleaved A/B instead of a third independent pass

The before and after reports each measured wall clock as two independent sessions, hours apart,
under different machine conditions (CPU thermal state, page cache, background load, other agents'
processes). Comparing their medians is confounded regardless of how quiet either session was
individually. The fix is not a quieter machine — it is running both trees *in the same session*,
alternating between them, and taking the **paired difference per round** (B−A). Load drift then
moves both arms of a round together and cancels out of the difference, which is the one quantity
this report trusts.

## Trees

- **A = before**: `639284b9` (the before-baseline doc's measured commit). Verified
  `git diff --stat 7cfaefca..639284b9` touches only `docs/work/checklist/2026-08-16-rerun-1-20-0.json`
  (docs-only) — so `639284b9` is interchangeable with the plan's named baseline `7cfaefca` for `src/`.
  Built in a separate `git worktree` at commit `639284b9`, `dotnet build -c Release`.
- **B = after**: `origin/main` at `0609d019` (current tip when this branch was cut). Built in this
  worktree, `dotnet build -c Release`.

## Method (filled in as the run progresses)

_To be completed once the driver and round data exist._

## Branch / commits

- `perf/wp5-interleaved-ab`, pushed to `origin/perf/wp5-interleaved-ab`.
- Not merged to main; integration is the owner's. Do not merge this PR.
