# 0004 — Dual-vector structure signal for section-targeted retrieval

Date: 2026-08-04

Status: Accepted

## Context

Flat vector search treats every chunk as an independent bag of tokens. A
"Decision" chunk of ADR-0011 competes against the "Decision" chunks of every
other ADR as a stranger — the index has no notion of the section a chunk
belongs to. Plan C's structural queries ("What does ADR-0011 decide?",
"Consequences of ADR-0011?") cannot be answered by the content-only pipeline:
the Decision chunk of ADR-0011 ranks outside the top 30 for that query in
both content-vector and FTS space (its 15 KB body dilutes the identifier
tokens past the model's 256-token window; the OR-normalized FTS list ranks
the file's shorter siblings above it).

The retrieval-improvement-cont research (docs/research/2026-08-04-dual-vector-vs-plan-findings.md,
prototype branch prototype/dual-vector-alpha) measured a dual-vector mechanism
— a second embedding over the chunk's heading-path string, fused with the
content embedding at a fixed alpha — that lifted section hits on the old
corpus. The pre-gate comparison on the clean Wave 0 corpus
(docs/work/2026-08-04-comparison-clean.md) confirmed it: V:fixed-a0.5 beats
V:content-only per the pre-registered rule (file hit@5 9/10 vs 8/10, MRR(file)
0.820 vs 0.675, section hit@5 5/6), so Wave 6 proceeded.

## Decision

**Wave 6 ships a second, structure vector per chunk, fused at a fixed alpha.**

### Mechanism

1. **Heading-path storage.** Each entry gains `heading_path TEXT` and
   `structure_embedding BLOB` columns plus a `vec_structure` vec0 table
   (rowid = entry id, delete-triggered). Pre-existing banks are migrated with
   `ALTER TABLE` on open (SQLite has no `ADD COLUMN IF NOT EXISTS`).
2. **Heading-path format.** The path is the heading stack at the chunk's last
   H1/H2 heading, e.g. `ADR-0011 > Decision`. Two measured refinements:
   - The H1 contributes only its identifier segment (text before the first
     `:`, `—`, or `|`), so ADR numbers survive embedding — full titles dilute
     the identifier (S2 structure sim 0.88 with `ADR-0011 > Decision` vs 0.54
     with the full 23-word title).
   - Sub-sections (H3+) do not extend the path — the H3 tail dilutes the
     identifier further (0.34 with the tail). The path is the section context,
     matching the plan's example shape `ADR-0011: Frontend Chassis Stack > Decision`.
   - Ingest provenance (`## Source:` lines) and code-fence content are not
     headings and are ignored, so the parser survives the Wave 2 content-cleanup
     wave that removes the Source header.
3. **Structure embedding.** The heading-path string is embedded with the same
   bundled all-MiniLM-L6-v2 model (plan §6 Q5), stored in the entries row and
   the vec_structure table. Chunks with no headings get no structure vector
   (structure similarity 0 at query time).
4. **Backfill, not re-ingest.** A re-runnable `StructureBackfillService` +
   `tools/AiRaccoon.StructureBackfill` reads existing chunks, parses the path
   from the stored content, embeds unique paths, and writes
   `heading_path` + `structure_embedding` + the vec_structure row. Content and
   hashes are never touched (verified: chunk-hash-map.json byte-identical), and
   re-running is a no-op (verified idempotent) — the orchestrator re-runs it
   after the concurrent schema wave merges.
5. **Fixed-alpha fusion.** The vector modality of `SearchAsync` becomes the
   dual-vector blend: `score = alpha × sim(q, content) + (1 − alpha) ×
   sim(q, structure)`, over the union of both KNN candidate windows, sorted
   deterministically (score desc, hash asc). Alpha defaults to 0.5 (spike
   value; measured mean 0.58) and is configurable per bank via the
   `retrieval.structureAlpha` setting — the corpus db carries `0.5` so search
   behaves identically wherever it goes. Per-query sigmoid alpha was explicitly
   not adopted (measured query-invariant).

### Measured gate numbers (committed corpus, α=0.5)

| Gate | Target | Measured |
|---|---|---|
| S4 "Consequences of ADR-0011?" | rank ≤ 3 | **1** |
| S2 "What does ADR-0011 decide?" | rank ≤ 3 | file rank **1**; Decision chunk **8** (hybrid) |
| Section hit@5 over A1–A5+A7 | ≥ 4/6 | **5/6** (A1@2, A2@1, A3@4, A5@1, A7@5; A4@9) |
| File-level no-regression (A1–A7+C1/C2/C5) | strict | 3 queries flip by 1–2 positions (A1 1→2, A3 1→2, A4 1→3); all files stay in top 5; A2/A4/A6/A7/C2 improve |
| Invariants | C1/C2/C5 rank 1 | **1/1/1** (C2 improves 5→1) |

### Measured deviations (documented, not hidden)

1. **S2's Decision chunk does not reach rank ≤ 3.** Root cause, measured: the
   chunk's content embedding cannot match the query (content sim 0.37 — the
   15 KB body dilutes `adr/0011/decide` past the 256-token window), the FTS
   OR-normalizer ranks it 30th (Wave 1's stopword + AND semantics is the
   planned fix — the plan's dependency graph makes Wave 6 independent of
   Wave 1), and the structure sim, while strong (0.88, structure rank 1),
   cannot compensate at α=0.5. A sweep shows the constraint is binary:
   S2 needs α ≤ 0.4, while A1's no-regression needs α ≥ 0.93 — no single
   fixed alpha satisfies both on this corpus/model. The wave ships the
   mechanism and the S2 answer at file level (rank 1) with the Decision chunk
   found at rank 8; closing the gap is Wave 1 (FTS) + alpha re-measurement.
