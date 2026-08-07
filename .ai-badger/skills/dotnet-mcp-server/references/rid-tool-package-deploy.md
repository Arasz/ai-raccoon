# RID-specific tool packages: shell vs companion (PackAsTool + RuntimeIdentifiers)

Verified 2026-08 while reviewing an MCP-server task (the project, `dotnet pack` deploy target).

## Symptom

A "local NuGet feed deploy" MSBuild target passes every individual step — `dotnet pack`
emits files, `dotnet nuget push` lands a file in the feed — but the feed is unusable:

```
$ dotnet tool install ai-raccon --version 0.1.0-beta --add-source ./.nupkg-local --tool-path /tmp/x
Skipping NuGet package signature verification.
Version 0.1.0-beta of package ai-raccon.osx-arm64 is not found in NuGet feeds https://api.nuget.org/...;.../.nupkg-local/
```

This is the "pieces pass alone, fail together" case: pack ✓, push ✓, install ✗.

## Why

With `PackAsTool=true`, `dotnet pack -p:RuntimeIdentifiers=<host-rid> --no-build` emits TWO packages:

| File                     | Size                      | Contents                                                                                         |
|--------------------------|---------------------------|--------------------------------------------------------------------------------------------------|
| `Id.Version.nupkg`       | ~4 KB (binary-free SHELL) | nuspec, README, `.mcp/server.json`, `tools/net10.0/any/DotnetToolSettings.xml` ONLY              |
| `Id.<rid>.Version.nupkg` | hundreds of KB (payload)  | `tools/net10.0/<rid>/<exe>` + `the project.deps.json`, `runtimeconfig.json`, all dependency DLLs |

The shell's `DotnetToolSettings.xml` redirects the installer to the companion:

```xml
<DotNetCliTool Version="2">
  <Commands><Command Name="the project" /></Commands>
  <RuntimeIdentifierPackages>
    <RuntimeIdentifierPackage RuntimeIdentifier="osx-arm64" Id="ai-raccon.osx-arm64" />
  </RuntimeIdentifierPackages>
</DotNetCliTool>
```

`dotnet tool install` therefore resolves `ai-raccon.osx-arm64` from the same feed (s). Pushing only the shell (e.g. `dotnet nuget push "$(PackageOutputPath)$(PackageId).$(PackageVersion).nupkg"`)
guarantees the install fails.

Note the RID name slots between the id and version: `ai-raccon.osx-arm64.0.1.0-beta.nupkg`.

## Fix

Push everything the pack produced, to the same source:

```bash
dotnet nuget push "$(PackageOutputPath)*.nupkg" --source ./.nupkg-local --skip-duplicate
```

Then prove the feed works with the actual consumer operation:

```bash
dotnet tool install ai-raccon --version 0.1.0-beta --add-source ./.nupkg-local --tool-path /tmp/tooltest
```

## Verification probes (reusable)

- Inspect a package for actual binaries: `unzip -l .nupkg/Id.Version.nupkg` — a shell shows only
  `DotnetToolSettings.xml` under `tools/net10.0/any/`; the payload package shows the exe + DLLs.
- Confirm the redirect: `unzip -p .nupkg/Id.Version.nupkg tools/net10.0/any/DotnetToolSettings.xml`
- Prove gitignore claims: `git check-ignore -v <path>` (exit 0 + rule line = ignored).
- Prove which transport a `dotnet run` started (dual-mode server): `lsof -nP -iTCP:8080 -sTCP:LISTEN`
  — nothing listening + `StdioServerTransport` in the log = stdio branch; port bound = HTTP branch.

## Related notes

- `dotnet run` with no `--launch-profile` uses the FIRST profile in `launchSettings.json` — list
  `stdio` first so plain `dotnet run` serves stdio.
- From a src/ layout repo root, `dotnet run` fails "Couldn't find a project to run" — always pass `--project src/<Proj>`.
- MSBuild env-var gates are case-insensitive: `Condition="'$(DOTNET_ENV)' == 'local'"` fires when the shell exports `dotnet_env=local` (verified on macOS).
