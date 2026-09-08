# Build plan: llama_index + Chroma fusion-retrieval harness

*One line: architecture blueprint only — no code written, no files touched.*

All defaults below are **verified** from the research record + cited sources I re-read (`FtsQueryNormalizer.cs`, `ReciprocalRankFusion.cs`, `SourceAffinityRanker.cs`, `SearchResultMerger.cs`, `SearchDefaults.cs`, `SearchQuery.cs`, `make_memory_copy.py`, `build_eval_corpus.py`, `pyproject.toml`). Anything I could not verify is labelled **HYPOTHESIS**.

## Context map (mandatory gate §2)

- **Primary (port semantics from):** `src/AiRaccoon.Infrastructure/Sqlite/Memory/{FtsQueryNormalizer,ReciprocalRankFusion,SourceAffinityRanker,SearchResultMerger,SqliteMemoryStore.Search}.cs`, `src/AiRaccoon.Core/Memory/{SearchQuery,SearchDefaults,SearchParameters,CandidateWindowMode}.cs`
- **Secondary (reuse as-is):** `scripts/retrieval_tuning/make_memory_copy.py` (bank-copy pattern), `scripts/retrieval_tuning/corpora/eval-set-100.json` (read-only input), `pyproject.toml` (dep declarations)
- **New files (all under one folder — screaming over `Utils/`):** `scripts/retrieval_tuning/llamaindex_harness/{ingest.py,retrieve.py,evaluate.py,report.py}` + `scripts/tests/test_llamaindex_harness_*.py`
- **Existing patterns to follow:** stdlib-only + `argparse` + `main(argv)` + import-safe scripts (per `make_memory_copy.py`); `test_dependencies_declared.py` derives third-party imports from AST — **every new import must be added to `pyproject.toml` or CI fails**
- **Edit sequence:** freeze `Doc` record contract → P1+P2 build concurrently → P3 wires both → PR

## Frozen replication contract (both legs, exact knobs)

`rrfK=60`, `ftsWeight=vectorWeight=1`, `limit=8`, `minRelativeScore=0.6`, `sourceLambda=0.1`, `consolidationThreshold=0.1`, `docScoreFormula=Max`, candidate window `clamp(limit*3, min 100)` (=100 at limit 8), `scope=project`, project `ai-raccoon`, memory kind only. Pipeline order: legs → RRF fuse (normalize to max, payload from first list carrying the hash) → source-affinity rank (λ, doc-Max, consolidation, re-normalize) → relative floor → `Take(8)`. Path queries force λ=0. Skipped legs (empty FTS plan / empty vector / weight 0) are **absent**, never empty. Chunk-faithful: reuse bank rows verbatim (`hash, value, source_file, chunk_index, path/section`) — **no re-chunking**.

## Plan-review disposition (2026-09-08, code-reviewer d-625, verdict PLAN-NEEDS-WORK — all 8 MUSTs folded in, full review /tmp/d625-full.md)

