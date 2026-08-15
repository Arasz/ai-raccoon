# 0055. A discard is load-bearing while its entry lives

Date: 2026-08-15

Status: Accepted

## Context

Two tables grew with no reaper. On the live bank at review time: `promotion_discards` **965 rows** —
by far the largest artefact the promotion feature had produced, against 19 queued and 138 shared
entries — and `search_quality` **424 rows**, one per `memory_search` call, forever. A repo-wide search
found **no `DELETE` for either** (2026-08-14 project-scope review, data-access F5/F6).

`noise_entries` already had a working reaper in `BankMaintenanceHostedService.RunPassAsync`, added by
ADR-0029. The pattern was there; it was never generalised.

`search_quality` even carries `idx_sq_project_time (project_id, created_at)` — an index whose shape
only makes sense for a range purge that was never written.

## Decision

**Both are age-bounded in the same maintenance pass, behind settings.**

| Table | Setting | Default |
|---|---|---|
| `promotion_discards` | `maintenance.promotion-discard-retention-days.global` | 180 days **and** the entry no longer exists |
| `search_quality` | `maintenance.search-quality-retention-days.global` | 90 days |

**The discard purge requires both conditions, and that is the decision this ADR exists for.** A
discard means *"the agent said no, do not propose this again."* Age alone is the wrong rule: while the
entry is still in the bank, the discard is the only thing stopping the propose path from offering the
same candidate again, so forgetting it re-inflicts a rejection the agent already made. Once the entry
is gone the discard cannot suppress anything, and the retention window then guards only the narrow
case of the same content being re-written under the same hash.

```sql
DELETE FROM promotion_discards
WHERE discarded_at < @cutoff
  AND NOT EXISTS (SELECT 1 FROM entries e WHERE e.hash = promotion_discards.hash)
```

`search_quality` has no such coupling — it is telemetry about a call that has already happened, so age
alone is the right rule.

**On the defaults.** Question 18 of the review asks the owner for retention windows and is unanswered.
180 and 90 days are chosen to be conservative in the direction that matters — a discard kept too long
costs bytes, a discard dropped too early costs the agent a rejection it has to make again. Both are
**settings**, not constants, so changing them needs no code and no release. Recorded here so the
choice is visible rather than inherited.

## Consequences

- Retention is housekeeping and never fails a maintenance pass: the purge is wrapped in the same log-and-continue shape as the noise purge, rethrowing only genuine cancellation.
- `BankMaintenanceHostedService` gains two dependencies, both already registered (`IPromotionQueueStore`, `ISearchQualityService`).
- Event ids 522-524 join the maintenance service's contiguous run; `docs/reference/logging-event-ids.md` is updated, and its derived gate caught the stale method count when it was not.
- Adding `PurgeOldDiscardsAsync` to `IPromotionQueueStore` made the compiler name all six implementors — the port hygiene from ADR-0054 working as intended one package later.

## Evidence

`tests/AiRaccoon.Tests/Integration/Maintenance/RetentionReaperTests.cs`. The discard test seeds three
rows against one surviving entry and asserts **exactly one** is purged — the aged discard whose entry
is gone — leaving the aged discard whose entry lives and the recent orphan. That asymmetry is the
decision; a purge that removed two would be age-only, and a purge that removed none would be inert.

Watched red first in the strongest available form: both methods did not exist, so the gate could not
compile.
