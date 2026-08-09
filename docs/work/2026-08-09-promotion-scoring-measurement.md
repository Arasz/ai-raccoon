# Promotion scoring — restoring measurement, and what the live bank shows

Date: 2026-08-09. Task: `fix-promotion-algorithm`. Baseline: scoring v3 (ADR-0018 §v3, PR #195).

## 1. Measurement was impossible before this task

`PromotionScoringRealDataTests` is the only gate on the promotion scorer, and it could not run:

| Artifact | Where it was | Consequence |
|---|---|---|
| `reference-labels.json` (61 owner labels) | only on unmerged `task/otlp-research` (`3efb97e6`) | not reachable from `main` |
| `eval.py` (Spearman/nDCG harness) | same commit only | same |
| `labeled_all.json` / v2 / v3 / holdout fixtures | never committed, gone from disk | gate skips permanently |

The ADR already flagged this as "a known reproducibility gap". It was worse than recorded: the
labels themselves — not just the row bodies — were off `main`.

**Fix: the fixture is a join, not an artifact.** The committed labels carry each row's `hash`, and
the row bodies still live in the local bank, so `rebuild_fixture.py` reconstitutes the fixture on
demand. 59 of 61 rows still resolve (ids 1 and 20 have left the bank). The row bodies stay
uncommitted — they quote private-repo docs — but nothing else has to be.

### First measurement in the restored harness

| Scorer | n | Spearman | nDCG@10 |
|---|---|---|---|
| v3 (shipped, agentC prototype) | 59 | **+0.710** | 0.864 |
| v1 incumbent (four additive bonuses) | 59 | +0.085 | 0.553 |

Consistent with ADR-0018's recorded 0.705 (C#) / 0.735 (python) over the full 61; the two dropped
rows account for the difference. **The shipped model is sound on the reference data it was
designed against.** The problems below are elsewhere.

## 2. What the live bank shows

13,286 project-scope rows scored with the v3 prototype.

### The promotion queue is one channel wearing 965 hats

| | |
|---|---|
| queued rows | 965 (across 5 projects) |
| **from the `adr` channel** | **566 — 58.7%** |
| `auto_memory_note` | 138 — 14.3% |
| everything else | 261 — 27.0% |

`adr` is 12.2% of the bank but 58.7% of the queue, and 94% of all `adr` chunks clear 2.0. For
`jsaa` the queue's *minimum* score is 3.25/4 — within that project the ranking has stopped
discriminating.

### The real driver: whole documents promote chunk by chunk

965 queued rows come from **297 distinct source documents — a mean of 3.2 chunks per document.**

| chunks from one document | 33 | 21 | 18 | 16 | 14 | 12 |
|---|---|---|---|---|---|---|
| documents | 1 | 1 | 3 | 1 | 1 | 1 |

The worst case is `contact-backend-architecture.md`, occupying **33 queue slots on its own**.

This is a *selection* defect, not only a scoring one. `SharedExtractionService.RankAll`
(`src/AiRaccoon.Core/Memory/SharedExtractionService.cs:88-105`) sorts by score and dedups only
against the shared tier by exact value or path. Nothing limits how many chunks of one source
document may occupy the queue, so a document whose channel prior is high floods it. A
chunk-level scorer with a perfect ranking would still do this — every chunk of a good document
is individually a good chunk.

### Two channels the priors and the evidence layer disagree about

Channel prior vs. the mean score actually observed over the bank:

| channel | prior | observed mean | n | reading |
|---|---|---|---|---|
| `adr` | 2.55 | **2.76** | 1622 | evidence *lifts* ADRs further |
| `auto_memory_note` | 2.70 | 2.87 | 479 | same |
| `organic_note` | 2.30 | **1.67** | 159 | refinement removes 0.63 on average |
| `measurement` | 2.10 | **1.00** | 96 | evidence removes 1.10 — near the -1.60 clamp |

The evidence layer moves `organic_note` and `measurement` down and `adr` up. ADR-0018 anticipated
the first of those: it flagged `OrganicRefinement`'s lexicons as owing "a follow-up look" after the
organic-only subset fell to 0.3875 on the 292-row round.

> **Corrected 2026-08-09, same day.** This section originally read that `organic_note` and
> `measurement` are "the two channels whose content is most likely to be a durable portable fact"
> and were therefore being *unfairly* suppressed. Labeled data refutes it: against jury consensus
> both are **over**scored, `organic_note` by +0.57 and `measurement` by +0.55. A prior-versus-
> observed-mean gap shows what the evidence layer does; it says nothing about what it should do,
> and reading a direction of *error* out of it was unsound. Only the labels settle that — see
> `2026-08-09-promotion-scoring-tournament.md`.

## 3. Where this leaves the algorithm

The v3 model ranks well *within* the reference set (+0.7156 unrounded; the +0.710 first recorded
here came from the scorer CLI's 4-decimal output, whose ties perturb the average ranks) and badly
*across* a real bank, for two separable reasons:

1. **No document-level diversity.** Fixable in selection, independent of any scoring change.
2. **Provenance prior dominates content for high-prior channels.** An ADR chunk inherits 2.55
   before a word is read, and the bounded evidence layer (clamp `[-1.60, +1.30]`) cannot demote a
   boilerplate "Alternatives rejected" chunk below the floor of a genuinely portable note.

Both are addressed in the work that follows this record.
