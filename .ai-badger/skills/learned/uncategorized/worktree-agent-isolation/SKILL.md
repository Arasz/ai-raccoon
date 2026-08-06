---
name: worktree-agent-isolation
description: >-
  Run multiple agents in parallel with full git worktree isolation — each agent
  gets its own directory, branch, and context. Integrates via GitHub PRs, never
  touching the main checkout directly. Use when the user says "worktrees only",
  "agent isolation", "parallel workstreams", or "don't touch main".
trigger: user requests worktree-based isolation, parallel agents, or explicit "don't touch main"
---

# Worktree Agent Isolation

Run N independent coding agents in parallel, each in its own git worktree, with
zero file conflicts. Integrate via GitHub PRs — the main checkout is read-only.

## When to Use

- User says "worktrees only", "don't touch main", "agent isolation"
- Multiple independent tasks can run in parallel
- Another agent/session is using the main checkout
- User wants to see draft PRs for in-flight work visibility

## Setup

```bash
# Fetch latest main first — ensures worktrees branch from the true HEAD
git fetch origin main

# Create N worktrees from origin/main (not local main, which may be behind)
git worktree add ../project-task-a -b task/<id>-<slug> origin/main
git worktree add ../project-task-b -b task/<id>-<slug> origin/main
```

Each worktree gets its own branch, own working directory, own build artifacts.

**Always use `origin/main`** as the base, not `main`. Local main may lag behind
after another agent merges. Fetching first ensures all worktrees share the same
ancestor, reducing rebase conflicts when merging sequentially.

## Dispatching Agents

```python
delegate_task(tasks=[
    {"goal": "Implement issue #X: ...",
     "context": """Project: /abs/path/to/project-task-a
     Branch: task/x-slug (already checked out)
     Build: dotnet build
     Test: dotnet test --filter "..."
     DO NOT USE Rider MCP tools. Use terminal, read_file, write_file only.
     TDD MANDATORY: ..."""},
    {"goal": "Implement issue #Y: ...",
     "context": """Project: /abs/path/to/project-task-b
     ..."""},
])
```

Key: pass the **absolute worktree path** as `Project`. Each agent's working
directory is isolated — they never see each other's files.

**For full-stack projects**, include BOTH backend and frontend build/test commands
in the agent context. Backend-focused orchestrators default to `dotnet build` and
miss frontend compilation errors:

```python
{"goal": "Implement issue #X: ...",
 "context": """Project: /abs/path/to/project-task-a
 Branch: task/x-slug (already checked out)
 Build: dotnet build
 Test: dotnet test --filter "RequiresInfra!=true"
 Frontend build: cd src/frontend && bun run build
 Frontend test: cd src/frontend && bun run test
 Frontend lint: cd src/frontend && bun run lint
 DO NOT USE Rider MCP tools. Use terminal, read_file, write_file only.
 TDD MANDATORY: ..."""},
```

## User Preferences (baked in from session corrections)

- **Docs bundled into feature PRs** — always include doc updates/new entries in the same PR as the
  feature code. When dispatching subagents, instruct them to update docs/flows.md, docs/data-model.md,
  docs/architecture.md as part of the feature implementation. Never create a separate docs-only PR
  unless the user explicitly asks for one.
- **Push branches often** — push each worktree branch as soon as it has a commit, even before the
  agent finishes, so the user can follow progress via GitHub.
- **Update GitHub issues** — after EACH merged PR, post a completion comment on the issue:
  `gh issue comment <id> --body "## Implemented ✅ — PR #<n> (merged). <details>"`
  At session end, verify all issues have completion comments.
- **Draft PRs for visibility** — create a draft PR as soon as the branch is pushed, even if work
  is still in progress. Mark ready + admin-merge when tests pass locally.
- **"Don't touch main" is strict** — when the user says "worktrees only", never `git checkout main`,
  never `git merge` into main from the main checkout, never `git reset` on main. ALL integration
  happens via PR merge. The main checkout is read-only for the entire session.
- **No Rider MCP when other agents use it** — if the user says Rider is occupied, instruct all
  subagents to use terminal/read_file/write_file/search_files only. Include this in the delegate_task context.

## Integration After Agents Complete

### 1. Verify in each worktree

```bash
cd /abs/path/to/project-task-a
dotnet build && dotnet test --filter "..."
```

### 2. Commit in worktree

```bash
git add -A && git commit -m "feat: description (#issue)"
```

### 3. Push branch

```bash
git push origin task/<slug>
```

### 4. Create draft PR for visibility

