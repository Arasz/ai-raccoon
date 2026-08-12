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

Semantica is a session-scoped knowledge graph MCP server (MIT, v0.6.5+). Every MCP invocation shares one in-memory graph — entities, relationships, and decisions accumulate across tool calls within a session but do not survive a restart.

## When NOT to Use

- A one-off lookup where the fact already exists in indexed docs — use `memory_search`
  (AiRaccoon) first.
- No decision was made and no entity needs extraction — skip the graph ceremony.
- Trivia or facts you won't query again — the graph is for active reasoning within a session, not a permanent store. Durable facts go to AiRaccoon (`memory_write`).

## Workflows

### 1. Decision recording

When making an architectural or design decision:

1. `record_decision(category="...", scenario="...", reasoning="...", outcome="...", confidence=0.85)` — persist with full context
2. `add_entity` for key concepts the decision references
3. `add_relationship(source="...", target="...", relationship_type="...")` to link decision to affected components
4. Cite the decision id in commit messages or PR descriptions for traceability

### 2. Entity extraction

When analyzing a document, conversation, or spec for structured facts:

1. `extract_entities(text)` → structured entity list
2. `extract_relations(text)` → triplet relations
3. `get_graph_summary()` → verify the graph reflects the extraction

Note: `extract_entities` and `extract_relations` need `torch` and `transformers` for full NLP. Without them, entity extraction returns types without names and relation extraction returns empty. Install with `pip install torch transformers`.

### 3. Decision archaeology

When asking "why did we do this?":

1. `query_decisions(query="keyword")` → find relevant decisions
2. `get_causal_chain(decision_id="...")` → trace full ancestry
3. `find_precedents(scenario="...")` → has this pattern appeared before?

### 4. Graph export for audit trails

When you need a record of session decisions:

1. `export_graph(format="json")` → produces an archival snapshot
2. Note: there is no `import_graph` — exported graphs cannot be re-loaded. The graph is session-scoped only. For durable facts, write to AiRaccoon.

## Escalation by result

- **Graph is empty** → `get_graph_summary` returns zero nodes; start with entity extraction or decision recording
- **No precedent found** → record the decision now so it becomes a precedent for the next inquiry
- **Causal chain incomplete** → add missing intermediate entities/relationships, then re-query
- **Extraction returns empty** → torch/transformers may be missing; try manual `add_entity`/`add_relationship` instead

## AiRaccoon complementarity

- AiRaccoon (`memory_search`): "what do we know?" — semantic recall over indexed documents
- Semantica (`query_decisions`): "how are things connected?" — structured reasoning over the graph
- Use both: search AiRaccoon for context first, then trace relationships in Semantica
- Durable facts → `memory_write` (AiRaccoon); decisions and causal chains → Semantica (session-scoped)

## Gotchas

- **Session-scoped only**: the graph is in-memory and does not survive a restart. There is no import mechanism — `export_graph` produces an archival snapshot only. For facts that must outlive the session, write to AiRaccoon memory.
- **Extraction needs ML deps**: `extract_entities` and `extract_relations` need `torch` and `transformers` for full NLP, not an LLM API key. Without them, extraction returns degraded results.
- **Parameter names**: `add_relationship` uses `source`/`target` parameter names, not `source_id`/`target_id`.
- **Structured fields**: `record_decision` expects structured fields — don't paste raw text.
- **Known issue**: `get_graph_analytics` is not available in this version. Use `get_graph_summary` for graph statistics instead.

## Verification Checklist

- [ ] `get_graph_summary` returns node/edge counts reflecting the session's activity
- [ ] At least one decision was recorded and is findable via `query_decisions`
- [ ] `extract_entities` was used on at least one document or conversation
- [ ] AiRaccoon `memory_search` and Semantica `query_decisions` return complementary, non-overlapping results
- [ ] `export_graph` produces valid JSON when an audit trail is needed
