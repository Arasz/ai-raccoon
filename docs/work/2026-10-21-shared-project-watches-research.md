# Research: Shared project identity for document watches

**Date:** 2026-10-21
**Question:** How should AiRaccoon support watching documents outside any repository through a reserved `shared` project ID, with direct access through that ID and implicit access from every project's memory set?

## Findings

### F1 — Watches require an identity and configured paths, but no repository [READ]

The watch tool passes its project ID through the common gate. The service checks watch enablement, the ingest allowlist and filesystem existence; it does not require a Git repository. The actual obstacle is a usable identity and storage destination, rather than the directory being outside a repository. A blank ID currently resolves from configured scopes/watches or fails.

**Evidence:** `src/AiRaccoon/Tools/WatchTools.cs:21-30`; `src/AiRaccoon/Tools/ToolGate.cs:40-59,69-87`; `src/AiRaccoon.Infrastructure/Watch/WatchService.cs:24-42`; `src/AiRaccoon/Projects/CwdProjectIdResolver.cs:30-70`.

### F2 — A project called shared would still have private project rows [READ]

File ingestion defaults to `project:<projectId>`. Therefore, merely registering a project whose ID is `shared` produces `scope='project', project_id='shared'`. Searches from another project do not include that project's context.

**Evidence:** `src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs:264-275`; `src/AiRaccoon.Infrastructure/Sqlite/EntryBucket.cs:18-22`; `src/AiRaccoon.Infrastructure/Sqlite/SearchContexts.cs:36-55`.

### F3 — The existing shared tier already provides implicit cross-project retrieval [READ]

`scope=all` includes the shared context, and `scope=shared` selects it alone. Its filter selects all `scope='shared'` rows without a project-owner predicate. Shared rows can still carry an originating project ID. Visibility and ownership are separate dimensions. Explicit `scope=project` omits the shared tier.

**Evidence:** `src/AiRaccoon.Core/Memory/SearchQuery.cs:5-9`; `src/AiRaccoon.Infrastructure/Sqlite/SearchContexts.cs:32-64`; `src/AiRaccoon.Infrastructure/Sqlite/Memory/ContextFilterProvider.cs:19-31`; `src/AiRaccoon.Infrastructure/Sqlite/EntryBucket.cs:8-21`.

### F4 — A guaranteed shared identity needs explicit lifecycle support [READ]

The gate applies aliases, access policy, retirement checks and registration. The registration guard has legacy compatibility branches, but an unregistered raw-text ID is not universally valid. Explicit registration of a new non-GUID ID without existing rows is refused by the directory. There is no reserved `shared` branch in these inspected paths.

**Evidence:** `src/AiRaccoon/Tools/ToolGate.cs:40-59`; `src/AiRaccoon/Projects/ProjectRegistrationGuard.cs:24-50`; `src/AiRaccoon.Infrastructure/Sqlite/SqliteProjectDirectory.cs:15-48`.

### F5 — Watch replacement currently passes no shared context [READ]

Watch digestion calls replace-by-path, which passes a null context onward to ingestion. That reaches the project-context default in F2. Initial scans and later changes must agree on the reserved destination. Setting a context only on an initial import would leave later watch replacements using the existing default.

**Evidence:** `src/AiRaccoon.Infrastructure/Watch/WatchDigestExecutor.cs:125-141`; `src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.Replace.cs:23-36,254-270`; `src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs:274-275`.

### F6 — Shared visibility does not imply global deletion rights [READ]

File deletion is already restricted by owner ID and source path, and removes fingerprints with the entries. Removing a watch stops registration and runtime watching; it does not purge indexed content. Public context deletion rejects the shared context. A reserved identity can retain owner-filtered file cleanup without permitting a blanket deletion of every project's shared contributions.

