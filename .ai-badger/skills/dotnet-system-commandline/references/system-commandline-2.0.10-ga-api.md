# System.CommandLine 2.0.10 GA API — verified facts (2026-08-04)

Verified empirically against the installed `system.commandline/2.0.10` package (net8.0 lib)
and its XML docs (`lib/net8.0/System.CommandLine.xml`) while implementing parse-first CLI args for the the MCP server. The GA release differs sharply from the 2.0.0-beta/rc API that most blog posts (and the plan being executed) assumed.

## Parse — no CommandLineBuilder, no UseDefaults

- `System.CommandLine.Builder` namespace, `CommandLineBuilder`, and `UseDefaults()` do NOT exist in 2.0.10 — the XML docs contain zero members under those names. Anything showing
  `new CommandLineBuilder(root).UseDefaults().Build().Parse(args)` is the beta API.
- GA parse entry points: `Command.Parse(IReadOnlyList<string>, ParserConfiguration)` and
  `(string, ParserConfiguration)`. Also `CommandLineParser.Parse(Command, ...)`.
- `ParserConfiguration` documented members: `EnablePosixBundling`, `ResponseFileTokenReplacer`
  (XML docs may be incomplete; `EnablePosixBundling = true` is the safe default).
- `RootCommand` auto-wires help (`-?, -h, --help`) and `--version` — no manual addition.

## Action idioms (help / version / errors)

Probe results (args → `Action` type name, errors):

| Args                | Action                                                 | Errors       |
|---------------------|--------------------------------------------------------|--------------|
| `--help` / `-h`     | `System.CommandLine.Help.HelpAction`                   | 0            |
| `--version`         | `System.CommandLine.VersionOption+VersionOptionAction` | 0            |
| `--bogus`           | `System.CommandLine.Invocation.ParseErrorAction`       | 1            |
| `--help --bogus`    | `HelpAction` (help wins)                               | 0            |
| `--version --bogus` | `VersionOptionAction` (version wins)                   | 0            |
| `--help --version`  | `VersionOptionAction`                                  | 1 (conflict) |
| `[]`                | null                                                   | 0            |

- Help: `parseResult.Action is HelpAction` — public type, compiles.
- Version: `parseResult.Action is VersionOption.VersionOptionAction` does NOT compile (CS0122: inaccessible due to its protection level — nested type, no public accessibility). Pinned idiom:
  `parseResult.Action?.GetType().Name == "VersionOptionAction"`. The type is also absent from the XML docs.
- Errors: `parseResult.Errors` — `ParseError.Message` strings.

## Reading values

- `parseResult.GetResult("--name")` returns `SymbolResult` — cast to `OptionResult` before calling `GetValueOrDefault<T>()` (no 0-arg `GetValueOrDefault()` overload exists).
- `OptionResult.GetValueOrDefault<T>()` **THROWS** `InvalidOperationException` on invalid enum values: "Cannot parse argument 'ftp' for option '--transport' as expected type
  'McpTransport'. Did you mean one of the following? …" → read values only when
  `parseResult.Errors.Count == 0` (presence-check first, then read).
- Enum binding is case-insensitive ("user" → User). `ToString()` yields PascalCase ("Http") — `ToLowerInvariant()` it when the contract says "https".
- Unparsed option: `GetResult` is null; `GetValue` returns `default(T)` (e.g. Stdio for an enum) — use `GetResult` presence to distinguish "not given" from the default.
- Also available: `ParseResult.GetValue<T>(string)`.

## Duplicates, missing values, empty strings

- Repeated single-value option → parse error: "Option '--data-root' expects a single argument but 2 were provided." NOT last-wins (last-wins was beta behavior). No switch restores last-wins — pin the error in tests.
- Missing value (`--data-root` trailing): "Required argument missing for option:
  '--data-root'."
- `--data-root ""` → empty string value, 0 errors (merge layer must treat whitespace as unset if that is the contract).
- `--data-root --access-mode ro` → `--data-root` consumes `--access-mode` as its value, then "ro" errors: "Unrecognized command or argument 'ro'."

## Rendering (stdout discipline)

- `parseResult.Invoke(new InvocationConfiguration { Output = writer, Error = writer })`
  returns the process exit code: 0 for help/version, 1 for parse errors.
- `InvocationConfiguration.Output` / `.Error` are settable (XML: P:…Output / P:…Error). HelpAction renders help to Output; ParseErrorAction renders error message + help (both to the configured writers) — so with both set to the same stderr
  writer, ALL CLI text goes to stderr.
- Version renders the ENTRY assembly's informational version. `<InformationalVersion>0.1.0-beta</InformationalVersion>`
  yields "0.1.0-beta+<git-sha>" (SourceLink appends the SHA). In a WebApplicationFactory test host the entry assembly is the test runner → assert routing only (non-empty writer, exit 0), never the version content.

## Option construction (GA ctor changes vs beta)

- `Option<T>` ctor: `(string name, string[] aliases)` — the beta's description parameter is gone; set `Description` via property initializer. Passing an `Argument<T>` as the 2nd arg fails to compile (it is `string[]`).
- Help placeholder: `Option.HelpName` (or `Argument<T>("stdio|http|https")` — the
  `Argument<T>(string name)` ctor exists) renders `--transport <stdio|http|https>`.
