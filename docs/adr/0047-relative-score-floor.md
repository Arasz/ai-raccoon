# 0047. The score floor is relative to the top hit, and says so

Date: 2026-08-14

Status: Accepted

Supersedes the "minScore semantics — unchanged" section of
[0006 — RRF parameter optimization](0006-rrf-parameter-optimization.md).

## Context

`memory_search` shipped a `minScore` parameter defaulting to `0.7`, described as a
"floor on the normalized 0..1 ranking". The name and the description both read as an
absolute quality bar: raise it for stronger matches, lower it for more, weaker ones.

It is not one. `ReciprocalRankFusion.Fuse` divides every fused score by the maximum in
that response, and `SourceAffinityRanker.Rank` re-normalizes the same way after the
adjacent-chunk boost. Rank 1 therefore scores exactly `1.0` in every response, and the
floor is applied to a fraction of *that* response's best hit.

Two consequences, both measured on the committed JSAA corpus
(`tests/AiRaccoon.Tests/Resources/jsaa-memory.db`, project `job-search-ai-assistant`,
44 baseline queries from `scripts/baseline-queries.json`, shipped fusion parameters
k = 60, weights 1:1, λ = 0.1):

**The floor is a rank cutoff, and lowering it does nothing.** Total results returned
across the 44 queries:

| limit | 0.0 | 0.1 | 0.3 | 0.5 | 0.7 | 0.9 | 0.95 | 0.99 |
|---|---|---|---|---|---|---|---|---|
| 5 | 220 | 220 | 220 | 220 | 220 | 196 | 134 | 46 |
| 20 | 880 | 880 | 880 | 880 | **830** | 262 | 137 | 47 |
| 50 | 2200 | 2200 | 2200 | 2147 | **996** | 285 | — | — |

`0.0`, `0.1`, `0.3` and `0.5` are indistinguishable up to limit 20. A caller who lowers
the floor to widen the result set gets the identical set back, because the normalized
score is very nearly a function of rank alone — the per-rank curves of unrelated queries
sit on top of each other (rank 1 = 1.000 for every query; rank 10 ≈ 0.80–0.90; rank 20 ≈
0.65–0.78).

**The score carries no absolute match quality.** Three off-corpus queries against the
job-search bank:

| query | results at `minScore` 0.7, limit 20 | top 5 scores |
|---|---|---|
| "zygomatic tessellation of pelagic thermoclines" | 20 | 1.000, 0.986, 0.936, 0.921, 0.879 |
| "how to braise a wombat in aspic" | 20 | 1.000, 0.972, 0.936, 0.921, 0.892 |
| "quarterly dividend restatement for maritime insurance underwriters" | 15 | 1.000, 0.952, 0.938, 0.925, 0.878 |

A question about braising a wombat scores 1.000 at rank 1 and is filtered no more than
a real query is. The curve is indistinguishable from query A1's (1.000, 0.978, 0.964,
0.950, 0.937). This is by construction, not by tuning: RRF scores rank positions, and
the normalization then pins the best of them to 1.0 whatever it is.

**The shipped default was silently truncating.** `SearchQuery.MinScore` and the MCP tool
both defaulted to `0.7`, while every internal caller and every ranking gate passed `0.0`.
At the shipped `limit=20`, ten of the 44 baseline queries came back short —
A1 17, A2 14, A5 15, A8 15, S6 18, D4 14, E1 14, E3 12, G1 19, G2 12 — and at limit 50
**all 44** came back short, 996 results returned against 2200 requested. So the
agent-facing path was the only configuration running a value that no measurement in this
repo supports and no test exercised.

Two shipped claims are falsified by the table above.
`docs/explanation/architecture.md` said the threshold "cannot bite until past rank ~28"
at `limit=20`; measured, it bites at rank 18 on A1 and rank 13 on E3. ADR-0006 recorded
minScore as "measured inert at the chosen point" and the tool default 0.7 as "equivalent"
to the swept 0.0 — true at the sweep's `limit=10`, false at the `limit=20` the product
ships.

## Decision

