# Decision graph data model

Status: reviewed contract. The domain model and SQLite schema/store are implemented in draft PR #923; service and public surfaces remain in progress or pending. Read after [research](research.md), before the [API](api.md).

## Ownership and identity

Store decision graph tables in the existing encrypted bank. Use a narrow `IDecisionStore` port and `IDecisionService`; domain types and transitions live in `AiRaccoon.Core/Decisions`, persistence in Infrastructure, application policy in a Decisions service. No new graph database, model download or generic entity registry is required.

All identities include canonical `project_id`. Fold aliases through existing project resolution before every operation, verify registration and access, and refuse the reserved `shared` target. Composite foreign keys contain project ID so a guessed decision/task ID cannot cross the boundary. IDs are UUIDv7 strings minted by the server, distinct from content hashes. Task IDs are opaque, case-sensitive external strings, not minted UUIDs and not trusted file paths. Limit IDs to 200 characters without control characters.

## Relational records

| Table | Key and fields | Constraints and indexes |
|---|---|---|
| `decision_projects` | `project_id` PK, `revision` integer | Revision starts at 0; one increment per successful graph mutation, transactionally serialized |
| `decision_tasks` | PK `(project_id, task_id)`; title, `registered_order` integer, registered_at UTC, source_ref nullable | Unique `(project_id, registered_order)`; allocate order under write transaction, never sort by client clocks or UUID text |
| `decisions` | PK `(project_id, decision_id)`; origin_task_id, current_version integer, created_at UTC | Composite FK to tasks; index `(project_id, origin_task_id, decision_id)` |
| `decision_versions` | PK `(project_id, decision_id, version)`; task_id, project_revision, title, category, scenario, outcome, reasoning, alternatives JSON array, consequences JSON array, confidence nullable, decision_maker nullable, status, valid_from/until nullable, recorded_at UTC, actor, session_id, change_reason nullable on version 1 only, sources JSON array | Composite FKs to decision and task; unique `(project_id, project_revision, decision_id)`; index `(project_id, task_id, project_revision)` |
| `decision_edges` | PK `(project_id, edge_id)`; source_id, target_id, kind, reason, task_id, created_revision, removed_revision nullable, created_at UTC, removed_at nullable, actor, session_id, removal_task_id/reason/actor/session_id nullable as a group | Both endpoint FKs include project; creation and removal task FKs; CHECK removal fields are either all null or all present; unique live `(project_id, source_id, target_id, kind)`; outgoing and incoming indexes by project, endpoint, kind |
| `decision_requests` | PK `(project_id, request_id)`; canonical_payload_hash, result JSON, committed_revision | Persistent replay receipt, same encrypted bank; request ID 1..200 chars; do not prune v1 |
| `decision_imports` | PK `(project_id, source_namespace, external_id)`; decision_id nullable, task_id, source_digest, raw_payload JSON, mapping JSON | Task FK; unique external identity; preserve unsupported source data and mapping, never execute references |

The decisions row is stable identity and current-version pointer. Version 1 is the initial registration. Updates append a full immutable version and move the pointer in the same transaction. This is a decision audit history, not a general event-sourcing framework. Edge removal retains its historical interval. Task rows are immutable after first registration (same ID/title/source replays; changed metadata conflicts). A task is a grouping/provenance record only; task-graph remains the lifecycle owner.

Every version belongs to the task that recorded that revision. Origin never changes. Export/preview select decisions touched by selected tasks (any version, edge creation or removal, using the respective creation/removal task IDs), then show their current version at the read snapshot. Thus revising an old decision during today's task includes it today without duplicating identity. An edge creation/removal selects its source decision only; the target is a boundary unless independently selected by a version or another source touch. Supersession versions the target, so both replacement and replaced decision are selected. Return selected version numbers and touch reasons, so current rationale cannot be mistaken for historical rationale from the selected task.

## Decision content and lifecycle

Title 1..200 characters, category 1..100, scenario/outcome/reasoning each 1..16000; alternatives and consequences each at most 20 strings of 2000 characters. Sources contain at most 20 `{uri, description}` records with lengths 2048/500; references are evidence pointers, never fetched automatically. Confidence is caller-assessed 0..1 or null, never an inferred certainty. Maker and actor are provenance, not authorization. Session ID is required, nonblank, at most 200 characters. Trim required text for validation; preserve accepted text. Dates are RFC3339 UTC instants with valid_until greater than valid_from when both exist. Validity does not silently change status.

Statuses: `proposed`, `accepted`, `rejected`, `superseded`, `deprecated`. Creation allows proposed or accepted. Transitions: proposed → accepted/rejected; accepted → superseded/deprecated. Other transitions are refused. Content amendments append a version while preserving status; rejected/superseded/deprecated are terminal and immutable. Store explicit change reason on each noninitial version (1..2000 characters). A replacement decision carries new rationale instead of rewriting a terminal record. `expectedVersion` prevents lost updates; stale writes return conflict with current version.

