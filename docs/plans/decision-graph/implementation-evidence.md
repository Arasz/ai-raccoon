# Native decision graph implementation evidence

Implementation PR: #923. Runtime plan `air-native-project-decision-graph-implementation`.

## Model

- Legal transition no-op: 4 failed / 4, then 4 passed / 4.
- Terminal amendment no-op: 3 failed / 28, then legal/terminal suite green.
- Invalid-selector stub: 3 failed / 3.
- Dependency-cycle validation no-op: 1 failed / 1.
- Confidence validation no-op: 4 failed / 4.
- Independent reviewer reproduced 56 passed / 56 and removed edge-kind filtering: 1 failed / 56.
- Reviewer found source-count and exposed-collection test gaps. Added targeted tests; removing source-count guard and returning a mutable copied array produced 2 failed / 13. Restored production yielded 58 passed / 58.
- Core has no HTTP or persistence dependencies. Static helper classes contain only pure validation/graph functions.

## Storage in progress

Fresh-bank table test: 1 failed / 1 before DDL; 1 passed / 1 after schema v23 tables. Storage suite now passes 9/9, covering reopen, cross-project FK refusal, read snapshot consistency, write rollback, removal/import metadata, v22-to-v23 migration, encryption and rekey. A NULL change-reason constraint test failed against SQLite's three-valued CHECK and then passed after explicit IS NOT NULL validation.

Legacy schema and sync consumers passed 27/27 after linking the worktree's missing model data asset from the existing main checkout. The encrypted rekey fixture performs its missing-key probe after rekey so a separate wrong-key connection pool does not hold the file during exclusive rekey.

Cloud exclusion is checked independently by the semantic condition that no table starting with decision survives any push snapshot. Removing graph exclusions caused 6 failures/15 sync cases; restoring them yielded 15/15 passing. Independent storage review completed at 6fb628bd with zero blockers/major findings. Project lookup, read snapshot, removal task and rollback mutants were killed; the restored storage/sync run passed 24/24.

## PR review corrections in progress

Review 5654842632 identifies stale status docs, occupied project repair and stale revision error classification. Status docs and graph snapshot now reflect actual milestones. Retirement and revision-conflict checks were observed red before correction and passed afterward in an 18-test focused run. HTTP 409/400 parity remains an adapter gate. The populated FK-enforced snapshot test failed when table reversal was removed (1/1) and passed after restoration (1/1). CI also exposed missing retry attributes on new integration tests and a cold-open DDL statement budget changed by twelve new schema statements; both consumer contracts are being updated with focused verification.

## Repair concurrency correction

Independent review reproduced a real graph-owner retirement race without production mutations: a graph task committed at the clock seam after preflight, then repair deleted its registry row (1/1 probe failed). Five new checks first failed: two nested-transaction cases, graph registration refusal, queued writer registration recheck, and SQLite-traced transactional graph preflight.

Repair now runs preflight plus all rewrites in one BEGIN IMMEDIATE transaction, existing nested bank writes use savepoints, and graph writers recheck registration after acquiring their write lock. The combined transaction/decision/project-fold/populated-snapshot run passed 40/40. Owner fixture setup now registers real project rows. A follow-up independent review is pending.

Service registration/record/replay/read passed its first behavior test; amendment/stale replay/concurrency passed 3/3. Four missing mutation-operation cases then went red, and implementing lifecycle transitions, links/removals and supersession produced 7/7 passing service tests, including an injected SQLite failure after edge insertion that proves all-or-none supersession and retry receipts.

## Service acceptance

The eight `service-contract` checks live in `tests/AiRaccoon.Tests/Integration/Decisions/DecisionServiceTests.Acceptance.cs`. Four overlapping view tests were folded into them. Each mutation came from the `implementation.md` table and was applied to production code. After each one the whole solution was rebuilt and `DecisionServiceTests` was run (21 tests). The named check went red every time:

| Mutation | Failed / 21 | Named check red |
|---|---|---|
| Tasks sorted by latest edit revision, not `registered_order` | 5 | LastUsesRegistrationOrder, EmptyNewestTaskRemains |
| Tasks selected only through an origin join to decisions | 5 | EmptyNewestTaskRemains |
| Version-touch membership dropped | 1 | RevisionAndEdgeTouchCount |
| Edge-touch membership dropped | 2 | RevisionAndEdgeTouchCount |
| Boundary stubs dropped | 1 | BoundaryClosureAndHistory |
| History filtered to selected-task versions | 2 | BoundaryClosureAndHistory |
| Read transaction removed (writer interleaved after the revision read) | 1 | ConcurrentWriterSingleSnapshot |
| Omission counts suppressed | 3 | CountsAndUnicodeBudget, ManyEmptyTasks |
| Excerpt sliced by UTF-16 unit | 2 | CountsAndUnicodeBudget |
| `removal_task_id` persisted as NULL | 3 | TaskProvenanceSurvivesRestart |
| `removal_task_id` dropped on read | 2 | TaskProvenanceSurvivesRestart |
| Task headers emitted without the budget check | 1 | ManyEmptyTasks |

With production restored, `DecisionServiceTests` passed 21/21 in four separate runs and `RetrySurfaceGateTests` passed 2/2. No production defect was found. Traversal termination is still covered by `Graph_preserves_causal_direction_and_explicit_depth_and_node_bounds`.

## Review fixes

An adversarial review of the pull request confirmed five findings. Each fix below was seen red before the change and green after it.

