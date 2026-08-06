# AiRaccoon MCP tools — full test results

Date: 2026-08-06 · Tested by: agent session (task `full-test-all-tools-in-ai-raccoon`)
Target: live ai-raccoon MCP server via the Hermes MCP bridge (dev data root)
Test project: `tool-test-20260806` (dedicated; fully cleaned up after — 0 residue verified)
Scope: all 21 exported tools = 19 `memory_*` tools + 2 prompts (`list_prompts`, `get_prompt`)

## Method

1. Expectations formulated **before** any call, from three sources: the docs contract
   (`docs/reference/agent-memory-server.md` tool table), the live tool schemas, and the
   mcp-index intents (section 1).
2. Each tool called with a test payload; response compared against the expectation.
3. Verdicts: **PASS** = matches expectation · **PARTIAL** = works but differs from the
   documented/ideal shape · **FAIL** = broken.
4. Test payloads used real end-to-end flows: write → search → share → search shared →
   workspace begin → write → status → consolidate → verify → discard → verify; watch →
   auto-index → search; ingest → skip-unchanged; delete/delete_context positive + negative
   controls; sweep dry-run and real; sync (unconfigured path).

## 1. Expectations (formulated before any call)

| # | Tool | Expected behaviour | Expected response shape |
|---|------|--------------------|-------------------------|
| 1 | `list_prompts` | No args; lists the 2 documented prompts | `{prompts: [{name, description}]}` with `memory-usage-guide` + `workspace-consolidation-guide` |
| 2 | `get_prompt` | `name` required; returns prompt with messages; body mentions search-first + watch setup | `{description, messages: [{role, content}]}` |
| 3 | `memory_write` | projectId+content required; optional workspaceId/agentId/context/sourceFile/section | `{hash, path, context, createdAt}` — hash 64-hex, path = `<hash>.md`, context = `project:<id>` |
| 4 | `memory_search` | Hybrid FTS+vector; written entry at rank 1 with matching hash | `{results: [{hash, seq, ranking, path, snippet}], projectId}` |
| 5 | `memory_search` scopes | scope=project/shared/all filter; workspaceId adds workspace; contextLabel filters custom rows | same shape, filtered |
| 6 | `memory_list` | Lists indexed files as tree | `{files: <json tree>}` containing written path |
| 7 | `memory_stats` | Bank size + pending + contexts | `{entries, pending, contexts}` — entries ≥ 1, contexts contains `project:tool-test-20260806` |
| 8 | `memory_embed_pending` | Processes deferred entries; `limit` optional | `{processed, pending}` |
| 9 | `memory_workspace_begin` | Returns isolated workspace id | `{workspaceId, context}` — context = `workspace:<id>` |
| 10 | `memory_workspace_status` | Lists outbox entries | `{entries, count}` |
| 11 | `memory_workspace_consolidate` | keep=[hashes] or ['all']; promotes to project memory, removes workspace | `{promoted, discarded}` |
| 12 | `memory_workspace_discard` | Removes outbox, nothing promoted | `{discarded}` |
| 13 | `memory_share` | Promotes hash into shared tier; additive | `{shared: true, context: "shared"}`; searchable with scope=shared |
| 14 | `memory_delete` | Deletes by hash | `{deleted: 0\|1}` — **expected denied** (hermes tier believed rw; delete needs full) |
| 15 | `memory_delete_context` | Deletes all entries under a context | `{deleted: n}` — **expected denied** in rw |
| 16 | `memory_sweep` | dryRun default true; lists candidates only; shared exempt | `{candidates, deleted}` |
| 17 | `memory_sync` | Syncs committed contexts to cloud | `{sent, received, reindexed}` — **expected not-configured error** (no sync config) |
| 18 | `memory_watch_add` | Registers path; initial scan background | `{projectId, path}`; status → scanning → healthy |
| 19 | `memory_watch_status` | Lists watches with state | `{watches: [{projectId, path, state}]}` |
| 20 | `memory_watch_remove` | Stops watch; non-existent watch = no-op | `{projectId, path}` |
| 21 | `memory_ingest_file` | Indexes one file; unchanged skipped | `{indexed: 1}` first, `{indexed: 0}` re-ingest |
| 22 | `memory_ingest_directory` | Recursive index, skips unchanged | `{scanned: n}` |

