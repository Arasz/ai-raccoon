# Memory Model Gap Analysis: AiRaccoon vs. State-of-the-Art Agent Memory

> Date: 2026-08-04
> Sources: LLM Wiki v2 gist (rohitg00, based on agentmemory patterns) + Noba Project — Memory (Encoding, Storage, Retrieval) by McDermott & Roediger
> Context: AiRaccoon — .NET 10 MCP server over sqlite-memory, currently in Wave A of native-memory-store plan

## Current System Baseline (What We Have)

| Capability | Implementation |
|---|---|
| Write model | `MemoryWriteRequest`: projectId, content, context, agentId, workspaceId |
| Search | Hybrid: FTS5 (BM25) + vec0 cosine KNN → RRF fusion with configurable k + weights |
| Degradation | `RatingPolicy` (half-life decay on age + access-count multiplier) + `DegradationPolicy` (rating < threshold AND age > TTL) → `SweepService` hard-deletes stale entries |
| Rating | On-row `rating` column bumped by `RetrievalRatingExtension` on every search |
| Forgetting knobs | TTL days (default 30), sweep threshold (default 0.3), per-entry TTL overrides; shared context is sweep-exempt |
| Dedup | Content-addressed: SHA-256(path + value); global content dedup via `SelectCommittedByValue` |
| Extension pipeline | `MemoryExtensionHost` + `IMemoryExtension` hooks: OnWrite, OnSearch, OnDelete, OnSweep, OnConsolidate |
| Workspace isolation | FK + XOR CHECK; workspace rows excluded from sync/sweep; consolidate promotes → project |
| Sync | Single-file row-merge over S3-compatible storage: VACUUM INTO snapshot, If-Match CAS, row-level LWW, tombstones |
| Cloud | SQLite Cloud integration via `CloudSyncConnection` |
| Embedding | Pluggable: bundled int8 ONNX (all-MiniLM-L6-v2) or any OpenAI-compatible endpoint; engine-change re-embed |

---

## 1. Memory Lifecycle

### 1a. Confidence Scoring

**Present?** **NO.** We have `rating` (half-life decay × access multiplier) but no *confidence score*.

The LLM Wiki v2 distinguishes:
> "Every fact in the wiki should carry a confidence score: how many sources support it, how recently it was confirmed, whether anything contradicts it."

Our `rating` is purely usage-based. A fact accessed 100 times in an hour gets a high rating even if it came from a single unreliable source. Confidence should be a separate axis:
- **Source multiplicity**: How many independent observations support this claim?
- **Recency of confirmation**: When was it last verified?
- **Contradiction status**: Is anything in the bank disputing it?

**Noba connection**: Human memory confidence ≠ accuracy (flashbulb memory studies: Talarico & Rubin 2003). Encoding distinctiveness and emotional salience drive subjective confidence but not necessarily objective accuracy. An agent memory should *track* the objective basis of confidence, not just usage popularity.

**What it would take to add (MEDIUM):**
- New on-row columns: `confidence_score` (0.0–1.0), `source_count` (int), `last_confirmed_at` (unix seconds), `contradicted_by` (nullable text — hash of contradictory entry).
- A `ConfidencePolicy` parallel to `RatingPolicy`: confidence = f(source_count, days_since_confirmation, has_contradiction).
- Write-time enrichment: when `context` or `agentId` repeat across writes with similar content, auto-increment `source_count`.
- Search-result annotation: surface confidence alongside ranking, so the caller can say "I'm 0.85 sure about X but only 0.3 about Y."

**Priority: HIGH.** This is the single most impactful concept in the LLM Wiki v2 after hybrid search (which we already have). It's what separates "I found this" from "I believe this."

### 1b. Supersession

**Present?** **NO.** We can delete old entries and write new ones, but there's no explicit "entry B supersedes entry A" link.

The LLM Wiki v2 says:
> "When new information contradicts or updates an existing claim, the old claim shouldn't just sit there with a note. The new one should explicitly supersede it. Linked, timestamped, old version preserved but marked stale."

