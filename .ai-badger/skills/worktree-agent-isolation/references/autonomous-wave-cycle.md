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
  7. REVIEW: Dispatch frontend + backend review subagents — for runtime/gate claims, hand them the server-start command and a scratch data-root so they probe LIVE. **Verify PRODUCTION WIRING, not just tests: a green suite can coexist with a dead production path** (a pipeline loop with zero callers in `src/` passed 62 scenarios driven manually). Briefs must require: for every background loop/hosted service, grep `src/` for the call that STARTS it, and a test that starts the real composition with NO manual tick/drain calls.
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
