# 0025 — The sweep reaper: default-on, global-scoped, gated on full access mode

Date: 2026-08-09

Status: Accepted.

## Context

1.6.0 shipped `SweepHostedService`: an unattended background job that, on every HTTP/S host, walks
every project in the bank on a timer (default 24 h) and deletes entries whose rating is below a
threshold and whose age exceeds a per-entry TTL. It is **on by default**
(`SweepConfigKeys.DefaultEnabled = true`) and runs against the one bank the whole machine shares.
This is the largest behavioural change in the release — a default-on, cross-project, unattended
deleter — and it shipped with no ADR, while a same-release no-behaviour-change contract
restatement got one ([ADR-0024](0024-unknown-id-contract.md)). This record closes that gap and
rules on three defects a post-release review found in the reaper's consent and observability
(referred to below as H6, H7, H8; see `docs/reviews/2026-08-09-1-6-0-high-fix-briefs.md`, Lane B).

Two facts, both measured, are load-bearing for the decisions below.

**Fact 1 — on an existing (pre-1.6.0) bank, the reaper is armed but inert.**
`DegradationPolicy.ShouldDegrade` is `ttlDays.HasValue && rating < threshold && ageDays > ttlDays.Value` —
an entry with no per-entry TTL can never be a candidate, full stop, regardless of rating or age. Before
1.6.0 there was no tool or CLI verb that could set one (`memory_set_ttl` is new in this release). The
review measured this directly: entries aged 3,650 days with ratings 37 orders of magnitude below the
sweep threshold swept nothing, because every one of them carried `ttl_days = NULL`. So shipping the
reaper on-by-default did not, by itself, put any pre-existing data at risk — it only starts mattering
once something calls `memory_set_ttl`, which is itself Destructive-gated (full mode required).

**Fact 2 — the kill switch is fail-open toward staying armed.**
```csharp
// SweepConfigKeys.cs
public static bool ParseEnabled(string? value) =>
    !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);
```
`'0'`, `'no'`, a typo, or any other garbage in `sweep.enabled.global` reads as *enabled*. Only the
exact string `false` (any casing) disarms it. `docs/reference/agent-memory-server.md` already
documents this choice ("the kill switch fails safe: ... an absent or unreadable row leaves the
reaper armed") as intentional, not accidental.

## Decision 1 — on by default, not off

The reaper stays on by default. Reversing this is not part of this ADR's scope (it would need its
own review of the retention story this release is building toward), but Fact 1 is why shipping it
on-by-default did not amount to arming a live deleter against unprotected data: the reaper needed a
second, independently-gated action (`memory_set_ttl`, full mode only) before it could delete
anything at all. On-by-default is a statement about the *policy* an operator gets without touching
config, not a statement that data was already at risk the day this shipped.

## Decision 2 — the kill switch and the threshold are global, not per-project

`sweep.enabled.global` and `sweep.threshold` are both single bank-wide settings rows — there is no
per-project variant of either, and the CLI (`ai-raccoon sweep enable|disable|threshold set`) writes
and reads them with no project concept at all. This is deliberate: the reaper sweeps *every* project
in one pass, and "how aggressively does this shared bank forget things" is an operator-level policy
question, not a per-project one — the same reasoning [ADR-0014](0014-settings-never-sync.md) applies
to settings generally (they describe the machine, not the data). A per-project threshold would let
one project's owner silently change what counts as "forgettable" for data another project's owner
put there, in a bank they both share.

`ForgettingPolicyService.GetSweepThresholdAsync`/`SetSweepThresholdAsync` still take a `projectId`
parameter (fixed as part of this same change to stop `GetSweepThresholdAsync` taking one it never
used) — but only `SetSweepThresholdAsync` needs it, and only as the caller's proof of Destructive
consent via *some* project's access mode, not as a scope for the write. The setting it writes still
applies to every project. This is documented on the method now, not hidden behind a signature that
implied otherwise.

## Decision 3 — H6: the reaper honours a project's access mode