We have content-addressable dedup, which *prevents* duplicate writes, but not version chains. If you discover "Project X uses PostgreSQL now, not Redis," we just write a new entry. The old one stays with its old rating, and both appear in search.

**What it would take to add (MEDIUM):**
- New columns: `superseded_by` (nullable text — hash), `superseded_at` (unix seconds).
- Write-time check: when an entry is created that's semantically close to an existing one, the extension pipeline could flag it as a potential supersession.
- Search filter: `superseded_by IS NULL` as default; `include_superseded=true` opt-in.
- Degradation integration: superseded entries drop rating by factor 0.1 or are sweep-eligible immediately if TTL_override is applied.

**Priority: MEDIUM.** Confidence scoring should come first (supersession needs it). Together they form the knowledge lifecycle backbone.

### 1c. Forgetting / Retention Curves

**Present?** **PARTIAL.** We have half-life decay rating (`RatingPolicy`) + TTL + sweep, which is a retention curve. But it's one-size-fits-all.

We have:
- `RatingPolicy`: `rating = baseScore * 0.5^(ageDays/halfLifeDays) * (1 + accessCount * accessMultiplier)` — this IS a forgetting curve (Ebbinghaus-style exponential decay + reinforcement reset).
- `DegradationPolicy`: `rating < threshold && age > TTL` — binary sweep gate.
- Shared context exempt from sweep.

What's missing per the LLM Wiki v2:
> "Architecture decisions decay slowly. Transient bugs decay fast."

We have no **decay-rate classification**. Everything decays at `DefaultHalfLifeDays = 30`. An architecture decision and a one-off error message both have the same half-life.

**Noba connection**: The DRM effect (Deese-Roediger-McDermott) shows that semantic associations can create false memories that *feel* as strong as real ones. Retroactive interference (newer memories disrupting older ones) mirrors what the LLM Wiki calls "the wiki that never forgets becomes noisy."

**What it would take to add (SMALL):**
- New column: `decay_class` (enum: `transient` = 7d, `observation` = 30d, `decision` = 90d, `architecture` = 365d, `permanent` = never).
- `RatingPolicy` to use per-entry `halfLifeDays` instead of a global constant — the `ttl_days` override already exists on-row; extend to `half_life_days`.
- Write-time inference: the LLM (or a rule-based classifier) assigns `decay_class` based on content signals (e.g., "error", "bug", "tried" → transient; "decided", "chose", "architecture" → decision/architecture).

**Priority: LOW-MEDIUM.** The current single-curve is already functional. Classification is a refinement.

### 1d. Consolidation Tiers

**Present?** **PARTIAL.** We have workspace → project → shared promotion, which is a 3-tier consolidation model. But the LLM Wiki v2 describes 4 tiers:

> - Observations: recent, unprocessed
> - Episodes: session summaries
> - Semantic: cross-session facts
> - Procedural: workflows and patterns

Our model:
- `workspace:<id>` ≈ raw observations (but mixed — not just observations)
- `project:<id>` ≈ consolidated project facts
- `shared` ≈ cross-project facts

Missing:
- **Episode tier**: No concept of "session summary" as a distinct tier. Workspace consolidation promotes *individual entries*, not session digests.
- **Procedural tier**: No extraction of repeated patterns into workflows. The `shared` tier is flat facts, not procedural knowledge.
- **Auto-promotion**: Consolidation is explicit (`memory_workspace_consolidate`), not triggered by evidence accumulation.

**What it would take to add (LARGE):**
- New scope/context labels: `episode:<session-id>`, `procedural`.
- Episode digest creation: an LLM-driven summary that takes workspace entries → produces a structured digest (question, findings, files, lessons).
- Pattern extraction: detect repeated entry patterns across sessions → promote to procedural.
- Auto-promotion rules: "promote observation to semantic when seen in 3+ independent sessions."

**Priority: LOW.** The 3-tier model covers the most important use case (workspace isolation + project accumulation + cross-project curation). Procedural tier is a "compound interest" feature that pays off at scale.

---

## 2. Knowledge Graphs

