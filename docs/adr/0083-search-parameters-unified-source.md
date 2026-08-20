# 0083. SearchParameters — one resolved record for every search

Date: 2026-08-20

## Status

Accepted. Plan: `docs/work/2026-08-20-search-parameters-plan.md` (rev 2, MoE-reviewed,
APPROVE-WITH-CHANGES — review record `docs/work/2026-08-20-search-parameters-plan-review.md`).

## Context

The 2026-08-20 hybrid-retrieval investigation
(`docs/work/2026-08-20-hybrid-retrieval-fusion-investigation.md`) surfaced the parameter
topology as the problem: three retrieval knobs were per-call (`rrfK`, `ftsWeight`,
`vectorWeight`), two were settings-table toggles read inline in the store
(`fusion.noRegression.enabled.global`, `retrieval.structureAlpha`), and three were
hardcoded C# defaults nobody could change without a build (`SourceLambda = 0.1`,
`ConsolidationThreshold = 0.1`, `DocScoreFormula = Max`, plus `CandidateWindowMode`).
`SearchQuery` mixed query identity with tuning defaults, and no single place answered
"what parameters did this search run with?".

## Decision

1. **`ISearchParametersSource`** (Core) — one nullable property per retrievable option.
   Null = "no opinion; the next source decides". Query identity and response shaping
   (projectId, query, scope, workspace, contextLabel, limit, minRelativeScore) are not
   options.
2. **`SearchParameters`** (Core) — the resolved record: non-nullable values, validated.
   `SearchParameters.FromSources(params ISearchParametersSource[])` — sync and pure:
   first non-null wins, left to right; options no source provides fall back to the
   canonical constants in `SearchParameterSettingsKeys`; zero sources throws; the
   resolved record is validated (rules mirror the former `SearchQuery` tunable rules).
3. **Precedence contract**: `FromSources(query, defaults)` — per-call values win; the
   store's defaults supply settings-where-set, constants otherwise.
4. **`SearchQuery`** is the per-call source: its seven tunables became nullable and it
   implements `ISearchParametersSource`; `StructureAlpha`/`FusionNoRegressionEnabled`
   report null (bank policy, never per-call). `SearchQuery.DefaultRrfK` survives as the
   canonical constant.
5. **`SqliteMemoryStore`** resolves `SearchParameters.FromSources(query, defaults)` as
   the first statement of `SearchAsync`, with `GetSearchParameterDefaultsAsync`
   (Infrastructure, new partial) reading the settings **batched on the search's own
   connection**: one `retrieval.%` prefix read + one `fusion.noRegression.enabled.global`
   read. Absent or malformed settings read as null and fall back to the constants — a
   bad setting can never crash a search. Every internal read (RRF k/weights, source
   affinity lambda/consolidation/formula, candidate window, structure alpha, fusion
   flag) consumes the resolved record; `ReadStructureAlphaAsync` and
   `NoFusionRegressionEnabledAsync` were deleted.
6. **Settings surface** — `SearchParameterSettingsKeys` (Core) owns all keys, parse
   helpers and defaults; `ISearchParametersSettings` (Core) exposes per-option async
   reads for CLI/ops consumers; CLI verbs `settings retrieval
   rrfk|fts-weight|vector-weight|source-lambda|consolidation|doc-formula|window
   set|show` plus `show-all`; `memory_search` exposes all seven knobs as nullable
   params, the two enums as wire strings (`"max"|"sum"`,
   `"max3x100"|"max5x50"`), validated fail-fast at the tool layer.
7. **Cost decision (amends the ADR-0078 "pays nothing" note)**: the settings reads are
   now eager and batched — two indexed SELECTs on every search's already-open
   connection, versus the previous 0-2 conditional reads. The flag's **application**
   stays conditional on two contributing legs (a single-leg search does no reorder
   work); only the read became unconditional. ADR-0078's cost paragraph is amended
   accordingly.

## Consequences

- **Positive**: every knob is runtime-configurable (settings table + CLI), per-call
  overrideable, and the search pipeline has no inline settings reads left; one place
  answers "what did this search run with?" (`settings retrieval show-all`).
- **Positive**: malformed settings degrade to constants, never to crashes; the
  constants are pinned against ADR-0006 by a test.
- **Negative**: every search now pays two settings SELECTs (was 0-2 conditional); the
  ADR-0078 note is amended, not preserved.
- **Negative**: `memory_search`'s schema no longer claims defaults for the seven knobs
  (they were never pinned by any test; the in-repo Hermes plugin passes explicit args
  and is unaffected).
- **Not addressed**: the nine default VALUES are unchanged (k=60, 1:1 weights, λ=0.1,
  consolidation 0.1, Max, Max3X100, alpha 0.5, flag false) — retuning belongs to
  ADR-0056's gate. The embedding model and the fusion-reorder logic are untouched.
  The `fusion.noRegression.enabled.global` key keeps its name (back-compat).

Amends ADR-0006 (provenance of the parameters: query > settings > constants) and
ADR-0078 (cost note).
