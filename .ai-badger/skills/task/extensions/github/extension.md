# task extension: github

This is a **config-gated extension** of the base `task` skill (`skills/task/`), not a
standalone skill. `scaffold.py` embeds this fragment into the scaffolded `task` skill only when
the project's `.ai-badger/config.json` satisfies the activation conditions below — otherwise the
base skill's generic issue/PR handling (or none at all) is what a project gets.

**Activates when:** `sourceControl.platform == "github"` AND `sourceControl.repoUrl` is set.
**Project-board sections below activate additionally when:** `sourceControl.projectUrl` is set.

Placeholders `{{owner}}`, `{{repo}}`, `{{repoUrl}}`, `{{projectUrl}}`, `{{projectNumber}}` are
resolved by `scaffold.py` from `config.json`'s `sourceControl` block at embed time — this file
never hardcodes an owner or repo name.

## Issue resolution (Phase 1 — Start)

Resolve the task's scope from GitHub instead of freeform text alone:

- An issue URL → `gh issue view <url>`. Its body holds scope, doc references, and acceptance
  criteria.
- Freeform task text → use it directly as scope/title, but still cross-check the project board
  for a matching issue by id/title and reference it if one exists (requires `projectUrl`):
  ```bash
  gh project item-list {{projectNumber}} --owner {{owner}}
  ```
- A bare issue URL with no separate task id also works: derive the task id from the issue title.

## PR flow (Phase 2 — Execute)

Open the PR **early**, as a **draft**, the first time there's a commit to show — don't wait for
the task to finish. This lets a human watch the diff grow in the PR UI while work is still in
progress:

```bash
gh pr create --draft --title "<task title>" --body "<summary, links the issue>"
```

Commit and push as each work package lands (small, focused commits — not one mega-diff at the
end; these get squashed on merge). Push to the remote branch immediately after each commit.

## Copilot review-round loop (Phase 4 — Finish protocol)

Required, spans multiple turns. Runs after marking the draft PR ready for review:

```bash
gh pr ready <n>
```

1. **Poll** for a new review by `copilot-pull-request-reviewer` (match the login
   case-insensitively — `copilot-pull-request-reviewer[bot]`) on the latest pushed commit:
   ```bash
   gh pr view <n> --json reviews
   ```
   Typical arrival is ~10-15 minutes, occasionally longer. Use a background wakeup/monitor/
   schedule mechanism, never busy-wait — it's fine for this to run across turns. If no review
   arrives well past the typical window, check for review-quota exhaustion and fall back to
   whatever alternate reviewer mechanism the project has configured (e.g. an `@claude review`
   mention), if any.
2. **Fetch inline comments** for the round:
   ```bash
   gh api repos/{{owner}}/{{repo}}/pulls/<n>/comments
   ```
3. **Triage before implementing.** When a review batch arrives, first plan fixes for *all*
   findings together (dispatch this to whichever agent the base `task` skill uses for planning/
   review — findings can interact, so avoid patching them one at a time).
4. **Verify against the branch head.** Confirm every finding still applies to the current commit
   before acting — review-tool analysis snapshots can lag the commit they're tagged against.
5. **Implement** the combined plan, then re-run the project's validation command(s) from
   `config.json`'s `commands.build` / `commands.test`.
6. **Push**, then for every finding addressed, deferred to a filed issue, or determined stale:
   reply on its review thread AND resolve it via the GraphQL mutation `resolveReviewThread`.
   Never leave a resolved-in-code thread open.
7. **Repeat** from step 1 until a poll finds no new review round since the last pushed commit.

## Merge (Phase 4 — Finish protocol)

Squash-merge once a review round returns with zero new findings since the last pushed commit —
this is the default, no per-task confirmation needed. The user can always intervene by commenting
on the PR before this point:

```bash
gh pr merge <n> --squash
```

## Notes for the base skill

- Never push directly to the platform's default branch; all work lands through the PR above.
- If `sourceControl.projectUrl` is not set, skip the project-board cross-check in Phase 1 — issue
  resolution still works from a bare issue URL or freeform text.
- Repo-relative `gh` commands (`gh issue view`, `gh pr create`, …) infer `{{owner}}/{{repo}}` from
  the working directory's git remote in most setups; the explicit `gh api
  repos/{{owner}}/{{repo}}/...` calls above need the values spelled out because the REST API path
  requires them.
