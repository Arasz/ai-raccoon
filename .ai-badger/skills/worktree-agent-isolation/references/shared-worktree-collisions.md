## Agents sharing ONE worktree (parallel WIP build collisions)

The isolation model is one worktree per agent, but parallel packages can still
land in the SAME worktree (e.g. a wave runs two agents on one checkout). When a
parallel agent's UNCOMMITTED WIP breaks the shared test-project compile, do
NOT touch their files. Verify YOUR slice with a zero-file-change MSBuild override:

```bash
dotnet test tests/X.Tests/X.Tests.csproj --filter "FullyQualifiedName~YourSlice" \
  -p:DefaultItemExcludes="**/TheirInFlightFolder/**"
```

Mechanics (verified on .NET SDK 10):

- Overriding `DefaultItemExcludes` with ONLY your folder pattern is safe — bin/obj are
  excluded by `DefaultExcludesInProjectFolder`, not by the value you replace.
- `-p:` values split on `;` — use a SINGLE pattern with no semicolons (a `%3B`-escaped
  list misbehaved). Confirm the exclude took effect with
  `dotnet msbuild <proj> -getItem:Compile "-p:DefaultItemExcludes=**/TheirFolder/**" | grep '"Identity": "TheirFolder'`.
- CS2002 "source file specified multiple times" warnings are artifacts of the override, NOT your code.
- Report the collision in your summary: the canonical gate re-runs clean once the
  other agent lands their fix; don't claim full-suite green.

**The sibling's broken file shares YOUR namespace folder → DefaultItemExcludes cannot
separate you.** The exclude override is folder-granular;
when the parallel agent's in-flight RED test file sits in the SAME folder your tests live in,
excluding their file excludes yours too. Fall back to the
throwaway scratch test project:
`mktemp -d "${TMPDIR:-/tmp}/hermes-verify-<slice>.XXXXXX"`, minimal csproj with explicit
package versions, ProjectReference to the real production project, `<Compile Include>` your
exact test files + the shared test helpers by absolute path, `dotnet test`, `rm -rf`. Then
poll `git log` — once the sibling's GREEN lands, the shared assembly builds again and the
canonical `dotnet test --filter` run SUPERSEDES the scratch run; re-run the real gate rather
than reporting scratch evidence as final.

**Sibling RED already COMMITTED → `dotnet test --no-build` runs the last-built bin.** When the parallel agent's RED lands as a commit (not just WIP),
the test project won't compile until their GREEN — but the bin/ from BEFORE their commit is
still intact, so `dotnet test --no-build --filter 'YourSlice'` executes it without triggering
a build. Two caveats: (a) the bin must predate their breakage (your own changes since then
need a pre-check via the isolated compile, or you stash/rebuild only after their GREEN);
(b) `--no-build` does NOT touch obj/bin, so it cannot race the sibling's build. Use it to get
real execution evidence for your slice mid-blockage; the canonical filtered run after their
GREEN still supersedes it. Scratch-project refinement: prefer
`<Reference><HintPath>` to the already-built production DLLs (wildcard HintPaths do NOT
expand — list each DLL) over ProjectReference, so the scratch build never writes the shared
obj/bin of src projects; and a repo whose SQLite provider is activated by an app-level
`[ModuleInitializer]` needs that initializer replicated in the scratch host (plus the repo's
exact native-package pins) or bank-opening tests die on the wrong native provider.

**Contract types owned by a parallel agent: ship a shape-identical stand-in, reconcile when
they land.** When your section needs a shared type (record/DTO)
the plan assigns to a parallel section that hasn't landed yet, do NOT block and do NOT create
it in their directory. Define a stand-in with the identical shape in your own namespace, build
and test against it, and flag the duplication in your report. The moment the owner's commit
lands (poll `git log` / `ls` their dir), reconcile in one small refactor commit: delete your
copy, `using` theirs, re-run the gate. Two same-named types in different namespaces compile
fine until one file imports both namespaces — then CS0104 ambiguity — so reconcile promptly,
before the next wave's files import both.