## 2. Actual results (25 calls)

| # | Tool · payload | Actual response (abridged) | Verdict |
|---|----------------|----------------------------|---------|
| 1 | `list_prompts` () | `{prompts: [{name: "workspace-consolidation-guide", description, arguments}, {name: "memory-usage-guide", ...}]}` | **PASS** — exactly the 2 documented prompts, each with arguments metadata |
| 2 | `get_prompt(memory-usage-guide)` | `{description, messages: [{role: "user", content: <search-first ladder, watch setup, workspace, share, sweep, sync, ingest>}]}` | **PASS** — content matches the documented guide |
| 3 | `get_prompt(workspace-consolidation-guide, args={projectId})` | `{description, messages: [ritual: status → decide → consolidate keep=[...] / share → discard]}` | **PASS** — args accepted, ritual complete |
| 4 | `memory_write` (fox entry, sourceFile+section) | `{hash: 4e03cf4d…, path: 48056986….md, context: "project:tool-test-20260806", createdAt: 1786007827}` | **PASS** — see note: `path` is NOT `<hash>.md`; it is `sha256(content).md`, hash is `sha256(path+content)` (expectation corrected) |
| 5 | `memory_write` (context="tool-test-custom-ctx") | `{hash: d4da12bf…, context: "tool-test-custom-ctx", …}` | **PASS** — custom context label honored verbatim |
| 6 | `memory_search("quick brown fox keys")` scope=all | rank 1: hash `4e03cf4d…` == write hash, snippet + `sourceFile`, `chunkIndex`, `totalChunks` | **PASS** — hash round-trip exact; response richer than docs table |
| 7 | `memory_search(scope=project)` | project rows only — the custom-ctx row (d4da12bf) did NOT appear | **PASS** — project scope excludes custom-scope rows (by design) |
| 8 | `memory_search(scope=shared)` before share | `{results: []}` | **PASS** |
| 9 | `memory_search(contextLabel="tool-test-custom-ctx")` | BOTH project rows AND the custom row returned | **PASS vs schema** — `contextLabel` is *additive* (project scope + labelled rows), not a filter; schema wording says exactly this |
| 10 | `memory_list()` | `{files: "<json string>"}` — stringified tree containing the project row's path | **PARTIAL → FIXED** — was an escaped JSON *string*; tool now returns a real JSON object (`files` is parsed into the response). See section 6. Custom-ctx row absent (consistent with project-only view) |
| 11 | `memory_stats()` | `{entries: 1, pending: 0, contexts: […, "project:tool-test-20260806"]}` | **PASS** — entries counts committed rows only (custom-ctx row invisible, by design); `pending: 0` proves write-time embedding |
| 12 | `memory_embed_pending(limit=1)` | `{processed: 0, pending: 0}` | **PASS** — nothing pending (model embeds at write time); limit accepted |
| 13 | `memory_workspace_begin(name=…)` | `{workspaceId: 019fd65d…, context: "workspace:019fd65d…"}` | **PASS** |
| 14 | `memory_write(workspaceId=…)` | `{hash: ff5fe895…, context: "workspace:019fd65d…"}` | **PASS** — lands in the outbox |
| 15 | `memory_workspace_status(ws)` | `{entries: [{hash, path, context, value, createdAt}], count: 1}` | **PASS** — `value` (full content) included; richer than docs |
| 16 | `memory_workspace_consolidate(keep=["all"])` | `{promoted: 1, discarded: 1}` | **PASS** — entry promoted (same hash, now searchable in project scope), workspace context removed (the `discarded: 1`) |
| 17 | `memory_share(fox hash)` | `{shared: true, context: "shared"}` | **PASS** |
| 18 | `memory_search(scope=shared)` after share | shared row found — NEW hash `a07b51ab…`, path `shared/48056986….md` | **PASS** — share end-to-end; shared row gets its own content hash (path prefix changes the hash) |
| 19 | `memory_delete(real hash)` | `{deleted: 1}` | **PASS** — **NOT denied**: expectation corrected, tier permits deletes on this deployment |
| 20 | `memory_delete(bogus hash)` | `{deleted: 0}` | **PASS** — negative control |
| 21 | `memory_delete_context("tool-test-ctx2")` | `{deleted: 2}` | **PASS** — both seeded rows gone |
| 22 | `memory_delete_context("project:tool-test-20260806")` (cleanup) | `{deleted: 7}` | **PASS** — bulk project-context delete works |
| 23 | `memory_sweep(dryRun=true)` | `{candidates: [], deleted: []}` | **PASS** — fresh entries not eligible; arrays, not counts |
| 24 | `memory_sweep(dryRun=false)` | `{candidates: [], deleted: []}` | **PASS** — nothing eligible; real run safe on fresh bank |
| 25 | `memory_sync()` | error: `sync-not-configured: run 'ai-raccoon sync add s3 <url> --bucket <name>' or 'ai-raccoon sync add azure <container>' …` | **PASS** — typed, actionable error as expected |
| 26 | `memory_watch_add(/tmp/tool-test-watch)` (1st) | error: `watching-disabled: Watching is disabled for project 'tool-test-20260806'.` | **PASS** — config gate works; remedy implicit (CLI `watch enable`) |
| 27 | CLI: `watch enable` + `scope add` → `memory_watch_add` (retry) | `{projectId, path}` | **PASS** |
| 28 | `memory_watch_status()` | `{watches: [{projectId, path, state: "Healthy", lastSync: …}]}` | **PASS** — scanning → Healthy; 3 files auto-indexed and searchable |
| 29 | `memory_watch_remove(path)` | `{projectId, path}` | **PASS** |
| 30 | `memory_watch_remove(path)` again | same `{projectId, path}` — no-op, no error | **PASS** — idempotent |
| 31 | `memory_watch_status()` after | `{watches: []}` | **PASS** |
| 32 | `memory_ingest_file(a.md)` | `{indexed: 1}` | **PASS** |
| 33 | `memory_ingest_file(a.md)` re-run | `{indexed: 0}` | **PASS** — unchanged skip |
| 34 | `memory_ingest_directory(/tmp/tool-test-ingest)` | `{scanned: 2}` | **PASS** |
| 35 | `memory_workspace_discard(ws2)` | `{deleted: 1}` — entry gone, zero trace in search | **PARTIAL → FIXED** — response key was `deleted` while docs say `discarded`; tool now returns `{discarded: n}` (code aligned to the documented contract). See section 6 |

