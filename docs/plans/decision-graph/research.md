# Native decision graph: research record

Date: 2026-10-26. Scope: design and implementation planning only.

## Request and completion criteria

AiRaccoon takes over Semantica's primary decision registration and graph responsibilities. Produce the data model first, then agent tools and CLI contracts, add an ADR, obtain independent review of both, and publish an implementation DAG with acceptance gates. The author does all research and planning; only review is delegated. Completion means reviewed design artifacts and a recorded implementation plan, not shipped runtime behavior.

## Evidence checked

| Finding | Evidence | Design consequence |
|---|---|---|
| Existing memory entries are content/hash-oriented, with Memory/Code/File query types | `src/AiRaccoon.Core/Memory/MemoryEntry.cs`, `EntryType.cs` | Separate relational decision records; do not silently add an EntryType or repurpose hashes as decision IDs |
| Bank schema has a migration ladder and encrypted bank lifecycle | `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs`; ADR-0011 | Add next migration at implementation time, not a hardcoded version in this proposal |
| Only the server owns bank writes | ADR-0075; `src/AiRaccoon/Settings/WatchEndpoint.cs`, `ServerSettingsStore.cs` | Shared decision service; MCP and HTTP adapters; CLI uses server HTTP |
| Tool gate handles scope, registration, access and availability | `src/AiRaccoon/Tools/ToolGate.cs`; ADR-0139 | Every new tool declares scope; HTTP must enforce equivalent gates |
| Workspace is a separate explicit lifecycle | `src/AiRaccoon.Core/Isolation/Workspace.cs`; ADR-0135 | Do not overload task ID as workspace ID; v1 decision writes are explicitly committed |
| Sync starts from whole-bank snapshots and strips local tables | `src/AiRaccoon.Infrastructure/Sync/SyncService.cs` | Explicitly exclude decision tables from sync v1 before enabling writes; otherwise they could leak through snapshots |
| Older bridge description is historical | `docs/work/2026-08-13-semantica-integration-and-auditable-memory-findings.md`, retrieved by AiRaccoon query | Do not describe its tool aliases or export location as current |
| Current exporter writes timestamped per-session files in `.semantica/` and accepts graph envelopes | `.ai-badger/skills/semantica-knowledge-graph/scripts/export_semantica_graph.py` | Import requires reviewed fixtures and explicit ownership/task mapping |
| Current Semantica surface has record/query/precedent/causal tools and generic entity/link/export operations | Live MCP tool declarations checked 2026-10-26 | Take over the decision subset, not NER, arbitrary ontologies or all serialization formats |
| Semantica record accepts category, confidence, outcome, reasoning, scenario, maker, validity dates; no project/task arguments | Live `record_decision` declaration | Preserve business fields; require native project and task provenance |
| Connected graph was ready but empty | Live `get_graph_summary`: node_count=0, decision_count=0 | No evidence that historical persisted graphs are absent; never interpret this as migration completion |
| Docs are already watched | `get_watch_status(projectId=ai-raccoon)` returned healthy repository-root watch | No overlapping watch needed |
| Code graph is stale | Semantic graph result built 2026-10-17 at c8c6a43d, current checkout 2e31a1e4 | Verify implementation seams directly from source |

Memory queries: `Semantica decision registration causal graph persistence`; `decision task export preview graph model`, both scope=all, projectId=ai-raccoon. The first returned the historical bridge record; the second mostly unrelated embedding graphs and is not design evidence.

## Assumptions and boundaries

“Last task” means the latest task registered for this project's decisions, ordered by server ordinal, including a task with no decisions. It does not mean latest completed tracker task or a process-global current session. Explicit task selection handles concurrent sessions. This default is proposed and reviewable.

V1 is committed project data only, independent of embeddings, local-only for cloud sync, with an explicit migration command. Workspace drafts, shared promotion, generic entities, automatic causal inference, full RDF compatibility, and distributed graph merging are deferred. Existing Semantica configuration stays active until native acceptance gates pass.
