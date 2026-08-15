---
name: ai-raccoon-memory
description: >-
  Use when a project needs a memory server — search project and shared memory first, write
  durable facts with source paths, watch a docs directory, or promote facts across projects.
version: 0.1.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
scope: default
metadata:
  hermes:
    tags: [memory, retrieval, semantic-search, persistence]
    related_skills: [mcp-index, hermes-mcp-setup]
---

# AiRaccoon Memory

## When NOT to Use

- A one-off lookup ("have we seen X before?") — run `memory_search` and be done, no watch ritual, no write-back
- No docs directory to watch and no durable fact to write — the ritual adds ceremony, not value
- The memory-grade hook when you only need one answer — it is opt-in by env var; don't enable it for a single search

## 1. Watch-on-docs ritual (do this first)

On session start, run `memory_watch_status(projectId)` for this project. If the docs directory
is not in the watched list, run `memory_watch_add(projectId, <absolute path to docs>)` to mirror
it into memory. The watch starts `scanning` and settles to `healthy`; an already-watched path is
a no-op.

**CLI prerequisite (only when the watch errors):** `watching-disabled` or `path-outside-scope`
means the one-time per-install setup is missing (quote the `*` so the shell does not expand it):
`ai-raccoon watch scope add '<project-id|*>' <path>`, then
`ai-raccoon watch enable '<project-id|*>' true`. If the `memory_watch_*` tools are not listed at
all (older tool build on another machine), update the tool: `dotnet tool update -g arasz.ai-raccoon`.

## 2. Search-first workflow

Always pass `projectId`. Before web search, code search, or asking the user, run
`memory_search(projectId, scope=all)` with 2-3 formulations: exact phrase → keywords →
plain-English restatement. Entries carry source paths — cite them as evidence.

**A query is a question, not a payload — and the limit is a real number.** The bundled
embedding model reads **254 WordPiece tokens**, roughly **1,000 characters** of English prose.
Everything past that is cut from the query's vector before the search runs, so a long query is
matched on its opening and nothing else.

Measured on this project's own traffic: the median query is **61 characters** and works well.
Of the 57 queries over 1,200 characters, **every single one was pasted machine output** — HTTP
header dumps, log errors, test output — with the actual question in the first line and
thousands of characters of noise after it. One was 448,900 characters.

So: **paste the question, not the output.** If you are searching *about* a log or a stack
trace, extract the identifying line — the exception type, the error code, the failing test
name — and search for that. Keyword matching still sees your whole query, so nothing is lost
from FTS; it is the semantic half that goes blind past 254 tokens.

## 3. Escalation by result

- Decisive hit → use it; cite the source path.
- Partial hit → one targeted external search, then reconcile.
- No hit → search externally, then write the finding back with `memory_write` (source path included).

## 4. Write discipline

Durable facts only, one per entry, source included. Plain writes land in committed project
memory (`project:<id>`). For in-progress notes use workspace isolation:
`memory_workspace_begin` → `memory_workspace_status` → `memory_workspace_consolidate(keep=[...])`
(or `["all"]` to promote everything; `memory_workspace_discard` to drop). Promote durable
cross-project facts with `memory_share` — never automatically. `memory_sweep` removes old
low-rated entries; shared entries are exempt.

## 5. Scopes

`scope=all` (default: shared + project), `scope=project`, `scope=shared` (the promotion tier only).

## 6. Gotchas

- `memory_write` has **no `path` param** — the entry path is derived from its content.
- **Never pass `context`** unless workspace isolation is intended: it silently sets
  `scope='custom'`, invisible to project-scoped search.
- `memory_embed_pending`: omit `limit` to process all pending entries.
- `memory_delete_context` requires full access mode.

## 7. Bulk ops

`memory_ingest_file` / `memory_ingest_directory` bulk-load files; `memory_stats` reports bank
size; `memory_sync` exchanges snapshots with cloud storage when configured.

## 8. Verification Checklist

- [ ] `memory_watch_status` shows the docs dir `healthy`
- [ ] `memory_search(projectId, scope=all)` returns docs-derived hits
- [ ] A durable finding was written back with `memory_write`, source path included
