# Memory-First Gate: Enforce memory MCP before text search (Hermes, Claude, Copilot) Implementation Plan

> **For Hermes:** Use subagent-driven-development skill to implement this plan task-by-task.

**Goal:** Enforce "memory MCP first" on every agent — Hermes, Claude Code, and GitHub Copilot — by blocking text-search
tools (grep/find/search_files) until the session has consulted AiRaccoon memory, shipped through the ai-badger framework
so every scaffolded repo inherits it.

**Architecture:** One shared pure-logic module (`memory_first_gate.py`) — tool matchers, per-session state markers,
per-host decision builders — wired into each agent's existing hook surface: the Hermes plugin (`pre_tool_call`), the
Claude `hooks.json` (`PreToolUse`), and the Copilot `.github/hooks/ai-badger-hooks.json` (`preToolUse`). All three
implement the same deny-and-retry loop: block text search with a reason naming `memory_search`; once the session records
a `memory_search` call, text search passes. Memory-first, never memory-only.

**Tech Stack:** Python 3 (framework hook modules, pytest), JSON hook manifests per host, git.

**Sources verified 2026-08-06:**

- Hermes shell/plugin hooks: hermes-agent docs (user-guide/features/hooks, developer-guide/plugins) — `pre_tool_call`
  blocks via `{"action": "block", "message"}`; plugin payload lacks session_id (has tool_name/args/task_id).
- Claude: code.claude.com/docs/en/hooks — `PreToolUse` deny via
  `hookSpecificOutput.permissionDecision + additionalContext`; matchers `Bash|Glob|Grep`.
- Copilot: docs.github.com/en/copilot/reference/hooks-reference — `preToolUse` output
  `{"permissionDecision": "deny", "permissionDecisionReason": "..."}`; runtime tool names include `grep`, `rg`, `Glob`,
  `bash`; command preToolUse hooks are **fail-closed** (crash/exit!=0 denies the call); timeouts fail-open. Hooks loaded
  from `.github/hooks/*.json` (repo), `~/.copilot/hooks/*.json` (user), policy dir.

---

## Phase 0 — Remove Junie support from ai-badger (ONE commit, per user instruction)

**User spec (2026-08-06):** "remove June [Junie] support from ai-badger, in one commit, remove every file and
integration + ADR — I don't use junie and we don't have manpower to support more than 3 agents now." Supported agents
after this commit: `claude`, `copilot`, `hermes` only. The user explicitly overrides the small-commits invariant for
this unit: everything lands in ONE commit (and one PR — the one-PR-per-task invariant still holds).

### Task 0.1: Delete the Junie feature and its test

**Files (tracked — verified with `git ls-files | grep -i junie`):**

- Delete: `features/junie/` — `adjustments/adjust_skills.py`, `adjustments/adjustment.json`,
  `plugins-instructions.json`, `scaffolding.json`, `templates/AGENTS.md.tmpl` (symlink →
  `../../common/templates/CLAUDE.md.tmpl`, delete the link, NOT the target)
- Delete: `tests/test_adjust_skills_junie.py`

### Task 0.2: Remove junie from the engine catalog and schemas

**Files (occurrence counts verified):**

- `engine/badger_lib.py:158` — `AGENT_NAMES = ["claude", "copilot", "hermes", "junie"]` → drop `"junie"` (this is the
  canonical list; everything else derives from it)
- `index.json` (4 junie refs, incl. the catalog entry at 615-629) — remove the `junie` entry and any other junie tokens
- `features/common/support.json` (entry at 222-256: `.junie/AGENTS.md`, `.junie/guidelines.md`, `.junie/skills/`
  mechanism rows) — remove the junie agent row and any junie tokens in other rows
- `schemas/agents.schema.json` (2), `schemas/config.schema.json` (1), `schemas/manifest.schema.json` (1),
  `schemas/skills-source.schema.json` (1), `schemas/stack-mcp.schema.json` (1), `schemas/support.schema.json` (1) —
  remove `"junie"` from every agent enum (schema validation then rejects junie configs — that IS the enforcement)

