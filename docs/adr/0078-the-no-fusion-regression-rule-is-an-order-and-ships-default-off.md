# 0078. The no-fusion-regression rule is an order, and it ships default-off

Date: 2026-08-17

Status: Accepted

Closes the fusion half of issue #367. Ships production code, **disabled by default**, with
telemetry on the enabled path only. The chunking half of #367 is
[ADR-0077](0077-table-chunking-is-not-adjudicable-on-a-table-blind-corpus.md) and is not
re-litigated here.

## Context

[ADR-0006](0006-rrf-parameter-optimization.md) declares a **"no fusion regression" gate** and
states it precisely: *the hybrid never ranks the expected chunk below the best single modality*
(`0006:49-50`, and the Consequences entry at `0006:67-72` naming the three queries it was
checked on — A6 `2 ≤ min(2, miss)`, S2 `3 ≤ min(4, miss)`, A5 `3 ≤ min(miss, 4)`). It holds on
all eleven tuning queries.

**The real bank is a counterexample to a rule this project already claims.** On the
25,995-entry bank, a query whose target chunk is **rank 1 on FTS alone** lands at **#18 under
default hybrid**. The direction reproduces independently on a different query: FTS-only 4,
vector-only 14, hybrid 6.

The mechanism is not a bug. RRF at `k = 60`, weights 1:1, pays consensus. A result retrieved by
one leg at rank 1 scores `1/61 ≈ 0.0164`. A result retrieved by both legs at ranks 3 and 2
scores `1/63 + 1/62 ≈ 0.0320` — nearly double. Two mediocre agreeing votes beat one decisive
one, by construction. That is what RRF is for, and it is also exactly the regression ADR-0006
promised would not happen.

## The rule is an order, not a score

The first design attempt injected `max(rrfScore, bestSingleLegScore)` into the fused score. It
does not work, and the reasons are structural rather than a matter of tuning. They are recorded
in the appendix, because a reader who cannot see what lost proposes it again.

The short version: `SearchResultMerger.cs:26` hands the already-fused list back to
`ReciprocalRankFusion.Fuse` as a single list, which **rebuilds every score from rank position**
as `(k+1)/(k+rank)`. Any magnitude injected upstream is discarded before the caller sees it —
[ADR-0058](0058-the-second-fusion-is-order-preserving-and-its-removal-is-not-yet-measurable.md)
documented this second fusion and proved it order-preserving.

So **only position survives**, and the rule must therefore produce a **permutation**. ADR-0058's
order-preserving property is what carries it through.

### The rule, stated

Reorder the fused list by

    key(x) = min( fusedRank(x), bestRank(x) over the CONTRIBUTING legs )

ties broken by `fusedRank`. Absence from a leg contributes no key at all — it falls back to the
result's own fused rank, so it can never demote.

Three properties matter more than the formula:

- **It has no tunable constant.** No threshold, no weight, no window, no cap. There is nothing to
  select from a held-out column, which is the whole reason this can ship where ADR-0072 could not
  (below).
- **It is a derivation of ADR-0006's own sentence**, not a new policy. "Never below the best
  single modality" is what `min(fusedRank, bestRank)` says.
- **It introduces no ties.** The output is a strict total order over distinct positions, and the
  second fusion turns position into a distinct score. Nothing reaches
  `ThenBy(result.Path, StringComparer.Ordinal)` to let a filename decide the top hit.

The guarantee is honest rather than absolute. With `L` legs, up to `L` results can claim rank 1,
so the promoted result lands at position `≤ L` rather than exactly at its best leg rank. On the
end-to-end fixture the target moves from last place to position 2 behind the consensus row, not
to position 1. The worst case is bounded by `(L+1) × bestRank(x)`; the typical case is much
tighter because the leg and fusion orders overlap heavily.

## What actually survives, established rather than assumed

The reorder is applied at `SqliteMemoryStore.SearchAsync` **after `ReciprocalRankFusion.Fuse`,
before `SearchResultMerger.Merge`**. What comes out the far side is not the reorder:

| Stage | Effect on the reorder |
|---|---|
| Second fusion (`SearchResultMerger.cs:26`) | **Preserved exactly.** `(k+1)/(k+rank)` is strictly decreasing in rank (ADR-0058) |
| `SourceAffinityRanker.Rank`, λ = 0 | **Preserved exactly.** λ ≤ 0 returns the input untouched |
| `SourceAffinityRanker.Rank`, λ = 0.1 (the shipped default) | **Can override it.** Measured below |
| `Consolidate` | Can drop a result the reorder promoted |

