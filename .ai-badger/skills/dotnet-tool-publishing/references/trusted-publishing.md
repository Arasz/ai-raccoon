## NuGet Trusted Publishing (no API keys)

The nuget.org policy (Account -> Trusted Publishing) binds: package owner, repo, and WORKFLOW FILE NAME — the file name must match `.github/workflows/<file>`
exactly (file name only, e.g. `publish.yml`). If the policy declares an
`environment:`, the workflow must use that environment.

**Environment-mismatch verification rule (verified 2026-08-05 on the reference tool):**
the `NuGet/login@v1` failure names the expected environment —
`Token exchange failed (HTTP 401) ... Environment mismatch for policy 'X':
expected 'production', actual 'publish'`. Read the expected name from the live error or the policy page (which prints "Workflow: publish.yml Environment:
production"), NOT from a user's report that they "fixed the policy" — the fix may not have stuck (the policy still expected `production` two failed runs after the owner said it was fixed to `publish`). Re-verify before changing the
workflow's `environment:`.

Workflow requirements:

- `permissions: { id-token: write, contents: read }` — without `id-token: write`
  the OIDC request silently fails and no key is issued.
- `NuGet/login@v1` with `user: <nuget.org username>` (profile name, NOT email; it is public — no need to secret it). Exchanges the OIDC token for a temporary API key: valid 1 h, single use per token — request it shortly before pushing.
- Push: `dotnet nuget push ./artifacts/*.nupkg --api-key ${{ steps.login.outputs.NUGET_API_KEY }} --source https://api.nuget.org/v3/index.json`
- Triggers: `push: tags: ['v*']` + `workflow_dispatch`.

Additional patterns verified 2026-08-05 (PR #12):

- **Manual approval gate**: put `environment: production` on the PUSH job only (not the pack matrix). With a required-reviewer protection rule on the environment, the job waits for an Approve before ANY step runs — approval therefore covers
  the push. The nuget.org policy's declared environment must match the workflow's `environment:` name exactly.
- **Version input for repeatable releases**: a hardcoded `PackageVersion` makes the workflow single-shot — the second dispatch pushes the same id+version and
  `--skip-duplicate` no-ops the whole run. Prefer a `workflow_dispatch` input `version`
  (semver-validated) passed as `-p:PackageVersion=${{ inputs.version }}`.
- **Branch-scoped dispatch**: the trusted-publishing identity is repo-scoped, not branch-scoped. `workflow_dispatch` has NO `branches` key — actionlint rejects it (`expected "inputs" key for "workflow_dispatch" section but got "branches"`),
  and dispatch always runs from the default branch anyway, so a `branches:` restriction is both invalid and pointless. If a branch guard is genuinely needed, use a first-step `if: github.ref == 'refs/heads/main'`.
- **Tests before push is a judgment call**: acceptable to skip when a human approval gate exists and PR CI already gates merges to the release branch; otherwise run the fast suite in the pack job.

Working template: `templates/publish.yml`.