| Finding | Check | Red | Green |
|---|---|---|---|
| Decision-only project planned for retirement, so the repair request threw on every maintenance pass | `DecisionProjectRepairTests.Census_keeps_a_registered_project_that_owns_only_decisions_instead_of_retiring_it`, `DecisionProjectRepairJobTests.Repair_request_folds_unrelated_ids_and_finishes_when_a_project_owns_only_decisions` | 2/6 | 6/6 |
| Required text measured untrimmed; identifiers accepted U+202E, U+200B, U+2028, U+2029, U+00AD and padding | `DecisionTextTests` | 8/14 | 14/14 |
| Cyclic or self link with a stale revision returned a revision conflict | `DecisionServiceTests.Invalid_links_are_refused_as_invalid_even_with_a_stale_revision` | 1/22 | 22/22 |
| v23 ladder step re-ran decision DDL the digest-gated Ddl had already run | `DecisionBankLifecycleTests.Version_twenty_two_bank_migrates_without_losing_existing_settings` now checks every decision table; removing the decision DDL from the Ddl turned it red | 2/2 | 2/2 |
| `AmendAsync` read the clock directly | refactor only, covered by the existing service tests | n/a | 115/115 |

The census now counts decision graph rows per id as an attachment, so a graph owner is never retire-eligible. The repair still refuses to fold or drop a graph owner. After all five fixes, the project-id repair classes, every decision class and the repo-wide source gates (`NoHandRolledCryptoTests`, `NoConfigureAwaitTests`, `RetrySurfaceGateTests`, `LoggerMessageEventIdTests`, `ProjectIdAliasDefaultCollectionGateTests`) passed 492/492.

The `INSERT ... SELECT` backfill and `decision_fts` rebuild remain in `DecisionSchema.Ddl`. They run once per schema digest change and no longer run a second time from the ladder.

## Adapters acceptance

The 13 decision operations now have one HTTP route and one MCP tool each (`DecisionTools`, `DecisionEndpoint`). Both adapters build the same request records (`src/AiRaccoon/Decisions/DecisionRequests.cs`), call the same service, require the explicit project id through the same check and refuse through one mapping (`DecisionRefusals`). The MCP refusal filter uses that mapping for decision tools, so a call refuses with the same kind over HTTP and MCP. Decision tools declare `ToolScope.Project`, require `projectId`, return `structuredContent` with the same JSON as text, and set all four annotations explicitly.

| Check | What it proves | Mutation | Red | Green |
|---|---|---|---|---|
| `DecisionApiTests.HttpRoutes_MapEveryOperationOnce`, `OperationTable_MatchesTheDecisionToolClass` | One HTTP route and one tool per api.md row | n/a (table equality) | n/a | 20/20 |
| `DecisionApiTests.ToolsList_DeclaresEveryAnnotationExplicitly` (raw `tools/list` JSON) | All four hints present with the expected value, `projectId` required, an output schema | drop `Destructive = false` from `export_decisions` | 1/20 | 20/20 |
| same + `Success_ReturnsTheEnvelopeAsStructuredContentAndText`, `McpToolContractTests.OnlyTheDecisionTools_DeclareAnOutputSchema` | Structured content and the text equivalent | drop `UseStructuredContent` from `register_decision_task` | 2/20, 1/3 | 20/20, 3/3 |
| `DecisionApi_AnnotationsAndGuardParity` (13 rows) | Unregistered id, padded id, blank id and read-only mode refuse with the same kind on both adapters; only writes get `access-denied` | MCP writes require `Read` | 7/20 | 20/20 |
| same | | HTTP writes require `Read` | 7/20 | 20/20 |
| same | | HTTP skips the gate | 13/20 | 20/20 |
| `DecisionEndpointTests.WriteRoute_RefusesAReadOnlyProject` (7 rows) | HTTP write routes need write access | HTTP writes require `Read` | 7/23 | 23/23 |
| `DecisionEndpointTests.Route_RefusesAnUnregisteredProject` (13 rows) | The gate runs before the service on every route | HTTP skips the gate | 14/23 (with the rows below) | 23/23 |
| `DecisionEndpointTests.NonCanonicalId_ReachesTheServiceCanonical` | A braced upper-case id is stored and returned under the canonical id | HTTP passes the raw id to the service | 1/23 | 23/23 |
| `DecisionApiTests.NonCanonicalId_ReachesTheServiceCanonical` | Same over MCP | MCP passes the raw id to the service | 1/20 | 20/20 |
| `DecisionApiTests.DomainRefusals_HaveTheSameKindOnBothAdapters` | not-found 404, conflict 409, invalid transition 409, invalid argument and unknown enum 400, same kinds over MCP | MCP filter skips `DecisionRefusals` | 4/20 | 20/20 |
| `DecisionApiTests.Export_OverTheMcpCap_IsRefusedOverMcpAndServedOverHttp` | MCP refuses a 1.2 MB export with `decision-export-too-large` and the CLI command, no partial content; HTTP serves it whole | remove `EnsureWithinMcpLimit` | 1/20 | 20/20 |
| `UnregisteredIdRefusalTests` | Decision tools join the derived unregistered-id theory | n/a | n/a | 42/42 |

Review follow-up, same commit range:

| Check | Mutation | Red | Green |
|---|---|---|---|
| `DecisionServiceTests.Graph_preserves_causal_direction_and_explicit_depth_and_node_bounds` now asserts `Truncated`, reasons `[depth]` and null omitted counts at depth 0 | delete `reasons.Add("depth")` | 1/23 | 23/23 |
| `DecisionServiceTests.Graph_cuts_edges_past_the_internal_cap_and_counts_the_rest` crosses `GraphEdgeLimit` (2000, internal, no API input) with 2001 seeded edges | delete `reasons.Add("edges")` | 1/23 | 23/23 |
| same | return every edge (no cut) | 1/23 | 23/23 |
| same | report 0 omitted edges | 1/23 | 23/23 |

