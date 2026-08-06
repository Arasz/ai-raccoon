# Research: Enforcing memory-MCP-first on any agent via hooks

**Date:** 2026-08-06 **Question:** What mechanisms — hooks or otherwise — can enforce that any agent (Hermes, Claude
Code, etc.) queries the AiRaccoon memory MCP before falling back to text-search tools like grep/find?

```chart:matrix
title: blocking levers by agent platform (1 = available, 0 = not/unknown)
capability: Hermes, Claude Code, Codex, Cursor
pre_tool block: 1, 1, 1, 1
deny with redirect message: 1, 1, 0, 1
session-stateful gate: 1, 1, 0, 0
scaffold ships in this repo: 1, 1, 0, 0
```

## Findings

### F1 — Nothing blocks text search today: no shell hooks are configured and the ai-badger plugin registers no pre_tool_call [MEASURED]

`hermes hooks list` reports zero shell hooks or outbound webhooks in `~/.hermes/config.yaml`, and the enabled ai-badger
plugin (0.81.0) declares only `on_session_start`, `pre_llm_call`, and `post_tool_call` — there is no `pre_tool_call`
hook anywhere in the chain, so no tool call can currently be vetoed. Enforcement, if any, is purely instructional.

**Evidence:** `hermes hooks list` on this machine (2026-08-06, macOS 26.5.2) → "No shell hooks or outbound webhooks
configured in ~/.hermes/config.yaml."; `hermes plugins list` → `ai-badger │ enabled │ 0.81.0`;
`~/.hermes/plugins/ai-badger/plugin.yaml` hooks list.

### F2 — Hermes can block any tool call before it runs, two ways: shell hooks or Python plugin hooks [READ]

Both surfaces fire `pre_tool_call` before tool execution and can veto the call. Shell hooks are subprocess scripts
declared in `hooks:` in `~/.hermes/config.yaml`, matching by tool-name regex, receiving a JSON payload on stdin and
returning a decision on stdout: `{"decision": "block", "reason": "..."}` (or Claude-style
`{"action": "block", "message": "..."}`). Python plugin hooks are registered via
`ctx.register_hook("pre_tool_call", fn)` and return a directive `{"action": "block", "message": ...}` to veto, or
`{"action": "approve", ...}` to escalate to the human-approval gate. Both run in CLI and gateway sessions. A broken hook
never crashes the agent — malformed output, non-zero exits, and timeouts log a warning and pass the call through.

**Evidence:** https://hermes-agent.nousresearch.com/docs/user-guide/features/hooks (Shell Hooks section: config schema,
JSON wire protocol, block decision shapes, worked example
`block-rm-rf.sh`); https://hermes-agent.nousresearch.com/docs/developer-guide/plugins (Hook reference table:
`pre_tool_call` fires before any tool executes, callback `(tool_name, args, task_id)`, returns block/approve directive).

### F3 — The shell-hook payload carries session_id and cwd — enough for a session-stateful "memory consulted yet?" gate [READ]

The stdin payload for `pre_tool_call` is `{"hook_event_name", "tool_name", "tool_input", "session_id", "cwd", "extra"}`.
The matcher is a regex over the tool name, so `search_files` (Hermes's grep/find surface) can be matched directly;
`terminal` can be matched and the command string inspected from `tool_input.command` (the documented `block-rm-rf.sh`
example does exactly this against `rm -rf /`). This is the missing ingredient for real enforcement: a hook that blocks
text search unless a `memory_search` call is on record for this session, keyed by `session_id`.

**Evidence:** https://hermes-agent.nousresearch.com/docs/user-guide/features/hooks (JSON wire protocol example; worked
example 2 "Block destructive terminal commands").

### F4 — Claude Code can deny a tool call and redirect it, with matchers covering grep/find surfaces [READ]

Claude Code's `PreToolUse` hook can return `hookSpecificOutput` with `permissionDecision: "deny"`,
`permissionDecisionReason`, and `additionalContext` — the deny stops the call and the context tells the model what to do
instead. Matchers include `Bash|Glob|Grep` (and `PowerShell`), with conditional `if:` expressions like `Bash(rm *)` so a
Bash hook can inspect the command. A deny-and-redirect loop is the documented enforcement pattern: the model sees the
denial reason, calls the sanctioned tool, and retries.

**Evidence:** https://code.claude.com/docs/en/hooks (worked block-rm.sh example with matcher `Bash` and
`permissionDecision: "deny"`; matcher coverage `Bash|PowerShell`; `additionalContext` field semantics).

### F5 — The repo already ships the Claude scaffold (hooks.json) but it is advisory-only on the memory side [READ]

`.ai-badger/hooks/hooks.json` wires Claude Code hooks: SessionStart/Stop/SessionEnd, UserPromptSubmit (context
enrichment, prompt markers), PreToolUse matcher `Agent` (dispatch gate), and PostToolUse — commit reminder on edits, and
matcher `memory_search` → `memory_grade_hook.py`. That memory hook logs each search and returns a grade ask as
`additionalContext`; its docstring states the discipline explicitly: "Advisory only, never blocking". Nothing in the
scaffold intercepts `Glob|Grep|Bash` to push toward memory first.

