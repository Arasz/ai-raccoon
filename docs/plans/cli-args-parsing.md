# Implementation Plan — Command-Line Argument Parsing for the ai-raccoon MCP Server

> **Based on:** `docs/work/2026-08-04-cli-args-exploration.md` (decision: System.CommandLine 2.0.10,
> parse-first, all CLI text → stderr).
> **Date:** 2026-08-04 · **Branch:** `task/add-command-line-args-parsing` · **PR scope:** one PR.
> **Workflow:** TDD mandatory — every production change below is preceded by a failing,
> behavior-focused xunit test (RED → GREEN → REFACTOR).

---

## 0. Goal

A user installs ai-raccoon from NuGet as a global dotnet tool (`dotnet tool install -g ai-raccoon`),
adds a two-line entry to their MCP client's `.mcp.json`, and the server works — with no env vars
set. Users who need non-default behavior pass flags (`--data-root`, `--transport`, …) either in
`.mcp.json` `args` or on the shell. Secrets stay environment-only, forever.

Precedence: **CLI args > env vars > built-in defaults** (the framework's own layering, per
exploration F4). Stdout carries only stdio MCP protocol frames; every byte of CLI output (help,
errors, version) goes to **stderr** (exploration F6 — System.CommandLine writes help to stdout by
default; we set the output writer to stderr).

---

## 1. Env-var inventory → CLI surface mapping

Full current surface (13 vars in code; the registry manifest
`src/AiRaccoon/.mcp/server.json` declares the 11 non-transport, non-passphrase ones —
no `MCP_TRANSPORT`, no `AIRACCOON_DB_PASSPHRASE`):

| # | Env var | Read at | Becomes CLI option | Secret? |
|---|---------|---------|--------------------|---------|
| 1 | `MCP_TRANSPORT` | `Setup/McpServerSetup.cs:15` | `--transport` | no |
| 2 | `AIRACCOON_DATA_ROOT` | `Infrastructure/Options/InfrastructureOptions.cs:29` | `--data-root` | no |
| 3 | `AIRACCOON_INSTALL_SCOPE` | `Setup/Dependencies.cs:24` | `--install-scope` | no |
| 4 | `AIRACCOON_ACCESS_MODE` | `Infrastructure/Sqlite/SqliteConnectionFactory.cs:66` | `--access-mode` | no |
| 5 | `AIRACCOON_EMBEDDING_MODEL` | `Infrastructure/Embedding/BundledModel.cs:38` | `--embedding-model` | no |
| 6 | `AIRACCOON_SYNC_ENDPOINT` | `Setup/Dependencies.cs:35` | `--sync-endpoint` | no |
| 7 | `AIRACCOON_SYNC_BUCKET` | `Setup/Dependencies.cs:36` | `--sync-bucket` | no |
| 8 | `AIRACCOON_SYNC_REGION` | `Setup/Dependencies.cs:39` | `--sync-region` | no |
| 9 | `AIRACCOON_SYNC_OBJECT_KEY` | `Setup/Dependencies.cs:40` | `--sync-object-key` | no |
| 10 | `AIRACCOON_SYNC_ACCESS_KEY` | `Setup/Dependencies.cs:37` | — **env-only** | **YES** |
| 11 | `AIRACCOON_SYNC_SECRET_KEY` | `Setup/Dependencies.cs:38` | — **env-only** | **YES** |
| 12 | `AIRACCOON_OPENAI_API_KEY` | `Infrastructure/Embedding/EmbeddingService.cs:88`, `Tools/MemoryTools.cs:414` | — **env-only** | **YES** |
| 13 | `AIRACCOON_DB_PASSPHRASE` | `Infrastructure/Sqlite/EnvEncryptionKeyProvider.cs:13` | — **env-only** | **YES** |

