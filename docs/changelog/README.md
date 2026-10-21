# Changelog

One file per release, `{version}-{slug}.md`, written in the same PR that bumps `VERSION`. Each
entry says what changed for a user of the installed tool, and names the PRs it ships.
A PR that leaves `VERSION` alone cuts no release and adds no file: the entry of the release that
next bumps `VERSION` describes and names it, so several PRs can ship as one release.
Releases before 1.50.1 are recorded in their `chore(release)` commit messages and the README's
What's new list.

## Index

Newest first; add each release's line in the same PR that adds its file.

- [1.62.0 — shared document watches](1.62.0-shared-document-watches.md)

- [1.61.1 — how to change the embed drain's rows-per-run, and the CLI help names the setting](1.61.1-embed-rows-per-run-how-to.md)
- [1.61.0 — the embed drain's rows-per-run runs from 64 to 4096, and what that value costs](1.61.0-embed-rows-per-run-range.md)
- [1.60.3 — the root help points at the monitoring command](1.60.3-observability-help-names-the-monitoring-verb.md)
- [1.60.2 — `serve observability counters` names every registered meter](1.60.2-observability-counters-flag.md)
- [1.60.1 — the project id repair counts open workspaces](1.60.1-repair-counts-open-workspaces.md)
- [1.60.0 — project id repair uses the default alias map](1.60.0-project-id-repair-default-map.md)
- [1.59.5 — MCP tool inventory is generated at compile time](1.59.5-mcp-tool-inventory-generated.md)
- [1.59.4 — vector search reads only its own context](1.59.4-partitioned-vector-search.md)
- [1.59.3 — concurrent searches run in parallel](1.59.3-parallel-searches.md)
- [1.59.2 — `watch remove` says when nothing was watched; a missing encryption key is named](1.59.2-watch-remove-and-missing-key-messages.md)
- [1.59.1 — a tool call the caller gave up on is answered, not logged as a stack trace](1.59.1-caller-cancelled-calls-answered.md)
- [1.59.0 — register watches from a terminal; notebooks are indexed; skipped files are reported](1.59.0-watch-cli-and-notebooks.md)
- [1.58.1 — SQLite3 Multiple Ciphers 2.6.1 and the upstream pool fix](1.58.1-sqlite-engine-and-pool-fix.md)
- [1.58.0 — the 1Password CLI can key the bank](1.58.0-1password-key-source.md)
- [1.57.6 — concurrent bank opens no longer share a pooled connection](1.57.6-serialized-pooled-opens.md)
- [1.57.5 — the file watcher ignores directory-name notifications](1.57.5-watcher-ignores-directory-events.md)
- [1.57.4 — the file watcher's event buffer is 64 KB](1.57.4-file-watcher-buffer-64kb.md)
- [1.57.3 — an inconclusive probe says why no backend was started](1.57.3-inconclusive-probe-names-its-reason.md)
- [1.57.2 — failed identification no longer starts another server](1.57.2-proxy-refuses-unproven-backend.md)
- [1.57.1 — recover CUDA failures during embedding](1.57.1-cuda-runtime-fallback.md)
- [1.57.0 — reads need a registered project id too](1.57.0-reads-need-a-registered-project.md)
- [1.56.1 — long queries no longer time out in keyword search](1.56.1-long-query-fts-cap.md)
- [1.56.0 — stop-path secrets ride the proven connection; cancel-safe writes everywhere](1.56.0-stop-path-token-and-cancel-safe-writes.md)
- [1.55.6 — a named backend executable is never silently replaced](1.55.6-named-executable-never-swapped.md)
- [1.55.5 — `doctor` handler split into named steps](1.55.5-doctor-run-split.md)
- [1.55.0 — re-chunk phase instrumentation](1.55.0-rechunk-phase-instrumentation.md)
