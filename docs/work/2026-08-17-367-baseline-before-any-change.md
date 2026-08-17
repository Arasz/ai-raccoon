# #367 baseline, measured before any change

Date: 2026-08-17
Base commit: `e4384ab0` (VERSION 1.22.0), before WP1–WP7.

This is the **pre-change baseline** the WP2 decision point compares against. It exists so that a
later improvement can be attributed rather than assumed.

## What was measured

The live bank (`~/.ai-raccoon/memory.db`, 25,995 entries) through the running server, via
`memory_search` with `projectId=ai-badger`, `scope=project`, `limit=20`. Search is read-only; the
bank was never opened read-write. A WAL-safe copy for destructive work lives outside the repo and
was made with `VACUUM INTO`, not `cp` — the copy read 25,995 rows against 25,993 in the main file,
so the WAL was carrying writes a plain file copy would have dropped.

The legs were isolated the way #367 prescribes: `vectorWeight=0` for FTS-only, `ftsWeight=0` for
vector-only.

**Query** (a paraphrase — deliberately does *not* contain the literal term `clipped`):

> the stored query text was truncated at the field cap so only a prefix was saved, not the whole query

**Targets.** Both chunks of `/Users/arasz/RiderProjects/ai-badger/docs/retrieval.md` that carry the
row `| clipped | The stored text hit the 200-char field cap, so it is a prefix, not the query. |` —
`chunk_index` 7/64 (`entries.id` 18336) and 9/64 (18340). The row appears in both because the
48-token overlay duplicates it across the boundary.

## Result

| leg | chunk 9/64 | chunk 7/64 |
|---|---|---|
| FTS-only (`vectorWeight=0`) | **4** | 6 |
| Vector-only (`ftsWeight=0`) | 14 | absent from top 20 |
| Default hybrid (1:1, k=60) | **6** | 12 |

**Hybrid ranks the target below FTS-only** — 6 against 4. By ADR-0006's own definition
(`0006-rrf-parameter-optimization.md:49-50, 67-72`) that is a fusion regression: *the hybrid never
ranks the expected chunk below the best single modality*. The rule holds on all 11 tuning queries
and does not hold here.

## Why this observation is worth more than the one in the issue

#367 rested on a single query, and a single query is precisely what ADR-0072 declined to act on.
This is a **second, independent query** exhibiting the same direction on the same corpus, found
without tuning anything toward it. It does not make the effect adjudicable — two observations are
not an evaluation — but it removes "one anomalous query" as an explanation.

The magnitude is much milder than the issue's (FTS 1 → hybrid 18). The issue's exact query text was
not recorded, so this is a different point in the same failure mode, not a reproduction of that
measurement. **Do not quote 4→6 and 1→18 as though they are the same experiment.**

## What this baseline is for

WP2 (#371, `chunk_index` becomes document order) ships first and changes the adjacency signal that
`SourceAffinityRanker` reads. Re-running exactly this measurement afterwards answers whether the
ordering defect was contributing to #367 — before any chunking or fusion change exists to take the
credit.

Re-run identically: same query string, same `projectId`/`scope`/`limit`, same three weight settings,
same two target chunks identified by hash rather than by rank.

- chunk 9/64 → `0eb20831923593b5c2ac04c13769c9a8b0b2d13020765e30be022b7006a8ba78`
- chunk 7/64 → `cc697fdd070bd84daa24eb9e94658e11b7ab98dab72db6f551de15fa1c75f1bd`

Identify by hash: ranks move, which is the point, and a target identified by its rank cannot be
followed across a change.

## Still open

- The issue's original query text is unrecorded, so its 1 → 18 measurement cannot be reproduced
  exactly. If it matters, it has to be re-derived from the issue author's session rather than guessed.
- One paraphrase is not a query set. Whether the effect generalises is WP5's question, not this
  record's.
