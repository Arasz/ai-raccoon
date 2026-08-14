---
name: worktree-agent-isolation
description: "Use when running multiple agents in parallel, or when the user says 'worktrees only', 'agent isolation', 'parallel workstreams', or 'don't touch main': give each agent its own git worktree branched from origin/main (fetch first), integrate via GitHub PRs, keep the main checkout read-only, and avoid shared obj/ races and file-modification conflicts."
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: optIn
metadata:
  hermes:
    tags: [worktrees, parallel, agents, isolation]
    related_skills: [task]
---

# Worktree Agent Isolation

Run N independent coding agents in parallel, each in its own git worktree, with
zero file conflicts. Integrate via GitHub PRs — the main checkout is read-only.

## Why

Two agents in the same directory cause:
- **Build failures:** Shared `obj/` directories cause MSB3492 or "Building target
  completely" errors. Workaround is `rm -rf obj/` but it is destructive and racy.
- **File modification races:** One agent patches stale content.
- **Test corruption:** Process-global state (env vars, temp dirs) interferes.

## When to Use

- User says "worktrees only", "don't touch main", "agent isolation"
- Multiple independent tasks can run in parallel
- Another agent/session is using the main checkout
- User wants to see draft PRs for in-flight work visibility

## Setup

```bash
# Fetch latest main first — ensures worktrees branch from the true HEAD
git fetch origin main

# Create N worktrees from origin/main (not local main, which may be behind)
git worktree add ../project-task-a -b task/<id>-<slug> origin/main
git worktree add ../project-task-b -b task/<id>-<slug> origin/main
```

Each worktree gets its own branch, own working directory, own build artifacts.

**Always use `origin/main`** as the base, not `main`. Local main may lag behind
after another agent merges. Fetching first ensures all worktrees share the same
ancestor, reducing rebase conflicts when merging sequentially.

## Dispatching Agents

```python
delegate_task(tasks=[
    {"goal": "Implement issue #X: ...",
     "context": """Project: /abs/path/to/project-task-a
     Branch: task/x-slug (already checked out)
     Build: dotnet build
     Test: dotnet test --filter "..."
     DO NOT USE Rider MCP tools. Use terminal, read_file, write_file only.
     TDD MANDATORY: ..."""},
    {"goal": "Implement issue #Y: ...",
     "context": """Project: /abs/path/to/project-task-b
     ..."""},
])
```

Key: pass the **absolute worktree path** as `Project`. Each agent's working
directory is isolated — they never see each other's files.

**For full-stack projects**, include BOTH backend and frontend build/test commands
in the agent context. Backend-focused orchestrators default to `dotnet build` and
miss frontend compilation errors:

```python
{"goal": "Implement issue #X: ...",
 "context": """Project: /abs/path/to/project-task-a
 Branch: task/x-slug (already checked out)
 Build: dotnet build
 Test: dotnet test --filter "RequiresInfra!=true"
 Frontend build: cd src/frontend && bun run build
 Frontend test: cd src/frontend && bun run test
 Frontend lint: cd src/frontend && bun run lint
 DO NOT USE Rider MCP tools. Use terminal, read_file, write_file only.
 TDD MANDATORY: ..."""},
```

## User Preferences (baked in from session corrections)

Docs bundled into feature PRs, push branches often, update GitHub issues after each merge, draft PRs
for visibility, strict "don't touch main", and no Rider MCP when other agents use it: `references/user-preferences.md`.

## Integration After Agents Complete

### 1. Verify in each worktree

```bash
cd /abs/path/to/project-task-a
dotnet build && dotnet test --filter "..."
```

### 2. Commit in worktree

```bash
git add -A && git commit -m "feat: description (#issue)"
```

### 3. Push branch

```bash
git push origin task/<slug>
```

### 4. Create draft PR for visibility

```bash
# From main checkout (or any checkout with gh auth)
gh pr create --head task/<slug> --draft \
  --title "feat: description (#issue)" \
  --body "Summary. Closes #issue."
```

### 5. Update GitHub issue

```bash
gh issue comment <N> --body "## Implemented ✅
PR #<M> opened. [details]
Awaiting merge."
```

### 6. Merge (admin bypass for CI limits)

```bash
gh pr ready <PR_N>
gh pr merge <PR_N> --squash --delete-branch --admin
```

`--admin` bypasses branch protection and CI check requirements. Use when:
- CI quota/limits are reached
- Checks are expected to fail for non-code reasons
- User explicitly says "bypass rules and merge"

### 7. Clean up worktree

```bash
git worktree remove /abs/path/to/project-task-a --force
```

