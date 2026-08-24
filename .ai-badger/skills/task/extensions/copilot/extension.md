# task extension: copilot

This is a **config-gated extension** of the base `task` skill (`skills/task/`), not a standalone
skill. The base skill names delegation *roles* — "a high-reasoning agent", "a cheap model" —
because it scaffolds for several coding agents. This extension says how those roles map when
the agent is GitHub Copilot, and is deliberately short: it records the shape of the mapping and
sends you to the product for the specifics, rather than hardcoding a model lineup that changes.

**Activates when:** the project's `.ai-badger/config.json` has `"copilot"` in its `agents` array.

## What is different from the Claude lane

The base skill's Phase 3 says "dispatch implementation subagents". Copilot has no in-session
subagent primitive equivalent to Claude Code's `Agent` tool, so **the phases run sequentially in
one session** rather than fanning out. That changes two things and nothing else:

- **Phase boundaries have to be deliberate.** Nothing enforces the separation between planning,
  implementing and reviewing when it is all one conversation. Finish the plan and state it
  before writing code; re-read the diff as a reviewer before declaring the task done.
- **The quality gate is a re-read, not a second opinion.** A separate agent reviewing the work
  is a genuinely different check from the author reviewing their own. Copilot's coding agent can
  open a PR that a human or another agent reviews — prefer that for anything load-bearing.

Everything else in the base skill — the tracked task entry, token checkpoints, TDD, state.json
at finish — applies unchanged, and the same scripts back it.

## Model selection

Copilot exposes a model picker, and which models are available (and which count as premium
requests) depends on your plan. This file will not repeat a lineup that changes between
releases:

- **Check the picker and your plan's usage page** before assuming a model is free to use in a
  loop. Premium requests are metered.
- **Match the model to the phase, not to the task.** Use the strongest reasoning model available
  for decomposition (Phase 2 PLANNING) and for the correctness/architecture review (Phase 4); use a
  cheaper one for executing an already-decided spec.
- **Say which model you switched to and why** in the task record, the same way the Claude
  extension prefixes dispatches with a lane name. A model switch that nobody recorded is a
  result nobody can reproduce.

> Nothing here is verified against a specific Copilot release. If you find the mapping is wrong
> for your setup, that is a bug in this file — `feed-badger` it back.
