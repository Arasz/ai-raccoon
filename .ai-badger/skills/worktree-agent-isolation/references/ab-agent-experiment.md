# A/B Agent Experiment Pattern

Controlled comparison of two agents with different tool configurations,
executed in parallel worktrees. Used to measure whether a tool (MCP server,
graph, linter, etc.) actually improves agent output quality.

## When to use

- User asks "is tool X worth it?" or "does the graph actually help?"
- You have a tool you want to benchmark against a baseline
- The task is bounded enough that both agents can finish in a reasonable time

## Setup

1. **Create two worktrees** from the same base commit:
   ```bash
   git worktree add ../project-agent-a -b experiment/agent-a origin/main
   git worktree add ../project-agent-b -b experiment/agent-b origin/main
   ```

2. **Create tool-usage logging** — one log file per agent:
   ```bash
   mkdir -p experiments/agent-a experiments/agent-b
   echo "AGENT_START $(date -u +%Y-%m-%dT%H:%M:%SZ)" > experiments/agent-a/tool-usage.log
   echo "AGENT_START $(date -u +%Y-%m-%dT%H:%M:%SZ)" > experiments/agent-b/tool-usage.log
   ```

3. **Build the tool's index** in the worktree that needs it (e.g., code-review-graph):
   ```bash
   cd ../project-agent-a && code-review-graph build
   ```

4. **Dispatch both agents** with `delegate_task(tasks=[...])`:
   - Agent A: has access to the tool, instructed to use it BEFORE reading files
   - Agent B: no access to the tool, explores by reading files directly
   - Both must log tool usage: `echo 'TOOL_NAME' >> experiments/<agent>/tool-usage.log`
   - Both must log start/finish: `AGENT_START` and `AGENT_FINISH`
   - Both get identical task descriptions and acceptance criteria

## What to measure

| Metric | How to get it |
|--------|---------------|
| API calls | From delegation result (`api_calls` field) |
| Duration | From delegation result (`duration` field) |
| Files changed | `git diff --stat HEAD` in each worktree |
| Lines added/removed | Same diff |
| New tests added | Count new `.spec.ts` files or test cases |
| Components/modules touched | Compare diff scope against the target area |
| Tool calls logged | `cat experiments/<agent>/tool-usage.log` |

## Analysis template

```markdown
| Metric | Agent A (with tool) | Agent B (without tool) |
|--------|---------------------|------------------------|
| API calls | N | N |
| Duration | Ns | Ns |
| Files changed | N | N |
| Lines added | N | N |
| New tests | N | N |
| Target coverage | N/M | N/M |
```

Key question: did the tool improve **completeness** (more files, more coverage)
or **efficiency** (fewer API calls, less time) — or both?

## Merge strategy

1. Pick the better refactor (usually Agent A if the tool helped)
2. Merge into the main branch: `git merge experiment/agent-a --no-edit`
3. Verify: build + test + lint
4. Clean up: `git worktree remove` both, delete experiment branches

## Pitfalls

- **Both agents must start from the same commit** — create worktrees from
  `origin/main` (or equivalent), not from local main which may lag.
- **Tool-logging is honor-system** — agents may forget to log some calls.
  The delegation result's `api_calls` field is the reliable count; tool logs
  show which tools were used, not how many total calls.
- **Duration includes thinking time** — a slower agent isn't necessarily worse.
  Focus on completeness and quality metrics.
- **Rider MCP tools don't work in worktrees** — if using JetBrains, instruct
  agents to use terminal/read_file/write_file only.
- **The tool's index must be built per-worktree** — code-review-graph stores
  its SQLite DB in `.code-review-graph/` which is worktree-local.
- **Clean up experiment artifacts** — remove `experiments/` directory,
  worktrees, and branches after analysis. Don't commit experiment scaffolding.
