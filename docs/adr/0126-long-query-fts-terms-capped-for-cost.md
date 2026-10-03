# 0126. A long query's keyword terms are capped, for cost

Date: 2026-10-03

Status: Accepted. Supersedes the "dedup" and "dedup + cap" rejections in ADR-0072.

## Context

ADR-0072 kept `FtsQueryNormalizer`'s long-query path unchanged: a query over four content tokens
became a plain OR of every raw token, stopwords and repeats included, with no limit on the count.
It turned down deduplication because "its remaining case is **cost** … and latency was not
measurable". It turned down a cap because no cap could be chosen on retrieval quality from a
held-out set of three queries.

The cost is now measured, and it is the failure. A caller sending 6,000-7,000-character queries
had both of its `memory_search` calls cancelled by the client, at 15.5 s and 8.6 s. The keyword
leg was the cause, not the embedder or the WAL. Production `MATCH … ORDER BY bm25(…) LIMIT` ran
on a snapshot of the live bank (22,450 entries, no context filter), using prefixes of
`CLAUDE.md` as the query:

| query chars | OR terms, current | ms, current | stopwords dropped + deduped | + capped at 64 |
|---|---|---|---|---|
| 1,000 | 139 | 1,710 | 81 terms / 488 ms | 64 / 170 ms |
| 4,000 | 562 | 18,326 | 284 / 2,250 | 64 / 224 |
| 7,259 | 968 | 74,301 | 472 / 2,215 | 64 / 286 |
| 20,000 (ADR text) | — | — | 903 / 3,040 | 64 / 199 |
| 60,000 (ADR text) | — | — | 1,884 / 6,418 | 64 / 113 |

Two sessions measured these, each on a different run. The "current" and "deduped" columns at
1,000-7,259 characters come from the first run. The capped column and the 20k/60k rows come from
the second, a warm-cache run at load average ~4 that reported the deduped 7,259 row at 1,372 ms.
Read the table as orders of magnitude, not as a benchmark. Two things are clear from it anyway:
the uncapped cost grows much faster than the query does, and deduplication alone does not
bound it (6.4 s at 60k characters).

Nearly every row contains `the`, `a` or `to`, so a pasted query matches nearly the whole bank,
and `bm25()` scores every match before `LIMIT` applies.

## Decision

The long-query OR join is capped at `FtsQueryNormalizer.MaxOrTerms` = **64** terms.

- A query of **≤ 64 raw tokens** keeps the existing verbatim OR join, stopwords and repeats
  included. All 44 catalog queries are ≤ 10 raw tokens (ADR-0072), so every gate query takes
  this path and its plan is byte-identical. The stopword-bearing ranking that
  `BuildPlan_LongQueryOrPrimary_RetainsStopwords` pins (`"Why was X chosen?"`) is kept.
- Above that, the plan **drops stopwords**, **drops repeats** (first occurrence wins) and keeps
  the **first 64** distinct terms.
- `QueryLengthGuard`'s caller-visible warning now says the keyword leg is capped too, instead of
  "Keyword (FTS) matching still searches the query in full."

The cap is a **cost bound, not a quality claim**. ADR-0072's quality findings stand:

- A pasted query's keyword leg scores 0.0000 nDCG@5 uncapped (Result 1), so the cap has nothing
  measured to lose there.
- First-N keeps the wrong words when the question is not at the start (Result 3).
- No cap number is adjudicable on quality (Result 4).

64 was chosen on latency alone. It is the largest of the timed caps (16, 32, 48, 64, 96, 128)
that kept every measured long query under 0.3 s. Code search shares `BuildPlan` and gets the same
bound.

## Consequences

- A long query's keyword ranking changes. A word repeated in the paste no longer counts more
  under `bm25`, and words past the 64th distinct one are not searched. The semantic leg is
  unaffected; it was already trimmed to the engine's window (ADR-0071).
- A query whose question sits after a long paste gets no keyword help from the question. That was
  already true in effect (ADR-0072 Result 1), and the warning now says so.
- What ADR-0072 asked for is still what would make any quality claim possible: a held-out family
  of long, pasted-output queries with expected documents pinned by someone else. Until then, any
  change to which 64 words are kept is a cost choice and should be described as one.
