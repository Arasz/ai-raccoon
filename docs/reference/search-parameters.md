# Search parameters — what a search runs with

Every search resolves one `SearchParameters` record: `SearchParameters.FromSources(query, defaults)`
— the per-call values win where provided, otherwise the bank's settings, otherwise the canonical
constants (ADR-0083, docs/adr/0083-search-parameters-unified-source.md).

## Precedence

1. `memory_search` arguments (per call)
2. settings table (`settings retrieval …`, bank-wide)
3. canonical constants (ADR-0006's chosen values)

An absent or malformed setting falls back — it can never fail a search.

## Options

| Option | MCP param | Settings key | CLI verb | Default |
|---|---|---|---|---|
| RRF cutoff | `rrfK` (int ≥ 1) | `retrieval.rrfK` | `settings retrieval rrfk set/show` | 60 |
| FTS5 list weight | `ftsWeight` (int ≥ 0; 0 disables the leg) | `retrieval.ftsWeight` | `settings retrieval fts-weight set/show` | 1 |
| Vector list weight | `vectorWeight` (int ≥ 0; 0 disables the leg) | `retrieval.vectorWeight` | `settings retrieval vector-weight set/show` | 1 |
| Sibling boost λ | `sourceLambda` (0..1) | `retrieval.sourceLambda` | `settings retrieval source-lambda set/show` | 0.1 |
| Consolidation threshold | `consolidationThreshold` (≥ 0) | `retrieval.consolidationThreshold` | `settings retrieval consolidation set/show` | 0.1 |
| Document-score formula | `docScoreFormula` ("max"\|"sum") | `retrieval.docScoreFormula` | `settings retrieval doc-formula set/show` | max |
| Candidate window | `candidateWindow` ("max3x100"\|"max5x50") | `retrieval.candidateWindow` | `settings retrieval window set/show` | max3x100 |
| Structure alpha | — (bank policy) | `retrieval.structureAlpha` | `settings retrieval alpha set/show` | 0.5 |
| Fusion no-regression flag | — (bank policy) | `fusion.noRegression.enabled.global` | `settings retrieval fusion enable/disable/show` | false |

`settings retrieval show-all` (alias `list`) prints every option with its source
(`setting` or `default`) — one call answers "what does a search run with?".

## Notes

- A zero weight disables that leg entirely (`vectorWeight=0` ⇒ FTS-only search;
  `ftsWeight=0` ⇒ vector-only).
- The enums travel as wire strings over MCP; a typo is rejected with `invalid-params`
  before any bank work, and so are out-of-range numbers (`rrfK=0`, `sourceLambda=2`).
- The fusion flag's read is part of the eager settings snapshot (two SELECTs per
  search); its application still requires two contributing legs (ADR-0078 as amended).
- Changing the default VALUES is a retuning exercise, gated by ADR-0056 — not a
  settings change.
