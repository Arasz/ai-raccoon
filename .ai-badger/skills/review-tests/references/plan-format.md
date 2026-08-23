# Plan format — the review's one output shape

`SKILL.md`'s `## The improvement plan` points here rather than repeating this table; read this
file before writing the first row of a review's output.

## The table

One table, columns fixed, in this order:

| # | Finding | Rule | Sev | Evidence | Fix | Gate that proves the fix |
|---|---|---|---|---|---|---|
| 1 | `Failure_parks_awaiting_user` asserts the flag, never that anything parked | `T1-SCO-04` | blocker | `AutoApplyTests.cs:214` | assert the application's state is `AwaitingUser`, not only `InterventionRequired == true` | delete the state write at `AutoApply.cs:88`; the test must redden — `verified` / `unverified (static reasoning)` |

## Four rules, always

1. **Every row carries a location, or it is deleted.** A finding with no `path:line` is an
   impression, not a finding — `code-review-checklist`'s house rule, carried here unchanged.
2. **The gate column names the exact edit.** Never "add a test" — the literal production line to
   change or delete, plus the command that must redden then green around it, marked `verified`
   only once that edit was actually applied and run.
3. **The summary's counts equal the enumerated rows.** A summary reading "2 blockers" means the
   table holds exactly two rows marked `blocker`, recomputed after every edit to the table — not
   once at the end.
4. **Everything the suite could not be run against is labelled `unverified (static reasoning)` —
   in the row itself, and again in the summary line.** This is the single most common failure of
   a review skill: telling someone their tests are weak when nobody ran the suite against the
   finding. A summary reading "3 blockers" while one of the three rows is itself `unverified
   (static reasoning)` violates rule 3 as much as rule 4.

Lead the plan with what the suite already does well, before the table. Inflating severity, or the
row count, to make a thin review look thorough is itself a finding against the review.

## Handing a row to `/task`

Each row becomes one work package, a short section directly under the table — not a JSON
manifest. `spec.json` exists because `task` consumes it; there is no consumer for a plan manifest
here, and inventing one would be the over-engineered version:

```
### WP1 — assert the parked state, not the flag beside it   [blocker, T1-SCO-04]
acceptance: AutoApplyTests asserts the application's state after a failed auto-apply.
gate:       dotnet test --filter "FullyQualifiedName~AutoApplyTests"
proof:      delete the state write at AutoApply.cs:88 → the test reddens; restore → green.
```

That satisfies `proof-of-done` (acceptance criteria and the gate named before the work starts) and
`prove-the-check-fails` (the gate has a defect put in front of it) without a new file format.

## Gotchas

- A `Gate` column entry names a command that was actually run, not a promise it would work — write
  `unverified (static reasoning)` rather than guessing which word the row would otherwise carry.
- A row deleted for having no location is not "softened" to `minor` instead — it is removed from
  both the table and the count, because a location-less row was never a finding.
