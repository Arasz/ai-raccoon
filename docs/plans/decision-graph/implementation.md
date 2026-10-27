# Decision graph implementation plan

Status: implementing in PR #923. Model and SQLite storage/schema v23 are complete and independently reviewed; service is in progress. Adapters, CLI, import and release remain pending. See implementation-evidence.md for executed checks.

## Deliverables and traceability

| Request or finding | Design | Implementation step |
|---|---|---|
| Native registration and graph ownership | data-model.md | model, storage, service |
| Data model before API | data-model.md then api.md | model → service → adapters |
| Agent tools and equivalent backend | api.md tool table | adapters |
| CLI export latest task / N / all | api.md CLI and selector | cli |
| Compact preview and minimal graph | api.md compaction | service, cli |
| ADR | ../../adr/0142-native-project-decision-graph.md | release |
| Review both designs | review.md | design review before runtime work |
| Semantica handover | api.md migration | migration, release |
| Scope, aliases, lifecycle, sync | data-model.md integration boundaries | storage, service, migration |

The full implementation DAG is recorded in task-graph under `air-native-project-decision-graph-implementation`. Its machine document is exported beside this file as `implementation-plan.json`; do not treat prose or checked planning milestones as completed runtime work. The graph is the current execution state; the exported snapshot is refreshed after recorded milestones. The task-tracker planning unit is `air-own-project-decision-graph-planning`.

## Execution order and ownership

| Step | Depends on | Owns | Acceptance gate |
|---|---|---|---|
| model | none | Core/Decisions types, ports and unit tests | Lifecycle, selector and edge invariant tests fail on illegal transition/cycle/selector mutation, then pass |
| storage | model | Sqlite/Decisions stores, MemorySchema migration, sync exclusions and storage tests | Fresh/legacy/encrypted/reopen/replay/rollback tests; cloud snapshot contains no decision tables on every path |
| service | storage | Infrastructure/Decisions service and FTS, service tests | Task touch membership, bounded traversal, atomic supersession, ownership and access tests |
| adapters | service | host Decisions protocol/endpoints/tools, DI and inventory tests | HTTP/MCP parity and actual tools/list schema/annotations; read/write/propose mode and authentication tests |
| cli | adapters | CLI decisions tree/commands/client/renderers and CLI integration tests | Real server-backed export/preview for defaults/N/all/empty/concurrent tasks and atomic output failures |
| migration | cli | import adapter, repair/delete integration and fixtures/tests | Dry run/apply/replay counts, source preservation, conflicting IDs, graph-aware repair refusals/delete, restart and rekey |
| release | migration | current specifications/reference/how-to, ADR status, VERSION and changelog | Cross-surface scenario via CLI and MCP after restart; review-tests; CI; documented handover and rollback |

This serial DAG has one sink (release), which owns cross-surface integration checks. Keep execution in the main session as requested; delegate reviews only. Separate future implementation PR from this planning PR. Recheck paths and schema version against its fresh base before coding. No runtime version bump for a design-only PR; the implementation release takes the next minor from the version then current.

## Test design before implementation

Each row names an observable failure and a mutation that must turn its test red. Write that test before its production change. Have a reviewer other than the test author run review-tests and verify mutation evidence. Test names below are planned contracts, not existing tests or executed results.

| Planned case | Failure mode | Deliberate mutation |
|---|---|---|
| DecisionLifecycle_RejectsTerminalAmendment | Accepted history can be rewritten | Permit amend on superseded record |
| DecisionEdges_RejectsDependencyCycle | Traversal introduces invalid prerequisites | Disable live cycle check |
| DecisionStore_CrossProjectEndpointNotFound | Guessed IDs read/link another graph | Remove project predicate from endpoint lookup |
| DecisionStore_ReopenPreservesHistory | Session exit loses graph | Skip version persistence |
| DecisionStore_ReplayBeforeExpectedVersion | Network retry duplicates or conflicts | Check version before receipt |
| DecisionStore_AtomicSupersession | Half-updated state after failure | Commit edge before target version |
| DecisionStore_MigrationReplayPreservesMemory | New schema damages existing memory | Drop existing entries in fixture migration |
| DecisionSync_StripsGraphOnEveryPushPath | Local-only graph uploads | Remove one decision-table exclusion |
| DecisionSelection_LastUsesRegistrationOrder | Editing old task changes latest task | Sort by updated timestamp |
| DecisionSelection_EmptyNewestTaskRemains | Default silently falls back to older decisions | Select tasks only through inner join to decisions |
| DecisionEdgeRemoval_TaskProvenanceSurvivesRestart | Removing an old link loses current task attribution | Omit removal_task_id persistence |
| DecisionPreview_ManyEmptyTasks | Task headers exceed output budget | Emit every selected task header without budget check |
| DecisionRepair_TwoGraphsRefused | Independent order/revision namespaces collide | Remove occupied-loser preflight |
| DecisionSelection_RevisionAndEdgeTouchCount | Updates disappear from task export | Select only origin_task_id |
| DecisionExport_BoundaryClosureAndHistory | Dangling links or lost rationale | Drop boundary stub or old version |
| DecisionExport_ConcurrentWriterSingleSnapshot | Mixed versions/tasks in one file | Remove read transaction |
| DecisionPreview_CountsAndUnicodeBudget | Missing data pretends complete; Unicode breaks | Suppress omission count or slice UTF-16 surrogate |
| DecisionApi_AnnotationsAndGuardParity | CLI bypasses policy; discovery hides flags | Remove HTTP gate or explicit false annotation |
| DecisionCli_DefaultNAllMatchBackend | Flags select decisions instead of tasks | Apply N after flattening decisions |
| DecisionCli_InterruptedFileIsNotPublished | Failed stream overwrites valid export | Write destination before completion |
| DecisionImport_DryRunReplayAndUnknownData | Migration destroys legacy metadata | Write during dry run or drop unsupported raw record |
| DecisionRepair_FoldAndConflictReport | Repair folds a nonempty retired graph or changes winner graph | Bypass occupied-loser graph preflight or use INSERT OR REPLACE |
| DecisionIntegration_RestartAndAccessModes | Works only within one process/full mode | Use a process-local repository instead of persistent store, or bypass committed-write access guard |

Run focused modified suites and their consumers once. CI runs the broader matrix. Full export should also get a bounded large fixture proving streaming and cancellation without inventing a performance target; benchmark only if it settles a design decision. Release is blocked by any unmapped input, unresolved must-fix review finding, missing mutation evidence, or cross-surface parity failure.

## Rollout and deferred scope

Land schema with sync exclusion before exposing writes, then service, adapters, CLI and migration. Run end-to-end gates against the combined branch. Enable native instructions only after native persistence/traversal/selection passes; framework source updates and refresh are a follow-up coordinated ai-badger PR. A mixed old/new binary must fail closed on a newer bank schema, never downgrade it. Back up before migration; rollback restores a matching binary/bank backup and legacy configuration. Do not open a newly migrated bank with an older binary.

Deferred: workspace drafting/consolidation, shared graph promotion, automatic cloud merge, semantic embeddings, generic entity/ontology operations, RDF formats, native JSON restore, arbitrary historical revision reads, and automatic task-tracker ingestion. These are explicit scope limits, not claimed Semantica parity.
