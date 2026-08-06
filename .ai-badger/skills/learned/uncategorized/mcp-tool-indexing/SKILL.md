---
name: mcp-tool-indexing
description: Use when the MCP tool index is stale after server reloads.
version: 1.0.0
author: hermes
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [mcp, indexing, tool-discovery, ai-badger]
    related_skills: [AiRaccoon/mcp-index, hermes-mcp-setup]
---

# MCP Tool Indexing

## When to use

- After an MCP server reload/add/remove: the tool index needs syncing.
- `mcp-index update` prints "No changes — index is up to date" but the session's
  live tool catalog has tools the index lacks.
- Per-server tool counts in the index don't match the session's
  `mcp__<server>__<tool>` names.

The underlying tool is the ai-badger `mcp-index` skill (`scripts/mcp_index.py`,
`mcp-index update/validate/tag/intent`). This skill carries the operational
knowledge that skill's docs don't: rebuilding the index when every host listing is
tool-less.

## The core insight

`claude mcp list` and `hermes mcp list` (text table) carry server NAMES and statuses
only — no tool names. Only `hermes mcp list --json` carried tools, and that flag no
longer exists (hermes issue #188). So a plain `mcp-index update` can restate
statuses but can never ADD tools that newer server builds expose (typically the
standard MCP lifecycle set: `get_prompt`, `list_prompts`, `list_resources`,
`read_resource`).

The authoritative tool-detail listing is the agent's own live session: the deferred
MCP catalog in the system prompt names every tool as `mcp__<server>__<tool>`.

## Rebuild workflow

1. **Diff.** Read the index (JSON, tools live under `sources[].tools`) and compare
   per-server counts against the session's tool names per server. Expect the
   missing set to be the standard lifecycle tools.
2. **Build the document** — `{"servers": [...]}`, one entry per index source,
   names matching the index's `sources[].name` EXACTLY:
   - Live servers: `{"name": ..., "tools": [{"name": ...}], "tools_known": true}`
   - Non-live sources (hermes bridge, rider, `plugin:<p>:<s>` copies, disabled
     servers): `{"name": ..., "tools": [], "tools_known": false}`
3. **Run**: `mcp_index.py update --target <root> --from-json "$(cat /tmp/listing.json)"`.
   `--from-json` takes the JSON DOCUMENT ITSELF as argv — not a file path.
4. **Validate + curate**: `mcp_index.py validate`; every new tool lands tagged
   `[general]` and fails validation until curated with `tag`/`intent` (idempotent,
   marks origin=manual).

## Pitfalls

- **Never omit an index source from a tools-carrying document.** `carries_tool_detail()`
  is true as soon as ANY server has `tools_known: true`; an omitted source then hits
  `_update_source(source, None)` → status `absent` + ALL its tools marked removed.
- `tools_known: false` sources are left completely untouched (status restated as
  `unknown`) — that is the safe shape for anything the session cannot see.
- **Status fidelity**: `server_status()` reads, in order: `host_status` phrase
  ("Failed to connect" → unreachable, "Pending approval" → pending_approval,
  "Connected" → falls through), then `enabled: false` → disabled, then
  `!tools_known` → unknown, else ok/empty. Do NOT carry `host_status` for a source
  whose ok status comes from a different host's connection — claude may report
  `ai-raccoon` pending_approval while the hermes session is live on it.
- **Curation conventions** (match sibling tools): standard lifecycle tools
  (`get_prompt`, `list_prompts`, `list_resources`, `read_resource`) → `[read]`;
  ai-raccoon `memory_share_extract` → `[read]` (`memory_share` is `[write]`);
  glider-trace `trace_restore` → `[opentelemetry, tracing, run]`.
- **Parallel sessions may commit your update mid-run** (this user runs concurrent
  agents). After the run, check `git log --oneline -- .ai-badger/mcp-tools.json` and
  `git show HEAD:<file>` tool counts before reporting; your remaining diff may only
  be follow-up status fixes.
- The index file is git-tracked; `update` is idempotent and preserves manual
  tags/intents (origin=manual).

## Verification

- `mcp_index.py validate` exits 0 with "OK: N tool(s) validated".
- Per-server counts in the index equal the session's catalog counts.
- Statuses match what the hosts actually report (ok for live servers, unreachable/
  disabled/unknown for the rest).

## Support files

- `references/session-listing-rebuild.md` — exact document schema, field semantics,
  and the verified rebuild recipe from a full session (2026-08).
