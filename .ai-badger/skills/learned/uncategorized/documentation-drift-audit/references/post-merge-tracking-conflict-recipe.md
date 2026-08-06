# Post-merge tracking-file conflict recipe (memory-grade audit, 2026-08-05)

Worked example for the Phase-5 post-merge audit when the fast-forward is blocked by
a locally-modified tracking file that the merge commit also modifies. Repo:
ai-badger, PR #304 (0.79.0 memory-grade hook), tracked file `.ai-badger/state.json`.

## Setup commands

```bash
git log --oneline -1                 # premise said b643786; reality: e2488b1 (behind)
git log --oneline -1 origin/main     # b643786 — premise stale, ff needed
git diff e2488b1 b643786 --stat -- .ai-badger/state.json   # merge touches it (22 lines)
git stash push -- .ai-badger/state.json                    # stash ONLY the tracking file
git merge --ff-only origin/main      # clean ff (state.json out of the way)
git stash pop                        # CONFLICT: the merge also changed state.json
```

## Why "take the stash version" is wrong

The stash was authored against the pre-merge base (e2488b1): it carries the
orchestrator's local delta (memory-grade-hook completedTask + updated `next`
pointer) but LACKS the merge's own entry (ai-raccoon-integration, 0.78.0). The
merged version has that entry but the pre-implementation `next` pointer. Correct
final = merged content + local semantic delta. Verify the delta first:

```bash
git diff b643786 stash@{0} -- .ai-badger/state.json   # = exactly the local delta
```

## Reconstruction (JSON surgery, not text edits)

```python
import json, subprocess
merged = json.loads(subprocess.run(["git","show","HEAD:.ai-badger/state.json"],capture_output=True,text=True).stdout)
local  = json.loads(subprocess.run(["git","show","<stash-sha>:.ai-badger/state.json"],capture_output=True,text=True).stdout)
mg = [t for t in local["completedTasks"] if t.get("id") == "memory-grade-hook"]  # .get: one entry is keyed taskId
merged["completedTasks"].insert(0, mg[0])
merged["next"] = local["next"]
json.dump(merged, open(".ai-badger/state.json","w"), indent=2, ensure_ascii=False)
```

Then clear the unmerged state without leaving the file staged:

```bash
git add .ai-badger/state.json && git restore --staged .ai-badger/state.json
git status --short   # " M .ai-badger/state.json" — plain unstaged mod again
```

## Dropped-stash recovery

`git stash drop` before verification is recoverable: the drop removes only the
ref; the commit object persists until gc. `git show <sha>:<path>` (the sha is
printed by the drop) still returns the content. Recover and verify first, then
move on.

## Pending-PR check (prevents duplicated fixes)

Before editing pre-existing drift, check open PRs:

```bash
gh pr view 303 --json title,state,files   # "docs: post-#302 audit — README + skills.md drift fixes", OPEN
gh pr diff 303                            # covers README counts, bundled-MCP table, docs/skills.md counts+rows
```

Result: all 0.78.0-era count drift (README 13→14 default, mermaid "13 default",
bundled-MCP table missing hermes/ai-raccoon, docs/skills.md 21/13/7→22/14/8,
missing table rows) was already fixed in #303 — report as covered-by-#303, edit
nothing. Note the merge hazard: #303 also touches state.json → conflicts with the
feature merge's state.json change; needs a rebase.

## Expected mirror differences (not drift)

- `.ai-badger/hooks/hooks.json` vs `features/common/hooks/hooks.json`: the
  scaffold rewrites ${CLAUDE_PLUGIN_ROOT}/features/... commands into
  ${CLAUDE_PROJECT_DIR}/.ai-badger/... if-exists/elif/else fallback command
  shapes. Always differs by design; diff the shape, not bytes.
- `skills/<name>/SKILL.md` (plugin copy): frontmatter-only stub;
  `SKILL.full.md` holds the full content and equals the features copy.
- Root CLAUDE.md/HERMES.md/.hermes.md vs `.ai-badger/` sources: exactly a 2-line
  managed-by header prepend (comment + blank). `tail -n +2` misaligns — use a
  plain `diff`.

## Verified ground truth for the memory-grade audit (0.79.0)

- `badger_lib.SKILL_SCOPES`: 14 default / 8 optIn (22 skills incl. claude
  auto-wm) — the declaration, not sibling docs, is the source of truth.
- Changelog index row present and title matches the entry's `# ` heading.
- SKILL.md §8 (memory-grade, default off, `AI_BADGER_MEMORY_GRADE=1`) present in
  features + `.ai-badger` mirror + plugin `SKILL.full.md`.
- Wiring verified: hooks-manifest memory-grade entry (claude/copilot/hermes);
  hooks.json + `.claude/settings.json` `memory_search` PostToolUse matcher;
  `.github/hooks/ai-badger-hooks.json` postToolUse matcher; `ai_badger_hooks.py`
  lazy `memory_grade` import + `pop_ask`; `adjust_hooks.py` SHARED_SKILL_MODULES
  entry.
- Re-scaffold verdict: NOT needed (all mirrors regenerated inside the PR;
  `tooling/index_build.py --check` green).
