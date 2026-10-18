# Research — default map for project-id repair

Date: 2026-10-19
Task: `air-repair-project-ids-use-default-map`

## Evidence

- `~/.ai-raccoon/project-id-map.json` exists on this machine and is the operator-maintained map used for the local repair workflow. It contains 19 aliases, 14 canonicals, and 32 dropped ids; no map contents or identifiers are copied into this repository.
- The separate `~/.ai-raccoon/project-id-map.template.json` is a generated review template, not the applied operator map.
- `docs/work/2026-10-04-unregistered-project-id-research.md` documents the same local map path and use with `repair project-ids --apply`.
- `docs/adr/0099-empty-default-alias-map.md` keeps the shipped/public alias map empty to avoid embedding machine-specific folds.
- `ProjectIdsRepairCommands` already receives `dataRoot` and writes a map-less template beside the bank. Therefore the stable default is `<data-root>/project-id-map.json`; `--map <path>` remains an explicit override.

## Decision direction

Load the local default map only when it exists. If neither `--map` nor the default file exists, retain empty-map/no-fold planning and tell the operator where to save a reviewed map. Keep the generated template filename separate so creating a template never enables folds implicitly.
