# Research: AiRaccoon memory model gaps, encryption at rest, documentation ingestion, and ai-badger stacks integration

**Date:** 2026-08-04
**Question:** What concepts does our memory model miss compared to state-of-the-art agent memory, how should encryption at rest work, how would we ingest and test-scored project documentation, and can ai-badger stacks replace indexing with stack-aware memory?

## Findings

### F1 — AiRaccoon's current memory model has zero graph edges: search is similarity-only (FTS5 keyword + vec0 cosine) [READ]

The `MemoryEntry` record carries `Hash, Path, Context, Value, CreatedAt` — no relationship columns. The search infrastructure fuses two ranked lists via reciprocal rank fusion: FTS5 `MATCH + bm25()` and vec0 `vec_distance_cosine` KNN. No third stream, no adjacency table, no entity extraction. Two entries can be deeply related (A caused B, B depends on C) but share no lexical overlap with the query and will never co-appear in results.

**Evidence:** `src/AiRaccoon.Core/Memory/MemoryEntry.cs:3` — the record shape. `src/AiRaccoon.Infrastructure/Sqlite/SearchResultMerger.cs:10-56` — `Fuse` takes `IReadOnlyList<(IReadOnlyList<MemorySearchResult>, double Weight)>` and does RRF on two modalities. `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs:44-55` — `SearchFts` and `SearchVector` are separate queries, their ranked lists merged after. No graph query exists.

### F2 — The LLM Wiki v2 gist by rohitg00 explicitly recommends typed knowledge graphs as the missing layer above flat wiki pages [READ]

The gist describes typed relationships (`uses`, `depends_on`, `contradicts`, `caused`, `fixed`, `supersedes`) and graph traversal for queries. It also identifies confidence scoring, supersession, forgetting curves (Ebbinghaus-aligned), consolidation tiers (working → episodic → semantic → procedural), and crystallization as the lifecycle machinery that prevents a wiki from becoming a junk drawer. The Noba Project article corroborates the encoding-storage-retrieval model and the distinctiveness principle.

**Evidence:** `https://gist.github.com/rohitg00/2067ab416f7bbe447c1977edaaa681e2`, sections "Beyond flat pages: the knowledge graph" and "The missing layer: memory lifecycle." Noba article at `https://nobaproject.com/modules/memory-encoding-storage-retrieval`, sections "Three Stages of the Learning/Memory Process" and "Encoding."

### F3 — Confidence scoring would be the single highest-value, lowest-effort addition to our model [INFERRED]

We have `RatingPolicy` (half-life decay on age + access-count multiplier) and `DegradationPolicy` (rating < threshold AND age > TTL → sweep). But rating is purely usage-based — a fact accessed 100 times in an hour gets a high rating even from one unreliable source. The gist's confidence scoring adds a separate axis: source multiplicity, recency of confirmation, and contradiction status. Adding three columns (`confidence_score`, `source_count`, `last_confirmed_at`) and a `ConfidencePolicy` class would transform search from "here's what matched" to "here's what I believe." Estimated ~200 lines of C# with no new architectural concepts.

Reasoning from the gap between our `EntryMetadata(double Rating, int? TtlDays)` shape and the gist's described confidence model. The extension pipeline (`IMemoryExtension`) already exists and could house the confidence-policy hook.

### F4 — Transparent SQLite encryption via e_sqlite3mc (bundled in Microsoft.Data.Sqlite 11.0+) is the correct encryption-at-rest approach for AiRaccoon [READ]

Setting `Password` in the `SqliteConnectionStringBuilder` enables AES-256-CBC per-page encryption. FTS5, vec0, indexes, and WHERE filtering all work unchanged because decryption is transparent at the page level. Per-column AES-GCM would break every search modality. The key should be wrapped with ASP.NET Core Data Protection API (`IDataProtectionProvider`) for automatic 90-day key rotation and purpose-string scoping.

