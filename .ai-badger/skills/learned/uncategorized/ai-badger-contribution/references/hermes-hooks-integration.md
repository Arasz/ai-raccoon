# Hermes Agent Hook Systems vs Claude Code Hooks

Research compiled 2026-07-22 from live Hermes Agent docs
(https://hermes-agent.nousresearch.com/docs/user-guide/features/hooks).

## Three hook systems

Hermes has three independent hook systems — more comprehensive than Claude Code's
single hook model:

| System | Registered via | Runs in | Use case |
|--------|---------------|---------|----------|
| Gateway hooks | `HOOK.yaml` + `handler.py` in `~/.hermes/hooks/` | Gateway only | Logging, alerts, webhooks |
| Plugin hooks | `ctx.register_hook()` in a plugin | CLI + Gateway | Tool interception, metrics, guardrails |
| Shell hooks | `hooks:` block in `~/.hermes/config.yaml` | CLI + Gateway | Blocking, auto-formatting, context injection |

All three are non-blocking: errors are caught and logged, never crashing the agent.

## Gateway hooks

Directory structure:
```
~/.hermes/hooks/<name>/
  HOOK.yaml   # declares events
  handler.py  # async def handle(event_type, context)
```

### Available events

| Event | Fires when | Context keys |
|-------|-----------|-------------|
| `gateway:startup` | Gateway process starts | `platforms` |
| `session:start` | New messaging session | `platform, user_id, session_id, session_key` |
| `session:end` | Session ends | `platform, user_id, session_key` |
| `session:reset` | `/new` or `/reset` | `platform, user_id, session_key` |
| `agent:start` | Agent begins processing | `platform, user_id, session_id, message` |
| `agent:step` | Each tool-calling loop iteration | `platform, user_id, session_id, iteration, tool_names` |
| `agent:end` | Agent finishes | `platform, user_id, session_id, message, response` |
| `command:*` | Any slash command (wildcard) | `platform, user_id, command, args` |

### Handler rules
- Must be named `handle`
- Receives `event_type` (str) and `context` (dict)
- Can be `async def` or regular `def`
- Errors caught and logged

### Examples
- **BOOT.md pattern**: `gateway:startup` hook spawns a one-shot agent to run a
  checklist on every gateway boot
- **Long task alert**: `agent:step` hook sends Telegram message at threshold
- **Command logger**: `command:*` hook logs slash command usage to JSONL
- **Session webhook**: `session:start` + `session:reset` POST to external service

## Plugin hooks

Registered programmatically in a plugin's `register()` function:
```python
def register(ctx):
    ctx.register_hook("pre_tool_call", my_tool_observer)
    ctx.register_hook("post_tool_call", my_tool_logger)
    ctx.register_hook("pre_llm_call", my_memory_callback)
    ctx.register_hook("post_llm_call", my_sync_callback)
    ctx.register_hook("on_session_start", my_init_callback)
    ctx.register_hook("on_session_end", my_cleanup_callback)
```

### Available plugin hooks

| Hook | Fires when | Can return |
|------|-----------|-----------|
| `pre_tool_call` | Before any tool executes | `{"action": "block", "message": str}` to veto |
| `post_tool_call` | After any tool returns | ignored (observer) |
| `pre_llm_call` | Once per turn, before tool loop | `{"context": str}` to prepend to user message |
| `post_llm_call` | Once per turn, after tool loop | ignored (observer) |
| `pre_verify` | Before agent verifies code edits | `{"action": "continue", "message": str}` to keep going |
| `on_session_start` | New session (first turn) | ignored (observer) |
| `on_session_end` | Session ends | ignored (observer) |
| `on_session_finalize` | CLI/gateway tears down session | ignored (observer) |
| `on_session_reset` | Gateway swaps session key | ignored (observer) |
| `subagent_start` | delegate_task child constructed | ignored (observer) |
| `subagent_stop` | delegate_task child exited | ignored (observer) |
| `pre_gateway_dispatch` | Before auth + dispatch | `{"action": "skip" \| "rewrite" \| "allow", ...}` |
| `pre_approval_request` | Approval decision requested | ignored (observer) |
| `post_approval_response` | Approval decision made | ignored (observer) |
| `transform_tool_result` | After tool returns, before model sees it | `str` to replace, `None` to leave unchanged |
| `transform_terminal_output` | Inside terminal tool, before truncate/strip | `str` to replace, `None` to leave unchanged |
| `transform_llm_output` | After tool loop, before final delivery | `str` to replace, `None` to leave unchanged |

### Callback rules
- Always accept `**kwargs` for forward compatibility
- Crashes are logged and skipped — never crashes the agent
- Only `pre_tool_call` and `pre_llm_call` can affect behavior
- Observer callbacks receive `telemetry_schema_version`, `turn_id`,
  `api_request_id`, `task_id`, `session_id`, `api_call_count` automatically

## Shell hooks

Minimal drop-in scripts declared in `~/.hermes/config.yaml`:
```yaml
hooks:
  pre_tool_call:
    - ~/scripts/audit-tools.sh
  pre_session_start:
    - ~/scripts/init.sh
```

Shell hooks fire in both CLI and Gateway. They're simpler than plugin hooks
but less powerful — no structured return values, no context injection.

## Claude Code → Hermes hook mapping

For ai-badger's drift-notice hook (currently a Claude `SessionStart` hook via
`hooks/hooks.json` → `drift_notice_hook.py`):