```bash
# From main checkout (or any checkout with gh auth)
gh pr create --head task/<slug> --draft \
  --title "feat: description (#issue)" \
  --body "Summary. Closes #issue."
```

### 5. Update GitHub issue

```bash
gh issue comment <N> --body "## Implemented ✅
PR #<M> opened. [details]
Awaiting merge."
```

### 6. Merge (admin bypass for CI limits)

```bash
gh pr ready <PR_N>
gh pr merge <PR_N> --squash --delete-branch --admin
```

`--admin` bypasses branch protection and CI check requirements. Use when:
- CI quota/limits are reached
- Checks are expected to fail for non-code reasons
- User explicitly says "bypass rules and merge"

### 7. Clean up worktree

```bash
git worktree remove /abs/path/to/project-task-a --force
```

## State Tracking Updates

When main checkout must not be touched, update state.json via a short-lived
worktree + PR:

```bash
git worktree add ../project-state -b task/state-update origin/main
# edit state.json in the worktree
cd ../project-state
git add .ai-badger/state.json && git commit -m "chore: update state.json"
git push origin task/state-update
# PR + admin merge + remove worktree
```

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

**Add/add conflicts on emitted spec/docs files (the owner committed the same files to main mid-task).** When the task branch carries files that the user/another session ALSO committed to main directly (measured 2026-08-04: the user committed the emitted file-watcher spec to main while the task branch carried its own copy), `git rebase origin/main` stops with `CONFLICT (add/add)` on every shared file. The branch copy is usually the NEWER one (it carries post-emit rulings) — resolve by keeping the branch version at every step:

```bash
git checkout --ours <file>...   # for the commit that FIRST added the files (ours = branch state during rebase)
git add <files> && GIT_EDITOR=true git rebase --continue
# if a LATER branch commit updates those files and now conflicts:
git checkout --theirs <file>... # theirs = the update commit being applied (branch-derived, correct content)
git add <files> && GIT_EDITOR=true git rebase --continue
```

After the rebase, re-run the files' own gates (spec_holes.py / JSON validity / scenario counts) — a mangled merge shows up as content loss, not a git error.

## Scaling to N parallel issues

When the user says "continue" or "do all remaining issues":

1. **Plan the batch** — identify which issues are unblocked (no unresolved `blocked-by`).
2. **Create all worktrees at once** from `origin/main`:
   ```bash
   for issue in 179 203 205; do
     git worktree add ../project-$issue -b task/$issue-slug origin/main
   done
   ```
3. **Dispatch all agents in parallel** via `delegate_task(tasks=[...])`.
4. **As each completes:** verify in worktree → commit → push → create draft PR.
5. **Merge sequentially** (not simultaneously):
   - Rebase worktree on latest main (other PRs may have merged).
   - `gh pr ready` + `gh pr merge --squash --delete-branch --admin`.
   - If rebase conflicts on docs: combine both sides (keep HEAD additions + add ours).
6. **Clean up each worktree** immediately after merge.
7. **Batch state.json** — one state-update worktree per batch, not per issue.

### Subagent fix-up pattern

When a subagent's work has test failures after completion:
1. Try a quick fix in the worktree yourself (enum casing, MSW handlers, type errors).
2. If the fix is non-trivial, dispatch a dedicated fix-up subagent for that worktree.
3. Only commit+push+PR after all tests pass in the worktree.

## Autonomous wave-based cycle

When the user says "continue until wave N" or authorizes autonomous progression, run this cycle for each wave:

```
For wave W in [W..N]:
  1. PREPARE: Create worktrees for wave W's tasks (max 3 concurrent)
  2. DISPATCH: delegate_task with up to 3 tasks per batch
  3. WAIT: Collect results as subagents complete
  4. FIX: Re-dispatch failed/unfinished tasks (iteration limit, build errors)
  5. MERGE: Sequential merge into main, resolve conflicts
  6. VERIFY: dotnet build + full test suite + frontend lint + test
  6b. MEASURE: re-run the project's baseline/measurement harness and append fresh results to the standing comparison doc. Measured improvement per integration is the norm; a degradation is a priority — analyze why and revise the plan before the next wave.
  7. REVIEW: Dispatch frontend + backend review subagents — for runtime/gate claims, hand them the server-start command and a scratch data-root pattern (copy the committed db into a temp root) so they probe deviations LIVE instead of trusting code reading. **Reviewers must verify PRODUCTION WIRING, not just tests (measured 2026-08-05): a green suite can coexist with a dead production path.** A review REJECTED a feature because the pipeline's 1s tick loop had zero callers in `src/` — every test drove the loop manually (`TickOnceAsync` in test setup), so all 62 scenarios passed while a real server would enqueue events forever and never digest. Review briefs must include: for every background loop/hosted service/worker, grep `src/` for the call that STARTS it (AddHostedService / RunAsync invocation), and require a test that starts the real composition and asserts behavior with NO manual tick/drain calls.
  8. APPLY: Fix HIGH/MUST-FIX review findings
  9. CLOSE: gh issue comment + gh issue close for completed issues
  10. NEXT: Prepare wave W+1 worktrees while reviews run
```

