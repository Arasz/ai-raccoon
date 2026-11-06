# Semantica import plan (#924)

Status: reviewed. The amendments section below overrides anything earlier that it contradicts.

No schema migration is needed: the bank stays at v23. The tool count stays at 43.

## Contract corrections

1. `data-model.md` says creation allows proposed or accepted, but real records need `rejected` or `deprecated`. Writing v1 proposed plus v2 rejected in one import is impossible (`decision_versions` has `UNIQUE(project_id, project_revision, decision_id)`, and a mutation gets one revision) and would invent history. An imported decision is created as version 1 directly in its mapped status, with a null `change_reason`. Amend `data-model.md`.
2. The CLI line in `api.md` cannot pass the source digest or the preview revision. Add `--source-digest`, `--mapping-digest` and `--expected-project-revision`.
3. "Source digest" means two things. The request digest covers the file. `decision_imports.source_digest` is per record, otherwise "changed source under an existing external ID conflicts" cannot work across a newer export.
4. `superseded` cannot be imported: it needs a replacement and the permanent edge, and no fixture shows a supersession relationship. It is a blocker. The remedy is to import both records as `accepted`, then call `supersede_decision`.
5. `HttpRoutes_MapEveryOperationOnce` compares routes to `DecisionOperations.All`, which also feeds `McpToolContractTests` and `UnregisteredIdRefusalTests`, and those assume every row is a tool. The `DecisionEndpoint` summary says "one per decision tool". Add a separate `DecisionOperations.HttpOnly` list.
6. Relationship `metadata` is optional: 5 of the fixture's 13 relationships carry it.
7. Issue #924 still requires both shapes. Say so in the PR body.

## Measured (.NET 10)

| Probe | Result |
|---|---|
| `JsonDocument` with `AllowDuplicateProperties=false` | Rejects top-level, nested and escaped duplicates with `JsonException` |
| Invalid UTF-8 inside a string | Passes `JsonDocument.Parse`. `GetRawText` then throws `InvalidOperationException`, which `DecisionRefusals.For` does not map, so it would surface as a 500. Decode strictly first. |
| `new UTF8Encoding(false, true).GetString` on bad bytes | `DecoderFallbackException`, an `ArgumentException`, so it maps to 400 |
| UTF-8 BOM | Rejected by the parser |
| `byte[]` in a record, Web options | Base64 binds. Bad base64 gives `JsonException`. A missing field gives null. |
| Tool count | 43 `[McpServerTool]` lines, 13 in `DecisionTools.cs` |
| `ToolNameScope.ScopeOf` on a non-tool name | Returns `Project`, so a route name with no tool passes the gate safely |
| jq mapping skeleton on the fixture | Produces 22 record entries |

## Hypotheses (unverified)

- H1: Kestrel's default body limit is 30,000,000 bytes and `src` sets no override. The largest legal body is about 27.96 MB (two 10 MiB files in base64). T15 settles it. If it fails, set `IHttpMaxRequestBodySizeFeature` on the two import routes.
- H2: `DecisionHost` runs real Kestrel (`McpServerSetup.cs:70` suggests so).
- H3: Semantica `CAUSED` runs cause to effect. If so, mapping it to `depends_on` inverts the edge, because `depends_on` runs dependent to prerequisite.
- H4: Incremental `EnsureCanLink` is O(E²) because it rebuilds its lookup per call. Unmeasured near 40k edges, and it runs inside the write lock. Accepted for a one-time command.
- H5: The test binary path is `tests/AiRaccoon.Tests/bin/Debug/net10.0/`. Confirm before using the gates.

## Mapping file

```json
{
  "sourceNamespace": "semantica-main",
  "legacyTaskId": "semantica-legacy",
  "edgeKinds": { "depends_on": "depends_on", "supports": "supports", "contradicts": "contradicts" },
  "records": {
    "33da32fb-4d6e-49e0-9dc2-00172691389a": { "externalId": "33da32fb-4d6e-49e0-9dc2-00172691389a", "status": "rejected", "taskId": "storage-task" },
    "category_storage": { "externalId": "category_storage" },
    "e513693a-1279-5a30-86ae-bf8e3c391803": { "externalId": "e513693a-1279-5a30-86ae-bf8e3c391803" }
  }
}
```

