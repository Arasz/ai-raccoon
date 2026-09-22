# G2 verification — memory lifecycle + retrieval/ranking (F19, F20, F22–F26)

Scratch environment used for every live re-run: server `ai-raccoon --data-root docs/work/2026-09-22-project-scope-review/verify/g2/data --port 18991 serve` (installed `1.42.5+8a1f1dde`; product code identical to base `5bca1900` — the two later commits are skill-only), MCP over HTTP with that root's token; all SQL read/written only in that root. Every mechanism was also re-derived from the cited source at HEAD before the live run.

### F22 — CONFIRMED
**Method:** `src/AiRaccoon.Core/Memory/MemoryWriteService.cs:57-65` (candidate built without `ScorerVersion` → `PromotionQueue.cs:15` default 0; reason set unconditionally at `:65`), `SharedExtractionRunner.cs:34` (`ClearStaleAsync(projectId, PromotionScorer.Version)`), `PromotionQueueSql.cs:6-13`; live: `memory_write {context:"shared"}` then `memory_share_extract {mode:"propose"}` + `sqlite3 promotion_queue`.
**Result:** Live reproduction is exact: queue row `score 1.0, scorer_version 0, reasons ["agent-requested-share"]`; after one propose `candidates: []` and `count(*) = 0`. A short note is genuinely terminal (CandidateFloor 0.4 in `SharedExtractionService.cs`); a longer note is re-ranked by score and re-admitted as `{score 2.1567, scorer_version 2, reasons ["organic-note", …]}`, i.e. even above the floor the explicit reason is erased. The guarding test uses `FakePromotionQueue`, and the ClearStale tests re-admit only a scorer-eligible stub, so nothing pins the agent-requested case.

### F23 — CONFIRMED
**Method:** `MemorySql.cs:622-631` (`BumpAccess` rating uses `(now - created_at)`), `SweepService.cs:29-43` (reads `metadata.Rating` + `entry.CreatedAt`; `last_accessed_at` has no reader), `ForgettingPolicyService.SetEntryTtlAsync` (does not recompute), `DegradationPolicy`; live on g2p2: write → `memory_set_ttl 7` → backdate `created_at` 200 d, `rating 0.5` → sweeps.
**Result:** Exact: TTL'd 200-day-old entry at stored rating 0.5 → `memory_sweep(dryRun:true) candidates: []`; one search → `access_count 1, rating 0.0054137160` → same sweep lists it; five searches with fresh `last_accessed_at` → `rating 0.0073823499` → candidate; a real sweep deletes both. The plan's own WP1 (`docs/plans/2026-08-09-memory-decay-implementation-plan.md`: "an entry nobody touches keeps the schema default 0.5 forever … SweepService:41 measures age from created_at rather than last use") confirms this is a known-but-live state, not a misread.

### F24 — CONFIRMED
**Method:** `src/AiRaccoon.Infrastructure/Sqlite/ContextResolver.cs:10-17` (explicit context wins), `EntryBucket.For` (custom label → scope `custom`, workspace NULL), `MemoryWriteService.cs:40-51`; live on g2p3: workspace begin W, then `memory_write` with `workspaceId=W` + `context` (both spellings) and `memory_workspace_status`.
**Result:** Exact: `context:"design-notes"` → response `context:"design-notes"`, row `scope=custom, context_label=design-notes, workspace_id=NULL`, outbox `count: 0`; `context:"shared"` → row `scope=project` plus a promotion candidate, outbox still 0. The input validator has no rule against the combination. Nuance only: the `context` parameter description already says "instead of the default project/workspace context", so the doc conflict is less flat than "two descriptions contradict" — the silent part is that an *active* workspace is bypassed with no refusal, disclosed only by the echoed `context`.

### F25 — CONFIRMED
**Method:** `MemoryWriteService.cs:57-65` (reason unconditional, `ProposeOutcome` discarded), `PromotionQueueSql.cs:6-13` (`Upsert … WHERE NOT EXISTS (promotion_discards)`); live on g2p4: write shared → discard → rewrite identical content shared.
**Result:** Exact: write → queue 1; `memory_promotion_discard` → `{discarded:1}`, queue 0, `promotion_discards` 1; the identical rewrite returns `reason: "queued-for-promotion: agent-requested-share"` with queue 0 and no new discard. Nuance: the same envelope's `meta.waitingPromotionsCount` reads 0, so the contradiction is visible to a caller who inspects meta — but the `reason` field itself asserts queue state that does not exist, which is the filed claim.