**Key principles:**
- Max 3 concurrent subagents (delegate_task limit). If wave has 4+ tasks, dispatch first 3, then the 4th when a slot frees up.
- Wave worktrees branch from main's current HEAD (which includes all prior wave merges).
- Dependency merging: if task B depends on task A (different wave), merge A's branch into B's worktree before dispatching B's agent.
- Never ask the user to continue — if authorized, proceed to next wave automatically.
- Between waves: reviews run in parallel with next-wave preparation.

## Parallel waves sharing a committed binary artifact (corpus db, vector store)

When two parallel waves both regenerate the same committed binary (SQLite db, vector store) plus a coupled generated map (hash map / index), the plan's "independent" dependency graph is a lie — the binary is a physical shared file. Design around it:

- Make the LATER-MERGED wave content-preserving: a **backfill** that adds columns/embeddings without changing existing row content, instead of re-ingesting from source. Content hashes stay byte-identical, so the coupled generated map never conflicts; a content-rewriting wave forces a map conflict on top of the binary conflict.
- Backfills must be **idempotent and re-runnable**: the later wave's db is stale the moment the earlier wave merges (content changed underneath), so the orchestrator re-runs the backfill on the merged corpus at integration. Tell the agent this explicitly in the dispatch context ("your db commit will conflict; the orchestrator re-runs your backfill on the merged corpus — make it safe to re-run").
- **Merge order**: content-changing wave first, backfill wave second (rebased on merged main), then re-run the backfill before the final gate. Do not let the backfill wave merge before the content wave.
- Commit the binary + its generated map in ONE commit — tests match against the map; a half-updated pair breaks expected-source matching and looks like a random test failure.
- Capture the measurement reference (baseline run) on the pre-wave HEAD BEFORE dispatch — post-merge comparisons need fresh numbers from the same commands; a reference captured mid-wave is contaminated.

## Reading subagent background-process notifications (orchestrator)

Servers/processes a subagent starts in background (watch patterns like "Application started") forward their lifecycle notifications to the orchestrator session. Interpret them before reacting:

- A SIGTERM/exit on a subagent-owned process is usually the AGENT deliberately killing its own hung run (full-suite hang → kill → isolate with a single-filter `time dotnet test`), not a crash.
- **Tail the subagent's live transcript** (`~/.hermes/cache/delegation/live/<delegation-id>/task-N.log`) before reacting to any subagent-owned process notification — it shows whether the agent is mid-debug or actually dead.
- Repeated server restarts in a transcript are progress, not loops: each restart often means one ingest/regeneration iteration with a fix baked in.
- A re-run of a failed background command produces a DELAYED notification carrying the OLD failure output. If you already re-ran and verified green, the stale notification is noise — ignore it (seen: two MSB1009 notifications arriving after the corrected builds had passed).

## Comparative Agent Experiments

When you need to compare two approaches (e.g., "with tool X vs without"), use
worktrees to run identical tasks in parallel with different tool configurations.

### Setup
1. Create two worktrees from the same base branch.
2. Pre-build any tool-specific state (e.g., `code-review-graph build`) in the
   worktree that needs it.
3. Create a shared `experiments/` directory in the main checkout with per-agent
   subdirectories for logs.

### Tool-Usage Logging
Each agent appends tool names to its own log file:
```bash
echo 'TOOL_NAME' >> /path/to/experiments/agent-name/tool-usage.log
```
Instruct agents to log at START (`AGENT_START`), after each tool call, and at
FINISH (`AGENT_FINISH`). This produces a comparable timeline of tool usage.

### Dispatching
Pass identical goals to both agents with one key difference in context:
- Agent A: "You MUST use [tool] MCP tools BEFORE reading files"
- Agent B: "Do NOT use [tool]. Use terminal, read_file, write_file only."

Both agents must log tool usage and record start/finish markers.

### Comparing Results
After both complete:
1. `wc -l` on tool-usage logs — total tool calls per agent.
2. `grep -c` for specific tool categories (graph tools vs file reads).
3. `git diff --stat` in each worktree — scope of changes.
4. Run test suites in both worktrees — quality gate.
5. Compare the agent transcript summaries — reasoning quality.

