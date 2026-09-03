# task extension: pi

This is a **config-gated extension** of the base `task` skill, not a standalone skill.
It adapts task orchestration patterns for the pi coding agent.

**Activates when:** the project's `.ai-badger/config.json` has `"pi"` in its `agents` array.

## Subagent delegation

pi subagents are **real OS child processes** with their own `cwd`, `--model`, and `--tools`.
Use the built-in subagent extension or spawn via:

```
pi --mode json -p --no-session --model <model> --tools <toolset> "<task>"
```

The reference subagent implementation is at `examples/extensions/subagent/` in the pi repo.

## Session management

- Resume work: `pi -p --session <session_id>` — `--resume, -r` takes no argument (it opens an
  interactive selector); `--session <path|id>` accepts a partial UUID
- pi has no built-in `/branch` or `/fork` — use git worktrees for parallel work
- Context compression: automatic by default

## Token tracking

pi does not expose per-session token usage through an API. The task tracker's pi session
source reads it from the session JSONL instead:
`~/.pi/agent/sessions/--<cwd-with-slashes-as-dashes>--/<timestamp>_<uuid>.jsonl`, one JSON
object per line. A `"type": "message"` line carries the usage, nested under `message.usage`
(not a top-level key), with pi's own field names — `input`/`output`/`cacheRead`/`cacheWrite`,
not Anthropic's `input_tokens`/`output_tokens`. The reader sums `message.usage` across every
Delegation tokens come from the subagent logs the extension tees to
`~/.pi/agent/subagent-logs/<runId>.jsonl` — see the task skill's receipts note
(Phase 3) and `task_tracker.py subagent --delegation <receipt-id>`.
such line in the file matching `PI_SESSION_ID`, and degrades to zero — never raises — on a
missing directory, missing file, or a line that is not valid JSON.

## Hook integration

pi loads extensions from `~/.pi/agent/extensions/` (user scope). The
ai-badger adapter extension translates pi event shapes to Claude-shaped JSON
that the existing Python hooks expect, and maps responses back to pi's format.

| Claude event | pi event | Purpose |
|---|---|---|
| `UserPromptSubmit` | `input` | Transform user prompt |
| `PreToolUse` | `tool_call` | Block/modify tool calls |
| `PostToolUse` | `tool_result` | Observe tool results |
| `SessionStart` | `session_start` | Side effects |
| `SessionEnd` | `session_shutdown` | Cleanup |
| `Stop` | `agent_settled` | Task checkpoint |

## Scope extension

pi's `input` event exposes `streamingBehavior` (`"steer"` | `"followUp"` | `undefined`),
which may fix ai-badger's documented mid-turn marker defect. `undefined` when idle,
`steer` for mid-stream interrupts, `followUp` for messages queued until the agent finishes.