---
name: dotnet-workload
description: "Use when working on any .NET workload concern — BDD/Reqnroll testing, immutable domain modeling, flaky test diagnosis, BackgroundService/IHostedService review or testing, [LoggerMessage] log-line design, MCP servers in .NET, SQLCipher-encrypted SQLite, System.CommandLine CLI parsing, .NET tool/NuGet publishing, or an observability/instrumentation review — and you must pick which specialized dotnet skill covers it. This gateway routes; read manifest.json to choose a member and open that member's SKILL.md for the deep guidance."
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [dotnet, routing, gateway]
    related_skills: []
---

# dotnet-workload

One registered entry point for eleven specialized .NET skills. The members are not registered
skills — they live one nesting level below registration, where agents never look on their own.
This file is the router; `manifest.json` is the machine-readable map.

## How to route

1. Match the task against the table below (or the member `triggers` in `manifest.json`, which
   carry the same keywords).
2. Open exactly one member's `SKILL.md` — `manifest.json` gives each member's `paths.skill`.
3. Do the work from the member. If no member matches, say so; do not improvise from this page.

| Member | Open it when |
|---|---|
| [dotnet-bdd-testing](references/dotnet-bdd-testing/SKILL.md) | Use when adding Gherkin `.feature` files or a BDD runner (Reqnroll) to a .NET project. |
| [dotnet-domain-modeling](references/dotnet-domain-modeling/SKILL.md) | Use when modeling immutable C# domain layers: sealed records, guards, state transitions. |
| [dotnet-flaky-test-diagnosis](references/dotnet-flaky-test-diagnosis/SKILL.md) | Use when a .NET test fails in the full suite but passes alone, or flakes intermittently. |
| [dotnet-hosted-service-review](references/dotnet-hosted-service-review/SKILL.md) | Use when reviewing a PR that adds or modifies a .NET BackgroundService/IHostedService. |
| [dotnet-hosted-service-testing](references/dotnet-hosted-service-testing/SKILL.md) | Use when writing or reviewing .NET BackgroundService tests with FakeTimeProvider. |
| [dotnet-logger-message-design](references/dotnet-logger-message-design/SKILL.md) | Use when designing or testing [LoggerMessage] log lines in .NET. |
| [dotnet-mcp-server](references/dotnet-mcp-server/SKILL.md) | Use when adding MCP tools or servers to a .NET project. |
| [dotnet-sqlcipher-encryption](references/dotnet-sqlcipher-encryption/SKILL.md) | Use when working with SQLCipher-encrypted SQLite in .NET. |
| [dotnet-system-commandline](references/dotnet-system-commandline/SKILL.md) | Use when adding CLI argument parsing to a .NET app or dotnet tool. |
| [dotnet-tool-publishing](references/dotnet-tool-publishing/SKILL.md) | Use when packaging or publishing a .NET CLI tool or library to NuGet. |
| [observability-contract-review](references/observability-contract-review/SKILL.md) | Use when reviewing claims that all calls are instrumented. |

Each row names its member's SKILL.md under `references/` — open one only when its "Use when"
column matches the task, so a single member loads instead of all eleven.

## Gotchas

No environment-specific gotchas known.
