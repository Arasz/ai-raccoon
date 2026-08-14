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
real and reproducible at the policy layer. Its *effect* on the database was already zero before
this change. Removing the whole subsystem is correct either way: as designed, it silently
overrode agent intent on every short write; as actually wired, it was dead code spending CPU on
every write for no persisted effect.

## Decision
No heuristic assigns a TTL at write time. `PromotionScorerTtlPolicy` and its `IAutoTtlPolicy`
abstraction (verified: the TTL policy was its only implementer) are deleted.
`SqliteMemoryStore`'s constructor drops the `IEnumerable<IAutoTtlPolicy> autoTtlPolicies`
parameter, and `WriteAsync` drops the per-policy TTL-evaluation loop entirely — including the now
provably-dead `ttl_days` insert parameter.

**The motivation behind ADR-0030 is relocated, not abandoned.** `memory_set_ttl` already provides
the explicit path an agent (or operator) can use to mark content transient — the one this ADR
prefers over a heuristic guessing from word count and provenance archetype. The reaper
(ADR-0025) still degrades anything an agent or operator explicitly TTLs; it simply no longer
degrades things a heuristic guessed about without being asked.

Supersedes ADR-0030. Restores ADR-0025's "Fact 1" (an explicit TTL is the only source of one).

## Consequences
- **Positive:** A write's TTL is never a surprise — it is either absent (permanent, the default)
  or exactly what `memory_set_ttl` set.
- **Positive:** Removes CPU spent scoring every write's promotion-worthiness for an effect that,
  per the `MemorySql.InsertEntry` finding above, was already discarded.
- **Neutral:** No live-bank data is affected — `ttl_days` was already NULL on every row before this
  change, for a different reason than assumed.
- **Negative:** An agent that wants short-lived notes to self-expire must now call `memory_set_ttl`
  explicitly; there is no more heuristic safety net for a rushed, un-set TTL.
