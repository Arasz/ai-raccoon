# reference/

Information-oriented lookups consulted mid-task: tool contracts, environment variables,
packaging metadata. Filenames are bare nouns.

## Contents

- [`agent-memory-server.md`](agent-memory-server.md) — the MCP server's complete
  agent-facing contract: 29 tools, 2 prompts, contexts, env vars, launch flags and
  transports, error shapes.
- [`embedding-benchmark.md`](embedding-benchmark.md) — measured retrieval
  quality and latency per embedding model (small local GGUF vs LM Studio
  served models), with every metric explained and a size/speed recommendation.
- [`logging-event-ids.md`](logging-event-ids.md) — the measured, zero-duplicate
  `[LoggerMessage]` `EventId` allocation across the solution, and how the table is
  reproduced.
- [`releases-and-publishing.md`](releases-and-publishing.md) — how a GitHub release
  tag relates to (and doesn't guarantee) a nuget.org package, and how to check what's
  actually installable.
- [`whats-new-history.md`](whats-new-history.md) — archived release highlights from
  versions older than 1.29.0, moved here from the README.
