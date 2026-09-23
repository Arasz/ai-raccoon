# 0109. code_fts carries a derived identifiers column

Date: 2026-09-23

Status: Accepted

Plan: `docs/work/2026-09-23-code-retrieval-eval-plan.md` §P2.

## Context

Plain FTS5 tokenization never splits a camelCase, PascalCase, snake_case or kebab-case
identifier: `WatchOverlapResolver` is one token to `code_fts`, so a query for `overlap` — the
natural way an agent describes a symbol it half-remembers — never matches the chunk that defines
it. The plan's P2 spike (`scripts/retrieval_tuning/spike_p2_identifiers.py`, run against a copy
of the B1 baseline bank with its `watches` rows deleted) measured what a derived,
identifier-split keyword column buys: held-out nDCG@5 **+0.052** (95% CI [0.021, 0.088]), with
the identifier-fragment query category alone improving by **+0.110**. Both clear the keep/drop
bar (P2 §"Keep/drop rule": CI lower bound above 0 and the noise band, mean gain ≥ +0.02, target
category improves, no floor violated) — the spike's verdict is **KEEP**.

## Decision

**`code_entries` gains an `identifiers TEXT NOT NULL DEFAULT ''` column, and `code_fts` becomes
a 3-column `fts5(value, source_file, identifiers, content='code_entries', content_rowid='id')`
— equal bm25 weights, no column weighting, no query-side change.** `MATCH` stays unqualified
(`SqliteCodeSearchService`'s `WHERE code_fts MATCH @match`), so the new column is picked up by
every existing query for free.

**`IdentifierSplitter` (`src/AiRaccoon.Core/Ingestion/IdentifierSplitter.cs`), a pure static
class, is the single source of the split.** `Split(string)` matches the Python reference
`split_identifier` (`scripts/src/retrieval_tuning/code_corpus.py`, the function P2's query-set
circularity guard also uses) byte-for-byte: boundaries are separators (`_ - .` and whitespace),
a lower-to-upper case transition, an acronym run followed by a new capitalised word
(`HTTPServer` → `http`, `server`), and letter/digit transitions, lowercased. `Identifiers(string
chunkText)` extracts every identifier-like token (`[A-Za-z_][A-Za-z0-9_\-]*[A-Za-z0-9]|[A-Za-z]`),
keeps only tokens that split into 2 or more parts, space-joins each kept token's parts,
deduplicates within the chunk, and newline-joins the result — the exact shape the spike's
harness computed in Python. `CodeIngestor` calls it once per chunk at insert time
(`MemorySql.InsertCodeEntry`); the dedup-rediscovery position-refresh path
(`UpdateCodeChunkPosition`) is unchanged, since a rediscovered chunk's text — and therefore its
identifiers — cannot have changed.

**Migration is additive and digest-gated, matching `code_entries`/`code_fts`/`vec_code`'s own
precedent (ADR-0085): no ladder step, no `CurrentVersion` bump.** Because a bare `ALTER TABLE
ADD COLUMN` is not idempotent under two connections racing the same digest mismatch, and an FTS5
external-content table's `CREATE ... IF NOT EXISTS` no-ops against an already-existing 2-column
`code_fts`, the migration runs in two steps inside `MemorySchema.EnsureAsync`'s digest-mismatch
branch, in order:

1. **`EnsureCodeIdentifiersColumnAsync`** — a pragma probe, then `ALTER TABLE code_entries ADD
   COLUMN identifiers TEXT NOT NULL DEFAULT ''`, catching "duplicate column" the same way
   `EnsureCodeEmbedAttemptsColumnAsync` does. Runs first: the second step's backfill reads this
   column.
2. **`EnsureCodeFtsIdentifiersAsync`** — skips entirely once `code_fts` already carries
   `identifiers` (a fresh bank, or an already-migrated one); a no-op here must never re-run the
   backfill, which would recompute every row's identifiers from its current `value` and silently
   discard whatever a caller wrote there directly. Otherwise, in one `BEGIN IMMEDIATE` (re-checking the column under the write lock, so a
   racing connection's finished rebuild is not repeated): backfill every row's `identifiers` from
   its `value` in C# (`IdentifierSplitter`), drop and recreate `code_fts` and its 3 triggers in the 3-column shape
   (`code_fts_au` now fires `AFTER UPDATE OF value, source_file, identifiers`), then run
   `INSERT INTO code_fts(code_fts) VALUES('rebuild')` — the same crash-safe shape as the
   `entries_fts` rebuild in `MigrateToV9Async`.

The fresh-bank DDL is updated in place (the `code_fts` `CREATE VIRTUAL TABLE` and its 3 triggers
gain the `identifiers` column); it takes effect only for a genuinely new bank, since `IF NOT
EXISTS` no-ops against an existing legacy shape — the migration above is what actually moves a
legacy bank.

## Consequences

- An identifier-fragment query (`"overlap resolver"` for `WatchOverlapResolver`) now matches
  through the derived column even though the chunk's raw text never contains that phrase.
- Every legacy bank pays a one-time backfill (proportional to its `code_entries` row count) on
  its first open after upgrading; a bank that never re-opens on this version never pays it.
- `code_entries.identifiers` is a derived, re-derivable value — like the rest of the code
  corpus (ADR-0085), it carries no independent source of truth and a bank rebuild can always
  regenerate it from `value`.

## Evidence

`scripts/retrieval_tuning/spike_p2_identifiers.py` (P2-A, harness-only, never merged), run
against a copy of the B1 baseline bank: held-out nDCG@5 **+0.052**, 95% CI **[0.021, 0.088]**,
identifier-fragment category **+0.110** — recorded in the task's results record per the P2
plan's keep/drop rule.

## Related decisions

- [ADR-0085 — A second, code-only corpus in the same bank](0085-a-second-code-only-corpus-in-the-same-bank.md):
  this ADR's digest-gated, no-ladder-step migration shape for `code_entries`/`code_fts`/`vec_code`
  is the direct precedent this one follows for `identifiers`.
- [ADR-0088 — Code search surface: `kind`, the `results`/`code` envelope, no cross-corpus
  fusion](0088-code-search-surface-kind-envelope-no-fusion.md): the query surface this decision
  changes nothing about — `MATCH` stays unqualified, so no tool contract moves.
