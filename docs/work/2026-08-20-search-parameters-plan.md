# SearchParameters — unified retrieval-parameter source (plan)

Task: unify every parameter that shapes a search behind one resolved record.
Date: 2026-08-20. Plan-only (main is mid-fix; no implementation until review lands and the owner says go).
Status: **plan rev 2** — MoE review folded (APPROVE-WITH-CHANGES; record:
`docs/work/2026-08-20-search-parameters-plan-review.md`). MUST-FIX folded: M1 (async +
connection-scoped resolution), M2 (eager-vs-lazy read invariant decided). SHOULD-FIX
folded: S1 (seven, not eight), S2 (enum wire shape = strings), S3 (fail-fast validation
kept at the tool layer), S4 (Hermes-plugin risk corrected; plugin = stated non-goal),
S5 (size-ratchet seam). Gaps folded: G1 (descriptions + enum values pinned), G2
(constants move to Core), G3 (executable gates), G4 (settings-row test plumbing),
plus the reviewer's G1-G4 (param order, show-all verb, acceptance grep, integration
helper).

## 1. Why

The 2026-08-20 hybrid-retrieval investigation
(`docs/work/2026-08-20-hybrid-retrieval-fusion-investigation.md`) surfaced the current
parameter topology as the problem: three retrieval knobs are per-call (`rrfK`,
`ftsWeight`, `vectorWeight`), two are settings-table toggles read inline in the store
(`fusion.noRegression.enabled.global`, `retrieval.structureAlpha`), and three are
hardcoded C# defaults nobody can change without a build (`SourceLambda = 0.1`,
`ConsolidationThreshold = 0.1`, `DocScoreFormula = Max`, plus `CandidateWindowMode`).
The store reads its own settings inline (`ReadStructureAlphaAsync`,
`NoFusionRegressionEnabledAsync`), and `SearchQuery` mixes query identity with tuning
defaults. There is no single place that answers "what parameters did this search run
with?".

Goal: one resolved `SearchParameters` record every search runs on, assembled by
`SearchParameters.FromSources(query, defaults)` — the query's values win where
provided, the store supplies defaults (settings where set, code constants otherwise) —
plus a settings interface covering all options, so every knob becomes runtime-
configurable and the search pipeline stops reading settings inline.

## 2. Current state (verified by both review lanes)

| Parameter | Current home | Runtime-configurable? | Exposed per call? |
|---|---|---|---|
| `RrfK` (60) | `SearchQuery` default | no | yes (`memory_search`) |
| `FtsWeight` (1) | `SearchQuery` default | no | yes |
| `VectorWeight` (1) | `SearchQuery` default | no | yes |
| `MinRelativeScore` (0.0) | `SearchQuery` default | no | yes — **not an option** (response shaping) |
| `SourceLambda` (0.1) | `SearchQuery` default | **no — hardcoded** | no |
| `ConsolidationThreshold` (0.1) | `SearchQuery` default | **no — hardcoded** | no |
| `DocScoreFormula` (Max) | `SearchQuery` default | **no — hardcoded** | no |
| `CandidateWindowMode` (Max3X100) | `SearchQuery` default; used via `CandidateWindowFor` | no | no |
| `StructureAlpha` (0.5) | settings key `retrieval.structureAlpha`, read inline in store (only when a query vector exists) | yes (CLI `settings retrieval alpha`) | no |
| Fusion no-regression flag (false) | settings key `fusion.noRegression.enabled.global`, read inline in store (only when ≥2 legs contribute) | yes (CLI `settings retrieval fusion`) | no |
| `Limit`, `Scope`, `WorkspaceId`, `ContextLabel`, `ProjectId`, `Query` | `SearchQuery` | n/a — query identity / response shaping | yes |

Verified facts (both lanes, source-checked): exactly one production `new SearchQuery(`
site (`src/AiRaccoon/Tools/MemoryTools.cs:141`) and one `store.SearchAsync(` caller
(`MemoryTools.cs:158`); `ISettingsStore` (Core) exposes Get/Set/GetByPrefix/Delete;
`McpToolContractTests` pins names/types/required-ness only — the existing `rrfK` line
already renders `integer?`, so nullability changes nothing there, only the four NEW
params do; **no test pins schema `default` values**; `SqliteSettingsStore.GetSettingAsync`
opens a new bank connection per call; the store partials live under
`src/AiRaccoon.Infrastructure/Sqlite/Memory/`; ADR-0006 chose k=60 / 1:1 weights /
`max(limit*3, 100)`; ADR-0078 documents "a single-leg or degraded search pays no extra
bank read at all" for the fusion flag; `SqliteMemoryStore.cs` is at 996/1066 lines
(size ratchet); the Hermes plugin hand-curates `TOOL_SCHEMAS` and never sends the three
tuning knobs.