## State Tracking Updates

When main checkout must not be touched, update the project state file via a short-lived
worktree + PR:

```bash
git worktree add ../project-state -b task/state-update origin/main
# edit the state file in the worktree
cd ../project-state
git add <state-file> && git commit -m "chore: update state"
git push origin task/state-update
# PR + admin merge + remove worktree
```


> Merge conflicts: read `references/merge-conflicts-rebase.md` when a worktree branch hits merge conflicts (rebase pattern).

## Scaling to N parallel issues

When the user says "continue" or "do all remaining issues":

1. **Plan the batch** — identify which issues are unblocked (no unresolved `blocked-by`).
2. **Create all worktrees at once** from `origin/main`:
   ```bash
   for issue in 179 203 205; do
     git worktree add ../project-$issue -b task/$issue-slug origin/main
   done
   ```
3. **Dispatch all agents in parallel** via `delegate_task(tasks=[...])`.
4. **As each completes:** verify in worktree → commit → push → create draft PR.
5. **Merge sequentially** (not simultaneously):
   - Rebase worktree on latest main (other PRs may have merged).
   - `gh pr ready` + `gh pr merge --squash --delete-branch --admin`.
   - If rebase conflicts on docs: combine both sides (keep HEAD additions + add ours).
6. **Clean up each worktree** immediately after merge.
7. **Batch state updates** — one state-update worktree per batch, not per issue.

### Subagent fix-up pattern

When a subagent's work has test failures after completion:
1. Try a quick fix in the worktree yourself (enum casing, MSW handlers, type errors).
2. If the fix is non-trivial, dispatch a dedicated fix-up subagent for that worktree.
3. Only commit+push+PR after all tests pass in the worktree.


> Autonomous wave cycles: read `references/autonomous-wave-cycle.md` when running a fully autonomous wave-based cycle.


> Shared binary artifacts: read `references/parallel-waves-shared-artifact.md` when parallel waves share a committed binary artifact (corpus db, vector store).

## Reading subagent background-process notifications (orchestrator)

Servers/processes a subagent starts in background (watch patterns like "Application started") forward their lifecycle notifications to the orchestrator session. Interpret them before reacting:

- A SIGTERM/exit on a subagent-owned process is usually the AGENT deliberately killing its own hung run (full-suite hang → kill → isolate with a single-filter `time dotnet test`), not a crash.
- **Tail the subagent's live transcript** (`~/.hermes/cache/delegation/live/<delegation-id>/task-N.log`) before reacting to any subagent-owned process notification — it shows whether the agent is mid-debug or actually dead.
- Repeated server restarts in a transcript are progress, not loops: each restart often means one ingest/regeneration iteration with a fix baked in.
- A re-run of a failed background command produces a DELAYED notification carrying the OLD failure output. If you already re-ran and verified green, the stale notification is noise — ignore it.


> A/B agent experiments: read `references/comparative-agent-experiments.md` when comparing agents or tools in parallel workstreams.


> Shared-worktree collisions: read `references/shared-worktree-collisions.md` when agents must share ONE worktree (WIP build collisions, git discipline, shared-main commits).

