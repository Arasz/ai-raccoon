# User preferences (session-corrected defaults)

- **Docs bundled into feature PRs** — always include doc updates/new entries in the same PR as the
  feature code. When dispatching subagents, instruct them to update docs/flows.md, docs/data-model.md,
  docs/architecture.md as part of the feature implementation. Never create a separate docs-only PR
  unless the user explicitly asks for one.
- **Push branches often** — push each worktree branch as soon as it has a commit, even before the
  agent finishes, so the user can follow progress via GitHub.
- **Update GitHub issues** — after EACH merged PR, post a completion comment on the issue:
  `gh issue comment <id> --body "## Implemented ✅ — PR #<n> (merged). <details>"`
  At session end, verify all issues have completion comments.
- **Draft PRs for visibility** — create a draft PR as soon as the branch is pushed, even if work
  is still in progress. Mark ready + admin-merge when tests pass locally.
- **"Don't touch main" is strict** — when the user says "worktrees only", never `git checkout main`,
  never `git merge` into main from the main checkout, never `git reset` on main. ALL integration
  happens via PR merge. The main checkout is read-only for the entire session.
- **No Rider MCP when other agents use it** — if the user says Rider is occupied, instruct all
  subagents to use terminal/read_file/write_file/search_files only. Include this in the delegate_task context.
