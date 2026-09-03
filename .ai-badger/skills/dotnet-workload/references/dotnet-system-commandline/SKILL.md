---
name: dotnet-system-commandline
description: "Use when adding CLI argument parsing to a .NET app or dotnet tool: System.CommandLine 2.0.x GA idioms (parse-first, HelpAction/VersionOptionAction detection, Option.Validators, FromAmong), parser-landscape verdicts (Cocona archived — don't adopt), and the stdio-MCP trap where help/version must render to stderr. Includes Cocona-maintenance guidance for existing tools."
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [dotnet, cli, system-commandline, parsing]
    related_skills: [dotnet-tool-publishing, dotnet-mcp-server]
---

# .NET CLI argument parsing

Choosing and implementing CLI args for .NET console apps and global dotnet tools
(PackAsTool). State verified 2026-08-04 against primary sources; full worked example:
the repo's CLI-args design doc (`docs/plans/cli-args-parsing.md`).

## Parser landscape (as of 2026-08)

| Option | Status | Verdict |
|---|---|---|
| System.CommandLine 2.0.x | GA Nov 2025, 2.0.10 current, MIT, net8.0 asset (zero deps), used by dotnet CLI, trim-friendly | **default choice** for flat option sets |
| Spectre.Console.Cli | 0.55.0 stable, still 1.0.0-alpha; very active | good, but a command framework (DI, nested commands) — overkill for ~10 flat options |
| Microsoft.Extensions.Configuration.CommandLine + EnvironmentVariables | ship in the ASP.NET Core shared framework (zero new packages); `CreateBuilder(args)` already layers CLI > env > appsettings | key-value ONLY — not a parser: unknown `--keys` silently stored, no --help/validation, trailing valueless `--key` dropped, `--key --other` consumes next token as value |
| Cocona | archived 2024 | do not adopt |
| McMaster.Extensions.CommandLineUtils | maintained, minimal | third-party, thinner than the official parser |
| hand-rolled | — | rejected wherever 'official NuGet over hand-rolled' applies |

## Maintaining an existing Cocona tool (verified 2026-08-05, the reference tool)

Cocona is archived — don't adopt it for new work — but tools already built on it
need these behaviors (Cocona 2.2.0, observed on a real CLI):

- Exceptions escaping a command handler do NOT propagate to your code: Cocona's
  internal `HandleExceptionAndExitMiddleware` catches them, prints the FULL
  exception (`ex.ToString()`, stack trace included) to stderr, and returns exit
  code 1. A `try/catch` around `consoleApp.Run()` in Program.cs never fires — it
  is dead code.
- Setting `Environment.ExitCode` inside a handler is clobbered: the dispatcher
  writes the exit code from the handler result (0 on success).
- Working pattern for clean errors + exit codes: make the handler lambdas return
  `Task<int>` — Cocona uses an int-returning handler's value as the process exit
  code. Wrap each command: `static async Task<int> RunAndReportErrors(Func<Task> action)`
  `{ try { await action(); return 0; } catch (Exception ex) { Console.Error.WriteLine($"Error: {ex.Message}"); return 1; } }`.
- `CoconaAppOptions` (2.2.0) has NO exception-handler hook (verified by
  reflection) — no built-in message-only error mode; the wrapper above is the way.

## The protocol-stream trap (stdio MCP / JSON-RPC daemons)

- System.CommandLine renders **help to stdout by default** (`InvocationConfiguration.Output`
  → `Console.Out`); only parse **errors** default to stderr (`InvocationConfiguration.Error`
  → `Console.Error`). Both are settable from the parse result.
- If stdout carries a protocol (stdio MCP), ALL CLI text — help, version, errors — must go
  to stderr. Do parse-first: `root.Parse(args, new ParserConfiguration { EnablePosixBundling = true })`,
  inspect `result.Errors` and `result.Action`, render through a writer you pass in
  (Console.Error) via `result.Invoke(new InvocationConfiguration { Output = writer, Error = writer })`.
  Never invoke the default invoke path that prints help to stdout: a client spawning the
  tool with bad args would get help frames mixed into the protocol stream.
- Lock it with a test: help/version/parse-error renderings write ZERO bytes to the stdout
  writer.

## System.CommandLine 2.0.10 GA — pinned idioms (verified 2026-08-04)

