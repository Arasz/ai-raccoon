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

On session start, run `memory_watch_status(project_id)` for this project. If the docs directory
is not in the watched list, run `memory_watch_add(project_id, <absolute path to docs>)` to mirror
it into memory. The watch starts `scanning` and settles to `healthy`; an already-watched path is
a no-op.

**CLI prerequisite (only when the watch errors):** `watching-disabled` or `path-outside-scope`
means the one-time per-install setup is missing (quote the `*` so the shell does not expand it):
`ai-raccoon watch scope add '<project-id|*>' <path>`, then
`ai-raccoon watch enable '<project-id|*>' true`. If the `memory_watch_*` tools are not listed at
all (older tool build on another machine), update the tool: `dotnet tool update -g arasz.ai-raccoon`.

## 2. Search-first workflow

Always pass `project_id`. Before web search, code search, or asking the user, run
`memory_search(project_id, scope=all)` with 2-3 formulations: exact phrase → keywords →
plain-English restatement. Entries carry source paths — cite them as evidence.

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
low-rated entries; shared entries are exempt. Only entries carrying a TTL are sweepable at
all — set one with `memory_set_ttl` (and note a TTL alone is not enough: the entry's rating
must also be below the sweep threshold).

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

## 8. Memory-grade hook (dogfooding, default off)

Every `memory_search` can be logged to a machine-wide quality log with a 1-5 usefulness grade
filled in afterwards — retrieval-quality telemetry correlated by `projectId`/`workspaceId`,
`host` and `sessionId` across sessions. **Off by default**: the hook does no reads, no writes,
and no injection until the env var is set. Enable on the machine (then restart the agent host):

```sh
echo 'export AI_BADGER_MEMORY_GRADE=1' >> ~/.zshrc
launchctl setenv AI_BADGER_MEMORY_GRADE 1
```

**Host coverage (verified 0.80.0):** the hook fires on Claude Code/Copilot via the
PostToolUse hook, and on Hermes only when the `ai-badger` plugin is installed as a directory
plugin (`~/.hermes/plugins/ai-badger/` with `plugin.yaml` + `register(ctx)`, shipped by the
scaffold when `hermes` is an agent) **and enabled**:

```sh
hermes plugins enable ai-badger   # or plugins.enabled: [ai-badger] in ~/.hermes/config.yaml
```

Before trusting the log on any host, verify the capture path once: set the env var, run one
organic `memory_search`, and confirm a line appeared (see the checklist). A missing line means
"no capture", not "no usage" — the `host`/`sessionId` fields make the two distinguishable.

When on, each search appends one line to `~/.ai-badger/memory-grade/memory-quality.jsonl`
(with `usefulness: null`), and the very next turn asks the agent to rate it. Answer by filling
the grade in place — nothing is lost when an ask goes unanswered:

```sh
python3 ~/.hermes/plugins/ai-badger/memory_grade.py grade <ts> <1-5> [note]
python3 ~/.hermes/plugins/ai-badger/memory_grade.py probe  # config state, log path, last 3 lines
```

The hook line is a superset of the manual shape: `ts, query, scope, projectId, workspaceId,
host, sessionId, result, usefulness, note` — `host` (hermes/claude/copilot) and `sessionId`
are null in manual lines.

## Verification Checklist

- [ ] `memory_watch_status` shows the docs dir `healthy`
- [ ] `memory_search(project_id, scope=all)` returns docs-derived hits
- [ ] A durable finding was written back with `memory_write`, source path included
- [ ] `AI_BADGER_MEMORY_GRADE=1` is set in the host env and the host was restarted
- [ ] One organic `memory_search` appended a line to
      `~/.ai-badger/memory-grade/memory-quality.jsonl` (with `host` set)
