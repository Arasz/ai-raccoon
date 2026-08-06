---
name: ai-badger-project-ops
description: >-
  Use for ai-badger project ops: skills, refresh, links.
---

# ai-badger project operations

The ai-badger framework's own skills (`task`, `welcome-ai-badger`, `den-refresh`,
`scaffold-documentation` — project copies under `.ai-badger/skills/`) describe what the
tools do. This skill carries the Hermes-integration facts their docs assume you know:
symlink lifetimes, scaffold ordering, and how opt-in skills get enabled.

## The Hermes skill-link lifetime rule (the big one)

`~/.hermes/skills/<project>/*` are **symlinks into `<scaffold-target>/.ai-badger/skills/`**.
welcome-ai-badger / den-refresh / scaffold.py repoint them at whatever `--target` they ran
against. Consequences:

- Scaffolding inside a task worktree repoints the links at the worktree path; `task_tracker.py
  finish` then **deletes the worktree → dangling links → the skills vanish from Hermes**.
  Run skill-enablement scaffolds on the durable checkout (master/main), never in the worktree.
- The user's explicit ordering (correction, 2026-08): **merge pending work to main first,
  THEN scaffold on master** — a dirty tree pollutes the scaffold commit's diff review. The
  config edit (`include.skills`) and the scaffold commit land on master per that instruction.
- Repair after a mistake: `relink_hermes_skills` removes dangling links too (a broken symlink
  still `is_symlink()`), so re-running the scaffold against the durable checkout heals them.
  For a link-only fix, call `skill_delivery.relink_hermes_skills(master, config, skills)`
  directly instead of a full re-scaffold (which rewrites manifest.json).
- The generated root `CLAUDE.md` copy is managed — its task-skill line-budget flag is a
  framework-template observation, never a hand-compaction target ("Do not edit this copy").

## Enabling opt-in skills

- Opt-in skills are catalogued but withheld until named. Enable via the schema-valid top-level
  `include` key in `.ai-badger/config.json`: `{"include": {"skills": [...]}}`. The group name
  `"documentation"` expands to scaffold-documentation + update-documentation + migrate-
  documentation (`badger_lib.SKILL_GROUPS`); listing all 8 opt-in names explicitly is the
  clearest form.
- **This user wants ALL opt-in skills enabled** (debug-issue, evidence-first-research,
  explore-codebase, migrate-documentation, refactor-safely, review-changes,
  scaffold-documentation, update-documentation), not just the group a task names.
- The scaffold is idempotent: same config → only missing items added, zero drift on a
  same-version re-run. Verify with `git status` + a Hermes link count
  (`ls ~/.hermes/skills/<project>/ | wc -l` should equal the skills dir count).

## Running the refresh (pitfalls)

- `$AI_BADGER` is often unset in Hermes sessions — locate the framework checkout (e.g.
  `/Users/arasz/RiderProjects/ai-badger`, verify `VERSION` matches config.json's
  `frameworkVersion`) and pass `--root` explicitly instead of relying on the env var.
- Run framework scripts with the framework venv python
  (`/Users/arasz/RiderProjects/ai-badger/.venv/bin/python`), not bare `python3` — the macOS
  CLT build has no site-packages (no jsonschema).
- **Target selection when the session cwd IS the framework checkout.** A refresh run with
  `--target .` inside the framework repo is a no-op (sources == scaffold origin). Find the
  real targets first:
  `grep '"frameworkVersion"' /Users/arasz/RiderProjects/*/.ai-badger/config.json` vs
  `cat "$AI_BADGER/VERSION"`, then refresh each behind project with
  `--target <project> --root "$AI_BADGER"`. The den-refresh skill assumes you are in the
  target repo, so when several projects are behind, surface the choice (2026-08-06: jsaa +
  arasz-home-page refreshed in one session, both 0.79.0 -> 0.80.0).
