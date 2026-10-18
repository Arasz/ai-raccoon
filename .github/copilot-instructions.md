<!-- Managed by ai-badger. Source of truth: .ai-badger/copilot-instructions.md. Do not edit this copy by hand; edit the source and re-run welcome-ai-badger. -->

# AiRaccoon

C# .NET 10 MCP server exposing agent memory management over sqlite-memory: project-scoped memory bank, workspace sandboxes, shared promotion tier, hybrid search, degradation, and optional cloud sync.

> Domain: Provides AI agents with persistent, project-scoped memory over the Model Context Protocol, backed by sqlite-memory.
> Stacks: dotnet, mcp, python, github, ai-raccoon, changelog
> Scaffolded by ai-badger 0.199.0. Source of truth for this file: `.ai-badger/copilot-instructions.md`.

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
- Parallel dispatches each name their own `isolation` — lanes sharing a tree share its build output.

## Prompt markers

This project understands prompt markers (see `.ai-badger/skills/prompt-markers`):

- `h:` / `hint:` — a lead to validate before acting (research first).
- `f:` / `feedback:` — a high-priority correction; adjust immediately.
- `e:` / `extension:` — a request to expand the current task's scope.
- `q:` / `queue:` — a queued instruction to analyze and run after active work completes.
- `i:` / `important:` — important: high priority, do not drop it; handle at the next natural boundary.
- `i!:` / `important!:` — immediate emergency interrupt: STOP, pause/cancel active tasks, and react instantly.

A `!` before the colon (`f!:`, `queue!:`) makes any marker interrupt-grade: stop and handle it before the work in flight, never queued behind it. A marker sent mid-turn skips the hook that expands it — apply it yourself.

<!-- code-review-graph MCP tools -->
## MCP Tools: code-review-graph

**This project has a knowledge graph. Reach for the code-review-graph MCP tools before
Grep/Glob/Read** — they cost fewer tokens and return structural context (callers, dependents,
test coverage) that file scanning cannot. Start at `semantic_search_nodes_tool`; fall back to
Grep/Glob/Read only where the graph doesn't reach. Each tool's own description covers the rest.

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

Browser automation over accessibility snapshots: `browser_navigate`, then `browser_snapshot` for
element refs, then `browser_click`/`browser_type` on those refs. Each tool's own description
covers the rest.

<!-- task-graph MCP tools -->
## MCP Tools: task-graph

`task-graph` owns the decomposed task plan — a DAG of `step`s carrying status, acceptance criteria and evidence, persisted beside the task tracker in the project's `tracking.db`.

Plan time: `plan_create` validates and stores the DAG; `plan_replace` revises it before
execution starts; `steps_ready` returns the dispatchable frontier and its waves. Execution:
`step_start`, `step_complete` (evidence + AC results), `step_fail`, `step_skip`, `ac_check`;
`progress_checklist` is the status view, and `plan_get`, `step_get`, `plan_export` read back.

No project `.mcp.json` (worktree sessions) → use the CLI twin `task_graph_cli.py <tool> --json`; Hermes operators add the proposed `mcp_servers:` entry to `~/.hermes/config.yaml`.



## Non-negotiable invariants

Each rule's full text is `.ai-badger/invariants/<name>.md`.