Full verified detail (ctor signatures, exact error messages, probe transcripts):
Read `references/system-commandline-2.0.10-ga-api.md` when pinning 2.0.10 GA API facts; instance-based reads + custom
value-grammar validation via `Option.Validators` (verified 2026-08-06):
Read `references/system-commandline-2.0.10-option-validation.md` when validation ordering matters. The short version:

- **GA has NO `CommandLineBuilder`, no `Builder` namespace, no `UseDefaults()`** — every
  pre-GA post and older skill text showing `new CommandLineBuilder(root).UseDefaults().Build()`
  is the beta API. Parse directly: `root.Parse(args, new ParserConfiguration { EnablePosixBundling = true })`.
- Help: `parseResult.Action is HelpAction` (`System.CommandLine.Help`). Version:
  `parseResult.Action?.GetType().Name == "VersionOptionAction"` — `VersionOption.VersionOptionAction`
  is an inaccessible nested type, so the `is` pattern does not compile (CS0122); it is also
  absent from the package XML docs.
- Errors: `parseResult.Errors` (`ParseError.Message`); `OptionResult.GetValueOrDefault<T>()`
  THROWS on invalid enum values → read values only when `Errors.Count == 0`.
- Duplicate single-value options are a PARSE ERROR ("Option '--x' expects a single argument
  but 2 were provided"), NOT last-wins (beta behavior; no switch restores last-wins).
  `--help --bogus` → help wins, 0 errors.
- Render: `parseResult.Invoke(new InvocationConfiguration { Output = writer, Error = writer })`
  → exit 0 for help/version, 1 for parse errors (error text + help both to the writers).
- Values: `((OptionResult)parseResult.GetResult("--name")!).GetValueOrDefault<T>()` —
  `GetResult(string)` returns `SymbolResult` (needs the cast); use presence (`GetResult` not
  null) to distinguish "option not given" from the default value. Enum `ToString()` is
  PascalCase ("Http") — lower it if your contract says "https".
- **Instance-based reads are the duplicate-alias fix (verified 2026-08-06):** GA has
  `ParseResult.GetValue<T>(Option<T>)` — `GetValueForOption` is the PRE-GA name and does
  NOT compile against 2.0.10 (CS1061). Root and a subcommand may both declare the same
  alias (`--port` on root AND on `serve`; parses fine, help renders both), but name-based
  `GetResult("--port")` resolves the ROOT option (DFS order) when both are given — the
  subcommand's value is unreachable by name. Expose the subcommand's `Option` instances as
  `internal static readonly` fields and read `parseResult.GetValue(optionInstance)`
  (defaults via `DefaultValueFactory = _ => value` are honored: absent → default).
- **Custom value grammar: `Option.Validators` is the ONLY native parse-error hook in GA
  (no `ParseArgument`).** The validator delegate receives the `OptionResult`; read the raw
  token with `result.GetValueOrDefault<string>()` (null when absent) and report failure via
  `result.AddError("...")` — there is NO `ErrorMessage` property in GA (CS1061). Pattern:
  `Option<string>` + a pure static `TryParse` + validator, e.g. SCL's built-in TimeSpan
  converter exists but lacks `4h` sugar, so `--idle-timeout` stays a string option parsed
  by a custom span parser. Pin the invalid-value case with a parse test (`Errors` not empty).
- **Pure span parsers must be total:** `TimeSpan.FromDays(int)` throws
  `ArgumentOutOfRangeException` (NOT `OverflowException`) on overflow — a `TryParse` that
  wraps `From*` must catch it (or bounds-check) or `"999999999d"` throws instead of
  returning false. Add an overflow row to the invalid-input theory.
- `Option<T>` ctor is `(string name, string[] aliases)` — no description parameter (set via
  property); help placeholder via `HelpName` (gives `--transport <stdio|http|https>`);
  hide via `Symbol.Hidden`; `RootCommand.ExecutableName` is a read-only STATIC property.
- GA help format is "Description:" / "Usage:" / "Options:" (NOT "USAGE:"), built-in help
  aliases `-?, -h, --help`; `--version` prints the ENTRY assembly's informational version —
  in a test host that is the test runner, so assert routing only and prove the real version
  via the packed-tool smoke gate.

### Re-verifying the pins when the package version changes

The pins above are for 2.0.10. When the package bumps, re-pin EMPIRICALLY instead of
trusting this doc: build a throwaway console project (restore hits the NuGet cache, so it's
fast) that parses a battery of arg sets and prints `parseResult.Action?.GetType().FullName`,
error count, showHelp/showVersion, and the `Invoke` exit code. Starter:
`templates/system-commandline-idiom-probe/` (copy into /tmp — never into the repo under
review). Probe pitfalls that cost real time:

- Fastest existence check, no decompiler: grep the shipped package XML docs —
  `grep -o 'member name="[MTPS]:System.CommandLine.Parsing.ParseResult.[^"]*"' ~/.nuget/packages/system.commandline/<ver>/lib/net8.0/System.CommandLine.xml`
  (swap the type for any API surface; `grep -A8 'M:System.CommandLine.Parsing.SymbolResult.AddError'`
  for the doc text). Member existence — and the exact GA name — resolved in seconds.
- `Assembly.GetType("System.CommandLine.VersionOptionAction")` returns **null** — the type
  is NESTED (`System.CommandLine.VersionOption+VersionOptionAction`, note the `+`). The
  instance's `GetType().Name` is still just "VersionOptionAction", which is why the name
  pin works.
- `someType is HelpAction` where `someType` is a `Type` object is ALWAYS false (CS0184
  warning) — test the ACTION INSTANCE (`parseResult.Action is HelpAction`), never
  `parseResult.Action?.GetType() is ...`.
- A bare SDK-style csproj has no implicit usings — `Console`/`TextWriter` fail to resolve
  until you add `<ImplicitUsings>enable</ImplicitUsings>`.
- After the bump, re-run the E2E suite too: `--help --bogus` (help-wins), duplicate-option
  (error, not last-wins), and the WebApplicationFactory hidden-flag triplet are the three
  behaviors most likely to shift between versions.

### WebApplicationFactory trap (parse-first entry points)

`WebApplicationFactory<T>` invokes the REAL entry point with
`--environment=Development --contentRoot=<projdir> --applicationName=<AssemblyName>`.
Parse-first code rejects these as unknown options → early return → "The entry point exited
without ever building an IHost." → every E2E test fails (the old `CreateBuilder(args)` path
silently stored them as config keys, which is why this only bites after switching to
parse-first + `CreateBuilder([])`). Fix: declare the three as HIDDEN no-op options
(`new Option<string>("--environment") { Hidden = true }`, likewise `--contentRoot`,
`--applicationName`), values intentionally never consumed, and pin with a test that the
triplet parses with zero errors and yields no options.

