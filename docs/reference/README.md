# reference/

Information-oriented lookups consulted mid-task: tool contracts, environment variables,
packaging metadata. Filenames are bare nouns.

## Contents

- [`agent-memory-server.md`](agent-memory-server.md): the MCP server's complete
  agent-facing contract, 29 tools, 2 prompts, contexts, env vars, launch flags and
  transports, error shapes.
- [`ai-raccoon-ignore-template.ignore`](ai-raccoon-ignore-template.ignore): a commented
  starter `ai-raccoon.ignore` covering common noise families; copy it to a watched root
  and delete what doesn't apply.
- [`cli-reference.md`](cli-reference.md): the complete `ai-raccoon` verb tree and the
  full ADR-0107 exit-code table, derived from the CLI's own command definitions.
- [`embedding-benchmark-report.html`](embedding-benchmark-report.html): the interactive
  report backing `embedding-benchmark.md`'s numbers.
- [`embedding-benchmark.md`](embedding-benchmark.md): measured retrieval quality and
  latency per embedding model, current bundled default first, with every metric
  explained and a size/speed recommendation.
- [`logging-event-ids.md`](logging-event-ids.md): the measured, zero-duplicate
  `[LoggerMessage]` `EventId` allocation across the solution, and how the table is
  reproduced.
- [`releases-and-publishing.md`](releases-and-publishing.md): how a GitHub release
  tag relates to (and doesn't guarantee) a nuget.org package, and how to check what's
  actually installable.
- [`search-parameters.md`](search-parameters.md): every `memory_search` tuning
  parameter, its settings-table key, its CLI verb, and its default, with the
  precedence order between them.
- [`whats-new-history.md`](whats-new-history.md): archived release highlights,
  1.6.0 through 1.43.0, moved here from the README.