Corollary worktree gotcha: a fresh worktree of a repo whose `NuGet.config` lists
a local source (e.g. `./.nupkg-local/`) restores with NU1301 until you
`mkdir -p .nupkg-local` — the main checkout has that directory, worktrees do not.

### Git discipline when sharing ONE worktree

The `git add -A` in the integration flow above is ONLY safe for single-agent
worktrees. With a concurrent agent in the SAME worktree:

- **Never `git add -A`** — it stages the other agent's in-flight files. Stage
  only your own paths explicitly (`git add src/X.cs tests/Y.cs`).
- **`git status --short` immediately before EVERY commit** — confirm only your
  files are staged; the other agent may have landed files since your last check.
- **Run only targeted builds/tests** (`dotnet build src/App/App.csproj`,
  `dotnet test --filter 'YourSlice'`) — the full-suite gate runs at the wave
  join, and a full build can collide with the other agent's in-flight obj/bin
  writes.
- **TDD red commits are safe here** — a test-only commit that breaks the TEST
  project compile does not disturb a concurrent agent who builds only the app
  project (the test project is never a dependency of the app). This preserves
  red-first discipline in a shared worktree; confirm the other agent's build
  target first.

### Orchestrator committing from a SHARED main checkout

`git commit` (no pathspec) commits the WHOLE INDEX, not just your `git add`ed
file — another session's staged changes (including DELETIONS) silently ride inside
your commit. Checks before committing from a shared checkout:

- `git branch --show-current` — if you are NOT on the branch you think, the
  checkout was switched under you; stop and relocate to a worktree.
- `git status --short` — the staged column (first char) must contain ONLY your
  files; unstage foreign paths (`git restore --staged <path>`) or move to a worktree.
- Verify the branch base is intact: `git ls-tree HEAD -- <dir>` for directories
  your commit should NOT have touched — a swept-in deletion shows as a missing tree entry.

If the branch is already contaminated (foreign changes in the commit): rebuild
it cleanly rather than trying to surgically remove hunks —
`git checkout -B <branch> origin/main` (drops the contaminated commit), then
re-apply your changes in TDD order (RED commit first, then GREEN). Worktrees
isolate this class entirely — prefer them over committing from a shared
checkout whenever another session may be active.

### Verification evidence for your slice

When the platform demands script-based verification evidence (automated
verification tracker: "no canonical test/lint/build command detected"), wrap the
repo's canonical gates in a throwaway script instead of asserting green from
memory:

```bash
SCRIPT=$(mktemp -t hermes-verify-)   # OS-safe temp path, hermes-verify- prefix
cat > "$SCRIPT" <<'EOF'
#!/bin/bash
set -u
cd <worktree> || exit 1
BUILD=$(dotnet build src/App/App.csproj --nologo 2>&1)
echo "$BUILD" | grep -q "0 Warning(s)" || { echo "FAIL build"; exit 1; }
echo "$BUILD" | grep -q "0 Error(s)"   || { echo "FAIL build"; exit 1; }
TEST=$(dotnet test tests/App.Tests/App.Tests.csproj --filter 'YourSlice' --nologo 2>&1)
echo "$TEST" | grep -q "Passed!"       || { echo "FAIL test"; exit 1; }
echo "$TEST" | grep -q "Failed:     0" || { echo "FAIL test"; exit 1; }
echo "VERIFY OK"
EOF
chmod +x "$SCRIPT" && "$SCRIPT"; RC=$?; rm -f "$SCRIPT"
```

Assert on the canonical output markers (`0 Warning(s)`, `Passed!`) rather than
inventing your own. Report the run as targeted/ad-hoc verification — a targeted
filter run is NOT full-suite green, and the shared-worktree caveat above still
applies.

**Verification-script pitfalls (measured 2026-08-04):** `mktemp -t hermes-verify-`
can fail on macOS with "File exists" — use an explicit path + unique suffix and check `$?`
after mktemp; the tracker's recording hook keys on the command SHAPE and can record a false
`passed` from the script TEXT when mktemp failed — confirm real execution (exit 0 AND PASS
lines in the output) before trusting a recorded event; the tracker re-fires per edit turn on
changed paths — one clean recorded canonical run satisfies it.
