# Memory Model Research — Consolidated Synthesis

> Date: 2026-08-04  
> Task: memory-model-research  
> Status: Research complete (no implementation)  
> Sources: LLM Wiki v2 gist (rohitg00/agentmemory), Noba Project human memory article, Microsoft .NET docs, ai-badger repo (arasz/ai-badger v0.77.2)

---

## Research Scope

Four parallel research tracks:

1. **Encryption at rest** — .NET patterns for SQLite-based memory stores
2. **Documentation ingestion** — Pipeline design for job-search-ai-assistant project docs into memory.db
3. **ai-badger stacks & mcp-index** — Architecture and integration potential
4. **Memory model gaps** — Current AiRaccoon model vs. state-of-the-art agent memory

---

## 1. Encryption at Rest

### Recommendation: Transparent SQLite Encryption

**Winner:** SQLite3 Multiple Ciphers (e_sqlite3mc), bundled in `Microsoft.Data.Sqlite` 11.0+.

**Why it beats alternatives for AiRaccoon:**
- Zero code changes to FTS5, vec0, indexes — page-level encryption is transparent
- 5-15% read/write overhead (acceptable for memory store, not OLTP)
- Existing bundles: remove `SQLitePCLRaw.bundle_e_sqlite3`, use the one from Microsoft.Data.Sqlite
- Just set `Password` in connection string

**Key storage pattern:**
```
MacOS Keychain (dev) / X.509 cert (prod)
        ↓
ASP.NET Core Data Protection (wraps passphrase)
        ↓
IEncryptionKeyProvider → SqliteConnectionFactory.Password
        ↓
SQLite3MC encrypts per 4KB page (AES-256-CBC)
```

**What NOT to use:**
- ❌ Windows DPAPI: `PlatformNotSupportedException` on macOS — not viable
- ❌ Per-column AES-GCM: breaks FTS5, vec0, WHERE filtering, ORDER BY — kills search
- ❌ macOS Keychain directly: first access pops GUI dialog (headless), SecKeyChain API not in vanilla .NET

**Migration path:** Opt-in first (env var `AIRACCOON_DB_PASSPHRASE`), `sqlcipher_export` for existing DBs, eventually make default.

**Full report:** `docs/research/encryption-at-rest.md`

---

## 2. Documentation Ingestion Pipeline

### Design for job-search-ai-assistant → memory.db

**Inventory:** ~150-180 documentation files across 10 knowledge categories
- 85 ADRs (Nygard format: Context → Decision → Consequences)
- 6 architecture docs, 22 invariants, 19 skills, 9 agent personas, 11 instruction files
- Domain rules (ATS screening, LinkedIn optimization), reference specs, .remember archive

**Chunking strategy:** Type-aware, not character-count
- ADRs → per-section chunks (5-6 per ADR)
- Architecture docs → per-H2 sections
- Invariants, skills, agents, instructions → one file = one chunk (atomic)
- .remember → per-week entries
- Rules → per-table-row + preamble

**Estimated:** ~770 chunks into 12 typed contexts (docs:adr, docs:architecture, ai-badger:invariants, ai-badger:skills, etc.)

**Test suite:** 43 queries across 8 categories
- Architecture Decisions (7), System Architecture (6), Invariants (6), Skills (4), Domain Rules (3), Project History (3), Cross-Cutting Multi-Context (3), Negative Tests (3)

**Scoring form:** Self-contained HTML with vanilla JS
- Per-query: top-5 results, 1-5 radio score, reason textarea
- Summary panel: per-category averages
- Export/Import baseline JSON + regression diff view
- localStorage persistence

**Full report:** `docs/design/job-search-ai-assistant-ingestion-pipeline.md`

---

## 3. ai-badger Stacks & mcp-index

### Architecture

**Stacks** are technology-domain groupings in the ai-badger framework:
- 19 stacks: angular, aspire, azure, claude, common, copilot, cosmos, css, dotnet, hermes, js, junie, mcp, node, python, react, terraform, ts...
- Common stack holds stack-agnostic features (code-review-graph, hermes MCP servers)
- Each stack has: `skills.json`, `invariants/`, `personas/`, `instructions/`
- Projects declare which stacks apply (e.g., AiRaccoon: dotnet + mcp)
- `welcome-ai-badger` scaffolds the intersection of project stacks + agent support

