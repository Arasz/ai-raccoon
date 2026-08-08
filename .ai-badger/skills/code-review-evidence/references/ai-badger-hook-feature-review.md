# ai-badger hook-feature review (worked case: memory-grade hook, PR #304)

Review of an env-gated, tri-agent hook feature in the ai-badger framework
(`task/memory-grade-hook`). All 29 new tests passed; the gaps below are what
the evidence audit found anyway. Reuse the checkpoints for any ai-badger
`features/common/hooks/` feature branch (commit-reminder, context-enrichment,
task hooks, ...).

## The feature shape (what to expect on these branches)

- New logic module lives in `features/common/skills/<feature>/scripts/`
  (`memory_grade.py`), a thin transport entry per agent
  (`memory_grade_hook.py` for Claude/Copilot PostToolUse).
- Hermes wiring: a `# <Feature>` section in
  `features/common/hooks/ai_badger_hooks.py` — lazy sibling import
  (`_load_<feature>()` mirroring `_load_commit_reminder`), called from
  `post_tool_observer` (exception-guarded) and from `pre_llm_inject_context`
  (NOT guarded — see gap 2).
- Manifest: `features/common/hooks/hooks-manifest.json` entry naming all
  three agents — claude `{type: hooks-json, event: PostToolUse}`,
  hermes `{type: plugin, method: post_tool_call}`, copilot
  `{type: hooks-json, event: postToolUse}` (note the event casing differs per
  agent: `PostToolUse` vs `postToolUse`).
- `features/common/hooks/hooks.json` gains a PostToolUse matcher entry;
  `features/hermes/adjustments/adjust_hooks.py` `SHARED_SKILL_MODULES` gains
  the `(skill, filename)` tuple so the sibling ships beside `ai_badger_hooks.py`
  in both `~/.hermes/plugins/` and `.ai-badger/hooks/`.
- The copilot rewrite (`features/copilot/adjustments/adjust_hooks.py`) wires
  entries automatically by iterating the manifest and filtering source
  commands by script suffix — so a new manifest entry + hooks.json entry
  flows to `.github/hooks/ai-badger-hooks.json` with no copilot-side change.
- Release: VERSION bump + `docs/changelog/<v>-<slug>.md` + changelog README
  row + all `version_sync` targets (`.claude-plugin/plugin.json`,
  `.claude-plugin/marketplace.json`, `index.json` via index_build) + scaffold
  version stamps (CLAUDE.md/HERMES.md/.hermes.md/copilot-instructions.md).

## Findings from the worked review (each is a reusable check)

1. **Untested inert-without-sibling path (minor).** The commit-reminder
   precedent pins it (`test_post_tool_observer_is_inert_without_..._module` /
   `test_pre_llm_inject_context_is_inert_without_..._module` in
   `tests/test_commit_reminder_hermes.py`); the memory-grade suite (always
   injecting the sibling into `sys.modules`) has no equivalent. Check for the
   precedent-parity pair on BOTH callbacks.
2. **Loader called outside try/except in the per-turn callback.** A
   `_load_x()` that raises in `pre_llm_inject_context` breaks every LLM turn
   on every host; the post_tool side is wrapped, the pre_llm side is not.
   The None-path must be proven by a test, or the whole host is one bad
   refactor from dead.
3. **Shared logic stashes for transports that never pop (minor).** The Claude
   hook returns `additionalContext` directly, but the shared `log_search`
   also wrote the Hermes `pending.json` — a later Hermes session pops a stale
   ask from a Claude session. The plan said "no stash needed" for Claude; the
   code deviated. Fix shape: `stash: bool = True` parameter.
4. **Exit-0-always contract vs unguarded IO (minor).** `log_search`'s
   `_append_log`/`_save_pending` can raise OSError; on the Claude path that
   escapes `main()` as traceback + exit 1 (PostToolUse failures don't block
   the tool call, but the contract says advisory, exit 0). The precedent
   (`commit_reminder.py`) guards its IO internally. Compare the precedent's
   guards against the new module.
5. **Env-pinning verified empirically.** Ran the 6 new test files with
   `AI_BADGER_MEMORY_GRADE=1` exported — 29/29 pass, proving the OFF-path
   `monkeypatch.delenv/setenv` pinning is real (the plan enabled the var
   machine-wide for dogfooding, so ambient ON was a live risk).

## Adversarial sweep checklist (all verified by reading, most by test)

- env absent / `0` / `yes` / `true` / `garbage` / `""` → feature off, zero IO,
  zero injection (guard is exact `os.environ.get(...) == "1"`).
- Two events before one turn → per-project keyed stash, last-wins; the first
  line stays logged unanswered (honest nulls, not loss).
- Unknown pointer (ts) → exit 1, no file mutation (byte-for-byte other lines).
- Absent optional arg → `null`; flag `""`-vs-null inconsistencies (projectId
  defaulted to `""` while workspaceId defaulted to `null` — nit).
- Non-string tool names never match; non-memory tools (`memory_write`,
  `memory_stats`, `terminal`, `mcp__code_review_graph__get_prompt`) never
  match; all name spellings match (`mcp__the_project__memory_search`,
  `mcp__<server>__memory_search`, `<server>:memory_search`, bare).
- Matcher is unanchored-regex-search per Claude Code semantics — same shape
  as the existing `Edit|Write|MultiEdit|NotebookEdit` precedent.
- Dual-spelling args (`project_id`|`projectId`, `workspace_id`|`workspaceId`)
  read defensively, tested on both the Hermes and Claude paths.
- Deployment on older scaffolds: `.claude/settings.json` entries carry the
  `[ -f ... ]` guard fallback; the hooks.json `${CLAUDE_PLUGIN_ROOT}` path
  always has the sibling beside it (same release).
