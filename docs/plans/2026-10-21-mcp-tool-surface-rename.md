# The proposed MCP tool surface: names, entry types, and the moves they force

**Date:** 2026-10-21
**Status:** proposed by the orchestrator, pending the owner's nod. Every name below is a
*proposal*; the plan lanes must not treat it as settled.

**Owner decisions already taken:** hard rename (a breaking change, no aliases); the ai-badger
framework is updated before the release ships; high-effort loop; the quality surface splits
into a read tool and one write tool.

## The names

| today | proposed | why this name |
|---|---|---|
| `memory_search` | `query` | the tool performs a query; the old name promised "memory" but the tool searches three corpora |
| `memory_list` | `get_index` | it lists the bank's indexed *files*, not its memories — "index" is what it returns |
| `memory_get` | `get_entry` | one entry by hash |
| `code_get` | `get_entry` | the same entry by hash, from the code corpus — the two tools differ only in which corpus they read, which becomes the entry-type parameter |
| `memory_performance` | `get_engine_statistics` | the engine's own statistics: the per-tool latency series and the embedding-engine state. Replaces the latency name — the latency series lives under a statistics name |
| `memory_record_followthrough`, `memory_record_grade` | `get_quality_report` | the read side: what the server knows about past queries |
| `memory_record_followthrough`, `memory_record_grade` | `set_query_result_grade` | the write side: records the 1–5 usefulness grade for a query |
| `memory_record_followthrough` | `set_memory_usage_record` | the other write: records that the agent opened a file the query served |
| `memory_write` | `add_memory` | an agent adds a memory — "add", not "write" (nothing is written to a file) |
| `memory_ingest_file` | `ingest_file` | a file is ingested |
| `memory_ingest_directory` | `ingest_directory` | a directory is ingested |
| `memory_delete` | `delete_entry` | deletes one entry by hash, from any corpus |
| `memory_delete_context` | `delete_context` | deletes every entry under one context label |
| `memory_share` | `share_memory` | moves one memory into the shared tier |
| `memory_share_extract` | `trigger_memory_sharing` | scans a project's memories for the ones worth sharing; proposes them, or promotes the queued ones |
| `memory_promotion_list` | `get_memories_sharing_ranking` | the propose-tier queue, ranked |
| `memory_promotion_discard` | `delete_memory_from_ranking` | drops one queued candidate |
| `memory_sweep` | `trigger_memory_cleanup` | runs degradation: deletes entries whose rating fell below the threshold and whose age passed their TTL |
| `memory_set_ttl` | `set_memory_ttl` | sets one memory's TTL — TTL is a memory-only property (see the note below) |
| `memory_workspace_begin` | `initialize_workspace` | starts a sandbox |
| `memory_workspace_status` | `get_workspace_status` | lists a workspace's outbox |
| `memory_workspace_consolidate` | `close_workspace` | closes a sandbox, promoting the kept hashes into the project |
| `memory_workspace_discard` | `discard_workspace` | drops a sandbox and everything in it |
| `memory_watch_add` | `add_watch` | registers a path watch |
| `memory_watch_status` | `get_watch_status` | the watch's live state |
| `memory_watch_remove` | `remove_watch` | unregisters a watch |
| `memory_sync` | `sync_to_cloud` | syncs the bank's committed contexts to cloud storage |
| `memory_embed_pending` | `embed_deferred_entries` | embeds the entries whose embedding was deferred |
| `memory_stats` | `get_store_statistics` | the store's own counts: entries, the deferred-embedding backlog, the contexts in use |
| `project_id_get` | `get_project_id` | looks up the id registered under a name |
| `project_id_token_get` | `save_project_id_token` | mints a new id |

The shape is verb-first, object-second, no `memory_` prefix: `query`, `get_*`, `add_*`,
`ingest_*`, `delete_*`, `move`, `trigger_*`, `set_*`, `initialize_*`, `close_*`,
`discard_*`, `sync_*`, `embed_*`, `save_*`.

The owner's corrections (2026-10-21) settled the sharing family — `move`, `trigger_memory_sharing`,
`get_memories_sharing_ranking`, `delete_memory_from_ranking` — and the workspace family —
`initialize_workspace`, `get_workspace_status`, `close_workspace`, `discard_workspace`.

### TTL is a memory-only property — so `set_memory_ttl` is accurate

