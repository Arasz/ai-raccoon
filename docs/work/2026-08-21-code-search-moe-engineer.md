# MoE — Code corpus (engineer lane)

**Date:** 2026-08-21
**Task:** code-search-implementation-plan (plan only — no code lands in this task)
**Scope:** the engineer lane of the combined implementation plan for a code corpus in AiRaccoon: the code chunker, the code ingest path, watch channeling (incl. `ai-raccoon.ignore`, no-overlapping-watches, repo-watch-by-default — owner requirements), the code embedding engine, and the code maintenance job. Schema/wire-shape (architecture lane), the test catalog (QA lane), and CLI/migration (ops lane) are referenced by name, not duplicated.
**Inputs:** `docs/work/2026-08-21-code-search-exploration.md` (exploration, §3–§5), `docs/work/2026-08-21-arbitrary-embedding-models-plan.md` (engine generalization, D1–D12/WP1–WP4 — **assumed fully implemented before this feature starts**), and the source files cited below (all read 2026-08-21).
**Status:** engineer lane draft — decisions here are proposals for the MoE owner, not accepted ADRs.

---

## 1. Verified baseline (what the code does today)

| Concern | Current shape | Evidence |
|---|---|---|
| Memory ingest | `FileIngestor.IngestFileAsync`: scope check → hidden check → `IFileTypeMatcher.TryGetHandler` → read → `InsertChunksAsync` (bucket, budget, dedup by `(path, hash, bucket)`, insert, embed inline) | `src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs:30-43,90-202` |
| Directory walk | `IngestDirectoryAsync`: single walk (`Directory.EnumerateFiles(AllDirectories)`), filters hidden + in-scope, skips non-indexable | `FileIngestor.cs:45-70` |
| Hidden files | name starts with `.` — never chunked | `FileIngestor.cs:287-302` |
| Extension registry | `FileTypeMatcher` builds a case-insensitive `FrozenDictionary`; duplicate extension across handlers throws; `.md/.markdown/.txt` (Markdown handler), `.json` (Json handler) | `FileIngestor.cs` sibling `FileTypeMatcher.cs:19-33,49-58`; `MarkdownFileTypeHandler.cs:13-16`; `JsonFileTypeHandler.cs:13-16`; registered `AppRegistrations.cs:199-213` |
| Scope allowlist | `ingest.scope.{project\|global}` JSON path list; every disk-reading surface gates on it; unscoped project refuses every ingest | `FileIngestor.cs:253-279`; `IngestScopeKeys.cs:12-13` |
| Chunk budget | engine-aware: provider row decides; `local` ⇒ real BERT tokenizer as counting override, budget `min(DefaultMaxTokens=256, SafeChunkBudgetFor)`; unset provider ⇒ bundled local (ADR-0063) | `FileIngestor.cs:204-246`; `docs/adr/0036` |
| Watch digest | `DigestAsync`: rename→delete old path; missing→delete; hash-skip via `watch_files` fingerprint; else `ReplaceIfFileChangedAsync` (one transaction: delete by source path + re-ingest `embedInline:false` + fingerprint upsert) then best-effort `EmbedPendingAsync` | `WatchDigestExecutor.cs:20-61,68-78,81-95`; `SqliteMemoryStore.Replace.cs:17-32,57-113` |
| Watch fingerprints | `watch_files (project_id, path, file_hash, updated_at)`; SHA-256 over normalized path + full content; **non-indexable files are fingerprinted too** (the fingerprint upsert is unconditional in `ReplaceCoreAsync`) | `WatchDigestExecutor.cs:81`; `MemorySchema.cs:259-265`; `SqliteMemoryStore.Replace.cs:93-99` |
| Catch-up scan | lease (60 s TTL / 20 s heartbeat) + watermark; due = no watermark ‖ mtime > watermark ‖ never fingerprinted; reconcile deletes (fingerprinted file missing on disk ⇒ `Deleted` event); **no hidden filter in the scan enumeration** | `WatchCatchUp.cs:38-63,65-117,123-132`; `WatchScanLease.cs:27-36` |
| Watch pipeline | 1 s tick, per-path pending aggregation, deterministic digest ownership = **most specific containing watch wins** (`FindContainingWatch`, longest path); `UnregisterWatch` is the one removal choke point (drops runtime state, pending digests, cancels in-flight scan) | `WatchPipeline.cs:100-126,262-280` |
| Watch registration | `WatchService.AddAsync`: enabled check, scope check, existence check, `AddWatchAsync` (idempotent `INSERT ... IF ABSENT`), `RegisterWatch`; `RemoveAsync` deletes watch row + cascades `watch_files` in one transaction, entries survive | `WatchService.cs:15-47`; `WatchStore.cs:36-44,46-75` |
| Containment predicate | `IngestPath.IsWithinScope(path, scope)`: real-path (symlink-resolved) equality or **separator-aware prefix** `scope + Path.DirectorySeparatorChar` — `/repo2` is NOT inside `/repo`; host-OS case comparison | `IngestPath.cs:12-17,47-60` |
| Memory engine | `EmbeddingService` caches one `IEmbeddingGenerator` per fingerprint; local ⇒ `OnnxEmbeddingGenerator` (256 ctx, 254 content tokens, WordPiece); `EngineFingerprint = local:<model>`; query trim 254 | `EmbeddingService.cs:33-47,54-63,74-109,111-126`; `OnnxEmbeddingGenerator.cs:19-27,125-142` |
| Pending embed | `EntryEmbedder`: settings read, batch 32, `EmbedPendingAsync(projectId, limit)`, `EmbedPendingBatchAsync(limit)`, `EmbedQueryAsync` ⇒ `QueryVector.Empty` when no engine | `EntryEmbedder.cs:19,173-204,207-268,270-276` |
| Maintenance | `maintenance_jobs` ledger (ADR-0070); `IMaintenanceJob` with `Interval`/`HasWorkAsync`; job list = schedule; `PendingEmbedJob` (on-demand, bounded 4×32 rows/run, never due without engine) | `IMaintenanceJob.cs:9-35`; `MaintenanceJobs.cs:16-56`; `MemorySchema.cs:115-123`; `AppRegistrations.cs:149-179` |
| Trigger family | `entries_fts_ai/ad/au` (external-content FTS5), `vec_entries_au` (embed → upsert vec row), `vec_entries_pending` (embedded→pending → delete vec row), `vec_entries_ad` | `MemorySchema.cs:125-141,166-201` |
| Glob/ignore handling | **none anywhere in the watch or ingest paths** — no glob matcher, no `.gitignore` parsing, no wildcard support in `Watch/` (grep of `Watch/` for glob/wildcard/pattern/ignore: only config keys and event names); `Microsoft.Extensions.FileSystemGlobbing` is **not** in the package graph | grep 2026-08-21; `Directory.Packages.props:6-50` |
| Code model (verified) | faxenoff/code-daemon-embed-v1: 768-dim INT8 QAT ONNX 187 MB, sentencepiece (`<s>=2 </s>=3 <pad>=0 <unk>=1`), pooling+L2 fused in graph ⇒ `pooling.mode=model-output`, hard 128-token cap (graph does not truncate), symmetric (no prefix) | exploration §1; `docs/work/2026-08-21-code-search-exploration.md:42-53` |
| Engine generalization (prereq) | manifest-driven engine (D1), tokenizer families wordpiece+sentencepiece (D5), dynamic vec0 `float[N]` (D3), ctx−2 budget capped 510 (D6), fingerprint = manifest+sha256s (D7), `IEmbeddingTokenizer` routing incl. repair family (D9), pooling incl. `model-output` (D1) | `docs/work/2026-08-21-arbitrary-embedding-models-plan.md:85-96` |

---

## 2. Engineer-lane decisions

All decisions below are engineer-lane proposals; D-E1…D-E12 are the engineer's, referenced from the WP gates.

