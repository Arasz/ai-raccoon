---
name: version-bump
description: Use when bumping the ai-raccoon tool version. Run scripts/version-bump.py <patch|minor|major>.
---

# Version bump (ai-raccoon)

Bump the tool version and ship it.

## Script

```bash
python3 scripts/version-bump.py <patch|minor|major>
```

- `patch` — bug fixes / internal refactors (1.8.0 → 1.8.1).
- `minor` — a user-facing feature (1.8.0 → 1.9.0).
- `major` — a breaking change (1.8.0 → 2.0.0).

`VERSION` at the repo root is the **only** hand-written version marker. The script bumps it and
fails loudly if it doesn't hold exactly one occurrence of the current version.

Everything else derives from `VERSION`, not from the script:

- `Directory.Build.props` sets `$(Version)` by reading the `VERSION` file, so the built assembly's
  `AssemblyVersion`/`InformationalVersion` follow automatically.
- `src/AiRaccoon/.mcp/server.json` is tracked with the literal token `__VERSION__` in both version
  slots (top-level `version` and `packages[0].version`) — never a real semver. A csproj target
  (`GenerateMcpServerJson`, hooked to `BeforeTargets="_GetPackageFiles"`) substitutes `$(Version)`
  into a generated copy and packs that copy, not the tracked file.

## Gate

`VersionContractTests` prove the derivation, including the packed artifact (not just the repo copy
or the `obj/` intermediate — a manifest correct in the repo but wrong in the package is the failure
mode this exists to catch):

```bash
dotnet test tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj --filter FullyQualifiedName~VersionContractTests
```

`PackedMcpServerJson_CarriesTheVersionFileVersion` runs `dotnet pack` itself and opens the resulting
`.nupkg` (a zip) to read `.mcp/server.json` out of it, so this one test run is the full proof.

## Release notes

`README.md` "## What's new" gets a compact entry only for a braggable user-facing feature (see the `whats-new-update` skill). No prose, no internal fixes. The root README is packed as the NuGet package readme (`<None Include="..\..\README.md" ...>` in the csproj), so the entry shows on nuget.org too.