**Summary: 33/35 calls PASS, 2 PARTIAL, 0 FAIL** (all 21 tools functional; the two PARTIALs were
response-shape warts, not failures — both FIXED same-day, see section 6).

## 3. Findings

1. **Docs drift — `agent-memory-server.md` tool table is stale in three places** (verified
   against the live server):
   - `memory_write` table lacks `sourceFile` / `section` params (both exist and work; `sourceFile`
     round-trips into search results).
   - `memory_search` result rows carry `sourceFile`, `chunkIndex`, `totalChunks` — not in the table.
   - `memory_workspace_discard` documented as `{discarded}`, actual response is `{deleted}` —
     inconsistent with `memory_workspace_consolidate`'s `discarded` key.
2. **`memory_delete` gating — expectation corrected, intent was RIGHT.** The index intent says
   "denied in rw access mode (needs full)". The live calls (delete, delete_context,
   workspace_discard) all succeeded — not because the intent is wrong, but because the tested
   data root resolves **full** access (per-project or global setting). Verified in code:
   `MemoryAccessGuard.EnsureAsync` → `RequireAsync(..., AccessRequirement.Destructive, ...)`
   gates all three tools at mode `full`; `rw` denies them. The intent's claim is accurate.
3. **Delete scope (Q1 answer).** Both delete tools are project-scoped by the `project_id`
   **column**, not by the context:
   - `memory_delete(projectId, hash)` runs `DELETE FROM entries WHERE hash = @hash AND
     project_id = @projectId` (MemorySql.cs:124). It can delete any row owned by that project —
     committed rows AND that project's shared rows (a shared row keeps the originating
     project's `project_id`; its `hash` differs from the source row's because the path gains a
     `shared/` prefix, so you must pass the shared row's own hash, e.g. from a scope=shared
     search). Rows of other projects are never touched.
   - `memory_delete_context(projectId, context)` builds `DELETE FROM entries WHERE {filter}`
     via `FilterFor` (SqliteMemoryStore.cs:908): `project:<id>`, `workspace:<id>`,
     custom labels and `label:` prefixes all carry a `project_id` filter.
   - **Hazard found:** the `"shared"` context branch filters ONLY `scope = 'shared'` — no
     `project_id`. `memory_delete_context(<anyProjectId>, "shared")` therefore deletes **every
     shared row in the bank, across all projects**. The access gate checks the caller's project
     mode, so a full-mode caller of any project can wipe the shared tier. This deserves an
     owner decision: either scope the shared branch to the caller's `project_id`, or document
     that the shared tier is globally managed and gate it separately.
