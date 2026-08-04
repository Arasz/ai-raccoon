# 0003 — Source file as first-class citizen (source_file schema + weighted FTS)

Date: 2026-08-04

Status: Accepted

## Context

Chunks in the memory bank had no document-level identity. The `entries` row carried
`path` — a SHA-256-derived filename (`WritePathFor`), deliberately content-addressed so
identical content maps to one slot (FR-NM-7) — and the original file path existed only
as text embedded in the chunk value (`## Source: <structured_path>` header plus a
`[<context>]` prefix). Three consequences followed (plan C §2.3, §2.2):

1. **Identifier queries lost to cross-referencing prose.** `"ADR-0070"` matched the
   body text of every ADR that mentions ADR-0070; the owning chunks had no signal
   beyond the same body text. Under FTS-only the target ranked 11th on the polluted
   corpus (plan C §2.2).
2. **Provenance polluted BM25 and embeddings.** The `[docs:adr] docs:adr:...` and
   `## Source:` prefixes were indexed and embedded as content, and the hash contract
   (ContentHash.Of over the exact written value) became hostage to the prefix format.
3. **No section- or source-targeted retrieval.** Nothing let a caller ask for "the
   Decision chunk of ADR-0011" — the query could not name the source.

## Decision

**Wave 2 (plan C §3) makes the source file a first-class citizen of the schema and the
search result.**

### 1. `entries.source_file` + `entries.section` columns

`ALTER TABLE entries ADD COLUMN source_file TEXT` — the original relative path
(e.g. `docs/adr/0011-frontend-chassis-stack.md`); every chunk of one file shares it.
`ALTER TABLE entries ADD COLUMN section TEXT` — the chunk's section slug
(e.g. `decision`). `memory_write` gains optional `sourceFile` and `section` parameters so
the ingest pipeline carries provenance out-of-band instead of in the content. Legacy
banks gain both columns on open (schema migration in `MemorySchema.MigrateAsync`).

### 2. Weighted FTS source/section columns

`entries_fts` is rebuilt as a three-column external-content index over
`entries(value, source_file, section)`. Searches rank with `bm25(entries_fts, 1.0, 8.0,
16.0)` — a source-path match carries 8× and a section match 16× the signal of a
body-text match, so `adr 0070` ranks the owning file's chunks above cross-referencing
prose. FTS5 auxiliary functions (`bm25`) cannot share a SELECT with window functions,
so the ChunkIndex/TotalChunks window computation lives in a `MATERIALIZED` CTE (an
inlined window subquery is re-executed per FTS row — O(n²) on the corpus).

### 3. Source identity on `MemorySearchResult`

`MemorySearchResult` gains `SourceFile`, `ChunkIndex` (0-based position within the
source), and `TotalChunks` — computed per `source_file` partition at query time
(`ROW_NUMBER()`/`COUNT(*) OVER (PARTITION BY source_file)`), so no write-side bookkeeping
is needed and per-chunk `memory_write` calls stay correct. Rows without a source report
`0`/`0`.

### 4. Provenance leaves the content

The ingest script stops embedding `[<context>]` and `## Source:` prefixes; chunk values
are clean body text, and provenance lives in `source_file`/`section`. Chunk hashes
change as a result — the corpus (`jsaa-memory.db`) and `scripts/chunk-hash-map.json` are
regenerated and committed together.

### 5. Source-path queries match the source columns

A query shaped like a source path (`docs/adr/0011-frontend-chassis-stack.md#decision`)
is matched against the `source_file`/`section` FTS columns with AND semantics
(`SourcePathQuery`): the exact chunk is the only match and ranks first, without
body-text noise.

### 6. Context labels become searchable

`memory_search` gains an optional `contextLabel`; when set, the project scope also
searches the project's `scope='custom'` rows under that label (the filter is a union:
project rows plus matching custom rows). Previously `scope='custom'` rows were
invisible to every project-scoped search (plan C §2.3).

## Consequences

Measured on the regenerated 752-chunk corpus (plan C §3 Wave 2 gates):

- **Identifier queries work without the vector modality:** FTS-only `"ADR-0070"` ranks
  ADR-0070's file at ≤3 (decision chunk at 6 — the header chunk legitimately carries
  the ADR's title); FTS-only `"What is ADR-0070 about?"` ranks the decision chunk at 3.
- **Source-path queries return the exact chunk at rank 1.**
- **All expected-source file ranks hold vs Wave 0** (A1–A7, C1, C5 at their Wave 0
  ranks; A6 unchanged at 4; S2's file at 1). ADR nDCG@5 improves 0.642 → 0.674.
- **Known deviation — section-targeted natural-language queries (S2):** the Decision
  chunk of ADR-0011 ranks ~13 FTS-only and beyond the top 30 hybrid for "What does
  ADR-0011 decide?" — FTS5 has no stemming (`decide` ≠ `decision`) and bm25's
  document-length normalization crushes the 13.8 KB decision chunk in every column.
  The section-level ≤3 target is Wave 6's dual-vector structure signal.
- **Known deviation — C2 hybrid rank:** the screaming-architecture invariant's vector
  rank collapsed (>100) with clean content (2d); the RRF fusion (k=60) then sinks a
  perfect FTS rank 1. The invariant holds at FTS-only rank 1; the fusion weighting is
  Wave 4's sweep.
- `memory_ingest_file`/`memory_ingest_directory` populate `source_file` from the file
  path; `memory_write(sourceFile:, section:)` from callers. Shared-tier rows and
  workspace rows have no source and report `0`/`0` — document-level ranking (Wave 3)
  must decide how they participate (plan C §6 open question 6).
- The migration is destructive to the old FTS index only (drop + recreate + repopulate);
  `entries` data is preserved. Runs once per legacy bank on first open.
- **Content dedup shadows a file's identity (Wave 2 review):** `entries` is
  content-addressed (FR-NM-7), so byte-identical chunks from a second file — 10 of
  jsaa's `HERMES.md` chunks are byte-identical to `CLAUDE.md`'s — keep the first
  file's row. The shadowed file has no rows of its own, its structured paths are
  absent from the db while still listed in `chunk-hash-map.json`, and a source-path
  query for the shadowed file matches nothing. The duplicates are dropped correctly;
  the map asymmetry is the accepted trade-off of FR-NM-7.
- **Title handling asymmetry (Wave 2 review):** the H1 title prepend was dropped for
  `chunk_heading` chunks during the 2d provenance cleanup while `chunk_adr` keeps the
  title in content — heading chunks and ADR chunks embed different title signals.
- **`agent_id` still carries the structured path (Wave 2 review):** the ingest script
  passes `agent_id=chunk.structured_path`, so every corpus row retains the structured
  path in `agent_id` (unindexed, harmless) — provenance left the content, not the row
  entirely.
