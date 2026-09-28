# Plan: config D chunk budgets (memory 254 → 1022, code stays 510)

**Date:** 2026-09-28
**Lane:** planning only. This document is the deliverable; nothing else changes here.
**Ships as:** 1.54.0 (`VERSION` is 1.53.13 today, read from the repo root).
**Owner decisions at dispatch (settled):** (1) memory chunk budget becomes 1022 via `chunkTokens` in
the bundled manifest, code stays at 510; (2) existing banks migrate by re-chunking **and**
re-embedding, both, not one; (3) the query-trim budget follows `chunkTokens` to 1022, the coupling is
deliberate and ratified; (4) no pre-adoption retrieval measurement, a post-merge 254-vs-1022
measurement is a separate follow-up task, referenced from the new ADR; (5) the MLX padding
precondition from the 2026-09-25 research must be verified on every execution-provider path and
pinned by tests, tests only where a gap exists.

**Review fold (2026-09-28).** `docs/work/2026-09-28-config-d-plan-review.md` returned APPROVE-WITH-FIXES
(3 MUST, 6 SHOULD); all are folded into the sections below in place. Two fold decisions where the
review offered options: (a) skip classes are split — *retryable* skips (note groups whose hash check
fails after `NoteTextOrder` backtracking) gate the `embedding.chunkBudget` stamp and are retried via
the re-chunk-only drift path once per server start until they converge; *terminal* skips (mirror rows
whose source file is gone — nothing re-chunkable exists) are counted in the phase report, named in
ADR-0125 and breaking-changes, and do not gate the stamp because they cannot converge (F1 mech 4 + F6);
(b) budget drift opens a **re-chunk-only** migration (no `MarkAllEmbeddedPending`) — chunk boundaries
are the only thing that changed, so a full re-embed there is the pointless outage F4 identified.

**Anchor verification.** Every file the brief named exists in this worktree and says what the brief
claimed. Three small corrections, all recorded here:

1. The brief cites `EmbeddingServiceManifestBudgetTests.cs:72-80` for the 254 pin. The pin is at
   `tests/AiRaccoon.Tests/Integration/Embedding/EmbeddingServiceManifestBudgetTests.cs:77`
   (`.ShouldBe(254)` inside `ResolveChunkBudgetFor_BundledLocal_IsItsManifestsChunkTokens`, lines
   72-79). The brief's range is right to within a line.
2. The brief lists `scripts/src/retrieval_tuning/eval-sets/code-smoke.json` CS-04 as a "wording" pin
   of 254/510/256. It is not a numeric pin. CS-04's query and answerSpan
   (`scripts/src/retrieval_tuning/eval-sets/code-smoke.json:56-57`) name "the bundled model's
   embedding window", never a number. Nothing moves; one wording refresh is recommended (see
   "Docs/ADR work").
3. The brief's mechanism list for re-chunking is complete but misses one live path worth naming:
   `ai-raccoon repair reingest --apply` already re-chunks existing **file** rows whenever the current
   chunker cannot reproduce their hashes (`src/AiRaccoon.Infrastructure/Ingestion/ReingestRepair.cs:89-101`),
   which a budget change triggers. It is manual, covers mirror rows only, and discards per-row
   metadata (`ReingestRepair.cs:22`). It is a building block, not the answer.

Nothing met the abort criteria. The code contradicts no settled owner decision (decision 3's
coupling is literally how the code is written, see F1-F3 below), every anchor file exists, and the
migration's destructive shape (delete-and-reinsert rows) has a recovery path as long as one
transactional rule is obeyed (specified in P1). One semantic loss is real and is reported, not
buried: re-chunking discards per-row metadata (rating, access_count, last_accessed_at), the same
loss the existing reingest repair already documents.

---

## 1. Facts vs inferences

Everything below is cited `path:line` unless marked `[INFERRED]`, which means "reasoned from the
cited code, not stated by it". Review corrections that amend these facts: the `ReconcileVecDimensionsAsync`
slot is at `EntryEmbedder.cs:185` (not 183-184) — the substantive slot claim is TRUE for every
migration-closing path (review F4); F5's code-churn claim holds only for the **bundled-default** code
engine (a separately-activated code model fingerprints its own manifest and sees no churn); F10's gap 1
is re-scoped by review Facts-corrected 1 — a real-MLX padding assertion exists
(`BundledEngineMlxSessionTests.cs:47`) but `Assert.Skip`s on every suite host, and test #10 below is
rewritten to a short-row pin because a full-budget row defeats its own mutation (review F2).

### 1.1 Known unknown 1: fingerprint and the re-embed mechanism

**F1.** The bundled engine fingerprint is `local:bundled#<sha256 of the bundled manifest file>`:
`src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs:308-314` hashes
`ai-raccoon.manifest.json` bytes wholesale. Editing `chunkTokens`
(`src/AiRaccoon/Models/granite-embedding-small-english-r2/ai-raccoon.manifest.json:75`) changes the
manifest bytes, so it changes the fingerprint. Confirmed by the tests that pin this property
(`tests/AiRaccoon.Tests/Integration/Embedding/EmbeddingServiceManifestBudgetTests.cs:202-213`,
`:216-224`).

**F2.** A fingerprint change re-embeds the whole memory bank automatically. `ModelMigrationJob`
calls `EntryEmbedder.ReconcileFingerprintAsync` on every maintenance poll
(`src/AiRaccoon.Infrastructure/Maintenance/ModelMigrationJob.cs:26-33`); the reconcile compares the
stored `embedding.engine` setting against a fresh `EngineFingerprint` and, on mismatch, calls
`StartMigrationAsync` (`src/AiRaccoon.Infrastructure/Embedding/EntryEmbedder.cs:49-67`). That runs
one outbox transaction that writes the new engine settings, opens the `model_migration` row, marks
every embedded row `pending` and resets embed attempts (`EntryEmbedder.cs:96-121`, statements
`MemorySql.MarkAllEmbeddedPending` / `ResetEmbedAttempts` at
`src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs:463`, `:456-459`). The relay then drains: every
pending row is re-embedded in batches of 32 until none are left and the migration closes
(`EntryEmbedder.cs:125-232`, selection `MemorySql.cs:450-453`, close `EntryEmbedder.cs:210`). This
is ADR-0108's "one engine, one re-embed" (`docs/adr/0108-one-bundled-engine-granite-small-fp16-on-the-gpu.md`,
Decision 3).

**F3.** While the migration row is open, tool calls are refused. The outbox row is "the lock for
'all DB operations refused for the duration' (ADR-0076)"
(`src/AiRaccoon.Core/Memory/IModelMigrationStore.cs:22-25`), enforced by the per-tool gate
(`src/AiRaccoon/Tools/IToolGate.cs:5`,
`src/AiRaccoon/Settings/ServerSettingsStore.cs:122-125`). `docs/reference/breaking-changes.md:5`
promises users exactly this ("the bank refuses tool calls until that finishes, minutes on a large
bank").

**F4. Cost shape, measured anchors.** A real 25,917-entry bank drained in 357 s wall-clock on the
old engine (`docs/adr/0076-model-set-is-an-outbox-drained-by-an-on-demand-relay.md:296`), and
ADR-0108 quotes "roughly 6 minutes of refused tool calls" for that bank
(`docs/adr/0108-...-on-the-gpu.md`, Consequences). On MLX, embed cost per 1k tokens is flat from 128
to 1022 tokens (46-57 ms per 1k, `docs/work/2026-09-25-chunk-size-vs-attention-window-mlx.md` F5).

**INFERRED (cost estimate for a ~23k-row bank under config D).** Re-chunking at 1022 shrinks the
row count by roughly 4x where content was split (the eval corpus went 1,608 → 383 memory chunks,
2026-09-24 record table) while total embedded text stays about the same minus overlay duplication.
Combined with F5's flat per-token cost, the drain stays minutes-scale on MLX and is dominated by
token count, not row count. The outage window (F3) is therefore of the same order as the 1.47.0
migration, single-digit minutes on a 23k-row bank. Not measured; the post-merge measurement should
record it. The figure counts embed tokens only — the re-chunk phase's own cost (scan, file reads,
`NoteTextOrder` verification search, column repair) is excluded and must be measured too (review F8).

**F5. The code corpus re-embeds too, as churn.** The code engine's fingerprint is the same bundled
manifest hash (`src/AiRaccoon.Infrastructure/Embedding/CodeEmbedder.cs:175`), and `CodeReindexJob`
reconciles it on every poll (`src/AiRaccoon.Infrastructure/Maintenance/CodeReindexJob.cs:40-50`),
invalidating all `code_entries` (`MemorySql.MarkAllCodeEmbeddedPending`,
`src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs:466-468`). Code chunks do not change (the code
budget never reads `chunkTokens`, see F10), so the code rows re-embed to identical vectors. This is
wasted work inside the accepted "one engine, one re-embed" shape. The plan keeps it (fingerprint
semantics deliberately cover every manifest byte,
`docs/adr/0108-...-on-the-gpu.md` Decision 3, D7) and the ADR names the cost.

