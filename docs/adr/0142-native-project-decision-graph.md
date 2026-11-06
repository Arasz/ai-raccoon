# ADR-0142: AiRaccoon owns a persistent project decision graph

Date: 2026-10-26

Status: Accepted 2026-11-04 for 2.4.0. Semantica import is deferred to #924, and the agent framework's switch to the native tools is a separate change.

## Context

Agents need durable decisions, rationale, precedents and causal links across sessions. The historical Semantica bridge exported JSON for AiRaccoon ingestion. That preserves searchable text but does not give AiRaccoon typed graph constraints, stable decision identity or task-scoped export and preview. Current Semantica tool declarations also lack project/task arguments on decision registration.

## Decision

Make AiRaccoon the primary owner of project decision registration and graph retrieval. Store stable decisions, immutable content versions, typed edges, task provenance and replay receipts as relational tables in its existing encrypted bank. Require canonical project ownership on every key and endpoint. Serialize project repair preflight and rewrites under one SQLite write lock; decision writes recheck registration inside their acquired transaction so an already-authorized queued writer cannot recreate a retired owner. Nested bank writes use savepoints. Keep task metadata limited to attribution and ordering; task-graph retains execution state.

Expose one decision service through matching HTTP operations and thin MCP tools. CLI calls HTTP and never writes the bank. Support registration, amendments, legal lifecycle transitions, atomic supersession, explicit links, exact/FTS queries, precedents and bounded causal traversal. Agent tools declare all four annotations and project scope. A decision graph is separate from Memory/Code/File and does not change general query semantics.

Export defaults to the latest registered task and supports N tasks, explicit tasks or the entire project. It preserves selected decisions' history and boundary endpoints. Preview uses deterministic excerpts plus a bounded direct edge list, with omissions reported. “Latest” uses server registration order, including empty tasks; editing an old task never makes it latest.

V1 stores committed project decisions, is independent of embeddings and excludes graph tables from automatic cloud sync. Workspace drafts and shared promotion need later designs. Import Semantica JSON through a dry-run-first, explicit mapping adapter with raw source preservation. Switch framework instructions only after native acceptance gates pass; retain old exports for rollback.

The detailed [data model](../plans/decision-graph/data-model.md), [API contract](../plans/decision-graph/api.md), [evidence](../plans/decision-graph/research.md), [review](../plans/decision-graph/review.md) and [implementation plan](../plans/decision-graph/implementation.md) define the proposed contract.

## Alternatives and consequences

Embedding graph JSON as ordinary memory cannot enforce ownership or atomic supersession. A general entity/RDF graph expands the problem beyond decisions. A second graph database or SQLite file duplicates deployment, encryption and backup lifecycle. Typed tables in the current bank give the required constraints with fewer moving parts.

This adds migrations, graph-aware project repair (it holds back or refuses graph owners; v1 has no project deletion), sync exclusion and a dedicated retrieval surface. It does not replace Semantica NER, generic graph operations or every export format. Proposed and accepted decision records accept amendments; each amendment appends a version with its change reason, so history is never rewritten. Rejected, superseded and deprecated records are terminal and immutable; a changed direction is a new decision that supersedes the old one.

## Acceptance

Independent review covers the model and API before implementation. Release requires restart persistence, isolation, replay safety, atomic supersession, deterministic task selection, complete export, honest preview limits, import preservation, actual MCP discovery checks and CLI/HTTP/MCP parity. Tests include failing mutations and separate test-quality review. No runtime behavior was claimed by the planning change.

### What shipped in 2.4.0

The bank is schema v23 with the decision tables, excluded from cloud sync. `IDecisionService` sits behind 13 MCP tools and the matching `/decisions/projects/{projectId}` HTTP routes, and `ai-raccoon decisions` covers task registration, record, get, query, export and preview through HTTP. Project repair holds back or refuses graph owners, and a held-back id keeps its own identity at the gate.

Each release requirement has a test that was seen to fail under a targeted mutation; the [implementation evidence](../plans/decision-graph/implementation-evidence.md) lists them. `DecisionIntegration_RestartAndAccessModes` covers the cross-surface part. It registers, records and links over HTTP, MCP and the CLI, restarts the server on the same bank, and checks that reads, export and preview agree on every surface in `ro`, `rw` and `full`, and that writes are refused alike in `ro`. It found that MCP refused `depends_on` links, which was fixed before release. Independent test-quality reviews covered the model and service, the adapters and CLI, and the migration and repair suites.

Import preservation is not met. Semantica import is not part of 2.4.0 and is deferred to #924; until it ships, operators keep their Semantica configuration. Rollback restores the previous binary together with a bank backup taken before the upgrade, because a 2.3.0 binary refuses a v23 bank (ADR-0019). The [how-to](../how-to/record-project-decisions.md) gives the steps.