**mcp-index** is MCP *tool* indexing, not project-knowledge indexing:
- Creates `.ai-badger/mcp-tools.json` — maps every MCP server tool to tags + intent
- Feeds tool recommendations into LLM turns via `pre_llm_call` hook
- Uses a closed tag taxonomy: csharp, typescript, build, diagnostic, search, database...
- Auto-tags by name heuristics when no catalog entry exists (~60% coverage)
- Status: AiRaccoon currently has this active (code-review-graph, glider, playwright, etc.)

### Key ADRs

- **ADR-0010 (Stack-local skill discovery):** How stacks determine which skills ship. A skill can be `default` (ships everywhere), `optIn` (project must declare), or referenced in its parent stack's `skills.json`.
- **ADR-0013 (MCP tool index purpose):** The index solves token waste (80K token prompt with all tool definitions) and tool-selection errors. It does NOT index project documentation or knowledge.
- **ADR-0014 (MCP support is configuration, not retrieval):** MCP server configuration belongs in `.mcp.json`, not in a retrieval system. The AI should use mcp-index for tool discovery, not for documentation recall.

### Integration Opportunity

The user's idea: **Replace/supplement mcp-index with stack-aware memory.**

Currently:
```
Project docs → separate ingestion/indexing → token-heavy prompt inclusion
MCP tools → mcp-index → pre_llm_call hook → inline recommendations
```

Proposed:
```
Project docs → AiRaccoon memory.db (with stack tags) → memory_search on demand
                   ↑
          ai-badger stacks (dotnet, mcp, common) ← tag entries
```

**What this enables:**
- ai-badger scaffolds a project, then queries memory for stack-specific conventions
- "What invariants apply to dotnet projects?" → memory_search(context:"ai-badger:invariants", stack:"dotnet")
- New agent arrives at project → memory loads relevant context, not full docs tree
- Stack changes (add mcp, remove angular) → memory scoping updates automatically

**Feasibility:** The extension pipeline (`IMemoryExtension`) is the integration point. An ai-badger extension could:
1. On scaffold: write stack metadata as memory entries
2. On session start: query memory for stack-appropriate rules/conventions
3. On doc change: auto-reingest modified files

