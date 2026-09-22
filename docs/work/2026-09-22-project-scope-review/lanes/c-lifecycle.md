# Lane C — Memory lifecycle: ingest → extract → promote → rate → degrade → share

Date: 2026-09-22 · worktree `/Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/psr-c-lifecycle` @ `5bca1900` (clean).
All live work in `docs/work/2026-09-22-project-scope-review/c-lifecycle/data` via `ai-raccoon --data-root … serve --port 7799` (own scratch
server; the `mcp_ai-raccoon_*` tools were never called). Full lifecycle driven over MCP: write → search-bump →
TTL → sweep(dry+real) → propose → promote/discard → shared search → workspace begin/status/consolidate/discard →
file ingest → project-ids fold. Scratch DB queried with `sqlite3` at every stage.

### F1 — A pending `agent-requested-share` promotion request is destroyed by the next propose pass's scorer-version auto-clear and is never re-admitted [MEASURED]
**Severity:** HIGH
**Evidence:** `memory_write(context:"shared")` → `promotion_queue` row `{score 1.0, scorer_version 0, reasons ["agent-requested-share"]}`; `memory_share_extract(mode:"propose")` → `{candidates:[], meta.waitingPromotionsCount:0}` and `SELECT count(*) FROM promotion_queue` → 0. Code: `SharedExtractionRunner.cs:34` clears every row with `scorer_version != PromotionScorer.Version` (`PromotionScorer.cs:16` = 2) before ranking; the write path stamps the default 0 (`PromotionQueue.cs:15`, `MemoryWriteService.cs:57-60`).
The re-rank that is supposed to re-admit cleared rows scores *entries*, not queue rows, so a short agent note below the 0.4 floor never comes back — the clearing is terminal for exactly the candidates the ADR calls strongest.
Cost: the write response (`Reason = "queued-for-promotion: agent-requested-share"`, `MemoryWriteService.cs:65`) claims a review that a later routine propose silently cancels; the memory row survives in the project, the agent's explicit intent does not.
`docs/adr/0067-naming-shared-asks-for-promotion.md:45-47` names this as the failure mode the feature exists to avoid; the guarding test (`SharedWriteIsAPromotionRequestTests.cs:36-40`) stops at a `FakePromotionQueue`, so the real queue interaction is untested.
Smallest fix: stamp agent-requested rows with the current `PromotionScorer.Version` (or exempt the reason from `ClearStale`).

### F2 — `rating` is recomputed only on a search hit and from `created_at`; the sweep branches on that stale, age-driven cache, so the reaper does not measure "not used" [MEASURED]
**Severity:** MEDIUM
**Evidence:** TTL'd entry backdated 200 days with stored rating 0.5 → `memory_sweep(dryRun:true)` `{candidates:[]}`; one search later (`access_count 1`, `rating 0.00541372`) → the same sweep lists it, and a real sweep deletes it. An entry searched 5× with `last_accessed_at` 0 s old but `created_at` 200 d old → `rating 0.00738234` → candidate. The only writer of `entries.rating` in `src/` is `MemorySql.cs:625-630` (`BumpAccess`: `pow(0.5,(now-created_at)/86400/halfLife)*(1+(access_count+1)*0.1)`); `SweepService.cs:40` reads the stored value and nothing reads `last_accessed_at` for it.
Consequence: a TTL'd entry nobody ever touches can never expire (`canEverExpire:false` is the only warning), while an entry used minutes ago can be swept once it is old enough — creation age, not disuse, drives the rating.
The ratified plan `docs/plans/2026-08-09-memory-decay-implementation-plan.md:3` says "planned, not started"; its WP1 (`:59`, `:68` — "treat the stored rating column as a cache … nothing may branch on it without recomputing") describes this exact state. Report is "live, known, unshipped", not novel; WP1 is the fix.

### F3 — Supplying `context` together with `workspace_id` silently overrides the sandbox: the row is committed to the project, not the outbox [MEASURED]
**Severity:** MEDIUM
**Evidence:** inside an active workspace, `memory_write(workspaceId=W, context:"design-notes")` returned `context:"design-notes"` and the row landed `scope='custom', context_label='design-notes', workspace_id NULL` (id 11); `memory_workspace_status` reported outbox count 0. With `context:"shared"` the row landed `scope='project'` (id 84) plus a promotion candidate, again outbox count 0. `ContextResolver.cs:10-17` returns `request.Context` whenever it is non-empty regardless of `workspaceId`; `MemoryWriteService.cs:44-49` rewrites `shared` to the project context while leaving `workspaceId` to be ignored.
Cost: notes the caller believed were soaking in the outbox are committed and project-searchable, and consolidation/discard never sees them. The tool descriptions conflict for this combination (`MemoryTools.cs:57` "naming a workspace_id routes them into that isolated workspace" vs `:62` "context … instead of the default project/workspace context").
Graded MEDIUM rather than HIGH because the response's `context` field does disclose where the row went, so an attentive caller can notice. Smallest fix: make `workspace_id` win when both are set (or refuse the combination) and align the two descriptions.

