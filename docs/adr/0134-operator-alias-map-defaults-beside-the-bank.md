# 0134 — Operator alias map defaults beside the bank

Date: 2026-10-19

Status: Accepted

## Context

ADR-0099 keeps the public binary's alias map empty. Operators who already maintain a local
map must currently pass its path on every `repair project-ids` invocation. Omitting `--map`
plans no folds and writes a separate review template beside the bank, which is easy to mistake
for the map to apply.

## Decision

When `--map <path>` is omitted, `repair project-ids` loads
`<data-root>/project-id-map.json` if that file exists. An explicit `--map` always takes
precedence. If neither is available, repair continues to use the empty map and reports that
no folds are planned, with a hint to the default path. The generated
`<data-root>/project-id-map.template.json` remains a review template and is never loaded
implicitly.

## Consequences

- Operators can save their reviewed alias map beside the bank and use the same default for
  dry runs and apply runs.
- The binary still contains no machine-specific project ids; maps remain operator-owned files.
- A malformed default map fails like a malformed explicit map, instead of silently falling
  back to no folds.
