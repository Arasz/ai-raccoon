# 0035. `memory_get`, plus query-relevant snippets and content-keyed dedup

Date: 2026-08-14

## Status
Accepted

## Context
The 2026-08-14 MoE codebase review (`docs/reviews/2026-08-14-moe-codebase-review.md`, blocker
B2) found that the 25-tool MCP surface had no tool that returns an entry's content by hash. The
only content an agent received for an ordinary search hit was `memory_search`'s snippet, and for
a vector-only hit that snippet came from `SnippetFallback.From`, which opened a 200-character
window at an offset derived from `SHA256(hash)` — deterministic but unrelated to the query. Mean
entry length in the gate corpus is ~2,850 characters, so the agent saw roughly 7% of the entry,
chosen arbitrarily; a term that made the entry match the query could easily fall outside the
window.

The review also found that a promoted shared-tier copy gets a different content hash from its
project original: `ContentHash.Of(path, value)` for the project row versus
`ContentHash.Of("shared/<sha256(value)>.md", value)` for the promoted copy (`ShareAsync` /
`AddContentAsync`). Dedup in `ModalityCandidates` happened on that row hash — the right stage,
but the wrong key — so `scope=all` returned both copies of the same text.

Separately, `MemorySearchResult` carried a `Seq` field hardcoded to `0 AS Seq` in all three search
SQL statements, with no consumer anywhere in the codebase.

## Decision

1. **Add `memory_get(projectId, hash)`.** Returns the full entry (`hash`, `value`, `path`,
   `context`, `createdAt`) for a hash reachable from the caller — the caller's own project/custom/
   workspace rows plus the cross-project shared tier, the same reach `BumpAccess` already grants
   search hits (`WHERE hash = @hash AND (project_id = @projectId OR scope = 'shared')`). An unknown
   hash throws `UnknownHashException`, which the existing `ToolRefusals` table already maps to a
   structured `unknown-hash` refusal — no new plumbing needed.

   `IMemoryStore.GetAsync` is added as a **default interface member** returning "not found"
   (mirroring `DeleteInScopeAsync`'s existing precedent), not a required member on all 16 test
   fakes that implement `IMemoryStore` — only `SqliteMemoryStore` and the fakes that specifically
   test `memory_get` need an override.

2. **Make the vector-hit snippet query-relevant.** `SnippetFallback.From` now takes an optional
   `query` parameter: when a query token literally occurs in the value, the window centers on the
   earliest such match; when no token matches (a genuinely semantic-only hit with no literal term
   overlap), it falls back to the prior hash-seeded window unchanged. Existing 2-arg call sites
   keep their exact prior behavior.

3. **Dedup a promoted shared copy against its project original, by content, not row hash.**
   `ModalityCandidates.ByBm25`/`ByCosine` take an optional `valueByHash` map (already computed by
   `SqliteMemoryStore.SearchAsync` for snippet resolution); when supplied, candidates are grouped
   by `ContentHash.OfValue(value)` instead of the row hash, and the project-scoped copy (path not
   under `shared/`) wins the group over a same-content shared-tier duplicate. Omitting the
   parameter — the case for every caller other than `SqliteMemoryStore` — degrades to the prior
   per-row-hash grouping, so this is additive.

4. **Drop the dead `Seq` field** from `MemorySearchResult` and the three search SQL statements
   (`SearchByFilter`, `VectorSearchByFilter`, `StructureVectorSearchByFilter`).

Amends ADR-0004 and ADR-0006 (which established `MemorySearchResult`'s shape and the RRF/dedup
pipeline `ModalityCandidates` sits in) by removing the unused field and changing the dedup key.

## Consequences
- **Positive:** an agent can now read a memory's full content by hash — search's snippet no
  longer has to double as the only read path.
- **Positive:** a vector-only hit's snippet is far more likely to contain the text that made it
  relevant, rather than an arbitrary slice of a long entry.
- **Positive:** `scope=all` no longer double-counts a promoted-and-original pair as two results.
- **Negative:** the "prefer the project copy" dedup rule is a policy choice — a caller who wants
  the shared copy specifically (e.g. to confirm what the shared tier holds) gets the project row's
  hash/path back instead when both exist. `memory_share`'s own read path (`memory_promotion_list`)
  is unaffected, since it does not go through `ModalityCandidates`.
- **Negative:** `SnippetFallback`'s query-token match is a literal, case-insensitive substring
  match, not a semantic one — a purely semantic hit with no literal term overlap still falls back
  to the hash-seeded window (unchanged from before this ADR).
- **Not addressed:** raising the FTS5 `snippet()` token count / adding real match delimiters (the
  review's other B2 sub-finding) is left for a later pass — it was not in this package's
  acceptance criteria and did not have a red test motivating it here.
