# Hook wiring constraints + memory-first gate architecture (verified 2026-08-06, ai-badger 0.84.0)

Learned while building the memory-first gate (block text-search tools until the session
consulted `memory_search`; shipped as `features/common/skills/ai-raccoon-memory/scripts/
memory_first_gate.py` + `memory_first_gate_hook.py`, PRs Arasz/ai-badger#319 and
Arasz/ai-raccoon#74). These constraints apply to ANY new hook added to the framework.

## Constraint 1 — script filters match only the command TAIL

`hook_wiring.select_hooks()` (Claude scaffold) and the Copilot adjuster both select a
hook's command with `command.rstrip('"').endswith(script)`. A command with a trailing
flag — e.g. `python3 ".../memory_first_gate_hook.py" --record` — NEVER matches the
filter, so it is silently dropped from scaffolded configs. **Fold recorders into an
existing entry that already matches** rather than adding a flagged variant. (The
memory-first gate records its consulted marker inside `memory_grade_hook.py`, which the
existing PostToolUse `memory_search` entry already runs; a separate `--record` entry was
built, tested, then removed for exactly this reason.)

## Constraint 2 — merge_hooks dedupes by script identity per event

`hook_wiring.merge_hooks()` collapses hooks whose `skill_script_id(command)` is equal
into one, and `select_hooks` filters per manifest entry by script name. Consequences:
- The same script cannot be wired under TWO matchers in one event (the second entry's
  hooks are dropped as duplicates). If one script must fire for several tool names, use
  ONE entry with a combined matcher (`"Grep|Glob|Bash"`) and let the hook inspect
  `tool_input` itself.
- Claude's `if:` prefix conditions on Bash hooks do not survive the scaffold wiring
  (per-hook `if` objects share one script id; re-wiring prunes all but the first).

## Constraint 3 — hooks.json entries reach scaffolded projects ONLY via the manifest

Both the Claude scaffold (`hook_wiring.py`) and the Copilot adjuster iterate
`hooks-manifest.json` and pick commands from `features/common/hooks/hooks.json` by the
arm's `script` name. A hooks.json entry with no manifest arm works for the Claude
PLUGIN path (repo-root hooks.json, `${CLAUDE_PLUGIN_ROOT}`) but never lands in
`.claude/settings.json` or `.github/hooks/ai-badger-hooks.json`. Add the manifest arm
first; the coverage test (`test_hooks_manifest_agent_coverage.py`) then forces you to
name all three capable agents or record an honest exemption in
`tooling/validate.py::HOOKS_MANIFEST_AGENT_EXEMPTIONS` (reason ≥ 20 chars, no
placeholders).

## Constraint 4 — Copilot matcher casing + the per-arm matcher override

Copilot matches runtime tool names CASE-SENSITIVELY and lowercases them (`grep`, `rg`,
`bash`) where Claude's are PascalCase (`Grep`, `Glob`, `Bash`). The manifest copilot arm
supports an optional `"matcher"` override that the adjuster prefers over the source
entry's matcher (`copilot_entry.get("matcher") or entry.get("matcher")`). The
hooks-manifest agent-arm schema has `additionalProperties: false`, so adding the field
REQUIRED a `schemas/hooks-manifest.schema.json` change — any new arm field needs the
schema edit in the same commit.

## Constraint 5 — Copilot command hooks are fail-closed: exit 0 on EVERY path

A crash or non-zero exit in a Copilot command preToolUse hook DENIES the tool call it
only meant to gate. The hook must catch everything, print nothing on pass paths (silence
= allow on Claude AND Copilot), print the deny JSON only when denying, and exit 0 even
on malformed stdin. Test this explicitly (malformed payload → exit 0, empty output).

## Memory-first gate architecture (the reference implementation)

- **Deny-and-retry loop**: block text search with a reason naming `memory_search` and
  explicitly permitting re-issue after consultation; the first `memory_search` of the
  session opens the gate. Memory-first, never memory-only.
- **3-strike pass-through**: per-session denial counters (`~/.ai-badger/memory-first/
  <session>.denials`, `MAX_DENIALS = 3`); after 3 denials the gate stops blocking so an
  agent cannot stall on a bank with no hit.
- **Matcher strictness**: `search_files`/`Grep`/`Glob`/`grep`/`rg` are always text
  search; `bash`/`terminal` count ONLY when the command's first token is
  `grep|rg|find|rg.exe` (shlex-split) — piped grep in build steps passes. `read_file`,
  `memory_search` itself, and non-search commands never block.
- **Markers**: `~/.ai-badger/memory-first/<session_id>` (mirrors the memory-grade dir
  convention); sanitize session ids for path safety.
- **Per-host deny shapes**: hermes → `{"action": "block", "message": reason}`; claude →
  `{"hookSpecificOutput": {"hookEventName": "PreToolUse", "permissionDecision": "deny",
  "permissionDecisionReason": reason}}`; copilot → `{"permissionDecision": "deny",
  "permissionDecisionReason": reason}`. Unknown host → `{}` (fail open).
- **Hermes plugin state**: plugin payloads carry NO session_id, so the plugin keys
  in-process state by project cwd (`_memory_consulted` set + `_gate_denials` dict),
  reset at `on_session_start`. The consulted flag is recorded in `post_tool_observer`
  reusing `memory_grade.is_memory_search` as the SINGLE matcher (never write a second
  memory-search matcher). Missing gate module = fail open.
- **project_id**: repo basename of cwd, env override `AI_RACCOON_PROJECT_ID`, fallback
  `unknown`.
- **Known limitations** (recorded in ADR-0017): Hermes gateway multi-session shares the
  consulted flag per project until the next session start; Copilot cloud-agent jobs are
  ephemeral so the per-session marker has no cross-job meaning there.

## Live-verification recipe for a shipped hook (deployed-copy e2e)

Verify the DEPLOYED artifacts, not the framework source: run the scaffolded copies
(`.ai-badger/skills/<skill>/scripts/`) with a scratch `HOME` (markers land in the real
`~/.ai-badger/` otherwise):

```bash
HOME=/tmp/gate-verify python3 .ai-badger/skills/ai-raccoon-memory/scripts/memory_first_gate_hook.py <<< '{"hook_event_name":"PreToolUse","session_id":"t1","cwd":"<repo>","tool_name":"Grep","tool_input":{"pattern":"x"}}'
# expect deny JSON; touch the marker → silence; malformed stdin → exit 0, silence
```

For the Hermes plugin, fresh-load the deployed module
(`importlib.util.spec_from_file_location` on `~/.hermes/plugins/ai-badger/ai_badger_hooks.py`),
call `reset_gate_state()`, then drive `pre_tool_call_memory_gate` /
`post_tool_observer` directly and assert block → consult → allow.

Live agent-CLI probes (claude -p / copilot -p) have environment prerequisites to check
BEFORE burning a run: Claude Code needs the workspace trust dialog accepted
(`projects["<repo>"].hasTrustDialogAccepted` in `~/.claude.json`) and a valid OAuth
session; Copilot CLI needs monthly quota. If either is missing, the script-level e2e
above is the artifact-level evidence — say so in the record rather than claiming the
live probe ran.
