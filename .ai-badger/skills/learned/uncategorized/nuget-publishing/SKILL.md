---
name: nuget-publishing
description: >-
  Use when publishing .NET packages to NuGet.
---

# NuGet publishing via GitHub Actions

Release engineering for .NET packages/tools: Trusted Publishing (no API keys),
the environment gate, and the workflow triggers that survive both the NuGet OIDC
policy and GitHub environment protection rules. Verified 2026-08-05 on the
dotnet-ignore tool (three failed publish runs diagnosed end to end).

## Trusted Publishing essentials

- Policy is created on nuget.org: Account -> Trusted Publishing. Fields: Package
  Owner, Repository Owner, Repository, **Workflow File** (file name only, e.g.
  `publish.yml`), **Environment (optional)**.
- The workflow requests a short-lived key: `NuGet/login@v1` with `user:` =
  the nuget.org **profile name of the policy creator** (not the owner, not an
  email). Outputs `NUGET_API_KEY`; push with
  `dotnet nuget push ./artifacts/*.nupkg --api-key ${{ steps.login.outputs.NUGET_API_KEY }} --source https://api.nuget.org/v3/index.json`.
- Job needs `permissions: id-token: write` or the OIDC request silently fails.

## The environment-matching rule (the #1 failure mode)

The GitHub Actions **environment name carried in the OIDC token must EXACTLY
equal the policy's Environment field**. Any mismatch fails at the token exchange:

```
Token exchange failed (HTTP 401) ... Environment mismatch for policy 'Dotnet Ignore':
expected 'production', actual 'publish'
```

- The error message names both sides — read it as the ground truth; do not trust
  what the user thinks the policy says. When user intent and the live 401 disagree,
  the 401 wins (verified: user believed policy said `publish`; policy page and
  three live 401s said `production`).
- The policy page (nuget.org -> Trusted Publishing -> policy) is the authoritative
  source: it shows `Workflow: publish.yml Environment: production`.
