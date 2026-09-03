# Worked example: the project NuGet publish PR review (2026-08-05)

Reviewed `task/configure-nuget-publish` (PR #12): 3 workflows + csproj metadata for a
6-RID PackAsTool MCP server tool. Verdict: APPROVE-WITH-CHANGES, 7 findings (1 Medium,
3 Low, 2 Info, 1 Low spec-count). All acceptance criteria passed; nothing blocked the
first publish.

## Verified action versions (2026-08-05, via releases/latest redirects)

- actions/checkout v7.0.1, actions/setup-dotnet v6.0.0, actions/upload-artifact v7.0.1,
  actions/download-artifact v8.0.1, NuGet/login v1.2.0
- Versions move — re-verify each review, never cite this list from memory.

## The workflow shape that passed review

- build.yml: push/PR to main → `dotnet build` + `dotnet test --filter "Speed=Fast"`
  (trait applied via `[Trait(TestCategories.Speed, TestCategories.Fast)]` constants —
  literal grep for `Trait("Speed"` returns 0 hits, misleading).
- nightly.yml: `cron: "0 2 * * *"` (02:00 UTC) + `workflow_dispatch`, full suite unfiltered.
- publish.yml: workflow_dispatch; pack matrix over 6 RIDs with
  `-p:RuntimeIdentifiers=${{ matrix.rid }}`; push job `needs: pack`,
  `environment: production`, `permissions: {contents: read, id-token: write}`,
  `NuGet/login@v1` with `user: ${{ secrets.NUGET_USER }}`, push via
  `${{ steps.login.outputs.NUGET_API_KEY }}` to api.nuget.org with `--skip-duplicate`.

## Findings that generalized (see SKILL.md)

1. Medium — static `PackageVersion` 0.1.0-beta → every re-dispatch is a no-op via
   --skip-duplicate. Fix: workflow_dispatch `version` input → `-p:PackageVersion`.
2. Low — dispatch not restricted to `main` (identity is repo-scoped; human gate still
   applies). Fix: `workflow_dispatch: branches: [main]`.
3. Low — no tests before push; accepted by design (manual approval + PR CI gate).
4. Low — PackageReleaseNotes missing; the only DO item in the MS best-practices doc
   not covered. Fix: link the releases page.
5. Low — 27 tags vs 28 in spec; all search-oriented, no internal feature tags.
6. Info — --skip-duplicate is REQUIRED for multi-RID shell copies (nuget.org dedupes by
   id+version, 409 on differing content), not just an optimization.
7. Info — no concurrency group; harmless thanks to --skip-duplicate.

## Repo-specific facts (the project)

- nuget.config: `<clear/>` + nuget.org + local folder `.nupkg-local/` (gitignored) →
  fresh runners need `mkdir -p .nupkg-local` before restore or NU1301.
- csproj packs `.mcp/server.json`, README.md, Models/vocab.txt + Models/*.onnx; the
  project's own `DeployToLocalSource` target (DOTNET_ENV=local) already proved the
  shell+payload pack shape locally.
- PackageType=McpServer custom type; PackageReadmeFile + README packed pre-existing.
