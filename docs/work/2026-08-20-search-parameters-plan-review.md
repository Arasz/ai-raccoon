# Plan review — `search-parameters` (2026-08-20)

Plan reviewed: `docs/work/2026-08-20-search-parameters-plan.md`
MoE: 2 parallel lanes — architect (design/decomposition) + code-reviewer (correctness/
gates/claims). Delegation `deleg_1f7486a0`. Both lanes reviewed the plan rev 1 draft and
verified its factual claims against `src/`, `tests/`, `docs/adr/`, and `integrations/`.
No files were modified by the lanes. All findings folded into plan rev 2 (same file).

## Verdict: APPROVE-WITH-CHANGES (both lanes)

The design goal (one resolved `SearchParameters` record; query > settings > default
precedence; settings interface for all options) is sound, layering-clean, and the
factual base of the plan verifies — with two MUST-FIX design issues that rev 2
resolves by taking explicit positions (eager batched resolution + amended ADR-0078
note), and several SHOULD-FIX/gap items folded.

---

## Findings (folded into plan rev 2)

### MUST-FIX

| id | Finding | Fold decision (plan rev 2) |
|---|---|---|
| M1 (both lanes) | Sync `FromSources` + sync property getters cannot be backed by async settings reads; per-option `ISettingsStore` reads open a new bank connection each (up to 9 per search vs today's 0-2 SELECTs on the search's own connection). | Async lives in the store: connection-scoped, **batched** `GetSearchParameterDefaultsAsync(SqliteConnection, ct)` — 2 SELECTs (`retrieval.%` + fusion key) on the search's already-open connection. `FromSources(params ISearchParametersSource[])` stays sync and pure over the query + the defaults snapshot. §3.2(3). |
| M2 (architect) / M1 (reviewer) | Eager one-shot resolution defeats the two lazy-read properties (alpha read only when a query vector exists; fusion flag read only when ≥2 legs contribute, per ADR-0078 "pays nothing at all"). | Decision taken: **eager, batched** — 2 indexed SELECTs on every search; the flag's ≥2-legs condition survives as the *application* condition; ADR-0078 note and `SqliteMemoryStore.Search.cs:93-96` comment amended; a read-count test pins the cost. §3.2(3), §4 WP3, §6 risk 2. |
| M2 (reviewer) | "All existing search tests pass unchanged" is false: `MemoryToolsTests.Search_WithoutFusionParameters_AppliesDefaults` (asserts `RrfK == DefaultRrfK` on a now-nullable field) and `SearchQueryTests` default-constructor assertions fail; four test files reference `SearchQuery.DefaultRrfK` whose fate was unplanned. | `SearchQuery.DefaultRrfK` survives as the canonical constant; affected files enumerated in WP2 (`SearchQueryTests`, `ReorderSurvivalThroughMergeTests`, `ScoreInjectionTests`, `SearchResultMergerTests`, `MemoryToolsTests`); §7 reworded to "pass with the enumerated updates; behavior unchanged". §4 WP2. |

### SHOULD-FIX

| id | Finding | Fold decision |
|---|---|---|
| S1 (architect) | "Eight tunables" is off-by-one: seven move (MinRelativeScore stays response shaping). | Count corrected to seven everywhere. §3.2(1). |
| S2 (architect) / G1 (reviewer) | MCP wire shape of the two enum knobs unspecified; SDK enums serialize as opaque ordinals; contract test pins type only, not values; declaration order is contract-sensitive. | Enums exposed as **strings** (`"max"|"sum"`, `"max3x100"|"max5x50"`), parsed in the tool; inserted after `vectorWeight`, before `contextLabel`; contract test extended to pin the enum value lists. §3.3, §4 WP4. |
| S3 (architect) / S1 (reviewer) | Validation moves off the fail-fast tool layer (post bank-open/embedding); invalid-params mapping survives only incidentally. | Two-layer: tool validates *provided* values before any bank work (fail-fast preserved) + resolved record validates at resolution (defense in depth). MCP test pins `rrfK=0`/`lambda=2` → `invalid-params` pre-bank. §3.3, §4 WP4. |
| S4 (architect) / S3 (reviewer) | Hermes-plugin risk overstated: the plugin hand-curates `TOOL_SCHEMAS`, passes explicit args, never sends the knobs — no re-generation needed. | Risk 2 corrected; plugin changes are an explicit **non-goal** (exposing the new knobs to Hermes = follow-up). §5, §6 risk 3. |
| S5 (architect) | WP3 hits the size ratchet (996/1066 lines) without mentioning it. | New partial `SqliteMemoryStore.SearchParameters.cs` (seam precedent: Rows/Search); ratchet green added to WP3's gate. §3.2(3), §4 WP3. |
| S2 (reviewer) | Contract-test rationale mis-stated (nullability changes nothing on the existing line — it already renders `integer?`; only the 4 new params change it); schema defaults pinned by no test. | Plan §2 corrected; WP1 adds a constants-pin test asserting the nine fallback values against ADR-0006. §4 WP1. |

