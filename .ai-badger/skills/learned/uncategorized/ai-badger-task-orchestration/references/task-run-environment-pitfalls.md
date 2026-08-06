# Task-run environment pitfalls (2026-08-06, ai-raccoon scripts-refactor)

## A parallel session's test fixture can SHADOW a real binary on PATH

Hit: `which dotnet` → `/tmp/pts_parity/dotnet` — a shim (left by another session's
test run) that printed `cwd=… env_regenerate=UNSET args=…` and exited 0 WITHOUT
building. Symptoms: the tool's output is suspiciously empty, or looks like a wrapper
echo, and grep for "Build succeeded|error" finds nothing.

- Diagnose: `which <tool>` / `type <tool>` — the path tells the story.
- Fix: use the real binary's absolute path for the rest of the session
  (`/usr/local/share/dotnet/dotnet`; macOS SDKs also live at `~/.dotnet/dotnet`,
  `/opt/homebrew/bin/dotnet`). Re-run the gate with the absolute path.
- NEVER delete the shim: it belongs to another session's live test fixture.
- Check for shims early when a gate produces empty output — do not debug the build.

## Staging after subagents: deletions are already staged

Subagents that `git rm` files leave the deletion STAGED. A follow-up
`git add <deleted-path>` fails with "fatal: pathspec '<path>' did not match any
files" — the file no longer exists in the working tree. Stage only the surviving
(new/modified) paths; the staged deletion rides the commit.

## Worktree base verification when main moved

`git worktree list` showing a different main HEAD than your last commit does not
mean your commit is lost — concurrent sessions commit to main between your calls.
Verify with `git log --oneline -3` in the worktree: your commit should be in the
lineage (the worktree branches from main's CURRENT head). The worktree base
including a parallel session's commits is normal and harmless for the PR.
