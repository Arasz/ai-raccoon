# Multi-RID dotnet tool shell: the race that breaks every other platform (2026-08-05, ai-raccoon)

A PackAsTool global tool with per-RID payloads publishes: shell nupkg (`<PackageId>.<ver>.nupkg`,
type DotnetTool) + one payload per RID (`<PackageId>.<rid>.<ver>.nupkg`, type
DotnetToolRidPackage). The shell's `tools/net10.0/any/DotnetToolSettings.xml` is the INSTALL
contract: `dotnet tool install` reads `<RuntimeIdentifierPackages>` and refuses any platform
whose RID is missing.

## Failure signature

`dotnet tool install -g <id>` works on ONE platform (whichever RID the shell lists) and fails
on all others with an error like "The package is incompatible with the current operating
system" / "no version of package ... is compatible" — while the payload packages for those
platforms ARE on nuget. Green publish run in the log. The tool looks fine from the workflow.

## Root cause (measured)

A publish matrix of parallel jobs each running `dotnet pack -p:RuntimeIdentifiers=<rid>`
emits the SAME shell nupkg name in every job. `--skip-duplicate` on the push keeps whichever
shell arrived first; the SDK writes ONLY that job's RID into the shell's
RuntimeIdentifierPackages. Whichever job won the race decides who can install. Hit on
ai-raccoon 1.0.1 (owner hand-packed the same way) AND 1.0.2 (CI race; linux-musl-x64 job won
— every Mac/Windows/Linux-glibc install rejected).

Verified shell shapes (ground truth from nuget flatcontainer):
- Broken shell: `DotNetCliTool Version="2"` + `<Command Name="ai-raccoon" />` + exactly ONE
  `<RuntimeIdentifierPackage RuntimeIdentifier="linux-musl-x64" Id="<id>.linux-musl-x64" />`.
- Payload package: `DotNetCliTool Version="2"` + `<Command Name="..." EntryPoint="AiRaccoon"
  Runner="executable" />`, NO RuntimeIdentifierPackages block.
- Framework-dependent single-package tool (no payloads; e.g. dotnet-ignore):
  `DotNetCliTool Version="1"` + EntryPoint + `Runner="dotnet"`, no RID block at all —
  installs anywhere with the runtime, but is NOT self-contained.

## Fix pattern (chosen; proven end-to-end)

Keep the per-RID payload packs; patch EVERY job's shell to list ALL RIDs before upload.
Then all six shells are byte-identical and `--skip-duplicate` keeps a correct one no matter
who wins — the race becomes harmless. A ~70-line python script (`scripts/patch-tool-shell.py`
in ai-raccoon) does it:

- open the nupkg (zipfile); find the single `tools/*/DotnetToolSettings.xml`;
- parse: assert exactly one `<RuntimeIdentifierPackage ...>` (anything else = wrong input —
  this also fails loudly on double-patch, an idempotency guard);
- derive the id prefix from the existing entry (`Id` minus `.`+rid);
- rewrite the zip with the template (Version 2, Command Name, one entry per requested rid);
- self-gate: re-read and assert every requested rid appears exactly once.

Workflow step (version-agnostic — never hardcode the version, the csproj carries it):

```yaml
- name: Patch the tool shell to reference every RID
  run: |
    VER=$(python3 -c 'import re;m=re.search(r"<PackageVersion>([^<]+)</PackageVersion>",open("src/<Proj>.csproj").read());print(m.group(1))')
    python3 scripts/patch-tool-shell.py artifacts/<Id>.$VER.nupkg win-x64 win-arm64 osx-arm64 linux-x64 linux-arm64 linux-musl-x64
```

Portability: `grep -oP` is GNU-only (fails on macOS BSD grep) — use the python3 extraction.
Never pass a RID list through `-p:RuntimeIdentifiers=a;b` on the dotnet CLI: the CLI splits
`-p:` values on `;` even when quoted ("Switch: b" / MSB1006), and `%3B`-escaped semicolons
decode into values that break downstream scalar consumers (OutputPath → MSB4115
HasTrailingSlash / NETSDK1083 on SDK 10.0.302). The SDK's `ToolPackageRuntimeIdentifiers`
multi-RID single-pack exists in the targets (Microsoft.NET.PackTool.targets,
`CreateRidSpecificToolPackages`) but is not reachable cleanly from the CLI — don't fight it.

