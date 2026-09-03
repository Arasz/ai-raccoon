# Delegation map — AiRaccoon

> Scaffolded by ai-badger 0.161.1. Regenerated on every scaffold; do not edit.

## Stacks

dotnet, mcp, python, github, ai-raccoon

## Personas available here

- `architect` — Architecture and decomposition specialist. Lane: opus.
- `code-reviewer` — Quality and security review gate. Lane: opus.
- `delegator` — Work-routing lead for multi-package sessions. Lane: opus.
- `dotnet-engineer` — .NET implementation engineer. Lane: sonnet.
- `qa` — Test-quality authority. Lane: opus.
- `qa-backend` — QA for .NET server-side code. Lane: opus.
- `test-engineer` — Testing specialist. Lane: sonnet.

## Routing (config.json personaRouting)

- architecture, design decisions, project structure → `architect`
- tests, test strategy, test coverage → `test-engineer`
- code review, quality gates, PR review → `code-reviewer`
- C#/.NET implementation, MCP tools, backend work → `dotnet-engineer`

## Parallel dispatch

Lanes running at the same time need their own tree, not just their own files:
agents sharing a checkout share its build output, so a green run proves nothing
about the change that produced it. Dispatch with your agent tool's native
`isolation` rather than a hand-made worktree. The `dispatch-gate` hook denies a
write-capable dispatch that names none while a sibling lane is live; read-only
lanes are exempt. Worked cases live in `.ai-badger/skills/worktree-agent-isolation`.

## Reasoning-model dispatch

When dispatching to a reasoning model (opus, o-series, Claude extended
thinking, DeepSeek-R1), adjust the prompt:

- **State goals and success criteria only** — strip prescriptive step-by-step
  plans, CoT scaffolding, and few-shot examples. These constrain the model's
  internal search and reduce quality.
- **Keep the system prompt short** — elaborate prompts constrain reasoning
  model search space (Anthropic). Prefer "what done looks like" over "how to
  get there."
- **Use API parameters for depth control** — `reasoning_effort` (OpenAI) or
  `thinking_budget_tokens` (Anthropic) instead of prompt-side "think harder."

For standard instruction-tuned models (sonnet, flash), the existing
prescriptive persona descriptions are appropriate.

## Verifiers

- `build`: `dotnet build`
- `test`: `dotnet test`

## MCP servers reachable here

- `ai-raccoon` — AiRaccoon is the project memory server
- `code-review-graph` — This project has a knowledge graph
- `hermes` — Read operations use Hermes's session store and work without a running gateway; sending messages needs the gateway and its platform adapters
- `playwright` — The Playwright MCP server provides browser automation capabilities through the Model Context Protocol, enabling LLMs to interact with web pages using structured accessibility snapshots without requiring vision models
- `semantica` — Semantica is the project knowledge graph