## 3. Target design

### 3.1 Core (`AiRaccoon.Core.Memory.SearchParameters`)

**`ISearchParametersSource`** — one nullable property per retrievable option. Null =
"no opinion; let the next source decide". Identity fields (projectId/query/scope/
workspace/contextLabel/limit/minRelativeScore) are NOT options: they are per-call
request shape, not retrieval tuning.

```csharp
public interface ISearchParametersSource
{
    int? RrfK { get; }
    int? FtsWeight { get; }
    int? VectorWeight { get; }
    double? SourceLambda { get; }
    double? ConsolidationThreshold { get; }
    DocScoreFormula? DocScoreFormula { get; }
    CandidateWindowMode? CandidateWindow { get; }
    double? StructureAlpha { get; }
    bool? FusionNoRegressionEnabled { get; }
}
```

**`SearchParameters`** — the resolved record: non-nullable values + validation.

```csharp
public sealed record SearchParameters(
    int RrfK, int FtsWeight, int VectorWeight,
    double SourceLambda, double ConsolidationThreshold,
    DocScoreFormula DocScoreFormula, CandidateWindowMode CandidateWindow,
    double StructureAlpha, bool FusionNoRegressionEnabled)
{
    public static SearchParameters FromSources(params ISearchParametersSource[] sources);
    // Sync, pure. First non-null wins, left to right; zero sources throws.
    // After resolution every value is validated (rules mirror the current
    // SearchQuery validator: RrfK > 0, weights >= 0, lambda in [0,1], ...).
}
```

Precedence contract: `FromSources(query, defaults)` — per-call values win; the store's
defaults supply settings-where-set, constants otherwise.

**`ISearchParametersSettings`** — the settings interface covering all options (one
nullable `Task<T?>` per option), for CLI/ops consumers.

**Constants** — `SearchParameterSettingsKeys` in Core: all keys + `Parse…` helpers +
`Default…` fallbacks, mirroring the `FusionConfigKeys` pattern. Keys (new ones under
the existing `retrieval.` namespace):
`retrieval.rrfK`, `retrieval.ftsWeight`, `retrieval.vectorWeight`,
`retrieval.sourceLambda`, `retrieval.consolidationThreshold`,
`retrieval.docScoreFormula`, `retrieval.candidateWindow`,
`retrieval.structureAlpha` (moved here from `StructureFusion.cs`, which then
references Core), and `fusion.noRegression.enabled.global` (existing key, kept for
back-compat; namespace inconsistency noted, not fixed, in this plan).
`SearchQuery.DefaultRrfK` **survives** as the canonical constant (referenced by
`SearchParametersSettingsKeys` and four test files — no breakage).

### 3.2 Implementations

1. **`SearchQuery`** (first `ISearchParametersSource`) — the **seven** tunable
   properties become nullable (`int? RrfK = null`, …). `MinRelativeScore` stays
   non-nullable (response shaping). `StructureAlpha` / `FusionNoRegressionEnabled`
   return null (bank policy — never per-call). The `Validator` narrows to the
   non-nullable fields; the resolved record validates the rest.
2. **`SqliteSearchParametersSettings : ISearchParametersSettings`** (Infrastructure) —
   per-option reads via `ISettingsStore`; used by the CLI verbs. **Not** used on the
   per-search path (see 3.2.3 — that path is connection-scoped).