| Claude Code | Hermes equivalent | Notes |
|------------|-------------------|-------|
| `SessionStart` | `on_session_start` (plugin hook) or `session:start` (gateway hook) | Plugin hook works in CLI + Gateway, gateway hook is gateway-only |
| `PostToolUse` | `post_tool_call` (plugin hook) | Observes tool calls and results |
| `PreToolUse` | `pre_tool_call` (plugin hook) | Can block dangerous tool calls |
| `UserPromptSubmit` | `pre_llm_call` (plugin hook) | Can inject context into LLM prompt |
| `Stop` | `on_session_end` (plugin hook) | Fires at end of every turn |

### For ai-badger drift notice on Hermes

The Claude version uses a plugin-provided `SessionStart` hook that compares
`frameworkVersion` in the scaffolded manifest against the installed plugin's
`VERSION`. The Hermes equivalent would be:

1. **Gateway hook** (`gateway:startup`): Fire a one-shot agent to check version
   mismatch and notify. Only works on gateway platforms (Telegram, Discord, etc.).

2. **Plugin hook** (`on_session_start`): Print version mismatch notice at session
   start. Works in both CLI and gateway. Requires a Hermes plugin — equivalent
   complexity to the Claude plugin approach.

3. **Shell hook**: Run a script on session start that reads manifest version and
   plugin version, prints mismatch. Simplest for CLI users.

4. **Cron job**: A scheduled job that checks version mismatch and notifies via
   the configured platform. Works across all surfaces and doesn't need a plugin.

The cron approach is the most portable — it doesn't depend on hook mechanics
and works the same regardless of how Hermes is invoked.

## Recommended integration path (implemented)

ai-badger ships `ai_badger_hooks.py` in the Hermes task extension
(`features/hermes/skills/task-extensions/hermes/ai_badger_hooks.py`). This
module registers three plugin hooks via `register(ctx)`:

1. **`on_session_start`** — drift notice: compares scaffold manifest version
   against framework VERSION, logs warning on mismatch
2. **`pre_llm_call`** — context injection: injects framework version status
   and Hermes usage hints (/usage, session_search) into every turn
3. **`post_tool_call`** — observer: logs tool calls at DEBUG level

Installation: copy `ai_badger_hooks.py` to `~/.hermes/plugins/`.

For users who don't install the plugin, document the alternatives:
- **Cron job**: scheduled check that notifies via configured gateway platform
- **Gateway hook**: `gateway:startup` hook for gateway-only users
- **Shell hook**: `pre_llm_call` shell hook for CLI users