M1 scope/buckets: route each eval query at its own targetProjectId/targetScope; ingest ALL targeted buckets (ai-raccoon/project + hermes-default/project + ai-raccoon/shared + ai-badger/shared), OR restrict to the 75 file-targeted rows and report the restriction. Decision at build; report must state it.
M2 live mutation: eval ai-raccoon leg against a scratch-server copy via scripts/src/retrieval_tuning/server.py + mcp.py client (BumpAccessAsync mutates the live bank on every served search) — or explicitly accept + re-verify.
M3 dual-vector: bank vector leg is content+structure fused at structureAlpha=0.5 (StructureFusion.cs: missing structure scores 0). Either ingest heading-path structure texts + fuse at α, or scope harness to content-only with `structureAlpha=n/a (content-only)` in params.json + split diagnostic into embedding-gap vs structure-gap columns.
M4 path queries: port SourcePathQuery.TryBuild regex + rewrite verbatim ({source_file[: section]} : AND-terms, Fallback=null, IsPathQuery → λ=0); add file.md + file.md#section goldens.
M5 FTS fallback trigger: fallback runs only when plan.Fallback non-null AND ftsResults.Count <= max(TokenCount, query.Limit) (Limit, not window); under/over-match goldens.
M6 reuse: build P3 on scripts/src/retrieval_tuning/{mcp,server,evaluate,scoring}.py seam (MCPClient + ScratchServer + server.search(entry)); justify F1/MCC over nDCG/MRR/hit or document the fork; reconcile exact-hash vs prefix/suffix/anchor scoring and limit 5-vs-8 (uniform limit either way; window identical at 5 and 8).
M7 null metadata: freeze coercion (null source_file/section → "", keep chunk_index=-1 verbatim, "" counts as null in affinity); gate ingesting a shared row + a -1 row. (Chroma-None-rejection unverified — probe at build.)
M8 dep gate: extend _IMPORT_TO_DISTRIBUTION (llama_index→llama-index + any direct sentence_transformers/huggingface_hub use), import only top-level names; pre-existing matplotlib red is not ours — keep gates separable.
SHOULD S1–S10 folded: RRF leg order [fts, vector] + tie-break goldens (incl. path-tiebreak + skipped-leg); affinity consolidation exact rule (adjacent-to-boosted-best, gap ≥ threshold, chunk_index ≥ 0; doc-score from boosted scores, Max/Sum from params.json, null-source branch); FTS parity = schema (value, source_file, section) + bm25(1.0,8.0,4.0) + order; 2-vs-3-token bigram goldens + reserved-vs-stopword-only + M4/M5 pairs; report hit-rate primary/F1 secondary, MCC null-with-reason on zero denominator, transport failures excluded-pairwise; params.json also records structureAlpha + fusionNoRegression + scope/kind/limit, Doc adds totalChunks or justifies; Chroma persists to gitignored scratch (not harness dir), record bytes + --offline.

## Package 1 — Ingestion: bank-copy → Chroma + FTS

**Builds:** `llamaindex_harness/ingest.py`. Reads a `make_memory_copy.py` copy read-only (`file:…?mode=ro`), filters `project_id='ai-raccoon' AND scope='project'`, embeds `value` with HF `Salesforce/SFR-Embedding-Code-400M_R` via llama_index `HuggingFaceEmbedding`, writes a **persistent** Chroma collection (ids = content hashes, metadata = `source_file, chunk_index, path, section`), plus a SQLite FTS5 index over the same rows. Persists under the harness dir. Writes all 9 knobs to a `params.json` next to the store (compensates the settings-leak noted in `make_memory_copy.py`).

- **AC1:** re-ingest is idempotent (same hashes → same count, no dupes). **Gate:** run `ingest.py` twice, `chromadb count` equal both times + `pytest test_llamaindex_harness_ingest.py`.
- **AC2:** chunk-faithful: every Chroma id exists in the copy with byte-identical value (SHA-256 spot check, same method as `make_memory_copy.spot_check_hashes`). **Gate:** `ingest.py --verify-only` exit 0.
- **AC3:** FTS index returns the same row set as bank FTS for a 1-token probe. **Gate:** probe query diff empty in test.

## Package 2 — Retrieval: FTS leg + vector leg + RRF + post-rank

**Builds:** `llamaindex_harness/retrieve.py` exposing a `BaseRetriever`-compatible `FusionRetriever.retrieve(query, limit=8)`.

- **FTS leg:** port `FtsQueryNormalizer.BuildPlan` exactly (regex `[\p{L}\p{N}_]+`, lowercase, reserved `{and,or,not,near}`, the 30 stopwords, 0→skip / 1→term / ≤4→`AND`+`OR`-fallback(raw+bigrams) / >4→plain `OR`). Rank = BM25 ascending, top-100.
- **Vector leg:** embed query with the same model, Chroma `query(top_n=100)`, rank = cosine descending.
- **Fusion:** score = Σ `w/(60+rank)`, normalize to max, first-list payload. **Post:** port `SourceAffinityRanker` exactly (sibling = same `source_file`, `|Δchunk_index|==1`, sibling ranking ≥ `maxRaw−0.1`; `chunk_index<0` scores 0; consolidation drops adjacent weak siblings with gap ≥0.1; doc-Max tie-break, then path, then chunk_index; re-normalize). Then floor `≥0.6` of boosted max, `Take(8)`.

