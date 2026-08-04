<!-- Managed by ai-badger. Source of truth: .ai-badger/CLAUDE.md. Do not edit this copy by hand; edit the source and re-run welcome-ai-badger. -->

# AiRaccoon

C# .NET 10 MCP server exposing agent memory management over sqlite-memory: project-scoped memory bank, workspace sandboxes, shared promotion tier, hybrid search, degradation, and optional cloud sync.

> Domain: Provides AI agents with persistent, project-scoped memory over the Model Context Protocol, backed by sqlite-memory.
> Stacks: dotnet, mcp
> Scaffolded by ai-badger 0.77.2. Source of truth for this file: `.ai-badger/CLAUDE.md`.

## Non-negotiable invariants

- **Ask if a simpler shape would do** — prefer the simpler design; abstractions without callers are costs without buyers.
- **Check the source, not your own reasoning** — re-read docs/data/code before stating facts about them.
- **Guard clauses over hand-rolled null checks** — use CommunityToolkit.Diagnostics guards, fail fast at boundaries.
- **Measure only when the measurement pays** — benchmark when the decision repays the cost; otherwise cite or say unverified.
- **Minimal comments** — 1-3 line doc comments stating the contract; point at ADRs for rationale.
- **No hand-rolled crypto or security orchestration** — delegate to audited, platform-provided libraries.
- **No hardcoded secrets** — read from config/env; fake values in samples/tests.
- **Plain names** — simplest accurate word; rare words only when no common equivalent exists.
- **One PR per task** — every unit of work ends in a PR; never push to main unless explicitly instructed.
- **Done means proven** — evidence the thing works (passing test, green gate), not just code written.
- **Screaming architecture** — organize by domain/business concept, not technical bucket.
- **Small commits, early draft PR** — one coherent work package per commit; draft PR from first commit.
- **TDD is mandatory** — failing behavior-focused test before any production code; implementation follows the test.
- **Releases are traceable** — record version and changes using the project's existing scheme.
- **Clean layering** — domain layer free of framework/persistence/HTTP/SDK deps; new deps need an ADR.
- **High-performance logging** — nested static `Log` class with `[LoggerMessage]` methods instead of direct `ILogger` calls.
- **MCP stays thin** — MCP tools 1:1 map to backend API; no business logic in the MCP layer.

## Commands

- `build`: `dotnet build`
- `test`: `dotnet test`

## Path-specific instructions

Before editing matching files, read the applicable scoped instruction file:

- `documentation.instructions.md` → `.ai-badger/instructions/documentation.instructions.md`
- `csharp.instructions.md` → `.ai-badger/instructions/csharp.instructions.md`
- `mcp.instructions.md` → `.ai-badger/instructions/mcp.instructions.md`

## Agent delegation

- architecture, design decisions, project structure → `architect`
- tests, test strategy, test coverage → `test-engineer`
- code review, quality gates, PR review → `code-reviewer`
- C#/.NET implementation, MCP tools, backend work → `dotnet-engineer`
- Every dispatch names its `model` — the delegation map is `.ai-badger/delegation.md`.

## Prompt markers

This project understands prompt markers (see `.ai-badger/skills/prompt-markers`):

- `h:` / `hint:` — a lead to validate before acting (research first).
- `f:` / `feedback:` — a high-priority correction; adjust immediately.
- `e:` / `extension:` — a request to expand the current task's scope.

<!-- code-review-graph MCP tools -->
## MCP Tools: code-review-graph

**This project has a knowledge graph. Reach for the code-review-graph MCP tools before
Grep/Glob/Read** — they cost fewer tokens and return structural context (callers,
dependents, test coverage) that file scanning cannot. Fall back to Grep/Glob/Read only
where the graph doesn't reach.

Entry points: `semantic_search_nodes_tool` to locate code, `query_graph_tool` to trace
callers/callees/imports/tests, `detect_changes_tool` for review, `get_impact_radius_tool`
for blast radius, `get_architecture_overview_tool` for structure. Each tool's own
description covers the rest; the graph auto-updates on file change.

<!-- Hermes MCP tools -->
## MCP Tools: hermes

Hermes Agent exposes a stdio MCP bridge for connected messaging platforms. Use it when another
agent needs to list conversations, read history, poll live events, send text messages, browse
channels, or manage approval requests through Hermes.

The server is started by the client with `hermes mcp serve`. Read operations use Hermes's session
store without a running gateway; sending messages requires the gateway and its platform adapters.

The common declaration is conditional: ai-badger emits it only when `hermes` resolves on PATH.



## Framework

Skills, personas, and instructions here are managed by ai-badger. Run `welcome-ai-badger`
to re-scaffold after changing `.ai-badger/config.json`, and `feed-badger` to contribute
project-agnostic improvements back to the framework. Provenance: `.ai-badger/manifest.json`.