Supersession is a single command: accepted replacement A supersedes accepted B. Atomically add A → B `supersedes`, append B's superseded version attributed to the current task, and increment project revision once. Require expected versions for A and B. A remains accepted; no half-state is visible. Several replacements of one target are refused because B is terminal after the first success.

## Typed edges and traversal

| Kind | Direction | Rule |
|---|---|---|
| `depends_on` | dependent → prerequisite | No self-link or cycle within the live depends_on subgraph |
| `supersedes` | replacement → replaced decision | Added only by supersession command; no self-link or cycle; never removed |
| `supports` | supporting decision → supported decision | Directed, self-link refused; cycles permitted |
| `contradicts` | lower ordinal UUID string → higher | Symmetric meaning, canonical storage order, self-link refused |

Require a reason 1..2000 characters and both existing endpoints in the same project. A repeated live link is a replay only with the same reason/task; otherwise conflict. Removing a depends_on/supports/contradicts edge is an audited mutation under project revision, expected project revision and request ID; supersedes is permanent. No arbitrary edge labels v1.

Causal upstream follows outgoing depends_on and supersedes links (why this decision exists); downstream follows incoming links (what depends on or replaced it). Supports and contradicts are context links, shown separately, never silently interpreted as causality. Traverse iteratively with visited IDs, deterministic edge/ID ordering and explicit depth/node limits. Preserve direction in every result.

## Transactions, selection and retention

Use the existing connection factory, migrations and transaction conventions. In one write transaction validate request replay, project revision/version guards, endpoints, graph constraints, append versions/edges, bump revision and persist receipt. Same request ID and same normalized DTO returns the original result without a write; different payload refuses. Hash the serializer's canonical DTO bytes using platform SHA-256; include command name and every semantic argument, including expected versions. Check replay before version conflicts. A crash commits all or none.

For export/preview, task selection is one of `last:1` (default), `last:N` (1..1000), `all:true`, or explicit `taskIds` (1..100 unique IDs), mutually exclusive. Latest means descending registered_order; edits do not reorder tasks. No registered tasks produces an empty successful result; a named missing task refuses. Fewer than N tasks selects all available and reports count. Tasks without decisions remain present. `all` includes every task and every decision, including terminal decisions; it never drops old data by TTL.

Each read/export runs in a single SQLite read transaction and returns projectRevision. Order tasks newest first and decisions by ID, versions by version, edges by source/kind/target/ID. Export is a full snapshot for that selector, not a delta or a promise to recreate an arbitrary historical revision. V1 does not page full export: stream it over HTTP in one transaction. Use request cancellation and a documented server read timeout; aborted output is incomplete and never published as a successful file.

A selected decision's direct links can point outside selection. Export includes those edge records and minimal boundary nodes `{decisionId,title,status,currentVersion,boundary:true}`, never recursively expands them. Include historical incident edges and all versions of selected full decisions; boundary nodes remain stubs. Include full selected task records plus referenced-task stubs for every origin/version/edge/import task outside selection. Export relevant decision_imports rows for selected decisions or selected tasks; all:true includes every import row, including unsupported entities. Imported records always carry an explicitly mapped task. No dangling endpoint IDs. Preview includes live direct links only; no transitive expansion. Boundary nodes remain inside the same project. Separate `selected` and `boundary` membership in the response.

## Integration boundaries

V1 rejects workspaceId and shared scope. Native decision writes are explicitly committed, including proposed decisions; tools say so. They do not participate in workspace consolidate/discard or promotion. General `query` and EntryType are unchanged: decisions have dedicated exact/FTS retrieval. Precedent search uses deterministic FTS over current decision fields, reports method `fts`, and returns rationale/source links without claiming calibrated semantic similarity. Optional hybrid search is follow-up work.

Cloud decision sync is deferred. Add all decision tables and decision FTS shadow tables to snapshot exclusion on every push path; pull never merges them. Export/explicit import is portability, not automatic cloud sync. Project-ID repair v1 refuses before any mutation whenever a retired/loser project has any graph rows (including tasks, receipts or imports), whether or not the canonical winner has a graph. Report occupied graph families and retain both graphs unchanged; do not rewrite order, revision or replay namespaces. A graph-free loser can fold into a winner with a graph without changing that graph. Combining graph histories requires a later explicit merge design; export is archival and is not an available graph-merge workaround. This conservative gate avoids ambiguous registration order and stale revision collisions. Project removal and bank reset must enumerate these tables. Logical archive preserves history; project deletion follows existing destructive access policy and deletes owned graph rows in FK-safe order. Backup/rekey include the graph because it lives in the bank.

## Simpler alternatives considered

JSON memories would be smaller to implement but cannot enforce endpoint ownership, atomic supersession or stable IDs. A general RDF/entity graph adds an ontology and import burden to a decision-specific requirement. A separate SQLite file repeats encryption, backup and connection lifecycle. The relational decision-only graph is the smallest shape that preserves these guarantees.