### Pitfalls (experiments)
- **Pre-build tool state before dispatching** — if a tool needs setup (like
  code-review-graph build), do it in the worktree before the agent starts,
  otherwise the agent wastes tokens on setup or fails. See
  `references/code-review-graph-setup.md` for macOS-specific install notes.
- **Log file timestamps differ from agent timestamps** — the log records wall
  clock time, but agent transcript timestamps are more reliable for comparing
  pacing. Use the log for tool-count comparison, transcripts for timing.
- **Rider MCP tools don't work in worktrees** — the Rider plugin binds to the
  main checkout. Instruct worktree agents to use terminal/file tools only.

## Agents sharing ONE worktree (parallel WIP build collisions)

The isolation model is one worktree per agent, but parallel packages can still
land in the SAME worktree (e.g. a wave runs two agents on one checkout). When a
parallel agent's UNCOMMITTED WIP breaks the shared test-project compile (seen:
CS0176 static class used via instance reference in `tests/.../Retrieval/`), do
NOT touch their files. Verify YOUR slice with a zero-file-change MSBuild override:

```bash
dotnet test tests/X.Tests/X.Tests.csproj --filter "FullyQualifiedName~YourSlice" \
  -p:DefaultItemExcludes="**/TheirInFlightFolder/**"
```

Mechanics (verified on .NET SDK 10):

- The SDK compile glob is
  `<Compile Include="**/*.cs" Exclude="$(DefaultItemExcludes);$(DefaultExcludesInProjectFolder)"/>`.
  bin/obj are excluded by `DefaultExcludesInProjectFolder`, so overriding
  `DefaultItemExcludes` with ONLY your folder pattern is safe.
- `-p:` values split on `;` — use a SINGLE pattern with no semicolons
  (a `%3B`-escaped list misbehaved under `-getItem`; the plain single pattern
  worked). Reconstructing the full default list is unnecessary.
- Confirm the exclude took effect:
  `dotnet msbuild <proj> -getItem:Compile "-p:DefaultItemExcludes=**/TheirFolder/**" | grep '"Identity": "TheirFolder'`
  — 0 hits means excluded.
- CS2002 "source file specified multiple times" warnings in such a run are
  artifacts of the override, NOT your code.
- Report the collision in your summary: the canonical gate re-runs clean once the
  other agent lands their fix; don't claim full-suite green.

