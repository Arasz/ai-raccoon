---
name: sqlite-schema-review
description: Review SQLite schema/migration changes.
version: 1.0.0
---

# SQLite schema & migration review

Reviewing changes to SQLite DDL, on-open migrations, unique indexes, and insert-path
dedup (the ai-raccoon storage layer and similar single-file banks). The core rule: **SQLite
semantics claims get verified with a scratch DB, never accepted from the plan, the PR, or the
docs** — the docs are ambiguous on `ON CONFLICT` scope and reviewed plans routinely get it
wrong (one plan asserted bare DO NOTHING "swallows FK/CHECK" — it does not).

## Empirically verified semantics (scratch-DB confirmed, 2026-08)

1. **Bare `ON CONFLICT DO NOTHING` swallows ONLY UNIQUE/PK conflicts.** NOT NULL, CHECK, and
   FK violations still throw `IntegrityError` even with DO NOTHING. "DO NOTHING silently
   swallows real errors" is not a valid risk — real errors surface loudly; only intended
   bucket-key conflicts are ignored.
2. **`last_insert_rowid()` is STALE after a swallowed insert**: previous successful rowid on
   the same connection, or 0 on a fresh connection whose first statement was swallowed. Any
   "insert → read back by `last_insert_rowid`" pattern must switch to a business-key re-read
   once DO NOTHING is added — otherwise you return the wrong row / throw "stored no row", and
   in a per-chunk loop you re-embed the *previous* chunk's id with the wrong content.
3. **`CREATE TRIGGER` does NOT validate column references at creation time** — a body
   referencing `old.source_file` on a table/FTS lacking that column is created successfully
   and **fails at FIRE time**. A migration DELETE firing such a trigger rolls back the whole
   migration. Check trigger/upgrade-shape interplay (legacy single-column FTS + new-shape
   trigger = runtime "no such column" on every DELETE).
4. **UNIQUE indexes treat NULLs as distinct; GROUP BY treats NULLs as equal.** A
   dedupe-then-index migration deletes NULL-key dups but the new index admits future NULL-key
   duplicates. Harmless only if no insert path produces NULL keys — check schema NULLability
   and the corpus.
5. **Expression/partial indexes cannot be named as `ON CONFLICT` targets** — bare DO NOTHING
   (no target) is required; it then applies to all UNIQUE/PK constraints.
6. **`CREATE UNIQUE INDEX IF NOT EXISTS` still THROWS on a violating table** — IF NOT EXISTS
   only skips when the index already exists. Dedupe-before-create must run on open, never in
   raw DDL, or a violating bank bricks on every open.
7. **`id NOT IN (SELECT MIN(id) … GROUP BY …)` dedupe is safe iff the subquery cannot return
   NULL** — `MIN(id)` over a NOT NULL PK never does, so NULL-poisoning of `NOT IN` doesn't
   apply. The DELETE's WHERE must match the index's partial WHERE and the GROUP BY must match
   the index key exactly (COALESCE included).
8. **`scope IS @scope` matches NULL correctly** (`IS`, not `=`) — the idiom for bucket-key
   lookups that must work with NULL scope/context_label/workspace values.
9. **Content identity claims**: `hash = SHA-256(path ‖ value)` means same path+hash ⟺ same
   content, so a MIN(id) survivor rule is content-preserving *only if every insert site uses
   the same hash over the same inputs* — verify chunk/import paths separately (chunks share a
   path, differ by hash).

## Migration/schema review checklist

- **Placement vs early returns**: a migration method with an early `return` on the healthy
  path (e.g. "FTS up-to-date → return") makes code appended at the end **dead code on healthy
  banks**. Pin exact placement; restructure the early return to guard only its own block; run
  new migration blocks last, each in its own transaction (no nesting).
- **Trigger cleanup on dedupe DELETE**: verify FTS/vec/embedding delete-triggers exist and
  fire, else dedupe orphans index rows.
- **Global uniqueness vs bucket-scoped re-read**: if the UNIQUE key is global (e.g.
  `(path, hash)` across projects) but the post-DO-NOTHING re-read is scoped by `project_id`,
  the losing writer's re-read returns NULL and it **throws** instead of returning the winner.
  Decide + document: fallback global re-read, or accept the loud failure (self-heals next
  pass if in-process dedup exists).
- **First-open concurrency**: `BEGIN IMMEDIATE` + `busy_timeout` serializes racing
  migrations; loser's index-existence guard + IF NOT EXISTS make it a no-op. Guard both index
  names if the block may grow.
- **FK pragma interplay**: with `PRAGMA foreign_keys=ON`, a dedupe DELETE is safe only if
  deleted rows can't be FK-referenced (e.g. a workspace-XOR-scope CHECK guarantees
  `workspace_id IS NULL` on the rows being deleted).
- **Tombstone reasoning**: sync layers with `(hash, scope)` tombstones cannot tombstone a
  dedupe delete — the kept row shares the hash. Residual (replica re-pushes the dup,
  converges on next write) is the correct accepted risk; verify the tombstone key shape first.
- **Scope-of-coverage claims**: check that the rows the plan says are protected are actually
  inside the index partials — e.g. chunk rows are only covered if the ingest path resolves to
  a committed scope (context null → project scope), not a workspace scope. Trace the caller,
  don't take the plan's word.

## Scratch-verification recipe (5 min)

```bash
cd /tmp && rm -rf sqlscratch && mkdir sqlscratch && cd sqlscratch
python3 - <<'EOF'
import sqlite3
# minimal entries-like table; test: DO NOTHING vs CHECK/NOT NULL/FK/UNIQUE,
# last_insert_rowid after swallowed insert, CREATE TRIGGER on missing column + fire it,
# UNIQUE-with-NULLs vs GROUP BY dedupe, MIN(id) NOT IN dedupe, partial index + bare DO NOTHING
EOF
```
Bundled SQLite version: `strings libe_sqlite3mc.dylib | grep -i sqlite` (partial indexes
≥3.8, expression indexes ≥3.9 — ancient, rarely a risk).

## Reporting shape

Numbered findings with MUST-FIX / SHOULD-FIX / NIT severities, file:line evidence, an
approve-with-changes verdict, and owner questions for every decision the plan left open
(cross-project race failure mode, migration placement, test-seed shapes).
