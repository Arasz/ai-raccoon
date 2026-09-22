# Releases and publishing

A GitHub release tag and a nuget.org package are two separate systems that never check each
other. A tag existing does **not** mean `dotnet tool install -g ai-raccoon --version X.Y.Z`
will resolve.

## Release tags (automatic)

`.github/workflows/release.yml` tags `main` (`vX.Y.Z`) and creates a GitHub release the
instant the `VERSION` file changes on `main` — no separate approval. Release notes come from
`gh release create --generate-notes`, built from merged PRs since the previous tag.

## nuget.org publish (manual, human-gated)

`.github/workflows/publish.yml` is a separate `workflow_dispatch`-only workflow: nothing
triggers it automatically, and no tag or release triggers it either. When dispatched, it packs
whatever is at the **default branch's tip at dispatch time** (not the tagged commit) for six
RIDs and pushes to nuget.org via OIDC trusted publishing, gated on a required-reviewer approval
on the `production` environment.

## What this means

A release tag (and its GitHub release page) can exist with no matching nuget.org package —
`publish.yml` may never have been dispatched for that version, or its `pack` job may have
failed or been cancelled after the tag was already cut. **A tagged release is not proof a
version is installable**, and nothing retries a failed publish automatically.

## Check what's actually installable

- `dotnet tool search ai-raccoon` — lists the versions nuget.org currently serves.
- <https://www.nuget.org/packages/ai-raccoon> — the package page, with every published version.
- `dotnet tool install -g ai-raccoon --version X.Y.Z` fails with a clear error for a version
  that was never published.