**Source affinity wins, and by how much is measurable.** At the shipped `λ = 0.1`, `k = 60`, a
result the reorder placed at rank `r` carries `61/(60+r)` and gains `0.1` per adjacent sibling.
Solving `61/(60+r) + 0.1 > 1.0` gives `r ≤ 7`: **one adjacent sibling is worth about seven rank
positions**; two are worth about sixteen. `ReorderSurvivalThroughMergeTests` pins it concretely —
a chunk the reorder placed **9th**, flanked by two same-file siblings, scores `61/69 + 0.2 =
1.0841` and outranks the chunk the reorder placed **1st** at `1.0`.

**So the reorder is a proposal that source affinity may still revise, and the served order is
neither one alone.** That is the answer to "what survives", and it is stated here rather than
left for a reader to assume — it is also precisely why the enabled path measures the diff on the
**served** list (after `Merge`) and not on the fused list, which would overstate the change.

## Why this ships default-off, where ADR-0072 shipped nothing

[ADR-0072](0072-a-term-budget-for-long-queries-is-not-adjudicable.md) refused to ship a term
budget, and its reason was not timidity: *"the existing gates cannot adjudicate this change, and
that is the blocker."* All 44 catalog queries are ≤ 10 tokens with no duplicates, so the gates
were blind to every option it measured. Its Result 4 named the failure exactly — the held-out
column swings `0.3919 → 0.0437 → 0.2409` with no optimum, and **choosing a cap from that is
fitting noise on n = 3**, the same condition that stopped ADR-0058.

That reasoning applies here with full force. Per
[ADR-0056](0056-a-retrieval-gate-measured-off-its-tuning-set.md), out-of-sample retrieval scores
**0.285 against an in-sample 0.673 — 42% of the published figure** — and the held-out tier is
**three queries** (A8, A9, A10), one of which (A8) a *reversed* ranking improves. ADR-0058 built
its change, measured it four ways, and refused because `0.2818` against `0.2846` is
inconclusive and picking the `0.3333` variant would have been tuning on the held-out set.

**The offline corpus cannot adjudicate a fusion change.** Nothing in this record claims
otherwise, and no nDCG figure is offered for the reorder — offering one would repeat exactly the
mistake ADR-0056 measured.

What makes this different from ADR-0072 is not better evidence. It is that **there is nothing to
choose.** ADR-0072 had to pick a cap number, and the only surface capable of picking it was a
3-query column; every candidate number was therefore unjustifiable, and shipping any of them
would have been fitting noise. This rule has **zero free parameters** — it is on or it is off.
The only open question is whether the rule itself helps, and a 3-query held-out set cannot answer
that either.

So the honest move is not to ship it, and not to bury it: it is to **make it collectable**. The
flag's enabled path is the evidence surface the static corpus cannot be — real queries, a real
bank, real graded outcomes. The 44-query catalog will never contain the #367 shape, because that
shape is a property of a 25,995-entry bank and the catalog is not one. This is the same request
ADR-0058 and ADR-0072 both ended on — *held-out capacity* — answered by collecting it from
production rather than waiting for someone to author it.

**Default off is not hedging; it is the claim.** The rule is unproven, this record says so, and
nothing changes for any user who does not opt in.

## Decision

**Ship the rule behind `fusion.noRegression.enabled.global`, default off.** With the flag absent
or explicitly `false`, behaviour is byte-identical to today: the reorder is never constructed,
no extra settings read is issued, and `SearchResults.Fusion` is null.

**The flag is bank-wide and is deliberately NOT on the MCP tool surface.** `rrfK`, `ftsWeight`
and `vectorWeight` are per-request tuning knobs an agent may reasonably vary. This is an
evidence-gathering toggle for an operator deciding whether a rule should become the default. A
per-request flag would produce a stream of measurements with no consistent population behind it,
which is the opposite of what the record needs. It takes no `CliWriteOptOuts` exception —
`encryption` remains the only one (ADR-0076).