3. **`SqliteMemoryStore`** (second `ISearchParametersSource` provider) — a new partial
   `SqliteMemoryStore.SearchParameters.cs` (size-ratchet seam; the main file is at
   996/1066 lines) implements:
   - `Task<ISearchParametersSource> GetSearchParameterDefaultsAsync(SqliteConnection
     connection, CancellationToken ct)` — **connection-scoped and batched**: two
     SELECTs on the search's already-open connection (one `retrieval.%` prefix read,
     one `fusion.noRegression.enabled.global` read), returning a private
     `SettingsBackedSearchParameters` record (all nine values non-null; absent or
     malformed settings fall back to the `Default…` constants).
   - `SearchAsync` resolves **once, as its first statement**:
     `var parameters = SearchParameters.FromSources(query, await
     GetSearchParameterDefaultsAsync(connection, ct));` — and every internal read
     (candidate window, structure alpha, fusion flag, merger parameters) consumes
     `parameters`.
   - `ReadStructureAlphaAsync` and `NoFusionRegressionEnabledAsync` are deleted; the
     flag's "only when ≥2 legs contribute" condition survives as the *application*
     condition at the fusion post-pass (it is fusion-shape logic, not a settings
     read).
   - **Decision (M1+M2, stated): eager, batched resolution.** Cost: 2 indexed SELECTs
     on every search vs today's 0-2 conditional reads on the same connection. The
     ADR-0078 note ("pays no extra bank read at all") and the comment at
     `SqliteMemoryStore.Search.cs:93-96` are **amended** to "one batched settings read
     per search; single-leg/degraded searches pay no search-shape work for the flag".
     A WP3 test pins the read count (see §4 WP3).

### 3.3 MCP tool (`MemoryTools.Search`)

The seven tunable params become nullable in the tool signature; four new ones are
exposed, inserted **after `vectorWeight`, before `contextLabel`** (declaration order
is contract-test-sensitive):
`double? sourceLambda`, `double? consolidationThreshold`,
`string? docScoreFormula` (**wire shape: strings** `"max"|"sum"`, parsed in the tool —
never integer ordinals; matches the CLI verb naming), `string? candidateWindow`
(`"max3x100"|"max5x50"`, parsed likewise). `structureAlpha` and the fusion flag stay
settings-only (bank policy). Tool-layer validation of the *provided* values runs
before any bank work (preserves today's fail-fast `invalid-params` for e.g.
`rrfK=0`, `lambda=2`); the resolved record validates again at resolution (defense in
depth for settings values). Existing `[Description]` texts that pin defaults are
updated to name the precedence ("bank setting, else 60"); new descriptions name the
enum wire values.

### 3.4 CLI (`settings retrieval`)

New verb families mirroring the existing `alpha`/`fusion` pattern (per option:
`set`/`show`), plus **`settings retrieval show-all`** printing every option with its
source (setting / default) — one call answers "what does a search run with?".

## 4. Work packages (each: TDD — failing test first — and its gate)

**WP1 — Core contracts.** `ISearchParametersSource`, `SearchParameters` +
`FromSources` + validator, `ISearchParametersSettings`,
`SearchParameterSettingsKeys` (keys/parse/fallback; alpha constants moved from
`StructureFusion`). Gate: unit tests — precedence (query > settings > default, per
option), first-non-null order, zero-sources throws, validator rules, and a
**constants-pin test asserting the nine fallback values (60/1/1/0.1/0.1/Max/
Max3X100/0.5/false) against ADR-0006's chosen values** (nothing pins these today —
reviewer S2). Run: `dotnet test --filter Category=Unit`.

**WP2 — SearchQuery as source.** Seven tunables nullable; interface implementation;
validator narrowed; `SearchQuery.DefaultRrfK` kept. Affected tests enumerated and
updated: `SearchQueryTests` (default-constructor assertions move to resolved-record
tests; validator-rule tests for the seven tunables move to `SearchParameters`'s
validator — reviewer S1), `ReorderSurvivalThroughMergeTests`, `ScoreInjectionTests`,
`SearchResultMergerTests` (keep referencing `SearchQuery.DefaultRrfK`), and
`MemoryToolsTests.Search_WithoutFusionParameters_AppliesDefaults` (asserts null
passthrough + resolution through the store — reviewer M2). Gate:
`dotnet test --filter Category=Unit`; `QueryConstructionTests` green.

