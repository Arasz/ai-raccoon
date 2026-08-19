# Parallel-lane commit races in a shared worktree (2026-08-11, github-request-access)

WP-A (backend) and WP-B (frontend) lanes committed into the SAME worktree branch concurrently. The race, the detection, and the recovery — three distinct failure shapes observed in one session:

## Shape 1 — your staged files ride into the OTHER lane's commit

`git add <your-files>` + `git commit` is not atomic across lanes. Between your add and your commit, the other lane's `git add .` / commit captured YOUR staged entries into ITS commit (observed: a "feat (frontend)" commit carried the backend
lane's
`betaAccessPublish.ts`, its test, and `contracts/index.ts` — `git show --stat <sha>`
showed 5 files where the message promised 2). Your own `git commit` then fails with **"no changes added to commit"** — the index was consumed.

## Shape 2 — the other lane AMENDS, and HEAD moves under you

The lane that swept your files may notice and `git commit --amend` (or rebase) to drop them. Symptoms:

- `git log --oneline` shows a different HEAD hash than the one you saw minutes ago.
- `git show <old-hash> -- <file>` shows your change in the old commit, while
  `git show HEAD:<file>` shows the file WITHOUT it (the amend dropped it back out).
- `git diff` (worktree vs index) shows your edits as unstaged ` M` even though you staged them — the index was rewritten by the other lane's operations.

## Recovery / safe commit sequence

1. **Verify your bytes survived**: `grep` the WORKTREE files for your change markers before touching git at all. The worktree is the source of truth.
2. **Re-read `git log --oneline` fresh** — never trust a HEAD-relative check against a hash captured earlier in the turn.
3. **Commit ONLY your paths**, leaving foreign staged entries untouched:
   `git commit -m "<msg>" -- <your-file-1> <your-file-2>`
   The pathspec form builds the commit from the named paths only — it does NOT sweep the rest of the index. (This is the prevention AND the recovery.)
4. Do NOT re-run `git add .` — that re-sweeps foreign files into your index state. Stage your exact paths, then commit with the pathspec form.
5. After committing: `git status --short` should show only the other lane's files, and
   `git show --stat HEAD` must list exactly your package's files.

## Detection checklist (cheap, in order)

- `git status --short` — TWO columns: `M ` (staged) vs ` M` (unstaged). A filter that only excludes `^ M backend` misses staged foreign files.
- `git diff --cached --name-only` right before `git commit` — list what you are about to commit explicitly.
- `git show --stat HEAD` immediately after committing — the file set must match your package.
- When a commit fails with "no changes added": check `git log --oneline -3` — another lane's commit probably just landed and took your staged entries.
