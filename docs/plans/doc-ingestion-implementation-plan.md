# Doc Ingestion Pipeline & Retrieval Test Suite — Implementation Plan

**Task:** `doc-ingestion-and-tests`
**Source design:** `job-search-ai-assistant-ingestion-pipeline.md` — removed from this repo 2026-08-07; it belongs to the `job-search-ai-assistant` project.
**Project:** AiRaccoon — C# .NET 10 MCP server over sqlite-memory
**Date:** 2026-08-04

---

## Overview

Implement the document ingestion pipeline and retrieval test suite for ingesting the `job-search-ai-assistant` project's documentation (~150–180 files, ~770 chunks) into AiRaccoon's memory store, then run 35 baseline queries to establish a scored retrieval baseline.

### Deliverables

| # | Deliverable | File |
|---|---|---|
| 1 | Ingestion script | `scripts/ingest-jsaa-docs.py` |
| 2 | Baseline query definitions | `scripts/baseline-queries.json` |
| 3 | Scoring HTML form | `scripts/scoring-form.html` |
| 4 | Test runner | `scripts/run-baseline-queries.py` |
| 5 | Baseline results (first run) | `scripts/baseline-results.json` (gitignored, generated) |

---

## Dependencies Map

```
                    ┌──────────────────┐
                    │ AiRaccoon MCP    │
                    │ server running   │
                    └────────┬─────────┘
                             │
              ┌──────────────┼──────────────┐
              ▼              ▼              ▼
    ┌─────────────────┐ ┌───────────┐ ┌──────────────┐
    │ SECTION A       │ │ SECTION B │ │ SECTION C    │
    │ Ingestion script│ │ Baseline  │ │ Scoring HTML │
    │ (Python)        │ │ queries   │ │ form (HTML)  │
    └────────┬────────┘ │ JSON      │ └──────┬───────┘
             │           └─────┬─────┘        │
             │                 │              │
             ▼                 ▼              │
    ┌─────────────────┐ ┌───────────┐         │
    │ SECTION D       │ │ Test      │         │
    │ Ingestion       │ │ runner    │         │
    │ verification    │ │ (Python)  │         │
    └────────┬────────┘ └─────┬─────┘         │
             │                 │              │
             └────────┬────────┘              │
                      ▼                       │
              ┌───────────────┐               │
              │ SECTION E     │◄──────────────┘
              │ E2E gate      │
              │ (run all,     │
              │  verify)      │
              └───────────────┘
```

**Parallel work:** Sections A, B, and C can proceed simultaneously. Section D is sequential after A. Section E gates everything.

---

## Pre-requisites (All Sections)

- AiRaccoon MCP server built and running (`dotnet run` from `src/AiRaccoon/`)
- `python3` available with `pip` for `mcp` client package
- `job-search-ai-assistant` repo present at `/Users/arasz/RiderProjects/job-search-ai-assistant`
- The `job-search-ai-assistant-ingestion-pipeline.md` design doc is the source of truth (held by the `job-search-ai-assistant` project; removed from this repo 2026-08-07)

---

## Section A: Ingestion Script (`scripts/ingest-jsaa-docs.py`)

**Can run in parallel with:** Sections B, C

### A1: MCP Client Setup

Implement a lightweight Python HTTP client to talk to the AiRaccoon MCP server over its Streamable HTTP transport at `http://localhost:8080/mcp`.

**Sub-tasks:**
1. AiRaccoon MCP server started with `MCP_TRANSPORT=http` (launch settings at `src/AiRaccoon/Properties/launchSettings.json`). Server listens on `http://localhost:8080/mcp` via `MapMcp("/mcp")`.
2. Implement `AiRaccoonClient` class using `httpx` with `async with` context manager:
   - `async memory_configure(project_id, provider, **kwargs)` — POST JSON-RPC
   - `async memory_write(project_id, content, context, agent_id)` — POST JSON-RPC
   - `async memory_embed_pending(project_id, limit=None)` — POST JSON-RPC (omit `limit` for all)
   - `async memory_stats(project_id)` — POST JSON-RPC
   - `async memory_search(project_id, query, limit=5, min_score=0.0)` — POST JSON-RPC
   - `async memory_delete_context(project_id, context)` — POST JSON-RPC