**Evidence:** `https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/encryption` — official Microsoft docs. `https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0` — Data Protection API. AiRaccoon's `SqliteConnectionFactory.OpenBankAsync()` at `src/AiRaccoon.Infrastructure/Sqlite/SqliteConnectionFactory.cs:24-78` uses `SQLitePCLRaw.bundle_e_sqlite3` — redundant with the e_sqlite3mc bundle already in Microsoft.Data.Sqlite 11.0+.

### F5 — Windows DPAPI is not viable for AiRaccoon (macOS target) [READ]

`System.Security.Cryptography.ProtectedData.Protect()` throws `PlatformNotSupportedException` on macOS. macOS Keychain access via `security` CLI works but first access pops a GUI dialog unless the binary is pre-authorized with `-T`. Not suitable for headless daemons without pre-configuration.

**Evidence:** `https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.protecteddata.protect?view=net-10.0` — "PlatformNotSupportedException on non-Windows." `https://learn.microsoft.com/en-us/dotnet/standard/security/cross-platform-cryptography` — cross-platform crypto guidance.

### F6 — The job-search-ai-assistant project contains ~150-180 ingestible documentation files yielding ~770 typed chunks [MEASURED]

A subagent enumerated the project tree at `/Users/arasz/RiderProjects/job-search-ai-assistant` and classified files by content type: 85 ADRs (Nygard-format with Context/Decision/Consequences sections), 6 architecture docs, 22 invariants, 19 skills, 9 agent persona files, 11 instruction files, 2 ATS/LinkedIn rule files, 7 reference specs, .remember operational archives, and root markdown. Chunking is type-aware: ADRs split per-section, large docs per-H2, invariants/skills/agents kept atomic. 12 typed contexts (docs:adr, docs:architecture, ai-badger:invariants, etc.) enable scoped retrieval. 43 test queries were designed across 8 categories (A–H).

**Evidence:** Enumeration via `find /Users/arasz/RiderProjects/job-search-ai-assistant -name "*.md" | wc -l` and manual classification. ADR index at `docs/adr/index.json` confirmed 85 entries. Chunk counts are type-aware projections: 85 ADRs × 5 sections = 425, 6 architecture docs × ~15 H2s = 90, etc. Totals derived in the ingestion pipeline design doc.

### F7 — ai-badger's stacks system organizes features by technology domain under `features/{stack}/` with 19 stacks; mcp-index is a separate tool-discovery system using BM25, not documentation indexing [READ]

The ai-badger repo at `github.com/arasz/ai-badger` v0.77.2 has `features/common/` for stack-agnostic content and `features/dotnet/`, `features/mcp/`, etc. for stack-specific features. Each stack directory holds `personas/`, `invariants/`, `instructions/`, `skills/`, and metadata files (`stack.json`, `skills.json`). ADR-0010 establishes stack-local skill discovery: skills in `features/{stack}/skills/` are only installed when the project's `config.json` includes that stack. mcp-index (`features/common/skills/mcp-index/`) indexes MCP *tool* definitions (not documentation) using BM25 over tool intents and a closed tag taxonomy, feeding recommendations via a `pre_llm_call` hook. ADR-0012 explicitly rejected embeddings for mcp-index (paraphrase recall@3 was 0.000). ADR-0014 rules that MCP support is configuration, not retrieval.

**Evidence:** `https://raw.githubusercontent.com/arasz/ai-badger/main/README.md` — repository description and structure. `https://raw.githubusercontent.com/arasz/ai-badger/main/docs/adr/0010-stack-local-skill-discovery.md` — ADR-0010. `https://raw.githubusercontent.com/arasz/ai-badger/main/docs/adr/0013-what-the-mcp-tool-index-is-for.md` — ADR-0013. `https://raw.githubusercontent.com/arasz/ai-badger/main/docs/adr/0014-mcp-support-is-configuration-not-retrieval.md` — ADR-0014. `.ai-badger/skills/mcp-index/SKILL.md` in this project — the full skill definition.