### Task 0.3: Remove junie from the scaffold engine

**Files:**

- `features/common/skills/welcome-ai-badger/scripts/agent_files.py` (1), `detect.py` (3 — junie traces: `.junie/` dir
  detection in repo AND user scope), `scaffold.py` (3 — `.junie/AGENTS.md` copy + `.junie/skills/` symlinks) — remove
  junie branches; the detect/scaffold chain must no longer produce `.junie/` files or count junie as a present agent
- `features/common/skills/welcome-ai-badger/SKILL.md` (3), `features/common/skills/prompt-markers/SKILL.md` (1),
  `features/common/skills/maintain-agent-instructions/SKILL.md` (1),
  `features/common/skills/maintain-agent-instructions/references/copilot-compatibility.md` (1) — drop junie from the
  supported-agent lists and compatibility table

### Task 0.4: Update tests to the three-agent reality (and add a regression test)

**Files (modify — TDD: adjust first, verify failure is junie-shaped, then fix):**

- `tests/test_detect.py` (5), `tests/test_hooks_manifest_agent_coverage.py` (3), `tests/test_mcp_feature_type.py` (1),
  `tests/test_release_convention_invariant.py` (2), `tests/test_scaffold_agent_files.py` (1),
  `tests/test_stack_skill_sources.py` (1)
- Add to `tests/test_detect.py` (or `test_scaffold_agent_files.py`): a regression test asserting
  `AGENT_NAMES == ["claude", "copilot", "hermes"]` and that a scaffold with agents `claude,copilot,hermes` writes no
  `.junie/` file and no junie token appears in generated files. Write it first (RED — it fails on the current 4-agent
  list), then apply 0.1-0.3 (GREEN).

Run: `.venv/bin/python3 -m pytest tests/ -q` → all PASS, no skips (main checkout per repo convention).

### Task 0.5: Update docs + ADR + changelog + version (release-shaped)

**Files:**

- `README.md` (5 refs — supported-agents table row 91, agent list 97/124/131, layout 265/268), `SECURITY.md` (1 —
  scaffolding write table row 47), `docs/getting-started.md` (6), `docs/framework-architecture.md` (9), `docs/skills.md`
  (1), `docs/dictionary.md` (3), `docs/authoring-a-feature.md` (1) — remove junie from all live docs; update the
  diagrams' agent lists
- Create `docs/adr/0016-junie-support-removed.md` — the "+ ADR" from the user spec: decision = support exactly three
  agents (claude, copilot, hermes); rationale = unused by owner, no manpower to maintain a fourth agent surface;
  effect = configs naming junie fail schema validation; supersedes index.json/support.json junie entries. (No existing
  ADR mentions junie — verified `grep junie docs/adr/` is empty — so this is a new decision record, not an edit.)
- Create `docs/changelog/0.82.0-junie-support-removed.md`
- `VERSION` 0.81.0 → 0.82.0; run `version_sync.py`; `release_guard` per project convention
- KEEP `docs/changelog/0.47.0-junie-can-see-skills.md` — historical release record; the releases-are-traceable invariant
  keeps it, and the 0.82.0 entry documents the removal. (Flagged for user override.)

### Task 0.6: ONE commit + PR + verification

```bash
git add -A && git commit -m "feat(agents): remove junie support — three-agent scope (claude, copilot, hermes)"
```

- Pre-push gate: `.lefthook verify.sh` must pass.
- Verification (done means proven):
    -
    `grep -ri junie . --exclude-dir=.git --exclude-dir=__pycache__ --exclude-dir=.ai-badger --exclude-dir=.ai-badger.bckp` →
    remaining hits ONLY: `docs/changelog/0.47.0-junie-can-see-skills.md`,
    `docs/changelog/0.82.0-junie-support-removed.md`, `docs/adr/0016-*`, and untracked `.remember/` history
    - Full pytest suite green; scaffold smoke test: temp project config with `"agents": ["junie"]` → schema validation
      error; with `["claude","copilot","hermes"]` → clean scaffold, no `.junie/` output
    - PR in Arasz/ai-badger (no direct push to main)