**It IS on the CLI**, under `settings` per ADR-0076, because a flag with no sanctioned way to
toggle it ships dark — and the release checklist has to exercise search both ways against a copy
of the real bank. There is no generic `settings set <key> <value>` verb, and ADR-0075 makes the
server the only writer, so without a verb the setting is reachable by no supported path at all:

    ai-raccoon settings retrieval fusion enable
    ai-raccoon settings retrieval fusion disable
    ai-raccoon settings retrieval fusion show

`enable`/`disable` rather than `set on|off` follows the convention every other boolean in this
tree already uses (`sweep`, `noise`, `queryguard`), so there is no argument to validate — an
unknown verb is refused by the parser. `show` names the default in its own output
(`enabled: False  (default: False — off serves the baseline fusion)`): reading `False` alone
cannot distinguish an unset bank from a deliberately disabled one, and at 2am that is the
difference between trusting a result and re-running it.

### Leg availability ships regardless of the rule

`ModalityLeg` (Core) records, per leg, whether it was **queried** and what it returned.
`Contributes` is `Queried && Candidates.Count > 0`. It wraps the caller's candidate list rather
than copying it, so constructing one costs the default path nothing. This is a correctness
signal, not a tuning result, and it is what stops a degraded leg being read as a leg that
disagrees:

- `EntryEmbedder.EmbedQueryAsync` returns null with no engine configured, and `SearchAsync` then
  skips the vector leg entirely (`queryVector is not null && query.VectorWeight != 0`).
- `QueryFtsBatchAsync` catches `SqliteException`, logs `Log.KeywordModalityFailed` and degrades to
  vector-only. `QueryDualVectorBatchAsync` has no such catch.
- Rows with pending embeddings have no `vec_entries` row and never reach the vector leg at all.
- `StructureFusion` scores an absent structure similarity as 0 (ADR-0057), depressing a vector
  rank for reasons unrelated to content relevance.

`ftsQueried` / `vectorQueried` are derived once from the same expressions the two query sites
take, so the signal cannot drift from whether the leg actually ran.

**A finding, stated because it changes what the availability check is for.** At the *ordering*
level the check is redundant, and this was proved rather than assumed: with one contributing leg
the fused list **is** that leg's list (a single-list `Fuse` is order-preserving), so
`key(x) = min(fusedRank, legRank) = fusedRank` for every result and **every rank-based rule of
this family collapses to the identity**. Removing the check does not redden the ordering
assertion — `Reorder_VectorLegSkipped_ReturnsTheFusedOrderUnchanged` stays green under that
perturbation. Where the check *is* load-bearing is the evidence: without it, an FTS-only search
records a fusion diff of all zeros, inflating the denominator of every rate the flag exists to
measure and making the data claim the rule was inert when it was never applicable. That is the
observable the gate below tests, and it does redden.

### The evidence collected, and who consumes it

Three signals, recorded into the existing `metrics` table (no new sink; `search_quality` is the
wrong shape — it is one row per search keyed by correlation id, and these are per-search
measurements, which is what `metrics` is for). All three carry `project_id`, `query_hash` and
`correlation_id`, and no query text — `SqliteMetricsStore`'s save-time allowlist would reject it.
Tags are null, so the allowlist needed no change.

| Metric | Value | Consumer |
|---|---|---|
| `search.fusion.top1_changed` | 0 or 1 | `AVG(value)` over a window is *how often the rule changes the answer at all*. Near 0 on real traffic closes #367 — the rule is inert and the default stays off |
| `search.fusion.top1_rank_delta` | positions the baseline winner fell; `-1` if it left the list | Magnitude. Concentrated at 1–2 means gentle reordering. A long tail, or many `-1`s, means the rule is displacing strong results and must clear an offline gate before it goes on by default |
| `search.fusion.top5_moved` | positions in the top 5 holding a different result | Breadth. `top1_changed = 0` with `top5_moved = 3` is reshuffling below the winner — a real but cheaper risk than changing the answer, and only separable because both are recorded |

**The verdict recipe.** `metrics.correlation_id` joins back to `search_quality`, which already
carries follow-through and a 1–5 usefulness grade. That join is what turns movement into quality:

```sql
SELECT m.value AS changed,
       COUNT(*)                    AS searches,
       AVG(sq.usefulness_grade)    AS mean_grade,
       AVG(sq.follow_through_count) AS mean_follow_through
FROM   metrics m
JOIN   search_quality sq ON sq.correlation_id = m.correlation_id
WHERE  m.name = 'search.fusion.top1_changed'
GROUP  BY m.value;
```

