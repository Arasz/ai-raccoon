---
name: semantica-knowledge-graph
description: >-
  Use when reasoning over structured project knowledge — record decisions with provenance,
  trace causal chains, extract entities from conversations, or run graph analytics.
  Complements AiRaccoon memory (recall) with structured reasoning (connections and causality).
version: 0.1.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [knowledge-graph, decision-tracking, causal-reasoning, provenance]
    related_skills: [ai-raccoon-memory, mcp-index, hermes-mcp-setup]
---

# semantica-knowledge-graph

Semantica is a session-scoped knowledge graph MCP server (MIT, v0.6.5+). In-memory graph state accumulates within a session but dies on process exit.

## When NOT to Use

- A one-off fact lookup — use `memory_search` (AiRaccoon) first.
- No decision or extraction needed — skip graph ceremony.
- Durable facts outliving the session — write to AiRaccoon (`memory_write`).

## Workflows

### 1. Decision recording
1. `record_decision(category="...", scenario="...", reasoning="...", outcome="...", confidence=0.85)`
2. `add_entity` for key concepts
3. `add_relationship(source="...", target="...", relationship_type="...")`
4. Cite decision id in commits or PRs for traceability

### 2. Entity extraction
- **Option 2 (Agent-Guided — Primary)**: Use LLM reasoning to extract domain concepts and call `add_entity` + `add_relationship`. Instantaneous, zero dependencies.
- **For Code Structures**: Use `code-review-graph` MCP tools (`semantic_search_nodes_tool`, `find_callers`, `find_dependents`) for code symbol graphs.
- **Option 1 (Native Local ML)**: Optional `extract_entities` / `extract_relations` via PyTorch/HuggingFace (`pip install torch transformers`). Degrades if ML deps missing.
- Verify with `get_graph_summary()`.

### 3. Decision archaeology
1. `query_decisions(query="keyword")` → find decisions
2. `get_causal_chain(decision_id="...")` → trace ancestry
3. `find_precedents(scenario="...")` → check prior patterns

### 4. Graph export & AiRaccoon persistence pattern
To prevent data loss from Semantica's ephemeral process:
1. **Export auto-saves per session**: the hook saves each `export_graph` result to `.semantica/<session>.json`, per-session and timestamped, so parallel sessions never collide. Wired but inert on 0.6.6 — see Gotchas.
2. **Watch the directory once**: The `ai-raccoon-memory` skill registers a one-time directory watch on `.semantica/` via `memory_watch_add(projectId, <absolute path to .semantica>)`; re-adding is a no-op.
3. **Structural JSON Integration**: AiRaccoon ingests every `.semantica/` file, parses graphs and decisions, and embeds them into its persistent SQLite memory bank (`memory.db`).
4. **Cross-Session Retrieval**: `memory_search` in AiRaccoon returns both textual decision rationale and structural JSON graph relations.
5. **`.semantica/` is local staging**: gitignore `.semantica/` in the consumer repo — the durable record lives in ai-raccoon memory, not the repo.

## Escalation by result

- **Graph empty** → `get_graph_summary` returns zero nodes; record decision or entities first
- **No precedent** → record decision now so it becomes a precedent for next time
- **Causal chain incomplete** → add missing intermediate entities/relationships, re-query

## AiRaccoon complementarity

- AiRaccoon (`memory_search`): "what do we know?" — semantic recall over indexed docs
- Semantica (`query_decisions`): "how are things connected?" — structured reasoning over graph
- Durable facts → `memory_write` (AiRaccoon); ephemeral causal reasoning → Semantica

## Gotchas

- **Session-scoped only**: in-memory graph dies on process exit. No `import_graph` mechanism exists.
- **Extraction ML deps**: `extract_entities` needs `torch` + `transformers`, not LLM API keys.
- **Parameter names**: `add_relationship` uses `source`/`target`, not `source_id`/`target_id`.
- **Known issue**: `get_graph_analytics` unavailable in 0.6.5 — use `get_graph_summary`.
- **Upstream export bug (0.6.5/0.6.6)**: *every* `export_graph` format errors, no fix
  released — an empty `.semantica/` is this bug, not a broken bridge. Graph tools work.
  RDF writes progress to **stdout**, corrupting MCP; the entry sets
  `SEMANTICA_DISABLE_PROGRESS=1`.
- **All agents auto-save and nudge**: export autosave is wired for Hermes (plugin),
  Claude Code (PostToolUse), Copilot (postToolUse) — every `export_graph` result lands in
  `.semantica/`. The once-per-session export guidance nudge is active across all three hosts.

## Verification Checklist

- [ ] `get_graph_summary` returns node/edge counts reflecting session activity
- [ ] At least one decision recorded and findable via `query_decisions`
- [ ] AiRaccoon `memory_search` and Semantica `query_decisions` return complementary results
- [ ] `export_graph` auto-saves to `.semantica/` (per-session, timestamped) for AiRaccoon directory-watch ingestion
