# Agent memory capabilities and tiered lifecycle

AiRaccoon's storage model, search pipeline, workspace sandboxes, and memory decay lifecycle.

---

## Storage architecture and scope partitioning

AiRaccoon stores memories in SQLite **banks** based on install scope:

```mermaid
flowchart TD
    subgraph InstallScopes ["Install Scopes"]
        UserScope["User Scope (~/.ai-raccoon/memory.db)"]
        ProjectScope["Project Scope (<project>/.ai-raccoon/memory.db)"]
    end
    
    subgraph Contexts ["Context Partitioning"]
        SharedCtx["shared (Cross-project / Curated)"]
        ProjCtx1["project:repo-alpha"]
        ProjCtx2["project:repo-beta"]
        WorkCtx["workspace:ws-8f92 (Isolated Sandbox)"]
    end
    
    UserScope --> SharedCtx
    UserScope --> ProjCtx1
    UserScope --> ProjCtx2
    ProjectScope --> WorkCtx
```

1. **User scope (`~/.ai-raccoon`):** One global database shared across projects. Projects isolate memories via `project:<id>` context tags.
2. **Project scope (`<project>/.ai-raccoon`):** A dedicated local database bound to a single repository.

---

## Hybrid search pipeline (FTS5 + vec0 + RRF)

When an agent calls `memory_search`, AiRaccoon combines keyword BM25 matches and vector distances using Reciprocal Rank Fusion (RRF):

```mermaid
flowchart TD
    Query["Search Query string"] --> Split{Parallel Search Streams}
    
    subgraph Lexical ["FTS5 BM25 Stream"]
        Split -->|Text Query| FTS["FTS5 Full-Text Match"]
        FTS --> RankFTS["Sort by BM25 Rank"]
    end
    
    subgraph Vector ["vec0 KNN Stream"]
        Split -->|Embedding| Vec["vec0 k-NN Vector Match"]
        Vec --> RankVec["Sort by Cosine Distance"]
    end
    
    RankFTS --> RRF["Reciprocal Rank Fusion (RRF)<br/>RRF_Score = 1/(k + Rank_FTS) + 1/(k + Rank_Vec)"]
    RankVec --> RRF
    
    RRF --> Boost["Apply Retrieval Rating Boost"]
    Boost --> Final["Top K Ranked Results"]
```

---

## Workspace sandbox context lifecycle

Workspace sandboxes let agents test multi-step edits in isolation without touching committed project memories:

```mermaid
stateDiagram-v2
    [*] --> Active: memory_workspace_begin
    
    state Active {
        [*] --> Outbox: memory_write(workspaceId)
        Outbox --> Outbox: Additional Writes
    }
    
    Active --> Committed: memory_workspace_consolidate
    Active --> Discarded: memory_workspace_discard
    
    Committed --> [*]: Promoted to project context
    Discarded --> [*]: Outbox deleted
```

- **Isolated outbox:** Writes with `workspaceId` land in `workspace:<id>`.
- **Consolidation:** `memory_workspace_consolidate` moves selected or all outbox entries into the project context.
- **Discard:** `memory_workspace_discard` deletes the outbox.

---

## Propose and shared promotion tier

Memories can move from project notes into cross-project shared facts:

```mermaid
flowchart LR
    ProjMem["Project Context (project:<id>)"] -->|memory_share| SharedMem["Shared Context (shared)"]
    ProjMem -->|memory_share_extract| ProposeQueue["Propose Queue"]
    ProposeQueue -->|Accepted| SharedMem
    ProposeQueue -->|Discarded| DiscardStore["Persistent Discard Filter"]
```

- **`memory_share`:** Immediately promotes an entry hash to `shared`. Shared entries are visible across projects and exempt from degradation sweeps.
- **Propose queue:** `memory_share_extract` finds candidate memories worth sharing.
- **Persistent discards:** Rejected candidates are recorded permanently so the background extractor never re-proposes them ([ADR-0026](../adr/0026-persistent-discards-and-shared-exclusion.md)).

---

## Memory rating and degradation (Sweep reaper)

To stop unused notes from accumulating, project entries decay over time unless frequently retrieved:

```mermaid
stateDiagram-v2
    [*] --> Fresh: Memory Entry Created
    Fresh --> Rated: Retrieved via memory_search
    Rated --> Rated: Retrieval Rating Incremented
    
    state "Degradation Sweep" as Sweep {
        Fresh --> Aged: Age > TTL & Low Rating
        Rated --> Aged: Age > TTL & Low Rating
        Aged --> Purged: Sweep Reaper Run
    }
    
    Purged --> [*]: Deleted from DB
```

- **Retrieval boost:** Hits in `memory_search` raise an entry's rating.
- **TTL expiration:** Entries given a TTL via `memory_set_ttl` become eligible for sweeps once aged.
- **Sweep reaper:** The background reaper (`ai-raccoon settings sweep`) periodically purges low-rated, aged project entries. Shared entries are protected.

---

## Extensible filetype handlers and JSON overlay

AiRaccoon supports custom file ingestion and JSON overlay extraction ([ADR-0027](../adr/0027-extensible-file-type-handlers-and-json-support.md)):

```mermaid
flowchart LR
    File["Source File (.json, .md, .cs)"] --> Handler{FileType Handler}
    Handler -->|Markdown/Code| Chunker["Text Chunker"]
    Handler -->|JSON Document| JSONOverlay["JSON Structure Extractor<br/>(Extracts keys, schema, nodes)"]
    Chunker --> Store[("SQLite memory.db")]
    JSONOverlay --> Store
```

---

## Related documentation

- [ADR-0004: Dual vector structure signal](../adr/0004-dual-vector-structure-signal.md)
- [ADR-0007: Propose tier](../adr/0007-propose-tier.md)
- [ADR-0018: Promotion scoring v3](../adr/0018-promotion-scoring-v2.md)
- [ADR-0025: The sweep reaper](../adr/0025-the-sweep-reaper.md)
- [ADR-0026: Persistent discards and shared exclusion](../adr/0026-persistent-discards-and-shared-exclusion.md)
- [ADR-0027: Extensible file type handlers and JSON support](../adr/0027-extensible-file-type-handlers-and-json-support.md)