Rule: **9 CLI options, 4 env-only secrets.** The four secret vars are *not declared* as options,
so the parser's own unknown-option error is the defense: `ai-raccoon --sync-access-key x` must
fail with "Unrecognized command or argument '--sync-access-key'" — the option does not exist.
(Rationale: `.mcp.json` may be a tracked/shared file and `args` are visible in process listings;
repo invariants "No hardcoded secrets" + `mcp.instructions.md` "Read credentials … only from
configuration or environment variables".)

Env vars remain fully supported as the middle precedence layer — nothing that works today stops
working.

---

## 2. CLI surface spec

Single root command, no subcommands. All options kebab-case, long-form only (plus `-h`/`--help`
and `--version` from the framework). No credentials among them.

| Option | Type | Values | Default | Maps to |
|--------|------|--------|---------|---------|
| `--transport` | enum | `stdio`, `http`, `https` (https → unsupported warning, no endpoints — mirrors the env layer today) | `stdio` | `MCP_TRANSPORT` → `McpTransport` |
| `--data-root` | string (path, `~` expanded) | any | `~/.ai-raccoon` | `AIRACCOON_DATA_ROOT` |
| `--install-scope` | enum | `user`, `project` | `user` | `AIRACCOON_INSTALL_SCOPE` → `InstallScope` |
| `--access-mode` | enum | `ro`, `rw`, `full` | `rw` | `AIRACCOON_ACCESS_MODE` (seed) |
| `--embedding-model` | string (path, `~` expanded) | any | bundled model | `AIRACCOON_EMBEDDING_MODEL` |
| `--sync-endpoint` | string (URL) | any | unset (sync off) | `AIRACCOON_SYNC_ENDPOINT` |
| `--sync-bucket` | string | any | unset | `AIRACCOON_SYNC_BUCKET` |
| `--sync-region` | string | any | unset | `AIRACCOON_SYNC_REGION` |
| `--sync-object-key` | string | any | `memory-<projectId>.db` | `AIRACCOON_SYNC_OBJECT_KEY` |

Validation: enum options reject unknown values at parse time ("Cannot parse argument 'foo' for
option '--transport'"); unknown options and missing values are parse errors. `https` is a
declared enum member and therefore parses — it maps to the existing unsupported-transport
warning path (`SelectTransports`/`HandleHttpsTransport`), matching env-layer behavior today.
`--help` and `--version` are the framework built-ins.

Help text shape (rendered to stderr):

```
ai-raccoon 0.1.0-beta — MCP server exposing agent memory over sqlite-memory

USAGE:
  ai-raccoon [OPTIONS]

OPTIONS:
  --transport <stdio|http|https> MCP transport; https unsupported (default: stdio)
  --data-root <path>             Bank data root (default: ~/.ai-raccoon)
  --install-scope <user|project> Install scope (default: user)
  --access-mode <ro|rw|full>     Global access-mode seed (default: rw)
  --embedding-model <path>       Custom ONNX model path (default: bundled)
  --sync-endpoint <url>          S3-compatible endpoint URL (sync off when unset)
  --sync-bucket <name>           S3 bucket name
  --sync-region <name>           S3 region
  --sync-object-key <key>        S3 object key (default: memory-<projectId>.db)
  -h, --help                     Show help
  --version                      Show version

Secrets are read from environment variables only, never from the command line:
  AIRACCOON_OPENAI_API_KEY, AIRACCOON_SYNC_ACCESS_KEY,
  AIRACCOON_SYNC_SECRET_KEY, AIRACCOON_DB_PASSPHRASE
```

Exit codes: `0` on success and on `--help`/`--version`; `1` on any parse error (error text on
stderr, nothing on stdout).

---

## 3. "Plan defaults" — the zero-config `.mcp.json` entry

Scenario: fresh machine, `dotnet tool install -g ai-raccoon`, then add to the client's
`.mcp.json`. **Minimal zero-config form:**

```json
{
  "mcpServers": {
    "ai-raccoon": {
      "command": "ai-raccoon"
    }
  }
}
```

This works because the built-in defaults are: `--transport stdio`, `--data-root ~/.ai-raccoon`
(user profile dir), `--install-scope user`, `--access-mode rw`. No args, no env block required.

**Example with explicit args** (identical behavior, spelled out):

```json
{
  "mcpServers": {
    "ai-raccoon": {
      "command": "ai-raccoon",
      "args": [
        "--transport", "stdio",
        "--data-root", "~/.ai-raccoon",
        "--install-scope", "user",
        "--access-mode", "rw"
      ]
    }
  }
}
```

Secrets live in the client's **user-scoped** config only (e.g. Claude Code `~/.claude.json`
`env` block or the client's per-user settings) — never in a shared/tracked `.mcp.json`. Example
snippet for docs (values obviously fake):

```json
{
  "mcpServers": {
    "ai-raccoon": {
      "command": "ai-raccoon",
      "env": {
        "AIRACCOON_OPENAI_API_KEY": "sk-...",
        "AIRACCOON_DB_PASSPHRASE": "change-me"
      }
    }
  }
}
```

The registry manifest (`src/AiRaccoon/.mcp/server.json`) keeps `packageArguments: []` — the
zero-config contract — and keeps its `environmentVariables` list as the secret channel
(exploration F10: the schema separates the two by design).

---

## 4. Architecture & precedence mechanics

### 4.1 New files

**`src/AiRaccoon/Setup/CliArgs.cs`** — the only file that touches System.CommandLine.
- `internal sealed record CliOptions` — 9 nullable properties (unspecified = null). No defaults
  applied here; this layer answers "what did the user type".
- `internal sealed record CliParseResult(CliOptions? Options, bool ShowHelp, bool ShowVersion,
  IReadOnlyList<string> Errors)`.
- `internal static class CliArgs`:
  - `static RootCommand BuildRootCommand()` — declares the 9 `Option<T>`s (kebab-case names,
    descriptions from §2), `-h`/`--help` built in.
  - `static CliParseResult Parse(string[] args)` — pure: `new CommandLineBuilder(BuildRootCommand())
    .UseDefaults().Build().Parse(args)`; collects `result.Errors`; detects help/version.
  - `static int Render(CliParseResult result, TextWriter output)` — writes help or errors to the
    given writer (Program.cs passes `Console.Error`); returns the exit code (0 help, 1 errors).
  - **Stdout discipline:** nothing in this class ever writes to stdout. `Parse` writes nothing;
    `Render` takes its writer as a parameter. The parse-error path must not invoke the default
    help-to-stdout behavior (exploration F6) — help text is rendered by `Render` to the same
    stderr writer instead. The unit test in §6.3 locks this: stdout writer receives zero bytes.

**`src/AiRaccoon/Setup/ServerConfig.cs`** — the merge layer, framework-free and pure.
- `internal sealed record ServerConfig(McpTransport Transport, InfrastructureOptions Options)`.
- `static ServerConfig Build(CliOptions? cli, Func<string, string?> readEnv)` — applies
  **CLI > env > default** per key:
  - transport: `cli.Transport ?? parse(readEnv("MCP_TRANSPORT")) ?? Stdio`
  - data root: `cli.DataRoot ?? readEnv("AIRACCOON_DATA_ROOT") ?? ~/.ai-raccoon` (with `~`
    expansion for CLI/env paths). **Null or whitespace counts as unset** for the two path
    options (`--data-root`, `--embedding-model`) — preserving today's `IsNullOrWhiteSpace`
    gates (`InfrastructureOptions.cs:30`, `BundledModel.cs:39`): an empty env value or
    `--data-root ""` must fall back to the default root, never reach
    `Directory.CreateDirectory("")`. Access-mode and sync need no special casing
    (`AccessModePolicy.Parse` trims and nulls; `SyncOptions.IsConfigured` checks whitespace —
    verified).
  - scope: `cli.InstallScope ?? (env=="project" ? Project : User)`
  - access mode: `cli.AccessMode ?? readEnv("AIRACCOON_ACCESS_MODE")` (raw string, validated at
    seed time by `AccessModePolicy.Parse` exactly as today)
  - embedding model: `cli.EmbeddingModel ?? readEnv("AIRACCOON_EMBEDDING_MODEL")` — null or
    whitespace falls back to the bundled model (see data-root rule above)
  - sync: per-field `cli.X ?? readEnv("AIRACCOON_SYNC_X")`
  - `InfrastructureOptions.Rid` unchanged (RuntimeInformation default).
- Secrets are **not** in `ServerConfig` at all — the four env-only vars keep being read where
  they are read today (EmbeddingService fallback, EnvEncryptionKeyProvider), because env is the
  only source and there is nothing to merge.

### 4.2 Changes to existing files

**`src/AiRaccoon/Program.cs`** — becomes the composition root:

```csharp
var parsed = CliArgs.Parse(args);
if (parsed.Errors.Count > 0 || parsed.ShowHelp || parsed.ShowVersion)
{
    return CliArgs.Render(parsed, Console.Error);
}

var config = ServerConfig.Build(parsed.Options, Environment.GetEnvironmentVariable);
var builder = WebApplication.CreateBuilder([]); // args already consumed by CliArgs

builder
    .ConfigureMcpServer(config.Transport)
    .Services.RegisterMemoryServices(config.Options);

var app = builder.Build().ConfigureMcpEndpoints(config.Transport);
await app.RunAsync();
```

(`CreateBuilder([])` — empty args — prevents the built-in CommandLine config provider (F5) from
re-parsing consumed flags into `builder.Configuration`; the merged values flow via `ServerConfig`
instead.)

**`src/AiRaccoon/Setup/McpServerSetup.cs`** — remove the static
`ConfiguredTransport`/`Environment.GetEnvironmentVariable("MCP_TRANSPORT")` (line 15) and the
`Lazy`; the extension methods take the transport as a parameter:
`ConfigureMcpServer(this WebApplicationBuilder, McpTransport)` and
`ConfigureMcpEndpoints(this WebApplication, McpTransport)`. `SelectTransports(string?)` stays a
pure function (now the env→transport coercion used by `ServerConfig.Build`); the existing
`McpServerSetupTests.SelectTransports_ResolvesEnvironmentValue` theory stays green unchanged.

**`src/AiRaccoon/Setup/Dependencies.cs`** — `RegisterMemoryServices(this IServiceCollection,
InfrastructureOptions options)`; delete the seven direct `Environment.GetEnvironmentVariable`
reads (lines 23-42: scope + six sync fields). `options.Scope` and `options.Sync` are pre-merged
by `ServerConfig.Build`. Everything else (registrations) unchanged.

**`src/AiRaccoon.Infrastructure/Options/InfrastructureOptions.cs`** — add
`string? AccessMode { get; init; }` and `string? EmbeddingModelPath { get; init; }` (the merged
seed values; null = unset). **Committed decision (review R3):** `ServerConfig.Build` supplies
the resolved data root; `DefaultDataRoot()` becomes the pure
`Path.Combine(UserProfile, ".ai-raccoon")` fallback and **its env read is deleted** — this is
what makes the "remove ALL direct env reads for the 9 non-secret vars" claim verifiable. The
§6.2 precedence tests are the arbiter.

**`src/AiRaccoon.Infrastructure/Sqlite/SqliteConnectionFactory.cs:66`** —
`SeedGlobalAccessModeAsync` reads `options.AccessMode` (via the ctor-injected
`InfrastructureOptions`) instead of `Environment.GetEnvironmentVariable("AIRACCOON_ACCESS_MODE")`;
behavior identical when null (no seed).

**`src/AiRaccoon.Infrastructure/Embedding/BundledModel.cs`** and **`EmbeddingService.cs`** —
`BundledModel.ResolveModelPath(string? configuredPath)` overload; `EmbeddingService.CreateLocal`
passes `options.EmbeddingModelPath` (injected) when `settings.Model` is empty. **Committed
decision (review R4):** the env read is **deleted** from `BundledModel`; the no-arg
`ResolveModelPath()` becomes `ResolveModelPath(null)` (bundled-only). Tests call the no-arg
overload (`EmbeddingFeatureTests.cs:77`, `EmbeddingServiceTests.cs:74`,
`BundledModelGateTests.cs:25`) but none set `AIRACCOON_EMBEDDING_MODEL` (verified), so behavior
is unchanged. The merged path wins.

**`EnvEncryptionKeyProvider.cs`, `EmbeddingService.cs:88` (api-key fallback)** — unchanged.
Env-only by definition.

### 4.3 E2E `McpServerFactory` — keeps working, no migration

The factory mutates `MCP_TRANSPORT`/`AIRACCOON_DATA_ROOT`/`AIRACCOON_ACCESS_MODE` before host
build. Because env remains the honored middle precedence layer, the same values reach
`ServerConfig.Build` and all E2E tests pass unchanged — that is the point of the 3-layer design.
No migration needed; the serial `E2ETestCollection` stays as-is. Add one assertion-level check
only: a new E2E test class (or extension of an existing one) that sets a CLI-relevant env var
(`AIRACCOON_INSTALL_SCOPE=project`) and asserts the server honors it, proving the env path
end-to-end after the refactor. **Env lifecycle (review R6):** (a) set the var **before** the
first `CreateClientAsync` on the factory instance (host build is lazy); (b) restore it in
`finally` — or extend `McpServerFactory`'s save/restore set (currently only
`MCP_TRANSPORT`/`AIRACCOON_DATA_ROOT`/`AIRACCOON_ACCESS_MODE`, McpServerFactory.cs:26-33, 70-72)
— or it leaks into the serial E2E collection. Concrete assertion: with project scope the bank
lives at `<dataRoot>/.ai-raccoon/memory.db` (`SqliteConnectionFactory.cs:23-30`). (CLI-args
end-to-end is proven by the manual smoke gate §8, since `WebApplicationFactory<Program>` cannot
inject `args` into the entry point.)

---

## 5. stdout discipline (the stdio constraint)

1. `CliArgs.Render` writes only to the writer passed in — Program.cs passes `Console.Error`.
2. `CliParseResult` is a value: parse errors never print themselves; the default
   parse-error→stdout help behavior of System.CommandLine is never invoked (exploration F6).
3. `WebApplication.CreateBuilder([])` — empty args — guarantees no second parser writes anything.
4. Existing `mcp.instructions.md` logging rule unchanged: `HandleStdioTransport` already routes
   console logging to stderr (`LogToStandardErrorThreshold`).
5. Guard test (§6.3) asserts stdout stays empty for `--help`, unknown option, missing value,
   enum-invalid value — the protocol-corruption regression test.

---

## 6. Test plan (TDD — failing test first per behavior)

All new tests under `tests/AiRaccoon.Tests/Unit/Setup/` unless noted. Use `Shouldly` + xunit.v3
traits `[Trait(TestCategories.Category, TestCategories.Unit)]` / `Fast` per existing convention.

### 6.1 `CliArgsTests.cs` — parser behavior (pure, no IO)
- `Parse_ParsesDataRootOption` — `["--data-root", "/x"]` → `Options.DataRoot == "/x"`.
- `Parse_ParsesEqualsSyntax` — `["--access-mode=ro"]` → `Options.AccessMode == "ro"`.
- `Parse_UnknownOption_ReturnsError` — `["--bogus"]` → 1 error mentioning `--bogus`; no throw.
- `Parse_UnknownSecretOption_ReturnsError` — `["--sync-access-key", "x"]`, `["--db-passphrase",
  "x"]` → error; **proves secrets are not exposed as flags** (see §1).
- `Parse_MissingValue_ReturnsError` — `["--data-root"]` (trailing) → error.
- `Parse_InvalidEnumValue_ReturnsError` — `["--transport", "ftp"]` → error.
- `Parse_Help_ReturnsShowHelp` — `["--help"]` → `ShowHelp`, no errors.
- `Parse_TransportHttps_ParsesToHttps` — `["--transport", "https"]` → `Options.Transport ==
  "https"` (declared enum member; the unsupported warning is emitted downstream, review R2).
- `Parse_HelpAndBogus_BehaviorPinned` — `["--help", "--bogus"]`: pin whichever System.CommandLine
  2.0.x does (help wins vs. error wins) during WP2 and assert it (review O4).
- `Parse_NoArgs_ReturnsNullOptions` — `[]` → `Options == null`, no errors, no help.
- `Parse_LastDuplicateWins` — `["--data-root", "/a", "--data-root", "/b"]` → `/b`.

### 6.2 `ServerConfigTests.cs` — precedence merge (pure; inject a `Dictionary`-backed `readEnv`)
- `Build_CliOverridesEnv` — cli `/cli`, env `/env` → `/cli` (for data root and transport).
- `Build_EnvUsedWhenCliAbsent` — cli null, env `/env` → `/env`.
- `Build_DefaultWhenBothAbsent` — data root → `~/.ai-raccoon`; transport → `Stdio`; scope →
  `User`; access mode → null.
- `Build_TransportCoercesEnvironmentValue` — env `MCP_TRANSPORT=http` → `Http`; invalid → `Stdio`
  (mirrors `SelectTransports` contract).
- `Build_TransportPreservesHttpsEnvValue` — env `MCP_TRANSPORT=https` → `Https` (live value
  today, review R2).
- `Build_WhitespaceEnvFallsBackToDefault` — env `AIRACCOON_DATA_ROOT=" "` →
  `~/.ai-raccoon`; env `AIRACCOON_EMBEDDING_MODEL=" "` → null (review R1).
- `Build_WhitespaceCliValueTreatedAsUnset` — `--data-root ""` → default root; `--embedding-model
  ""` → bundled (review R1).
- `Build_ExpandsTildeInPaths` — `--data-root ~/x` → full path.
- `Build_SyncFieldsMergeIndependently` — endpoint from cli, bucket from env, region unset.
- `Build_SecretVarsNeverMerged` — the four secret env vars are not read by `Build` (assert via a
  `readEnv` spy that records keys: `AIRACCOON_SYNC_ACCESS_KEY` etc. absent from the recorded set).
- `Build_AccessModeAndEmbeddingModelPassThrough` — merged seed values land on
  `InfrastructureOptions.AccessMode` / `EmbeddingModelPath`.

### 6.3 `CliOutputRoutingTests.cs` — the stdout-corruption guard
- `Render_Help_WritesOnlyToErrorWriter` — `Parse(["--help"])` + `Render(parsed, errorWriter)`;
  assert `stdoutWriter.ToString() == ""` and errorWriter contains "USAGE".
- `Render_ParseError_WritesOnlyToErrorWriter` — same for `["--bogus"]`; exit code 1.
- `Render_Version_WritesOnlyToErrorWriter`.
- `Render_Help_ReturnsZeroExitCode`.
- `Render_Version_ReturnsZeroExitCode` (review O3).

### 6.4 Adjusted / unchanged existing tests
- `tests/AiRaccoon.Tests/Unit/Mcp/McpServerSetupTests.cs` — **unchanged** (`SelectTransports`
  stays pure; compile-only impact if the extension signatures change: no test calls
  `ConfigureMcpServer` directly — verified).
- `tests/AiRaccoon.Tests/E2E/McpServerFactory.cs` + all E2E — **unchanged** (§4.3). Add the one
  env-honored E2E assertion test (§4.3) after the refactor is green.
- `TestData.CreateInfrastructureOptions(dataRoot, rid)` — unchanged; add overload taking
  `(dataRoot, rid, accessMode, embeddingModelPath)` if §6.2 tests need it (optional).

---

## 7. Work packages (execution order; commit per package, draft PR from WP1)

**WP1 — Dependency + tool metadata.** Add `System.CommandLine 2.0.10` to
`Directory.Packages.props` (`<PackageVersion Include="System.CommandLine" Version="2.0.10" />`)
and a version-less `<PackageReference Include="System.CommandLine"/>` to
`src/AiRaccoon/AiRaccoon.csproj` only — never Core/Infrastructure (clean-layering invariant).
In the same csproj: `<ToolCommandName>ai-raccoon</ToolCommandName>` (already applied — review
R5c; the packed command must be lowercase so `.mcp.json` `"command": "ai-raccoon"` resolves on
case-sensitive filesystems) and `<InformationalVersion>0.1.0-beta</InformationalVersion>` so
`--version` and MCP `serverInfo.version` report the real version, not the assembly default
`1.0.0.0` (review R7). Fix the misleading DeployToLocalSource comment ("dotnet_env (lowercase)")
to say `DOTNET_ENV` (review R5a). Gate: `dotnet build` green.

**WP2 — Parser.** RED: write `CliArgsTests.cs` (§6.1) against the not-yet-existing `CliArgs` —
all fail to compile (the failing-test-first state). GREEN: add `src/AiRaccoon/Setup/CliArgs.cs`
per §4.1. Pin the exact 2.0.10 help-detection idiom from the package's XML docs during this WP.
Gate: §6.1 all green; `dotnet build` green.

**WP3 — Merge.** RED: `ServerConfigTests.cs` (§6.2). GREEN: `src/AiRaccoon/Setup/ServerConfig.cs`
+ `InfrastructureOptions.AccessMode`/`EmbeddingModelPath` additions. Gate: §6.2 green.

**WP4 — Program + stdout routing.** RED: `CliOutputRoutingTests.cs` (§6.3) — help/error routing
via `CliArgs.Render`. GREEN: `Render` in `CliArgs` + `Program.cs` composition (§4.2) — including
`CreateBuilder([])`. Gate: §6.3 green; full `dotnet test` green (E2E must pass unchanged — env
layer still honored).

**WP5 — De-env the consumers.** RED (in order, one failing test each before its production
change): (a) `Dependencies.RegisterMemoryServices(services, options)` — compile break is the RED
for the signature change; (b) `SqliteConnectionFactory` access-mode seed reads
`options.AccessMode` (unit test: open a bank with `InfrastructureOptions{ AccessMode = "ro" }`
in a temp root, assert the settings row); (c) `BundledModel.ResolveModelPath(configured)` +
`EmbeddingService` uses `EmbeddingModelPath` (unit test: configured path wins over env).
GREEN: per-change edits per §4.2. Gate: full `dotnet test` (428 existing + new) green.

**WP6 — E2E env-honored assertion.** Add the §4.3 E2E test. Gate: E2E green.

**WP7 — Docs + manifest.**
- Root `README.md` — env-var table (lines 58-67) and `MCP_TRANSPORT` description (lines 47-51)
  must gain the CLI-options section / precedence sentence too (review R8).
- `src/AiRaccoon/README.md` — new "Command-line options" section (table from §2, help text,
  minimal + explicit `.mcp.json` entries from §3, secret-env-only note, precedence rule).
- `docs/reference/agent-memory-server.md` — same additions beside the env-var table (lines
  ~129-143), precedence sentence.
- `src/AiRaccoon/.mcp/server.json` — keep `packageArguments: []`; add a comment-free note in the
  README that registry installs pass no args (zero-config contract). No JSON change required.

**WP8 — Manual smoke gate (§8) + PR.** One PR on branch `task/add-command-line-args-parsing`.

---

## 8. Acceptance criteria & quality gates

What must be true, and the exact commands that prove it:

1. **Build:** `dotnet build` (repo root; solution is `AiRaccoon.slnx` — never `dotnet build` with
   a `.sln` name) → 0 errors, 0 warnings.
2. **Full suite:** `dotnet test` (repo root) → all previously-passing tests (428 at baseline)
   plus the new §6 tests green; 0 failures.
3. **New behavior covered:** §6.1–6.3 tests exist and pass — precedence, unknown options,
   missing values, invalid enums, secrets-not-exposed, stdout-empty-on-help/errors.
4. **Global-tool smoke test** (manual, after WP8):
   ```bash
   # from repo root; host-RID pack into the local feed (mirrors the DeployToLocalSource target).
   # NOTE: the target's condition is '$(DOTNET_ENV)' == 'local' and MSBuild env lookup is
   # case-sensitive on macOS — use DOTNET_ENV, not dotnet_env (review R5a).
   export DOTNET_ENV=local
   dotnet build src/AiRaccoon            # triggers pack+push to .nupkg-local/ for the host RID
   dotnet tool uninstall -g ai-raccoon 2>/dev/null; true
   dotnet tool install -g ai-raccoon --add-source "$(pwd)/.nupkg-local" --version 0.1.0-beta
   ai-raccoon --help 2>/dev/null | wc -c   # → 0: help on STDERR, exit 0, NOTHING on stdout
   ai-raccoon --bogus 2>/dev/null | wc -c  # → 0: "Unrecognized command or argument '--bogus'" on STDERR, exit 1
   ai-raccoon --sync-access-key x   # → unknown-option error (secret not exposed), exit 1
   # stdio initialize handshake: stdout must carry exactly one JSON-RPC frame.
   # macOS has no `timeout` (no coreutils); also, immediate stdin-EOF races the response write —
   # the frame is lost (empirically verified: zero bytes on stdout). Hold stdin open ~3s
   # (review R5b), then EOF exits cleanly:
   { printf '%s\n' '{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"smoke","version":"0"}}}'; sleep 3; } \
     | ai-raccoon 2>/tmp/ai-raccoon-smoke.err | head -c 400
   # → JSON-RPC initialize result frame on stdout (stderr may carry startup logs)
   ```
   Accept: the initialize response parses as JSON; the `wc -c` checks above yield 0 on stdout.
   Fallback if DeployToLocalSource misbehaves on first run (review O5):
   `dotnet pack src/AiRaccoon -p:RuntimeIdentifiers=osx-arm64 -o .nupkg-local` then
   `dotnet nuget push <nupkg> --source .nupkg-local` for both the shell and RID packages.
5. **Docs updated:** README + agent-memory-server.md reflect §2/§3; `grep -c "AIRACCOON"` tables
   remain accurate vs §1.
6. **PR:** one PR, gates green, no direct push to main.

---

## 9. Out of scope

- Subcommands, nested command trees, positional args (single flat root command only).
- Short aliases beyond `-h` (no `-t`, `-d`, …); Windows `/key` syntax (System.CommandLine's
  POSIX+Windows conventions already handle `--key=value`).
- `appsettings.json` / config-file support; `IConfiguration` plumbing (args consumed before
  `CreateBuilder`).
- Shell completions, response files, `dotnet-suggest` integration.
- New env vars, renamed env vars, or deprecating the existing 13 (backward compatible by design).
- Credentials as options — permanently (the four secrets are rejected at parse time).
- `--urls`/Kestrel flag pass-through (HTTP transport config stays env/launch-profile driven;
  `CreateBuilder([])` intentionally drops generic host flags).
- Version bump (stays 0.1.0-beta), release notes, changelog.
- AOT/trimming work, `.mcp/server.json` schema changes, registry-client UI work.
- Moving `SelectTransports`/`AccessModePolicy.Parse` — existing pure functions are reused, not
  rewritten.

---

## 10. Risks & open items

- **System.CommandLine 2.0.10 API idiom for help interception** — pinned (review R7):
  `parseResult.Action is HelpAction` (and `VersionOptionAction` for `--version`) is the 2.0.x GA
  idiom; the custom-help-action fallback is contingency only. Stream facts are source-verified
  (exploration F6). WP2 pins it from the installed package's XML docs.
- **`--version`/`serverInfo.version` report `1.0.0.0`, not `0.1.0-beta`** — verified against the
  installed tool; fixed by `<InformationalVersion>0.1.0-beta</InformationalVersion>` in WP1
  (review R7).
- **stdio stdin-EOF race** — immediate stdin EOF can exit the server before the initialize
  response flushes (empirically verified); the §8 smoke gate holds stdin open ~3s (review R5b).
- **`WebApplicationFactory` cannot pass args** — CLI-over-env is proven at unit level (§6.2) and
  by the manual smoke gate (§8.4); E2E keeps the env path. Accepted trade-off, documented.
- **`~` expansion** — only applied to the two path options (`--data-root`, `--embedding-model`)
  in `ServerConfig.Build`; documented in help text.
- **Central version pinning** — `Directory.Packages.props` gains exactly one line (WP1);
  `CentralPackageTransitivePinningEnabled=false` is already set, so System.CommandLine's
  netstandard2.0/System.Memory edge does not ripple.
