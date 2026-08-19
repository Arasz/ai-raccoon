---
name: git-worktree-isolation
description: Isolate code-editing subagents in separate git worktrees.
platforms: [linux, macos]
---

# git worktree isolation for subagents

When dispatching a subagent to edit code, isolate it in its own git worktree — never the
same directory as the orchestrating session.

## Why

Two agents in the same directory cause:
- **dotnet build failures:** Shared `obj/` directories cause MSB3492 or "Building target
  completely" errors. Workaround is `rm -rf obj/` but it is destructive and racy.
- **File modification races:** One agent patches stale content.
- **Test corruption:** Process-global state (env vars, temp dirs) interferes.

## How

1. **Orchestrator:** Works in its own worktree from `task_tracker.py start`.
2. **Subagent:** Gets a SEPARATE worktree via `git worktree add .ai-badger/worktrees/<taskId>-<sub> <base>`.
   The subagent's `context` field must use the subagent's worktree path.
3. **After completion:** Pull subagent commits into orchestrator's worktree via `git fetch` +
   `git merge`, then remove with `git worktree remove`.

## One task worktree can host parallel subagents — if file sets are disjoint

The task skill mandates one worktree per task; parallel implementation subagents CAN share it safely when the plan assigns each package a disjoint file set (verified 2026-08-06:
waves of 3 agents on P1/P3+P4/P5 then P2/P7/P6 in one worktree, zero collisions). The plan must name which files each package owns — shared files (docs edits, .gitignore) belong to ONE package or a final text-only sweep package. The
orchestrator reviews at the seams (full suite run together after the wave), then commits per package.

- Subagents that `git rm` a file leave the deletion STAGED; a later `git add <deleted-path>`
  fails with "pathspec did not match any files" — stage only the surviving paths of the package (the deletion rides along in the commit automatically).
- Commit per package as each wave lands, not one mega-commit — keeps the review and any rollback scoped.

## Another lane's STAGED files ride into your commit — read both status columns

In a worktree shared with parallel lanes, `git commit` captures the WHOLE index, not just the files you `git add`ed. If another lane staged its files (or its `git rm` left a staged deletion), your `git add <your-path>` + `git commit` sweeps
their staged files into YOUR commit (verified 2026-08-11: a frontend-only commit carried a backend lane's staged
`betaAccessPublish.ts` + tests — `git show --stat HEAD` revealed 5 files where 2 were intended).

- `git status --short` has TWO columns: `M ` (staged, column 1) vs ` M` (unstaged, column 2). A filter like `git status --short | grep -v "^ M backend"` hides only UNSTAGED backend files — staged foreign files (`M ` in column 1) sail
  straight through. Filter both columns, or list what you are about to commit explicitly:
  `git diff --cached --name-only` right before `git commit`.
