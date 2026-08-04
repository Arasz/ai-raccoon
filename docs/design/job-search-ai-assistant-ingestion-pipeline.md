# Document Ingestion Pipeline & Test Suite Design

**For:** job-search-ai-assistant → AiRaccoon memory.db  
**Status:** Design (no implementation)  
**Date:** 2026-08-04

---

## 1. Documentation Inventory

### 1.1 Overview by Source

| Source | Files | Est. Total Lines | Est. Total Size | Ingest? | Rationale |
|---|---|---|---|---|---|
| `docs/adr/` | 85 ADR files + `index.json` + `README.md` | ~12,000 | ~1.1 MB | **Yes, all** | Core architectural knowledge — decisions, context, trade-offs, dates, statuses |
| `docs/` (top-level) | `architecture.md`, `data-model.md`, `flows.md`, `requirements.md`, `CHANGELOG.md`, `README.md` | ~7,000 | ~350 KB | **Yes, all** | System overview, domain model, requirements, flow diagrams |
| `docs/explanation/` | 3 files: `frontend-architecture.md`, `frontend-technical-notes.md`, `prompt-caching-and-agent-cost.md` | ~500 | ~40 KB | **Yes, all** | Deep-dive explanations with YAML frontmatter |
| `docs/how-to/` | 1 file: `deploy-the-application.md` | ~1,100 | ~90 KB | **Yes** | Operational runbook with commands |
| `docs/reference/` | 7 files: `agent-instruction-files.md`, `behaviour-specification.md`, `ci-workflows.md`, `configuration.md`, `domain-glossary.md`, `linkedin-archive-format.md`, `README.md` | ~800 | ~170 KB | **Yes, all** | Reference specifications, glossary, CI/CD details |
| `docs/rules/` | 2 files: `ats-cv-screening-rules.md`, `linkedin-optimization-rules.md` | ~150 | ~10 KB | **Yes, all** | Curated domain rules (seed data for AI pipelines) |
| `docs/meta/` | `baseline.json`, `dropped.md`, `index.json`, `ledger.jsonl`, `migration.json`, `moves.json`, `trust-debt.md`, `trust-index.json` | ~300 | ~15 KB | **Selective** | `baseline.json`, `trust-debt.md`, `trust-index.json` only — documentation system health metadata |
| `docs/work/` | Subdirs: `backlog/`, `designs/`, `incidents/`, `plans/`, `research/`, `reviews/`, `specs/` + dated markdown | varies | ~1 MB | **Exclude** | Dated work records — historical, not durable knowledge (ADR-0070 distinguishes these from living docs) |
| `docs/brand/` | Logo, favicon, README | ~5 | ~50 KB | **Exclude** | Brand assets, not knowledge |
| `docs/tutorials/` | 1 file | ~50 | ~5 KB | **Yes** | Tutorial content |
| `docs/legacy/` | ~5 files | ~200 | ~20 KB | **Yes** | Legacy docs with historical value |
| `.ai-badger/invariants/` | 22 files | ~66 | ~8 KB | **Yes, all** | Short, dense rules — project constitution |
| `.ai-badger/skills/` | 19 skill dirs, each with `SKILL.md` + references | ~2,000 | ~120 KB | **Yes, all** | Agent workflows, procedures, conventions |
| `.ai-badger/agents/` | 9 agent persona files | ~450 | ~20 KB | **Yes, all** | Agent role definitions, model routing, disallowed tools |
| `.ai-badger/instructions/` | 11 instruction files | ~200 | ~15 KB | **Yes, all** | Per-language/per-stack conventions |
| `.ai-badger/` (config) | `config.json`, `delegation.md`, `copilot-instructions.md`, `manifest.json` | ~300 | ~95 KB | **Selective** | `config.json`, `delegation.md`, `copilot-instructions.md` — project structure, agent routing, AI policy |
| `.ai-badger/agent-instructions/` | `README.md`, `model.json`, `schema.json` | ~100 | ~5 KB | **Yes** | Agent instruction model definition |
| `.ai-badger/state.json` | 1 file | ~375 | ~93 KB | **Exclude** | Session state, completed tasks — operational, not knowledge |
| `.ai-badger/status-notes.json` | 1 file | ~3,000 | ~147 KB | **Exclude** | Session notes — too large, operational |
| `.ai-badger/task-tracking/` | ~15 files | ~2,000 | ~150 KB | **Exclude** | Work tracking — operational |
| `.ai-badger/hooks/` | `hooks.json` | ~20 | ~2 KB | **Exclude** | Git hooks config |
| `.remember/` | `now.md`, `recent.md`, `archive.md`, 16 `today-*.done.md` | ~500 | ~30 KB | **Selective** | `recent.md`, `archive.md` — project history and identity patterns |
| Root markdown | `README.md`, `CLAUDE.md`, `HERMES.md`, `REVIEW.md` | ~250 | ~55 KB | **Yes, all** | Project entry points |
| `infra/README.md` | 1 file | ~100 | ~8 KB | **Yes** | Infrastructure documentation |