3. Handle MCP JSON-RPC protocol: `tools/call` with camelCase parameter names matching AiRaccoon's schema (`projectId`, not `project_id`).
4. Log each tool call with timing for progress visibility.

**Design-to-implementation gaps:**
- `memory_write` has NO `path` parameter (confirmed: only `projectId, content, workspaceId?, agentId?, context?`). AiRaccoon auto-generates `path = SHA256(content).hex + ".md"`. The structured path (`docs:adr/0011-...#decision`) must be embedded in chunk content as `## Source: <path>`.
- `memory_search` has NO `context` filter (confirmed: only `scope` = all/project/shared). All baseline queries use `scope="project"`. Context-scoped cross-cutting queries (G1-G3) are deferred.
- `memory_embed_pending` takes `int? limit` — omit/null means "all", not `limit=0`.
- AiRaccoon requires `full` access mode for destructive ops (`memory_delete_context`).

### A2: File Enumeration & Classification

Implement the `enumerate_files()` function that walks the JSAA project tree applying the INCLUDE/EXCLUDE rules from the design doc §3.3.

**Sub-tasks:**
1. Walk `/Users/arasz/RiderProjects/job-search-ai-assistant` with `pathlib.Path.rglob()`
2. Apply include globs (exact paths per design §3.3)
3. Apply exclude globs (`.ai-badger/state.json`, `docs/work/`, etc.)
4. Classify each file into its type by path pattern:
   - `adr` — `docs/adr/*.md`
   - `architecture` — `docs/architecture.md`, `docs/data-model.md`, `docs/flows.md`, `docs/requirements.md`, `docs/CHANGELOG.md`
   - `explanation` — `docs/explanation/*.md`
   - `howto` — `docs/how-to/*.md`
   - `reference` — `docs/reference/*.md`
   - `rules` — `docs/rules/*.md`
   - `invariants` — `.ai-badger/invariants/*.md`
   - `skills` — `.ai-badger/skills/*/SKILL.md`
   - `agents` — `.ai-badger/agents/*.md`
   - `instructions` — `.ai-badger/instructions/*.md`
   - `config` — `.ai-badger/config.json`, `.ai-badger/delegation.md`, `.ai-badger/copilot-instructions.md`
   - `agent-model` — `.ai-badger/agent-instructions/*`
   - `remember` — `.remember/recent.md`, `.remember/archive.md`
   - `tutorials` — `docs/tutorials/*.md`
   - `legacy` — `docs/legacy/*.md`
   - `meta` — `docs/meta/{baseline.json,trust-debt.md,trust-index.json}`
   - `root-md` — `README.md`, `CLAUDE.md`, `HERMES.md`, `REVIEW.md`
   - `infra` — `infra/README.md`
5. Log a summary: total files found, breakdown by type, any paths that couldn't be read

**Acceptance criteria:**
- All files from design §1.1 marked "Yes" or "Selective" are found
- No files from the EXCLUDE list are included
- Classification is deterministic (by path prefix, not content heuristics)
- Files that don't exist at the expected path are logged as warnings, not errors

**Quality gate:** Run `python3 scripts/ingest-jsaa-docs.py --dry-run` and verify:
- Output lists ~150–180 files
- Each file has the correct type classification
- No excluded paths appear in the output

### A3: Content-Type Chunking

Implement chunking strategies per the design doc §3.4 table.

