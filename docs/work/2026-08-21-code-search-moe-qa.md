# QA lane — TDD test-case catalog: code corpus (code search / code-daemon-embed-v1)

**Date:** 2026-08-21
**Task:** code-search-implementation-plan — QA lane deliverable (MoE: architecture / engineer / ops / QA)
**Design source:** `docs/work/2026-08-21-code-search-exploration.md` (worktree `code-embedding-exploration`, §3–§5)
**Owner requirements folded in (2026-08-21, mid-review):** ai-raccoon.ignore (v1 root-only, beats extension dispatch), no overlapping watches (broader add prunes narrower, boundary containment, no-resurrect under concurrency), repo-watch-by-default (root add prunes inners, whole-repo catch-up) — WP4-B/C/D, gaps G16–G19.
**Engine prerequisite (ASSUMED FULLY IMPLEMENTED):** `docs/work/2026-08-21-arbitrary-embedding-models-plan.md` rev 2 (worktree `embedding-model-support`) — manifest engines, sentencepiece family, `model-output` pooling, dynamic vec0 dims, `ctx − 2` chunk budget (`MaxManifestChunkTokens`), fingerprint per D7, `model download` verb.
**Project invariants in force:** TDD is mandatory; *a check you have not seen fail is not a check* — every case below names its RED witness; done means proven.

## 0. How to read this catalog

- **One row = one named, behavior-focused test.** Each case lists: the behavior it pins, the **RED** precondition (what is witnessed failing *before* the production change), the **GREEN** assertion (the observable after), where it lives, and its kind.
- **Kinds:** `unit` (pure logic, fakes, no SQLite / no model download) · `integration` (scratch bank via the existing `TestSqliteInit`/`BankContent` helpers, real SQLite, fake or counting embedders — `CountingEmbeddingService`, `FakeEmbeddingEndpoint`) · `bdd` (Reqnroll `.feature` + steps, style of `docs/work/features-native-memory/native-memory.feature`) · `e2e` (spawned server, `McpServerToolSurfaceE2ETests` family).
- **RED honesty:** for a *new type/tool*, the first witnessed RED is a compile failure (`CodeChunker` does not exist) or an MCP `invalid-params` rejection (`kind` argument unknown); after a minimal stub exists, the same test is re-witnessed failing on its assertion. Both witnesses are recorded when the test lands, per the *prove-the-check-fails* invariant.
- **Naming:** repo convention `MethodName_Condition_Expected` (e.g. `Digest_NewFile_IngestsFingerprintsAdvancesWatermark`, `Search_WithConfiguredEngine_ReturnsVectorOnlyHit_WhenKeywordHasNoMatch`); traits `[Trait(TestCategories.Category, TestCategories.Unit|Integration)]` + `[Trait(TestCategories.Speed, TestCategories.Fast)]`; Shouldly assertions.
- **Hypotheses** (design doc does not pin the detail) are marked `⚠ HYPOTHESIS` and collected in §6. They are written as tests only where the direction is safe; otherwise the decision is left to the owner with a pointer.
- **Order of implementation = order of catalog:** WP1 → WP2 → … → WP8, each WP's tests are numbered so the implementer can go RED-first in sequence.

## 1. Work packages covered (1:1 with the plan)

