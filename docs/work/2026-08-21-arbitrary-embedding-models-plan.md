# Plan — support for arbitrary embedding models (MoE, combined, rev 2)

**Date:** 2026-08-21
**Task:** support-for-other-embedding-models → **implementation task:** embedding-model-support. This document is the **plan of record** for the implementation (WP1-WP6 in progress on `task/embedding-model-support-u1`, PR #402).
**Case study:** BAAI/bge-m3
**Status:** combined from three MoE lanes (architecture / engineer / ops), **reviewed by two MoE
reviewers (code-reviewer + architect)** — review round 1 findings folded into rev 2. The
reviewers' verdicts: REQUEST-CHANGES (code-reviewer) / APPROVE-WITH-CHANGES (architect); every
finding is decision-level, no redesign required.
**G0 — owner review (2026-08-21): APPROVED.** Owner decisions: (1) rev 2 approved; (2) **no
default model changes** — WP5's eval report never changes the shipped default in this task;
(3) the `model download` verb ships in the same PR.

Section sources (read for depth):
- `docs/work/2026-08-21-embedding-moe-architecture.md` · `-engineer.md` · `-ops.md`
- Prior research: `docs/work/2026-08-21-embedding-model-replacement.md`

## 0. The questions this plan answers

1. **What do we need to do to download a model locally and run it with our memory?**
   A new `ai-raccoon model download <repo-id>` verb (SHA-256 pinned from HF LFS oids, verified
   download, manifest written) + the engine generalization (manifest-driven tokenizer, pooling,
   dims) + one dimension migration. For bge-m3: 2.27 GB (graph 725 KB + external-data 2.11 GiB),
   sentencepiece tokenizer (`sentencepiece.bpe.model`, no vocab.txt), pooling per WP5 parity
   (graph `sentence_embedding` output preferred), 1024 dims → vec0 rebuild + full re-embed on a
   bank copy.
2. **What about LM Studio — can we add a URL to a model and handle it?**
   **Already structurally supported today**: `ai-raccoon model set openai <model> --base-url
   <url>` speaks any OpenAI-compatible `/v1/embeddings` endpoint; LM Studio serves exactly that
   on localhost:1234 and its URL-add flow accepts HF repo URLs (UI search bar /
   `POST /api/v1/models/download`; direct-file URLs unverified). Only 1024-dim output blocks
   bge-m3 via LM Studio (WP4). A 384-dim remote model works end-to-end TODAY with zero code
   changes — verified as a WP2/WP3 optional smoke check.
3. **What refactor is needed for an arbitrary user-provided embedding model? Do we need
   tokenizers per model family?**
   Yes — one `IEmbeddingTokenizer` abstraction with per-family implementations:
   `bert-wordpiece` (implemented today), `sentencepiece` (bge-m3 — `Microsoft.ML.Tokenizers`
   2.0.0 ships `SentencePieceTokenizer`, no new dependency), `tokenizer-json` (Qwen2-style BPE —
   capability-gated, deferred). Plus per-model pooling strategies, a manifest (dims, tokenizer,
   pooling, ctx, normalization, input/output names), dynamic vec0 dimension, per-engine context
   budget. Full shape below.

## 1. Verified facts (2026-08-21, all lanes + reviewers)

| Fact | Value | Grade |
|---|---|---|
| Provider dispatch exists | `EmbeddingService` local (ONNX) / openai (OpenAI-compatible SDK client); settings `embedding.{provider,model,baseUrl,apiKey,engine}`; fingerprint change → auto re-embed | READ (code) |
| Current local engine | bundled all-MiniLM-L6-v2 qint8 23 MB, 384-dim, mean-pool+L2, WordPiece `BertTokenizer` from vocab.txt, 256-token ctx, chunk budget 254 | READ (code) |
| vec0 dimension | `float[384]` in DDL (`MemorySchema.cs:137-141`), `EmbeddingMath.Dimension=384`; sqlite-vec has NO dim inference — mismatched insert errors | READ + verified |
| **DDL is digest-gated, NOT every-open** | `MemorySchema.cs:450-460` (ADR-0075): runs only when `storedDigest != SchemaDigest` — a runtime DROP is NOT healed by the next open; there is no in-process every-open DDL (the relay is a background job). The review round's most important correction. | VERIFIED by reviewer |
| `RebuildVecTableAsync` repopulates | `MemorySchema.cs:1417-1423` reads blob columns back into vec tables — a reconcile that "rebuilds" after `MarkAllEmbeddedPending` (which does NOT null the blob columns) would insert OLD-dim blobs into the NEW-dim table → stuck migration + ToolGate closed | VERIFIED by reviewer |
| `ReadVecDimensionAsync` conflates missing with 384 | falls back to 384 when the table is absent (`MemorySchema.cs:1436-1439`) — presence must be read explicitly | VERIFIED by reviewer |
| bge-m3 ONNX | official `onnx/model.onnx` 724,923 B graph + `onnx/model.onnx_data` 2,266,820,608 B; inputs `input_ids`+`attention_mask`; outputs `token_embeddings` + graph `sentence_embedding` (semantics UNVERIFIED — WP5 parity gate) | MEASURED |
| bge-m3 tokenizer | `sentencepiece.bpe.model` + `tokenizer.json`; no vocab.txt; xlm-roberta, hidden 1024, ctx 8194; `<mask>` id = 250001 (not 4) | MEASURED |
| bge-m3 quantization / GGUF | community `gpahal/bge-m3-onnx-int8` 570 MB single-file; `gpustack/bge-m3-GGUF` Q4_K_M 417.5 MiB | MEASURED |
| ML.Tokenizers 2.0.0 | `SentencePieceTokenizer.Create(stream, bos, eos, specialTokens)` present in the pinned DLL; TiktokenTokenizer exists, Qwen2 file-shape compat UNVERIFIED | READ + DLL probe |
| HF LFS oids | tree API `expand=true` returns `lfs.oid` SHA-256 pre-download; trust domain = HF itself (protects corruption/drift, NOT malicious repos — say so honestly; registry pins are the reviewed tier) | MEASURED |
| Migration machinery | outbox + lease (60 s TTL, expiry re-acquisition) + relay retry + ADR-0076 ToolGate; kill-9 mid-drain recovery is genuinely supported | VERIFIED by reviewer |
| Repair family tokenizer coupling | `ChunkPositionScanner.BudgetAsync` + `ChunkBackfill`/`ChunkIndexRepair`/`ReingestRepairJob`/`SqliteRepairStore` count with the BUNDLED WordPiece tokenizer while the budget is engine-aware — on a bge-m3 bank, re-chunking would count with the wrong tokenizer (ADR-0036 invariant violated) | VERIFIED by reviewer |

## 2. Target architecture (combined, rev 2)

```
user / badger
  │  ai-raccoon model download <repo-id>          (WP2: resolve tree, LFS-oid pins,
  ▼                                                    verify-download, write manifest)
<data-root>/models/<slug>/{manifest.json, model.onnx, model.onnx_data, sentencepiece.bpe.model, ...}
  │  ai-raccoon model set local <dir>             (WP3: manifest-required activation;
  ▼                                                    fingerprint change → re-embed)
EmbeddingService ── engine descriptor (manifest / settings rows)
  ├─ local  → OnnxEmbeddingGenerator: IEmbeddingTokenizer (wordpiece|sentencepiece|tokenizer-json)
  │            pooling (mean|cls|model-output|last-token*), normalization, dims, ctx,
  │            inputs/outputs        (*last-token: pass-through only, no consumer in scope)
  └─ openai → OpenAI-compatible client (LM Studio / any /v1/embeddings)   [works today @384]
  ▼
vec0 tables with float[N]  (WP4: transactional reconcile inside the migration relay)
  ▼
re-embed drain (existing outbox + lease machinery) → eval harness on a bank copy (WP5)
```

## 3. Decisions (rev 2 — review findings folded; changes marked ⟲)

| # | Decision |
|---|---|
| D1 | **Manifest contract — ONE pinned JSON schema** ⟲ (review B2/F1): sidecar `manifest.json` next to local model files; canonical shape = nested (architecture §5.6) extended with `requiresTokenTypeIds` and a **numeric special-token map** taken from `special_tokens_map.json`/`tokenizer_config.json` at download time (no `<mask>` mapping — xlm-roberta mask id is 250001, model-specific); `pooling.mode ∈ {mean, cls, model-output, last-token}` (engineer's C# enum MUST include `model-output`); bundled model = compiled-in manifest; remote = settings rows (+ `embedding.dimensions`); legacy `embedding.model=<path>.onnx` keeps working. WP1 golden fixtures + WP2 mocked-downloader fixtures both pin this schema. |
| D2 | New settings row `embedding.dimensions` (remote dims); `embedding.model` accepts a directory (local). Key hygiene ⟲ (review F10b): `model set local` deletes `embedding.dimensions`; `ModelResetAsync` deletes the new key alongside the existing five. |
| D3 | **Dimension strategy = transactional vec0 reconcile** ⟲ (review B1/F2/F7 — mechanism REWRITTEN): the drain's reconcile is **create-if-missing-or-mismatch at the target dim**, executed as ONE `BEGIN IMMEDIATE` transaction: read table PRESENCE explicitly (never `ReadVecDimensionAsync`'s 384 fallback for a missing table), `DROP TABLE` + `CREATE VIRTUAL TABLE … float[N]` for BOTH `vec_entries` and `vec_structure`, **no repopulate** (the drain refills via the existing triggers; `RebuildVecTableAsync`'s repopulate-from-blobs is a trap when blobs still hold old-dim vectors). Kill-9 mid-tx rolls back cleanly; retry machinery handles the rest. The DDL stays CONSTANT at `float[384]` — the "DDL dimension becomes a parameter" idea is dropped (it collides with the digest gate and buys nothing). The six triggers survive DROP (verified empirically) and reference the recreated tables. The "every-open DDL heal" narrative is superseded everywhere: the rev-1 MoE section docs (`-architecture.md` §7.2 steps 3/5, R7; `-engineer.md` §4.3 steps 3-4, S6) carry **> SUPERSEDED by rev-2 D3** inline markers plus a top-of-document rev-1 banner. |
| D4 | New CLI verb `ai-raccoon model download <repo-id>` with `--revision/--file/--dir/--dry-run/--yes`; **`--set` chaining removed** ⟲ (review m2 — download must not silently activate); SHA-256 pinned from HF LFS oids pre-download; >500 MB requires `--yes`; disk-space check + `.part` cleanup on Ctrl-C ⟲ (m12); tree API paginated for repos >1000 entries ⟲ (m7); external-data detection enumerates the ONNX protobuf's `external_data` entries (glob fallback) ⟲ (m6); ORT opset smoke-test at download-verify time ⟲ (m10). The verb is OPTIONAL for the core ask (hand-placement + manifest is a viable v1) but kept ⟲ (F12). |
| D5 | Tokenizer scope: `bert-wordpiece` + `sentencepiece` required; `tokenizer-json` gated on an ML.Tokenizers capability check (deferred — Qwen3 also lacks official ONNX). Manifest validation: unknown family / bad dims / missing sha / missing files / `model-output` without `onnx.embeddingOutput` → reject with actionable errors. |
| D6 | Chunk budget for manifest-local models = a NAMED constant `MaxManifestChunkTokens = 510` ⟲ (review F14) — a **deliberate v1 conservative cap**, NOT derived from the model's context window: 510 = 512 − 2 (one 512-token window minus the special-token reservation; bge-m3's MCLS pooling applies ABOVE 512 tokens and is deferred until parity work, so CLS-pooling semantics are only valid within 512; the model's full `ctx` stays recorded in the manifest for the future MCLS/long-context phase). `TrimQueryToWindow` trims to the same cap for manifest models ⟲ (F10c); remote stays 8191; bundled stays 254. The budget change lands in WP3, confined to manifest models ⟲ (F8). SentencePiece bos/eos double-reservation must be pinned by parity fixtures (CountTokens vs ids-minus-specials semantics) ⟲ (m4). |
| D7 | **Fingerprint = hash of the manifest's semantic content INCLUDING per-file sha256s** ⟲ (review F3): re-downloading a model (same path, same dims, new weights) changes the file hashes → re-embed fires. `local:<path>#<model>@<dims>` alone is insufficient. |
| D8 (ops) | Download tooling = URL + SHA via the `BundledResource.IsVerified` pattern; **the verb is C#** (CliCommandTree.cs:151-164 family), NOT the python `fetch_verified` ⟲ (review M5 — name the C# home and its test surface; no shell-out to python). Pinning = registry pins (committed, blessed models — incl. a committed pin for the official bge-m3 fp32 pair in WP5 ⟲ F11) + first-download TOFU pins (persisted in the manifest, re-verified on every load; warning: TOFU trusts the channel once). |
| D9 (eng) | Tokenizer routing: `IEmbeddingTokenizer` per engine; `ILocalTokenizer` stays for bundled-default consumers; the two engine-relative call sites (FileIngestor override, TrimQueryToWindow) route through `EmbeddingService` as tokenizer resolver. **The repair/backfill family (ChunkPositionScanner.BudgetAsync, ChunkBackfill, ChunkIndexRepair, ReingestRepairJob, SqliteRepairStore) routes through the same resolver** ⟲ (review F4) — budget AND counter must both match the active engine (ADR-0036 invariant); the openai provider stays on the o200k proxy at 8191 (unchanged, honest per-counted-tokenizer guarantee). |
| D10 ⟲ (new) | **Remote dims probe: pre-commit primary** — `model set openai --dims N` probes the endpoint (explicit timeout ⟲ m11) BEFORE the outbox commits; probe ≠ declared → refuse with an actionable error, outbox never commits. Drain probe remains as defense-in-depth. **Undeclared dims (no `--dims`) + probe ≠ 384 → fail-closed** with "set --dims N", never a silent 384 assumption. Wrong-`--dims` recovery: re-issue `model set` (documented), bank never wedged. Negative gate in G4. |
| D11 ⟲ (new) | **Pooling provenance at download time** (review F10a): read `1_Pooling/config.json` + `modules.json` when present; otherwise ASK the user — never a silent default. WP2 writes a placeholder pooling that WP5's parity measurement rewrites (G2 fixtures update accordingly) ⟲ (M1). |
| D12 ⟲ (new) | Repair-family + remote steady-state: a server-side dims change without migration makes writes/queries fail loudly at trigger/MATCH time (no silent corruption); add a first-embed dims assertion and document the failure mode ⟲ (m13). |

## 4. bge-m3 case-study specifics (rev 2)

- **Download**: repo `BAAI/bge-m3`, auto-select `onnx/model.onnx` + its external-data pair (protobuf-enumerated), tokenizer `sentencepiece.bpe.model` (+ `tokenizer.json` as normalization fallback source ⟲ m8), `config.json`/`tokenizer_config.json` as provenance; 2.27 GB → `--yes` guard. Registry pin committed in WP5 so the case-study download is never TOFU ⟲ F11.
- **Manifest**: dims 1024, ctx 8192, tokenizer family sentencepiece with numeric special-token map from tokenizer_config, pooling = `model-output` placeholder (graph `sentence_embedding`) with `token_embeddings` CLS fallback — **WP5 parity decides** ⟲ M1; normalization l2; no query instruction.
- **Parity gate (WP5, RED before trusting)**: bar FIXED at cosine ≥ 0.999 ⟲ M7 (same weights + same pooling give ≈1−1e-7; wrong pooling lands well below); negative control: deliberately wrong pooling MUST fail; spot-check texts capped ≤512 tokens (or reference truncation mirrored); threshold never re-baselined from measurement.
- **Re-embed**: 22,514 entries, bge-m3 fp32 CPU wall time UNVERIFIED — measured in WP5 on a bank copy. Default model stays unchanged; owner decides from the eval report (defaults 0.6105 vs bge-m3 vs tuned 0.6655, per-query regression table + test-set-10 grades).
- **LM Studio route**: user loads `gpustack/bge-m3-GGUF` Q4_K_M via URL-add, `model set openai bge-m3 --base-url http://127.0.0.1:1234/v1 --api-key lm-studio --dims 1024` → works after WP4. 384-dim remote works today; the zero-code smoke check moves to WP2/WP3 ⟲ F8.

## 5. Phased work packages (rev 2 — gates updated; all TDD, no behavior change before WP5)

| WP | Lane | Deliverable | Gate |
|---|---|---|---|
| WP0 | all | This plan reviewed (round 1 folded; round 2 = owner) | G0 owner review in Rider |
| WP1 | arch+eng | Manifest contract per D1: record + (de)serializer + validation + golden fixtures (null-manifest legacy, full bge-m3, malformed set) | G1: unit green; rejects bad dims/sha/family; fixtures round-trip; **fixtures pin the ONE D1 schema** |
| WP2 | eng+ops | `model download` verb per D4/D8: HF resolution (paginated), LFS-oid pins, external-data enumeration, manifest writer (pooling placeholder per D11), `--dry-run`, size+disk guards, ORT opset smoke | G2: mocked-HF fixture tests; SHA-mismatch RED→GREEN; real `--dry-run` on BAAI/bge-m3 without downloading; optional 384-dim LM Studio smoke check ⟲ F8 |
| WP3 | eng | Behavior-preserving engine generalization per D1/D5/D6/D9: tokenizer families (wordpiece+sentencepiece), inputs/outputs, pooling (mean, cls, model-output), normalization, runtime dims, directory activation (**manifest REQUIRED for directories — reject with actionable error, only legacy .onnx keeps defaults** ⟲ M3), fingerprint per D7, repair-family tokenizer routing per D9, 510-cap budget for manifest models only, **384-refusal for non-384 manifests ships here** ⟲ M4 | G3: full `dotnet test` green; golden vector gate — MiniLM embeddings **byte-identical same-arch** (capture arch == run arch) with tolerance secondaries (L2 ≤ 1e-6 + token-id equality) ⟲ M2/F9; the `EncodeToIds(text, true, true, true)` overload is pinned in the seam ⟲ F9a; legacy custom-path test unchanged; event ids 414/415/416 preserved (+ 415 wording change per engineer S8 ⟲ m5); refusal test in G3's list |
| WP4 | eng | Transactional dimension reconcile per D3 + remote dims per D10: `embedding.dimensions` row, `--dims` on `model set openai`, pre-commit probe | G4: fixture-bank tests on a scratch server — (a) 1024 manifest migrates: counts parity + DDL `float[1024]` for **BOTH vec_entries AND vec_structure** (row parity incl. heading-path rows ⟲ F13); (b) kill-9 **between DROP and CREATE** (and at random drain points) recovers: both tables recreated at target dim ⟲ F7; (c) legacy 384 untouched; (d) wrong-`--dims` (declared 1024, probe 384) → refused, outbox NOT committed ⟲ F6; (e) undeclared dims + probe ≠ 384 → fail-closed error ⟲ F6; (f) reconcile-disabled negative: blob-length guard fires, migration stays open ⟲ M6a |
| WP5 | ops+eng+eval | bge-m3 case study (only behavior-changing phase): real download (registry-pinned), parity check RED→GREEN (fixed 0.999 bar + negative control), swap on a bank COPY, timed re-embed, eval harness run; 384-dim LM Studio remote eval | G5: parity passes with the negative control witnessed; eval report defaults vs bge-m3 (regression table + test grades); re-embed time recorded; **default model unchanged** |
| WP6 | arch+eng | ADR (extends docs/adr/0036), docs drift audit, one squash-merge PR | G6: ADR reviewed; PR merged |

## 6. Risks (rev 2)

- **Migration wedge** (the review round's headline): any mechanism that inserts old-dim blobs into new-dim tables (repopulate trap) or relies on a non-existent every-open DDL heal wedges the bank with the ToolGate closed. D3's single-transaction create-if-missing-or-mismatch + the G4 kill-9 tests are the defense.
- bge-m3 `sentence_embedding` semantics unverified → WP5 parity gate RED-before-trust with the CLS fallback.
- Re-embed wall time on 22.5k entries with a 2.1 GB fp32 model — measured, not assumed; LM Studio/Ollama is the cheap alternative.
- `tokenizer-json` gated/deferred; Qwen3 also lacks official ONNX.
- TOFU trust for arbitrary repos is one-time per channel — the case study gets a registry pin; the CLI warning is honest about the trust model.
- Dimension flips cost a full re-embed per switch, in both directions — the default model stays unchanged until the owner decides from WP5 evidence.

## 7. Review-round disposition (what changed between rev 1 and rev 2)

All review findings are folded into the decisions/gates above. Mapping: code-reviewer B1→D3, B2→D1, B3→D10; M1→D11+§4, M2→G3, M3→WP3 gate, M4→WP3 gate, M5→D8, M6→G4, M7→G5, m1-m13→D4/D6/D7/D9/D10 + gates. Architect F1→D1, F2→D3, F3→D7, F4→D9, F5→D1, F6→D10, F7→D3+G4, F8→WP2/WP3, F9→G3, F10→D2/D6/D11, F11→D8+WP5, F12→D4, F13→G4, F14→D6. The reviewers' answers to the original open questions are incorporated (Q3 → tolerance-backed gate; Q4 → TOFU accepted with honest framing + registry pin; Q5 → pre-commit probe; Q6 → trims).

## 8. Owner decisions (resolved in Rider)

1. **Rev-2 plan approved** (G0, 2026-08-21) — implementation under way (PR #402, WP1-WP6).
2. **No default model changes** — WP5's eval report never changes the shipped default in this task (a separate ADR would be required for any default change).
3. **`model download` ships in the same PR** as the engine generalization.