- `NuGet/login@v1` also fails if `user:` is wrong ("use the username of the policy
  creator, not the policy owner").

## Approval gate: GitHub environment required reviewers

- `environment: <name>` on the job + a **required reviewer** rule on that
  environment pauses the run before ANY step; the run shows "Review deployments" ->
  "Approve and deploy" (reject fails the run). This is the standard manual gate.
- `required_reviewers` is **UI-only**: the REST API returns 404 for
  POST .../environments/{name}/protection-rules with type required_reviewers
  (verified twice). wait_timer/branch_policy rules are API-manageable; required
  reviewers are not. Tell the user: Settings -> Environments -> <name> ->
  Required reviewers.
- "Prevent self-approvals" on the environment blocks a sole owner from approving
  their own runs — leave it off for single-owner repos.

## Trigger choice: push to trunk, not pull_request (branch-policy trap)

A `pull_request`-triggered run executes from the PR **merge ref**
(`refs/pull/<n>/merge`). If the target environment has a branch policy, the run
is rejected:

```
Branch "refs/pull/15/merge" is not allowed to deploy to production due to
environment protection rules.
```

Fix: trigger on the trunk push — a merge to master IS a push, and the run's ref
is `refs/heads/master`, which branch policies allow:

```yaml
on:
  push:
    branches: [master]
  workflow_dispatch:
```

Consequence: every push to trunk creates a pending run; the approval gate is the
release control (approve, or ignore/cancel). Manual `workflow_dispatch` runs from
the default branch ref and also passes branch policies.

## PackAsTool: build before pack (MSB3030)

`dotnet pack -c Release` on a `PackAsTool` project **fails on a clean checkout**
with MSB3030 ("Could not copy the file bin/Release/net10.0/... because it was not
found") — the implicit build inside pack does not produce the publish-stage
outputs its own publish pass expects. Reproduced on a fresh clone; the workflow
must be:

```yaml
- name: Build
  run: dotnet build src/<Proj>.csproj -c Release
- name: Pack
  run: dotnet pack src/<Proj>.csproj -c Release --no-build -o ./artifacts
```

## Green publish run that published nothing (409 on every push)

With `--skip-duplicate` on the push, EVERY 409 conflict becomes a no-op and the run
still concludes **success** — a green run proves nothing was pushed. The push-step
lines are the truth: `PUT ... 201 Created` = published, `Conflict ... already exists` =
skipped. Always read them before telling anyone the release is live.

Diagnosis when every push 409s for a version the read APIs can't see (hit 2026-08-05,
ai-raccoon 1.0.0; full ladder in `references/push-409-invisible-package.md`):

1. **Read-API sweep** — flat container `https://api.nuget.org/v3-flatcontainer/<id>/index.json`
   (404 = no versions AT ALL, listed or unlisted), registration
   `.../v3/registration5-gz-semver2/<id>/index.json` (XML BlobNotFound), search
   `https://azuresearch-usnc.nuget.org/query?q=packageid:<id>&prerelease=true` (totalHits 0),
   gallery page 404. All four invisible = the id exists nowhere public.
2. **Control the queries** — query a known-live package owned by the SAME account
   (e.g. `dotnet-ignore`). If it shows up in all four, your queries are sound AND the
   account's OIDC publishing mechanism works — the problem is specific to this id/version.
3. **Check earlier runs** — `gh run list --workflow publish.yml` + `gh run view <n> --log |
   grep -E "Pushing|PUT http|Conflict|Created"`. An older run that ALSO all-conflicted on
   an earlier version means the block predates the current bump; the login step saying
   "Successfully exchanged OIDC token" rules out a policy problem.
4. **Official docs rule out "deleted"** — nuget.org does NOT support permanent deletion,
   only unlisting, and unlisted versions STAY in the flat container.
5. **Definitive cause when EVERY push 409s (all versions, all sub-ids) and the id is
   invisible in every read API: RESERVED NAMESPACE (prefix reservation) owned by someone
   else.** Proven from the gallery's own source (NuGetGallery
   `src/NuGetGallery/Controllers/ApiController.cs`, `GetHttpResultFromFailedApiScopeEvaluationForPush`):
   `ReservedNamespaceFailure` / `OwnerlessReservedNamespaceFailure` return **409 Conflict**
   with the body "This package ID has been reserved..." — and the dotnet CLI renders ANY
   409 as "already exists at feed", hiding the real message. The check is per-ID (every
   version conflicts), prefix matching covers sub-IDs (a reservation on `foo` blocks the
   `foo.win-x64` RID payloads), and auth failures return 401/403 — so a 409 with a valid
   OIDC key IS the namespace check, not a policy problem. MEASURED: the 1.0.1
   "fresh version" bump FALSIFIED the duplicate/blocked-version theory — 12/12 pushes
   still 409'd for a version that had never been pushed.
6. **Confirm + fix**: the web Upload page (nuget.org -> Upload, drag the nupkg in) shows
   the reservation message verbatim — fastest confirmation, do it before any support
   ticket. Then: ownerless reservation -> email support@nuget.org; owned reservation ->
   request access from the prefix owner; or rename `PackageId` (`ToolCommandName` is
   independent — the installed command name does NOT change when the package id does).
   Never rely on a fresh-version bump to clear a persistent 409.

## PackageId bridges and command-name shims

- **Shim rule:** a global tool installs a shim named after `ToolCommandName`.
  `dotnet <cmd>` dispatches to a `dotnet-<cmd>` shim on PATH — so `dotnet ai-raccoon`
  only works if `ToolCommandName` is `dotnet-ai-raccoon`, and bare `ai-raccoon` only
  works if it is `ai-raccoon`. The two forms are mutually exclusive. READMEs must show
  the RUN command (`ai-raccoon`) separately from the INSTALL id
  (`dotnet tool install -g <PackageId>`); a user's `dotnet <cmd>` failure is this
  rule, not a broken install.
