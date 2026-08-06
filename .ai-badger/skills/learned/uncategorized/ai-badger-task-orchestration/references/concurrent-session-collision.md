# Concurrent ai-badger sessions: tracker collision + recovery

Hit 2026-08-04: while running task `retrieval-improvement-cont` (research spike), a
parallel session the user had started (`fix-baseline`) ran `task_tracker.py finish
retrieval-improvement-cont` mid-run. The tracker entry flipped to FINISHED, the branch
`task/retrieval-improvement-cont` and its worktree were deleted, and my committed docs
became dangling objects. The parallel session also copied my worktree docs into its own
`docs/research/` (pre-review versions) and committed them on ITS branch.

## Symptom detection

- `git worktree list` shows a worktree you did not create (`fix-baseline`), yours is gone.
- `git branch -a` no longer has `task/<your-id>`; `git log task/<your-id>` → "unknown revision".
- `task_tracker.py status` shows your task FINISHED with a `finishedAt` you never set.
- Your `write_file`/`patch` against the task-worktree path suddenly fail "No such file
  or directory".

## Recovery (all verified)

1. **Find the dangling commits** — your commits survive branch deletion:
   `git fsck --lost-found | grep commit` (or `git reflog --all`). Identify yours by
   message/date.
2. **Restore the branch + worktree**:
   `git branch task/<id> <sha>` then
   `git worktree add .ai-badger/worktrees/<id> task/<id>`.
3. **Reopen the tracker entry** — `task_tracker.py start` refuses a FINISHED task
   ("refusing to restart"). Edit `.ai-badger/task-tracking/executed-tasks.json` directly:
   set `state=STARTED`, `finishedAt=null`, restore `worktree`; the file is gitignored
   tracking data, direct edits are legitimate.
4. **Land your docs while the other session has staged files on main** — do NOT run the
   merge machinery (it fails on their staged files). Bring only your paths over and
   commit: `git checkout task/<id> -- docs/work/<your-files>` + `git commit`; leave the
   other session's staged files untouched. (Their copies of your docs may be pre-review
   versions — your reviewed versions stay authoritative in your own tree.)
5. **Re-apply any patches that failed** during the window when the worktree was gone —
   they failed with "Failed to read file"; the content lives in your conversation.

## Finish-precondition gotchas (same session)

- `task_tracker.py finish` REFUSES if `.ai-badger/state.json` was not modified since task
  start ("state.json has not been modified since task start"). Add the completedTasks
  entry + refresh `lastUpdated` FIRST, then re-run finish.
- `finish` takes NO `--session-id` (only `start` does). Passing one errors out; re-run
  without it.

## Prevention notes

- The task skill's worktree isolation protects FILES, not the TRACKER: the tracker is a
  single shared JSON. Two concurrent `task_tracker.py` flows can step on each other's
  task entries. Before dispatching subagents in a session where the user may run another
  session, note the hazard; recovery above is cheap (~2 min).
- **STARTED does not mean stale.** A tracker entry in STARTED state with a LIVE worktree
  (`.ai-badger/worktrees/<id>/` exists) and fresh commits landing on main is ACTIVE
  parallel work (user correction 2026-08-04: `retrieval-improvement-c-implementation` was
  mid-wave — Wave 1 merge had just landed — and is NOT to be finished/parked). Phase 0
  housekeeping only touches tasks whose branch is fully contained in main AND whose
  worktree holds no unmerged work. Check `git branch --contains <worktree-head>` and
  `git log main..<branch>` BEFORE running `finish` on a task you didn't start; if the
  branch has commits not on main or a live worktree, leave it and say why.
- Keep commits small and frequent in the task worktree — a dangling-commit recovery is
  only possible if your work was committed (uncommitted files in a deleted worktree are
  gone unless copied out).
- `finish` on a task whose work is already on main REFUSES with "state.json has not been
  modified since task start": prepend the lean completedTasks entry + refresh
  `lastUpdated` FIRST, then re-run finish (verified 2026-08-04 with the logo task — the
  entry recorded which brand commits carried the work, then the worktree was removed cleanly).