**The parameter is named for what it filters, defaults to off, and its description warns
that a high score is not evidence of a good match.**

- `minScore` → **`minRelativeScore`**, at the MCP tool and on `SearchQuery`. It is a
  fraction of this response's top hit; the old name asserted an absolute scale that
  cannot exist downstream of a max-normalization.
- The default moves **0.7 → 0.0** at both layers: off unless the caller asks for it.
  0.0 is what ADR-0006's 96-point sweep chose, what every internal caller already
  passed, and the only value under which the tool returns what `limit` asked for.
- The tool description states the semantics that are true — a relative floor, useful for
  "keep only hits in the same league as the best one" — and states the trap explicitly:
  ranking is normalized per response, so rank 1 always scores 1.0 **even when nothing in
  the bank answers the query**.
- The behaviour of the filter itself is unchanged. Nothing in the ranking moves.

The retained knob is not redundant with `limit`. Because the per-query curve has cliffs
(A2 drops 0.954 → 0.853 between ranks 2 and 3; A5 drops 0.902 → 0.781 between ranks 6
and 7), a relative floor does express "results comparable to the best one" in a way a
fixed `limit` cannot. That is a real, if narrow, control — it was simply never the one
the name and default advertised.

## Consequences

- **Breaking wire change.** `memory_search`'s parameter is renamed; a client passing
  `minScore` by name now gets the default instead of its value. `McpToolContractTests`
  pins the wire contract and was updated as a deliberate one-line diff.
- **Callers.** The only caller passing anything other than `0.0` was the MCP tool's own
  default. `SearchResultMerger.Merge` already defaulted to `0.0`; the internal
  `ReciprocalRankFusion.Fuse` call inside `SqliteMemoryStore.SearchAsync` passes `0`
  and is unaffected.
- **No ranking movement.** Every retrieval gate (`SourceIdentityTests`,
  `QueryConstructionTests`, `RrfParameterSweepTests`, `SourceAffinitySweepTests`,
  `SectionTargetedRetrievalTests`, `ParityGateTests`, `BaselineMetricsTests`,
  `RetrievalBaselineTests`, hybrid-search storage tests) passes an explicit floor of
  `0.0` already, so no pin moved. ADR-0006's chosen point is untouched.
- **Validation.** `MinRelativeScore` keeps its `InclusiveBetween(0.0, 1.0)` rule and the
  camelCase error-property gate now expects `minRelativeScore`.

## Alternatives considered

- **Filter on the raw pre-normalization fused score.** Rejected: the raw RRF score is
  `Σ weight / (k + rank)` — derived purely from rank positions, carrying no more quality
  information than the normalized value, on an unusable 0..0.033 scale. It would make
  the number absolute without making it meaningful.
- **Keep the 0.7 default and fix only the prose.** Rejected: the default is not just
  mis-described, it silently discards results the caller asked for, and no measurement
  in this repo supports it. ADR-0006 chose 0.0.
- **Delete the parameter and let `limit` do the job.** Considered seriously — two knobs
  for one monotone curve is one too many. Rejected because of the cliff behaviour above:
  a relative floor adapts to the query's own score gap, which `limit` cannot.
- **Carry an absolute quality signal (max cosine / bm25) and filter on that.** This is
  the change that would actually let an agent tell "nothing here" from "decisive hit" —
  the escalation decision AiRaccoon's own tool guidance asks agents to make. Deferred,
  not rejected: FTS-only results have no cosine, bm25 is unbounded and corpus-relative,
  and under degradation (no embedding engine) there is no cosine at all, so the filter's
  meaning would change with the runtime's health. Choosing a threshold also needs a
  labelled relevance-vs-similarity study this change did not run. Naming it honestly
  first is the prerequisite: a parameter that pretends to be that signal is what stopped
  anyone from building it.

## Follow-up

An absolute "is anything here actually relevant?" signal remains unbuilt. Until it
exists, an agent cannot distinguish a decisive hit from a top-ranked irrelevance, and the
`memory_search` result set alone should not be read as evidence that the bank has an
answer. The measurement above is the case for building it.