| Field | Rule |
|---|---|
| `sourceNamespace` | Required, `DecisionText.Identifier` |
| `legacyTaskId` | Required, the operator's explicit task for records with no `taskId`. Also the `DecisionWrite.TaskId` for the command. |
| `edgeKinds` | Source relationship type (case-sensitive) to `depends_on`, `supports` or `contradicts`. Unlisted types are retained, not promoted. Any other value, including `supersedes`, is a blocker. |
| `records` | Keyed by original ID, entities and relationships in one key space. Every source record needs an entry. |
| `records.*.externalId` | Required, Identifier |
| `records.*.taskId` | Optional, falls back to `legacyTaskId` |
| `records.*.status` | Required on `type == "decision"` entities, refused on anything else. Allowed: `proposed`, `accepted`, `rejected`, `deprecated`. Parsed as a string so the preview reports bad values rather than refusing the file. |
| `records.*.title` | Optional, decisions only. Overrides `text`, which mirrors `scenario` and may exceed the 200-character title limit. |

Unknown mapping fields are refused (`UnmappedMemberHandling.Disallow`). Import never creates tasks: unknown tasks are blockers, and operators pre-register tasks in order with `decisions task register`.

The how-to gives this jq skeleton. It leaves `status` out on purpose so the preview blocks until the operator decides:

```sh
jq '.data|fromjson|{sourceNamespace:"semantica-main",legacyTaskId:"semantica-legacy",edgeKinds:{depends_on:"depends_on",supports:"supports",contradicts:"contradicts"},records:(((.entities+.relationships)|map({key:.id,value:{externalId:.id}}))|from_entries)}' export.json
```

## DTOs, routes, refusals

No MCP tool: `api.md` says no agent tool imports external graphs. Both routes use the gate operation name `"import-semantica"`.

| Route | Access | Body | Result (`value`) |
|---|---|---|---|
| `POST P/imports/semantica/preview` | Read | `{source, mapping}` | `DecisionImportReport` |
| `POST P/imports/semantica/apply` | Write | `{sessionId, requestId, source, mapping, sourceDigest, mappingDigest, expectedProjectRevision, actor?}` | `DecisionImportOutcome` |

`source` and `mapping` are base64 of the exact file bytes, which keeps digests honest and keeps duplicate-key checks on the original text. The envelope is the existing `ApiEnvelope` with `projectId`, `projectRevision` and `replayed`.

```csharp
// src/AiRaccoon.Core/Decisions/DecisionImports.cs
public static class DecisionImportLimits { public const int MaxBytes = 10 * 1024 * 1024; public const int MaxIssues = 200; }
public sealed record DecisionImportCommand(string SessionId, string RequestId, byte[] Source, byte[] Mapping,
    string SourceDigest, string MappingDigest, long ExpectedProjectRevision, string? Actor = null);
public sealed record DecisionImportIssue(string Code, string Path, string Message, string? RecordId = null);
public sealed record DecisionImportCounts(int Entities, int Relationships, int Decisions, int Edges,
    int Retained, int AlreadyImported, int Collapsed);
public sealed record DecisionImportReport(string SourceDigest, string MappingDigest, bool CanApply, DecisionImportCounts Counts,
    IReadOnlyList<DecisionImportIssue> Blockers, int OmittedBlockers,
    IReadOnlyList<DecisionImportIssue> Notices, int OmittedNotices);
public sealed record DecisionImportOutcome(string SourceDigest, DecisionImportCounts Counts);

// IDecisionService
Task<DecisionResult<DecisionImportReport>> PreviewSemanticaImportAsync(string projectId, byte[] source, byte[] mapping, CancellationToken cancellationToken = default);
Task<DecisionResult<DecisionImportOutcome>> ApplySemanticaImportAsync(string projectId, DecisionImportCommand command, CancellationToken cancellationToken = default);

// src/AiRaccoon/Decisions/DecisionRequests.cs
internal sealed record ImportPreviewRequest(byte[] Source, byte[] Mapping);
internal sealed record ImportApplyRequest(string SessionId, string RequestId, byte[] Source, byte[] Mapping,
    string SourceDigest, string MappingDigest, long ExpectedProjectRevision, string? Actor = null);
```

`Counts`: `Decisions` and `Edges` are what will be or were newly written. `Retained` is new import rows with no promoted decision or edge. `AlreadyImported` is records skipped because they match an existing row. `Collapsed` is byte-equal duplicates plus promoted relationships whose edge already exists.