- Note: this does NOT delete existing `.junie/` dirs in already-scaffolded projects (user files are not the framework's
  to delete); it stops generation and validation. ai-raccoon needs no regeneration (its manifest already lists only
  claude/copilot/hermes — verified).

---

## Phase 1 — Shared gate module (ai-badger repo)

### Task 1.1: Write failing matcher tests

**Files:** Create `tests/test_memory_first_gate.py` (new), under `/Users/arasz/RiderProjects/ai-badger/`.

Test `is_text_search(tool_name, tool_input)` for:

- Hermes built-ins: `search_files` → True; `read_file`, `write_file`, `terminal` (non-search command) → False.
- Hermes `terminal` with `tool_input.command` = `grep -r foo src/`, `rg foo`, `find . -name x` → True; `dotnet build`,
  `git status | grep x` (grep not first token) → False.
- Claude names: `Grep`, `Glob` → True; `Bash` with command first-token grep → True; `Read`, `Write` → False.
- Copilot names: `grep`, `rg`, `Glob` → True; `bash` with command first-token grep → True; `view`, `read` → False.

Run: `cd /Users/arasz/RiderProjects/ai-badger && .venv/bin/python3 -m pytest tests/test_memory_first_gate.py -v`
Expected: FAIL (module missing).

### Task 1.2: Implement `memory_first_gate.py`

**Files:** Create `features/common/skills/ai-raccoon-memory/scripts/memory_first_gate.py`.

Pure functions only (framework invariant: static modules = pure functions):

- `is_text_search(tool_name, tool_input)` — the matcher table above. Bash/terminal inspection: only when the command's
  first token is `grep|rg|find|rg.exe` (allows piped grep in build steps).
- `marker_path(session_id)` → `~/.ai-badger/memory-first/<session_id>` (mirrors the existing
  `~/.ai-badger/memory-grade/` convention).
- `record_search(session_id)` — touch marker (mkdir -p, atomic).
- `search_consulted(session_id)` — marker exists.
- `project_id(cwd)` — repo dir basename (matches bank convention: ai-raccoon, ai-badger, job-search-ai-assistant);
  fallback `unknown`.
- `build_decision(host, tool_name, tool_input, session_id)` → per-host dict: `hermes` →
  `{"action": "block", "message": ...}`; `claude` → `{"hookSpecificOutput": {...deny...}}`; `copilot` →
  `{"permissionDecision": "deny", ...}`. Reason:
  `Memory-first gate: run memory_search (project_id=<id>) before repo text search; re-issue this call if the bank has no relevant hit.`
- Denial-loop guard: `deny_count(session_id)` / `increment_denials(session_id)` — after 3 denials for the same session,
  pass through (prevents agent stalls; configurable constant).

Run tests → PASS. Commit: `feat(memory-first): add shared gate module` (ai-badger repo).

### Task 1.3: Write failing hook-payload tests

**Files:** Create `tests/test_memory_first_gate_hook.py`.

Feed synthetic stdin payloads (Claude PascalCase and Copilot camelCase shapes) to the hook entry point:

- `tool_name=Grep`/`grep`, no marker → deny JSON with reason, exit 0.
- marker present → `{}` (pass), exit 0.
- `tool_name=Read`/`view` → `{}`.
- Malformed JSON → `{}`, exit 0 (never crash; Copilot preToolUse is fail-closed, so exit 0 on every path is a hard
  requirement). Expected: FAIL.

### Task 1.4: Implement `memory_first_gate_hook.py`

**Files:** Create `features/common/skills/ai-raccoon-memory/scripts/memory_first_gate_hook.py`.

Reads JSON stdin (accept camelCase `toolName`/`toolArgs` and PascalCase `tool_name`/`tool_input`; `sessionId`/
`session_id`; `cwd`), wraps `memory_first_gate` (sys.path sibling-import like `memory_grade_hook.py`), prints decision
JSON, always exit 0. Recorder mode: when invoked with `--record`, calls `record_search(session_id)` instead (used by the
postToolUse entries).

