# Promotion scorer -- Agent C

## Diagnosis

Every one of the 61 candidates in this eval carries the incumbent's `cross-project` and
`recent` flags (verified: `Counter({'cross-project': 61, 'recent': 61, ...})`). That is
the "flood" failure mode described in the brief made concrete: a plan chunk that merely
contains the substring `ingest-jsaa-docs.py` in a filename trips the "mentions another
project" check with zero cross-repo value, exactly as often as a genuine cross-repo
convention like ADR-0002's "the jsaa repo established the reference pattern...". The
incumbent's four additive booleans (organic write / mentions-other-project / accessed /
recent) collapse this whole queue to two scores (2.5 or 3.5) with essentially no
correlation to the reference `usefulness` labels (Spearman ~0.08 for `score`,
`access_count`, and `rating` individually against `train_labeled.json`). Something
content-shaped is needed.

## Approach

A hand-weighted linear model over ~15 interpretable features extracted from `value`
and `path`, each traceable to one clause of the usefulness rubric given in the task:

**Positive**
- `organic` -- no `source_file` (agent chose to write this directly)
- `access_log` = `log1p(access_count)` -- real usage; three archived docs and one
  incident report in the eval set have 10-200x the access of everything else, and the
  brief's rubric explicitly rewards "another project will hit" reuse, which access
  history is direct (if imperfect) evidence of
- `measured` -- regex hits for quantified evidence (`%`, `ms`, `4.5x`, `tokens`, ...)
  -- rubric's "4" explicitly names "measured result"
- `gotcha` -- pitfall language ("returns empty", "no error", "silently", "mitigations:")
  -- rubric's "4" explicitly names "reusable gotcha"
- `attribution` -- another project's name found within an 80-character window of
  pattern/convention language ("established", "same approach", "applies the same",
  "reference pattern", ...). This is deliberately *not* a bare substring match on the
  other project's id (see the `ingest-jsaa-docs.py` problem above) -- the word-boundary
  regex used for the match treats `-`/`_` as word characters, so a project name
  embedded in a filename or compound identifier does not count.
- `len_log` -- weak, capped-influence proxy for a developed thought vs. a terse table
  row (Spearman 0.56, p=0.01 on its own -- the single strongest raw feature, used with
  a modest weight to avoid rewarding padding)

**Negative**
- `superseded` -- "superseded"/"deprecated"/"obsolete" -- brief names "superseded
  reviews" as noise
- `review_meta` -- self-referential review-document preamble ("Reviewer lane:",
  "worktree:", "Lens ", "commissioned", "## Verdict") -- describes the review process,
  not a fact about the system
- `plan_boilerplate` -- work-item bookkeeping ("AC:", "Gate:", "Effort:",
  "Persona / model:") -- matches the "plan/finding detail" (1) rubric rather than a
  durable fact
- `pipe_ratio`, `link_density`, `index_filename` (README/CHANGELOG/skills-index
  basenames) -- catalog/table/TOC shape -- brief names "changelog indexes" and
  "READMEs" as noise verbatim
- `archive_path` -- mild extra discount for a doc under `/archive/`
- `turn_mirror` -- `User:`/`Assistant:` transcript-shaped lines -- brief names
  "turn-mirrors" as noise

All keyword lists and regexes are content-shape heuristics computed fresh from each
candidate's own text -- nothing is keyed off an `id`, and the "other projects" set an
`attribution` match can fire against is derived from the distinct `project_id`s present
in whatever batch is passed to the scorer, never hardcoded. This is what lets the same
script run unchanged against unseen future candidates.

## Weight selection and overfitting check

Weights were selected by coordinate-ascent maximizing Spearman correlation against
`train_labeled.json` (20 points), then validated with leave-one-out re-fitting: for each
held-out point, weights were re-tuned on the other 19 and used to score the one left
out, and the resulting out-of-sample Spearman was compared to the full-train figure.

| | Spearman |
|---|---|
| Untuned initial weights (hand-picked) | 0.69 |
| Coordinate-ascent tuned on all 20 train points | 0.77 |
| Leave-one-out cross-validated | 0.70 |

The LOO figure staying close to the untuned baseline (rather than collapsing) is the
signal that the tuned weights aren't just memorizing 20 points -- an early version of
this scorer that included a raw "cross-project mention count" feature and a looser
"protocol language" keyword list scored higher on train (0.84) but was visibly
overfitting: it inflated single-repo implementation-plan chunks (`AC:`/`Gate:`-heavy
text from `scripts-refactor.md`) above genuine cross-repo convention docs, purely
because both happen to use words like "contract" and "must". That version was reverted
in favor of the tighter `attribution` (proximity-gated) and `plan_boilerplate`
(work-item-bookkeeping-gated) features shipped here.

**Final train Spearman (via the shipped `scorer.py`, exact CLI invocation): 0.77**
(p < 0.001, n=20).

## Verification

```
$ python3 scorer.py candidates_all_unlabeled.json > /tmp/out.json; echo $?
0
$ python3 -c "import json; o=json.load(open('/tmp/out.json')); print(len(o), sorted(x['id'] for x in o)==list(range(1,62)))"
61 True
```

## Top 10 of the full 61-candidate set

| rank | id | score | project | path (tail) |
|---|---|---|---|---|
| 1 | 21 | 13.20 | ai-badger | (organic write) jsonschema import-cost measurement |
| 2 | 4 | 9.19 | ai-raccoon | docs/work/archive/2026-08-04-memory-model-research-synthesis.md |
| 3 | 61 | 8.94 | hermes-default | jsaa pre-push gate flake diagnosis (access_count=8) |
| 4 | 2 | 8.71 | ai-raccoon | docs/work/archive/2026-08-06-adoption-moe-report-a-systems.md |
| 5 | 3 | 8.61 | ai-raccoon | docs/work/archive/2026-08-04-memory-model-research-synthesis.md |
| 6 | 47 | 7.33 | jsaa | PLAN-integrated.md (project_id UX gotcha; train label 4) |
| 7 | 1 | 7.04 | ai-raccoon | docs/adr/0002-opentelemetry-observability.md (train label 3) |
| 8 | 39 | 6.72 | ai-badger | 0.89.0-gate-stops-writing-to-home.md |
| 9 | 42 | 6.61 | jsaa | docs/reference/ci-workflows.md |
| 10 | 8 | 6.58 | ai-raccoon | docs/plans/scripts-refactor.md |

Rank 1 (id 21) is the one organic, measured-benchmark entry in the whole set -- a clean
positive control. Ranks 2-5 are the four candidates with anomalously high access counts
(8 to 178, vs. 0-2 for everything else), which the `access_log` feature surfaces even
though none of them appear in the labeled training set. Ranks 6-7 are the two highest
labels actually present in `train_labeled.json` (4 and 3).
