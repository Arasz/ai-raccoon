---
name: red-proof
description: Use when a committed fix's test needs a witnessed RED run.
version: 1.0.0
author: hermes-curator
license: MIT
metadata:
  hermes:
    tags: [tdd, code-review, verification]
    related_skills: [test-driven-development, code-review-checklist, artifact-verification]
---

# RED-proof against committed fixes

The "a check you have not seen fail is not a check" gate normally runs RED-before-fix (commit the failing test first, watch it fail, then fix). When the fix is ALREADY in the tree — reviewing a fix PR, adding a missing test after the fact,
validating a subagent's output — you must still witness the fail once, or the test is indistinguishable from a tautology.

## When to Use

- Reviewing a bug-fix PR where the fix is already committed and the test coverage must be proven honest
- Backfilling a missing test after the fix landed, when no RED commit exists to witness
- Validating a subagent's or peer agent's test before trusting it
- Satisfying the ai-badger prove-the-check-fails gate on review work

## Workflow

1. **Locate the pre-fix version of the production file.** The RED commit in history is the cleanest source (`git log --oneline -- <file>` — the commit before the fix commit usually has the old implementation). If the fix was squashed,
   reconstruct the old code by reverse-applying the fix's diff mentally or with `git show <fix-commit> -- <file>`.

2. **Temporarily restore the pre-fix file in a WORKTREE, not the shared checkout:**

   ```sh
   git checkout <pre-fix-commit> -- src/Path/To/File.cs   # e.g. the RED commit's version
   dotnet test tests/X.Tests --filter "FullyQualifiedName~TheNewTest"   # expect FAIL
   git checkout HEAD -- src/Path/To/File.cs               # restore — verify with git status
   ```

3. **Read the failure line.** The test must fail on the INTENDED assertion (the behavior the fix changed), not on an unrelated setup error — a test that fails for the wrong reason proves nothing about coverage.

4. **Re-run with the fixed code.** Same filter, expect PASS. Then, if you touched a shared fixture/constructor, run the WHOLE test class (not just the new test) — a fixture field added for one test is exercised by every test in the class.

5. **Record the evidence** in the PR/review: pre-fix commit, expected-fail assertion, post-fix pass count.

## Pitfalls

- **Always restore the pre-fix file** (`git checkout HEAD -- <file>`) and confirm with
  `git status --short` — leaving a staged old version on disk poisons the next run and the commit.
- **Worktree discipline:** in a shared checkout, another session's branch reset can clobber your `git checkout`; use the branch's own worktree (often already present at
  `.ai-badger/worktrees/<branch>` or `.git/worktrees/`).
- **Pre-existing red on the base branch:** before attributing a failure, check whether the test was ALREADY failing on the base commit (e.g. a version-contract test pinned to an old constant vs the csproj). A PR that bumps both makes
  green — the fix is legitimate, and the base being red is worth noting in the review.
- **Beware tests that pass on BOTH old and new code** — a test that never exercises the changed predicate is a silent no-op; the RED run is the only honest proof.
- **The RED run needs the new test compiled against the OLD production code.** If the test references symbols added by the fix, the old-code build fails to compile — in that case the assertion must be written against the stable API surface
  (as it should be for a behavior test).

## References

- `references/ai-raccoon-pr254-example.md` — worked example: promotion-candidate survival test, RED failure line, class-scope re-run, commit history as evidence.