| # | Decision |
|---|---|
| D-E1 | **CodeChunker = line-range splitter, blank-line blocks + brace-balance boundary preference, hard token floor per line.** No AST in v1. Emits `CodeChunk(Text, LineStart, LineEnd)` (1-based inclusive). Budget 126 = `128 − 2` (D6, ctx−2; the 510 cap never binds). Overlay = 0. Full algorithm in §3. |
| D-E2 | **Code extension registry is a separate matcher over one constant set.** `CodeExtensions` (Core, static const `FrozenSet<string>`), `CodeFileTypeMatcher : IFileTypeMatcher` (Infrastructure, case-insensitive, same normalize shape as `FileTypeMatcher.cs:49-58`). The memory `FileTypeMatcher` is untouched (repair jobs keep using it for memory only). Overlap with memory extensions is forbidden: a unit test asserts `CodeExtensions ∩ {.md,.markdown,.txt,.json} = ∅` (derive-or-delete), and `IngestDispatcher`'s runtime rule is *memory wins* (§4). |
| D-E3 | **Watch channeling seam = corpus-agnostic delete-both + re-ingest-both in the store's existing transaction; no digest-level classification, no new watch flag.** `ReplaceCoreAsync` and `DeleteSourcePathAsync` gain a `DELETE FROM code_entries` leg and a `CodeIngestor` re-ingest leg; each ingestor self-filters by its own matcher (extension dispatch already exists inside every ingestor — `FileIngestor.cs:287-296`). The digest keeps its exact hash-skip/fingerprint/transaction semantics; its only changes are the ignore gate (§5.3), the dual pending-embed drain, and the ignore-change re-scan trigger (§5.4). *Rejected:* classifying in `WatchDigestExecutor` and passing a corpus set into the store — it duplicates the matcher truth (two sources of extension routing), changes the store API for zero behavioral payoff, and the deletes it would avoid are index-backed no-ops. |
| D-E4 | **`ai-raccoon.ignore`: one file at the watched/ingested root, gitignore-subset syntax, no negation, no caching, ignored ⇒ never fingerprinted, ignore-file change ⇒ full re-scan of the watch.** Full spec in §5.2. |
| D-E5 | **No overlapping watches.** The watch whose scope contains the other wins. Adding a narrower watch inside an existing broader one is rejected (`WatchOverlapException` naming the broader watch); adding a broader watch prunes every contained watch through the existing remove path (`RemoveWatchAsync` + `UnregisterWatch` — the one choke point). Containment = `IngestPath.IsWithinScope` (separator-safe, symlink-safe — `IngestPath.cs:47-60`), per project. Repo-watch-by-default falls out: watch-add on a repo root prunes contained watches, then its fresh registration (`lastChangeTs=0`, `WatchService.cs:34-39`) triggers the full initial scan. Full spec in §5.5. |
| D-E6 | **Code engine = `embedding.codeModel` settings row (manifest directory), local-only in v1, 768-dim-only refusal.** Two `InferenceSession`s in-process (memory ~23 MB + code ~50 MB resident — exploration §4.3). Generator cache keyed per corpus kind + fingerprint. Remote code embeddings (OpenAI-compatible) deferred. |
| D-E7 | **Unconfigured code ingest counts with a bundled code tokenizer** (code-daemon's `sentencepiece.bpe.model`, 626 KB, shipped as a `BundledResource`-style asset; default descriptor compiled in, mirroring the bundled MiniLM descriptor of the engine plan). This preserves the ADR-0036 invariant ("the chunker counts with the tokenizer that will embed") from day one, exactly like the memory path's `LocalTokenizer` for the unset-provider case (ADR-0063). Budget 126 is the most-restrictive-plausible window (ADR-0063 direction): any later code engine has ctx ≥ 128. *Rejected:* o200k proxy while unconfigured (counts can drift over the real budget — the exact class of bug ADR-0036 exists to prevent) and deferring code chunking until an engine is configured (new catch-up machinery; worse). |
| D-E8 | **CodeEmbedder mirrors EntryEmbedder minus heading/structure modality**, with the pending-loop internals shared via a small internal static helper if it falls out naturally — the two corpora differ in settings key, table set, and no `heading_path`/`structure_embedding`; a generic base class is contorted for that difference. |
| D-E9 | **Code re-embed has no outbox, no lease, no ToolGate.** v1 keeps `vec_code` at fixed `float[768]` and refuses non-768 manifests at configure time (D-E6), so there is no dimension reconcile and no bank-wedged state; per-row vec swap via triggers is atomic and search-safe mid-drain. If a future code model changes dims, the engine plan's WP4 reconcile machinery is the extension point (then the outbox shape returns). |
| D-E10 | **`memory_ingest_directory` ingests both corpora on mixed trees** (per-file dispatch in the existing single walk — exploration diagram 5.5). `memory_ingest_file` stays memory-only in v1 (a `.cs` file returns 0, unchanged); a code routing flag there is an open question (§10). |
| D-E11 | **Dedup rediscovery refreshes position metadata.** A re-ingested unchanged chunk (same `ContentHash.Of(path, text)`) gets `line_start/line_end/chunk_index/total_chunks/updated_at` rewritten — a file that gains lines above an unchanged method must not keep stale line numbers, because for code the line range IS the retrieval payload. Deliberate divergence from `FileIngestor` (which only fixes `chunk_index`/`total_chunks` — `FileIngestor.cs:195-199`; memory has no position metadata to go stale). |
| D-E12 | **Code corpus has no sweep, no TTL, no promotion, no sync** — code is a re-derivable cache of disk; degradation semantics are memory-only (exploration Q2 table). One maintenance job: `code-reindex` (on-demand pending drain, §7). |

**Confirmed per the task brief:** no new watch flag in v1 (D-E3's argument); `.gitignore` is **not** consulted by either pipeline (verified — the only filter anywhere is the dot-name hidden check, `FileIngestor.cs:298-302`; `ai-raccoon.ignore` is the new exclusion surface, §5.2).

---

## 3. CodeChunker — implementable algorithm spec (D-E1)

New Core types: `CodeChunk` (sealed record `CodeChunk(string Text, int LineStart, int LineEnd)`), `ICodeChunker`, `CodeChunker`, `CodeChunkingDefaults` (`DefaultChunkTokens = 126`, `OverlayTokens = 0`). `ICodeChunker` deliberately does **not** extend `IChunker`: the memory interface returns `IReadOnlyList<string>` (`IChunker.cs:9`) and code needs the line range — a structural mismatch, not an adaptation.

**Signature:** `IReadOnlyList<CodeChunk> Chunk(string text, int maxTokens, TokenCount countTokens)` — no overlay parameter (overlay is 0 by construction, D-E1).

**Step 0 — line endings.** Normalize `\r\n`/`\r` → `\n` (mirror `MarkdownChunker.cs:396`).

**Step 1 — lines.** Split into lines keeping each line's terminating `\n` (mirror `MarkdownChunker.cs:373-394`). Line *i* (0-based array index) is file line *i+1*.

**Step 2 — blocks.** A *block* = a maximal run of non-blank lines (blank = trimmed empty). Blank lines attach to the **end** of the preceding block (file-leading blanks attach to the first block; a trailing all-blank tail forms its own final block). Each block carries:
- `Lines` (verbatim, newlines included),
- `LineStart`/`LineEnd` (1-based, `LineEnd` includes attached trailing blank lines),
- `TokenCount` (exact, `countTokens(joined block text)`),
- `BraceDelta` = `count('{') − count('}')` over the block text — a raw character scan, **no string/comment awareness** (documented v1 limitation; a brace inside a string literal skews the heuristic, never the budget — the budget is the hard bound).

**Step 3 — per-line floor (single-line overflow hard-split).** Any single line with `countTokens(line) > maxTokens` (minified code, generated code, a giant string literal) is hard-split into pieces, each piece becoming its own block with `LineStart == LineEnd` = that line's number: repeatedly `piece = TokenBudget.Trim(remaining, maxTokens, countTokens)` (`TokenBudget.cs:10-36` — the shared binary-search prefix trim); if `Trim` returns an empty string (a single character tokenizing over budget), take 1 char anyway so every split makes progress (mirror `MarkdownChunker.cs:330-336`). Terminates; every block is proven ≤ `maxTokens` alone.

**Step 4 — greedy pack with brace-balance boundary preference.** Walk blocks; maintain the current chunk's blocks, its joined text `current`, and its running `balance = Σ BraceDelta`. For the next block `b`:
- if `current` is non-empty and `countTokens(current + b.Text) > maxTokens` → **cut**: scan the current chunk's internal block boundaries from the end backwards for the last boundary where `balance == 0`; emit the chunk up to that boundary (if none, emit at the natural end — budget wins); reset and continue packing from the block after the cut.
- else append `b` (`balance += b.BraceDelta`).
The pack decision uses the **exact joined recount** (`countTokens(current + b.Text)`), not summed per-block counts — token counts are not composable across a join (the same reason `MarkdownChunker.cs:11-14,81-99` verifies joins). A chunk never starts with a blank line (blank lines ride the preceding block).

**Step 5 — emit.** `Text = string.Concat(block lines)`; `LineStart`/`LineEnd` from the first/last block. Empty file or all-blank file → `[]` (caller treats 0 chunks as 0 indexed, mirroring `FileIngestor.cs:98-101`).

**Behavioral notes:**
- *Brace-balanced languages* (C#, Go, TS…): a method body typically ends with its closing brace, so `balance == 0` boundaries approximate function ends; the budget cuts long bodies into several balanced pieces.
- *Python*: `BraceDelta == 0` everywhere ⇒ blank-line blocks dominate (documented weakness, exploration §6.2; the v1 eval corpus must include a Python repo — QA lane).
- *Budget interplay with D6*: `maxTokens` for code = `min(CodeChunkingDefaults.DefaultChunkTokens (126), EmbeddingService.CodeSafeChunkBudgetFor(codeSettings))` where the latter = code manifest `ctx − 2` (capped at the engine plan's 510; 126 < 510 so the cap never binds for code-daemon). The counting `TokenCount` = the code engine's `IEmbeddingTokenizer` when configured (D9 routing), else the bundled code tokenizer (D-E7). Same resolution shape as `FileIngestor.ChunkSizeForAsync` (`FileIngestor.cs:232-246`).
- *Guarantee:* every emitted chunk ≤ `maxTokens` under the tokenizer that will embed it (ADR-0036), by construction of Steps 3+4 — a corpus-guarantee test (QA lane) sweeps hostile fixtures (minified JS, hex blob, one 10 000-char line, CJK, unbalanced braces) and asserts zero violations.

---

## 4. CodeIngestor + extension registry (D-E2, D-E10, D-E11)

### 4.1 Extension registry

```csharp
// Core — constant set (static-class rule: constants only).
public static class CodeExtensions
{
    // v1 proposal; owner-adjustable (open question OQ1). Case-insensitive matching.
    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".fs", ".fsx", ".py", ".ts", ".tsx", ".js", ".jsx", ".go", ".rs",
        ".java", ".kt", ".kts", ".swift", ".rb", ".php", ".c", ".h", ".cc", ".cpp", ".hpp", ".m", ".mm", ".scala", ".lua"
    };
}
```

- Registered via `CodeFileTypeMatcher` (Infrastructure) implementing `IFileTypeMatcher` — same `NormalizeExtension` shape as `FileTypeMatcher.cs:49-58` (dot-prefixed, lowercased) so `.CS` and `.cs` both match; the matcher itself is a thin `IReadOnlySet.Contains(Path.GetExtension(path))`.
- **Not in the registry:** `.md/.markdown/.txt/.json` (memory-owned), `.ipynb` (JSON), `.lock`, `.min.js` (covered by `.js`), dotfiles (hidden rule), and everything else — files matching neither registry are skipped (fingerprinted by watch, never chunked — unchanged).
- `IngestDispatcher` (Core, static pure function — allowed): `CorpusKind Classify(IFileTypeMatcher memoryMatcher, IFileTypeMatcher codeMatcher, string path)` → `Memory | Code | Neither`, **memory wins on overlap** (D-E2 priority rule; unreachable in v1 because the sets are disjoint by test, kept as the runtime rule for future drift).
- DI: register `CodeFileTypeMatcher` as its own singleton (NOT as `IFileTypeMatcher` — that registration stays memory-only for `FileIngestor` and the repair jobs, `AppRegistrations.cs:210-212,169-172`).

### 4.2 CodeIngestor

`ICodeIngestor` (Infrastructure, mirroring where `IFileIngestor` lives) with **one method**:

```csharp
Task<int> IngestFileAsync(SqliteConnection connection, string projectId, string path,
    CancellationToken cancellationToken, bool embedInline = true);
```

`CodeIngestor` mirrors `FileIngestor` shape (caller opens the bank once — one-bank-open-per-ingest, `FileIngestor.cs:12-16`):

1. **Scope check** — identical `ReadScopeAsync` + `RequireInScope` (`FileIngestor.cs:253-279`): the scope allowlist gates disk access for every corpus. Unscoped project → `PathOutsideScopeException`.
2. **Hidden check** — `IsHidden` verbatim (`FileIngestor.cs:298-302`); dotfiles are never code-indexed.
3. **Code check** — `codeFileTypeMatcher.TryGetHandler(path, out _)`; false → return 0.
4. **Read** — `File.ReadAllTextAsync` (same as `FileIngestor.cs:40`).
5. **Budget** — `maxTokens` + counting `TokenCount` resolved from the code engine settings (§6.4); chunk with `codeChunker.Chunk(content, maxTokens, countTokens)`.
6. **Insert** — for each `ordinal`, `hash = ContentHash.Of(path, chunk.Text)` (`ContentHash.cs:12` — same path+value identity as memory); dedup `SELECT id FROM code_entries WHERE project_id=@projectId AND path=@path AND hash=@hash LIMIT 1`:
   - **existing** → `UPDATE code_entries SET line_start=@ls, line_end=@le, chunk_index=@ordinal, total_chunks=@count, updated_at=@now WHERE id=@id` (D-E11 position refresh; mirrors the GH #371 ordinal-fix intent at `FileIngestor.cs:109-113,195-199` but for positions too);
   - **new** → `INSERT INTO code_entries (hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at, embed_state, chunk_index, total_chunks)` with `embed_state='pending'`; when `embedInline` → `codeEmbedder.EmbedIfConfiguredAsync(connection, id, chunk.Text, ct)` (same embed-inline-vs-pending contract as `FileIngestor.cs:26-29,185-189`).
7. **Return** `inserted > 0 ? 1 : 0` (same shape as `FileIngestor.cs:201`).

**No** `sources` row (no `source_id`), no `section`/`heading_path`, no scope/workspace/agent/rating/ttl columns — the exploration's `code_entries` shape (§4.2). Hash dedup is `(project_id, path, hash)`; the memory dedup's bucket keys (`scope/context_label/workspace_id`, `MemorySql.cs:89-94`) don't exist for code.

### 4.3 Directory ingest on mixed trees (D-E10)

`FileIngestor.IngestDirectoryAsync` keeps the single walk (`FileIngestor.cs:45-70`) and gains, per file (after the existing hidden + scope filters):
- the ignore filter (§5.2, root = the ingested directory),
- `IngestDispatcher.Classify(...)` → `Memory` → existing path unchanged; `Code` → `codeIngestor.IngestFileAsync(connection, projectId, file, ct)`; `Neither` → skip.
The `context` parameter applies to memory files only (code has no context labels — documented). The returned count sums both corpora; `memory_ingest_directory`'s wire shape is unchanged (arch lane owns the description wording).
*Rejected:* a separate `DirectoryIngestor`/rename of `FileIngestor` — pure churn across the store and tests for no behavior gain (D-E10 rationale).

---

## 5. Watch channeling — the critical case (D-E3, D-E4, D-E5)

### 5.1 The dispatch seam

Today one digest event funnels into `store.ReplaceIfFileChangedAsync(projectId, path, hash)` → `ReplaceCoreAsync` which, **in one `BEGIN IMMEDIATE` transaction**: queue-restore dance, `DELETE FROM entries` by source path (+ subtree), `fileIngestor.IngestFileAsync(..., embedInline:false)`, `UpsertWatchFile` (`SqliteMemoryStore.Replace.cs:57-113`). The code corpus extends exactly this transaction (D-E3):

```
ReplaceCoreAsync (one transaction, unchanged outer shape):
  1. guard (fingerprint compare)                       — unchanged (Replace.cs:67-73)
  2. queue-restore dance for entries                   — unchanged (Replace.cs:75-91)
  3. DELETE FROM entries  WHERE project_id=@p AND workspace_id IS NULL
        AND (path=@path OR path LIKE @prefix)          — existing (MemorySql.cs:196-200)
  4. DELETE FROM code_entries WHERE project_id=@p
        AND (path=@path OR path LIKE @prefix)          — NEW (no workspace column)
  5. fileIngestor.IngestFileAsync(embedInline:false)   — unchanged; memory-matcher self-filter
  6. codeIngestor.IngestFileAsync(embedInline:false)   — NEW; code-matcher self-filter
  7. restore queue rows still backed                   — unchanged (Replace.cs:89-91)
  8. UpsertWatchFile                                   — unchanged (Replace.cs:93-99; one shared fingerprint)
```

`DeleteSourcePathAsync` (`SqliteMemoryStore.cs:309-342`) gains leg 4 in its existing transaction — deletions and renames remove from **both** corpora in one transaction, as required. Because every change deletes from both corpora and re-ingests through both self-filtering ingestors, extension routing is handled entirely by the two matchers, and **the digest needs no classification**:

| Event | Behavior |
|---|---|
| `.md` changed | delete-both (code leg is a no-op) → memory re-ingest → code ingestor returns 0 |
| `.cs` changed | delete-both (memory leg is a no-op) → code re-ingest → memory ingestor returns 0 |
| `.png`/other changed | delete-both (both no-ops) → both ingestors return 0 → **fingerprint upserted** (unchanged — non-indexable files stay fingerprinted today, `Replace.cs:93-99`) |
| file deleted / renamed away | `DeleteSourcePathAsync` removes from both corpora + watch_files cascade |
| rename `.cs` → `.md` | old path: delete-both (removes code chunks); new path: memory ingest |
| rename `.md` → `.cs` | symmetric |
| crash mid-digest | whole replace rolls back — file never chunkless behind a matching hash (Replace.cs:49-56 doc) |
| two digests racing | fingerprint guard under the write lock ⇒ exactly-once chunk+embed (Replace.cs:10-16) |

The digest's remaining changes: (1) the ignore gate (§5.3), (2) the dual pending-embed drain — `TryEmbedPendingAsync` (`WatchDigestExecutor.cs:68-78`) calls `store.EmbedPendingAsync` **and** `store.EmbedCodePendingAsync`, both best-effort (a code embed failure is logged, never breaks the digest), (3) the ignore-change re-scan trigger (§5.4).

### 5.2 `ai-raccoon.ignore` (owner requirement 1)

**Verified first:** no glob/wildcard/pattern handling exists anywhere in `Watch/` (grep 2026-08-21; the only path-filtering in the entire ingest surface is the dot-name hidden check, `FileIngestor.cs:298-302`) and no earlier 'watch-star-glob' work left path-pattern code in `src/`. `Microsoft.Extensions.FileSystemGlobbing` is not in the package graph (`Directory.Packages.props:6-50`). So the matcher is new and hand-rolled, kept minimal by design.

- **Placement (v1):** exactly one file per tree root — `<watchRoot>/ai-raccoon.ignore` for a directory watch, `<ingestRoot>/ai-raccoon.ignore` for `memory_ingest_directory`. **No** parent-directory discovery, **no** per-subdirectory ignore files, **no** `~` expansion. A single-file watch applies no ignore rules (it has no tree). Documented in the how-to.
- **Syntax (gitignore subset, v1):** one pattern per line; empty lines and `#` comments are ignored; `\r\n` normalized before parsing (mirror `MarkdownChunker.cs:396`); **no `!` negation** (re-include semantics interact badly with directory pruning; additive later), no `?`, no `[...]`, no escaping. Pattern grammar:
  - `*` — any run of non-separator characters (matches within one path segment);
  - `**` — any run of characters including separators (only as a full segment or trailing, gitignore convention);
  - trailing `/` — directory-only pattern: matches the directory itself and everything beneath it;
  - leading `/` — anchored to the ignore file's directory; a pattern with a slash anywhere else is also anchored (gitignore rule); a pattern with no slash matches at **any depth** below the root.
- **Matching:** for a candidate path (normalized, made relative to the root; host-OS case comparison, same `Comparison` as `IngestPath.cs:12-13`), it is ignored iff **any** pattern matches it or any of its ancestor directories (directory patterns). No negation ⇒ "last match wins" does not exist; any match wins.
- **Types (Core, pure):** `IgnoreRules` (static: `Parse(string content) → IgnoreRules`, `IsIgnored(IgnoreRules, string relativePath, bool isDirectory) → bool`; the file read itself lives in the Infrastructure callers). Static-class rule satisfied (pure functions, no state).
- **When read (v1):** read + parsed once per scan (`WatchCatchUp.ScanCoreAsync` start), once per directory ingest walk, and once per digest event that passes the extension gate. **No cache** — the file is small (parse ≈ µs), digest events are rate-limited by the pipeline's 1 s tick (`WatchPipeline.cs:61`), and a cache needs mtime invalidation that costs more than the parse it saves.
- **Fingerprints (v1: none).** An ignored file is **never fingerprinted and never chunked**. The digest, on an ignored path: delete stale chunks (`DeleteSourcePathAsync` — a file that was indexed before the ignore line was added must be cleaned; deletion is not gated by ignore) → update watch `last_change_ts` → return, without reading content, hashing, or touching `watch_files`. The catch-up scan skips ignored files at enumeration (§5.4) so they never enter the pipeline at all.
- **Does an ignore change trigger a re-scan? (v1: yes — via the file's own digest event.)** The ignore file lives inside the watch tree, so editing it produces a normal digest event for `ai-raccoon.ignore`. Rule: **the ignore file itself is never matched against the rules** (never self-ignored, even by a pattern like `**`), and when the digest sees it at the watch root, after the normal replace handling it enqueues a full initial scan of the watch (single-flighted by the existing scan guard, `WatchCatchUp.cs:22-23`). Justification: lazy application (waiting for per-file events) leaves stale chunks searchable indefinitely after an explicit user edit — the wrong mental model; a full scan is hash-skip cheap for unchanged files (no re-embed) and the enumeration-level skip bounds the per-event cost. Rapid edits coalesce in the scan guard.
- **Where the checks live:** (1) `WatchCatchUp.EnumerateFiles` — skip ignored (the enumeration gains the ignore set as a parameter); (2) `WatchCatchUp.ScanCoreAsync` — after `ReconcileMissingAsync`, a new `ReconcileIgnoredAsync` pass: every fingerprinted path under the watch that is now ignored → enqueue `Deleted` (stale-chunk cleanup on the ignore-change scan); (3) `WatchDigestExecutor.DigestAsync` — the per-event gate above; (4) `FileIngestor.IngestDirectoryAsync` — skip ignored files in the walk.
- **Edge cases:** a pattern matching the root itself (e.g. `*`) ignores everything except the ignore file — allowed, documented; missing ignore file = empty rules (zero overhead); the ignore file itself is fingerprinted like any other non-indexable file (its digest must still run to detect the edit).

### 5.3 Digest flow (final, per event)

```
DigestAsync(projectId, watchPath, filePath, kind, oldPath):
  renamed        → DeletePathAsync(oldPath)                (unchanged; deletes BOTH corpora)
  file missing   → DeletePathAsync(filePath); return       (unchanged)
  ignore root resolves for (watchPath, filePath):
    file == <watchRoot>/ai-raccoon.ignore  → no ignore match applies to it
    else if ignored(filePath)              → DeleteSourcePathAsync (stale chunks)
                                             + UpdateLastChange; return   [no fingerprint]
  read content; hash; previous == hash     → TouchAsync; return            (unchanged)
  ReplaceIfFileChangedAsync                → delete-both + ingest-both + fingerprint (one tx)
  if replaced: TryEmbedPendingAsync        → memory drain + code drain (best-effort)
  if file == <watchRoot>/ai-raccoon.ignore → rescanInitiator.EnqueueInitialScan(projectId, watchPath)
  UpdateLastChange                         (unchanged)
```

The re-scan trigger needs a back-reference that must not cycle (`WatchPipeline → executor → pipeline`): new tiny interface `IWatchScanInitiator { void EnqueueInitialScan(string projectId, string path); }` implemented by `WatchCatchUp` (which already exposes `EnqueueInitialScan`, `WatchCatchUp.cs:22-23`) and injected into `WatchDigestExecutor`. `WatchCatchUp` depends on `WatchPipeline`, not on the executor — no cycle.

### 5.4 Catch-up scan of a mixed tree

`ScanCoreAsync` (lease, heartbeat, watermark — `WatchCatchUp.cs:65-117`) loads the ignore rules once per scan; `EnumerateFiles` (`WatchCatchUp.cs:38-58`) gains the ignore skip (before the due check) — ignored files are neither enqueued nor fingerprinted; `ReconcileMissingAsync` (`WatchCatchUp.cs:123-132`) is joined by `ReconcileIgnoredAsync` (fingerprinted-but-now-ignored ⇒ `Deleted`). The due predicate (`WatchCatchUp.cs:60-63`) is unchanged — a full initial scan (watermark null) re-checks every non-ignored file; hash-skip makes that cheap. The scan's per-file `Created` events then channel through the digest's delete-both/ingest-both seam.

### 5.5 No overlapping watches + repo watch by default (owner requirements 2 & 3)

**Containment predicate (precise):** `Contained(inner, outer) ⇔ IngestPath.IsWithinScope(inner, outer)` — real-path equality or separator-aware prefix `outer + Path.DirectorySeparatorChar` (`IngestPath.cs:47-60`). This is exactly the boundary-safe predicate: `/repo/src` ⊂ `/repo`; `/repo2` ⊄ `/repo` (the prefix carries the separator, `IngestPath.cs:57-59`); symlinked trees compare by resolved real path (a link to `/repo` IS `/repo`); case comparison is host-OS (`IngestPath.cs:12-17`). It is the same predicate the pipeline already uses for digest ownership (`WatchPipeline.cs:273`) — one containment truth, derived, not duplicated.

**The rule (per project):** the watch whose scope contains the other wins.
- **Adding a broader watch** (the repo-watch-by-default case): every existing watch `w` with `IsWithinScope(w.Path, newPath)` is pruned, in order: `store.RemoveWatchAsync(projectId, w.Path)` (deletes the watch row + cascades its `watch_files` in one transaction — `WatchStore.cs:46-75`) then `pipeline.UnregisterWatch(projectId, w.Path)` (the one removal choke point: runtime state dropped, pending digests dropped, in-flight scan cancelled — `WatchPipeline.cs:100-126`). **Already-ingested entries stay** — this is exactly `RemoveWatchAsync`'s existing semantics (`WatchStore.cs:46-75`; remove never deletes entries). Then the new watch registers normally with `lastChangeTs = 0` → full initial catch-up scan (`WatchService.cs:34-39`; `WatchHostedService.cs:176-178`). Pruned watches' OS watchers stop on the hosted service's next registration poll (`WatchHostedService.cs:9-13,160-178`); events arriving in the gap are still routed correctly — `FindContainingWatch` deterministically owns them (`WatchPipeline.cs:262-280`).
- **Adding a narrower watch inside a broader one** is **rejected**: `AddAsync` checks existing watches; `∃ b : IsWithinScope(newPath, b.Path)` → throw `WatchOverlapException(projectId, newPath, b.Path)` (new Core exception in `Core/Watch`, alongside `WatchDisabledException`). Nothing is written.
- **Equal path re-add** stays an idempotent no-op (`InsertWatchIfAbsent`, `WatchStore.cs:41`) — neither pruned nor rejected.
- **Ordering of checks in `AddAsync`:** after the existing enabled/scope/existence validation (`WatchService.cs:15-40`): (1) reject if contained by an existing watch; (2) prune existing watches contained by the new one AND register — **one `BEGIN IMMEDIATE` store transaction (`PruneAndAddAsync`; codereviewer MUST-FIX 7)**: a kill-9 anywhere in the step leaves either the old watches or the new watch, never an unwatched path. Runtime `UnregisterWatch` runs after commit (idempotent; crash between commit and unregister → stale runtime state, reconciled by the hosted service's registration poll; digest ownership stays deterministic).
- **Boundary cases:** a file watch `/repo/notes.md` is contained by a directory watch `/repo` (pruned/rejected accordingly); a file watch contains nothing (`Contained(w, fileWatch)` only when equal); cross-project overlap is impossible (watches are keyed `(project_id, path)`, `MemorySchema.cs:249-257`) — the same path may be watched by two projects independently; same-path re-add idempotent.

**Test shape (QA lane catalog, named gates below):** `/repo` vs `/repo/src` (prune), `/repo` vs `/repo2` (no relation — the string-prefix false-positive trap), symlink containment, file-vs-dir, reject-then-prune ordering, idempotent re-add, **kill-9 at each step of `PruneAndAddAsync` (old watches or new watch, never unwatched — codereviewer MUST-FIX 7)**, fingerprint cascade on prune (watch_files rows under the pruned watch are deleted by `DeleteWatchFilesByProjectPathCascade` — re-fingerprinted by the new watch's initial scan; correct, bounded, and covered by the gate).

### 5.6 Channeling edge-case list (explicit)

1. `.md` inside a code directory → memory (priority rule; registries disjoint by test).
2. Rename with extension change (`.cs`→`.md`, `.md`→`.cs`) → old path deleted from both corpora; new path routed by its extension.
3. Rename without extension change → delete-old + re-ingest same corpus; positions refreshed (D-E11).
4. Mixed-tree catch-up scan → per-file classification; ignored files skipped at enumeration.
5. `memory_ingest_directory` on a mixed tree → both corpora; on a pure code tree → code only; on a pure docs tree → memory only (other corpus's ingestor returns 0).
6. Hidden files → never chunked, still fingerprinted (unchanged).
7. Ignored files → never fingerprinted, never chunked; stale chunks removed on touch/delete or by `ReconcileIgnoredAsync`.
8. Binary/unindexable change → fingerprint only, both delete legs are no-ops (unchanged).
9. Two digests racing the same file → fingerprint guard under the write lock, exactly-once (unchanged).
10. Crash mid-replace → rollback; file never chunkless behind a matching hash (unchanged).
11. Transient overlap after a crash mid-prune → deterministic longest-watch ownership; no double ingest.
12. Extension case-insensitivity (`.CS` ≡ `.cs`) → matcher normalize (D-E2).
13. Symlinked watch trees → containment and ignore roots by resolved real path (`IngestPath.cs:31-45`).
14. Ignore file edited → full re-scan via its own digest event; single-flighted; hash-skip cheap.
15. File watch (single file) → no ignore rules apply (no tree).

---

## 6. EmbeddingService: the code engine (D-E6, D-E7, D-E8)

### 6.1 Settings

New rows in the settings table (mirror `EmbeddingSettingsKeys.cs:9-17`):
- `embedding.codeModel` — local manifest **directory** (same shape `embedding.model` accepts for directories per engine plan D2);
- `embedding.codeEngine` — the code fingerprint (`local:<modelPath>` + D7 manifest identity: dims, pooling, tokenizer family, per-file sha256s). Written by the configure path; a change invalidates all code vectors.

No `codeProvider` in v1: the code engine is local-only (D-E6). `EmbeddingSettingsKeys` gains `CodeModel` and `CodeEngine` constants.

### 6.2 Resolution + caching

`EmbeddingService` keeps its per-fingerprint `ConcurrentDictionary` cache (`EmbeddingService.cs:33-34`) and gains a second, **kind-prefixed** cache key (`"code:" + CodeEngineFingerprint(...)` vs `"" + EngineFingerprint(...)`) so the two corpora can never share a generator. `CreateCodeGenerator(CodeEmbeddingSettings)`:
- resolve the manifest directory (engine plan D1 — validation: missing dir, missing manifest, missing tokenizer file, unknown family, `model-output` without `onnx.embeddingOutput` → actionable errors, never silent defaults);
- **768-dim refusal:** a manifest whose dimension ≠ 768 is refused at configure time with "code engine must be 768-dim in v1" (D-E6; `vec_code` is fixed `float[768]` — arch lane DDL);
- build the same manifest-driven `OnnxEmbeddingGenerator` the engine plan's WP3 produces (sentencepiece family, `model-output` pooling — **no mean-pool, no extra L2**: the graph returns the pooled, normalized vector, exploration §1 line 43; `requiresTokenTypeIds=false` — verified spike inputs were `input_ids` + `attention_mask` only);
- the generator's session lives as long as the cached generator (two sessions in-process, ~50 MB code + ~23 MB memory resident — exploration §4.3).

### 6.3 Fingerprint

`CodeEngineFingerprint(provider, modelPath)` = `"code:local:" + <D7 fingerprint>` (manifest semantic content INCLUDING per-file sha256s, per engine plan D7). Re-downloading the model (same path, new weights) changes the file hashes → fingerprint changes → re-embed fires. The `EmbeddingService` doc comment for the fingerprint contract (`EmbeddingService.cs:101-109`) is mirrored verbatim in spirit.

### 6.4 Per-corpus routing

- **Chunk budget:** `CodeSafeChunkBudgetFor(CodeEmbeddingSettings)` = manifest `ctx − 2` (D6, ≤ 510) → 126 for the default descriptor; `CodeChunkingDefaults.DefaultChunkTokens = 126` when no engine is configured.
- **Tokenizer:** `GetCodeTokenizer(CodeEmbeddingSettings)` — the code engine's `IEmbeddingTokenizer` (D9) when configured, else the bundled code tokenizer (D-E7). The instance is shared with the generator for the same fingerprint (the ADR-0036 invariant by construction, same pattern as the engine plan §2.2).
- **Query trim:** `TrimCodeQueryToWindow` — trim to the code engine's content budget (126) with the code tokenizer, new `LoggerMessage` event **417** (mirror of 416, `EmbeddingService.cs:149-158`; 416's wording stays memory-bound). Code queries are **symmetric — no prefix** (model card; exploration §1 line 45).

### 6.5 CodeEmbedder

`ICodeEmbedder` (Infrastructure, mirror of `IEntryEmbedder` minus structure/heading):
- `EmbedIfConfiguredAsync(connection, id, value, ct)` — no `embedding.codeModel` row → return (row stays pending, exactly like `EntryEmbedder.cs:176-182`); else generate with `CreateCodeGenerator` and `MarkCodeEmbedded(id, blob)`. **No heading-path double call** (the `EntryEmbedder.cs:185-204` structure leg does not exist for code).
- `EmbedPendingAsync(connection, projectId, limit)` / `EmbedPendingBatchAsync(connection, limit)` — `SelectCodePendingForEmbed`/`SelectAllCodePendingForEmbed`, batch 32 (the `EntryEmbedder.BatchSize` pattern, `EntryEmbedder.cs:19`); no `HealStructureAsync` leg.
- `EmbedQueryAsync(connection, query, ct)` — `QueryVector.Empty` when no code engine (mirror `EntryEmbedder.cs:255-268`), else trim + generate with the code engine.
- `ConfigureCodeAsync(connection, modelPath, ct)` — write `embedding.codeModel` + `embedding.codeEngine`; when the fingerprint changed: `UPDATE code_entries SET embed_state='pending' WHERE embed_state='embedded'` (the `vec_code` rows vanish via the arch-lane `code_entries_pending` trigger). **No outbox, no lease, no ToolGate** (D-E9).

---

## 7. Maintenance (D-E9, D-E12)

`CodeReindexJob : IMaintenanceJob` — a direct mirror of `PendingEmbedJob` (`MaintenanceJobs.cs:16-56`):
- `Name = "code-reindex"` (ledger key, `maintenance_jobs` — ADR-0070);
- `Interval = null`; `HasWorkAsync` = `embedding.codeModel` configured **and** `EXISTS (SELECT 1 FROM code_entries WHERE embed_state='pending')` (a pending row with no code engine is legitimately never due — same argument as `MaintenanceJobs.cs:40-44`);
- `RunAsync` = `codeEmbedder.EmbedCodePendingBatchAsync(connection, RowsPerRun)` with `RowsPerRun = 4 * 32` (same bounded-drain reasoning as `MaintenanceJobs.cs:26-36`), returns `false` (a re-embed never creates work);
- registered in the job list after `PendingEmbedJob` (`AppRegistrations.cs:155-179` — the list is the schedule).
The one job covers both drain causes — initial pending rows and fingerprint-change invalidation — because both manifest as `embed_state='pending'` (D-E9). **No sweep/TTL job for code** (D-E12); nothing in the degradation path (`SweepService` etc.) touches `code_entries` by construction (table separation). The engine-fingerprint-change flow is: ops-lane `model set code <dir>` → server configure (`CodeEmbedder.ConfigureCodeAsync`) → invalidation UPDATE → triggers clear `vec_code` → `code-reindex` drains on the 15 s on-demand poll (`BankMaintenanceHostedService`).

---

## 8. File-by-file change list (engineer-owned, in WP order)

New files marked **(new)**; modified marked **(mod)**. Architecture/ops-lane files are listed for dependency only, marked **(dep — other lane)**.

| # | File | Responsibility | Interfaces / rules |
|---|---|---|---|
| E1 | `src/AiRaccoon.Core/Chunking/CodeChunk.cs` **(new)** | `record CodeChunk(string Text, int LineStart, int LineEnd)` — pure | Core, sealed record |
| E2 | `src/AiRaccoon.Core/Chunking/ICodeChunker.cs` **(new)** | `IReadOnlyList<CodeChunk> Chunk(string text, int maxTokens, TokenCount countTokens)` | Core interface (NOT `IChunker` — line-range output) |
| E3 | `src/AiRaccoon.Core/Chunking/CodeChunker.cs` **(new)** | §3 algorithm; ctor `(TokenCount countTokens)` like `MarkdownChunker.cs:16-27` | Core, sealed, pure |
| E4 | `src/AiRaccoon.Core/Chunking/CodeChunkingDefaults.cs` **(new)** | `DefaultChunkTokens = 126`, `OverlayTokens = 0` | static class, constants only |
| E5 | `src/AiRaccoon.Core/Ingestion/CorpusKind.cs` **(new)** | `enum CorpusKind { Memory, Code, Neither }` | Core |
| E6 | `src/AiRaccoon.Core/Ingestion/CodeExtensions.cs` **(new)** | §4.1 constant extension set | static class, constants only |
| E7 | `src/AiRaccoon.Core/Ingestion/IngestDispatcher.cs` **(new)** | `CorpusKind Classify(IFileTypeMatcher memory, IFileTypeMatcher code, string path)`; memory wins | static class, pure function |
| E8 | `src/AiRaccoon.Core/Ingestion/IgnoreRules.cs` **(new)** | §5.2 parse + match (`Parse`, `IsIgnored`); `IgnorePattern` record | static class, pure functions |
| E9 | `src/AiRaccoon.Core/Watch/WatchOverlapException.cs` **(new)** | overlap rejection exception (message names the containing watch) | Core, `Core/Watch` family (like `WatchDisabledException`) |
| E10 | `src/AiRaccoon.Infrastructure/Ingestion/CodeFileTypeMatcher.cs` **(new)** | `IFileTypeMatcher` over `CodeExtensions`, case-insensitive normalize | Infrastructure, sealed |
| E11 | `src/AiRaccoon.Infrastructure/Ingestion/ICodeIngestor.cs` **(new)** | §4.2 one-method contract | Infrastructure (mirrors `IFileIngestor`'s home) |
| E12 | `src/AiRaccoon.Infrastructure/Ingestion/CodeIngestor.cs` **(new)** | §4.2 flow: scope → hidden → matcher → chunk → insert/refresh → embed-inline-or-pending | Infrastructure, sealed; ctor `(ICodeFileTypeMatcher, ICodeChunker, ICodeEmbedder, TimeProvider)` |
| E13 | `src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs` **(mod)** | `IngestDirectoryAsync` gains ignore filter + `IngestDispatcher` routing to `ICodeIngestor` | ctor gains `ICodeIngestor` + code matcher |
| E14 | `src/AiRaccoon.Infrastructure/Embedding/ICodeEmbedder.cs` **(new)** | §6.5 contract | Infrastructure |
| E15 | `src/AiRaccoon.Infrastructure/Embedding/CodeEmbedder.cs` **(new)** | §6.5: configure/invalidation, inline embed, pending drain, query embed | Infrastructure, sealed; ctor `(IEmbeddingService, TimeProvider)` |
| E16 | `src/AiRaccoon.Infrastructure/Embedding/CodeModelDefaults.cs` **(new)** | default code descriptor (768, 128 ctx, sentencepiece, model-output, ids 2/3/0/1) + bundled tokenizer resolution (BundledResource pattern) | Infrastructure; static constants + resource path |
| E17 | `src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs` **(mod)** | `CreateCodeGenerator`, kind-prefixed cache, `CodeEngineFingerprint`, `CodeSafeChunkBudgetFor`, `GetCodeTokenizer`, `TrimCodeQueryToWindow` (event 417), `EmbeddingSettingsKeys.CodeModel/CodeEngine` usage | per D-E6/D-E7, engine plan D7/D9 |
| E18 | `src/AiRaccoon.Infrastructure/Embedding/EmbeddingSettingsKeys.cs` **(mod)** | `CodeModel = "embedding.codeModel"`, `CodeEngine = "embedding.codeEngine"` | static class, constants |
| E19 | `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs` **(mod)** | code SQL constants: `InsertCodeEntry`, `SelectCodeChunkIdByPathAndHash`, `UpdateCodeChunkPosition`, `MarkCodeEmbedded`, `SelectCodePendingForEmbed`, `SelectAllCodePendingForEmbed`, `HasCodePendingEmbed`, `DeleteCodeBySourcePath`, `MarkAllCodeEmbeddedPending`, `CountCodeProjectEntries` | internal static class (existing pattern) |
| E20 | `src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.Code.cs` **(new partial)** | code CRUD + `EmbedCodePendingAsync(projectId, limit)` + `EmbedCodePendingBatchAsync(limit)` + `HasCodePendingAsync` + stats helper | partial seam (the repo's established pattern: `Replace.cs`, `Search.cs`, `SearchParameters.cs` — keeps the size-ratchet test green) |
| E21 | `src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.cs` + `.Replace.cs` **(mod)** | ctor gains `ICodeIngestor` + `ICodeEmbedder`; `ReplaceCoreAsync` legs 4+6; `DeleteSourcePathAsync` leg 4; `IngestDirectoryAsync` dispatch | one-bank-open rule preserved |
| E22 | `src/AiRaccoon.Infrastructure/Watch/IWatchScanInitiator.cs` **(new)** | `void EnqueueInitialScan(string projectId, string path)` — breaks the pipeline→executor cycle | Infrastructure |
| E23 | `src/AiRaccoon.Infrastructure/Watch/WatchCatchUp.cs` **(mod)** | implements `IWatchScanInitiator`; ignore load per scan; `EnumerateFiles` ignore skip; `ReconcileIgnoredAsync` | — |
| E24 | `src/AiRaccoon.Infrastructure/Watch/WatchDigestExecutor.cs` **(mod)** | §5.3 flow: ignore gate (no fingerprint), dual pending drain, ignore-change re-scan trigger | ctor gains `ICodeEmbedder`-aware store + `IWatchScanInitiator` |
| E25 | `src/AiRaccoon.Infrastructure/Watch/WatchService.cs` **(mod)** | §5.5: containment check, reject contained, prune containing-existing | — |
| E26 | `src/AiRaccoon.Infrastructure/Maintenance/CodeReindexJob.cs` **(new)** | §7 on-demand drain job | `IMaintenanceJob` |
| E27 | `src/AiRaccoon/Setup/AppRegistrations.cs` **(mod)** | DI: code matcher/ingestor/embedder/chunker/defaults, `IWatchScanInitiator`, job list entry | — |
| — | `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs` **(dep — arch lane)** | `code_entries`/`code_fts`/`vec_code float[768]` DDL + `code_fts_ai/ad/au`, `vec_code_au/pending/ad` triggers; `idx_code_entries_path`; no `sweep`/`ttl` columns | — |
| — | `src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.Search.cs` + `SearchResultMerger.cs` **(dep — arch lane)** | `SearchCodeAsync` hybrid (FTS5 `code_fts` + vec0 `vec_code` + existing RRF), project scope only | — |
| — | `src/AiRaccoon/Tools/MemoryTools.cs` + `WatchTools.cs` **(dep — arch lane)** | `memory_search kind=memory\|code\|both`, `code_get`, envelope records; watch tools unchanged wire | MCP-thin |
| — | CLI `model set code` / docs **(dep — ops lane)** | settings write → server configure path | — |

---

## 9. Work packages with gates (TDD, RED-first)

Every WP starts with its failing test (TDD mandatory — CLAUDE.md invariant). Gates name the QA-lane test categories; the QA lane owns the detailed cases, the engineer owns these acceptance criteria. Engine-generalization WPs 1–4 (support-for-other-embedding-models) and the arch-lane schema WP are prerequisites (marked). `dotnet test --filter "Category=<name>"` is the checkable form.

| WP | Lane | Deliverable | Acceptance criteria | Gate |
|---|---|---|---|---|
| **WP-E0** | all | Plan review | owner + arch/QA/ops lane alignment on §2 decisions (esp. D-E3 seam, D-E4 ignore, D-E5 overlap) | G-E0: owner review in Rider |
| **WP-E1** | eng | §3 `CodeChunker` + `CodeChunk`/`ICodeChunker`/defaults (Core, pure) | RED first: blank-line blocks; brace-balance boundary preference (balanced cut beats greedy end; unbalanced ⇒ budget cut); single-line overflow hard-split via `TokenBudget.Trim`; 126-budget packing with exact joined recount; 1-based line ranges incl. hard-split pieces (`LineStart == LineEnd`); empty/all-blank ⇒ `[]`; hostile-fixture sweep zero violations | G-E1: `Category=CodeChunker` green (unit + corpus-guarantee property) |
| **WP-E2** | eng | §4.1 + §5.2 Core routing/ignore primitives: `CodeExtensions`, `CodeFileTypeMatcher`, `IngestDispatcher`, `CorpusKind`, `IgnoreRules`, `WatchOverlapException` | RED first: matcher case-insensitivity; dispatcher memory-wins + Neither; registry-disjoint test (`CodeExtensions ∩ memory extensions = ∅`); ignore: comments/blank lines, `*`, `**`, leading-`/` anchor, trailing-`/` directory, unanchored depth, ancestor-dir matching, CRLF, host-case; `/repo` vs `/repo2` boundary | G-E2: `Category=IngestRouting` green |
| **WP-E3** | eng (needs arch schema WP + engine WP3) | §4.2/§4.3 code ingest: `CodeIngestor`, `ICodeIngestor`, `MemorySql` code constants, `CodeEmbedder` skeleton (pending-only), store code partial, `FileIngestor` directory dispatch | RED first: unscoped project refuses; hidden skipped; non-code skipped; insert + `embed_state='pending'` with no engine; **position refresh on dedup rediscovery** (file gains leading lines ⇒ `line_start` shifts without re-insert); mixed-tree `memory_ingest_directory` (docs→memory, code→code, other→skipped); embed-inline with fake generator; return-count semantics | G-E3: `Category=CodeIngest` green |
| **WP-E4** | eng | §5 watch channeling: `ReplaceCoreAsync`/`DeleteSourcePathAsync` both-corpora legs, digest ignore gate + dual drain + re-scan trigger, `WatchCatchUp` ignore + reconcile, `WatchService` overlap prune/reject, `IWatchScanInitiator` | RED first: `.cs` change → code rows replaced + fingerprint shared; `.md` change → memory rows replaced, code leg no-op; delete/rename removes from **both** corpora in one transaction (crash-rollback test); rename `.cs`↔`.md`; mixed-tree catch-up; ignored file: no fingerprint, stale chunks deleted, hash-skip never touches it; ignore edit → single-flighted full re-scan (test awaits `LastScan`, `WatchCatchUp.cs:19-20`); overlap: `/repo`+`/repo/src` prune (registration+watch_files cascade+runtime removed via `UnregisterWatch`, entries survive), `/repo`+`/repo2` unaffected, narrower-add rejected naming the broader watch, symlink containment, idempotent re-add | G-E4: `Category=WatchCodeChanneling` + `Category=WatchOverlap` green |
| **WP-E5** | eng (needs ops code-model download for the real-model leg) | §6 code engine: `EmbeddingService` code resolution/cache/fingerprint/budget/tokenizer/query-trim (event 417), full `CodeEmbedder` (configure/invalidate/query), `CodeModelDefaults` | RED first: two engines coexist (memory + code generators cached under distinct keys; sessions distinct); fingerprint change (`embedding.codeEngine`) ⇒ all code rows pending (trigger clears `vec_code`) ⇒ drain re-embeds; non-768 manifest refused with actionable error; missing dir/manifest refused; query trim to 126 with code tokenizer, no prefix; `QueryVector.Empty` without engine; **real-model leg (fixture bank, code-daemon artifact from ops lane):** 768-dim blobs land in `vec_code`, `code_search`-shaped query returns rows, cosine sanity on a known pair | G-E5: `Category=CodeEmbedding` green (fake-generator + manifest-fixture tests); G-E5b (real model, ops artifact present): integration + parity smoke |
| **WP-E6** | eng | §7 `CodeReindexJob` + registration | RED first: `HasWorkAsync` false without engine or without pending rows; bounded drain (4×32/run, returns next poll); ledger row `code-reindex` recorded (ADR-0070); no sweep/TTL job for code in the job list | G-E6: `Category=CodeReindex` green |
| **WP-E7** | eng+arch+ops | ADR (extends the engine-generalization ADR family), docs drift audit, one squash-merge PR | ADR covers D-E1…D-E12 (chunker contract, dispatch seam, ignore semantics, overlap rule); PR merged | G-E7: review + merge |

**Cross-cutting:** the code model artifact (ops lane `model download faxenoff/code-daemon-embed-v1`) must exist before G-E5b; the arch lane's schema/triggers WP must land before WP-E3's store legs; the QA lane's eval-harness phase (code corpus A/B: code-daemon vs jina-code-v2 on the heuristic chunks, exploration §6 risk 2) is the quality gate outside this lane's correctness gates.

---

## 10. Assumption & UNVERIFIED register

| # | Item | Status | Resolves |
|---|---|---|---|
| A1 | No glob/ignore handling exists in `Watch/` today (nothing to reuse or collide with) | VERIFIED (grep 2026-08-21) | — |
| A2 | `IngestPath.IsWithinScope` is the correct containment predicate for the overlap rule (separator-safe, symlink-safe) | VERIFIED (`IngestPath.cs:47-60`) | — |
| A3 | Non-indexable files are fingerprinted today and must stay so | VERIFIED (`SqliteMemoryStore.Replace.cs:93-99`) | — |
| A4 | The engine-generalization WP1–WP4 lands before this feature (manifest, sentencepiece, model-output, dynamic dims, ctx−2 ≤ 510, D7 fingerprint, D9 routing) | ASSUMED (task brief; plan approved) | — |
| A5 | code-daemon `sentencepiece.bpe.model` can ship as a bundled resource (626 KB, MIT) for the unconfigured-counting invariant (D-E7) | ASSUMED — owner sign-off; alternative: defer code chunking until engine configured (rejected, worse) | OQ4 |
| A6 | A raw brace-char scan is a good-enough boundary heuristic (strings/comments skew tolerated) | ASSUMED — eval harness decides (exploration §6.2); budget is the hard bound regardless | QA eval phase |
| A7 | Full re-scan on ignore edit is cheap enough (hash-skip) for repos this feature targets | ASSUMED — measured in G-E4 gate test with a 10k-file fixture tree | G-E4 |
| A8 | sqlite-vec `float[768]` insert of a wrong-dim blob errors loudly (existing behavior, not re-verified for 768) | ASSUMED — inherits the engine plan's WP4 guard discussion | arch lane |
| A9 | `vec_code` fixed at 768 for v1; non-768 manifests refused (no dim migration for code in v1) | DECIDED (D-E6/D-E9) | — |
| A10 | The v1 extension list (§4.1) covers the eval corpus repos | ASSUMED — owner-adjustable | OQ1 |
| A11 | `memory_ingest_file` on a code file stays memory-only (returns 0) in v1 | DECIDED (D-E10) | OQ2 |

---

## 11. Hand-off notes for sibling lanes

- **Architecture lane:** schema DDL + trigger family for `code_entries`/`code_fts`/`vec_code float[768]` (mirror `MemorySchema.cs:125-201`; `vec_code_au/pending/ad` + `code_fts_ai/ad/au`; `idx_code_entries_path` for the delete legs); search partials (`SearchCodeAsync`) over the two corpora; `memory_search kind`/`code_get` wire shape per exploration §3 Q3; `memory_stats` code counts (OQ6). The engineer's store legs (WP-E4) depend on `idx_code_entries_path` existing.
- **QA lane:** test catalog for the named categories (G-E1…G-E6) — chunker corpus-guarantee sweep, routing/dispatcher matrix, ingest + position-refresh, channeling + overlap matrix (incl. the `/repo` vs `/repo2` trap), embedding two-engine + invalidation, reindex ledger; eval-phase A/B per exploration §6 risk 2 (Python repo in the corpus).
- **Ops lane:** `model download faxenoff/code-daemon-embed-v1` + registry pin (exploration §6 risk 1) before G-E5b; `model set code <dir>` CLI verb calling `CodeEmbedder.ConfigureCodeAsync`; docs for `ai-raccoon.ignore` (placement/syntax) and the overlap rule; release notes.
- **Docs lane:** ADR covering D-E1…D-E12; ADR-0036's known-gap wording audit for the code tokenizer family.

## 12. Owner decisions requested

1. Approve D-E1…D-E12 (G-E0).
2. Sign off the §4.1 extension list (OQ1).
3. Accept bundling the code-daemon tokenizer (626 KB) as the unconfigured counting tokenizer (OQ4), or pick the reject-alternative.
4. Confirm `memory_ingest_file` stays memory-only in v1 (OQ2) — the watch and directory-ingest paths cover code onboarding.
