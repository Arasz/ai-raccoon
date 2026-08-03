# native-memory-store — implementation plan (rev 2)

> Task: native-memory-store · Spec: `docs/features/native-memory/spec.json` + `native-memory.feature`
> Gate: owner-gate review 2026-08-03 (15/15 approve; f: refinements — access modes ro/rw/full, bundled model, watcher to part 2)
> Plan review: 2026-08-03 — MUST-FIX M1-M4 + SHOULD-FIX S1-S6 incorporated (rev 2)
> Status: APPROVED — Wave A in progress

## 1. Goal

Replace the pinned sqlite-memory native extension with our own .NET layer on
SQLite + FTS5 + sqlite-vec, so that: the embedding provider is pluggable (any
OpenAI-compatible endpoint, default = bundled in-process ONNX model), the bank is
one self-describing `memory.db`, workspaces are structurally isolated, access is
mode-gated (ro/rw/full), and sync is our own single-file row-merge over
S3-compatible storage. All 17 MCP tools keep their names.

## 2. Before / after

### 2.1 Data flow

```
BEFORE (extension-owned)                              AFTER (managed)

 MCP tools (17)                                       MCP tools (17)  [gated by access mode]
   |                                                     |
   v                                                     v
 SqliteMemoryStore (thin SQL)                          MemoryTools / WorkspaceService
   |  memory_add_text / memory_search VT                  |  (domain policy stays)
   v                                                     v
 sqlite-memory 1.3.5  ── llama.cpp (GGUF)             IMemoryStore (managed impl)
   |                    vectors.space (HARDCODED)        |  write / search / share / delete
   v                                                     v
 dbmem_content/vault/vault_fts (fixed schema)         memory.db  ── OUR schema
 + sqlite-vector 1.0.0                                    |  entries + metadata columns
 + sqlite-sync 1.1.2 (cloudsync, CRDT)                   |  workspaces (FK + XOR CHECK)
                                                         |  settings / sync_meta / tombstones
 raccoon_meta.db (rating, workspaces)                    |  fts5 (external content) + vec0
   |                                                     |
   v                                                     v
 ExtensionProvisioner (download 3 natives)            IEmbeddingGenerator (MEAI.OpenAI)
                                                         |  default: bundled int8 ONNX
                                                         |  alt: any OpenAI-compatible baseUrl
                                                         v
                                                      ICloudStore (S3-compatible)
                                                         |  VACUUM INTO + If-Match CAS
                                                         |  row merge (updated_at LWW + tombstones)
```

### 2.2 Schema

```
BEFORE                              AFTER (single memory.db)
dbmem_content (fixed, extension)    entries(id PK, hash, path, value, scope,
  hash/path/context/value/...         project_id, context_label, workspace_id NULL FK,
                                      agent_id, created_at, updated_at,
                                      access_count, last_accessed_at, rating,
                                      ttl_days, embed_state, embedding)
raccoon_meta.db                     workspaces(id PK, project_id, agent_id, name,
  entries(rating...)                  created_at, closed_at, status)
  workspaces(status...)             settings(key PK, value)   ← access modes live here
                                    sync_meta(key PK, value)
                                    sync_tombstones(hash, scope, deleted_at)
                                    CHECK (workspace_id IS NULL
                                           AND scope IN ('shared','project','custom'))
                                        OR workspace_id IS NOT NULL
```

## 3. Architecture

- **Core (domain, no infra deps):** IMemoryStore port (unchanged shape), AccessMode
  policy (ro/rw/full resolution + tool gating rules), chunking **contract** (deterministic,
  token-accurate, fence-aware; TokenCounter delegate injected — keeps Core pure),
  dedup/hash policy (path-scoped SHA-256), RRF fusion policy (k + weights),
  rating/degradation policies (rating math unchanged; storage moves on-row).
- **Infrastructure:** own schema init + WAL policy; managed SqliteMemoryStore (write/search/
  share/delete over our tables); FTS5 + vec0 (NuGet natives, LoadVector); EmbeddingService
  over IEmbeddingGenerator (bundled ONNX default, configurable provider/baseUrl/model);
  chunker implementation (Microsoft.ML.Tokenizers + fence handling); SyncService over
  ICloudStore (S3-compatible) with snapshot/merge/tombstones; ExtensionProvisioner deleted
  in P10 (reference binaries stay as harness test assets, not runtime deps).
