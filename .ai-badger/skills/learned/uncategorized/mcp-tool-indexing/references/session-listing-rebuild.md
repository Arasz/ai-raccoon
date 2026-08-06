# Session-listing rebuild recipe (verified 2026-08)

Full worked example: MCP servers reloaded (179 tools / 8 servers); `mcp-index update`
via `claude mcp list` reported "No changes" while the index was missing 21 tools.
This recipe fixed it; `validate` then passed with 189 tools.

## Why update can't see new tools

- `hermes mcp list --json` → exit 2 `error: unrecognized arguments: --json` (issue #188).
- `claude mcp list` → server names + status phrases only; `tools_known: false` for all.
- `hermes mcp list` text table → names + enabled flag only.
- Consequence: `update` restates statuses but can never add tools.

## Session catalog as the listing

The session's deferred MCP tool catalog (system prompt) names every tool as
`mcp__<server>__<tool>`. Strip the `mcp__<server>__` prefix — that is the tool name
the index uses (`<server>:<tool>` keys). Tool count check: the session total equals
the sum of the per-server counts (e.g. 22+34+16+51+25+7+24 = 179).

## Document schema

```json
{"servers": [
  {"name": "ai-raccoon", "tools": [{"name": "memory_search"}], "tools_known": true},
  {"name": "hermes",     "tools": [], "tools_known": false},
  {"name": "plugin:github:github", "tools": [], "tools_known": false,
   "host_status": "Failed to connect"},
  {"name": "llmstudio",  "tools": [], "tools_known": false, "enabled": false}
]}
```

Pass it as argv (inline JSON), not a path:
`mcp_index.py update --target <root> --from-json "$(cat /tmp/listing.json)"`
(`parse_hermes_json_listing` runs `json.loads(value)` on the argument itself).

## What the code actually does (mcp_index.py + host_listings.py)

- `carries_tool_detail(servers)` = `any(s.get("tools_known", True))` — defaults TRUE,
  so a hand-built document with any live server makes omission an evidence of removal.
- `_update_source(source, None)` → status `absent` + `_mark_removed` on ALL tools.
- `_sync_tools`: server `None` → wipe; `tools_known` false → untouched (return 0);
  `tools_known` true → add new, mark vanished removed.
- `server_status()` order: `host_status` phrase → `enabled is False` → disabled →
  `!tools_known` → unknown → `tools` present ? ok : empty.
- Claude line format: `<name>: <detail> - <marker><phrase>`; marker glyph is its own
  regex group; phrase = rest of line, `.split(" — ")[0]` strips the detail suffix.

## Status phrase mapping (measured)

| claude phrase | status |
|---|---|
| `✘ Failed to connect` (phrase="Failed to connect") | unreachable |
| `⏸ Pending approval` | pending_approval |
| `✔ Connected` (phrase="Connected" → None) | falls through to inference |
| `! Needs authentication` | unauthenticated |

hermes text table has no phrases; only `enabled` (→ disabled when false).

## Cross-host trap

claude may report `ai-raccoon`/`code-review-graph`/`hermes` as "Pending approval"
(claude hasn't approved the project's .mcp.json) while the hermes session is LIVE on
them. Do NOT copy that `host_status` into the document — the index source represents
the hermes connection; keep `tools_known: true` and no `host_status` → ok.

## Curation after update

New tools land `[general]`; `validate` lists them. Conventions applied:
- `get_prompt`, `list_prompts`, `list_resources`, `read_resource` → `[read]`
- `ai-raccoon:memory_share_extract` → `[read]` (sibling `memory_share` is `[write]`)
- `glider-trace:trace_restore` → `[opentelemetry, tracing, run]` (trace family)
- Batch via shell loop; `tag`/`intent` are idempotent and set origin=manual.

## Parallel-session race

A concurrent agent committed the first update mid-run (`chore(ai-badger): refresh
mcp-tools manifest...`). Symptoms: working-tree diff shrinks between commands.
Verify with `git log --oneline -- .ai-badger/mcp-tools.json` and
`git show HEAD:<file> | python3 -c '...count tools...'` before reporting; the
remaining diff may be only the follow-up status fixes.