### Typed Relationships vs. Similarity-Only

**Present?** **NO.** We have **zero graph edges**. Search is purely similarity-based: FTS5 keyword match + vec0 cosine distance. Two entries can be deeply related (A caused B, B depends on C) but share no lexical overlap, and our system won't connect them.

The LLM Wiki v2 describes:
> "Typed relationships: uses, depends on, contradicts, caused, fixed, supersedes"

> "When someone asks 'what's the impact of upgrading Redis?', the LLM shouldn't just keyword-search. It should start at the Redis node, walk outward through 'depends on' and 'uses' edges, and find everything downstream."

**Current state**: Our `entries` table has `hash`, `path`, `value`, `project_id` — no relationship columns, no adjacency list, no graph table. The `MemoryEntry` record has: Hash, Path, Context, Value, CreatedAt. That's it.

**What it would take to add (LARGE):**
1. **New `relationships` table**: `(source_hash, target_hash, relationship_type, confidence, created_at, agent_id)` where `relationship_type` is an enum: `uses`, `depends_on`, `contradicts`, `caused_by`, `supersedes`, `fixes`, `relates_to`.
2. **Graph extraction extension**: An `IMemoryExtension` that, on write, asks an LLM to extract entities and relationships from the content. Model the extracted entities as typed nodes (person, project, library, concept, file, decision).
3. **Graph traversal in search**: A third search modality beyond FTS5 + vec0. `SearchContexts` already splits search by context; add a `GraphTraversal` context that walks edges.
4. **RRF fusion update**: Fuse 3 ranked lists (FTS5, vec0, graph) instead of 2.
5. **New `entities` table**: `(id, type, name, attributes_json)` — nodes in the graph.

**Priority: HIGH** — but phased. Start with the simplest relationship type: `supersedes` (ties directly into lifecycle). Then `depends_on` and `uses` (most query value). Full typed graph with LLM extraction is a Wave-G level effort.

### What "similarity-only" means concretely

Our current search finds entries that *look like* the query. If the query is "Redis upgrade impact," we find entries containing "Redis," "upgrade," or "impact" (FTS5), or entries with similar embedding vectors (vec0). We do NOT find entries about "the caching layer" that depend on Redis, unless they happen to mention Redis. The graph would close that gap.

---

## 3. Hybrid Search

### BM25 + Vector + Graph Traversal

**Present?** **PARTIAL — 2 of 3 streams.**

We have:
- ✅ **BM25** via FTS5 `MATCH` + `bm25()` ranking
- ✅ **Vector** via vec0 `vec_distance_cosine` KNN
- ✅ **RRF fusion** via `ReciprocalRankFusion.Fuse()` with configurable k + modality weights
- ✅ **Candidate window** K = max(limit×3, 100)
- ❌ **Graph traversal** as a third stream

The LLM Wiki v2:
> "The best approach combines three streams: BM25, vector, graph. Fuse the results with reciprocal rank fusion. Each stream catches things the others miss."

And agentmemory:
> "We run all three (BM25 + vector + knowledge graph) and fuse with RRF, that's how we hit 95.2% on LongMemEval-S."

**Current RRF fusion**: Two ranked lists per context → `ReciprocalRankFusion.Fuse([(ftsResults, ftsWeight), (vectorResults, vectorWeight)], rrfK, minScore, limit)`. Adding a third stream is structurally straightforward — the `Fuse` method already takes `IReadOnlyList<(IReadOnlyList<MemorySearchResult>, double Weight)>`.

**What it would take to add (MEDIUM, after graph exists):**
- `GraphTraversalContext` that, given a query, extracts entities → walks edges → returns connected entries as a ranked list.
- Pass it as a third modality into the existing `Fuse` call with `graphWeight` parameter.
- New `SearchQuery` parameter: `graphWeight` (default 1 when graph exists, 0 when not).

**Priority: HIGH** — but dependent on knowledge graph (item 2). Graph traversal is the third stream; without the graph there's nothing to traverse.

---

## 4. Automation Hooks

### Event-Driven Ingest, Session-Start Context Injection, Auto-Lint

