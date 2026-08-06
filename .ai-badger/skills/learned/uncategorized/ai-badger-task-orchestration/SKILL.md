---
name: ai-badger-task-orchestration
description: >-
  Use when running an ai-badger task end-to-end from Hermes.
platforms: [linux, macos]
metadata:
  hermes:
    tags: [ai-badger, orchestration, delegation, tdd, spec, gherkin]
    related_skills: [evidence-first-research, hermes-session-telemetry]
---

# ai-badger task orchestration (Hermes side)

Run one ai-badger task from spec to merged code using Hermes `delegate_task` subagents. The
framework's own skills (`task`, `create-task-spec`, `owner-gate-review`) define the phases;
this skill carries the Hermes execution mechanics and the pitfalls found in practice.

## When to use

Any ai-badger repo task executed with Hermes delegation: spec-first feature work, refactors
with owner-gate approval, multi-wave implementations. Trigger on "spec this out",
"run owner-gate-review", "start task X", "go" after a spec is agreed.

## Pipeline at a glance

1. **Spec** — `create-task-spec`: emit `<Name>.feature` + `spec.json`; verify with
   `spec_holes.py` (exit 0 = complete). Pitfalls in `references/gherkin-spec-mechanics.md`.
2. **Gate** — `owner-gate-review`: build the form (`cp template → patch CONFIG + DECISIONS`),
   start the watch BEFORE telling the reviewer, ingest by reading the file (check the trailing
   `<!-- end refinement feedback -->` marker), reconcile `## Not answered` explicitly.
3. **Register + worktree FIRST** — `task_tracker.py start <taskId> --session-id <...>
   --branch task/<id>` → creates the worktree. Create it BEFORE any review or exploration;
   all subsequent work happens inside it (user correction: a plan drafted in the main
   checkout first is work to redo). Pass the REAL session id:
   `--session-id "$HERMES_SESSION_ID"` — Hermes exports it to every terminal subprocess, so
   it is always the true id (keep reattach/resume working; a synthetic
   `hermes-$(date +%s)` only unblocks `start`). The tracker's auto-detection
   (CLAUDE_CODE_SESSION_ID / current-session.json) fails in plain CLI sessions with
   "No session reference ...", so pass the flag explicitly. (Fallback if the var is empty:
   query `~/.hermes/state.db` sessions matching cwd, source='cli'.) The worktree branches from the main
   checkout's CURRENT HEAD — if a parallel agent already committed there (e.g. a sibling
   plan branch), your worktree includes their commits; for parallel-plan work that IS the
   merge base you want.
4. **Plan in the worktree** — `docs/work/<date>-<slug>-plan.md` with before/after flow
   diagrams (user preference), then dispatch a READ-ONLY plan-review sub-agent. Incorporate
   its MUST-FIX items as a plan rev and commit before any implementation.
