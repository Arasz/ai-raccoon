# .NET RAG storage on SQLite + sqlite-vec, and pluggable embedding providers

Verified research (2026-08, grades MEASURED/READ/INFERRED/UNVERIFIED as marked). Motivation:
replace a pinned native sqlite-memory extension whose remote embedding URL was hardcoded, with a native .NET layer where the embedding provider is a config value. Facts carry dates — re-verify versions via `library-adoption-evaluation` →
`references/nuget-package-inspection.md`.

## sqlite-vec status (github.com/asg017/sqlite-vec)

- **Actively maintained**: stable **v0.1.9 (2026-03-31)**, latest **v0.1.10-alpha.4 (2026-05-18)**, repo pushed 2026-05-18, ~8k stars, Apache-2.0, 200 open issues. READ (GitHub API)
- **Pre-v1**: README banner "`sqlite-vec` is a pre-v1, so expect breaking changes!" — pin the version. READ
- **No stability red flags found**: only 3 crash-title issues ever (all closed), zero segfault/panic-title issues. READ (issue search API)
- **Brute-force KNN**: vector search is full-table scan, no ANN. READ (author blog + community repos)
- **Platforms**: release assets = linux aarch64/x86_64, macos aarch64/x86_64, windows x86_64, android, ios. **No linux-musl in v0.1.9 or v0.1.10-alpha.4** — musl consumers must build from source (same gap as the sqlite-memory pin it
  replaces). MEASURED (asset lists)

## Loading sqlite-vec in Microsoft.Data.Sqlite (.NET 10)

- Official docs: `SqliteConnection.LoadExtension()` (since v3.0); .NET's native-lib resolution does NOT apply — the .so/.dylib must be discoverable (PATH/LD_LIBRARY_PATH/DYLD_LIBRARY_PATH or next to the app). READ
  (learn.microsoft.com/dotnet/standard/data/sqlite/extensions)
- **Official NuGet `sqlite-vec`** (author = Alex Garcia): `0.1.7-alpha.2` (2025-05-08) and
  `0.1.7-alpha.2.1` (2025-05-09), netstandard2.0, no deps. Nupkg inspected: bundles
  `runtimes/{linux-x64,linux-arm64,win-x64,osx-x64,osx-arm64}/native/vec0.*` and ships
  `Microsoft.Data.Sqlite.SqliteVectorExtensions.LoadVector(this SqliteConnection)`
  → `connection.LoadExtension("vec0")`. So: `dotnet add package sqlite-vec` + `conn.LoadVector()`. MEASURED (nupkg inspection)
- **Community `HiraokaHyperTools.sqlite-vec` 0.1.9 (2026-05-20)**, 890 downloads, same packaging with current v0.1.9 binaries (MEASURED). Neither package has musl natives.
- No official .NET bindings repo (`asg017/sqlite-vec-dotnet` → 404); open issues #193/#202 requested .NET support, the alpha package was the author's response. READ

## FTS5 hybrid search (BM25 + vector) — yes, in SQL

sqlite-vec is vector-only; BM25 comes from SQLite's built-in FTS5. Author's guide (2024-10-02):
create `fts5` + `vec0` virtual tables, fuse with (1) keyword-first CTE + UNION ALL, (2)
vector-first re-rank, (3) **reciprocal rank fusion (RRF)**. FTS5 `rank` = negative BM25. READ (alexgarcia.xyz/blog/2024/sqlite-vec-hybrid-search)

**FTS5 IS compiled into the current Microsoft.Data.Sqlite bundle**: `SQLitePCLRaw.bundle_e_sqlite3`
3.0.5 (2026-07-27) → native `SQLite` package 3.53.4. `strings` on the shipped osx-arm64 dylib and linux-musl-x64 .so shows `ENABLE_FTS5` (also FTS3/4, RTREE, JSON). This overturns the old ericsink/SQLitePCL.raw#171 concern (2018). MEASURED.
The bundle also ships linux-musl natives — so Microsoft.Data.Sqlite itself is musl-clean even though sqlite-vec binaries are not.

## Embedding provider pluggability (.NET 10) — the fix for a hardcoded endpoint

One seam, `IEmbeddingGenerator<string, Embedding<float>>` (Microsoft.Extensions.AI, v10.8.3 has a `lib/net10.0/` — MEASURED via nuspec). Provider = config value (model + baseUrl + apiKey):

