# reference/

Information-oriented lookups consulted mid-task: tool contracts, environment variables,
packaging metadata. Filenames are bare nouns.

## Contents

- [`agent-memory-server.md`](agent-memory-server.md) — the MCP server's complete
  agent-facing contract: 27 tools, 2 prompts, contexts, env vars, launch flags and
  transports, error shapes.
- [`embedding-benchmark.md`](embedding-benchmark.md) — measured retrieval
  quality and latency per embedding model (small local GGUF vs LM Studio
  served models), with every metric explained and a size/speed recommendation.
- [`logging-event-ids.md`](logging-event-ids.md) — the measured, zero-duplicate
  `[LoggerMessage]` `EventId` allocation across the solution, and how the table is
  reproduced.