**The finding.** `memory_sweep(dryRun:false)` is `Destructive`, which `AccessModePolicy` grants only
in `full` mode (default `rw`). Verified in code and reproduced live during the review: the MCP call
on an `ro` project is refused (`access-denied: memory_sweep requires mode full (current ro)`), while
the reaper called `SweepService` directly with no gate at all — the same project, reaped by the
timer with no consent check. The in-code comment justifying this ("a timer has no caller") was
written about the *threshold read*, and silently came to cover the *deletes* as well.

**The ruling: honour the mode.** `SweepHostedService` now resolves each project's effective access
mode (per-project setting, else the global default, else `rw` — the same resolution
`MemoryAccessGuard.ResolveAsync` performs) and skips any project that is not `full`, exactly the
requirement `memory_sweep` already enforces for a human or agent caller.

**Why this over exempting the reaper.** The alternative — argue retention is operator policy, not
agent permission, and let the reaper bypass the mode entirely — was seriously considered. It is not
unreasonable on its own; `full` vs. `rw` distinguishes what an *agent* may do through the MCP surface,
and the reaper is not an agent. But two things tip the ruling the other way:

- **`full` mode exists specifically to gate destructive operations**, and a project's owner setting
  a mode is the one lever this system gives them to say "not this project, not without more consent
  than the default." A background job that ignores that lever makes the distinction between `rw` and
  `full` mean less than it appears to: an operator reading the access-mode table
  (`docs/reference/agent-memory-server.md`) would reasonably read `rw` as "no destructive operations
  happen here" and be wrong, silently, the first time the reaper runs.
- **This does not newly disarm anything that was actually live.** Per Fact 1, the reaper was already
  inert against every pre-1.6.0 entry (no TTL). The only entries this ruling newly protects from the
  reaper are ones a caller has *already* set a TTL on — and setting a TTL is itself Destructive-gated,
  so whoever did that already had `full` access on that project at the time. The only new case this
  ruling changes is: a project drops from `full` back to `rw`/`ro` *after* TTLs were set on some of
  its entries — those entries now correctly stop being swept until the project is `full` again. That
  is exactly the behaviour "the mode is real consent, not just an entry gate" implies.

A store failure while resolving one project's mode (a setting read that throws) is caught by the same
per-project `try`/`catch` that already wraps the sweep itself — it counts as that project's failure
(H8) and does not abort the pass for the rest.

## Decision 4 — H7: a span only when the pass deletes something

**The finding.** `BackgroundTelemetry`'s span filter defaults to suppress and only emits when
`NoteWork()` is called. `SweepHostedService` never called it — confirmed by grepping `NoteWork`
across `src`, which found Watch, Extraction, BankMaintenance and IdleWatchdog, but not Sweep. A live
7.5-minute OTLP capture recorded `watch.reconcile` and `bank.maintenance` spans and zero
`sweep.reaper` spans, including across ticks. The one background job that destroys data was the one
job invisible in traces, on every pass, including ones that deleted rows.

**The fix.** `RunPassAsync` now takes the pass's `IOperationScope` and calls `NoteWork()` plus tags
`deleted` with the count, but only when `deletedTotal > 0`. An empty pass — the overwhelming majority
of ticks on most banks, per the span-volume reasoning `9ac3d543` already established for `watch.reconcile`
— stays silent on purpose; it still records its counter and duration (every scope always does), just
no span. The rule stays what it was: span the passes worth reading, and a pass that deleted user data
is always worth reading.

## Decision 5 — H8: a pass is not `success` when every project in it failed

**The finding.** Per-project failures inside the sweep/extraction loops were caught, logged, and then
`pass.Succeeded()` ran unconditionally — so `ai_raccoon.background.passes{result}`'s failure rate
stayed at 0% even on a pass where every project threw.

**The fix.** `IOperationScope` gained a third outcome, `PartiallyFailed(int failureCount)`, distinct
from `Succeeded()` (nothing failed) and `Failed(exception)` (the whole pass threw — shutdown or a
programming error outside the per-project loop). Both `SweepHostedService` and
`ExtractionHostedService` now count per-project failures and call `PartiallyFailed` instead of
`Succeeded` when that count is greater than zero; the per-project `catch` that lets one bad project
skip without stopping the others is unchanged. `PartiallyFailed` always opens a span (like `Failed`) —
a pass with a failure in it is never the quiet, nothing-happened case span suppression exists for —
and tags it with the failure count.

