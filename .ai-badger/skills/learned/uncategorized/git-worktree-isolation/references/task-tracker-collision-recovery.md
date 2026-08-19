# Task-tracker collision: another session finished your taskId and removed your worktree

Verified 2026-08-19 (ai-raccoon, Hermes session racing a Claude Code session).

## The mechanism

The ai-badger task tracker store (`.ai-badger/task-tracking/executed-tasks.json`) is SHARED across every agent session on the repo — Claude Code, Copilot, and Hermes write the same file. Task ids are the collision key:

1. Session A starts task `X` (`task_tracker.py start X`), creating the worktree
   `.ai-badger/worktrees/X` on branch `task/X-slug`.
2. Session B starts the SAME task id `X` minutes later. `start` finds the existing entry, prints a worktree path, and re-creates/adopts the worktree.
3. Session A ends — a weekly usage-limit cutoff fires its stop-hook, which runs
   `finish X`. That marks the entry FINISHED **and removes the task's worktree**
   (`git worktree remove`) — including the one session B's `start` just created.
4. Session B's next `start` refuses: "Task X is already FINISHED; refusing to restart it." `git worktree list` no longer shows the path.

Nothing is lost if the raced session did no work (the finish's blocker check refuses to remove a worktree holding uncommitted files). The collision is bookkeeping + directory, not data.

## Diagnosis

- `git worktree list` — is the path registered at all?
- The entry in `.ai-badger/task-tracking/executed-tasks.json` — check `sessionId`,
  `trackingSource` (`claude` vs `hermes`), `startedAt`/`finishedAt`. A FINISHED entry you did not finish, whose sessionId names another session, plus a vanished worktree = another session raced you.
- Check the other session's transcript tail for a cutoff ("You've hit your weekly limit" etc.) to confirm it died mid-task.

## Recovery

1. Flip the entry's `state` back to STARTED under the store lock (writers hold an exclusive flock on `.ai-badger/task-tracking/.write.lock` and replace the file atomically):

   ```python
   import json, fcntl, os
   path = '.ai-badger/task-tracking/executed-tasks.json'
   with open('.ai-badger/task-tracking/.write.lock', 'w') as lock:
       fcntl.flock(lock, fcntl.LOCK_EX)
       with open(path) as f:
           data = json.load(f)
       for t in data['tasks']:
           if t.get('taskId') == '<taskId>':
               t['state'] = 'STARTED'
               t['finishedAt'] = None
       tmp = path + '.tmp'
       with open(tmp, 'w') as f:
           json.dump(data, f, indent=1)
       os.replace(tmp, path)
       fcntl.flock(lock, fcntl.LOCK_UN)
   ```

2. Re-run `task_tracker.py start <taskId> --title "..." --branch task/<taskId>-<slug>`.
   `start` refuses FINISHED entries but re-opens STARTED ones: it re-creates the worktree, overwrites sessionId/transcriptPath/trackingSource with YOUR session, and clears finishedAt. Verify the worktree with `git worktree list` and
   `git -C .ai-badger/worktrees/<taskId> branch --show-current` — never trust the printed path alone.
3. If the other session is still alive, its stop-hook can re-finish the entry again. Check for a live process before trusting the reopened state; warn the user to close the other window.

## Related

- `start`/`finish`/`reattach` semantics and file schemas:
  task skill's `references/file-schemas.md` (executed-tasks.json, current-session.json, the flock + atomic-replace write pattern).
- The `--delegation <id>` subagent-token record fails for Hermes dispatches ("no token record in this session source") because the delegation manifest carries no token data — the tracker refuses to fabricate; record a manual count only if
  you have one, otherwise skip and note it.
