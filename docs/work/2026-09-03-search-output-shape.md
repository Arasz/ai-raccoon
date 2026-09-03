# Research: search output shape — snippets plus links, content behind get

**Date:** 2026-09-03
**Question:** What does memory_search return per hit — full content, or chunk links plus snippets?

## Findings

### F1 — memory_search returns links plus a short snippet per hit, never the full value [MEASURED]

A live `kind=memory, limit=2` search returned two hits shaped
`{hash, ranking, path, snippet, sourceFile, chunkIndex, totalChunks}` plus an empty
`code: []` section. The first hit's snippet was ~60 visible characters
("…the instruction layer remains the fallback for hosts without a hook surface…")
while `memory_get` on the same hash returned the full multi-paragraph value. The
snippet is an excerpt; the hash/path/sourceFile/chunk locators plus `memory_get`
are the path to the content.

**Evidence:** `memory_search` (projectId `cfe47dab-…`, query `snippet fallback window`,
`limit=2`, 2026-09-03, this machine) returned hashes `fb87aea5…` / `7a1c05d6…` with
short `snippet` strings and `code: []`; `memory_get` on `fb87aea5…` returned the full
`value` (Consequences section, ~900 chars) for the identical hash and path.

### F2 — Memory snippet text is a deferred ~200-char window: FTS5 snippet() for FTS survivors, hash/query-seeded fallback otherwise [READ]

Only post-ranking survivors get snippet text. FTS-originated survivors are resolved
with the native FTS5 `snippet()` query; every other survivor falls back to a
~200-char window that centers on the literal query match when present and on a
hash-seeded offset otherwise, slid to word boundaries with `…` affixes.

**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.cs:844-870` (`ResolveDeferredSnippetsAsync`); `src/AiRaccoon.Infrastructure/Sqlite/SnippetFallback.cs:15` (`WindowChars = 200`).

### F3 — Code hits carry line ranges instead of chunk locators, with a 12-token FTS snippet or a 200-char fallback [READ]

The code row is `{hash, ranking, path, snippet, lineStart, lineEnd}`. The FTS leg
selects `snippet(code_fts, 0, '', '', '…', 12)`; the vector-only fallback truncates
the value at 200 chars plus `…`. The first list carrying a hit supplies the payload,
so the FTS snippet wins when both legs match.

**Evidence:** `src/AiRaccoon.Core/Memory/Code/CodeSearchResult.cs:4`; `src/AiRaccoon.Infrastructure/Sqlite/Code/SqliteCodeSearchService.cs:87` (FTS snippet), `:164` (fallback), `:124-135` (first-list-wins fuse).

### F4 — Full content lives behind memory_get (memory) and code_get (code), keyed by the search hit's hash [READ]

`memory_get` returns `{hash, value, path, context, createdAt}`; `code_get` returns
`{hash, value, path, lineStart, lineEnd}` and mirrors `memory_get` for the code
corpus. Unknown hashes are refused as `unknown-hash`. The documented wire table
states the same split: `memory_search` rows carry `snippet`, the two get tools carry
`value`.

**Evidence:** `src/AiRaccoon/Tools/MemoryTools.cs:412` (`GetResult`), `src/AiRaccoon/Tools/CodeTools.cs:39` (`CodeGetResult`); `docs/reference/agent-memory-server.md:44-45,70`.

### F5 — The intended read pattern is search-then-get: rank on snippets, open the winners [INFERRED]

From F1–F4: search narrows by ranked excerpts without moving full values; get fetches
exactly the hashes worth reading. The docs say it directly ("read the full chunk with
`code_get`"), and the tool descriptions point both gets at "as returned by
memory_search".

## Still open

- Live code-leg row shape was not observed: two `kind=code` probes on this bank returned `code: []` (no code corpus / no match), so F3's wire shape is source-read, not measured — a bank with indexed code plus one `kind=code` call would settle it.
- Snippet-length distribution in production (how often FTS vs fallback wins, typical char counts) was not measured — three timed searches plus a length histogram over one bank would settle it.
