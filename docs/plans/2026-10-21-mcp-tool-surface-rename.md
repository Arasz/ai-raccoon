# The proposed MCP tool surface: names, entry types, and the moves they force

**Date:** 2026-10-21
**Status:** proposed by the orchestrator, pending the owner's nod. Every name below is a
*proposal*; the plan lanes must not treat it as settled.

**Owner decisions already taken:** hard rename (a breaking change, no aliases); the ai-badger
framework is updated before the release ships; high-effort loop; the quality surface splits
into a read tool and one write tool; an entry has exactly one type (settled 2026-10-22), so the
stored kind is single-valued, `Code` names a corpus rather than a row kind, and the flag set
belongs to the query parameter alone; the stored column is NOT NULL with the default `none`
(same ruling), so a row that carries no kind yet says so in the column rather than by a null.

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
| `memory_workspace_consolidate` | `close_workspace` | closes a sandbox, promoting the kept hashes into the project. Promote-on-close is intended (owner, 2026-10-21), so the tool's contract text must say that closing promotes |
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
`ingest_*`, `delete_*`, `share_*`, `trigger_*`, `set_*`, `initialize_*`, `close_*`,
`discard_*`, `sync_*`, `embed_*`, `save_*`.

The owner's corrections (2026-10-21) settled the sharing family — `share_memory`,
`trigger_memory_sharing`, `get_memories_sharing_ranking`, `delete_memory_from_ranking` — the
workspace family — `initialize_workspace`, `get_workspace_status`, `close_workspace`,
`discard_workspace` — the statistics family — `get_store_statistics` (the store) and
`get_engine_statistics` (the engine) — and split the quality writes into
`set_query_result_grade` (the grade) and `set_memory_usage_record` (the follow-through).

### TTL belongs to Memory only — the rule the implementation must enforce

`ttl_days`, `rating` and `access_count` exist on `entries` and not on `code_entries` (the
schema says so: "no rating/ttl_days/access_count — no degradation, code is a re-derivable
cache"), so a Code entry can never carry a TTL. The owner's further rule is that a **File**
entry must not carry one either: an ingested document is a mirror of a file on disk,
re-derivable by re-ingesting, so a TTL on it would expire content the watcher would put back.

`set_memory_ttl` therefore has to refuse a File entry by type, not merely by hash — the tool
knows the entry's type from the same discriminator `query` and `get_entry` use. On the live bank
no row of any type carries a TTL today (0 manual, 0 file, 0 transcript), so the rule is
forward-looking and there is no legacy data to migrate; the sweep keeps honouring whatever
`ttl_days` a row carries.

### A transcript: a file by structure, memory by origin

Measured on the live bank, the 1,496 `transcript` rows are all chunked
(`total_chunks > 0`), all carry `source_file = hermes/<timestamp>_<id>` (a session locator, no
path, no extension), and all belong to the project `hermes-default`. Their content is the
agent's own account of a session. The write path classifies them by the `hermes/` prefix
(`SqliteMemoryStore.ClassifySource`), which is why they are `SourceType.Transcript` rather than
`File`.

So: structurally a transcript is a file's chunks and lands in the File population under the
denormalised signature; semantically it is agent-authored memory whose locator happens to be a
session.

**Settled by the owner on 2026-10-21: transcript → Memory.** And it must be *fixed*, not merely
classified: today a transcript falls on the File side of the cheap signature, and it has to be
moved. That fixes three things at once:

1. **The discriminator.** `memory_source.source_type` decides: `manual` | `transcript` → Memory,
   `file` → File. The denormalised `source_file IS NULL AND total_chunks = 0` signature is
   ruled out — it cannot express the transcript case at all.
2. **The historical rows.** The fix has to reach the rows already in the bank, not only the ones
   written after it. On this bank that is **1,496 transcript rows** (all in project
   `hermes-default`, all chunked, all carrying `source_file = hermes/<timestamp>_<id>`). If the
   implementation denormalises the type onto `entries`, the backfill must classify those rows as
   Memory — a backfill that reads the signature would put them back on the wrong side.
3. **The TTL rule's reach.** A transcript is Memory, so it may carry a TTL; a File entry (an
   ingested document) may not. The two rules now agree on one classifier.

## The entry type

`query` and `get_entry` gain an **entry type** parameter with the values Memory, Code, File.

- `query` defaults to **Memory | Code | File** (the sum), so the default answers exactly what
  today's default (`kind=both`) answers.
- `get_entry` has **no default**: the type is required, because a hash alone does not say which
  corpus to read from — and the merged `get_entry` must know where to look.

**Settled by the owner on 2026-10-21: the entry type is part of the query path, for sure.** The
classifier is not a `get_entry` detail that happens to be reused in search — it is a first-class
part of the query path, so the search itself filters by entry type. Two consequences the
implementation must honour:

1. The classifier has to be available where the search selects candidates, not only where a hash
   is resolved. A discriminator that needs a join per candidate row has to earn its cost in the
   hot path; the denormalised column is the shape that does.
2. The type filter and the scope filter (`all` | `project` | `shared`) compose: a `shared` query
   with type `Code` is empty by construction, exactly as a `shared` query is empty for the code
   leg today (ADR-0137), and the refusal text must not pretend otherwise.

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
- **(b) Transcripts** — settled by the owner on 2026-10-21: transcript → Memory, and the existing
  transcript rows must be moved onto that side (the `source_type` join, plus a backfill that
  reaches the 1,496 historical rows if the type is denormalised). See the section above.
- **(c) The two placeholder names** — settled by the owner on 2026-10-21
  (`trigger_memory_sharing`, `get_memories_sharing_ranking`).

## The release split (decided 2026-10-22)

**2.0.0 is the rename alone. The entry types ride in 2.1.0.**

The rename is a client-side string change with no effect on the engine and **no schema change at
all** — the bank is untouched by an upgrade to 2.0.0. The entry-type wave brings schema v20: the
denormalised `entry_type`, the ladder step, and the 1,496 transcript rows moving onto the Memory
side — with its own failure shapes (a half-done backfill that must resume, a row with no
provenance falling back to its shape). Carrying both in one release would let an upgrade fail in
two independent ways and make a rollback of either drag the other.

The parameter also lands on the search's hot path, and a rename release is the wrong debugging
story for it: "did this ranking regression come from the rename or from the new parameter?" is a
question a release should not create.

And the contract is already public: ai-raccoon #909 is up, ai-badger #650 is timed against it,
and "2.0.0 = the 30 new names" is what callers are writing against. Keeping that claim true is
worth more than saving one minor version.

The entry-type work continues on `task/air-entry-type-classifier`, which builds on the rename —
so it is 2.1.0 by construction, and the rename's own verification pass is not invalidated by it.

**The schema cannot ship without the parameter.** Measured against the merged 2.0.0 binary, a
v20 bank refuses to open: `bank schema v20 is newer than this binary supports (v19)`, the MCP
server does not start, and every read-write verb fails the same way (`doctor` still runs and
names the mismatch). ADR-0019's forward-version write guard is why — the ladder only ever moves
forward, so there is no downgrade path. The v20 step fires on the first read-write open, not
lazily.

So a schema-only 2.1.0 is a one-way door: every user's bank becomes unreadable by 2.0.0, in
exchange for a type no client can ask about, and a rollback is a dead end rather than a
rollback. 2.1.0 therefore ships the schema v20, the entry-type parameter and the TTL rule
together. The breaking-changes entry for 2.1.0 says so in as many words: "2.1.0 migrates your
bank to v20; 2.0.0 can no longer open it."
