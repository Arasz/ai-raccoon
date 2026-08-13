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

The script reads the current version from `src/AiRaccoon/AiRaccoon.csproj` (`PackageVersion`), bumps it, and writes the five markers in sync — failing loudly if they've drifted:

- `src/AiRaccoon/AiRaccoon.csproj` — `PackageVersion`, `InformationalVersion`, `AssemblyVersion`.
- `src/AiRaccoon/.mcp/server.json` — top-level `version` + `packages[0].version`.
- `tests/AiRaccoon.Tests/Unit/Setup/VersionContractTests.cs` — `ExpectedVersion`.

## Gate

`VersionContractTests` assert the markers agree and carry no prerelease suffix. Prove the bump:

```bash
dotnet test tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj --filter FullyQualifiedName~VersionContractTests
```

## Release notes

`README.md` "## What's new" gets a compact entry only for a braggable user-facing feature (see the `whats-new-update` skill). No prose, no internal fixes. The root README is packed as the NuGet package readme (`<None Include="..\..\README.md" ...>` in the csproj), so the entry shows on nuget.org too.
