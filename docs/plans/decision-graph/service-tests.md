# Decision service test design

Oracle: the reviewed data-model.md and api.md contracts. Real SQLite decision store in unique temporary directories; fixed FakeTimeProvider; no external network or process environment; record IDs are asserted by relationships, never predicted. The deterministic HTTP/MCP boundary will enforce registration/access before this internal service. Tests use project-qualified records to expose ownership leaks.

| Behavior | Failure mode | Mutation or failure injection |
|---|---|---|
| Same request replays before stale-version check | Retry creates a duplicate or conflicts | Move replay lookup after callback |
| Different payload with same request refuses | Replayed request changes meaning | Skip payload-hash comparison |
| Concurrent amendments have one winner | Lost update | Skip expected-version check |
| Supersession atomically versions target and adds edge | Half supersession | SQLite trigger aborts target version after edge insertion |
| Last-task ordering includes empty tasks | Latest silently chooses an older task | Filter tasks through decision origin join |
| Amendments/link removals select source task touches | Old decisions disappear from current work | Ignore version/removal task membership |
| Snapshot export closes edges and carries history/import/task provenance | Dangling or lossy export | Remove boundary/history/import projection |
| Preview keeps Unicode and reports task/decision/edge omissions | Output overflows or hides missing data | Disable budget/footer accounting |
| FTS query and precedence use current-version fields | Stale rationale retrieved, false semantic claims | Leave old current-search row or remove status/validity filter |
| Traversal preserves direction and bounds | Unbounded graph or inverted causality | Reverse edge selection or skip visited/node budget |

Write behavior-focused tests before each service behavior and record executed red/green evidence. General query has project-wide default; export/preview default to latest registered task.