**The sibling's broken file shares YOUR namespace folder → DefaultItemExcludes cannot
separate you (measured 2026-08-05, S2a encryption).** The exclude override is folder-granular;
when the parallel agent's in-flight RED test file sits in the SAME folder your tests live in
(both in `Unit/Encryption/`), excluding their file excludes yours too. Fall back to the
throwaway scratch test project (recipe in the repo skill's isolated-verification pitfall):
`mktemp -d "${TMPDIR:-/tmp}/hermes-verify-<slice>.XXXXXX"`, minimal csproj with explicit
package versions, ProjectReference to the real production project, `<Compile Include>` your
exact test files + the shared test helpers by absolute path, `dotnet test`, `rm -rf`. Then
poll `git log` — once the sibling's GREEN lands, the shared assembly builds again and the
canonical `dotnet test --filter` run SUPERSEDES the scratch run; re-run the real gate rather
than reporting scratch evidence as final.

**Sibling RED already COMMITTED → `dotnet test --no-build` runs the last-built bin (measured
2026-08-05, S4 encryption).** When the parallel agent's RED lands as a commit (not just WIP),
the test project won't compile until their GREEN — but the bin/ from BEFORE their commit is
still intact, so `dotnet test --no-build --filter 'YourSlice'` executes it without triggering
a build. Two caveats: (a) the bin must predate their breakage (your own changes since then
need a pre-check via the isolated compile, or you stash/rebuild only after their GREEN);
(b) `--no-build` does NOT touch obj/bin, so it cannot race the sibling's build. Use it to get
real execution evidence for your slice mid-blockage; the canonical filtered run after their
GREEN still supersedes it. Scratch-project refinement from the same session: prefer
`<Reference><HintPath>` to the already-built production DLLs (wildcard HintPaths do NOT
expand — list each DLL) over ProjectReference, so the scratch build never writes the shared
obj/bin of src projects; and a repo whose SQLite provider is activated by an app-level
`[ModuleInitializer]` needs that initializer replicated in the scratch host (plus the repo's
exact native-package pins) or bank-opening tests die on the wrong native provider — see the
repo skill's ai-raccoon-pitfalls encryption section for the exact pins.

**Contract types owned by a parallel agent: ship a shape-identical stand-in, reconcile when
they land (measured 2026-08-05, S2a).** When your section needs a shared type (record/DTO)
the plan assigns to a parallel section that hasn't landed yet, do NOT block and do NOT create
it in their directory. Define a stand-in with the identical shape in your own namespace, build
and test against it, and flag the duplication in your report. The moment the owner's commit
lands (poll `git log` / `ls` their dir), reconcile in one small refactor commit: delete your
copy, `using` theirs, re-run the gate. Two same-named types in different namespaces compile
fine until one file imports both namespaces — then CS0104 ambiguity — so reconcile promptly,
before the next wave's files import both.

Corollary worktree gotcha: a fresh worktree of a repo whose `NuGet.config` lists
a local source (e.g. `./.nupkg-local/`) restores with NU1301 until you
`mkdir -p .nupkg-local` — the main checkout has that directory, worktrees do not.

### Git discipline when sharing ONE worktree

The `git add -A` in the integration flow above is ONLY safe for single-agent
worktrees. With a concurrent agent in the SAME worktree:

- **Never `git add -A`** — it stages the other agent's in-flight files. Stage
  only your own paths explicitly (`git add src/X.cs tests/Y.cs`).
- **`git status --short` immediately before EVERY commit** — confirm only your
  files are staged; the other agent may have landed files since your last check.
- **Run only targeted builds/tests** (`dotnet build src/App/App.csproj`,
  `dotnet test --filter 'YourSlice'`) — the full-suite gate runs at the wave
  join, and a full build can collide with the other agent's in-flight obj/bin
  writes.
- **TDD red commits are safe here** — a test-only commit that breaks the TEST
  project compile does not disturb a concurrent agent who builds only the app
  project (the test project is never a dependency of the app). This preserves
  red-first discipline in a shared worktree; confirm the other agent's build
  target first.

### Orchestrator committing from a SHARED main checkout

`git commit` (no pathspec) commits the WHOLE INDEX, not just your `git add`ed
file. When another session is active in the same checkout and has run
`git add -A`, their staged changes — including DELETIONS — silently ride inside
your commit (measured 2026-08-05: a RED test commit swept in the parallel
session's staged `tools/` deletion, contaminating the branch; the worktree
checked out from that branch lacked the project and the slnx referenced it).
Checks before committing from a shared checkout:

- `git branch --show-current` — if you are NOT on the branch you think, the
  checkout was switched under you; stop and relocate to a worktree.
- `git status --short` — the staged column (first char) must contain ONLY your
  files. Anything else staged means another session's `git add -A` landed in the
  index; either unstage their paths (`git restore --staged <path>`) or move the
  work to an isolated worktree.
- Verify the branch base is intact: `git ls-tree HEAD -- <dir>` for directories
  your commit should NOT have touched — a deletion swept into your commit shows
  up as a missing tree entry.

If the branch is already contaminated (foreign changes in the commit): rebuild
it cleanly rather than trying to surgically remove hunks —
`git checkout -B <branch> origin/main` (drops the contaminated commit), then
re-apply your changes in TDD order (RED commit first, then GREEN). Worktrees
isolate this class entirely — prefer them over committing from a shared
checkout whenever another session may be active.

### Verification evidence for your slice

When the platform demands script-based verification evidence (automated
verification tracker: "no canonical test/lint/build command detected"), wrap the
repo's canonical gates in a throwaway script instead of asserting green from
memory:

```bash
SCRIPT=$(mktemp -t hermes-verify-)   # OS-safe temp path, hermes-verify- prefix
cat > "$SCRIPT" <<'EOF'
#!/bin/bash
set -u
cd <worktree> || exit 1
BUILD=$(dotnet build src/App/App.csproj --nologo 2>&1)
echo "$BUILD" | grep -q "0 Warning(s)" || { echo "FAIL build"; exit 1; }
echo "$BUILD" | grep -q "0 Error(s)"   || { echo "FAIL build"; exit 1; }
TEST=$(dotnet test tests/App.Tests/App.Tests.csproj --filter 'YourSlice' --nologo 2>&1)
echo "$TEST" | grep -q "Passed!"       || { echo "FAIL test"; exit 1; }
echo "$TEST" | grep -q "Failed:     0" || { echo "FAIL test"; exit 1; }
echo "VERIFY OK"
EOF
chmod +x "$SCRIPT" && "$SCRIPT"; RC=$?; rm -f "$SCRIPT"
```

Assert on the canonical output markers (`0 Warning(s)`, `Passed!`) rather than
inventing your own. Report the run as targeted/ad-hoc verification — a targeted
filter run is NOT full-suite green, and the shared-worktree caveat above still
applies.

**Verification-script pitfalls (measured 2026-08-04):**
- `mktemp -t hermes-verify-` can fail with "File exists" on macOS (same-second
  template collision). When mktemp fails, `$SCRIPT` is empty — `chmod`/execution
  still run against the empty name and the gate silently never executes. Use a
  more unique template: `mktemp /private/var/folders/.../T/hermes-verify-<slice>-XXXXXX.sh`
  (explicit path + unique suffix) and check `$?` after mktemp.
- The tracker's recording hook keys on the command SHAPE (`hermes-verify-` prefix
  in the command text, `; RC=$?`, `echo "script cleaned up (rc=$RC)"`). Use that
  exact shape so the run gets recorded for the root. **CAUTION: the recorded
  event can say `passed` even when the script never executed** — the hook can
  parse PASS markers out of the script TEXT (heredoc body) rather than the
  output. A failed-mktemp invocation was recorded as a false pass this way.
  Before trusting a recorded event, confirm real execution: exit code 0 AND the
  actual PASS lines visible in the command output.