| Case | Kind | Status |
|---|---|---|
| Either file over 10 MiB | `decision-import-too-large` (new) | 413 |
| Kestrel body limit hit | Same kind, new arm `BadHttpRequestException { StatusCode: 413 }` in `DecisionRefusals.For` | 413 |
| Apply with any blocker | `decision-import-blocked` (new), message gives the count and says to run preview | 409 (the `StatusFor` default) |
| Not UTF-8, bad JSON, duplicate keys, wrong shape, unsupported format, count mismatch, bad base64 | `invalid-argument`, with `source` or `mapping` as the parameter | 400 |
| `sourceDigest` or `mappingDigest` differs from the bytes | `decision-conflict` | 409 |
| Stale revision | `decision-conflict` with `currentProjectRevision`, via `RequireRevisionAsync` | 409 |
| Same `requestId`, different command | `decision-request-conflict` | 409 |
| Read-only project, unregistered project | Existing gate refusals | Existing |

Add `decision-import-too-large` to `StatusFor`. `ForDecisionRefusal` needs no change: both new kinds fall to `RequestRejected` (17).

Blocker codes: `unmapped-record`, `unknown-record`, `missing-status`, `unknown-status`, `superseded-status`, `status-on-non-decision`, `unknown-task`, `invalid-identifier`, `duplicate-record`, `duplicate-external-id`, `invalid-content`, `invalid-edge-kind`, `unresolved-endpoint`, `invalid-link`, `changed-source`, `changed-mapping`.

Notice codes: `unsupported-entity`, `unsupported-relationship`, `collapsed-duplicate`, `collapsed-edge`, `already-imported`.

Each list is capped at `MaxIssues`, with the remainder counted in `OmittedBlockers` and `OmittedNotices`. Paths use source positions such as `entities[3].properties.outcome`.

## Parsing, layering, atomic apply

Core holds only DTOs, limits and the status rule. JSON handling and policy sit beside `DecisionService` in Infrastructure, flat in `Decisions/`:

- `SemanticaExport.cs`: `Parse(byte[])` returns entities and relationships
- `SemanticaMapping.cs`: `Parse(byte[])`
- `SemanticaImportPlan.cs`: static pure `Build(...)`, no I/O, taking a pre-minted map of source ID to native decision ID from the service
- `DecisionService.Import.cs`: the partial class with both operations

Source parse, in order. Any failure refuses the whole file:

1. Strict UTF-8 decode (`new UTF8Encoding(false, true)`).
2. `JsonDocument.Parse` with `AllowDuplicateProperties=false`. Wrap `JsonException` as `ArgumentException(..., "source")`.
3. The envelope has exactly `format` and `data`. `format == "json"`. `data` is a string.
4. Parse `data` with the same strict options. The outer parse sees only a string, so this second parse is required.
5. The inner object has exactly `entities`, `relationships` and `statistics`.
6. `statistics` has exactly `entity_count` and `relationship_count`, and they equal the array lengths. There is no version field, so this exact-shape check stands in for "unsupported schema version" and also catches truncation.
7. Each entity has string `id` and `type`. Each relationship has string `id`, `source_id`, `target_id` and `type`. IDs pass `DecisionText.Identifier`.

Extra fields on a record are allowed and kept in the raw payload.

Field limits are not duplicated. The planner constructs `DecisionContent` and catches `ArgumentException` as `invalid-content`, so overlong fields appear per record in the report, and apply refuses.

Duplicates:

- Same source ID with identical raw text: `collapsed-duplicate`, one row.
- Same source ID with different raw text: `duplicate-record`, a blocker.
- Two original IDs with one `externalId`: `duplicate-external-id`, a blocker.

Edges:

- For a type listed in `edgeKinds`, both endpoints must be decision entities in the file, otherwise `unresolved-endpoint`.
- An endpoint already imported resolves through the import row's `decision_id`.
- `contradicts` is canonicalized by ordinal order, as `LinkAsync` does.
- Self-links and `depends_on` cycles go through `DecisionGraphRules.EnsureCanLink`, incrementally against live plus planned edges, and become `invalid-link`.
- A repeat of a live or planned triple becomes `collapsed-edge`. Its import row is still written.

Existing rows for this namespace and `externalId`: same digest and same mapping JSON is `already-imported` and skipped; a different digest is `changed-source`; different mapping JSON is `changed-mapping`.

Apply flow:

1. Refuse if either array exceeds `MaxBytes`.
2. Compute digests with `SHA256.HashData` and `Convert.ToHexString`. Compare `command.SourceDigest` and `command.MappingDigest` to the computed digests, ignoring case. A changed mapping can turn previewed edges into retained rows with no blocker, so apply is bound to the mapping that was previewed.
3. Parse both files.
4. Build `new DecisionWrite(mapping.LegacyTaskId, sessionId, requestId, actor)`.
5. Call the existing `MutateAsync` with operation `"import-semantica"`, payload `{ sourceDigest, mappingDigest, expectedProjectRevision }`, and `requireTask: false`, so an unknown task is reported as a blocker rather than a 404. The payload uses computed digests, so changing either file under one `requestId` refuses.
6. Inside the action, after the replay check: `RequireRevisionAsync` always, then read tasks, imports and edges, then build the plan. Any blocker throws `decision-import-blocked`, which rolls back.
7. Write in source order: `AddRecordAsync`, `AddVersionAsync`, `AddImportAsync` per decision, then edges, then the remaining import rows. Per-record attribution uses `new DecisionWrite(taskId, session, request, actor)`.
8. Return `Mutation` with `Changed = plan has any new row`.

`InWriteTransactionAsync` gives all-or-nothing. A successful import raises the revision by exactly one. Applying the same file with a new `requestId` and a fresh preview revision writes nothing and returns `replayed: true` through the existing `!Changed` path.

Preview runs the same parse and `Build` inside `store.ReadAsync`, so its revision and report come from one snapshot. It shares one private loader with apply. Minted IDs are discarded.

## Content mapping and retention

| Native field | Source | If absent |
|---|---|---|
| title | `records.*.title`, else entity `text` | `invalid-content` |
| category | `properties.category` | `invalid-content` |
| scenario | `properties.scenario` | `invalid-content` |
| outcome | `properties.outcome` | `invalid-content` |
| reasoning | `properties.reasoning` | `invalid-content` |
| confidence | `properties.confidence`, JSON number in 0..1 | null. Non-number: `invalid-content` |
| maker | `properties.decision_maker` | null |
| status | Mapping only, never `outcome` | `missing-status` |
| alternatives, consequences, sources, validFrom, validUntil | Not mapped | Empty or null |
| task | Mapping | `legacyTaskId` |
| recordedAt, createdAt | Server clock | Not applicable |

Read only `properties`, with no fallback to `metadata`; `metadata` stays in the raw payload. `timestamp` and `recorded_at` stay raw only, because client timestamps never determine order. Fixture record `33da32fb` has `outcome: "rejected"`, the case that proves status is not inferred. Edge `reason` has no source, so use a provenance statement: `Imported from Semantica {namespace}/{externalId} ({type}).`

`decision_imports` holds one row per unique source record:

- `decision_id`: set for promoted decisions, null for everything else, including promoted relationships
- `task_id`: the resolved task
- `raw_payload`: the record's `GetRawText()`, verbatim
- `source_digest`: uppercase hex SHA-256 of that raw text
- `mapping`: compact JSON `{originalId, recordKind, type, externalId, taskId, status?, title?, edgeKind?}`

The mapping column must not contain the file digest or minted IDs, otherwise re-importing a newer export falsely reports `changed-mapping`.

Fixture expectations with the mapping fixture (`db05` accepted, `120d` accepted, `33da` rejected, `c1d5` proposed):

- 4 decisions and 3 edges
- 22 import rows: 4 with `decision_id`, 18 null
- 5 `unsupported-entity` notices and 10 `unsupported-relationship` notices (`CAUSED`, `reviewed_by`, 4 `belongs_to`, 4 `made_by`)
- Revision raised by 1
- Fixture digest `290A36C00C2DDE2CBA4C32243E71F5244D9FEA0D8F8F17B5560AD475B74F5A11`

## CLI

```text
ai-raccoon decisions import-semantica --project-id ID --input PATH --mapping PATH
    [--apply --request-id ID [--session-id ID] --source-digest HEX --mapping-digest HEX --expected-project-revision N]
```

