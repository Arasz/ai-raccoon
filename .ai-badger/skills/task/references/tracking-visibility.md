# Tracking visibility

`status-report` only sees what the tracker recorded. These are the failure
modes that once left a task invisible mid-flight:

- **`start` must exit 0 before any branch, worktree, or commit.** On exit 2
  (session already claimed, or no session source resolved) STOP: pass
  `--session-id` explicitly (copilot ships no session source, so it always
  must), or `reattach` after a resume. Work done with no tracker row is
  invisible to status except as an "untracked worktree" name.
- **`start` takes a derived `{repo-alias}-{key}` id, never prose.** An id that
  is not letters and digits joined by single hyphens (`take`, `I`, `1.`,
  `https`) exits 2, and `/task <free-form prose>` registers nothing until you
  derive the id. A stray row with no title, no branch and no worktree is
  removed with `task_tracker.py drop <taskId>`; anything else is refused.
- **A stale session record misdirects `start`.** Exact env identity
  (PI_/HERMES_/CLAUDE_ session id) wins over pid/cwd guesses inside the
  tracker, but a sessions row whose pid is long dead is still a hygiene flag
  — status marks it STALE. On a strange "already attached" refusal,
  re-resolve with explicit `--session-id` rather than working untracked.
- **Lane worktrees are `<taskId>-lane-*`** under `.ai-badger/worktrees/` —
  status recognises only that shape (plus the orchestrator's own `<taskId>`);
  any other worktree name reports as untracked.
- **Write the plan where status reads it:**
  `.ai-badger/task-tracking/plans/<YYYY-MM-DD>-<taskId>.md` — taskId in the
  filename, one `**P<N> …**` heading per package, one `- [ ]` checkbox per
  acceptance point (checked as points land). Only a filename carrying the
  whole taskId matches; review documents (`.review.md`, `.plan-review-*`)
  never count as the plan. A plan living only in a delegation brief reports
  as "(no plan file)": confirm the status script shows it matched (not
  fallback) before dispatching implementation.
- **Record `total_tokens` on EVERY delegation completion** before the next
  dispatch — a mid-flight lane is invisible to status until its record lands.
  `--delegation <id>` wherever the harness keeps receipts (pi subagent-logs,
  hermes async_delegations, claude transcripts); positional totalTokens
  otherwise (copilot, mechanical lanes).
- **`finish` keeps a worktree holding work that exists nowhere else.** Read
  the `worktree.keptBecause` field: a kept worktree is unmerged or
  uncommitted work, not failed cleanup. Resolve it and re-run, or pass
  `--keep-worktree` when deliberately leaving it in place.
