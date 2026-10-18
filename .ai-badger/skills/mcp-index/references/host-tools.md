# Host tools — the measured cost, the name mapping, the statuses

`scripts/host_tools.py` reads Claude Code's headless init event for `mcp-index --discover`.

Measured on Claude Code 2.1.302 (2026-10-18):

| cost | measured |
|---|---|
| wall time | ~5.5s to the init event (it waits for the host's own MCP connections) |
| model turn / billing | none: `/cost` is a local command, 0 API requests, `total_cost_usd: 0` |
| hooks | none: `disableAllHooks` ran 0 settings hooks (43 in the same project without it); it does not switch off managed-policy hooks |
| transcript | none under `~/.claude/projects` (`--no-session-persistence`) |
| other writes | Claude Code's own `~/.claude.json` bookkeeping and plugin-cache refresh, as any start does |

The host does the authenticating, so ai-badger handles no credentials and never reads Claude Code's
token stores. The event's tool names are `mcp__<server>__<tool>`, the server name with every
character outside `[A-Za-z0-9_-]` turned into `_` (`claude.ai Exa` → `claude_ai_Exa`,
`plugin:context7:context7` → `plugin_context7_context7`). Two names that normalize alike, or one
tool name that parses more than one way (two servers, or two splits of one name such as server `x_`
and `mcp__x___t`), refuse every server involved, because guessing would misfile tools. A server the
event reports **`connected`** with tools gets them **added**, never as the whole truth: init's
`tools` list is filtered by permission deny rules and managed policy while the server still reads
`connected`, so a curated or seeded tool it lacks is never marked `removed`. A connected server with
0 tools is left as listed. The event carries **names only**, so intents come from the catalog when
it covers the tool, else a `TODO` heuristic, never an invented description. Any other host status
(`needs-auth`, `failed`, `pending`) leaves the server's tools untouched and is noted (`no config;
claude init reports needs-auth`). When the listing gave no status of its own, it maps onto the
listing vocabulary (`pending` becomes `pending_approval`). A direct answer always wins over the
host, servers refused for approval stay refused, and the host source never makes an unnamed source
`absent`. A host run that fails (no init event, timeout, `claude` exiting) fails only the host
source: `(claude init failed: <reason>)`.
