---
name: dotnet-tool-publishing
description: Use when packaging or publishing a .NET CLI tool to NuGet.
---

# Publishing .NET CLI tools (PackAsTool -> NuGet)

Use when: packing a `PackAsTool` dotnet tool, pushing it to NuGet (incl. Trusted
Publishing), building the GitHub Actions publish workflow, or version-bumping a
published tool. Reference implementation: `dotnet-ignore`
(github.com/Arasz/dotnet-ignore, Cocona 2.2.0), published 1.1.0 via Trusted
Publishing 2026-08-05. Verified against the official NuGet Trusted Publishing
docs the same day.

## The pack trap: MSB3030 on clean checkouts (verified 2026-08-05)

`dotnet pack` on a PackAsTool project FAILS on a clean checkout:

    Microsoft.NET.Publish.targets(372,5): error MSB3030: Could not copy the file
    ".../bin/Release/net10.0/dotnet-ignore.deps.json" because it was not found

The publish pass inside pack expects bin/Release outputs the implicit build does
not produce. Reproduced: fresh clone + `dotnet pack -c Release` alone -> 4x MSB3030.
A local worktree that already ran `dotnet build -c Release` masks the bug (pack
then succeeds), which is why it only explodes in CI. dotnet-ignore's first
publish dispatch failed exactly this way.

Fix — always build first, then pack with --no-build:

    dotnet build src/CliTool/CliTool.csproj -c Release
    dotnet pack  src/CliTool/CliTool.csproj -c Release --no-build -o ./artifacts

In GitHub Actions publish.yml the Build step MUST precede Pack. Reproduce the
failure on a clean clone (`git clone --depth 1`) before/after fixing — never
trust a worktree that has Release artifacts lying around.

**EXCEPTION — Web-SDK (`Microsoft.NET.Sdk.Web`) multi-RID tools: the advice
inverts (measured 2026-08-05 on ai-raccoon).** For a project with
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
CI). Verified 2026-08-05 on ai-raccoon — the workflow author hit this after the first
dispatch and fixed it in a follow-up commit.

## csproj essentials for a tool package

- `<PackAsTool>true</PackAsTool>` + `<ToolCommandName>dotnet-ignore</ToolCommandName>`.
  **The ToolCommandName dispatch rule (verified 2026-08-05):** the shim a global
  tool install creates is named exactly `ToolCommandName`, and `dotnet <command>`
  resolves by looking for a shim literally named `dotnet-<command>` on PATH.
  So `ToolCommandName=ignore` creates a shim `ignore` — bare `ignore list` works
  but the README-documented `dotnet ignore list` fails with "a dotnet-prefixed
  executable with this name could not be found on the PATH" (`dotnet mcp` works
  only because its shim is `dotnet-mcp`). Make ToolCommandName match the
  documented invocation: `dotnet-ignore` (matches AssemblyName + package id).
  dotnet-ignore shipped 1.1.0 with the broken `ignore` value; the 1.2.0 fix
  renamed it and `dotnet ignore` worked — verify via pack -> install to a
  tool-path -> `dotnet ignore list`, not from a pre-existing global install
  (the old shim lingers there).