Also: script annotations must stay Python 3.9-compatible — `str | None` in a signature raises
TypeError AT IMPORT on the 3.9 CommandLineTools python (fine on CI's 3.12). Use
`Optional[str]`. Same gotcha killed the local verification run before CI ever saw it.

### Push ordering: payloads before shells

`dotnet nuget push` pushes sequentially; the shell name sorts before its payloads in a glob,
so the shell can be live while its payloads are not — `dotnet tool install` fails in that
transient window. Push payloads first, then shells. Shell vs payload glob (names are
`<Id>.<ver>.nupkg` vs `<Id>.<rid>.<ver>.nupkg`): shells have a DIGIT right after the package
prefix (`arasz.ai-raccoon.[0-9]*.nupkg` with find -name), payloads start with a letter.

### Hardening the patch script (post-review items, all cheap)

- Validate the input Id shape: `Id` must end with `.` + rid (a malformed Id would silently
  rewrite to garbage with a green gate).
- RID-count guard: assert `len(rids) == <matrix count>` — a RID added to the matrix but not
  to the script args would otherwise ship a shell that silently excludes a platform.
- Preserve `EntryPoint`/`Runner` attributes if the Command line carries them (the packed
  shell shape doesn't; the intermediate SDK shape does).
- Gate entry preservation: `[Content_Types].xml` byte-identity + entry-list stability.
- The gate reading the exact bytes that get uploaded is what makes the race harmless —
  keep every assertion on the post-rewrite file, not on the inputs.

## The fix version can be burned too: dispatch runs from the default branch

`workflow_dispatch` runs the workflow from the DEFAULT branch, not from the fix PR's branch.
If the user dispatches publish.yml BEFORE the fix PR merges, the "new" version ships with the
OLD broken shell — and nuget immutability burns it (hit for real: 1.0.3 was dispatched from
main minutes before the fix PR merged; the shell was linux-musl-x64-only again). Recovery:

1. Check the PR state FIRST (`gh pr view <n> --json state,isDraft`) before pushing follow-up
   commits — this user merges immediately and the branch dies at merge; commits pushed after
   the merge land on a closed PR's branch and never reach main.
2. Cut a NEW branch from `origin/main` (after `git fetch`), cherry-pick the follow-up work,
   bump to X+2 (same TDD: test pin → RED → bump all version sites → GREEN), push, new PR.
3. The user deletes the burned version on nuget.org (manage page) — possible while it is
   still "Validating" with 0 downloads; once Listed it needs unlist-then-delete.

## Verifying a fresh push

A just-pushed version shows status "Validating" on the gallery for minutes; the flatcontainer
returns 404 (a 215-byte error page — check the file SIZE before trusting a download) until
validation completes. You cannot inspect the shell bytes in that window — ask the pusher
which mechanism they used (workflow dispatch from main vs web upload vs fixed branch) and
verify once validation lands.

## nuget immutability forces a version bump

A broken shell at version X can never be re-pushed (id+version unique). The fix MUST ship as
X+1: bump every version site (PackageVersion/InformationalVersion/AssemblyVersion in the
csproj + both `version` fields in .mcp/server.json) and the version-contract test pin — TDD:
pin the new version in the test first (RED), bump the code (GREEN). Optionally unlist the
broken version on nuget.org afterwards.

## End-to-end proof BEFORE the PR (do this every time)

1. `dotnet pack -p:RuntimeIdentifiers=<any-rid>` → fresh shell; patch it with the script.
2. Pack the HOST payload locally too (`dotnet pack -p:RuntimeIdentifiers=osx-arm64`) — the
   new version does not exist on nuget yet (a flatcontainer curl returns a 404 error page;
   check `ls -la` size before trusting a download).
3. Local-only feed: nuget.config with `<clear/>` + the feed dir; copy patched shell + host
   payload in.
4. `dotnet tool install --tool-path /tmp/tooltest <id> --version <v> --configfile <feed>`
   (NEVER combine `-g` with `--tool-path` — the CLI rejects the pair) and run the tool.
5. Negative control: the same install with the UNPATCHED shell fails exactly like the user
   report — proves the shell was the bug.

## Verify-with-script gotcha

When counting `RuntimeIdentifierPackage` in the settings XML, count the self-closing ENTRY
tags (`RuntimeIdentifier=` occurrences), not the bare substring — the `<RuntimeIdentifierPackages>`
open/close tags inflate the count by 2.
