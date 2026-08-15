# 0052. The workspace lifecycle is a write, not a destruction

Date: 2026-08-15

Status: Accepted

## Context

`AccessRequirement.Destructive` requires mode `full`. Six tools asked for it:
`memory_delete`, `memory_delete_context`, `memory_sweep` (non-dry-run), `memory_set_ttl`, and —
`memory_workspace_consolidate` and `memory_workspace_discard`.

The last two are the ordinary workspace lifecycle. `memory_workspace_begin` opens a sandbox at `rw`;
both ways of closing one demanded `full`. So at the default mode an agent could **start** a workspace
and then neither finish nor abandon it.

That pushed installs to `access.mode.global = full` just to use workspaces — which is what the
deployed bank was set to when the 2026-08-14 project-scope review found a cross-project delete
reachable at exactly that mode (ADR-0051). **The miscategorisation was manufacturing the precondition
for the blocker.**

Neither operation can reach committed memory:

- `WorkspaceService.ConsolidateAsync` promotes selected hashes from `workspace:<id>` into the
  **caller's own project** — a write, by any reading.
- `WorkspaceService.DiscardAsync` calls `DeleteContextAsync(projectId, "workspace:<id>")`, and that
  filter binds **both** `workspace_id` and `project_id`. It deletes uncommitted sandbox rows the
  caller created, and nothing else. The nearest analogue is `git stash drop`, not `rm`.

## Decision

**`memory_workspace_consolidate` and `memory_workspace_discard` require `Write`, not `Destructive`.**

`Destructive` keeps its meaning: reaching *committed* memory — `memory_delete`,
`memory_delete_context`, `memory_sweep`, `memory_set_ttl`. A sandbox the caller opened and never
committed is not committed memory.

`ro` still refuses both, and a test pins that — relaxing to `Write` must not relax to `Read`.

## Consequences

- Workspaces are fully usable at the default `rw`: begin, consolidate, discard.
- `full` is no longer required for everyday work, so an install can run `rw` globally and grant `full`
  per project only where a manual delete or sweep is genuinely wanted.

- **Release ordering, learned the hard way: the mode switch must follow the install, never precede
  it.** Setting the deployed bank to `rw` while the *installed* binary was still 1.12.0 broke
  workspaces immediately, and probing it rather than reasoning about it produced the cleanest
  statement of the defect this ADR exists to remove:

  ```
  memory_workspace_begin   -> {"workspaceId":"01a002dd…"}          # opened fine at rw
  memory_workspace_discard -> access-denied: memory_workspace_discard
                              requires mode full (current rw)      # and cannot be closed
  ```

  A workspace that can be opened and not closed, in one round trip. The bank was restored to `full`
  within about ninety seconds and the orphaned workspace discarded. **Switch the mode after
  `dotnet tool update -g ai-raccoon` reports 1.13.0, not before.**
- **Automatic sweeping is unaffected.** `SweepHostedService` does not go through the tool gate, and
  says so at `:110-111`: "a timer has no caller to gate in the first place." Only the agent-invoked
  `memory_sweep` tool needs `full`.
- `SECURITY.md`'s mode table needed no change in meaning, only in accuracy — `full` still means
  "destructive operations", the set is just correct now.

## Evidence

Red-first: `RwMode_WorkspaceConsolidate_IsAllowed` and `RwMode_WorkspaceDiscard_IsAllowed` both failed
with `AccessDeniedException` before the change; `RoMode_WorkspaceDiscard_IsStillRefused` passed
throughout, so the relaxation is bounded. Green after, with the surrounding 149 workspace and
access-mode tests unchanged.

`src/AiRaccoon/Tools/WorkspaceTools.cs`, `src/AiRaccoon.Infrastructure/Workspace/WorkspaceService.cs`,
`src/AiRaccoon.Core/Access/AccessModePolicy.cs`.
