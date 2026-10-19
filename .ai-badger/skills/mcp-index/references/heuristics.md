# Auto-tagging heuristics

These are the **last resort** — they run only where the catalog has nothing to say about a tool.

A name substring may infer an *action*; it must never guess a *technology* (issue #171 found
`build` implying `dotnet`, and `log` matching inside `dialog` to imply `opentelemetry`). Only a
tight, unambiguous alias earns a technology tag.

| Tool name pattern | Assigned tags |
|---|---|
| Contains `database`, `schema`, `db` | `[database]` |
| Contains `sql` | `[database, sql]` |
| Contains `build` | `[build]` |
| Contains `search`, `find` | `[search]` |
| Contains `symbol` | `[semantic, search]` |
| Contains `problem`, `error`, `diagnostic` | `[diagnostic]` |
| Contains `span`, `otel`, or the compound `service_map` | `[tracing, opentelemetry]` |
| Contains `browser`, `navigate`, `screenshot` | `[browser]` |
| Contains `run`, `execute` | `[run]` |
| Contains `refactor`, `rename`, `reformat` | `[refactoring]` |
| Server is `playwright` | adds `[browser]` |
| **First** `_`/`-` token is `get`, `list`, `fetch`, `download`, `export`, `whoami`, `describe` | adds `[read]` |
| First token is `query`, `search` | adds `[read, search]` |
| First token is `create`, `update`, `delete`, `add`, `upload`, `set`, `patch` | adds `[write]` |
| First token is `cancel`, `pause`, `resume`, `start`, `stop` | adds `[run]` |
| No match | `[general]` |

The verb rules match the whole first token only, so `budget_report` is not `get` and
`settings_page` is not `set`. They name an action, never a technology.

A tool with no description gets the intent `TODO: describe what <tool> does`
(`tool_descriptions.PLACEHOLDER_INTENT`). `validate` reports it as `intent is a placeholder`, so an
index of unwritten intents never passes.
