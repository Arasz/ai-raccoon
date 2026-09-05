# ADR-0099: the public binary ships an empty project-id alias map

**Status:** accepted · **Date:** 2026-09-04 · **Task:** `air-remove-machine-default-alias-map`

## Context

`ProjectIdAliasMap.Default` compiled one machine's ids into the public binary
(`job-search-ai-assistant → jsaa`, `AI-RACCOON → ai-raccoon`, nine canonicals, two
drops). Every steady-state choke point consumed it — `ToolGate`, watch boundaries,
ingest/access/watch key helpers, the `watch registered` filter — plus the repair
planner, the repair job, and the sync pull fold. A second machine-local table
(`CandidateFeatures.ProjectAliases`) shipped alongside it. Settings rows never cross
sync, so a settings-backed map would diverge per replica silently; a new bank table
would move `SchemaDigest` for no benefit. CLI and server are separate processes
(HTTP via `RepairProtocol`), so a `--map <path>` on the CLI side is meaningless to
the server unless the content crosses the existing outbox.

## Decision

- `Default = Empty`. Steady-state folds are defined pass-through (guid D-form
  normalization only). Post-repair loser writes are refused by the existing
  registration guard on writes instead of silently folded.
- Maps are one-shot file-loaded parameters: `repair project-ids --map <path>`
  (`ProjectIdAliasMap.LoadFromFile`), transported CLI → server as
  `repair_requests.map_json` (nullable `TEXT` column, v12 → v13 ladder plus the
  digest-path ensure; the row is already sync-stripped so maps never leave the
  machine). The job re-derives its plan from a live census at apply time — never a
  replay of the CLI's dry-run plan.
- A dry run without `--map` plans with the empty map (zero folds) and writes a
  non-overwriting editable template to `<data-root>/project-id-map.template.json`
  (exact payload shape, pretty-printed). No auto-suggested folds: pre-filling from
  case-insensitive collisions conflicts with the recorded `Ordinal`
  case-sensitivity (d-425 SHOULD-5) and pushes operators toward data loss; the
  dry-run's unresolved worklist with per-guid registered-name hints is the
  attribution instrument.
- `--apply` without `--map` stays legal (null map ≡ empty; a clean bank no-ops and
  still stamps the finished marker). Missing/malformed `--map` fails with
  `ExitCode.InvalidArgument` and never requests a repair; malformed `MapJson` over
  the wire is a 400.
- The sync fold gains a `(column, map)` overload; the empty map degrades to the
  remote-projects name resolution alone (SQLite rejects a `CASE` with zero `WHEN`
  arms — verified, not assumed).
- `CandidateFeatures.ProjectAliases` is deleted (matching falls back to the bare
  id). Measured impact: the `airaccoon`-without-hyphen synonym leg no longer
  counts as `ForeignSubject`; bare-id mentions are unaffected.

## Consequences

**+** The public lib carries zero machine ids (grep gate over `src/`).
**+** One table forever: the empty singleton plus ephemeral per-invocation maps —
  no divergent state, no new bank table, no lingering sidecars.
**+** The outbox row is kept post-finish as the audit of which map a repair ran with.
**−** One `CurrentVersion` bump plus `IRepairStore`/endpoint churn.
**−** Operators with existing fragment ids must run one mapped repair (one-time cost).
**−** Sync no longer converges other replicas' loser ids (honest: cross-replica
  convergence for arbitrary ids was never possible without the hardcoded table).
**Neutral:** the `Default` member is retained as the empty singleton so call sites
  keep their shape; machine ids live on as test-fixture data only.

## Alternatives considered

- Auto-suggest pre-fill (rejected: conflicts with the `Ordinal` decision, data-loss risk).
- Persistent map via settings / DI provider / sidecar file (rejected: lingering
  divergent state, TOCTOU, per-call file IO on hot paths).
- POSTing an explicit fold plan (rejected: the job must re-derive live at apply
  time; bigger contract for a narrower guarantee).
- Synthetic-seed synonym table for the scorer (rejected: fake data teaches
  nothing; mechanism without data is dead code).