**Estimated total ingest:** ~150–180 files, ~25,000 lines of prose, ~2.5 MB (excluding 1.5 GB `.ai-badger` binary artifacts and operational state).

### 1.2 Knowledge Categories by Content Type

1. **Architecture Decisions (ADR-0001–AD-0088)**
   - Nygard-format: Context → Decision → Consequences → Alternatives
   - Each carries: status (accepted/superseded/draft), date, title
   - Cross-reference network (superseded-by, amended-by links)
   - Content themes: domain library choices, frontend stack, CI/CD, security, data protection, agent framework, channel monitoring

2. **System Architecture & Models**
   - `architecture.md`: Components diagram, layered architecture, principles (API-only writer, pure Domain), repository layout, auth model, extension points
   - `data-model.md`: Cosmos aggregates, entity diagrams, JSON Resume superset schema, domain glossary
   - `flows.md`: Mermaid flowcharts, sequence diagrams, state machine diagrams for every user flow
   - `requirements.md`: Vision, scope (MVP / Post-MVP), FR-1 through FR-CM-6.x with [MVP]/[POST-MVP] tags

3. **Project Invariants (Constitution)**
   - 22 short rules (3 lines each): TDD mandatory, screaming architecture, clean layering, no hardcoded secrets, guard clauses, proof-of-done, single writer, etc.
   - Each is a one-sentence imperative with a one-paragraph rationale

4. **Code Conventions (Per-Stack)**
   - 11 instruction files: C#, React, TypeScript, JavaScript, CSS, Node, Cosmos, Azure Functions, Terraform, Hermes, Documentation
   - Each: YAML frontmatter with `applyTo` glob, bullet-point conventions

5. **Agent Skills & Personas**
   - 19 skills: Each SKILL.md has YAML frontmatter (name, description, trigger phrases, version), procedural steps, decision trees, extension points
   - 9 agents: Persona definitions with model routing, disallowed tools, mandatory gates, scope boundaries

6. **Project Structure & Routing**
   - `config.json`: Framework version, stacks, agents, source control, persona routing, verifier commands
   - `delegation.md`: Scaffolded delegation map — stacks, personas with lanes, routing rules, MCP servers

7. **Operational Memory**
   - `.remember/recent.md`: Weekly summaries with identity patterns
   - `.remember/archive.md`: Archived week summaries
   - Pattern: Bullet-point accomplishments with PR numbers, test counts, metrics

8. **Domain Rules (Seed Data)**
   - `ats-cv-screening-rules.md`: 19 ATS rules with ID, category, rule text, rationale
   - `linkedin-optimization-rules.md`: LinkedIn profile optimization rules

9. **Reference Specifications**
   - `behaviour-specification.md`: User flows, state machine specification, REST API table, intervention model
   - `configuration.md`: Configuration sources, precedence, secrets management
   - `ci-workflows.md`: GitHub Actions workflows, trigger conditions
   - `domain-glossary.md`: Polish tax/compensation terminology mapping

10. **Documentation System Metadata**
    - `docs/meta/baseline.json`: Quality ceilings (dead links, legacy files)
    - `docs/meta/trust-index.json`: Trust verification records
    - `docs/meta/trust-debt.md`: Known documentation trust issues

### 1.3 File Format Patterns

