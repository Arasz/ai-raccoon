# changelog/

Release notes, one file per version: `{version}-{slug}.md`. There is no single, growing
`CHANGELOG.md` at the repo root.

## Convention

Every release, however small, does three things:

1. Bumps [`VERSION`](../../VERSION) at the repo root — semver patch for a fix, minor for a
   feature, major for a breaking change.
2. Adds `docs/changelog/{version}-{slug}.md` describing what changed, its first line a
   `# {version} — <title>` heading. One release can add more than one file if several
   unrelated changes land at the same version.
3. Updates this README when the convention itself changes. The table below is plain and
   hand-maintained — nothing here regenerates it, so add the row yourself.

## Releases, newest first

| Version | Entry |
|---|---|
| 1.51.0 | [WebGPU plugin off macOS, CUDA opt-in](1.51.0-webgpu-plugin-cuda-opt-in.md) |

## Relationship to the README's "What's new"

`README.md`'s "What's new" section is the short, user-facing highlight reel: one plain line
per release, features only, no fix or perf entries. This directory is the fuller record —
every behavior a release changed, not only its headline, with enough detail for someone
tracking down a regression to find what shipped in a given version and why. Most releases
need both: one line in the README, one file here.