**Present?** **PARTIAL.** We have the extension pipeline (`IMemoryExtension`) which IS the hook architecture. But we use it only for `RetrievalRatingExtension` (bump access count on search).

The LLM Wiki v2 describes:
> "Events that fire automatically: on-source-added (auto-ingest, extract entities, update graph, update index), on-session-start (load relevant context), on-session-end (compress into observations), on-answer-generated (file if quality > threshold), on-contradiction-detected (trigger supersession), on-schedule (periodic lint, consolidation, retention decay)."

We have:
- ✅ `OnWriteAsync` — fires on every write
- ✅ `OnSearchAsync` — fires on every search
- ✅ `OnDeleteAsync` — fires on every delete, including sweep- and consolidation-driven deletes
- ❌ `OnSweepAsync` / `OnConsolidateAsync` — this was wrong even at the time this doc was
  written: neither hook ever had a dispatcher in `MemoryExtensionHost`, so neither could
  fire. Both were removed per ADR-0013; sweep and consolidation stay observable through
  `OnDeleteAsync` above.
- ❌ No auto-ingest trigger (filesystem watcher deferred to part 2)
- ❌ No session-start context injection
- ❌ No session-end compression
- ❌ No auto-lint (no lint operation at all)
- ❌ No scheduled/periodic operations (no cron)

**What it would take to add (MEDIUM-SMALL for hooks, LARGE for actual automation logic):**

The hook architecture is already in place. What's missing:
1. **Session lifecycle hooks**: `OnSessionStart`, `OnSessionEnd` — new `IMemoryExtension` methods with corresponding context records. Requires the MCP client to signal session boundaries.
2. **Auto-ingest**: A filesystem watcher (already on the roadmap as "part 2") that fires `OnSourceChanged` → extension runs ingest pipeline.
3. **Auto-lint**: An extension that periodically (or on-demand) checks for orphan entries, stale claims, broken cross-references. This is mostly an LLM-driven operation over the bank.
4. **Scheduled operations**: A background timer that triggers sweep, lint, and consolidation on a schedule. Could be a simple `PeriodicTimer` in the MCP server.

**Priority: MEDIUM.** The extension pipeline is the hard part and it's done. Session-start context injection is the highest-value hook (gives agents relevant memory without explicit search). Auto-lint + scheduled ops can follow.

---

## 5. Quality and Self-Correction

### Scoring, Self-Healing, Contradiction Resolution

**Present?** **NO — almost entirely absent.**

The LLM Wiki v2:
> "Every piece of content the LLM writes should get a quality score. Is it well-structured? Does it cite sources? Is it consistent with the rest of the wiki?"
> "The lint operation should automatically fix what it can. Orphan pages get linked or flagged. Stale claims get marked. Broken cross-references get repaired."
> "The LLM should propose which claim is more likely correct based on source recency, source authority, and number of supporting observations."

We have:
- ❌ No quality scoring of entries
- ❌ No contradiction detection
- ❌ No self-healing / auto-repair
- ❌ No lint operation at all
- ✅ Rating (usage-based, not quality-based)

**What it would take to add (MEDIUM-LARGE):**

1. **Quality scoring** (SMALL): Add `quality_score` column (0.0–1.0). On write, run a lightweight LLM self-evaluation check: "Rate this entry for structure, source citation, consistency." Store the score. Below-threshold entries get flagged.
2. **Contradiction detection** (MEDIUM): `OnWrite` extension that searches for semantically similar but factually conflicting entries. E.g., new entry says "uses PostgreSQL", existing entry says "uses Redis" for the same entity. Flag the conflict with a `contradiction` relationship.
3. **Contradiction resolution** (MEDIUM): When two entries contradict, an LLM-driven resolution proposes which is more likely correct (by source recency, source count). The older/weaker gets superseded.
4. **Lint operation** (MEDIUM): New MCP tool `memory_lint` that: (a) finds entries with no incoming/outgoing relationships, (b) detects stale entries (age > 2× TTL), (c) checks for semantic duplicates (near-identical content with different hashes), (d) proposes fixes.
5. **Self-healing** (SMALL after lint): Auto-apply low-risk fixes (mark orphans, flag stale) without human approval; high-risk fixes (merge duplicates, resolve contradictions) propose + await confirmation.

