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

Also watch `.semantica/` (one-time per project): `mkdir -p .semantica`, then
`memory_watch_add(projectId, <absolute path to .semantica>)`. Re-adding is a no-op; the durable
record lives in memory, so gitignore `.semantica/` in the consumer repo.

**CLI prerequisite (only when the watch errors):** `watching-disabled` or `path-outside-scope`
means the one-time per-install setup is missing (quote the `*` so the shell does not expand it):
`ai-raccoon watch scope add '<project-id|*>' <path>`, then
`ai-raccoon watch enable '<project-id|*>' true`. If the `memory_watch_*` tools are not listed at
all (older tool build on another machine), update the tool: `dotnet tool update -g arasz.ai-raccoon`.

## 2. Search-first workflow

Always pass `projectId` and `sessionId`. Pass your session id on every search. The server rejects a blank one and stores the value verbatim on the search row. Before web search, code search, or asking the user, run `memory_search(projectId, sessionId, scope=all)` with 2-3 formulations. Try the exact phrase first, then keywords, then a plain restatement. Entries carry source paths. Cite them as evidence. Every reply carries `meta.correlationId`. Keep that id. You need it to grade the search or to record that you opened one of its files.

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

## 5. Scopes and kinds

`scope=all` (default: shared + project), `scope=project`, `scope=shared` (the promotion tier only). Kind defaults to `both`. It runs the memory and code hybrids separately and returns both sections. `kind=memory` searches memory only. `kind=code` searches code only. Recording follows the request. Memory and both store the memory leg count and files. Code stores the code count with an empty file list. Code paths never enter the table.

## 6. Gotchas

- `memory_write` has **no `path` param** — the entry path is derived from its content.
- **Never pass `context`** unless workspace isolation is intended: it silently sets
  `scope='custom'`, invisible to project-scoped search.
- `memory_embed_pending`: omit `limit` to process all pending entries.
- `memory_delete_context` requires full access mode.
- When you open a file the search returned, call `memory_record_followthrough` with the same `correlationId`, the file path, and `servedRank` when you saw one. Rank is 1-based. Under `kind=both` a bare rank cannot name its section. That ambiguity is intentional, so send no section qualifier. Grade with `memory_record_grade` (1-5) on the same id when you have a judgment.
- A `memory_search` result (`kind=code` or `kind=both`) carrying a warning starting
  `code engine not configured` means its code section is FTS5 keyword matches only — semantic
  code hits are missing, not absent; don't read it as a complete answer. Tell the user, once
  per session, verbatim: run `ai-raccoon model set code default` (downloads and activates the
  default code embedding model, ~187 MB, one-off) — don't repeat the hint on later searches in
  the same session. Don't re-run the search hoping for vector results: nothing changes until
  that command runs.

## 7. Bulk ops

`memory_ingest_file` / `memory_ingest_directory` bulk-load files; `memory_stats` reports bank
size; `memory_sync` exchanges snapshots with cloud storage when configured.

## 8. Verification Checklist

- [ ] `memory_watch_status` shows the docs dir `healthy`
- [ ] `memory_search(projectId, sessionId, scope=all)` returns docs-derived hits
- [ ] A durable finding was written back with `memory_write`, source path included