### Gaps

| id | Finding | Fold decision |
|---|---|---|
| G1 (architect) | `[Description]` texts pin stale defaults; contract tests ignore descriptions, so drift is silent. | WP4 updates the three existing descriptions to name precedence + writes accurate descriptions for the four new knobs (incl. enum wire values). |
| G2 (architect) | "All constants in Core" conflicts with `StructureFusion.AlphaSettingKey/DefaultAlpha` living in Infrastructure. | Constants move to `SearchParameterSettingsKeys` (Core); `StructureFusion` references Core. §3.1. |
| G3 (architect) | Gates name a non-existent test project (`AiRaccoon.Tests.Unit`). | Gates reworded to `dotnet test --filter Category=Unit` / `Category=Integration` (single test project, Trait categories). §4. |
| G2 (reviewer) | `show-all` is an acceptance criterion but no work package builds it. | Added to WP4 (verb + round-trip test), mirroring the alpha/fusion pattern. §3.4. |
| G3 (reviewer) | Acceptance grep omits the fusion flag / consolidation / docScoreFormula — would pass with inline reads intact. | Extended grep covers all nine options. §7. |
| G4 (reviewer) | WP3's gate needs settings rows on real banks; no such test plumbing exists (settings writes today live in Unit fake-store tests). | WP3 names a seeded-bank fixture writing settings via `ISettingsStore` on the harness bank before `SearchAsync`. §4 WP3. |

---

## Verified claims (both lanes, source-checked)

| Claim | Verdict | Evidence |
|---|---|---|
| One production `new SearchQuery(` site | VERIFIED | sole src hit `MemoryTools.cs:141` (others are tests/benchmarks) |
| One `store.SearchAsync(` caller | VERIFIED | sole src hit `MemoryTools.cs:158` |
| `McpToolContractTests` pins the schema; nullability changes it | PARTIALLY VERIFIED | pins names/types/required only (`Describe` reads type+required); the `rrfK` line already renders `integer?` — only the 4 new params change it; no test pins schema defaults |
| ADR-0006: k=60, 1:1 weights, `max(limit×3,100)` | VERIFIED | ADR-0006 Decision; `SearchQuery.cs:12,21`; `SqliteMemoryStore.Search.cs:25-28` |
| ADR-0006 in-sample caveat → ADR-0056 | VERIFIED | ADR-0006 amendment 2026-08-15 |
| Settings keys + inline store reads (`retrieval.structureAlpha`, `fusion.noRegression.enabled.global`); CLI verbs exist | VERIFIED | `StructureFusion.cs:17-20`, `FusionConfigKeys.cs:9-15`, `SqliteMemoryStore.Search.cs:81-103`, `CliCommandTree.cs:182-197`, `SettingsCommands.cs:181-216` |
| λ/consolidation/formula/window hardcoded, no key, no exposure | VERIFIED | `SearchQuery.cs:16-19`; tool signature `MemoryTools.cs:100-129`; no `retrieval.sourceLambda` keys anywhere |
| `ISettingsStore` surface (Get/Set/GetByPrefix/Delete) | VERIFIED | `ISettingsStore.cs:9-16` |
| Nine fallback constants match plan (60/1/1/0.1/0.1/Max/Max3X100/0.5/false) | VERIFIED | `SearchQuery.cs`, `StructureFusion.cs:17`, `FusionConfigKeys.cs:11` |
| "All existing search tests pass unchanged" | PARTIALLY VERIFIED — **false as written** | Integration tests keep identical behavior (no settings rows on harness banks), but unit tests assert `SearchQuery` defaults (`MemoryToolsTests.cs:429-436`, `SearchQueryTests.cs:24-36`) → M2 (reviewer) |
| Hermes plugin relies on schema defaults (re-generation needed) | **NOT VERIFIED — overstated** | plugin passes explicit args (`client.py:143`, `__init__.py:117`); tests pass `{query,limit,minRelativeScore}`; no schema-default consumption found → S4/S3 |
| Candidate window flows via `CandidateWindowFor` | VERIFIED | `SqliteMemoryStore.Search.cs:25-28`, `SqliteMemoryStore.cs:656` |
| Fusion flag key kept for back-compat | VERIFIED (as plan design) | key used everywhere today; no rename regresses |

---

## What the review did NOT cover

- Whether the nine default VALUES are the right ones (explicitly out of scope —
  retuning belongs to ADR-0056's gate, per plan §5).
- The embedding model and the fusion-reorder logic (unchanged, per plan §5).