- Without `--apply`, it previews. With `--apply`, all four companion flags are required. Companion flags without `--apply` are a usage error. Both cases exit `Usage.InvalidValue` (10) with nothing sent.
- `--session-id` is optional, as on `register` and `record`, and defaults to the request ID, so a retry with the same `--request-id` hashes identically and the shared `Replay` text stays valid.
- Check `FileInfo.Length` before reading. Oversized: exit 10, nothing sent. IO failure: `Environment.IoFailed`, as `RecordAsync` does.
- Stdout is the JSON report or outcome via `WriteFlatAsync` or `SendWriteAsync`. Diagnostics go to stderr through `TerminalText.Visible`.
- Preview with blockers: report on stdout, stderr gives the count, exit `RequestRejected` (17).
- Clean preview: exit 0, and stderr prints the exact apply command with both digests and the revision filled in.
- Both client calls pass a null deadline, like export. Apply passes the `Replay` text.
- Add `ImportPreviewAsync` and `ImportApplyAsync` to `IDecisionClient`, `DecisionHttpClient`, `DirectGraphDecisionClient`, `FakeDecisionClient`, `ServerSettingsStore` and `LazyServerSettingsStore` (which forwards through `AsDecisionClient`).
- Add the `["decisions", "import-semantica"]` case in `ConfigCommands.cs:148-153` and the command in `CliCommandTree.DecisionsCommand`.

## Steps (one lane, sequential, test first)

Build gate for every code step: `dotnet build AiRaccoon.slnx`. Run tests with `dotnet exec <tests>.dll --filter-class <class>`. New Integration tests use `[RetryFact]` and class-level traits. Stage by path and push after each commit.

| id | Goal | Files | depends_on | Acceptance criterion | Gate |
|---|---|---|---|---|---|
| S1 | Ship fixtures and fix the contract | csproj `<Content Include="Resources/Semantica/*.json" CopyToOutputDirectory="PreserveNewest"/>`; new `Resources/Semantica/export-graph-json-0.7.0.mapping.json`; `api.md`, `data-model.md` | none | Fixture and mapping exist in test output; `api.md` names one supported shape and the full CLI line; `data-model.md` states the import creation rule | Build, then `ls` the output `Resources/Semantica` |
| S2 | Core types and status rule | `DecisionImports.cs`, `DecisionErrorKinds.cs`, `DecisionState.cs` (`public static bool Importable(DecisionStatus)`), `docs/reference/agent-memory-server.md` (a row per new kind) | S1 | `Importable` is true for exactly proposed, accepted, rejected, deprecated | T1, plus `ToolRefusalsTests.DocumentedPrefixes_MatchCodeExactlyInBothDirections`, because the new kinds join `DecisionPrefixes` |
| S3 | Strict parsers | `SemanticaExport.cs`, `SemanticaMapping.cs` | S2 | The fixture parses to 9 entities and 13 relationships; every malformed case refuses as `ArgumentException` | `SemanticaParsingTests` |
| S4 | Pure planner | `SemanticaImportPlan.cs` | S3 | Fixture plan matches the expectations above, and each blocker code has a test | `SemanticaImportPlanTests` |
| S5 | Service operations | `IDecisionService.cs`, `DecisionService.Import.cs` | S4 | Preview writes nothing; apply is atomic, replay-safe and raises the revision by one | `DecisionImportTests`, including `DecisionImport_DryRunReplayAndUnknownData` |
| S6 | HTTP routes | `DecisionEndpoint.cs`, `DecisionRefusals.cs`, `DecisionRequests.cs`, `DecisionOperations.cs` (`HttpOnly`), `DecisionApiTests.cs` | S5 | Both routes are mapped, kinds and statuses match the refusal table, tool count stays 43 | `DecisionImportApiTests`, `DecisionApiTests`, `McpToolInventoryTests`, `McpToolContractTests` |
| S7 | CLI verb | `DecisionCommands.cs`, `CliCommandTree.cs`, `ConfigCommands.cs`, client files, `ServerSettingsStore.cs`, `LazyServerSettingsStore.cs`, `FakeDecisionClient.cs` | S6 | Preview then apply works against a real server and exit codes match the CLI section | `DecisionImportCliTests`, `DecisionCommandTreeTests`, `SettingsCommandTreeTests` |
| S8 | Rehearse on a bank copy | No source change; results go in `implementation-evidence.md` | S7 | Source and mapped decision counts, edge counts, raw row counts and at least one refusal are recorded; restart keeps the rows | Manual run, commands and output recorded |
| S9 | Docs and release | `docs/how-to/record-project-decisions.md`, `docs/reference/cli-reference.md`, `docs/adr/0142-*.md`, `implementation-evidence.md`, `README.md`, `VERSION`, `docs/changelog/2.5.0-*.md`, `docs/changelog/README.md`, `DecisionEndpoint` summary | S8 | ADR-0142's "Import preservation is not met" is replaced by what shipped; each T row has a recorded red and green | `AdrIndexTests`, repo-wide `NoConfigureAwaitTests` and `RetrySurfaceGateTests`, whole-solution build |