## Gotchas
- **`git worktree add <path> origin/<branch>` without `-b` = DETACHED HEAD — commits silently vanish.** When a task worktree was already removed and you re-add one just to fix a pushed PR branch, plain `git worktree add /tmp/x origin/task/foo` checks out the remote branch DETACHED: your commit lands on the detached HEAD, `git push` answers "Everything up-to-date" (the branch ref never moved), and `git worktree remove` discards the work with no error. The local branch often STILL EXISTS after a tracker finish — reuse it directly (`git worktree add /tmp/x task/foo`). Otherwise create one explicitly: `git worktree add /tmp/x -b <local> origin/<remote-branch>`, then push with `git push origin <local>:<remote-branch>`. After any post-hoc worktree commit, verify it landed: `git log --oneline origin/<branch>` must show your commit before you report it.
- **Tooling that creates worktrees from LOCAL main builds on a stale base.** When the user merges PRs via the GitHub UI and never pulls locally, local main lags origin — the fresh worktree silently lacks merged PRs AND user's direct commits. Symptom: your patches apply cleanly but the base is missing expected content. Before ANY work in a new worktree: `git fetch origin main && git log --oneline -3 origin/main` vs `git log --oneline -1` in the worktree — if origin is ahead, rebuild: save ONLY your intended diffs, then `git checkout -- . && git checkout -B <branch> origin/main`, re-apply. **Rebuild clobbering trap:** files whose content depends on the base (csproj metadata, contract tests, server.json) carry the STALE base's values — re-copying a saved copy reverts the base's correct values. Restore each base-dependent file from git first (`git checkout -- <file>`), then re-apply ONLY the version/metadata delta; verify base identity after the rebuild with a marker your diff does NOT touch.
- **Main can move while your branch sits in review** — with no remote PR flow, another agent/user session can merge into LOCAL main while your wave branch sits in review. Before EVERY integration, check `git log <your-base>..main` — if main moved, merge main back into the wave branch, resolve conflicts (keep the other session's newer work where it overlaps; their rewrite of a file your wave also touched is usually additive), re-run the full suite, and only then merge to main. A stale-base merge onto moved main produces conflicts that masquerade as your waves' fault.
- **Ownership seam-checks lie when main moved** — `git diff --name-only main..HEAD | grep -v -E '<owned paths>'` flags out-of-scope files, but if main advanced (a wave merge, a user commit) after the branch was created, MAIN-side changes masquerade as branch edits. Before accusing a subagent of a scope violation: `git merge-base main HEAD` — a base older than main's tip means main-side diffs will appear; then `git log --oneline main..HEAD -- <file>` — empty means the file changed on main's side, not the branch. Quick whole-branch version: `git diff $(git merge-base origin/main HEAD)..HEAD --stat` — a scary 91-file stat is usually a moved main (other sessions' commits appear as deletions/edits on your side), not a scope violation.
- **Full-suite failures at join: attribute before blaming the branch.** A shared machine runs other sessions; the first full-suite run after a package lands can fail many tests (E2E/embedding/watch families are timing/env-sensitive: serial-collection env mutation, model files, concurrent builds). Do NOT treat that as your branch's failure: (1) re-run the full suite once (run 2 often passes — flake), (2) if it still fails, create a throwaway baseline worktree (`git worktree add <path> origin/main`) and run ONLY the failing test classes there — failures on baseline = pre-existing environmental flakes, branch is clean; remove the worktree after. Only failures that reproduce on your branch but NOT on baseline are yours. The task skill's "run the suite as the only session working" is often impossible on a shared machine — the baseline-attribution drill is the practical substitute, and a single clean full-suite run on the final state is the gate.
- **A parallel task owning the same file beats duplicating the work** — when two tasks both need to rewrite one surface (a file-watcher's CLI section vs a CLI-config refactor, both touching the same files), DEFER the section to the owning task instead of implementing it twice: shrink your wave (drop the section), pin the exact shared contract (settings keys, command formats) in BOTH subagent briefs, and re-validate the deferred scenarios at integration. Two branches rewriting the same file collide at merge no matter how clean each side is.
- **The verification tracker re-fires even after a recorded canonical run** — the "one clean recorded run satisfies it" note is wrong as measured: it kept re-firing every turn on the stale changed-path list. Do not loop re-running identical checks on unchanged bytes. Per turn: either stat/mtime-prove byte-identity or run the check once, then state the blocker in one line. Re-run only when a new edit actually occurred.
- **Parallel branches adding the same interface method cause duplicate implementations at merge** — when two branches both add the same method to an interface, the merge keeps both implementations (compile error CS0111). Fix: after sequential merge, grep for duplicate method signatures in the implementation files. Keep the one from the branch that owns the feature; delete the duplicate from the merged branch. This commonly hits repository interfaces, container configs, and DI registrations.
- **Committed conflict markers compile-fail loudly, but only at build** — a merge committed with `<<<<<<<` markers still inside fails CS8300 on the next build. Two causes seen: (1) `git checkout --ours <path1> <path2> <badpath>` is ALL-OR-NOTHING — one bad pathspec makes the whole command fail silently (stderr suppressed with 2>/dev/null), leaving every file unresolved while the rest of the chain proceeds and `git add -A` stages the marker-laden files; (2) the merge repair itself then gets committed. Playbook: after ANY conflict resolution, `grep -rl '<<<<<<<' <resolved dirs>` BEFORE committing; if markers remain, `git show <merge-commit>^1:<file>` (the branch's pre-merge version) over each affected file, re-commit, then rebuild. Check each pathspec of a multi-path checkout individually when one might not exist.
- **Subagent file writes can silently produce empty files** — always verify `wc -l` on files written by subagents. If a subagent says "file written" but `wc -l` shows 0, re-dispatch with explicit write verification instructions.
- **`gh issue comment` uses `-b` for body, `gh issue close` uses `-c` for comment** — not interchangeable. `gh issue comment <N> -b "text"` and `gh issue close <N> -c "text"`.
- **`--delete-branch` fails if worktree exists** — remove worktree first, then
  the remote branch is cleaned up by the merge or manually
- **Draft PRs can't be merged** — always `gh pr ready <N>` before merging
- **`--admin` requires repo-owner privileges** — won't work for contributors
- **`git merge -X theirs`** helps resolve state file conflicts when cherry-picking
  across worktrees
- **Commits land on wrong branch** if agent checks out a different branch inside
  its worktree — verify with `git branch --show-current` in agent context. A task
  worktree hosts ALL of a task's branches at once — the task branch plus every PR
  branch created during the task (`git checkout -b` switches the worktree, it does
  not create a second copy). Commits land on whichever branch is currently checked
  out, so a commit made while the worktree sits on PR A's branch pollutes PR A even
  when the work belongs to PR B. Repair a stray commit: `git branch -f <wrong-branch> <pre-stray-sha>`
  (only if the remote hasn't got it — a pushed stray needs a force-push or revert
  instead), then `git checkout <right-branch> && git cherry-pick <stray-sha>`.
- **Cherry-pick between worktrees** may conflict on shared files like a state file —
  resolve by writing the merged content rather than trusting auto-merge
- **Subagent commits sometimes aren't committed** — verify `git status --short` after agent completes, commit manually if needed. Iteration caps commonly cut an agent off RIGHT BEFORE committing: the summary usually names the intended commit plan — follow its explicit-add list, never `git add -A`, and keep coupled artifacts (db + hash map) in ONE commit.
- **Subagent may leave files untracked** — `git status` shows `??` for new files; the agent read them but forgot to `git add`. Always verify and commit in the worktree before creating the PR.
- **`git reset --hard` may be blocked by user** — if the user rejects `git reset --hard` on a branch, use `git checkout -B branch origin/main` instead to reset the branch pointer without the "destructive" flag.
- **`-X theirs` on the state file** — when merging a state-update worktree into main and the state file conflicts (because another agent touched it), use `git merge -X theirs` to accept the worktree's version. Don't trust auto-merge on JSON with conflict markers.
- **Multiple parallel agents need separate worktrees from the SAME base** — create all worktrees from `origin/main` (not local `main`) so they share a common ancestor. Always `git fetch origin main` before creating worktrees. Agents modifying the same file will conflict at merge time — plan parallel tasks to touch non-overlapping files.
- **Sequential merges cause increasing conflicts** — when merging PR A then PR B, PR B's branch was created from the pre-A state. After PR A merges, PR B conflicts on shared docs (flows.md, data-model.md, architecture.md). Always rebase PR B's worktree on `origin/main` after PR A merges, then force-push.
- **Issue comments after merge** — post a summary comment on each GitHub issue after the PR merges, not just after creating the PR. Include: files created/modified, test counts, and any follow-up items.
- **Pre-flight every fresh worktree BEFORE dispatch** — run bare `dotnet build` in each new worktree before delegating: it catches NU1301 (repo NuGet.config lists a local source like `./.nupkg-local/` that the worktree lacks — `mkdir -p` it), a wrong solution filename, and restore problems while they cost you seconds instead of the agent's tokens. .NET 10 solutions are often `.slnx`, not `.sln`: naming the wrong file fails MSB1009 "Project file does not exist" — use bare `dotnet build`/`dotnet test` and let discovery find the solution.
- **Orchestrator cwd drift** — the terminal session's cwd can move between commands (workdir is per-command). When a relative-path build/test suddenly fails "Project file does not exist", run `pwd` first, then re-run with explicit workdir. A corrupt-looking test result (e.g. "Passed: 3, Failed: 4" that re-runs green 7/7) is usually wrong-cwd or concurrent-restore noise — re-run clean before trusting it.
- **Worktrees can vanish under you** — another session's `git worktree remove` / `git worktree prune` (or its cleanup scripts) can delete a worktree you created minutes earlier — including the directory. Re-check `git worktree list` before relying on a path, and re-create as needed; never assume a worktree survives across turns when multiple sessions share the repo.
- **The stash crosses worktrees safely** — all worktrees share one `.git`, so `git stash push` in the main checkout, then `git stash pop` inside a worktree works. Use it to move uncommitted edits between checkouts without losing them.
- **After a PR merges: branch the next PR from the NEW `origin/main`** (`git checkout -b <next> origin/main` after `git fetch`) — never from the merged branch's old base.

## References

- `references/ab-agent-experiment.md` — A/B agent experiment pattern (tool comparison in parallel worktrees); read when running A/B agent experiments.