**Sub-tasks:**
1. Define a `Chunk` dataclass: `path (str), content (str), context (str)`
2. Implement chunkers by type:
   - **ADR chunker:** Parse Nygard sections (Context, Decision, Consequences, Alternatives) by `## ` headers. Emit Title+Status as chunk #1, then one chunk per section. Combine very short ADRs (<1000 chars total) into single chunks.
   - **Heading chunker** (architecture, reference, root-md, explanation, howto, tutorials, legacy, infra): Split by `## ` headers. Prepending H1 title as context prefix. Max ~3000 chars per chunk.
   - **Atomic chunker** (invariants, agents, instructions, config, agent-model, meta): One file = one chunk. No splitting.
   - **Skill chunker:** One chunk per SKILL.md. If `references/` subdirectory exists, each reference file becomes a separate chunk with `context` = `ai-badger:skills:<name>:references`.
   - **Remember chunker:** Split by `## Week of` or `## ` date headers. Each temporal section = one chunk.
   - **Rule chunker:** Parse markdown tables. Each rule row becomes a chunk. Preamble text prepended to each chunk for context.
3. Each chunk's `path` field: `<source-prefix>:<relative-path>#<section-identifier>` (per design §3.4)
4. Each chunk's `context` field: one of the 12 typed context labels
5. Aggregate: expect ~770 total chunks

**Acceptance criteria:**
- ADRs (<1000 chars total) produce a single chunk, not 5 tiny ones
- Heading-level chunks include the H1 title as a prefix for standalone retrieval
- Invariants, agents, instructions produce exactly one chunk per file
- Skill chunks include reference files as separate entries
- Rule rows each have preamble context prepended
- Sentence boundaries are respected (split on `\n\n` or `.\n`, not mid-sentence)

**Quality gate:** Run chunking logic in isolation (without MCP writes):
```bash
python3 scripts/ingest-jsaa-docs.py --dry-run --chunk-only
```
Verify:
- ~770 total chunks
- ~425 ADR chunks (85 ADRs × 5, minus combined short ADRs)
- 22 invariants → exactly 22 chunks
- Each chunk has non-empty `path`, `content`, `context`
- Context labels are from the 12-type set

### A4: Batch Write Pipeline

Implement the write loop: configure → write batches → embed between batches.

**Sub-tasks:**
1. `memory_configure(projectId="job-search-ai-assistant", provider="local")`
2. Iterate chunks in batches of 50:
   - For each chunk: `memory_write(projectId="job-search-ai-assistant", content=chunk.content, context=chunk.context, agentId=chunk.path)`
   - After batch: `memory_embed_pending(projectId="job-search-ai-assistant")` (omit limit to process all pending)
3. After all batches: final `memory_embed_pending(projectId="job-search-ai-assistant")`
4. Progress logging: `[batch N/M] wrote 50 chunks, 300/770 embedded`

**Acceptance criteria:**
- All ~770 chunks are written without errors
- No more than 50 chunks pending at any time (batch size)
- `memory_stats` shows `pending: 0` after final embed
- `memory_stats` shows `entries: ~770` across `contexts: ~12`

**Quality gate:**
```bash
python3 scripts/ingest-jsaa-docs.py --ingest-only
```
Then verify:
- Exit code 0 (no unhandled exceptions)
- `memory_stats(projectId="job-search-ai-assistant")` returns `pending: 0`
- Entry count within ±5% of 770

### A5: Verification & Spot-Checks

Run the design's verification queries (§3.7).

**Sub-tasks:**
1. `memory_stats(projectId="job-search-ai-assistant")` — log to console
2. Run spot-check searches:
   - "what is the frontend component library?" — expect ADR-0011
   - "TDD policy" — expect invariants/tdd-mandatory
   - "Cosmos DB partition key strategy" — expect invariants/partition-by-userid
   - "channel monitoring architecture" — expect ADR-0024 or ADR-0061
3. Print summary: entry count, context breakdown, spot-check results

**Acceptance criteria:**
- All 4 spot-check queries return relevant results in top 3
- At least 2 of 4 spot-checks find the exact expected source at rank #1

**Quality gate:** Run the full ingestion + spot-checks:
```bash
python3 scripts/ingest-jsaa-docs.py --verify
```
All 4 spot-checks pass with relevant results.