**Priority: MEDIUM.** Quality scoring is cheap and high-value (it gates what becomes "established knowledge"). Full contradiction resolution depends on knowledge graph relationships.

---

## 6. Multi-Agent Mesh Sync

**Present?** **PARTIAL — single-agent sync exists; mesh coordination does not.**

The LLM Wiki v2:
> "If multiple agents are working in parallel, their observations need to merge into a shared wiki. Last-write-wins works for most cases. For conflicts, timestamp-based resolution with manual override."
> "Private vs. shared scoping: Some knowledge is personal (my preferences, my workflow). Some is shared (project architecture, team decisions)."
> "Coordination tracking: Who's working on what. What's blocked. What's done."

We have:
- ✅ Single-file sync (VACUUM INTO → pull → merge → push with If-Match CAS)
- ✅ Row-level LWW merge (can merge concurrent writes from multiple agents)
- ✅ Per-project scoping (but no per-agent private tier)
- ❌ No agent identity in sync (rows carry `agent_id` but merge doesn't use it)
- ❌ No work coordination tracking
- ❌ No "private → shared" promotion across agents
- ❌ No mesh topology (single cloud DB, not agent-to-agent)

**What it would take to add (LARGE):**

1. **Private scoping** (MEDIUM): New scope `private:<agent_id>`. Private entries never sync. Promotion from private → project requires explicit `memory_share` from that agent.
2. **Agent-aware merge** (SMALL): The sync merge already handles concurrent writes. Add `agent_id` to the merge conflict policy: when two agents write to the same path, keep both (content-addressed hashes differ) or LWW on `updated_at`.
3. **Coordination table** (MEDIUM): `coordination(project_id, agent_id, status, topic, started_at, updated_at)`. Agents register what they're working on. Lightweight — not a task manager, just visibility.
4. **Mesh sync** (LARGE): Agent-to-agent sync without a central cloud DB. Requires peer discovery, vector clocks or CRDTs (beyond current LWW), and conflict-free merge that handles schema changes.

**Priority: LOW.** The LLM Wiki v2 rates this as the last tier: Level 5 (after hybrid search, quality scoring, and automation). It's important for teams but AiRaccoon's primary use case is currently single-agent.

---

## 7. Crystallization

### Compounding from Exploration

**Present?** **NO — consolidation exists but crystallization does not.**

The LLM Wiki v2:
> "Crystallization is the process of taking a completed chain of work (a research thread, a debugging session, an analysis) and automatically distilling it into a structured digest. What was the question? What did we find? What files/entities were involved? What lessons emerged? This digest becomes a first-class wiki page, and the lessons get extracted as standalone facts that strengthen the knowledge base."

> "Your explorations are a source, just like an article or a paper. The wiki should treat them that way. Ingest the results, update the graph, strengthen or challenge existing claims."

We have:
- ✅ Workspace consolidation: `memory_workspace_consolidate` promotes individual entries from workspace → project
- ❌ No digest/summary creation — we promote raw entries, not structured digests
- ❌ No lesson extraction
- ❌ No "exploration as a source" treatment
- ❌ No confidence reinforcement from crystallization

**What it would take to add (MEDIUM):**

1. **Digest generator** (MEDIUM): When a workspace is consolidated, run an LLM over all workspace entries → produce a structured digest: title, question, findings, files involved, lessons, confidence adjustments.
2. **Lesson extraction** (SMALL after digest): From the digest, extract standalone facts and write them as new entries with `source_type = 'crystallized'`.
3. **Confidence reinforcement** (SMALL): When crystallized facts match existing entries, increment `source_count` and bump `confidence_score`.
4. **Graph integration** (MEDIUM after graph exists): When crystallization produces a digest, create relationships between the digest and the files/entities/decisions it references.

**Noba connection**: The testing effect / retrieval practice effect — every time we retrieve a memory, it becomes stronger. Crystallization IS retrieval practice for the wiki: re-processing explorations to strengthen what was learned. The encoding specificity principle suggests that the more we associate new findings with existing knowledge (via graph edges), the more retrievable both become.

**Priority: LOW-MEDIUM.** This is the "compound interest" feature. It's what takes a memory system from useful to transformative at scale. But it depends on several preceding features (confidence scoring, graph, quality scoring).

---

## Summary: Priority Matrix

| # | Concept | Status | Effort | Priority | Dependencies |
|---|---------|--------|--------|----------|-------------|
| 1a | Confidence scoring | ❌ Missing | Medium | **HIGH** | None (new columns + policy) |
| 1b | Supersession | ❌ Missing | Medium | **MEDIUM** | Confidence scoring |
| 1c | Per-class retention curves | 🟡 Partial | Small | LOW-MED | None (extend existing TTL) |
| 1d | Consolidation tiers (4-tier) | 🟡 Partial | Large | LOW | Episode digest, pattern extraction |
| 2 | Knowledge graph (typed edges) | ❌ Missing | Large | **HIGH** | New relationships table, LLM extraction |
| 3 | Graph traversal in search | ❌ Missing | Medium | **HIGH** | Knowledge graph (item 2) |
| 4 | Automation hooks | 🟡 Partial | Med-Sm | **MEDIUM** | Session lifecycle signals from client |
| 5a | Quality scoring | ❌ Missing | Small | **MEDIUM** | None (new column + LLM eval) |
| 5b | Contradiction detection | ❌ Missing | Medium | MEDIUM | Knowledge graph + confidence |
| 5c | Lint/self-healing | ❌ Missing | Medium | LOW-MED | Quality scoring, contradictions |
| 6 | Multi-agent mesh sync | 🟡 Partial | Large | LOW | Per-agent private scope, coordination |
| 7 | Crystallization | ❌ Missing | Medium | LOW-MED | Confidence, graph, quality scoring |

### Recommended Implementation Order

```
Phase 1 (Wave F-G equivalent): Confidence scoring (1a) + Quality scoring (5a)
    └─ Foundation: everything else builds on knowing what's reliable.

Phase 2 (Wave H): Knowledge graph basics (2) + Supersession (1b)
    └─ Typed edges: supersedes, depends_on, uses. Add to entities table.

Phase 3 (Wave I): Graph traversal search stream (3) + Per-class retention (1c)
    └─ Third modality in RRF fusion. Classify decay rates.

Phase 4 (Wave J): Automation hooks (4) + Contradiction detection (5b)
    └─ Session-start context injection. Auto-detect conflicting claims.

Phase 5 (Wave K): Crystallization (7) + Lint/self-healing (5c)
    └─ Digest generation. Auto-repair.

Phase 6 (Wave L): Multi-agent mesh (6) + 4-tier consolidation (1d)
    └─ Team-scale features.
```

### What We Already Have That's Strong

1. **The extension pipeline** (`IMemoryExtension`) is elegant and forward-compatible — every new feature hooks into it.
2. **Hybrid search with RRF** is production-grade: configurable k/weights, candidate window, modality degradation.
3. **Content-addressed dedup** eliminates the hardest class of sync conflicts.
4. **The Ebbinghaus decay model in RatingPolicy** is conceptually aligned with both the LLM Wiki v2 and Noba's research on forgetting curves.
5. **Single-file sync with VACUUM INTO + If-Match CAS** is the correct primitive for agent memory sync (much simpler than CRDTs for a single-writer-per-install model).
6. **The 3-tier context model** (workspace → project → shared) maps cleanly onto the consolidation pipeline.

### What Would Give the Biggest Lift with the Least Code

**Confidence scoring** (1a): Add 3 columns (`confidence_score`, `source_count`, `last_confirmed_at`), a `ConfidencePolicy` class, and surface it in `MemorySearchResult`. ~200 lines of C#. Impact: transforms search results from "here's what matched" to "here's what I believe."