- **MCP (thin):** 17 tools unchanged in name; memory_configure gains baseUrl; access-mode
  gating at the tool boundary (ro: reads only; rw (default): +writes; full: +deletes,
  sweep-real, forgetting knobs).

## 4. Waves (rev 2 — review fixes incorporated)

Serial dependencies: **P1 → everything; P4 → P6; P8 → P9; P9 → P10; P7 gates P10**
(harness reference assets are vendored, so P10's runtime removal does not break the harness).

| Wave | Packages | Parallel? | Acceptance gate |
|---|---|---|---|
| A | **P1** schema + single-DB: new memory.db schema; `SqliteConnectionFactory` single file + vec0/FTS5 loading; **MetaStore + SqliteWorkspaceStore repointed into memory.db** (FR-NM-1 s1-s3); rating pipeline rewired to on-row columns (MemoryExtensionHost/RetrievalRatingExtension); **store-test rewrite** (SqliteMemoryStoreTests dbmem_content assertions + SearchResultMerger unit tests); **P5** chunking (FR-NM-10); **P7** harness scaffolding (FR-NM-5: vendored reference assets + corpus + metrics, fail-not-skip) | P5, P7 parallel; **P1 serial after** (shared obj/ build dir + its integration tail) | bank is one file (no raccoon_meta.db); workspace begin writes Active row in memory.db; rating bump lands on-row; chunk scenarios green; harness reference runner produces golden output on osx-arm64 and **fails (not skips)** when assets missing |
| B | **P2** access modes (FR-NM-2, config in settings table, per-project + global); **P3** content identity/dedup (FR-NM-7); **P4** embeddings + bundled model (FR-NM-3) | P2 parallel; P3→P4 serial (shared write path) | 8 mode scenarios; 3 dedup scenarios; 6 embedding scenarios (bundled model asset defined: committed artifact or pinned script, CI-reachable; tests fail-not-skip) |
| C | **P6** hybrid search RRF (FR-NM-4) incl. **SearchResultMerger RRF rework** | single | 4 search scenarios; harness parity nDCG Δ≤0.02 vs vendored reference (GGUF) on the shared corpus |
| D | **P8** workspaces structural (FR-NM-6 s1-s3; **s4 'sync/sweep exclude workspace rows' moves to P9's acceptance** — needs the new sync) | single | 3 workspace scenarios (begin/Active, XOR isolation, one-transaction consolidate); P8 schema + FK + CHECK in place |
| E | **P9** own sync (FR-NM-8 all 6 + FR-NM-6 s4; per-project SemaphoreSlim + lockfile; merged-rows-reindexed scenario) → then **P10** provisioning removal + tool parity (FR-NM-9: no download-on-first-run, 17 tools listed, baseUrl config; `.mcp/server.json` env list updated; `memory_sync` intact because P9 already replaced it) | P9 then P10 serial (P9 first — no sync gap) | 6 sync scenarios (MinIO-backed integration or fake ICloudStore + real merge) + workspace-exclusion scenario; P10: no sqliteai runtime download; harness still green (vendored assets) |
| F | **P11** docs (reference, env vars incl. AIRACCOON_SYNC_*, README, feature table; note fresh-start consequence + 17-tools wording nuance) + full `dotnet build`/`dotnet test` + merge | single | full suite green, build 0 warnings; docs match code |

Each package: TDD RED→GREEN (failing scenario test first), small commits, `dotnet build` +
targeted tests after each step; full suite at wave ends. P10 (runtime removal of the pinned
extension) fires only after P7's harness passes AND P9's sync is in.

## 5. Key decisions carried from the gate (do not re-litigate)

- RRF with swept k/weights; ranking normalized to 0..1 (contract preserved).
- hash = SHA-256(path + value); add_text global dedup; share/consolidate path-scoped rows.
- Workspaces: FK + XOR CHECK; consolidate/discard one transaction; sync/sweep filter
  `workspace_id IS NULL` structurally.
- Sync: S3-compatible, VACUUM INTO + quick_check + If-Match CAS + row merge; tombstones;
  telemetry columns excluded from merge; workspace rows never sync.
- Model: bundled portable int8 ONNX (~23MB, SHA-256 pinned) in the tool package; custom
  path overrides; NoEmbed flavor deferred.
- Access modes: ro / rw (default) / full; per-project + global default, stored in settings.
- No Semantic Kernel, no sqliteai natives at runtime, no CommunityToolkit.VectorData.SqliteVec.
- Watcher + memory_inspect + telemetry: part 2 (not in this task).

## 6. Risks (rev 2 — harness and model risks added)

| Risk | Mitigation |
|---|---|
| sqlite-vec pre-v1 | pin version; re-run harness on upgrade |
| linux-musl gap (vec0/ONNX) | verify at build; fail clearly if musl needed |
| RRF k corpus-dependent | harness sweeps k×weights |
| Engine change → full re-embed | settings table records engine; one-time re-embed on change |
| Delete propagation (tombstone bugs) | dedicated unit tests; GC below min(last_pull watermark) |
| int8 ONNX retrieval regression | harness compares int8 vs fp32; fp32 fallback |
| **Cross-model parity bet** (reference = GGUF via llama.cpp vs new = int8 ONNX — same all-MiniLM family, different engines) | harness pins the reference model file (SHA-256) and the corpus/graded set; nDCG gate measured against vendored reference output |
| Harness false-green (skip-when-absent) | gate tests FAIL when assets missing — no skips |
| Concurrent builds in one worktree | Wave A runs P5/P7 parallel, P1 serial; later waves are single-package |

## 7. File map (rev 2 — full inventory)

- **P1 (shared, serial):** `SqliteConnectionFactory.cs` (single file, vec0/FTS5 load,
  settings-based options), `SqliteMemoryStore.cs` + `MemorySql.cs` (rewritten over our
  schema), `MetaStore.cs` → on-row columns, `SqliteWorkspaceStore.cs` → memory.db
  workspaces table, `Core/Rating/*` (rating storage rewire), `tests/Store/*` (rewrite
  dbmem_content assertions), `SearchResultMerger.cs` unit tests move to P6.
- **P5 (new files):** `Core/Chunking/IChunker.cs` + pure splitter (TokenCounter delegate),
  `Infrastructure/Chunking/TokenizerChunker.cs` (Microsoft.ML.Tokenizers + fence handling),
  `tests/Chunking/*`. Adds `Directory.Packages.props` pins: Microsoft.ML.Tokenizers 2.0.0,
  Microsoft.ML.Tokenizers.Data.O200kBase.
- **P7 (new files):** `tests/Retrieval/Harness*` (metrics: nDCG@10/20, MRR, Recall@k,
  Kendall-τ; sweep runner k×weights), reference-asset bootstrap (pinned sqlite-memory
  1.3.5 full + sqlite-vector 1.0.0 + reference GGUF model, SHA-256, reusing
  ExtensionCatalog patterns), corpus + graded query set (reuse benchmarks/
  AiRaccoon.Benchmarks corpus + generator if present, else a fixed asset).
- **P2:** `Core/Access/AccessMode.cs` + resolution, `MemoryTools.cs` gating, settings-table
  keys. **P3:** write/share/consolidate hash paths in `SqliteMemoryStore.cs`. **P4:**
  `Infrastructure/Embedding/EmbeddingService.cs`, bundled-model asset + packaging,
  `MemoryTools.cs` configure(baseUrl). **P6:** `SqliteMemoryStore.SearchAsync`,
  `SearchResultMerger.cs` RRF. **P8:** schema FK/CHECK, `WorkspaceService.cs`,
  `SearchContexts.cs` structural filters. **P9:** `Sync/ICloudStore.cs` + S3 impl,
  `Sync/SyncService.cs` rewrite, `sync_meta`/`sync_tombstones` merge. **P10:**
  `Provisioning/*` deletion, `Dependencies.cs` + `Program.cs` DI, `.mcp/server.json`
  env list, `SqliteConnectionFactory.cs` extension-load removal. **P11:** `docs/reference/
  agent-memory-server.md`, `src/AiRaccoon/README.md`, `docs/features/README.md`.
- DI wiring (`Dependencies.cs`, `Program.cs`) touched in P4 (embedding), P9 (sync), P10 (provisioner removal).