### A6: Hash Map Export

Export a JSON map of structured paths → pre-computed SHA256 hashes for the test runner's `isExpectedSource` matching.

**Sub-tasks:**
1. For each chunk, compute the expected hash using AiRaccoon's two-step formula:
   - `assigned_path = SHA256(chunk.content).hex() + ".md"` (matches `WritePathFor`)
   - `expected_hash = SHA256(UTF8(assigned_path) + UTF8(chunk.content)).hex()` (matches `ContentHash.Of(path, value)`)
2. Build a `{structured_path: expected_hash}` dictionary: `{"docs:adr/0011-frontend-chassis-stack.md#decision": "abc123def456...", ...}`
3. Write `scripts/chunk-hash-map.json` — loaded by the test runner in Section D
4. The test runner matches `result.hash` (from `MemorySearchResult`) against expected hashes from this map.

**Acceptance criteria:**
- Every ingested chunk has an entry in the hash map
- Hash computation matches AiRaccoon's `SHA256(path_bytes + content_bytes).hex + ".md"` formula
- File is valid JSON, readable by `run-baseline-queries.py`

**Quality gate:**
```bash
python3 -c "import json; m = json.load(open('scripts/chunk-hash-map.json')); assert len(m) >= 700, f'Expected 700+ entries, got {len(m)}'"
```

---

## Section B: Baseline Queries JSON (`scripts/baseline-queries.json`)

**Can run in parallel with:** Sections A, C

### B1: Define the 43 Query Objects

Create `scripts/baseline-queries.json` with all 43 queries from the design doc §4.2, categories A–H.

**Sub-tasks:**
1. Define JSON schema per query:
   ```json
   {
     "id": "A1",
     "category": "Architecture Decisions (ADR)",
     "query": "Why was shadcn/ui chosen over gluestack.io?",
     "expectedKnowledge": "...",
     "expectedSource": "docs:adr/0011-frontend-chassis-stack.md#decision",
     "contextScope": "project",
     "searchLimit": 5,
     "negativeTest": false
   }
   ```
2. Transcribe all 43 queries from design §4.2 A–H tables
3. For negative tests (H1–H3), set `"negativeTest": true` and `"expectedSource": null`
4. Category mapping:
   - A1–A7 → `Architecture Decisions (ADR)`
   - B1–B6 → `System Architecture & Knowledge`
   - C1–C6 → `Invariants & Conventions`
   - D1–D4 → `Skills & Agent Workflows`
   - E1–E3 → `Domain Rules (Seed Data)`
   - F1–F3 → `Project History & Identity`
   - G1–G3 → `Cross-Cutting (Multi-Context)`
   - H1–H3 → `Negative Tests`