**UseSetting IS an arg-injection path (verified 2026-08-04 with a throwaway probe).**
`builder.UseSetting("key", "value")` inside `ConfigureWebHost` is rendered as a
`--key=value` argument to the REAL entry point's Program.Main (WebApplicationFactory's
`DeferredHostBuilder` flattens its host configuration into args; captured verbatim:
`--transport=http --environment=Development --dataRoot=/tmp/xxx --contentRoot=... --applicationName=...`).
This is the escape hatch when a refactor removes env vars: E2E factories can still
inject CLI-only launch flags (`UseSetting("data-root", tempRoot)` → `--data-root=<temp>`).
The earlier claim "WebApplicationFactory can't inject args anyway" is WRONG — the
probe recipe (a throwaway web `Probe` project + `Probe.Tests` capturing `Program.Main`'s
args into a static, `dotnet test` once) is how to re-verify when the SDK version changes.

### Optional subcommands: a root with children REQUIRES a verb (verified 2026-08-04)

A `RootCommand` (or any `Command`) that has subcommands cannot be invoked bare: parsing
`--flag value` with no verb yields the parse error "Required command was not provided."
— there is NO switch making subcommands optional in 2.0.10 GA. The
"bare invocation runs the daemon, verbs run commands" pattern: build TWO roots over the
same launch options — the full tree (flags + verbs) and a launch-only root (flags only).
Parse the full tree first (help/version render from it, so `--help` lists the verbs);
when it has errors AND a token pre-scan (skip `--opt` + its value / `--opt=value`,
then look for known verb tokens) finds no verb, re-parse against the launch-only root
(genuine option errors re-surface there). Pin with tests: bare flags parse with zero
errors; a verb without its subcommand still errors.

## Precedence and wiring

- **CLI > env > default** is the ASP.NET Core default layering (AspNetCore.Docs: command
  line, env vars, user secrets (dev), appsettings.{ENV}.json, appsettings.json).