- Hide options: `Symbol.Hidden = true` (there is no `IsHidden`).
- `RootCommand.ExecutableName` is a read-only STATIC property — cannot be set per instance; the usage line derives from the entry assembly name.
- GA help format: "Description:\n  <desc>\n\nUsage:\n  <name> [options]\n\nOptions:\n --x <helpname>  desc\n -?, -h, --help Show help and usage information\n --version Show version information" — note "Usage:" (not "USAGE:"), `-?` included.

## WebApplicationFactory trap (parse-first entry points)

- `WebApplicationFactory<T>` invokes the REAL entry point with
  `--environment=Development --contentRoot=<project-dir> --applicationName=<AssemblyName>`
  (captured verbatim from a debug write in Program.cs). These are how the factory applies the test environment/content root.
- A parse-first entry point rejects them as unknown options → early return → the factory throws "The entry point exited without ever building an IHost." at `CreateClient()`, failing every E2E test. The old
  `WebApplication.CreateBuilder(args)` path tolerated them (the config provider silently stores unknown keys), so this only surfaces after switching to parse-first + `CreateBuilder([])`.
- Fix: declare the three as hidden no-op options in the root command (`new Option<string>("--environment") { Hidden = true }`, same for `--contentRoot`,
  `--applicationName`), values intentionally never consumed. Pin with a test: the triplet parses with zero errors and produces no options (so `ServerConfig` still gets "nothing typed").

## Empirical verification recipe

Throwaway console project (ImplicitUsings enable) referencing System.CommandLine 2.0.10; parse known arg lists and print `Action?.GetType().Name`, `Errors.Count`, `GetResult`
presence. Use `root.Parse(...)` with a `StringWriter`-backed
`InvocationConfiguration { Output = sw, Error = sw }` to capture rendered help/errors and exit codes. This pins framework behavior before writing the real tests (the plan's assumed behaviors — last-wins duplicates, `VersionOptionAction`
pattern — were both wrong for GA).

## 2026-08-04 additions — verb-tree era (all probe-verified)

### Root with subcommands REQUIRES a verb

`RootCommand` (and any `Command`) with subcommands cannot be invoked bare. Probe results:

| Args                          | Errors                                                                           |
|-------------------------------|----------------------------------------------------------------------------------|
| `[]`                          | 1: "Required command was not provided."                                          |
| `--data-root /x`              | 1: "Required command was not provided." (options parse fine, error still raised) |
| `access` (verb with children) | 1: "Required command was not provided."                                          |
| `--help`                      | 0, `HelpAction` (help wins over the missing verb)                                |

There is no `Command.Required`-style switch making subcommands optional in 2.0.10 (only `Option.Required` exists). The working pattern: two roots over the same launch options (full tree for help/version/verb parsing, launch-only root for
verb-less flag sets), switching on a token pre-scan for known verb tokens (skip `--opt` and its value /
`--opt=value`; single-dash tokens are short options; any other token in the verb set means "verb present"). Re-parse only when the full-tree parse has errors AND no verb token was found — genuine option errors re-surface identically on the
launch-only root.

### Root options must PRECEDE subcommands

`--data-root /x access list` → 0 errors, option parses. `access list --data-root /x` → parse errors ("Unrecognized command or argument"). Options after a verb are NOT matched against root options in GA — document "flags first, then verb".

### Optional arguments need `Arity = ArgumentArity.ZeroOrOne`

`new Argument<string?>("path")` is REQUIRED by default: `model set local` (path omitted)
fails with "Required argument missing for command: 'local'." Fix:
`new Argument<string?>("path") { Arity = ArgumentArity.ZeroOrOne }`. Same for any optional positional. Value-typed `Argument<bool>`/`Argument<int>` bind "true"/"false" / integers natively; invalid values are parse errors.

### Required options via `Option.Required = true`

`new Option<string>("--bucket") { Required = true }` — without it, a missing option is NOT a parse error (the command runs with null). With it: "Option '--bucket' is required."
Repeated single-value options stay a parse error (see above).

### `ParseResult` lives in `System.CommandLine`, not `.Parsing`

`ParserConfiguration` is in `System.CommandLine.Parsing`; `ParseResult` is in the parent namespace. Only importing `.Parsing` yields CS0246 for `ParseResult` — import both.

### Command construction idioms that compile in GA

- `new Command("name", "description")` — the description ctor param EXISTS for Command (unlike `Option<T>`, which lost it).
- Collection initializer adds arguments/options: `new Command("x", "d") { new Argument<string>("y") }`
  works; `command.Add(new Option<string>("--o"))` also works.
- Command-path traversal for dispatch: walk `CommandResult.Parent as CommandResult` up to the root, collect `Command.Name`s, drop the root's name → the verb path.

### WebApplicationFactory `UseSetting` → entry args (corrects the "can't inject args" claim)

Probe: a throwaway web `Probe` project whose `Program.Main` stores `args` in a static, plus `Probe.Tests` with `WebApplicationFactory<Program>` overriding `ConfigureWebHost` to
`builder.UseSetting("transport", "http")` + `UseSetting("dataRoot", "/tmp/xxx")`. Captured args after `CreateClient()`:

```
--transport=http
--environment=Development
--dataRoot=/tmp/xxx
--contentRoot=/private/tmp/wafprobe/Probe
--applicationName=Probe
```

So `UseSetting(key, value)` → `--key=value` in Program.Main's args (the factory's
`DeferredHostBuilder` flattens host configuration into args). Use the EXACT option names (`UseSetting("data-root", ...)` for a `--data-root` option — keys are case-sensitive). This is the injection path for E2E when env vars are no longer a
channel.