Tool-surface gates updated with the new tools: `McpToolContractTests` (input snapshot, output-schema rule), `ScopeCheckTests`, `McpToolCompositionTests`, `ToolInventoryTests` (docs/reference/agent-memory-server.md table and count) and `ToolRefusalsTests` (error-shapes table now lists the six decision kinds). After the last commit the decision classes, the tool-surface classes and the repo-wide source gates (`NoHandRolledCryptoTests`, `NoConfigureAwaitTests`, `RetrySurfaceGateTests`, `LoggerMessageEventIdTests`) passed. `DecisionProjectRepairJobTests` ran with the granite model data present locally.

## CLI acceptance

`ai-raccoon decisions task register | export | preview` (`DecisionCommands`, tree in `CliCommandTree.DecisionsCommand`) reach the decision routes through the CLI's existing server binding: `ServerSettingsStore` and `LazyServerSettingsStore` implement `IDecisionClient` over `DecisionHttpClient` (`src/AiRaccoon/Decisions/Client/`), and the `encryption` graph gets a refusing `DirectGraphDecisionClient`. The CLI sends the same request records the HTTP adapter binds, so enums and defaults come from one declaration. Export and preview send no selector unless a flag names one, leaving the default (latest task) to the server. `--tasks`, `--all` and `--task-id` are mutually exclusive at parse time (exit 10).

The CLI reads the whole answer before writing anything: a body that ends early exits 62 with "nothing was published" and prints nothing. `--output` stages an owner-only file beside the destination and renames it into place; an existing destination needs `--force`. JSON output is the result's project header followed by the export's own fields, written element by element with the default (escaping) encoder. Markdown renders the same snapshot with control and format runes shown as `\uXXXX`, Markdown punctuation escaped and source URIs as code. Preview text replaces control, format and separator runes with U+FFFD one for one, so the server's Unicode budget still holds; the server's own `Display` leaves bidi and other format characters in place, so this step is not redundant.

Every test drives the real CLI dispatcher (`ConfigCommands`) against a real token-guarded web host (`DecisionHost`) through the production `LazyServerSettingsStore` → `ServerSettingsStore` path. The interruption fixture passes the server's real bytes through and resets the connection after 64 bytes of the export body.

| Check | What it proves | Mutation | Red | Green |
|---|---|---|---|---|
| `DecisionCli_DefaultNAllMatchBackend` (4 rows) | Default, `--tasks 2`, `--all` and repeated `--task-id` select the expected tasks, and the CLI export equals the backend's `/export` for the same selector field by field (revision included, `exportedAt` excluded) | `--tasks N` sent as `last: 1` | 1/4 | 4/4 |
| same | | no flag sends `all: true` | 1/4 | 4/4 |
| `DecisionCli_InterruptedFileIsNotPublished` | A reset while the answer is read exits nonzero, leaves the existing destination byte-identical and prints nothing; the read fails before publishing starts, so no staging file is ever created | open the destination for writing before the request | 1/1 | 1/1 |
| `DecisionCli_RenderFailingMidFile_LeavesNoStagingFileAndTheDestinationUntouched` | A render that fails after its first write into the staging file leaves the destination byte-identical and deletes the staging file | drop `File.Delete(staged)` from the `catch` | 1/1 | 1/1 |
| `DecisionCommandTreeTests.MixedOrOutOfRangeSelectors_AreParseErrors` (5 rows, export and preview) | Exclusive selector parsing | drop the exclusivity error | 3/5 | 5/5 |
| `DecisionCli_ExistingFileIsRefusedWithoutForce_AndReplacedOwnerOnlyWithIt` | No overwrite without `--force`; with it the file is replaced, `0600`, no staging file left | drop the existing-file check | 1/1 | 1/1 |
| `DecisionCli_PreviewTextSanitizesTerminalControls` | ESC/OSC, BEL, C1 CSI and bidi overrides never reach stdout | print the server text unsanitized | 1/1 | 1/1 |
| `DecisionCli_PreviewTextStaysWithinTheUnicodeBudget` | `--max-chars 1000` bounds astral-heavy output in Unicode scalars, with no broken surrogate | drop `--max-chars` forwarding | 1/1 | 1/1 |
| `DecisionCli_MarkdownExportKeepsFullTextAndEscapesTerminalAndMarkup` | Markdown keeps a 600-character rationale whole and shows ESC/bidi/`<script>` inert | `Inline` returns raw text | 1/1 | 1/1 |

Also green without a dedicated mutation: empty project (valid empty arrays), latest-after-`task register` with four concurrent exports identical at one revision, stdout interruption (nothing printed, nonzero), task registration printing generated ids to stderr and replaying with explicit ids, an unregistered project exiting 18, and preview JSON counts. `SettingsCommandTreeTests` now lists `decisions` in the top level and its leaves in the no-configuration-write sweep. After the last commit the decision classes, the CLI classes (tree, help, exit codes, routing, bank-open, write-ledger) and the repo-wide source gates (`NoHandRolledCryptoTests`, `NoConfigureAwaitTests`, `LoggerMessageEventIdTests`, `RetrySurfaceGateTests`, `LayeringRulesTests`) passed, 768/768.