- **The packed README must document the PATH requirement** (user-driven lesson
  2026-08-05, PR #18 on dotnet-ignore): `dotnet <tool> <subcommand>` dispatches
  to a shim in `~/.dotnet/tools` (macOS/Linux) / `%USERPROFILE%\.dotnet\tools`
  (Windows), which must be on PATH. A README that jumps from
  `dotnet tool install -g <pkg>` straight to usage makes the FIRST command a
  new user runs fail with "dotnet-prefixed executable could not be found" —
  the exact trap that surfaced this bug. Since the packed README is what the
  nuget.org readme tab shows, ship the install dir + `export PATH="$PATH:$HOME/.dotnet/tools"`
  in the Installation section (mirrors the ai-raccoon tool README).
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

### The full fresh-install gate (MCP tools with bundled assets) — verified 2026-08-06 on arasz.ai-raccoon 1.0.6

The smoke gate above proves the tool RUNS; it does NOT prove a clean install works first
try with all assets present. For tools that ship bundled models/native libs and speak MCP
over stdio, run the full protocol (executed as `scripts/manual-fresh-install-test.py` in
ai-raccoon; full detail: `references/fresh-install-verification.md`):

- **Isolate everything.** `dotnet tool install --tool-path $TOOLPATH --version <v>` (never
  `-g` — the user's real install stays untouched), fresh `NUGET_PACKAGES=<dir>` so the
  install genuinely fetches from nuget.org instead of the local package cache,
  `--data-root $DATAROOT` (fresh dir) to bypass the tool's default data dir, and
  `unset AIRACCOON_DB_PASSPHRASE` — an inherited secret env var silently changes the tested
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
  (`AI_RACCOON_VERSION`) so the gate re-runs against the next release without editing the
  script.

## Multi-RID tool packages: matrix pack, push BOTH packages

`dotnet pack -p:RuntimeIdentifiers=<rid>` on a PackAsTool project emits TWO nupkgs per run:
the tool shell (`<id>.<version>.nupkg`) and the RID payload
(`<id>.<version>.<rid>.nupkg`). `dotnet tool install` needs both on the feed — pushing only
the shell leaves the RID payload missing and installs fail. Verified 2026-08-05 on
ai-raccoon (6-RID tool, PR #12 review).

GitHub Actions pattern:
- One pack job per RID (`rid: [win-x64, win-arm64, osx-arm64, linux-x64, linux-arm64,
  linux-musl-x64]`), pack with `-p:RuntimeIdentifiers=${{ matrix.rid }}` — the command line
  overrides the project's multi-RID `RuntimeIdentifiers` property per job. Upload each
  job's `artifacts/*.nupkg` as a named artifact.
- For a Web-SDK project the pack job is ONE step — `dotnet pack ... -p:RuntimeIdentifiers=<rid>
  -o artifacts` with NO separate build step and NO `--no-build` (see the MSB3030 exception
  above; the build-then-pack matrix form fails on clean trees for that SDK shape).
- ONE non-matrix push job (`needs: pack` waits for all matrix legs). Every leg produces the
  same shell id+version, so the download contains N copies of it. `--skip-duplicate` is
  REQUIRED, not an optimization: nuget.org dedupes by id+version and rejects a
  differing-content push with 409 — without it legs 2..N fail the run.
- Push glob: `dotnet nuget push "artifacts/**/*.nupkg"` — download-artifact v4+ extracts
  each artifact into `artifacts/<artifact-name>/`, and NuGet's push glob engine supports
  `**`.
- Packing `win-arm64` / `osx-arm64` from a linux runner is fine: pack resolves runtime
  packs from NuGet; it does not cross-compile.

### The shell race: a per-RID matrix publishes a shell that references ONE RID (verified 2026-08-05, ai-raccoon)

The matrix pattern above has a hidden race. **Every matrix job emits its OWN shell
package with the same id+version** — and each shell's `DotnetToolSettings.xml` lists
ONLY the RID that job packed (`<RuntimeIdentifierPackage RuntimeIdentifier="<rid>"
Id="<id>.<rid>" />`). The push job then glob-pushes N copies of the shell; nuget.org
dedupes by id+version and keeps whichever arrived FIRST. The surviving shell references
one RID — typically the first matrix leg alphabetically (or whichever job won the race) —
so `dotnet tool install` FAILS on every other platform even though every RID payload
package exists on the feed.

Measured on `arasz.ai-raccoon` 1.0.2: the published shell's `DotnetToolSettings.xml`
references only `linux-musl-x64`; `arasz.ai-raccoon.osx-arm64` 1.0.2 exists on nuget.org
but macOS install dies with "The tool does not support the current architecture or
operating system (osx-arm64). Supported runtimes: linux-musl-x64". The old pre-bridge
package id was deleted from nuget, so there was no working fallback to reinstall.

**Fix — make every job's shell identical and correct:** (a) "pack the shell once with
the full RID list" is a DEAD END — measured on SDK 10.0.302 2026-08-05: the dotnet CLI
splits `-p:` values on `;` into separate switches even when quoted ("Switch:
linux-musl-x64"), the `%3B` escape decodes but then OutputPath evaluation breaks
(MSB4115 HasTrailingSlash non-scalar) / NETSDK1083, and the SDK's own multi-RID
`ToolPackageRuntimeIdentifiers` path hits the same OutputPath wall. There is no
working one-invocation multi-RID shell. Use (b) — the validated fix: **post-process
each job's shell before upload** with a small committed script (ai-raccoon:
`scripts/patch-tool-shell.py`) that rewrites `DotnetToolSettings.xml`'s
`RuntimeIdentifierPackages` to the full RID list. Every matrix job runs it on its own
shell, so all N candidate shells are byte-identical-correct and `--skip-duplicate`
keeps a good one no matter which job wins. The script must be SELF-GATING — fail the
job (exit ≠ 0) unless: input shape is exactly the SDK's single-RID shell (one
RuntimeIdentifierPackage, Command Name present), the id ends with "." + rid, the
rewritten XML lists every requested RID exactly once, the ids match `prefix.rid`, the
entry list and `[Content_Types].xml` are byte-preserved, and the RID count matches the
workflow matrix (a matrix-vs-args drift then fails loudly). A bonus property: the
single-entry assertion makes re-patching an already-patched shell fail loudly
(idempotency guard). Python-side: keep annotations 3.9-compatible (`str | None` fails
at runtime on macOS CommandLineTools python3) — CI is 3.12, local verification often
3.9.

**Immediate install workaround (broken published shell, same machine):** clone the
source, pack locally for the host RID, install from a local folder feed:

```bash
git clone <repo> /tmp/<name>-build && cd /tmp/<name>-build
mkdir -p .nupkg-local   # nuget.config references the gitignored local feed -> NU1301 otherwise
dotnet pack src/<App>/<App>.csproj -c Release -p:RuntimeIdentifiers=<host-rid> -o /tmp/<name>-build/out
unzip -p out/<id>.<ver>.nupkg tools/net10.0/any/DotnetToolSettings.xml  # verify it lists <host-rid>
dotnet tool install -g <id> --version <ver> --source /tmp/<name>-build/out
```

Single `--source` (folder) REPLACES nuget.org so the broken published shell cannot win.
Verify before/after with the same data: `shasum -a 256` the tool's data DB and its entry
count before and after the swap — the version swap must never touch user data.

### After the fix ships — deployment reality (measured 2026-08-05, ai-raccoon 1.0.3/1.0.4)

- **nuget validation lag:** a just-pushed version returns 404 from the v3 flatcontainer
  (shell AND payloads) while the gallery shows "Validating" — sometimes 10+ minutes. A
  404 right after a green push is NOT a failed push; the workflow log ("Pushing ...",
  "already exists at feed" skips) is ground truth. Inspect live bytes only after the
  flatcontainer returns 200.
- **Immutability:** a version whose shell shipped broken can never be replaced — a
  re-push of the same id+version is a `--skip-duplicate` no-op. The fix MUST bump the
  version (contract-test pin first, TDD RED→GREEN). A still-validating / 0-download
  broken version can optionally be deleted from the gallery while the window is open.
- **A dispatch raced against a merge can win either way:** 1.0.3 was dispatched from
  OLD main (pre-fix) seconds before the fix PR merged, yet the LIVE 1.0.3 turned out
  correct — the fixed workflow's push landed first (`--skip-duplicate` keeps the first
  successful push, and its pack jobs finished after the merge). Never conclude a version
  is broken from the dispatch timeline or a clarify answer alone — inspect the live
  shell's `DotnetToolSettings.xml` bytes.
- **Push order matters:** one glob push puts the shell live seconds before its payloads
  (shell filename sorts first) — `dotnet tool install` in that window fails to resolve
  the payload. Push payloads first, then shells: two steps like
  `find artifacts -type f -name '*.nupkg' ! -name 'arasz.ai-raccoon.[0-9]*.nupkg' -print0 | xargs -0 -n1 dotnet nuget push ...`
  (shell = digit right after the package prefix). Verified in production: all 6 payloads
  200 before the shell push, 5 duplicate shells skipped cleanly.
- **Local E2E verification of the fixed flow on a Mac:** pack (one RID) -> run the patch
  script -> local-only feed (`nuget.config` with `<clear/>` + folder source; a plain
  `--source <folder>` still leaves nuget.org resolvable and the broken published shell
  can win version resolution) -> `dotnet tool install --tool-path /tmp/x --version <v>
  --configfile <feed>/nuget.config` -> run the tool. `--tool-path` keeps the global tool
  state untouched; verify the version with the installed tool's `--version`.
- **Local http-cache lag (UPDATING a tool, not publishing):** right after a fix ships,
  `dotnet tool update -g <id> --version X` can fail with `Version X of package <id> is
  not found in NuGet feeds` even though the flatcontainer index AND the
  registration5-semver1 index both list X and the nupkg blob returns HTTP 200 — the
  local NuGet HTTP cache holds a stale registration. Fix: `dotnet nuget locals
  http-cache --clear`, then retry with the explicit `--version X`. May take 1–2
  clear+retry cycles over ~2 minutes (the RID-payload package's registration lags the
  shell's — you can see the shell resolve while `arasz.ai-raccoon.osx-arm64` still
  reports not-found). Also: a tool originally installed from a local folder feed
  (`--source <dir>`) needs the explicit `--version` on update — a bare
  `dotnet tool update -g` answers "already installed" and never moves versions.
- **"The fix is out, update the tool" — confirm WHICH version first (user correction
  2026-08-05):** when a human says a fix was published and to update, do NOT grab the
  first new version the index shows. The racing-dispatch scenario above means the first
  new version can be the OLD code (arasz.ai-raccoon 1.0.3 raced from pre-fix main) while
  the real fix is the NEXT one (1.0.4). Ask which version is the fix (or verify the fix
  commit actually merged before the dispatch) before updating — an eager update to the
  wrong version burns a round-trip and needs a second update once the true fix lands.

Full measured detail — SDK internals, failure transcripts, script shape, deployment
timeline: `references/multirid-shell-race-fix.md`.

## Bundled content assets: gitignored pack globs, store layout, provisioning (verified 2026-08-05, ai-raccoon)

A `PackAsTool` project that ships runtime assets (embedding models, fixtures, native libs)
has a silent failure mode distinct from the MSB3030 trap:

- **A pack-time glob over a GITIGNORED dir matches nothing on a fresh runner.** `<None
  Include="Models/*.onnx" Pack="true" PackagePath="Models/">` packs only files present in
  the checkout; CI checkout has no untracked files, so the glob is empty and the nupkg ships
  WITHOUT the asset — while a COMMITTED sibling (`vocab.txt`) ships fine. The tool installs,
  then fails at runtime with "<asset> not found next to the tool". Verify pack CONTENT, not
  pack success: `unzip -l <shell>.nupkg` (and the RID payload) and diff against the csproj's
  intent.
- **Fix: provision gitignored assets in CI BEFORE pack** — a SHA-pinned, idempotent download
  step (skip-if-present-and-verified, fail loudly on SHA mismatch) between setup-dotnet and
  Pack; note the dependency in the workflow header comment so nobody reorders it.
- **Installed store layout (global tool):** AppContext.BaseDirectory of the RID payload is
  `~/.dotnet/tools/.store/<PackageId>/<ver>/<PackageId>.<rid>/<ver>/tools/net10.0/<rid>/`.
  Content assets land in up to THREE store locations (payload tools dir, payload root, shell
  root). Resolvers that walk UP from BaseDirectory checking `Models/` at each ancestor hit the
  payload tools dir FIRST — that is where manual provisioning must copy.
- **The tool shim is a native Mach-O apphost, not a script** — no dll path inside. For a
  RUNNING tool, `lsof -p <pid> -iTCP -sTCP:LISTEN` reveals the port and the exact store paths
  (`txt REG` lines) plus the process cwd — the cwd also explains cwd-relative asset-resolution
  bugs (a settings value resolved via `Path.GetFullPath` picks up the server's working dir).
- **Manual provisioning without reinstall:** copy the SHA-verified asset next to its committed
  sibling in the first-hit dir. No restart needed when resolution is per-call (filesystem /
  settings read at call time, generator cached by fingerprint) — verified live: a search
  succeeded on the running server immediately after the copy.
- **Runtime self-heal:** production code that can fetch the missing asset at startup
  (`BundledModel.EnsureAsync` pattern: locate-and-verify first, download only when absent,
  NEVER throw — collect errors, warn to stderr, keep booting) turns a broken package into a
  recoverable install; bound the download with a ~30 s linked-cts timeout so slow networks
  cannot stall boot. Fail-fast in the RESOLVER (clear message naming the resolved path and
  both remediations when a configured asset path does not exist) converts cryptic runtime
  errors into actionable ones.

Full measured case (ai-raccoon 1.0.4): `references/global-tool-content-assets.md`.

## NuGet Trusted Publishing (no API keys)

The nuget.org policy (Account -> Trusted Publishing) binds: package owner, repo,
and WORKFLOW FILE NAME — the file name must match `.github/workflows/<file>`
exactly (file name only, e.g. `publish.yml`). If the policy declares an
`environment:`, the workflow must use that environment.

**Environment-mismatch verification rule (verified 2026-08-05, dotnet-ignore):**
the `NuGet/login@v1` failure names the expected environment —
`Token exchange failed (HTTP 401) ... Environment mismatch for policy 'X':
expected 'production', actual 'publish'`. Read the expected name from the live
error or the policy page (which prints "Workflow: publish.yml Environment:
production"), NOT from a user's report that they "fixed the policy" — the fix
may not have stuck (the policy still expected `production` two failed runs after
the owner said it was fixed to `publish`). Re-verify before changing the
workflow's `environment:`.

Workflow requirements:
- `permissions: { id-token: write, contents: read }` — without `id-token: write`
  the OIDC request silently fails and no key is issued.
- `NuGet/login@v1` with `user: <nuget.org username>` (profile name, NOT email;
  it is public — no need to secret it). Exchanges the OIDC token for a temporary
  API key: valid 1 h, single use per token — request it shortly before pushing.
- Push: `dotnet nuget push ./artifacts/*.nupkg --api-key ${{ steps.login.outputs.NUGET_API_KEY }} --source https://api.nuget.org/v3/index.json`
- Triggers: `push: tags: ['v*']` + `workflow_dispatch`.

Additional patterns verified 2026-08-05 (ai-raccoon PR #12):
- **Manual approval gate**: put `environment: production` on the PUSH job only (not the
  pack matrix). With a required-reviewer protection rule on the environment, the job waits
  for an Approve before ANY step runs — approval therefore covers the push. The nuget.org
  policy's declared environment must match the workflow's `environment:` name exactly.
- **Version input for repeatable releases**: a hardcoded `PackageVersion` makes the
  workflow single-shot — the second dispatch pushes the same id+version and
  `--skip-duplicate` no-ops the whole run. Prefer a `workflow_dispatch` input `version`
  (semver-validated) passed as `-p:PackageVersion=${{ inputs.version }}`.
- **Branch-scoped dispatch**: the trusted-publishing identity is repo-scoped, not
  branch-scoped. `workflow_dispatch` has NO `branches` key — actionlint rejects it
  (`expected "inputs" key for "workflow_dispatch" section but got "branches"`), and
  dispatch always runs from the default branch anyway, so a `branches:` restriction
  is both invalid and pointless. If a branch guard is genuinely needed, use a
  first-step `if: github.ref == 'refs/heads/main'`.
- **Tests before push is a judgment call**: acceptable to skip when a human approval gate
  exists and PR CI already gates merges to the release branch; otherwise run the fast
  suite in the pack job.

Working template: `templates/publish.yml`.

## Version bump / stable-release prep

- Bump `<Version>` (semver), update `PackageReleaseNotes`, add a CHANGELOG.md
  entry (Keep a Changelog), refresh the README badge to the GitHub Actions
  workflow: `https://github.com/<owner>/<repo>/actions/workflows/build.yml/badge.svg`.
- Migrating CI off Azure DevOps: the README badge and any in-repo pipeline refs
  are the only in-repo traces; the old pipeline lives in the external DevOps
  project and must be deleted there by the owner.

**The version lives in 4+ places — grep for the OLD string repo-wide, not just the
csproj** (verified 2026-08-05, ai-raccoon 0.1.0-beta → 1.0.0):
- csproj: `PackageVersion` (nupkg id+version), `InformationalVersion` (the `--version`
  flag string), `AssemblyVersion` (MCP `serverInfo.version` reads THIS and must stay
  numeric-only — a `-beta` suffix there breaks the MCP handshake). All three move together.
- `.mcp/server.json` (MCP servers, packed into the tool): the top-level `version` AND
  `packages[].version` BOTH hardcode the version — easy to miss; both must match the
  csproj (found only via the repo-wide grep).
- Comments in tests/scripts that name the version — they go stale silently.

**Prerelease→stable audit: classify every grep hit before editing.** (a) real version
mentions — bump; (b) test fixtures / corpus content / model vocab (`beta.md` filenames,
`"beta content"` test strings, a `vocab.txt` token) — leave, they are data; (c)
historical plan/research docs (`docs/plans/*`, `docs/work/*`) — leave, they are
point-in-time records and rewriting them falsifies history. Acceptance gate: "no
beta/old-version in src/ + tests/" excluding fixtures, proven by the post-change grep.
NuGet semver: a stable `1.0.0` is allowed after a published `0.1.0-beta` (higher
precedence), and an un-bumped re-dispatch of the publish workflow is a
`--skip-duplicate` no-op — the version bump IS the publish trigger.

**Pin the version with a contract test (the TDD vehicle).** A version bump has no
behavioral test, so the RED step is a version-contract test that FAILS on the old
version: walk up from `AppContext.BaseDirectory` to the repo root (same pattern as the
test project's ReferenceAssets), read the csproj + `.mcp/server.json`, assert
PackageVersion = InformationalVersion = AssemblyVersion = the new version, server.json
fields match, and no prerelease suffix (`Contains('-')` is false). Worked shape:
`references/stable-release-version-bump.md`.
**Shouldly trap in such tests:** `actual.ShouldNotContain("-", "msg")` does NOT compile
for strings — the two-arg overload resolves to the `IEnumerable<char>` predicate form
(CS1503). Use `actual.Contains('-').ShouldBeFalse("msg")`.

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

## See also

- `references/web-sdk-multirid-pack-reproduction.md` — measured A/B/C reproduction
  of the MSB3030 trap on Web-SDK multi-RID tools (build-then-pack vs single-pack)
  and the nested-pack recursion guard for AfterTargets local-deploy targets.
- `references/nuget-publish-pr-review.md` — worked example: reviewing a NuGet publish PR
  end-to-end (6-RID matrix, trusted publishing, verdict + findings that generalized).
- `references/dotnet-dependency-upgrade-notes.md` — upgrading a .NET tool's
  dependencies to current majors (xunit v3, Octokit 14): NuGet latest-version
  lookup, `dotnet fsi` API probing, per-package deltas, macOS case-only git mv.
- `references/stable-release-version-bump.md` — worked prerelease→stable bump:
  version-location map, beta audit + hit classification, contract-test shape,
  Shouldly overload trap, post-merge verification sequence.
- `references/global-tool-content-assets.md` — the ai-raccoon 1.0.4 case: gitignored
  pack globs shipping nothing on fresh runners, installed .store layout, native-shim
  lsof diagnosis, manual provisioning without reinstall, live MCP verification.
- `dotnet-cli-parsing` — Cocona/System.CommandLine error handling and exit codes.
