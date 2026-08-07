# NuGet package id migration — worked example (the project `<pkg-id>` -> `the project`, 2026-08-06)

Task record in-repo: `docs/work/<plan>`. This file
captures the durable facts + the review checklist that generalized.

## Why a migration instead of dual-publishing (measured)

`dotnet tool install` refuses a SECOND shim for the same command name in the same
tool-path:

```
$ dotnet tool install --tool-path /tmp/shims --add-source /tmp/feed the project --version 1.0.8
  (with <pkg-id> 1.0.7 already installed in the same tool-path)
Tool 'the project' failed to update due to the following:
Failed to create shell shim for tool 'the project': Command 'the project'
conflicts with an existing command from another tool.
```

Both packages install `ToolCommandName=the project`, so users could never hold both
ids side by side. Migration = `dotnet tool uninstall -g <old-id>` THEN
`dotnet tool install -g <new-id>` (order matters). The old id keeps working for
existing installs; it is deprecated (message + alternate-package link) on
nuget.org — NuGet does not delete packages.

## Ownerless reserved namespace: 409s are prefix-wide

A prefix reservation with no owner rejects EVERY push under the prefix — the raw
id AND each `id.<rid>` payload (measured: 12/12 pushes across 3 versions x 7 ids).
The dotnet CLI masks any 409 as "already exists at feed". Owner assignment
(contact support@nuget.org) unblocks the id and its payloads together. Trusted
Publishing needs no policy change: it is keyed to owner + repo + workflow file
name, NOT package id.

## What a migration PR must touch (checklist verified against a clean review)

1. csproj: `PackageId` + `PackageVersion` + `InformationalVersion` + `AssemblyVersion`.
2. `.mcp/server.json`: top-level `version`, `packages[0].version`, `packages[0].identifier`.
3. VersionContractTests: `ExpectedVersion` const AND the id-pinning fact
   (PackageId == server.json identifier == new id; ToolCommandName unchanged).
   TDD: test-only RED commit first — verify the RED fails behaviorally at that
   commit (the id/version-pinning facts fail, unrelated facts stay green).
4. publish.yml: patch-tool-shell.py target path `artifacts/<new-id>.$VER.nupkg`
   and the shell/payload find patterns (`<id>.[0-9]*.nupkg` = shell, digit after
   prefix; `<id>.<rid>.<version>.nupkg` = payload). A `DeployToLocalSource`
   target using `$(PackageId)` needs NO change.
5. scripts: verify-tool-package.sh nupkg name (`$TMP/<new-id>.$VER.nupkg`);
   manual-fresh-install-test.py install id + VERSION default + docstring pin.
6. READMEs: install line + a migration note (uninstall old THEN install new;
   data root is keyed to install scope, not package id — `~/.<app>`
   survives). Old-id mentions in migration notes and the task record are
   intentional; historical records (state.json, task-tracking, old work docs)
   stay as-is.
7. **Sweep tracked scaffold mirrors**: `.ai-badger/skills/*` and
   `.claude/skills/*` (symlinks to the former) can carry a stale live
   instruction like `dotnet tool update -g <old-id>` — a real missed surface
   even when src/scripts/.github are clean. A stale `update -g <old-id>` is a
   migration bug, not a doc nit: the old id has no package at the new version,
   so it strands users on the last old-id release.

## Verification of the migration record's claims (review technique)

Evidence-first record claims to re-derive cheaply: the "12/12 pushes" figure
lives in `.ai-badger/state.json` (bump task summary); the data-root path claim
is a `file:line` citation (DefaultOptions.cs:10); the shim-conflict error is
re-measurable only by packing both ids into one tool-path (documented verbatim
in the record instead). Check cited task records exist
(`.ai-badger/task-tracking/executed-tasks.json`) before accepting them.
