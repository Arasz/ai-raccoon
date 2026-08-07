# Giving a stdio MCP server a CLI (System.CommandLine) — verified facts

Context: the project CLI-args wave (2026-08-04), System.CommandLine 2.0.10 (GA; net8.0 +
netstandard2.0, zero transitive baggage). Everything below was verified against source and/or
empirically against the installed tool binary.

## stdout discipline (the stdio constraint)

Default System.CommandLine behavior (source-verified):
- Help renders via `parseResult.InvocationConfiguration.Output`, default `Console.Out` = **stdout**.
- Parse errors write to `InvocationConfiguration.Error` (stderr) and THEN render help — to stdout.

For a stdio MCP server, either behavior corrupts the newline-delimited JSON-RPC stream. Safe
pattern: **parse-first, never invoke actions**:

```csharp
var result = new CommandLineBuilder(BuildRootCommand()).UseDefaults().Build().Parse(args);
if (result.Errors.Count > 0 || result.Action is HelpAction || result.Action is VersionOptionAction)
    return Render(result, Console.Error);  // own renderer; stdout untouched; exit 0 help/version, 1 errors
```

- `UseDefaults()` wires `-h`/`--help` and `--version`.
- Help-detection idiom (2.0.x GA): `parseResult.Action is HelpAction` / `VersionOptionAction`.
  Fallback if a future version changes it: a custom help option action rendering to the passed writer.
- `Parse` itself writes nothing; `Render` takes the writer as a parameter — unit-test that the
  stdout writer receives zero bytes for help, unknown option, missing value, invalid enum.

## Enum option trap

`Option<McpTransport>` accepts EVERY enum member name, case-insensitive. A spec that says
`--transport <stdio|http>` does NOT reject `--transport https` when the enum also defines
`Https` — it parses to the Https member. Either:
- restrict with a string option + `.FromAmong("stdio", "http")`, or
- document the extra member and let it behave like the env path does today
  (`MCP_TRANSPORT=https` → unsupported warning, no endpoints).

Review plans against the actual enum member set, not against the spec's stated value list.

## Empty-string/whitespace semantics in CLI > env > default merges

`cli.X ?? env ?? default` silently regresses `X=""`. Existing env reads typically gate on
`IsNullOrWhiteSpace` (empty env → built-in default). After the naive merge:
- `--data-root ""` → `DataRoot=""` → `Directory.CreateDirectory("")` throws at first bank open;
- `--embedding-model ""` → `Path.GetFullPath("")` = current directory → model load fails.

The merge layer must treat null OR whitespace as unset for path-like keys, and the precedence
tests must cover whitespace env values, not just null.

## Dotnet tool packaging facts (verified via ~/.dotnet/tools)

- **Command name defaults to the ASSEMBLY name, not PackageId.** Package `the project` installed
  a shim named `the project`:
  `~/.dotnet/tools/the project → .store/the project/0.1.0-beta/the project.osx-arm64/0.1.0-beta/tools/net10.0/osx-arm64/the project`.
  Works on case-insensitive filesystems (Windows, default APFS) by luck; breaks `.mcp.json`
  `"command"` on Linux/case-sensitive macOS. Fix: `<ToolCommandName>the project</ToolCommandName>`.
  **Fastest verification — before installing anything:** `dotnet pack` once (host RID), then
  read the tool settings inside the SHELL nupkg (not the RID payload):
  `unzip -p <out>/the project.0.1.0-beta.nupkg tools/net10.0/any/DotnetToolSettings.xml`
  → must contain `<Command Name="the project" />` (and no `<Command Name="the project"`). This
  asserts the packed command name in one pack instead of an install cycle.
  (Slower fallback: `ls ~/.dotnet/tools` + read the shim's store path.)
- `dotnet tool uninstall -g <id>` matches by PACKAGE id, not command name.
- **`--version` prints `AssemblyInformationalVersion`** — defaults to `1.0.0` even when
  `PackageVersion` is `0.1.0-beta` (verified: MCP `serverInfo.version` = `"1.0.0.0"` from the
  installed tool; `serverInfo.name` = assembly name). For parity: `<InformationalVersion>` or
  `<Version>` in the csproj.
- **Local-feed install** needs BOTH nupkgs in the feed: the shell package
  (`Id.Version.nupkg`) and the RID payload (`Id.<rid>.Version.nupkg`). Host RID check:
  `dotnet --info | grep RID`. Rule out nuget.org source ambiguity:
  `curl -o /dev/null -w '%{http_code}' https://api.nuget.org/v3-flatcontainer/<id>/index.json`
  → 404 = not published, local feed wins.
- **Env-var-gated MSBuild deploy targets**: MSBuild property lookup is case-INSENSITIVE
  (`Condition="'$(DOTNET_ENV)' == 'local'"` sees exported `dotnet_env`), while .NET code using
  `Environment.GetEnvironmentVariable` is case-SENSITIVE on Unix. ⚠ CONTESTED: a plan review
  (2026-08-04) asserted the opposite for macOS — "MSBuild env lookup is case-sensitive on
  macOS; `dotnet_env=local` does NOT trigger `'$(DOTNET_ENV)' == 'local'`" — but did not
  demonstrate it empirically. Don't argue either direction in a review; the robust move is to
  **export the env var in the exact case the condition uses** (`export DOTNET_ENV=local` for
  `'$(DOTNET_ENV)' == 'local'`) so the gate fires under either behavior, and prove the gate
  fired by running the build and checking the feed was populated (or, when the target is
  suspect, fall back to the manual `dotnet pack -p:RuntimeIdentifiers=<rid> -o .nupkg-local`
  + `dotnet nuget push` commands).

## stdio handshake smoke test (empirically verified)

The frame `{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18",
"capabilities":{},"clientInfo":{"name":"smoke","version":"0"}}}` is a valid MCP initialize;
the C# SDK answers a result frame on stdout.

**EOF race**: `printf '<frame>' | tool 2>/tmp/err | head -c 400` can return ZERO bytes — the
transport reads the message, sees stdin-EOF, and shuts down before the response flushes
(stderr: "transport completed reading messages"; process exits 0). Hold stdin open:

```bash
{ printf '%s\n' '<frame>'; sleep 3; } | <tool> 2>/tmp/the project-smoke.err | head -c 400
```

- Process exits 0 on stdin-EOF → no `timeout` needed; `timeout(1)`/`gtimeout` are absent on
  stock macOS anyway.
- Same root cause as the Python-probe pitfall in SKILL.md §3b (never `communicate(input=…)`).

## E2E notes

- `WebApplicationFactory<TProgram>` invokes the entry point with EMPTY args — CLI-over-env
  behavior cannot be E2E-tested through WAF. Correct split: CLI proven by unit tests + manual
  smoke gate; env path proven by E2E (env mutated in factory ctor before the lazy host build,
  restored in Dispose; new env vars must join the factory's save/restore set; serial collection).
- `CreateBuilder([])` after consuming args is safe: launchSettings URLs flow via
  `ASPNETCORE_URLS` env (not args), E2E uses TestServer (no URLs).
