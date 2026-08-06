# Un-ignoring @ignore Reqnroll scenarios — agent-memory.feature session (2026-08-06)

Session record for the "implement the @ignore-tagged scenarios" pass over
`docs/work/features-agent-memory/agent-memory.feature` (AiRaccoon, Reqnroll.xunit.v3,
net10.0). Outcome: 13 @ignore tags → 2 (both step-less OQ scenarios whose fallback shipped),
filtered BDD gate 64 passed / 0 failed / 4 skipped (the 4 = 2 genuinely-unbuilt native
scenarios + the 2 OQs). All steps live in one shared steps class
(`tests/AiRaccoon.Tests/BDD/NativeMemorySteps.cs`) because both feature files are compiled
into one suite.

## Per-tag decisions (the implement/delete/keep taxonomy in practice)

| @ignore site | Decision | Why |
|---|---|---|
| Tool listing over stdio | DELETE | native-memory FR-NM-9 "All 17 tools are still listed" is live; the stdio transport does not change the tool surface. Subset assertion (11 tools) is weaker, not additive. |
| Context restricts search | IMPLEMENT | unique (context-label search `docs:api`); fixed the dormant binding's wrong positional arg + added the Then-kind `[StepDefinition]` variant. |
| Workspace begin returns id | IMPLEMENT | unique agent variant; rewired to `WorkspaceService.BeginAsync` (real v7 id) and made "its context is workspace:<id>" probe the workspace bucket. |
| Search spans project+workspace | IMPLEMENT (query fix) | query `"finding"` could never match "committed fact"; changed to `"fact finding"` — FTS AND-primary misses both rows, so the OR-fallback fires per context and both rows return. Assertion untouched. |
| Consolidation keep-list | IMPLEMENT | Given must capture real hashes under the `h1`/`h2` scenario keys; `ConsolidateAsync` silently drops unknown keep hashes (`keep.Where(byHash.ContainsKey)`), so the literal "h1" made the scenario a no-op. |
| Embedding config rule (3 scenarios) | DELETE rule | native FR-NM-3 live with 6 scenarios covering the same behavior (local engine, deferred writes, embed_pending). |
| Extension hooks (2 scenarios) | IMPLEMENT | `MemoryExtensionHost` (Core/Rating) is real; first-party `RetrievalRatingExtension` hooks are no-ops by design (search-hit bump moved into `SqliteMemoryStore.SearchAsync`). Hook-order scenario constructs a host with two recorder extensions; access-count scenario writes two entries, searches one twice, asserts on-row `access_count == 2` and rating > never-searched (0.5 default → 0.5·1.2). |
| Sweep dry-run / real / shared-protection | IMPLEMENT | `SweepService.SweepAsync(projectId, 0.3, dryRun, ct)` is real (rating < threshold && age > explicit ttl, shared hashes exempt). Replaced raw-SQL DELETE approximations. "Shared entry" Given uses `UPDATE scope='shared'` on a project row — `ListContextAsync(project, "shared")` filters on scope so the row is seen; assert the row survives (project stats exclude shared scope, so the old EntryCount assertion would false-fail). |
| Sync rule (3 scenarios) | 2 DELETE, 1 IMPLEMENT | credential-error and workspace-exclusion are exact duplicates of native FR-NM-8; kept "local-only install syncs bank" asserting the shared row lands in the pushed snapshot (real `SyncService` + production `FakeCloudStore`). |
| OQ-4 MRTR / OQ-5 cloud RLS | KEEP @ignore | genuinely unbuilt (MRTR/RLS only in spec docs; fallback = keep-list tool call / cloud-db correlation point, both live and covered). |

## Real behavior facts discovered by reading code (not the feature text)

- Workspace rows store the context in `workspace_id`; `context_label` stays NULL.
  `ContextResolver.Resolve` derives `workspace:<id>` from `WorkspaceId` when no context is
  given. Assert workspace addressing via `ListContextAsync(projectId, "workspace:<id>")`,
  not `SELECT context_label`.
- Search bump: `BumpAccessAsync` runs over merged results of every search;
  `rating = 0.5 · 0.5^(age/30) · (1 + 0.1·accessCount)` — two hits ⇒ 0.6 vs 0.5 never-searched.
- FTS fallback: `BuildPlan("fact finding")` → primary `fact AND finding`, fallback
  `fact OR finding`; the store falls back when AND matches ≤ max(tokenCount, limit) rows.
  Single-token queries have no fallback — use them when a search must hit exactly one row.
- Sweep candidate: `ttlDays.HasValue && rating < threshold && ageDays > ttlDays` (age from
  `created_at`); the dry-run Given must set `created_at=1` + `ttl_days=10` to qualify.
- `SyncService` strips workspace rows from the snapshot (`DELETE ... WHERE workspace_id IS
  NOT NULL`); shared rows ride along. `SyncCloudStoreFactory.CreateAsync` throws
  `SyncNotConfiguredException` without creds.

## Workflow notes

- Dormant bindings never ran: after un-ignoring, run the filtered suite
  (`--filter "FullyQualifiedName~AgentMemory|FullyQualifiedName~NativeMemory"` — feature
  display names ARE the scenario texts, per the pitfall in SKILL.md) and fix failures in
  batches; most were 1-line binding bugs.
- Feature-text edits that are chain fixes (query changed so both rows match) are acceptable;
  do not weaken assertions.
- Commit in two groups: feature-file decisions, then step implementations.
