# Git config repair — lost remote-tracking config

Read this when `origin/<branch>` seems frozen or `git pull` complains it has no upstream. Cause:
`.git/config` lost `remote.origin.fetch` or one or more `[branch "<name>"]` sections — usually a
hand-edit, a shell redirect, or `sed -i` that rewrote the whole file and dropped what it didn't
reproduce.

## Symptom

`git fetch origin` reports success and exits 0:

    $ git fetch origin
     * branch            HEAD       -> FETCH_HEAD

but `refs/remotes/origin/*` does not move. Every later comparison against `origin/<branch>` —
diff, log, merge-base, a rebase target — answers about the commit the config broke on, not the
current remote. Separately, on a branch missing `branch.<name>.merge`, `git pull` fails outright
with "There is no tracking information for the current branch."

## Confirm

    git config --get remote.origin.fetch   # empty output / exit 1 means the refspec is gone
    git for-each-ref refs/remotes/         # SHAs here are frozen if they don't match the remote

Compare the `for-each-ref` SHAs against what the remote actually holds (`git ls-remote origin`);
a mismatch confirms the refspec is missing, not just unset for one branch.

## Repair

    git config remote.origin.fetch '+refs/heads/*:refs/remotes/origin/*'   # restores the refspec fetch needs
    git fetch origin                                                       # refs/remotes/origin/* starts moving again
    git branch --set-upstream-to=origin/<branch> <branch>                  # restores branch.<name>.remote and .merge

Run them in this order: fetch has nothing to update until the refspec is back, and
`--set-upstream-to` needs the remote ref it points at to already exist.

## Restoring several branches at once

    for b in $(git for-each-ref --format='%(refname:short)' refs/heads/); do
      git rev-parse --verify -q "refs/remotes/origin/$b" >/dev/null && \
        git branch --set-upstream-to="origin/$b" "$b"
    done

Skips any local branch with no matching remote branch instead of failing on it.

## Do not hand-edit the fix in

Opening `.git/config` in a text editor to restore the missing lines is what caused this in the
first place — a mistyped or partial save drops everything the edit didn't touch. `git config
--edit` is the same trap: it opens the identical raw file in your editor, not a safe interface to
it, and the git-internals guard refuses it alongside the rest. Use `git config`, `git remote`, and `git branch --set-upstream-to` above — each writes
through git's own lock file and touches only the key it's given.
