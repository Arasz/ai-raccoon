# Delegation map — AiRaccoon

> Scaffolded by ai-badger 0.134.1. Regenerated on every scaffold; do not edit.

## Stacks

dotnet, mcp, python, github, ai-raccoon

## Personas available here

- `architect` — Design and decomposition specialist — architecture decisions (module/layer boundaries, extension-point interfaces, folder structure), ADR authoring, multi-file change blueprints, and well-architected-style trade-off analysis (cost vs resilience vs velocity). Lane: opus.
- `code-reviewer` — Independent quality and security gate — OWASP Top 10 (plus OWASP LLM Top 10 when an LLM-integration surface is present) review scoped to a targeted plan (pick the 3-5 relevant risk categories for the diff, not a blanket checklist), two-pass performance/anti-pattern analysis, and adversarial verification of AI-generated claims. Lane: opus.
- `delegator` — Work-routing lead for long, multi-package sessions — decomposes a task into independently verifiable packages, dispatches each to the persona and model lane that fits it, and does only integration, arbitration and gate-running itself. Lane: opus.
- `dotnet-engineer` — Default implementation engineer for .NET codebases — writes and edits C# across the project's layers, TDD-first (failing test before code), SOLID-minded, matching existing conventions (validation library idioms, guard-clause helpers, source-generated logging, current-generation C# features). Lane: sonnet.
- `qa` — Test-quality authority — designs a suite from acceptance criteria before anyone writes it, and audits an existing suite for whether it can fail at all. Lane: opus.
- `qa-backend` — QA for .NET server-side code — the qa persona's judgment applied with this stack's runner, isolation tooling and blind spots: xUnit v3 parallelism and traits, TimeProvider/FakeTimeProvider, Testcontainers and emulator-gated lanes, WebApplicationFactory, Stryker and ArchUnitNET false-negatives. Lane: opus.
- `test-engineer` — Testing specialist — designs test strategy, writes failing tests first, plans phased test coverage (leaf types unmocked → mid-layer with leaf mocks → top-layer), and enforces edit-boundary discipline between test files and production code. Lane: sonnet.

## Routing (config.json personaRouting)

- architecture, design decisions, project structure → `architect`
- tests, test strategy, test coverage → `test-engineer`
- code review, quality gates, PR review → `code-reviewer`
- C#/.NET implementation, MCP tools, backend work → `dotnet-engineer`

## Verifiers

- `build`: `dotnet build`
- `test`: `dotnet test`

## MCP servers reachable here

- `ai-raccoon` — AiRaccoon is the project memory server
- `code-review-graph` — This project has a knowledge graph
- `hermes` — Read operations use Hermes's session store and work without a running gateway; sending messages needs the gateway and its platform adapters
- `playwright` — The Playwright MCP server provides browser automation capabilities through the Model Context Protocol, enabling LLMs to interact with web pages using structured accessibility snapshots without requiring vision models
- `semantica` — Semantica is the project knowledge graph
