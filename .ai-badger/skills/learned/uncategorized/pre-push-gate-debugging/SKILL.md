---
name: pre-push-gate-debugging
description: >-
  Use when a pre-push gate blocks a push or a lane fails.
---

# Pre-push verification gate debugging

Repos with heavy local gates block `git push` until every lane passes (lefthook pre-push hook
calling a verify script that runs docs, unit, E2E, infra and frontend lanes). This skill is for
operating and debugging that gate without burning whole-gate cycles. Repo specifics for
job-search-ai-assistant live in `references/jsaa-gate-playbook.md`.

## Detect and understand the gate

- `lefthook.yml` → `pre-push` → `script` + `runner` (+ `root`). Read the script's header
  comments FIRST: usage, lane names, exit codes, env vars (`VERIFY_MODE`, `VERIFY_SKIP`),
  self-test, and the "reproduce / bypass / logs" failure contract.
- A blocked push ends with `🥊 quality gate (Ns)` and `exit status 1`. The hook buffers lane
  output — never diagnose from the push output alone.

## Workflow

1. **Read the gate's own logs first.** The failure block prints:
   - `reproduce: bun <script> <lane>` — run exactly this to re-run one lane (minutes, not the whole gate).
   - `logs: <dir>/<lane>.log` — the lane's full output; `<dir>/progress.log` is the run timeline
     (mode, which lanes ran, PASS/FAIL per lane).
   - Log dir is per-worktree (hash of the worktree path) — find it by mtime (`ls -t`), not by
     recomputing the hash.
2. **Run one lane** for fast iteration: `bun <script> <lane>` (e.g. `api-e2e`). Only re-run the
   full gate (`<script> pre-push`) when the lane is green.
3. **The gate tests the WORKING TREE, not the pushed ref.** If you push from a different
   checkout than the one you want verified, the lanes verify the wrong bytes. Gate-test the
   tree you intend to push.
4. **E2E/infra lanes**:
   - They start their own AppHost (Docker: emulator containers) OR **reuse a live dev stack**
     that is already ready (probed first). Reuse is convenient but carries state forward.
   - **Cross-run state contamination**: emulator data persists across suite runs on a reused
     stack. Durable orchestration instance IDs are PERMANENTLY TAKEN after a terminal state,
     so a later run scheduling the same deterministic ID 409s or silently dedupes. Symptom: a
     test passes alone, then a DIFFERENT test fails on a later full run with no code change.
     Fix: fresh ephemeral volumes (kill the reused stack, let the lane start its own).
5. **Worktree gotchas**: a bare `git worktree add` has NO node_modules → tooling lanes fail
   (`tsc` TS2688 "Cannot find type definition file for 'bun'", bun test resolution errors).
   Fix: symlink the main checkout's node_modules into the worktree. Never commit node_modules.
6. **Manual repro harness** (when the lane's test output hides the real error — e.g. an
   orchestration ends `failed` with an EMPTY error field):
   - Start the infra exactly as the lane does (see the reference / lane source for the AppHost
     command and env).
   - Build the host under test and run it with the E2E settings (the lane copies a
     `local.settings.e2e.json`-style file into the build output before the tests run).
   - Drive the failing flow with curl, sending the app's auth header (see reference).
   - Read the HOST's own logs: orchestration/activity failures appear there with the full
     exception ("Task 'X' failed with an unhandled exception: ..."), never in the HTTP status
     payload. `failed` + empty `error` in the API body = the detail is in the host logs.
7. **Auto-repair**: some gates try to regenerate drifted artefacts (baseline.json, index.json)
   before failing. If repair "declined", the cause is NOT drift — read the lane log.

## Pitfalls

- **A push can LOOK hung while the pre-push hook is simply running.** `git push 2>&1 | tail` buffers everything, so the lefthook banner (`🥊 lefthook ... hook: pre-push`) never shows and the full gate (which can take 5+ minutes) looks like a network stall — zero output, no timeout. Diagnose by re-running the push UNPIPED or in background and reading the first lines: a banner means the gate is working, not hanging. If the user has said "skip gates" / "just push", `git push --no-verify` is the sanctioned bypass and returns instantly — do not keep waiting on a gate the user already waived.
- `full` vs `limited` mode changes which lanes run; limited skips infra/e2e and prints that
  coverage is still owed. Unset `VERIFY_MODE` for the real gate.
- `VERIFY_SKIP=<lane>` and `--no-verify` bypass the gate entirely — prefer fixing the lane.
- A 401 with "Missing or invalid x-functions-key" can really mean a missing auth header on your
  curl, not a missing key — check the app's principal/auth scheme before assuming.
- Don't re-run the full gate to test one fix — single-lane runs are minutes faster.
- Force-pushing a rebased PR branch re-triggers remote CI; the local gate still runs against
  your working tree, so keep the local tree on the branch you are pushing.
- **A green full-tree lint does NOT prove the changed-file lint lane passes.** If the gate's
  lint lane lints only changed files (git-diff scope) while your manual run covered every
  tracked file, the lane can flag findings the full run reports clean — e.g. a W0108
  `unnecessary-lambda` on a freshly added `resolve=lambda: f()` that a whole-repo
  `pylint $(git ls-files ...)` rated 10.00/10. Reproduce with the actual lane
  (`<script> <lane>`, e.g. `verify.sh pylint`) before pushing, not with a full-tree equivalent.