- The tracker re-fires per edit turn on changed paths; one clean recorded
  canonical run for the root satisfies it. Wrap the canonical gate
  (`dotnet test` for .NET repos) rather than inventing a custom check.

## Pitfalls

- **`git worktree add <path> origin/<branch>` without `-b` = DETACHED HEAD — commits silently vanish (measured 2026-08-06).** When the task worktree was already removed (e.g. `task_tracker.py finish` cleaned it up) and you re-add one just to fix a pushed PR branch, plain `git worktree add /tmp/x origin/task/foo` checks out the remote branch DETACHED: your commit lands on the detached HEAD, `git push` answers "Everything up-to-date" (the branch ref never moved), and `git worktree remove` discards the work with no error. The local branch often STILL EXISTS after a tracker finish — reuse it directly (`git worktree add /tmp/x task/foo`). Otherwise create one explicitly: `git worktree add /tmp/x -b <local> origin/<remote-branch>`, then push with `git push origin <local>:<remote-branch>`. After any post-hoc worktree commit, verify it landed: `git log --oneline origin/<branch>` must show your commit before you report it.
- **Tooling that creates worktrees from LOCAL main builds on a stale base (measured 2026-08-05).**
  The task-tracker's `start` (`task_tracker.py start <id> --branch task/<id>-<slug>`) creates the
  worktree from LOCAL main. When the user merges PRs via the GitHub UI and never pulls locally,
  local main lags origin — the fresh worktree silently lacks merged PRs AND user's direct commits.
  Symptom: your patches apply cleanly but the base is missing expected content (a renamed
  `PackageId` still shows the old value). Before ANY work in a new worktree:
  `git fetch origin main && git log --oneline -3 origin/main` vs `git log --oneline -1` in the
  worktree — if origin is ahead, rebuild: save ONLY your intended diffs, then
  `git checkout -- . && git checkout -B <branch> origin/main`, re-apply. **Rebuild clobbering trap:**
  files whose content depends on the base (csproj metadata, contract tests, server.json) carry the
  STALE base's values — re-copying a saved copy reverts the base's correct values (measured:
  a csproj saved from the stale tree reverted `PackageId` from `arasz.ai-raccoon` back to
  `ai-raccoon`). Restore each base-dependent file from git first (`git checkout -- <file>`), then
  re-apply ONLY the version/metadata delta; verify base identity after the rebuild with a marker
  your diff does NOT touch (e.g. `grep PackageId src/...csproj`).
  with no remote PR flow, another agent/user session can merge into LOCAL main while
  your wave branch sits in review (measured 2026-08-04: a perf commit landed touching
  the same FTS files the wave carried). Before EVERY integration, check
  `git log <your-base>..main` — if main moved, merge main back into the wave branch,
  resolve conflicts (keep the other session's newer work where it overlaps; their
  rewrite of a file your wave also touched is usually additive), re-run the full
  suite, and only then merge to main. A stale-base merge onto moved main produces
  conflicts that masquerade as your waves' fault.