Grades for searches the rule changed against searches it left alone, on the same bank, same
period, same population. That comparison is the thing no offline corpus in this repo can produce,
and it is the only reason the flag exists.

**Cost, stated.** On the enabled path a hybrid search runs `SearchResultMerger.Merge` **twice** —
once for the baseline, once for the served order — and issues **one extra settings `SELECT`** on
the already-open connection. Both are accepted precisely because they are behind a flag that is
off. The second merge is deliberately left **outside** the `search.affinity` timing bracket so
that phase stays comparable across flag states. A single-leg or degraded search pays nothing at
all: the availability check short-circuits ahead of the settings read.

> **Amended 2026-08-20 (ADR-0083).** The "pays nothing at all" sentence no longer holds for
> the settings read: since the SearchParameters refactor the flag's value is part of the eager
> batched defaults snapshot (`SearchParameters.FromSources(query, defaults)`, one `retrieval.%`
> prefix read + this flag's read on the search's own connection), so a single-leg search pays
> the same two SELECTs as any other. What stays conditional is the **application**: the reorder
> work still runs only when two legs contributed. The flag's read cost is no longer "behind a
> flag that is off" — it is a fixed two-SELECT tax on every search, accepted and pinned by
> ADR-0083.

## Gates

Every one was watched red against the built change, with the perturbation named.

| Gate | Perturbation that reddened it | Result |
|---|---|---|
| Flag OFF is byte-identical (absent and explicitly `false`; same order, same rankings) | heuristic applied unconditionally — settings read removed | **4 red**, incl. `Search_FlagAbsent_…` and `Search_FlagExplicitlyFalse_MatchesTheFlagAbsentOrderAndScoresExactly` |
| Flag ON reorders in the #367 shape, end to end | `Reorder` returns the fused list unchanged | **4 red**, incl. `Search_FlagEnabled_RanksTheSingleLegWinnerAtLeastAsWellAsItsBestLeg` and `Reorder_TargetRankedFirstByOneLeg_AndUnseenByTheOther_RisesToTheTop` |
| A degraded leg is not disagreement | availability check dropped (`legs.Count >= 2`, all legs consulted) | **1 red** — `Search_FlagEnabled_ButOnlyTheKeywordLegRan_ChangesNothingAndRecordsNothing`, on `Fusion.ShouldBeNull()` |
| Absent-from-window ≠ ranked-poorly, and neither is a penalty | absence scored as `windowSize + 1`, `max` over legs instead of `min` | **3 red**, incl. `Reorder_AbsentFromAWindow_AndRankedPoorlyInIt_AreBothNeutral` |
| Telemetry only when the flag is on | both perturbations above | red, as above |
| No ties introduced | `ScoreInjectionTests` pins the failure mode directly (below) | see appendix |
| The CLI verb exists at all | the three rows added to `SettingsLeaves` before the tree node | **7 red**, incl. `SettingsLeaves_CoverEverySettingsPath` |
| `show` reports the stored value, and off by default | `show` hardcoded to `enabled = true` | **2 red** |
| `disable` actually disables | `disable` writes `"true"` | **1 red** |
| `show` writes nothing | `show` back-writes the value it just read | **1 red** |

The opposed pair is deliberate: gate 2 alone is satisfied by a rule that fires on everything, and
gate 3 is what refuses that.

**The existing fusion tests were not modified.** `ReciprocalRankFusionTests` (which pins
`61.0/62` to `1e-9`), `ModalityCandidatesTests`, `SearchResultMergerTests` and
`SqliteMemoryStoreHybridSearchTests` are all untouched and all green — the flag-off path is the
same code they already pinned. `Category=Retrieval` is green too, including ADR-0056's held-out
nDCG@5 floors: 2101 unit, 1099 integration, 60 retrieval, 0 failures.

## Appendix — what does not work

Recorded so a reader who cannot see what lost does not propose it again, in the same service
ADR-0072's rejected-options list performs. The first two are pinned by
`tests/AiRaccoon.Tests/Unit/Fusion/ScoreInjectionTests.cs`.

