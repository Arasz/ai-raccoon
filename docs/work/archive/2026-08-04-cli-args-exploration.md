# Research: CLI argument parsing for the ai-raccoon MCP server (global dotnet tool)

**Date:** 2026-08-04
**Question:** Which CLI-args approach should the ai-raccoon MCP server (a .NET 10 global dotnet tool, stdio transport by default) use, and what is the resulting CLI surface, precedence rule, and client-config shape?

<!--
Decision inputs: version/maintenance facts for each candidate parser, the stdout-protocol
constraint, the repo's secret and layering invariants, and the .mcp.json / MCP registry
manifest conventions the CLI must plug into.
-->

## Findings

### F1 — System.CommandLine is GA and current: latest stable 2.0.10, MIT, published 2026-07-14 [MEASURED]

The stable line that went GA in November 2025 (2.0.0, timed with .NET 10) is at 2.0.10 and is
the package's current stable; a 3.0.0-preview line exists but is not stable. The package targets
`net8.0` (zero dependencies) and `netstandard2.0` (System.Memory only), so it runs on net10.0
with no transitive baggage. The Microsoft Learn overview describes it as providing parsing and
help text, used by the .NET CLI itself and "many global and local tools"; it is trim-friendly.

**Evidence:**
- `curl https://api.nuget.org/v3-flatcontainer/system.commandline/index.json` → versions end
  `...2.0.9, 2.0.10, 3.0.0-preview.6.26359.118`; latest stable = 2.0.10.
- Catalog entry `.../catalog0/data/2026.07.14.17.30.59/system.commandline.2.0.10.json` →
  `licenseExpression: MIT`, `published: 2026-07-14`, dependency groups `net8.0 []` and
  `.NETStandard2.0 [System.Memory]`.
- GA timing: dotnet/command-line-api issue #2576 ("Announcing System.CommandLine 2.0.0-beta5 and
  our path to a stable release"): "Our objective is to publish a stable (non-preview) release of
  System.CommandLine 2.0.0 around the same time .NET 10 ships in November 2025."
- https://learn.microsoft.com/en-us/dotnet/standard/commandline/ (overview): "Apps that use
  System.CommandLine include the .NET CLI, additional tools, and many global and local tools."

### F2 — Spectre.Console.Cli is mature but still pre-1.0: latest stable 0.55.0, 1.0.0-alphas in flight [MEASURED]

Spectre.Console.Cli's stable line is 0.55.0; the newest published version overall is
`1.0.0-alpha.0.16`, so a 1.0 has not shipped. The project is very active (last push 2026-07-27,
~11.6k stars, MIT), which is the main point in its favor; the pre-1.0 API churn between 0.x
releases is the main cost. It is a full command application framework (commands, nested
commands, validation, DI via a type registrar, auto help) — far more than a flat ~10-option
daemon needs.

**Evidence:**
- `curl https://api.nuget.org/v3-flatcontainer/spectre.console.cli/index.json` → stable line ends
  `0.53.1, 0.55.0`; overall latest `1.0.0-alpha.0.16`.
- GitHub API `repos/spectreconsole/spectre.console` → `archived: false`,
  `pushed_at: 2026-07-27T19:34:12Z`, `stargazers_count: 11575`.

### F3 — The Configuration.CommandLine and Configuration.EnvironmentVariables providers already ship in the ASP.NET Core shared framework — zero new packages for option (c) [MEASURED]

`Microsoft.Extensions.Configuration.CommandLine.dll` and
`Microsoft.Extensions.Configuration.EnvironmentVariables.dll` are present in the
`Microsoft.AspNetCore.App` shared framework (10.0.10 and 10.0.9 installed on this machine). A
`Microsoft.NET.Sdk.Web` project therefore gets both providers without any new NuGet reference —
the only candidate with zero dependency cost.

**Evidence:** `ls /usr/local/share/dotnet/shared/Microsoft.AspNetCore.App/10.0.10/` lists
`Microsoft.Extensions.Configuration.CommandLine.dll` and
`Microsoft.Extensions.Configuration.EnvironmentVariables.dll` (also in 10.0.9 and
10.0.0-preview.6 under `~/.dotnet/shared`).

### F4 — `WebApplication.CreateBuilder(args)` already layers CLI args above env vars above appsettings: CLI > env > default is the framework default precedence [READ]

The default app configuration sources, highest to lowest priority, are: command-line arguments,
environment variables (non-`ASPNETCORE_`/`DOTNET_`), user secrets (Development only),
`appsettings.{ENVIRONMENT}.json`, `appsettings.json`. So "CLI overrides env, env overrides
built-in default" is exactly the layering the app already gets for free from
`WebApplication.CreateBuilder(args)` — option (c) is not a new mechanism, it is reading the
config the builder already builds.

