# Gaps Resolution Workflow

Reusable pattern for analyzing a known-gaps/issues doc, designing fixes, getting
subagent review, and implementing.

## Flow

1. **Read and classify** each gap:
   - STILL VALID → actionable
   - PARTIALLY FIXED → check what's done, design remainder
   - DEFER → document why (complexity vs ROI)
   - SKIP → growth items, project-specific, or ongoing

2. **Design implementation** for each actionable gap:
   - Problem statement (1-2 lines)
   - Design (what to change, where, edge cases)
   - Files to modify/create
   - Test strategy

3. **Parallel subagent review** — dispatch 2 subagents:
   - **Technical feasibility**: "Read the plan, check actual code. For each task:
     (1) Is the design correct? (2) Edge cases? (3) Scope right? (4) Risks?"
   - **Scope/priority**: "Are DEFER/SKIP decisions correct? Execution order?
     Dependencies between tasks? Add/remove from scope?"

4. **Integrate review feedback** into revised plan. Common findings:
   - Missing items from lists (e.g., forgot a schema file)
   - Flag interaction issues (e.g., --execute vs --no-install)
   - Path bugs (e.g., ${CLAUDE_PLUGIN_ROOT} doesn't work in scaffolded projects)
   - Test design issues (e.g., "mock network calls" when there are none)

5. **Create branch** and implement in order (quick wins first):
   - Mechanical fixes (schema updates, path fixes)
   - Medium complexity (new scaffold methods, CLI flags)
   - Integration tests (depend on earlier fixes)

6. **Update the gaps doc** after all changes — mark resolved items, update status.

## Subagent prompt templates

Technical review:
```
Review the technical feasibility of the plan at <path>. Read the plan, then check
the actual code at <key files>. For each task, assess: (1) Is the design technically
correct? (2) Are there edge cases missed? (3) Is the scope right or too ambitious?
(4) Any risks? Return a structured review with specific actionable feedback per task.
```

Scope review:
```
Review the scope and priority of the plan at <path> and the original gaps at <doc>.
Evaluate: (1) Are the DEFER decisions correct? (2) Is the SKIP reasoning sound?
(3) Is the execution order optimal? (4) Dependencies between tasks? (5) Should
anything be added or removed from scope? Return a structured review with recommendations.
```
