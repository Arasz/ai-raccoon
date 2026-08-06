# Multi-RID tool shell race — measured fix (ai-raccoon 1.0.2 → 1.0.4, 2026-08-05)

## The race (verified on the published packages)

`dotnet pack -p:RuntimeIdentifiers=<rid>` on a PackAsTool project emits per run: the
tool shell `<id>.<ver>.nupkg` and the RID payload `<id>.<rid>.<ver>.nupkg`. The SDK
writes the SHELL's `DotnetToolSettings.xml` with exactly ONE RuntimeIdentifierPackage —
the RID that job packed:

```xml
<DotNetCliTool Version="2">
  <Commands>
    <Command Name="ai-raccoon" />
  </Commands>
  <RuntimeIdentifierPackages>
    <RuntimeIdentifierPackage RuntimeIdentifier="linux-musl-x64" Id="arasz.ai-raccoon.linux-musl-x64" />
  </RuntimeIdentifierPackages>
</DotNetCliTool>
```

A 6-job per-RID matrix therefore emits 6 shells with the same id+version; the push job's
`--skip-duplicate` keeps whichever pushed FIRST. The surviving shell references one RID,
so `dotnet tool install` fails on every other platform — even though every payload
exists. Verified: the published `arasz.ai-raccoon` 1.0.1 AND 1.0.2 shells both
referenced only linux-musl-x64 (1.0.1 was owner-web-uploaded, so the flaw predates CI).

The payload's settings file has a different shape (no RuntimeIdentifierPackages at all):
`<Command Name="ai-raccoon" EntryPoint="AiRaccoon" Runner="executable" />` — this is also
the shell's INTERMEDIATE SDK shape; the final packed shell drops EntryPoint/Runner.

## Why "pack the shell once with the full RID list" is a dead end (SDK 10.0.302, measured)

1. `dotnet pack -p:RuntimeIdentifiers="osx-arm64;linux-musl-x64"` (quoted):
   `Switch: linux-musl-x64` — the dotnet CLI splits `-p:` values on `;` into separate
   switches even when the shell quoting is intact.
2. `-p:RuntimeIdentifiers=osx-arm64%3Blinux-musl-x64` (`%3B` IS the CLI's documented
   escape; the value arrives with a literal `;`): the outer build's OutputPath becomes
   `bin/Release/net10.0/osx-arm64;linux-musl-x64/` and breaks:
   `MSB4115: The "HasTrailingSlash" function only accepts a scalar value, but its
   argument "$(OutputPath)" evaluates to "bin\Release/net10.0/osx-arm64;linux-musl-x64/"`.
3. The SDK's intended multi-RID property (`ToolPackageRuntimeIdentifiers` + the
   `_CreateRIDSpecificToolPackages` outer/inner machinery in
   `Sdks/Microsoft.NET.Sdk/targets/Microsoft.NET.PackTool.targets`) hits the SAME
   OutputPath wall via the RuntimeIdentifiers fallback.

Conclusion: there is no one-invocation way to produce a multi-RID shell on this SDK.
Post-process instead.

## The validated fix: per-job shell patching (ai-raccoon scripts/patch-tool-shell.py)

Every matrix job runs the patch script on its own shell before upload; all 6 candidate
shells become byte-identical and correct; `--skip-duplicate` then keeps a good one
regardless of race outcome. The script (zipfile-based rewrite, ~110 lines, py3.9-safe
annotations — `str | None` raises TypeError on macOS CommandLineTools python3) must be
self-gating; it exits nonzero unless ALL hold:

- exactly one `tools/*/DotnetToolSettings.xml` in the nupkg;
- exactly one `<RuntimeIdentifierPackage>` entry, and its `Id` ends with `"." + rid`
  (prefix derivation validated — a garbage id would otherwise silently rewrite);
- the output lists every requested RID exactly once AND `Id="<prefix>.<rid>"` per RID;
- the entry list is unchanged and `[Content_Types].xml` is byte-identical;
- the requested RID count equals the workflow matrix count (matrix-vs-args drift fails
  loudly — a RID added to the matrix but not the args would otherwise ship a shell that
  silently excludes a platform with a fully green gate);
- Command `EntryPoint`/`Runner` attributes, when present, are preserved (the payload/
  intermediate shape has them).

Bonus property observed live: re-running the script on an ALREADY-patched shell fails
loudly at the single-entry assertion (idempotency guard) — which also means the
verification recipe must pack a FRESH shell before patching, never reuse a patched one.

## E2E verification recipe (proved the fix on macOS)

```bash
dotnet pack src/<App>/<App>.csproj -c Release -p:RuntimeIdentifiers=linux-musl-x64 -o /tmp/x
python3 scripts/patch-tool-shell.py /tmp/x/arasz.ai-raccoon.<ver>.nupkg win-x64 win-arm64 osx-arm64 linux-x64 linux-arm64 linux-musl-x64
# local-only feed — a plain --source folder still lets nuget.org win version resolution:
printf '<?xml version="1.0" encoding="utf-8"?>\n<configuration>\n  <packageSources>\n    <clear />\n    <add key="local" value="/tmp/feed" />\n  </packageSources>\n</configuration>\n' > /tmp/feed/nuget.config
dotnet tool install --tool-path /tmp/tooltest arasz.ai-raccoon --version <ver> --configfile /tmp/feed/nuget.config
/tmp/tooltest/ai-raccoon --version
```

The UNPATCHED shell in the same feed fails install with "The tool does not support the
current architecture or operating system (osx-arm64)". Payloads must be in the feed too
(1.0.3's osx-arm64 payload wasn't on nuget yet — pack it locally for the E2E).

## Deployment timeline (what can go wrong around the fix)

- 1.0.2 burned (broken shell, immutable) → fix + bump to 1.0.3 in the PR.
- User dispatched the workflow from OLD main (pre-merge) — then merged the fix PR.
  The live 1.0.3 turned out CORRECT: the fixed workflow's pack jobs finished after the
  merge and its push landed first (`--skip-duplicate` keeps the FIRST successful push).
  Lesson: check the live bytes, never assume from the timeline.
- The bumped 1.0.4 (needed because the 1.0.3 story was unknowable until validation
  finished) shipped via the fixed workflow: 6 payloads pushed first (17:18:44–52Z), then
  the shell (17:18:54Z), 5 duplicate shells skipped as "already exists at feed".
- nuget validation lag: flatcontainer 404s for shell AND payloads for 10+ minutes after
  a green push while the gallery shows "Validating". The workflow log is ground truth;
  the 404 is not a failure signal.
- Version-agnostic workflow step (works for future bumps):
  `VER=$(python3 -c 'import re;m=re.search(r"<PackageVersion>([^<]+)</PackageVersion>",open("src/<App>/<App>.csproj").read());print(m.group(1))')`
  (portable — `grep -oP` is GNU-only and fails on macOS BSD grep; sed with escaped
  slashes gets mangled by patch tooling).

## Review hardening (post-merge review findings, all applied)

Prefix-derivation validation; entry-preservation gate; EntryPoint/Runner preservation;
RID-count == matrix guard; ordered push (payloads first). The first-review version
(patch step only) shipped 1.0.3 correctly — the hardening is defense-in-depth, and the
ordered push removes a transient install-failure window (shell live before payloads).