**Evidence:** dotnet/AspNetCore.Docs `aspnetcore/fundamentals/configuration/index.md:81-87`
("Default app configuration is loaded in the following order, from highest to lowest priority:
1. Command-line arguments … 2. Environment variables …").

### F5 — The CommandLine configuration provider is a key-value provider, not a CLI parser: unknown options are silently accepted, no --help, no validation, no error output [READ]

`CommandLineConfigurationProvider.Load()` semantics from source: `--key value` and `--key=value`
are stored as key-value pairs; an unknown long option is **silently stored as a config key**; a
trailing `--key` with no following token is **silently dropped**; `--key --other` consumes the
next token as the value even when it starts with `-`; bare tokens (no `--`/`-`/`/` prefix) are
ignored; duplicate keys resolve last-wins; keys are case-insensitive; single-dash switches are
ignored unless listed in `switchMappings` (and throw `FormatException` when written as
`-x=value` without a mapping). There is no help generation, no unknown-option error, no
enum/type validation. That is fine for config layering and fatal for a user-facing CLI: a typo
(`--data-rot`) silently does nothing.

**Evidence:** `dotnet/runtime` main branch
`src/libraries/Microsoft.Extensions.Configuration.CommandLine/src/CommandLineConfigurationProvider.cs`
(fetched 2026-08-04): the `Load()` loop — "If there is neither equal sign nor prefix in current
argument, it is an invalid format … Ignore invalid formats"; "ignore missing values" for a
trailing switch; `data[key] = value` with no allowlist; "Override value when key is duplicated.
So we always have the last argument win."

### F6 — System.CommandLine writes help to stdout and parse errors to stderr by default; both writers are settable, so a stdio server can route all CLI text to stderr [READ]

`HelpAction.Invoke` renders help through `parseResult.InvocationConfiguration.Output`, whose
default is `Console.Out` (stdout). `ParseErrorAction` writes error messages to
`parseResult.InvocationConfiguration.Error`, whose default is `Console.Error` (stderr) — and then
also renders help (to stdout) after a parse error. Both `Output` and `Error` are settable
properties, and `InvocationConfiguration` is reachable from the parse result. Conclusion for the
stdio constraint: with default configuration, a bad invocation from an MCP client would put help
text on **stdout** and corrupt the protocol stream; the server must set `Output` (or invoke with
a custom console) so that **all** CLI text — help and errors — goes to stderr. This is a
one-line, well-supported configuration, not a fork.

**Evidence:** dotnet/command-line-api `main` branch, fetched 2026-08-04:
- `src/System.CommandLine/Help/HelpAction.cs:56-65` — `var output = parseResult.InvocationConfiguration.Output; … Builder.Write(helpContext);`
- `src/System.CommandLine/Invocation/ParseErrorAction.cs:47-53` — `var stdErr = parseResult.InvocationConfiguration.Error; … stdErr.WriteLine(error.Message);`
- `src/System.CommandLine/Invocation/InvocationConfiguration.cs:36-53` — `Output => _output ??= Console.Out`, `Error => _error ??= Console.Error`, both with public setters.

### F7 — Spectre.Console.Cli renders help and validation errors through IAnsiConsole, which defaults to stdout; redirecting is possible but is an extra, easy-to-forget step [READ]

Spectre.Console.Cli's `CommandApp` renders help and validation errors through the console it was
built with; the default is `AnsiConsole.Console`, which writes to stdout. Its test-double
captures stdout only by default (issue #1732: "`TestConsole` currently only captures stdOut"),
confirming the stdout-centric design. A stdio MCP server must therefore construct the app with a
custom console whose output stream is stderr — a supported but non-default configuration that
every future maintainer must remember to keep. Same MCP hazard as F6, with a heavier framework
attached.

**Evidence:**
- spectreconsole/spectre.console issue #1732 ("`TestConsole` currently only captures stdOut, it
  should capture stdErr as well") — stdout-only capture is the default behavior.
- spectreconsole.net CLI docs ("Build powerful command-line applications with
  Spectre.Console.Cli — type-safe argument parsing, nested commands, and dependency injection").
- The precise custom-console API for 0.55 (`CommandApp(IConfigurator, IAnsiConsole)` /
  `AnsiConsoleSettings.Out`) was not fetched from source — see F7 note in Still open.

### F8 — Cocona is archived and out of the running; McMaster.Extensions.CommandLineUtils is maintained but minimal [MEASURED]

Cocona (2.2.0, last stable) is archived (`archived: true`, last push 2024-08-13) — a dead
dependency for a brand-new tool. McMaster.Extensions.CommandLineUtils is active (latest stable
5.1.0, last push 2026-07-01, ~2.3k stars) but is a bare attribute-based helper: no DI, no help
customization beyond templates, fewer guarantees than the official parser. It offers nothing
option (b) doesn't, from a third party.

**Evidence:** GitHub API `repos/mayuki/Cocona` → `archived: True, pushed_at: 2024-08-13`; GitHub
API `repos/natemcmaster/CommandLineUtils` → `archived: False, pushed_at: 2026-07-01`; NuGet
flatcontainer `cocona/index.json` (latest stable 2.2.0) and
`mcmaster.extensions.commandlineutils/index.json` (latest stable 5.1.0).

### F9 — Client `.mcp.json` entries for stdio servers are `{command, args[], env{}}`; dotnet global tools appear as bare command names [READ]

The dominant stdio client-config shape is `{"mcpServers": {"<name>": {"command": …,
"args": […], "env": {…}}}}` (Claude Code / JetBrains / community convention; VS Code's agent
config uses the equivalent `servers` block with `"type": "stdio"`). When the server is a dotnet
global tool, `command` is the tool's command name and `args` carries flags — e.g. a
`vscode:mcp/install` link with `"command":"tsqlanalyze","args":["-mcp"]` for a .NET tool, or the
NuGet.Mcp.Server README's `"command": "dnx", "args": ["NuGet.Mcp.Server", "--source", …]`. The
.NET blog's C# MCP-server walkthrough uses `"command": "dotnet", "args": ["run", "--project",
…]` for source checkouts. There is no special handling for dotnet tools: the client spawns
`command args…` with `env` merged over the process environment.

**Evidence:**
- https://www.jetbrains.com/help/ai-assistant/mcp.html (mcpServers template with command + args)
- https://code.visualstudio.com/docs/agents/reference/mcp-configuration (`"type": "stdio",
  "command": "npx", "args": […]`)
- https://erikej.github.io/mcp/dotnet/copilot/2025/05/06/mcp-dotnet-copilot.html
  (`"command":"tsqlanalyze","args":["-mcp"]` — a dotnet global tool as an MCP server)
- https://www.nuget.org/packages/NuGet.Mcp.Server (`"command": "dnx", "args": ["NuGet.Mcp.Server",
  "--source", "https://api.nuget.org/v3/index.json", "--yes"]`)
- https://devblogs.microsoft.com/dotnet/build-a-model-context-protocol-mcp-server-in-csharp/

### F10 — The MCP registry manifest declares `packageArguments` (named `--flag={value}` or positional, with `{var}` substitution) separately from `environmentVariables` [READ]

The registry server manifest (`server.json`, schema 2025-10-17) models each package with
properties including `transport`, `packageArguments` ("A list of arguments to be passed to the
package's binary"), `environmentVariables`, and `runtimeArguments`/`runtimeHint`. Arguments are
`named` (`--flag={value}`) or `positional` (with `value` or `valueHint`); both support a
`variables` map whose values substitute `{curly_brace}` placeholders in the argument value, and
`isRepeated`. ai-raccoon's current manifest already declares the 11 `AIRACCOON_*` env vars and an
empty `packageArguments` — which remains correct under the zero-config default (F11), and the
schema explicitly keeps credentials out of the argument list by having a separate
`environmentVariables` channel.

**Evidence:** `https://static.modelcontextprotocol.io/schemas/2025-10-17/server.schema.json`
(fetched 2026-08-04): `definitions/Package/properties` = `[environmentVariables, fileSha256,
identifier, packageArguments, registryBaseUrl, registryType, runtimeArguments, runtimeHint,
transport, version]`; `NamedArgument` = `--flag={value}` with `variables` substitution;
`PositionalArgument` requires `value` or `valueHint`. Matches the existing
`src/AiRaccoon/.mcp/server.json` shape.

### F11 — Precedence should be CLI > env > default, and secrets must stay env-only: CLI args and .mcp.json entries are visible and shareable, env vars are not [INFERRED]

Reasoned from F4 (the framework already implements CLI > env > appsettings > defaults), from the
client-config mechanics in F9 (`.mcp.json` is a file that may live in a shared/committed project
config and is visible to every client and collaborator, while `env` values for secrets belong in
the client's user-scoped config), and from the repo invariants "No hardcoded secrets" and
"Credentials are read from the environment only" (`.ai-badger/CLAUDE.md`, `mcp.instructions.md`).
Consequences: non-secret knobs (`--data-root`, `--transport`, sync endpoint/bucket/region/object
key, access mode, scope, embedding model) are safe as CLI options; the four secret variables
(`AIRACCOON_OPENAI_API_KEY`, `AIRACCOON_SYNC_ACCESS_KEY`, `AIRACCOON_SYNC_SECRET_KEY`,
`AIRACCOON_DB_PASSPHRASE`) must never be definable as options — the parser should reject them as
unknown so a secret can never leak into a shared config or process listing by accident.

### F12 — Hand-rolled parsing is rejected by repo convention, so it is the baseline, not a candidate [READ]

The task brief states the repo invariant "official NuGet over hand-rolled"; the repo's own
instructions push every validation and parsing need to a maintained library (guard clauses via
`CommunityToolkit.Diagnostics`, "Keep NuGet versions centralized", "official NuGet over
hand-rolled" as the project convention). A hand-rolled `--key value` loop would re-implement
quoting, `=` handling, error messages, and help — exactly what the invariant exists to prevent.
It is listed only to be rejected.

**Evidence:** task brief ("the repo invariant 'official NuGet over hand-rolled' rejects
hand-rolled"); `.ai-badger/instructions/csharp.instructions.md` (centralized NuGet versions,
guard-clause library, xUnit/Shouldly conventions).

## Comparison matrix

```chart:matrix
title: CLI-args options vs. needs
criterion, System.CommandLine 2.0.x, Spectre.Console.Cli 0.55, Config providers (CreateBuilder), Cocona, McMaster, hand-rolled
--help, built-in, built-in, none, built-in, template, rejected
validation (enum/required), built-in, built-in, none, built-in, manual, rejected
unknown-option error, built-in, built-in, silent accept, built-in, built-in, rejected
aliases, built-in, built-in, switchMappings only, built-in, built-in, rejected
stdio-safe (all text → stderr), set Output (1 line), custom console (extra step), never prints, n/a (archived), n/a, rejected
dependency cost, 1 small pkg, 1 pkg (pre-1.0), zero (shared framework), dead, 1 pkg, zero
DI/testability, Parse() pure + custom console, TypeRegistrar + TestConsole, IConfiguration, n/a, n/a, n/a
```

## Recommendation

**Use System.CommandLine 2.0.10** (option b), invoked in parse-first mode with all CLI text
routed to stderr.

Why over (c) — the user's candidate: the config providers are already wired and cost zero, but
they are a key-value store, not a parser (F5). An MCP server's users interact with the CLI
exactly twice — installing it and debugging why a client won't start — and both times `--help`
and a loud unknown-option error are the whole UX. Silent typo acceptance on a daemon that then
runs with a wrong data root is a support ticket. The precedence F4 gives for free is preserved
anyway: the plan keeps the CLI > env > default merge as an explicit, tested rule in one class,
which is simpler to reason about than `IConfiguration` key-space gymnastics (the env keys are
`AIRACCOON_*` while CLI keys would be `--data-root`, so the two providers land in *different*
key spaces and need a mapping layer regardless).

Why over (a): Spectre.Console.Cli is excellent but pre-1.0 (F2), is a command-application
framework sized for nested command trees and DI containers — ~4× the API surface a flat
10-option daemon needs — and its stdout-default rendering (F7) adds the same stderr-redirect
obligation with more machinery around it. The repo invariant "ask if a simpler shape would do"
settles it: System.CommandLine's single `RootCommand` with nine `Option<T>`s is the simpler
adequate shape, and it is the official, GA, dotnet-CLI-proven parser (F1) — the best fit for the
"official NuGet" invariant.

Why not the others: Cocona is archived (F8); McMaster is third-party and thinner than the
official option (F8); hand-rolled is rejected by invariant (F12).

MCP-specific handling: parse first (`parser.Parse(args)`), inspect `result.Errors` and the help
request, and render help/errors exclusively to stderr (F6) — under stdio the only thing that
may touch stdout is the protocol. Secrets are simply not declared as options (F11), so the
parser's own unknown-option error is the defense against accidental secret exposure.

## Still open

- The exact 2.0.10 API idiom for intercepting `--help` output (checking
  `parseResult.Action is HelpAction` vs. a custom help action on the builder) was not pinned
  against the installed package; the implementation agent should pin it from the package's XML
  docs/tests before writing the help-routing test. The stream facts in F6 are source-verified
  and stable across the 2.0.x line.
- Whether concrete MCP registry clients (VS Code's registry UI, `npx @modelcontextprotocol/
  registry-client`) surface `packageArguments` for user editing before install is unverified;
  the plan only relies on the schema (F10) and on `packageArguments: []` remaining valid.
- Spectre.Console.Cli's exact custom-console constructor for 0.55 was not fetched (F7 note);
  irrelevant to the decision since it lost on shape, not on redirectability.
- The `Microsoft.Extensions.Configuration.CommandLine` "value = next token even if it starts
  with '-'" behavior (F5) was read from the current `main` source; if a future servicing
  release adds a switch guard, that specific pitfall listing may drift.