Run tests → PASS. Commit.

---

## Phase 2 — Hermes plugin gate (ai-badger repo)

### Task 2.1: Write failing plugin hook test

**Files:** Modify `tests/test_hermes_plugin_payloads.py` (existing payload-shape tests) — or new
`tests/test_memory_first_gate_hermes.py`.

Assert: registering the plugin yields a `pre_tool_call` callback; calling it with `tool_name="search_files"`, no prior
`memory_search` in session state → returns `{"action": "block", "message": ...}`; after a `post_tool_call` with
`tool_name="mcp__ai_raccoon__memory_search"` (use `memory_grade.is_memory_search` semantics:
`mcp__ai_raccoon__memory_search` and `ai-raccoon:memory_search` spellings) → same `pre_tool_call` returns `None`
(allow).

Run: `.venv/bin/python3 -m pytest tests/test_memory_first_gate_hermes.py -v` → FAIL.

### Task 2.2: Implement the plugin gate

**Files:** Modify `features/common/hooks/ai_badger_hooks.py` (the framework source the Hermes plugin copies from).

- Add module-level session state: `_memory_consulted: set` (of project cwd) + `_denials: dict`; reset in
  `on_session_start_drift_notice` (already the reset point — reuse `reset_session_hints` pattern).
- New callback `pre_tool_call_memory_gate(tool_name, args, task_id=None, **_kwargs)` →
  `{"action": "block", "message": ...}` when `memory_first_gate.is_text_search(...)` and project not in
  `_memory_consulted` and denials < 3; else `None`.
- In the existing post_tool_call observer (where `memory_grade.log_search` is invoked for memory_search): call
  `memory_first_gate.record_search` and add project to `_memory_consulted`. Reuse `memory_grade.is_memory_search` for
  the tool-name check (single matcher, verified `features/common/.../memory_grade.py:31-40`).
- `register(ctx)` adds `ctx.register_hook("pre_tool_call", pre_tool_call_memory_gate)` (VALID_HOOKS includes
  `pre_tool_call` — verified in the diagnosis doc).
- Known limitation to document in code (1-2 lines): plugin callbacks carry no session_id, so state is per-process keyed
  by project cwd; CLI sessions are per-process, gateway multi-session shares the flag until the next on_session_start
  reset.

Run tests → PASS. Commit.

### Task 2.3: Update the plugin manifest wiring

**Files:** Modify `features/hermes/adjustments/adjust_hooks.py`:

- `plugin.yaml` template (`PLUGIN_YAML` in adjust_hooks.py, currently
  `hooks: [on_session_start, pre_llm_call, post_tool_call]`) → add `pre_tool_call`.