S8 cautions: use the bank copy under `~/ai-raccoon-eval`, delete its `watches` table first, run with `--idle-timeout 0`, always pass `--port` and never use 7721. If the copy is unavailable, use a fresh scratch bank and say so in the evidence.

ADR: amend ADR-0142 with a "What shipped" paragraph rather than writing a new ADR.

VERSION: a feature, so 2.5.0. Merging a bump auto-cuts a public release, so timing is decided at S9.

## Test design

| # | Test (class) | Failure mode | Mutation that proves it |
|---|---|---|---|
| T1 | `Importable_AllowsOnlyMappableStatuses` (`DecisionStateTests`) | `superseded` imported without its edge | Return true for `Superseded` |
| T2 | `Parse_RealFixture_ReadsEveryRecord` (`SemanticaParsingTests`) | Inner `data` string not parsed | Return empty lists after the envelope check |
| T3 | `Parse_DuplicateKey_InEnvelopeInnerDataAndMapping_Refuses` | Duplicate hides inside the `data` string or `properties` | Parse the inner string with default options |
| T4 | `Parse_InvalidUtf8InsideString_RefusesAsArgument` | Bad bytes become a 500 | Remove the strict decode |
| T5 | `Parse_UnsupportedShape_Refuses` (`format` not json, extra key, normalized shape, count mismatch) | An unsupported export imports silently | Drop the exact-key check; separately drop the count comparison |
| T6 | `Plan_Fixture_MatchesExpectedCounts` (`SemanticaImportPlanTests`) | Records lost or over-promoted | Promote every relationship |
| T7 | `Plan_NeverInfersStatusFromOutcome` | `outcome: "rejected"` drives status | Fall back to `outcome` when status is missing |
| T8 | `Plan_OverlongOrMissingContent_BlocksPerRecord` | Truncating or defaulting required text | Substitute `text` for a missing `reasoning` |
| T9 | `Plan_MappedEdgeToNonDecision_Blocks` and `Plan_UnmappedCaused_RetainedNotPromoted` | Dangling edge, or unknown type becomes causal | Skip the endpoint check; default unmapped types to `depends_on` |
| T10 | `Plan_DuplicateRules_CollapseEqualRefuseDifferent` | Differing duplicate dropped silently | Compare by ID only |
| T11 | `Plan_DependsOnCycleAndContradictsOrder` | Cycle reaches the store, or `contradicts` not canonical | Remove the `EnsureCanLink` call; remove the swap |
| T12 | `DecisionImport_DryRunReplayAndUnknownData` (`DecisionImportTests`) | Preview writes; replay duplicates; unknown data lost or promoted. Flow: dump tables, preview, tables equal; apply; same `requestId` replays with `replayed: true`; rows equal; `raw_payload` for `person-1` and `CAUSED` equal source text; no `CAUSED` edge; export `importRecords` holds 22 rows | Write in preview; skip the receipt; store parsed instead of raw text |
| T13 | `Apply_FailureMidWrite_LeavesEveryTableUnchanged` | Partial import commits | Decorator throws on the Nth `AddImportAsync`; mutation commits per record |
| T14 | `Apply_StaleRevision_SourceDigestMismatch_MappingDigestMismatch_ChangedSource_ChangedMapping_Refuse`, plus `Apply_NewerExport_AddsOnlyNewRecords` | Stale or altered input applied, or a newer export blocked | Remove `RequireRevisionAsync`; drop each digest compare; put the file digest in the mapping column |
| T15 | `ImportRoutes_AcceptMaxSizeAndRefuseOverLimit` (`DecisionImportApiTests`) | A legal 10 MiB pair refused; oversize returns an unkinded error. Settles H1. | Remove the `BadHttpRequestException` arm; lower the limit constant |
| T16 | `ImportRoutes_RefusalKindsAndGate` (read-only apply refused, read-only preview allowed, unregistered project, 409 blocked) | Import bypasses access mode | Use `AccessRequirement.Read` on apply |
| T17 | `HttpRoutes_MapEveryOperationOnce` (updated) plus `McpToolInventoryTests` | A tool or route appears silently | Add a route without a row; add a tool |
| T18 | `ImportSemantica_PreviewThenApply_AgainstARealServer` (`DecisionImportCliTests`) | CLI output cannot drive apply, or exit code is wrong | Omit the digest from stderr; return 0 on blockers |
| T19 | `ImportSemantica_ApplyWithoutCompanions_SendsNothing` and `_RetryWithSameRequestId_Replays` | Partial flags send a write; retry conflicts | Drop the flag check; generate a session ID |
| T20 | `ImportSemantica_OversizedFile_SendsNothing` | 10 MiB limit enforced only by the server | Remove the `FileInfo.Length` check |