- **Stale local main is the norm under concurrent sessions — branch, commit, then
  `git rebase --autostash origin/main`.** A parallel session merging while you refresh
  leaves local main behind origin/main; a plain rebase refuses because tracked dirty files
  (jsaa's `.ai-badger/state.json` carries the task tracker's state) block it. `--autostash`
  stashes them around the rebase and pops them back, giving your PR a clean merge-base on
  the current origin/main. (Observed 2026-08-06: rebase onto #751, conflict-free.)
- **Seed-once state.json may be dirty with ANOTHER task's tracker state — never sweep it
  into the refresh commit.** jsaa's `.ai-badger/state.json` holds PR_READY entries and
  `lastUpdated` written by other sessions' task-tracker work. Stage the refresh footprint
  (root-level agent files + `.ai-badger/` minus state.json), then
  `git restore --staged .ai-badger/state.json` before committing; same for untracked
  review docs from parallel tasks. After the commit, `git status` should show exactly the
  pre-existing dirt.
- **The pre-commit scaffold-freshness-guard STASHES unstaged changes.** pre-commit stashes
  whatever is not staged, runs hooks, then restores. If the working tree has unstaged
  scaffold outputs (e.g. `.ai-badger/manifest.json`, refreshed skill copies) while you
  `git add` only the source files, the hook's re-scaffold comparison runs against the
  STASHED (old) tree → false failure listing exactly the unstaged files ("content differs,
  stale / regenerates differently"). Fix: `git add` ALL modified paths together — never
  commit with mixed staged/unstaged scaffold outputs. (Hit 2026-08-05 on the memory-grade
  hook; the guard passes fine when run standalone — the failure only appears inside
  pre-commit.)
- If the framework's `index.json` is missing/stale, run `tooling/index_build.py` first.
- **Capture the full JSON report from the FIRST run.** The first invocation does the
  re-scaffold; a re-run then reports `reScaffolded: false` with zero drift while `git status`
  shows the changes the first run made — a clean second report is a no-op confirmation, not
  proof nothing happened. Don't pipe the first run through `tail` and lose the drift section;
  redirect to a file and parse it.
- A project-edited `config.json` (summary/domain text) surfaces as `drift.configChanged` and
  the re-scaffold regenerates every managed agent file (CLAUDE.md, HERMES.md, .hermes.md,
  copilot-instructions copies) with the new text — expected, not a bug. Seed-once files
  (state.json, markers-context.json, model.json) stay out of the diff.
- **Stage the refresh with a root `git add -A`, never directory pathspecs.** `git add -A
  .ai-badger .claude .github` misses root-level managed files — in the 0.77.3 refresh
  `.mcp.json` was left unstaged, producing two same-message commits. Squash with
  `git reset --soft HEAD~2 && git commit`. A lefthook pre-commit runs but skips everything
  (no staged matching files) — not a failure signal.
- **The scaffold's `<file>.bak-<timestamp>` backups are transient** (e.g. `.mcp.json.bak-*`,
  `.github/mcp.json.bak-*`). Diff against `git show HEAD:<file>`: byte-identical means the
  pre-refresh content is preserved in git history — delete them, never commit.
- **Concurrent-session staging hygiene (ai-raccoon):** snapshot `git status --short` BEFORE
  running refresh so you know what was already dirty. The refresh output is exactly the managed
  files (`.ai-badger/` minus status-notes.json/task-tracking/stack-ignore.json, plus the root
  regenerated agent files) — stage it with an explicit path list, NOT `git add -A`, because a
  root `add -A` sweeps in the pre-existing session-state files AND anything a parallel session
  is editing (observed 2026-08-06: `tests/.../McpServerSetupHostTests.cs` appeared mid-refresh).
  After the commit, re-run `git status --short` and confirm only the pre-existing dirt remains.
- In the JSON report, `scaffold.entries` is an **int** (entry count), not a list — a summary
  parser that does `len()` on it crashes; read it as a scalar.

## After the refresh: push to green merge (user preference)

- A refresh does not end at the diff review. **User preference (2026-08, `f:` marker): run the whole
  loop** — branch → commit → push → watch CI until green → fix anything red → merge to
  main (`gh pr merge <n> --squash --delete-branch`). Never stop at "draft PR pushed" and never
  ask commit-vs-discard; the deliverable is the refresh merged on the default branch.
  **Explicit instruction scoping wins over the default loop:** when the user says "commit,
  create PR for each", the deliverable is the OPEN PR — he merges immediately himself, so
  stop there (2026-08-06: jsaa #752, arasz-home-page #209).
- **Per-repo divergence:** that PR loop is the job-search-ai-assistant norm. In ai-raccoon the
  framework-bump history is all direct commits on main (`chore: bump ai-badger framework to
  X.Y.Z` — fd522d8, f919480, 2f8ab76, 237dba8), and asked outright (2026-08-06) the user chose
  "commit directly on main, no PR". Match the repo's own history; for ai-raccoon a direct-main
  commit (no branch, no PR) is the expected deliverable — push after commit.
- In ai-raccoon, the working tree is never quiet: `status-notes.json`, `task-tracking/*` are
  session-state files that are dirty between sessions, and parallel agent sessions edit
  `tests/**` mid-refresh. Commit message for the bump follows history:
  `chore: bump ai-badger framework to <ver> — <headline changes>`.
- In job-search-ai-assistant a framework-managed-only push (`.ai-badger/` + regenerated agent
  files + `.mcp.json`/`.claude/settings.json`) triggers ONLY the docs lane of the lefthook
  pre-push gate (~19s pass; "Agent instruction validation passed"); the dotnet/frontend lanes
  do not fire, and CI path-filters the terraform/API-E2E lanes to `skipping`. Expect green in
  minutes — a multi-lane run means the branch carries real code changes too.
- The gate's agent-instruction soft warnings (CLAUDE.md/HERMES.md > 200 lines) are warnings,
  not failures.
- A refresh can deliver framework-catalog MCP declarations into `.mcp.json` +
  `.claude/settings.json` (e.g. the `hermes` server in the 0.77.1 refresh) — review that as
  any other delivered change.

## Reading den-refresh output

- `skillUsage` with no evidence channels (no Claude Code transcripts, no audit records)
  reports **nothing** as unused — `cannotTell` is the honest bucket; never propose pruning.
  The `hint` (enable call-behaviorist's audit log) is advisory only.
- `frameworkCopies` lists Claude Code's plugin-cache versions — report, never offer to delete
  (Claude Code owns that path). Only `~/.ai-badger/framework` is ever prunable, via
  `--prune-cache`.
- `hermesSkillLinks.created` lists the profile skill links the refresh ensured (symlinks into
  the project's `.ai-badger/skills/`); a non-empty `created` with empty `removed` is normal
  link maintenance, not drift. The linked skills ARE the framework's copies — never patch
  them in place; contribute fixes upstream via feed-badger.
- A safety `.ai-badger.bckp/` appears whenever a re-scaffold ran (it can also appear with
  `reScaffolded: false`). It is untracked noise in `git status` — **user preference (2026-08):
  gitignore it** with `/.ai-badger.bckp/` in `.gitignore` so the refresh commit stays scoped
  to the managed files. Safe to remove once the diff is reviewed.

## Hermes task-tooling notes

- `task_tracker.py start` requires `--session-id`; use `$HERMES_SESSION_ID` (mandatory for
  non-Claude hosts).
- `delegate_task` subagents expose no token counts — record `0` with an honest description
  in `task_tracker.py subagent`. (Hermes state.db DOES hold per-delegation tokens:
  `async_delegations.result_json` → `results[].tokens.{input,output}` — query it when the
  tracker's `--delegation` lookup says "no token record".)
- **No delegation model pinning on this machine:** `~/.hermes/config.yaml` `delegation:` has
  only `max_iterations` — subagents inherit the session's model. "Lane: opus/sonnet" in
  delegation.md is aspirational routing, not a model guarantee; judge a task run's model-mix
  accordingly (the task skill's hermes extension says exactly this: lanes are purpose, not
  model).
- `process(action=wait)` cannot wait on a delegation: delegate_task ids are not terminal
  sessions (returns not_found). Results re-enter the conversation as a message — continue
  other work and let it arrive; never poll the live transcript in a loop.
- `task_tracker` finish removes the worktree; a `worktree.keptBecause` field is the only
  signal something unmerged is still in it.
- **state.json conflict after finish when a doc-audit PR carries the previous update.**
  The finish-protocol entry is written to the main checkout's `.ai-badger/state.json`, and
  the Phase-5 doc-audit agent commits it into its PR. If that PR is unmerged when the NEXT
  task's baseline commit also touches state.json (or origin/main advances), `git merge
  --ff-only origin/main` leaves `UU .ai-badger/state.json`. Resolve by combining: take
  `git show origin/main:.ai-badger/state.json` as the base, prepend the new task's
  completedTasks entry, refresh `next`/`lastUpdated`, `git add` — never pick one side whole
  (you lose either the previous task's entry or the current one). The resolved file rides
  into the next doc-audit PR like the original did. (Hit 2026-08-05 after PR #304 merged
  while PR #303's state.json was still in flight.)

## Memory-grade hook operations (0.79.0–0.80.0)

- **0.80.0+ ships the hooks as a Hermes DIRECTORY plugin** at `~/.hermes/plugins/ai-badger/`
  (`plugin.yaml` declaring hooks, `__init__.py` re-exporting `register`, sibling modules
  `ai_badger_hooks.py`, `memory_grade.py`, `debug_log.py`, …). Load is opt-in via
  `plugins.enabled: [ai-badger]` in `~/.hermes/config.yaml`. Flat `.py` drops in
  `~/.hermes/plugins/` are invisible to Hermes' loader (the 0.79.x trap — that shape never
  loaded). Verify a refresh actually delivered the fix: plugin dir exists, config lists it
  enabled, and the plugin copies byte-match the refreshed `.ai-badger/hooks/` sources.
- `post_tool_observer` (0.80.0) normalizes BOTH payload spellings: Hermes' plugin emitter
  sends `function_name`/`function_args`/`session_id` (no `cwd` — falls back to
  `os.getcwd()`), shell hooks send `tool_name`/`args`/`cwd`. When live-probing, pass the
  Hermes spelling.
- Memory-grade JSONL lines now carry `host` (hermes/claude/copilot) and `sessionId`;
  a missing capture line means "no capture", not "no usage" — the fields make them
  distinguishable. The `ai-raccoon-memory` SKILL.md ships a capture-verification checklist.
- After any version-bump refresh, re-check stale negative notes from older versions (e.g.
  "Hermes hooks dead in 0.79.x") — a bump may have fixed them; verify the shipped shape
  instead of trusting the old note.
- Machine-wide enable: `echo 'export AI_BADGER_MEMORY_GRADE=1' >> ~/.zshrc` AND
  `launchctl setenv AI_BADGER_MEMORY_GRADE 1` (GUI-launched hosts); verify with
  `launchctl getenv AI_BADGER_MEMORY_GRADE`.
- The plugin copies under `~/.hermes/plugins/` are what real sessions load — after a merge,
  diff `ai_badger_hooks.py` and `memory_grade.py` against the merged feature sources; a
  scaffold refresh in the worktree updates them, but verify rather than assume.
- Live-probe the installed plugin (not the worktree source): load
  `~/.hermes/plugins/ai_badger_hooks.py`, call `post_tool_observer` with a
  `mcp__ai_raccoon__memory_search` name + args, confirm the line in
  `~/.ai-badger/memory-grade/memory-quality.jsonl`, then grade it with
  `memory_grade.py grade <ts> <1-5>` and pop the pending ask (`pop_ask`) so no stale ask
  nags the next session.

## MCP tool index (mcp-tools.json) — description improvement pass

The `mcp-index` skill documents the tag/intent commands; it is a framework-owned copy
(like all linked skills — never patch in place, contribute upstream via feed-badger).
When its workflow isn't enough, the analysis recipe (verified 2026-08-06 on ai-raccoon):

- **Index JSON shape:** `sources[]`, each entry `{name, status, tools}` where `tools` is a
  DICT `{toolname: {tags, intent, origin}}` — not an array. Analyze with a small python
  pass listing entries by `origin` and intent length.
- **`validate` exit 0 ≠ good descriptions.** Heuristic-origin entries can carry
  name-restating one-liners ("Close the page") that don't disambiguate siblings.
  Improvement candidates = `origin == "heuristic"` with short intents (<50 chars);
  catalog entries (capped ~200 chars) are already curated, `manual` entries were human-set.
- **`mcp-index intent` promotes the entry to `manual` origin** — a later `update` keeps
  the rewrite. Batch the rewrites in a loop, then re-run `validate`.
- Worked example: 26 rewrites (playwright browser tools, glider package consolidation,
  a memory delete) moved origins 77 manual → 103 and cut sub-50-char intents 17 → 4.

## References

- `references/docs-init-2026-08.md` — worked example: enabling all opt-in skills on master,
  the symlink shapes, the docs-tree scaffold, and the review that caught the
  dotnet-run/launch-profile client bug.
- `references/refresh-0.77.1-jsaa-2026-08.md` — worked example: the 0.74.0 -> 0.77.1 refresh
  (real invariant/skill/MCP changes), report-shape notes, and the gate/merge behavior for a
  framework-only push in job-search-ai-assistant (PR #716).
- `references/refresh-0.77.3-arasz-home-page-2026-08.md` — worked example: a large-gap
  refresh (0.59.0 -> 0.77.3): 7 newItems, 142 entries, the `.bak` handling, the
  directory-pathspec staging miss (.mcp.json) and squash fix, skillUsage limits wording.
- `references/refresh-0.80.0-ai-raccoon-2026-08.md` — worked example: 0.79.1 -> 0.80.0 on
  ai-raccoon: the Hermes directory-plugin hook ship (plugin.yaml + register, opt-in
  plugins.enabled), dual payload spellings in post_tool_observer, host/sessionId
  memory-grade lines, concurrent-session staging hygiene, direct-to-main commit.
- `references/refresh-0.80.0-multi-project-2026-08.md` — worked example: one session
  refreshing TWO projects (jsaa + arasz-home-page 0.79.0/0.77.3 -> 0.80.0): target
  selection from inside the framework checkout, staging exclusions (other-task state.json,
  review docs, .mcp.json.bak), rebase --autostash, the VERIFY_SKIP=docs gate episode,
  skillUsage prune-candidate reporting.