**WP3 — Settings implementation + store.** `SqliteSearchParametersSettings`;
`SqliteMemoryStore.SearchParameters.cs` partial with the connection-scoped batched
defaults + `SearchAsync` resolution; inline settings reads deleted; size-ratchet
seam. Gates: store integration tests — settings-set values override defaults, query
overrides settings, using a **new seeded-bank fixture that writes settings rows via
`ISettingsStore` on the harness bank before `SearchAsync`** (reviewer G4; no such
plumbing exists today); a **settings-read-count test** (counting settings store:
single-leg search performs exactly one `retrieval.%` batch + one fusion-key read, and
search-shape work stays conditional — reviewer M1); `SqliteMemoryStoreSizeRatchetTests`
green. Run: `dotnet test --filter Category=Unit|Integration`, then CI (slow suite).

**WP4 — MCP tool + CLI.** Nullable params + four new knobs (string enums) in the
declared order; tool-layer validation of provided values; `[Description]` updates;
`settings retrieval` verbs incl. `show-all`; `McpToolContractTests` updated — the
memory_search contract line changes **only because of the four new params**, and the
enum params' value lists are pinned in the contract test (extend `Describe` to emit
enum values — reviewer S2/G1); CLI set/show/show-all round-trip tests; a new MCP-level
test for `rrfK=0` / `lambda=2` → `invalid-params` **before** any bank work (reviewer
S3). Gate: contract + CLI tests green; `mcp-tool-surface-testing` shows the new
params. Run: `dotnet test --filter Category=Unit`.

**WP5 — Docs.** ADR for the parameter-source design (amends ADR-0006's "parameters
stand unchanged" note: values unchanged, provenance now query>settings>default) +
**ADR-0078 cost-note amendment** (eager batched settings read per search) +
`docs/reference/` search-parameters page. Gate: owner doc review; no code.

## 5. Explicit non-goals

- **No value changes**: k=60, 1:1 weights, λ=0.1, consolidation 0.1, Max, Max3X100,
  alpha 0.5 remain the defaults; retuning is ADR-0056's gate, not this plan's.
- No embedding-model work; no fusion-reorder logic changes (the ≥2-legs application
  condition is preserved).
- `fusion.noRegression.enabled.global` keeps its name (back-compat).
- No per-call exposure of `structureAlpha`/fusion flag (bank policy).
- **Hermes plugin `integrations/hermes/ai-raccoon` is unchanged** (reviewer S3: it
  hand-curates `TOOL_SCHEMAS`, passes explicit args, and never sends the tuning knobs
  — nothing breaks, no re-generation needed). Exposing the four new knobs to Hermes
  models is a follow-up, deliberately out of scope while main is mid-fix.

## 6. Risks / decisions (reviewed; positions taken)

1. **Nullable-as-"provided"** is the load-bearing semantic; no current call site needs
   explicit-set-to-default vs not-provided (the only constructor passes MCP values
   through). Accepted.
2. **Eager batched resolution costs 2 settings SELECTs per search** (vs today's 0-2
   conditional). Accepted with the ADR-0078 amendment; bounded, indexed, on the
   search's own connection; read-count test pins it.
3. **Schema change**: `memory_search` gains four params; defaults are no longer
   claimed by the schema (they were never pinned by any test). The in-repo Hermes
   plugin is unaffected (non-goal).
4. **Enum wire shape**: strings with pinned value lists, parsed in the tool — clients
   never guess ordinals.
5. Malformed settings values fall back to constants (`FusionConfigKeys` pattern) —
   a malformed setting can never crash a search.

## 7. Acceptance criteria

- `SearchParameters.FromSources(query, defaults)` is the ONLY way a search's options
  are resolved; the extended grep `rrfK|SourceLambda|ConsolidationThreshold|
  DocScoreFormula|CandidateWindow|StructureAlpha|FusionNoRegression|noRegression`
  shows only `parameters.` reads (plus the documented `GetSearchParameterDefaultsAsync`
  site) in the store's search path (reviewer G3 — the earlier grep missed the flag).
- Every option in §3.1 has a settings key and a CLI verb; `settings retrieval
  show-all` prints each with its source.
- `memory_search` schema exposes the four new knobs (string enums with pinned value
  lists); `McpToolContractTests` green.
- All existing search tests pass **with the enumerated updates in WP2/WP4**; end-to-end
  behavior unchanged (same defaults).
- The settings-read-count test proves single-leg searches pay exactly one batch + one
  fusion-key read; no per-option connection opens on the search path.
- The constants-pin test asserts the nine fallback values against ADR-0006.
- No new hardcoded search tuning outside the `SearchParameterSettingsKeys` fallback
  block.
