## Comparative Agent Experiments

When you need to compare two approaches (e.g., "with tool X vs without"), use
worktrees to run identical tasks in parallel with different tool configurations.

### Setup
1. Create two worktrees from the same base branch.
2. Pre-build any tool-specific state (e.g., a graph index build) in the
   worktree that needs it.
3. Create a shared `experiments/` directory in the main checkout with per-agent
   subdirectories for logs.

### Tool-Usage Logging
Each agent appends tool names to its own log file:
```bash
echo 'TOOL_NAME' >> /path/to/experiments/agent-name/tool-usage.log
```
Instruct agents to log at START (`AGENT_START`), after each tool call, and at
FINISH (`AGENT_FINISH`). This produces a comparable timeline of tool usage.

### Dispatching
Pass identical goals to both agents with one key difference in context:
- Agent A: "You MUST use [tool] MCP tools BEFORE reading files"
- Agent B: "Do NOT use [tool]. Use terminal, read_file, write_file only."

Both agents must log tool usage and record start/finish markers.

### Comparing Results
After both complete:
1. `wc -l` on tool-usage logs — total tool calls per agent.
2. `grep -c` for specific tool categories (graph tools vs file reads).
3. `git diff --stat` in each worktree — scope of changes.
4. Run test suites in both worktrees — quality gate.
5. Compare the agent transcript summaries — reasoning quality.

### Gotchas (experiments)
- **Pre-build tool state before dispatching** — if a tool needs setup (like
  a graph index build), do it in the worktree before the agent starts,
  otherwise the agent wastes tokens on setup or fails.
- **Log file timestamps differ from agent timestamps** — the log records wall
  clock time, but agent transcript timestamps are more reliable for comparing
  pacing. Use the log for tool-count comparison, transcripts for timing.
- **Rider MCP tools don't work in worktrees** — the Rider plugin binds to the
  main checkout. Instruct worktree agents to use terminal/file tools only.