The `entries` table carries `ttl_days`, `rating` and `access_count`; the `code_entries` table
carries none of them — the schema comment says so in as many words ("no rating/ttl_days/
access_count — no degradation, code is a re-derivable cache"). A code chunk can therefore never
be swept, rated or given a TTL, and `set_memory_ttl` names the only population the operation
can reach. The same holds for the File population: an ingested document chunk *is* an `entries`
row and does carry a TTL, so the tool's reach is "every entry in the `entries` table", not
"every agent-written memory" — the name says memory, and the mechanism says entries. Worth one
word from the owner: if the tool must also set a TTL on an ingested document, `set_memory_ttl`
understates it.

### Two names that still do not sit in the shape

- `move` is a bare verb where every other name is verb + object. `move_memory` would say what
  moves; `move` says only that something does. Taken literally as the owner wrote it.
- `get_engine_statistics` reports the bank's entry counts, the deferred-embedding count and the
  contexts in use — it is not the embedding engine's statistics (those live in
  `get_tools_latency`, the per-tool latency series). The name reads as the engine's; the
  behaviour is the bank's.

## The entry type

`query` and `get_entry` gain an **entry type** parameter with the values Memory, Code, File.

- `query` defaults to **Memory | Code | File** (the sum), so the default answers exactly what
  today's default (`kind=both`) answers.
- `get_entry` has **no default**: the type is required, because a hash alone does not say which
  corpus to read from — and the merged `get_entry` must know where to look.

The parameter is a flag enum, so a caller can ask for Memory alone, Code alone, File alone, or
any sum. It replaces `kind` (`memory|code|both`).

### The mapping onto the data

| entry type | rows | how it is stored today |
|---|---|---|
| Code | 72,290 rows on the live bank | the `code_entries` table — a separate corpus, own FTS and vec0 |
| File | 25,089 rows on the live bank | `entries` where `source_file` is set and `total_chunks > 0` |
| Memory | 214 rows on the live bank | `entries` where `source_file` is NULL and `total_chunks = 0` |

The `entries` partition is exact — zero rows in the two mixed cells — so no migration is needed
to *read* the distinction. Two derivations are available and the plan must pick one:

1. a join on `entries.source_id` → `memory_source.source_type` (`file` | `transcript` |
   `manual`), or
2. the denormalised signature (`source_file IS NULL` AND `total_chunks = 0`).

Option 1 is first-class but needs a join in the search path; option 2 is free but re-derives a
provenance fact the schema already stores. A third option — a denormalised `entry_type` column
on `entries`, backfilled once — costs a schema change and a ladder step but makes the filter
indexable.

## The moves this forces (the plan must cover every one)

1. **`search_quality.kind` is pinned by a CHECK constraint.** The stored taxonomy is
   `CHECK(kind IN ('memory','code','both'))` (`MemorySchema.cs:141`), with a backfill ladder
   step (v12) and ADR-0097 behind it. The new taxonomy needs a new ladder step, a widened
   CHECK, and a decision about what the old `memory` rows mean: they covered *both* agent notes
   and ingested chunks, which the new `memory` does not.
2. **`SearchKind` is a domain enum** (`Memory | Code | Both`) with a wire-name vocabulary
   (`SearchKindWireNamesTests`). It becomes the entry-type flag enum, or a new one beside it.
3. **The generator, the scope map and the inventory change shape.** The Roslyn generator emits
   the tool inventory and the scope map from the `[McpServerTool]` methods; the renamed methods
   change the emitted names, and `ToolNameScope.ScopeOf` reads them.
4. **Every pinned name moves**: the contract test (`McpToolContractTests`), the inventory test
   (`ToolInventoryTests`), `docs/reference/agent-memory-server.md`, the Python evaluation harness
   (`scripts/src/retrieval_tuning/mcp.py`), the CLI's own copy of the surface if it has one, and
   the ai-raccoon skill/hook material.
5. **The ai-badger framework speaks the old names.** ~900 occurrences across ~40 files in the
   framework checkout: the memory-first gate, the query pipeline, the grade hook, the
   ai-raccoon-memory skill, the semantica skill, the hermes provider docs, and the generated
   agent instruction files. That is a framework change (feed-badger) that must land before the
   ai-raccoon release ships.
6. **This project consumes its own tools.** The `.mcp.json`/`.pi/mcp.json` registrations name the
   server, not the tools, but the agent instruction files and skills in this repo call the old
   names by hand.
7. **Version.** A breaking public-contract change is a major bump: 1.63.0 → 2.0.0, with a
   `docs/changelog/2.0.0-*.md` entry and the changelog index updated.

## Open decisions the owner still owes

- **(a) Follow-through** — settled by the owner on 2026-10-21: the writes split. `set_query_result_grade`
  records the 1–5 usefulness grade; `set_memory_usage_record` records the follow-through (the
  file the agent opened out of the served set).
- **(b) Transcripts** — the owner's question, answered above: a transcript is a file by structure
  and memory by origin. The recommendation is Memory, and it decides the discriminator (the
  `source_type` join, not the denormalised signature). Awaiting the nod.
- **(c) The two placeholder names** — settled by the owner on 2026-10-21
  (`trigger_memory_sharing`, `get_memories_sharing_ranking`).
