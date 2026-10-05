# Changelog

One file per release, `{version}-{slug}.md`, written in the same PR that bumps `VERSION`. Each
entry says what changed for a user of the installed tool, and names the PRs it ships.
A PR that leaves `VERSION` alone cuts no release and adds no file: the entry of the release that
next bumps `VERSION` describes and names it, so several PRs can ship as one release.
Releases before 1.50.1 are recorded in their `chore(release)` commit messages and the README's
What's new list.

## Index

Newest first; add each release's line in the same PR that adds its file.

- [1.57.2 — failed identification no longer starts another server](1.57.2-proxy-refuses-unproven-backend.md)

- [1.56.1 — long queries no longer time out in keyword search](1.56.1-long-query-fts-cap.md)
- [1.56.0 — stop-path secrets ride the proven connection; cancel-safe writes everywhere](1.56.0-stop-path-token-and-cancel-safe-writes.md)
- [1.55.6 — a named backend executable is never silently replaced](1.55.6-named-executable-never-swapped.md)
- [1.55.5 — `doctor` handler split into named steps](1.55.5-doctor-run-split.md)
- [1.55.0 — re-chunk phase instrumentation](1.55.0-rechunk-phase-instrumentation.md)