### 1.2 Known unknown 2: what re-chunks existing rows on a budget change

**F6. Nothing today, automatically.** Mechanism by mechanism:

| mechanism | what it does | re-chunks on budget growth? |
|---|---|---|
| `FileIngestor` chunking | chunks new ingests at the current budget (`src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs:240-246`) via `ChunkSizeForAsync` (`FileIngestor.cs:378-392`, budget from `ResolveChunkBudgetFor` at `:387`) | no, new content only |
| `SqliteMemoryStore.ChunkToBudgetAsync` | chunks new `memory_write` content (`src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.cs:120-124`, `FileIngestor.cs:219-227`) | no, new content only |
| `ChunkBackfill` | splits rows whose own value exceeds the budget (`src/AiRaccoon.Infrastructure/Ingestion/ChunkBackfill.cs:57`, `rows.Where(r => countTokens(r.Value) > budget)`) | **no**. After a growth every row is under budget, so the filter selects nothing. Row-at-a-time anyway, no group merge |
| `ChunkPositionScanner` | re-chunks a source file and matches stored rows to it by content hash for position repair (`src/AiRaccoon.Infrastructure/Ingestion/ChunkPositionScanner.cs:57-80`) | no, read-only scan |
| `ReingestRepairJob` / `ReingestRepair` | delete-and-reingest of mirror rows the current chunker cannot reproduce (`src/AiRaccoon.Infrastructure/Ingestion/ReingestRepair.cs:89-101`), via `IMemoryStore.ReplaceAsync` | **yes for file mirror rows**, but only on an explicit `ai-raccoon repair reingest --apply` request (`src/AiRaccoon.Infrastructure/Maintenance/ReingestRepairJob.cs:30-43`), only for `path = source_file` rows whose file still exists (`ReingestRepair.cs:55-63`), and it discards per-row metadata (`ReingestRepair.cs:22`) |
| model migration drain | re-embeds row values as stored (`EntryEmbedder.cs:194-205`) | no, never touches chunk boundaries |

**F7. Notes have no source file, so no existing repair reaches them.** A `memory_write` note is
stored as one row per chunk (`SqliteMemoryStore.cs:120-124`,
`src/AiRaccoon.Infrastructure/Sqlite/WriteChunks.cs:36-53`). Its chunks share `path =
<sha256-of-full-content>.md` (`SqliteMemoryStore.WritePathFor`, `SqliteMemoryStore.cs:837`); the
whole-write delete uses exactly that group definition (`MemorySql.DeleteMatchPredicate`,
`MemorySql.cs:186-191`). Chunk strings include a 48-token overlay: each chunk after the first is
built from overlay units plus new units (`src/AiRaccoon.Core/Chunking/MarkdownChunker.cs:58-59`,
`:82-83`, `:169-193`), so concatenated stored chunks duplicate the overlay and do not equal the
original note.

**F8 (the recovery key).** `WritePathFor` commits the full note content into the row's own path name
(`SqliteMemoryStore.cs:837`). A reassembly of a note group can therefore be proven exact: `sha256` of
the merged text must equal the hex stem of `path`. Any failure means "do not touch this group".

