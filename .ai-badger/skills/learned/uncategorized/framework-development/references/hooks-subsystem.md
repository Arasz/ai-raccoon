# Hooks subsystem map (verified 2026-08-05)

Architecture of the ai-badger hooks subsystem — what fires where, per agent. Re-verified while writing `docs/plans/memory-grade-hook.md` (branch `task/ai-raccoon-integration`, worktree; plan implements as a SEPARATE PR after #302 merges).

## Two surfaces, one logic split

- **Hermes**: `features/common/hooks/ai_badger_hooks.py` is a plugin loaded from
  `~/.hermes/plugins/` (loose copies — see Deployment). **[STALE, corrected 2026-08-06:
  the loose-copy deployment NEVER loads.** Hermes loads only DIRECTORY plugins (`~/.hermes/plugins/<name>/plugin.yaml` + `__init__.py` with `register(ctx)`, opt-in via `plugins.enabled`); flat `.py` drops are invisible to
  `_scan_directory`, so none of the hermes entries in
  `hooks-manifest.json` has ever fired. The module ABI described below (register/ctx.register_hook signatures, callback kwargs) is accurate — the deployment shape is not. Verified diagnosis + fix direction:
  `references/hermes-plugin-deployment-gap.md`.**] Entry `register(ctx)` registers
  `on_session_start` / `pre_llm_call` / `post_tool_call`. Callbacks take
  `(cwd="", **kwargs)`; post_tool_call is `post_tool_observer(tool_name, result,
  duration_ms, cwd, **kwargs)` where kwargs carry `args`/`status`. Hermes has NO return channel from post_tool_call into the model context — inject-once works via a disk stash popped by pre_llm_call.
- **Claude/Copilot**: `features/common/hooks/hooks.json` is the rewrite SOURCE (paths
  `${CLAUDE_PLUGIN_ROOT}/features/common/skills/...` rewritten to `.ai-badger/skills/`). Hook scripts are separate processes reading the payload from stdin; PostToolUse emits
  `additionalContext` (advisory only, exit 0 — never `decision`/`permissionDecision`, the 0.33.0 discipline). Drift-notice is plugin-level via the repo-root `hooks.json`
  (Claude skips it in the rewrite).

## Stash/pop inject-once pattern (commit-reminder precedent)

- Stash file `~/.ai-badger/commit-reminder/pending.json`, a dict keyed by RESOLVED project path → message. `_set_pending_reminder(project, msg)` / `_pop_pending_reminder(project)`.
- `post_tool_observer` stashes; `pre_llm_inject_context` pops and prepends the message to `{"context": "..."}` (joined with other parts). Popped = surfaced exactly once; last-wins when two events stash before a turn (unanswered = silently
  dropped, fine).
- Kept deliberately SEPARATE from the commit-reminder ratchet state file (different lifecycle, no schema coupling). New features get their own file under
  `~/.ai-badger/<feature>/pending.json` (memory-grade: `~/.ai-badger/memory-grade/pending.json`).

## hooks-manifest.json coverage rule (issue #147)

Every hook feature gets a manifest entry naming ALL capable agents:
`HOOK_CAPABLE_AGENTS = ("claude", "hermes", "copilot")` in `tooling/validate.py`;
`test_hooks_manifest_agent_coverage.py` fails on a gap unless
`HOOKS_MANIFEST_AGENT_EXEMPTIONS` records an honest, non-trivial reason (text is checked, not just the key). Per-agent shapes:
`claude: {type: hooks-json, entry: hooks.json, event: <Event>, script: x.py}`,
`hermes: {type: plugin, entry: ai_badger_hooks.py, method: post_tool_call}`,
`copilot: {type: hooks-json, entry: hooks.json, event: postToolUse, script: x.py}`. Wire all three when the hook can run on all agents; use the exemption only for a real platform limit (e.g. session-start-tracking is Claude-only by design).

- **Copilot wiring has no skills filter (F1 trap, verified 0.79.0)**: the Copilot adjust (`features/copilot/adjustments/adjust_hooks.py`) wires EVERY manifest copilot entry with no skills filter and no scaffolded-script existence check — a
  new PostToolUse entry breaks exact-count assertions (`len(post_tool_use) == 1`) in copilot wiring tests; update them to assert PRESENCE among wired entries (e.g. filter by trailing script name). Claude's `hook_wiring.py`, by contrast,
  skips commands whose script is not scaffolded into the project ("not scaffolded — skipped"), so Claude-side count assertions survive a new entry.
- **Presence test (F3)**: for each new hook add a test asserting
  `set(entry["agents"]) == {"claude", "hermes", "copilot"}` for its manifest entry — the generic gap test only iterates hooks already in the manifest, so it stays green while the entry is absent, which is exactly the gap it exists to close.

## Deployment (adjust_hooks.py)

`features/hermes/adjustments/adjust_hooks.py` copies into `.ai-badger/hooks/` AND
`~/.hermes/plugins/`:

- `USER_PLUGINS = ("ai_badger_hooks.py", "learned_skills_sync.py")` (from hooks dir)
- `SHARED_SKILL_MODULES = ((skill_name, filename), ...)` — skill-scripts that must land BESIDE the plugin for lazy sibling imports (`_load_commit_reminder` /
  `_load_impact_estimator` use `importlib.util.spec_from_file_location` on
  `Path(__file__).parent / name`). Extend this tuple for new sibling modules (memory-grade adds `("ai-raccoon-memory", "memory_grade.py")`).
- `RETRIEVAL_MODULES = ("tokenizer.py", "bm25.py", "mcp_matcher.py")` from
  `features/common/retrieval/`.
- Copies record `frameworkRoot` + `copiedFromVersion` in
  `~/.hermes/plugins/.ai-badger/manifest.json`; `COPY_SKEW_REFUSAL` in the plugin refuses to register when copies are stale. `test_hermes_plugin_install.py` asserts the copy lists (its `SHARED_SKILL_FILES` tuple must track
  `SHARED_SKILL_MODULES`).

## Tool-name matching

- Hermes deferred MCP tools arrive as `mcp__<server>__<tool>` (verified family:
  `mcp__code-review-graph__*`; tolerate both `mcp__ai_raccoon__` and `mcp__ai-raccoon__`).
- The MCP index (`_record_tool_index_check`) partitions on `:` — `server:tool`
  (`ai-raccoon:memory_search`). Bare names are built-ins, not MCP tools.
- A hook that must match a tool defensively normalizes all three forms to the bare name (last `__` segment / after `:`) before comparing.

## Config-knob convention

- All hook knobs are env vars: `AI_BADGER_COMMIT_REMINDER_THRESHOLD`,
  `AI_BADGER_COMMIT_ESCALATE_AFTER`, `AI_BADGER_COMMIT_REMINDER_IMPACT`, `AI_BADGER`
  (framework root). Guarded int-parse, silent default.
- Design decision for memory-grade (plan §2): default OFF via `AI_BADGER_MEMORY_GRADE=1`. Bank settings row was rejected: bank-global = machine-wide, BUT the bank is SQLCipher-encrypted whenever `AIRACCOON_DB_PASSPHRASE` is set, so an
  agent-side hook reading `~/.ai-raccoon/memory.db` settings would silently read OFF on encrypted machines and couples the framework to AiRaccoon's on-disk schema. Env var inherits into every host process; per-project opt-out later via
  `.ai-badger/config.json`
  checked only when the env is on.

## ai-raccoon bank facts (verified)

- `~/.ai-raccoon/memory.db`; settings table `settings(key TEXT PRIMARY KEY, value TEXT
  NOT NULL)` — bank-global (no project scope). Plain `sqlite3` read works when
  `AIRACCOON_DB_PASSPHRASE` is unset (EnvEncryptionKeyProvider returns null = no encryption), fails on encrypted banks.
- `memory_search(projectId, query, scope, workspaceId?, limit?, minScore?)` →
  `{"results": [{hash, seq, ranking, path, snippet, sourceFile, chunkIndex, totalChunks}]}`; projectId always required, workspaceId present only when a workspace is active.
- CLI has per-domain `config` subcommands (access/model/retrieval/sync/encryption/watch), no generic arbitrary-key reader.

## Round-trip design (SHIPPED 0.79.0, branch task/memory-grade-hook)

Hook logs the full JSONL line AT CALL TIME (`usefulness: null`) — the result payload is only reliably available to the hook; relaying it via the injected ask = prompt bloat — then a tiny helper (`memory_grade.py grade <ts> <1-5> [note]`)
fills the grade in place (one line per search preserved). Ask is one short line injected once; unanswered asks stay `null` (honest data). Log target: machine-wide `~/.ai-badger/memory-grade/
memory-quality.jsonl` (never the repo working tree — a repo-local log pollutes
`git status` and feeds the commit-reminder loop). Line shape = superset of the manual
`docs/work/2026-08-05-ai-raccoon-memory-quality.jsonl` (ts, query, scope, projectId, workspaceId, result.results[], usefulness, note).

Shipped pieces (all in `features/common/skills/ai-raccoon-memory/scripts/`):

- `memory_grade.py` — ALL logic: `enabled()` (env exactly "1"), `is_memory_search()`
  (normalize mcp__/colon forms to bare name), `log_search(args, result, cwd)` (builds the line, appends to LOG_FILE, stashes ask keyed by resolved cwd — returns the ask text, None when disabled), `pop_ask(project)` (inject-once, gated on
  enabled),
  `grade_line(ts, grade, note)` (guard 1..5, exact-ts lookup, rewrite file — other lines byte-identical), `probe()` (config state + log path + last 3 lines). Read args defensively for BOTH spellings: project_id|projectId,
  workspace_id|workspaceId (F4).
  `HELPER = Path(__file__).resolve()` so each deployment shape's ask points at its own copy.
- `memory_grade_hook.py` — Claude/Copilot PostToolUse entry: reads stdin payload (tool_name|toolName, tool_input, tool_response, cwd), calls log_search, prints
  `additionalContext`; exit 0 always.
- ai_badger_hooks.py wires `_load_memory_grade()` + `_maybe_log_memory_grade()` into post_tool_observer (exception-guarded, same discipline as `_maybe_remind_commit`) and pops the ask in pre_llm_inject_context right after the
  commit-reminder pop.
- Manifest entry names all three agents; hooks.json PostToolUse gains a second entry with matcher `"memory_search"`.

## Test conventions for hooks

- `tests/conftest.py` `load_script` fixture imports a repo-relative script fresh;
  `test_commit_reminder_hermes.py` is the template: monkeypatch the pending-file constant to `tmp_path`, stub sibling modules by inserting a fake module into
  `sys.modules` under the exact `*_MODULE_NAME` constant, assert file contents.
- Claude hook tests drive `main()` with a stdin-shaped payload dict and capture stdout/additionalContext.
- **F8 sibling injection (memory-grade, verified)**: `_load_memory_grade` returns None in the framework checkout (the script lives under `features/common/skills/...`, never beside ai_badger_hooks.py), so tests inject the REAL module — `real = load_script(...);
  monkeypatch.setitem(sys.modules, hooks.MEMORY_GRADE_MODULE_NAME, real)` — and redirect paths with `monkeypatch.setattr(real, "LOG_FILE", tmp_path / ...)`. Register the real module object directly rather than copying its functions into a
  fresh ModuleType: the functions' `__globals__` IS the real module's dict, which is what makes the constant patch take effect.
- **cwd-key chdir trap (bit us, WP3)**: post_tool_observer receives cwd explicitly, pre_llm resolves via `os.getcwd()`. Stash tests pass while injection tests fail when the test does not `monkeypatch.chdir(tmp_path)` — the pop looks up a
  different key. Always chdir when the round-trip crosses both callbacks.
- **Claude-hook path redirection**: the hook module does `sys.path.insert; import
  memory_grade` at import time, so inject the redirected module into `sys.modules` under the BARE name ("memory_grade") BEFORE load_script-ing the hook module.
- **F5 env pinning**: every config-gate test pins the env var explicitly (`monkeypatch.delenv(...)` for OFF, `setenv(..., "1")` for ON) — never rely on the ambient environment (a machine-wide enable makes OFF-path tests silently vacuous).
