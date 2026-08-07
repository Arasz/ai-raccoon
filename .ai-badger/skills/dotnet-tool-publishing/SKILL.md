---
name: dotnet-tool-publishing
description: "Use when packaging or publishing a .NET CLI tool (PackAsTool) or library to NuGet: the MSB3030 build-before-pack trap (and its Web-SDK inversion), multi-RID matrix shells + the shell-race fix, gitignored bundled assets, Trusted Publishing/OIDC with human approval gates, the 409-published-nothing diagnosis, ToolCommandName/PATH shim rules, and full fresh-install verification for MCP tools with bundled models."
version: 1.0.0
author: ai-badger
license: MIT
platforms: [linux, macos, windows]
metadata:
  hermes:
    tags: [dotnet, nuget, packaging, publishing, tools]
    related_skills: [dotnet-system-commandline, dotnet-mcp-server]
---

# Publishing .NET CLI tools (PackAsTool -> NuGet)

Use when: packing a `PackAsTool` dotnet tool, pushing it to NuGet (incl. Trusted
Publishing), building the GitHub Actions publish workflow, or version-bumping a
published tool. Reference implementation: the reference tool (Cocona 2.2.0), published 1.1.0 via Trusted
Publishing 2026-08-05. Verified against the official NuGet Trusted Publishing
docs the same day.

## The pack trap: MSB3030 on clean checkouts (verified 2026-08-05)

`dotnet pack` on a PackAsTool project FAILS on a clean checkout:

    Microsoft.NET.Publish.targets(372,5): error MSB3030: Could not copy the file
    ".../bin/Release/net10.0/<tool>.deps.json" because it was not found

The publish pass inside pack expects bin/Release outputs the implicit build does
not produce. Reproduced: fresh clone + `dotnet pack -c Release` alone -> 4x MSB3030.
A local worktree that already ran `dotnet build -c Release` masks the bug (pack
then succeeds), which is why it only explodes in CI. the reference tool's first
publish dispatch failed exactly this way.

Fix — always build first, then pack with --no-build:

    dotnet build src/CliTool/CliTool.csproj -c Release
    dotnet pack  src/CliTool/CliTool.csproj -c Release --no-build -o ./artifacts

In GitHub Actions publish.yml the Build step MUST precede Pack. Reproduce the
failure on a clean clone (`git clone --depth 1`) before/after fixing — never
trust a worktree that has Release artifacts lying around.

**EXCEPTION — Web-SDK (`Microsoft.NET.Sdk.Web`) multi-RID tools: the advice
inverts (measured 2026-08-05 on a reference tool).** For a project with
`RuntimeIdentifiers` set on a Web-SDK project, `dotnet build -p:RuntimeIdentifiers=<rid>`
then `dotnet pack --no-build` FAILS MSB3030 because a plain Web-SDK build does
NOT emit RID-scoped publish outputs (`bin/Release/net10.0/<rid>/`). The working
form on a clean tree is the single pack WITHOUT `--no-build` and WITHOUT a
separate build step — pack builds for the RID itself and emits the tool shell +
RID payload in one step (~7 s, both nupkgs):

    dotnet pack src/X/X.csproj -c Release -p:RuntimeIdentifiers=<rid> -o artifacts

Always verify the actual sequence on a clean tree (`rm -rf obj bin` first) — the
"build then pack --no-build" recipe and its inverse each hold for different
project SDK shapes, and a worktree with leftover RID-scoped outputs masks either
bug.

**Nested-pack recursion in AfterTargets local-deploy targets.** A
`DeployToLocalSource`-style target (`AfterTargets="Build"`, gated on an env var
like `DOTNET_ENV=local`) that Execs `dotnet pack` will re-enter itself forever:
the nested pack inherits the env var, its own Build fires the target again, and
the run hangs (observed: minutes of repeated builds, empty log). Guard the
target's Condition with a suppression property and pass it to the nested pack:

    Condition="'$(DOTNET_ENV)' == 'local' and '$(SuppressDeployToLocalSource)' != 'true'"
    <Exec Command="dotnet pack ... -p:SuppressDeployToLocalSource=true ..."/>

Also drop `--no-restore` from that nested pack if the outer build restored
without the RID — the referenced projects' assets files then lack the
`net10.0/<rid>` target (NETSDK1047).

## Restore trap: NU1301 from a gitignored local feed on fresh runners

