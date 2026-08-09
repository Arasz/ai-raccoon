# METHOD — lane A, repair of the shipped v3 scorer

**Headline train numbers (228 labelled rows).**

| model | Spearman ρ | nDCG@10 | MAE | 5-fold min ρ | 5-fold mean ρ |
|---|---|---|---|---|---|
| v3 incumbent | **+0.649** | 0.703 | 0.793 | +0.553 | +0.654 |
| lane A (this) | **+0.688** | 0.782 | 0.625 | +0.626 | +0.695 |

Folds are the deterministic interleave `i % 5 == k` — they measure *stability of the
same fitted model across subsets*, not out-of-sample fit; nothing here was fitted per fold.

Architecture is unchanged: channel-routed prior → bounded content evidence → organic
refinement. Everything below is recalibration and three added terms.

---

## 1. What was actually wrong with v3

I reproduced the brief's finding on the training split and then looked at the
mechanism rather than the mean.

**ADR chunks were not just too high, they were degenerate.** v3 put 32 of the 46
training ADR chunks inside `[3.40, 3.85]` and its ADR maximum was 3.85 — the prior of
2.55 plus an evidence layer whose positive terms are one-sided and saturate the
`+1.30` cap on almost any prose. Two consequences:

- the mean error (+1.30 on train, +1.36 on the brief's 347 rows), and
- **within-ADR ρ of only +0.33** — 20% of the corpus compressed into a 0.45-wide band
  at the top of the ranking, where it blocks every genuinely durable entry from any
  other channel. On train, **15 of v3's top-16 rows are ADR chunks** — three of them
  labelled 1, five labelled 2 — while three of the nine label-4 rows score below 2.6.

**The mean errors in the other channels are almost all this same saturation effect
seen from a different channel prior**, not independent bugs. Once the evidence layer
is centred, every channel with n ≥ 10 lands on its labelled mean by construction (§2).

**One outright sign error.** v3 penalises a chunk that starts mid-sentence by −0.18.
On the labels that feature is *positive* (ρ +0.19 overall): a chunk starting mid-sentence
is body prose, and a chunk starting at a heading is disproportionately a section opener,
a TOC or a metadata block. Flipped to +0.15.

## 2. Priors are now fitted, not hand-set

Two steps, both mechanical:

1. Start each channel at its labelled mean shrunk toward the corpus mean (1.40) with a
   pseudo-count of 6, so a thin channel cannot claim a prior it has not earned.
2. Iterate: subtract the mean adjustment the evidence layer actually applies inside that
   channel, to a fixed point. Only channels with **n ≥ 10** get this correction.

The result is that predicted mean = labelled mean for every channel with n ≥ 10 (see the
table in §5) — the brief's bias column goes to zero by construction rather than by tuning.
The ADR prior falls **2.55 → 1.42**.

Six channels are *not* fitted and are hand-set below the 0.4 promotion floor:
`transcript`, `turn_mirror`, `remember_log`, `auto_memory_session`, `auto_memory_index`,
`doc_index`. Every labelled row in them is a 0 (n = 30 between them) and their payload —
a chat transcript, a status journal, an index of pointers — is definitionally not a fact.
Shrinking them toward the corpus mean would drag them to 0.59–0.93 and re-admit noise
above the floor for no gain.

## 3. Three added terms

All three are centred, so they move an entry both ways and do not shift a channel's mean.

**Portability** (`adr`, `charter`, `explanation`, `measurement`, `research_synthesis`,
`reference` only): `0.28 × min(distinct third-party technologies, 5) − 0.55 − 0.15 × xref_density`.

This is the one that fixes the ADR *ranking*. The count of distinct named
outside-the-repo technologies (sqlite, Aspire, Gmail, Angular, GitHub Actions, …) has
ρ **+0.47** inside ADR and **+0.41** inside research_synthesis, against ≈ 0 overall. The
label-4 and label-3 ADR chunks are the ones about an Aspire bug, an Angular `@defer`
clause, a Gmail attachment API, `eslint --fix`; the label-1 chunks are the ones about
this repo's own `UserConfiguration`, `PreferredContractType`, `deriveFreezeList`. Split
the vocabulary in half at random and both halves keep the sign inside ADR (+0.30, +0.40),
so it is not one or two lucky words.

`xref_density` (ADR-nn, issue #nn, `§`, NFR-x) is the mirror image — intra-repo
bookkeeping — at ρ −0.23 inside ADR.

**Substance ramp:** `0.55 × clip((words − 110) / 90, −1, +1)`. Chunk length is the single
strongest content feature on the whole set (ρ +0.43) and is positive inside 10 of the 11
channels with n ≥ 10 (the exception is `auto_memory_note`, at −0.03). It replaces v3's
two cliff penalties (< 420 chars, < 60 words), which fired on the same rows but
discontinuously. Label means by length bucket:
< 30 words → 0.14, 30–70 → 0.50, 70–120 → 1.18, 120–180 → 1.78.

**Durability:** `clip(0.30 × impersonal-rule density, 0, 0.40) − 0.08`, where an
impersonal rule is `is|are|means|requires|holds|applies … never|always|only|not` within
one clause. ρ +0.29 overall, positive in 8 of 11 channels. This is the rubric's "rule,
contract or design decision" shape, distinguished from a first-person work report.

## 4. Ablations

| variant | ρ | nDCG@10 | 5-fold min |
|---|---|---|---|
| v3 incumbent | +0.649 | 0.703 | +0.553 |
| **lane A final** | **+0.688** | **0.782** | **+0.626** |
| − portability layer | +0.693 | 0.724 | +0.546 |
| − durability term | +0.680 | 0.821 | +0.594 |
| − substance ramp | +0.666 | 0.818 | +0.584 |
| − transcript channel | +0.685 | 0.782 | +0.617 |
| − mid_sentence sign flip | +0.680 | 0.783 | +0.608 |
| − fitted priors (v3 priors back) | +0.682 | 0.743 | +0.560 |

**Read this table sceptically, and I do.** The standard error of Spearman's ρ at n = 228
is ≈ 0.066, so *no single row of this table is individually significant*. Two of the
ablations (dropping portability, restoring v3's priors) raise train ρ slightly while
lowering both nDCG@10 and fold stability. I kept both components anyway, for reasons that
are not the train ρ:

- **portability** is the mechanism behind the within-ADR improvement (+0.33 → +0.49) and
  the top-of-ranking gain, and it is the only term that reads the brief's actual
  complaint ("an *arbitrary chunk* of an ADR is ordinary"). Dropping it costs 0.08 nDCG
  and 0.08 fold-min.
- **fitted priors** are justified by evidence outside the train ρ entirely: the brief's
  own per-channel bias table over 347 fresh rows. Calibration also matters for the 0.4
  floor, which ρ cannot see.

## 5. Per-channel result (train, n ≥ 10)

| channel | n | v3 within-ρ | A within-ρ | v3 bias | A bias |
|---|---|---|---|---|---|
| adr | 46 | +0.33 | **+0.49** | +1.30 | −0.00 |
| plan | 24 | +0.52 | +0.49 | −0.40 | +0.00 |
| review | 20 | +0.41 | +0.40 | −0.24 | −0.00 |
| changelog_entry | 15 | +0.01 | +0.20 | −0.34 | −0.00 |
| doc_index | 15 | +0.00 | +0.05 | +0.08 | +0.04 |
| work_note | 14 | +0.48 | +0.57 | −0.15 | −0.00 |
| research_synthesis | 14 | +0.40 | **+0.62** | +0.47 | −0.00 |
| auto_memory_note | 13 | +0.46 | +0.31 | +0.64 | +0.00 |
| other_doc | 13 | +0.32 | **+0.76** | −0.05 | −0.00 |
| reference | 11 | +0.46 | +0.31 | −0.07 | −0.00 |
| explanation | 11 | +0.48 | +0.28 | +0.48 | +0.00 |

Three channels (`auto_memory_note`, `reference`, `explanation` — 35 rows between them)
got *worse* at ordering while getting better at calibration. At n = 11–13 those moves are
inside noise, but they are the honest cost of a globally-shared evidence layer.

Rank separation by label on train (mean percentile rank): 0 → 19.6, 1 → 48.4,
2 → 60.0, 3 → 79.6, 4 → 82.7 — monotone, with only five of the 56 label-0 rows
scoring above the median. The 3-vs-4 boundary is where it stops separating.

## 6. Robustness to constant jitter

Method: tokenize `scorer.py`, multiply every **float** literal (137 of them — priors,
gains, caps, centres, thresholds) by an independent factor, exec the result, re-measure.
Numbers inside regex/string literals are untouched, since tokenization sees a string as
one token.

| perturbation | train ρ |
|---|---|
| none | +0.688 |
| 20 seeds, each constant ±15% independently | **min +0.671, max +0.691, mean +0.682** |
| every constant × 1.15 | +0.679 |
| every constant × 0.85 | +0.697 |
| every constant × 1.30 | +0.672 |
| every constant × 0.70 | +0.697 |

The worst of 20 independent ±15% jitters is 0.671 — still above the incumbent's
unperturbed 0.649. Nothing here sits on a knife edge. (Integer thresholds such as
`min(tech_breadth, 5)` are not perturbed by this check; a ±15% change to them mostly
rounds to the same integer.)

## 7. The `source_file` question

**Decision: keep v3's routing rule, and add exactly one discriminator on top of it.**

What the labels say (this is *all* the evidence there is — 8 rows in train, 3 in
validation, 3 in the 79-row holdout, so I refuse to build more than one rule on it):

| `source_file` on a hex-path row | n | labels |
|---|---|---|
| `hermes/<stamp>_<hex>` conversation id | 4 | 0, 0, 0, 0 |
| null | 1 | 4 |
| a real repo document (`docs/work/….md`) | 1 | 4 |
| a code file (`…/SshKeyDerivation.cs`) | 1 | 3 |
| the bare string `hermes-memory` | 1 | 1 |

Three conclusions:

1. **v3's rule is right about routing.** A hex `path` means the store minted the name for
   an agent write; the row is organic and belongs in the organic refinement, which is v3's
   single best-performing component (within-channel ρ +0.95 on these rows). Whatever
   `source_file` holds on such a row is a *citation*, not a provenance — the entry is a
   curated finding *about* that file, not a chunk *of* it. The two highest-labelled rows in
   the whole family are exactly that (a bank-health measurement citing a work record, an
   owner ruling citing the `.cs` file it overturns), and both are already scored 2.8–3.4 by
   the content layer without any help from the path.
2. **Inverting it — treating the cited path as provenance and routing to its document
   channel — is wrong.** It would route an owner ruling into `work_note` (prior 1.44)
   purely because the code file it cites is not under `docs/`, and it would route a
   measured bank-health fact into `work_note` because the *cited* work record is dated.
   Both are labelled 3–4.
3. **A conversation-transcript id is the one case where `source_file` carries real
   information, and it is negative.** `hermes/20260806_215718_fd7f66` is not a document
   at all; it is the id of a chat in which the agent dumped a status recap. All four are
   labelled 0. v3's organic layer already catches three of them (0.10, 0.10, 0.65) but
   scores the fourth at 1.30 — above the promotion floor. So `TRANSCRIPT_SRC` routes any
   `hermes/<digits>_` source to its own channel with a prior of 0.15 and no content lift.

I am flagging this as the weakest-evidenced rule in the file: **n = 4**, well under the
brief's 20-row bar. I kept it because (a) it is a *cap*, so its only failure mode is
suppressing a durable fact that an agent happened to write during a chat, (b) the content
layer independently agrees on 3 of the 4, so it changes little, and (c) the semantics are
not in doubt — a conversation id is not a document. Its measured effect is +0.003 train ρ,
i.e. nothing. If it must go, delete `TRANSCRIPT_SRC` and the `"transcript"` prior; nothing
else depends on it.

I did **not** build a rule for `hermes-memory` (n = 1) or for the code-file case (n = 1).

## 8. Tried and rejected

| idea | result | verdict |
|---|---|---|
| Surprise/gotcha vocabulary (`silently`, `no error`, `undocumented`, `blind spot`, …) as a separate term | ρ 0.679 → 0.663 when added on top | Redundant: `RULE_RE` already carries trap/gotcha/silently/fails-open. Deleted, not just zeroed. |
| Bolded lead clause (`**…**` at line start) as a "stated finding" signal | +0.007, only when restricted to the document family | Inside noise, and restricting it to a hand-picked family is exactly the fitting I am trying to avoid. Rejected. |
| ADR section type (Decision / Context / Alternatives / Status), which the brief hypothesises | Headings are too sparse to key on — the most common heading word appears in 9 chunks, most in ≤ 5. Chunks with vs without any heading: mean label 1.40 vs 1.39 | Untestable at this sample size, not implemented. |
| Portability applied to *all* channels rather than the document family | ρ 0.688 → 0.634 | Naming tools in a plan or a review is enumerating what was inspected, not deciding about a technology. Kept the restriction; see §9 for the risk this carries. |
| Replace the hand-written evidence layer with a ridge-regressed linear model over 14 features | in-sample 0.695, **out-of-fold 0.630** — below the incumbent | The hand rules (organic status-dump refinement, the hard-noise channels) carry real signal a linear form throws away. Rejected. |
| `access_count`, `rating` as features | 210 of 228 rows are `access_count = 0`; 210 are `rating = 0.5` | No variance. Rejected. |
| `project_id` as a feature | Channel means differ (jsaa 1.52, arasz-home-page 0.87) | Not portable to a new project; refused on principle, not on numbers. |
| Local-identifier density (backticked CamelCase) as a "this is our code" penalty | ρ −0.12 overall, −0.10 inside ADR | Too weak to earn a rule; the positive half (tech breadth) does the same work better. |
| Dropping `measurement` from the document family | +0.009 | Fits 6 rows. Refused. |

## 9. Where I expect this to generalise badly

- **The technology vocabulary is a closed list.** It was written from this corpus's
  stacks (.NET, Python, Angular, Azure, SQLite, GitHub, the messaging platforms). A
  memory bank from a Rust/Kafka/Terraform shop would score near-zero breadth on every
  chunk, and the portability term would collapse to a constant −0.55 inside the document
  family — harmless for ranking (it is a constant), but the ADR ordering gain would be
  gone. This is the single most brittle thing in the file. A maintainer porting to C#
  should treat `TECH_RE` as configuration, not code.
- **Restricting portability to six channels is fitted, not derived.** The story I tell in
  §3 is plausible, but the evidence for the restriction is that extending it to every channel costs 0.054 ρ. If the holdout's plan/review mix differs, this could
  go either way.
- **Priors for `charter` (0 labelled rows), `turn_mirror` (0), `measurement` (6),
  `catalog_page` (7), `organic_note` (4)** are shrunk guesses. `organic_note` in
  particular now sits at 2.00 on the strength of four rows whose mean label is 3.0; if the
  holdout's organic writes are more ordinary, that prior is too generous.
- **`changelog_entry`, `doc_index`, `explanation`, `reference` still order badly**
  (within-ρ 0.05–0.31). Nothing I built reads them well, and I did not invent rules for
  them rather than fit 11–15 rows.
- **The whole model tops out around ρ 0.69 and I do not think regex features will reach
  the 0.92 rater ceiling.** Perfect within-channel ordering *at these priors* would give
  ρ 0.96, so all remaining headroom is semantic reading inside a channel — judging whether
  a paragraph states something an outsider could act on. That is a job for a model that
  reads, not for a vocabulary.
- **The evidence layer is calibrated against a corpus of five projects, four of them the
  same owner's.** House style (bolded lead clauses, `§` cross-references, ADR numbering,
  em-dashes) is doing more work in these features than I can separate out.
