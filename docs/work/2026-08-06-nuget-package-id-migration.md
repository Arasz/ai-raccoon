# NuGet package id migration: arasz.ai-raccoon -> ai-raccoon

Date: 2026-08-06. Task: ai-raccoon-package-id. PR: (this task's PR).

## Context

- `ai-raccoon` was blocked by an ownerless reserved namespace (prefix reservation
  with no owner). Every push 409'd (`bump-version-to-1.0.1` record; 12/12 pushes
  across 3 versions and all 7 ids — the raw id AND the six `ai-raccoon.<rid>`
  payloads, proving the reservation is prefix-based).
- Bridge: published as `arasz.ai-raccoon` since 1.0.1 (`rename-package-id-to-arasz-ai-raccoon`),
  ToolCommandName stayed `ai-raccoon`.
- Owner emailed support@nuget.org (2026-08-05). NuGet support responded: the
  `ai-raccoon` id was assigned to the account.

## Q1: can we deploy both ids? — NuGet rules (verified against source docs)

| Rule | Finding | Source |
|---|---|---|
| Package id uniqueness | The ONLY hard id rule: an id must be unique on the gallery. No rule prohibits the same software under two ids you own. | `docs/create-packages/creating-a-package-msbuild.md` ("Choose a unique package identifier"), `includes/choose-package-id.md` |
| Prefix reservation | A reserved prefix rejects submissions from non-owners; the owning account may publish any matching id. Assignment makes `ai-raccoon` AND `ai-raccoon.<rid>` payloads publishable. | `docs/nuget-org/id-prefix-reservation.md` |
| Trusted Publishing (OIDC) | Policy is OWNER-scoped ("The policy will apply to all packages owned by the selected owner") — keyed to owner + repo + workflow file + optional environment, NOT to package id. No nuget.org policy change needed for the new id under the same account. | `docs/nuget-org/trusted-publishing.md`; consistent with the learned `dotnet-nuget-publish` skill ("keyed to package owner + repo + workflow file name only") |
| Deprecation | nuget.org supports deprecating a package with a message and an alternate-package link. | NuGet.org FAQ / Manage-packages deprecation flow |

**Verdict: legal, but operationally impossible for this package — measured:**

```
$ dotnet tool install --tool-path /tmp/shims --add-source /tmp/feed ai-raccoon --version 1.0.8
  (with arasz.ai-raccoon 1.0.7 already installed in the same tool-path)
Tool 'ai-raccoon' failed to update due to the following:
Failed to create shell shim for tool 'ai-raccoon': Command 'ai-raccoon'
conflicts with an existing command from another tool.
```

Both packages install the same command shim (`ToolCommandName=ai-raccoon`), and
the .NET SDK refuses a second shim for the same command name. Dual-publishing
would also double every release surface and show two identical package pages.
→ **Migrate to `ai-raccoon`; do not dual-publish.**

## Q2: migration path

### This PR (merged code changes)

- `PackageId` -> `ai-raccoon`, version 1.0.8 (csproj + `.mcp/server.json`
  identifier/version; VersionContractTests RED first, TDD).
- publish.yml: patch-tool-shell target + shell/payload find patterns.
- scripts: verify-tool-package.sh nupkg name, manual-fresh-install-test.py id +
  version default.
- READMEs: install line + migration note for existing users.

### Owner actions after merge (nothing in this PR publishes)

1. **Verify the assignment** on nuget.org (account -> reserved prefixes shows
   `ai-raccoon`, or simply trust the first push).
2. **Dispatch publish.yml** (manual approval on the `production` environment).
   Trusted publishing already covers the new id — owner-scoped policy, no change.
   This lands `ai-raccoon` 1.0.8 shell + 6 RID payloads, the first packages
   under the raw id.
3. **Deprecate `arasz.ai-raccoon`** on nuget.org: message "renamed to
   `ai-raccoon`" + alternate package `ai-raccoon`. Old versions stay listed
   (NuGet does not delete); deprecation is the supported retirement signal.
4. **Verify the published tool**: `python3 scripts/manual-fresh-install-test.py`
   (defaults to `AI_RACCOON_VERSION=1.0.8`, installs from nuget.org into an
   isolated tool-path).

### Existing users

- `dotnet tool uninstall -g arasz.ai-raccoon && dotnet tool install -g ai-raccoon`
  (order matters: same shim name, side-by-side install is refused — measured).
- **The memory bank survives**: the data root is a hardcoded path,
  `~/.ai-raccoon` (`src/AiRaccoon/Setup/DefaultOptions.cs:10`), keyed to the
  install scope, not the package id. No data migration.

## Evidence log

- `ai-raccoon` on nuget.org at task time: flat-container 404, search 0 hits,
  gallery 404 — nothing published under the raw id yet (assignment is
  account-side, invisible to read APIs until the first push).
- Shim conflict: measured locally with two packs from this branch
  (arasz.ai-raccoon 1.0.7 + ai-raccoon 1.0.8, osx-arm64, same `--tool-path`).
- Packed payload naming under the new id: `ai-raccoon.<rid>.<version>.nupkg`
  (verified: `ai-raccoon.osx-arm64.1.0.8.nupkg`), matching the reserved prefix.
