# NuGet package-id migration — evidence record (2026-08-06)

Case: `arasz.ai-raccoon` → `ai-raccoon` (ai-raccoon repo, PR #53). NuGet support
assigned the raw id one day after the support email about an ownerless reserved
namespace. Companion article PR: arasz-home-page #210.

## Rules (verified against NuGet docs, not search snippets)

| Rule | Finding | Source |
|---|---|---|
| Package id uniqueness | The only hard id rule: an id must be unique on the gallery. No rule prohibits the same software under two ids you own. | docs/create-packages/creating-a-package-msbuild.md ("Choose a unique package identifier"), includes/choose-package-id.md |
| Prefix reservation | Reserved prefix rejects submissions from non-owners; the owning account may publish any matching id. Assignment makes the id AND every `<id>.<rid>` RID payload publishable (RID payloads are `<PackageId>.<rid>`, so they fall under the same prefix). | docs/nuget-org/id-prefix-reservation.md |
| Trusted Publishing (OIDC) | Policy is OWNER-scoped: "The policy will apply to all packages owned by the selected owner" — keyed to owner + repo + workflow filename + optional environment, NOT package id. A new id under the same account needs no policy change. | docs/nuget-org/trusted-publishing.md |
| Retirement | Deprecation (message + alternate package link) is the supported mechanism; NuGet never deletes packages. Old versions stay listed. | NuGet.org FAQ / Manage-packages deprecation flow |

Raw-doc fetch trick: Microsoft Learn pages are hard to extract (ddgs backend is
search-only); pull the markdown from the NuGet docs GitHub repo instead:
`https://raw.githubusercontent.com/NuGet/docs.microsoft.com-nuget/main/docs/...`
(tree listing via the git-trees API + grep).

## Measured: shim conflict forbids dual-publishing a global tool

Local pack of both ids from the same branch (edit csproj PackageId between packs;
`-p:PackageId=` overrides trip restore, see below), same `--tool-path`:

```
$ dotnet tool install --tool-path /tmp/shims --add-source /tmp/feed arasz.ai-raccoon --version 1.0.7   # OK
$ dotnet tool install --tool-path /tmp/shims --add-source /tmp/feed ai-raccoon --version 1.0.8
Tool 'ai-raccoon' failed to update due to the following:
Failed to create shell shim for tool 'ai-raccoon': Command 'ai-raccoon' conflicts
with an existing command from another tool.
```

One command shim per ToolCommandName. Migration sequence that WORKS (verified):
`dotnet tool uninstall -g <old>` → `dotnet tool install -g <new>` → shim runs,
`dotnet tool list` shows only the new id. Data roots survive: `~/.ai-raccoon`
is a hardcoded path (DefaultOptions.cs), keyed to install scope, not package id.

## Version split

The concurrent release lane had already shipped the old id at 1.0.8, so the raw
id's first release was 1.0.9 (owner f:). TDD contract bump ran twice: 1.0.8 →
RED → GREEN, then 1.0.9 → RED → GREEN. Check the flatcontainer of the OLD id
before choosing the new version (`curl https://api.nuget.org/v3-flatcontainer/<id>/index.json`).

## Migration PR surface checklist

- csproj `PackageId` (payload ids follow automatically)
- `.mcp/server.json`: `identifier` + `version` (both top-level and packages[0])
- CI: patch-tool-shell.py target arg; shell/payload find patterns
  (shell = `<id>.<version>.nupkg`, digit right after prefix; payloads = `<id>.<rid>.<version>.nupkg`)
- verify-tool-package.sh nupkg name; fresh-install script install id + version default
- READMEs: install line + uninstall-first note
- Live agent-instruction files (grep `.ai-badger/skills/` — a stale
  `dotnet tool update -g <old>` was caught by the review subagent in
  `ai-raccoon-memory/SKILL.md`; other repos' `.ai-badger` copies need the same fix)
- Announcement surfaces: personal-site article + LinkedIn copy (arasz-home-page:
  content/articles/*.md AND frontend/src/app/data/articles/*.data.ts AND
  content/linkedin/*.md must stay in sync; .data.ts callouts render inline content
  only — a code block after a callout renders outside the quote)
- VersionContractTests id-contract fact (TDD RED first)

## Owner actions after merge (nothing in the PR publishes)

1. Verify reservation in nuget.org account.
2. Dispatch publish.yml → manual approval → shell + 6 RID payloads land.
3. Deprecate old id (message + alternate package).
4. Run the fresh-install script (defaults to the new version).
5. Merge the article PR before/with the release.

## Pitfalls

- `dotnet pack -p:PackageId=<override>` → restore error
  `NuGet.targets(198): error : Ambiguous project name '<id>'` — edit the csproj
  between packs instead of overriding via -p.
- Concurrent sessions on one repo: main moves constantly; merge origin/main
  before the final gate. Worktrees under `.ai-badger/worktrees/` sit INSIDE the
  main checkout — other sessions' repo-wide tools rewrite files there (a doc
  got markdown-reflowed three times); `git status` + revert before merging,
  never commit those.
- Full-suite port flakes: host tests binding the default port (7721) fail while
  another session runs a live server or its own suite. Fix the TEST (FreePort()),
  don't re-run and document — user f: "if tests are failing more than 2 times
  in a session, its better to fix them" (2026-08-06).
- Pre-existing failures: prove with a scratch worktree at origin/main
  (`git worktree add /tmp/maincheck origin/main`, copy gitignored model into
  src/AiRaccoon/Models/, run the same filter) before classifying a gate failure
  as yours.
