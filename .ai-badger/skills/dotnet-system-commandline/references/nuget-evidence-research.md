# Primary-source research recipe (NuGet / GitHub / Microsoft Learn)

Recipe used 2026-08-04 for the project CLI-parsing exploration, when web_extract was unavailable (search-only backend). All commands: curl + inline python3 JSON parsing.

## Versions (flatcontainer)

    curl -s https://api.nuget.org/v3-flatcontainer/<package-id>/index.json | python3 -c "import json,sys; v=json.load(sys.stdin)['versions']; print('latest:', v[-1]); print('stable:', [x for x in v if '-' not in x][-5:])"

## Metadata: license, TFMs, dependencies, published date

The registration leaf (`registration5-gz-semver2`) is **gzipped** — pass `--compressed`, or curl returns a JSON decode error. Its `catalogEntry` is a URL **string**, not an object; fetch that URL for the full metadata:

    curl -s --compressed "https://api.nuget.org/v3/registration5-gz-semver2/<id>/<version>.json"
    # → { "@id", "catalogEntry": "https://api.nuget.org/v3/catalog0/data/<ts>/<id>.<version>.json", "listed", "published", ... }
    curl -s --compressed "<catalogEntry URL>"
    # → version, licenseExpression, published, dependencyGroups: [{targetFramework, dependencies: [{id}]}]

## Repo status (archived? maintenance? stars)

    curl -s "https://api.github.com/repos/<owner>/<repo>" | python3 -c "import json,sys; d=json.load(sys.stdin); print('archived:', d.get('archived'), '| pushed_at:', d.get('pushed_at'), '| stars:', d.get('stargazers_count'))"

Tree listing (find source files by name across a repo):

    curl -s "https://api.github.com/repos/<owner>/<repo>/git/trees/main?recursive=1" | python3 -c "import json,sys; [print(t['path']) for t in json.load(sys.stdin)['tree'] if t['path'].endswith('.cs')]"

## Source-level behavior (authoritative, beats docs)

    curl -sL "https://raw.githubusercontent.com/<owner>/<repo>/main/<path>" | grep -n ...

## Shared-framework contents (MEASURED zero-dependency evidence)

    ls /usr/local/share/dotnet/shared/Microsoft.AspNetCore.App/<version>/ | grep Configuration
    # → Microsoft.Extensions.Configuration.CommandLine.dll + EnvironmentVariables.dll present
    #   ⇒ providers ship in-box for Microsoft.NET.Sdk.Web projects; no PackageReference needed

## Microsoft Learn pages

Learn serves server-rendered HTML; strip tags/scripts to read:

    curl -sL "<url>" | python3 -c "import sys,re,html; t=sys.stdin.read(); t=re.sub(r'<script.*?</script>|<style.*?</style>','',t,flags=re.S); t=re.sub(r'<[^>]+>',' ',t); print(re.sub(r'\s+',' ',html.unescape(t))[:3000])"

Pitfall: some Learn pages return "Access to this page requires authorization" to anonymous curl (e.g. the System.CommandLine overview did NOT; others do) — treat that as UNVERIFIED and use the GitHub raw source of the docs repo instead
(`dotnet/AspNetCore.Docs`, `dotnet/command-line-api`).

## Verified facts snapshot (2026-08-04, with sources)

- **System.CommandLine**: stable line ends 2.0.10; 3.0.0-preview.6 in flight; GA Nov 2025 (dotnet/command-line-api issue #2576); MIT; net8.0 (zero deps) + netstandard2.0 (System.Memory) per catalog entry. Used by the dotnet CLI itself
  (Microsoft Learn overview).
- **Spectre.Console.Cli**: stable line ends 0.55.0; latest overall 1.0.0-alpha.0.16 (pre-1.0).
- **Cocona**: 2.2.0 last stable, archived 2024-08-13 (GitHub API). **McMaster.Extensions. CommandLineUtils**: 5.1.0, active 2026-07.
- **System.CommandLine stream routing** (command-line-api source): `HelpAction.cs:56-65`
  help → `parseResult.InvocationConfiguration.Output`; `ParseErrorAction.cs:47-53` errors →
  `InvocationConfiguration.Error`; `InvocationConfiguration.cs:36-53` defaults
  `Output ??= Console.Out`, `Error ??= Console.Error`, both settable. Parse errors also render help (to Output) after the error lines.
- **CommandLineConfigurationProvider** (dotnet/runtime source): unknown long options silently stored as config keys; trailing valueless switch silently dropped; `--key --other`
  consumes the next token as the value (no switch guard); bare tokens ignored; last duplicate wins; case-insensitive keys; single-dash switches ignored unless in switchMappings (`-x=value` without mapping throws FormatException).
- **CreateBuilder default layering** (dotnet/AspNetCore.Docs
  `aspnetcore/fundamentals/configuration/index.md:81-87`): command line > env vars (non-ASPNETCORE_/DOTNET_) > user secrets (dev) > appsettings.{ENV}.json > appsettings.json.
- **MCP registry server.json** (schema 2025-10-17): `Package` properties include
  `packageArguments` ("A list of arguments to be passed to the package's binary"),
  `environmentVariables`, `runtimeArguments`, `runtimeHint`, `transport`; arguments are
  `named` (`--flag={value}`) or `positional` (value/valueHint), both with `{curly_brace}`
  variable substitution and `isRepeated`.
- **Client .mcp.json** (JetBrains/VS Code/Claude Code conventions, ErikEJ blog, NuGet.Mcp.Server README): `{"mcpServers":{"<name>":{"command":...,"args":[...],"env":{...}}}}`; dotnet global tools appear as bare command names.
