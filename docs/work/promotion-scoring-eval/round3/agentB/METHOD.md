# Lane B — score the speech act, not the provenance

## The bet

The incumbent asks *where did this row come from* and lets content nudge the answer inside a
clamp. This model never asks. It reads only `value`, and it asks a different question:

> **What kind of utterance is this, and how many utterances of that kind does the chunk contain?**

The rubric is already written in those terms and I took it literally. It says a chunk that is
"only a heading, a link list, or a table of contents is 0 even if the document it came from would
be a 3"; that "test counts and command output are not measurements"; and that "a durable fact
inside a plan is still a durable fact". Those are three statements about *speech acts* — an index
is a pointer, a build log is a status report, a gotcha is a claim — and all three explicitly
overrule the document kind. So the unit of judgement here is not the row and not the file: it is
the **utterance**.

The model:

1. **Split the chunk into units.** A unit is a paragraph, a bullet item, or a sentence
   (`\n\n`, a bullet start, or a sentence boundary), keeping only units of ≥8 words.
2. **Score each unit's claim strength.** Positive when the unit reads as connected English
   (function-word fraction), rules something out (negation/contrast), names a surprise
   (`silently`, `trap`, `root cause`, `no error`), states a rule (`must` / `never` / `is not`),
   gives a mechanism (`because`, `so that`, `which means`), carries a measured quantity with
   units, or tells a reader what to do. Negative when it reports progress, does plan-ledger
   bookkeeping (`WP-3`, `AC:`, `Gate:`, `Effort`, `Severity`, `Findings`), or is a table row.
3. **Combine at chunk level.** The mean of the two strongest units (does this chunk contain a
   good claim at all?), the total positive claim volume (is it one good line in a wall of noise,
   or sustained?), a capped length term, and four whole-chunk artifact detectors that no
   unit-level view can see: link density, frontmatter, cross-references per unit, and
   starts-with-a-bullet.
4. **Calibrate.** Variance-match the ridge output onto the 0–4 label scale so the 0.4 floor means
   something, with a monotone soft toe below 0.1 so the worst entries stay ordered instead of
   tying at zero. Both steps are monotone: they move the floor decision, never the ranking.

Chunk weights were fitted by ridge regression (λ=3, closed form, 8 parameters over 228 rows) and
rounded to two decimals; rounding costs nothing (ρ 0.6630 exact → 0.6630 rounded). Unit weights
are reasoned, not searched — see "Rejected" below for why.

## Measured

All numbers are against `train_labeled.json` (228 rows) unless stated.

| model | train ρ | nDCG@10 |
|---|---|---|
| **this scorer** | **+0.663** | 0.681 |
| incumbent v3, same 228 rows | +0.649 | 0.703 |

Honest generalisation estimates (feature definitions fixed, weights refitted inside each fold):

| estimate | ρ |
|---|---|
| 5-fold CV | +0.645 |
| nested holdout, 3 rotations (fit on 152, score 76) | +0.685 / +0.622 / +0.643 → mean **+0.650** |

The incumbent's published holdout is +0.603, so the expected gain is real but modest — roughly
+0.04, not the +0.25 the 0.92 inter-rater ceiling says is theoretically available. Regex features
appear to saturate around ρ ≈ 0.65 on this corpus: five structurally different designs I tried
(flat linear, multiplicative form-gate, unit-aggregation, greedy-selected, ridge-over-everything)
all landed between 0.61 and 0.66 CV.

Calibration, on train: mean predicted score by true label is 0.50 / 1.40 / 1.86 / 2.28 / 2.63 for
labels 0–4. 20.6% of train rows and 25.3% of validation rows fall under the 0.4 floor, against a
24.6% label-0 rate on train — so the floor excludes about the right *number* of rows. Of the 47
train rows it excludes, 32 are labelled 0, 15 are 1, and 4 are 2.

Runtime: 0.10s for 327 rows. Standard library only, no clocks, no randomness.

### Drop-one ablations

