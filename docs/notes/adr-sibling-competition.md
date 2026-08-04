# ADR Sibling Competition — Problem & Solutions

> Extracted from retrieval improvement plans A, B, and C (2026-08-04).

## The Problem

ADRs are chunked by heading sections — Status, Context, Decision, Consequences, etc. Each section becomes an independent chunk. When a query targets a specific ADR section, three forms of competition degrade ranking:

1. **Intra-document competition:** The "Decision" section of ADR-0067 competes against its own "Context" and "Consequences" sections. They all share keyword overlap with the query topic. The system treats each as an independent candidate with no awareness they're siblings.

2. **Inter-document competition:** ADR-0067 (erasure) competes against ADR-0068 (also about erasure). Both have chunks with similar keywords. Without document-level identity, the system can't say "these are from different documents on the same topic."

3. **Cross-type competition:** A short, keyword-dense document like README.md beats a long ADR chunk purely on BM25 length normalization. The ADR chunk carries more information but gets punished for its length.

### Concrete Casualties

| Query | Expected | Actual (FTS-only, committed DB) | Failure Type |
|-------|----------|-------------------------------|--------------|
| "How does data erasure work?" | ADR-0067 | ADR-0068 at rank 1, ADR-0067 at rank 4 | Inter-document |
| "What is ADR-0070 about?" | ADR-0070 | NOT FOUND (README wins) | Cross-type (length) + token spread |
| "offer-page fetching security" | ADR-0006 | Rank 1 (surprisingly) | Works — distinctive token saves it |
| "What does ADR-0011 decide?" | ADR-0011 Decision | Untested — section targeting can't work yet | Intra-document |

## Why It Happens

### The system knows siblings exist but can't use that knowledge

All chunks from the same file share the same `path` column. The pipeline *has* the data. But:
- `MemorySearchResult` carries no `SourceFile`, `ChunkIndex`, or `TotalChunks` — the agent can't group results
- RRF fusion operates on individual chunk hashes with no document-level signal
- Two high-scoring chunks from the same ADR are treated as independent confirmation — they compete instead of cooperate

### BM25 length normalization punishes long chunks

ADR-0070 has 19 chunks. Only ONE contains the token "0070" (the title chunk). The content lives in the other 18. BM25 length normalization then ranks short docs (README.md) above the ADR because the ADR's chunks are long and the signal token ("0070") is sparse across them.

### FTS query construction drowns the signal

`"What is ADR-0070 about?"` → `what OR is OR adr OR 0070 OR about`

The stopwords `what`, `is`, `about` match nearly every document. BM25 then ranks by length, so the shortest match wins. ADR-0070's title chunk (containing "0070") loses to README.md (containing "what" + "is" + being shorter).

## Solutions — Layered

### Layer 0: Fix the measurement first (Plan B §0, Plan C Wave 0)

None of the solutions below can be verified until the baseline is reproducible. The committed DB has 0 embeddings and 71% pollution from excluded `docs/work/`. Generate a clean canonical corpus via `ingest-jsaa-docs.py` with structured paths and explicit exclusions first.

### Layer 1: Query Construction — cheapest, no schema changes (Plan C Wave 1)

**Stopword removal** in `FtsQueryNormalizer`: strip `what, is, the, how, does, about, are, do, can, should, will, would, could, has, have, been, was, were, being, a, an, in, on, at, to, for, of, by, with, from`

ADR-0070 query: `what OR is OR adr OR 0070 OR about` → `adr AND 0070`

**AND for identifier queries:** detect `\bADR-\d+\b` → emit `adr AND <number>`. For short queries (≤4 tokens): implicit AND instead of OR.

**Diagnostic triplet** to isolate failure mode:
- Q1 "What is ADR-0070 about?" (full question — current failure)
- Q2 "ADR-0070" (identifier-only — isolates tokenization)
- Q3 "documentation structure trust model" (content-only, no number — isolates content spread)

If Q2 works but Q1/Q3 fail → tokenization problem (solved by stopwords). If all fail → content spread problem (needs Layer 3).

**Effect:** Noise tokens disappear. BM25 concentrates on the real signal. ADR-0070's title chunk (containing "0070") no longer competes against README.md (containing "what" + "is").

### Layer 2: Source Identity — give the system self-awareness (Plan C Wave 2)

**Schema:**
```sql
ALTER TABLE entries ADD COLUMN source_file TEXT;
```
For ingested files: set to relative path (`docs/adr/0067-registry-driven-erasure.md`). All chunks from the same file share it. For `memory_write` entries: NULL.

**Results API:**
```csharp
public sealed record MemorySearchResult(
    string Hash, int Seq, double Ranking, string Path,
    string Snippet,
    string? SourceFile,      // original file path
    int ChunkIndex,           // 0-based position within source
    int TotalChunks           // total chunks in source file
);
```
An agent can now tell: "this is chunk 2 of 4 from ADR-0067" vs "this is chunk 1 of 3 from ADR-0068."

**FTS source column:** Add `source_file` as a weighted FTS column. A query for "ADR-0067 decision" can now match the *source path* (`docs/adr/0067-...#decision`), not just body text. FTS5's `bm25(fts, weight_content, weight_source)` per-column weights control how much source identity matters vs content match.

**Stop embedding provenance in content:** The `## Source:` header and `[context]` prefix currently live in chunk text — they pollute BM25 scores (path tokens count as content tokens) and make hash matching fragile. Move them to `source_file` and stop prepending.

**Context labels searchable:** `memory_write(context="docs:adr")` currently stores `scope='custom'` — invisible to search. Either include custom-scope rows in search or map labels into project scope with a filter.