**What changes:** Memory entries need a `stacks` tag field. The ingestion pipeline maps file origin to stacks (config.json's `stacks` array tells which stacks apply). mcp-index doesn't go away — it serves a different purpose (tool selection vs. knowledge recall).

---

## 4. Memory Model Gaps

### What We Have (Solid Foundation)

| Capability | Status |
|---|---|
| Hybrid search (FTS5 + vec0 RRF fusion) | ✅ Production-grade |
| Ebbinghaus decay rating (half-life + access multiplier) | ✅ Aligned with Noba research |
| Sweep/degradation pipeline | ✅ Shared tier exempt |
| Extension pipeline (IMemoryExtension) | ✅ Forward-compatible hooks |
| 3-tier context model (workspace → project → shared) | ✅ Maps to consolidation |
| Content-addressed dedup | ✅ Eliminates dup sync conflicts |
| Single-file VACUUM INTO sync + If-Match CAS | ✅ Simple, works |

### What's Missing (Priority-Ordered)

#### HIGH — Do These First

**1. Confidence Scoring**
- Current: Only `rating` (usage-based, half-life decay). No source multiplicity, no recency of confirmation.
- Missing: `confidence_score`, `source_count`, `last_confirmed_at` columns.
- Impact: ~200 lines of C#. Transforms search from "here's what matched" to "here's what I believe."
- Human memory parallel: Flashbulb memories feel confident but aren't accurate. Agent memory should track *objective* confidence basis.

**2. Knowledge Graph (Typed Relationships)**
- Current: Zero graph edges. Search is similarity-only (FTS5 + vec0).
- Missing: `relationships` table with typed edges (uses, depends_on, contradicts, supersedes, fixes). Entity extraction on write.
- Impact: "What's impacted by upgrading Redis?" → graph traversal finds downstream dependents that don't mention "Redis."
- This is the biggest conceptual gap. Everything downstream (graph search, contradiction resolution, crystallization) depends on it.

**3. Graph Traversal Search (Third Stream)**
- Current: 2-stream RRF (FTS5 + vector). RRF infrastructure already supports N streams.
- Missing: Graph traversal as third modality. `graphWeight` parameter.
- Depends on: Knowledge graph (item 2).

#### MEDIUM — Do After Foundation

**4. Supersession**
- Current: No explicit "B supersedes A" link. New writes don't weaken old claims.
- Missing: `superseded_by` hash column, `superseded_at` timestamp, search filter.
- Depends on: Confidence scoring (needs it for resolution).

**5. Quality Scoring**
- Current: No content quality assessment on write.
- Missing: `quality_score` (LLM self-evaluation: structure, citations, consistency). Below-threshold → flagged.
- Impact: Gates what becomes "established knowledge."

**6. Automation Hooks**
- Current: Extension pipeline exists but only used for `RetrievalRatingExtension`.
- Missing: Session-start context injection, session-end compression, auto-lint, scheduled ops.
- The hook architecture is done — the logic isn't.

**7. Contradiction Detection**
- Current: No detection of conflicting claims.
- Missing: On-write extension that searches for semantically similar but factually conflicting entries.
- Depends on: Knowledge graph + confidence scoring.

#### LOW — Compound Interest at Scale

**8. Per-Class Retention Curves**
- Current: Single half-life (30 days) for everything.
- Missing: Decay classes (transient=7d, observation=30d, decision=90d, architecture=365d, permanent=never).
- Small change: extend existing `ttl_days` to `half_life_days` per-entry.

**9. Crystallization (Compounding from Exploration)**
- Current: Workspace consolidation promotes raw entries, not structured digests.
- Missing: Digest generator (title, question, findings, files, lessons). Lesson extraction. Confidence reinforcement.
- Depends on: Confidence, graph, quality scoring.

**10. Multi-Agent Mesh Sync**
- Current: Single-file cloud sync works for one-agent-per-install.
- Missing: Per-agent private scoping, work coordination, peer-to-peer mesh.
- LLM Wiki v2 rates this as Tier 5 (last). Important for teams, not for current single-agent primary use case.

### Implementation Roadmap

```
Phase 1 (Wave F-G): Confidence scoring (1) + Quality scoring (5)
       └─ Foundation: everything builds on knowing what's reliable

Phase 2 (Wave H): Knowledge graph basics (2) + Supersession (4)
       └─ Typed edges: supersedes, depends_on, uses. Entity table.

Phase 3 (Wave I): Graph traversal search (3) + Per-class retention (8)
       └─ Third RRF stream. Classify decay rates.

Phase 4 (Wave J): Automation hooks (6) + Contradiction detection (7)
       └─ Session-start injection. Auto-detect conflicts.

Phase 5 (Wave K): Crystallization (9) + Lint/self-healing
       └─ Digest generation. Auto-repair.

Phase 6 (Wave L): Multi-agent mesh (10) + 4-tier consolidation
       └─ Team-scale features.
```

**Full report:** `docs/work/2026-08-04-memory-model-gap-analysis.md`

---

## 5. The Memory Graph Concept

### What the user described

> "We are using content and meaning similarity now, but we don't have connections."
> "Documents with links are easy (links - edges - connections)."
> "We could use relations like remembered-with (we will connect all results returned together)."

### How it maps to the research

The LLM Wiki v2's "typed relationships" is exactly this concept. The Noba article's "encoding" phase describes how human memory forms associations — we encode new information by relating it to what we already know. The more associations (edges), the more retrieval paths.

**Concrete shape for AiRaccoon:**

```sql
-- New: relationships table
CREATE TABLE relationships (
    source_hash TEXT NOT NULL REFERENCES entries(hash),
    target_hash TEXT NOT NULL REFERENCES entries(hash),
    relationship_type TEXT NOT NULL,  -- 'uses', 'depends_on', 'contradicts', 'supersedes', 'fixes', 'remembers_with'
    confidence REAL DEFAULT 1.0,
    created_at INTEGER NOT NULL,
    agent_id TEXT,
    PRIMARY KEY (source_hash, target_hash, relationship_type)
);
```

**"remembered-with" relationship:** When two entries are returned together in the same query result, or written in the same session, they get an auto-generated `remembers_with` edge with a decaying confidence. This is implicit relationship mining — the system learns connections from co-occurrence, not just explicit declaration.

### What it gives us

1. **Structural search:** "What depends on X?" → graph walk, not keyword match
2. **Contradiction detection:** Two entries about same entity with different values → `contradicts` edge
3. **Knowledge lineage:** Entry → superseded_by → superseded_by → ... → original claim
4. **Serendipitous discovery:** Graph traversal finds connections similarity-search misses
5. **Co-occurrence memory:** "These facts were always retrieved together" → `remembers_with` edges

---

## 6. Stacks-Aware Memory Integration Proposal

### The idea

Currently, ai-badger's `config.json` declares project stacks. `welcome-ai-badger` scaffolds stack-specific skills, invariants, and personas. But there's no runtime link between "this project uses the dotnet stack" and "here are the dotnet conventions in memory."

**Proposal:** When AiRaccoon ingests documentation, tag each entry with the stacks it belongs to. When ai-badger needs to recall conventions, it queries memory by stack.

### Implementation shape

```csharp
// MemoryEntry gains a stacks field
public sealed record MemoryEntry(
    string Hash, string Path, string Context, string Value, 
    long CreatedAt,
    IReadOnlyList<string>? Stacks = null  // NEW: ["dotnet", "mcp"]
);

// SearchQuery gains a stack filter
public SearchQuery(
    ...
    IReadOnlyList<string>? Stacks = null  // NEW: search only dotnet-tagged entries
);
```

### Extension hook for ai-badger integration

A new `AiBadgerIntegrationExtension : IMemoryExtension`:
- `OnWrite`: If content source is from `.ai-badger/`, auto-tag with stacks from `config.json`
- `OnSearch`: Boost results matching the querying project's stacks
- `OnSessionStart` (new hook): Load top-N entries matching project stacks as context

### What replaces what

| Before | After |
|---|---|
| mcp-index for tool discovery | Still mcp-index (separate concern) |
| Separate doc indexes per stack | Memory banks tagged by stack |
| Agents read full CLAUDE.md | Agents query memory for stack-relevant facts |
| Stack conventions in files only | Stack conventions in searchable memory |
| "What invariants apply?" → grep | "What invariants apply?" → memory_search(stacks:["dotnet"]) |

The mcp-index doesn't go away — it solves tool selection, which is a different problem from knowledge recall. But documentation that's currently only in files becomes queryable through memory, with stack awareness making results relevant to the current project's technology choices.

---

## Research Deliverables

| Document | Location |
|---|---|
| Encryption at rest report | `docs/research/encryption-at-rest.md` |
| Doc ingestion pipeline design | `docs/design/job-search-ai-assistant-ingestion-pipeline.md` |
| Memory model gap analysis | `docs/work/2026-08-04-memory-model-gap-analysis.md` |
| This synthesis | `docs/work/2026-08-04-memory-model-research-synthesis.md` |

## Source Materials

| Source | Type |
|---|---|
| LLM Wiki v2 gist (rohitg00) | Retrieved via curl from gist.githubusercontent.com |
| Noba Project — Memory (Encoding, Storage, Retrieval) | Retrieved via curl from nobaproject.com |
| ai-badger repo (arasz/ai-badger) | Explored via browser + curl on GitHub |
| Microsoft .NET docs | Searched via web_search + microsoft-docs MCP |
| AiRaccoon codebase | Read directly from source |
| job-search-ai-assistant project | Enumerated via terminal + read_file |

---

## Next Steps

This task is **research complete**. The user said "research-before-implementation" — implementation should be a separate task. The logical next tasks are:

1. **Confidence scoring + quality scoring** (Phase 1 from gap analysis) — the highest-value, lowest-effort additions
2. **Knowledge graph schema** — `relationships` + `entities` tables, typed edges
3. **Encryption at rest** — transparent SQLite encryption with Data Protection key wrapping
4. **Doc ingestion test suite** — implement the pipeline designed in this research
5. **Stacks-aware memory** — extension that tags entries by ai-badger stack and enables stack-filtered search