Limits worth knowing. The CLI parses the full JSON answer in memory before writing it, as the server already builds the whole snapshot in memory; output is then written in array-element chunks. A failure during rendering (after the answer was read) deletes the staging file in the same `catch`; `DecisionCli_RenderFailingMidFile_LeavesNoStagingFileAndTheDestinationUntouched` injects one. `import-semantica` belongs to the migration step.

### CLI follow-up: record, get and query

`decisions record --input` reads one JSON object, refuses it before sending (exit 10, nothing sent, no ids announced) when it is not an object, repeats a key, or names `taskId`, `sessionId` or `requestId`, then completes it with `--task-id` and the given or generated retry ids and posts it to `P/records`. An unreadable file exits 80. `decisions get` sends `GET P/records/{id}?includeHistory=`, printing the result as JSON or as Markdown escaped like the export (`DecisionMarkdown.RenderDecision`). `decisions query` posts `--query`/`--category` to `P/query`.

| Check | What it proves | Mutation | Red | Green |
|---|---|---|---|---|
| `DecisionCli_Record_RefusesAnInputItCannotSendAsIs_BeforeSending` (5 rows) | Write fields in the input are refused, not sent as a second copy | drop the write-field check | 2/5 | 5/5 |
| same | Duplicate keys get the CLI's own refusal | parse with `AllowDuplicateProperties = true` | 1/5 | 5/5 |
| `DecisionCli_Get_HistoryMatchesBackend_AndMarkdownEscapesDecisionText` | `--history` returns both versions equal to the backend's; Markdown shows ESC/bidi/`<script>` inert | always send `includeHistory=false` | 1/1 | 1/1 |
| same, and the export Markdown test | Version titles are escaped | `Version` prints the raw title | 2/2 | 2/2 |
| `DecisionCli_Query_MatchesBackend` (4 rows) | `--query`/`--category` reach the server; output equals the backend's | drop `--category` | 2/4 | 4/4 |
| `DecisionCommandTreeTests.RecordGetQuery_ParseTheirDocumentedFlags` | The verbs are in the tree with their flags | remove `get` from the tree | 1/1 | 1/1 |

Also green without a dedicated mutation: record replay with explicit ids, an unregistered task refused (exit 17, nothing recorded), an unknown decision (exit 17, `decision-not-found` on stderr), required-argument parse errors, and the three leaves in `SettingsCommandTreeTests`' no-configuration-write sweep.

### Review follow-ups

An independent review of the MCP and CLI tests found mutations that survived. Each check below now goes red under the reviewer's mutation.

| Check | What it proves | Mutation | Red | Green |
|---|---|---|---|---|
| `DecisionServiceTests.Graph_follows_a_chain_one_step_per_depth` | On A→B→C→D, depth 2 returns three nodes with reason `depth`, depth 3 all four | `node.Depth >= Math.Min(request.Depth, 1)` | 1/1 | 1/1 |
| `DecisionApiTests.GraphBounds_AreTheSameOnBothAdapters` (7 rows) | depth 11/-1 and maxNodes 0/501 are `invalid-argument` (400) over HTTP and MCP; depth 10, maxNodes 500 and 1 are accepted | depth guard 0..1000 | 1/7 | 7/7 |
| same | | maxNodes guard 0..100000 | 2/7 | 7/7 |
| same | | depth guard 0..9 | 1/7 | 7/7 |
| `Graph_cuts_edges_past_the_internal_cap_and_counts_the_rest` | The cap is the literal 2000 | `GraphEdgeLimit = 1999` | 1/1 | 1/1 |
| `DecisionCommandTreeTests.TasksAtItsBounds_IsAccepted` (2 rows) | `--tasks 1` and `--tasks 1000` parse and map to `last` | upper bound 999 | 1/2 | 2/2 |
| `DecisionEndpointTests.RecordAndGet_…`, `RegisterTask_…` | `data.projectId` is the project, and the value is the task or decision asked for | result `ProjectId` replaced | 2/2 | 2/2 |
| `DecisionServiceTests.Transition_replay_returns_the_first_result_without_a_second_version` | A replayed transition returns the first version and revision; the same request id with another target status conflicts | transition payload hash omits `targetStatus` | 1/1 | 1/1 |
| `DecisionServiceTests.Unlink_replay_returns_the_first_removal_even_after_the_revision_moved` | A replayed unlink returns the first removal at its revision; another reason conflicts | unlink payload hash omits `reason` | 1/1 | 1/1 |
| `DecisionApiTests.DomainRefusals_HaveTheSameKindOnBothAdapters` | Conflict rows carry `currentVersion` and `currentProjectRevision` in both the HTTP problem and the MCP text | drop `currentVersion` from the details | 1/1 | 1/1 |

`DecisionCommandTreeTests.ProjectId_IsRequired` now pins exit 10: a missing required option is `Usage.InvalidValue`, as `CliInput` maps it; `Usage.MissingValue` is for an option given without its value.

## Migration acceptance

Semantica import (`DecisionImport_DryRunReplayAndUnknownData`) is deferred to #924: no real exporter fixture exists. This section covers the rest of the `migration` step: graph-aware repair, deletion, encrypted backup and rekey.

`DecisionGraphFixture` (`tests/AiRaccoon.Tests/Integration/Decisions/DecisionGraphFixture.cs`) seeds one project through `DecisionService`: two tasks, an amended decision, a live edge, a removed edge, replay receipts, and one `decision_imports` row. That fills all eight graph tables. The repair checks compare `total_changes()` on the repair connection before and after a refused repair, so a write that is later rolled back still fails the check. They also compare a dump of every ordinary table in the bank.

