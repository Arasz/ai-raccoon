# Worktree re-creation, push verification, and follow-up-commit pitfalls

Hit 2026-08-06 on task `encryption-commands-rework` (follow-up round after `tracker
finish` had removed the worktree).

## Re-creating a finished task's worktree lands on DETACHED HEAD

`git worktree add .ai-badger/worktrees/<id> origin/task/<branch>` checks out the remote
ref **detached** — it does not create a local branch. Every follow-up commit then
accumulates on detached HEAD, and `git push` fails with:

```
fatal: You are not currently on a branch.
To push the history leading to the current (detached HEAD) state now, use
    git push origin HEAD:<name-of-remote-branch>
```

**The failure is easy to miss:** in a `cmd && git commit ... && git push 2>&1 | tail -1
&& git log ...` chain, the pipeline's exit code is `tail`'s (0), so the chain continues
and the fatal error is swallowed by the pipe. Result: origin silently stays at the old
SHA — no CI run for your "pushed" commits, `gh pr view` shows the stale head, and you
only notice when `git rev-parse origin/<branch>` disagrees with HEAD.

Correct recipe after re-creating a worktree from a remote branch:

```bash
git worktree add .ai-badger/worktrees/<id> origin/task/<branch>
cd .ai-badger/worktrees/<id>
git switch -C task/<branch>      # force-create the local branch AT the detached HEAD
git switch -B task/<branch>      # (same thing; -B is the --force-create spelling)
```

and after EVERY push:

```bash
git rev-parse HEAD
git rev-parse origin/<branch>    # must equal HEAD
```

Never trust a piped push's silence; never end a follow-up round without the SHA check.
(Also re-copy gitignored assets into the re-created worktree — see the assets pitfall in
SKILL.md — or model-dependent tests fail until the copy lands.)

## `patch` replace_all matches indentation exactly

`replace_all=true` replaces occurrences of the EXACT old_string including leading
whitespace. Two occurrences of the same logical edit with different indentation (e.g. 16
vs 12 spaces in the same file) → the second is silently missed. Hit 2026-08-06: a
`new EncryptionData("env")` survived a replace_all because its line was indented one
level deeper than the replaced occurrence. After any replace_all, grep the file for the
literal to verify zero remain — the build won't catch a surviving string literal.

## Model-dependent tests: Assert.Skip is the durable CI fix

A test that walks up to a gitignored asset (e.g. `src/<App>/Models/*.onnx`) passes
locally (asset present) and fails on fresh CI checkouts (asset absent) — hit on
`build-fast` for `BundledModelLoggingTests.EnsureAsync_WhenAssetsVerified_LogsDebug`.
The durable fix is xunit v3 `Assert.Skip("reason")` when the asset is absent: the pin
still runs locally where the asset exists, CI goes green without provisioning, and the
skip count documents the environmental dependency. The worktree asset copy is for the
local full-suite join gate, never a CI substitute.
