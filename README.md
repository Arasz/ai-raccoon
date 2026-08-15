# AiRaccoon

[![build](https://github.com/Arasz/ai-raccoon/actions/workflows/build.yml/badge.svg)](https://github.com/Arasz/ai-raccoon/actions/workflows/build.yml)
[![nightly](https://github.com/Arasz/ai-raccoon/actions/workflows/nightly.yml/badge.svg)](https://github.com/Arasz/ai-raccoon/actions/workflows/nightly.yml)
[![publish](https://github.com/Arasz/ai-raccoon/actions/workflows/publish.yml/badge.svg)](https://github.com/Arasz/ai-raccoon/actions/workflows/publish.yml)
[![NuGet](https://img.shields.io/nuget/v/ai-raccoon.svg)](https://www.nuget.org/packages/ai-raccoon)

An MCP server providing AI agents with persistent, project-scoped memory. Built on .NET 10 with local-first SQLite, hybrid FTS5+vec0 search, workspace sandboxes, a shared promotion tier, and optional cloud sync.

```mermaid
flowchart LR
    subgraph Clients ["MCP Clients"]
        Agent["Claude Code / Hermes / IDE"]
    end

    subgraph Core ["AiRaccoon Core Stack"]
        Proxy["ai-raccoon (Proxy)"]
        Server["ai-raccoon serve (HTTP Backend)"]
        Store[("SQLite memory.db\nFTS5 + vec0 + Workspace + Shared")]
        
        Agent <-->|JSON-RPC| Proxy
        Proxy <-->|Loopback :7721| Server
        Server <--> Store
    end

    subgraph Sync ["Cloud Sync (Optional)"]
        Cloud[("S3 / Azure Blob")]
        Server -.-|Snapshot Sync| Cloud
    end
```

## What's new

- **The bank now compacts itself, and repairs entries too long to be searchable.** (1.17.0) [ADR-0070](docs/adr/0070-maintenance-is-a-list-of-jobs-with-a-ledger.md)
- **The bank reclaims most of its vector storage.** (1.16.0) [ADR-0068](docs/adr/0068-ctx-is-a-vec0-metadata-column-not-a-partition-key.md)
- **A long `memory_write` is now searchable across its whole length.** (1.15.0) [ADR-0064](docs/adr/0064-memory-write-chunks-like-everything-else.md)
- **Naming `shared` on a write asks for promotion instead of bypassing review.** (1.15.0) [ADR-0067](docs/adr/0067-naming-shared-asks-for-promotion.md)
- **Workspaces no longer require `full`.** (1.13.0) [ADR-0052](docs/adr/0052-the-workspace-lifecycle-is-a-write-not-a-destruction.md)
- **SECURITY — a delete could name another project, and wipe the shared tier.** (1.13.0) [ADR-0051](docs/adr/0051-a-context-never-names-another-project.md)
- **BREAKING — `memory_search`'s `minScore` is now `minRelativeScore`, and defaults to off.** (1.12.0) [ADR-0047](docs/adr/0047-relative-score-floor.md)
- **Section-anchored search works, and ranking improved with it.** (1.12.0) [ADR-0044](docs/adr/0044-section-fts-weight.md)
- **Noise filtering, rebuilt around what could be measured.** (1.12.0) [ADR-0040](docs/adr/0040-read-path-query-guard.md)
- **Honest write outcomes and one explicit TTL path.** (1.12.0) [ADR-0032](docs/adr/0032-truthful-write-outcome.md) · [ADR-0034](docs/adr/0034-explicit-ttl-is-authoritative.md)
- **Semantic promotion classifier removed.** (1.11.0) [Why it was removed](docs/work/2026-08-13-fixing-zero-shot-promotion-classifier.md)
- **Semantic Noise Filtering & Real-time TTLs.** (1.9.0) [ADR-0029](docs/adr/0029-pre-write-noise-filtering.md) · [ADR-0030](docs/adr/0030-realtime-heuristic-ttl.md)
- **FileType Handlers & Native JSON Support.** (1.8.0) [ADR-0027](docs/adr/0027-extensible-file-type-handlers-and-json-support.md)
- **Search-Quality Metric System.** (1.7.0) [Plan](docs/plans/2026-08-11-search-quality-metric-plan.md)
- **Persistent Propose Queue Discards.** (1.6.5) [ADR-0026](docs/adr/0026-persistent-discards-and-shared-exclusion.md)
- **Always-On HTTP Proxy.** (1.6.0) [ADR-0020](docs/adr/0020-always-on-http-stdio-proxy.md)

---

## Quick Start

Install the global CLI tool (`ai-raccoon`):

```bash
dotnet tool install -g ai-raccoon
```

Add AiRaccoon to your agent's `.mcp.json`:

```json
{
  "mcpServers": {
    "ai-raccoon": { "command": "ai-raccoon" }
  }
}
```

> 📖 **Full Walkthrough:** See [Get started with AiRaccoon](docs/tutorials/get-started-with-ai-raccoon.md) for migration guides, transport options (proxy, stdio, http), and initial setup steps.

---

## Agent Capabilities at a Glance

```mermaid
mindmap
  root((AiRaccoon Agent Memory))
    Hybrid Search
      FTS5 Lexical
      vec0 Vector KNN
      RRF Fusion
    Workspace Sandboxes
      Isolated Outbox
      Consolidate / Discard
    Shared Tier
      Cross-Project Fact Base
      Sweep-Exempt
    Lifecycle & Decay
      Retrieval Rating Boost
      TTL Degradation Reaper
    Security & Encryption
      SQLite3MC ChaCha20
      Authenticated Loopback
```

| Feature | Description | Reference Guide |
|---|---|---|
| **Scope Partitioning** | Local banks in `~/.ai-raccoon` or `<project>/.ai-raccoon`, partitioned by `project:<id>` | [Capabilities Overview](docs/explanation/agent-memory-capabilities.md#storage-architecture--scope-partitioning) |
| **Hybrid Search** | Reciprocal Rank Fusion (RRF) combining keyword and vector semantic search | [Search Pipeline Guide](docs/explanation/agent-memory-capabilities.md#hybrid-search-pipeline-fts5--vec0--rrf) |
| **Workspace Sandboxes** | Isolated edit outboxes with explicit consolidation or discard | [Workspace Guide](docs/explanation/agent-memory-capabilities.md#workspace-sandbox-context-lifecycle) |
| **Shared Tier** | Elevated cross-project facts exempt from degradation sweeps | [Shared Tier Guide](docs/explanation/agent-memory-capabilities.md#propose--shared-promotion-tier) |
| **Memory Degradation** | Retrieval-based rating boost with automated background TTL sweeps | [Degradation Guide](docs/explanation/agent-memory-capabilities.md#memory-rating-and-degradation-sweep-reaper) |
| **Cloud Sync** | Optional S3 / Azure Blob VACUUM snapshot sync with optimistic locking | [Architecture Explanation](docs/explanation/architecture.md#cloud-sync-cycle) |
| **Encryption at Rest** | Page-level ChaCha20 encryption via `AIRACCOON_DB_PASSPHRASE` | [Configuration Recipe](docs/how-to/configure-ai-raccoon-server.md#managing-database-encryption) |

> 📖 **Detailed Architecture:** Read the complete [Agent Memory Capabilities Explanation](docs/explanation/agent-memory-capabilities.md) and [Tool Contract Reference](docs/reference/agent-memory-server.md).

---

## Configuration & Server Execution

Run in proxy mode (default), stdio, or background HTTP serve mode:

```bash
ai-raccoon                    # Proxy mode (Default): relays to HTTP backend, auto-starting if needed
ai-raccoon --transport stdio  # In-process standalone server
ai-raccoon serve              # Long-lived daemon with idle watchdog and loopback token auth
```

> 📖 **Configuration Recipe:** Learn about environment variables, port binding, database encryption passphrases, and zero-downtime updates in [Configure and run the AiRaccoon server](docs/how-to/configure-ai-raccoon-server.md).

---

## Embeddings

AiRaccoon supports in-process ONNX models and remote OpenAI-compatible backends:

| Engine | Model | Latency | Benchmark MRR |
|---|---|---|---|
| **Local (Default)** | Bundled `all-MiniLM-L6-v2` (int8) | ~9 ms | 0.836 |
| **Remote OpenAI** | `text-embedding-3-small` / Ollama | ~25-120 ms | 0.854 - 0.858 |

> 📖 **Setup & Benchmarks:** See [Configure embedding engines](docs/how-to/configure-embedding-engines.md) and [Embedding Benchmark Data](docs/reference/embedding-benchmark.md).

---

## Observability & Telemetry

Inspect live performance or stream OpenTelemetry metrics and traces:

```bash
ai-raccoon serve observability pid        # Discover live server PID
ai-raccoon serve observability counters   # Launch dotnet-counters
ai-raccoon serve observability trace      # Capture dotnet-trace spans
```

> 📖 **Telemetry Guide:** See [Monitor and export server telemetry](docs/how-to/monitor-and-export-telemetry.md) and [OTLP Export Boundaries (ADR-0009)](docs/adr/0009-otlp-export.md).

---

## Architecture Overview

```text
src/AiRaccoon/                 # Thin MCP Server (Tool handlers)
src/AiRaccoon.Core/            # Pure Domain (Memory logic, RRF, Workspace, Rating)
src/AiRaccoon.Infrastructure/  # SQLite Store, Embeddings, S3/Azure Sync
tests/AiRaccoon.Tests/         # xunit.v3 test suite (1100+ tests)
```

> 📖 **Deep Dive:** Read [Architecture Explanation](docs/explanation/architecture.md).

---

## Documentation Index

Explore the complete [Documentation Tree](docs/README.md):

- [Tutorials](docs/tutorials/README.md) — Step-by-step guides for newcomers
- [How-To Recipes](docs/how-to/README.md) — Goal-oriented configuration and operations
- [Explanation](docs/explanation/README.md) — Architectural background and design concepts
- [Reference](docs/reference/README.md) — Tool contracts, CLI verbs, and benchmarks
- [ADRs](docs/adr/README.md) — Architectural Decision Records

---

## Contributing & Security

- Read [CLAUDE.md](CLAUDE.md) for repo conventions and mandatory TDD workflow.
- `scripts/` holds standalone Python tooling (embedding-model download, JSAA docs ingest,
  benchmark-corpus generation, and more) — see [Run the Python scripts](docs/how-to/run-the-python-scripts.md)
  for setup with `uv`.
- Report security issues privately per [SECURITY.md](SECURITY.md).

## License

[MIT](LICENSE). Copyright (c) 2026 Rafał Araszkiewicz.
