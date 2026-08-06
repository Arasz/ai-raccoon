# code-review-graph Setup Notes

## macOS Installation

System Python on macOS is 3.9.6 — too old for code-review-graph (requires 3.10+).

Install via Homebrew Python:
```bash
/opt/homebrew/bin/python3.14 -m pip install --break-system-packages code-review-graph
```

The `--break-system-packages` flag is needed because Homebrew Python enforces PEP 668.

## Building the Graph

```bash
cd /path/to/repo
/opt/homebrew/bin/python3.14 -m code_review_graph build
```

The `code-review-graph` CLI command may not be on PATH after `--break-system-packages`
install. Use `python3.14 -m code_review_graph` as the entry point instead.

## Pre-building for Worktree Experiments

When running comparative experiments with worktrees, build the graph in the CRG
agent's worktree BEFORE dispatching the agent:
```bash
cd /path/to/crg-worktree
/opt/homebrew/bin/python3.14 -m code_review_graph build
```

This avoids the agent wasting tokens on setup and ensures the graph is ready
when the agent starts querying it.

## MCP Integration

After installation, configure MCP for your AI tools:
```bash
code-review-graph install
```

If the CLI is not on PATH, the MCP config may need manual adjustment to use
`python3.14 -m code_review_graph` as the command.