4. **Hash derivation is undocumented**: `path = sha256(content).md`, `hash = sha256(path+content)`.
   `memory_share` re-hashes the shared row (path gains a `shared/` prefix), so the shared row's
   hash differs from the source row's — callers who match hashes across scopes will be surprised.
5. **Custom-context rows are invisible to project scope** (search, stats counts, list) and
   `contextLabel` is *additive*, not a filter. This is documented in the codebase's own skill
   (ingest-docs-to-memory) but not visible in the tool description; a caller holding a context
   label can find its rows only via `contextLabel`.
6. **`memory_sweep` returns arrays** (`candidates: []`, `deleted: []`); the docs table does not
   say counts vs arrays. Array shape is fine but should be stated.
7. **Error UX is typed and actionable** (good): `sync-not-configured` names the exact CLI command;
   `watching-disabled` names the state. Perfect version of the latter would also name the remedy
   (`ai-raccoon watch enable '<id>' true` + `watch scope add`), saving a docs lookup.
8. **Response envelope inconsistency** (host-side, not server-side): prompt tools return bare JSON
   (`{prompts: …}`), memory tools return a stringified JSON payload inside `{"result": "…"}`.
   Both parse fine; clients must handle both shapes.

## 4. How the perfect response should look

Per tool, the ideal (contract-conformant) response:

| Tool | Perfect response |
|------|------------------|
| `list_prompts` | `{"prompts": [{"name", "description", "arguments": [{"name", "description", "required"}]}]}` — as today |
| `get_prompt` | `{"description", "messages": [{"role", "content"}]}` — as today; content already the full guide |
| `memory_write` | `{hash, path, context, createdAt}` — as today; ideal would also echo `sourceFile`/`section` for confirmation |
| `memory_search` | `{results: [{hash, seq, ranking, path, snippet, sourceFile, chunkIndex, totalChunks}], projectId}` — as today (docs should list the extra fields) |
| `memory_list` | `{"files": {<object tree>}}` — actual object, not an escaped string |
| `memory_stats` | `{entries, pending, contexts}` — as today |
| `memory_embed_pending` | `{processed, pending}` — as today |
| `memory_workspace_begin` | `{workspaceId, context}` — as today |
| `memory_workspace_status` | `{entries: [{hash, path, context, value, createdAt}], count}` — as today |
| `memory_workspace_consolidate` | `{promoted, discarded}` — as today (document that `discarded` counts the removed workspace context) |
| `memory_workspace_discard` | `{discarded: n}` — rename `deleted` → `discarded` to match docs and the sibling tool |
| `memory_share` | `{shared: true, context: "shared", hash}` — today's response plus the shared row's new hash (currently you must re-search to learn it) |
| `memory_delete` | `{deleted: 0\|1}` — as today |
| `memory_delete_context` | `{deleted: n}` — as today |
| `memory_sweep` | `{candidates: [...], deleted: [...]}` — as today, with the array shape documented |
| `memory_sync` | `{sent, received, reindexed}` when configured; typed `sync-not-configured` with the setup command when not — as today |
| `memory_watch_add` | `{projectId, path}` — as today; the `watching-disabled` error should add the remedy commands |
| `memory_watch_status` | `{watches: [{projectId, path, state, lastError?, lastSync?}]}` — as today |
| `memory_watch_remove` | `{projectId, path}` — as today (idempotent no-op confirmed) |
| `memory_ingest_file` | `{indexed: 0\|1}` — as today |
| `memory_ingest_directory` | `{scanned: n}` — as today |

## 5. Environment notes

- Test data was fully removed after the run: 8 rows deleted (7 project-context + 1 shared),
  watch registration removed, `watch enable`/scope config for the test project disabled, temp
  dirs deleted; `memory_stats` verified 0 residue.
- The `docs/` watch for project `ai-raccoon` was untouched throughout (separate project).
- All expectations that were corrected during the run (write path semantics, delete gating,
  contextLabel additive semantics) are marked in section 2 — the corrections came from the live
  contract, which outranks prior assumptions.

## 6. Fixes applied (same day, TDD)

Both PARTIALs fixed with failing tests first (RED → GREEN), plus the docs drift:

| Fix | Change | Tests |
|-----|--------|-------|
| `memory_list` returns a real JSON object | `ListResult(string Files)` → `ListResult(JsonNode Files)`; tool parses the store's JSON string (`JsonNode.Parse(files) ?? new JsonObject()`). Response shape: `{"files": {<tree>}}` | new: `List_ReturnsFilesAsJsonObject`, `List_PreservesNestedFileTree` (unit, MemoryToolsTests) |
| `memory_workspace_discard` returns `{discarded: n}` | new `WorkspaceDiscardResult(int Discarded)` used by `WorkspaceDiscard`; `DeleteContext` keeps `DeletedContextResult` | new: `WorkspaceDiscard_ReportsDiscardedKey` (unit); E2E `Worktree_Discard_RemovesTheOutbox` assertion updated `"deleted":1` → `"discarded":1` |
| Docs drift | `agent-memory-server.md` tool table: `memory_write` gains `sourceFile?`, `section?`; `memory_search` gains `contextLabel?` param and `sourceFile?`, `chunkIndex`, `totalChunks` in the result row. (`memory_workspace_discard` row already said `{discarded}` — the code was the drift.) | n/a (docs) |

Targeted suite: 20/20 green. Full suite run after the changes (section 7).

## 7. Quality gate

- Targeted (MemoryToolsTests + E2E discard): **20/20 passed**.
- Full suite (`dotnet test`): **1116 passed / 0 failed / 43 skipped** (baseline 1110/2-flaky/43
  — net +6 green, both known flakes absent this run).
- Live re-test of the two fixed tools against the Hermes MCP bridge was not possible without
  restarting that server (it runs the installed tool, not this build tree); the E2E suite
  (`McpServerE2ETests`, real server over WebApplicationFactory) covers the fixed paths instead.
- Remaining open decision for the owner: the `memory_delete_context(…, "shared")` cross-project
  hazard (finding 3) — not changed unilaterally since it alters destructive semantics.