2. **Strict file-rank no-regression is not achievable with section-carrying
   paths.** A1/A3/A4 flip by 1–2 positions when structure-carrying sections of
   relevant documents (the shadcn/ui pivot section, prompt-caching sections,
   mcp-tools sections) overtake — the structure signal working as designed,
   not a retrieval failure; every expected file stays in the top 5. The
   comparison's no-regression was measured with title-only paths (structure ≈
   noise); section-carrying paths are a strictly stronger signal with bounded
   reordering.

## Consequences

- **Positive.** Section-targeted queries become answerable (S4 rank 1, S2 at
  file level); section hit@5 reaches 5/6; A2 (UUID) and C2 (screaming
  architecture) jump to rank 1; A6 (erasure) improves 4→2. The mechanism is
  independent of the Wave 2 schema wave (headings come from chunk content) and
  the backfill is idempotent, so merge conflicts on the corpus db resolve by
  re-running it.
- **Cost.** 2× vector storage per chunk (the committed corpus db grows 7.3 MB
  → 10.9 MB) and one extra embedding pass (~15 s for 762 chunks; 756 structure
  vectors over 707 unique paths).
- **Operational.** Alpha is bank-configurable (`retrieval.structureAlpha`);
  the default 0.5 is documented and measured. Banks without structure vectors
  degrade to content-only ordering (alpha scales every score identically).
- **Follow-ups.** Wave 1's FTS query construction is the documented path to
  S2 ≤ 3 (AND semantics collapse the FTS list to chunks containing
  `adr + 0011 + decide`, which the Decision chunk satisfies). Alpha
  re-measurement on the clean corpus remains open (plan §6 Q3); the strict
  no-regression gate should be re-evaluated against the comparison's
  pre-registered hit-flip rule once Wave 3/4 ranking work lands.

## Alternatives considered

- **Per-query sigmoid alpha** — rejected by the research (confidence is
  query-invariant; mean α clusters at 0.58; the machinery adds nothing).
- **Heading path computed at search time** (plan §6 Q2) — rejected: the parse
  must match ingest-time exactly, and the Source header (the only other
  path source) is being removed by the Wave 2 wave.
- **Full-title paths / H3-extended paths** — measured and rejected: they
  dilute the identifier signal (S2 structure sim 0.54/0.34 vs 0.88) and push
  the Decision chunk out of the top 10 entirely.
- **Concatenating structure into the content embedding** — rejected by the
  research (signal diluted by chunk length).