**Acceptance criteria:**
- 43 query objects, each with unique `id` (A1–H3)
- Exactly 8 categories with correct counts per design (7+6+6+4+3+3+3+3 = 35 positive + 3 negative = 38? Wait — recalculating: A=7, B=6, C=6, D=4, E=3, F=3, G=3, H=3 = 35 queries? No — let me recount from the design: A=7, B=6, C=6, D=4, E=3, F=3, G=3, H=3 = 35 total. But the design says 43. Re-reading... Actually the design says 43 queries but the tables show different counts. I'll use the counts from the tables as authoritative since those are what's actually defined.
- Actually re-reading: A1-A7 (7), B1-B6 (6), C1-C6 (6), D1-D4 (4), E1-E3 (3), F1-F3 (3), G1-G3 (3), H1-H3 (3) = 35 queries. The plan says "43 queries" in the design summary — this may include category A's queries and the design has grown during writing. Let me just comment on it.
  Actually the design says 43 in the summary but the tables show 35. I'll note this discrepancy and go with the tables (35).

**Quality gate:**
```bash
python3 -c "import json; qs = json.load(open('scripts/baseline-queries.json')); \
  assert len(qs) == 35, f'Expected 35 queries, got {len(qs)}'; \
  assert all(k in q for q in qs for k in ['id','category','query']); \
  print(f'OK: {len(qs)} queries validated')
```

---

## Section C: Scoring HTML Form (`scripts/scoring-form.html`)

**Can run in parallel with:** Sections A, B, D

### C1: HTML Structure & Layout

Build the self-contained HTML form per design §5.2 layout diagram.

**Sub-tasks:**
1. Single HTML file: no framework, no build step, no server
2. CSS Grid layout with left sidebar (category nav + progress) + main area (query cards)
3. Header bar: project name, ingestion metadata, action buttons (Export, Load, Compare)
4. Per-query card:
   - Category badge + query ID
   - Query text display
   - Expected source display
   - Top-5 results panel (rank, score, path, snippet, expected-source indicator)
   - Score radio buttons (1–5) with labels per design §5.4
   - Reason textarea
   - Prev/Next navigation
5. Summary panel (collapsible): per-category average scores, overall total
6. Progress bar

**Acceptance criteria:**
- Opens in any modern browser without a server (`file://` protocol)
- CSS Grid renders correctly at 1024px+ widths
- All 35 query cards render from loaded JSON
- Navigation (Prev/Next) works with keyboard (left/right arrows)

**Quality gate:** Open `scripts/scoring-form.html` in Safari:
- Verify all 35 query cards are present
- Verify Prev/Next navigation cycles through all cards
- Verify sidebar category links jump to correct card
- Verify progress bar updates on navigation

### C2: Scoring Logic & State

Implement scoring interactions and state persistence.

**Sub-tasks:**
1. Radio button click → update score in state
2. Reason textarea → update reason in state
3. `localStorage` persistence: save after every score change
4. Load from `localStorage` on page open
5. "Save All Scores" button → persists all
6. Progress bar: `N/35 scored`

**Acceptance criteria:**
- Score persists across page reload (localStorage)
- Progress bar updates correctly (0/35 → 35/35)
- Score 3 = Adequate (expected source found but ranked 3–5) label matches design

**Quality gate:**
1. Score 5 queries with different scores
2. Reload page — all 5 scores persist
3. Check localStorage key contains expected JSON

### C3: JSON Export/Import

Implement baseline JSON import and scored JSON export.

**Sub-tasks:**
1. "Load Baseline" button → `<input type="file">` → parse JSON → populate query cards
2. "Export Baseline" button → `<a download>` → generate scored JSON with all scores + reasons
3. Export format matches design §5.3 schema
4. Import validation: check required fields, warn on missing data

**Acceptance criteria:**
- Load a `baseline-results.json` file → all 35 query cards populate with results
- Export after scoring → downloaded JSON contains all scores + reasons
- Re-importing exported JSON restores scores and reasons

**Quality gate:**
1. Load `baseline-results.json` (from test runner output)
2. Score queries A1 (4), A2 (5), C1 (5)
3. Export → verify JSON has 35 entries, 3 with scores
4. Clear localStorage, re-import → verify 3 scores restored

### C4: Regression Compare View

Implement side-by-side comparison between two scored baselines.

**Sub-tasks:**
1. "Compare" button → load second JSON file
2. Compute per-query deltas (new_score - old_score)
3. Display regressions (score dropped by ≥1) and improvements (score increased by ≥1)
4. Per-category delta summary
5. Highlight regressions in red, improvements in green

**Acceptance criteria:**
- Loading two JSON files with different scores shows correct deltas
- Query A1: 4→2 shows as regression (-2) in red
- Query G3: 3→5 shows as improvement (+2) in green

**Quality gate:**
1. Export baseline as `v1.json`
2. Manually edit `v1.json` to change a few scores (simulate regression)
3. Load original + edited → verify compare view flags the changed queries

---

## Section D: Test Runner (`scripts/run-baseline-queries.py`)

**Depends on:** Section A (ingestion complete), Section B (queries JSON)

### D1: Query Execution Loop

Implement the script that runs all 35 baseline queries against AiRaccoon.

**Sub-tasks:**
1. Load `scripts/baseline-queries.json`
2. Connect to AiRaccoon MCP server (reuse `AiRaccoonClient` from Section A)
3. For each query object:
   - Call `memory_search(projectId="job-search-ai-assistant", query=q.query, limit=5, scope="project", minScore=0.0)`
   - Map results to `{hash, ranking, path, snippet}`
   - Annotate `isExpectedSource` by matching `result.hash` against pre-computed SHA256 hashes from the ingestion chunk map (see A6). AiRaccoon auto-generates `path = SHA256(content).hex + ".md"` — structured paths like `docs:adr/0011-...#decision` are embedded in content but never appear in `result.path`. Use a hash lookup table exported by the ingestion script.
4. For cross-cutting queries (G1–G3): optionally run additional context-scoped searches (deferred — requires `memory_search` context parameter not yet available)
5. Output: `scripts/baseline-results.json` in design §4.4 format
6. Print summary: queries run, expected-source matches, average result count

**Acceptance criteria:**
- All 35 queries execute without errors
- Output JSON has 35 `queryResults` entries
- Each result contains `{hash, ranking, path, snippet}`
- `isExpectedSource` flag is set when `result.hash` matches the ingestion chunk's pre-computed hash
- At least 25 of 35 queries find their expected source at rank ≤ 3

**Quality gate:**
```bash
python3 scripts/run-baseline-queries.py
```
Verify:
- Exit code 0
- Output file `scripts/baseline-results.json` exists
- `jq '.queryResults | length' scripts/baseline-results.json` → 35
- `jq '[.queryResults[] | select(.results[0].isExpectedSource or .results[1].isExpectedSource or .results[2].isExpectedSource)] | length' scripts/baseline-results.json` → ≥ 25

---

## Section E: Ingestion Verification & Cleanup

**Depends on:** Section A (ingestion complete)

### E1: Stats & Context Verification

Verify ingestion produced the expected state in AiRaccoon.

**Sub-tasks:**
1. `memory_stats(projectId="job-search-ai-assistant")` — assert:
   - `entries` between 730–810 (~770)
   - `pending` = 0
   - `contexts` count = ~12
2. `memory_list(projectId="job-search-ai-assistant")` — spot-check a few paths
3. Run negative-test queries (H1–H3) to verify excluded content is NOT found

**Acceptance criteria:**
- Entry count within 5% of 770
- Zero pending embeddings
- H1–H3 queries return no results referencing excluded files

**Quality gate:**
```bash
python3 scripts/ingest-jsaa-docs.py --verify-only
```
All stats assertions pass. Negative tests pass.

### E2: Idempotency (Re-ingestion)

Verify the pipeline can be re-run after clearing.

**Sub-tasks:**
1. Ensure the project is in `full` access mode (re-ingestion requires destructive `memory_delete_context`; rw mode denies it). Run `memory_set_access(projectId="job-search-ai-assistant", mode="full")` before cleanup.
2. `memory_delete_context(projectId="job-search-ai-assistant", context="...")` for each of the 12 contexts
3. Re-run ingestion
4. Verify stats match first run (±0 entries because deterministic)

**Acceptance criteria:**
- Re-ingestion produces identical entry count
- No duplicate entries (same path+content = same hash, handled by AiRaccoon)

**Quality gate:** Run twice, compare `memory_stats` output — entry counts identical.

---

## Section F: End-to-End Integration Gate

**Depends on:** All previous sections complete

### F1: Full Pipeline Run

Execute the complete pipeline end-to-end and verify all outputs.

**Steps:**
1. Start fresh AiRaccoon server (or clear job-search-ai-assistant project)
2. Run `python3 scripts/ingest-jsaa-docs.py --ingest-only` — verify stats
3. Run `python3 scripts/run-baseline-queries.py` — verify 35 results
4. Open `scripts/scoring-form.html` — load `baseline-results.json`
5. Score all 35 queries (1–5 with reasons)
6. Export scored baseline → `scored-baseline-2026-08-04.json`
7. Verify export format is valid JSON, all 35 entries have scores

**Acceptance criteria:**
- Ingestion produces ~770 entries, 0 pending
- Test runner produces 35 query results
- Scoring form loads and scores all queries
- Export produces valid scored baseline

**Quality gate:**
```bash
# 1. Clean start
python3 scripts/ingest-jsaa-docs.py --reset  # clears old data

# 2. Ingest
python3 scripts/ingest-jsaa-docs.py --ingest-only
# → Exit 0, stats show ~770 entries, pending=0

# 3. Run baseline
python3 scripts/run-baseline-queries.py
# → Exit 0, 35 results

# 4. Manual: open scoring-form.html in browser
# → Load baseline-results.json, verify all 35 queries display results

# 5. Manual: score all queries, export
# → scored-baseline-2026-08-04.json has 35 scored entries
```

---

## Risk Register

| Risk | Probability | Impact | Mitigation |
|---|---|---|---|
| `memory_search` lacks `context` filter | **Confirmed** | Medium — cross-cutting queries (G1–G3) can't be context-scoped | File issue; G1–G3 run as project-scoped only for now |
| `memory_write` lacks `path` parameter | **Confirmed** | Low — embed path in content header instead | Use markdown header `## Source: <path>` at top of each chunk |
| ~770 chunks → embedding takes too long | Low | Medium — local ONNX may be slow | Batch size of 50; consider `provider=openai` with local LM Studio as fallback |
| Chunk boundaries cut mid-sentence | Medium | Low — affects retrieval quality | Sentence-boundary-aware splitting (split on `\n\n` or `.\n`) |
| JSAA repo structure has changed from design | Medium | Medium — missing files, new directories | `--dry-run` first; log warnings for missing files, don't fail |
| MCP client Python package API changes | Low | Low | Pin `mcp` version; use stdio transport (stable) |

---

## Implementation Order (Recommended)

1. **Sections A1+A2** (MCP client + file enumeration) — foundation for everything else
2. **Parallel fork:**
   - **Section A3** (chunking) + **Section A4** (batch writes)
   - **Section B** (baseline queries JSON)
   - **Section C1** (HTML structure)
3. **Section A5** (verification spot-checks) — validates ingestion quality
4. **Section D** (test runner) — depends on A + B
5. **Section C2+C3+C4** (scoring logic + export/import + compare) — less urgent, can trail
6. **Section E** (cleanup + idempotency) — gate before declaring done
7. **Section F** (E2E gate) — final validation

---

## Files to Create

| File | Section | Type |
|---|---|---|
| `scripts/ingest-jsaa-docs.py` | A1–A5 | Python script (~500 lines) |
| `scripts/baseline-queries.json` | B1 | JSON data file |
| `scripts/scoring-form.html` | C1–C4 | Single HTML file |
| `scripts/run-baseline-queries.py` | D1 | Python script (~100 lines) |
| `scripts/baseline-results.json` | D1 output | Generated (gitignored) |
| `.gitignore` | — | Add `scripts/baseline-results.json` and `scripts/scored-baseline-*.json` |

---

## Issues to File (Non-Blocking)

1. **AiRaccoon: `memory_search` missing `context` filter** — The tool doesn't support context-scoped search. Cross-cutting queries (G1–G3) rely on this. Low priority; file for future enhancement.
2. **AiRaccoon: `memory_write` missing `path` parameter** — The design assumes a `path` provenance field. Workaround: embed in content. File for future enhancement.
3. **Query count discrepancy** — Design says 43 queries but tables show 35 (7+6+6+4+3+3+3+3). Resolve before Section B implementation. Likely the 43 includes some duplicate counting or the design evolved during writing. Check the design doc tables as authoritative.

---

**Plan status:** Ready for execution.
**Next action:** Begin Section A1 (MCP client setup) after AiRaccoon server is confirmed running.