- Check `git show --stat HEAD` immediately after committing; the file set must match the package you own.
- Repair (index + working tree fully preserved, the other lane's state untouched):
  `git reset --soft HEAD~1`, `git restore --staged <foreign-files>`, recommit. The foreign lane's staged edits simply return to being its staged changes; nothing is lost.

## Multi-branch task worktrees: check `git branch --show-current` BEFORE committing

A task worktree hosts ALL of a task's branches at once — the task branch plus every PR
branch created during the task (`git checkout -b` switches the worktree, it does not create
a second copy). Commits land on whichever branch is currently checked out, so a commit made
while the worktree sits on PR A's branch pollutes PR A even when the work belongs to PR B.
Verified 2026-08-06: PR B's docs commit landed on `fix/watch-tools-registration` and had to
be un-stuck with `git branch -f <branch> <sha>` + cherry-pick.

- Before every `git commit` in a shared task worktree: `git branch --show-current` and confirm
  it is the intended branch.
- Repair a stray commit: `git branch -f <wrong-branch> <pre-stray-sha>` (only if the remote
  hasn't got it — a pushed stray needs a force-push or revert instead), then
  `git checkout <right-branch> && git cherry-pick <stray-sha>`.
- After a PR merges: branch the next PR from the NEW `origin/main` (`git checkout -b <next>
  origin/main` after `git fetch`) — never from the merged branch's old base.
- The stash crosses worktrees safely: `git stash push` in the main checkout, then `git stash
  pop` inside a worktree works because all worktrees share one .git. Use it to move uncommitted
  edits between checkouts without losing them (verified 2026-08-06).

## Detached HEAD when adding a worktree from a remote ref

`git worktree add <path> origin/<branch>` checks out DETACHED HEAD. Commits land on
no branch and `git push` fails with "fatal: You are not currently on a branch" — and
the failure is easily SWALLOWED in a chain like
`cmd && git push 2>&1 | tail -1 && cmd` (the pipeline's exit code is tail's 0, so the
chain continues and you believe the push happened). Verified twice in one session: two
commits sat unpushed while CI ran on the old SHA.

- Fix: `git switch -C <branch>` inside the worktree before committing, or create it
  properly: `git worktree add -b <branch> <path> <base>`.
- Verify after any push: `git rev-parse origin/<branch>` must equal `git rev-parse HEAD`.
- `git push` from a worktree on its branch still works normally (branch-name push).

## Worktrees can vanish under you

Another session's `git worktree remove` / `git worktree prune` (or its cleanup scripts)
can delete a worktree you created minutes earlier — including the directory. Re-check
`git worktree list` before relying on a path, and re-create as needed; never assume a
worktree survives across turns when multiple sessions share the repo.

## Patch-tool write-backs can land in the MAIN checkout, not the worktree

When editing worktree files with the patch tool, a reported success does NOT guarantee
the bytes changed where you think. Verified 2026-08-06: patch returned successful diffs
("already applied" / full replacement diff) but `grep` on the worktree file showed the
old content — and `git status` in the MAIN checkout showed the edit had landed THERE
instead (partial migration of a test file leaked into the shared main checkout and had
to be reverted with `git checkout -- <file>`).

- After EVERY patch on a worktree file, verify on disk with `grep` in the WORKTREE path
  before continuing — never trust the patch tool's returned diff.
- If the change is missing from the worktree, check the main checkout's `git status
  --short <paths>` for the leak; revert it there (`git checkout -- <file>`), then re-apply
  in the worktree.
- When the patch tool claims "already applied" but the file lacks the text, the tool's
  view is stale — re-read the file (fresh read_file), then re-apply; repeat-until-gone is
  a wrong loop, re-read is the fix.
- For bulk renames (replace_all) and insertions, prefer a script that reads, replaces,
  writes, then greps the result — deterministic, verifiable in one step.
- `dotnet build` on the WORKTREE (not the main checkout) is the compile-truth check after
  any edit batch; a green build in the wrong tree proves nothing about the worktree files.

## A concurrent session can push a RED origin/main — verify before merging it in

Another session's commit can leave origin/main broken at its own head (verified 2026-08-06:
a const rename `TN_*` → `Tn*` in MemoryTools without updating ToolInventoryTests, which
still filtered `StartsWith("TN_")` — every tool-name assert failed). Merging that into your
branch poisons your full-suite gate with a failure that is NOT yours.

- Before `git merge origin/main`, sanity-check the new commits: `git log origin/main --oneline
  -3` and spot-check what changed (`git show <sha> --stat`). If the diff is large and touches
  code+tests asymmetrically (rename without test update, behavior change without test), run
  the affected test class on the pristine origin/main first.
- A RED origin/main blocks every in-flight PR's gate. Fix it as its OWN tiny PR (often a
  2-line test fix) rather than bundling it into your refactor PR — the fix PR lands first,
  then your branch merges the now-green main.
- When the break is already merged into your branch, merge the tiny-fix branch in too
  (`git merge <fix-branch>`) to restore a green gate; the fix PR stands alone on main.

## Reference branch may already be merged — fast-forward instead of cherry-picking

When a task says "mirror sibling branch X" or "cherry-pick X's commits", CHECK FIRST whether
X was merged into origin/main since your branch was cut (verified 2026-08-06: a task claimed
"the sibling's production fixes are NOT on your branch, cherry-pick them" — but origin/main
had advanced to the sibling's merged PR; `git merge --ff-only origin/main` brought in the
complete reference work in one clean step).

- `git log origin/main --oneline -3` + `git diff <sibling-branch> origin/main --stat` settle
  it in seconds. If the diff is empty for the files you care about, the sibling work is in
  main: fast-forward, don't re-implement or cherry-pick piecemeal.
- Cherry-picking an already-merged commit is harmless but noisy; fast-forwarding keeps the
  branch history linear and the diff against main minimal.

## Gitignored local NuGet source is absent in fresh worktrees (NU1301)

A NuGet.config `<add key="local" value="./.nupkg-local/" />` pointing at a gitignored,
build-artifact directory (packed local tool packages) breaks `dotnet build` in any NEW
worktree: error NU1301 "The local source '.../.nupkg-local/' doesn't exist." The directory
only exists in the main checkout (verified 2026-08-06, AiRaccoon).

- Fix: `cp -R <main-checkout>/.nupkg-local/ <worktree>/` before the first restore/build.
- Check `git check-ignore .nupkg-local` to confirm it is gitignored (then copying is safe
  and never pollutes the branch).

## Full-suite runs can dirty tracked docs — restore before committing

Some integration tests regenerate tracked report docs as a side effect (verified 2026-08-06:
`RrfParameterSweepTests` rewrites `docs/work/2026-08-04-wave4-rrf-sweep.md` — pure
line-wrap normalization — on EVERY run, leaving the worktree dirty with a 200-line diff).

- After any full-suite run, `git status --porcelain` and inspect unexpected modifications
  BEFORE committing. If the diff is a mechanical re-wrap of a generated report, restore it
  (`git checkout -- <file>`) — it is not your change and does not belong in your PR.
- `grep -rln "<doc-name>" tests/` finds the test that writes it, so you can report the
  side-effect confidently instead of guessing.

## Prove a full-suite failure is pre-existing at the base commit

When the full suite fails on tests your diff cannot plausibly touch, don't argue — prove it
(verified 2026-08-06: `RetrievalBaselineTests.CorpusIntegrity_*` failed identically at base;
they compare a bundled corpus DB fixture against a hash-map JSON that had drifted).

- `git worktree add /tmp/base-check <base-sha>` (copy `.nupkg-local` if the repo uses one),
  run just the failing tests there, then `git worktree remove --force /tmp/base-check`.
- Identical failure at base = pre-existing fixture drift / env issue; report it with that
  evidence instead of chasing it. (If it PASSES at base, it is yours — bisect your diff.)

## Verify merged content with a throwaway worktree when the shared checkout is blocked

After integration (task worktree removed, PR merged), fresh gate evidence on the MERGED state may be needed while the shared main checkout cannot be updated — another session's staged files block a fast-forward (verified 2026-08-06: main
held another session's staged ai-badger learned-skill adds while origin/main had moved 4+ commits past local main).

- `git worktree add /tmp/verify-<topic> origin/main`, run the gates there (pytest, dotnet test), then `git worktree remove --force /tmp/verify-<topic>`. Detached HEAD is fine — read-only verification, no commits.
- This is the honest answer when "the suite passed on the branch" needs re-proving on the exact merged content (e.g. the owner squash-merged while you were mid-task).

## A no-op tool shim on PATH can fake a green gate

A leftover shim can shadow a real tool and exit 0 without doing anything (verified 2026-08-06: another session's test harness left `/tmp/pts_parity/dotnet` on PATH — `dotnet
build` printed only `cwd=… env_regenerate=UNSET args=build` and exited 0; no build happened, so a silent gate would have looked green).

- When a gate command exits 0 but the output lacks the expected success marker (`Build
  succeeded`, test counts), run `which <tool>` before trusting the result.
- Run the real SDK by absolute path (`/usr/local/share/dotnet/dotnet build`) or with a cleaned PATH, and grep the output for the success marker — never rely on the exit code alone.

## dotnet build serialisation

dotnet projects share `obj/` across worktrees. Don't build in two worktrees simultaneously.
If a build fails with MSB3492: `find . -name obj -type d -maxdepth 3 -exec rm -rf {} +`
then rebuild. Only do this when no other build is running.

## LoggerMessage partial class pitfall

C# classes with a nested `partial class Log` containing `[LoggerMessage]` methods MUST
declare the parent class as `partial`. Otherwise: CS0260 "Missing partial modifier; another
partial declaration of this type exists".