**Retriever pick: manual RRF, NOT `QueryFusionRetriever`.** Justification: stock `QueryFusionRetriever` generates N queries via LLM (nondeterministic, wrong abstraction — our two legs are fixed FTS+vector, not LLM paraphrases); even at `num_queries=1` it still routes through the LLM and applies its own fusion defaults, so parity would be approximate where a ~20-line manual port is exact and pure-testable. Use llama_index only for `Document`/`HuggingFaceEmbedding`/`BaseRetriever` interface compliance.

- **AC1:** FTS plan port matches C# on ≥20 probe queries incl. 0/1/≤4/>4-token and stopword-only cases. **Gate:** `pytest test_llamaindex_harness_fts_plan.py` (golden vectors derived from the C# source, not from the Python code).
- **AC2:** RRF + affinity rank match C# on a synthetic hand-worked example (3 hashes × 2 legs). **Gate:** golden-value unit test.
- **AC3:** end-to-end `retrieve()` returns ≤8 hashes with `ranking ≥ 0.6·top`, sorted desc. **Gate:** property test over 20 corpus queries.

## Package 3 (integration) — Eval: both systems → F1 + MCC → markdown report

**Builds:** `llamaindex_harness/evaluate.py` + `report.py`. For each of the 100 corpus queries: run ai-raccoon `memory_search` (**memory kind, `scope=project`, project `ai-raccoon`, limit 8, all fusion defaults** — note corpus rows carry `searchLimit:5`; both systems must run at the same limit, 8, and the report must say so) and `FusionRetriever.retrieve()`. Emit `docs/work/llamaindex-fusion-eval-<date>.md` with per-query table + aggregates.

- **AC1:** all 100 queries run on both systems; harness failures = 0, ai-raccoon errors logged not fatal. **Gate:** `evaluate.py --corpus … --out results.json` exit 0 with 100×2 rows.
- **AC2:** report contains per-query F1, both hit-rates, MCC, and the parity-gap discussion (ONNX vs HF). **Gate:** reviewer checks the 5 required sections exist (`pytest test_llamaindex_harness_report.py` asserts headers).
- **AC3 (cross-package):** ingestion→retrieval→eval wired end-to-end from a fresh bank copy in one command. **Gate:** `make_memory_copy.py --target $SCRATCH && ingest.py && evaluate.py --limit-queries 10` green on a clean checkout.

## Metric definitions (AC5; "MMC" read as MCC)

- **Per-query F1.** Expected set `E = {expectedHash}` (singleton; `expectedSource` is diagnostic only). Returned set `R` = system's served hashes (post-floor, post-limit, order ignored). Hit `h = 1` if `expectedHash ∈ R` else `0`. `P = h/|R|` (`|R|=0` → `P=0`), `Rc = h/1 = h`, `F1 = 2PR/(P+R)` (`0` when `h=0`). Report mean-F1 per system. (`negativeTest` is `false` for all 100 rows — no exclusion rule needed; assert that in eval and fail loud if a future corpus adds any.)
- **MCC — two numbers, because the naive one is degenerate.** (a) Per-system hit/miss over 100 queries has actual=`1` for every trial (each query has a known relevant doc), so a single-system MCC denominator is zero — **report hit-rate, not MCC, per system, and say why.** (b) The valid MCC is the **inter-system agreement phi coefficient**: 2×2 table over the 100 queries of {harness-hit × ai-raccoon-hit} (a=both-hit, b=harness-only, c=airaccoon-only, d=neither), `MCC = (ad−bc)/√((a+b)(a+c)(b+d)(c+d))`. This measures whether the replica succeeds/fails on the same queries — the actual parity question. **Gate:** eval asserts the contingency cells sum to 100 and guards zero-denominator.
- Optional (not gating): McNemar on b vs c for "are the systems significantly different."

## Parallelism (AC3)