### F4 — After a permanent discard, rewriting the same content as a shared request reports `queued-for-promotion` while nothing is queued [MEASURED]
**Severity:** MEDIUM
**Evidence:** write `context=shared` → queue 1; `memory_promotion_discard(hash)` → `{'discarded':1}`, queue 0, `promotion_discards` 1; rewrite the identical content with `context=shared` → response `reason: 'queued-for-promotion: agent-requested-share'`, queue count still 0. `MemoryWriteService.cs:65` sets the reason unconditionally; `PromotionQueueSql.cs:9-13` refuses the upsert via `NOT EXISTS (… promotion_discards …)` so nothing is enqueued.
Cost: the response asserts queue state that does not exist; an agent that trusts it looks for a candidate in `memory_promotion_list`, finds none, and cannot tell a deliberate rejection from a lost write. Smallest fix: return `ProposeOutcome.Upserted` per candidate, or an explicit "discarded-earlier" reason.

### F5 — One multi-chunk `memory_write` creates N rows but reports one hash and one path; `memory_delete` on the reported hash removes exactly one row [MEASURED]
**Severity:** MEDIUM
**Evidence:** a 140-paragraph write produced 140 rows under one content-addressed path (ids 85-224, `SELECT count(*) … WHERE path='727944d5….md'` → 140); `memory_delete(returnedHash)` → `{deleted:1}` and the count fell to 139; a follow-up search returned 8 hits from the remaining rows. The write inserts every chunk (`SqliteMemoryStore.cs:125-145`) and returns only `chunks[0]`; `MemorySql.cs:167-168` deletes `WHERE hash = @hash AND project_id = @projectId`; the MCP surface has no delete-by-path verb (`MemoryTools.cs:41-42`).
Cost: "delete what I wrote" removes 1/N and reports success; the rest stays searchable and promotable with no supported single verb to remove it (only context-wide delete). The MCP contract's per-chunk wording mitigates this, but the write result gives the caller no way to learn N.
All 140 rows carry `chunk_index=-1/total_chunks=0` because the write-time recompute is gated on `sourceFile` (`SqliteMemoryStore.cs:160-164`) — expected (the sentinel is handled in `SourceAffinityRanker.cs:73`), not the finding. Smallest fix: report the chunk count/hashes in `WriteResult`, or let delete target the write's path.

### F6 — `memory_promotion_list(allProjects=true)` still requires no global read-all mode; ruling S1's "read-all mode" is realized as a boolean consent flag [READ]
**Severity:** LOW
**Evidence:** owner ruling `docs/work/2026-08-21-delta-review-owner-rulings.md:67` — "promotion_list without projectId requires a global read-all mode instead of skipping the gate". `PromotionTools.cs:44-53` requires only `allProjects=true` plus `RequireBankAvailableAsync` (`ToolGate.cs:32-39`, a migration-lock check); no access-mode check runs. The mode lattice has no read-all member (`AccessModePolicy.cs:28-36`: `Read => true` in every mode).
Lane J owns the security reading (H8/S1); this lane records the ruling-vs-code drift: the substance "not free by omission" landed, the named mechanism did not. An agent in a per-project `rw` bank can list every project's queue with one explicit flag.

### F7 — `memory_set_ttl` on a hash this project can read from the shared tier refuses with a factually false "No entry with hash …" [MEASURED]
**Severity:** LOW
**Evidence:** on one shared row, `memory_get(projectId, hash)` returned the entry, then `memory_set_ttl(projectId, hash, 1)` → `unknown-hash: No entry with hash '690d2306884e…' in project 'c-lane-p1'`. `UpdateEntryTtl`/`SelectEntryMetadata` filter through `ProjectRows.Of()` (`MemorySql.cs:669-686`, `ProjectRows.cs:24-26`), which excludes shared rows by design; `ForgettingPolicyService.cs:56-66` maps the resulting `false` to `UnknownHashException`.
Cost: the caller is told the hash is unknown while it is readable, which invites destructive "fix it" reactions; the accurate answer is "shared entries are sweep-exempt and carry no TTL". Smallest fix: word the refusal from the found row's scope.

### F8 — `promotedHashes` reports the source (project) hash; the created shared row has a different, unreported hash [MEASURED]
**Severity:** NIT
**Evidence:** promote returned `promotedHashes: ['f3e399a780…']` while the shared row created is hash `5393d4b0489b…` with path `shared/e0d91590….md` (`SELECT hash, path FROM entries WHERE scope='shared'`). `PromotionQueueService.cs:144-148` adds `row.Hash` (the queue/source hash) and ignores `shared.Hash`; the project row's hash derives from `<valuehash>.md` and the shared row's from `shared/<valuehash>.md`, so they can never coincide.
Nothing breaks (the value is identical and searchable via `scope:"shared"`), but the field cannot address the artifact it names.

## Verified sound (not findings)