- **Announce parallel work** (`announce-parallel-work`) — When another agent session shares the project, announce started work, opened PRs, review requests and merges on the project bus — read `multi-agent-communication` when parallel work is active.
- **Diagrams default to Archify** (`archify-diagrams`) — Author architecture, workflow, sequence, data-flow and lifecycle diagrams with the `archify` skill when it is installed.
- **Ask if a simpler shape would do** (`ask-if-simpler`) — Before calling any design or change finished, ask whether it is over-engineered and what the simpler version would look like.
- **Check the source, not your own reasoning** (`check-sources-not-yourself`) — Re-read the docs, the data and the code before stating a fact about them — those are what go stale, get misremembered, or change under you.
- **Consolidated restart** (`consolidated-restart`) — After two failed revision turns, restart the task with a single merged prompt instead of continuing the same thread and compounding drift.
- **Critical instruction placement** (`critical-instruction-placement`) — Keep the most important requirements in the first or last block of the prompt; do not bury them in the middle where they are easy to miss.
- **Derive the list, or delete it** (`derive-or-delete-the-list`) — A hand-maintained list meant to mirror something else — the gates on disk, the copies of a helper, the skills in the catalog — drifts the moment someone adds to one side and not the other, and nothing notices because nothing compares them.
- **Few-shot only for format** (`few-shot-format-only`) — Start zero-shot and use examples only when the output format is the failure mode; otherwise the task should be solvable from the specification alone.
- **Final output schema separation** (`final-output-schema-separation`) — Keep free-form reasoning separate from the final output schema and emit the schema last so the output is auditable and machine-checkable.
- **Grounded feedback** (`grounded-feedback`) — Every correction must cite failing checks, compiler output, validator output, or source evidence before proposing a new patch.
- **Guard clauses over hand-rolled null checks** (`guard-clauses`) — Prefer a dedicated guard/throw-helper for argument validation over hand-rolled `x ?? throw ...` or ad hoc `if (x == null) throw` blocks — a guard reads as intent, not boilerplate, and keeps the exception type/message consistent across the codebase.
- **Measure only when the measurement pays** (`measure-when-it-pays`) — Run your own benchmark or experiment when the time it costs is repaid by the decision it settles, and not otherwise.
- **Minimal comments** (`minimal-comments`) — Write a doc comment as the 1-3 line contract an editor shows on hover — what the thing is and how to use it — and never let a PR, issue or ticket reference appear anywhere in it.
- **Let git write git's own storage** (`never-hand-edit-the-git-dir`) — Write a git dir through `git config`, `git remote` and `git branch --set-upstream-to`, each of which takes the config lock and rewrites only the key it was given.
- **Use platform security APIs** (`no-hand-rolled-crypto`) — Always use the platform's built-in security and crypto APIs.
- **Store secrets outside tracked files** (`no-hardcoded-secrets`) — Keep credentials, connection strings, API keys, and tokens in environment variables, secret managers, or user-scoped config.
- **One-turn specification** (`one-turn-specification`) — State the objective, constraints, data sources, and success criteria in the first turn; keep the final ask last so the prompt is explicit and executable without a long back-and-forth.
- **Run what you changed; the pipeline runs the rest** (`pipeline-runs-the-rest`) — Run the build and the tests your change touches, and let the pipeline run everything else — a full local sweep buys no coverage the pipeline does not already have and spends the same time twice.
- **Plain names** (`plain-names`) — Name things with the simplest accurate word — variables, functions, types, files, folders, flags.
- **Positive constraints and validation** (`positive-constraints-and-validation`) — Prefer positive requirements and validator-backed checks over long negative instruction lists; specify the expected outcome and the validation gate directly.
- **One PR per task** (`pr-per-task`) — Every unit of work ends in a pull request; never push directly to the main/trunk branch.
- **Done means proven** (`proof-of-done`) — Every unit of planned work carries its acceptance criteria and the gate that checks them, named before the work starts.
- **A check you have not seen fail is not a check** (`prove-the-check-fails`) — Put the defect a gate, test or acceptance criterion exists to catch in front of it, watch it go red, take the defect away and watch it go green — a check that has only ever passed is indistinguishable from one whose comparison can produce a single answer that looks like success.
- **Reasoning scaffolding minimization** (`reasoning-scaffolding-minimization`) — Avoid step-by-step CoT plans on modern reasoning models unless the task is genuine symbolic reasoning; state the goal and constraints instead of narrating the process.
- **Screaming architecture** (`screaming-architecture`) — Organize folders and modules by domain/business concept, not by generic technical bucket.
- **Small commits, early draft PR** (`small-commits-early-draft-pr`) — Commit one coherent work package at a time and push often.
- **Route state transitions through a state machine** (`state-transitions-through-a-machine`) — Where a domain object has explicit states, make the declared transitions the only way it moves between them, and record what triggered each move.
- **TDD is mandatory** (`tdd-mandatory`) — Write a failing, behavior-focused test before any production code change.
- **Run each suite once; CI runs the rest** (`test-run-economy`) — Normal flow runs the modified surface's suite once — plus the suites of anything that consumes the changed behavior, so an API change pulls the frontend end-to-end that exercises it — and leaves the full suite to CI on push.
- **Tests are designed before they are written, and judged after** (`tests-are-designed-and-reviewed`) — Green is the floor, not the evidence: a test list comes out of the acceptance criteria before the first test is written (`design-tests`, each row naming the failure mode it targets and the mutation that proves it real), and a change that adds or alters tests is not done until something other than its author has run `review-tests` and asked whether that suite could have gone red.
- **Tool schema and success criteria outrank persona prose** (`tool-schema-success-criteria`) — Define explicit tool contracts, required parameters, stop conditions, and success predicates before polishing role text; in short-horizon work, a clear schema plus outcome check beats a clever persona.
- **Releases are traceable** (`traceable-releases`) — Every release records the version it went out at and what changed in it, using whatever version marker and release notes this project already keeps.
- **Clean layering** (`clean-architecture-layering`) — Keep the domain/pure-logic layer free of framework, persistence, HTTP, and third-party-SDK dependencies.
- **High-performance logging** (`high-performance-logging`) — Use a nested static partial `Log` class with static `[LoggerMessage]`-attributed methods (taking `ILogger` as a parameter, with an explicit `EventId`) instead of calling `logger.LogInformation(...)`/`LogError(...)` etc. directly — it avoids boxing/allocation on the hot path and keeps event ids centrally discoverable.
- **Static classes: extensions, constants, and pure functions only** (`static-classes`) — Static classes are allowed for extensions, constants, and pure functions — no state, no I/O, no injectable dependencies.
- **MCP stays thin** (`mcp-thin`) — An MCP server maps its tools 1:1 onto the backend REST/API surface and holds no business logic of its own.
- **Pin actions to a commit SHA; declare least-privilege permissions** (`pin-actions-to-sha`) — Every third-party GitHub Action referenced in a workflow is pinned to a full commit SHA, never a tag or branch — a mutable tag is remote code you re-fetch on every run, not a fixed dependency.
- **Always bump VERSION and add changelog entry** (`version-changelog-required`) — Every release — no matter how small — must:
  1. Bump `VERSION` (semver patch for fixes, minor for features, major for breaking changes)
  2. Add a `docs/changelog/{version}-{slug}.md` entry describing what changed
  3. Update `docs/changelog/README.md` if adding a new changelog format convention

## Framework

Skills, personas, and instructions here are managed by ai-badger. Run `welcome-ai-badger`
to re-scaffold after changing `.ai-badger/config.json`, and `feed-badger` to contribute
project-agnostic improvements back to the framework. Provenance: `.ai-badger/manifest.json`.
