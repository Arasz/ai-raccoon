# Our retriever — block diagram (memory search path, base 95b612c2)

`memory_search` → `SqliteMemoryStore.SearchAsync` → `ExecuteSearchPipeline`.
Code: `src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.cs`
(`:174 SearchAsync`, `:595 ExecuteSearchPipeline`). All defaults: limit 8, floor 0.6
(`SearchDefaults`), rrfK 60, weights 1:1, sourceLambda 0.1, threshold 0.1, Max
(`SearchParameterSettingsKeys`), structureAlpha 0.5.

```mermaid
flowchart TB
    Q[memory_search<br/>projectId + query + limit] --> PARAMS[SearchParameters.FromSources<br/>per-call → retrieval.* setting → default<br/>bad setting reads as null, never crashes]
    Q --> PLAN[FtsQueryNormalizer.BuildPlan<br/>+ AsPathQuery]
    Q --> EMB[embedder.EmbedQueryAsync<br/>SFR-ONNX CLS-pool, no norm<br/>empty if unconfigured]
    PARAMS --> PIPE

    subgraph PIPE[ExecuteSearchPipeline — one connection]
        direction TB
        CTX[SearchContexts.ResolveAsync<br/>shared + project + custom + workspace]

        subgraph PERCTX[per context]
            direction LR
            FTSQ[FTS5 BM25<br/>QueryFtsBatchAsync<br/>+ fallback]
            VECQ[vec_entries KNN<br/>+ vec_structure KNN<br/>QueryDualVectorsAsync]
            VECQ --> SFUSE[StructureFusion.Rank<br/>α·content + 1−α·structure<br/>α default 0.5]
        end

        CTX --> PERCTX
        PERCTX --> DEDUP[ModalityCandidates<br/>cross-context CONTENT dedupe<br/>project copy wins]
        DEDUP --> LEGF[fts leg: ByBm25]
        DEDUP --> LEGV[vector leg: ByCosine]

        LEGF --> FUSE[ReciprocalRankFusion<br/>.FuseWithEvidence<br/>score = Σ w/k+rank, k=60<br/>max-normalized to 1.0]
        LEGV --> FUSE

        FUSE --> MERGE[SearchResultMerger.Merge]
        subgraph MERGE
            direction TB
            RF[unit-weight positional RE-fusion<br/>k+1 over k+rank — magnitude discarded<br/>ADR-0058]
            AFF[SourceAffinityRanker.Rank<br/>+λ·sibling boost, consolidate<br/>adjacent same-file only, renormalize]
            FL[minRelativeScore floor<br/>fraction of top hit, ADR-0047<br/>+ Take limit]
            RF --> AFF --> FL
        end

        MERGE --> ADJ{no-regression flag on<br/>+ ≥2 legs?}
        ADJ -- yes --> REORD[NoFusionRegression.Reorder<br/>order-only] --> MERGE2[Merge 2nd pass] --> SNIP
        ADJ -- no --> SNIP[snippet resolution<br/>FTS snippet wins]
        SNIP --> BUMP[BumpAccess<br/>per-hit writes]
    end

    BUMP --> OUT[SearchResults<br/>rows + timings + FusionDiff<br/>+ EvidenceByHash + Stats]

    subgraph POC[PoC branch only — NOT shipped]
        MMR[MaximalMarginalRelevance.Rerank<br/>ρ=0.1, env-gated<br/>leg → MMR → fusion]
    end
    LEGF -.-> MMR
    LEGV -.-> MMR
    MMR -.-> FUSE
```

Notes:

- Relevance entering `MERGE` is pure rank position (the re-fusion at RF); absolute
  match magnitude is discarded before the caller sees it (ADR-0058, ADR-0078:39).
- Dedup at DEDUP is exact-content only; consolidation at AFF is same-`SourceFile`
  adjacency only. Cross-source near-dupes pass both — the gap MMR was meant to fill
  (P0a found the served population nearly empty: `2026-09-08-mmr-bank-redundancy-probe.md`).
- The PoC leg-MMR (dashed) reorders LEGF/LEGV by `rel − ρ·sim` before FUSE at ρ=0.1;
  measured effect on 5 queries × k=8: answer-chunks 29→11/40
  (`task/air-mmr-leg-level-poc-branch`, `poc/eval-report.md`).
```

```mermaid
flowchart LR
    subgraph LEGEND[legend]
        direction LR
        S[shipshape: production path] --> D[dashed: PoC-only insertion]
    end
```