| Pattern | Example | Frontmatter? |
|---|---|---|
| ADRs | `0001-fluentvalidation-in-domain.md` | No (plain markdown with Nygard headers) |
| Docs with modality | `architecture.md`, `data-model.md` | No (plain markdown, some with Mermaid) |
| Explanation/How-to/Reference | `frontend-architecture.md` | **Yes** — YAML: `modality`, `title`, `type`, `updated`, `version` |
| Invariants | `tdd-mandatory.md` | No (H1 title + 1-2 paragraph body) |
| Skills | `SKILL.md` | **Yes** — YAML: `name`, `description`, `version`, `author`, `platforms`, `metadata` |
| Agents | `architect.md` | **Yes** — YAML: `name`, `description`, `model`, `disallowedTools` |
| Instructions | `csharp.instructions.md` | **Yes** — YAML: `description`, `applyTo` |
| Config | `config.json` | N/A (JSON) |
| .remember | `recent.md` | No (markdown with ## date headers) |
| Rules | `ats-cv-screening-rules.md` | No (markdown tables) |

---

## 2. AiRaccoon Memory Tools API

The ai-raccoon MCP server exposes 19 tools. The ingestion pipeline primarily uses:

### 2.1 Key Tools for Ingestion

| Tool | Parameters | Use in Pipeline |
|---|---|---|
| `ai-raccoon model set local\|openai` (CLI, not an MCP tool) | n/a — config verbs take no tool params | **Step 0:** Set up the embeddings engine before ingestion via the CLI (`ai-raccoon model set local` for the bundled ONNX all-MiniLM-L6-v2, dim 384 — zero API cost, offline; `model set openai` for remote). Configuration is not an MCP tool since the CLI-config refactor. |
| `memory_write` | `projectId`, `content`, `context?`, `agentId?` | **Step 3:** Write each chunk. Returns `{hash, path, context, createdAt}`. |
| `memory_ingest_file` | `projectId`, `path`, `context?` | **Alternative:** Ingest a whole file as one entry (for small files like invariants). Returns `{indexed: 0\|1}`. |
| `memory_ingest_directory` | `projectId`, `path`, `context?` | **Alternative:** Bulk directory ingest. Returns `{scanned: n}`. |
| `memory_embed_pending` | `projectId`, `limit?` | **Step 4:** Process pending embeddings. Writes are stored deferred until embedded. |
| `memory_search` | `projectId`, `query`, `scope`, `limit`, `minScore`, `rrfK`, `ftsWeight`, `vectorWeight` | **Testing:** The retrieval side. Hybrid FTS5+vector RRF fusion. |
| `memory_list` | `projectId` | **Verification:** List ingested files as a JSON tree. |
| `memory_stats` | `projectId` | **Verification:** Get `{entries, pending, contexts}`. |
| `memory_share` | `projectId`, `hash` | **Optional:** Promote cross-project knowledge to `shared` context. |
| `memory_delete` | `projectId`, `hash` | **Cleanup:** Remove specific entries. |
| `memory_delete_context` | `projectId`, `context` | **Cleanup:** Remove all entries under a context label. |

### 2.2 Context Strategy

AiRaccoon's memory is partitioned by context:

| Context | Use |
|---|---|
| `project:job-search-ai-assistant` | Default — all ingested knowledge lands here |
| `shared` | For cross-project reusable rules (e.g., ATS rules, coding invariants) — promoted via `memory_share` |
| Custom labels | Use for sub-categories during ingestion: `docs:adr`, `docs:architecture`, `docs:reference`, `ai-badger:invariants`, `ai-badger:skills`, `ai-badger:agents`, `ai-badger:instructions`, `remember:operational` |

Custom context labels enable targeted retrieval: `memory_search(scope="project", context="docs:adr", query="...")` returns only ADR chunks.

### 2.3 Embedding Engine Choice

**Recommendation: `provider=local`** using the bundled ONNX all-MiniLM-L6-v2 model:
- Dimension 384, runs in-process, no API cost
- Fast enough for ~25K lines of text
- Consistent, reproducible embeddings (no provider drift)
- SHA-256 verified model binary

---

## 3. Ingestion Pipeline Design

### 3.1 Pipeline Overview

```
Phase 0: Configure embeddings
Phase 1: Enumerate & classify files
Phase 2: Chunk files by content type
Phase 3: Write chunks with typed contexts
Phase 4: Embed pending entries
Phase 5: Verify (stats, spot-check search)
```

### 3.2 Phase 0: Configure Embeddings

```
memory_configure(
  projectId="job-search-ai-assistant",
  provider="local"
)
→ {provider: "local", engine: "local:bundled"}
```

### 3.3 Phase 1: File Enumeration

Walk the project tree with these inclusion rules:

```
INCLUDE:
  docs/adr/*.md
  docs/*.md                           (top-level: architecture, data-model, flows, requirements, CHANGELOG, README)
  docs/explanation/*.md
  docs/how-to/*.md
  docs/reference/*.md
  docs/rules/*.md
  docs/tutorials/*.md
  docs/legacy/*.md
  docs/meta/{baseline.json,trust-debt.md,trust-index.json}
  .ai-badger/invariants/*.md
  .ai-badger/skills/*/SKILL.md       (+ references/ subfiles as separate entries)
  .ai-badger/agents/*.md
  .ai-badger/instructions/*.md
  .ai-badger/config.json
  .ai-badger/delegation.md
  .ai-badger/copilot-instructions.md
  .ai-badger/agent-instructions/*
  .remember/{recent.md,archive.md}
  README.md
  CLAUDE.md
  HERMES.md
  REVIEW.md
  infra/README.md

EXCLUDE:
  .ai-badger/state.json
  .ai-badger/status-notes.json
  .ai-badger/status-history.json
  .ai-badger/task-tracking/
  .ai-badger/hooks/
  .ai-badger/worktrees/
  .ai-badger/prompt-markers/
  .ai-badger/skills-data/
  .ai-badger/mcp-tools.json
  .ai-badger/manifest.json
  .ai-badger/stack-ignore.json
  .ai-badger/mcp-tools.yaml.migrated
  .remember/now.md                   (volatile, changes too frequently)
  .remember/today-*.md               (daily logs — too fine-grained)
  .remember/tmp/
  .remember/logs/
  docs/work/                         (dated work records — ADR-0070 classifies as non-living)
  docs/brand/
  .github/                           (CI workflows are code, not knowledge)
  node_modules/
  .git/
```

### 3.4 Phase 2: Chunking Strategy

**Principle:** One chunk = one self-contained retrievable unit of knowledge. A chunk should answer a specific question without needing adjacent chunks.

#### Chunking by Content Type

| Content Type | Chunking Strategy | Max Chunk Size | Justification |
|---|---|---|---|
| **ADRs** (Nygard format) | **Per-section chunks:** Title+Status+Date → Context → Decision → Consequences → Alternatives | ~2,000 chars each | Each ADR is already structured as 5-6 self-contained sections. Search for "why FluentValidation" should return the Decision section, not the full ADR. |
| **Architecture, Data Model, Flows, Requirements** | **Heading-level chunks:** Each H2 section becomes a chunk. H1 title prepended to each chunk as context prefix. | ~3,000 chars | Large docs (30K–75K chars) with natural heading boundaries. "What is the auth model?" → returns architecture.md §4. |
| **Invariants** | **One file = one chunk.** No splitting. | ~400 chars | Already atomic — each file is a single rule. |
| **Skills** | **Per SKILL.md file.** If a skill has references/, those get separate chunks with `context: "ai-badger:skills:<name>:references"`. | ~5,000 chars | Skills are procedural documents that lose coherence if split mid-procedure. |
| **Agents** | **One file = one chunk.** | ~2,500 chars | Already concise persona definitions. |
| **Instructions** | **One file = one chunk.** | ~1,900 chars | Already short convention lists. |
| **.remember (recent, archive)** | **Per-week entry.** Each `## Week of` or `## date` section becomes a chunk. | ~1,500 chars | Temporal chunks — "What happened in week of 2026-07-27?" |
| **Rules (ATS, LinkedIn)** | **Per-table-row + preamble.** Each rule row becomes a chunk with the document preamble for context. | ~800 chars | "What is ATS-001?" → single rule row retrieval. |
| **Reference specs** | **Heading-level chunks** like architecture docs. | ~3,000 chars | Similar to architecture docs. |
| **Config/JSON files** | **One file = one chunk.** JSON is serialized as formatted text. | ~3,000 chars | Small enough, structured enough. |
| **Root markdown (README, CLAUDE, HERMES)** | **Per H2 section** chunks. | ~2,000 chars | Entry-point docs with clear sections. |

#### Chunk Metadata

Each chunk carries as its `path` parameter a structured identifier:

```
Format:  <source>:<file-path>#<section>
Examples:
  docs:adr/0011-frontend-chassis-stack.md#decision
  docs:architecture.md#4-authentication
  ai-badger:invariants/tdd-mandatory.md
  ai-badger:skills/debug-issue/SKILL.md
  remember:recent.md#2026-07-27
  rules:ats-cv-screening-rules.md#ATS-001
```

And `context` as a typed label:

```
context = "docs:adr"           → for ADRs
context = "docs:architecture"  → for architecture, data-model, flows, requirements
context = "docs:explanation"   → for explanation docs
context = "docs:how-to"        → for how-to docs
context = "docs:reference"     → for reference docs
context = "docs:rules"         → for rule seed data
context = "ai-badger:invariants"
context = "ai-badger:skills"
context = "ai-badger:agents"
context = "ai-badger:instructions"
context = "remember:operational"
```

### 3.5 Phase 3: Write Chunks

Loop over chunks, calling:

```
memory_write(
  projectId="job-search-ai-assistant",
  content=<chunk_text>,
  path=<structured_identifier>,
  context=<typed_context>
)
```

**Batching:** Since MCP tool calls are sequential, write chunks in batches of ~50, calling `memory_embed_pending` after each batch to keep the pending queue from growing too large (this is important for the local ONNX engine which runs in-process).

### 3.6 Phase 4: Embed

After all chunks are written:

```
memory_embed_pending(
  projectId="job-search-ai-assistant",
  limit=0   // 0 = no limit, process all
)
→ {processed: N, pending: 0}
```

### 3.7 Phase 5: Verify

```
memory_stats(projectId="job-search-ai-assistant")
→ Expected: {entries: ~300-400, pending: 0, contexts: ~12}

// Spot-check searches:
memory_search(projectId="job-search-ai-assistant", query="what is the frontend component library?")
memory_search(projectId="job-search-ai-assistant", query="TDD policy")
memory_search(projectId="job-search-ai-assistant", query="Cosmos DB partition key strategy")
memory_search(projectId="job-search-ai-assistant", query="channel monitoring architecture")
```

### 3.8 Estimated Chunk Count

| Category | Files | Est. Chunks |
|---|---|---|
| ADRs | 85 | ~425 (5 sections × 85) |
| Architecture docs | 6 | ~90 |
| Invariants | 22 | 22 |
| Skills | 19 | ~30 (SKILL.md + references) |
| Agents | 9 | 9 |
| Instructions | 11 | 11 |
| Config/delegation/copilot | 3 | 3 |
| Agent-instructions model | 3 | 3 |
| .remember | 2 | ~18 |
| Rules | 2 | ~42 (rows + preamble) |
| Reference specs | 7 | ~50 |
| Explanation | 3 | ~15 |
| How-to | 1 | ~10 |
| Tutorials | 1 | ~3 |
| Legacy | ~5 | ~10 |
| Meta | 3 | 3 |
| Root markdown | 4 | ~20 |
| Infra README | 1 | ~5 |
| **Total** | | **~770 chunks** |

---

## 4. Test Query Suite

### 4.1 Query Design Principles

Test queries exercise specific retrieval dimensions:
1. **Precision:** Does the top result answer the question?
2. **Recall:** Are all relevant chunks found?
3. **Cross-reference:** Can queries follow ADR supersession chains?
4. **Context filtering:** Does context-scoped search exclude irrelevant results?
5. **Hybrid fusion:** Do keyword-heavy and semantic queries both work?

### 4.2 Query Categories

#### A. Architecture Decisions (ADR Retrieval)

| # | Query | Expected Knowledge | Expected Source |
|---|---|---|---|
| A1 | "Why was shadcn/ui chosen over gluestack.io?" | ADR-0011 Decision section — gluestack v5 is Expo-Router-first, no Vite guide, lacks Table/Tabs/Badge/Progress primitives | `docs:adr/0011-frontend-chassis-stack.md#decision` |
| A2 | "What ADR governs UUID choice?" | ADR-0004 — UUID version 7 for generated identifiers | `docs:adr/0004-uuid-version7-for-identifiers.md` |
| A3 | "How does the project handle offer-page fetching security?" | ADR-0006 — Client-side fetch; backend never dereferences user-supplied URLs | `docs:adr/0006-client-side-offer-page-fetch.md` |
| A4 | "What happened to the MCP server?" | ADR-0060 — MCP server deleted, not deprecated | `docs:adr/0060-delete-the-mcp-server.md` |
| A5 | "What replaced the LLM cost NFR?" | ADR-0046 — retired NFR-1, superseded ADR-0022 | `docs:adr/0046-retire-nfr-1-llm-cost-protection.md` |
| A6 | "How does the project handle data erasure?" | ADR-0067 + ADR-0068 — registry-driven fan-out, with explicit what-erasure-does-not-erase | `docs:adr/0067-*` + `docs:adr/0068-*` |
| A7 | "What is ADR-0070 about?" | Documentation structure and trust model — distinguishes living docs from work records | `docs:adr/0070-documentation-structure-and-trust-model.md` |

#### B. Architecture & System Knowledge

| # | Query | Expected Knowledge |
|---|---|---|
| B1 | "What are the core architectural principles?" | API is only Cosmos writer, Domain is pure C# (no infra deps), only vetted computation libraries allowed |
| B2 | "What Azure services does the project use?" | Cosmos DB serverless, Blob Storage, Azure Functions (isolated), Static Web Apps, Key Vault, Application Insights via OTel |
| B3 | "How does local development work?" | .NET Aspire AppHost orchestrates Cosmos emulator + Azurite + Functions + Bun frontend; one `dotnet run` command |
| B4 | "What is the Cosmos partition key strategy?" | All containers partitioned by `/userId` (see invariants/partition-by-userid.md) |
| B5 | "What are the extension points?" | Architecture §5 — `IPipelineStep`, `IRanker`, `ITailoringRule`, `IProfileSink`, `IChannelMonitor`, `ISignalClassifier`, `ISignalEnricher`, `ISignalCorrelator` |
| B6 | "How does authentication work?" | Easy Auth v2 (GitHub OAuth) on the API, SPA calls with `credentials: 'include'`, local dev short-circuits via `IsLocal()` |

#### C. Invariants & Conventions

| # | Query | Expected Knowledge |
|---|---|---|
| C1 | "Is TDD required?" | Yes — "Write a failing, behavior-focused test before any production code change" |
| C2 | "What is the screaming architecture rule?" | Organize folders by domain concept, not technical bucket. Avoid `Services/`, `Controllers/`, `Utils/` |
| C3 | "How should NuGet packages be managed?" | Centralized in `Directory.Packages.props`, never pin Version on individual PackageReference |
| C4 | "What logging pattern is required?" | Source-generated `[LoggerMessage]` methods, not direct `ILogger` calls |
| C5 | "Are hardcoded secrets allowed?" | No — "No hardcoded secrets. Every credential lives in user secrets, environment variables, or Key Vault" |
| C6 | "What error format does the API use?" | RFC 7807 problem details; invalid state transitions return 409; long-running ops return 202 + operationId |

#### D. Skills & Agent Workflows

| # | Query | Expected Knowledge |
|---|---|---|
| D1 | "How does the task orchestration skill work?" | task/SKILL.md — 3-phase workflow: plan→implement→gate, model delegation, config-driven verifiers |
| D2 | "What agents are available?" | 9 personas: architect, dotnet-engineer, frontend-engineer, api-engineer, cloud-infra-engineer, test-engineer, code-reviewer, delegator, hermes-agent-author |
| D3 | "What model does the architect use?" | Opus — high-reasoning model, read-only, produces blueprints/ADRs |
| D4 | "How do prompt markers work?" | prompt-markers/SKILL.md — marker prefixes (`h:`, `hint:`) for context injection |

#### E. Domain Rules (Seed Data)

| # | Query | Expected Knowledge |
|---|---|---|
| E1 | "What is ATS-001?" | Single-column layout; no tables, graphics, photos, or skill bar charts |
| E2 | "What are the three layers of modern hiring screens?" | ATS layer → AI/LLM layer → Human layer |
| E3 | "What ATS rules govern keyword usage?" | ATS-008 through ATS-012: mirror job description terminology, include acronym+full name, repeat 2-3×, exact job title, cluster technologies |

#### F. Project History & Identity

| # | Query | Expected Knowledge |
|---|---|---|
| F1 | "What major work happened in week of 2026-07-27?" | Compensation Waves 1-3, Bun/TS port of pre-push verify, ADRs 0049–0053, CI optimization to 7.2k billed-min/mo |
| F2 | "When was the project established?" | Week of 2026-07-06 — MVP design, domain model, 18 endpoints, MCP integration |
| F3 | "What identity patterns does the project exhibit?" | Waves-based delivery + systematic ADR documentation + mutation-driven test coverage; vocabulary unification as systematic refactoring pattern |

#### G. Cross-Cutting (Multi-Context)

| # | Query | Expected Knowledge |
|---|---|---|
| G1 | "How is compensation calculated?" | ADR-0035 (ICountryCompProfile), ADR-0048 (offer-dictated terms), domain-glossary.md (Polish tax terms), architecture.md (compensation engine) |
| G2 | "What is the frontend technology stack?" | ADR-0011 + frontend-architecture.md — React 19, TypeScript strict, Vite, Bun, TanStack Query, React Hook Form, shadcn/ui, Tailwind, Vitest |
| G3 | "How does channel monitoring work?" | ADR-0024 (foundation), ADR-0061 (the fold), ADR-0062 (composed pipeline), ADR-0063 (deterministic signal ID), requirements FR-CM-1.x–5.x |

#### H. Negative Tests (Should NOT Return)

| # | Query | Expected |
|---|---|---|
| H1 | "What is in state.json?" | Should NOT return state.json contents (excluded) |
| H2 | "What happened today?" | Should NOT return `now.md` contents (excluded — volatile) |
| H3 | "Show me the Aspire config" | Should return ADR-0029 or architecture.md §Local dev orchestration, not `aspire.config.json` (excluded) |

### 4.3 Test Execution Plan

For each query:
1. Call `memory_search(projectId="job-search-ai-assistant", query=<Q>, limit=5)`
2. Record: top-5 results with `{hash, ranking, path, snippet}`
3. For cross-referencing queries (G1-G3), also run with specific `context` filter to verify scoping

### 4.4 Baseline Storage

Store the results as a JSON baseline file:

```json
{
  "projectId": "job-search-ai-assistant",
  "ingestedAt": "<ISO timestamp>",
  "embeddingEngine": "local:bundled",
  "queryResults": {
    "A1": {
      "query": "Why was shadcn/ui chosen over gluestack.io?",
      "expectedSource": "docs:adr/0011-frontend-chassis-stack.md#decision",
      "results": [
        {"hash": "...", "ranking": 0.95, "path": "...", "snippet": "..."}
      ]
    }
  }
}
```

---

## 5. Scoring HTML Form Layout

### 5.1 Purpose

An HTML form that presents each test query alongside its retrieved results, allows scoring (1–5) with a reason, and saves results as a JSON baseline for later comparison.

### 5.2 Layout Design

```
┌──────────────────────────────────────────────────────────────┐
│  Job Search AI Assistant — Memory Retrieval Scoring          │
│  Project: job-search-ai-assistant                            │
│  Ingested: 2026-08-04 | Engine: local:bundled | Chunks: 770  │
│  [Export Baseline] [Load Baseline] [Compare]                 │
├──────────────────────────────────────────────────────────────┤
│                                                              │
│  ┌─ Query A1 ────────────────────────────────────────────┐  │
│  │ Category: Architecture Decisions (ADR)                │  │
│  │                                                       │  │
│  │ Query: "Why was shadcn/ui chosen over gluestack.io?"  │  │
│  │ Expected: docs:adr/0011-frontend-chassis-stack.md     │  │
│  │                                                       │  │
│  │ Results (top 5):                                      │  │
│  │ ┌─────────────────────────────────────────────────┐  │  │
│  │ │ #1  Ranking: 0.95                              │  │  │
│  │ │     Path: docs:adr/0011-frontend-chassis-stack  │  │  │
│  │ │     Snippet: "Rejected gluestack; adopted       │  │  │
│  │ │     shadcn/ui (Radix UI primitives + Tailwind   │  │  │
│  │ │     CSS + class-variance-authority)..."         │  │  │
│  │ │     ✓ Expected source found at rank #1          │  │  │
│  │ ├─────────────────────────────────────────────────┤  │  │
│  │ │ #2  Ranking: 0.82                              │  │  │
│  │ │     Path: docs:explanation/frontend-arch        │  │  │
│  │ │     Snippet: "shadcn/ui (Radix UI + Tailwind    │  │  │
│  │ │     CSS + class-variance-authority)..."         │  │  │
│  │ ├─────────────────────────────────────────────────┤  │  │
│  │ │ #3  Ranking: 0.71                              │  │  │
│  │ │     ...                                         │  │  │
│  │ ├─────────────────────────────────────────────────┤  │  │
│  │ │ #4  Ranking: 0.65                              │  │  │
│  │ │     ...                                         │  │  │
│  │ ├─────────────────────────────────────────────────┤  │  │
│  │ │ #5  Ranking: 0.58                              │  │  │
│  │ │     ...                                         │  │  │
│  │ └─────────────────────────────────────────────────┘  │  │
│  │                                                       │  │
│  │ Score:  ○1  ○2  ○3  ●4  ○5                           │  │
│  │                                                       │  │
│  │ Reason:                                               │  │
│  │ ┌─────────────────────────────────────────────────┐  │  │
│  │ │ Correct ADR found at #1 with exact decision     │  │  │
│  │ │ content. #2 is the deep-dive explanation doc    │  │  │
│  │ │ which is also relevant but secondary. Good      │  │  │
│  │ │ precision, minor ordering nit (explanation      │  │  │
│  │ │ doc should rank below the ADR).                 │  │  │
│  │ └─────────────────────────────────────────────────┘  │  │
│  │                                                       │  │
│  │ [◄ Prev Query]  [Save Score]  [Next Query ►]         │  │
│  └───────────────────────────────────────────────────────┘  │
│                                                              │
│  ┌─ Query A2 ────────────────────────────────────────────┐  │
│  │ ...                                                    │  │
│  └───────────────────────────────────────────────────────┘  │
│                                                              │
│  ───────────  Progress: 12/43 scored  ───────────           │
│                                                              │
│  ┌─ Summary Panel ──────────────────────────────────────┐  │
│  │ Category           Count   Avg Score   Min   Max     │  │
│  │ Architecture (A)    7/7      4.3        3     5      │  │
│  │ System (B)          4/6      4.0        2     5      │  │
│  │ Invariants (C)      6/6      4.8        4     5      │  │
│  │ Skills (D)          4/4      3.8        3     4      │  │
│  │ Rules (E)           3/3      4.7        4     5      │  │
│  │ History (F)         2/3      3.5        3     4      │  │
│  │ Cross-cutting (G)   1/3      3.0        3     3      │  │
│  │ Negative (H)        3/3      N/A       N/A    N/A    │  │
│  │ ─────────────────────────────────────────────────    │  │
│  │ TOTAL              30/35     4.1        2     5      │  │
│  └──────────────────────────────────────────────────────┘  │
│                                                              │
│  [Save All Scores]  [Export as Baseline JSON]               │
│                                                              │
└──────────────────────────────────────────────────────────────┘
```

### 5.3 Form State Schema

Each scored query:

```json
{
  "queryId": "A1",
  "category": "Architecture Decisions (ADR)",
  "query": "Why was shadcn/ui chosen over gluestack.io?",
  "expectedSource": "docs:adr/0011-frontend-chassis-stack.md#decision",
  "scoredAt": null,
  "score": null,
  "reason": "",
  "results": [
    {
      "rank": 1,
      "hash": "abc123...",
      "ranking": 0.95,
      "path": "docs:adr/0011-frontend-chassis-stack.md#decision",
      "snippet": "Rejected gluestack; adopted shadcn/ui...",
      "isExpectedSource": true
    }
  ]
}
```

### 5.4 Score Scale

| Score | Label | Criteria |
|---|---|---|
| 1 | Poor | Wrong answer, irrelevant results, expected source not in top 5 |
| 2 | Weak | Some relevant content but expected source missing or ranked very low |
| 3 | Adequate | Expected source found but ranked 3–5, or content partially answers |
| 4 | Good | Expected source in top 2, content answers well, minor issues |
| 5 | Excellent | Expected source at #1, precise answer, no irrelevant noise |

### 5.5 Scoring Workflow

1. **Load:** The form loads from a baseline JSON file (generated after ingestion runs all queries)
2. **Score:** Human reviewer reads each query, inspects top-5 results, assigns 1–5 with reason
3. **Navigate:** Prev/Next buttons, category jump-links, progress bar
4. **Save:** Individual save per query, bulk save-all
5. **Export:** Generates `scored-baseline-YYYY-MM-DD.json` — this becomes the golden baseline for regression testing

### 5.6 Baseline Comparison

After a re-ingestion (e.g., after documentation changes):

```
Compare: scored-baseline-2026-08-04.json vs new-results-2026-08-11.json

┌─ Regressions ─────────────────────────────────────────────┐
│ A1: 4→2  "shadcn/ui ADR now ranks #5 (was #1)"           │
│ B6: 5→3  "Auth section snippet truncated mid-sentence"   │
└────────────────────────────────────────────────────────────┘

┌─ Improvements ────────────────────────────────────────────┐
│ G3: 3→5  "Channel monitoring query now finds full set"    │
└────────────────────────────────────────────────────────────┘
```

### 5.7 HTML Implementation Notes

- **Single self-contained HTML file** — no build step, no server, no framework
- **Vanilla JS** for interactivity (score radio buttons, navigation, JSON export/import)
- **CSS Grid** layout: left sidebar (category nav + progress) + main content area (query cards)
- **File I/O:** Use `<input type="file">` for loading baseline JSON; `<a download>` for export
- **Persistence:** `localStorage` for in-progress scores (survives page reload)
- **Print-friendly:** Hide navigation chrome when printing

---

## 6. Pipeline Script Structure

The ingestion should be a Python script at `scripts/ingest-jsaa-docs.py`:

```
scripts/ingest-jsaa-docs.py          # Main ingestion script
scripts/baseline-queries.json         # Query definitions
scripts/scored-baseline-template.html # The scoring form
```

### Script flow:

```python
# 1. Configure embeddings
memory_configure(project_id="job-search-ai-assistant", provider="local")

# 2. Walk project tree, classify files
files = enumerate_files(JSAA_ROOT, INCLUDE_GLOBS, EXCLUDE_GLOBS)

# 3. Chunk each file
chunks = []
for file in files:
    file_type = classify(file.path)
    file_chunks = chunk(file.content, strategy=CHUNK_STRATEGIES[file_type])
    chunks.extend(file_chunks)

# 4. Write chunks in batches
for batch in batch_iter(chunks, 50):
    for chunk in batch:
        memory_write(
            project_id="job-search-ai-assistant",
            content=chunk.text,
            path=chunk.path,
            context=chunk.context
        )
    memory_embed_pending(project_id="job-search-ai-assistant")

# 5. Verify
stats = memory_stats(project_id="job-search-ai-assistant")
print(f"Ingested {stats['entries']} entries across {len(stats['contexts'])} contexts")

# 6. Run baseline queries
with open("scripts/baseline-queries.json") as f:
    queries = json.load(f)

results = {}
for q in queries:
    search_result = memory_search(
        project_id="job-search-ai-assistant",
        query=q["query"],
        limit=5
    )
    results[q["id"]] = {
        "query": q["query"],
        "expectedSource": q.get("expectedSource"),
        "results": search_result["results"]
    }

# 7. Write baseline
baseline = {
    "projectId": "job-search-ai-assistant",
    "ingestedAt": datetime.now().isoformat(),
    "embeddingEngine": "local:bundled",
    "estimatedChunks": len(chunks),
    "queryResults": results
}
with open("scripts/baseline-results.json", "w") as f:
    json.dump(baseline, f, indent=2)
```

---

## 7. Risks & Mitigations

| Risk | Mitigation |
|---|---|
| 85 ADRs × 5 sections = 425 chunks may be too granular | Combine very short ADRs (shorter than 1,000 chars total) into single-chunk ADRs |
| Local ONNX embedding may be slow for 770 chunks | Batch size of 50 + embed_pending after each batch; consider `provider=openai` with LM Studio local endpoint for faster embedding |
| Chunk boundary cutting mid-sentence | Use sentence-boundary-aware splitting (split on `\n\n` or `.\n`, not mid-sentence) |
| Memory bank grows too large (770 entries) | Acceptable — SQLite handles millions of rows; FTS5+vec0 are designed for this scale |
| ADR supersession chains need traversal | Store `superseded_by` and `amended_by` as metadata in chunk content; enable follow-up query pattern |
| Re-ingestion after docs change needs idempotency | Use `memory_delete_context` per context label before re-ingesting that context's files; or use hash-based dedup (same path+content = same hash) |

---

## 8. Summary

This design covers a complete pipeline from raw project documentation to scored retrieval baseline:

1. **~150–180 files** identified across 10 knowledge categories
2. **~770 chunks** using type-aware chunking strategies (per-section ADRs, per-heading docs, atomic invariants)
3. **12 typed contexts** for scoped retrieval
4. **Local ONNX embeddings** for zero-cost, reproducible vector search
5. **43 test queries** across 8 categories (A–H) exercising architecture, invariants, skills, rules, history, cross-cutting knowledge, and negative tests
6. **Self-contained HTML scoring form** with per-query 1–5 scoring, reason capture, baseline export/import, and regression comparison
