# Web-SDK multi-RID pack: measured reproduction (2026-08-05, ai-raccoon)

Reproduction recipe for the MSB3030 trap and its inverse on a `Microsoft.NET.Sdk.Web`
PackAsTool project with `<RuntimeIdentifiers>` set (6 RIDs). All three scenarios
run from a CLEAN tree — `rm -rf src/*/obj src/*/bin` first, because leftover
RID-scoped outputs mask the bug. The repo's own `.nupkg-local` feed dir also
hides it (stale nupkgs from a previous successful pack satisfied the
`DeployToLocalSource` Outputs gate for days).

## Scenario A — build with RID, then pack --no-build (the CI failure)

    dotnet build src/X/X.csproj -c Release -p:RuntimeIdentifiers=osx-arm64
    dotnet pack  src/X/X.csproj -c Release -p:RuntimeIdentifiers=osx-arm64 --no-build -o artifacts

Result: **MSB3030** — `Could not copy the file
"obj/Release/net10.0/osx-arm64/X.dll" because it was not found`, plus
bin/Release/net10.0/osx-arm64/{runtimeconfig.json,deps.json,pdb} and the
referenced projects' RID-scoped dlls. A plain Web-SDK build does NOT emit
RID-scoped publish outputs, so pack --no-build cannot find them.

## Scenario B — plain build, then pack --no-build with RID

    dotnet build src/X/X.csproj -c Release
    dotnet pack  src/X/X.csproj -c Release -p:RuntimeIdentifiers=osx-arm64 --no-build -o artifacts

Result: **also MSB3030** — same root cause. This is the form the generic
"build then pack --no-build" advice recommends; it fails for Web-SDK multi-RID.

## Scenario C — single pack WITHOUT --no-build (the fix)

    dotnet pack src/X/X.csproj -c Release -p:RuntimeIdentifiers=osx-arm64 -o artifacts

Result: **works** — 6.6 s on osx-arm64, emits BOTH `X.<version>.nupkg` (tool
shell) and `X.osx-arm64.<version>.nupkg` (RID payload), exit 0. Pack builds for
the RID itself.

## Conclusion

For Web-SDK multi-RID PackAsTool: NO separate build step, NO `--no-build` —
one pack step per RID in the matrix. For plain (non-Web-SDK) tools the
build-then-pack-`--no-build` form is correct. Verify the sequence on a clean
tree for each project SDK shape; never trust a worktree with stale RID outputs.

## Nested-pack recursion (DeployToLocalSource-style AfterTargets)

An `AfterTargets="Build"` target gated on an env var (e.g. `DOTNET_ENV=local`)
that Execs `dotnet pack` re-enters itself: the nested pack inherits the env var,
its own Build fires the target again → infinite loop (observed: empty log,
minutes of repeated project builds, must be killed). Fix:

    <Target Name="DeployToLocalSource" AfterTargets="Build"
            Condition="'$(DOTNET_ENV)' == 'local' and '$(SuppressDeployToLocalSource)' != 'true'" ...>
        <Exec Command="dotnet pack ... -p:RuntimeIdentifiers=$(NETCoreSdkRuntimeIdentifier)
                       -p:SuppressDeployToLocalSource=true -o ..."/>
    </Target>

Also drop `--no-restore` from that nested pack: the outer build restored
without the RID, so referenced projects' `project.assets.json` lack the
`net10.0/<rid>` target → NETSDK1047. Let the nested pack restore.

## XML comment trap

`--` is illegal inside an XML comment (MSB4025: "An XML comment cannot contain
'--'"). When documenting `--no-build` in a csproj comment, write "the no-build
flag" instead.
