# Structured Document Retrieval: SCAMPER Expansion

**Date**: 2026-08-04
**Method**: SCAMPER — Eberle (1971), building on Osborn (1953)
**Base idea**: Chunks carry a structure path from markdown headers. Queries match against both structure and content; structure match contributes to ranking.

## How Chunks Reference Structure

Three mechanisms, simplest to richest:

### Path String
Chunk stores its header lineage: `"ADR-007 > Decision > Database Selection"`. Cheap, embeddable, human-readable. Can be embedded separately (dual-vector) or concatenated with content.

### Graph Edges
Chunk stores pointers: `parent`, `children[]`, `prev_sibling`, `next_sibling`. The index becomes a navigable tree, not a flat bag. Enables traversal-based retrieval.

### Section-Type Classification
Chunk stores a normalized role: `section_type=decision`. Lossy compression of the path — groups across documents. "ADR-007 > Decision" and "ADR-012 > Decision" both map to `decision`.

## Solution Ideas

### A — Adapt: Glob/XPath Structured Querying

Treat document collection as a filesystem where headings are directories. Query *"what database did we choose"* → structured query: search under `**/Decision` for database content. Structure match is a hard pre-filter. Query language is the interface.

**Failure mode**: Users don't type structured queries. Need query→structure mapping step (same classification problem). Mapping from natural language to structured query is fragile.

---

### E — Eliminate Flat Index: Heading-Tree Index

Tree of indices: root = h1 headings, each h1 has h2 children index, each h2 has h3/chunk leaf index. Query descends the tree — heading embeddings compete only against same-level headings. Content similarity only matters at leaves.

**Failure mode**: Error propagation (wrong h1 → all downstream wrong). Fails when headings are generic ("Overview", "Details"). Index explosion: N levels × M nodes = many small indices.

---

### R — Reverse: Structure as Pre-Filter, Content as Ranker

Two-phase: Phase 1 matches query to structure only (which heading paths are relevant?) — binary or top-K gate. Phase 2 ranks survivors by content similarity. Structure is a gate, not a signal — avoids the fragile weight-tuning problem entirely.

**Failure mode**: Hard gating misses content in misclassified sections. Binary gating brittle; top-K gating safer but dilutes benefit.

---

### Grounded: Dual-Vector with Tunable Structure Weight

Each chunk gets two embeddings:
1. **Content embedding** — chunk text only, no headers
2. **Structure embedding** — header path string only, no body text

Fusion: `score = α × sim(query, content) + (1-α) × sim(query, structure)`

α is tunable per query or per collection. Separating vectors lets structure weight be controlled independently — unlike concatenation where structure signal is diluted by content length.

**Failure mode**: 2× vector storage. α tuning is query-dependent — no single α works for all. Structure embedding space may be degenerate (all heading paths look similar). Embedding model may not encode heading strings distinctly.

**Effort**: ~afternoon to prototype
**Stack**: Double vectors, add weight parameter to query call

## Comparison Matrix

| Approach | Structure signal | Query matching | Index change | Key failure mode |
|---|---|---|---|---|
| Path string concatenation | Weak (diluted) | Implicit in embedding | None | Signal lost in long chunks |
| Glob/XPath structured query | Hard pre-filter | Explicit query language | None | Query→structure mapping fragile |
| Heading-tree index | Search algorithm | Hierarchical descent | Full rebuild | Error propagation, index explosion |
| Structure-first gating | Binary/top-K gate | Phase 1 classifier | Light (heading index) | Brittle gating |
| Dual-vector (tunable α) | Tunable weight | Cosine × 2, fused | 2× vectors | α tuning, degenerate structure embeddings |
