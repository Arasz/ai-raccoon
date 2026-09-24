# A9 excluded from the RRF fusion gate (issue #691)

**Date:** 2026-09-24
**Question:** `build-nightly-gates` fails `RrfParameterSweepTests.Sweep_ChosenRrfConfiguration_PassesAllGates`
gate (c) with "A9: hybrid exact rank 3 must not exceed the best single modality's 2 (fts 2, vector -)".
Is this a fusion defect worth fixing now, or a known limitation to document and pin?

## Findings

### F1 — Measured modality ranks for A9 (chosen config: k=60, weights 1:1, minScore 0.0, Max3x100) [MEASURED]

| Modality | Exact-chunk rank |
|---|---|
| FTS-only | 2 |
| Vector-only | outside top 10 (miss) |
| Hybrid | 3 |

Evidence: `dotnet exec tests/AiRaccoon.Tests/bin/Debug/net10.0/AiRaccoon.Tests.dll --filter-class AiRaccoon.Tests.Integration.RrfParameterSweepTests` against the committed MiniLM corpus bank (`docs-memory.db`) with the pinned query vectors (ADR-0050). The failure message before this change reads verbatim "A9: hybrid exact rank 3 must not exceed the best single modality's 2 (fts 2, vector -)" — the query vectors are a committed fixture, so this number is deterministic across platforms and a local run is valid evidence (no Linux-only variance the way A9's held-out CPU-dependent embeddings could introduce elsewhere).

### F2 — The mechanism is issue #367, not a fresh regression [READ]

Equal-weight reciprocal rank fusion (`ReciprocalRankFusion.cs`) cannot express "one modality is
confident, the other missed entirely" — RRF sums `1/(k+rank)` per leg, and a leg that never
retrieves the chunk contributes 0 rather than a penalty proportional to how badly it missed. A
query where FTS finds the answer at rank 2 but vector search misses outright gets fused against
every OTHER hybrid candidate that both modalities agree on, which pushes A9's chunk to hybrid
rank 3 even though FTS alone would have served it at rank 2. This is the same mechanism tracked
under #367, and ADR-0078's no-regression flag (default off, `FusionConfigKeys`) and ADR-0072's
measured-tradeoff weight tuning are the two prior attempts at a real fix — both left as follow-up
work rather than shipped, because a general fusion change moves other queries too (ADR-0072).

### F3 — A9's held-out floor is unaffected [READ]

`HeldOutRetrievalGateTests.cs:42` pins A9's held-out nDCG@5 floor at `0.722727`, measured
independently of this sweep's in-sample gate. A9 already sits in the tuning set (`RrfGateQueryIds`
includes it), so the two gates measure different things: the sweep's gate (c) is a no-regression
check on exact-chunk rank ordering between modalities, while the held-out gate scores the whole
ranked list. Excluding A9 from gate (c) does not touch the held-out floor or weaken it.

### F4 — Excluded ids that no longer violate gate (c) [MEASURED]

Reporting per the owner's instruction to report rather than silently trim the exclusion list.
Fresh fusion measurement (same run as F1) against the current exclusion list (`A2, A3, A8, A9,
A10, C1, C2, C5, S2`):

| Id | Hybrid | FTS | Vector | Best single | Still violates? |
|---|---:|---:|---:|---:|---|
| A2 | miss | 2 | miss | 2 | yes — hybrid misses where FTS holds rank 2 |
| A3 | miss | 3 | miss | 3 | yes — same shape as A2 |
| A8 | 4 | 3 | miss | 3 | yes — hybrid 4 > 3 |
| A9 | 3 | 2 | miss | 2 | yes — this exclusion (new, #691) |
| A10 | 9 | miss | miss | — (both miss) | no — best-single is undefined, so the gate already skips it regardless of the exclusion list |
| C1 | 3 | 4 | 1 | 1 | yes — pinned separately at exact rank 3 (gate b) |
| C2 | 1 | 3 | 1 | 1 | **no** — hybrid (1) now ties the best single modality (1) |
| C5 | 1 | 1 | 3 | 1 | **no** — hybrid (1) now ties the best single modality (1); still pinned separately at ≤5 (gate b) |
| S2 | miss | miss | miss | — (all miss) | no — best-single is undefined, same shape as A10 |

A10, C2, C5 and S2 no longer need their exclusion to pass gate (c) on the current corpus, but
they stay excluded here: A10/S2 have an undefined best-single rank so the exclusion is a no-op,
not a hidden pass, and C1/C5 already carry their own separate pinned ceilings (gate b, lines
~132-134) that are the real gate for those two. Removing entries from the exclusion list is a
follow-up cleanup, not part of this fix — the brief scoped this task to A9 alone.

## Decision

Exclude A9 from the general fusion no-regression gate (c), citing #691 and #367 in the test
comment, and pin its own measured hybrid rank as a ceiling (`ShouldBeLessThanOrEqualTo(3, ...)`)
in the same style as C1/C5's gate-(b) pins, so a further regression in A9's fusion rank still
fails the gate. A real fusion fix (weighted RRF, ADR-0078's no-regression flag, or similar) is
out of scope here — it is a ranking-behavior change that would need its own measurement pass
across every query, not a test-only nightly-gate fix.

## Addendum — re-verified against PR #694 (FusionLeader / full-recall leader, 1.49.2)

A sibling lane's #694 (`bc5c06f1`, merged to main ahead of this branch's fetch) touches
`FusionLeader.cs`, `SourceAffinityRanker.cs` and `SqliteMemoryStore.cs`. `bc5c06f1` was already
an ancestor of this branch at its creation (confirmed with `git merge-base --is-ancestor bc5c06f1
HEAD`), so every measurement in this document already reflects the post-#694 ranking. A rebuild
and re-run against the fully up-to-date branch (main tip through #699) reproduces the same
numbers: A9 still measures hybrid rank 3 against best-single rank 2 (fts 2, vector miss) — gate
(c) still needs the exclusion, and F4's table is unchanged.

## Still open

- A real fix for #367 (equal-weight RRF cannot express one confident leg) would need to move
  more than A9; it is tracked separately and not part of this change.
- A10/C2/C5/S2's now-redundant exclusion-list entries (F4) are worth trimming once someone
  re-verifies the whole list against a fresh sweep; left untouched here per the brief's scope.