| WP | Lane | Deliverable | Gate (QA's contribution) |
|---|---|---|---|
| WP0 | all | Plan reviewed (owner) | G0 — no tests; QA lane participates in review |
| WP1 | eng | Code-corpus schema: `code_entries` + `code_fts` + `vec_code float[768]` in the same digest-gated `memory.db` ladder; per-corpus vec dimension | G1 — WP1-T01…T08 green; fresh + migrated bank; 768/384 independence |
| WP2 | eng | `CodeChunker`: line-range, 126-token budget (`ctx − 2`), blank-line + brace-balance heuristics, hard-split, chunk accounting | G2 — WP2-T01…T11 green; token budget never exceeds 126 |
| WP3 | eng | `CodeFileTypeHandler` + matcher (code path registration), `CodeIngestor` (scope, embed pending→embedded, dedup) | G3 — WP3-T01…T10 green; routing/scope/embed-state/ dedup witnessed |
| WP4 | eng | Repo-wide watch behavior: extension channeling (dispatch by extension, both-corpus transactional delete, catch-up routing, rename, fingerprint unchanged), **ai-raccoon.ignore** (v1 root-only, beats extension dispatch), **no overlapping watches** (broader add prunes narrower; boundary containment), **repo-watch-by-default** (root add → prune inners → whole-repo catch-up) | G4 — WP4-T01…T29 green incl. crash-mid-delete rollback and prune/no-resurrect (the critical cases) |
| WP5 | eng | `CodeSearchAsync`: FTS5 + vec0 + RRF per-corpus hybrid, project scope only, per-section `minRelativeScore`/`limit`, code-engine query embed + 126 trim | G5 — WP5-T01…T10 green; no cross-corpus fusion pinned |
| WP6 | eng | `memory_search kind` (`memory`/`code`/`both`), `CombinedSearchResultList`, `code_get` tool; QueryGuard + gate parity | G6 — WP6-T01…T12 green incl. byte-for-byte default regression |
| WP7 | eng+ops | Maintenance & non-interference: `code-reindex` ledger job, sweep/sync/promotion exclusion, code-engine pending drain | G7 — WP7-T01…T08 green |
| WP8 | qa+ops+eng | Eval harness code-corpus mode + eval set, BDD feature, model provenance fixture, ADR + docs drift + PR | G8 — WP8-T01…T07 green; every BDD scenario witnessed RED→GREEN |

Test files proposed (new unless marked *extend*): `Unit/Chunking/CodeChunkerTests.cs` · `Unit/Ingestion/CodeFileTypeMatcherTests.cs` · `Unit/Ingestion/CodeIngestPathTests.cs` · `Unit/Watch/WatchDigestExecutorCodeChannelingTests.cs` (extends `WatchTestStack` with a code-ingest fake) · `Unit/Watch/IgnoreRulesTests.cs` (pattern parsing, pure) · `Integration/Watch/WatchPruningTests.cs` · `Integration/MemorySchemaCodeCorpusTests.cs` · `Integration/Storage/SqliteCodeStoreTests.cs` · `Integration/Storage/SqliteCodeSearchTests.cs` · `Integration/Storage/CodeIngestorTests.cs` · `Integration/Watch/WatchCodeChannelingTests.cs` · `Integration/Maintenance/CodeReindexJobTests.cs` · `Integration/Sync/SyncServiceCodeExclusionTests.cs` · `Integration/Mcp/CodeGetToolTests.cs` · `Unit/Memory/MemorySearchKindToolTests.cs` · *extend* `Integration/Observability/ToolTelemetryCoverageTests.cs`, `E2E/McpServerToolSurfaceE2ETests.cs`, `Integration/Storage/SqliteMemoryStoreHybridSearchTests.cs`-style store tests · `docs/work/features-native-memory/code-corpus.feature` + `BDD/CodeCorpusSteps.cs`.

---

## 2. Test-case catalog

### WP1 — Code-corpus schema

**WP1-T01 — `EnsureAsync_OnAFreshBank_CreatesCodeTablesAt768`**
- **Behavior:** a fresh bank creates `code_entries`, `code_fts`, and `vec_code float[768] distance_metric=cosine`; `vec_code` has no structure-table analogue in v1.
- **RED:** before WP1 the ladder creates no code tables → `SELECT COUNT(*) FROM code_entries` throws `no such table: code_entries` (witnessed on a scratch bank).
- **GREEN:** all three tables exist; `vec_code` DDL reports `float[768]` + cosine; `sqlite_master` contains no `vec_code_structure`.
- **Where:** `Integration/MemorySchemaCodeCorpusTests.cs` — integration.

**WP1-T02 — `EnsureAsync_OnAnExistingBank_AddsCodeTables_AndPreservesMemoryTables`**
- **Behavior:** migrating a pre-feature bank adds the code tables and leaves `entries`/`entries_fts`/`vec_entries float[384]`/`vec_structure` untouched.
- **RED:** a pre-feature scratch bank after `EnsureAsync` still lacks `code_entries` (no migration step ran).
- **GREEN:** code tables present; `vec_entries` still `float[384]`; memory row count unchanged; `vec_structure` still present.
- **Where:** `Integration/MemorySchemaCodeCorpusTests.cs` — integration.

**WP1-T03 — `SchemaDigest_ChangesWhenCodeDdlIsAdded`**
- **Behavior:** the code DDL is part of the digest-gated ladder (ADR-0075): adding it changes the stored schema digest, so existing banks re-run the ladder exactly once.
- **RED:** digest constant unchanged → a pre-feature bank's stored digest matches → ladder skipped → no code tables (witnessed: second open still lacks tables).
- **GREEN:** digest differs from the pre-feature value; first open after upgrade creates code tables; second open skips (digest-stamp tests, `MemorySchemaVersionTests` pattern).
- **Where:** `Integration/MemorySchemaCodeCorpusTests.cs` — integration.

**WP1-T04 — `CodeEntriesRow_HasExactlyTheDesignedColumns_AndNoMemoryOnlyColumns`**
- **Behavior:** `code_entries` carries `id, hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at, embed_state, embedding, chunk_index, total_chunks` and no `scope/workspace_id/agent_id/rating/ttl_days/heading_path`.
- **RED:** table absent → PRAGMA fails; after a wrong-shape stub, the column assertion fails.
- **GREEN:** `PRAGMA table_info(code_entries)` matches the designed column set exactly (no memory-only columns).
- **Where:** `Integration/MemorySchemaCodeCorpusTests.cs` — integration.

**WP1-T05 — `VecCode_Accepts768DimVectors_AndRejects384`**
- **Behavior:** `vec_code` KNN round-trips a 768-dim row; inserting a 384-dim vector into `vec_code` fails loudly (sqlite-vec has no dim inference).
- **RED:** `vec_code` missing → insert fails `no such table`; before the dim fix, a 384 insert succeeds silently or the 768 KNN errors.
- **GREEN:** 768-dim insert + `MATCH` KNN returns the row; 384-dim insert throws (sqlite error), `SqliteVectorTests` pattern.
- **Where:** `Integration/Storage/SqliteCodeStoreTests.cs` — integration.

**WP1-T06 — `CodeFts_ExternalContent_MatchReturnsCodeRows`**
- **Behavior:** `code_fts` is FTS5 external-content over `(value, source_file)`; a keyword `MATCH` returns the matching chunk rows.
- **RED:** `code_fts` absent → MATCH throws.
- **GREEN:** inserting a chunk then `SELECT … FROM code_fts WHERE code_fts MATCH 'token'` returns the row (and snippet fields work).
- **Where:** `Integration/Storage/SqliteCodeStoreTests.cs` — integration.

**WP1-T07 — `PerCorpusDimension_IsIndependent_768And384Coexist`**
- **Behavior:** memory and code vec tables hold different dimensions in one bank simultaneously; embedding writes for one corpus never touch the other's table.
- **RED:** a single shared dimension constant (or a single vec table) makes the 768 write hit the 384 table → vec0 error or silent mismatch.
- **GREEN:** after one memory write and one code write, `vec_entries` contains 384-dim rows only and `vec_code` 768-dim rows only (dimension read via `length(embedding)`-style probe), `MemorySchemaDigestTests` pattern.
- **Where:** `Integration/Storage/SqliteCodeStoreTests.cs` — integration.

**WP1-T08 — `CodeTables_AreCreatedInsideTheEncryptedBank`**
- **Behavior:** encryption-at-rest covers the new tables automatically (bank file encrypted wholesale).
- **RED:** (before WP1) no code rows exist to encrypt; after a stub that writes outside the encrypted connection, rows are plaintext-readable.
- **GREEN:** with encryption enabled, a second connection without the key cannot read `code_entries` content (`SqliteConnectionFactoryEncryptionTests` pattern).
- **Where:** `Integration/Storage/SqliteConnectionFactoryEncryptionTests.cs` (extend) — integration.

### WP2 — CodeChunker

Fixtures: unit tests use a counting tokenizer (repo pattern: `CharCount` in `MarkdownChunkerTests`); exactly one integration case uses the real sentencepiece tokenizer from the code-engine fixture to pin the 126 arithmetic. Budget constant: `126 = 128 − 2` (model hard cap 128, bos/eos double reservation per D6), never the memory budget 254.

**WP2-T01 — `Chunk_NoChunkExceedsThe126TokenBudget`**
- **Behavior:** every emitted chunk is ≤ 126 tokens (never exceeds the model's 128 cap even with `<s></s>` added).
- **RED:** `CodeChunker` does not exist → compile failure (witnessed via `dotnet test`); after a stub, a chunker that emits an over-budget chunk fails the assertion.
- **GREEN:** for a synthetic C# file with functions of varying size, `chunks.All(c => CountTokens(c) <= 126)`.
- **Where:** `Unit/Chunking/CodeChunkerTests.cs` — unit.

**WP2-T02 — `Budget_IsCtxMinusTwo_NotTheMemory254`**
- **Behavior:** the code chunker's budget is derived from the code engine's manifest ctx (128 → 126), never the memory chunker's 254.
- **RED:** a budget borrowed from the memory path (254) lets a 200-token chunk through.
- **GREEN:** a 200-token function produces ≥ 2 chunks; a 120-token function stays one chunk; the constant is `ctx − 2` of the code manifest (parity fixture, D6 style).
- **Where:** `Unit/Chunking/CodeChunkerTests.cs` — unit.

**WP2-T03 — `Chunk_LineRanges_AreContiguousAndCoverTheFile`**
- **Behavior:** chunk line ranges cover the file exactly: the union of ranges is `1..N` with no gaps; ranges are disjoint **except** hard-split lines (a line split by WP2-T06 appears in >1 range). The Σ property `Σ(line_end − line_start + 1) = N` holds **only for files with no hard-split lines** (review F-05: T03 and T06 must not contradict each other).
- **RED:** stub emits ranges with a gap (drops a function) → coverage assertion fails.
- **GREEN:** for a 40-line file: `ranges` are ordered, adjacent (`next.start == prev.end + 1`), and the union covers lines 1..40; for a file with a hard-split line, the union still covers 1..N and only the split line appears twice.
- **Where:** `Unit/Chunking/CodeChunkerTests.cs` — unit.

**WP2-T04 — `Chunk_BlankLines_ArePreferredSplitPoints`**
- **Behavior:** blank lines are the primary split candidates (v1 heuristic, no AST); a file with blank-line-separated members splits on blank lines, not mid-function.
- **RED:** stub splits mid-function or ignores blank lines → split-point assertion fails.
- **GREEN:** each chunk boundary sits at a blank line whenever one exists near the budget edge; no chunk starts inside a function body that was separated by blank lines.
- **Where:** `Unit/Chunking/CodeChunkerTests.cs` — unit.

**WP2-T05 — `Chunk_BraceBalance_DelaysSplitsInsideUnbalancedRegions`**
- **Behavior:** a candidate boundary with an unbalanced brace count is postponed (brace-balance heuristic); splits prefer brace-depth-0 boundaries.
- **RED:** naive line-budget splitter cuts inside a method → the emitted range's brace balance is non-zero at the boundary.
- **GREEN:** with a file containing `{ }`-wrapped members and no blank lines, boundaries land at brace-depth-0 lines; every chunk's emitted region is balanced when it ends at a depth-0 line.
- **Where:** `Unit/Chunking/CodeChunkerTests.cs` — unit.

**WP2-T06 — `Chunk_SingleLineOverflow_HardSplitsTheLine`**
- **Behavior:** a single line longer than the budget (minified/one-liner) is hard-split across multiple chunks; no chunk exceeds 126 and the line's text is preserved in order.
- **RED:** stub that refuses to split a line emits one >126-token chunk.
- **GREEN:** a 300-token one-liner yields ≥ 3 chunks each ≤ 126; concatenating chunk values reproduces the line; the same line appears in >1 chunk range (the documented hard-split exception to disjointness).
- **Where:** `Unit/Chunking/CodeChunkerTests.cs` — unit.

**WP2-T07 — `Chunk_ChunkIndexAndTotalChunks_AreContiguousAndDeterministic`**
- **Behavior:** chunk `i` reports `chunk_index = i` and `total_chunks = N` with `0..N−1` contiguous; identical input yields identical accounting (stable hashes).
- **RED:** stub with wrong bookkeeping → indices not 0..N−1 or non-deterministic.
- **GREEN:** two runs on the same file produce identical `(chunk_index, total_chunks, value)` triples; indices are contiguous.
- **Where:** `Unit/Chunking/CodeChunkerTests.cs` — unit.

**WP2-T08 — `Chunk_EmptyFile_ProducesNoChunks`**
- **Behavior:** an empty/whitespace-only file produces no chunks (mirrors `Chunk_EmptyOrWhitespace_ReturnsEmptyOrSingleChunk` for JSON — code pins the empty case).
- **RED:** stub returns one garbage chunk for `""`.
- **GREEN:** `Chunk("")` and `Chunk("\n\n  \n")` return empty collections.
- **Where:** `Unit/Chunking/CodeChunkerTests.cs` — unit.

**WP2-T09 — `Chunk_NoSplitPoints_StillBoundedAndComplete`**
- **Behavior:** a file with no blank lines and never-balanced braces (long dense method) still chunks within budget and still covers the file.
- **RED:** heuristic-only stub loops or emits one unbounded chunk.
- **GREEN:** dense 500-token method body → chunks all ≤ 126, coverage holds (WP2-T03 property), no throw, no infinite loop (test has a hard timeout guard).
- **Where:** `Unit/Chunking/CodeChunkerTests.cs` — unit.

**WP2-T10 — `Chunk_NoOverlay_LineRangesAreDisjoint`**
- **Behavior:** v1 code chunks do NOT reuse the memory overlay mechanism — line ranges are disjoint except for the hard-split case, so chunks never duplicate content.
- **RED:** an overlay/overlap bug emits the same line in two adjacent chunks (other than hard-split).
- **GREEN:** for non-hard-split files, no line number appears in two ranges.
- **Where:** `Unit/Chunking/CodeChunkerTests.cs` — unit. ⚠ HYPOTHESIS: design pins "line ranges contiguous + cover the file" but not "no overlay" explicitly; disjointness is the safe reading (task scope) — owner confirm.

**WP2-T11 — `Chunk_RealSentencePiece_RespectsTheHardCap`**
- **Behavior:** with the real code-engine tokenizer (sentencepiece, special ids 2/3/0/1), emitted chunks embed without graph-side truncation (the graph does not truncate; the caller must).
- **RED:** chunker counting with a different tokenizer (e.g. WordPiece/o200k) passes unit counts but produces > 126 sentencepiece tokens on real code.
- **GREEN:** integration fixture (real sentencepiece from the code-model fixture, `ChunkBudgetIsEngineAwareTests` pattern) asserts ≤ 126 sentencepiece tokens per chunk on a representative code sample; no truncation is logged.
- **Where:** `Integration/ChunkBudgetIsEngineAwareTests.cs` (extend) — integration.

### WP3 — Code ingest and extension routing

**WP3-T01 — `TryGetHandler_RoutesCodeExtensionsToTheCodePath_AndDocsToMemory`**
- **Behavior:** `.cs/.py/.ts/.go/.rs/…` route to the code handler; `.md/.markdown/.txt/.json` route to memory handlers; `.png/.bin/unknown` route to neither.
- **RED:** no code handler registered → `TryGetHandler("Program.cs")` false.
- **GREEN:** code extensions resolve to `CodeFileTypeHandler`; docs resolve to memory handlers; unsupported resolve false (mirror `FileTypeMatcherTests` with a fake handler).
- **Where:** `Unit/Ingestion/CodeFileTypeMatcherTests.cs` — unit.

**WP3-T02 — `TryGetHandler_CodeExtensions_AreCaseInsensitive`**
- **Behavior:** `.CS`, `.Py`, `.Ts` match the code path.
- **RED:** ordinal case-sensitive map rejects `README.CS`.
- **GREEN:** `README.CS` → code handler; existing `README.MD` → memory handler (regression).
- **Where:** `Unit/Ingestion/CodeFileTypeMatcherTests.cs` — unit.

**WP3-T03 — `IngestDirectory_CodeWalk_SkipsHiddenFilesAndDirectories_AndDenySet`**
- **Behavior:** code directory ingest skips dotfiles, hidden directories (policy pinned per review arch-10: hidden-directory segments skipped during enumeration), and the v1 built-in deny set for repo-root watches: `node_modules`, `bin`, `obj`, `.git`, `.venv`, `__pycache__`, `dist`, `build`, `target` (owner-approved OQ8; the ignore file is the extension surface).
- **RED:** a naive walk ingests `.git/config`, `obj/…`, or `node_modules/**/*.js` into the code corpus.
- **GREEN:** a scratch tree containing `Program.cs`, `.hidden.cs`, `.git/HEAD`, `obj/Debug/x.cs`, `node_modules/pkg/index.js` produces code rows only for `Program.cs`; the deny-set list is a single documented constant.
- **Where:** `Unit/Ingestion/CodeIngestPathTests.cs` (pure path filter) + integration row-count check — unit + integration.

**WP3-T04 — `IngestFile_OutsideScope_IsRefused_NoCodeRowsNoFingerprint`**
- **Behavior:** the scope check (`ingest.scope`) applies to code ingest exactly as to memory; an out-of-scope code file is refused with no rows and no fingerprint.
- **RED:** code ingest without a scope check writes rows for an out-of-scope path.
- **GREEN:** out-of-scope `../x.cs` → refused (same refusal shape as `SqliteMemoryStoreIngestScopeTests`); in-scope `.cs` ingests; fingerprint only for the in-scope path.
- **Where:** `Integration/Storage/CodeIngestorTests.cs` — integration.

**WP3-T05 — `IngestFile_EmbedInlineFalse_LeavesRowsPending`**
- **Behavior:** `CodeIngestor` mirrors `FileIngestor`'s `embedInline` contract: with the caller holding a write transaction, rows land with `embed_state = pending` and no embedding blob.
- **RED:** stub always embeds inline (or never) → pending state wrong.
- **GREEN:** after `embedInline:false` ingest, all new `code_entries` rows have `embed_state='pending'`, `embedding IS NULL`, and `vec_code` has no rows for them.
- **Where:** `Integration/Storage/CodeIngestorTests.cs` — integration.

**WP3-T06 — `PendingCodeRows_EmbedWithTheCodeEngine_AndTransitionToEmbedded`**
- **Behavior:** the pending-embed drain embeds code rows with the code engine (768, sentencepiece) and transitions `embed_state` pending → embedded with a 768-dim blob + `vec_code` row.
- **RED:** before WP3/WP7 no code rows exist to drain; with a wrong-engine stub, the blob dimension is 384 or state never flips.
- **GREEN:** after the drain (`PendingEmbedJob`-style pass, `CountingEmbeddingService` recording the code engine), every pending code row is `embedded` with a 768-dim `embedding` and a matching `vec_code` row; memory rows drained by the memory engine unchanged.
- **Where:** `Integration/Storage/CodeIngestorTests.cs` + `Integration/Maintenance/PendingEmbedJobTests.cs` (extend) — integration.

**WP3-T07 — `Reingest_SameContent_IsIdempotent_NoDuplicateRows`**
- **Behavior:** re-ingesting an unchanged file does not duplicate `code_entries`/`vec_code` rows (content-hash replace semantics, mirroring memory dedup).
- **RED:** an upsert-less insert doubles row counts on the second ingest.
- **GREEN:** ingest → ingest again: `code_entries` count unchanged, `vec_code` count unchanged, no duplicate `(path, chunk_index)` pairs, `updated_at` may change.
- **Where:** `Integration/Storage/CodeIngestorTests.cs` — integration.

**WP3-T08 — `Reingest_ChangedContent_ReplacesOldChunks`**
- **Behavior:** an edited file replaces its old code rows (delete + insert in one transaction): no stale chunks, chunk indices restart at 0 for the new content.
- **RED:** append-only stub leaves old chunks searchable after edit.
- **GREEN:** after edit, `code_entries` for the path contains only new-content hashes; old hashes are gone from `code_entries`, `code_fts`, and `vec_code`; `chunk_index` runs 0..N−1 for the new set.
- **Where:** `Integration/Storage/CodeIngestorTests.cs` — integration.

**WP3-T09 — `CodeRows_StoreNormalizedPathAndSourceFileWithLineRanges`**
- **Behavior:** every code row carries the normalized absolute `path`, `source_file`, and correct `line_start`/`line_end` for its chunk (line 1-based).
- **RED:** stub stores the raw relative path or wrong ranges.
- **GREEN:** row `path` equals `IngestPath.Normalize` of the input; ranges match the chunker's emitted ranges for a known fixture.
- **Where:** `Integration/Storage/CodeIngestorTests.cs` — integration.

**WP3-T10 — `IngestDirectory_MixedRepo_RoutesEachFileByExtension`**
- **Behavior:** one `memory_ingest_directory` pass over a mixed repo routes `.md` → `entries`, `.cs/.py` → `code_entries`, unsupported → neither.
- **RED:** a single-corpus walker puts `.cs` content into `entries` (or skips it).
- **GREEN:** scratch repo with `README.md`, `src/A.cs`, `src/B.py`, `logo.png` → 1 memory entry, N code rows for A.cs + B.py (each ≥ 1), 0 rows for the png; fingerprints set for the indexed files.
- **Where:** `Integration/Storage/CodeIngestorTests.cs` — integration. ⚠ HYPOTHESIS: exploration's §5.5 diagram includes `memory_ingest_directory` in the dispatch flow; the §4.3 table only names the digest executor — owner confirm the ingest tools route code files in v1 (recommended: yes, one mechanism).

### WP4 — Repo-wide watch behavior (critical case): channeling · ai-raccoon.ignore · watch pruning · repo-default

**Owner requirements folded in (2026-08-21):** ai-raccoon.ignore (v1), no overlapping watches, repo-watch-by-default. Sub-groups: **A** channeling (T01–T11), **B** ai-raccoon.ignore (T12–T20), **C** watch pruning (T21–T26), **D** repo-watch-by-default (T27–T29).

Extends `WatchTestStack` with a code-ingest fake (`FakeCodeIngestor` recording `Ingested`/`DeletedPaths`/`EmbedCalls`, alongside the existing `FakeWatchMemoryStore`), so the same digest tests run with zero SQLite.

**WP4-T01 — `Digest_MarkdownEdit_RoutesToMemoryCorpusOnly`**
- **Behavior:** one watch on a repo root; a `.md` edit ingests into the memory corpus only — the code store sees no ingest call.
- **RED:** current executor has no code path (no-op) — the code store fake records nothing *and* the memory ingest is unchanged; the assertion that pins "code untouched" passes vacuously today, so the RED witness is the code-side fixture existing at all: before WP4, `FakeCodeIngestor` is never wired into the executor (test compiles against the new seam, fails wiring) — the behavior RED is witnessed once the seam lands with a wrong route.
- **GREEN:** `stack.Memory.Ingested` has the file; `stack.Code.Ingested` empty; `stack.Code.EmbedCalls` empty; memory fingerprint updated.
- **Where:** `Unit/Watch/WatchDigestExecutorCodeChannelingTests.cs` — unit.

**WP4-T02 — `Digest_CodeEdit_RoutesToCodeCorpusOnly`**
- **Behavior:** a `.cs` edit ingests into the code corpus only — the memory ingestor sees no call.
- **RED:** before WP4, a `.cs` digest is a no-op (no code rows appear); the assertion `stack.Code.Ingested` contains the file fails.
- **GREEN:** `stack.Code.Ingested` single item with correct path/content; `stack.Memory.Ingested` empty; fingerprint updated; code embed net fired.
- **Where:** `Unit/Watch/WatchDigestExecutorCodeChannelingTests.cs` — unit.

**WP4-T03 — `Digest_NonIndexableEdit_IsIgnored_ByBothCorpora`**
- **Behavior:** an edited `.png`/unknown file triggers no ingest into either corpus and the digest completes without error.
- **RED:** a stub routes unknown extensions to one corpus.
- **GREEN:** `stack.Memory.Ingested` and `stack.Code.Ingested` both empty; no throw. ⚠ HYPOTHESIS: whether the digest records a fingerprint for non-indexable files is today's behavior — the test pins "call sequence identical to today's digest for the same file" as a regression (RG-05).
- **Where:** `Unit/Watch/WatchDigestExecutorCodeChannelingTests.cs` — unit.

**WP4-T04 — `CatchUp_MixedTree_RoutesEveryFileCorrectly`**
- **Behavior:** a fresh watch on a repo root (watermark 0) full-scans and routes each file by extension: `.md` → memory, `.cs` → code, unsupported → ignored.
- **RED:** before WP4 the catch-up scan only produces memory ingests (code files dropped).
- **GREEN:** mixed tree (`README.md`, `src/A.cs`, `docs/B.md`, `logo.png`) → 2 memory ingests, 1 code ingest, png ignored; fingerprints set for all three indexed files (`WatchCatchUpTests` style, real temp dir).
- **Where:** `Integration/Watch/WatchCodeChannelingTests.cs` — integration.

**WP4-T05 — `Digest_Deletion_RemovesThePathFromBothCorporaInOneTransaction`**
- **Behavior:** a deleted path is removed from BOTH corpora in one transaction — even when the extension only ever routed it to one corpus, the delete reaches both stores (covers rename leftovers).
- **RED:** before WP4, deletion only calls the memory delete (code rows for the path survive).
- **GREEN:** after `WatchEventKind.Deleted` for a path that has rows in both corpora (arranged via a rename, WP4-T07), both stores record `DeletedPaths`; on a real bank, `code_entries`/`entries` rows and fingerprints for the path are all gone.
- **Where:** `Unit/Watch/WatchDigestExecutorCodeChannelingTests.cs` + `Integration/Watch/WatchCodeChannelingTests.cs` — unit + integration.

**WP4-T06 — `Digest_Deletion_CrashMidDelete_RollsBackBothSides`**
- **Behavior:** if the second corpus delete fails mid-transaction, the first is rolled back — the file's rows remain fully present and a retry completes the deletion.
- **RED:** a delete that commits corpus A then throws on corpus B leaves A's rows gone (partial delete).
- **GREEN:** fault-inject the code delete (`DeleteReplaceRollbackTests` pattern: force an error between the two deletes) → both corpora still contain the path's rows + fingerprint intact; on retry without the fault, both sides delete; no `no such table`/orphan states.
- **Where:** `Integration/Watch/WatchCodeChannelingTests.cs` — integration (transaction seam via the shared connection).

**WP4-T07 — `Digest_RenameWithExtensionChange_MovesTheChunkBetweenCorpora`**
- **Behavior:** renaming `notes.md` → `notes.cs` deletes the memory rows at the old path and ingests the content as code rows at the new path (chunk moves between corpora).
- **RED:** before WP4, rename digest only re-ingests via the old extension → content lands back in memory (wrong corpus) or is dropped.
- **GREEN:** `stack.Memory.DeletedPaths` contains old path; `stack.Code.Ingested` contains new path with the file content; fingerprints: old null, new set; on a real bank both corpora consistent (no memory rows for new path, no code rows for old path).
- **Where:** `Unit/Watch/WatchDigestExecutorCodeChannelingTests.cs` + `Integration/Watch/WatchCodeChannelingTests.cs` — unit + integration.

**WP4-T08 — `Digest_RenameSameExtension_ReplacesCodeRowsAtTheNewPath`**
- **Behavior:** `.cs → .cs` rename applies the existing rename semantics to the code corpus: old-path code rows removed, new-path code rows ingested.
- **RED:** rename is memory-only today → code rows stay at the old path.
- **GREEN:** old-path code rows gone, new-path code rows present (counts + hashes), fingerprint moved (mirror `Digest_Rename_RemovesOldPathChunksAndDigestsNewPath`).
- **Where:** `Unit/Watch/WatchDigestExecutorCodeChannelingTests.cs` — unit.

**WP4-T09 — `Digest_CodeFile_FingerprintSemanticsUnchanged`**
- **Behavior:** one fingerprint per path shared by both corpora; a code file's digest writes the same `watch_files` fingerprint shape as memory, and a metadata-only touch hash-skips (no re-ingest, no re-embed).
- **RED:** a stub that skips fingerprinting for code files re-digests unchanged files forever (or a stub that re-ingests memory on code touch).
- **GREEN:** after `.cs` ingest + identical-content Changed event: `stack.Code.Ingested` single, `stack.Code.EmbedCalls` single; fingerprint value equals `WatchDigestExecutor.ComputeHash(path, content)`; memory untouched.
- **Where:** `Unit/Watch/WatchDigestExecutorCodeChannelingTests.cs` — unit.

**WP4-T10 — `Digest_HashSkip_IsPerPath_NotPerCorpus`**
- **Behavior:** hash-skip decisions stay per path: an unchanged `.cs` skips while a changed `.md` on the same watch re-ingests memory only.
- **RED:** a corpus-wide skip flag would skip the changed `.md` too.
- **GREEN:** sequence `.cs` ingest → touch `.cs` (skip) → edit `.md` (memory re-ingest) → code store untouched throughout; memory ingest count 2.
- **Where:** `Unit/Watch/WatchDigestExecutorCodeChannelingTests.cs` — unit.

**WP4-T11 — `WatchRemove_LeavesCodeRows_InLineWithMemorySemantics`**
- **Behavior:** removing a watch removes its fingerprints; code rows for removed paths are left in the bank (consistent with memory, where watch removal does not delete entries). ⚠ HYPOTHESIS — see §6 gap G8.
- **RED:** (decision-dependent) — implemented only after the owner confirms G8; if confirmed, RED is the current cascade deleting nothing for code because code has no fingerprint rows beyond the shared one.
- **GREEN:** after `memory_watch_remove`, fingerprints gone, code rows still queryable via `kind=code`, re-adding the watch re-digests only changed files.
- **Where:** `Integration/Watch/WatchCodeChannelingTests.cs` — integration.

#### WP4-B — ai-raccoon.ignore (v1: honored only at the watch root)

**WP4-T12 — `IgnoreRules_ExactFileDirectoryAndGlobPatterns_MatchTheExpectedPaths`**
- **Behavior:** the ignore parser honors the gitignore-like subset: exact file (`build.ps1`, `src/secret.cs`), directory (`bin/`, `obj/`, trailing slash), and glob (`*.generated.cs`, `**/node_modules/**`) patterns against normalized absolute paths.
- **RED:** parser does not exist → compile failure; a stub with only exact matches drops directory/glob semantics.
- **GREEN:** a rules fixture `bin/\n*.generated.cs\nbuild.ps1` ignores `/repo/bin/x.cs`, `/repo/src/y.generated.cs`, `/repo/build.ps1` and does not ignore `/repo/bins/x.cs`, `/repo/BUILD.ps1` (case per decision), `/repo/src/keep.cs`.
- **Where:** `Unit/Watch/IgnoreRulesTests.cs` — unit.

**WP4-T13 — `IgnoreRules_Negation_UnignoresAMatchedPath`** ⚠ HYPOTHESIS G17
- **Behavior:** if the design includes `!` negation, `*.generated.cs` + `!keep.generated.cs` un-ignores `keep.generated.cs` (last-match-wins).
- **RED:** negation unsupported → `keep.generated.cs` wrongly ignored.
- **GREEN:** negation round-trip as above. **Pinned only if G17 includes negation; otherwise this case is dropped and G17 records the decision.**
- **Where:** `Unit/Watch/IgnoreRulesTests.cs` — unit.

**WP4-T14 — `IgnoreRules_CommentsAndBlankLines_AreNotPatterns`**
- **Behavior:** `#` comment lines and blank lines are inert; patterns are per-line.
- **RED:** a parser that treats comments as patterns ignores everything after `# bin/`.
- **GREEN:** a rules file with comments/blank lines ignores exactly the non-comment patterns.
- **Where:** `Unit/Watch/IgnoreRulesTests.cs` — unit.

**WP4-T15 — `Ignore_OnlyReadAtTheWatchRoot_NestedIgnoreFilesAreNotHonored`**
- **Behavior:** v1 reads `ai-raccoon.ignore` at the watch root only; a nested `sub/ai-raccoon.ignore` has no effect.
- **RED:** recursive discovery honors the nested file.
- **GREEN:** nested ignore lists `*.cs`; files under `sub/` are still ingested; root ignore still applies.
- **Where:** `Unit/Watch/IgnoreRulesTests.cs` (resolution) + `Integration/Watch/WatchCodeChannelingTests.cs` — unit + integration.

**WP4-T16 — `Digest_IgnoredFile_NotIngestedIntoEitherCorpus_NotFingerprinted_NoDigestOnChange`**
- **Behavior:** a file matched by the watch-root ignore file is not ingested into EITHER corpus, receives no `watch_files` fingerprint, and a later change to it performs no digest work (no ingest, no embed, no fingerprint) — the ignore check short-circuits before routing.
- **RED:** before the ignore feature, the ignored `.cs` is ingested into code (and the ignored `.md` into memory); after a stub that only skips at ingest-time, the fingerprint is still written.
- **GREEN:** create + change events for an ignored `.cs` and an ignored `.md` → `stack.Memory.Ingested`, `stack.Code.Ingested`, and both `EmbedCalls` empty; `GetFileHashAsync` null for both; on a real bank, zero rows in `entries` and `code_entries`.
- **Where:** `Unit/Watch/WatchDigestExecutorCodeChannelingTests.cs` + `Integration/Watch/WatchCodeChannelingTests.cs` — unit + integration.

**WP4-T17 — `Digest_IgnoreWinsOverExtensionDispatch_BothMdAndCsSkipped`**
- **Behavior:** the interaction is pinned: an ignored `.md` AND an ignored `.cs` are BOTH skipped — ignore is evaluated before extension routing, so a file that would route to either corpus is suppressed.
- **RED:** ignore applied only on the code path leaves the ignored `.md` ingested into memory.
- **GREEN:** rules `secret.md\nsecret.cs` → both files skipped from their respective corpora; a non-ignored `.cs` next to them ingests normally.
- **Where:** `Unit/Watch/WatchDigestExecutorCodeChannelingTests.cs` — unit.

**WP4-T18 — `CatchUp_IgnoredFiles_AreSkippedInTheScan_OthersRouteCorrectly`**
- **Behavior:** the catch-up scan filters ignored files (not merely the digest): the scan yields no events for them and routes the remaining files into the correct corpora.
- **RED:** scan-level filtering absent → ignored files appear as digest events and are ingested.
- **GREEN:** mixed tree with `ai-raccoon.ignore` (`bin/`, `*.generated.cs`, `secret.md`) → memory ingests and code ingests exclude all ignored paths; fingerprints only for non-ignored files.
- **Where:** `Integration/Watch/WatchCodeChannelingTests.cs` — integration.

**WP4-T19 — `IgnoreFile_Itself_IsNeverIngested`**
- **Behavior:** `ai-raccoon.ignore` itself produces no rows in either corpus (it is neither code nor docs, and it must not become memory content).
- **RED:** a walker that ingests every non-ignored file stores the ignore file's content.
- **GREEN:** after a repo-root catch-up, no `entries` or `code_entries` row has `source_file`/`path` ending in `ai-raccoon.ignore`.
- **Where:** `Integration/Watch/WatchCodeChannelingTests.cs` — integration.

**WP4-T20 — `IgnoreChange_TriggersARescanOfTheWatchRoot_IncludingMidScanEdits`** (resolved G16: yes, re-scan; review F-16: follow-up when one is in flight)
- **Behavior:** editing the watch-root `ai-raccoon.ignore` re-scans the root (fresh-catch-up semantics): newly ignored files stop being digested (stale chunks cleaned), newly unignored files are ingested. The trigger is single-flighted, and when a scan is already in flight the edit **queues a follow-up scan** (or re-checks the ignore file's mtime at scan end and re-scans if changed — one of the two, pinned by this test) so the new rules always apply before the scan chain settles.
- **RED:** static rules loaded once → after the edit, a newly unignored file is still skipped and a newly ignored file is still digested; an edit DURING a long catch-up joins the old scan and the new rules never apply.
- **GREEN:** edit `ai-raccoon.ignore` → re-scan applies the new rules (newly ignored file's stale chunks deleted, newly unignored file ingested); an edit mid-scan results in the new rules applying before the chain settles (no stale-rule window).
- **Where:** `Integration/Watch/WatchCodeChannelingTests.cs` — integration.

#### WP4-C — No overlapping watches (broader add prunes narrower)

**WP4-T21 — `AddBroaderWatch_PrunesNarrower_RegistrationFingerprintsAndRuntimeStateGone_EntriesStay`**
- **Behavior:** adding a watch on `/repo` while `/repo/src` is registered prunes the narrower watch: its registration row, its `watch_files` rows, and its runtime state (scan lease / scheduler entries) are removed in one transaction; memory AND code entries for already-ingested files remain (no data loss; catch-up re-digest is idempotent — no duplicates).
- **RED:** pre-feature, overlapping watches coexist (both registrations present).
- **GREEN:** after the broader add: `watches` contains only `/repo`; no `watch_files` rows at/under `/repo/src`; runtime state empty; `entries` + `code_entries` row counts unchanged; a follow-up catch-up produces no duplicate rows (hash-skip/dedup).
- **Where:** `Integration/Watch/WatchPruningTests.cs` — integration.

**WP4-T22 — `AddNarrowerInsideBroader_IsRejectedWithAnActionableError`** (resolved G18: reject; ops doc amended)
- **Behavior:** adding a narrower watch under an existing broader watch is **refused** with `WatchOverlapException` naming the covering watch; nothing is written; `absorbedBy` is NEVER set on a rejected add (it reports only the identical-path re-add no-op).
- **RED:** pre-feature the narrow add succeeds (overlap allowed) or silently no-ops.
- **GREEN:** `memory_watch_add(/repo/src)` with `/repo` watched → refusal naming `/repo`; no new registration, no `absorbedBy` in the result.
- **Where:** `Integration/Watch/WatchPruningTests.cs` + BDD scenario — integration + bdd.

**WP4-T23 — `DisjointWatches_BothSurvive_NoCrossPrune`**
- **Behavior:** watches on `/repo-a` and `/repo-b` (and `/repo/src` + `/repo-other`) are disjoint and both remain registered after either add.
- **RED:** an over-eager containment check prunes the sibling.
- **GREEN:** both registrations + fingerprints present; digests route to the right watch.
- **Where:** `Integration/Watch/WatchPruningTests.cs` — integration.

**WP4-T24 — `BoundaryContainment_RepoVsRepo2_NoFalsePrune`** + tie-break family (review F-15)
- **Behavior:** containment respects path separators: adding `/repo` must NOT prune `/repo2` or `/repository` (prefix match without a separator boundary is not containment). **Mutual containment (real-path-equivalent registrations — symlink spellings, case-differing spellings on a case-insensitive host) resolves by tie-break: keep the longest literal path; on equal length, the first-registered; never prune a watch whose real path equals the survivor's.**
- **RED:** a naive `StartsWith` prune removes `/repo2`; a tie-break-less prune removes BOTH members of a symlink-equivalent pair.
- **GREEN:** after adding `/repo`, `/repo2`'s registration, fingerprints, and runtime state are intact; same for `/repository`; a symlink-equivalent pair (`/repo` + `/link-to-repo`) → exactly one watch survives (the longest literal path); case-differing pair on a case-insensitive host → exactly one survives.
- **Where:** `Integration/Watch/WatchPruningTests.cs` — integration.

**WP4-T25 — `Prune_IsIdempotent_ReAddAndRePruneAreNoOps`**
- **Behavior:** re-adding the broader watch (or pruning an already-pruned path) is a no-op: no exception, no duplicate rows, no state churn.
- **RED:** a prune path that re-runs deletes rows twice or throws on a missing target.
- **GREEN:** add `/repo` twice + re-prune a gone path → single registration, no throw, `watch_files` consistent.
- **Where:** `Integration/Watch/WatchPruningTests.cs` — integration.

**WP4-T26 — `Prune_ConcurrentWithInFlightDigest_NoResurrect`** + kill-9 atomicity (review codereviewer MUST-FIX 7)
- **Behavior:** the existing UnregisterWatch contract holds under concurrency: a digest in flight for the narrow watch while the broader watch prunes it must not re-register or re-apply the narrow watch afterwards (no resurrect); the fingerprint writes it completes do not recreate narrow watch state. **Plus: prune+register is ONE `BEGIN IMMEDIATE` transaction — a kill-9 between the prune step and the register step leaves EITHER the old watches OR the new watch, never an unwatched path (no coverage gap).**
- **RED:** a digest that re-checks/re-writes after prune resurrects the narrow registration; a non-transactional prune+register loses the path on a crash between the two.
- **GREEN:** gate the digest mid-flight (existing `WatchDigestConcurrencyTests`/`OnListFiles` seam), prune concurrently, release → final state: only `/repo` registered; no narrow `watch_files` rows; no exception; the in-flight file's rows exist under the broader watch (ingested, correct corpus). Kill-9 at each step of `PruneAndAddAsync` (before/inside/after the tx) → bank reopens with old watches or the new watch, consistent.
- **Where:** `Integration/Watch/WatchPruningTests.cs` (or extend `WatchDigestConcurrencyTests`) + `E2E/` kill-9 family — integration + e2e.

#### WP4-D — Repo watch by default

**WP4-T27 — `AddRepoRootWatch_RemovesAllInnerWatches_ThenCatchUpIndexesTheWholeRepoIntoTheCorrectCorpora`**
- **Behavior:** `memory_watch_add` on a repo root with inner watches (`/repo/src`, `/repo/docs`) prunes all inner watches and the resulting catch-up scan (watermark 0) indexes the whole tree, routing `.md` → memory and `.cs` → code.
- **RED:** pre-feature, inner watches remain and the scan only covers the root's own digest surface.
- **GREEN:** after the root add: only the root registration exists; catch-up produces memory ingests for every `.md` and code ingests for every `.cs` across the tree; per-corpus row counts match the tree's files.
- **Where:** `Integration/Watch/WatchPruningTests.cs` + `Integration/Watch/WatchCodeChannelingTests.cs` — integration.

**WP4-T28 — `RepoWatch_EmptyRepo_ScanYieldsNothingWithoutError`**
- **Behavior:** a repo-root watch on an empty repo completes cleanly: no ingests, no rows, no throw, watermark advances.
- **RED:** a scan that throws on empty enumeration or spins on zero files.
- **GREEN:** empty temp repo → watch registered; catch-up completes; `entries` and `code_entries` empty; no exception.
- **Where:** `Integration/Watch/WatchCodeChannelingTests.cs` — integration.

**WP4-T29 — `RepoWatch_RepoWithIgnoreFile_ScanSkipsIgnoredFiles`**
- **Behavior:** the default repo-root watch honors `ai-raccoon.ignore` during the whole-repo catch-up: ignored files are skipped (both corpora), everything else is indexed into the correct corpus.
- **RED:** catch-up ignores the ignore file → `bin/*.cs` and `*.generated.cs` ingested.
- **GREEN:** repo with `ai-raccoon.ignore` (`bin/`, `*.generated.cs`) → no rows for ignored paths; non-ignored `.cs`/`.md` all present; fingerprints only for non-ignored.
- **Where:** `Integration/Watch/WatchCodeChannelingTests.cs` — integration.

### WP5 — Code search (per-corpus hybrid)

**WP5-T01 — `CodeSearch_KeywordOnlyHit_ReturnsViaFtsLeg`**
- **Behavior:** code search surfaces a hit whose unique identifier appears in the text but has no vector resemblance (FTS leg alone).
- **RED:** no `CodeSearchAsync` → tool-level `kind=code` unknown (WP6 covers the surface); store-level RED: method absent → compile failure, then a vector-only stub returns nothing for a keyword-only fixture.
- **GREEN:** seeded bank (distinct keyword-only and vector-only fixtures, `SqliteMemoryStoreHybridSearchTests` pattern) → the keyword-only chunk ranks in the code section.
- **Where:** `Integration/Storage/SqliteCodeSearchTests.cs` — integration.

**WP5-T02 — `CodeSearch_VectorOnlyHit_ReturnsViaVecLeg`**
- **Behavior:** a chunk whose meaning matches the query but shares no keywords is found through `vec_code` KNN alone.
- **RED:** FTS-only stub returns nothing for the behavioural query.
- **GREEN:** the semantically-related chunk appears (pinned-query-vector fixture or `CountingEmbeddingService` with a scripted similarity).
- **Where:** `Integration/Storage/SqliteCodeSearchTests.cs` — integration.

**WP5-T03 — `CodeSearch_RrfWeights_FlipTheWinner`**
- **Behavior:** the code hybrid is a real weighted RRF fusion of the FTS5 and vec0 legs, not a union or a single leg (flipping `ftsWeight`/`vectorWeight` flips the winner).
- **RED:** single-leg stub ignores weights.
- **GREEN:** two fixtures with opposite leg winners: weights 1:0 vs 0:1 reorder the top hit (mirror `Search_FusionWeights_FlipTheWinnerBetweenKeywordAndVectorFavoured`).
- **Where:** `Integration/Storage/SqliteCodeSearchTests.cs` — integration.

**WP5-T04 — `CodeSearch_ProjectScopeOnly_NoSharedOrWorkspaceLeakage`**
- **Behavior:** code search is project-scoped by construction: identical code text in project B never appears for project A, and `scope=shared`/workspace never contribute code rows.
- **RED:** a search that ignores `project_id` leaks the other project's chunks.
- **GREEN:** two projects with identical `Program.cs` → each returns only its own rows; `kind=code` with `scope=shared` returns an empty code section (design: code has no shared tier).
- **Where:** `Integration/Storage/SqliteCodeSearchTests.cs` — integration.

**WP5-T05 — `CodeSearch_MinRelativeScore_AppliesPerSection`**
- **Behavior:** `minRelativeScore` floors each section relative to *its own* top hit — code hits below the code floor are dropped while the memory section is unaffected, and vice versa.
- **RED:** a global (cross-corpus) normalization would drop/keep the wrong rows.
- **GREEN:** with a strong + weak code hit and a weak memory hit: `minRelativeScore=0.5` keeps the strong code hit, drops the weak code hit, keeps the weak memory hit (memory's floor is relative to memory's top).
- **Where:** `Integration/Storage/SqliteCodeSearchTests.cs` + `Integration/RelativeScoreFloorTests.cs` (extend) — integration.

**WP5-T06 — `CodeSearch_Limit_AppliesPerSection`**
- **Behavior:** `limit` caps each section independently: `kind=both, limit=2` returns ≤ 2 memory + ≤ 2 code results, even when one corpus has 20 hits.
- **RED:** a single global limit truncates the weaker corpus or lets the stronger exceed.
- **GREEN:** seeded 20/20 → each section has exactly 2 (or fewer if the corpus is empty).
- **Where:** `Integration/Storage/SqliteCodeSearchTests.cs` — integration.

**WP5-T07 — `CodeSearch_NoCrossCorpusFusion_CodeNeverAppearsInMemorySection`**
- **Behavior:** code results are never ranked into the memory section and memory results never into the code section (no shared ranked list, no shared score).
- **RED:** a fusion stub merges both corpora into one list.
- **GREEN:** `kind=both` with the code hit scoring far above the memory hit → the code section's top is the code chunk and the memory section contains no code chunk (and vice versa).
- **Where:** `Integration/Storage/SqliteCodeSearchTests.cs` — integration (+ BDD scenario in WP8 feature).

**WP5-T08 — `CodeQuery_EmbeddedWithTheCodeEngine_AndTrimmedTo126`**
- **Behavior:** the code query is embedded with the code engine (symmetric, no prefix) and trimmed with the code engine's budget (126) — the memory 254-token trim path must not apply to code queries.
- **RED:** a shared trim using the memory budget lets a 200-token query reach the 128-cap model (graph-side truncation, silent quality loss); before WP5 the code trim seam doesn't exist.
- **GREEN:** a > 126-token query is trimmed to ≤ 126 sentencepiece tokens before embedding (assert via the code tokenizer counting the trimmed query; `QueryTruncationTests` + `QueryTrimSharesTheLocalTokenizerTests` pattern); the memory query on the same call is still trimmed at 254 (parity guard, both budgets on one bank).
- **Where:** `Integration/Embedding/QueryTruncationTests.cs` (extend) + `Unit/Chunking/CodeChunkerTests.cs` trim seam — integration + unit.

**WP5-T09 — `CodeSearch_EmptyCodeCorpus_ReturnsEmptySectionWithoutError`**
- **Behavior:** `kind=code` on a bank with no code rows returns an empty code section, not an error.
- **RED:** a stub that throws on empty corpus fails the tool call.
- **GREEN:** fresh bank → `kind=code` returns `{ code: [] }` (shape per WP6-T02) with no exception.
- **Where:** `Integration/Storage/SqliteCodeSearchTests.cs` — integration.

**WP5-T10 — `CodeSearch_Results_CarryPathAndLineRange`**
- **Behavior:** every code result exposes `path` + `line_start`/`line_end` (+ `hash`) so the agent can read the exact range.
- **RED:** a stub returning only text lacks the range metadata.
- **GREEN:** result objects for a seeded two-chunk file carry the chunker's ranges; `code_get` on the returned hash returns exactly that range's source.
- **Where:** `Integration/Storage/SqliteCodeSearchTests.cs` — integration.

### WP6 — `memory_search kind` + `code_get`

**WP6-T01 — `Search_NoKind_IsSemanticallyIdenticalToCurrentBehavior` (regression guard, see also RG-01)**
- **Behavior:** `memory_search` without `kind` returns the legacy envelope — memory-only, **no `code` key**, same defaults. The compat promise is **semantic identity modulo `Meta.CorrelationId`** (the correlation id is per-call random; exact bytes are unachievable — review F-02).
- **RED (two-phase, review F-03):** (1) the golden response is captured and committed in WP1, BEFORE any `kind` work; (2) this test is witnessed failing against a deliberately broken intermediate — a stub that serializes the `code` key for `kind=memory` (or defaults `kind` to `both`); (3) green after the real change. Both runs recorded in the PR.
- **GREEN:** for the same seeded bank + query, the no-`kind` response equals the WP1 golden exactly, modulo the correlation id (exact JSON key order pinned); `code` key absent; existing `HeldOutRetrievalGateTests`/`GoldenFileTests`/`RetrievalBaselineTests` run unchanged and pass.
- **Where:** `Unit/Memory/MemorySearchKindToolTests.cs` (fake store) + gate suites unchanged — unit + integration.

**WP6-T02 — `Search_KindCode_ReturnsCodeSectionWithEmptyResults`**
- **Behavior:** `kind=code` returns the code section populated and the memory section empty; **both keys present — `results` and `code`** (review F-12: the existing `SearchResultList` key is `results`, `MemoryTools.cs:346`; `memory` is NOT a key). `kind=code` serializes `{ results: [], code: [...] }`.
- **RED:** `kind` is an unknown argument today → MCP rejects with `invalid-params` (witnessed live against the running server).
- **GREEN:** `kind=code` returns code hits only; `results` key present and empty; no memory hits leak in; the wire key names are asserted exactly (so the drift cannot recur).
- **Where:** `Unit/Memory/MemorySearchKindToolTests.cs` + BDD scenario — unit + bdd.

**WP6-T03 — `Search_KindBoth_ReturnsBothSections_EachRankedByItsOwnHybrid`**
- **Behavior:** `kind=both` runs both hybrids and returns one envelope with both sections (`{ results: [...memory...], code: [...] }`), each ranked independently.
- **RED:** `kind=both` rejected today (unknown argument).
- **GREEN:** seeded bank with both corpora → both sections non-empty; ordering within each section matches the single-kind results for the same query.
- **Where:** `Unit/Memory/MemorySearchKindToolTests.cs` + BDD scenario — unit + bdd.

**WP6-T04 — `Search_InvalidKind_IsRejectedWithInvalidParams`**
- **Behavior:** an unknown `kind` value fails fast with an `invalid-params` McpException (mirroring the `scope` validation pattern), never silently defaulting.
- **RED:** before WP6 no `kind` exists; after a silently-defaulting stub, `kind="banana"` returns memory results.
- **GREEN:** `kind="banana"` → `McpException` with `invalid-params: Invalid kind 'banana': expected memory, code, or both.` (exact message per the scope pattern). ⚠ HYPOTHESIS: exact wording not pinned in design — align with the scope message.
- **Where:** `Unit/Memory/MemorySearchKindToolTests.cs` + BDD scenario — unit + bdd.

**WP6-T05 — `Search_KindCode_QueryGuardRefusalsAndShadowsApplyIdentically`**
- **Behavior:** the QueryGuard applies to code queries identically: a refuse-tier query is refused for `kind=code`/`both`; a shadowed query returns results with the warning.
- **RED:** a code-only search path that skips the guard answers refused queries.
- **GREEN:** refuse-tier fixture → `invalid-params` refusal with guidance for all three kinds; shadow-tier fixture → results + `QueryGuardShadowVerdict` log + warning (`QueryGuardServiceTests` pattern).
- **Where:** `Integration/Mcp/CodeGetToolTests.cs` + `Unit/Memory/QueryGuard/QueryGuardPolicyTests.cs` (extend) — integration + unit.

**WP6-T06 — `Search_KindCode_WarningComposesForTheCodeBudget`**
- **Behavior:** the query-length warning for code queries reflects the code engine's 126-token window (the memory 254-token note must not be emitted for code). ⚠ HYPOTHESIS on exact wording — see §6 gap G7.
- **RED:** a shared warning composer always reports the 254-token note.
- **GREEN:** a 200-token query: `kind=code` carries the code-budget warning; `kind=memory` carries the memory-budget warning (or the memory note unchanged); both never fire together.
- **Where:** `Unit/Memory/MemorySearchKindToolTests.cs` — unit.

**WP6-T07 — `KindCodeAndCodeGet_RespectTheAccessGate`**
- **Behavior:** the ToolGate applies identically: `kind=code` search and `code_get` require Read; `ro` mode allows them, denied/write-only modes refuse.
- **RED:** before WP6, `code_get` doesn't exist (tool not found — RED via tool-surface refusal); a code path bypassing the gate answers in denied mode.
- **GREEN:** `ro` → both succeed; `rw` without Read → refused with the standard access-denied shape; BDD scenario in the access-mode rule.
- **Where:** `Integration/Mcp/CodeGetToolTests.cs` + BDD — integration + bdd.

**WP6-T08 — `CodeGet_KnownHash_ReturnsFullSourceWithPathAndRange`**
- **Behavior:** `code_get(hash)` returns the chunk's full source (`value`), `path`, `line_start`/`line_end`, mirroring `memory_get`.
- **RED:** tool absent → `code_get` is an unknown tool (witnessed tool-not-found refusal).
- **GREEN:** seeded chunk hash → full source returned; value equals the chunker's chunk text; range metadata present.
- **Where:** `Integration/Mcp/CodeGetToolTests.cs` — integration.

**WP6-T09 — `CodeGet_UnknownHash_IsRefused`**
- **Behavior:** an unknown hash refuses with the same refusal family as `memory_get`'s `UnknownHashException` (no silent empty result, no SDK error log).
- **RED:** tool absent; after a stub returning an empty result, the refusal assertion fails.
- **GREEN:** unknown hash → `UnknownHashException`-derived refusal with the standard `invalid-params` prefix (via `ToolRefusals`, `KnownRefusal_ReturnsRefusal_WithoutAnSdkErrorLog` pattern).
- **Where:** `Integration/Mcp/CodeGetToolTests.cs` + `Integration/Mcp/ToolRefusalsTests.cs` (extend) — integration.

**WP6-T10 — `ToolSurface_MemorySearchDocumentsKind_AndCodeGetIsRegistered`**
- **Behavior:** the MCP tool inventory registers `code_get` and `memory_search` documents `kind`; E2E surface assertions include both.
- **RED:** `code_get` missing from the inventory → E2E tool-surface test fails (extend `McpServerToolSurfaceE2ETests`/`RegisteredTools`); before WP6 the expected list has no `code_get`.
- **GREEN:** spawned server lists `code_get`; `memory_search` input schema contains `kind` with the three enum values; existing tool list otherwise unchanged.
- **Where:** `E2E/McpServerToolSurfaceE2ETests.cs` (extend) — e2e.

**WP6-T11 — `Telemetry_CodeSearchAndCodeGet_AreInstrumented`**
- **Behavior:** the observability contract covers the new surface: `code` search calls and `code_get` calls are recorded in tool telemetry like every other tool.
- **RED:** new tools not in the telemetry catalog → `ToolTelemetryCoverageTests` fails (the coverage test enumerates the tool inventory).
- **GREEN:** telemetry coverage test passes with `code_get` and the `kind`-parameterized search; a live call produces a measurement row.
- **Where:** `Integration/Observability/ToolTelemetryCoverageTests.cs` (extend) — integration.

**WP6-T12 — `Search_Kind_IsCaseInsensitiveLikeScope`**
- **Behavior:** `kind` values normalize case (`CODE`, `Both`) like `scope` does today. ⚠ HYPOTHESIS mirroring scope behavior — §6 gap G1.
- **RED:** ordinal comparison rejects `BOTH`.
- **GREEN:** `kind="BOTH"` behaves as `both`; `kind="Code"` as `code`.
- **Where:** `Unit/Memory/MemorySearchKindToolTests.cs` — unit.

### WP7 — Maintenance & non-interference

**WP7-T01 — `CodeReindex_OnCodeEngineFingerprintChange_ReembedsCodeOnly`**
- **Behavior:** a code-engine fingerprint change (`embedding.codeModel` settings row + D7 fingerprint) invalidates code rows (embed_state → pending) and the `code-reindex` ledger job re-embeds them; memory rows are not re-embedded (their fingerprint unchanged).
- **RED:** before WP7 no `code-reindex` job exists → fingerprint change leaves code rows embedded under the old engine (assertion on embed_state fails).
- **GREEN:** fingerprint flip → all code rows pending → job drains → all embedded with 768 blobs; memory rows untouched (`ModelMigrationJobTests` pattern).
- **Where:** `Integration/Maintenance/CodeReindexJobTests.cs` — integration.

**WP7-T02 — `CodeReindex_ReembedsAt768_WhileMemoryStays384`**
- **Behavior:** the reindex never writes memory-dimension vectors into `vec_code` and never touches `vec_entries`.
- **RED:** a shared re-embed path writes 384-dim blobs into `vec_code` → vec0 insert error.
- **GREEN:** after reindex, `vec_code` rows are 768-dim, `vec_entries` rows 384-dim; counts match `code_entries`/`entries`.
- **Where:** `Integration/Maintenance/CodeReindexJobTests.cs` — integration.

**WP7-T03 — `Sweep_DoesNotTouchCodeRows`**
- **Behavior:** the memory sweep/degradation pipeline operates on `entries` only — `code_entries` count is unchanged by a sweep that deletes degraded memory rows.
- **RED:** a sweep query that joins or scans code tables could delete code rows; before WP7 the code table doesn't exist so the witness is the stub with a shared degradation query.
- **GREEN:** seed degraded memory rows + healthy code rows → sweep (dry-run and real) removes memory rows only; `code_entries`/`vec_code`/`code_fts` counts unchanged (`SweepHostedServiceTests` pattern).
- **Where:** `Integration/Sweep/SweepHostedServiceTests.cs` (extend) + `Unit/Rating/DegradationPolicyTests.cs` (extend, query-level) — integration + unit.

**WP7-T04 — `Sync_CodeTables_AreDroppedFromThePushedSnapshot`**
- **Behavior:** cloud sync copies `entries`-family data only. `StripNonSyncableAsync` **DROPs** `code_entries`, `code_fts`, `vec_code` (and their shadows/triggers) from every pushed snapshot — row-deletion is NOT the mechanism (the gate asserts table absence; review F-22 + external review B1). Verified: the strip already runs with vec0 loaded (`SyncService.cs:427-429`) and on both push paths (local + merged, `SyncService.cs:70-74,101-107`).
- **RED (rewritten per review F-06 / external B2):** the fixture SEEDS code rows via the WP3 machinery and runs a sync push **before** the strip change lands → the pushed snapshot contains the code tables → the assertion fails. (The old "before WP7 no code rows exist" witness could never fail.)
- **GREEN:** `MemorySync_CodeTables_NotInSyncPayload` (mirror `MemorySync_WorkspaceRows_NotInSyncPayload`): after a sync round-trip, the remote snapshot contains NO `code_entries`/`code_fts`/`vec_code` tables (asserted via `sqlite_master` on the pulled snapshot); the local code corpus is untouched by a pull.
- **Where:** `Integration/Sync/SyncServiceCodeExclusionTests.cs` — integration.

**WP7-T05 — `Promotion_NeverSeesCodeRows`**
- **Behavior:** code rows are not promotable: promotion-queue operations and `memory_share` never surface code hashes; `memory_share` on a code hash refuses (unknown hash).
- **RED:** a promotion scan over all tables would propose code rows.
- **GREEN:** after a promotion pass, the propose tier contains no code hashes; `memory_share(codeHash)` → unknown-hash refusal.
- **Where:** `Integration/Extraction/PromotionQueueServiceTests.cs` (extend) — integration.

**WP7-T06 — `PendingEmbedDrain_CoversCodeRowsWithTheCodeEngine`**
- **Behavior:** the pending-embed maintenance drain processes code pending rows through the code engine and memory pending rows through the memory engine in one pass (no cross-engine embedding).
- **RED:** a single-engine drain embeds code rows with the memory engine (384 blob in `vec_code` → error) or never drains them.
- **GREEN:** mixed pending corpus → after drain both corpora fully embedded, each with its own engine's dimension (`PendingEmbedMaintenanceDrainTests` pattern).
- **Where:** `Integration/Maintenance/PendingEmbedMaintenanceDrainTests.cs` (extend) — integration.

**WP7-T07 — `CodeReindex_CrashMidRun_RecoversIdempotently`**
- **Behavior:** a kill-9 mid-reindex leaves the bank consistent: the job re-runs and completes (re-embed is idempotent, lease-based, `ModelMigrationCrashRecoveryE2ETests` pattern).
- **RED:** a non-transactional reindex corrupts `code_entries`/`vec_code` pairing on interrupt.
- **GREEN:** kill at a random drain point → bank opens, ToolGate open, job resumes, final state = all code rows embedded, counts consistent.
- **Where:** `E2E/ModelMigrationCrashRecoveryE2ETests.cs` (extend) — e2e.

**WP7-T08 — `CodeEngineUnconfigured_FtsOnlyWithWarning_ConfiguredButUnloadable_ActionableError`**
- **Behavior (pinned per review F-11):** with NO `embedding.codeModel`, code files are still ingested (bundled sentencepiece counting tokenizer) and stored pending; `kind=code` returns FTS5-only results with a warning — NOT a configuration error, NOT an empty corpus. A **configured-but-unloadable** engine (missing manifest/files/dims mismatch) fails code operations with an actionable error only; memory is unaffected.
- **RED:** a single `EmbeddingService` that throws on code-engine load takes memory search down with it (or: unconfigured mode returns an error instead of FTS5-only results).
- **GREEN:** with no code engine: code ingest still chunks (pending rows), `kind=code` returns FTS5 hits + warning, `memory_search`/`memory_write` fully functional; with a broken code manifest: `kind=code` returns the actionable configuration error, memory untouched, server stays up.
- **Where:** `Integration/Embedding/EmbeddingServiceConfiguredPathTests.cs` (extend) — integration.

### WP8 — Eval harness, BDD, provenance, docs

**WP8-T01 — `EvalHarness_CodeCorpusMode_ProducesANdcgReport`**
- **Behavior:** `scripts/src/retrieval_tuning/evaluate.py` gains a code-corpus mode that scores the code eval set on a bank copy (mean nDCG@5, per-query table) exactly like the memory harness (`2026-08-21-parameter-tuning-matrix.md` method).
- **RED:** harness has no `--corpus code` mode → the gate command fails to produce a report.
- **GREEN:** the gate command runs against a scratch server with a seeded code corpus and emits a per-query nDCG table + mean, hash-anchored to the eval set.
- **Where:** `scripts/src/retrieval_tuning/evaluate.py` (extend) + gate script — script gate (QA-owned, run in CI lane).

**WP8-T02 — `CodeEvalSet_IsCommittedAndHashAnchored`**
- **Behavior:** the code eval set (2–3 repos, one Python repo per exploration risk #2; queries + graded hits) is committed and hash-anchored (`HeldOutRetrievalGateTests`/`GoldenFileTests` pattern) so results are reproducible.
- **RED:** eval set absent → harness cannot score; a regenerated set with drifted anchors fails the reference gate.
- **GREEN:** committed fixtures round-trip; the gate reproduces the recorded nDCG within tolerance.
- **Where:** `tests/AiRaccoon.Tests/Integration/Retrieval/` (new `CodeEvalSet.cs`-family) — integration/gate.

**WP8-T03 — `ChunkerAB_EvalComparesHeuristicChunksVsTokenWindowBaseline`** (arm settled per review arch-7)
- **Behavior:** the eval reports heuristic line-range chunks vs a **token-window baseline** (fixed 126-token sliding window — NOT whole-file, NOT MiniLM-on-same-chunks; the MiniLM reference is scratch-only, never a gate) so the v1 chunker is measured, not assumed. **Cross-arm anchoring: relevance is scored by span overlap** — a chunk is relevant iff its line range intersects the graded answer span; per-arm re-anchoring regenerates the spans' hashes against that arm's chunks (expectedHash anchoring breaks when chunk boundaries differ). This scoring extension is in-scope for WP8.
- **RED:** report without the comparison column fails the gate; an arm scored with stale cross-arm hashes fails the span-overlap assertions.
- **GREEN:** the WP8 report table contains both arms with nDCG@5 and the delta, both scored by span overlap against the same graded spans.
- **Where:** eval harness + report doc (QA lane runs it; `2026-08-21-parameter-tuning-matrix.md` style) — script gate.

**WP8-T04 — `CodeCorpusFeature_BDDScenarios_WitnessedRedThenGreen`**
- **Behavior:** the Reqnroll feature (`docs/work/features-native-memory/code-corpus.feature`) pins the tool-level behaviors: routing (WP3-T10), channeling (WP4), `kind` semantics (WP6-T02…T07), `code_get` (WP6-T08), no-cross-fusion (WP5-T07).
- **RED:** each scenario runs against the pre-feature server and fails (tool unknown / `kind` rejected) — RED witnessed in the PR's test log.
- **GREEN:** all scenarios pass; tags follow the `@FR-CODE-n @AC-n` convention; steps live in `BDD/CodeCorpusSteps.cs` (`NativeMemorySteps.cs` style).
- **Where:** `docs/work/features-native-memory/code-corpus.feature` + `BDD/CodeCorpusSteps.cs` — bdd.

**WP8-T05 — `CodeEngineManifest_GoldenFixture_RoundTrips`**
- **Behavior:** the code engine's manifest (sentencepiece, `model-output` pooling, 768 dims, ctx 128, numeric special tokens 2/3/0/1) is pinned by a golden fixture that round-trips through the D1 serializer and validation (G1-style).
- **RED:** fixture absent or serializer rejects the code shape.
- **GREEN:** golden fixture loads, validates, and round-trips; a mutated fixture (wrong pooling/dims/special-token map) is rejected with an actionable error.
- **Where:** `Unit/Embedding/` (extend the D1 fixture family) — unit.

**WP8-T06 — `CodeModel_RegistryPin_IsCommittedAndVerified`**
- **Behavior:** the code-daemon-embed-v1 artifact is registry-pinned (committed SHA-256, D8 pattern) and the bundled/verified-download path checks it (provenance risk #1 mitigation).
- **RED:** no pin → a swapped artifact passes the download (SHA mismatch test RED→GREEN, G2 style).
- **GREEN:** the pin exists in the registry file; a tampered fixture fails `BundledResource.IsVerified`-style verification.
- **Where:** registry file + `Unit/Embedding/BundledModelTests.cs` (extend) — unit.

**WP8-T07 — `DocsDriftAudit_ArchitectureAndAdr_MatchTheImplementation`**
- **Behavior:** post-implementation `docs/explanation/architecture.md` (§ after-diagram) and a new ADR match the shipped schema/tools; the docs drift audit passes (WP8 docs lane).
- **RED:** audit script (doc-link-comments style) flags the stale architecture section before the docs land.
- **GREEN:** audit passes; ADR reviewed; release note updated per traceable-releases.
- **Where:** docs + audit script — script gate.

---

## 3. Regression guards (memory behavior unchanged by default)

| ID | Guard | Witness / failure mode |
|---|---|---|
| RG-01 | `memory_search` default (`kind` absent) is today's envelope — semantically identical, no `code` key, modulo `Meta.CorrelationId` (WP6-T01, two-phase RED) | any default flip adds the `code` key or reorders results |
| RG-02 | Existing retrieval gates run unchanged and green: `HeldOutRetrievalGateTests`, `GoldenFileTests`, `RetrievalBaselineTests`, `SectionTargetedRetrievalTests` | a shared ranking/tuning surface change moves them |
| RG-03 | Memory chunk budget stays 254; only manifest code engine gets 126 (WP2-T02, WP5-T08) | budget constant shared across corpora |
| RG-04 | Memory `entries`/`vec_entries float[384]`/`vec_structure` DDL untouched by the code ladder (WP1-T02/T07) | dimension or table drift |
| RG-05 | Digest call sequence for existing file types unchanged: `.md` edits hit memory exactly as today (WP4-T01/T03, extend `WatchDigestExecutorTests` unchanged) | extension dispatch alters memory ingest behavior |
| RG-06 | `memory_get`/`memory_write`/promotion/sweep/sync surfaces and wire shapes unchanged (WP7-T03…T05 + existing suites) | code corpus leaks into memory machinery |
| RG-07 | Watch fingerprints: one fingerprint per path, hash-skip semantics unchanged (WP4-T09/T10) | fingerprint shape/update policy drift |
| RG-08 | Existing watch-add/remove contract for non-overlapping watches is unchanged: disjoint watches coexist, removal cascade intact (WP4-T23/T25, existing `WatchStoreCascadeTests`/`WatchIntegrationTests` green) | pruning logic breaks unrelated watch lifecycles |

---

## 4. BDD scenarios (WP8-T04 — draft scenario list for `code-corpus.feature`)

Rules and scenarios map to the catalog IDs; each must be witnessed RED against the pre-feature server:

- **Rule: file ingest routes by extension** — `Scenario: a .cs file is ingested into the code corpus` (WP3-T10) · `Scenario: a .md file is ingested into the memory corpus only` (WP3-T01/RG-05) · `Scenario: an unsupported file is ignored` (WP3-T01).
- **Rule: one watch serves both corpora** — `Scenario: editing a code file updates the code corpus` (WP4-T02) · `Scenario: deleting a file removes it from both corpora` (WP4-T05) · `Scenario: renaming .md to .cs moves the chunk between corpora` (WP4-T07).
- **Rule: memory_search kind selects the corpus** — `Scenario: the default kind is memory` (WP6-T01) · `Scenario: kind=code returns only code results` (WP6-T02) · `Scenario: kind=both returns both sections` (WP6-T03) · `Scenario: an invalid kind is rejected` (WP6-T04) · `Scenario: a refused query is refused for code too` (WP6-T05).
- **Rule: code retrieval is project-scoped and read-gated** — `Scenario: code search never leaks across projects` (WP5-T04) · `Scenario: ro mode allows code_get` / `Scenario: code_get refuses an unknown hash` (WP6-T07/T09).
- **Rule: ai-raccoon.ignore and watch containment (owner requirements)** — `Scenario: an ignored file is ingested into neither corpus` (WP4-T16) · `Scenario: an ignored code file and an ignored doc file are both skipped` (WP4-T17) · `Scenario: a repo watch skips ignored files during the initial scan` (WP4-T29) · `Scenario: adding a repo watch replaces its inner watches and indexes the whole repo` (WP4-T27) · `Scenario: a narrower watch inside a broader one is rejected` (WP4-T22 — as decided in G18) · `Scenario: disjoint watches coexist` (WP4-T23).

## 5. RED-witness logistics

- **Per-case witness:** every test in WP1–WP8 lands with a recorded RED run (`dotnet test --filter "FullyQualifiedName~<Case>"`), executed before the production change (compile failure or runtime assertion), then the GREEN run after. The PR description lists the RED→GREEN pairs (project convention, e.g. G2/G5 in the engine plan).
- **No model download in tests:** unit + integration cases use counting/fake embedders (`CountingEmbeddingService`, `FakeEmbeddingEndpoint`) and the committed sentencepiece tokenizer fixture; the real ONNX artifact is exercised only by the WP8 eval gates on a bank copy.
- **Scratch banks:** integration cases use `TestSqliteInit`/`BankContent`; watch cases reuse `WatchTestStack` + `TempDir` (extended with `FakeCodeIngestor`).
- **Ordering:** implementers run each WP's cases in ID order (WP1-T01 → … → WP8-T07); a WP's gate is green when all its cases pass with witnessed REDs.

## 6. Design gaps the other lanes must decide

| # | Gap | Recommended direction (test pins it once decided) |
|---|---|---|
| G1 | `kind` normalization + exact invalid-kind error message | Mirror `scope`: lowercase normalization + `invalid-params: Invalid kind 'x': expected memory, code, or both.` (WP6-T04/T12) |
| G2 | `CombinedSearchResultList` shape when a section is empty: always both keys vs omitted | **RESOLVED (review F-12):** keys are `results` + `code`; `kind=code`/`both` always serialize both; `kind=memory` serializes neither (`WhenWritingNull` — no `code` key) (WP6-T01/T02/T03) |
| G3 | `code_get` surface: params (projectId + hash), unknown-hash refusal type, telemetry tool name | Mirror `memory_get`; `UnknownHashException` refusal (WP6-T08/T09/T11) |
| G4 | `memory_stats`/`memory_list`/`memory_performance` vs code corpus (counts? file tree?) | Memory tools stay memory-only in v1; code counts surface via the `code-reindex` ledger/metrics only |
| G5 | Do `memory_ingest_file`/`memory_ingest_directory` route code files (diagram says yes; §4.3 table names only the digest executor)? | Yes — one dispatch mechanism (WP3-T10) |
| G6 | `codeRetrieval.*` settings namespace: v1 reuses RRF constants; do per-call tuning args (`rrfK`, `ftsWeight`, `vectorWeight`, `candidateWindow`…) apply to the code section of `kind=both`? | Accept them per-call for the code section, defaults = the same constants (WP5-T03 extends to args) |
| G7 | Query-length warning for code queries: wording + whether the 126 note replaces the 254 note per kind | Separate code-budget warning (WP6-T06) |
| G8 | `memory_watch_remove` cascade vs code rows (memory analog leaves entries in place) | Leave code rows; fingerprints removed (WP4-T11) |
| G9 | Digest behavior for non-indexable files: fingerprint recorded or not (regression-sensitive) | Keep today's memory behavior byte-for-byte; add the code analogue (WP4-T03/RG-05) |
| G10 | Code `chunk_index` repair/backfill family (memory has `ChunkIndexRepair`/`ChunkBackfill`/`ReingestRepairJob`); code is re-derivable from disk | Out of scope v1 — re-ingest from disk; document in ADR |
| G11 | Second-engine fingerprint/invalidation mechanics: which settings key (`embedding.codeModel`), trigger reuse, and whether a code-engine change can ever invalidate memory | Code-engine fingerprint invalidates code corpus only (WP7-T01) |
| G12 | Code-engine load-failure mode (missing model / bad manifest): degrade code only? | Memory unaffected; code search returns actionable error (WP7-T08) |
| G13 | Model provenance: registry pin for code-daemon-embed-v1 (SHA-256) + eval-before-default; jina-code-v2 A/B in Phase D | Pin + A/B in WP8 (WP8-T06) |
| G14 | Code eval-set ownership + corpus choice (needs a Python repo for the blank-line heuristic risk) | QA lane assembles; owner approves repos (WP8-T02) |
| G15 | `kind` + `scope` interaction for code: `kind=code, scope=shared/workspace` → empty code section vs refusal | Empty code section; document in tool description (WP5-T04) |
| G16 | **ai-raccoon.ignore change semantics** (owner req): does editing the watch-root ignore file trigger a re-scan? | Recommended: yes — re-scan the watch root with fresh-catch-up semantics (WP4-T20); decision flips the assertion |
| G17 | **ai-raccoon.ignore pattern syntax** (owner req): exact file / directory / glob included; is `!` negation in v1? comment + blank-line handling; case sensitivity | Recommended: gitignore-like subset with `!` negation, last-match-wins, case-sensitive paths (WP4-T12/T13/T14) |
| G18 | **Narrower-inside-broader watch add** (owner req): reject with actionable error vs silent prune | Recommended: reject naming the covering watch (WP4-T22) |
| G19 | **Repo-watch-by-default** (owner req): does `memory_watch_add` on a repo root ALWAYS prune inner watches, or is pruning opt-in? | Recommended: always prune (WP4-T21/T27) |

## 7. WP → test mapping (acceptance matrix)

| WP | Test IDs |
|---|---|
| WP1 | WP1-T01…T08 |
| WP2 | WP2-T01…T11 |
| WP3 | WP3-T01…T10 |
| WP4 | WP4-T01…T11 (channeling) · T12…T20 (ai-raccoon.ignore) · T21…T26 (watch pruning) · T27…T29 (repo-watch-by-default) |
| WP5 | WP5-T01…T10 |
| WP6 | WP6-T01…T12 |
| WP7 | WP7-T01…T08 |
| WP8 | WP8-T01…T07 |
| Regression | RG-01…RG-07 (executed inside WP1/WP3/WP5/WP6/WP7 suites and the unchanged gate suites) |