- **Concurrent:** P1 (ingest) ∥ P2-pure (FTS-plan port, RRF, affinity rank — pure functions, testable on synthetic data with no store). Prerequisite: freeze the `Doc{hash,value,source_file,chunk_index,path,section}` record + `params.json` key list first (30-min contract, owned by P1).
- **Serial:** P3 after P1+P2 (needs a populated Chroma store + working retriever). `pyproject.toml` dep edits serialise (one PR section, P1 owns it; P2/P3 request additions through P1 to avoid merge skew).
- **Shared files:** `corpora/eval-set-100.json` (read-only, all packages), bank copy path (read-only, P1+P3), `params.json` + Chroma dir (P1 writes, P2/P3 read), `pyproject.toml` (P1 writes).

## Dependency / environment risks (AC4)

1. **`pip install llama-index chromadb` weight + pins.** Mitigation: `python3 -m pip install` (note: bare `pip` is **absent**; brew python3 3.14 is the interpreter; no repo venv — the root `.venv` belongs to the parent checkout, not this worktree). Pin strategy — **HYPOTHESIS, verify with `pip index` at build:** `llama-index>=0.12`, `llama-index-embeddings-huggingface`, `llama-index-vector-stores-chroma`, `chromadb>=0.6,<1.0`; record exact resolved pins in the PR. Watch `test_dependencies_declared.py` — declare every new distribution.
2. **HF model download size (~1–2 GB, HYPOTHESIS).** `~/.ai-raccoon/models` already holds 2.3 GB and the HF hub cache exists; `torch`+`transformers`+`sentence-transformers` 5.6.1 are preinstalled, so only weights download. Mitigation: cache under the harness dir or `HF_HOME`, document bytes, support `--offline` reuse.
3. **ONNX-vs-HF parity gap** (research record's own HYPOTHESIS: bank runs local ONNX `SFR-Embedding-Code-400M_R`, harness runs public HF weights — same architecture, possibly different quantization/normalization). Mitigation: not fixable in-plan — **measure and report it** (vector-leg-only recall@100 per system as a diagnostic column separating embedding gap from fusion gap); never tune harness knobs to close it silently.
4. **Python 3.14 + Chroma's bundled SQLite.** Chroma needs a modern SQLite (often via `pysqlite3-binary`); mitigation: gate is `import chromadb; chromadb.PersistentClient(path)` smoke test in P1's first commit — if it fails, fall back to direct `numpy` cosine over embedded rows (keeps P2 pure parts unblocked) and record an ADR.
5. **Live-bank safety.** All reads via `make_memory_copy.py`'s `mode=ro` + `.backup` pattern; harness never opens `~/.ai-raccoon/memory.db` writable. **Gate:** `make_memory_copy.py --verify-only` exit 0 before every eval run.
6. **Corpus staleness.** `expectedHash` values resolve against the copy at generation time; a drifted bank invalidates anchors. Mitigation: regenerate-verify step (`build_eval_corpus.py` asserts hash uniqueness) before the final numbers; report the copy's entry count in the report header.

## What I rejected and why (simpler-shape check)

- **`QueryFusionRetriever(num_queries=1)`** — rejected: LLM in the retrieval path destroys determinism and its fusion is not our RRF; manual port is smaller and exact.
- **Single-script harness** — rejected: ingestion (slow, I/O, run once) vs retrieval (pure, unit-tested) vs eval (needs both) have different gates and different rerun cadences; one file couples them. Three modules under one `llamaindex_harness/` folder is the smallest shape that keeps those gates independent.
- **`rank-bm25` instead of SQLite FTS5** — deferred fallback only: bank *is* FTS5/BM25, so FTS5-over-the-same-chunks is the faithful leg; accept `rank-bm25` only if FTS5 parity probe (P1 AC3) fails, documented in the report.
- **Re-chunking with llama_index splitters** — rejected: breaks hash identity with `expectedHash`; chunk-faithful reuse is load-bearing for the whole comparison.

## Hypotheses (unverified)

- Exact pip pins and HF weight size (§above). - `llama-index-vector-stores-chroma` version-compat with the pinned `chromadb` — P2's first commit decides adapter vs thin direct `chromadb` client, gated by a 10-line round-trip script. - Whether `memory_search` MCP latency permits 100 sequential calls in reasonable time; if not, P3 batches with documented backoff (no parallelism against the live server beyond what it tolerates — **HYPOTHESIS**, probe first).
