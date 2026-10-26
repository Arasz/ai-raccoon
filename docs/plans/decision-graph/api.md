# Decision agent API and CLI

Status: proposed, following the [data model](data-model.md). Names and flags below describe future behavior.

## Common contract

Every request requires explicit projectId. Mutations also require sessionId and requestId; registration/revision/edge writes require taskId. Resolve canonical project and apply the existing gate/access mode before reading replay receipts. Existing read/write/propose mode policy must be reused, not bypassed by a new endpoint; v1 returns the existing write refusal where committed writes are prohibited. Decision DTOs never authorize their actor fields. All records returned belong to the resolved project. REST routes sit behind existing node identity/token middleware.

Use typed DTOs from the same protocol declarations for MCP and HTTP. Both call the same decision application service; CLI uses the established server client/acquisition path and never opens SQLite. Success uses existing ApiEnvelope with structuredContent and its textual equivalent. Data includes projectId, projectRevision and typed result; mutation results include replayed. Errors use stable kinds with parameter paths and remediation, without stack traces or another project's data. HTTP maps validation to 400, missing owned resource to 404, conflicts/invalid transition to 409, forbidden access to the existing guard mapping, and cancellation to the established host behavior. Wrong-project IDs appear not-found, not an existence leak.

Input DTOs reject unknown fields, invalid enum values and mutually exclusive selectors. Typed limits are validated before store work. No tool accepts an unbounded arbitrary graph query. All four MCP annotations must appear explicitly in actual tools/list JSON: readOnlyHint, destructiveHint, idempotentHint, openWorldHint. Every tool declares project-only McpToolScope and is in the generated inventory.

## Tools and matching HTTP operations

`P` below expands to `/decisions/projects/{projectId}`. Path identity replaces projectId in HTTP body, with no second conflicting copy. Each table row is one backend operation and one tool; get and query do not create anything.

| Agent tool | HTTP | Additional inputs | Result |
|---|---|---|---|
| `register_decision_task` | POST P/tasks | taskId, title, sourceRef? | Immutable task metadata and server order |
| `record_decision` | POST P/records | taskId, title, category, scenario, outcome, reasoning, status=proposed, confidence?, maker?, alternatives=[], consequences=[], sources=[], validFrom?, validUntil? | Decision ID and version 1 |
| `amend_decision` | POST P/records/{decisionId}/amend | taskId, expectedVersion, changeReason, complete replacement content (status excluded) | New version, same identity/status |
| `transition_decision` | POST P/records/{decisionId}/transition | taskId, expectedVersion, targetStatus=accepted/rejected/deprecated, changeReason | New version after legal transition |
| `supersede_decision` | POST P/supersessions | taskId, replacementId, replacedId, expectedReplacementVersion, expectedReplacedVersion, reason | Replacement, replaced new version, permanent edge in one commit |
| `link_decisions` | POST P/edges | taskId, sourceId, targetId, kind=depends_on/supports/contradicts, reason, expectedProjectRevision | Edge identity |
| `unlink_decisions` | POST P/edges/{edgeId}/remove | taskId, reason, expectedProjectRevision | Audited historical edge; refuses supersedes |
| `get_decision` | GET P/records/{decisionId} | version? (defaults current), includeHistory=false | Full requested version; optional version history |
| `query_decisions` | POST P/query | query? (1..2000 chars), category?, statuses?, selector?, limit=20 (1..100), afterId? | Current decision summaries, totalMatched, hasMore, nextAfterId, method=fts or exact |
| `find_decision_precedents` | POST P/precedents | scenario (1..2000), category?, limit=5 (1..20), includeInactive=false | FTS-ranked accepted, currently valid decisions by default, raw rank and method; caller must assess applicability |
| `get_decision_graph` | POST P/graph | decisionId, direction=upstream/downstream/both, depth=2 (0..10), maxNodes=100 (1..500), includeContext=false | Nodes, typed directed edges, truncation and counts |
| `export_decisions` | POST P/export | selector? (default last:1), format=json/markdown (default json) | Complete selected snapshot, histories and boundary stubs; HTTP streams, MCP returns bounded content |
| `preview_decisions` | POST P/preview | selector? (default last:1), maxDecisions=20 (1..100), maxEdges=30 (0..200), maxChars=12000 (1000..50000) | Compact decision summaries, direct links and explicit omission counters |

Mutations have annotations `(readOnly=false, destructive=false, idempotent=true, openWorld=false)` because request replay is enforced and history is retained. Reads have `(true,false,true,false)`; reads observe current state and idempotent does not promise identical snapshots. No agent tool imports arbitrary external graphs in v1. `record_decision` accepts Semantica's scenario/outcome/reasoning/category/confidence/maker/validity information, but is not a drop-in alias: native project, task and replay fields are required. `decision_maker` maps explicitly to maker in migration.

Query pagination is a best-effort current view: afterId is an exclusive decision ID boundary, ordered ID ascending, and projectRevision is returned per page. Client detects a changed revision and restarts when it needs a coherent multi-page result; full export is the coherent alternative. FTS matches are filtered before limit; query is literal search text, not raw FTS grammar or SQL. Query status defaults to all, while precedents defaults to accepted and valid at server UTC now. A null query lists current records by ID. Tied precedent ranks break by decision ID. General graph depth 0 returns the seed alone; maxNodes and maxEdges limits preserve endpoint closure by dropping edges with absent endpoints and count omissions. Graph has a fixed server cap of 2000 edges. All truncation reports reason and omitted counts where known; use `unknown` rather than invented totals.

