# Lane C — passage rhetoric

## Headline

The label is attached to a chunk, but usefulness is a property of the *passage*. So this scorer
reads only `value`. It splits the chunk into rhetorical units, asks of each one **"is this a claim,
and of what kind?"** and **"is the claim's subject portable?"**, and scores the chunk as an order
statistic over its units plus whole-chunk register corrections. No provenance channel, no path
prior, no `source_file`, no `project_id`, no `rating`, no `access_count`, no `created_at`, no `id`.

Train **ρ = +0.6906**, nDCG@10 = 0.779 (incumbent v3 on the same 228 rows: ρ = +0.6493, nDCG@10 =
0.703). Held-out-fold ρ (constants frozen, 5 folds scored separately): **+0.687**.

## Why this shape

Two observations drove it.

**1. The strongest signals in the training data are grammatical, not lexical.** Univariate Spearman
against the label, over the 228 training rows:

| feature (per 100 words unless noted) | ρ |
|---|---|
| subordinating conjunctions (`because/which/if/unless/than/…`) | **+0.447** |
| chunk length in words | **+0.440** |
| negation (`no/not/never/nothing/without/…`) | **+0.416** |
| present-tense copula and stative verbs (`is/are/does/means/returns/…`) | **+0.416** |
| causal + failure-mode connectives | +0.381 |
| deontic modals (`must/never/always/required/…`) | +0.337 |
| digit characters (per 100 chars) | −0.306 |
| capitalised tokens | −0.263 |
| coordination vocabulary (`gate/wave/lens/worktree/AC-n/…`) | −0.204 |

A single regex over closed-class function words — the ones that only appear inside finite clauses —
reaches ρ = +0.512 on its own. That is the whole "a third of the corpus is machine-written prose
whose register gives it away" observation, made numeric: a status recap, a coordination table and a
transcript are all *short on subordinate clauses and long on digits and proper nouns*, whatever
file they live in.

**2. Once you read the passage, the document's identity adds nothing measurable.** Adding a
provenance prior on top of the finished model (ADR `+0.15`, `docs/work` `−0.075`, `.remember`
`−0.15`) moves train ρ from +0.6906 to +0.6959; at `+0.3` it is +0.6947, at `+0.8` it is +0.6716.
That is noise, and it points the wrong way at any weight big enough to matter. Rejected. The
incumbent's dominant error — ADRs at +1.36 — is not an ADR prior that needs retuning; it is a prior
standing in for a reading of the text. Reading the text removes it: this model's mean error on ADR
chunks is **−0.18** (n=47), and no path bucket with n≥7 is off by more than 0.36.

| path bucket | n | mean score | mean label | error |
|---|---|---|---|---|
| `/docs/work/` | 76 | 0.95 | 1.11 | −0.15 |
| `/docs/` (other) | 52 | 1.29 | 1.23 | +0.06 |
| `/docs/adr/` | 47 | 1.88 | 2.06 | **−0.18** |
| `~/.claude/…/memory/` | 21 | 1.22 | 1.43 | −0.21 |
| `/docs/reference/` | 10 | 1.45 | 1.50 | −0.05 |
| `/docs/plans/` | 7 | 0.90 | 0.86 | +0.04 |
| `/.remember/` | 3 | 0.29 | 0.00 | +0.29 |

## The model

**Segmentation.** The chunk is split into units: YAML frontmatter (kept only for its `description:`
line, which is a written summary), headings, fenced code, table rows, table separator rows, list
items with their continuation lines, link-only list items, and sentences of running prose.
Headings, code, frontmatter and separator rows carry no claim and are scored as absent. A chunk
whose first 300 characters contain a tool-call transcript is a transcript and returns 0.15; one
that reaches a transcript later is truncated at it and the prose prefix is scored.

**Per unit.** Counts per 100 words (denominator floored at 14 so a five-word line cannot
manufacture a density), each family capped:

- *claim* = deontic modals + causal/failure connectives (a `silently` / `no error` / `fails open`
  match counts double) + contrast markers + remedy markers + subordination + negation + a
  measurement (full weight when a measurement word and a number-with-unit co-occur, half otherwise;
  test counts are stripped first, per the rubric).