If nuget.config adds a local folder source that is gitignored (e.g. `.nupkg-local/`),
restore fails with NU1301 on a fresh runner where the dir doesn't exist. Fix: `mkdir -p
.nupkg-local &&` before build/restore in every workflow (or scope the local source out of
CI). Verified 2026-08-05 on a reference tool — the workflow author hit this after the first
dispatch and fixed it in a follow-up commit.

## csproj essentials for a tool package

- `<PackAsTool>true</PackAsTool>` + `<ToolCommandName><tool></ToolCommandName>`.
  **The ToolCommandName dispatch rule (verified 2026-08-05):** the shim a global
  tool install creates is named exactly `ToolCommandName`, and `dotnet <command>`
  resolves by looking for a shim literally named `dotnet-<command>` on PATH.
  So `ToolCommandName=ignore` creates a shim `ignore` — bare `ignore list` works
  but the README-documented `dotnet ignore list` fails with "a dotnet-prefixed
  executable with this name could not be found on the PATH" (`dotnet mcp` works
  only because its shim is `dotnet-mcp`). Make ToolCommandName match the
  documented invocation: `<tool>` (matches AssemblyName + package id).
  the reference tool shipped 1.1.0 with the broken `ignore` value; the 1.2.0 fix
  renamed it and `dotnet ignore` worked — verify via pack -> install to a
  tool-path -> `dotnet ignore list`, not from a pre-existing global install
  (the old shim lingers there).
- **The packed README must document the PATH requirement** (user-driven lesson
  2026-08-05, PR #18 on the reference tool): `dotnet <tool> <subcommand>` dispatches
  to a shim in `~/.dotnet/tools` (macOS/Linux) / `%USERPROFILE%\.dotnet\tools`
  (Windows), which must be on PATH. A README that jumps from
  `dotnet tool install -g <pkg>` straight to usage makes the FIRST command a
  new user runs fail with "dotnet-prefixed executable could not be found" —
  the exact trap that surfaced this bug. Since the packed README is what the
  nuget.org readme tab shows, ship the install dir + `export PATH="$PATH:$HOME/.dotnet/tools"`
  in the Installation section (mirrors the tool README).
  PackAsTool implies the DotnetTool package type — never set
  `<PackageType>DotnetCliTool</PackageType>`: it only supports .NET Core 2.2 and
  fails NETSDK1093 on modern SDKs.
- `<GeneratePackageOnBuild Condition="'$(Configuration)'=='Release'">True</GeneratePackageOnBuild>`
  — unconditional True packs a nupkg on every Debug build (slows test loops).
- `PackageLicenseExpression` (Apache-2.0), `PackageReadmeFile` +
  `<None Include="..\..\README.md" Pack="true" PackagePath="\"/>`,
  `PackageReleaseNotes` (NuGet shows these inline — keep them current).
- No `<DotNetCliToolReference>` (obsolete since .NET Core 3).

## Pack -> install -> smoke gate (the tool-as-shipped verification)

Unit tests never exercise the packaged artifact. After packing:

    dotnet tool install <name> --tool-path /tmp/tooltest --add-source <pack-out> --version <X>
    /tmp/tooltest/<command> -h
    /tmp/tooltest/<command> <subcommand> <happy-path args>   # run in a scratch dir
    /tmp/tooltest/<command> <bad args>; echo $?              # clean stderr + exit 1

`--version` and help render the real entry assembly only from the installed tool
— assert the version there, never from the test host.

### The full fresh-install gate (MCP tools with bundled assets) — verified 2026-08-06 on the reference tool 1.0.6

The smoke gate above proves the tool RUNS; it does NOT prove a clean install works first
try with all assets present. For tools that ship bundled models/native libs and speak MCP
over stdio, run the full protocol (a committed fresh-install script in the reference
tool; full detail: read `references/fresh-install-verification.md` when verifying a fresh install):