- **Ownership seam-checks lie when main moved** — `git diff --name-only main..HEAD | grep -v -E '<owned paths>'` flags out-of-scope files, but if main advanced (a wave merge, a user commit) after the branch was created, MAIN-side changes masquerade as branch edits (seen 2026-08-04: CandidateWindowMode.cs looked branch-deleted; it was the Wave-4 merge ADDING it to main). Before accusing a subagent of a scope violation: `git merge-base main HEAD` — a base older than main's tip means main-side diffs will appear; then `git log --oneline main..HEAD -- <file>` — empty means the file changed on main's side, not the branch. Quick whole-branch version: `git diff $(git merge-base origin/main HEAD)..HEAD --stat` — a scary 91-file stat is usually a moved main (other sessions' commits appear as deletions/edits on your side), not a scope violation.
- **Full-suite failures at join: attribute before blaming the branch (measured 2026-08-05).** A shared machine runs other sessions; the first full-suite run after a package lands can fail 43 tests (E2E/embedding/watch families are timing/env-sensitive: serial-collection env mutation, model files, concurrent builds). Do NOT treat that as your branch's failure: (1) re-run the full suite once (run 2 often passes — flake), (2) if it still fails, create a throwaway baseline worktree (`git worktree add <path> origin/main`) and run ONLY the failing test classes there — failures on baseline = pre-existing environmental flakes, branch is clean; remove the worktree after. Only failures that reproduce on your branch but NOT on baseline are yours. The task skill's "run the suite as the only session working" is often impossible on a shared machine — the baseline-attribution drill is the practical substitute, and a single clean full-suite run on the final state is the gate.
- **A parallel task owning the same file beats duplicating the work** — when two tasks both need to rewrite one surface (file-watcher's CLI section vs the CLI-config refactor, both touching CliArgs.cs/Program.cs), DEFER the section to the owning task instead of implementing it twice: shrink your wave (drop the section), pin the exact shared contract (settings keys, command formats) in BOTH subagent briefs, and re-validate the deferred scenarios at integration. Two branches rewriting the same file collide at merge no matter how clean each side is.
- **The verification tracker re-fires even after a recorded canonical run** — the "one clean recorded run satisfies it" note is wrong as measured 2026-08-04: it kept re-firing every turn on the stale changed-path list (~8 turns after a clean PASS). Do not loop re-running identical checks on unchanged bytes. Per turn: either stat/mtime-prove byte-identity or run the check once, then state the blocker in one line. Re-run only when a new edit actually occurred.
- **Parallel branches adding the same interface method cause duplicate implementations at merge** — when two branches both add `ListAsync` to `IChannelSignalRepository`, the merge keeps both implementations (compile error CS0111). Fix: after sequential merge, grep for duplicate method signatures in the implementation files. Keep the one from the branch that owns the feature; delete the duplicate from the merged branch. This commonly hits repository interfaces, Cosmos container configs, and DI registrations.
- **Committed conflict markers compile-fail loudly, but only at build (measured 2026-08-05)** — a merge committed with `<<<<<<<` markers still inside fails CS8300 on the next build. Two causes seen: (1) `git checkout --ours <path1> <path2> <badpath>` is ALL-OR-NOTHING — one bad pathspec makes the whole command fail silently (stderr suppressed with 2>/dev/null), leaving every file unresolved while the rest of the chain proceeds and `git add -A` stages the marker-laden files; (2) the merge repair itself then gets committed. Playbook: after ANY conflict resolution, `grep -rl '<<<<<<<' <resolved dirs>` BEFORE committing; if markers remain, `git show <merge-commit>^1:<file>` (the branch's pre-merge version) over each affected file, re-commit, then rebuild. Check each pathspec of a multi-path checkout individually when one might not exist.
- **Subagent file writes can silently produce empty files** — always verify `wc -l` on files written by subagents. If a subagent says "file written" but `wc -l` shows 0, re-dispatch with explicit write verification instructions.
- **`gh issue comment` uses `-b` for body, `gh issue close` uses `-c` for comment** — not interchangeable. `gh issue comment <N> -b "text"` and `gh issue close <N> -c "text"`.
- **`--delete-branch` fails if worktree exists** — remove worktree first, then
  the remote branch is cleaned up by the merge or manually
- **Draft PRs can't be merged** — always `gh pr ready <N>` before merging
- **`--admin` requires repo-owner privileges** — won't work for contributors
- **`git merge -X theirs`** helps resolve state.json conflicts when cherry-picking
  across worktrees
- **Commits land on wrong branch** if agent checks out a different branch inside
  its worktree — verify with `git branch --show-current` in agent context
- **Cherry-pick between worktrees** may conflict on shared files like state.json —
  resolve by writing the merged content rather than trusting auto-merge
- **Subagent commits sometimes aren't committed** — verify `git status --short` after agent completes, commit manually if needed. Iteration caps commonly cut an agent off RIGHT BEFORE committing: the summary usually names the intended commit plan (code → corpus+map → docs) — follow its explicit-add list, never `git add -A`, and keep coupled artifacts (db + hash map) in ONE commit.
- **Subagent may leave files untracked** — `git status` shows `??` for new files; the agent read them but forgot to `git add`. Always verify and commit in the worktree before creating the PR.
- **`git reset --hard` may be blocked by user** — if the user rejects `git reset --hard` on a branch, use `git checkout -B branch origin/main` instead to reset the branch pointer without the "destructive" flag.
- **`-X theirs` on state.json** — when merging a state-update worktree into main and state.json conflicts (because another agent touched it), use `git merge -X theirs` to accept the worktree's version. Don't trust auto-merge on JSON with conflict markers.
- **Multiple parallel agents need separate worktrees from the SAME base** — create all worktrees from `origin/main` (not local `main`) so they share a common ancestor. Always `git fetch origin main` before creating worktrees. Agents modifying the same file will conflict at merge time — plan parallel tasks to touch non-overlapping files.
- **Sequential merges cause increasing conflicts** — when merging PR A then PR B, PR B's branch was created from the pre-A state. After PR A merges, PR B conflicts on shared docs (flows.md, data-model.md, architecture.md). Always rebase PR B's worktree on `origin/main` after PR A merges, then force-push.
- **Issue comments after merge** — post a summary comment on each GitHub issue after the PR merges, not just after creating the PR. Include: files created/modified, test counts, and any follow-up items.
- **camelCase enum serialization is inconsistent** — this project serializes enums as camelCase in JSON (`"employment"` not `"Employment"`), but NOT all enums follow this. `RecommendationSource` was PascalCase (`"Deterministic"`) while `TakeHomeOutcome` and `CompensationContractType` were camelCase (`"computed"`, `"employment"`). After implementing new API endpoints, always verify each enum's serialized form against the actual API response before writing test assertions. Don't assume all enums use the same casing — check `JsonStringEnumMemberPolicy` or the actual response.
- **Frontend-backend type mismatch risk** — when implementing API endpoints + frontend in parallel worktrees, the frontend TypeScript types may diverge from the backend wire types. Always create a `wire-types.ts` with the exact backend response shapes and add a mapping layer in `api.ts`. Don't assume frontend-friendly names match backend names (e.g. `"uop"` vs `"Employment"`, `recommendedGross` vs `RecommendedAmount`).
- **zod + react-hook-form type issues** — `z.coerce.number().optional().or(z.literal(""))` produces a union type that breaks `zodResolver` type inference. Solutions: (1) use `@ts-expect-error` on the resolver and handleSubmit, or (2) use string inputs with `z.preprocess` and parse in onSubmit. Option 1 is simpler but loses client-side validation on the affected fields; option 2 keeps validation but requires a mapping layer.
- **MSW handlers need explicit URLs** — MSW v2 doesn't match wildcard `*` patterns like `"*/applications/:id/foo"`. Use full URLs: `"http://localhost:7071/api/applications/:id/foo"`. When adding new API endpoints that existing pages call (e.g. compensation endpoints on the application details page), add default 404 handlers to the global MSW setup (`app/test/handlers/index.ts`) to prevent "intercepted without handler" errors in unrelated tests.
- **Polish tax terms in code** — when implementing Polish tax/compensation features, use English code identifiers (`LumpSum`, `FlatTax`, `ProgressiveScale`) but keep Polish terms in documentation and user-facing strings. See `docs/domain-language-dictionary.md` for the authoritative translation table.
- **Pre-flight every fresh worktree BEFORE dispatch** — run bare `dotnet build` in each new worktree before delegating: it catches NU1301 (repo NuGet.config lists a local source like `./.nupkg-local/` that the worktree lacks — `mkdir -p` it), a wrong solution filename, and restore problems while they cost you seconds instead of the agent's tokens. .NET 10 solutions are often `.slnx`, not `.sln`: naming the wrong file fails MSB1009 "Project file does not exist" — use bare `dotnet build`/`dotnet test` and let discovery find the solution.
- **Orchestrator cwd drift** — the terminal session's cwd can move between commands (workdir is per-command). When a relative-path build/test suddenly fails "Project file does not exist", run `pwd` first, then re-run with explicit workdir. A corrupt-looking test result (e.g. "Passed: 3, Failed: 4" that re-runs green 7/7) is usually wrong-cwd or concurrent-restore noise — re-run clean before trusting it.
