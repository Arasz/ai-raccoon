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
| Decision-only project planned for retirement, so the repair request threw on every maintenance pass | `DecisionProjectRepairTests.Census_pins_a_registered_project_that_owns_only_decisions_instead_of_retiring_it`, `DecisionProjectRepairJobTests.Repair_request_folds_unrelated_ids_and_finishes_when_a_project_owns_only_decisions` | 2/6 | 6/6 |
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
