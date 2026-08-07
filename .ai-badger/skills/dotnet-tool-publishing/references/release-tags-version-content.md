# Release tags must point at content that carries the version (2026-08-05, the project v1.0.5)

## The failure

The repo convention was `vX.Y.Z` tags on the version-bump merge commits (v1.0.0..v1.0.3 all sat on their bump commits; 1.0.4 shipped with NO tag at all). A PARALLEL agent session tagged `v1.0.5` at its own embedding-fix merge commit (#24) —
whose csproj still carried `PackageVersion 1.0.4`. The tag claimed a version the commit does not contain:
a consumer or next session trusting `v1.0.5` would pull 1.0.4 content, and the next real 1.0.5 bump would collide with the tag's existence.

A tag is a claim about CONTENT, not about intent or timeline. The "check the source"
invariant applies to tags too.

## Verification before trusting or moving a tag

    git show <tag>:src/X/X.csproj | grep PackageVersion   # must equal the tag's version
    git tag -l --format='%(refname:short) -> %(objectname:short) %(subject)'   # full inventory

Spot-check every tag after a release wrap-up, and check for MISSING tags (a shipped version with no tag, like 1.0.4).

## Fix pattern when the tag is wrong

1. Bump the version first via the normal TDD path (version-contract test pin RED→GREEN, PR, merge) — never move the tag to an arbitrary commit; move it only to the merge commit whose content IS the new version.
2. After that merge lands: move the tag explicitly — delete the remote ref, then push:

       git fetch origin
       git tag -f v1.0.5 origin/main            # local move (fine: your own branch tip)
       git push origin :refs/tags/v1.0.5        # delete remote tag (avoids silent force)
       git push origin v1.0.5                   # push the corrected one

   Never `git push --force --tags` — that silently rewrites every tag including ones a collaborator may have moved intentionally.

3. Re-verify: `git show v1.0.5:src/X/X.csproj | grep PackageVersion` == 1.0.5, and the tag sits on origin/main's history.

## Preconditions that make this cheap

- The release workflow already has a version-contract test (see references/stable-release-version-bump.md) — the bump is a 3-file, TDD-gated change.
- The publish workflow reads PackageVersion from the csproj, so the bump commit IS the release trigger; the tag's job is purely navigational — keep it honest.