- *portability* = `1 + generic-technology-vocabulary − filename/path density`, floored at 0.25, and
  applied as a **multiplier** on the claim. An explanation of a local file is still local; the same
  explanation of `HttpClient` or a cron trigger is not.
- *noise* = status vocabulary + coordination vocabulary + links/cross-references + second-person
  instruction, subtracted.

Unit value = claim × portability − noise. A link-only list item is a flat −0.6.

**Per chunk.** `0.35 + 1.00·best + 0.30·runner-up + 0.50·mean` over unit values, then whole-chunk
corrections: log-length, sentence-terminator density, parenthesis density (asides, citations and
hedges), capitalised-token density, filename density, and coordination vocabulary anywhere in the
chunk. A logistic maps the unbounded raw score onto 0–4.

**Why the order statistic.** The rubric's own instruction is that a chunk is judged on what is in
it, and a durable fact inside a plan is still a durable fact. `best` alone gives ρ +0.682; `mean`
alone +0.682; the mixture +0.691. The runner-up term is what stops one lucky sentence carrying a
page of boilerplate.

## The `source_file` question

**Decision: ignore `source_file` (and `path`) entirely.** Not "invert the incumbent's rule" —
delete it.

The evidence: the eight training rows whose stored `path` is a store-minted hex name — the exact
population the incumbent routes to `organic-note` at prior 2.30 without ever consulting anything —
are ordered correctly by text alone.

| label | score |
|---|---|
| 4 | 2.89 |
| 4 | 2.52 |
| 3 | 1.69 |
| 1 | 0.67 |
| 0 | 0.50 |
| 0 | 0.38 |
| 0 | 0.25 |
| 0 | 0.22 |