Export has no silent truncation. MCP caps serialized content at 1 MiB; an oversized result returns `decision-export-too-large` with CLI command guidance and no partial success. HTTP/CLI stream to completion in the same read transaction. Markdown is a human-readable rendering of the same snapshot, with full rationale, sources, history and edge list. JSON is the lossless machine format with schemaVersion=1. Neither format fetches source URIs. Export includes selector, selectedTaskIds, projectRevision, tasks, decisions (all versions), edges (including removed history), boundaryNodes and counts. Empty project returns valid empty arrays. Timestamp metadata may vary; records and order at an unchanged project revision do not.

## CLI surface

```text
ai-raccoon decisions task register --project-id ID --task-id TASK --title TEXT [--source-ref URI]
ai-raccoon decisions record --project-id ID --task-id TASK --input decision.json
ai-raccoon decisions get DECISION --project-id ID [--history] [--format json|markdown]
ai-raccoon decisions query --project-id ID [--query TEXT] [--category CATEGORY]
ai-raccoon decisions export --project-id ID [--tasks N | --all | --task-id TASK ...] [--format json|markdown] [--output PATH] [--force]
ai-raccoon decisions preview --project-id ID [--tasks N | --all | --task-id TASK ...] [--format text|json] [--max-decisions N] [--max-edges N] [--max-chars N]
ai-raccoon decisions import-semantica --project-id ID --input PATH --mapping PATH [--apply] [--request-id ID]
```

Export/preview default to `--tasks 1`; `--tasks N` means latest N tasks, not N decisions. `--all` selects all tasks in this project. `--task-id` selects named tasks and may repeat; it conflicts with --tasks/--all. Register tasks at work start, before the first decision. Record requires a registered task and refuses an unknown task; no guessed tracker integration. Task registration has CLI-generated session/request IDs when not supplied. CLI record likewise accepts `--session-id` and `--request-id` for retry control, otherwise prints generated IDs to stderr before sending. Persisting a registration/record input permits explicit retry with those IDs; the CLI never invents current task from working directory. MCP callers supply their actual session ID.

Export defaults to JSON on stdout; diagnostics always go to stderr. A file output is staged next to its destination and atomically published only after full HTTP completion and valid terminal JSON/Markdown completion. Existing destinations refuse unless --force; stdout interruption returns nonzero and consumers must check exit status. Validate file permissions using the existing sensitive artifact convention; export is plaintext even for an encrypted bank and command help says so. No renderer evaluates HTML, ANSI or terminal escape sequences from decision content; escape text and Markdown syntax as appropriate. Preview JSON contains original strings as encoded JSON; text rendering sanitizes terminal controls.

Preview defaults to plain text, suitable for a terminal or compact agent context. It includes selected tasks even when empty, decision ID/title/status, one bounded outcome and rationale excerpt (at most 240 Unicode scalar values each), and a direct edge list. Boundary records are labelled outside selected tasks. Compaction is deterministic truncation, never generated paraphrase. maxChars counts Unicode scalar values of the entire emitted text, including headers and omissions footer. Reserve footer space first; include a decision atomically or omit it, and only include edges with represented endpoints. JSON preview enforces maxChars on its decoded textual fields collectively and reports its separately measured serializedBytes; it does not pretend JSON escaping is free. Truncation metadata includes omitted decisions/edges, abbreviated fields, and selection total. `--all` still has preview display limits; only export promises complete content.

Example shape (IDs shortened solely for display):

```text
Task air-native-decisions (latest 1; project ai-raccoon)
D1 [accepted] Store decisions in the bank
  Outcome: relational decision tables
  Why: reuse encryption and preserve typed links
D2 [superseded, boundary] Watch exported graph JSON
D1 --supersedes--> D2
Shown 1 selected decision, 1 boundary node, 1 edge; omitted 0 decisions, 0 edges.
```

## Migration and ownership handover

`import-semantica` is a one-time compatibility adapter over an authenticated backend import endpoint `POST P/imports/semantica/preview` and `POST P/imports/semantica/apply`, with a shared store/service, not direct CLI SQLite access. There is no generic import or NER/entity tool. Default is read-only dry run with mapping and validation report; --apply needs explicit request ID, source digest and the preview's project revision. CLI sends file bytes, never a backend-local path. Accept UTF-8 JSON up to 10 MiB and reject duplicate keys, overlong fields and unsupported schema versions. Two supported shapes must come from real exporter fixtures: normalized nodes/edges/decisions/metadata and current export_graph JSON envelope. Do not claim other Semantica formats supported until fixture-backed.

Mapping names a source namespace and maps every decision to a task plus every original ID to a stable external ID. Historical tasks may be registered explicitly beforehand in desired chronological order; imported client timestamps never determine order. Unattributed items need an explicit legacy task chosen by operator. Dry run reports unresolved endpoints, duplicates, unknown status/edge kinds and unsupported entities. Unknown material is retained verbatim in decision_imports but not promoted into causal edges; unresolved required endpoints block apply. Native statuses require explicit mapping; never infer accepted from an outcome string. Full apply is atomic and replay-safe by namespace/external ID plus digest; changed source under an existing external ID conflicts. Input duplicates conflict unless byte-equivalent and reported as collapsed. Native export import/restore is deferred; v1 JSON export is an archival snapshot, not a promised restore command.

Rehearse against a bank copy; compare source/mapped decision counts, known edge counts, raw preserved records and refusal cases. Then update ai-badger prompts/skills to call native tools. Framework changes belong in ai-badger's source and a separate PR, followed by scaffold refresh; generated AGENTS.md is not hand-edited. Disable Semantica decision autosave only after restart persistence, precedence lookup, task selectors and graph traversal pass. Retain source snapshots and the old configuration for rollback; do not dual-write or delete legacy files automatically. Other Semantica capabilities may remain configured independently.
