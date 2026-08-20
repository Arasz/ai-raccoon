# FTS5 + vec0 RRF implementation notes (measured in the P6 session, 2026-08)

Session: native-memory-store P6 — hybrid search over a C#/.NET 10 SQLite store (FTS5 external-content table + vec0 virtual table), fused with weighted RRF. Corpus: 174 real docs, 68 graded queries. All numbers measured on osx-arm64.

## The AND → OR fix (the one that made the harness pass)

First implementation normalized the user query to alphanumeric tokens joined with spaces — FTS5 implicit AND. Result: nDCG@10 = 0.6002 **at every one of the 9 sweep points** (k ∈ {10,30,60} × weights { (1,1), (1,2), (2,1)}). The tell was
not the low score; it was the *identical* score everywhere. Natural-language queries like
"What does the project decide or document about: ADR-0001 — Versioning and release model?" AND-match nothing (no doc contains "what does the project decide document about …"), so FTS returned zero rows and every point was vector-only.

Switching to `string.Join(" OR ", tokens)` gave:

| point   | nDCG@10 (OR) | Δ vs reference |
|---------|--------------|----------------|
| k10-w11 | 0.6639       | +0.0388        |
| k10-w12 | 0.6353       | +0.0102        |
| k10-w21 | 0.6880       | +0.0629        |
| k30-w11 | 0.6693       | +0.0442        |
| k30-w12 | 0.6540       | +0.0289        |
| k30-w21 | 0.6868       | +0.0617        |
| k60-w11 | 0.6719       | +0.0468        |
| k60-w12 | 0.6647       | +0.0396        |
| k60-w21 | 0.6857       | +0.0606        |

Reference (pinned sqlite-memory 1.3.5 + GGUF): nDCG@10 0.6251. So OR-join flipped the new side from −0.025 below to +0.01…+0.06 above the reference at every point. BM25 still ranks the OR matches sensibly (docs with more rare query terms
first), so RRF gets a good keyword list, not noise.

## Normalizer shape (safe by construction)

```csharp
[GeneratedRegex(@"[\p{L}\p{N}_]+", RegexOptions.CultureInvariant)]
private static partial Regex TokenRegex();

// Reserved words that FTS5 would parse as operators, not terms.
private static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    { "and", "or", "not", "near" };

// tokenize -> drop reserved -> lowercase -> join with " OR "
```

Why each piece:

- Token regex only: `:` in "about: ADR-0001" parses as an FTS5 *column filter*
  → `no such column: about` at MATCH time. Quotes would start a phrase. Emitting only alphanumeric runs means the MATCH string can never contain FTS5 syntax.
- Reserved words: a bare `and`/`or`/`not`/`near` token changes the query's boolean shape or errors (`NEAR(` needs an argument). Note this also silently drops the English words "and"/"or" from BM25 scoring — acceptable for a recall modality.
- Empty normalized expression (punctuation-only query): skip the FTS list entirely, never run `MATCH ''` (syntax error). The vec list still runs → vector-only COALESCE.
- Lowercasing matches the unicode61 tokenizer's case-folding; harmless.

## Two-layer fusion (scope-multiplexed stores)

Per context: `Fuse([(ftsList, ftsWeight), (vecList, vecWeight)], k, minScore:0, limit:int.Max)`. Then merge the per-context fused batches with the SAME fusion at uniform weight 1, applying the real minScore + limit once at the end.
Rationale: per-context modality weights shape each context's ranking; the cross-context pass is rank-based so the intermediate (un-normalized) scores are irrelevant — only batch order matters.

Fuse implementation notes:

- scores[hash] = Σ over lists of weight/ (k+rank); rank is 1-based position.
- Keep a payload map (first list wins) — the FTS list passes first, so its
  `snippet()` payload survives for docs both modalities retrieve.
- Normalize: ranking = score / maxScore (top = 1.0 exactly), then `>= minScore`, then `OrderByDescending(ranking).ThenBy(path)` for deterministic ties, then Take (limit).
- `with { Ranking = ... }` on a record works for the normalized copy.

## vec0 details that mattered

- Stored blob = float32 LE bytes (write with `BinaryPrimitives.WriteSingleLittleEndian`); the vec0 table accepts the same bytes via `INSERT INTO vec_entries(rowid, embedding)`.
- Query: `SELECT e.hash, e.path, substr(e.value,1,160) AS Snippet
  FROM vec_entries v JOIN entries e ON e.id = v.rowid WHERE {scopeFilter}
  ORDER BY vec_distance_cosine(v.embedding, @queryVector), e.path LIMIT @n`. Row position in this ordered result IS the vec rank. `@queryVector` = the query embedding serialized to the same blob format.
- Do NOT try `snippet(entries_fts, …)` in a scalar subquery for vec-only hits — FTS5 aux functions need a MATCH query context and throw. Plain teaser instead.
- vec0 rows follow embed_state via triggers (delete-then-insert on re-embed) — an un-embedded (pending) row is invisible to the vec list by design, which is also the clean way to test weight behavior (a pending doc is FTS-only; an embedded
  doc with no keyword overlap is vec-only; weights 2:1 vs 1:2 flip the winner deterministically: 2/ (k+1) vs 1/ (k+1)).

## No-engine degradation (absent modality must not crash)

When no embedding provider is configured, the store skips the query embedding and the vec list entirely → FTS5-only results, normalized so the top result is 1.0, still above minScore 0.7 by construction. The E2E contract "keyword search
works with deferred writes" is preserved because KNN over an empty vec_entries also yields an empty vec list (COALESCE).
