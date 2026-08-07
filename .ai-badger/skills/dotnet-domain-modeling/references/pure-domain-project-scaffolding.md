# Pure Domain Project Scaffolding (.NET)

How to add a new pure-domain class library to an existing .NET solution, TDD-first, without touching other layers. Validated in the project agent-memory worktree (`src/<Proj>.Core`, net10.0, xunit.v3 + Shouldly, TreatWarningsAsErrors repo).

## Project shape

- **csproj**: plain `Microsoft.NET.Sdk`, `<TargetFramework>` only, **zero PackageReferences**. A domain dependency is an ADR-level decision, not a routine `dotnet add package` — prefer hand-rolled guards (below).
- **Directory.Build.props** at repo root (nullable, implicit usings, TreatWarningsAsErrors, EnableNETAnalyzers, EnforceCodeStyleInBuild, file-scoped namespaces + braces as build warnings) applies automatically to any project under the
  root — the new csproj inherits it with no extra lines.
- **InternalsVisibleTo** is an SDK *item* in the csproj, not an attribute:
  ```xml
  <ItemGroup><InternalsVisibleTo Include="MyApp.Tests"/></ItemGroup>
  ```
- **Solution**: `.slnx` is XML — add `<Project Path="src/MyApp.Core/MyApp.Core.csproj"/>` so `dotnet build`/`dotnet test` from the root cover it explicitly.
- **Test project**: add a `ProjectReference` to Core; keep any existing server/app reference intact. Do NOT remove the old reference "because Core now covers it".

## Zero-dependency Guard class

When the repo's clean-layering rule forbids a new domain package, hand-roll a tiny `internal static` guard in `Common/`. Doubles make `int` args convert implicitly, so ONE overload set covers both int and double callers:

```csharp
internal static class Guard
{
    public static void NotNullOrWhiteSpace(string? value, string paramName)      // ArgumentException
    public static void GreaterThan(double value, double minExclusive, string paramName)          // ArgumentOutOfRangeException
    public static void GreaterThanOrEqualTo(double value, double minInclusive, string paramName) // ArgumentOutOfRangeException
    public static void InRange(double value, double minInclusive, double maxInclusive, string paramName) // ArgumentOutOfRangeException
}
```

- `ArgumentException` for blank strings; `ArgumentOutOfRangeException` for numeric ranges — matches BCL convention (tests assert the specific type).
- Keep only the methods the domain actually calls — don't pre-build a full guard library.

## Constructor-validated records

Records that must reject input at construction use an explicit constructor + get-only auto-properties (see SKILL.md "Constructor-Validated Records" section). Pure data shapes (entries, search results, candidates) stay positional records.
Computed expression-bodied properties are excluded from record equality.

## TDD red phase for a brand-new namespace

1. Write ALL test files + csproj + solution wiring first.
2. `dotnet test` → every file fails with the same `CS0234: type or namespace '<Proj>.Core' does not exist` — that is legitimate red; no stubs needed (nothing is masked, the error IS "feature missing").
3. Implement all domain types.
4. `dotnet build` → must show `0 Warning(s)` (TreatWarningsAsErrors — a warning IS a failure).
5. `dotnet test` → all green. Note the runner counts theory cases individually (6 `[InlineData]` rows = 6 tests), so a "54 new tests" report means counting cases, not `[Fact]` blocks.

## Domain type inventory checklist (agent-memory feature shape)

Typical pure-Core surface for a port-based feature: entity records (`MemoryEntry`, `MemorySearchResult`), validated request records (`MemoryWriteRequest`, `SearchQuery`), a port interface (`IMemoryStore` — thin, SQL-shaped,
`CancellationToken cancellationToken = default` on every method), pure policy statics (`RatingPolicy`, `DegradationPolicy` — static classes, guards at entry, const defaults), an extension contract + hook context records
(`IMemoryExtension` + `WriteContext`/`SearchContext`/`DeleteContext`/`SweepContext`/`ConsolidationContext`), a derived-context record (`Workspace` with `Context => ContextNaming.WorkspaceContext(Id)`), and a context-name builder static
(`ContextNaming`).

## Verification harness: no auto-detected canonical command

When the platform's verification step reports "no canonical test/lint/build command detected" even though the repo documents one (e.g. CLAUDE.md lists `dotnet build` / `dotnet test`), satisfy it with a disposable wrapper script, not by
inventing new checks:

```bash
tmp=$(mktemp /tmp/hermes-verify-XXXXXX.sh)
cat > "$tmp" <<'EOF'
#!/usr/bin/env bash
set -euo pipefail
cd /path/to/repo/root
build_out=$(dotnet build --nologo 2>&1); echo "$build_out" | tail -5
grep -q "0 Warning(s)" <<<"$build_out" || { echo "FAIL: build warnings"; exit 1; }
test_out=$(dotnet test --nologo 2>&1); echo "$test_out" | tail -3
grep -qE "Passed!.*Failed:[[:space:]]*0," <<<"$test_out" || { echo "FAIL: tests not green"; exit 1; }
echo "ALL CHECKS PASSED"
EOF
chmod +x "$tmp" && bash "$tmp"; rc=$?; rm -f "$tmp"; exit $rc
```

Rules:

- The script wraps the repo's **canonical gates** (build + full suite) plus a few targeted assertions on the changed files — it is NOT a separate or new test suite.
- Report it honestly as ad-hoc verification over the canonical suite; never claim it found things the real suite didn't run.
- Clean up (`rm -f`) in the same command; `mktemp` path keeps it OS-safe.
- Still show the plain `dotnet build` / `dotnet test` runs and their summaries as the primary evidence.
