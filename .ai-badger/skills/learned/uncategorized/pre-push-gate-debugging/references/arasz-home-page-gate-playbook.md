# arasz-home-page gate playbook

The arasz-home-page repo's local gate (lefthook) — the Angular + Azure Functions monorepo
behind arasz.me. Learned 2026-08-05 while pushing the AiRaccoon article revision.

## Gate layout (`lefthook.yml`)

- `pre-commit`: lint frontend (runs on staged files only), workflow invariants.
- `pre-push` (parallel jobs, each scoped by `glob` — docs-only pushes skip everything):
  - `frontend` (root `frontend/`): `npm run lint && npm run test:single && npm run test:scripts && npm run build`.
    - `test:single` = Vitest + Karma-style Angular suite, ~542 tests.
    - `test:scripts` = vitest script tests (~73) + `node --test` (~191).
    - `build` = `build-articles` (regenerates `frontend/src/app/data/articles/*.data.ts`,
      `articles.meta.ts`, `index.json`, `articles.provider.ts` from `content/articles/*.md`)
      + `ng build` + `verify:prerendered` (8 published articles) + rss + sitemap.
  - `backend` (root `backend/`): `npm run build && npm test` (~355 tests).
  - `e2e` (root `frontend/`): `npx playwright test --project=chromium` (51 tests).
  - `repo static checks`: shell scripts (no-silenced-workflow-failures, workflow-cost-invariants,
    detect-changes-contract, lighthouse-gate-coverage, automation-pr-contract), actionlint and
    shellcheck only if installed.
- Any commit touching `content/articles/*.md` MUST be followed by `npm run build-articles`
  (in `frontend/`), or the generated data files drift and the pre-commit lint or prerender
  verify catches it. Regenerating produces lint-fixed output; commit the generated files with
  the md.

## Port-4200 e2e collision (the big trap)

`playwright.config.ts`: `webServer: { command: 'npm run start', url: 'http://localhost:4200',
reuseExistingServer: !process.env['CI'] }`. Consequences:

- **Any live dev server on 4200 gets REUSED by the hook's e2e.** If it's the owner's `ng serve`
  (cwd = main checkout) the tests run fine against it. If it's a ZOMBIE from a removed worktree
  (its node_modules symlink is gone), the reused server floods `[WebServer] Cannot find module
  '.../@angular/compiler/fesm2022/compiler.mjs'` + TS2307 for every import and ~half the page
  tests time out (23/51 observed). Looks exactly like your change broke everything — it didn't.
- **Identify the port holder before touching anything:**
  `lsof -nP -iTCP:4200 -sTCP:LISTEN` then `lsof -p <pid> | grep cwd` — the cwd tells you which
  checkout the server serves. Never kill a process whose cwd is the owner's main checkout.
- **Concurrent `npm install` in the main checkout** transiently removes package dirs; a hook-run
  ng serve resolving via the symlinked worktree node_modules (realpath = main checkout) hits the
  gap and dies with the same TS2307 signature. Check the failing module exists
  (`ls node_modules/@angular/compiler/fesm2022/compiler.mjs`), then re-run e2e alone
  (`npx playwright test --project=chromium` in `frontend/`) for fast feedback, then re-push.

## Worktree node_modules symlink gotchas

- A bare `git worktree add` has NO `frontend/node_modules`. Symlink it from the main checkout:
  `ln -s /Users/arasz/RiderProjects/arasz-home-page/frontend/node_modules <worktree>/frontend/node_modules`.
- **git does NOT treat the symlink as an ignored dir**: `git check-ignore` exits 1 for a symlink
  even with `node_modules/` in .gitignore, so the worktree status shows `?? frontend/node_modules`.
  That untracked entry makes `git worktree remove` REFUSE, and `task_tracker.py finish` keeps the
  worktree with `keptBecause: uncommitted changes: frontend/node_modules`. Remove the symlink
  (`rm <worktree>/frontend/node_modules`) before finish/remove; recreate it when re-adding the
  worktree.
- `npm run build-articles` regenerates EVERY article data file; in a worktree cut from an older
  main, files the owner changed on main since (e.g. diagram SVG updates) come back MODIFIED as
  artifacts. They are regenerable — `git checkout --` them before removing the worktree.

## PR merged while you still work

The owner merges PRs from the GitHub UI at any moment. A commit pushed to the PR branch AFTER the
merge never reaches main (the squash captured only the head at merge time). After each push to a
PR branch — especially the final one — check `gh pr view <n> --json state,mergedAt`. If it merged
mid-work: the branch tip is orphaned; cherry-pick the straggler commit onto main
(`git cherry-pick <sha>` — applies cleanly because the squash's tree equals the merged head's
tree), re-run `npm run build-articles` (should produce zero drift), and push straight to main
ONLY with the owner's explicit authorization (the one-PR-per-task exception).

## Content-correction loop (owner f:/e: feedback)

- Owner corrections to an OPEN PR's content go into the PR branch worktree, then gates, then push.
- "on main" in a correction means the MAIN CHECKOUT working copy (the owner prepares/posts from
  there). Apply the edit there too, and make the PR's copy BYTE-IDENTICAL to the owner's working
  copy — the merge then cannot overwrite their local edits (a pre-existing 1-line layout diff
  between PR and working copy was caught by `git diff --no-index`).
- The owner may edit files (e.g. extend your hashtag line) while you work — re-read the file
  before patching; the on-disk state is authoritative.
