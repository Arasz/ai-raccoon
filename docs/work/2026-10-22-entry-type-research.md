# 2.1.0 entry types and typed vector partitions

## Consolidated scope after owner corrections

This block supersedes the older provenance-based recommendations below. PR #912 (`6acc4b3`) supplies the existing EntryType, writer declarations and v20 migration and is incorporated into this task branch without releasing it. Writers declare Memory/File; migration and legacy fallback classify by nonempty source_file equal to path. A note citing a file remains Memory. Repairs preserve the declared kind. The owner requires an injected `IEntryTypeClassifier` and one resolve method call that encapsulates stored-kind/fallback handling.

Keep get_entry's shipped response `entryType` as corpus (`memory` or `code`). Add `entryKind` for the Memory/File/Code taxonomy so File can be requested and reported without silently changing that legacy field. The input entryType is a typed flags enum: SDK 2.2.0 accepts `Memory, File` and `All`; validate zero, unknown bits and composite lookup arguments in the application. Scratch SDK schema/binding evidence is `/private/tmp/air-21-sdk-probe/result.txt`.

The owner's FTS A/B found raw stopwords in long-query OR plans; a control sharing no content word matched on `a` alone in both 1.63.0 and 2.0.0. The earlier report's claim that keywords could not carry that query is invalid and must not support any 2.1.0 claim. FTS normalization changes are outside this task.

Observed in this session: the old catalogue calls memory_search and receives `Unknown tool: memory_search` after the server upgrade. Release notes must require clients to reconnect and warn that cached agent instructions can name removed tools; the refreshed hook matcher targets query and does not gate stale memory_search calls. Document the forward-only bank migration and 2.0.0 incompatibility after upgrade.

Objective remains full 2.1.0 entry-type parameter, Memory-only TTL, native typed partitioning and measured impact. Use the pinned read-only backup at `/private/tmp/air-2.1-impact/source/memory.db` (25,340 embedded entries, integrity/count/hash checks passed). Preserve existing historical File TTL during migration/sweep, reject future File setter calls including clear, and normalize File TTL on future imports/recreated rows. Wrong-project hashes remain indistinguishable from absent hashes. No live bank mutation.

Baseline: `0b100b5c`, VERSION 2.0.0. The owner chose the high effort task loop and prefers entry type in the vector partition key. Measure the impact before selecting its encoding.

## Contract

The contract in `docs/plans/2026-10-21-mcp-tool-surface-rename.md` requires Memory, Code and File flags; query defaults to all; get_entry requires a type. Manual and transcript provenance means Memory, file provenance means File. Historical transcripts need the same classification. TTL belongs only to Memory. The simplest lookup contract accepts one type; query accepts all seven nonempty combinations.

## Source evidence

- `MemorySql.cs:168-187` and `SqliteMemoryStore.cs:575-690`: both content and structure KNN restrict ctx before candidate truncation; their lists are merged before final ranking. Typed filtering must happen before truncation in both modalities.
- `MemorySql.cs:959-1012`: context identity has SQL/C# twins and is reused outside vectors. Preserve that identity if introducing a typed vector key.
- `MemorySchema.cs:1388-1428,2528-2553`: schema v19 rebuilds from durable embedding blobs with preserved dimensions. New typed migration must avoid re-embedding and leave Code's independent layout intact.
- `docs/plans/2026-08-08-search-knn-perf.md:62-86`: native partition columns cannot be updated. A previous four-column layout was far slower than one encoded key (build 71,100 vs 245 ms, query 423.26 vs 1.50 ms). This does not prove a two-column layout is slow.
- `docs/adr/0133-ctx-is-the-vec0-partition-key-again.md`: encrypted blob reads and concurrent searches motivated restored ctx partitioning; small partitions increase allocation overhead.
- `SyncService.cs:461-520`: sync imports TTL directly and reconstructs provenance afterward. Old-peer File TTL and transcript imports need coverage.
- `MemorySql.InsertRebudgetEntry` and `ChunkBudgetReconciler.cs:352-368`: rechunking copies TTL and needs the same invariant.
- `MemorySchema.cs:141`: search_quality CHECK still uses memory/code/both. Historical memory means Memory+File; both means all. Preserve grades and follow-through during migration.
- `EntryTools.cs:17-60`: merged get_entry already exists and probes both corpora. Do not repeat the rename work.
- `scripts/src/retrieval_tuning/mcp.py:122-172`: Python caller still sends kind.

## Measurement and open hypotheses

Compare current ctx, ctx plus native type partition, and composite context/type key. Prove typed top-k and unrestricted ordering against an independent exhaustive distance oracle, including minority starvation and both vector modalities. Native omitted-key and IN behavior are hypotheses until probed against shipped sqlite-vec 0.1.9.

Use a consistent read-only backup of the live bank where feasible; never mutate the live bank. Pin corpus hash, actual distributions, dimensions, versions and build revision. Existing make_memory_copy.py supplies backup patterns; encrypted banks require the repository connection/key provider. Report natural distribution and labelled skew separately.

Measure warm serial and 20-concurrent typed/unrestricted queries, SQL and caller latency separately, p50/p95 and raw samples, recall/counts, migration/rebuild duration, database and vector allocation growth. Use deterministic vectors for correctness and timing observations without timing assertions.

## Acceptance design

Tests must catch: provenance-based transcript backfill; default query losing Files; either vector leg missing type restriction; minority starvation; scope leakage; required lookup type and wrong-corpus lookup; File TTL through setter, rechunk and sync; historical quality misclassification; lost structure vectors; stale partitions after source/context changes; destructive migration replay. Each test names its contract oracle and a mutation that must fail.

Three independent read-only research lanes supplied partition, contract and measurement findings. No measurements or test results are claimed yet. Semantica decision recording was refused by the tool approval policy; this record retains the owner's decision.
