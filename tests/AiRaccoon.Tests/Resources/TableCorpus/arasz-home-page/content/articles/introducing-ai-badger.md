---
id: 1
title: "Introducing ai-badger: A Portable Agent Framework for the Multi-Agent Era"
slug: introducing-ai-badger
publishedAt: "2026-07-24T08:30:00Z"
updatedAt: "2026-08-15T12:00:00Z"
author: "Rafał Araszkiewicz"
description: "What ai-badger is, how the stack×feature catalog model works, and why portable agent knowledge matters when you run Claude Code, Hermes, Copilot, and Junie side by side."
tags: [AI, Agents, Open Source, Developer Tooling, Claude Code, Hermes, Copilot]
status: published
categories: [agent-frameworks]
---

> **Updated:** Correction, 2026-08-15. This article presents JetBrains Junie as one of the four agents ai-badger scaffolds for. It no longer does. Junie support was removed entirely in v0.83.0 on 2026-08-06, thirteen days after this published. `features/junie/` is deleted, the agent enums in six schemas dropped it, and the detection, scaffolding and hook-exemption branches are gone. A project config naming `junie` now fails schema validation. Supported agents are exactly three: `claude`, `copilot`, `hermes`. ADR-0016 gives the reason plainly: the owner does not use Junie, and there was no capacity to maintain a fourth agent surface alongside the other three. Existing `.junie/` directories in already-scaffolded projects are left alone, because the framework stopped generating and validating them rather than deleting anyone's files. Every mention of Junie below describes a version that no longer exists: the four-agents-side-by-side opening, the `junie` entry in the stacks list, the Junie column in the feature-support table, and the Junie line under Limitations. A smaller drift, not an error: the "Eight installable skills" count has grown well past eight. `features/common/skills/` alone now holds 38 skill directories, with more filed under individual stacks. The original text stays below unedited.

## What ai-badger is

After months of running Claude Code, Hermes Agent, GitHub Copilot, and JetBrains Junie side by side across real projects, I kept hitting the same wall: every project needed the same guardrails, the same personas, the same workflow conventions — but each agent ecosystem reinvented them differently.

**ai-badger** is a portable agent framework — a stack × feature catalog of skills, personas, invariants, hooks, adjustments, and instructions that installs into any project with `welcome-ai-badger` and harvests improvements back with `feed-badger`. It's not a new agent and not a chatbot wrapper. It's the **shared memory and conventions layer** that sits between you and whatever agent you're using.

## Why it exists

The insight was simple: **project knowledge should be portable across agents**. The TDD rule you taught Claude Code yesterday should apply to Hermes tomorrow. The architecture invariant you documented for Copilot should be enforced by any agent that touches the codebase.

Each agent now has its own native skill system. Claude Code uses SKILL.md files in plugin directories. Copilot uses `.github/skills/` and `.agent.md` files. Hermes uses YAML frontmatter skills in `~/.hermes/skills/`. Junie uses `.junie/AGENTS.md`. ai-badger provides the **shared catalog and scaffolding layer** so you author knowledge once and let the framework render it into each agent's native format.

## The 3-layer model: `features/{stack | common}/{feature}`

Everything in the catalog is filed under a **stack** (a technology) and a **feature** (a kind of asset):

```
features/<stack>/<feature>/<item>
```

**Stacks** — `angular`, `azure`, `cosmos`, `css`, `dotnet`, `github`, `hermes`, `js`, `mcp`, `node`, `python`, `react`, `terraform`, `ts` — plus **`common`** for stack-agnostic content and agent-specific stacks (`claude`, `copilot`, `junie`).

**Features** — the kind of asset:

| Feature | Shape | Purpose |
|---|---|---|
| **personas** | `*.md` | Role definitions for task delegation (architect, test-engineer, code-reviewer) |
| **invariants** | `*.md` | Non-negotiable rules assembled into always-loaded context |
| **instructions** | `*.md` | Scoped guidance for specific file patterns (documentation, TypeScript, CSS) |
| **skills** | `SKILL.md` + scripts | Installable operational skills with extensions |
| **hooks** | `hooks-manifest.json` | Lifecycle hooks (session start, tool call, context injection) |
| **adjustments** | `adjustment.json` | Per-agent scaffold adaptations |

A persona that is genuinely stack-agnostic goes in `common`; anything tied to a specific technology — a routing table entry, a module invariant like *Domain purity* or *single-writer-Cosmos* — is filed under its owning stack.

<!-- CHART: context-cache-cost -->
<!-- kind: bar-horizontal -->
<!-- title: Context Token Cost: Cache Read vs Fresh Write -->
<!-- data: Cache read (Anthropic) 1.0, Fresh write (Anthropic) 10.0, Subagent cold start 10.0 -->
<!-- lowerIsBetter: true -->
<!-- xLabel: Relative cost per token -->
<!-- description: Cache reads cost 1/10th of fresh writes. ai-badger keeps context byte-stable to maximize cache hits. -->

## Two JSON contracts

ai-badger connects to a target project through two schema-validated JSON files:

**`config.json`** — the project profile that the agent authors. It declares stacks, agents, commands, persona routing, source control, and skill scope. Everything downstream is derived from it — which invariants apply, which task extensions activate, what build commands to run. Extensions are **config-gated**: the GitHub PR workflow only activates when `sourceControl.platform == "github"` and a `repoUrl` is set.

**`manifest.json`** — provenance that the scaffolder writes. It records every file the framework placed, its source path, its target path, and a content hash. This is what `feed-badger` diffs against to find candidate improvements, and what `den-refresh` uses to detect drift.

## The hard split: scripts do mechanics, agents do judgment

Every deterministic operation is a Python script with no LLM calls and no network access:

- `detect.py` — scans the repo and user scope to propose a config
- `validate.py` — validates any model against its JSON schema
- `scaffold.py` — materializes `.ai-badger/` from a validated config
- `index_build.py` — generates `index.json` from the catalog tree
- `detect_additions.py` — diffs manifest vs project tree for feed-badger

The agent is reserved for creative judgment only: refining the project summary, classifying improvements as generalizable vs project-specific, choosing feature placement. If a step can be expressed as a deterministic rule, it belongs in a script.

## Skills as the operational backbone

Eight installable skills drive the framework's lifecycle:

| Skill | What it does |
|---|---|
| **welcome-ai-badger** | Bootstrap: detect stacks → config → scaffold |
| **feed-badger** | Harvest project improvements back into the catalog |
| **den-refresh** | Pull framework updates into an already-scaffolded project |
| **task** | Orchestrate backlog tasks with TDD, delegation, and PR workflow |
| **maintain-agent-instructions** | Keep agent instruction files in sync with the catalog |
| **auto-wm** | Autonomous working mode with partner/away transitions |
| **prompt-markers** | Structured markers (`h:`, `f:`, `e:`) for agent communication |
| **mcp-index** | MCP tool index with tag and intent semantic matching |

The `task` skill is particularly interesting: it ships as a **generic base + config-gated extensions**. The base handles orchestration, model delegation, TDD enforcement, and token tracking. Extensions activate based on config — the GitHub extension creates issues and PRs when source control data is present; the Hermes extension maps Claude-specific hooks to native Hermes features.

## Hooks and adjustments: agent-aware scaffolding

Hooks are a first-class feature. A unified `hooks-manifest.json` maps lifecycle events to per-agent hook scripts. Claude Code gets `hooks.json` entries; Hermes gets plugin hooks registered via `ctx.register_hook()`; Copilot uses `.github/hooks/*.json` with eight supported events.

Adjustments handle per-agent scaffold adaptations — when Hermes needs different patterns than Claude Code for the same feature, an `adjustment.json` with targeted scripts bridges the gap at scaffold time rather than at runtime.