**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/MemorySql.cs:245-262`; `src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.Deletes.cs:47-69,79-108`; `src/AiRaccoon.Infrastructure/Watch/WatchService.cs:80-86`; `src/AiRaccoon.Core/Memory/ContextScope.cs:15-23`.

### F7 — Shared note writes currently request promotion [READ]

At the application service, a non-workspace note with `context='shared'` is rewritten into the caller's project and proposed to the promotion queue. The lower-level entry bucket can represent shared rows directly. The proposed operator-selected document watch therefore needs an explicit contract for direct shared publication; reusing the note promotion request would delay the requested visibility.

**Evidence:** `src/AiRaccoon.Core/Memory/MemoryWriteService.cs:53-84`; `src/AiRaccoon.Infrastructure/Sqlite/EntryBucket.cs:8-15`. The older statement that direct shared note writes remain allowed in `docs/adr/0051-a-context-never-names-another-project.md:52-54` does not describe the current application service.

### F8 — The current document pipeline has a bounded format set [READ]

Registered memory handlers cover Markdown, plain text, JSON and notebooks. Files outside the memory handlers can route into the separate code corpus or be skipped. A shared ID alone will not add PDF or DOCX extraction. The document use case should name supported formats so identity support is not mistaken for a document conversion feature.

**Evidence:** `src/AiRaccoon/Setup/AppRegistrations.cs:299-306`; `src/AiRaccoon.Infrastructure/Ingestion/MarkdownFileTypeHandler.cs:13-16`; `src/AiRaccoon.Infrastructure/Ingestion/PlainTextFileTypeHandler.cs:13-16`; `src/AiRaccoon.Infrastructure/Ingestion/JsonFileTypeHandler.cs:13-16`; `src/AiRaccoon.Infrastructure/Ingestion/NotebookFileTypeHandler.cs:13-16`; `src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs:57-65,84-120`.

### F9 — A reserved target over the existing shared tier is the simplest fit [INFERRED]

From F2-F6, reserve the literal `shared` as a built-in logical project target in the current memory bank. It should work without minting an ID, attaching a repository or relying on legacy compatibility. Keep watch/config/fingerprint ownership under `project_id='shared'`, and route its document chunks into `scope='shared'`. Ordinary projects continue writing into their own scopes. Searches already have the shared-tier inclusion path; no per-project copies or additional bank are needed.

Define this once in backend target resolution and storage routing, shared by REST, CLI, direct ingest and watch processing. Do not put a special case only in the MCP tool. Protect the reserved identity from renaming, alias folding into another project, retirement and repair cleanup. Check for a pre-existing literal `shared` owner before migration; it must not be silently reassigned or published.

### F10 — The public contract should distinguish shared reads from owned mutations [INFERRED]

From F3, F6 and F7, use the following proposed contract. This is a design recommendation, not implemented behavior. Here “all projects” means projects using the same bank; it does not imply every installation or user.

| Operation | Proposed result |
|---|---|
| `memory_watch_add(projectId="shared", path="/absolute/documents")` | Register a shared document watch after normal enablement, allowlist and write-access checks. |
| `memory_ingest_file` / `memory_ingest_directory` with `projectId="shared"` | Ingest supported documents directly into shared scope with the reserved owner. |
| Search with `projectId="shared", scope="all"` or `scope="shared"` | Read the existing shared tier, including previously promoted contributions. Deduplicate contexts/results. |
| Search from project A with default `scope="all"` | Read A's project contexts plus shared documents and existing shared contributions; include its workspace when requested. |
| Search from project A with `scope="project"` | Read A's project contexts only. Preserve explicit narrowing. |
| Search with `projectId="shared", scope="project"` | Recommended: read documents owned by the reserved target, excluding other projects' promoted contributions. Requires explicit scope mapping. |
| Shared watch change, rename or file deletion | Update or remove only the reserved owner's mirror rows and fingerprints. Other projects' contributions survive. |
| `memory_watch_remove(projectId="shared", ...)` | Stop mirroring and retain indexed content, matching current watch removal semantics. |
| Shared watch status | Query directly with the reserved ID. Ordinary project watch status can remain scoped to its own registrations. |

Example intended setup, once implemented:

```sh
ai-raccoon settings ingest scope add shared /absolute/documents
ai-raccoon settings watch enable shared true
ai-raccoon watch add shared /absolute/documents
ai-raccoon watch status shared
```

These command shapes follow `src/AiRaccoon/Prompts/MemoryPrompts.cs:23` and `src/AiRaccoon/Setup/Cli/CliCommandTree.cs:505-519`; reserved-target behavior is proposed. Reuse existing watch configuration and require a supported document type. Keep explicit `projectId="shared"` in examples. Whether omitted IDs should resolve to shared is a separate choice; broadly configured shared scopes could otherwise introduce ambiguity with real projects (F1).

### F11 — Acceptance needs lifecycle and isolation cases [INFERRED]

From F1-F10, these cases would establish the feature. They are proposed gates, not tests executed during this research.

| Acceptance case | Defect it must catch |
|---|---|
| Fresh bank, non-repository directory, explicit shared ID, no minted identity | Dependence on legacy rows, pre-existing registration or a repository. |
| Empty shared target search before any ingest | Reserved target only begins working after its first write. |
| Watch initial scan becomes searchable through shared and projects A/B | Initial chunks remain in `project:shared`, or only one project receives them. |
| Explicit project-only search excludes shared documents | Global inclusion defeats the caller's narrowing. |
| Shared target project-only search returns its owned shared documents | Project-scope mapping loses reserved-owner rows. |
| Change, rename, delete, recreate and restart catch-up | Stale chunks remain, new chunks lose shared scope, or fingerprints suppress re-ingestion. |
| Same source path also stored by project A; shared path removed | File cleanup deletes another owner's rows. |
| Legacy promoted content remains visible through direct shared search | New target creates a disconnected second shared tier. |
| Disabled watch, outside allowlist, read-only write refusal | Reserved identity bypasses existing configuration or access checks. |
| Watch removed, content retained | Watch removal unexpectedly becomes a global content purge. |
| Alias repair, retirement and existing literal shared-owner collision | Maintenance removes the reserved target or silently publishes private legacy rows. |

Each implementation test should be shown failing with its targeted defect before the fix. The simplest version reuses the current tier, watch engine and configuration. A second database, per-project copying or an automatic importer is unnecessary for this use case.

### F12 — Proposed reserved-target behavior has not been exercised [UNVERIFIED]

No implementation was added, and no application tests or live shared watch were run. Research inspected the working tree at `bccdab708e0df97364a6ad1ada6c26c2358fda87`. Connected memory supplied navigation leads; current local source supplied the behavior evidence. The code graph reported an older build commit, so its structural results were not treated as current proof. Runtime parity, performance and migration behavior require implementation and the acceptance cases above.

### F13 — Shared uniqueness can collide across owners [READ]

The shared bucket's unique key is `(path, hash)`, without an owner ID. A design that gives shared watches their own owner must handle an identical path/hash already present through promotion under another owner. Owner-filtered watch mutation alone does not settle which row should represent both sources. Decide whether to track multiple source claims or establish another explicit collision policy; never silently steal ownership. Add an acceptance case that promotes a file chunk from project A, then watches the same unchanged file through shared, changes it and deletes it.

**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:26-33`; `src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs:313-330`.

## Still open

- Confirm the recommended `scope=project` meaning for the reserved target: owned shared documents versus the whole shared tier. Default/all access already has a clear aggregate meaning.
- Decide whether explicit shared-target note writes also publish directly or continue requesting promotion. The document-watch requirement is sufficient to authorize direct watched-document publication, but does not specify note behavior.
- Specify labels, workspace use and code files for the reserved target. The existing code corpus has separate retrieval semantics; document sharing should not silently promise shared code search.
- Inspect deployed banks for an existing literal `shared` owner and decide collision handling before enabling the reserved name. No production content inventory was performed.
- Resolve the promoted-row/watch-row uniqueness collision in F13 before committing to owner-based lifecycle reuse. The recommendation is feasible in shape, but that source-ownership contract remains unsettled.
- Audit every read/mutation surface (get, list, stats, delete, sweep, sync and repair) for reserved-target semantics before implementation is considered complete. This investigation traced search, watch, file ingestion and core deletion paths, not the entire public API.
- Decide whether implicit access must cover more than search. The proposed behavior guarantees the default search memory set; list/stats aggregation and returned provenance need an explicit contract.
- Define supported document types for the first delivery. PDF/DOCX ingestion requires additional extraction work if those are part of the intended directory.
