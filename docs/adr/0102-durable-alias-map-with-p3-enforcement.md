# ADR-0102: the applied alias map persists in the bank; P3 refuses drops and folds through aliases

**Status:** accepted · **Date:** 2026-09-05 · **Task:** `air-run-once-repair-fully-converges`

## Context

ADR-0099 deliberately shipped no durable map ("no new bank table"): steady-state
writes under retired loser ids passed through unfolded, so every repair decayed
— the live bank grew `job-search-ai-assistant` 114 → 116 rows *during* its own
repair, and a fresh fragment id appeared mid-session. A converged bank cannot
stay converged while any writer may mint a new deviation. Amends ADR-0099's
no-new-table position for exactly one append-only table.

## Decision

- New `project_id_aliases` table (v13 → v14 ladder rung, idempotent,
  additive-only with a downgrade-safe note): `alias` PK (`Ordinal`),
  `winner`, `kind` (`alias`/`drop`; canonicals intentionally unpersisted),
  `applied_at`. The repair job persists the applied one-shot map on success,
  **before** `FinishRepairRequest` — a crash mid-persist leaves the request
  open, the retry re-persists the full map, `INSERT OR IGNORE` absorbs the
  partial (self-healing). Rows immutable thereafter.
- Choke points read a cached durable-backed `Default` (empty map =
  byte-identical pass-through): `ToolGate` **refuses** writes/destructives
  under *dropped* ids (`RetiredProjectException` naming the attribution, before
  access checks); writes under *alias* losers **fold through** to the winner
  (stale-config writers keep working, data lands canonical); watch boundaries
  refuse retired ids. Split rationale: resurrecting test residue is pure harm;
  refusing a live-but-renamed id breaks running agents for bookkeeping.
- Sync is per-table row-merge over an ATTACHed snapshot (verified H2): the map
  table gets a pull arm (existence-gated, conflict probe **before** entries
  mutate, fail-closed abort, `INSERT OR IGNORE`, cache reload);
  same-alias-different-winner surfaces as `unresolved` for a human; push folds
  loser-keyed entries/tombstones symmetrically. Excluded from
  `MachineLocalTables` (bank content, not a machine-local outbox).
- Reload legs (all three, each red-proven): job-side reload after persist,
  once-per-process startup warm (`ProjectIdAliasCacheHostedService`, fail-open,
  EventId 713), sync-pull reload. Null-winner skips are logged (EventId 712).

## Consequences

**+** No new deviation is possible: drops refused, aliases canonicalized at
  every choke point, replicas converge through the sync arm.
**+** Crash-safe persist ordering; insert-only rows never half-merge.
**−** One `CurrentVersion` bump (13 → 14): older binaries refuse v14 banks via
  the forward-version guard until they upgrade.
**−** Process-static `Default` cache requires test hygiene: every loader resets
  in teardown, all readers share one non-parallel collection (rule documented
  on the collection; three missing teardowns bit once).
**Neutral:** `SyncResult` shape unchanged (fail-closed abort, no merge channel).

## Alternatives considered

- Durable map in settings keys (rejected: user-editable opaque KV, wrong
  sync/transactional semantics — already rejected in ADR-0099, stands).
- Refuse alias-loser writes too (rejected: breaks running agents holding stale
  config for a bookkeeping event).
- Merge-then-report conflicts (rejected: needs a result-channel change; abort
  naming both winners is sufficient).