| Provider                                                                     | Package                                              | Shape                                                                                                                                                                                                                                                                                                                                                                                                                                                 |
|------------------------------------------------------------------------------|------------------------------------------------------|-------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Any OpenAI-compatible endpoint (vectors.space, LM Studio, vLLM, self-hosted) | `OpenAI` (2.12.x) + `Microsoft.Extensions.AI.OpenAI` | `new OpenAI.Embeddings.EmbeddingClient(model, key, new OpenAIClientOptions { Endpoint = new Uri(baseUrl) }).AsIEmbeddingGenerator()` — openai-dotnet README documents this exactly for "a proxy or self-hosted OpenAI-compatible LLM". READ                                                                                                                                                                                                           |
| Ollama (local or cloud)                                                      | `OllamaSharp` (5.4.x, 3.25M downloads)               | `OllamaApiClient` implements `IChatClient` + `IEmbeddingGenerator<string, Embedding<float>>` directly; ctor `new OllamaApiClient(new Uri("http://localhost:11434"))` or HttpClient overload (cloud/api-key). READ (repo README + source)                                                                                                                                                                                                              |
| **DEPRECATED** `Microsoft.Extensions.AI.Ollama`                              | —                                                    | "This package is deprecated and the OllamaSharp package is recommended… no further updates, features, or fixes are planned" (nupkg README, MEASURED). Final version 9.7.0-preview.1.25356.2 (2025-07-07), net462/net8/net9/netstandard2.0. Contains only `OllamaChatClient`/`OllamaEmbeddingGenerator`; **no `AddOllamaApi` DI extension exists in its XML docs** (the early-preview name is gone). UNVERIFIED-as-named → do not cite `AddOllamaApi`. |

OllamaSharp has no `AddOllamaSharp` DI extension either (no ServiceCollectionExtensions in its source) — register manually via `AddEmbeddingGenerator(factory)` or resolve the client and cast.

## Minimal component list for a native .NET RAG memory layer

1. Schema/store: Microsoft.Data.Sqlite + Dapper (no new ORM); `dbmem_content` rows keyed by content SHA-256; `vec0` virtual table; FTS5 external-content table (`content=` + `content_rowid=`).
2. sqlite-vec native via NuGet + `conn.LoadVector()` (or existing per-RID provisioning; keep pinning).
3. Hybrid query: FTS5 CTE + vec0 KNN CTE, fuse with RRF/weights in SQL (reuse existing
   `memory_search`-style contract so MCP tool SQL barely changes).
4. Embedding provider: resolve `IEmbeddingGenerator` from config (provider/model/baseUrl/apiKey) — OllamaSharp for Ollama, OpenAI SDK endpoint override for any OpenAI-compatible server.
5. Chunking: simple deterministic chunker (~512-token paragraphs).
6. Dedup: hash-before-embed; deferred queue: `pending` flag + on-demand/background embed tool.

## Risks

- sqlite-vec pre-v1: pin and re-test on upgrade.
- No linux-musl sqlite-vec binaries → musl deployment needs a source build.
- Brute-force KNN: fine at memory scale, not millions of rows.
- Verify FTS5 at runtime once (`PRAGMA compile_options`) if you ever swap SQLite bundles.

## CRDT cloud sync does NOT chain you to sqlite-memory (sqlite-sync is schema-agnostic)

`sqliteai/sqlite-sync` (pinned 1.1.2, Elastic License 2.0) syncs ANY table, not just sqlite-memory's fixed schema: `cloudsync_init('table')` enables CRDT sync on a table,
`cloudsync_enable/disable/is_enabled` toggle it, `cloudsync_set_filter(table, expr)` gives row-level filtering (RLS), `cloudsync_network_init/set_apikey/sync` do transport.
`memory_enable_sync` was only sqlite-memory's *bridge* for its own schema — a native rebuild can call `cloudsync_init` on its own tables directly, leaving the cloudsync extension as the ONE native dependency in the design. READ
(sqliteai/sqlite-sync API.md, fetched 2026-08).