## The reaper ↔ promoter interaction

A reaper delete removes an `entries` row, which fires the [ADR-0023](0023-promotion-queue-entries-delete-invalidation.md)
trigger and drops the matching `promotion_queue` row, if any — correct, by the same reasoning
ADR-0023 already gives for every other deleter. The interaction worth naming here is the race with
`PromotionQueueService.PromoteAsync`: it claims a queue row (a discard) *before* calling
`ShareAsync`, specifically so a concurrent discard mid-batch cannot double-promote. If the reaper
deletes the backing entry in the window between that claim and the share call, `ShareAsync` throws
`UnknownHashException`; the row is already off the queue and cannot be safely re-queued (that would
reopen the exact race the claim-before-share ordering exists to close), so it is recorded as a
`stale-hash` failure and the promote batch continues. That failure is logged at **Debug**
(`PromotionQueueService.Log.StaleHash`, EventId 705) — a candidate the promoter was actively
processing can silently vanish out from under it, with no default-level trace of why. This ADR does
not change that logging level; it is named here as a known, accepted interaction, not a defect this
change set fixes. `full` mode is required for both sides of the race (sweeping and the TTL/rating
state that makes something sweepable at all), so the race needs an operator who has already opted a
project into destructive operations — it is a live risk on such a project, not a hypothetical on a
default one.

## The fail-open kill switch (Fact 2): kept, flagged for a future call

`ParseEnabled`'s fail-open-toward-armed default is already documented as intentional in
`docs/reference/agent-memory-server.md`. This ADR does not reverse it — `SweepConfigKeys.cs` is
outside this change set's file list, and flipping a destructive job's default posture is its own
decision, not a side effect of a consent/telemetry fix. But it is worth recording plainly: for a
job whose only job is deleting data, failing toward *armed* on a corrupted or unreadable setting is
the higher-blast-radius direction to fail in, even though it protects against the opposite failure
mode (a corrupted row silently disabling protection nobody meant to disable). A future change could
require an exact `"true"` to arm rather than treating anything-but-`"false"` as armed; that is a
recommendation for a follow-up, not a ruling made here.

## Consequences

- **Positive.** A project's access mode is now real consent for background deletion, not only for
  agent-initiated deletion — the gap between what `memory_sweep` refuses and what the timer did
  anyway is closed.
- **Positive.** A reaper pass that deletes something is finally visible in traces; a pass where every
  project failed is finally visible in the failure-rate metric, without losing the "one bad project
  can't stop the others" property either pass type depended on.
- **Negative — the reaper is stricter than before it needed to be, in one narrow case.** A project
  moved out of `full` mode after TTLs were already set on some of its entries now keeps those entries
  past the point they would previously have been swept, until the project returns to `full`. This is
  the intended effect of Decision 3, not a regression to work around.
- **Neutral.** `sweep.enabled.global`/`sweep.threshold` remaining global-only, and the fail-open
  parse remaining as-is, are both explicit non-changes recorded here rather than silently carried
  forward.

## Alternatives considered

### Exempt the reaper from access mode entirely (H6 option 2)

Rejected — see Decision 3. Defensible on its own terms (retention as operator policy, not agent
permission), but it would leave `full` vs. `rw` meaning less than the access-mode table claims, for
no cost avoided that Fact 1 does not already show was near-zero.

### Span every sweep pass unconditionally

Rejected. The span-volume fix (`9ac3d543`) that quieted `watch.reconcile` down from spanning every
1 Hz tick applies with equal force here: an empty reaper pass on most ticks, most banks, is exactly
the case that fix exists to keep silent. `NoteWork()` only when `deletedTotal > 0` keeps the "span
what's worth reading" rule intact rather than special-casing the reaper out of it.

### Fail the whole pass instead of `PartiallyFailed`

Rejected. Both hosted services already had a per-project `catch` specifically so one bad project's
exception does not stop the loop from reaching the rest. Making `RunOnceAsync` throw on any
per-project failure would either abandon that property or require re-implementing it one layer up
for no benefit; a distinct `result=partial` value plus a `failures` tag gives the metric its honesty
back without disturbing that property.