All four zeros are Hermes conversation-transcript ids; all four land at or below the 0.4 promotion
floor, and they get there because they read as chat status (`Fixed and re-pushed in the
background…`), not because a path was inspected. The two fours are agent-written measured facts.
The three sub-populations the brief names — transcript ids, quotes from real repo documents, and
curated findings that *cite* a code file — do not need to be told apart at all, because what
separates them is exactly what the register test already measures. Treating a citation as a
provenance (the incumbent's failure) and treating it as a pointer (the naive inversion) are both
wrong; the fix is to stop asking the path anything.

The one thing a `source_file` mention still costs a chunk is indirect and comes from the *text*: a
chunk that spends its words naming `.md`/`.cs`/`src/` paths is scored down as a pointer, whether or
not those names also appear in its metadata.

## Tried and rejected

| idea | measured | verdict |
|---|---|---|
| Coordinate-descent fitting of all 22 constants on train ρ | train +0.634 → but 5-fold **re-tuned** CV +0.556 vs +0.591 for the hand-set constants at the same stage | Rejected. Fitting made it worse out of fold. Constants stayed hand-set on round numbers. |
| Linear model over 26 chunk-level features, weights fitted by rank ascent | train **+0.708**, honest 5-fold CV **+0.570** | Rejected. The in-sample ceiling of this feature set is ~0.71 and a fitted linear reader gets there by memorising. |
| Provenance/path prior added on top | +0.005 at best, sign-flips negative by weight 0.8 | Rejected (above). |
| Whole-chunk closed-class "prose-ness" as an extra term | alone ρ +0.512; added on top: 0.674 → 0.648..0.652 | Rejected — already carried by the unit-level subordination/negation terms plus length, sentence density and the table/list handling. Double counting. |
| Hedged-uncertainty register (`I cannot say`, `not verified`, `open question`) | 6 training rows, mean label 1.33 vs global 1.39 | Rejected: under the 20-row floor **and** no signal. It would have fixed exactly one visible error (a label-0 audit chunk that still scores 3.1). |
| Findings-table rows re-read as prose when a cell exceeds *N* words | N=20: 25 rows affected, ρ 0.691 → 0.689 (neutral). N=30: ρ 0.698 but only 16 rows affected | Rejected on both branches: the version with enough support does nothing, the version that helps is fitted to 16 rows. |
| Backticked-identifier density as a locality penalty | identifier density correlates **+0.15** with the label, not negatively; only filename/path references are negative (−0.14) | Changed rather than rejected: locality is measured by filenames and repo paths only. Naming `HttpClient` is concrete technical prose; naming `src/Foo/Bar.cs:143` is a pointer. |
| Whole-chunk digit-density, status-register and pointer-register terms | each neutral-to-negative once parenthesis, capitalisation and filename density were in — measured at that stage, ρ 0.662 → 0.674 on removing all three | Removed. |
| Hard-killing any chunk containing a tool-call transcript | sank a label-4 organic measured fact to 0.15 | Replaced by the 300-character prefix rule. |
| GENERIC technology vocabulary (the one hand-authored word list) | removing it: ρ 0.691 → 0.685, nDCG 0.779 → 0.684 | Kept, but it is the weakest component and the first thing to drop if it ages badly. |

## Robustness

Constants perturbed by ±15%, train ρ:

- **one at a time** (44 runs, every constant up and down): min **+0.6867**, max **+0.6948**.
  Worst single perturbation is `w_norm ×1.15` at +0.6867 — 0.004 below baseline.
- **all 22 simultaneously**, 300 independent uniform draws in ±15%: min **+0.6805**, p5 +0.6840,
  median **+0.6901**, p95 +0.6959, max +0.6991.

The response surface is flat because nothing was fitted to it. There is no constant whose exact
value the result depends on.

## Calibration at the floor

The logistic is centred so that `score < 0.4` is a usable exclusion gate rather than an artefact.
On train, 35 rows fall below 0.4: **30 label-0, 4 label-1, 1 label-2**. Nothing above label 2 is
ever excluded. That is 54% of all label-0 rows removed at 86% precision. Mean predicted 1.27 vs
mean label 1.39; RMSE 0.83. Mean label by score band: 0.17 (<0.4), 0.94, 1.59, 1.94, 1.88, 2.89,
2.67 — monotone except for a flat spot between 1.5 and 2.5, where 60 rows sit that the model cannot
separate, and a 3-row top band too small to read.

## Where I expect it to generalise badly

- **A local decision written in perfectly general prose.** The largest remaining errors are ADR
  chunks that explain the project's own type system in clean explanatory English —
  `UserConfiguration.BaseCompensationPreferences is typed …, because …` — labelled 1 and scored
  2.8–3.2. The portability multiplier can only see filenames and a generic-vocabulary list; it
  cannot see that `BaseCompensationPreferences` is a domain type nobody else has. This is the
  model's structural blind spot and I do not think a lexicon closes it.
- **A durable fact written as a grid.** The mirror failure: the label-4 findings-table row (`a wrong
  project_id returns empty with no error`) scores 2.14 because it lives in a table cell among
  severity columns. I tried and rejected the fix above for lack of support; if the holdout is
  table-heavy this will cost.
- **Register drift.** The status/coordination vocabulary is drawn from one org's agent output
  (`wave`, `lens`, `gate`, `worktree`, `AC-n`, `persona`). A project that coordinates in different
  words gets scored as prose. The grammatical features (subordination, negation, sentence density,
  digits, capitals) are drift-proof; the vocabulary lists are not.
- **Length.** `log(words)` earns its weight partly because this chunker leaves fragments short. A
  different chunk size distribution would weaken it; it is worth roughly 0.036 of train ρ.
- **Per-project spread.** Train ρ within project: arasz-home-page +0.83 (n=23), jsaa +0.67 (n=113),
  ai-raccoon +0.62 (n=45), **ai-badger +0.51 (n=41)**. ai-badger's docs are release notes and
  framework self-description — prose about prose — and the model reads them more generously than
  the raters do.
- **Non-English or heavily code-bearing chunks.** Everything here is English function-word counting.
  A chunk that is 80% code with a two-line explanation scores on the two lines, which is probably
  right, but a chunk of pure code scores 0.20 by fallback rather than by judgement.

## Reproducing

```
python3 scorer.py <candidates.json>      # -> [{"id": int, "score": float}] on stdout
```

99 validation rows in 0.09 s. Standard library only; the whole model is `re` counting and
arithmetic over `math.log`/`math.exp`, so the C# port is `Regex.Matches(...).Count` plus the same
constants.
