# Measurement Run Plan — JSON structural-chunk overlay (covers F4 research STILL OPEN)

Date: 2026-08-13
Research basis: `docs/work/2026-08-13-f4-json-overlay-consequences.md` (F4 record)
Task: `research-cont` (PR #267); WP5 of the F4 follow-up.
Status: **plan only — no run yet.** The run is the gate for whether the "no overlay on the JSON
structural path" decision (D5) should ever be revisited.

## Questions the run answers

1. **R1 — does lost cross-boundary context measurably hurt JSON retrieval?** (F4 record,
   Still open item 1.) Two banks over the same JSON corpus and query set, chunked by (A) the
   current non-overlapping structural chunker and (B) an experimental whole-property-overlap
   variant; compare nDCG@10 / MRR / Recall@10 via `SweepRunner`.
2. **R2 — does the spotty-overlap property bite on real JSON?** (F4 record, Still open item 2,
   F5b.) Measure the token-size distribution of properties/items in the corpus files and report
   the share that exceeds a 48-token overlay budget — a unit bigger than the budget is dropped
   whole from any overlap, so a large share makes whole-property overlap near-useless.

## Verified seams (read, not assumed)

- **Chunking happens only on the file-ingestion path.** `SqliteMemoryStore.AddContentAsync`
  (`src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs:535-580`) writes whole content
  unchunked; the chunker is reached via `FileIngestor.InsertChunksAsync`
  (`src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs:79`) →
  `handler.Chunker.Chunk(content, chunkMaxTokens, chunkOverlayTokens)`. So the run must ingest
  **files**, not `AddContentAsync` rows.
- **Connection-agnostic ingest exists:** `IMemoryStore.IngestFileAsync`
  (`SqliteMemoryStore.cs:476`) — the harness calls this with a project id and path; it routes
  through the matcher → `JsonFileTypeChunker`.
- **Scoring machinery:** `SweepRunner.RunAsync` (`tests/AiRaccoon.Tests/Unit/Retrieval/SweepRunner.cs:19-26`,
  `RankSource` delegate) + `ManagedHarness.RankAsync` (`tests/AiRaccoon.Tests/Integration/ManagedHarness.cs:80-105`)
  + nDCG@10/MRR/Recall@10 aggregation (pattern in `ParityGateTests.cs:37-66`). Corpus row count
  is asserted before ranking (`ParityGateTests.cs:31`).
- **Budget defaults:** `ChunkingDefaults.OverlayTokens = 48`, `FileIngestor.DefaultMaxTokens = 256`
  (WP3 of this PR). Both variants A and B use the same `TokenCount`/`maxTokens`; they differ
  only in whether the structural grouping re-emits tail properties.

## Design

### Corpus

- Real JSON files already in the repo (17 non-bin/obj/`.ai-badger` files: `spec.json` feature
  docs, `.mcp.json`, `tests/.../assets/manifest.json`, `scripts/baseline-*.json`,
  `.github/mcp.json`, `docs/.../promotion-scoring-eval/reference-labels.json`).
- Selection rule: files ≥ ~1 KB (enough to exceed 256 tokens and force multi-chunk splits);
  if fewer than ~8 qualify, supplement with generated JSON that mirrors the real files' shapes
  (deeply nested objects + arrays of objects — the shapes `ChunkObject`/`ChunkArray` actually
  exercise). Flag in the report whether the corpus is real-only or supplemented.
- The corpus is committed next to the harness (a `JsonCorpus.cs` in `benchmarks/.../Corpus/`,
  same pattern as `RealWorldCorpus.cs`), so the run is reproducible.

### Queries (labeled by construction)

- ~20 queries, half **boundary queries** whose answer spans two adjacent properties/items that
  land in different chunks at `maxTokens=256` (verified by chunking the file and confirming the
  answer is split), half single-chunk queries as control.
- Judgments are by construction: each query names its source doc; the relevant chunk(s) are the
  ground truth. Same query set for both variants — only the bank differs.

### Variant B (experimental overlap)

- A harness-local chunker (test-only, **not** shipped): same packing as `ChunkObject`/`ChunkArray`
  but re-emits whole tail properties/items of the previous chunk (up to the overlay budget,
  whole-unit granularity, mirroring `MarkdownChunker.BuildOverlay`'s per-unit budget break
  `MarkdownChunker.cs:60`). Lives in the test/benchmark project, not `src/`.
- Implementation note: with whole-property granularity, R2's size distribution decides how often
  the overlay admits anything at all.

### Procedure

1. Build bank A (current chunker) and bank B (variant) over the same corpus files
   (`IngestFileAsync`, `embedInline` deferred — no engine configured, matching
   `SearchFixtureBank.cs:97-99` "rows land pending" so the write loop pays one round trip).
2. Embed both banks (batched `memory_embed_pending` or the harness's bundled-model path as in
   `ManagedHarness.BuildAsync:47-62`).
3. Run `Sweeper`-style scoring per bank: nDCG@10 / MRR / Recall@10 over the shared query set.
4. **Three runs each** (the F4 record's own rule: "a single bar asserts a precision three runs
   will not support") — report nDCG@10 as a `range` (low..measured..high), never a single bar.
5. R2: one pass over the corpus files' parsed JSON, computing property/item token counts
   (o200k, via `O200kTokenizer`); report the distribution and the share > 48 tokens.

## Gates / acceptance

- [ ] Corpus committed and reproducible (`JsonCorpus.cs` + files, or a generator script).
- [ ] Boundary queries verified to actually straddle a chunk boundary at `maxTokens=256`
      (a `Chunk` call in the test asserts the answer text is split across ≥ 2 chunks).
- [ ] Both banks hold the full corpus (row-count assertion, `ParityGateTests.cs:31` pattern).
- [ ] nDCG@10 reported as a range over 3 runs per variant; report states which variant won and
      by how much (Δ with sign), and whether Δ exceeds noise (overlap of the ranges).
- [ ] R2 distribution reported: share of properties/items > 48 tokens, and the median/max.
- [ ] Findings written evidence-first to `docs/work/2026-08-13-json-overlay-measurement.md`
      (grade every number MEASURED with the command + conditions; render the HTML view outside
      the repo per the evidence-first skill).

## Decisions to make at run time (not locked here)

- Whether a real-only corpus is large enough; supplement or not (reported either way).
- Threshold for "Δ matters": range overlap vs a fixed Δ (the parity gate uses 0.02 nDCG@10,
  `ParityGateTests.cs:21`; the sweep here is not a parity gate, so this is a judgment call).
- Whether the run lives as a slow integration test (like `ParityGateTests`,
  `[Trait(Speed, Slow)]`) or a one-off harness invocation. Lean: slow test, so it can rerun in
  CI; one-off if the corpus is too big for CI.

## Not in scope

- Changing production behavior — this run only measures; D5 stands unless the run shows
  boundary queries measurably suffer AND overlap fixes them without duplication noise.
- Benchmarking chunk latency / token throughput (no performance claim is being made).
