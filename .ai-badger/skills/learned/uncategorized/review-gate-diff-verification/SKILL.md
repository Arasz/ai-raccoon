---
name: review-gate-diff-verification
description: "Use when gating a git diff: verify the diff base first."
version: 1.0.0
---

# Review-gate diff verification

Gate reviews (code-reviewer, phase-3, plan-vs-implementation) judge a branch diff. Before judging content, verify the diff itself is real — the plumbing lies more often than the code.

## Pitfall 1 — base drift → phantom changes (the big one)

**Symptom:** `git diff origin/main..HEAD` shows `D`/`M` on files that NO commit in `git log origin/main..HEAD --stat` touches.

**Cause:** the base ref moved past the declared fork point. Main gained commits after the branch was created (e.g. PRs merged post-fork); the worktree's `origin/main` ref now points past the declared base. Files ADDED on main after the fork appear as phantom `D` in the worktree diff; state-file entries appear as phantom `M`. The branch itself made no such change.

**Diagnosis sequence (in order):**
1. `git merge-base origin/main HEAD` — the true fork point.
2. `git rev-parse origin/main` vs `git rev-parse <first-commit>^` — if they differ, the base ref drifted.
3. `git ls-tree origin/main -- <path>` vs `git ls-tree HEAD -- <path>` — which side actually holds the file.
4. Confirm: `git diff <true-base>..HEAD --name-status` — clean means the "deletions" were main-side additions.

**Resolution:** review and gate against the true fork point (merge-base), not the moved ref; report the drift to the orchestrator. GitHub PR diffs use merge-base so they are usually correct — the hazard is local gate diffs computed against the moved ref. Never "fix" a phantom deletion; there is nothing to restore on the branch side.

## Pitfall 2 — `git show --name-status | grep "^path"` matches nothing

`git show <commit> --name-status | grep -E "^src|^docs"` returns empty even when the commit touched those files: name-status lines start with a status letter + TAB (`M\tsrc/...`). Anchor after the tab (`grep -E "\tsrc/"`) or grep the bare filename. A buggy grep that silently matches nothing wastes a full round-trip on a false "the commit didn't touch that file".

## Pitfall 3 — binary build artifacts pollute source greps

`grep -rn Symbol src/` hits `obj/`/`bin/` DLLs. Worse, a NEW symbol can be a substring of the OLD one (`DefaultCandidateLimit` contains `CandidateLimit`), so binary hits prove nothing. Restrict with `--include="*.cs"` (or `--exclude-dir=bin --exclude-dir=obj`) and distinguish "substring of the new symbol" from "stale old symbol" before reporting.

## Pitfall 4 — committed plan doc lags the accepted amended plan

The plan committed in the PR may be the pre-amendment version while the accepted (MoE-amended) plan lives only as uncommitted working-tree changes in the main repo. Diff them: `diff <(git show HEAD:docs/work/<plan>) /path/to/accepted/plan.md`. Judge the implementation against the ACCEPTED version; then check whether the commit messages document the amendments (they often do — that downgrades the stale-committed-plan finding to a NIT).

## Plan-claim verification

The plan's prose about existing code can be wrong or stale. Verify each "already does X" / "switches from Y" claim against the base's actual code: `git show <base>:<file> | sed -n '<range>p'`. Map diff hunks to METHODS by reading the surrounding code — the same-looking re-read exists in several methods with different old/new shapes (e.g. one already re-read by bucket key while the other used `last_insert_rowid`).

## Test-honesty heuristics for gates

- **Concurrency gates (Barrier + Task.WhenAll) are probabilistic RED drivers.** If the race serializes, the pre-check converges and the test passes on the UNFIXED code. They are deterministic green post-fix but can pass vacuously as RED drivers. Assess which tests carry the deterministic load (state/schema tests that seed violating rows and reopen) — that's the honest division of labor.
- **RED drivers must set the pre-condition before invoking** (e.g. cancel the token BEFORE calling, or the pin stays green forever). Check the ordering.
- **Verify "excluded row" expectations against the real scoring logic**, not the test comment — compute the score by hand from the production code (floor, bonuses) to confirm the excluded row actually scores below the threshold.
- **FakeLogger seam:** for `[LoggerMessage]` assertions, the non-generic FakeLogger does not implement `ILogger<T>`; use `FakeLogger<T>` + `Collector.GetSnapshot()` and assert on `Id.Id`, `Level`, and formatted `Message` (rank-order pins: snapshot order = emission order).

## Reporting shape

Numbered findings with MUST-FIX / SHOULD-FIX / NIT severities, file:line evidence, a verdict (APPROVE / APPROVE-WITH-CHANGES / REQUEST-CHANGES), and owner questions for anything the plan left open or the diff base made ambiguous. Lead with a compact "verified against plan" checklist (each plan point → pass/fail with evidence) so the verdict reads as a judgment, not a vibe.