No standalone project-deletion command exists. The only code that deletes a `projects` row is project-ID repair (fold, drop, retire). data-model.md states the v1 contract: no project deletion, and repair holds back or refuses any retired, dropped or loser project that owns graph rows, so a graph owner keeps its whole graph. Removing every graph table in FK-safe (reverse) order is already covered by the sync snapshot exclusion (`SyncService` `DecisionSchema.TableNames.Reverse()`, populated FK-enforced snapshot test above).

| Check | What it proves | Mutation (production) | Red | Green |
|---|---|---|---|---|
| `DecisionProjectRepairTests.DecisionRepair_TwoGraphsRefused` | Folding a loser into a winner, both with full graphs, throws before any write, reports all eight of the loser's graph tables, and leaves the bank unchanged | remove the `RefuseGraphOwnersAsync` call | 6/7 | 7/7 |
| same | | report only the first occupied table | 5/7 | 7/7 |
| same | | run the preflight after the folds, just before the `projects` rewrite (the rollback still restores rows) | 4/7 | 7/7 |
| `DecisionProjectRepairTests.DecisionRepair_FoldAndConflictReport` | A plan with a graph-free fold plus an occupied loser is refused as a whole, names `occupied` and exactly its `decision_projects`/`decision_tasks`, and writes nothing. The graph-free fold alone then moves the entry, retires the loser and leaves the winner's graph unchanged (rows, revision, task order, receipts, edge history, FTS rows) | `INSERT OR REPLACE` the winner's `decision_projects` row during the fold | 1/7 | 7/7 |
| same | | remove the preflight call; report only the first table; late preflight (rows above) | red in each | 7/7 |
| `DecisionProjectRepairTests.DecisionDelete_GraphOwnerIsRefusedWithItsWholeGraphIntact` (fold/drop/retire) | Every repair path that deletes a project row refuses a full-graph owner, with no writes and an identical bank | leave dropped and retired ids out of the preflight | drop+retire 2/3 rows (3/7 class) | 7/7 |
| `DecisionBankLifecycleTests.DecisionBank_EncryptedBackupAndRekeyKeepTheWholeGraph` | After a checkpoint, a file copy of the encrypted bank restores the whole graph under the old key. After `RekeyBankAsync`, the original reopens under the new key with an identical graph dump and a working `decision_fts` match. The old key, and a keyless open of either file, are refused | `PRAGMA rekey` replaced by a `SELECT` | 1/2 | 2/2 |
| same | | main open path drops `csb.Password` | 1/2 | 2/2 |

The late-preflight mutation leaves the retire row green, which is correct: retire writes nothing before the `projects` rewrite. The backup and rekey mutations act on the file and the key, not on graph tables. Both operate on the whole bank file, so no graph-specific production mutation exists. Graph completeness there comes from the eight-table dump equality.

After restoring production code, the whole solution built with no warnings. Every `Decision*` class, every ProjectId/repair/census class and the source gates (`NoHandRolledCryptoTests`, `NoConfigureAwaitTests`, `LoggerMessageEventIdTests`, `RetrySurfaceGateTests`, `LayeringRulesTests`, `ProjectIdAliasDefaultCollectionGateTests`) passed: 61 classes, 693/693.

## Repair hold-back

A review found that an alias loser or dropped id owning both entries and graph rows was still planned. The repair preflight then threw on every maintenance poll, the request never finished, and unrelated folds waited behind it. The planner now holds such an id back as `pinned-decision-graph`, with a reason that counts its graph rows, on the alias, name-attributed and drop paths. The rest of the plan runs. `RefuseGraphOwnersAsync` stays as the preflight behind it, and it now names every occupied project with its tables in one exception instead of stopping at the first.

| Test | What it proves | Red | Green |
| --- | --- | --- | --- |
| `ProjectIdsFoldPlanTests.FromCensus_GraphOwningAliasLoser_IsHeldBackWhileTheUnrelatedFoldPlans`, `..._GraphOwningDroppedId_IsHeldBackWhileTheUnrelatedDropPlans`, `..._GraphOwnerAttributedByName_IsHeldBack` | A graph owner with entries is pinned with a reason; the unrelated fold or drop is still planned | 3/46 failed before the planner change; removing only the name-path check failed 1/46 | 46/46 |
| `DecisionProjectRepairJobTests.Repair_request_holds_back_a_graph_owning_loser_with_entries_and_finishes_the_rest` | The request completes, the unrelated fold applies, the loser's entry and full graph are untouched, and the second pass returns false without throwing | 1/2 (threw `DecisionGraphRepairException`) | 2/2 |
| `ProjectIdsRepairTranscriptTests.Transcript_IsUnchanged(dry-run-decision-graph-held-back)` | The dry-run report shows the `pinned-decision-graph` line and reason, and counts no fold for the held-back id | 1/16 (report planned the fold) | 16/16 |
| `DecisionProjectRepairTests.DecisionRepair_ReportsEveryOccupiedLoserWithItsTables` | One refused plan names both occupied projects, each with its own tables, and writes nothing | 1/8 (only the first project reported) | 8/8 |

`DecisionRepair_TwoGraphsRefused`, `DecisionRepair_FoldAndConflictReport` and `DecisionDelete_GraphOwnerIsRefusedWithItsWholeGraphIntact` now assert a single owner in `Owners`. After the last change the solution built with no warnings, and every `Decision*` class, every ProjectId/repair/census class and the source gates (`NoHandRolledCryptoTests`, `NoConfigureAwaitTests`, `LoggerMessageEventIdTests`, `RetrySurfaceGateTests`, `LayeringRulesTests`, `ProjectIdAliasDefaultCollectionGateTests`) passed: 61 classes, 699/699.