**Smallest addition (design, P1 below).** A `ChunkBudgetReconciler` phase that runs inside the
model-migration drain, after the vec-dimension reconcile and before the embed loop (the exact slot
`ReconcileVecDimensionsAsync` already occupies, `EntryEmbedder.cs:183-184`), i.e. under the migration
lease and before the migration can close. It re-chunks mirror groups from disk (reusing
`ReingestRepair`'s scan-and-replace) and note groups by verified reassembly (F7, F8), replacing rows
atomically per group. Trigger: the model migration itself (fingerprint change, F2), plus a
budget-drift check on a new `embedding.chunkBudget` settings row so a bank that already consumed the
new manifest without the re-chunk (an interim build, see the rejected alternatives) self-heals.

**Ordering with the re-embed drain (owner requirement).** Because the phase runs before
`SelectAllPendingForEmbed` is first read (`EntryEmbedder.cs:194`), every row the drain embeds carries
new-bounds chunk text; re-chunked rows arrive `pending` and are embedded in the same drain; the
migration closes only when no pending row is left (`EntryEmbedder.cs:194-210`). Crash mid-re-chunk:
the migration row stays open, the next poll re-runs the phase (idempotent, see P1 ACs) and then the
drain. A re-embed before a re-chunk would embed rows about to be deleted and would close the
migration with replacement rows still pending; the phase ordering rules both out by construction.

### 1.3 Known unknown 3: the padding precondition (ADR-0114)

**F9. Where padding lives, per execution provider.** The product supports CPU, WebGPU (`device
gpu`/`auto`), MLX (`device mlx`, ADR-0110), CUDA (opt-in, ADR-0112) and CoreML/Neural Engine
(`device coreml`, ADR-0118, a WebGPU wrapper). Row padding by path:

| EP path | pads? | evidence |
|---|---|---|
| MLX | yes, to 64-token buckets (`LengthBuckets.Step`, `src/AiRaccoon.Core/Embedding/LengthBuckets.cs:10`) | `_bucketRows` set only when an MLX session is created (`src/AiRaccoon.Infrastructure/Embedding/OnnxEmbeddingGenerator.cs:611`; test hook `:247`); `RunBatch` pads the row length via `LengthBuckets.PaddedLength` with zero attention mask (`OnnxEmbeddingGenerator.cs:675-700`) |
| CoreML / Neural Engine | yes, to 256-token buckets up to a 1024-token window (`LengthBuckets.CoreMlStep`/`CoreMlWindow`, `LengthBuckets.cs:13-16`; buckets 256/512/768/1024, `src/AiRaccoon.Infrastructure/Embedding/NeuralEngine/CoreMlGraph.cs:22-23`) | `NeuralEngineEmbeddingGenerator` pads each row to its bucket (`src/AiRaccoon.Infrastructure/Embedding/NeuralEngine/NeuralEngineEmbeddingGenerator.cs:358-361`) and routes rows over 1024 tokens to the CPU fallback (`:142`) |
| CPU, WebGPU, CUDA | no, by decision | ADR-0114 Decision ("CPU, WebGPU and CUDA sessions run rows at their own length as before"); pinned for CPU by `tests/AiRaccoon.Tests/Integration/Embedding/BundledEngineBucketPaddingTests.cs:39-46` |

**F10. The MLX precondition is shipped.** ADR-0114's decision ("an MLX session pads every row to the
next multiple of 64") is implemented (F9 row 1) and the vector-parity property is tested
(`BundledEngineBucketPaddingTests.cs:23-37`, parity at cosine ≥ 0.99999, through the executor seam).
Config D's exact budget satisfies ADR-0114's alignment rule with zero waste: 1022 + 2 = 1024 is a
multiple of 64 and already enumerated in `tests/AiRaccoon.Tests/Unit/Embedding/LengthBucketsTests.cs:34-42`.

**What is unpinned by tests today (the gap the brief asks to close, tests only):**

1. Nothing asserts that a **real MLX session** pads. `BundledEngineBucketPaddingTests` reaches the
   padding code through `AttachMlxExecutorForTesting` over a CPU session
   (`BundledEngineBucketPaddingTests.cs:31`), so a regression that stops setting `_bucketRows` at
   `OnnxEmbeddingGenerator.cs:611` leaves every existing test green while the 17.9 GB footprint
   returns. This is the precondition pin.
2. Nothing ties the **bundled manifest's** `chunkTokens` to the bucket rules. A future manifest with
   `chunkTokens: 1000` would pad every full row 1002 → 1024 (waste, more shapes) and no test objects.
3. `CoreMlWindowBudgetTests` checks "every shipped budget + 2 ≤ 1024"
   (`tests/AiRaccoon.Tests/Unit/Embedding/CoreMlWindowBudgetTests.cs:20-28`) over a literal set
   {254, 510, 510} that does **not** include the manifest budget. After config D the shipped memory
   budget is 1022 and this gate would not cover it. 1022 + 2 = 1024 fits exactly; one more and every
   CoreML row falls back to CPU silently.
4. WebGPU footprint growth per distinct shape is **unmeasured**. The MLX record's own "Still open"
   says so ("Check whether the WebGPU path (`device auto`) shows the same per-shape growth"), and the
   CPU record's F7 left GPU cost unmeasured at 1022. Padding on WebGPU is deliberately absent (F9),
   so config D on WebGPU rests on unverified evidence. Cannot be closed by tests; named as residual
   risk in the ADR and folded into the post-merge measurement (see §5).

### 1.4 Known unknown 4: everything that pins 254 / 510 / 256

Legend for the last column: **moves** = must change with config D; **stays** = intentionally
unchanged afterwards; **historical** = a dated record, never edited.

| # | path:line | what it pins | disposition |
|---|---|---|---|
| 1 | `src/AiRaccoon/Models/granite-embedding-small-english-r2/ai-raccoon.manifest.json:75` | `chunkTokens: 254`, the value itself | **moves** → 1022 (the whole change) |
| 2 | `src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs:480` | `descriptor.ChunkTokens ?? min(510, ctx − 2)` resolution | stays (mechanism, no constant) |
| 3 | `src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs:215-216` + doc comment `:203-209` | bundled budget resolves through the manifest; comment says "bundled and legacy local stay 254" | comment **moves** (code stays) |
| 4 | `src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs:44` (`BundledModelContextTokens = 256`), `:54` (`MaxManifestChunkTokens = 510`) | legacy MiniLM window; the manifest cap | stays |
| 5 | `src/AiRaccoon.Infrastructure/Embedding/OnnxEmbeddingGenerator.cs:27` (`MaxContentTokens = 254`) | legacy `.onnx`-file budget and the non-manifest query-trim fallback (`EmbeddingService.cs:171-178`) | stays (MiniLM test assets and legacy file models still use it) |
| 6 | `src/AiRaccoon.Infrastructure/Chunking/CodeChunker.cs:29` (`DefaultBudget = MaxManifestChunkTokens` = 510) | code budget | stays (owner decision 1) |
| 7 | `src/AiRaccoon.Core/Memory/QueryGuard/QueryLengthGuard.cs:25-31` (`BundledBudgetTokens = 254`, `WarnThresholdChars = 1000`) | guard fallback + char ratio anchor | stays (default for callers that pass no budget); class doc `:5-24` **moves** ("254 for the bundled model" wording) |
| 8 | `tests/AiRaccoon.Tests/Integration/Embedding/EmbeddingServiceManifestBudgetTests.cs:77` | `.ShouldBe(254)` for bundled resolution | **moves** → 1022 |
| 9 | `tests/AiRaccoon.Tests/Integration/Embedding/EmbeddingServiceManifestBudgetTests.cs:81-88` | legacy `.onnx` stays 254 | stays |
| 10 | `tests/AiRaccoon.Tests/Integration/Embedding/EmbeddingServiceManifestBudgetTests.cs:12-15` | class doc "bundled stays 254" | **moves** |
| 11 | `tests/AiRaccoon.Tests/Integration/Embedding/EmbeddingServiceManifestBudgetTests.cs:155-186` | query trim at `MaxManifestChunkTokens` for a manifest **without** `chunkTokens` | stays (still true); the explicit-`chunkTokens` coupling is unpinned, new test below |
| 12 | `tests/AiRaccoon.Tests/Integration/Embedding/QueryTruncationTests.cs:75` | the 426 message quotes "254" | **moves** → "1022" |
| 13 | `tests/AiRaccoon.Tests/Integration/ChunkingCorpusGuaranteeTests.cs:21`, `:44` | corpus guarantee at budget 254, ceiling 256 | **moves**: parameterize to cover the resolved bundled budget 1022 with ceiling 1024 (the 254 case stays as the legacy path's guarantee) |
| 14 | `tests/AiRaccoon.Tests/Integration/Memory/WriteChunksToBudgetTests.cs:29` (`BertWindow = 256`) | write-path chunks fit the "bundled model's window" | **moves**: resolve the window from the manifest budget + reservation, red otherwise at the flip |
| 15 | `tests/AiRaccoon.Tests/Unit/Chunking/CodeChunkerTests.cs:87-92` | `CodeChunker.DefaultBudget.ShouldNotBe(254)`, message "254 is the memory bundled-model budget" | **moves**: `ShouldNotBe(1022)` + message |
| 16 | `tests/AiRaccoon.Tests/Unit/Embedding/CoreMlWindowBudgetTests.cs:20-28` | shipped budgets fit CoreML 1024; literal set lacks the manifest budget | **moves**: derive the bundled manifest budget into the set |
| 17 | `tests/AiRaccoon.Tests/Unit/Embedding/LengthBucketsTests.cs:34-42` | budgets 254/510/766/**1022** pad exactly | stays (1022 already covered) |
| 18 | `tests/AiRaccoon.Tests/Integration/ChunkBudgetIsEngineAwareTests.cs:75-77` | no-chunk-exceeds-window, window derived from `ResolveChunkBudgetFor` | stays (derived, follows automatically) |
| 19 | `tests/AiRaccoon.Tests/Integration/Ingestion/RepairFamilyRoutesThroughTheEngineResolverTests.cs:109-131` | repair budget at the D6 cap 510 for a manifest model; message names "the bundled 254" | stays (510 unchanged); message at `:110` is a stale wording fix |
| 20 | `tests/AiRaccoon.Tests/Integration/Embedding/WordPieceEmbeddingTokenizerTests.cs:79-91` | CLS + 254 + SEP = 256 on the legacy wordpiece path | stays |
| 21 | `tests/AiRaccoon.Tests/Unit/Memory/QueryGuard/QueryLengthGuardTests.cs:72`, `:105` | guard default 254 and the 1,000/254 ratio | stays (default unchanged); add the 1022 case |
| 22 | `tests/AiRaccoon.Tests/Unit/Mcp/MemoryToolsTests.cs:631` | comment "wider than 254" | comment **moves** (or drops the number) |
| 23 | `tests/AiRaccoon.Tests/Integration/Retrieval/QueryGuardRecallProbe.cs:82`, `tests/AiRaccoon.Tests/Integration/Storage/OverWindowRowProbe.cs:12`, `tests/AiRaccoon.Tests/Integration/Storage/ChunkBackfillTests.cs:16` | 254 in probe/historical comments | comment refresh where trivial; probes are not gates |
| 24 | `tests/AiRaccoon.Tests/Integration/Embedding/NonDefaultDimensionMigrationTests.cs:80`, `EntryEmbedderMigrationDrainReportingTests.cs:320,349` | fakes returning 254 / `MaxContentTokens` | stays (fakes, budget value irrelevant to what they test) |
| 25 | `tests/AiRaccoon.Tests/Unit/Chunking/ChunkWordBoundaryTests.cs:30` | `[InlineData(254, 0)]`, explicit budget parameter | stays |
| 26 | `tests/AiRaccoon.Tests/Unit/Retrieval/assets/reference-topk.json`, `tests/AiRaccoon.Tests/Resources/goldens/minilm-eval-set-100.json`, `MiniLmGoldenVectorTests` | golden retrieval/MiniLM fixtures over stored data | stays (fixture data, not budget-derived; reviewed for baked 254-chunk boundaries: none depend on the live budget) |
| 27 | `docs/explanation/architecture.md:345-350` | "254 content tokens for the bundled granite... An unconfigured bank resolves to the same bundled default: 254 tokens per chunk with a 48-token overlay" | **moves** (also fixes the stale "256-token window" justification, which is MiniLM's) |
| 28 | `docs/explanation/architecture.md:877` | "never the memory chunker's 254" | **moves** |
| 29 | `docs/reference/breaking-changes.md:5-11` | per-version upgrade actions | **moves**: new 1.54.0 entry |
| 30 | `docs/adr/0108-...-on-the-gpu.md` Decision 5 + Consequences | "chunkTokens: 254", "memory chunk budget stays 254 tokens" | **moves**: pointer amendment (recommended, see §5) |
| 31 | `docs/adr/0071-...-said-so.md` + its 2026-09-23 amendment | query trim at 254; "254 for the bundled model" | historical (ADR-0125 records the change; the amendment's mechanism, budget-from-`ResolveChunkBudgetFor`, is what makes the move automatic) |
| 32 | `docs/adr/0036-engine-aware-chunk-token-budget.md` | `MaxContentTokens = 254` derivation for MiniLM | historical |
| 33 | `docs/adr/0114-mlx-length-buckets-and-a-capped-buffer-cache.md` | "(256, 512, 1024) is itself a multiple of 64"; "None ships" | stays true for 1022 (1022 + 2 = 1024); no edit |
| 34 | `docs/work/2026-09-28-why-the-query-budget-stays-254.md`, `docs/work/2026-09-24-chunk-size-vs-attention-window.md`, `docs/work/2026-09-25-chunk-size-vs-attention-window-mlx.md` | dated research records | historical |
| 35 | `scripts/retrieval_tuning/llamaindex_harness/ingest.py:66` | comment "(254 for granite)" | **moves** (comment only); `:230` and `scripts/tests/test_llamaindex_harness_ingest.py:516-522` derive from the manifest (`EMBED_MAX_SEQ_LENGTH == manifest["chunkTokens"] + 2`) and follow automatically |
| 36 | `scripts/src/retrieval_tuning/eval-sets/code-smoke.json:56-57` (CS-04) | wording "the bundled model's embedding window" | **moves**: reword to "the active engine's chunk budget" (no number to move) |
| 37 | `src/AiRaccoon.Core/Chunking/ChunkingDefaults.cs:7` (`OverlayTokens = 48`) | overlay | stays |
| 38 | `src/AiRaccoon.Infrastructure/Embedding/Manifest/EmbeddingManifestValidator.cs:49-51` | `chunkTokens ≤ contextWindowTokens − 2` (1022 ≤ 8188, valid) | stays; add the 1022-valid case to the tests |
| 39 | `tests/AiRaccoon.Tests/Integration/Storage/SqliteMemoryStoreChunkingTests.cs:65-68` | literal `≤ 256` ceiling over budget-derived chunking | **moves** (review F3 item 1; premise corrected — the test configures `openai` at 256 on both sides, so the literal did not go red at the flip; §3 row 21) → derive from resolved budget + reservation |
| 40 | `src/AiRaccoon/Tools/MemoryTools.cs:139-145` | MCP `search` tool description: "254 tokens for the bundled model" — agent-facing | **moves** (review F3 item 2) → de-number or say 1022 |
| 41 | `docs/how-to/configure-embedding-engines.md:42` | "8,190 tokens (chunked to 254 for memory, 510 for code)" | **moves** (review F3 item 3) → 1022 for memory |
| 42 | `src/AiRaccoon/Tools/MemoryTools.cs:320-323`, `src/AiRaccoon.Infrastructure/Embedding/IEmbeddingService.cs:25-26`, `src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs:194-195`, `src/AiRaccoon.Infrastructure/Chunking/CodeChunker.cs:22`, `tests/AiRaccoon.Tests/Unit/Chunking/CodeChunkerTests.cs:85`, `scripts/retrieval_tuning/llamaindex_harness/ingest.py:67`, `tests/AiRaccoon.Tests/Integration/ChunkingCorpusGuaranteeTests.cs:43,51,104-105` | comment/doc wording "254"/"256" aging with the flip | **moves** (review F3 minors: comment/doc cleanups) |
| 43 | `tests/AiRaccoon.Tests/Integration/Embedding/CodeManifestBudgetGuardTests.cs`, `src/AiRaccoon/Setup/AppRegistrations.cs:307`, `src/AiRaccoon.Core/Memory/Code/CodeSearchWarnings.cs:20`, `docs/adr/0120-chunk-boundaries-fall-on-whitespace.md:69`, `docs/features/code-corpus/code-corpus.feature:15-18` | 510 pins / historical mentions | stays (named for exhaustiveness — review F3) |

### 1.5 The query-budget coupling, stated for the ADR

**F11.** The query trim budget is the chunk budget by construction, not by coincidence.
`TrimQueryToWindow` trims manifest models to `ManifestContentBudget`
(`src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs:143-165`, budget at `:154`), which is
`descriptor.ChunkTokens ?? min(510, ctx − 2)` (`EmbeddingService.cs:470-482`, `:480`). The
caller-visible guard takes the same resolved budget (`QueryLengthGuard.cs:33-45`; ADR-0071 amendment
2026-09-23). Flipping `chunkTokens` to 1022 therefore moves the query trim and the guard threshold
(~4,024 chars at the 1,000/254 ratio) with no code change. Per owner decision 3 this coupling is
deliberate and the ADR says so explicitly.

---

## 2. Packages

Merge order is binding where stated. Every package names its files, its acceptance criteria (each
checkable by someone who was not here) and the quality-gate run that proves each criterion.

### P1 — Re-chunk on budget change: `ChunkBudgetReconciler` inside the migration

Behavior on a steady-state bank is unchanged (the phase finds nothing to do while the budget
matches and the stamp is current) — with one correction from review F4: the *absent-stamp* trigger
opens a migration even at an unchanged budget, so the trigger must be the re-chunk-only shape above,
not the full-re-embed path, or P1-alone would open a pointless re-embed + outage on every existing
bank. This package lands the owner's "re-chunk AND re-embed" machinery.

**Subpackages**

- **P1a, note reassembly (Core, pure).** `src/AiRaccoon.Core/Chunking/ChunkReassembly.cs` (new):
  thin wrapper over `NoteTextOrder` (`src/AiRaccoon.Core/Memory/NoteTextOrder.cs`), NOT a new greedy
  matcher (review F1 mech 3 — the "longest suffix-prefix match" over-eats where body text repeats at a
  boundary, turning provable notes into "unverifiable" ones; `NoteTextOrder.Find`/`Joins` already
  enumerate every whole-line overlay candidate with backtracking until the join hashes to the `path`
  stem, `NoteTextOrder.cs:44-56`, `:128-166`, `:169-193`, `:186-191`). Factor `Joins` to also return
  the verified merged text. Acceptance is `sha256(merged)` == the `path` stem (F8); a group that fails
  it is handed to `NoteTextOrder`-style backtracking first, and counts *unprovable* only after that
  search fails. Pure, no I/O (clean layering).
- **P1b, the pass (Infrastructure).**
  `src/AiRaccoon.Infrastructure/Ingestion/ChunkBudgetReconciler.cs` (new) plus SQL selections in
  `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs` (group listing by `MemorySql.DeleteMatchPredicate`'s
  own group definition, `MemorySql.cs:186-191`). Note groups: P1a reassembly, then re-chunk at the
  current budget with the note chunker and the standard overlay rule (`FileIngestor.cs:387-388`),
  skip when the produced hash set equals the stored one, else replace. Mirror groups: delegate to
  `ReingestRepair.RunAsync(connection, store, apply: true)` (`ReingestRepair.cs:44-101`), which
  already re-ingests from disk. Replacement is **one transaction per group**: tombstone old hashes
  (`MemorySql.TombstoneFromPredicate`, `MemorySql.cs:213-231`, same surviving-hash exemption as
  `ChunkBackfill.cs:84-88`), delete, insert new rows `pending`, then the group-scoped column
  maintenance below. Note this closes a real gap in `ChunkBackfill.RunAsync`, whose delete commits
  before its inserts run (`ChunkBackfill.cs:75-107`); for note content, whose only copy is the bank,
  that gap is data loss and must not be copied.
  **Insert spec (review F5):** replacement rows carry `path`, `source_file`, `section`, the
  scope/context/workspace keys, `source_id`, `agent_id` and `created_at` forward from the group's
  rows (`WriteChunks.cs:36-53` is the template for what a note row holds) — `ChunkBackfill`'s
  `agentId = null, createdAt = now` (`ChunkBackfill.cs:99-103`) is explicitly NOT copied; rating,
  access_count and last_accessed_at are the accepted losses. **Insert order = text order**
  (ADR-0122's write convention), which is what keeps `NoteTextOrder`'s id-order candidate working.
  **Column maintenance (review F1 mech 1-2, F8):** plain notes (`source_file IS NULL`) copy
  `WriteChunks`' `-1`/`0` sentinel — nothing recomputes them (`MemorySql.cs:823` excludes them) and
  `NoteTextOrder.Find` recovers their order from id order (= text order by the insert rule above).
  Citing notes run `NoteChunkOrderRepair.RunAsync(connection, [path])` after the per-group replace
  (`NoteChunkOrderRepair.cs:33-42`, `:47-58` — the keeping-order renumber + proven text-order
  reposition), NOT `RecomputeChunkColumnsBankWide`, whose `ROW_NUMBER() ... ORDER BY id` fill over a
  mixed `(ctx, source_file)` partition duplicates kept file positions (the ADR-0123
  "gap or duplicate" defect). At most ONE `RecomputeChunkColumnsBankWideKeepingOrder`
  (`MemorySql.cs:834-881`, writes only changed rows) runs at the end of the phase, never a
  full-bank recompute inside per-group transactions (review F8: that shape is O(groups × bank) and
  would dominate the outage window).
- **P1c, trigger and ordering.** `src/AiRaccoon.Infrastructure/Embedding/EntryEmbedder.cs`: accept
  the reconciler beside `IVecDimensionReconciler` and call it inside `DrainMigrationAsync` in the
  slot after `ReconcileVecDimensionsAsync` (`EntryEmbedder.cs:185`), before the embed loop
  (`:190-207`). The slot precedes every migration close (`FinishModelMigration`'s only caller is
  `EntryEmbedder.cs:210`; `ModelResetAsync` refuses mid-migration, `SettingsEndpoint.cs:70-75`) —
  review F4 verified this for every closing path. **Drift trigger (review F4):** `StartMigrationAsync`
  short-circuits to a settings-only write whenever the fingerprint matches (`EntryEmbedder.cs:74-85`),
  which is exactly the drift case — so specify a **force-open** path (a `force: true` parameter or a
  dedicated `StartBudgetDriftMigration`) that runs the outbox transaction at `EntryEmbedder.cs:88-121`
  **without** `MarkAllEmbeddedPending` (a drift re-chunks; it does not invalidate vectors that are
  already in the engine's space). Rows the phase replaces arrive `pending` and are embedded by the
  same drain. The drift check (`ReconcileFingerprintAsync`, `EntryEmbedder.cs:49-67`, or beside it)
  compares a new `embedding.chunkBudget` settings row (`EmbeddingSettingsKeys` or its current home)
  against `ResolveChunkBudgetFor`: **stale value → force-open; absent on a non-empty bank → force-open
  (re-chunk-only: the P1-standalone build must NOT trigger the full-re-embed outage F4's sub-point
  names — this shape also fixes that); absent on an empty bank → stamp silently.** On the real 1.54.0
  upgrade the fingerprint itself changes, the normal full-re-embed path runs (owner decision 2), and
  the phase re-chunks inside it. **Stamp gate (review F1 mech 4, F6):** `embedding.chunkBudget` is
  written only when the phase completes with **zero retryable skips**. Retryable skips (note groups
  unprovable after backtracking) block the stamp and are retried via the re-chunk-only drift path
  **once per server start** until they converge. Terminal skips (mirror rows whose source file is
  gone) are counted in the phase report, named in ADR-0125 and breaking-changes as not re-chunked
  (still re-embedded at their stored bounds), and do not gate the stamp.

**Files owned:** the three above plus
`src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs` (additive), tests in §3 (3-7, 19). Ctor/registration
ripples (review F7): `src/AiRaccoon/Setup/AppRegistrations.cs:406` (register the reconciler beside
`IVecDimensionReconciler`), `tests/AiRaccoon.Tests/TestData.cs` (`CreateEntryEmbedder` factory),
`tests/AiRaccoon.Tests/Integration/Embedding/EntryEmbedderMigrationDrainReportingTests.cs:137` and
`tests/AiRaccoon.Tests/Integration/Embedding/QueryTruncationTests.cs:69` (positional `EntryEmbedder`
construction — lane A absorbs this ctor churn before P3's edits to the same file).

**Acceptance criteria**

1. A note whose chunks were written at budget 254 is stored after the phase as re-chunked pieces at
   the resolved budget, and the merged content is byte-identical to the original (proven by the
   `sha256`-equals-path-stem check after `NoteTextOrder` backtracking; a group still unprovable is
   left untouched and counted *retryable-skipped* in the phase report). *Gate:*
   `dotnet test --filter "FullyQualifiedName~ChunkReassembly"` and
   `--filter "FullyQualifiedName~ChunkRebudget"`.
2. A mirror group (file rows) is re-chunked from its file at the resolved budget; files that no
   longer exist are untouched and counted *terminal-skipped*. Covered by its own test row (§3 row 19:
   file rows at 254 → 1022 pieces from disk, deleted file untouched and counted — review F9 found the
   original gate vacuous with no mirror-group test). *Gate:* `--filter "FullyQualifiedName~ChunkRebudget"`.
3. Replacement is atomic per group: a failure injected between delete and insert leaves the old rows
   present (no silent loss of note content). *Gate:* `--filter "FullyQualifiedName~ChunkRebudget"`.
4. The phase is idempotent: running it twice on the same bank changes nothing the second time
   (hash-set equality skip). *Gate:* same filter.
5. Ordering: inside one migration run, every vector written was computed from post-re-chunk row
   values, and the migration does not close while any row is pending. *Gate:*
   `--filter "FullyQualifiedName~ModelMigrationRechunksAndReembeds|FullyQualifiedName~ChunkBudgetDriftOpensAMigration"`.
6. Budget drift (stale or absent `embedding.chunkBudget`) opens a **re-chunk-only** migration even
   when the engine fingerprint matches (no `MarkAllEmbeddedPending`; unchanged rows keep their
   vectors); a zero-row bank is stamped without one. *Gate:*
   `--filter "FullyQualifiedName~ChunkBudgetDriftOpensAMigration"`.
7. Stamp gate (review F1 mech 4): `embedding.chunkBudget` is written only when the phase report shows
   zero retryable skips; with retryable skips the stamp is withheld and a later run (once per server
   start) retries and then stamps; terminal skips do not block the stamp but are counted and logged.
   *Gate:* `--filter "FullyQualifiedName~ChunkRebudget|FullyQualifiedName~ChunkBudgetDriftOpensAMigration"`.

**Reported, not solved (accepted cost):** per-row metadata (rating, access_count, last_accessed_at)
is discarded when a group re-chunks, the same loss `ReingestRepair.cs:22` documents — and nothing
else is lost: `section`, `source_id`, `agent_id`, `created_at` are carried forward per the insert
spec above (review F5 caught `ChunkBackfill`'s wider loss; it is explicitly not copied). The ADR's
accepted-cost list matches this exactly. [INFERRED] Alternative of carrying the first row's
metadata forward was considered and left out: there is no 1:1 row mapping after boundaries move,
and inventing one is policy, not mechanics. Also named (review F6): mirror rows whose source file
is gone are *terminal-skipped* — not re-chunked, still re-embedded at their stored bounds — and the
ADR + breaking-changes entries say so instead of promising "every bank re-chunks" unconditionally.

### P2 — Precondition and padding pinning (tests only)

**Files owned:** `tests/AiRaccoon.Tests/Unit/Embedding/CoreMlWindowBudgetTests.cs`,
`tests/AiRaccoon.Tests/Unit/Embedding/LengthBucketsTests.cs` (additive),
`tests/AiRaccoon.Tests/Integration/Embedding/BundledEngineMlxSessionTests.cs`,
`tests/AiRaccoon.Tests/Integration/Embedding/BundledManifestBucketAlignmentTests.cs` (new).

**Acceptance criteria**

1. A full chunk of the **real bundled manifest's** budget pads to exactly its own length on the MLX
   rule (no waste) and fits the CoreML window: `PaddedLength(chunkTokens + 2) == chunkTokens + 2`
   and `chunkTokens + 2 ≤ 1024`, derived from `ai-raccoon.manifest.json`, never a literal. *Gate:*
   `dotnet test --filter "FullyQualifiedName~BundledManifestBucketAlignment|FullyQualifiedName~CoreMlWindowBudgetTests|FullyQualifiedName~LengthBucketsTests"`.
2. A real MLX session (plugin present) pads rows — pinned with a **short row**, because config D's
   zero-waste alignment (1022 + 2 = 1024) defeats a full-budget assertion (review F2): a 3-token row
   reports `LastSequenceLength == 64` on MLX while the CPU session reports its own length (the live
   shape `BundledEngineMlxSessionTests.cs:47` already uses), and it matches the CPU vector at
   cosine ≥ 0.9999. RED mutation: remove `_bucketRows = true` at `OnnxEmbeddingGenerator.cs:611` →
   the row runs unpadded at 3 and the assertion reddens. The skip must be **fail-closed somewhere
   named** (review F2 defect 1): an env flag (e.g. `AIRACCOON_REQUIRE_MLX=1`) turns `Assert.Skip` into
   `Assert.Fail` when the plugin is absent, set on the named MLX run: the `build-mlx` job in
   `.github/workflows/build.yml` (osx-arm64, `AIRACCOON_REQUIRE_MLX: 1` on the session-test step,
   every push to main and manual dispatch — the same trust-then-promote shape as `build-arm64`).
   The pre-release manual checklist (`ai-raccoon-manual-checklist`) keeps a named row as the manual
   backstop. *Gate:* same filter, plus the flag-bearing `build-mlx` run.
3. The CPU path stays unpadded (regression pin against someone "fixing" padding onto CPU).
   *Gate:* `--filter "FullyQualifiedName~BundledEngineBucketPaddingTests"` (existing test, kept).

**RED proof for gate 1-2 (prove-the-check-fails):** set a scratch manifest `chunkTokens` to 1000
and watch criterion 1 go red; comment out `_bucketRows = true` at `OnnxEmbeddingGenerator.cs:611`
and watch criterion 2's SHORT-row assertion go red on the MLX host (a full-budget row would NOT go
red — `PaddedLength(1024) == 1024` either way; review F2 defect 2).

### P3 — Config D value change: `chunkTokens: 1022`, resolution and query tests

**Merge order: after P1.** (A build that consumes `chunkTokens: 1022` before P1 would migrate banks
without re-chunking, and P1c's drift trigger is what heals such a bank later. No release ships
between the merges.)

**Files owned:** `src/AiRaccoon/Models/granite-embedding-small-english-r2/ai-raccoon.manifest.json`
(line 75), `tests/AiRaccoon.Tests/Integration/Embedding/EmbeddingServiceManifestBudgetTests.cs`,
`tests/AiRaccoon.Tests/Integration/Embedding/QueryTruncationTests.cs`,
`tests/AiRaccoon.Tests/Integration/Embedding/QueryTrimFollowsChunkTokensTests.cs` (new),
`tests/AiRaccoon.Tests/Unit/Memory/QueryGuard/QueryLengthGuardTests.cs` (additive),
`tests/AiRaccoon.Tests/Integration/ChunkingCorpusGuaranteeTests.cs`,
`tests/AiRaccoon.Tests/Integration/Memory/WriteChunksToBudgetTests.cs`,
`tests/AiRaccoon.Tests/Unit/Chunking/CodeChunkerTests.cs`,
`src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs` (doc comment only, `:203-209`),
`src/AiRaccoon.Core/Memory/QueryGuard/QueryLengthGuard.cs` (doc comment only).

**Acceptance criteria**

1. `ResolveChunkBudgetFor` for the bundled engine returns 1022; the bundled manifest literally
   declares `chunkTokens: 1022`; a manifest with an explicit `chunkTokens` resolves to it (not to
   the 510 cap). *Gate:* `dotnet test --filter "FullyQualifiedName~EmbeddingServiceManifestBudgetTests"`.
2. Code stays 510: `CodeChunker.DefaultBudget` is 510 and is not the memory budget.
   *Gate:* `--filter "FullyQualifiedName~CodeChunkerTests|FullyQualifiedName~CodeManifestBudgetGuardTests"`.
3. The query-trim budget follows `chunkTokens`: for a fixture manifest with `chunkTokens: N` the trim
   keeps `(N−20, N]` tokens and the 426 message quotes `N`; for the bundled engine `N == 1022`; the
   guard warns above ~4,024 chars and quotes 1022. *Gate:*
   `--filter "FullyQualifiedName~QueryTrimFollowsChunkTokensTests|FullyQualifiedName~QueryTruncationTests|FullyQualifiedName~QueryLengthGuardTests"`.
4. The chunker guarantee holds at the new budget: no docs-corpus or hostile-fixture chunk exceeds
   1022 content tokens (1,024 with specials) at the resolved budget, and the 254/256 legacy case
   still passes. *Gate:* `--filter "FullyQualifiedName~ChunkingCorpusGuaranteeTests"`.
5. The write path chunks at the resolved budget (the `BertWindow = 256` constant now derives),
   including `SqliteMemoryStoreChunkingTests` (review F3 item 1: its `≤ 256` literal at
   `tests/AiRaccoon.Tests/Integration/Storage/SqliteMemoryStoreChunkingTests.cs:65-68` matched no
   gate — and could not go red at the flip, since the test configures `openai`, whose resolved
   budget is 256 on both sides; §3 row 21 carries the corrected premise). *Gate:* `--filter "FullyQualifiedName~WriteChunksToBudgetTests|FullyQualifiedName~SqliteMemoryStoreChunkingTests"`.

### P4 — Docs, ADR-0125, version and changelog

**Files owned:** `docs/adr/0125-config-d-memory-chunks-1022-code-stays-510.md` (new),
`docs/adr/0108-one-bundled-engine-granite-small-fp16-on-the-gpu.md` (pointer amendment),
`docs/adr/README.md` (index row), `docs/explanation/architecture.md` (lines 345-350, 877),
`docs/reference/breaking-changes.md` (new 1.54.0 entry),
`scripts/retrieval_tuning/llamaindex_harness/ingest.py` (comment, line 66),
`scripts/src/retrieval_tuning/eval-sets/code-smoke.json` (CS-04 wording),
`tests/AiRaccoon.Tests/Unit/Mcp/MemoryToolsTests.cs:631` and
`tests/AiRaccoon.Tests/Integration/Ingestion/RepairFamilyRoutesThroughTheEngineResolverTests.cs:110`
(comment/wording only), `VERSION`, `docs/changelog/1.54.0-config-d-chunk-budgets.md` (new). Full
content lists in §5 and §6.

**Acceptance criteria**

1. ADR-0125 exists at `docs/adr/0125-config-d-memory-chunks-1022-code-stays-510.md` in the Nygard
   shape (Context / Decision / Consequences / Alternatives considered), cites both measurement
   records and the query-budget research record by path, records the five owner decisions in
   substance, states the query-trim coupling explicitly, and names the post-merge 254-vs-1022
   measurement as planned follow-up work (not part of this change). *Gate:* reviewer sign-off against
   this criterion (the `LoggerMessageEventId` filter is decorative for a prose criterion and is
   dropped — review F9; event ids are separately pinned by existing tests and must not move).
2. Every "moves" row of the §1.4 inventory is edited; every "stays"/"historical" row is untouched.
   *Gate:* `git grep -n 254 src/ tests/ docs/ scripts/` minus a named noise allowlist (vocab/tokenizer
   data, `P-256`, `AES-256`, `EventId = 510`, `MaximumLength(256)`, fixture windows) shows
   no unqualified current-behavior 254 — explicitly including `src/AiRaccoon/Tools/MemoryTools.cs`
   and `docs/how-to/` (review F3: the old two-file grep missed both) — and a reviewer walks the table.
3. `VERSION` reads 1.54.0 and `docs/changelog/1.54.0-config-d-chunk-budgets.md` describes the
   behavior change, the migration and its outage window. *Gate:* file existence + content review.

### P5 — Integration package (last): cross-package tests and the release train

**Files owned:** `tests/AiRaccoon.Tests/Integration/Embedding/ConfigDEndToEndMigrationTests.cs`
(new), §3 rows 18 and 21 reading across packages (row 21 lands in P3, is exercised here; the review
F7 dangling "items 14-15" reference is corrected to exactly these rows), and the release commit
(VERSION + changelog if P4 lands them on a branch that needs squashing).

**Acceptance criteria (each cross-package by construction)**

1. End to end on the real bundled manifest: a bank built under `chunkTokens: 254` (fixture manifest),
   upgraded to the 1022 manifest, runs one maintenance pass and comes out (a) re-chunked: note
   groups merged and file groups re-chunked at 1022, row count for split content dropped; (b)
   re-embedded: every row `embedded` and its stored vector equals a fresh embed of its current
   value; (c) query budget 1022 (426 message quotes 1022 on a 311-token... on a >1022-token query,
   not before); (d) migration closed and `embedding.chunkBudget == 1022`; (e) code rows re-embedded
   but code chunk boundaries unchanged (510). *Gate:*
   `dotnet test --filter "FullyQualifiedName~ConfigDEndToEndMigrationTests"`.
2. The padding precondition is exercised by P2's named fail-closed MLX run — the short-row pin is
   `BundledEngineMlxSessionTests.PreferMlx_FilesPresent_AShortRow_PadsToOneBucket_WithTheCpuSessionsVectors`,
   not the end-to-end scenario (that one runs a fake generator, which has no padding property to
   assert); on non-MLX hosts `AIRACCOON_REQUIRE_MLX=1`'s
   absence means 'not exercised', which is why P2's named run is the binding gate (review F2 defect 3:
   a full-size row on the MLX seam is a tautology and is dropped). *Gate:* P2's named run.
3. The suites of P1, P2 and P3 pass together on one tree (no cross-package interference), once.
   *Gate:* `dotnet test tests/AiRaccoon.Tests --filter "FullyQualifiedName~ChunkRebudget|FullyQualifiedName~ModelMigration|FullyQualifiedName~EmbeddingServiceManifestBudgetTests|FullyQualifiedName~QueryTrim|FullyQualifiedName~BundledManifestBucketAlignment|FullyQualifiedName~CoreMlWindowBudgetTests"`.
4. The full suite runs in CI on push (test-run-economy: local runs touched suites once).

---

## 3. Test list, designed before implementation

Each row is named, states its assertion and the mutation that makes it red (design-tests
discipline). Mandatory items (a)-(d) are marked.

| # | file (new unless noted) | assertion | red when | pkg |
|---|---|---|---|---|
| 1 **(a)** | `EmbeddingServiceManifestBudgetTests.cs` (update, `:77`) | bundled `ResolveChunkBudgetFor` == 1022 | manifest `chunkTokens` reverted to 254 | P3 |
| 2 **(a)** | `EmbeddingServiceManifestBudgetTests.cs` (new case) | the real bundled manifest JSON declares `chunkTokens: 1022` and the validator accepts it | value edited to anything else, or validator rejects 1022 | P3 |
| 3 **(a)** | `EmbeddingServiceManifestBudgetTests.cs` (new case) | fixture manifest `chunkTokens: 700` resolves to 700, not the 510 cap | `ManifestContentBudget` ignores `ChunkTokens` | P3 |
| 4 **(b)** | `Unit/Chunking/ChunkReassemblyTests.cs` | `NoteTextOrder`-backtracked merge of chunks-with-overlay equals the original text (including repeated-boundary notes the greedy match would eat — review F1 mech 3); `sha256` check accepts exact and rejects tampered; a group unprovable even after backtracking fails verification instead of corrupting | backtracking replaced by greedy longest-match; hash check removed | P1 |
| 5 **(b)** | `Integration/Storage/ChunkRebudgetTests.cs` | 254-written note group becomes 1022-budget pieces with identical merged content; single-chunk groups untouched; failing-verification groups untouched and reported; delete+insert atomic under injected failure (old rows survive); second run is a no-op | phase skips merge; phase writes without transaction; phase touches unverifiable groups | P1 |
| 6 **(b)** | `Integration/Embedding/ModelMigrationRechunksAndReembedsTests.cs` | one migration run on a 254 bank leaves re-chunked rows whose stored vectors equal fresh embeds of the **new** values, all rows `embedded`, migration closed | phase removed from the drain (rows stay at 254); phase moved after the embed loop (pending rows remain / vector-value mismatches) | P1 |
| 7 **(b)** | `Integration/Embedding/ChunkBudgetDriftOpensAMigrationTests.cs` | stale or absent `embedding.chunkBudget` on a non-empty bank opens a **re-chunk-only** migration despite an equal fingerprint (no all-pending re-embed; unchanged rows keep vectors); zero-row bank is stamped without migrating | drift check removed from `ReconcileFingerprintAsync`; drift routed through `StartMigrationAsync`'s equal-fingerprint short-circuit (review F4) | P1 |
| 8 **(c)** | `Integration/Embedding/BundledManifestBucketAlignmentTests.cs` | `PaddedLength(manifest.chunkTokens + 2) == manifest.chunkTokens + 2` and `≤ 1024`, derived from the real manifest | `chunkTokens` set to a non-2-below-multiple-of-64 value (e.g. 1000) | P2 |
| 9 **(c)** | `Unit/Embedding/CoreMlWindowBudgetTests.cs` (update) | shipped-budget set includes the manifest budget and each + 2 fits `CoreMlWindow` | `chunkTokens` raised past 1022 | P2 |
| 10 **(c)** | `Integration/Embedding/BundledEngineMlxSessionTests.cs` (new case) | on a real MLX session a **short** row reports `LastSequenceLength == 64` and cosine ≥ 0.9999 vs CPU (full-budget row defeats the mutation — review F2) | `_bucketRows = true` removed from `OnnxEmbeddingGenerator.cs:611` (the unpinned regression today) | P2 |
| 10a **(c)** | same file, skip policy (review F2) | with `AIRACCOON_REQUIRE_MLX=1` set and the plugin absent the test FAILS, not skips | flag ignored / skip stays silent | P2 |
| 11 **(c)** | `Unit/Embedding/LengthBucketsTests.cs` (keep `:34-42`) | 1022 + 2 pads to itself | `PaddedLength` math broken | P2 |
| 12 **(d)** | `Integration/Embedding/QueryTrimFollowsChunkTokensTests.cs` | fixture `chunkTokens: N` → trim keeps `(N−20, N]`, message quotes `N`; bundled → 1022 | trim hardcodes `MaxContentTokens` or `MaxManifestChunkTokens` | P3 |
| 13 **(d)** | `QueryTruncationTests.cs` (update `:75`) | 426 message contains "1022" for the bundled engine | message quotes a stale constant | P3 |
| 14 **(d)** | `Unit/Memory/QueryGuard/QueryLengthGuardTests.cs` (additive) | budget 1022 → threshold ≈ 4,024 chars, guidance quotes 1022 | threshold pinned to 1,000 chars | P3 |
| 15 | `Integration/ChunkingCorpusGuaranteeTests.cs` (update `:21`, `:44`) | corpus + hostile fixtures under ceiling `budget + 2` for both the legacy 254 and the resolved 1022 | chunker emits an over-budget chunk at 1022 (break `BuildChunk` verification to watch it red) | P3 |
| 16 | `Integration/Memory/WriteChunksToBudgetTests.cs` (update `:29`) | write chunks fit resolved budget + reservation, and at least one chunk passes the legacy 254 window (lower bound — the window is self-derived, so the ceiling alone cannot tell 1022 from 254) | constant left at 256 after the flip | P3 |
| 17 | `Unit/Chunking/CodeChunkerTests.cs` (update `:87-92`) | `CodeChunker.DefaultBudget` is 510 and `ShouldNotBe(1022)` | code budget dragged along with memory | P3 |
| 18 | `Integration/Embedding/ConfigDEndToEndMigrationTests.cs` | P5 criteria 1-2 in one scenario | any P1/P3 regression crossing packages | P5 |
| 19 **(b)** | `Integration/Storage/ChunkRebudgetTests.cs` (mirror-group case; review F9 — the original gate was vacuous with no mirror-group test) | file rows at 254 → 1022 pieces re-chunked from disk; a deleted source file is untouched and counted terminal-skipped | phase touches vanished-file groups; phase skips mirror groups silently | P1 |
| 20 **(b)** | `Integration/Storage/ChunkRebudgetTests.cs` (stamp-gate case; review F1 mech 4) | `embedding.chunkBudget` written only at zero retryable skips; retryable skips withhold the stamp and a later run stamps after convergence; terminal skips never block the stamp | stamp written unconditionally at phase end | P1 |
| 21 | `Integration/Storage/SqliteMemoryStoreChunkingTests.cs` (update `:65-68`; review F3 item 1) | ceiling derives from resolved budget + reservation instead of the literal 256 | the store chunks over the resolved budget (premise corrected: the test configures `openai`, whose resolved budget is 256 on both sides of the flip, so the old literal could not go red at the flip — the guard is the derived ceiling moving with the store, and the resolver itself is pinned by rows 1-3) | P3 |

Existing tests kept as regression pins without edits:
`BundledEngineBucketPaddingTests.cs` (CPU unpadded, bucketed parity),
`ChunkBudgetIsEngineAwareTests.cs:75-77` (derived window), `EmbeddingServiceManifestBudgetTests.cs:190-224`
(fingerprint over manifest content), `WordPieceEmbeddingTokenizerTests.cs:79-91` (legacy path).

---

## 4. Parallelism

| lane | packages | shared files | verdict |
|---|---|---|---|
| A | P1 (re-chunk mechanism) | `MemorySql.cs`, `EntryEmbedder.cs`, `ModelMigrationJob.cs`, Core/Ingestion | runs **in parallel** with B and C |
| B | P2 (padding pins) | none with A/C (test-only: `Unit/Embedding`, `Integration/Embedding/BundledEngine*`) | parallel |
| C | P3 (value change + resolution/query tests) | none with A/B on disk, but **merge-order serialised after A** (see P3) | develop in parallel, merge after A |
| D | P4 (docs/ADR/version) | none | parallel with A-C; ADR-0125 text quotes P1's final shape, so it is reviewed last but drafted early |
| E | P5 (integration) | touches tests from all lanes | serial, last |

Shared-file sections to serialise inside lane A: `MemorySql.cs` (P1b selections) and
`EntryEmbedder.cs` (P1c) are edited by one sub-author in sequence, P1b first.
`EmbeddingServiceManifestBudgetTests.cs` is edited only in P3 (plus P5 reading it).
`VERSION` and the changelog are P4/P5's alone. Ctor/registration ripples (review F7): lane A also
edits `AppRegistrations.cs:406`, `TestData.cs`, `EntryEmbedderMigrationDrainReportingTests.cs:137`
and `QueryTruncationTests.cs:69`; lane C touches `QueryTruncationTests.cs` only after lane A's ctor
churn lands (P3 merges after P1, so the ordering is automatic).

---

## 5. Docs/ADR work

**ADR-0125** at `docs/adr/0125-config-d-memory-chunks-1022-code-stays-510.md`, Nygard shape:

- *Context.* The 2026-09-24 CPU record (`docs/work/2026-09-24-chunk-size-vs-attention-window.md`,
  F1-F8: memory span MRR +0.166 at 1022, code file MRR −0.034 at 1022, cost bends at 512) and its
  MLX re-run (`docs/work/2026-09-25-chunk-size-vs-attention-window-mlx.md`, F1-F6 plus the config D
  table and the padding precondition) identify config D as the best trade. The query-budget record
  (`docs/work/2026-09-28-why-the-query-budget-stays-254.md`, F1-F9) shows 254 was never a query
  decision but `chunkTokens` seen through the coupling. ADR-0108 Decision 5 pinned 254 for
  eval-comparability reasons that this measurement set now supersedes.
- *Decision.* In substance, verbatim to the owner's five settled points: (1) the bundled manifest's
  `chunkTokens` becomes 1022, the code corpus stays at 510 via `MaxManifestChunkTokens`; (2) existing
  banks migrate by re-chunking to the new budget and re-embedding, both; (3) the query-trim budget
  follows `chunkTokens` to 1022 **by design, the coupling is deliberate and ratified**; (4) no
  pre-adoption retrieval measurement is run (queries under the old ~255-token budget are processed
  unchanged; longer ones were trimmed before, so more context is strictly better); (5) config D
  ships only with bucket padding in place on the MLX path (ADR-0114), and tests pin that
  precondition.
- *Consequences.* First start after upgrading re-chunks and re-embeds the bank with tool calls
  refused, minutes on a large bank (the 1.47.0 shape, `docs/reference/breaking-changes.md:5`);
  queries trim at 1022 and the guard threshold widens to ~4,024 chars; the code corpus re-embeds to
  identical vectors as accepted churn (one engine, one re-embed); a top-5 at 1022 hands the agent
  about 3,700 tokens per search (2026-09-24 record F4); re-chunking discards per-row metadata;
  MLX footprint stays near the padded curve (3.7 GB at 1022); the WebGPU per-shape footprint
  question remains unverified and rides with the follow-up.
- *Follow-up (named, out of scope here).* A post-merge 254-vs-1022 retrieval measurement on the live
  bank, as a separate task, including the WebGPU footprint check the MLX record left open and the
  re-chunk phase's own wall-clock (review F8: the phase's scan/reassembly/recompute cost is not in
  the embed-token estimate and must be measured, not inferred).
- *Alternatives considered.* B (both at 510: moves nothing measurable, 2026-09-24 F4), C (both at
  1022: loses code file MRR, interval excludes zero), keeping 254 (rejects the measured +0.166),
  and widening only the query budget (untested, 2026-09-28 record "Still open" bullet 2).

**ADR-0108: pointer amendment, recommended over leaving Consequences historical.** The file's own
convention already amends in place (the "> Amended 2026-09-25 by ADR-0112." blockquote inside
Consequences and the "## Amendment (2026-09-24)" section). Decision 5's "`chunkTokens: 254` keeps
memory chunks the size every measurement in this ADR used" and the Consequences bullet "The bundled
engine's memory chunk budget stays 254 tokens" read as current policy to anyone skimming; silence
would leave a verifiably false current-behavior claim in an accepted ADR. Add the same shape of
blockquote to both spots: "Superseded in part by ADR-0125 (config D, 1.54.0): `chunkTokens` is 1022
from 1.54.0." Nothing else in 0108 moves. ADR-0071, ADR-0036, ADR-0114 and the three research
records stay historical and are cited from ADR-0125.

**Every doc line that moves** (cross-reference to the §1.4 table):

1. `docs/explanation/architecture.md:345-350` (row 27): rewrite the chunk-bounds paragraph to
   "1022 content tokens via the bundled manifest's `chunkTokens` (ADR-0125)", drop the stale
   "256-token window minus 2" justification (MiniLM's window, not granite's), keep "8191 for
   OpenAI-compatible models" and note the 48-token overlay is unchanged.
2. `docs/explanation/architecture.md:877` (row 28): "never the memory chunker's 254" becomes "never
   the memory chunker's budget (1022 since ADR-0125)".
3. `docs/reference/breaking-changes.md` (row 29): new entry at the top:
   "1.54.0: memory chunks and query trimming move to 1022 tokens... On first start after upgrading
   every bank re-chunks and re-embeds once on its own (the bank refuses tool calls until that
   finishes, minutes on a large bank); per-row ratings and access counts reset on rows whose chunk
   boundaries move. [ADR-0125]".
4. `docs/adr/0108-...-on-the-gpu.md` (row 30): two pointer blockquotes per §5 above.
5. `docs/adr/README.md`: index row for 0125.
6. `scripts/retrieval_tuning/llamaindex_harness/ingest.py:66` (row 35): comment "(254 for granite)" →
   "(the manifest's `chunkTokens`, 1022 since 1.54.0)".
7. `scripts/src/retrieval_tuning/eval-sets/code-smoke.json:56-57` (row 36): CS-04 answerSpan
   "warns when a query is long enough that the bundled model's embedding window will likely trim it"
   → "warns when a query is long enough that the active engine's chunk budget will likely trim it".
   Correction to the brief: CS-04 carries no 254/510/256 figure; only this wording ages.
8. Comment-only cleanups (rows 22-23, 19): `MemoryToolsTests.cs:631`,
   `RepairFamilyRoutesThroughTheEngineResolverTests.cs:110`, probe comments.
9. Review F3 minors (row 42): `MemoryTools.cs:320-323` ("the bundled model's fixed 254" —
   de-number), `IEmbeddingService.cs:25-26` (interface contract doc), `EmbeddingService.cs:194-195`
   ("bundled-default" phrasing), `CodeChunker.cs:22` + `CodeChunkerTests.cs:85` ("not the memory
   chunker's 254" wording), `ingest.py:67`, `ChunkingCorpusGuaranteeTests.cs:43,51,104-105`.
10. Review F3 items 2-3 (rows 40-41): `src/AiRaccoon/Tools/MemoryTools.cs:139-145` (the MCP `search`
    tool description — agent-facing) and `docs/how-to/configure-embedding-engines.md:42`.

Explicitly **not** edited: `docs/work/2026-09-28-why-the-query-budget-stays-254.md` (its third
"Still open" bullet is answered by ADR-0125; dated research records stay as written),
`docs/adr/0071`, `0036`, `0114`, `docs/reference/logging-event-ids.md` (no event moves).

---

## 6. Version

`VERSION` (repo root) reads **1.53.13** at plan time. This task ships as **1.54.0** (assigned at
dispatch; behavior change plus a bank migration). Work in P4/P5: set `VERSION` to `1.54.0`, add
`docs/changelog/1.54.0-config-d-chunk-budgets.md` covering the budget change, the query-trim
follow-on, the migration (re-chunk and re-embed, outage window, metadata reset) and the pointer to
ADR-0125. `docs/changelog/README.md` needs no update (no new changelog convention).

---

## Alternatives rejected (reported as asked)

1. **Re-chunk via `repair reingest --apply` alone (manual).** Already works for file rows (F6) but
   never reaches notes, is opt-in, and cannot satisfy "existing banks MUST migrate". Kept as a
   building block (P1b delegates to it for mirror groups).
2. **Widening `ChunkBackfill` into a merge pass.** It is row-at-a-time and splits-only
   (`ChunkBackfill.cs:57`); group reassembly is a different operation, and its delete-then-insert
   sequence is unsafe for note content (P1b's atomicity criterion fixes the shape instead of
   inheriting it).
3. **Re-chunk as its own on-demand job, separate from the migration.** A crash between the jobs
   could close the migration (re-embed done) with re-chunk still owed, violating "both"; repair
   verbs in this codebase also never run on a clock (`ReingestRepair.cs:26-33`). Folding the phase
   into `DrainMigrationAsync` beside `ReconcileVecDimensionsAsync` gives ordering and lease
   serialization for free.
4. **Skipping the `embedding.chunkBudget` drift row (the truly smallest trigger: fingerprint only).**
   Rejected because a bank that consumes the new manifest under an interim build (manifest flipped,
   re-chunk code absent) migrates once and would then never re-chunk: the fingerprint already
   matches. The one settings row turns that hole into a self-healing path and names the trigger.
5. **Padding WebGPU rows to bound any per-shape growth there.** A product change, and the brief caps
   precondition work at tests. The gap is unverifiable by unit test (it is a process-footprint
   property), so it is named as residual risk and folded into the follow-up measurement.
6. **Re-embedding only re-chunked rows (skipping unchanged rows' re-embed).** Owner decision 2 says
   both, and the fingerprint mechanism already marks everything pending (`EntryEmbedder.cs:106`).
   Skipping would fight the mechanism for no measured gain.
