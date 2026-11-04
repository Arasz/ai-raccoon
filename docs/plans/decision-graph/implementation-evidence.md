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
| `DecisionCli_InterruptedFileIsNotPublished` | A reset mid-export exits nonzero, leaves the existing destination byte-identical, prints nothing and leaves no staging file | open the destination for writing before the request | 1/1 | 1/1 |
| `DecisionCommandTreeTests.MixedOrOutOfRangeSelectors_AreParseErrors` (5 rows, export and preview) | Exclusive selector parsing | drop the exclusivity error | 3/5 | 5/5 |
| `DecisionCli_ExistingFileIsRefusedWithoutForce_AndReplacedOwnerOnlyWithIt` | No overwrite without `--force`; with it the file is replaced, `0600`, no staging file left | drop the existing-file check | 1/1 | 1/1 |
| `DecisionCli_PreviewTextSanitizesTerminalControls` | ESC/OSC, BEL, C1 CSI and bidi overrides never reach stdout | print the server text unsanitized | 1/1 | 1/1 |
| `DecisionCli_PreviewTextStaysWithinTheUnicodeBudget` | `--max-chars 1000` bounds astral-heavy output in Unicode scalars, with no broken surrogate | drop `--max-chars` forwarding | 1/1 | 1/1 |
| `DecisionCli_MarkdownExportKeepsFullTextAndEscapesTerminalAndMarkup` | Markdown keeps a 600-character rationale whole and shows ESC/bidi/`<script>` inert | `Inline` returns raw text | 1/1 | 1/1 |

Also green without a dedicated mutation: empty project (valid empty arrays), latest-after-`task register` with four concurrent exports identical at one revision, stdout interruption (nothing printed, nonzero), task registration printing generated ids to stderr and replaying with explicit ids, an unregistered project exiting 18, and preview JSON counts. `SettingsCommandTreeTests` now lists `decisions` in the top level and its leaves in the no-configuration-write sweep. After the last commit the decision classes, the CLI classes (tree, help, exit codes, routing, bank-open, write-ledger) and the repo-wide source gates (`NoHandRolledCryptoTests`, `NoConfigureAwaitTests`, `LoggerMessageEventIdTests`, `RetrySurfaceGateTests`, `LayeringRulesTests`) passed, 768/768.

Limits worth knowing. The CLI parses the full JSON answer in memory before writing it, as the server already builds the whole snapshot in memory; output is then written in array-element chunks. A failure during rendering (after the answer was read) deletes the staging file in the same `catch`, but no test injects one. `decisions record|get|query` and `import-semantica` from api.md's CLI listing are not part of this step.
