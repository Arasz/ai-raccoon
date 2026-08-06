# Worked example: prerelease → stable version bump (2026-08-05, ai-raccoon)

Bumped `0.1.0-beta` → `1.0.0` on a PackAsTool MCP server to unlock the first stable
NuGet publish (PR #15). The complete checklist that made this a 1-commit, gate-green
change.

## Where the version actually lived (grep found all of them)

- `src/AiRaccoon/AiRaccoon.csproj` — THREE props, all moved together:
  - `PackageVersion` (nupkg id+version)
  - `InformationalVersion` (the `--version` flag string)
  - `AssemblyVersion` (MCP `serverInfo.version` reads this; MUST stay numeric-only —
    a `-beta` suffix there would break the MCP handshake; the csproj comment documents
    this contract)
- `src/AiRaccoon/.mcp/server.json` — TWO fields: top-level `version` AND
  `packages[].version`. Packed into the tool package, so registry consumers see them.
  Easy to miss — only the repo-wide grep surfaced them.
- A stale comment naming the version in `CliOutputRoutingTests.cs` ("the tool's own
  0.1.0-beta is proven by the smoke gate") — reworded to point at the new contract
  test instead of a version literal (any literal goes stale).

## The audit: classify every grep hit, don't edit blindly

Commands that covered the tree:

    grep -rni "beta" --include="*.md" .            # docs sweep (exclude docs/work, docs/plans, benchmarks, tests, .ai-badger)
    git grep -in "beta" -- src/ ':!src/.../vocab.txt'
    git grep -n "0\.1\.0" -- . ':!.ai-badger'

Classification (only (a) gets edited):
- (a) real version mentions → bump (csproj, server.json, test comment).
- (b) fixtures/corpus/data → leave: `beta.md` test filenames, `"beta content"` test
  strings, `zephyrbeta` watch-test content, `magnetostrictive` search queries,
  `chunk-hash-map.json`, `reference-topk.json` golden output, `jsaa-memory.db`,
  `vocab.txt` (a "beta" token in the embedding vocabulary — NOT a version mention).
- (c) historical docs → leave: `docs/plans/*` and `docs/work/*` are point-in-time
  records; rewriting them falsifies history.

Acceptance gate after the change: "no beta/0.1.0 in src/ or tests/" via the same
greps — the remaining hits must all be class (b)/(c).

## TDD vehicle: the version-contract test

A version bump has no behavioral test, so the RED step is a contract test that fails
on the old version. Shape (xunit v3 + Shouldly, Fast trait):

- Locate the repo root by walking up from `AppContext.BaseDirectory` until
  `src/AiRaccoon/AiRaccoon.csproj` exists (same pattern as the project's
  ReferenceAssets helper) — works from worktrees, local checkouts and CI.
- Parse csproj with `XDocument`, server.json with `JsonDocument` (BCL only, no new
  packages).
- Facts: PackageVersion = InformationalVersion = AssemblyVersion = "1.0.0";
  server.json `version` and `packages[0].version` = "1.0.0"; every declared version
  has no prerelease suffix.

RED: 3 failed (expected 1.0.0, got 0.1.0-beta). GREEN after the bump: 3 passed.
The test stays as the permanent guard — it would have caught the beta.

**Shouldly trap:** `actual.ShouldNotContain("-", "message")` does NOT compile for a
string — the two-argument overload resolves to the `IEnumerable<char>` predicate form
(`Expression<Func<char,bool>>`, CS1503). Use `actual.Contains('-').ShouldBeFalse("msg")`.

## Verification sequence that proved "done"

1. Contract test RED (3 failing) → bump → GREEN (3 passing).
2. `dotnet build` — 0 warnings.
3. Full `dotnet test` — 1048 passed / 0 failed / 43 skipped (the 43 are the usual
   environment-dependent skips; match the project's known skip count).
4. Post-merge: re-run the contract tests from the MAIN checkout (the merged tree)
   so the evidence is against the merged state, not the worktree.
5. NuGet semver note: stable `1.0.0` is allowed after a published `0.1.0-beta`
   (higher precedence); the publish workflow's `--skip-duplicate` means an
   un-bumped re-dispatch is a silent no-op — the version bump IS the publish
   trigger, and the dispatch itself stays human-gated (production env approval).
