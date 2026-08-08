# Agent A — improved shared-tier promotion scorer

## Diagnosis of the baseline

Ran the baseline's own reasons over all 61 candidates before touching weights.
Two of its four signals turned out to be non-discriminating on this pool:

- `cross-project` (value/source_file mentions another known project id):
  fires on **61/61** candidates. The bank is a multi-project memory store
  whose docs casually name sibling projects ("ai-badger", "ai-raccoon",
  "jsaa", "hermes-default") constantly; a bare substring match carries
  essentially zero ranking information here, exactly the failure mode
  named in the brief.
- `recent` (created in last 30 days): fires on **61/61** — the whole
  extraction pass is itself recent, so this always fires and never
  discriminates on this pool.

That leaves the baseline effectively differentiating only on
`organic-write` (1/61 rows) and `accessed` (10/61 rows), with ties broken
by recency. Baseline Spearman on `train_labeled.json`: **0.228**.

## Approach

Two feature families replace/extend the baseline's four additive terms:

### 1. Source-path taxonomy (primary driver)

A `source_bucket()` classifier (ordered, first match wins) replaces the
binary organic/not-organic split with a graded prior over where the text
came from, matching this project's own docs-tree conventions:

| bucket | base score | rationale |
|---|---|---|
| organic (no `source_file`) | 3.0 | a deliberate agent write, not an ingested doc chunk |
| adr (`/adr/` in path) | 2.0 | ratified decision / contract text |
| explanation (`docs/explanation`) | 1.8 | durable architecture/protocol semantics |
| reference (`docs/reference`) | 1.2 | |
| work (`docs/work`) | 0.5 | review/status notes — wide quality variance; content-shape features do the real work here |
| other | 0.3 | |
| plans (`docs/plans`) | 0.0 | pre-decision/speculative by default; content-shape can still lift it (e.g. a plan doc reporting an *executed, measured* result) |
| changelog | -0.5 | historical, point-in-time, rarely actionable outside its own release |
| readme_index (basename `README.md`) | -1.0 | catalog/table-of-contents file |

### 2. Content-shape signals (regex over `value`)

Additive/subtractive adjustments on top of the taxonomy base:

- `generalization` (+1.5): explicit cross-project-reach language ("every
  project", "any consumer", "framework-wide", "bites every…") — this is
  the *real* cross-project signal the baseline was trying to approximate
  with a bare name mention.
- `measured` (+1.0): numbers-with-units, "measured", "wall-clock", "RSS",
  percentages — rubric's "measured result" for a level-4 fact.
- `imperative` (+0.2, deliberately small): "must", "never", "do not"
  etc. Measured train correlation for this feature alone was weak
  (~0.22) and it fires on ordinary technical prose almost as often as on
  genuine gotchas (e.g. "the files do not actually overlap" is not a
  gotcha), so it stays a minor tiebreaker rather than a major driver.
- `index_row` (-1.5): the entry is mostly table rows that are either a
  markdown link (`[text](target.md)`) or a bare backtick filename in the
  first cell (`` | `name.md` | … ``) — the two shapes a doc index or
  changelog-of-releases table takes. Catches both README-style link
  catalogs and file-listing catalogs (`docs/work/README.md`).
- `table_heavy` (-0.3, only when `index_row` didn't already fire):
  mild penalty for content that is mostly table fragments — less
  self-contained out of its source document — without double-penalizing
  rows already caught by `index_row`.
- `status_dump` (-1.2): opens with YAML frontmatter or a bare
  `Date:`/`Integrator:`/`Corpus:`/`Updated:` metadata line — a
  review-process status header, not durable content.
- `turn_mirror` (-1.5): opens like a mirrored conversation turn
  ("User:", "Assistant:", "Let me…", "Sure, here…") — not present in the
  train set but named explicitly in the task's noise examples, so it's
  covered defensively.
- `superseded` (-0.8): "superseded", "deprecated", "reopened", "stale",
  "orphaned" — rubric's "superseded reviews" noise case.
- `uncertainty` (-1.0): "cannot say", "did not verify", "out of scope",
  "unverified" — review-caveat/negative-finding language, not a durable
  fact.

### 3. Retained baseline signals, down-weighted

- cross-project mention kept at **+0.3** (was +2.0). Since it fires on
  essentially every row in this pool it acts as a near-constant offset —
  harmless to keep (a real, sparser candidate pool would still get some
  signal from it) but no longer allowed to dominate ranking the way it
  did in the baseline.
- `accessed` (access_count>0 or rating>0.5) kept at **+0.4** (was +1.0).
  Train correlation for this alone was ~0.10 — weak but plausible in
  general, so it stays a minor factor.
- `recent` (entry created within 30 days of the extraction pass) kept at
  **+0.2** (was +0.5), computed from `entry_created_at` rather than
  reusing the saturated `created_at`.

Everything is deterministic, stdlib `re`/string logic — no ML, no
network — and ports directly to C# as a set of small predicate/weight
pairs plus a path-bucket switch.

## Train-set fit

Ran `evaluate.py` (Spearman via a small stdlib rank-correlation helper,
handling ties) against `train_labeled.json` (n=21):

- Baseline algorithm's own `score` field: **Spearman 0.228**
- This scorer: **Spearman 0.643**

Top of the train ranking now correctly surfaces the organic write (label
4), the two ADR-adjacent/explanation docs (labels 2–3), and the measured
RRF sweep results (label 2–3), while doc-index/changelog-catalog rows and
status-dump openers sink to the bottom (labels 0).

## Known remaining weaknesses

- `imperative` and `measured` are single-word/phrase regexes and can
  false-positive on incidental prose (e.g. "do not actually overlap").
  Weighted low deliberately for this reason.
- No feature currently distinguishes "plan-assignment minutiae" (wave/
  work-package scheduling prose) from genuine content within the `work`
  bucket beyond the caveat/status-dump/index detectors already in place;
  a couple of pure-planning-jargon train rows (label 0) still score
  moderately (~1.0–1.5) because nothing else fires on them. Left alone
  rather than adding a narrow regex fit to one document's wording.
- `turn_mirror` has no positive train example to validate against —
  included defensively per the task brief's noise-category list, not
  empirically tuned.

## Verification

`python3 scorer.py candidates_all_unlabeled.json` exits 0 and prints a
JSON array with all 61 `{"id", "score"}` entries (scores range
-1.5..4.7, median 0.9).
