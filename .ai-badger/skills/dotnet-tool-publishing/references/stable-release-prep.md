## Version bump / stable-release prep

- Bump `<Version>` (semver), update `PackageReleaseNotes`, add a CHANGELOG.md entry (Keep a Changelog), refresh the README badge to the GitHub Actions workflow: `https://github.com/<owner>/<repo>/actions/workflows/build.yml/badge.svg`.
- Migrating CI off Azure DevOps: the README badge and any in-repo pipeline refs are the only in-repo traces; the old pipeline lives in the external DevOps project and must be deleted there by the owner.

**The version lives in 4+ places — grep for the OLD string repo-wide, not just the csproj** (verified 2026-08-05, 0.1.0-beta → 1.0.0):

- csproj: `PackageVersion` (nupkg id+version), `InformationalVersion` (the `--version`
  flag string), `AssemblyVersion` (MCP `serverInfo.version` reads THIS and must stay numeric-only — a `-beta` suffix there breaks the MCP handshake). All three move together.
- `.mcp/server.json` (MCP servers, packed into the tool): the top-level `version` AND
  `packages[].version` BOTH hardcode the version — easy to miss; both must match the csproj (found only via the repo-wide grep).
- Comments in tests/scripts that name the version — they go stale silently.

**Prerelease→stable audit: classify every grep hit before editing.** (a) real version mentions — bump; (b) test fixtures / corpus content / model vocab (`beta.md` filenames,
`"beta content"` test strings, a `vocab.txt` token) — leave, they are data; (c)
historical plan/research docs (`docs/plans/*`, `docs/work/*`) — leave, they are point-in-time records and rewriting them falsifies history. Acceptance gate: "no beta/old-version in src/ + tests/" excluding fixtures, proven by the
post-change grep. NuGet semver: a stable `1.0.0` is allowed after a published `0.1.0-beta` (higher precedence), and an un-bumped re-dispatch of the publish workflow is a
`--skip-duplicate` no-op — the version bump IS the publish trigger.

**Pin the version with a contract test (the TDD vehicle).** A version bump has no behavioral test, so the RED step is a version-contract test that FAILS on the old version: walk up from `AppContext.BaseDirectory` to the repo root (same
pattern as the test project's ReferenceAssets), read the csproj + `.mcp/server.json`, assert PackageVersion = InformationalVersion = AssemblyVersion = the new version, server.json fields match, and no prerelease suffix (`Contains('-')` is
false). Worked shape:
read `references/stable-release-version-bump.md` when bumping a stable release version. **Shouldly trap in such tests:** `actual.ShouldNotContain("-", "msg")` does NOT compile for strings — the two-arg overload resolves to the
`IEnumerable<char>` predicate form (CS1503). Use `actual.Contains('-').ShouldBeFalse("msg")`.
