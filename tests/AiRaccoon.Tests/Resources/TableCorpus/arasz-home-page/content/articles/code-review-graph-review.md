---
id: 4
title: "code-review-graph: When Your AI Agent Needs a Map of Your Codebase"
slug: code-review-graph-review
publishedAt: "2026-07-26T10:00:00Z"
updatedAt: "2026-07-26T10:00:00Z"
author: "Rafał Araszkiewicz"
description: "A practical look at code-review-graph — how it builds a structural knowledge graph of your codebase, what it actually found in two real projects, and whether the token savings are real."
tags: [AI, Agents, Code Review, MCP, Developer Tools, Knowledge Graph, Tree-sitter]
status: published
categories: [agent-memory]
---

## The problem: AI agents re-read everything

Every time you ask an AI coding agent to review a PR or understand a codebase, it reads files. Lots of files. On a 1,200-file project, a single review question can burn through 200,000+ tokens just establishing context — before it even starts thinking about your question.

I hit this wall repeatedly during the full-project review of my job-search assistant. Seven parallel review agents, each needing to understand the architecture, trace call paths, and find blast-radius impacts. Each agent was re-reading the same files the others had already parsed. The context cost was enormous.

That is the problem [code-review-graph](https://github.com/tirth8205/code-review-graph) solves. It parses your repository into a [structural knowledge graph](https://en.wikipedia.org/wiki/Knowledge_graph) — a network where functions and classes are [nodes](https://en.wikipedia.org/wiki/Vertex_(graph_theory)) and calls, imports, and inheritance are [edges](https://en.wikipedia.org/wiki/Glossary_of_graph_theory#edge) — stored locally in SQLite, and exposes it via MCP so your AI agent queries the graph instead of re-reading the codebase.

I ran it on two projects and then ran a controlled A/B refactor to see whether it changes agent output, not just token counts. The headline result surprised me: the graph made my agent **slower and more thorough**, not faster. It finished 9% behind on wall-clock time — and covered 5 of 5 target components where the agent without it silently stopped at 3.

## What it found in my projects

The two projects are deliberately mismatched — a 1,200-file polyglot monorepo and a small Angular site — and what the graph was good for differed accordingly.

Every graph number below was measured on 2026-07-26 with code-review-graph v2.3.7. They move on every rebuild as the codebases change — treat them as a snapshot, not a constant.

### Job-search assistant (large .NET + React monorepo)

| Metric | Value |
|--------|-------|
| Nodes | 8,512 |
| Edges | 76,070 |
| Files | 1,243 |
| Communities | 18 |
| Languages | C#, TypeScript/TSX, JavaScript, Python, HCL, Bash |

The [community detection](https://en.wikipedia.org/wiki/Community_structure) immediately confirmed what I already knew architecturally: clean layering, no cross-community coupling warnings. Backend (C#), frontend (TypeScript), infrastructure (HCL), and tooling (Python) were well-separated. Community detection clusters code entities that are tightly connected internally — think of it as automatic module discovery.

But the graph surfaced things I did not know:

**The compensation calculator was the deepest execution flow.** Five levels deep, 35 nodes across 13 files — the longest call chain in the repository, and among the top three by criticality score. This is the feature that later turned out to have the most critical bugs. The graph pointed at it before the review found them.

**apiFetch was the second most critical [bridge node](https://en.wikipedia.org/wiki/Bridge_(graph_theory)).** If the API client breaks, everything downstream disconnects. A bridge node sits on the shortest path between many other nodes — it is an architectural chokepoint. The [betweenness centrality](https://en.wikipedia.org/wiki/Betweenness_centrality) calculation (how often a node appears on shortest paths between all other pairs) made it quantifiable — and pointed at the specific test file that should cover it.

**Test methods looked like architectural hotspots.** All top-15 [hub nodes](https://en.wikipedia.org/wiki/Centrality#Degree_centrality) (the most connected entities in the graph) were xUnit async Task methods (730 connections for the top one). These aggregate many mock setups and assertions — they are test infrastructure, not production risk. The tool gives you structural data, not judgment. This is the single most important caveat to understand before trusting the output.

### Personal homepage (small Angular + Azure Functions)

| Metric | Value |
|--------|-------|
| Nodes | 1,291 |
| Edges | 13,056 |
| Files | 155 |
| Communities | 10 |
| Languages | TypeScript, JavaScript, Python, HCL, Bash |

On a smaller project, the analysis was tighter and more actionable:

**The article model DSL was the highest-[blast-radius](https://en.wikipedia.org/wiki/Blast_radius) code.** Blast radius measures how far a change to one function propagates through callers and dependents. The `text()`, `bold()`, `paragraph()` constructors in `article.ts` had 378, 169, and 148 connections respectively — and no direct unit tests. Coverage came only transitively through build-articles integration tests. A regression in any of them would break the entire article build pipeline.

That one was worth acting on, so I wrote `article.spec.ts` — direct unit tests for every constructor in the DSL. Then I rebuilt the graph and asked it again. `tests_for` still returned zero results. The tests exist, they are in the graph, and the tool does not connect them to the functions they cover, because it links tests by resolving call edges and these are Vitest `describe('text')` blocks that name their subject in a string. Worth knowing before you read a coverage gap as ground truth: an empty `tests_for` means "no edge found," not "no test."

**Infrastructure had the highest [cohesion](https://en.wikipedia.org/wiki/Cohesion_(computer_science)) (0.688).** Cohesion measures how tightly connected the members of a cluster are to each other. Terraform files are tightly coupled by resource references, which is expected — but having it quantified confirms the infra layer is well-structured.

**Dead code analysis had zero actionable items.** All 79 flagged symbols were false positives from Angular template bindings, Terraform declarative blocks, and agent runtime hooks. The report categorized every single one. The job-search assistant had the same pattern: 2,354 flagged, ~50-100 genuinely removable after filtering framework false positives. Every project will have a different false-positive profile.

## How it works

The tool has three layers:

**1. Parser.** Tree-sitter (with targeted fallbacks) extracts functions, classes, imports, call sites, inheritance, and test detection from 30+ languages — Python, TypeScript, C#, Go, Rust, Java, Terraform HCL, and more. It handles monorepos, Jupyter notebooks, Vue/Svelte SFCs, and even Ansible playbooks.

**2. Graph.** The parsed structure goes into a local SQLite database as nodes (functions, classes, files) and edges (calls, imports, inheritance, test coverage). Community detection via the [Leiden algorithm](https://en.wikipedia.org/wiki/Leiden_algorithm) clusters related code. Incremental updates re-parse only changed files in under 2 seconds.

**3. MCP interface.** The graph is exposed to AI coding agents through the Model Context Protocol. Your agent asks questions like "what calls this function?" or "what's the blast radius of this change?" and gets precise, structured answers instead of re-reading files.

<!-- DIAGRAM: crg-pipeline -->
<!-- kind: flowchart -->
<!-- title: code-review-graph Pipeline -->
<!-- node: repo:Repository -->
<!-- node: parser:Tree-sitter Parser -->
<!-- node: graph:SQLite Graph -->
<!-- node: mcp:MCP Server -->
<!-- node: agent:AI Agent -->
<!-- edge: repo->parser -->
<!-- edge: parser->graph -->
<!-- edge: graph->mcp -->
<!-- edge: mcp->agent:structured answers -->
<!-- edge: agent->mcp:queries -->

The MCP tools I use most:

| Tool | What it does |
|------|-------------|
| `detect_changes_tool` | Maps a git diff to affected functions, flows, and test gaps with risk scores |
| `get_impact_radius_tool` | Traces blast radius from changed files through callers and dependents |
| `query_graph_tool` | Pattern-based queries: callers_of, callees_of, tests_for, imports_of |
| `get_architecture_overview_tool` | Community structure with coupling warnings |

Those are 4 of the roughly 30 MCP tools it registers; the rest cover flows, communities, hub and bridge nodes, wiki generation, and cross-repo search.

`detect_changes_tool` is the workhorse for code review. It takes a git diff, maps it to the graph, and returns risk-scored functions with their blast radius. Instead of reading entire files, the agent gets exactly the snippets it needs.

## The experiment: graph-guided vs blind refactoring

I ran a controlled experiment to test whether the graph actually improves agent output quality, not just token efficiency. Two AI agents got the identical task: review and refactor the experience-section components (5 Angular components, their tests, and templates). Same project, same starting point, same instructions.

**Agent A** had code-review-graph MCP tools. It was instructed to query the graph before reading files — understand the architecture first, then act.

**Agent B** had no graph access. It explored the codebase by reading files directly — the standard approach every AI coding agent uses today.

| Metric | Agent A (with graph) | Agent B (without graph) |
|--------|---------------------|------------------------|
| API calls | 44 | 50 |
| Duration | 721s | 664s |
| Files changed | 12 | 9 |
| Lines added | 115 | 120 |
| New tests | 5 | 6 |
| Components touched | 5/5 | 3/5 |

Agent A used 7 graph queries before reading any files: minimal context, architecture overview, semantic search (×2), and query_graph (×3). It then touched all 5 components. Agent B missed 2 components entirely — it never discovered that `experience-section.ts` and `skill-matrix.ts` had refactorable patterns because it stopped reading after the first few files.

The graph did not make Agent A faster (721s vs 664s). The graph queries add overhead. But it made it more thorough: 12% fewer API calls, 33% more files changed, and complete coverage of the target area. The architecture overview told Agent A exactly which files were connected before it started reading.

The merged refactor (from Agent A) added `computed()` signals replacing getter functions, `aria-describedby` with screen-reader-only description spans on every interactive element, and 5 new accessibility tests. All 362 tests pass.

## Token savings: real or marketing?

The project claims ~82x median token reduction across 6 benchmarked repositories. The 528x figure that gets quoted is the top of a 38x–528x range — one best-case repo (fastapi, the largest corpus), not the typical result. The README says so itself, which is a good sign.

My experience: on the job-search assistant (1,243 files), a typical architecture question that would require reading 15-20 files (~30,000 tokens) gets answered by a graph query returning ~500 tokens. That is roughly 60x reduction — in line with the benchmarks.

But the savings are uneven. For broad questions ("what does this codebase do?"), the graph helps less — you still need to read representative files. For targeted questions ("what calls this function?", "what breaks if I change this?"), the savings are dramatic. The tool is optimized for review and impact analysis, not for onboarding.

The honest caveat from the README: "Impact recall 1.0 is graph-derived and circular." The ground truth for measuring accuracy comes from the same graph edges the predictor walks. The real-world co-change metrics are lower. This is not dishonesty — the tool documents it clearly — but it means the headline numbers should be taken as an upper bound.

## Limitations I hit

**Flow detection is weak in some languages.** The README admits 33% recall for execution flow detection. Python and PHP/Laravel are strongest; JavaScript and Go need work. On my TypeScript-heavy projects, flow detection was hit-or-miss.

**Small single-file changes can cost more tokens with the graph.** For trivial edits, the structural metadata overhead exceeds naive file reads. The tool shines on multi-file changes and architectural questions.

**Test links depend on call edges.** As the `article.spec.ts` case above showed, tests that name their subject in a string rather than calling it directly never get a `TESTED_BY` edge. That makes "untested hotspot" a lead to check, not a verdict.

**You still need to filter.** Dead code and refactoring suggestions are candidates, not conclusions — and, as the 79-vs-zero split on my homepage showed, the false-positive rate depends entirely on which frameworks you use.

## Installation and setup

```bash
pip install code-review-graph   # or: pipx install code-review-graph
code-review-graph install       # auto-detects your AI tools (Claude Code, Codex, Cursor, Copilot, Hermes, 10+ others)
code-review-graph build         # parse your codebase (~10 seconds for 500 files)
```

**Semantic search (optional):** By default, the graph uses FTS5 keyword matching for search — structural queries (callers_of, tests_for, impact radius, change detection) work fine without embeddings. For true vector-based semantic search, install the embeddings extra:

```bash
pip install "code-review-graph[embeddings]"   # pulls sentence-transformers + PyTorch
code-review-graph embed                       # compute vectors (~30s for 1,000 nodes)
```

This downloads ~80MB for the default `all-MiniLM-L6-v2` model and requires Python 3.10+. If you skip it, `semantic_search_nodes_tool` silently falls back to keyword matching — which is sufficient for most code review workflows. Cloud embedding providers (OpenAI, Google Gemini, MiniMax) are also supported if you'd rather avoid the local PyTorch dependency.

If you use [ai-badger](https://github.com/arasz/ai-badger) (the multi-agent framework I built), code-review-graph is available as an integrated external tool — the framework's MCP index discovers it automatically and routes graph queries to it when your agent needs structural context. No manual MCP configuration needed beyond the initial `code-review-graph install`.

Incremental updates happen automatically via hooks or watch mode. There is also a GitHub Action that posts risk-scored review comments on PRs — sticky comments updated on every push, with an optional `fail-on-risk` merge gate. The graph builds and queries entirely on your CI runner; no source code leaves your infrastructure.

## Is it worth checking?

**Yes, if you:**
- Use AI coding agents on projects with 200+ files
- Do code reviews where blast-radius analysis matters
- Want your agent to answer "what calls this?" without re-reading the codebase
- Run a monorepo where context cost is the bottleneck

**Skip it if you:**
- Work on small projects (< 100 files) where reading everything is fine
- Need deep semantic understanding (the graph is structural, not semantic)
- Expect zero false positives in dead code analysis

As of 2026-07-26 the tool is at v2.3.7 with 733 commits, 26,500 GitHub stars, and commits landing within the last day. It is MIT-licensed, local-first, and needs no cloud service. The MCP integration means it works with essentially every AI coding tool on the market.

For my workflow — running multi-agent reviews on .NET + React monorepos — it has become a standard part of the setup. The architecture overview alone saves me 30 minutes of orientation on every new codebase I touch. The blast-radius analysis during the job-search assistant review directly led to finding bugs that file-scanning had missed for months.

The tool does not replace reading code. But it tells you which code to read first.

---

The project is open source: [tirth8205/code-review-graph on GitHub](https://github.com/tirth8205/code-review-graph).
