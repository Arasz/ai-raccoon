## Workflow skeleton (verified)

build.yml — on push/PR: checkout + setup-dotnet + `dotnet build` + fast tests only (`--filter "Speed=Fast"`); full suite moves to nightly.
publish.yml — `workflow_dispatch` restricted to `branches: [main]`:
- pack matrix, one job per RID:
  1. `mkdir -p .nupkg-local` (nuget.config local-feed reference → NU1301 on fresh runner)
  2. `dotnet build -c Release -p:RuntimeIdentifiers=${{ matrix.rid }}` — **build before pack** (packing an unbuilt multi-RID project fails MSB3030)
  3. `dotnet pack ... --no-build -o artifacts`
  4. upload-artifact
- publish job: `needs: pack`, `environment: production`, `permissions: {contents: read, id-token: write}`, download-artifact, `NuGet/login@v1` (`user: <nuget username>`), push all nupkgs with `--api-key ${{ steps.login.outputs.NUGET_API_KEY }} --source https://api.nuget.org/v3/index.json --skip-duplicate`.
nightly.yml — cron + `workflow_dispatch`, `concurrency: {group: nightly, cancel-in-progress: false}`, full `dotnet test`. Scheduled runs are best-effort (GitHub can drop/delay them); document `gh workflow run nightly.yml` for re-arming.
