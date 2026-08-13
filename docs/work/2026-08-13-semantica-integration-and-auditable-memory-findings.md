# Semantica Integration, Memory Persistence Bridge, and Auditable Decision Chains

**Date:** 2026-08-13  
**Authors:** AiRaccoon & Hermes Agent  
**Target:** `ai-raccoon`, `ai-badger`, and `semantica` knowledge graph ecosystem

---

## Executive Summary

This record documents the integration architecture and empirical findings for combining **Semantica** (session-scoped knowledge graph MCP server) with **AiRaccoon** (persistent project-scoped memory bank over sqlite-memory) and **ai-badger** (agent scaffolding and hook orchestration).

Key breakthrough finding: **Combining `ai-badger` and `AiRaccoon` automates the creation and indexing of structured memories, enabling AI agents to retrieve auditable reasoning chains ("why" decisions were made) across sessions without manual ceremony or data loss.**

---

## 1. The 3-Tool Knowledge Triad

In an agentic workflow, knowledge retrieval requires three distinct modalities:

| Tool | Focus Question | Primary Capability & Mechanism |
|---|---|---|
| **AiRaccoon** (`memory_search`) | *"What do we know?"* | Persistent hybrid vector + BM25 search over indexed markdown docs, research, and notes. |
| **Semantica** (`query_decisions`, `get_causal_chain`) | *"How are things connected and why?"* | In-memory causal graph tracking entities, decision rationale, confidence, and precedents. |
| **Code-Review-Graph** (`find_callers`, `find_dependents`) | *"How is code wired?"* | AST symbol graph, callers/callees, cyclomatic complexity, and code structural metrics. |

---

## 2. Ephemerality & The Persistence Bridge Pattern (ADR-0019)

### The Challenge
Semantica (`semantica-mcp` v0.6.5+) operates entirely in-memory for zero-latency graph traversal during an active agent session. When the process or CLI session exits, the graph resets.

### The Persistence Bridge Solution
1. **Atomic Graph Exporter**: `.ai-badger/skills/semantica-knowledge-graph/scripts/export_semantica_graph.py` takes a snapshot of the in-memory graph schema (`nodes`, `edges`, `decisions`, `metadata`) and performs an atomic write to `.ai-raccoon/semantica-graph.json`.
2. **AiRaccoon Watch Bridge**: AiRaccoon registers a live file watch (`memory_watch_add`) on `.ai-raccoon/semantica-graph.json`.
3. **Structured JSON Chunking**: AiRaccoon's `MarkdownChunker` and `TokenizerChunker` automatically digest the graph JSON file, chunking entities, relations, and decision rationale into `memory.db`.
4. **Cross-Session Retrieval**: In future sessions, `memory_search` in AiRaccoon returns both textual decision rationale and exact structural JSON graph relations.

---

## 3. Auditable Reasoning Chains in Memory

By pairing `ai-badger` prompt hooks with `AiRaccoon` memory ingestion, decision rationale is captured automatically as work occurs.

### Empirical Case Study (Session 2026-08-13)
When queried about *why* specific skills were added in this session:
- **`ai-raccoon-state-checklist`**: Added to automate pre-flight system state checks after global package updates and `ai-raccoon serve` restarts, producing a dated audit resource (`.ai-raccoon/state-checklist-20260813.json`).
- **`ai-text-humanization`**: Added following empirical literature research (`docs/research/llm-humanization-research.md`) identifying 9 humanization levers that reduce AI output detection by 80–90%.
- **`refactoring-fix`**: Added following PR #265 lessons where `dotnet build` silently skipped broken files; enforces `dotnet clean && dotnet build` and domain-type compensation rules.

Querying `memory_search` retrieves the exact causal chain (`Problem -> Research/Trigger -> Solution -> Verified Outcome`) directly from the memory bank.

---

## 4. Next Steps & Blog Article Outline

This architecture will be featured in a blog post on `arasz.me` (`arasz-home-page` repository):
1. **Semantica Overview**: External open-source session-scoped knowledge graph tool (`semantica-mcp`). Where to find it, how to install it.
2. **Integration with `ai-badger` & `ai-raccoon`**: Bridging in-memory graph ephemerality with durable SQLite vector+BM25 memory.
3. **Automated Auditable Memory**: How agents automatically record and retrieve auditable decision chains ("why" decisions were made) without human friction.
