# Trusted Publishing OIDC debugging saga (2026-08-05, dotnet-ignore)

Full record of diagnosing three failed publish runs, so a future session can
recognize the failure signatures immediately.

## The setup that was being built

dotnet-ignore (a PackAsTool global tool, net10.0) publishing via GitHub Actions:

- `.github/workflows/publish.yml` — builds, packs, NuGet/login@v1, pushes.
- NuGet policy "Dotnet Ignore" on nuget.org: package owner Arasz, repo
  arasz/dotnet-ignore, workflow file `publish.yml`, **Environment: production**.
- `production` GitHub environment with `required_reviewers` = the owner (the
  approval gate) — configured by the user in the UI.

## Failure 1 — workflow environment named wrong

Workflow had `environment: publish` (created by the agent). Live run failed at
NuGet/login@v1:

```
##[error]Token exchange failed (HTTP 401) at https://www.nuget.org/api/v2/token.
Make sure you are using the username of the policy creator, not the policy
owner: Environment mismatch for policy 'Dotnet Ignore': expected 'production',
actual 'publish'
```

Root cause: the GitHub Actions environment name is carried in the OIDC token and
must equal the policy's Environment field exactly. The workflow must use
`environment: production`.

## Failure 2 — user-side confusion on the policy

The user said "i fixed policy" and, when asked which environment the policy now
expects, answered "publish". The next run still failed with the SAME 401
(`expected 'production', actual 'publish'`). Lessons:

- The live token-exchange error is ground truth; user belief is not. Re-verify
  empirically on every run that matters.
- The policy page (nuget.org -> Trusted Publishing -> policy row) shows the
  fields verbatim: `Workflow: publish.yml Environment: production`.
- When the user pastes the policy details, they settle it — but the workflow
  change must follow the POLICY, not the user's recollection.

## Failure 3 — environment branch policy vs PR merge ref

After the environment name was corrected, the run was rejected BEFORE any step:

```
Branch "refs/pull/15/merge" is not allowed to deploy to production due to
environment protection rules.
The deployment was rejected or didn't satisfy other protection rules.
```

Root cause: a `pull_request: types: [closed]`-triggered run executes from the PR
merge ref `refs/pull/<n>/merge`. The `production` environment had a
`branch_policy` protection rule; the merge ref is not an allowed source.
Fix: `on: push: branches: [master]` (+ `workflow_dispatch`) — a merge to master
IS a push, the run's ref is `refs/heads/master`, branch policy satisfied.
Trade-off: EVERY push to master spawns a pending publish run; the required
reviewer is the release control (approve or ignore/cancel).

## Environment protection rules via API (what works, what doesn't)

- Create environment: `gh api --method PUT repos/{owner}/{repo}/environments/{name}`
  — works.
- Read rules: `gh api repos/{owner}/{repo}/environments/{name}` — works; shows
  `protection_rules[].type` (branch_policy, required_reviewers, wait_timer).
- Read deployment branches: GET .../environments/{name}/deployment-branches — 404
  (not exposed).
- CREATE required_reviewers rule:
  `POST .../environments/{name}/protection-rules` with
  `{"type":"required_reviewers","reviewers":[{"type":"User","id":<id>}]}` — **404,
  UI-only**. Verified twice. The user must do Settings -> Environments -> <name>
  -> Required reviewers once.
- The user's GitHub user id for such payloads: `gh api users/<login> --jq .id`.

## NuGet/login@v1 specifics

- `with: user:` must be the nuget.org profile name of the POLICY CREATOR (who
  clicked "Create" on the policy), not the package owner and not an email. The
  401 message literally says: "Make sure you are using the username of the policy
  creator, not the policy owner".
- `id-token: write` permission required at job level.
- Temporary keys are single-use and valid ~1 hour; request the key right before
  the push.

## Related workflow lessons (same session)

### actions/labeler v7 config format

v7 requires **match objects**, not the v4 bare-glob shorthand:

```yaml
dotnet:
  - changed-files:
      - any-glob-to-any-file:
          - src/**
          - test/**
```

Bare `dotnet: [src/**, test/**]` is INVALID in v7 (the editor LSP schema flags
it, correctly). The v7 README confirms: value = a list of match objects with
`changed-files` -> `any-glob-to-any-file` / `any-glob-to-all-files` /
`all-globs-to-any-file` / `all-globs-to-all-files`, plus `base-branch` /
`head-branch` and top-level `any`/`all` combinators. The user replaced a custom
github-script `/latest` command workflow with the official `actions/labeler@v7`
— preference: official actions over hand-rolled.

### pull_request_target narrowing

`pull_request_target` fires on every synchronize too; `types: [opened, reopened]`
labels a PR once (sync-labels defaults to false in v7). Also note:
`pull_request_target` workflows run from the DEFAULT branch's version of the
workflow file — changes only take effect after merge.

### Workflow YAML verification quirks

- pyyaml (YAML 1.1) parses `on:` as boolean `True` — read triggers with
  `d.get('on') or d.get(True)`.
- actionlint is the right linter: catches bad keys, unknown actions, and
  shellcheck issues in `run:` steps (e.g. SC2012 "use find instead of ls").
