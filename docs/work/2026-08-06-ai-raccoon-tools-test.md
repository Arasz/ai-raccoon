# AiRaccoon MCP tools — full test results

Date: 2026-08-06 Tested: all 21 tools exported by the ai-raccoon MCP server (19 memory tools + 2 prompts)
Target: live server via Hermes MCP bridge (dev data root)
Test project: `tool-test-20260806` (dedicated — real projects untouched)

## Method

1. Expectations formulated from three sources: the docs contract (`docs/reference/agent-memory-server.md` tool table),
   the tool schemas (live `tool_describe`), and the mcp-index intents.
2. Each tool called with a test payload; response compared against the expectation.
3. Verdict: PASS (matches expectation), PARTIAL (works but differs from expectation), FAIL (broken), N/A
   (environmental).
4. "Perfect response" = the ideal shape each tool should return, given the contract.

## 1. Expectations (formulated before any call)

| #  | Tool                           | Expected behaviour                                                                                        | Expected response shape                                                                               |
|----|--------------------------------|-----------------------------------------------------------------------------------------------------------|-------------------------------------------------------------------------------------------------------|
| 1  | `list_prompts`                 | No args; lists the 2 documented prompts                                                                   | `{prompts: [{name, description}]}` containing `memory-usage-guide` + `workspace-consolidation-guide`  |
| 2  | `get_prompt`                   | `name` required; returns prompt with messages; body mentions search-first + watch setup                   | `{description, messages: [{role, content}]}`                                                          |
| 3  | `memory_write`                 | projectId+content required; optional workspaceId/agentId/context/sourceFile/section; returns stored entry | `{hash, path, context, createdAt}` — hash 64-hex, `path = <hash>.md`, `context = project:<projectId>` |
| 4  | `memory_search`                | projectId+query; hybrid FTS+vector; result hash == write hash at rank 1                                   | `{results: [{hash, seq, ranking, path, snippet}], projectId}`                                         |
| 5  | `memory_search` scopes         | scope=project / shared / all; workspaceId adds workspace; contextLabel filters custom rows                | same shape, filtered                                                                                  |
| 6  | `memory_list`                  | Lists indexed files as tree                                                                               | `{files: <json tree>}` containing written path                                                        |
| 7  | `memory_stats`                 | Bank size + pending + contexts                                                                            | `{entries, pending, contexts}` — entries >= 1, contexts contains `project:tool-test-20260806`         |
| 8  | `memory_embed_pending`         | Processes deferred entries; limit optional                                                                | `{processed, pending}`; omit limit = all                                                              |
| 9  | `memory_workspace_begin`       | Returns isolated workspace id                                                                             | `{workspaceId, context}` — context = `workspace:<id>`                                                 |
| 10 | `memory_workspace_status`      | Lists outbox entries for workspaceId                                                                      | `{entries, count}`                                                                                    |
| 11 | `memory_workspace_consolidate` | keep=[hashes] or ['all']; promotes to project memory, removes workspace                                   | `{promoted, discarded}`                                                                               |
| 12 | `memory_workspace_discard`     | Removes outbox + entries, nothing promoted                                                                | `{discarded}`                                                                                         |
| 13 | `memory_share`                 | Promotes entry hash into shared tier; additive; no un-share                                               | `{shared: true, context: "shared"}`; entry searchable with scope=shared                               |
| 14 | `memory_delete`                | Deletes by hash; **expected DENIED** (hermes tier = rw, delete needs full)                                | `{deleted: 1}`; or typed access-denied error                                                          |
| 15 | `memory_delete_context`        | Deletes all entries under a context; **expected DENIED** in rw                                            | `{deleted: n}`; or access-denied                                                                      |
| 16 | `memory_sweep`                 | dryRun default true; lists candidates only; shared exempt                                                 | `{candidates, deleted}` — deleted 0 on dry run                                                        |
| 17 | `memory_sync`                  | Syncs committed contexts to cloud; **expected not-configured error** (no sync config on this root)        | `{sent, received, reindexed}`; or typed `sync-not-configured`                                         |
| 18 | `memory_watch_add`             | Registers path; initial scan background                                                                   | `{projectId, path}`; status → scanning/healthy                                                        |
| 19 | `memory_watch_status`          | Lists watches with state                                                                                  | `{watches: [{projectId, path, state}]}`                                                               |
| 20 | `memory_watch_remove`          | Stops watch; non-existent watch = no-op                                                                   | `{projectId, path}`                                                                                   |
| 21 | `memory_ingest_file`           | Indexes one file; unchanged file skipped                                                                  | `{indexed: 1}` first, `{indexed: 0}` re-ingest                                                        |
| 22 | `memory_ingest_directory`      | Recursive index, skips unchanged                                                                          | `{scanned: n}`                                                                                        |

Sources: agent-memory-server.md tool table (lines 32–50), live tool schemas, mcp-index intents.

## 2. Test payloads + actual results

(filled in during execution)

## 3. Findings

(filled in during execution)