**Evidence:** `.ai-badger/hooks/hooks.json:52-82` (PreToolUse/PostToolUse sections);
`.ai-badger/hooks/../skills/ai-raccoon-memory/scripts/memory_grade_hook.py:1-7`.

### F6 — The soft layer (instructions + context injection) is already in place [READ]

`.hermes.md` / `CLAUDE.md` carry a "Search memory FIRST" block instructing memory_search before web search, code search,
or asking the user — guidance, not enforcement. The ai-badger Hermes plugin additionally injects MCP tool-index
recommendations into every turn via `pre_llm_call` (BM25-ranked against `.ai-badger/mcp-tools.json`), so the model is
reminded of the memory tools per turn. A model can still skip both.

**Evidence:** `.hermes.md` "MCP Tools: ai-raccoon" section; `.ai-badger/hooks/ai_badger_hooks.py:456-500`
(`pre_llm_inject_context`, MCP tool index recommendations).

### F7 — Removing the search tool outright is a blunt, lossy lever [READ]

`hermes tools` enables/disables whole toolsets, not individual tools. The `file` toolset bundles
"read/write/search/patch", so disabling it to kill `search_files` also removes `read_file`, `write_file`, and `patch`.
Grep via `terminal` would survive regardless. There is no per-tool removal path, so tool removal cannot express "memory
first, text search as fallback" — it only expresses "no text search at all" at an unacceptable cost.

**Evidence:** hermes-agent skill `references/configuration.md` (Toolsets table: `file` → "File read/write/search/patch";
"Enable/disable via `hermes tools` (interactive) or `hermes tools enable/disable NAME`").

### F8 — Codex and Cursor both have hook surfaces, with narrower reach [READ]

Codex CLI hooks are opt-in (`[features].codex_hooks = true`, "hooks are off by default"), configured in `config.toml`,
and PreToolUse intercepts the shell (Bash) tool only — by design — so a grep/find gate would have to inspect command
text inside Bash, and would not intercept a hypothetical dedicated search tool. Cursor exposes `preToolUse` hooks with
deny/`updated_input` semantics (documented; forum reports confirm `updated_input` is silently ignored for the Task
tool — a caveat for that platform). Neither has a scaffold in this repo (only `.claude` exists).

**Evidence:** https://developers.openai.com/codex/hooks and https://developers.openai.com/codex/config-reference (
"Lifecycle hooks configured inline in config.toml"); https://cursor.com/docs/hooks
and https://forum.cursor.com/t/pretooluse-hook-updated-input-is-silently-ignored-for-the-task-tool/151985; repo root
listing shows `.claude` but no `.codex`/`.cursor`.

### F9 — The practical shape: a stateful pre_tool_call gate on Hermes, a deny-and-redirect PreToolUse on Claude Code, shipped through the existing scaffolds [INFERRED]

Reasoning from F2-F5: Hermes gets a `pre_tool_call` shell hook (config.yaml `hooks:` block) with matcher `search_files`
plus a `terminal` matcher that inspects `tool_input.command` for `grep|find|rg`; a small state file keyed by
`session_id` (from the F3 payload) records the first `memory_search` of the session; until it exists, the hook returns
`{"decision": "block", "reason": "run memory_search (project_id, query) first — re-issue this call if nothing relevant"}`.
Claude Code gets a new hooks.json entry: `PreToolUse` matcher `Glob|Grep` plus `Bash` with an `if:` condition on
grep/find command text, returning `permissionDecision: "deny"` + `additionalContext` naming the memory tool. Both are
the same deny-and-retry loop: the model is forced to consult memory, then may retry the text search — which is exactly
"memory first", not "memory only". One constraint this reasons from but could not verify in docs: MCP servers cannot
force clients to call their tools — enforcement must live client-side, which is why hooks are the only hard lever and
the instruction layer (F6) is what every platform shares. The `hermes hooks` CLI (test/doctor/list/revoke) plus the
first-use consent prompt make the Hermes side deployable without hand-editing config; the plugin path (F2) is the
stateful alternative if a Python plugin is preferred, at the cost of losing `session_id` in the callback signature
(plugin hooks receive `tool_name, args, task_id` only).

### F10 — Whether blocking actually changes agent behavior for the better is untested [UNVERIFIED]

No run has measured what a real agent does after a block: whether it reliably retries with `memory_search` or stalls,
and whether the extra round-trip outweighs the value of the memory hit. The repo's own retrieval findings (2026-08-04
dual-vector-vs-plan) show memory search wins on recall, but that is retrieval quality — not the loop dynamics of an
enforced gate. I did not instrument a session to check, because that would require installing the gate first.

## Still open

- Does a blocked agent retry with memory_search, or burn turns re-issuing the same grep? A pilot hook on one repo with
  `hermes hooks test` payloads + one real session would settle it.
- Should the gate be per-session (block until first memory_search) or per-query (block when the query looks like a
  memory-bank question)? The state file keyed by session_id supports either; which matches user intent is a product
  decision.
- Codex's Bash-only PreToolUse and Cursor's Task-tool `updated_input` bug mean those platforms need command-text
  inspection rather than tool matchers — unverified whether their deny surfaces return usable redirect context.
- Is the reason string enough, or should the block also offer the exact memory_search arguments (project_id)?
  ai-raccoon's project_id is repo-known, so the hook could inject it.