- **Isolate everything.** `dotnet tool install --tool-path $TOOLPATH --version <v>` (never
  `-g` — the user's real install stays untouched), fresh `NUGET_PACKAGES=<dir>` so the
  install genuinely fetches from nuget.org instead of the local package cache,
  `--data-root $DATAROOT` (fresh dir) to bypass the tool's default data dir, and
  `unset <APP>_DB_PASSPHRASE` — an inherited secret env var silently changes the tested
  path (encrypted-bank mode).
- **Integrity, not presence.** sha256-verify the bundled model + vocab against the pins in
  source (`BundledModel.ModelSha256` etc.). A wrong/tampered asset silently triggers the
  runtime download fallback, so a bare `ls` presence check false-passes.
- **CLI output goes to STDERR in stdio tools** (stdout is reserved for the protocol):
  `--version`/`--help` print to stderr by design — capture `2>&1`, assert substring
  (`1.0.6+<commit>`), never exact equality.
- **THE FALSE-PASS TRAP — a config-gated engine degrades silently.** On a fresh bank the
  embedding provider is unset, so `memory_write` SKIPS embedding and `memory_search` runs
  FTS5-only — an exact-keyword query still returns the entry, so a completely model-less
  install passes "search works". The empty-provider state is deliberate and user-surfaced
  (`model reset` prints "no engine (FTS5-only search)"), so the fix is not to change the
  product but to test honestly: (1) run the documented setup verb (`model set local`) as
  part of the happy path; (2) assert the engine actually ran — `stats.pending == 0`
  (pending > 0 means writes were deferred, never embedded); (3) assert stderr contains NO
  "Downloading bundled model asset" / "Bundled embedding model unavailable" / "Failed to
  download" lines (no silent repair); (4) keep a zero-config probe as an informational
  step that documents the degraded default rather than pretending it's the model path.
- **Result shapes come from source, not guesses.** The MCP SDK wraps tool results as
  `result.content[0].text` containing a JSON STRING — unwrap before asserting fields.
  `serverInfo` reports the ASSEMBLY identity (name = assembly name, version =
  AssemblyVersion numeric-only, e.g. `1.0.6.0`) NOT the `.mcp/server.json` marketing
  name/version — assert the version as a prefix. In this session 7 of 8 first-run
  "failures" were driver assertions against guessed shapes (WriteResult /
  StatsResult{Entries,Pending} / SearchResultList fields) — zero product bugs.
- **Regression checks:** dual-instance concurrent initialize on a second fresh data root
  (port-bind bugs like the 127.0.0.1:5000 class); graceful shutdown by closing stdin →
  clean exit 0 (EOF handling; no orphan processes).
- **Version pin per republish** (NuGet versions are immutable) — expose an env override
  (`<APP>_VERSION`) so the gate re-runs against the next release without editing the
  script.


> Multi-RID tool packages: read `references/multirid-tool-packages.md` when packing a per-RID matrix (shell-race fix and the deployment reality).


> Bundled content assets: read `references/bundled-content-assets.md` when shipping gitignored pack globs, store layout, or provisioning.


> Trusted Publishing: read `references/trusted-publishing.md` when setting up NuGet Trusted Publishing/OIDC (no API keys).


> Version bump / stable-release prep: read `references/stable-release-prep.md` when preparing a stable release or a version bump.

## Review checklist: a NuGet publish PR (workflows + csproj)

- **Run actionlint on every workflow file** — it catches syntax errors the YAML
  parser and GitHub's own lenient rendering both miss. This session's review
  "fix" (`workflow_dispatch: branches: [main]`) passed YAML parsing and looked
  reasonable but is a hard actionlint error; a code-reviewer suggested it and it
  shipped. `actionlint .github/workflows/*.yml` is the gate — run it before
  merging any workflow change, and validate the merged state again after.
- **Action versions are the latest** — don't trust memory. When the GitHub API is
  rate-limited (unauthenticated curl often is), the redirect trick still works:
  `curl -s -o /dev/null -w '%{url_effective}' -L https://github.com/<owner>/<repo>/releases/latest`
- **Test filters match real tests** — traits are often applied via constants
  (`[Trait(TestCategories.Speed, TestCategories.Fast)]`), so grepping the literal
  `Trait("Speed"` finds nothing. Grep for `Trait(` / the constant names to confirm the
  filter runs tests, not zero.
- **Fast-on-PR / full-nightly split is sound** — verify the nightly cron runs the full
  suite unfiltered and has `workflow_dispatch`; the fast filter must use the trait the
  suite actually declares.
- **Environment gate on the right job** — `environment:` only on the push job; pack
  matrix jobs stay un-gated.
- **Metadata DO items** (MS package-authoring best practices): Authors = pretty name,
  Copyright `Copyright (c) <name> <year>`, PackageProjectUrl, RepositoryUrl +
  RepositoryType=git, PackageLicenseExpression (OSI/FSF approved — must match the repo
  LICENSE), PackageReadmeFile AND the file packed (`None Include ... Pack="true"`),
  Description <4000 chars, PackageTags space-delimited search-oriented terms (<4000
  chars) — NOT internal feature names, PackageReleaseNotes (or a link to the releases
  page). Icon is CONSIDER-only: never suggest adding one unless an asset exists.
  **Tags rule (user preference, corrected twice 2026-08-05):** tags exist to help
  someone SEARCH for the tool — terms a user would type to find it (mcp, agent,
  memory, sqlite, dotnet-tool, rag...). NEVER internal implementation features
  (observability, sync, s3, encryption, workspace, sandbox, fts, json-rpc) — the
  user rejected those as misleading; they describe the package, they don't find it.
  When unsure, ask the "would anyone search this?" test, not "does the package do
  this?".
- **Respect explicit design constraints in the task** (e.g. "manual approval is the
  gate", "don't change the approval design") — flag risks as Low findings with
  "optional" fixes instead of redesigning.
- **Static version = single-shot workflow** — see version-input note above; flag it as
  Medium for any publish workflow meant to be re-run.

## Library packages (non-PackAsTool)

Same Trusted Publishing mechanics, two differences:

- No multi-RID matrix: one `dotnet pack -c Release` job suffices (multi-targeting via
  `TargetFrameworks` inside the single nupkg).
- The publish job can skip the fresh-install smoke (no tool shell to install); the
  metadata checklist below still applies.


> Workflow skeleton: read `references/workflow-skeleton.md` when writing the publish workflow from scratch.


> Trigger choice: read `references/trigger-choice.md` when choosing a workflow trigger (push-to-trunk vs pull_request).

## Green publish run that published nothing (409 on every push)

With `--skip-duplicate` on the push, EVERY 409 conflict becomes a no-op and the run
still concludes **success** — a green run proves nothing was pushed. The push-step
lines are the truth: `PUT ... 201 Created` = published, `Conflict ... already exists` =
skipped. Always read them before telling anyone the release is live.

Diagnosis when every push 409s for a version the read APIs can't see (full ladder in
`references/push-409-invisible-package.md`):

1. **Read-API sweep** — flat container `https://api.nuget.org/v3-flatcontainer/<id>/index.json`
   (404 = no versions AT ALL, listed or unlisted), registration
   `.../v3/registration5-gz-semver2/<id>/index.json` (XML BlobNotFound), search
   `https://azuresearch-usnc.nuget.org/query?q=packageid:<id>&prerelease=true` (totalHits 0),
   gallery page 404. All four invisible = the id exists nowhere public.
2. **Control the queries** — query a known-live package owned by the SAME account
   (e.g. a sibling tool). If it shows up in all four, your queries are sound AND the
   account's OIDC publishing mechanism works — the problem is specific to this id/version.
3. **Check earlier runs** — `gh run list --workflow publish.yml` + `gh run view <n> --log |
   grep -E "Pushing|PUT http|Conflict|Created"`. An older run that ALSO all-conflicted on
   an earlier version means the block predates the current bump; the login step saying
   "Successfully exchanged OIDC token" rules out a policy problem.
4. **Official docs rule out "deleted"** — nuget.org does NOT support permanent deletion,
   only unlisting, and unlisted versions STAY in the flat container.


> Metadata checklist: read `references/metadata-checklist.md` when filling NuGet package metadata.

## Gotchas

- MSB3030: pack fails on clean checkouts when the build-before-pack order is wrong — build, then pack.
- A green publish run can publish nothing (409 on every push) — verify the package id/tool command name pair, not just the workflow status.
## See also

- `references/push-409-invisible-package.md` — worked 409-everywhere diagnosis: the (read when every push 409s)
  four read-API probes, control queries, run-log archaeology, and the unlist-only rule.
- `references/web-sdk-multirid-pack-reproduction.md` — measured A/B/C reproduction (read when reproducing the MSB3030 trap)
  of the MSB3030 trap on Web-SDK multi-RID tools (build-then-pack vs single-pack)
  and the nested-pack recursion guard for AfterTargets local-deploy targets.
- `references/nuget-publish-pr-review.md` — worked example: reviewing a NuGet publish PR (read when reviewing a NuGet publish PR)
  end-to-end (6-RID matrix, trusted publishing, verdict + findings that generalized).
- `references/dotnet-dependency-upgrade-notes.md` — upgrading a .NET tool's (read when upgrading tool dependencies)
  dependencies to current majors (xunit v3, Octokit 14): NuGet latest-version
  lookup, `dotnet fsi` API probing, per-package deltas, macOS case-only git mv.
- `references/stable-release-version-bump.md` — worked prerelease→stable bump: (read when bumping prerelease→stable)
  version-location map, beta audit + hit classification, contract-test shape,
  Shouldly overload trap, post-merge verification sequence.
- `references/global-tool-content-assets.md` — the 1.0.4 case: gitignored (read when bundled assets ship nothing)
  pack globs shipping nothing on fresh runners, installed .store layout, native-shim
  lsof diagnosis, manual provisioning without reinstall, live MCP verification.
- `dotnet-system-commandline` — Cocona/System.CommandLine error handling and exit codes.
- `references/package-id-migration.md` — migrating an installed tool's package id (tool command name, store paths, fresh-install impact); read when migrating an installed tool's package id.
- `references/release-tags-version-content.md` — release tags vs version content: which tags carry which versions, and the version-content contract; read when release tags disagree with version content.
