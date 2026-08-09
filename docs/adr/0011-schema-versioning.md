# 0011 — Schema versioning: record the gap, defer the migration ladder

Date: 2026-08-07

Status: Implemented 2026-08-08 (WI-5) — see the addendum at the end

## Context

`MemorySchema.EnsureAsync` (`src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs`) has no
`PRAGMA user_version` marker. Every schema evolution to date has shipped as its own
existence-check block inside `MigrateAsync`: query `pragma_table_info('entries')` for a
column and `ALTER TABLE ... ADD COLUMN` when it is missing (four times, for
`source_file`, `section`, `heading_path`, `structure_embedding`); inspect
`sqlite_master.sql` for `entries_fts` and rebuild it when the shape predates the
three-column FTS index; probe `sqlite_master` for two index names and run a dedupe +
`CREATE UNIQUE INDEX IF NOT EXISTS` pass when either is missing.

This works today because every check is independent and idempotent — but it does not
scale, and it already shows the failure mode: there is no single source of truth for
"what shape is this bank in." A legacy bank predating the CHECK constraints and foreign
key on `entries` (a bank created before `workspace_id`'s FK and the
`workspace XOR committed scope` CHECK existed) has no way to distinguish itself from a
current bank without SQLite reintrospecting the same handful of column/index probes
`MigrateAsync` already runs — and those probes only cover the columns and indexes someone
remembered to add a check for. A structural change that isn't a new column or a renamed
index (e.g. tightening a CHECK constraint, which SQLite cannot ALTER in place) has no
probe pattern to reuse at all: the pre-CHECK/pre-FK shape has no way to distinguish itself
from a current bank, so legacy banks keep their pre-CHECK/pre-FK shape forever unless a
future migration happens to also add a compensating runtime check.

## Decision

Record the gap now; do not implement the fix in this ADR. The chosen direction for the
eventual implementation:

- Adopt SQLite's built-in `PRAGMA user_version` as the bank's schema-version marker — an
  integer bumped by one per shipped schema change, read once on `EnsureAsync` before the
  DDL runs.
- Replace the current pattern (N independent existence probes, one per historical change)
  with an ordered migration ladder keyed to `user_version`: each step runs only when the
  stored version is below the step's target version, then bumps the version. New schema
  changes become "add one more ladder step," not "invent a new existence probe."
  `CREATE TABLE IF NOT EXISTS` stays as-is for brand-new tables (idempotent, safe on every
  open); the ladder targets `ALTER`/constraint-shape changes on already-shipped tables,
  which is exactly where today's ad hoc probing lives.
- A fresh bank is created at the current schema and stamped with the current
  `user_version` directly — it never walks the ladder.
- The existing column/index probes in `MigrateAsync` are the ladder's first steps once
  this is implemented; they are not being rewritten by this ADR.

## Consequences

- Until implemented, legacy banks continue to rely on per-feature existence probing;
  this ADR does not change runtime behavior.
- The eventual migration ladder gives every future schema change one place to land
  (a new numbered step) instead of a bespoke probe, and gives operators a single
  queryable fact (`PRAGMA user_version`) to answer "what shape is this bank in" without
  re-deriving it from column/index introspection.
- A CHECK-constraint or foreign-key tightening — which SQLite cannot `ALTER` in place —
  becomes representable as a ladder step (rebuild-and-swap the table, as `MigrateAsync`
  already does for `entries_fts`), where today it has no representation at all.
- Implementation, including the exact ladder step abstraction and the rebuild-and-swap
  approach for constraint changes, is deliberately out of scope for this ADR and is a
  separate work item.

**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:173-391` (`EnsureAsync`/
`MigrateAsync`, the four column-existence checks, the FTS rebuild probe, the index-existence
probe); no `user_version` reference anywhere under `src/` (`grep -r user_version src` — zero
hits).

## Addendum — implemented 2026-08-08 (WI-5)

`MemorySchema.CurrentVersion` is 1, and `EnsureAsync` now reads `PRAGMA user_version` before
the DDL runs:

- **Fresh bank** — no user table in `sqlite_master` — gets the DDL plus the bucket-uniqueness
  indexes and is stamped directly; it never walks the ladder. (Freshness is *not* keyed to a
  single table: the oldest banks in the wild predate `entries`, and keying on it stamped one of
  them as new during implementation, which the watch-lease migration test caught.)
- **Unstamped existing bank** walks ladder step 1 — everything that shipped before the marker
  existed — and is stamped only if the step completed. The bucket-index step is deliberately
  soft (a bank must never fail to open), so a bank that hit it stays unstamped and retries on
  the next open.
- **Stamped bank** skips the ladder entirely, which is where the saving is: the store opens a
  connection per ingested file, and each open used to pay two `pragma_table_info` calls, two
  `sqlite_master` lookups and, when the FTS shape matched, two `count(*)` scans.

One step is deliberately outside the version gate: the `watch.scope.*` → `ingest.scope.*` key
move runs on every open. It is a data move guarding a deny-by-default gate, it costs one
indexed count, and a bank stamped by a newer build and then written by an older one would
otherwise keep a scope nothing can reach.

The bucket-uniqueness indexes moved out of the shared DDL into their own constant: on a legacy
bank the duplicates have to be deleted before the index can be created, so the DDL cannot
create them unconditionally — the ladder still owns that path.

> **2026-08-09 update.** `MemorySchema.CurrentVersion` is **3**, not 1 as the addendum above
> states — two further ladder steps shipped since it was written: v2 (`chunk_index`/
> `total_chunks` columns plus a vec0 rebuild) and v3 (issue #200's bank-wide
> `chunk_index`/`total_chunks` recompute, closed by PR #204). PR #204 also added a forward-version
> write guard — `EnsureAsync` now refuses a write-path open when the stored version is ahead of
> `CurrentVersion` instead of silently no-oping through it, which is the mechanism #200's drift
> exploited. That guard is its own architectural decision and has its own record: see
> [ADR 0019](0019-forward-version-write-guard.md).