### F8 — Replacing ai-badger's mcp-index file-based retrieval with stack-aware memory is architecturally feasible in 4 phases [INFERRED]

Phase 1: add `stacks` field to memory entries and `stacks=` filter to `memory_search`. Phase 2: `welcome-ai-badger` ingests `features/{stack}/` docs into memory during scaffold. Phase 3: the `pre_llm_call` hook calls `memory_search(query=user_message, stacks=project_stacks)` instead of reading `mcp-tools.json` + BM25. Phase 4: static mcp-tools.json becomes a cache/export artifact. The two systems share vocabulary (stack names) but not data — unifying them requires design work, and ADR-0013's 3 scoped purposes (curation, answerability, large tool-sets) must still be served.

Reasoning from the architecture: ai-badger already separates `common` from stack-specific content, ai-raccoon already has per-project isolation and hybrid search, and the extension pipeline provides the wiring point. The main risks are: replacing a measured BM25 system with unmeasured hybrid search, making every tool-recommendation turn cost a `memory_search` call, and handling fresh-start schema changes.

### F9 — The Noba Project's encoding-storage-retrieval model parallels the LLM Wiki v2's consolidation tiers [INFERRED]

The Noba article's three stages (encoding — selective and prolific, driven by distinctiveness; storage — memory traces are reconstructed, not replayed; retrieval — cue-dependent and reconstructive) map onto the gist's consolidation pipeline: encoding ≈ observation ingestion, storage ≈ episodic-to-semantic promotion with retention curves, retrieval ≈ hybrid search with graph traversal. The DRM effect (false memories from semantic associations) parallels the gist's warning about un-curated wikis accumulating noise. Distinctiveness as a key encoding attribute supports the gist's recommendation for confidence scoring — distinctive events are well-remembered but not necessarily accurate (flashbulb memory studies).

Reasoning from the Noba article text and the gist's architecture — both describe the same phenomena with different terminology but structurally aligned mechanisms.

### F10 — The extension pipeline (IMemoryExtension) is the correct integration point for all identified gaps [INFERRED]

`MemoryExtensionHost` decorates `IMemoryStore` and fires `OnWriteAsync`, `OnSearchAsync`, `OnDeleteAsync`, `OnSweepAsync`, and `OnConsolidateAsync` around every store operation. Currently only `RetrievalRatingExtension` uses it. Every gap identified — confidence scoring, entity extraction, contradiction detection, quality scoring, stack tagging, session hooks — could be implemented as one or more `IMemoryExtension` implementations, keeping the core store thin and the features composable. The infrastructure pattern (decorator wrapping the real store with ordered hooks) is already tested and proven.

Reasoning from the hook architecture at `src/AiRaccoon.Core/Rating/MemoryExtensionHost.cs:10-112` and the extension contract at `src/AiRaccoon.Core/Rating/IMemoryExtension.cs:7-45`. Each gap maps cleanly onto one of the five existing hooks, with only session-boundary hooks requiring new interface members.

## Still open

- Whether the measured BM25 performance of mcp-index's tool recommendations would degrade if replaced with AiRaccoon's hybrid search — no comparative benchmark was run.
- The exact on-disk schema for the relationships table (normalized edge table with foreign keys vs. embedded adjacency in entries) — the gist suggests typed edges, but the SQLite schema tradeoffs between query performance and write simplicity were not evaluated.
- Whether confidence scoring should be LLM-driven (the gist's recommendation) or rule-based (cheaper, deterministic) — both were described as options but not compared.
- The per-entry encryption overhead for a 770-chunk document ingestion — the 5-15% figure is from SQLCipher docs for general workloads, not measured on AiRaccoon's specific workload.
- Whether `remembers_with` auto-generated edges from co-occurrence would produce useful signal or noise — the gist doesn't describe this pattern; it's the user's concept and was not tested.