- Never reimplement the CloudSync wire protocol: undocumented, ELv2 (can't copy), and the server side (SQLite Cloud microservices) is fixed — keep the pinned native extension.
- Block-Level LWW was explicitly designed for markdown text sync (multiple agents editing different sections of the same doc merge without loss) — a match for memory content.

## sqlite-memory 1.3.5 → native rebuild: the parity hard items

Upstream is `sqliteai/sqlite-memory`; 1.3.5 (2026-06-10) is the deferred-embeddings release (PR #12). READ (GitHub releases). ~60% of the surface is trivial-to-moderate C# work (ingestion, deletes, deferred embeddings, settings, list_files,
remote embeddings) — and the
`memory_search` virtual table is an INTERNAL seam (only the store's own SQL consumes it), so replacing it with a C# search method behind the store interface breaks nothing external. Three genuinely hard items:

1. **CRDT cloud sync** → keep the native cloudsync extension (see above). Not a rebuild item.
2. **Hybrid ranking fusion + snippet parity** — the cosine/BM25 → 0..1 `ranking` normalization lives in unread C source (UNVERIFIED). Ship a golden-retrieval harness (old vs new rankings over a fixed corpus) or the quality regression is
   invisible until users complain.
3. **Local GGUF embedding engine choice** — ONNX Runtime + all-MiniLM-L6-v2 ONNX (single managed package, 384-dim; verify musl coverage at build) vs LLamaSharp (GGUF reuse, per-RID native baggage) vs sidecar `llama-server` process. ANY
   switch forces a one-time full re-embed of the bank (upstream does the same on model change). Subtle semantics to pin with scenario tests: global content-hash dedup ×
   `preserve_duplicate_paths` (three behaviors: add_text globally skips content that exists anywhere; add_content with a distinct path creates a REAL path-scoped row — this is how share/consolidate promote; hashes are path-scoped), and md4c
   markdown-aware chunking (chunk boundaries shift search hits/snippets — aim for quality parity, not byte parity).

## Framework/library evaluation verdicts (2026-08, all READ/MEASURED)

- **typical-rag-dotnet (NikiforovAll) — dead demo, not a blueprint**: ~100-line Microsoft.KernelMemory.Service.AspNetCore wrapper, .NET 8 only, storage is PostgreSQL/ pgvector ONLY (no SQLite anywhere), created + last-pushed 2024-09-03,
  zero releases/tags, nothing on NuGet. Do not cite it for sqlite-vec architecture.
- **SmartRAG (byerlikaya) — SKIP, confirmed "too bloated"**: 53.8k LOC / 278 files, .NET 6 TFM mixed with 10.x packages, ZERO tests (CI = build+pack only), 1 maintainer, and a quality red flag — its Qdrant "embeddings" are hash-seeded
  random noise (`new Random(text.GetHashCode())`
  filling a 768-dim vector). Dragging it in pulls ASP.NET Core + Qdrant + Redis + 3 SQL providers + Tesseract/OCR + Whisper + ffmpeg auto-download.
    - Steal 1: the **three-band strategy pattern** (high ≥0.7 / medium 0.3–0.7 → DatabaseOnly | DocumentOnly | Hybrid) as deterministic *retrieval-mode selection*
      (FTS-only / vector-only / hybrid) inside a store — NOT an LLM classifier per search.
    - Steal 2: its ~440-line `FileSystemWatcher` wrapper design (per-path dict, 1s/500ms debounce, MD5 duplicate skip, initial scan, 3-attempt retry) → `watch_add/status/remove`
      MCP tools; persist registrations so a server restart re-watches.
    - Reject: unified multi-source answers — that is a BI/QA product; the agent is the integrator, the memory store stores outcomes.
- **Semantic Kernel — SKIP wholesale and selectively (user asked \"is it worth using?\", 2026-08, MEASURED)**: the only genuinely useful artifact for a narrow memory store is `TextChunker`
  (a 363-line MIT file inside `Microsoft.SemanticKernel.Core`, `[Experimental(\"SKEXP0050\")]`, token-counting APIs removed in the 1.2x→1.7x churn, separator-based only, does NOT preserve code fences; token-accurate sizing needs
  Microsoft.ML.Tokenizers/SharpToken supplied as a delegate). `Microsoft.Extensions.VectorData.Sqlite` does NOT exist (NuGet 404). The SQLite connector that exists is `CommunityToolkit.VectorData.SqliteVec` 1.0.1-preview (2026-07-22):
  vector-only (HybridSearch: No, IsFullTextIndexed: No), model-first schema, EqualTo-only filters, pinned to the STALE official `sqlite-vec` 0.1.7-alpha.2.1. SK vector connectors moved SK-prefixed → `CommunityToolkit.VectorData.*` (GA
  1.0.0, 2026-07-22) after SK commit #14117. Getting TextChunker via SK costs ~7 packages incl. VectorData.Abstractions 10.1.0 (version skew vs current 10.8.x); SK's embedding entry points are passthroughs to MEAI. Verdict:
  `Microsoft.Extensions.AI.Abstractions` + `Microsoft.Extensions.AI.OpenAI` (endpoint override)
    + vendored TextChunker.cs + own SQL — 3–4 packages, ~700–900 LOC, full schema control. Revisit CommunityToolkit.VectorData.SqliteVec only if it goes GA AND hybrid FTS5+vector appears.

## Model distribution: bundling the default model into the dotnet tool package

dotnet tools ARE NuGet packages, so the default embedding model can ship INSIDE the tool (resolved at runtime via `AppContext.BaseDirectory`; NuGet cap 250 MB). all-MiniLM-L6-v2 (Apache-2.0; HF API sizes MEASURED 2026-08): fp32 ONNX **90.4
MB**, mixed O4 45.2 MB, **int8 quantized 23.0 MB** — the same footprint as the 21 MB GGUF Q5_K_M it replaces. TRAP: optimum's published int8 files are CPU-ISA-specific (`model_qint8_arm64/avx512/
avx512_vnni`, `model_quint8_avx2`) — NOT portable. Either bundle per-RID (provisioning complexity returns) or produce ONE portable dynamic-quantized int8 ONNX at build time (onnxruntime quantization tools) and pin SHA-256; fp32 is the
fallback if a retrieval harness shows int8 regression. One package bundling the model is the local-first default; a second no-model flavor (\"NoEmbed\") is a deferred decision, not built proactively — the bundled file is inert unless
`provider=local` is configured.

## Case study: ai-raccoon target design (user `f:` rulings, 2026-08)

Bank topology today: ONE `memory.db` per install scope — user scope `<dataRoot>/memory.db`, project scope `<dataRoot>/.ai-raccoon/memory.db` — contexts partition it, plus a local-only
`raccoon_meta.db` beside it (verified `SqliteConnectionFactory.cs:44-56`). The redesign:

- **R1 (user ruling): ONLY `memory.db`.** Metadata — rating, access_count, last_accessed_at, agent_id provenance, workspace lifecycle, watch registrations, embedding settings — lives in the same schema as entries; `raccoon_meta.db`
  disappears (single connection, single WAL, no cross-DB consistency).
- **R2 (user ruling): the memory layer knows itself and acts on it ("reflection in C#").**
  Self-knowledge = the metadata it already holds: `embed_state` → act with embed_pending / auto-embed on configure; ratings → act with sweep; `workspaces.status` → crash-traceable lifecycle + active-workspace listing; watches → re-register
  after restart; settings → re-embed when provider/model changes. Expose the knowledge via a `memory_inspect`
  self-description tool (schema version, engine, provider/model, counts per scope, pending, active workspaces, watches, last sync).
- **Workspace isolation designed into the model** (git-worktree equivalent): `workspaces`
  table (id, project_id, agent_id, name, created_at, closed_at, status) + `entries.workspace_id`
  nullable FK with a XOR CHECK — `(workspace_id IS NULL AND scope IN ('shared','project','custom'))
  OR (workspace_id IS NOT NULL)`. An entry is committed XOR in exactly one workspace, schema-guaranteed; sync/sweep filters become `WHERE workspace_id IS NULL` (scratch cannot leak); consolidate = promote kept rows (re-scope path, recompute
  path-scoped hash) + delete rest + close workspace, ONE transaction. Mapping: workspace = branch, consolidate =
  `merge --squash`, discard = delete branch. Rejected alternatives: keep the context-string convention (isolation stays a filter convention, no entity for the store to reason about) and physical isolation per workspace (over-engineered,
  kills project+workspace search in one query).
- **Dedup contract in the new schema**: `hash = SHA-256(path + value)`, path-scoped (replaces
  `preserve_duplicate_paths=1`); memory_write dedups globally on value (duplicate content written once); share/consolidate use distinct paths to create real rows.
