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
| No match | `[general]` |