- Copy lists: add `memory_first_gate.py` beside `ai_badger_hooks.py` (SHARED_SKILL_MODULES or RETRIEVAL_MODULES-style
  list — it must land inside the plugin dir AND in the project's `.ai-badger/hooks/`), and add the two skill scripts to
  the `ai-raccoon-memory` skill copy list (`features/common/skills/ai-raccoon-memory/scripts/` destination).

Run: existing `tests/test_hermes_plugin_install.py` and `tests/test_scaffold_hook_wiring.py` → PASS (they assert the
plugin-dir shape and copy lists; update them if the new module demands it).

### Task 2.4: hooks-manifest entry for Hermes

**Files:** Modify `features/common/hooks/hooks-manifest.json`.

Add hook entry `memory-first-gate`:

- `hermes`: `{ "type": "plugin", "entry": "ai_badger_hooks.py", "method": "pre_tool_call_memory_gate" }`
- (claude/copilot entries added in Phases 3-4 — `tests/test_hooks_manifest_agent_coverage.py` enforces all declared
  agents, which is the gate that keeps this honest.)

Run manifest test → PASS. Commit.

---

## Phase 3 — Claude Code gate (ai-badger repo)

### Task 3.1: Write failing Claude wiring test

**Files:** Modify `tests/test_hook_wiring_claude.py`.

Assert the generated `hooks.json` (from `features/common/hooks/hooks.json`) contains a `PreToolUse` entry matching
`Grep|Glob` (and `Bash` with an `if:` condition on grep-prefixed commands if Claude's condition syntax supports
alternation — verify against code.claude.com/docs/en/hooks at implementation time; fallback: `Grep|Glob` only) pointing
at `memory_first_gate_hook.py`, plus a `PostToolUse` matcher `memory_search` pointing at the recorder invocation.

Run → FAIL. (Do not hand-edit the test to pass; the template change comes next.)

### Task 3.2: Update the Claude hooks.json template

**Files:** Modify `features/common/hooks/hooks.json` (the template copied to `.ai-badger/hooks/hooks.json`).

Add:

```json
"PreToolUse": [
  { "matcher": "Grep|Glob", "hooks": [{ "type": "command",
    "command": "python3 .ai-badger/skills/ai-raccoon-memory/scripts/memory_first_gate_hook.py",
    "timeout": 10 }] }
],
"PostToolUse": [
  { "matcher": "memory_search", "hooks": [{ "type": "command",
    "command": "python3 .ai-badger/skills/ai-raccoon-memory/scripts/memory_first_gate_hook.py --record",
    "timeout": 10 }] }
]
```

(The existing PostToolUse memory_search entry already runs `memory_grade_hook.py`; keep both, or fold `--record` into
memory_grade_hook.py — prefer folding: one process per event, and the recorder is then automatically present on every
host that already ships memory_grade_hook.py. Decide at implementation; the tests must assert the chosen shape.)

### Task 3.3: hooks-manifest entry for Claude

Add `claude` arm of `memory-first-gate` in `hooks-manifest.json`:
`{ "type": "hooks-json", "entry": "hooks.json", "event": "PreToolUse", "script": "memory_first_gate_hook.py" }` (+ the
recorder under PostToolUse if the manifest tracks it).

Run `test_hooks_manifest_agent_coverage.py` + `test_hook_wiring_claude.py` → PASS. Commit.

### Task 3.4: Verify the Claude payload end-to-end (script level)

Run:

```bash
echo '{"hook_event_name":"PreToolUse","session_id":"t1","cwd":"/Users/arasz/RiderProjects/ai-raccoon","tool_name":"Grep","tool_input":{"pattern":"MemorySearch"}}' | python3 features/common/skills/ai-raccoon-memory/scripts/memory_first_gate_hook.py
```

Expected: JSON with `permissionDecision: "deny"` + reason naming `memory_search`. Repeat with a touched marker
(`~/.ai-badger/memory-first/t1`) → `{}`.

---

## Phase 4 — Copilot gate (ai-badger repo)

### Task 4.1: Write failing Copilot wiring test

**Files:** Modify `tests/test_adjust_hooks_copilot.py`.

Assert the generated `.github/hooks/ai-badger-hooks.json` (from `features/copilot/adjustments/adjust_hooks.py`) contains
a `preToolUse` entry with matcher `grep|rg|Glob` (+ `bash` with command inspection) pointing at the gate script, and a
`postToolUse` matcher `memory_search` recorder entry.

Run → FAIL.

### Task 4.2: Update the Copilot hooks adjustment

**Files:** Modify `features/copilot/adjustments/adjust_hooks.py` (the generator — verified it writes
`.github/hooks/ai-badger-hooks.json` at lines ~131-142).

Add `preToolUse` + `postToolUse --record` entries to the generated dict. Matcher semantics per
docs.github.com/en/copilot/reference/hooks-reference: camelCase event names use the runtime tool names (`grep`, `rg`,
`Glob`, `bash`); the `bash` entry inspects `toolArgs.command` first token via the shared module (the generator emits
`python3 .ai-badger/skills/ai-raccoon-memory/scripts/memory_first_gate_hook.py`).

Run `test_adjust_hooks_copilot.py` → PASS. Commit.

### Task 4.3: hooks-manifest entry for Copilot

Add `copilot` arm:
`{ "type": "hooks-json", "entry": "hooks.json", "event": "preToolUse", "script": "memory_first_gate_hook.py" }`. Run
manifest coverage test → PASS.

### Task 4.4: Verify Copilot payload end-to-end (script level)

```bash
echo '{"sessionId":"c1","cwd":"/Users/arasz/RiderProjects/ai-raccoon","toolName":"grep","toolArgs":{"pattern":"x"}}' | python3 features/common/skills/ai-raccoon-memory/scripts/memory_first_gate_hook.py
```

Expected: `{"permissionDecision": "deny", "permissionDecisionReason": ...}`; with marker → `{}`. Also verify
crash-safety: `echo 'not json' | ...` → exit 0 and `{}` (Copilot command preToolUse is fail-closed — this exit-0
discipline is mandatory).

---

## Phase 5 — Framework release + deploy to ai-raccoon + verify (both repos)

### Task 5.1: Framework docs + changelog + version (release-shaped PR per project convention)

**Files (ai-badger repo):** `VERSION` 0.82.0 → 0.83.0 (0.82.0 is the Junie removal from Phase 0);
`docs/changelog/0.83.0-memory-first-gate.md`; run `version_sync.py`; update `references/hooks-subsystem.md` (already
flagged in `docs/work/2026-08-06-hermes-integration-diagnosis.md` §5.4 — fold in the pre_tool_call surface); ADR note in
`docs/adr/` for the memory-first gate decision (deny-and-retry, matcher strictness, loop guard). Run the full suite:
`.venv/bin/python3 -m pytest tests/ -q` → all PASS, no skips. Pre-push gate: `.lefthook verify.sh`. **PR:** one PR in
Arasz/ai-badger (never push to main directly).

### Task 5.2: Deploy to ai-raccoon via den-refresh

**Files (ai-raccoon repo):** run `den-refresh` (or `welcome-ai-badger` re-scaffold) → regenerates
`.ai-badger/hooks/hooks.json`, `.github/hooks/ai-badger-hooks.json`, `.ai-badger/skills/ai-raccoon-memory/scripts/*`,
and the user-scope plugin `~/.hermes/plugins/ai-badger/*`. Verify: `git diff` shows exactly the expected regenerated
files; `hermes plugins list` still shows ai-badger enabled; `~/.hermes/plugins/ai-badger/plugin.yaml` lists
`pre_tool_call`.

### Task 5.3: Live verification per host (done means proven)

- **Hermes:** fresh CLI session in ai-raccoon → ask the agent to "find where memory_search is implemented" (tempts
  search_files) → expect the gate's block message naming memory_search; then run memory_search once → re-issue the same
  question → search proceeds. Evidence: session transcript lines.
- **Claude Code:** in ai-raccoon, `claude -p "grep for MemorySearch usage"` with a clean marker → expect denial +
  memory-first reason in the trace; with marker → passes.
- **Copilot CLI:** `copilot` in ai-raccoon, same probe (CLI hooks run locally; cloud agent fires only a subset —
  document that the repo-level file is what cloud agent loads, but cloud-agent jobs are ephemeral and non-interactive,
  so the gate's per-session marker has no cross-job meaning there; note as known limitation).
- **Docs/work record:** `docs/work/2026-08-06-enforce-memory-mcp-first-hooks.md` — append the implementation outcome (or
  link the changelog).

### Task 5.4: ai-raccoon PR

**PR:** one PR in Arasz/ai-raccoon with the regenerated scaffold files + any doc updates (one task = one PR invariant).

---

## Files likely to change (summary)

**ai-badger repo:**

- Create: `features/common/skills/ai-raccoon-memory/scripts/memory_first_gate.py`, `.../memory_first_gate_hook.py`,
  `tests/test_memory_first_gate.py`, `tests/test_memory_first_gate_hook.py`, `tests/test_memory_first_gate_hermes.py`,
  `docs/changelog/0.82.0-memory-first-gate.md`
- Modify: `features/common/hooks/hooks.json`, `features/common/hooks/hooks-manifest.json`,
  `features/common/hooks/ai_badger_hooks.py`, `features/hermes/adjustments/adjust_hooks.py`,
  `features/copilot/adjustments/adjust_hooks.py`,
  `features/common/skills/ai-raccoon-memory/scripts/memory_grade_hook.py` (recorder fold-in),
  `tests/test_hook_wiring_claude.py`, `tests/test_adjust_hooks_copilot.py`, `tests/test_hermes_plugin_install.py`,
  `tests/test_hooks_manifest_agent_coverage.py` (if the new entry needs coverage updates), `VERSION`,
  `references/hooks-subsystem.md`, `docs/adr/`

**ai-raccoon repo:**

- Regenerated: `.ai-badger/hooks/hooks.json`, `.github/hooks/ai-badger-hooks.json`,
  `.ai-badger/skills/ai-raccoon-memory/scripts/*`, `.hermes.md`/`CLAUDE.md` if the scaffold touches them
- User scope: `~/.hermes/plugins/ai-badger/*` (plugin.yaml + new module)
- Docs: `docs/work/2026-08-06-enforce-memory-mcp-first-hooks.md` outcome note

## Risks / tradeoffs / open questions

1. **Bash/terminal false positives** — blocking any command containing grep breaks build steps (`git log | grep x`).
   Mitigation: first-token-only matching (Task 1.1 pins this in tests). Bash `if:` conditions on Claude may only support
   prefix patterns — fallback is gating `Grep|Glob` only (Task 3.1).
2. **Copilot fail-closed** — a crash in the gate denies the tool. The hook must exit 0 on every path (Task 4.4 tests
   this explicitly). Timeouts are fail-open, so keep timeoutSec small.
3. **Denial loops** — agent re-issues the same grep and burns turns. Mitigation: 3-strike pass-through guard (Task
   1.2) + reason message that explicitly permits re-issue after memory consultation. Whether 3 is the right number is a
   pilot question.
4. **Hermes gateway multi-session** — plugin state is per-process keyed by project cwd (no session_id in plugin
   callbacks); a gateway handling several sessions for one project can leak the consulted flag across sessions.
   Documented limitation; shell hooks (config.yaml, payload carries session_id) are the alternative if it bites — out of
   scope for v1.
5. **Copilot cloud agent** — ephemeral sandbox per job; the per-session marker cannot persist across jobs. The
   repo-level hook file loads, but enforcement is effectively per-job-fresh. Document as known limitation.
6. **project_id derivation** — repo basename vs. the bank's canonical ids; ai-raccoon/ai-badger/job-search-ai-assistant
   match, other repos may need `AI_RACCOON_PROJECT_ID` env override. Open question.
7. **Instruction layer stays** — the gate complements, not replaces, the "Search memory FIRST" blocks in `.hermes.md`/
   `CLAUDE.md` and the pre_llm_call context injection; they carry agents that have no hook surface (e.g. Copilot cloud
   agent instructions).

## Verification checklist (final)

- [ ] `pytest tests/ -q` green, no skips (ai-badger main checkout, `.venv/bin/python3`)
- [ ] `.lefthook verify.sh` pre-push gate passes
- [ ] All three hosts demonstrably block text search before memory consultation and pass after (Task 5.3 transcripts)
- [ ] `hermes plugins list` → ai-badger enabled; `plugin.yaml` lists `pre_tool_call`
- [ ] Phase 0: one commit removing junie; `grep -ri junie` sweep shows only changelog history + ADR 0016; pytest green;
  junie config rejected by schema
- [ ] Two more PRs (ai-badger release-shaped 0.83.0, ai-raccoon regeneration), no direct pushes to main
- [ ] Changelog entries exist (0.82.0 junie removal, 0.83.0 gate); docs/work record updated
