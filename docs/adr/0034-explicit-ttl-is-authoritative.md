# 0034. An Explicit TTL Is Authoritative

Date: 2026-08-14

## Status
Accepted

## Context
ADR-0030 introduced `PromotionScorerTtlPolicy`: a write-time heuristic that reuses `PromotionScorer`
to score incoming content and, when the score falls below `0.6` — which every write under
`MinWordsFloor = 8` words does unconditionally (`Math.Min(prior, MinWordsCap = 0.50)`, always
&lt; 0.6) — assigns a 3-day TTL so the background sweep can degrade it later.

The 2026-08-14 MoE codebase review flagged this as a defect: a short, valid note like
`"Push after every commit."` (4 words) gets a 3-day TTL it did not ask for, with no way to opt out
short of `memory_set_ttl` after the fact.

**Verifying this against the code, not just the ADR, surfaced a second, independent defect the
review did not anticipate:** `SqliteMemoryStore.WriteAsync` computes `resolvedTtlDays` from the
policy loop and passes it to `MemorySql.InsertEntry` as `ttl_days = resolvedTtlDays` — but
`InsertEntry`'s column list never included `ttl_days` (`INSERT INTO entries (hash, path, value,
source_file, section, scope, project_id, context_label, workspace_id, agent_id, created_at,
updated_at, source_id) VALUES (...)`). Dapper silently drops parameters with no matching
placeholder. **The computed TTL was never persisted, for any write, regardless of word count.**
This is confirmed by `git diff` against the base commit: `MemorySql.cs` was untouched by this
change, so the gap predates it. It also explains, independently of the review's own explanation
(real writes running 64–166 words), why the live bank shows `ttl_days` NULL on every row: the
write path could not have set it even for a one-word note.

The heuristic's *intent* — described in ADR-0030 and pinned by `PromotionScorerTtlPolicyTests` — is
real and reproducible at the policy layer. Its *effect* on the database was always zero: this was
never a live behaviour that later broke, or a data-loss risk that this ADR closes. ADR-0030 shipped
with a benchmark and green tests, and described behaviour the system never actually had — the
`ttl_days` column was never in `InsertEntry`'s column list at any point after auto-TTL was added
(`git log -S"ttl_days" -- src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs` shows the auto-TTL
commit itself never added it). Deleting `PromotionScorerTtlPolicy` here is dead-code removal, not
a fix for a live defect — there is no "before" state in which this subsystem ever assigned a TTL
that reached a row.

## Decision
No heuristic assigns a TTL at write time. `PromotionScorerTtlPolicy` and its `IAutoTtlPolicy`
abstraction (verified: the TTL policy was its only implementer) are deleted as dead code —
they described and tested behaviour (`PromotionScorerTtlPolicyTests`) that never reached the
database. `SqliteMemoryStore`'s constructor drops the `IEnumerable<IAutoTtlPolicy>
autoTtlPolicies` parameter, and `WriteAsync` drops the per-policy TTL-evaluation loop entirely —
including the now provably-dead `ttl_days` insert parameter.

**The one TTL path this project keeps is `memory_set_ttl`, and it is load-bearing, not aspirational:**
`SqliteMemoryStore.SetEntryTtlAsync` issues `MemorySql.UpdateEntryTtl` — `UPDATE entries SET
ttl_days = @ttlDays WHERE project_id = @projectId AND hash = @hash` — a real, parameter-matched
write that persists. Unlike the deleted write-time heuristic, this is a verified, functioning
path today; ADR-0034's "explicit TTL is authoritative" rests on it actually working, not on intent.
The reaper (ADR-0025) still degrades anything an agent or operator explicitly TTLs through it.

Supersedes ADR-0030. Restores ADR-0025's "Fact 1" (an explicit TTL is the only source of one).

## Consequences
- **Positive:** Removes dead code — a subsystem, its DI wiring and its dedicated test file — that
  described behaviour (a write-time heuristic TTL) the system never actually exhibited, plus the
  CPU it spent scoring every write's promotion-worthiness for that non-existent effect.
- **Positive:** `memory_set_ttl` is now unambiguously the one TTL-setting path, and it is confirmed
  functioning (see `SqliteMemoryStore.SetEntryTtlAsync` above), so ADR-0034 does not trade a real
  capability away.
- **Neutral:** No live-bank data is affected — `ttl_days` was already NULL on every row before this
  change, and stays that way for writes; nothing regresses.
- **Negative:** An agent that wants short-lived notes to self-expire must call `memory_set_ttl`
  explicitly; there was never a working heuristic safety net for a rushed, un-set TTL, but an agent
  reading ADR-0030 could have believed there was one.