- If you parse args yourself, pass `[]` to `WebApplication.CreateBuilder([])` so the
  built-in CommandLine config provider doesn't re-parse consumed flags into
  `IConfiguration` (and doesn't silently accept typos as config keys).
- Implement the merge as one pure class: `cliValue ?? envValue ?? default`, keeping env a
  first-class middle layer. When a single-channel ruling removes env entirely, keep the
  same pure-class shape minus `readEnv`. Bonus: env-mutating E2E factories
  (WebApplicationFactory) keep working without migration — and when env is gone,
  `builder.UseSetting(...)` in `ConfigureWebHost` still injects launch flags as
  `--key=value` entry args (see the WebApplicationFactory trap section above).
- `~` expansion: only path options, done in the merge layer.


> Config-verb trees: read `references/config-verb-trees.md` when building one-shot config-verb commands over the bank.

## Secrets policy

- Never declare secrets as CLI options: args are visible in process listings and client
  config files (`.mcp.json` may be tracked/shared). Undeclared options fail with the
  parser's unknown-option error — that error IS the defense.
- Test it: parser rejects `--<secret-flag>` as unknown.
- Owner rulings can override this (2026-08-04: secrets moved into the settings
  table — DB encrypted at rest). FINAL state (2026-08-05): `model set openai --api-key`
  remains a documented option on that one command; `sync add s3` credentials are
  INTERACTIVE — the `--access-key/--secret-key` options were deleted. Interactive-entry
  pattern that worked: `RunAsync(commandPath, parseResult, store, stdout, stderr, stdin,
  ct)` gains a `TextReader stdin` (Program.cs passes `Console.In`; tests pass
  `StringReader`/`TextReader.Null` — every call site ripples: BDD steps, E2E host, unit
  helpers). The command prompts on STDERR (`S3 access key (empty aborts): `), reads one
  line, trims, and on empty input writes `<tool>: <name> key required — …` to stderr,
  returns exit 1, and persists NOTHING (prompt before any settings write). Pin with tests:
  happy path via StringReader; empty stdin → exit 1 + zero settings rows. Prefer
  interactive entry over secret flags when a ruling demands secrets-in-settings; a kept
  flag goes on the specific config command only (never the root), stays out of tracked
  example files, and the argv-visibility tradeoff is flagged in the report.

## Client config conventions (MCP global tools)

- `.mcp.json`: `{"mcpServers":{"<name>":{"command":"<tool-name>","args":[...],"env":{...}}}}` —
  dotnet tools appear as bare command names (e.g. `"command": "the project"`), unlike
  npx/dotnet-run forms.
- Zero-config entry works when defaults are: stdio transport, user-scoped data dir,
  rw access mode.
- MCP registry manifest (`server.json`, schema 2025-10-17): `packageArguments` (named
  `--flag={value}` or positional, `{var}` substitution) is a separate channel from
  `environmentVariables` — keep args static/`[]` and secrets in environmentVariables.

## Research technique: primary-source facts via curl

When web_extract is unavailable (search-only backend), gather package/repo facts directly —
versions via the NuGet flatcontainer API, metadata via the catalog, repo status via the
GitHub API, behavior from raw source. Exact recipe + verified 2026-08 facts with citations:
Read `references/nuget-evidence-research.md` when researching NuGet package facts.

## Gotchas

- In stdio-MCP tools, help/version must render to stderr — stdout is the protocol channel.
- A root with children REQUIRES a verb — an optional-subcommand root silently rejects bare invocations.
## References

- `references/system-commandline-2.0.10-ga-api.md` — System.CommandLine 2.0.10 GA API surface notes; read when the 2.0.10 API surface is in question.
- `references/system-commandline-2.0.10-option-validation.md` — Option.Validators and validation ordering; read when Option.Validators ordering matters.
- `references/system-commandline-2.0.10-subcommand-options.md` — subcommand option scoping and parse results; read when scoping subcommand options.
- `references/system-commandline-bool-flags-and-prescan.md` — bool-flag parsing traps and the pre-scan pattern for help/version detection; read when writing bool-flag pre-scans.
- `references/nuget-evidence-research.md` — NuGet package/API facts without docs: flatcontainer, catalog, raw source; read when researching NuGet facts without docs.
