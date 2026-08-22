# 0056. A retrieval gate measured off its tuning set

Date: 2026-08-15

Status: Accepted — the finding stands; its measurement is historical and its gate is now wider.
The in-sample/held-out gap (0.673 against 0.285) was measured over a tuning/held-out partition
of the private jsaa corpus, which left the repository at ADR-0090. On the public corpus the gap
is not merely unmeasured but undefined: nothing was ever tuned here, so `TuningQueryIds` is
empty and every gradeable query is out-of-sample. Stronger gate, weaker record — both deliberate.

## Context

Two defects, both in the measurement rather than the ranker.

**The headline gate asserted nothing.** `BaselineMetricsTests` computed nDCG@5, MRR and recall@5 for
every query and then asserted `ShouldBeInRange(0.0, 1.0)` on each — values those three functions
return by construction for any input, including a ranking with the relevant documents removed. The
file conceded it in a comment: *"logged as a data point, not asserted."*

**Every published number is in-sample.** The same **11** expected-source queries
(`A1`–`A7`, `S2`, `C1`, `C2`, `C5`) both selected the parameters — ADR-0006's 96-point RRF grid,
ADR-0005's source-affinity grid — and gated them, through two hand-copied `…GateQueryIds` arrays
that had to agree and were never compared.

## What the corpus can actually support

The improvement plan prescribed **leave-one-family-out**: partition by what generated each document
(jsaa / ai-badger / arasz-home-page), tune on some families, gate on the held-out ones. Measured
against `jsaa-memory.db`, that control is **not available here**:

| | |
|---|---|
| Generators in the corpus | **two** — `docs/` (112 source files) and `.ai-badger/` (78); no arasz-home-page |
| Families the tuning set touches | **both** |
| Families held out | **none** |

So the partition is taken one level finer, at the **document**. A query is held out when no tuning
query names its document — `S1`/`S3`/`S4`/`S5` are *not* held out, because `S2` tuned on the same
ADR-0011, and `S6` is not, because `A4` tuned on the same ADR-0060.

| Tier | Queries | What a number over it means |
|---|---|---|
| Tuning | A1–A7, S2, C1, C2, C5 (11) | in-sample; every figure in ADR-0005 and ADR-0006 |
| Held out | **A8, A9, A10** (3) | out-of-sample — the only honest gate |
| Document-leaked | S1, S3, S4, S5, S6 (5) | the parameters saw this document's other sections |

## Decision

**Gate on the held-out tier, derive it, and prove the floor discriminates.**

The tiers are computed at test time from one `TuningQueryIds` list, which both sweeps now reference
instead of keeping their own copy. Adding an id to that list removes its whole document from the
held-out side, so `RetrievalTuningSetsTests` pins the held-out count at ≥ 3 — an unnoticed expansion
of tuning would otherwise convert the gate back into an in-sample one silently.

`NoFamilyIsHeldOut_WhichIsWhyTheGateIsDocumentLevel` asserts the table above. It fails the day a
family the sweeps never tuned on appears, which is the signal to promote the gate from document-level
to the leave-one-family-out control the plan asked for.

## What it measured

Same store, same corpus, same pinned query vectors (ADR-0050), same shipped defaults:

| | mean nDCG@5 |
|---|---|
| In-sample (11 tuning queries) | **0.673** |
| Held out (3 unseen documents) | **0.285** |

**Out-of-sample retrieval scores 42% of the published figure.** That is the size of the circular
benchmark, and it is the number any future ranking change has to beat.

Per-query floors, measured 2026-08-15: `A8` 0.131205, `A9` 0.553146, `A10` 0.169580.

## On A8, which reversing the ranking improves

Scored against a **reversed** result list, `A8` goes from 0.131 to **0.491**. Its relevant chunks sit
below the top 5, so destroying the order helps it. A per-query floor of 0.131 is therefore one a
reversed ranking survives — by this ADR's own standard, not a gate.

That is why the discrimination proof runs on the **mean**: reversed 0.164 against a floor of 0.285.
The per-query pins stay as regression detectors, which is what they are good for; the mean is the
gate. Recording the asymmetry rather than quietly dropping A8 — a ranking this bad on a held-out
query is a finding for the ranking packages, not an inconvenience for the gate.

## Consequences

- The `ShouldBeInRange(0.0, 1.0)` assertions in `BaselineMetricsTests` are deleted. It keeps its
  finiteness checks and remains the determinism and report gate it was written as.
- ADR-0005 and ADR-0006 carry a status line marking every figure in them in-sample.
- The two duplicated 11-id arrays become one; a third (`PlatformNumericsProbe`, A1–A7) is a
  deliberately different subset and is left alone.
- **WP4 and WP12 can now be measured.** Neither could have been before: the old gate reported success
  for any ranking, so a change that helped the tuned queries and destroyed the rest would have shipped
  green.

## Evidence

`tests/AiRaccoon.Tests/Integration/Retrieval/HeldOutRetrievalGateTests.cs` and
`RetrievalTuningSetsTests.cs`. All seven assertions were watched red before being trusted:

| Assertion | Perturbation that reddened it |
|---|---|
| `HeldOutQueries_HoldTheirPinnedNdcg5Floor` | floors at 1.0 — reported the real 0.131 / 0.553 / 0.170 |
| `HeldOutMean_HoldsItsFloor` | `A9` floor raised to 0.9 |
| `ReversedRanking_FailsTheHeldOutMeanFloor` | scored the *unreversed* list — the floor held, so the test failed |
| `HeldOutSet_IsNotEmpty_…` | `A8` added to `TuningQueryIds` (held out drops to 2) |
| `EveryGradeableQuery_LandsInExactlyOneTier` | `DocumentLeaked`'s predicate negated |
| `NoFamilyIsHeldOut_…` | tuning set replaced with `A8`/`A9`/`A10` (ai-badger becomes untuned) |
| `InSampleScore_ExceedsHeldOutScore_…` | same swap — in-sample became the worse set |