### F26 — CONFIRMED
**Method:** `SqliteMemoryStore.WriteAsync` chunk loop (~:122-131 inserts every chunk, returns `ToEntry(chunks[0])`), `MemorySql.DeleteByHashAndProject` (`:164-169`), `MemoryTools.Delete` (hash-only) / `MemoryTools.cs:38-46` (only hash/context delete verbs); live on g2p5: 140-paragraph write, then `memory_delete`, then `memory_search`.
**Result:** Exact on the mechanism; N depends on chunking (my 28,278-char write chunked into 28 rows vs the finding's 140): one returned hash/path for 28 rows, `memory_delete(hash)` → `{deleted:1}`, rows 28→27 and hash row 0, and a follow-up search returned 8 hits all under the same `08e9039886ea…` path. The reference documents `memory_delete` per-hash (`docs/reference/agent-memory-server.md:52`), and the write result carries no chunk count/hashes, so the asymmetry is as filed.

### F19 — CONFIRMED
**Method:** `StructureFusion.cs:25-33` (`Fused` = `alpha*content + (1-alpha)*(structureSim ?? 0)`), `SqliteMemoryStore.cs:837-853` + `:860-865` (fused score → `Ranking`), `ReciprocalRankFusion.cs:67-70, 105-107` (`Ranking` → evidence `Cosine`); live on g2p6 with local engine: entries A (`content == query "source affinity ranking"`) and B, `vec_structure_rowids = 0`, then `memory_search` + stored-blob cosine.
**Result:** Exact: evidence cosine A = 0.5, B = 0.38187070; stored embeddings give `cos(A,A)=1.0` and the query-content cosine for B `cos(A,B)=0.76374149` (A's text is the query) → ratios 0.5000000 and 0.4999999. `fusionStats`/`legs` name only fts/vector, so nothing distinguishes a structure-absent row; the tool description calls the field "the fused vector similarity", so the label documents fusion but not the halving.

### F20 — CONFIRMED
**Method:** `SearchDefaults.cs:13-14` (limit 8, floor 0.6), `ReciprocalRankFusion.cs:83-92` (floor applied before `Take(limit)`), `MemoryTools.cs:140-149` (`limit` described only as "Maximum results (default 8)"); live on g2p7: 70 distinct entries all containing "zephyr", `kind:"memory"`, limits 100/50/45.
**Result:** Exact: `limit=100, minRelativeScore=0.0` → 70 served (last ranking 0.469231); `limit=100` default → 41 served, last ranking 0.60396 (= 61/101); `limit=50` → 41; `limit=45` → 41; response keys exactly `results/evidenceByHash/fusionStats`, `warning: null`. No marker reports the floor cut, and `PRAGMA table_info(search_quality)` confirms no `limit` column, so cap-vs-exact is unrecoverable after the call.

## Verdict tally

- CONFIRMED: 7 (F19, F20, F22, F23, F24, F25, F26)
- CORRECTED: 0
- REFUTED: 0

## Still open

- **Binary vs source:** every live run used the installed `1.42.5+8a1f1dde` tool, not a build from the `5bca1900` tree (campaign rule: no builds). The two commits between them are skill/docs-only, and each mechanism was read at HEAD source before the live run, so no claim rests on the binary alone.
- **F19 mixed arm:** my bank had `vec_structure_rowids = 0`, so only the structure-absent arm (the filed claim) was measured. A bank with some headed chunks would exercise the blended arm; the mechanism follows from `EntryEmbedder.cs:240-248` (structure embedding only when the parsed heading path is non-empty), read but not run.
- **F19 model-version caveat:** I did not test a re-embed/model swap; the finding's "cross-row comparable" objection is independent of that (the 2× factor is alpha geometry, not model drift).
- **F22 background loop:** `ExtractionHostedService` is off by default and was not run; the manual `memory_share_extract` propose path exercises the same `SharedExtractionRunner.ProposeAsync` + `ClearStaleAsync`, so the finding's arm is covered without it.
- **F23 owner intent:** the plan records the current state as known and unshipped (WP1); I found no ruling that says the current reap behaviour is accepted as-is, but I did not search every review/archive doc for a later acceptance note.
- **F24/F25 meta disclosure:** I measured the responses' `context` echo (F24) and `meta.waitingPromotionsCount = 0` (F25) as partial disclosure; the findings already account for F24's, and I did not judge whether the F25 meta is enough for an agent in every queue state.
- **Exact candidate counts:** the finding's original banks (66 fusion candidates; N=140 chunks) were not available to me; my re-runs produced 70 candidates and N=28 with the same floors/counts/ratios (41 served, 0.60396, ratio 0.5, 1-of-N delete).
