---
description: 'GitHub Actions workflow authoring conventions.'
applyTo: '**/.github/workflows/*.yml,**/.github/workflows/*.yaml'
---

<!-- Managed by ai-badger. Source of truth: .ai-badger/instructions/github-actions.instructions.md. Do not edit this copy by hand; edit the source and re-run welcome-ai-badger. -->


# GitHub Actions

- Pin every third-party action to a full commit SHA, not a tag or branch (`uses: owner/repo@<full-sha>`); a mutable tag is remote code you re-fetch on every run.
- Declare `permissions:` explicitly at workflow or job level, scoped to the least privilege that job needs — never rely on the repository's default token permissions.
- Add a `concurrency` group (with `cancel-in-progress` where superseding an in-flight run is safe) so redundant runs from rapid pushes don't pile up.
- Reference secrets only via `secrets.*` in `env:` or `with:`; never echo a secret value into a log, build output, or `run:` command directly.
- Set `timeout-minutes` on every job so a hung step fails fast instead of consuming runner time indefinitely.
- Where a workflow's output must be reproducible, pin the runner image to a specific version rather than a floating label like `ubuntu-latest`.