### Held-back ids at the gate

The independent review-tests pass at 252eff6a found that `ProjectIdsRepairJob.RunAsync` still persisted the whole requested map through `ProjectIdAliases.PersistAppliedAsync` and loaded it into `ProjectIdAliasMap.Default`. That included the alias or drop of an id the planner had held back. `ToolGate.RequireAsync` folds every call through `Default`, so after the job a held-back alias loser resolved to its winner and a held-back dropped id was refused as retired. Its graph rows were intact but no call could reach them. The job now persists `map.Without(...)` the `pinned-decision-graph` ids, and the same review's minor findings are fixed.

| Test | What it proves | Red | Green |
| --- | --- | --- | --- |
| `DecisionProjectRepairJobTests.Repair_request_holds_back_a_graph_owning_loser_with_entries_and_finishes_the_rest` | After the job, `Default.Apply("graph-loser")` is still `graph-loser`, the real `ToolGate` returns `graph-loser` for a decision read, and only the applied alias is stored | 1/3 (`Apply` returned `winner`) | 3/3 |
| `DecisionProjectRepairJobTests.Repair_request_holds_back_a_dropped_graph_owner_and_keeps_it_reachable` | A held-back drop is neither stored nor applied: the id is not retired, the gate passes it and its entry and graph are untouched | 1/3 (`Apply` returned `Dropped = true`) | 3/3 |
| `ProjectIdAliasMapTests.Without_RemovesTheNamedAliasesAndDrops_AndKeepsTheRest` | `Without` removes the named aliases and drops and keeps canonicals | did not compile before `Without` existed | 50/50 |
| `DecisionGraphFixture.DumpBankAsync` (used by `DecisionProjectRepairTests`) | The dump now includes `sqlite_master` (type, name, sql) and `sqlite_sequence`, so a repair that changes the schema is caught | Dropping trigger `decision_versions_search_ai` before the transaction: 0/8 failed with the old dump, 5/8 with the new one | 8/8 |
| `DecisionProjectRepairTests.DecisionRepair_ReportsEveryOccupiedLoserWithItsTables` | Also asserts `total_changes()` is unchanged by the refusal, like its siblings | — | 8/8 |

`Census_pins_a_registered_project_that_owns_only_decisions_instead_of_retiring_it` is renamed `Census_keeps_a_registered_project_that_owns_only_decisions_instead_of_retiring_it`: its body asserts the project is not retired, not that it is pinned.

An id held back only through its registered name (the alias key is the name, the pinned id is the guid) is not filtered, because the gate is asked for the guid, which the alias does not cover.

## Release acceptance

`DecisionIntegrationTests.DecisionIntegration_RestartAndAccessModes` runs one theory row per access mode (`ro`, `rw`, `full`). It works on a real web host serving HTTP and MCP. It registers a task over MCP, records one decision over HTTP and one over MCP, links them over MCP, then registers a second task and records into it through the CLI. Next it sets the mode, takes a full export, stops the host and starts a second host on the same data root. On that host, the full export must equal the pre-restart one (revision included). `get_decision`, `query_decisions` and `get_decision_graph` must return the same data over HTTP and MCP, and also over the CLI for get and query. CLI `export` and `preview --format json` with no selector, `--tasks 2` and `--all` must match the backend's export and preview. Last, one task registration is sent over each of HTTP, MCP and the CLI. In `ro` all three are refused (`access-denied`, `access-denied`, exit 17) and the revision is unchanged. In `rw` and `full` all three succeed and the revision moves by 3.

The test found a real defect on its first run: `link_decisions` over MCP refused `kind=depends_on` (`invalid-argument: The JSON value could not be converted to DecisionEdgeKind`). The SDK's enum converter outranked the decision enums' snake_case converter attributes. `DecisionTools` now registers with serializer options that put the decision converters first (87ca9662).

| Mutation | Rebuilt | Result |
| --- | --- | --- |
| MCP `register_decision_task` requires `AccessRequirement.Read` instead of `Write` (`DecisionTools.cs`) | yes, 0 errors | 1/3 red (`ro`: MCP write was accepted) |
| HTTP `get_decision` requires `Write` instead of `Read` (`DecisionEndpoint.cs`) | yes | 0/3 before the three-adapter read check (f5ff4729), 1/3 red after it (`ro`: HTTP read refused as `access-denied`) |
| Every reopen of a current bank deletes `decision_edges` (`MemorySchema.EnsureAsync`) | yes | 3/3 red (export differs across the restart) |
| `DELETE FROM decision_edges` appended to the decision DDL | yes | 0/3: the DDL runs only on a digest mismatch, which does not happen on a same-binary restart; recorded as not a restart mutation |

Each mutation was reverted and the solution rebuilt before the next; the class was 3/3 green after each restore.

Docs: [how-to](../../how-to/record-project-decisions.md) (registration, recording, linking, CLI export and preview, access modes, limits, Semantica, upgrade and rollback), ADR-0142 status and "What shipped in 2.4.0", ADR index row, how-to index, `VERSION` 2.4.0, [changelog](../../changelog/2.4.0-native-decision-graph.md), changelog index and the README What's new line. Rollback restores the 2.3.0 binary together with a data-root copy taken before upgrade: `MemorySchema.CurrentVersion` is 23, and 2.3.0 (`CurrentVersion` 22) refuses the bank under ADR-0019's forward-version guard.

