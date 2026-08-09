# Promotion scoring — round 3 (opus agents, 406-entry jury-labeled dataset)

Date: 2026-08-09. Baseline: the shipped scoring v3 (ADR-0018 §v3). Method deltas vs round 2 are
the labeling scheme and the fact that measurement had to be rebuilt before anything could be
scored at all — see `2026-08-09-promotion-scoring-measurement.md`.

**One round only**, by owner direction (weekly usage limit). No round 2, no merge round.

## The labels, and why the jury is checked before it is used

Round 1 and 2 used the owner's hand labels. Those were partly recoverable here (59 of 61 rows,
rejoined by hash — see `rebuild_fixture.py`), but 59 rows cannot carry a tournament, so the pool
was extended to 406 by three independent opus raters working from a shared rubric
(`promotion-scoring-eval/RUBRIC.md`).

**The 59 owner-labeled rows were mixed into the jury's input blind**, so the jury's agreement with
the owner is measured rather than assumed:

| | vs owner (59 blind rows) | vs each other (406 rows) |
|---|---|---|
| rater A / B / C | +0.678 / +0.687 / +0.700 | A–B +0.930, A–C +0.916, B–C +0.917 |

Two things follow, and both shaped the tournament:

1. **Raters agree with each other far more than with the owner** (0.92 vs 0.69). Jury consensus is
   a usable target but it is *not* the owner's target, so every scoreboard below reports agreement
   with the owner's 59 rows as a separate guard column.
2. **The shipped scorer already scores +0.7156 against the owner's labels** — at the level of an
   independent expert rater. There is no headroom left on that fixture; chasing it would fit noise.
   The headroom is against the live corpus, where the baseline sits at +0.60.

Final labels: the owner's where one exists, median-of-three otherwise. Split 55/25/20 stratified by
(channel, label) into train 228 / validation 99 / **holdout 79 that no agent ever saw**.

## Scoreboard (Spearman ρ / nDCG@10)

| Scorer | train | validation | **holdout** | owner-59 | owner-57 | ADR bias |
|---|---|---|---|---|---|---|
| Baseline (scoring v3) | +0.649 / 0.703 | +0.637 / 0.679 | +0.602 / 0.676 | **+0.710** | +0.687 | **+1.35** |
| **A — recentred evidence + refitted priors** | +0.688 / 0.782 | **+0.690 / 0.869** | +0.683 / 0.779 | +0.637 | **+0.720** | **+0.03** |
| B — speech acts over utterances, no metadata | +0.663 / 0.681 | +0.614 / 0.787 | +0.637 / 0.581 | +0.499 | +0.562 | +0.20 |
| C — claim strength × subject portability, no metadata | **+0.691 / 0.779** | +0.620 / 0.781 | **+0.700 / 0.776** | +0.493 | +0.538 | −0.15 |

"ADR bias" is mean predicted score minus mean label over the 83 ADR-channel rows — the dominant
error the round was called to fix.

**Winner: A.** C takes the holdout by 0.017 but trails A on validation by 0.070 and collapses on
the owner's labels (+0.538 vs +0.720). A is the only entrant that beats the baseline on the owner's
own rows, and it removes the ADR bias almost exactly.

### What A changed

The diagnosis is sharper than "the ADR prior is too high": v3's evidence terms are **one-sided**, so
they saturate the +1.30 ceiling on almost any prose — 32 of 46 training ADR chunks landed in
[3.40, 3.85] and 15 of the top 16 rows overall were ADRs. A centres the evidence layer so it moves
entries both ways, then refits every channel prior to a fixed point ("labelled mean minus the mean
adjustment this channel actually receives"), taking ADR from 2.55 to **1.42** and driving per-channel
bias to ≈0.00 for every channel with n ≥ 10. On top it adds a **portability** term — breadth of named
third-party technology minus intra-repo cross-reference density — which is what separates a durable
ADR chunk from an ordinary one (within-ADR ρ +0.33 → +0.49).

### Metadata-free designs lose the owner, twice now

B and C independently threw provenance away and scored it as no loss on jury labels — and both
land near +0.50 against the owner. Round 2 found the same thing from the other side ("dropping it
costs ~0.3 ρ on the secret set"). Two tournaments have now produced the same result: **the
provenance signal is load-bearing for the owner's notion of usefulness even where jury labels say
it is redundant.** Treat a future metadata-free proposal as needing that specific evidence.

## The contested category: `measurement`

A's owner-guard number is the whole story of where jury labels and owner labels part company.
Excluding **two** rows moves it from +0.637 to +0.720, and the incumbent from +0.710 to +0.687:

| channel | owner n / mean | jury n / mean |
|---|---|---|
| `measurement` | 2 / **2.00** | 9 / **0.22** |
| `organic_note` | 2 / **4.00** | 13 / **1.15** |
| `review` | 13 / 0.69 | 24 / 1.42 |

A fitted the jury's view and crushed the channel: two retrieval-sweep records the owner labels 2
score **0.08** and **0.37** under A (2.64 and 3.02 under the incumbent). The plausible reading is
that the owner ran those sweeps and knows the numbers transfer, where a rater sees project-specific
detail.

**Not corrected here.** Retuning a channel on an n=2 basis is the overfitting this round was
designed to avoid, and the disagreement is a question about whose labels define the target — an
owner decision, not a modelling one. It is the first named risk carried into A's port.

## Artifacts

`promotion-scoring-eval/round3/agent{A,B,C}/` — scorers and METHOD writeups, committed **before**
selection so the artifacts survive whichever model wins. Round 1's report was lost exactly this way:
it exists only on the unmerged branch `task/otlp-research`, so ADR-0018's citation of it has never
resolved from `main`.

Reference labels and the rebuild script are committed; the row bodies are not (they quote
private-repo docs). `rebuild_fixture.py` reconstitutes the fixture, `score_round.py` runs a whole
round against a split directory.