5. **Waves** — dispatch implementation sub-agents (TDD RED→GREEN, small commits). Run join
   gates YOURSELF after each wave (build + full test on the combined tree — "review every
   join, not just every part").
6. **Finish** — state.json entry, tracker `finish`, subagent entries recorded.

## Pitfalls (all hit in practice)

- **Delegation summaries truncate AND the cache cleans them.** Large sub-agent outputs are
  delivered head+tail only. Read the full file at `~/.hermes/cache/delegation/subagent-summary-*.txt`
  IMMEDIATELY on delivery — the cache deletes these files (twice in one session), and the live
  transcript holds only the same truncated summary. For load-bearing verdicts (plan reviews,
  quality gates) have the subagent ALSO write its full findings to a file in the worktree —
  that survives. If read_file flags the summary file as "Binary file", it is an encoding
  artifact, not content — decode it with python instead:
  `python3 -c "d=open(P,'rb').read(); print(d.decode('utf-8',errors='replace')[...])"` and
  slice from the plan/verdict body marker (hit 2026-08-06 for both the plan and the review
  summaries).
- **Parallel subagents in ONE git worktree.** File-disjoint packages can run in parallel, but
  concurrent `dotnet build`/`dotnet test` collide on shared `obj/` dirs and the second agent
  sees the first one's broken WIP. Instruct each agent to run ONLY targeted builds/filters
  (`--filter 'PackageName'`) and to stage only its own paths (never `git add -A`). The
  orchestrator runs the full suite at the wave join, alone.
- **The ORCHESTRATOR editing source files while a subagent builds is the same collision.**
  Comment/doc cleanups and tiny surgical fixes by the orchestrator land in the same worktree
  the implementation agent is building — the agent's `dotnet build` can hit the half-written
  file (transient CS1022 "Type or namespace definition expected" observed 2026-08-05 when a
  doc-comment patch briefly dropped a class declaration line). The subagent correctly reads
  it as a torn write and re-builds, but it wastes a cycle and can look like the agent broke
  something. Options, in order: (1) do orchestrator edits on src/ files BEFORE dispatching the
  implementer, or restrict them to files the agent won't build (docs, its test files); (2) if
  you must edit mid-flight, warn the subagent in its brief ("orchestrator may touch unrelated
  src files; transient build errors from torn writes are expected — re-run the build"); (3)
  never commit while the agent is mid-build — stage+commit at the join.
- **Patching doc comments: include the declaration line in old_string AND new_string.**
  When trimming a `/// <summary>` block that sits directly above a declaration (`internal
  static class X`, `public sealed record Y`), ending old_string at the newline after
  `/// </summary>` invites the fuzzy matcher to join `</summary>` with the next line
  (`/// </summary>public sealed class ...`) or drop the declaration — hit THREE times in one
  comment-cleanup pass. Always carry the declaration line through both sides of the patch,
  and re-read the file (or grep for the class/record line) after each edit; the build is the
  backstop, but the diff review catches it first.
- **Interrupted subagent** (model-API stall: "waiting for model response"). It usually dies
  during reads with ZERO changes. Check `git status` in the worktree, then re-dispatch the
  same package with a "RE-DISPATCH — first attempt stalled, nothing left over" note plus
  updated context (what landed since). Don't merge or "fix" its non-existent partial work.
- **delegate_task batch mode drops the top-level goal/context.** When dispatching with
  `tasks: [...]`, the top-level `goal`/`context` fields are IGNORED — every bit of the brief
  must live inside each task entry's own context. Hit 2026-08-06: a WP1–WP8 plan passed at
  top level arrived as "Implement WP1–WP8 exactly as specified" with no specification; the
  child was found `search_files`ing the repo for "WP1|WP8". Rescue that worked: write the
  full plan into a path the child is already searching (the gitignored
  `.ai-badger/task-tracking/` dir it had just probed) — it re-searched, found it, and
  completed the work correctly. (Children can also read the parent's session store via
  `~/.hermes/state.db` — don't rely on it, but don't be surprised.) Prefer a single-task
  dispatch (top-level goal/context) unless you truly need parallel fan-out.
- **Stale terminal cwd after delegations.** Sub-agents can leave the shared session cwd
  inside a deleted temp dir; subsequent `cd X && ...` chains then fail with exit 126 and a
  confusing "No such file or directory" for YOUR cd target. Use the `workdir` parameter on
  the terminal tool instead of `cd` chains.
- **File tools resolve against the session cwd, not the main checkout.** Once a terminal
  call has used `workdir=` (e.g. the task worktree), read_file/search_files resolve RELATIVE
  paths against that dir — so `.ai-badger/worktrees/<id>/infra/x.tf` written as if from the
  main checkout fails with "File not found". Use ABSOLUTE paths for repo files as soon as a
  task worktree is active (or paths relative to the worktree root).
- **Join gates are yours, not the sub-agents'.** After each wave, run `dotnet build` + the
  full `dotnet test` yourself on the combined tree before dispatching the next wave. A wave
  whose packages each passed alone can still fail together (shared files, version pins).
- **Red builds hide an error iceberg; the first visible error is the tip, not the
  inventory.** MSBuild stops at the first failing project, so dependent projects never
  compile and their errors stay invisible; Roslyn declaration-phase errors (missing types,
  wrong arity) also suppress method-body binding errors — a 1-error build was the gate to
  ~393 real compile errors across the test project (2026-08-06). Fix iteratively: first fix
  → rebuild → next wave surfaces. The same applies to behavior: a refactor commit that
  compiles can still be boot-broken with zero unit tests exercising the entry point. Two
  real bugs of that shape, both only surfaced by the E2E factory + a live boot: a
  `IBundledModel` registration without `services.AddHttpClient()` ("Unable to resolve
  IHttpClientFactory" at startup), and `GetRequiredService<ILogger>()` — default hosts do
  NOT register the non-generic `ILogger`; resolve `ILoggerFactory.CreateLogger("...")`
  instead. Prove Program.cs-style top-level flows with a live boot or a process-level test,
  never by trusting unit tests.
- **Error-path test mirrors must discriminate causes.** A BDD/unit mirror of a production
  error mapping that catches `Exception` broadly makes every failure scenario pass on ANY
  exception (wrong key, corrupt bank, unrelated IO — all green). Re-add typed filters
  (`catch (SqliteException ex) when (ex.SqliteErrorCode == 26)`) so a scenario can only pass
  for its stated cause; other causes surface their own message (review-gate S1, 2026-08-06).
- **Verification-evidence discipline.** For non-code artifacts (generated review forms,
  specs, docs) verify with a temp `hermes-verify-*.mjs`/`.sh` script under the OS temp dir
  that wraps the canonical gate (e.g. re-runs `spec_holes.py`), asserts structure, runs,
  then gets deleted. Report "ad-hoc verification, not suite green" — never claim a canonical
  gate ran when it didn't, and never let a harness failure be a false alarm (see the vm
  gotcha in `references/gherkin-spec-mechanics.md`). BSD `mktemp -t hermes-verify-XXXXXX.py`:
  the template MUST end in `XXXXXXXX`, or BSD mktemp returns the literal template string
  (a script literally named `XXXXXX.py`). The verification reminder refires EVERY turn
  against a persistent changed-path list and never clears once evidence exists — on
  unchanged bytes, one re-run is the cheapest compliance; repeated identical re-runs are
  theater, but so is arguing with the loop. After `tracker finish` removes the task
  worktree, the final state lives only on the pushed branch: to re-verify it (fresh
  evidence demands, post-merge checks), re-create the worktree at the final commit with
  `git worktree add .ai-badger/worktrees/<id> origin/task/<branch>` — and copy the
  gitignored assets again (see the assets pitfall): a fresh checkout fails model-dependent
  tests until the copied files land (hit 2026-08-06: BundledModelLoggingTests' all-present
  case on a fresh worktree).
- **Main can move under a task branch.** If the user (or a sibling branch) commits to main
  mid-task — including committing YOUR untracked spec files themselves — the worktree
  branch sits on a stale base and `git diff main..HEAD` shows phantom out-of-scope changes.
  Rebase early: `git merge-base main HEAD` to confirm staleness, then
  `git rebase origin/main`. Add/add conflicts on files both sides created (e.g. the spec
  the user also committed) resolve with `git checkout --ours` (branch content) during the
  rebase, `GIT_EDITOR=true git rebase --continue`; re-run the gates after. Full recipe in
  `references/multi-branch-delivery.md`.
- **Ownership-check false positives.** `git diff --name-only main..HEAD` lists files that
  differ because MAIN changed them (stale base), not because a sub-agent violated scope.
  Before accusing an agent: `git log main..HEAD -- <file>` — empty means the change is on
  main's side; `git ls-tree` each side to see who has the file.
- **Tracker state lives in the MAIN checkout.** `.ai-badger/task-tracking/` is gitignored,
  so worktrees don't contain it; tracker commands (`subagent`, `finish`) run from a
  worktree fail with "Unknown task <id>". Run them from the main checkout.
- **`.ai-badger/state.json` is a TRACKED file — state edits made in the worktree are lost
  when `finish` removes it.** The finish protocol's state.json entry must be COMMITTED
  before `finish` (or written in the main checkout): edit in the worktree, commit
  (`chore: record task tracker state (<taskId>)` matches repo precedent), push, and land it
  via the task's PR — a bookkeeping-only branch ships as its own tiny PR (hit 2026-08-05:
  PR #23 was state.json alone, merged by the owner). Uncommitted state edits either make
  `finish` refuse or vanish with the worktree. After the merge the changed path no longer
  exists on disk, so the verification is the COMMITTED BLOB — `git show
  origin/<branch>:.ai-badger/state.json` parsed with `json.loads` + the entry asserted
  (tree identity + content check; state it as ad-hoc, not suite green). Also check for
  ORPHANED chores: a state-chore commit sitting only on a local branch
  (`git branch --contains <sha>` shows no origin/main) means main's state.json lags the
  tracker — don't trust `completedTasks` to be current without the check.
  **Cwd-persistence trap (hit 2026-08-06):** with a task worktree active, a state.json
  update through execute_code or a relative path lands in the WORKTREE, not the main
  checkout — `finish` then keeps the worktree (`keptBecause: uncommitted changes:
 ai-badger/state.json`). Write state.json with an ABSOLUTE path to the main checkout (or
 `cd` back first) and confirm which checkout received it. **The finish guard checks the
 MAIN checkout's file: a state.json entry committed ONLY on the branch still refuses
 finish (`"state.json has not been modified since task start"`) until the PR merges and
 main is pulled.** Working sequence (hit 2026-08-06): commit the entry on the branch →
 merge the PR → `git pull --ff-only` in the main checkout → `finish`. If `finish` already removed the
  worktree but the chore never shipped: re-add it at the branch (`git worktree add
  .ai-badger/worktrees/<id> origin/task/<branch>`), copy the main checkout's state.json in,
  commit + push, remove again. Also: main's state.json may already carry a PARALLEL
  session's in-flight entries (e.g. an `online-pr-review` entry) — copying it into the
  worktree commits their state too; check the top entries are yours before committing, and
  expect their entries in the PR diff (harmless, but say so).
- **A kept worktree is a handoff vehicle, not just leftover cleanup.** `tracker finish`
  refuses to remove a worktree holding work that exists nowhere else — and that is exactly
  right when the NEXT task's starting artifacts (a plan doc, a quality-log JSONL) live
  there uncommitted. Pass `--keep-worktree` deliberately, record WHY in the state.json
  `next` field ("worktree kept — holds the hook plan"), and leave the artifacts in place;
  the next task starts from them. Read the `worktree.keptBecause` field in the finish
  output before assuming cleanup failed (hit 2026-08-05: the memory-grade-hook plan +
  memory-quality.jsonl rode the kept worktree to the next task).
- **`task_tracker.py finish` output is NOT pure JSON — a CLAUDE.md over-budget warning
  line precedes the report** (hit twice 2026-08-06: `json.loads(sys.stdin)` → JSONDecodeError
  "Expecting value: line 1 column 1"). Parse from the last `{` (`raw[raw.rfind('{'):]`), or
  `tail` the output. The same warning can also precede a `start` report.
- **Junk in the worktree blocks `finish`'s removal: read `keptBecause`, delete the junk,
  re-run.** `tests/AiRaccoon.Tests/TestResults/results.trx` (1.7 MB, untracked test output)
  made finish keep a worktree whose real work was already merged — `keptBecause` named the
  dir, `rm -rf` it, re-run finish, and the worktree is removed. Only deliberate artifacts
  justify `--keep-worktree`; transient test output never does.
- **Parking an obsolete STARTED task: update state.json FIRST, then finish.** A task whose
  PR merged seconds after `start` (review gate already passed in the PR flow) is dead on
  arrival. Prepend a lean `{id, title, summary: "PARKED — obsolete: <why>"}` entry to
  `state.json`'s `completedTasks` + refresh `lastUpdated` (this also satisfies finish's
  exit-3 guard "state.json not updated since task start"), then `finish <id>`. Recorded
  usage numbers then reflect the real (short) session.
- **Leftover `<taskId>-hermes` worktrees survive `finish` when the first finish kept the
  dir** (hit 2026-08-06: `review-encryption-refactor-hermes`, detached HEAD at an old
  commit, working tree clean). It is not a git-registered branch worktree you can trust;
  remove it manually: `git worktree remove --force .ai-badger/worktrees/<taskId>-hermes
  && git worktree prune`.
- **Closing a tracker: verify the WHOLE task shipped, or park it.** Merged PRs whose
  titles mention the task are NOT proof the task shipped — wave-1 PRs ("refactor: shared
  const", "docs: plan X") can merge while the wave-2 implementation sits unmerged on the
  branch or sibling branches (hit 2026-08-05: closed shell-prompt-real-username on
  #744/#745 MERGED, then found 33 files / +575 lines of GET /identity + useCurrentUser
  still unmerged; reverted). The shipped-check is a DIFF: `git log origin/main..<branch>
  --oneline` AND `git diff origin/main..<branch> --stat` must both be empty — never rely
  on `git merge-base --is-ancestor <tip> origin/main`, squash merges break ancestry in
  both directions. Also check sibling branches (`git branch -r | grep -i <keywords>`,
  e.g. lane-wp1-*, wp2-*), `gh pr list --state open` (empty is NOT proof — branch work can
  exist with no PR), and `git worktree list` (a live worktree on the task branch =
  in-flight). Any delta → PARK (leave STARTED, report the unmerged scope); undo recipe and
  full protocol in `references/zombie-tracker-close-verification.md`.
- **Delegation token recording on current Hermes.** `subagent --delegation <id>` WORKS on the
  current tracker (verified 2026-08-05: 4 successful recordings incl. a 3-task parallel batch;
  2026-08-06: three more after a source fix, see below). Use it FIRST. **The refusal "no
  token record in this session source … Refusing to record a fabricated number" usually
  means the task's `trackingSource` was recorded WRONG at `start`, not that the delegation
  is unknown.** Root cause (hit 2026-08-06): a plain CLI terminal has no HERMES_SESSION_ID,
  so `start` auto-detects the `claude` source (transcript fallback) and the claude source's
  `delegation_usage` is None → every `--delegation` refuses even though the tokens are in
  `~/.hermes/state.db`. Fix the RECORD rather than falling back to manual sums: the
  `subagent` command reads the task entry from `.ai-badger/task-tracking/token-usage.json`
  (`lib.load_usage()` + `find_entry`) — NOT `executed-tasks.json` (patching that file first
  has no effect). Patch the entry's `trackingSource` from `"claude"` to `"hermes"` (the
  hermes source registers itself by filename pattern, so it resolves the moment the record
  points at it) and re-run the SAME `--delegation` command — real per-delegation sums come
  straight from the session store (2026-08-06: architect 940K, plan-review 929K, implementer
  5.77M). If the record is already `hermes` and it still refuses, THEN fall back to querying
  `~/.hermes/state.db`: `SELECT state, result_json FROM async_delegations WHERE
  delegation_id = '<id>'` — parse `result_json.results[0].tokens` → `{'input': N,
  'output': M}`, sum, then `task_tracker.py subagent <task> <sum> --description "<what it
  did> (input+output)"`. Passing BOTH `<total_tokens>` and `--delegation` is an error
  ("Pass exactly one"). The refusal is by design — never invent a count. **Pipe the computed
  sum, never type it (hit 2026-08-06: typed 628141 for a real 2352514 — the record was
  corrected by editing `.ai-badger/task-tracking/token-usage.json` in place).** When the
  fallback path is used, capture the sum from the query output into a shell var and pass
  `$SUM`.
- **Subagent commits carry a bogus identity (`a <a@b.c>`)** when the subagent env has no git
  identity — harmless because PRs squash-merge, but check
  `git log --format='%an <%ae>' -5` before reading authorship off the branch (the plan
  commit you made yourself may show up under a different name).
- **Untracked spec files don't travel to worktrees.** Emit output created in the main
  checkout is invisible in the task worktree — copy it in and commit as the task's first
  commit so the acceptance contract rides the branch.
- **Gitignored build assets don't travel to worktrees either — and the copy can silently
  no-op.** Fresh worktrees lack gitignored files (e.g. a bundled ONNX model under
  `src/<App>/Models/`); csproj wildcard copies (`<None Include="Models/*.onnx"
  CopyToOutputDirectory="PreserveNewest">`) evaluate at BUILD time, so building BEFORE the
  file exists silently skips the copy and the whole embedding/retrieval test family fails
  with "model not found next to the tool" (hit twice, both tasks; zero failures sync-related).
  Copy the gitignored asset from the main checkout into the worktree BEFORE the join-gate
  build, then rebuild — the copy only happens on a build that runs after the file is present.
- **gh pr merge fails from a task worktree.** `gh pr merge` tries to check out the base
  branch locally; inside a worktree that is not the base branch it dies with
  "'main' is already used by worktree at ...". Run it from the MAIN checkout. Warning:
  the failure message appears AFTER the remote merge may already have succeeded
  ("Pull request #203 was already merged" on retry) — check `gh pr view`/`gh pr checks`
  before assuming it didn't merge. With `--delete-branch` and the task worktree still
  attached, the MERGE and the REMOTE branch deletion succeed but the LOCAL deletion
  fails ("cannot delete branch ... used by worktree") — expected, not an error; the
  worktree removal at `tracker finish` frees the branch name later.
- **Pre-push lefthook gates hide the real failure.** Every push runs lint + test:single +
  e2e (playwright, ~1-2 min) and aborts with a bare "failed to push some refs" — the
  reason (e.g. "Tests  1 failed") sits above, under the hook banner. Grep the full push
  output for `rejected|failed` before debugging the remote. A flaky-looking failure is
  often a REAL test broken by the branch (this session: new article id 12 exposed a
  spec fixture that assumed the newest published post always has a pager neighbour).
- **Fresh worktrees lack node_modules.** npm projects: run `npm ci` in the worktree
  before the build gate (extends the "gitignored assets don't travel" pitfall). Start it
  in the background while reviews run. Bun projects (jsaa frontend): `bun install
  --frozen-lockfile` — ~748 packages in under 2s, so just run it synchronously before
  the frontend lanes; without it `bun run lint` fails with "eslint: command not found".
- **Fresh ai-badger worktrees lack the repo `.venv` (gitignored) — and neither the shell `python3` (homebrew 3.14: "No module named pytest") nor `/usr/bin/python3` (has pytest, but gate subprocesses die on `import jsonschema` in engine/badger_lib.py) is the canonical interpreter.** Run worktree gates with the MAIN checkout's venv binary from the worktree cwd — venvs are path-independent, so `<main-checkout>/.venv/bin/python3 -m pytest -q` works from anywhere and carries jsonschema + pytest. Measured 2026-08-06 on an identical docs-only tree: `/usr/bin/python3` run → 40 failed + 99 errors (all environmental ModuleNotFoundError chains), `.venv` run → 3185 passed / 18 skipped.
- **User-initiated `git merge origin/main` into your task branch mid-task: the state.json add/add resolves by UNION, not `checkout --ours`.** The other side's entry is another task's record (each references its own PR); keep BOTH entries — dedupe by entry id, newest first, `lastUpdated`/`next` from your side — then commit the merge. Hit 2026-08-06: the owner merged the sibling PRs to main and merged main into the task branch themselves; the conflict was expected and the union kept both task records in one file. Also check whether the merged main content introduced its own docs-canonicality gap (a sibling docs PR had merged without its docs/work/README.md row — main CI went red on test_docs_tree_is_canonical; the row fix rode the next PR).
- **ng test output carries ANSI codes even when piped.** Asserting on captured output
  (grep for "Tests  12 passed") silently fails — strip first:
  `OUT=$(... | sed $'s/\033\[[0-9;]*m//g')` (hit twice in one verification script; the
  tests were green both times, the pattern was matching escape bytes).
- **An un-pushed previous task's commit rides into the next task's PR.** The worktree
  branches from local main HEAD, so a committed-but-unpushed refresh/bookkeeping commit
  appears in the next PR's diff. Don't push main directly to avoid it — say so in the
  PR description instead (the refresh commit rode PR #203 and merged cleanly).
- **den-refresh PR hygiene (2026-08-06, two repos).** A refresh run leaves a mixed dirty
  tree: seed-once files edited by OTHER tasks (`.ai-badger/state.json` tracker state),
  refresh-created backups (`.mcp.json.bak-<ts>`), and another task's untracked docs —
  none belong in the refresh PR. Stage the footprint explicitly (`git add .ai-badger`
  then `git restore --staged <foreign paths>`), never `git add -A`. INCLUDE `.mcp.json` +
  `.claude/settings.json` when their diff is the ai-raccoon-memory hook/MCP-enablement
  wiring (that IS part of the 0.80.0+ delivery), EXCLUDE the `.bak`. The refresh report:
  capture it to a file, never `tail` it — the first run does the real re-scaffold
  (config.json's frameworkVersion advances only when a re-scaffold ran) and a re-run then
  reports all-clean `reScaffolded:false`; that two-run shape is convergence, not a no-op,
  and the truncated first output hides it. Pre-push gates: a red docs/ledger stage caused
  by ANOTHER task's untracked review doc is not your PR's failure — the gate names its own
  stage-scoped bypass (`VERIFY_SKIP=docs git push`); use it only after confirming your
  commit touches none of that stage's files, and say so in the report (the gate's repair
  tool itself refuses to regenerate over uncommitted foreign files).
- **ai-badger repo's OWN commit mechanics (2026-08-06, framework repo self-work).** The
  pre-commit chain (version-sync, index-build, changelog-index, plugin-skills-sync,
  docs-guard, deps-guard, shipped-paths-guard, scaffold-freshness-guard, pylint) validates
  the WHOLE TREE, not the staged set — a feature/release two-commit split fails the
  changelog-index + docs-guard lanes (changelog entry without the regenerated README row).
  One combined commit (`feat: ... (0.81.0) (#313)`) is the shape the chain forces; the
  release tail (VERSION, changelog entry, `tooling/changelog_index.py`,
  `tooling/version_sync.py` → `.claude-plugin/plugin.json` + `marketplace.json` +
  `index.json`, self re-scaffold output) ships in that same commit. Further facts:
  (1) a MANUAL re-scaffold of the repo must run with `AI_BADGER_MCP_AVAILABILITY=all` —
  without it, on a dev machine with hermes/ai-raccoon installed, `.mcp.json` renders
  `${HOME}`-expanded commands while `.github/mcp.json` renders plain, so the #193
  "declared differently" rule DROPS those servers from the Copilot file and the tree
  diverges from CI (which keeps them); root-caused by rendering both destinations
  in-process. (2) `.ai-badger/skills/learned/` is untracked hook-synced machine state that
  the freshness guard still compares — when the shared learned store moves, the guard
  fails "content differs, regenerates differently" on it; the guard's remediation
  re-scaffold (with the env override) restores consistency; never stage the learned dir.
  (3) The shipped-paths guard rejects machine-specific absolute paths (`/Users/<name>/...`)
  in anything that renders into shipped agent files — project-local invariants and other
  rendered content must be portable (hit with the first dogfood invariant draft).
  (4) a self-scaffold backs up `.mcp.json` to `.mcp.json.bak-<ts>` — exclude the `.bak`,
  and restore `.github/mcp.json` from HEAD if your run diverged (the guard passes against
  the committed state).
- **Cross-branch contracts.** When a parallel task branch owns the CLI/config surface your
  feature reads (settings keys, command formats), pin the EXACT strings in both subagent
  briefs and plans, and verify key parity at the join — deviation only surfaces there.
- **Plan review must cross-check the FILE list against the TEST list.** A TDD cluster that
  requires behavior X (e.g. `ReadOptionsAsync_MapsNewRow`) needs every file containing X
  (e.g. `SyncCloudStoreFactory.cs`) in the plan's file list — one omission silently routes
  the whole feature to the no-op path (NullCloudStore; caught by review, would have shipped
  dead). Have the plan-reviewer verify test requirements → file coverage, not just the
  design prose.
- **Have plan review PROBE load-bearing SDK/API claims empirically.** In one review the
  reviewer ran probe projects against the exact pinned packages and corrected two claims
  the plan's design rested on: Azure no-login throws `CredentialUnavailableException` (not
  `AuthenticationFailedException`), and AWS SDK v4 resolves the credential chain LAZILY
  (ctor succeeds on a credential-less machine; the first call throws `AmazonClientException`).
  Docs and assumptions get these wrong; a probe against the pinned versions settles them and
  the corrected facts pin the tests.
- **Patching test files after a subagent: re-read first.** The patch tool's fuzzy matcher can
  splice a new test into the TAIL of the preceding method (three pre-existing assertions
  silently dropped in one patch, caught only in the diff). Read the region, apply, and
  re-read the returned diff for the pre-existing lines before running.
- **Process-level tests need a hermetic child environment.** Simulating "command missing" by
  PREPENDING an empty dir to the child's PATH does not work — PATH lookup falls through to
  the real command later in the chain (a real `bws` on the dev machine was found and used;
  the test passed solo for the wrong reason). Correct recipe (2026-08-06): resolve the
  launcher by absolute path (scan the parent PATH for `dotnet`), then give the child
  `PATH` = only the controlled dirs plus the system dirs shell scripts need
  (`/usr/bin:/bin`), and explicitly blank ambient secrets in the child (token + passphrase
  env vars). Capture BOTH streams and include them in assertion messages; `dotnet
  run`/`exec` banners ("Using launch settings from…") pollute stdout, so assert with
  contains/grep, never exact equality. FakeLogger's `Collector.LatestRecord` THROWS on an
  empty collector — for a "logs nothing" assertion use `Count.ShouldBe(0)`.
- **MCP server maintenance: provision, don't kill (2026-08-06).** When the project's
  ai-raccoon (or any stdio) MCP errors, the FIRST move is the server's stderr:
  `~/.hermes/logs/mcp-stderr.log` carries the real exception — hit: `Bundled embedding
  model 'model_qint8_arm64.onnx' not found next to the tool`. Fix = provision the
  SHA-pinned model + vocab into EVERY `~/.dotnet/tools/.store/arasz.ai-raccoon/<version>/*/Models/`
  dir (tool updates drop the gitignored assets). Never "restart" the server by killing its
  child process: the mcp_stdio_watchdog exits WITH the child and the Hermes client does NOT
  respawn mid-session — you lose that MCP server for the rest of the session (hit same day:
  killed the ai-raccoon child → watchdog gone → memory_search unreachable until a fresh
  session). Provisioned files are picked up per-call by NEW processes; stale-version
  processes keep failing — the fix is a session/client restart, not a kill.
- **Before implementing after any gap: skills + memory + branches + PRs (user correction
  f: 2026-08-06).** A session gap or a parallel session can mean the work is ALREADY DONE:
  project skills are updated in real time (ai-raccoon-encryption documented a whole
  follow-up wave with commit SHAs), and `gh pr list --state open` + `git branch -r` show
  live branches. Hit 2026-08-06: a full turn (CI skip fix + source-constants refactor)
  duplicated work already committed in open PR #32 because the skill wasn't consulted
  first — user correction: "first ensure you use memory mcp and skills". Order: load the
  governing skill(s), query the project memory bank, list open PRs/branches, THEN
  implement. The commit landed on a closed branch is the cost of skipping the check.
- **Review gates: the PR body is the contract; explore in your own worktree (user
  corrections f: 2026-08-06).** Review an open PR against the PR's OWN description —
  verify each claim it makes against the code and report per-claim — not against a
  reconstructed spec from skills/references ("f: base your review on PR"). For
  exploration create your OWN worktree (`git worktree add <path> origin/<branch>` then
  `git switch -C <branch>`); never read/run in another session's live worktree ("if you
  need explore something, create your own worktree"). Two per-claim checks that pay off:
  (1) a PR adding a consolidation helper (e.g. TestOpenSshKeyBuilder) while the duplicated
  copies stay in place has dead code AND unconsolidated duplication — flag both; (2) a
  rename PR must carry the new name into identifiers (fields/vars/params), not just type
  declarations — grep the old name across src AND tests (hit: EncryptionState→
  EncryptionSourceSidecar left `_encryptionState` fields everywhere).
- **Spec elicitation with this user.** Runs the `create-task-spec` stages but answers in
  terse confirmations to batched questions and rules every card inline via `f:` markers;
  a wall of 15 title-prompts confused more than it elicited — the approved mode is: agent
  drafts ALL scenario titles/steps as a proposal, user corrects. Record every ruling as a
  dated provenance comment (with the `f:` note) inside the `.feature`, keep the working +
  shipped copies in parity and verify both. Full playbook in
  `references/spec-elicitation-playbook.md`.
- **Empirical research before adopting frameworks/algorithms (user preference).** The user
  treats leads as leads, not decisions: "typical-rag-dotnet → check if Semantic Kernel is
  worth it" and "don't duplicate fusion — check the state of research first". Before
  committing to a framework, algorithm, or library, dispatch evidence-graded research
  sub-agents (MEASURED/READ/INFERRED/UNVERIFIED with URLs) and present the verdict as a
  decision card, never as silently-applied direction.
- **User style:** fast autonomous execution, waves with no confirmation between them,
  before/after flow diagrams in plans, one PR per task (local merge only when the user's
  established repo pattern says so), TDD everywhere. **No copilot-PR-reviewer round** —
 "there is no copilot review, merge if green" (stated repeatedly, incl. 2026-08-05
 "f: copilot will not run"): the code-reviewer quality gate + full-suite green are the
 merge condition; skip the copilot poll entirely.
- **Graded memory-result logging when dogfooding ai-raccoon (user preference, f: 2026-08-05).**
  When a task uses the ai-raccoon memory store, the user wants every `memory_search`
  result logged to a JSONL quality file in the repo — one line per search with `ts`,
  `query`, `scope`, `projectId`, **`workspaceId`** (null when absent — correlation across
  projects/workspaces is the point), the FULL result payload, and a `usefulness` grade
  1–5 the agent assigns ("include query, full result and how useful it was from 1 to 5").
  Write the line at search time (`usefulness: null`), fill the grade in place when the
  agent answers; unanswered lines stay null — honest data. The hook automation for this
  (env `AI_BADGER_MEMORY_GRADE=1`, default off, machine-wide enable) is planned in
  `docs/plans/memory-grade-hook.md` (ai-badger, separate PR); until it lands, do it
  manually per search and keep the log in the task's worktree so it survives to the next
  task.

- **Hermes-side ai-badger hooks NEVER fire (diagnosed 2026-08-06, FIXED 0.80.0): verify,
  don't assume.** ai-badger deployed flat `.py` files into `~/.hermes/plugins/`, but
  Hermes only loads DIRECTORY plugins (`~/.hermes/plugins/<name>/plugin.yaml` +
  `__init__.py` with `register(ctx)`, opt-in via `plugins.enabled`) — so none of the
  hermes entries in `hooks-manifest.json` (drift-notice, context-enrichment,
  commit-reminder, memory-grade) ever executed in a Hermes session before 0.80.0. The
  memory-grade quality log's only line was a scripted probe, not a live host. As of
  0.80.0 (PR #311) the fix shipped and is live-verified: plugin dir + `hermes plugins
  enable ai-badger` + fresh-session gate. Empirical checks before trusting a hook:
  `__pycache__` in `~/.hermes/plugins/`, `grep ai_badger_hooks ~/.hermes/logs/agent.log*`
  for execution (not lint) lines, and a live side-effect probe (run `memory_search`
  with the grade env on → line lands?). Full diagnosis: repo
  `docs/work/2026-08-06-hermes-integration-diagnosis.md` (PR #310); the Hermes plugin
  ABI + verification recipes live in the `hermes-plugin-authoring` skill.
 - **TDD-first for framework/hook fixes: RED tests pin the expected shape BEFORE the
 plan (user f: 2026-08-06).** For implementation work the user's order is: write the
 failing tests that pin the contract first ("first assume expected shape by tests") —
 the tests ARE the spec; plan docs stay lean or come after. Commit the RED state
 (test-only) before implementation, then implement until the pinned tests pass.
 Expect the suite to then expose STALE SHAPE TESTS encoding the old contract
 (deployment-shapes/copy-skew tests hardcode old paths — hit 2026-08-06 with the
 plugin-dir layout): fix those in a follow-up commit; they are RED-too, not regressions.
 - **Absolute paths must point AT the worktree, not the main checkout (2026-08-06).**
 After `start`, every repo-file write must target
 `.ai-badger/worktrees/<taskId>/...` — an absolute path to the MAIN checkout writes
 silently to main (no error), leaving the worktree without the change and main dirty
 (fix: `git checkout -- <file>` in main, rewrite in the worktree). Verify the path
 prefix contains `worktrees/<taskId>` before write_file on repo files.
 **Consequence hit twice the same day: a review-fix applied to the main checkout never
 reaches the branch — the merged PR silently misses it** (the ADR Shape-D fix from a
 review round shipped only as a follow-up chore PR). After every review-fix round,
 verify the BRANCH carries the fixes: `git diff origin/<branch> -- <file>` empty +
 the file present in `git ls-tree origin/<branch>` — a fix that "applied" in the main
 checkout is invisible to the PR.
 - **Live gate for Hermes-side features: fresh session, not the current one.** Hermes
 plugin hooks load at session start; enabling/installing mid-session changes nothing
 in the running session. Prove Hermes-side behavior with `hermes plugins enable
 <name>` + `hermes plugins list`, then a fresh `hermes chat -q "<prompt that
 exercises the feature>"` and check the artifact (e.g. the quality-log line with
 host/sessionId). A helper probe or unit test proves the pipeline, not the host
 firing — the WP7 gate the 0.79.0 memory-grade hook was missing.

## Files

- `scripts/verify-gate-form.mjs` — verify an owner-gate review form: CONFIG/DECISIONS
  integrity, JS syntax, no stale template strings, and a DOM-stubbed render smoke run with
  BOTH script blocks in ONE shared vm context.
- `references/gherkin-spec-mechanics.md` — spec_holes.py `@deferred` tag placement, owner-gate
  form generation + ingest protocol, the vm shared-global verification gotcha.
- `references/multi-branch-delivery.md` — rebase-when-main-moves recipe, add/add conflict
  resolution, ownership-check commands, tracker-from-main-checkout, cross-branch contract
  pinning (two parallel feature branches, watcher + cli-config).
- `references/spec-elicitation-playbook.md` — elicitation mode (a) (agent drafts, user
  corrects), batched-question style, `f:` rulings as dated provenance comments, spec_holes
  semantics, working/shipped copy parity, ≥1min re-ask pacing.
- `references/zombie-tracker-close-verification.md` — the shipped-check protocol
  (diff, not titles), finish/park mechanics, undo-a-wrong-finish recipe, worked example.