Final local run after the last change: the solution built with 0 warnings, and 32 classes passed 360/360. These were every `Decision*` class, `DecisionCliTests`, `DecisionEndpointTests`, `DecisionCommandTreeTests`, `ProjectIdsRepairJobTests`, `ProjectIdAliasMapTests`, the source gates (`NoHandRolledCryptoTests`, `NoConfigureAwaitTests`, `LoggerMessageEventIdTests`, `RetrySurfaceGateTests`, `LayeringRulesTests`, `ProjectIdAliasDefaultCollectionGateTests`), the docs gates (`AdrIndexTests`, `MergeConflictMarkerTests`, `FirstRunMemoryEngineTests`, `ObservabilityCountersCommandDocsTests`, `HowToExitTableTests`), `VersionContractTests`, `McpToolContractTests` and `ToolInventoryTests`. CI on the release head was still running when this was written.

## Semantica import acceptance

Each test was written first and seen to fail, then each named mutation was applied, built, run and restored, and the solution was rebuilt clean before the green was trusted. Counts are failed tests in the named classes.

| T | Test | Mutation | Red | Green |
| --- | --- | --- | --- | --- |
| T1 | `DecisionStateTests.Importable_AllowsOnlyMappableStatuses` | `Importable` true for `Superseded` | 1 of 36 | 36/36 |
| T2 | `SemanticaParsingTests.Parse_RealFixture_ReadsEveryRecord` | Return empty lists after the envelope check | 10 of 52 | 52/52 |
| T3 | `Parse_DuplicateKey_InEnvelopeInnerDataAndMapping_Refuses` | Parse the inner string with default `JsonDocument.Parse` | 3 of 52 | 52/52 |
| T4 | `Parse_InvalidUtf8InsideString_RefusesAsArgument` | Lenient `Encoding.UTF8` decode | 2 of 52 | 52/52 |
| T5 | `Parse_UnsupportedShape_Refuses` | Drop the exact-key check / drop the count comparison | 6 of 52 / 2 of 52 | 52/52 |
| T6 | `SemanticaImportPlanTests.Plan_Fixture_MatchesExpectedCounts` | Promote every relationship type | 5 of 38 | 38/38 |
| T7 | `Plan_NeverInfersStatusFromOutcome` | Fall back to `outcome` when status is missing | 1 of 38 | 38/38 |
| T8 | `Plan_OverlongOrMissingContent_BlocksPerRecord` | Use entity `text` for a missing `reasoning` | 1 of 38 | 38/38 |
| T9 | `Plan_MappedEdgeToNonDecision_Blocks`, `Plan_UnmappedCaused_RetainedNotPromoted` | Skip the endpoint check; default unmapped types to `depends_on` | 1 of 38; 5 of 38 | 38/38 |
| T10 | `Plan_DuplicateRules_CollapseEqualRefuseDifferent` | Collapse duplicates by ID only | 1 of 38 | 38/38 |
| T11 | `Plan_DependsOnCycleAndContradictsOrder` | Remove the `EnsureCanLink` call / remove the `contradicts` swap | 1 of 38 / 2 of 38 | 38/38 |
| T12 | `DecisionImportTests.DecisionImport_DryRunReplayAndUnknownData` | Store re-serialized JSON as the raw payload / run the preview in a write transaction | 1 of 8 / 3 of 8 | 8/8 |
| T13 | `Apply_FailureMidWrite_LeavesEveryTableUnchanged` | Swallow the injected failure on a later import row (a partial commit) | 1 of 8 | 8/8 |
| T14 | `Apply_StaleRevision_DigestMismatch_ChangedSource_ChangedMapping_Refuse`, `Apply_SameRequestIdWithADifferentFile_ConflictsOnTheRequest`, `Apply_NewerExport_AddsOnlyNewRecords` | Remove `RequireRevisionAsync` / drop the source digest compare / drop the mapping digest compare / drop the mapping digest from the request payload | 2 / 1 / 1 / 1 of 8 | 8/8 |
| T15 | `DecisionImportApiTests.ImportRoutes_AcceptMaxSizeAndRefuseOverLimit` | Disable the `BadHttpRequestException` arm / limit 9 MiB / limit 11 MiB | 1 / 1 / 1 | 2/2 |
| T16 | `ImportRoutes_RefusalKindsAndGate` | Apply requires `Read` | 1 of 42 | 42/42 |
| T17 | `DecisionApiTests.HttpRoutes_MapEveryOperationOnce` | Rename the apply route / add an extra route | 2 / 1 | 40/40 |
| T18 | `DecisionImportCliTests.ImportSemantica_PreviewThenApply_AgainstARealServer` | Print a wrong source digest / return 0 on blockers | 2 of 5 / 1 of 5 | 5/5 |
| T19 | `_ApplyWithoutCompanions_SendsNothing`, `_RetryWithSameRequestId_Replays` | Weaken the companion check / drop the lone-flag check / generate a session id | 1 / 1 / 1 | 5/5 |
| T20 | `_OversizedFile_SendsNothing` | Double the `FileInfo.Length` limit | 1 of 5 | 5/5 |

First attempts that did not prove anything, fixed before they counted:

- The first T4 test put the bad byte inside the word `json`, so the format check refused it too and the lenient-decode mutation survived. The bad byte now sits in a free-text field.
- A payload mutation that swapped the two digests survived because the swap is symmetric. The same-`requestId` test also changed the expected revision, so it could not isolate the payload. It now holds the revision fixed.
- The T15 test padded with the limit constant, so a 9 MiB limit survived. It now pins 10 MiB literally.
- Two CLI mutations written as `if (false)` failed to build (unreachable code is an error here), so those runs exercised a stale binary. They were discarded and redone.