| removed | train ρ | CV ρ |
|---|---|---|
| — (full) | 0.663 | 0.645 |
| top2 | 0.647 | 0.627 |
| volume | 0.655 | 0.635 |
| length | 0.661 | 0.644 |
| links | 0.650 | 0.631 |
| frontmatter | 0.650 | 0.638 |
| xrefs | 0.649 | 0.630 |
| startsbullet | 0.644 | 0.626 |

And the ablation that tests the central bet — strip every speech-act cue from the unit scorer and
leave only the "is this prose" and "is this a table row" terms:

| unit scorer | train ρ | CV ρ |
|---|---|---|
| full | 0.663 | 0.645 |
| shape only, no act cues | 0.576 | 0.549 |

The speech-act layer is worth about **+0.09** CV over a pure prose-density detector. That is the
part of the design that is actually new, and it is the part that pays.

### Rule support

Every lexicon and feature clears the ~20-row floor on 228 training rows: NEG 187, CONTRAST 136,
XREF 120, NORM 113, ADVICE 87, STATUS 86, LEDGER 64, CAUSAL 54, table-row units 49, GOTCHA 49,
MEASURE 34, STARTS_BULLET 31, FRONTMATTER 26, UNIT-with-units 26, LINK 25. Nothing is keyed to a
document, a project, or a path.

## Rejected

Each of these was built and measured, then dropped.

- **Portability by third-party technology name.** The most obvious reading of "portable" is
  "talks about something outside this repo", so I built a 90-term lexicon of external tech
  (Azure, Gmail, ZipArchive, Stryker, ESLint, Bun, SQLite, …). Distinct-vendor count vs label:
  **ρ = +0.028**, and the bucket means are flat (1.24 at zero vendors, 1.22 at six). Dead. The
  raters are not rewarding externality of *subject*; the label-4 rows about `ZipArchive` and the
  label-1 rows about `HttpClient` name equally foreign things.

- **Document kind as a feature at all.** `/adr/` alone is ρ +0.331 and all three path buckets
  together reach train ρ 0.309. Bolted onto the finished model they raise train (0.663 → 0.672)
  and *lower* CV (0.645 → 0.640). Once claims are counted at the unit level, document kind is
  redundant — which is the incumbent's mispricing stated as a measurement.

- **Searching the unit weights.** Coordinate descent over the ten unit-level weights, scored by
  5-fold CV, reached CV 0.661 / train 0.667. A nested check (tune on 152 rows, score the held-out
  76) gave **0.482** against **0.613** for the same model with hand-reasoned weights. The search
  was fitting the folds. Every unit weight in the shipped file is a round, reasoned number.

- **A multiplicative form-gate** (`score = form × act`, so an artifact can't be rescued by cue
  words): CV 0.628 vs 0.626 additive. A wash, and harder to read. Dropped.

- **Checkbox/tick-mark density** (`✅`, `[ ]`): a good discriminator (ρ −0.20) but it fires on
  only **11** of 228 rows. Below the support floor; dropped on the rule, not on the metric.

- **Frontmatter as a fraction of lines** rather than presence: train 0.654 / CV 0.631 vs
  0.663 / 0.645. Presence wins — a chunk carrying a YAML header is a document *opening*, and
  openings are context regardless of how much prose follows.

- **`rating` and `access_count`.** Both correlate (ρ ≈ −0.19). Both are outputs of the store's own
  promotion and retrieval machinery, so scoring on them is circular, and neither exists for a
  freshly written entry. Not used.

- **Numeric-token density** as a top-level penalty (ρ −0.39 alone): subsumed once table rows are
  penalised at unit level; adding it back moved CV 0.645 → 0.642.

## The `source_file` question

**Decision: the model never reads `source_file`, or `path`, or any other metadata field.** So the
`ProvenanceArchetype.cs:119` question — hex path, empty path, hermes transcript id, real repo
document, cited code file — simply does not arise for this scorer. Three reasons, in order of
weight:

1. **There is no data to decide it on.** Hex-named paths are 8 of 228 train rows and 3 of 99
   validation rows; `hermes/…` source files are 5 of 228. Every one of the three sub-cases the
   brief names is under the ~20-row floor. Any rule I wrote for them would be fitted to single
   digits, and the brief says the holdout will punish that. I would rather say so than invent one.

2. **The text already separates them.** Scored on content alone, the four hermes-transcript rows
   (all labelled 0) land at 0.85–1.39, while the two hex-path agent notes labelled 4 land at
   2.30 and 2.43 and the one labelled 3 (a `SshKeyDerivation.cs` citation) at 2.26. The ordering
   the provenance rule is supposed to produce falls out of the writing: a chat dump is a status
   report and scores like one; a curated finding citing a code file is a claim and scores like one.
   `source_file` would be re-deriving, worse, something the words say directly.

3. **The three cases are not one feature.** A transcript id, a document path and a code citation
   mean three different things, and only one of them (the transcript) is evidence about
   usefulness. A single field that means "provenance" for one row and "citation" for the next is
   not a feature; it is two features wearing one name. Splitting them would need the rows from
   point 1, which do not exist here.

If the holdout turns out to be dense in hex-path rows in a way this sample is not, this is the
first place the model will underperform — see below.

## Robustness

Every one of the 24 numeric constants (10 unit weights, 8 chunk weights, 6 shape/calibration
constants) perturbed together by a deterministic ±15% jitter, 300 trials:

| | ρ |
|---|---|
| baseline | 0.6630 |
| all constants × 0.85 | 0.6646 |
| all constants × 1.15 | 0.6608 |
| jitter min / p5 / median / p95 / max | 0.6567 / 0.6586 / 0.6616 / 0.6651 / 0.6673 |
| worst single-constant ±15% move | 0.6608 (`PROSE_FULL` ×1.15) |

Total spread under full ±15% jitter is **0.011 ρ**. Nothing here sits on a cliff, which is what
you would expect from saturating transforms and eight ridge-shrunk weights, and it is the main
reason I stopped tuning rather than chasing the last 0.02 of train ρ.

## Where I expect this to generalise badly

- **Non-English or heavily code-shaped entries.** The function-word fraction is the load-bearing
  shape signal. A chunk that is mostly a code block, a config dump, or non-English prose reads as
  an artifact whatever it says. On this corpus that is usually right; on a corpus of curated
  snippets it would be badly wrong.

- **A terse, high-value one-liner.** `volume` and `length` both reward sustained claim-bearing
  text, so a single perfect sentence — "wrong project_id returns empty with no error" — is
  structurally capped well below 4. Two of the nine train 4s (the two organic memory notes with
  YAML headers) sit at ranks 68 and 81 for exactly this reason. If the holdout's 4s skew short and
  organic rather than long and documentary, this model loses to the incumbent, whose organic-note
  prior would carry them.

- **The `LEDGER` and `STATUS` lexicons are this owner's vocabulary.** `WP-3`, `AC:`, `Gate:`,
  `Severity`, `Findings`, `wave` are how *these* repos write plans. They generalise across the
  five projects in the sample because one person and one framework wrote all five. A sixth project
  that writes plans differently would leak plan chunks upward. This is the least portable part of
  the design, and I would expect it to be the first thing to need re-measuring.

- **`xrefs` punishes dense citation.** A genuinely portable claim that happens to cite four ADRs
  gets penalised alongside the cross-reference stubs the feature is aimed at. It survives ablation
  (CV 0.630 without it) so it is net-positive here, but it is the crudest term in the model.

- **nDCG@10 is slightly worse than the incumbent's** (0.681 vs 0.703) even though ρ is better.
  The very top of my ranking is contaminated by two long, argumentative, entirely local review
  chunks (labels 1 and 0) that are dense in exactly the cues the unit scorer rewards. If selection
  weighs the top of the list more than the brief implies, that is where I lose.
