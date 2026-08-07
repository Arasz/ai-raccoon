## Multi-RID tool packages: matrix pack, push BOTH packages

`dotnet pack -p:RuntimeIdentifiers=<rid>` on a PackAsTool project emits TWO nupkgs per run:
the tool shell (`<id>.<version>.nupkg`) and the RID payload
(`<id>.<version>.<rid>.nupkg`). `dotnet tool install` needs both on the feed — pushing only
the shell leaves the RID payload missing and installs fail. Verified 2026-08-05 on
a 6-RID tool (PR #12 review).

GitHub Actions pattern:
- One pack job per RID (`rid: [win-x64, win-arm64, osx-arm64, linux-x64, linux-arm64,
  linux-musl-x64]`), pack with `-p:RuntimeIdentifiers=${{ matrix.rid }}` — the command line
  overrides the tool's multi-RID `RuntimeIdentifiers` property per job. Upload each
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

### The shell race: a per-RID matrix publishes a shell that references ONE RID (verified 2026-08-05)

The matrix pattern above has a hidden race. **Every matrix job emits its OWN shell
package with the same id+version** — and each shell's `DotnetToolSettings.xml` lists
ONLY the RID that job packed (`<RuntimeIdentifierPackage RuntimeIdentifier="<rid>"
Id="<id>.<rid>" />`). The push job then glob-pushes N copies of the shell; nuget.org
dedupes by id+version and keeps whichever arrived FIRST. The surviving shell references
one RID — typically the first matrix leg alphabetically (or whichever job won the race) —
so `dotnet tool install` FAILS on every other platform even though every RID payload
package exists on the feed.

Measured on `<pkg-id>` 1.0.2: the published shell's `DotnetToolSettings.xml`
references only `linux-musl-x64`; `<pkg-id>.osx-arm64` 1.0.2 exists on nuget.org
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
each job's shell before upload** with a small committed script (a `patch-tool-shell.py`
in the reference tool) that rewrites `DotnetToolSettings.xml`'s
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

### After the fix ships — deployment reality (measured 2026-08-05)

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
  `find artifacts -type f -name '*.nupkg' ! -name '<pkg-id>.[0-9]*.nupkg' -print0 | xargs -0 -n1 dotnet nuget push ...`
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
  shell's — you can see the shell resolve while `<pkg-id>.osx-arm64` still
  reports not-found). Also: a tool originally installed from a local folder feed
  (`--source <dir>`) needs the explicit `--version` on update — a bare
  `dotnet tool update -g` answers "already installed" and never moves versions.
- **"The fix is out, update the tool" — confirm WHICH version first (user correction
  2026-08-05):** when a human says a fix was published and to update, do NOT grab the
  first new version the index shows. The racing-dispatch scenario above means the first
  new version can be the OLD code (<pkg-id> 1.0.3 raced from pre-fix main) while
  the real fix is the NEXT one (1.0.4). Ask which version is the fix (or verify the fix
  commit actually merged before the dispatch) before updating — an eager update to the
  wrong version burns a round-trip and needs a second update once the true fix lands.

Full measured detail — SDK internals, failure transcripts, script shape, deployment
timeline: `references/multirid-shell-race-fix.md`.