Source gates, run once at the end: `NoConfigureAwaitTests` 1/1, `NoHandRolledCryptoTests` 7/7 (after `SemanticaImportPlan.cs` joined its hashing allowlist), `LoggerMessageEventIdTests` 5/5, `RetrySurfaceGateTests` 2/2, `AdrIndexTests` 7/7, `McpToolInventoryTests` 3/3 and `McpToolContractTests` 3/3 (tool count stays 43), `ToolRefusalsTests` 42/42, `SettingsCommandTreeTests` 172/172. All 46 `Decision*` and `Semantica*` test classes: 362/362.

Two existing gates needed updates the plan did not foresee. `SettingsCommandTreeTests.ApplyLeaves_MatchCliBankWriteTestsCoverage` expects every `--apply` leaf to have an outbox row in `CliBankWriteTests`; the import verb writes only through the server's HTTP routes, so it is listed as a server-routed leaf. `NoConfigurationWriteRemainsReachableOutsideSettings` needed required arguments for the new verb.

H1 is settled: Kestrel's default 30,000,000-byte body limit accepts a legal pair of 10 MiB files (about 28 MB of base64), and a larger body is refused with `decision-import-too-large` and a 413.

### Rehearsal on a bank copy

Source: a copy of `~/ai-raccoon-eval/copies/cb99fe6ecbe0.db`, an older copy of the live bank at schema v17, placed in a scratch data root outside the repository. The live bank and port 7721 were not touched. The copy's `watches` table was dropped before the first open, so it could not re-ingest real directories. The server ran as `serve --port 7945 --idle-timeout 0`; opening the copy migrated it to v23. The granite model data file was copied into the worktree's build output because the server refuses to start without it.

| Check | Result |
| --- | --- |
| Source file | 9 entities, 13 relationships, digest `290A36C0…5A11` |
| Preview before the tasks exist | exit 17, `unknown-task` for both task ids, 3 decisions and 3 edges plannable, nothing written |
| Preview after registering the two tasks | exit 0, 4 decisions, 3 edges, 15 retained, apply command printed on stderr |
| Refusals | stale revision: `decision-conflict`, exit 17; wrong source digest: `decision-conflict`, exit 17; missing companion flag: exit 10, nothing sent; bad status in the mapping: `decision-import-blocked`, exit 17; file over 10 MiB: exit 10, nothing sent; companion flag without `--apply`: exit 10. Request receipts stayed at 2 (the two task registrations) |
| Apply | exit 0, revision 2 to 3, `replayed: false` |
| Repeat of the printed command | exit 0, `replayed: true`, revision still 3 |
| Rows in the bank | 4 decisions, 4 versions, 3 edges, 22 import rows (4 with a decision id, 18 without) |
| After a server restart | `export --all`: 2 tasks, 4 decisions, 3 edges, 22 import records, revision 3; every raw payload parses equal to its source record; statuses accepted, proposed, accepted, rejected; the rejected one is under `storage-task` |
| Task selector | `export --task-id storage-task`: 1 decision, 1 edge, 1 boundary node, 1 import record |
| Precedent lookup | the HTTP `precedents` route for "where decisions live storage": 1 match, "Choose where decisions live", accepted, method `fts` |
| Graph traversal | from the first decision, both directions, depth 2: 2 nodes, 1 edge, not truncated |
| Synthetic file near the limit | source 9,802,497 bytes, mapping 392,071 bytes, 3,500 decisions, 5,248 relationships (a 3,499 `depends_on` chain plus 1,749 `supports`): preview 1.71 s, apply 1.95 s wall, both inside the 10 s budget; 3,500 decisions, 5,248 edges and 8,748 import rows written at revision 2. The fallback of planning in `ReadAsync` was not needed, and the quadratic cycle check (H4) was not a problem at this size |

The transcript of the commands and their output is in the scratch directory `/private/tmp/claude-501/-Users-arasz-RiderProjects-ai-raccoon/0661bdac-1d0f-4eba-9380-36817c5be932/scratchpad/s8files/transcript.txt`.

### Planner decisions where the plan was silent

- Status matching is exact and lowercase. `Accepted` or `ACCEPTED` is `unknown-status`, and `Superseded` is `unknown-status` too; only the exact lowercase `superseded` gets `superseded-status`.
- An explicit JSON `null` for `confidence` or `decision_maker` counts as absent.
- A mapping `title` on a record that is not a decision entity is an `invalid-content` blocker, as is `status` on one (`status-on-non-decision`).
- A relationship of an unlisted type whose endpoint is not in the file gets both an `unsupported-relationship` notice and an `unresolved-endpoint` notice, and is retained.
- An unlisted edge-kind target (`supersedes`, or any other word) is an `invalid-edge-kind` blocker for each such mapping entry.
- Apply with an unusable `legacyTaskId` identifier is refused as `decision-import-blocked` before the write; a task that is simply unregistered is reported by the planner as `unknown-task`.
- `Collapsed` also counts a byte-equal duplicate record, as the plan's count definition says, and a promoted relationship whose edge already exists.
- Apply digests are compared ignoring case; the preview prints uppercase.
- The CLI quotes a path or id in the printed apply command only when it holds characters outside letters, digits and `_ . / : -`.
- Closing the review-tests gate (S9) is not done here: it needs a reviewer other than the author.