- **`max(rrfScore, bestSingleLegScore)` injected into the fused score — rejected, three ways.**
  1. **The magnitude never survives.** `SearchResultMerger.cs:26` re-fuses the single already-fused
     list, rebuilding every score from position. Measured: a strong set (`1.0, 0.99, 0.98`) and a
     weak one (`0.03, 0.002, 0.001`) both leave `Merge` as exactly `1.0, 61/62, 61/63`. Whatever
     was injected is gone before the caller sees it.
  2. **It creates ties, and a tie is decided by the filename.** Two candidates on equal fused
     scores fall to `ThenBy(result.Path, StringComparer.Ordinal)`. Measured: identical legs and
     identical scores, changing **only the paths**, swap which result holds rank 1. A ranking whose
     top hit is a function of a filename is worse than the regression it was meant to fix.
  3. **"Raise only, never lower" is false.** Rank is a total order over a fixed set; raising one
     result necessarily lowers another. `max` looks conservative and is not. The shipped rule owns
     this instead of hiding it — it states the displacement bound and measures the movement.
- **Treating "absent from a leg's candidate window" as a bad rank** (`windowSize + 1`) — rejected.
  The default window is 100 at limit 20 (`CandidateWindowFor`). Absence means the leg never
  retrieved the result, which is routine on a 25,995-entry bank and is also what a *degraded* leg
  produces. Scoring it as a vote against turns a partially-failed FTS context, or a row whose
  embedding is still pending, into evidence of irrelevance. This is the perturbation that reddens
  gate 4.
- **An availability check placed on the ordering** — kept, but not where it first appeared to
  belong. On a single contributing leg the ordering is the identity by construction, so an
  ordering-level availability check cannot fail and would have been a check that has never been
  seen fail. It earns its place on the telemetry instead, where the perturbation does redden it.
  Stated because the reasoning, not the line of code, is what stops it being re-added as decoration.
- **Diffing the baseline against the adjusted list at the fused stage** — rejected. Cheaper, and
  wrong: `SourceAffinityRanker` runs downstream and can override the reorder by roughly seven rank
  positions per adjacent sibling, so a fused-stage diff would report movement the caller never
  saw. The enabled path pays for a second `Merge` and diffs what is actually served.
- **A per-request MCP parameter beside `rrfK`/`ftsWeight`/`vectorWeight`** — rejected. The
  measurement needs a stable population; a knob each caller may flip produces a metric stream with
  no denominator. Bank-wide, one operator decision, one population.
- **Recording five metrics** — cut to three. An earlier draft proposed five while its own verdict
  recipe read only one. Every signal kept above has a stated consumer in the table; anything
  without one was removed rather than kept "for later".

## Consequences

- `ReciprocalRankFusion`, `SearchResultMerger`, `SourceAffinityRanker` and `ModalityCandidates`
  are **unchanged**. The reorder sits between the two existing stages and touches neither.
- `SearchResults` gains a nullable `Fusion`, null on every default search. `MemoryTools` maps it
  to measurements the same way it already maps `SearchTimings.Phases()` — mapping, not pipeline
  (ADR-0065).
- `SqliteMemoryStore.cs` hit its line ratchet at 1144 against a cap of 1112. Taking that gate's
  own note at its word for the sixth time, **WP8's search seam came out instead of a raise**:
  `CandidateWindowFor`, `QueryFtsBatchAsync`, `BuildFtsResults`, `ReadStructureAlphaAsync` and the
  flag's settings read moved to `SqliteMemoryStore.Search.cs`, the partial-file seam
  `SqliteMemoryStore.Rows.cs` already established. The cap is **lowered** 1112 → 1066.
- **`ranking` still carries rank position**, and this record does not fix that — ADR-0058 explains
  why the fix is not measurable, and nothing here changes that situation.
- **What would unblock making this the default:** the flag's own data. Concretely, enough real
  searches with `top1_changed` recorded and a usefulness grade attached to run the join above with
  a difference in mean grade that is a result rather than noise. Until that exists, the default
  stays off, and no nDCG figure for this rule should be published from the 44-query catalog.

## Evidence

`tests/AiRaccoon.Tests/Unit/Fusion/` (`NoFusionRegressionTests`, `FusionDiffTests`,
`ReorderSurvivalThroughMergeTests`, `ScoreInjectionTests`),
`tests/AiRaccoon.Tests/Integration/Storage/SqliteMemoryStoreFusionFlagTests.cs`, and the two
`MemoryToolsTests` cases pinning that the fusion measurements are recorded on the same correlation
id and are absent on the default path.
