# AiRaccoon

C# .NET 10 MCP server exposing agent memory management over sqlite-memory: project-scoped memory bank, workspace sandboxes, shared promotion tier, hybrid search, degradation, and optional cloud sync.

> Domain: Provides AI agents with persistent, project-scoped memory over the Model Context Protocol, backed by sqlite-memory.
> Stacks: dotnet, mcp, python, github, ai-raccoon
> Scaffolded by ai-badger 0.134.1. Source of truth for this file: `.ai-badger/HERMES.md`.

## Commands

- `build`: `dotnet build`
- `test`: `dotnet test`

## Path-specific instructions

Before editing matching files, read the applicable scoped instruction file:

- `docs/**/*.md,README.md,CLAUDE.md` → `.ai-badger/instructions/documentation.instructions.md`
- `**/*.cs,**/*.csproj,Directory.Build.props,Directory.Packages.props` → `.ai-badger/instructions/csharp.instructions.md`
- `**/*Mcp*/**` → `.ai-badger/instructions/mcp.instructions.md`
- `**/*.py` → `.ai-badger/instructions/python.instructions.md`
- `**/.github/workflows/*.yml,**/.github/workflows/*.yaml` → `.ai-badger/instructions/github-actions.instructions.md`

Additional invariants load contextually via these paths — see `.ai-badger/invariants/` for the full set.

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
- `q:` / `queue:` — a queued instruction to analyze and run after active work completes.
- `i!:` / `important!:` — immediate emergency interrupt: STOP, pause/cancel active tasks, and react instantly.

A marker is expanded by a `UserPromptSubmit` hook, which fires only when a message **starts a turn**. A message sent **mid-turn** — queued while work is already running — reaches the model as an attachment and never passes through that hook, so its marker is never expanded. Apply the behaviour above yourself whenever you see a marker arrive that way.

<!-- code-review-graph MCP tools -->
## MCP Tools: code-review-graph

**This project has a knowledge graph. Reach for the code-review-graph MCP tools before
Grep/Glob/Read** — they cost fewer tokens and return structural context (callers, dependents,
test coverage) that file scanning cannot. Start at `semantic_search_nodes_tool`; fall back to
Grep/Glob/Read only where the graph doesn't reach. Each tool's own description covers the rest.

<!-- Hermes MCP tools -->
## MCP Tools: hermes

Read operations use Hermes's session store and work without a running gateway; sending messages
needs the gateway and its platform adapters. The server's own tool descriptions cover the rest.

<!-- ai-raccoon MCP tools -->
## MCP Tools: ai-raccoon

AiRaccoon is the project memory server. Search memory FIRST — before web search, code search, or
asking the user — with `memory_search` (projectId, scope=all) and 2-3 query formulations. Entries
carry source paths, so a decisive hit is evidence: cite it. Escalate by result — a partial hit gets
one targeted external search; no hit means search externally, then write the finding back with
`memory_write` including the source path.

Every call passes projectId. Plain writes land in committed project memory; active workspaces
isolate in-progress notes and consolidate on finish; `memory_share` promotes durable cross-project
facts. Keep the docs directory searchable: check `memory_watch_status`, then `memory_watch_add`
(projectId + absolute path) when no watch exists.

<!-- semantica MCP tools -->
## MCP Tools: semantica

Semantica is the project knowledge graph. It complements AiRaccoon: AiRaccoon answers
"what do we know?"; Semantica answers "how are things connected?" and "why was this
decision made?".

Start with `get_graph_summary` for orientation. Record architectural decisions with
`record_decision`. Drill into specifics with `query_decisions`, `find_precedents`, or
`get_causal_chain`. Each tool's own description covers the rest.

<!-- Playwright MCP tools -->
## MCP Tools: playwright

The Playwright MCP server provides browser automation capabilities through the Model
Context Protocol, enabling LLMs to interact with web pages using structured accessibility
snapshots without requiring vision models.

Start with `browser_navigate` to load the target URL. Use `browser_snapshot` to capture the
page's accessibility tree and element reference IDs (`ref=...`). Interact with elements using
`browser_click`, `browser_type`, `browser_fill_form`, or `browser_select_option` referencing
those IDs. Capture visual evidence with `browser_take_screenshot`. Monitor API calls with
`browser_network_requests` and debug issues with `browser_console_messages`. For multi-step
or complex interactions, execute custom Playwright scripts with `browser_run_code_unsafe`.
Each tool's own description covers the rest.

## Non-negotiable invariants

These 8 rules are always loaded. The full set (22 rules) lives in `.ai-badger/invariants/` and loads contextually via path-specific instructions.

- **TDD is mandatory** — Write a failing, behavior-focused test before any production code change.
  → `.ai-badger/invariants/tdd-mandatory.md`

- **Done means proven** — Every unit of planned work carries its acceptance criteria and the gate that checks them, named before the work starts.
  → `.ai-badger/invariants/proof-of-done.md`

- **Check the source, not your own reasoning** — Re-read the docs, the data and the code before stating a fact about them — those are what go stale, get misremembered, or change under you.
  → `.ai-badger/invariants/check-sources-not-yourself.md`

- **Use platform security APIs** — Always use the platform's built-in security and crypto APIs. Implementing key derivation, token signing, or encryption-at-rest yourself introduces vulnerabilities.
  → `.ai-badger/invariants/no-hand-rolled-crypto.md`

- **Store secrets outside tracked files** — Keep credentials, connection strings, API keys, and tokens in environment variables, secret managers, or user-scoped config — never in tracked code, examples, or fixtures.
  → `.ai-badger/invariants/no-hardcoded-secrets.md`

- **Clean layering** — Keep the domain/pure-logic layer free of framework, persistence, HTTP, and third-party-SDK dependencies.
  → `.ai-badger/invariants/clean-architecture-layering.md`

- **Plain names** — Name things with the simplest accurate word — variables, functions, types, files, folders, flags.
  → `.ai-badger/invariants/plain-names.md`

- **Ask if a simpler shape would do** — Before calling any design or change finished, ask whether it is over-engineered and what the simpler version would look like.
  → `.ai-badger/invariants/ask-if-simpler.md`

## Framework

Skills, personas, and instructions here are managed by ai-badger. Run `welcome-ai-badger`
to re-scaffold after changing `.ai-badger/config.json`, and `feed-badger` to contribute
project-agnostic improvements back to the framework. Provenance: `.ai-badger/manifest.json`.