- **Id bridge (blocked-id escape hatch):** when the package id is blocked (reserved
  namespace, ownership dispute), rename `PackageId` ONLY — `ToolCommandName` keeps the
  installed command identical. Versions are per-id, so the bridge id and the real id
  can carry the same version numbers without conflict, and flipping back later is just
  a `PackageId` edit + deprecating the bridge id on nuget.org with the alternate-package
  pointer set to the real id. Users migrate via uninstall/reinstall — `dotnet tool
  update` follows the installed id and does NOT cross ids, so the flip-back cost grows
  with the user base (zero before first publish).
- **Pin the id contract:** extend the version-contract test to assert
  `PackageId == .mcp/server.json packages[0].identifier == <bridge id>` and
  `ToolCommandName == <real command>` (TDD: add the fact first, watch it fail on the
  old id, then rename).
- **Local smoke install** without nuget.org: copy the packed shell + host-RID payload
  nupkgs into the gitignored `.nupkg-local/` feed, then
  `dotnet tool update -g <PackageId> --add-source ./.nupkg-local --version <v>` (use
  `update`, not `install`, when a previous version is installed globally).

## The .mcp/server.json registry schema (MCP server tools)

The `.mcp/server.json` packed into the tool is validated by MCP registry tooling —
VS Code's MCP config generator rejects an invalid file with "the server.json file
is invalid" and points you at the README, which sends you chasing the wrong thing.
Validate BEFORE publishing against the official schema:
`https://static.modelcontextprotocol.io/schemas/2025-10-17/server.schema.json`
(any jsonschema validator — pip `jsonschema`; the schema is a `$ref` to
`#/definitions/ServerDetail`). Constraint landmines, all hit on ai-raccoon 1.0.1
(2026-08-05; full case in `references/mcp-server-json-schema.md`):

- `description` maxLength 100 — a project blurb blows this instantly (152 chars
  in the real case). Keep <= 100.
- `packages[].environmentVariables` items must be OBJECTS (KeyValueInput:
  required `name`, optional `description`, `isSecret`, `isRequired`, `choices`,
  `default` — the field names are `isSecret`/`isRequired`, NOT `secret`/`required`).
  Bare strings are invalid (the validator reports "is not of type 'object'").
- `repository.url` must be the real repo URL (a wrong-org typo like
  `github.com/ai-raccoon/ai-raccoon` sailed through format-uri validation but
  misleads security inspection); `repository.id` (GitHub repo id via
  `gh api repos/<owner>/<repo> --jq '.id'`) is recommended by the schema.