### Layer 3: Source-Affinity Scoring — structural ranking boosts (Plan C Wave 3)

**Adjacent chunk boost:** If chunk N from source S ranks well, chunk N±1 from the same source gets a boost (λ ∈ {0.05, 0.1, 0.2}). Adjacent chunks tend to be semantically continuous. This directly addresses intra-document competition: the Decision chunk that matches the query also pulls its Context and Consequences siblings up in rank, because they're nearby in the same document.

**Source consolidation:** For each source file, take the best-scoring chunk and optionally merge its adjacent siblings into a single result. Two chunks from ADR-0067 that both score well stop competing for ranks — they collapse into one "ADR-0067" result.

**Document-first ranking:** After per-chunk scoring, compute a document score = max(chunk scores) or mean(top-3 chunks). Use as a secondary sort key. Documents with any strong match rank as a block before documents with only weak matches. ADR-0067 (with one strong chunk) ranks as a unit above ADR-0068 (with only weak matches across all chunks).

**BM25 length normalization:** Long ADR chunks lose to short docs purely on length — this is why README.md beats ADR-0070 even after stopword removal if "0070" is sparse. Options: reduce chunk max tokens for ADR-like content, accept it and let source-column matching carry identifier queries, or test FTS5 column weights to zero-out length normalization for source matches.

### Layer 4: Parameter Tuning (Plan C Wave 4)

Once the structural boosts are in place, sweep the fusion parameters:
- RRF k ∈ {10, 30, 60, 120} — lower k rewards top ranks harder (less sibling dilution)
- Weight ratios (1:1), (1:2), (2:1) — vector-heavy favors semantic queries, FTS-heavy favors identifier queries
- Candidate window: max(limit×3, 100) vs max(limit×5, 50) — smaller window reduces noise from 6675+ entries competing for the same ranks

## The Specific Fix for ADR-0067 vs ADR-0068

"How does data erasure work?" currently returns ADR-0068 at rank 1, ADR-0067 at rank 4. The fix chain:

1. **Wave 1 (query construction):** No change — "erasure" is already a good signal token, stopwords don't hurt this query. The problem is structural, not lexical.

2. **Wave 2 (source identity):** The system can now see that ADR-0067's chunks and ADR-0068's chunks are from different files. Results carry `SourceFile`, `ChunkIndex`, `TotalChunks`.

3. **Wave 2c (FTS source column):** The source path `docs/adr/0067-registry-driven-erasure.md#decision` is indexed. A refined query referencing the ADR number would match the source column directly.

4. **Wave 3 (adjacent boost):** ADR-0067's Decision chunk at rank 4 — if it's adjacent to the chunk that scored at rank 1 within its own file — gets boosted. Its siblings stop competing against it.

5. **Wave 3 (document-first ranking):** If ADR-0067 has even one chunk scoring well, it ranks as a block above ADR-0068 even if 0068 has more keyword matches spread thinly.

**Target:** Both ADR-0067 and ADR-0068 in top 3 for "erasure" queries.

## The Specific Fix for ADR-0070

"What is ADR-0070 about?" currently NOT FOUND (README.md wins). The fix chain:

1. **Wave 1 (stopwords):** `what OR is OR adr OR 0070 OR about` → `adr AND 0070`. Only chunks containing BOTH "adr" AND "0070" match. README.md drops out immediately.

2. **Wave 1 (AND semantics):** Under the new AND-for-short rule, this 3-token query after stopwords ("adr", "0070", "about" — "what" and "is" stripped) becomes `adr AND 0070 AND about`. But wait — "about" survived stopword removal? No: "about" IS in the stopword list. So the query is `adr AND 0070` — two tokens, both signal.

3. **Wave 1 (identifier detection):** `\bADR-\d+\b` pattern match on the original query → explicit `adr AND 0070` clause, bypassing the general rewrite logic entirely.

4. **Wave 2c (FTS source column):** The source path `docs/adr/0070-documentation-structure-trust-model.md` is indexed. A query for "0070" matches the source column directly, not just content text. Even if only 1 of 19 chunks contains "0070" in body text, ALL 19 chunks match through their shared source path.

5. **Wave 3 (BM25 length normalization):** If the title chunk containing "0070" is still penalized vs shorter docs, FTS column weights can zero-out length normalization for the source column: `bm25(fts, 0.0, 0.0, 1.0)` means source matches are pure boolean — match wins, no length penalty.

**Target:** ADR-0070 found at rank ≤3. Any chunk from ADR-0070 qualifies as success.

## What Doesn't Need Changing

- **The MarkdownChunker** — line-granular, code-fence-aware, token-bounded. Correctly produces section-level chunks. The problem isn't chunking quality; it's that the retrieval pipeline treats siblings as strangers.
- **The chunk size** (256 tokens) — correct for the bundled all-MiniLM-L6-v2 model's 256-token context window. Changing size requires changing the embedding model first.
- **RRF fusion** — the rank fusion algorithm itself is sound. The problem is it operates on individual chunk hashes with no document-level signal. The fix is adding that signal (Layer 2-3), not replacing RRF.
- **The embedding model** — all-MiniLM-L6-v2 is adequate for this task. Upgrading to text-embedding-3-small would help with conceptual queries but is orthogonal to the sibling competition problem (which is structural, not semantic).

---

*From: Plans A §2.1, A §4.1, A §4.2D-E, A §4.3G-I, B §2.3-2.4, B §4.2-4.4, C §2.2-2.4, C Waves 1-5*