CLI tests use real argv, because option names keep their `--` prefix at `GetValue`.

## What was cut

The normalized shape, any MCP tool, a schema migration, task creation during import, a mapping `schemaVersion`, a preview token or stored preview, custom edge reasons, per-row edge IDs, import of `superseded`, a CLI text format, a mapping generator command, and a separate cycle algorithm.

Kept beyond the literal contract: the `title` override (long scenarios cannot import otherwise), the `changed-mapping` blocker (a corrected status would be silently ignored otherwise), and the Kestrel 413 arm (the kind would be missing for non-CLI callers).

## Amendments from plan review

Malformed input (every case is a 400 or a per-record blocker, never a 500):

- `Guard.IsNotNull` on `source` and `mapping` before any length read. A `{}` body is `invalid-argument`.
- All JSON strings are read through one helper that checks `ValueKind` and converts `InvalidOperationException` from `GetString`. In the parsers it becomes `ArgumentException` naming `source` or `mapping`. In the planner it becomes `invalid-content`. This covers lone-surrogate escapes in `data` and in `properties`, and wrong-typed fields such as `properties.category: 5`.
- A decision entity with missing or non-object `properties` is `invalid-content`.
- T4, T5 and T8 gain a row for each of these cases.

Identifiers: a bad source `id` refuses the file. A bad `externalId`, `taskId` or `sourceNamespace` in the mapping is the per-record `invalid-identifier` blocker, so the mapping parser reads them as plain strings. `legacyTaskId` follows the same rule.

Tasks: `legacyTaskId` is always checked, whether or not a record uses it, so an empty export on a fresh project is `unknown-task` and never reaches the `CHECK(committed_revision > 0)` receipt failure. Test it with the empty file.

Edges:

- `contradicts` is canonicalized on native IDs, matching `LinkAsync`. T11 pins this with a fixed ID map.
- For a type listed in `edgeKinds`, an endpoint must be a decision entity in this file or an already-imported decision resolved through its import row, otherwise `unresolved-endpoint`. Mapping entries for endpoints that are not in this file are not required.
- A relationship of an unlisted type whose endpoint is not in the file gets an `unresolved-endpoint` notice and is retained, because the dry run must report unresolved endpoints.

Added tests:

- New `requestId` on already-imported data: no new rows, same revision, `replayed: true`.
- Stale revision on a no-op apply conflicts. This is stricter than `LinkAsync` on purpose.
- `RecordedAt` and `CreatedAt` equal the fake clock, not `timestamp` or `recorded_at`.
- T12 exports with the `all` selector, because the default selector only returns the latest task.
- T6 pins `Retained = 15`: 22 rows minus 4 decisions minus 3 edges.
- T19 asserts the printed retry command parses.

CLI: a clean preview generates a request ID and prints it in the apply command.

S8 also covers precedent lookup, task selectors and graph traversal after restart, and records each refusal case rather than one. It times a synthetic file near the 10 MiB limit against a 10-second budget for the write transaction. If that fails, build the plan in `ReadAsync` and have the write phase re-check the revision and only insert.

S9 runs `review-tests` by someone other than the author. It keeps ADR-0142's guidance that operators keep their Semantica configuration and how to roll back.

Out of scope here: the rest of `api.md:77` (ai-badger prompts and skills, scaffold refresh, disabling autosave, retaining old configuration). It is the separate framework PR that `implementation.md` names.

This work does not close #924. The issue still asks for the normalized shape, so the owner decides whether to narrow it.

The review's optional cut of `decision-import-too-large` is declined: callers other than the CLI need a kinded 413 on the Kestrel path.
