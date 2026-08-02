# AiRaccon

C# .NET 10 MCP server exposing random-number generation to AI assistants over the [Model
Context Protocol](https://modelcontextprotocol.io/), built on the
[ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) C# SDK.

> Domain: provides AI assistants with deterministic utility tools (random number
> generation) over MCP.
> Stacks: `dotnet`, `mcp`.

## What it does

AiRaccon is a small, dependency-light MCP server that registers one tool:

| Tool | What it does |
|---|---|
| `get_random_number` | Generates a random number between a minimum (inclusive) and maximum (exclusive) value |

Any MCP-capable client (VS Code Copilot Chat, Claude, other assistants) can call it. The
server is built on the MCP C# SDK (`ModelContextProtocol` 2.0.0) with tools declared via
`[McpServerTool]` attributes.

### Transports

- **stdio** (default) — what MCP clients expect when launching a server as a subprocess.
- **Streamable HTTP** — opt-in via `MCP_TRANSPORT=http`; serves the protocol at `/mcp`
  (launch profile `http`, `http://localhost:8080`).

Transport selection lives in one place: `McpTransportSelector` keys off the
`MCP_TRANSPORT` environment variable — anything other than `http` (case-insensitive)
runs stdio.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## Build & test

```bash
dotnet build
dotnet test
```

The test project (`tests/AiRaccon.Tests`, xunit.v3 + Shouldly) covers the tools and the
transport selector — 13 cases, keep them green.

## Quickstart — run it

Run from source with the stdio transport (the default):

```bash
dotnet run --project src/AiRaccon
```

Or with the HTTP transport:

```bash
MCP_TRANSPORT=http dotnet run --project src/AiRaccon
```

### Connect a client

To use the server from an MCP client (for example VS Code's `.vscode/mcp.json`, or Visual
Studio's `.mcp.json`):

```json
{
  "servers": {
    "AiRaccon": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "<PATH TO PROJECT DIRECTORY>"]
    }
  }
}
```

Then ask the assistant for a random number — e.g. "give me 3 random numbers" — and it
should use the `get_random_number` tool.

## Architecture

```
AiRaccon/
  src/AiRaccon/            # the MCP server (thin)
    Program.cs             # transport selection + MCP wiring
    McpTransportSelector.cs
    Tools/RandomNumberTools.cs
  tests/AiRaccon.Tests/    # xunit.v3 + Shouldly
  Directory.Build.props    # analyzers, warnings-as-errors
  Directory.Packages.props # central package versions
  docs/                    # canonical documentation tree (see docs/README.md)
```

The server keeps the [MCP layer thin](CLAUDE.md): `Tools/` maps parameters and formats
results, with no business logic of its own. Warnings are errors
(`TreatWarningsAsErrors`), analyzers are on, and package versions are managed centrally.

## Packaging & release

The server packs as a .NET tool (`PackAsTool`, package id `ai-raccon`, type `McpServer`):

```bash
dotnet pack -c Release
```

To deploy to the local NuGet feed (`.nupkg-local/`), set `dotnet_env=local` for the
directory — the `DeployToLocalSource` build target pushes the freshly built package. The
package embeds `.mcp/server.json`, so MCP clients can discover inputs.

## Contributing

Read [`CLAUDE.md`](CLAUDE.md) first — it is the source of truth for this repo's rules:

- **TDD is mandatory** — a failing, behavior-focused test precedes any production change.
- **One task per PR** — every unit of work ends in a pull request; never push directly to
  `main`. The one exception is an explicit instruction from the person you work with.
- Keep the [non-negotiable invariants](CLAUDE.md) (clean layering, minimal comments,
  guarded nulls, no hardcoded secrets, …).

Architecture decisions are recorded as ADRs under
[`docs/adr/`](docs/adr/README.md).

## Security

Do not open a public issue for a security problem — report it privately; see
[`SECURITY.md`](SECURITY.md) for the reporting channel, supported-versions policy, and the
threat model.

## License

MIT — see [`LICENSE`](LICENSE). Copyright (c) 2026 Rafał Araszkiewicz.