- **The interpreter you run the gate with determines what the gate's subprocesses can import.**
  Gates that spawn `sys.executable` (scaffold/regeneration lanes) inherit the python that
  launched the gate. On macOS the bare `python3` is often the CommandLineTools build (no
  site-packages, e.g. `ModuleNotFoundError: No module named 'jsonschema'`) — always run lanes
  with the repo's venv python, or rely on the gate's own `_resolve_python` fallback.
- **A worktree's own broken `.venv` shadows the main checkout's good one.** If the gate's
  python resolution prefers `$PWD/.venv` and the worktree has a bare/broken venv (e.g. no pip,
  no deps), every lane fails with import errors even though the main checkout's venv is
  populated. Fix: delete the worktree `.venv` so resolution falls back to the main checkout's
  venv. A stray venv you did not create is the first suspect when a fresh worktree's lanes
  fail on missing modules.
- **`git rebase --continue` hangs: the pre-commit hook wedges in rebase context.** Lefthook's
  pre-commit can hang indefinitely while a rebase is being continued (observed with the
  code-review-graph hook; the git process times out waiting on it and the rebase state survives
  the kill). Replay the rest of the rebase with hooks off:
  `GIT_EDITOR=true git -c core.hooksPath=/dev/null rebase --continue`. This is safe because the
  pre-push gate re-runs every relevant lane on the pushed tree — enforcement happens at push,
  not at rebase. A normal commit on a clean branch is unaffected; only the rebase context hangs.
- **The gate runs SCOPED lanes, not the full list.** `progress.log` shows e.g. `PUSH lanes: docs terraform`
  when a branch only touches docs+infra — the gate picks lanes by changed paths. Don't read a
  scoped run as proof the E2E/dotnet lanes passed, and don't be surprised a scoped run finished in
  seconds. `self-test` tooling tests ride inside the `docs` lane.
- **Playwright-style e2e lanes reuse a live dev server on a fixed port** (`reuseExistingServer:
  !CI`). A wholesale failure pattern — every page test timing out while the [WebServer] log floods
  `Cannot find module` / TS2307 for imports — is a SERVER-level failure, not your change. The
  reused server may be a zombie from a removed worktree (its node_modules symlink is gone), or the
  gate's own server hit a transient gap while a concurrent `npm install` in the main checkout
  churned package dirs. Diagnose in this order: (1) who owns the port — `lsof -nP -iTCP:<port>
  -sTCP:LISTEN`, then `lsof -p <pid> | grep cwd` (never kill a process whose cwd is the owner's
  checkout); (2) does the failing module exist on disk; (3) re-run the e2e lane alone for fast
  feedback; (4) re-push. Repo specifics (arasz-home-page: port 4200, lane commands, worktree
  symlink quirks): `references/arasz-home-page-gate-playbook.md`.
- **After an auto-repair commit, push AGAIN.** The gate's repair path regenerates drifted
  artifacts (e.g. `docs/meta/baseline.json`, `trust-index.json`), commits
  `chore(gate): regenerate stale artifacts`, and exits non-zero with "push again" — the first
  push deliberately fails. Re-push the same ref (plain push; after a rebase, `--force-with-lease`).
- **A lane can be red because the gate tests a working tree polluted by ANOTHER task's
  uncommitted files — the repair path refuses to touch them, so the lane stays red until that
  task commits.** Repair declines with "not regenerating: <path> has uncommitted changes, and
  the repair may only commit bytes it wrote itself". This is not your diff. Prove it before
  bypassing: (1) your branch diff contains no file in the failing lane's scope
  (`git show --stat HEAD` / `git diff origin/main...HEAD`); (2) the lane's own output shows
  your files clean (e.g. the jsaa docs lane prints "Agent instruction validation passed" /
  "Agent instruction drift check passed" for scaffold files while failing on
  `readmeIncomplete` / "N document(s) not yet recorded" / ledger `version-mismatch` —
  all docs-ledger state from parallel tasks). Then the gate's failure block itself names the
  sanctioned escape: `VERIFY_SKIP=<lane> git push` — stage-scoped, NOT `--no-verify`
  (skips every lane) and NOT fixing the other task's files. The skipped lane still runs on
  GitHub CI only if CI has it; jsaa's docs lane is local-only, so the PR is unaffected.
  (Observed 2026-08-06: framework-refresh commit with zero docs/ changes, docs lane red on
  a parallel session's untracked review doc + pre-existing ledger projection drift.)
- **`git push --delete <branch>` also runs the pre-push gate** and can fail on it; bypass with
  `git -c core.hooksPath=/dev/null push origin --delete <branch>` — deleting a remote ref needs
  no verification.

## References

- `references/jsaa-gate-playbook.md` — job-search-ai-assistant specifics: lane list + commands,
  log layout, AppHost command, E2E auth header + config, manual repro steps, blob-container
  provisioning gap, tests/Directory.Packages.props, Durable instance-ID lifecycle patterns,
  docs record CLI (incl. new-doc registration), task-tracker zombie verification, state.json
  drift.
- `references/ai-badger-gate-playbook.md` — ai-badger framework specifics: lane list + the
  interpreter trap (macOS CLT python3 lacks jsonschema — always use the repo venv), worktree
  `.venv` shadowing, the `AI_BADGER_MCP_AVAILABILITY=all` determinism override and its
  coverage gap (filesystem probes), the freshness-guard remediation trap (its printed
  command omits the override — restore `.github/mcp.json` rather than committing the
  regenerated file), the one-commit shape the whole-tree pre-commit chain forces, the
  session-source registry pattern, and the re-scaffold-after-catalog-change rule for the
  freshness guard.
