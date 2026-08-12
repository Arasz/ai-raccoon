# AiRaccoon

C# .NET 10 MCP server exposing agent memory management over sqlite-memory: project-scoped memory bank, workspace sandboxes, shared promotion tier, hybrid search, degradation, and optional cloud sync.

> Domain: Provides AI agents with persistent, project-scoped memory over the Model Context Protocol, backed by sqlite-memory.
> Stacks: dotnet, mcp, python, github
> Scaffolded by ai-badger 0.116.3. Source of truth for this file: `.ai-badger/copilot-instructions.md`.

## Non-negotiable invariants

- **Ask if a simpler shape would do** — Before calling any design or change finished, ask whether it is over-engineered and what the simpler version would look like.
  → `.ai-badger/invariants/ask-if-simpler.md`

- **Check the source, not your own reasoning** — Re-read the docs, the data and the code before stating a fact about them — those are what go stale, get misremembered, or change under you.
  → `.ai-badger/invariants/check-sources-not-yourself.md`

- **Derive the list, or delete it** — A hand-maintained list meant to mirror something else — the gates on disk, the copies of a helper, the skills in the catalog — drifts the moment someone adds to one side and not the other, and nothing notices because nothing compares them.
  → `.ai-badger/invariants/derive-or-delete-the-list.md`

- **Guard clauses over hand-rolled null checks** — Prefer a dedicated guard/throw-helper for argument validation over hand-rolled `x ?? throw ...` or ad hoc `if (x == null) throw` blocks — a guard reads as intent, not boilerplate, and keeps the exception type/message consistent across the codebase.
  → `.ai-badger/invariants/guard-clauses.md`

- **Measure only when the measurement pays** — Run your own benchmark or experiment when the time it costs is repaid by the decision it settles, and not otherwise.
  → `.ai-badger/invariants/measure-when-it-pays.md`

- **Minimal comments** — Keep doc comments to 1-3 lines stating the contract, not the provenance or rationale — point at an ADR or spec doc for the "why" instead of writing an essay inline.
  → `.ai-badger/invariants/minimal-comments.md`

- **No hand-rolled crypto or security orchestration** — Never implement security/cryptographic orchestration yourself — key derivation, token signing, session/cookie protection, encryption-at-rest schemes.
  → `.ai-badger/invariants/no-hand-rolled-crypto.md`

- **No hardcoded secrets** — No credentials, connection strings, API keys, or tokens in tracked files, examples, or fixtures.
  → `.ai-badger/invariants/no-hardcoded-secrets.md`

- **Run what you changed; the pipeline runs the rest** — Run the build and the tests your change touches, and let the pipeline run everything else — a full local sweep buys no coverage the pipeline does not already have and spends the same time twice.
  → `.ai-badger/invariants/pipeline-runs-the-rest.md`

- **Plain names** — Name things with the simplest accurate word — variables, functions, types, files, folders, flags.
  → `.ai-badger/invariants/plain-names.md`

- **One PR per task** — Every unit of work ends in a pull request; never push directly to the main/trunk branch.
  → `.ai-badger/invariants/pr-per-task.md`

- **Done means proven** — Every unit of planned work carries its acceptance criteria and the gate that checks them, named before the work starts.
  → `.ai-badger/invariants/proof-of-done.md`

- **A check you have not seen fail is not a check** — Put the defect a gate, test or acceptance criterion exists to catch in front of it, watch it go red, take the defect away and watch it go green — a check that has only ever passed is indistinguishable from one whose comparison can produce a single answer that looks like success.
  → `.ai-badger/invariants/prove-the-check-fails.md`

- **Screaming architecture** — Organize folders and modules by domain/business concept, not by generic technical bucket.
  → `.ai-badger/invariants/screaming-architecture.md`

- **Small commits, early draft PR** — Commit one coherent work package at a time and push often.
  → `.ai-badger/invariants/small-commits-early-draft-pr.md`

- **Route state transitions through a state machine** — Where a domain object has explicit states, make the declared transitions the only way it moves between them, and record what triggered each move.
  → `.ai-badger/invariants/state-transitions-through-a-machine.md`

- **TDD is mandatory** — Write a failing, behavior-focused test before any production code change.
  → `.ai-badger/invariants/tdd-mandatory.md`

- **Releases are traceable** — Every release records the version it went out at and what changed in it, using whatever version marker and release notes this project already keeps.
  → `.ai-badger/invariants/traceable-releases.md`

- **Clean layering** — Keep the domain/pure-logic layer free of framework, persistence, HTTP, and third-party-SDK dependencies.
  → `.ai-badger/invariants/clean-architecture-layering.md`

- **High-performance logging** — Use a nested static partial `Log` class with static `[LoggerMessage]`-attributed methods (taking `ILogger` as a parameter, with an explicit `EventId`) instead of calling `logger.LogInformation(...)`/`LogError(...)` etc. directly — it avoids boxing/allocation on the hot path and keeps event ids centrally discoverable.
  → `.ai-badger/invariants/high-performance-logging.md`

- **Static classes: extensions, constants, and pure functions only** — Static classes are allowed for extensions, constants, and pure functions — no state, no I/O, no injectable dependencies.
  → `.ai-badger/invariants/static-classes.md`

- **MCP stays thin** — An MCP server maps its tools 1:1 onto the backend REST/API surface and holds no business logic of its own.
  → `.ai-badger/invariants/mcp-thin.md`

- **Pin actions to a commit SHA; declare least-privilege permissions** — Every third-party GitHub Action referenced in a workflow is pinned to a full commit SHA, never a tag or branch — a mutable tag is remote code you re-fetch on every run, not a fixed dependency.
  → `.ai-badger/invariants/pin-actions-to-sha.md`

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
asking the user — with `memory_search` (project_id, scope=all) and 2-3 query formulations. Entries
carry source paths, so a decisive hit is evidence: cite it. Escalate by result — a partial hit gets
one targeted external search; no hit means search externally, then write the finding back with
`memory_write` including the source path.

Every call passes project_id. Plain writes land in committed project memory; active workspaces
isolate in-progress notes and consolidate on finish; `memory_share` promotes durable cross-project
facts. Keep the docs directory searchable: check `memory_watch_status`, then `memory_watch_add`
(project_id + absolute path) when no watch exists.

<!-- semantica MCP tools -->

## MCP Tools: semantica

Semantica is the project knowledge graph. It complements AiRaccoon: AiRaccoon answers
"what do we know?"; Semantica answers "how are things connected?" and "why was this decision made?".

Start with `get_graph_summary` for orientation. Record architectural decisions with
`record_decision`. Drill into specifics with `query_decisions`, `find_precedents`, or
`get_causal_chain`. Each tool's own description covers the rest.

## Framework

Skills, personas, and instructions here are managed by ai-badger. Run `welcome-ai-badger`
to re-scaffold after changing `.ai-badger/config.json`, and `feed-badger` to contribute
project-agnostic improvements back to the framework. Provenance: `.ai-badger/manifest.json`.