- **Sweep conjunction and shared protection** (FR-MEM-1.15): TTL alone (`rating 0.5`, age 200 d, ttl 1 d) → no candidate; low rating with no TTL (six rows at 0.0059) → no candidates; both → candidate and real delete. A project row whose value also lives in the shared tier was swept while the shared row survived and a `scope:"shared"` search still returned it.
- **Cross-project isolation** (B1 fix still holds): `memory_delete_context` with `project:c-lane-p2`, `label:c-lane-p2:notes` and `shared` all refused `context-outside-project`; `memory_delete` with p2's hash from p1 → `{deleted:0}` and p2's row intact; `memory_write(context:"project:c-lane-p2")` under p1 refused.
- **Project-ids repair (ADR-0102)**: census → `--apply --map` folded 1 fragment row in one pass; `project_id_aliases` holds `c-lane-frag → c-lane-p1`; a post-repair write under the loser id returned `context:"project:c-lane-p1"` and landed canonical.
- **Workspace lifecycle**: begin/status/consolidate kept 1 of 1 rows (workspace row → project row id 12), closed workspace refused a second consolidate and a late write (`unknown-workspace`); discard deleted the outbox row and left status `Discarded`.
- **Promotion concurrency (A-F11)**: a queue row with `claimed_at` 600 s old was reclaimed and promoted by the next `mode:promote` pass; queue drained, one shared row created.
- **Share gates**: `autoPromote` without `confirm` → `confirm-required`; 9 projects and a blank project element refused.
- **Noise rejection**: enabled noise filter rejected a Hermes log line (`stored:false`, reason naming the policy) and `noise entries` reported the recorded rejection — DI resolves the real `SqliteNoiseEntryStore`, not the no-op ctor.
- **File ingest/chunking**: 45,757-byte markdown → 66 chunks, `chunk_index` 0..65, `total_chunks` 66, one `source_id`, no duplicate positions; unchanged re-ingest `indexed 0`; an edited paragraph re-ingested as a new id with its authoritative document position (GH #371 behaviour).
- **Watch digest atomicity (read)**: `WatchDigestExecutor.cs:36-96` delete/touch paths and `SqliteMemoryStore.Replace.cs:247-265` — the fingerprint upsert sits inside the prune transaction, after the chunk prune, immediately before `COMMIT` (digest stamped last); a crash rolls back.

## Still open

- **Watch was not run live** (no watcher process registered): rename/delete/hash-skip semantics are read-only evidence (`WatchDigestExecutor.cs:36-96`); deleted-file chunk removal and the ignore-file rescan were not exercised.
- **M4 (`HasWorkAsync` guard) and D3 (reconcile-at-open)** were not re-verified — Lane D owns them; only the watch analogue of "stamp last" was checked.
- **Extraction scoring/capacity** beyond the measured propose/promote: floor 0.4 and per-source cap 3 read (`SharedExtractionService.cs:16-24`); no content in my bank scored ≥0.4, so `includeTtlRows` exclusion (`:77-80`) could not be discriminated live.
- **`ReclaimStaleClaims` has no owner column** (`PromotionQueueSql.cs:134-138`): a promote pass running longer than 5 minutes could have its claim reclaimed by a concurrent pass. No failing scenario was produced, so this stays an open question, not a finding.
- **Harness note:** the session's `grep` tool is intercepted by a memory-first gate that demands `memory_search` (projectId=ai-raccoon), which this lane's contract forbids (`mcp_ai-raccoon_*` points at the live bank). All searching used `rg`/`read`; the gate could not be satisfied from inside the lane.
- **Ambiguity resolved in favour of evidence:** F3 was graded MEDIUM (not HIGH) because the response `context` discloses the redirect; F5 keeps the per-chunk contract's wording in view before calling the asymmetry a defect.

Grade mix: MEASURED 7 (F1, F2, F3, F4, F5, F7, F8) · READ 1 (F6) · INFERRED 0 · UNVERIFIED 0.

## Owner questions

1. May any propose pass (manual or the auto-promote loop) delete an agent-requested candidate whose scorer version is stale, or must `reason=agent-requested-share` be exempt from `ClearStale`? (F1)
2. Is the reaper allowed to remain gated on a rating that only changes when a memory is read, or must WP1 of the decay plan ship before TTL expiry is trusted? (F2)
3. Are `workspace_id` and `context` a supported combination — if so which wins, and should a `shared`/custom context be refused inside an active workspace? (F3)
4. After a permanent discard, should a later explicit `context=shared` write report success, a refusal, or deliberately resurrect the candidate? (F4)
5. Is the chunk the intended unit of `memory_delete`; if yes, how is a caller expected to delete the rest of a long write it just made? (F5)
6. Does S1 require an access-mode check on `allProjects=true`, or is the explicit consent flag the accepted realization of "read-all mode"? (F6)
7. Should `memory_set_ttl` on a shared-tier hash answer "shared entries are sweep-exempt" instead of `unknown-hash`? (F7)
8. Should `promotedHashes` name the shared rows created, or stay a list of source hashes? (F8)
