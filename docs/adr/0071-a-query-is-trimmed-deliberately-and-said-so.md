# 0071. A query is trimmed deliberately, and says so

Date: 2026-08-15

Status: Accepted

Amends ADR-0036, which introduced the embed-time truncation detector. The detector stands; what it
could not distinguish does not.

## Context

Live warning, on a running 1.17.0 server:

```
warn: AiRaccoon.Infrastructure.Embedding.EmbeddingService[414]
      Chunk truncated at embed time: 407 BERT WordPiece tokens exceed the bundled model's 256-token window
```

**No stored entry was over the window.** Measured against the live bank at the time: 0 rows above
254 tokens, out of 24,674. The largest row is 12,952 characters and tokenises to **249** — a markdown
table, whose whitespace padding collapses.

The 407 tokens were a **search query**. `OnnxEmbeddingGenerator.Encode(string)` is a single choke
point that every embed call funnels through, and `EmbedQueryAsync` reaches it exactly like the write
path does. So the generator truncated the query to 256 tokens, silently, and reported it as a
truncated *chunk*.

**Three defects in one line:**

1. **The trimming was silent to the caller.** The tail of a query was dropped from its vector and
   nothing said so — the search simply matched on less than was asked.
2. **The message named the wrong thing.** "Chunk truncated" for something that is not a chunk cost
   four separate measurements to pin down.
3. **It made an acceptance criterion unmeasurable.** WP3 requires *"EventId 414 count is zero over a
   day of normal ingest"*. That can never reach zero while queries share the event, and a non-zero
   count no longer proves a chunking defect. A gate that can only fail is not a gate.

## Decision

**The query path trims before the generator sees the text, and reports it in its own words.**

`IEmbeddingService.TrimQueryToWindow` trims a query to `MaxContentTokens` using `TokenBudget.Trim` —
the same binary-search-on-characters the chunker uses, lifted into Core as a pure function so both
callers share one definition. `EmbedQueryAsync` calls it before generating.

Consequences by design:

- **414 becomes what it always claimed to be**: stored content only. WP3's criterion is measurable
  again, because a non-zero count now means exactly one thing.
- **416 is the query event**, separately countable, so "are we cutting queries?" and "are we cutting
  entries?" stop being one number.
- The generator is unchanged. It still truncates whatever it is handed — it simply is no longer
  handed an over-length query.

**Both messages are written for the person reading the log**, not for the person who wrote the code:

> Search query was shortened to fit the embedding model: 407 tokens exceeded the 254-token window, so
> only the first 1,203 of 1,890 characters were used to find matches. Results may miss what the rest
> of the query asked for — use a shorter, more specific query.

> A stored entry was shortened before embedding: 407 tokens exceeded the 254-token window, so the
> tail of that entry is missing from its search vector. The entry's text is intact; only what search
> matches on is short. Queries are trimmed separately and reported as event 416 — this one is always
> a write or an ingest.

Each says what happened, what it costs, and what to do. The second also says which path it *is not*,
because that ambiguity is the whole reason this record exists.

## Consequences

**Watched fail:** reverting to generator-side truncation turns two of the four tests red — the one
asserting 414 does not fire for a query, and the one asserting the message is useful. The other two
(a short query is untouched; a query exactly on the limit is untouched) stay green, which is the
separation they are for.

**The off-by-one is pinned.** A query at exactly `MaxContentTokens` must not be trimmed; the fixture
asserts it sits exactly on the limit first, so the test cannot pass by testing a query that was never
near the boundary.

**A non-local provider is not trimmed**, because the window is the bundled model's. A remote model
with a different window would need its own budget, and guessing one would be worse than not trimming.

**What this does not do:** it does not make a long query work. 407 tokens still become 254 — the
query is simply cut knowingly and audibly instead of silently. Embedding a long query faithfully
(chunk it, pool the vectors) is a retrieval-semantics change that needs measurement before it ships,
and it is not this record.

## Amendment (2026-08-22) — the query-trim event is 418, not 416

Everything this record decided stands; only the number moved. `OnnxEmbeddingGenerator`
owned 414-415 and sat wedged between `BundledModel`'s 413 and this record's 416, so it had
nowhere to put the new event #466 needed (417, the graph-pools-its-own-output warning) —
the same wedge that moved `MetricsFlusher` off 962-964. The neighbour moved instead of the
grower because 414 is named by number in three docs and a live test constant, and 416 in
two: `EmbeddingService.Log.QueryTrimmedToWindow` is now **EventId 418**, and
`QueryTruncationTests.QueryTrimmedEventId` follows it.

**416 is retired, not reused** — nothing will be given that number again, so a log line
reading `[416]` is always this event from a build before 1.32.0. The split this record
argued for is unchanged: the stored-content event (414) and the query event (418) are still
two countable ids, which was the point.

## Amendment (2026-08-23) — the query-trim event is 426, not 418

The number moved again, same reason as before: `EmbeddingService`'s own block (418-419) sat
wedged against `NoOpCodeChunker`/`CodeEmbedder`/`ManifestPoolingRepair` (420-425) with no
room to grow for #522's new session-created event, so the whole block relocated rather than
orphaning that event in a type of its own. `EmbeddingService.Log.QueryTrimmedToWindow` is
now **EventId 426**; `QueryTruncationTests.QueryTrimmedEventId` follows it. **418 is retired,
not reused** — see `docs/reference/logging-event-ids.md`.