## Feature support by agent

> **Warning:** The Junie column in this table is dead. v0.83.0 (2026-08-06) dropped the agent from the catalog `index.json`, from `features/common/support.json`, and from the agent enums in six schemas, so there is no Junie scaffold left to run and no support row left to read. Take the table as three agents plus Generic. ADR-0016 records the removal.

| Feature | Claude Code | Hermes | Copilot | Junie | Generic |
|---|---|---|---|---|---|
| Personas | native | native | native (custom agents) | native | via AGENTS.md |
| Invariants | CLAUDE.md | always-loaded | native | native | via AGENTS.md |
| Cache-aware dispatch | native | native | — | — | — |
| Skill catalog | native | native | native | native | manual |
| Hooks | plugin hooks | plugin + gateway | `.github/hooks/*.json` (8 events) | — | — |
| Adjustments | native | native | — | — | — |
| Feed-back loop | native | native | partial | — | manual |

Copilot's hook system (`.github/hooks/*.json`) covers `sessionStart`, `sessionEnd`, `preToolUse`, `postToolUse`, `userPromptSubmitted`, `agentStop`, `subagentStop`, and `errorOccurred`. Custom agents (`.github/agents/*.agent.md`) define their own tool sets, MCP servers, and behavioral instructions. ai-badger scaffolds all three for Copilot: hooks via `adjust_hooks.py`, skills via `adjust_skills.py` (symlinks into `.github/skills/`), and persona-to-agent mapping via `adjust_agents.py`.

## Two-tier drift detection

The framework tracks staleness at two levels:

**Tier 1 — version drift:** The scaffolded `manifest.json` records the framework version at scaffold time. On every session start (via hooks), the agent checks if the local version differs from the installed framework and suggests running `den-refresh`.

**Tier 2 — new items (v0.8.0):** `den-refresh` walks `index.json` for the project's configured stacks and finds catalog items not present in the manifest. New personas, invariants, or instructions added to the framework since scaffolding are detected as drift and trigger a re-scaffold. This closes the loop: the catalog grows via `feed-badger`, and existing projects pick up new content via `den-refresh`.

## Concept dictionary: one concept, many names

ai-badger maintains a [concept dictionary](https://github.com/Arasz/ai-badger/blob/main/docs/dictionary.md) that maps every framework concept to each agent's native terminology. A "skill" in ai-badger is a "plugin skill" in Claude Code, a "skill" in Hermes, and a `SKILL.md` in `.github/skills/` for Copilot. A "persona" is a custom slash command in Claude Code, a `delegate_task` role in Hermes, and a custom agent (`.github/agents/*.agent.md`) in Copilot. The dictionary keeps everyone speaking the same language.

## Get started

> **Note:** ai-badger is open source and stack-agnostic. Install it into any project, customize the catalog to your stack, and let your agents share the same institutional knowledge.

```bash
# Install into your project (Claude Code)
/plugin marketplace add https://github.com/Arasz/ai-badger
/plugin install ai-badger
welcome-ai-badger

# Or for Hermes Agent
hermes skills install ai-badger
welcome-ai-badger
```

Once scaffolded, run `den-refresh` periodically to pick up new catalog items, and `feed-badger` when your project develops improvements worth sharing.

[View ai-badger on GitHub](https://github.com/Arasz/ai-badger)

## Limitations

> **Warning:** ai-badger is an active framework under development. Known limitations:

- Agent detection is best-effort — `detect.py` checks repo and user scope traces, but non-standard setups may need manual config
- Plugin-level hooks are not auto-wired in all cases — some hooks require manual setup per the skill's docs
- Catalog depth varies by stack — some stacks have full persona/invariant/instruction coverage, others are minimal-but-real and grow via feed-badger
- Junie support is scaffolded (file generation) but lacks hook and skill integration
