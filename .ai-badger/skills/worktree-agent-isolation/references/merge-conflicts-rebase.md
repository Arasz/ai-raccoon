## Handling merge conflicts (rebase pattern)

When multiple PRs merge sequentially, later ones conflict with earlier merges.
Rebase the worktree branch on latest main before retrying the merge:

```bash
# In the worktree
git fetch origin main
git rebase origin/main

# For docs conflicts (flows.md, data-model.md, architecture.md):
# Keep BOTH sides — HEAD has upstream additions, ours has new feature content
python3 -c "
import re
for f in ['docs/flows.md', 'docs/data-model.md', 'docs/architecture.md']:
    with open(f) as fh: content = fh.read()
    content = re.sub(r'<<<<<<< HEAD\n(.*?)=======\n(.*?)>>>>>>> .*?\n',
        lambda m: m.group(1) + m.group(2), content, flags=re.DOTALL)
    with open(f, 'w') as fh: fh.write(content)
"

git add -A
GIT_EDITOR=true git rebase --continue
git push origin task/<id>-<slug> --force-with-lease

# Retry merge
gh pr merge <PR_N> --squash --delete-branch --admin
```

`GIT_EDITOR=true` prevents rebase from opening an editor interactively.

**Add/add conflicts on emitted spec/docs files (the owner committed the same files to main mid-task).** When the task branch carries files that the user/another session ALSO committed to main directly, `git rebase origin/main` stops with `CONFLICT (add/add)` on every shared file. The branch copy is usually the NEWER one (it carries post-emit rulings) — resolve by keeping the branch version at every step:

```bash
git checkout --ours <file>...   # for the commit that FIRST added the files (ours = branch state during rebase)
git add <files> && GIT_EDITOR=true git rebase --continue
# if a LATER branch commit updates those files and now conflicts:
git checkout --theirs <file>... # theirs = the update commit being applied (branch-derived, correct content)
git add <files> && GIT_EDITOR=true git rebase --continue
```

After the rebase, re-run the files' own gates (spec validator / JSON validity / scenario counts) — a mangled merge shows up as content loss, not a git error.