- Collect ALL violations in one pass: `jsonschema.Draft7Validator(schema).iter_errors(doc)`
  — the first error is rarely the only one (description was error #1, the env-var
  shape errors #2-4).
- Pin the constraints in the version-contract test (description length, env-var
  object shape, repo url) so the registry gate is a test, not a post-publish surprise.
- A burned version (already registered, invalid server.json inside) cannot be
  replaced — nuget.org forbids re-upload of an existing id+version. Keep it
  unlisted and ship the fix as the next version; CI pushes are listed by default,
  so the fixed version becomes the findable one.

## Green publish run but install fails: the stale NuGet http-cache

Sibling of the 409 case: the push SUCCEEDS (log shows `PUT ... Created` / "Your package
was pushed" for every nupkg; flat container + registration + search all show the new
version), yet `dotnet tool install --version <new>` fails:

```
Version 1.0.8 of package arasz.ai-raccoon is not found in NuGet feeds https://api.nuget.org/v3/index.json;<local-feed>.
```

Root cause: the user-level NuGet **http-cache** (`~/.local/share/NuGet/http-cache/`)
holds a stale registration index. dotnet resolves tool versions via the registration
endpoint, and the cache serves yesterday's copy even though the live endpoints have the
new version. Verified 2026-08-06 (ai-raccoon 1.0.8: push log 201 Created x7; flatcontainer,
semver1 + semver2 registrations all showed 1.0.8; install still failed).

Diagnosis ladder (don't chase the package — check the cache first):

1. **Confirm the publish actually landed**: read the push-step lines
   (`gh run view <n> --log | grep -E "Pushing|PUT http|Created"`). Green run + 201s =
   the blob exists.
2. **Verify live endpoints**: flatcontainer
   `https://api.nuget.org/v3-flatcontainer/<id>/index.json` shows the version (HTTP 200 on
   the nupkg URL). If BOTH the blob and registration show it, the failure is client-side cache.
3. **Clear the http-cache — the RIGHT path is `http-cache`, not `v3-cache`**:
   `rm -rf ~/.local/share/NuGet/http-cache` (the `v3-cache` dir does not exist / is not the
   cache — clearing it changes nothing; verified). Retry the install.
4. **RID-payload lag variant**: the SHELL package (`<id>` versionless) may resolve while a
   RID payload (`<id>.osx-arm64` etc.) still fails — the RID package's registration lags
   the blob (flatcontainer 200, registration still at the old version). Same cure: wait for
   indexing OR clear the cache and retry; the error message names the exact package that
   failed, so read it to know which side lagged.
5. **Make the manual install test immune**: a fresh-install verification script should clear
   `~/.local/share/NuGet/http-cache` in its isolation preamble (before the `dotnet tool
   install`), or a brand-new version false-FAILs on yesterday's cache. Also bump the script's
   default `VERSION` pin in the same PR as the release bump — a stale pin silently tests the
   OLD version (serverInfo will show it).

## Version bumps: one contract, many sites

The publish workflow packs whatever the csproj carries, so a release is: bump → merge →
dispatch → approve gate. The version lives in MORE than one place on a dotnet-tool repo:
`PackageVersion`, `InformationalVersion`, `AssemblyVersion` (numeric-only — MCP
`serverInfo.version` reads it), and `.mcp/server.json` (both the server `version` and the
`packages[0].version` fields — the file ships INSIDE the tool package). Pin all of them
with a TDD version-contract test that reads the csproj + server.json from the repo root
(walk up from `AppContext.BaseDirectory`), asserts one expected version and NO prerelease
suffix, and forbids `-` in every declared version. RED against the old version → bump all
sites → GREEN. It makes the "remove beta / bump version" task provable instead of
grep-able, and it catches a site that was missed.

## Release-flow conventions (this user, dotnet-ignore)

- "master is implicitly latest": NO label/condition on the publish trigger —
  every merge to master is a release candidate; the approval click is the control.
- The release PR bumps `<Version>` in the csproj AND adds a CHANGELOG entry.
- Use official GitHub actions over hand-rolled `github-script` workflows (the
  user replaced a custom /latest comment-command workflow with actions/labeler@v7).
- Everything goes through a PR to master; the user merges immediately.

## Verification of workflow changes

- `actionlint .github/workflows/*.yml` — the workflow linter (installed at
  /opt/homebrew/bin/actionlint); catches YAML structure, action versions, shell
  issues (shellcheck) in `run:` steps.
- pyyaml parses YAML 1.1, so `on:` in workflow files loads as boolean `True` —
  read triggers via `d.get('on') or d.get(True)`.
- After any workflow edit: verify with a focused temp script under $TMPDIR
  (hermes-verify-*.sh): actionlint + YAML-shape assertions + the C# suite as a
  guard, then delete it.

## References

- `references/multirid-tool-shell.md` — multi-RID dotnet tool shells: the per-RID pack race
  that breaks every other platform, the patch-the-shell fix (patch-tool-shell.py pattern,
  self-gating), nuget immutability forcing version bumps, the pre-merge-dispatch trap that
  burns the fix version too, payloads-first push ordering, and the local-feed end-to-end
  proof recipe.
- `references/trusted-publishing-oidc.md` — the full 2026-08-05 debugging saga:
  exact 401s, policy fields, environment rules API behavior, and related workflow
  lessons (actions/labeler v7 config format, pull_request_target narrowing).
- `references/push-409-invisible-package.md` — reserved-namespace 409 diagnosis
  ladder (read-API sweep, control experiment, gallery-source proof, fix paths).
- `references/mcp-server-json-schema.md` — the .mcp/server.json registry-schema
  validation case: exact violations, schema field shapes, validation recipe.
