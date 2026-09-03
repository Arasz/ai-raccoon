# 0097. `search_quality` grows a `kind` column; backfill stops at the kind landing

Date: 2026-09-03

Status: Accepted

Ships the honest repair ADR-0094 deferred (its Consequences, fourth negative): `result_count`
means different things by kind and the row gave no way to tell which. This record adds the
column, wires it through every layer, and backfills only what is knowable.

## Context

Three facts set the shape. First, every pre-kind row ran the memory leg. Kind arrived with
the code corpus work; before that there was one leg and one meaning. A backfill can therefore
name `memory` for old rows without guessing. Second, the landing commit is known:
`git show -s --format=%ct 356afe95` reads `1788447366`. Rows older than that second predate
kind entirely. Rows at or after it do not, and their kind arrives on the write path or not at
all. Third, the eval corpus cannot adjudicate the design. It holds 181 rows, 140 of them
grade-2 against 18 grade-5, with a single follow-through in its whole history, and no kind
labels at all. Nothing in it distinguishes one kind from another, so the fixture seeds its
own grades and follow-through rather than borrowing a signal the corpus never had.

P1 (required `sessionId`, end to end) landed directly underneath. Kind follows the same
plumbing: a required attribution string beside the session, recorded on every row.

## Decision

1. **Nullable `kind TEXT` with `CHECK(kind IN ('memory','code','both'))`.** Nullable because
   post-cutoff legacy rows are genuinely unknown, and NULL says so where a guess would lie.
   The CHECK is the backstop, never the validator. No index. Kind is a label for later
   grouping, not a lookup key, and an index on a three-value column buys nothing.
2. **The service takes kind as a required string, after `projectId`, before `sessionId`,
   on both verbs.** `RecordSearchAsync` guards fail-fast (`ArgumentException` on anything
   outside the three values, before any bank work). `RecordSearchSafeAsync` keeps its
   never-throws contract and swallows into the existing log event. The dispatcher passes
   `SearchKind.ToString().ToLowerInvariant()`, so the enum stays the single vocabulary and
   the wire string is derived, never hand-typed twice.
3. **The row names the request, not the leg.** `memory` records `memory`, `code` records
   `code`, `both` records `both`, even though a `both` row still describes the memory leg
   per ADR-0094. This is deliberate: the request is what the agent chose, and grouping later
   by choice is the question the column exists to answer. Counts keep their ADR-0094
   meanings; the column only says which meaning applies.
4. **Backfill is one rung, `v11→v12`, and one UPDATE.** `UPDATE search_quality SET
   kind = 'memory' WHERE kind IS NULL AND created_at < 1788447366`. Strictly older-than:
   ties stay NULL. `kind IS NULL` only: an explicit kind is never clobbered, so reruns are
   idempotent. A set UPDATE with no row reads, so an empty table is a no-op, not an edge.
5. **The rung heals what the digest gate cannot see.** The digest hashes the Ddl string, so
   a runtime DROP leaves the digest current and the Ddl block skipped. A v11 bank with the
   table missing recreates it from the same `SearchQualityTableDdl` const the Ddl block
   interpolates (one definition, two call sites) and returns. The column ensure is
   probe-first inside one `BEGIN IMMEDIATE`: single-writer by construction, a crash rolls
   back to the intact v11 shape, and the next open retries.
6. **Everything else is untouched.** Grades, follow-through, promotion scoring, the sync
   snapshot (kind rides with the row, the same leak class as the query text, not a new
   one), `MemoryTools`, `user_version`/digest stamping, and the product version. Fresh
   banks get the column from Ddl; the 58-statement digest block is unchanged in count.

## Consequences

- **Positive**: counts are comparable within a kind again. The question ADR-0094 left open
  ("do not compare counts across kinds blindly") now has the label that makes the
  comparison careful instead of forbidden.
- **Positive**: NULLs are honest. A post-cutoff legacy row reads unknown until a new write
  replaces the question, instead of wearing a backfilled `memory` it never earned.
- **Negative**: `both` rows keep the ADR-0094 asymmetry (memory-leg counts under a `both`
  label). A consumer joining counts to kinds must know the leg rule, not just the label.
  Accepted: the alternative (recording the code leg too) re-opens the path-storage question
  0094 closed.
- **Negative**: every existing bank reruns the digest block once (the Ddl string changed,
  so the digest did). It is 58 statements of mostly no-ops plus one ladder UPDATE. The
  count is pinned, both sides.
- **Not addressed**: stripping telemetry from the sync snapshot (0094's bigger fix, still
  owed); per-kind retrieval tuning; any index, if a kind-filtered query ever needs one.
