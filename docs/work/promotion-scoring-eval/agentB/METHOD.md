# Promotion scoring: provenance prior + bounded content evidence

`scorer.py` — Python 3 stdlib only, deterministic, no network, no model at scoring time.
`python3 scorer.py candidates.json` → `[{"id": int, "score": float}, ...]`, one row per input
candidate, scores on the same 0–4 scale as the reference labels.

## The diagnosis

The incumbent scorer's inputs are almost all constant on this corpus. Of 61 candidates, 60 have a
`source_file` (so `+2 organic write` fires once), and essentially every one mentions another
project id (so `+2 cross-project` fires on all of them, including directory-index chunks whose
only "cross-project" content is a row linking to a file with `ai-raccoon` in its name). What
remains to separate 61 entries is `+1 accessed` and `+0.5 recent` — which is why the incumbent
produces exactly three distinct values across the whole set (2.5 ×50, 3.5 ×10, 4.5 ×1) and scores
**0.052 Spearman** against the reference labels on the 20-item dev set. It is not mis-weighted;
it is measuring something that does not vary.

The property that does vary, and that the labels track, is **what kind of document the chunk came
out of**, and **whether the chunk states a fact or points at one**.

## The model

Two stages, deliberately kept legible rather than fit.

### 1. A provenance archetype supplies a prior

Every candidate is classified into one of 14 archetypes from its path, its `source_file`, and (for
one case) a markup signature in its text. The archetype carries a prior on the 0–4 scale. Ordered
first-match, because the ordering is itself a judgement — a document named
`…-full-project-review-charter.md` is a charter, not a review; a file under `docs/plans/` is a plan
even when its name says `perf`.

| Archetype | Prior | Recognised by | Why that prior |
|---|---|---|---|
| `organic_note` | 3.45 | `source_file is null`, or a content-addressed `<hex>.md` name | An agent chose to write this. It was never in a doc tree, so promotion is the only way another project sees it. |
| `adr` | 3.00 | `/adr/`, `/decisions/`, or `NNNN-slug.md` that is not `YYYY-MM-DD-…` | A ratified decision plus its rationale — durable by construction, and the rationale generalises past the repo. |
| `charter` | 2.45 | `charter` in the filename | Standing rules written before the work, not findings from it. |
| `explanation` | 2.30 | `/explanation/`, `/design/`, `architecture` in name | Why the system is shaped this way; outlives any given release. |
| `measurement` | 2.10 | `sweep`, `benchmark`, `perf` in name | A measured result, but usually a tuning number for one system. |
| `research_synthesis` | 1.90 | `/archive/`, `research`, `synthesis`, `report` | Durable analysis, single-project reach, already partly superseded. |
| `reference` | 1.45 | `/reference/`, `/how-to/`, `/tutorial/` | True and useful, but it is this repo's configuration, not a portable lesson. |
| `work_note` | 1.15 | fallback | |
| `catalog_page` | 1.10 | `skills.md`, `getting-started.md`, `/guide` | Catalog rows; re-findable by search in its own repo. |
| `changelog_entry` | 1.05 | `/changelog/`, `/releases/` | Historical status: it says what changed, in a version nobody else runs. |
| `plan` | 0.85 | `/plans/`, `PLAN-*`, `*-plan.md` | Work packages, acceptance criteria, wave schedules — true only while in flight. |
| `review` | 0.80 | `/reviews/`, `review`/`findings`/`incident` in name, `moe-*`, `A6-`-style lens files | Findings about one repo at one commit. |
| `doc_index` | 0.25 | basename `README.md` / `CHANGELOG.md` / `index.md` | Rows of pointers. Promoting one adds nothing a search would not find. |
| `turn_mirror` | 0.45 | ≥2 `<invoke …>` / `<parameter …>` / `</content>` markup hits in the value | A transcript of the agent's own tool calls. Whatever durable fact it contains is landing as its own entry. |

### 2. Bounded content-shape evidence moves the entry off the prior

The adjustment is clamped to `[-1.60, +1.30]`, so evidence argues within an archetype and only
rarely across one. All lexicons are density-normalised (hits per 100 words) so long chunks are not
rewarded for length.

Positive:
- **Generalisable-rule language** (`never`, `by design`, `trap`, `mitigation`, `is not evidence`,
  `recorded here so nobody…`, `read that comment before…`) — up to +0.75. This is the single
  feature that separates the label-3 charter chunk ("A cited log is not evidence", "Never present a
  reasoned guess as a measurement") from the label-1 findings table in the same directory.
- **Measurement, with the word and the number** — `measured`/`best-of`/`wall time`/`observed`
  co-occurring with a number carrying a unit (`470ms`, `96 MB`, `174/174`, `nDCG@5`). Up to +0.50.
  Requiring both keeps it from firing on every review table that happens to contain integers.
- **Cross-project subject** (+0.25) — a foreign project id in the *first 250 characters*, not
  anywhere in the text. The label-4 entry is stored under `hermes-default` and opens "jsaa pre-push
  gate…". This is the incumbent's `+2 cross-project` narrowed until it discriminates.
- Heading start (+0.12), any prior access (up to +0.20, log-saturating).

Negative:
- **Pointer density** (up to −1.00): ≥55 % table rows, ≥1.5 markdown links per 100 words, ≥2.0
  `*.md` filenames per 100 words, ≥3 version-number table rows. An entry whose payload names other
  documents is re-findable by search; shared promotion adds less than an organic write does.
- **Findings-register rows** (−0.28 / −0.45): lines like `| G4 |`, `| **D1** |`, `| A13 |`.
- **In-flight coordination language** (up to −0.65): `AC:`, `Gate:`, `Effort:`, `Impact:`,
  `worktree`, `Wave 2`, `Persona / model`, `Closes:`, `PR #744`.
- **Superseded / historical** (−0.40): `Superseded`, `Historical note`, `no longer`, `was reversed`.
- **Header-only chunk** (−0.55 / −0.25): YAML frontmatter plus a short preamble is a document
  header, not content. Plus −0.35 under 420 chars, −0.25 under 60 words.

Two guards close the obvious ways this could be gamed by prose quality:
`doc_index` and `turn_mirror` cap their *positive* adjustment at +0.15 — an index cell does not
become a durable fact because the sentence describing another document is well written — and any
entry under 25 words is capped at 0.60 regardless of provenance.

## Why this should beat an additive-reasons scorer

1. **It scores the thing that varies.** The incumbent's features are ~constant here; document
   archetype and content shape are not.
2. **It is a prior plus evidence, not a sum of bonuses.** Additive bonuses let three weak signals
   outvote the fact that the chunk is a README table. A prior with a clamped adjustment cannot: a
   `doc_index` chunk has no path to the top of the queue.
3. **The dominant failure mode is named and penalised directly.** "Ingested doc chunks that merely
   mention another project id" is exactly `doc_index` + pointer-density + foreign-mention-anywhere.
   All three are addressed: the archetype floors it, the pointer penalty subtracts, and the
   cross-project signal was narrowed to the chunk's subject.
4. **Nothing keys on this corpus's strings.** No project name, repo path, filename, or ADR number
   appears in a scoring rule. The features are structural (path segment, markup shape, table
   fraction, link density) or generic English (rule verbs, units of measurement), so a fourth
   project with a different docs tree still classifies.

## Train-set fit

Measured with `eval.py` (average-rank Spearman, exponential-gain nDCG) on the 20 labelled
candidates:

| Scorer | Spearman ρ | nDCG@5 | nDCG@10 |
|---|---|---|---|
| Incumbent additive `score` | **0.052** | — | — |
| Archetype prior alone | 0.875 | — | — |
| Content adjustment alone | 0.783 | — | — |
| **Full model** | **0.911** | **1.000** | **1.000** |

The two stages agree on the coarse ordering and disagree usefully at the margins, which is why the
combination beats either — the prior cannot tell the label-3 charter from the label-1 review plan
in the same directory, and the content features cannot tell an ADR from a README.

**Honest caveat on that number.** The 14 priors were set by reading these 20 labels, so 0.911 is a
fit, not a held-out estimate. The prior-alone row (0.875) is the better indicator of what
generalises: it is 14 numbers over 20 points, and the archetype boundaries were drawn from the
document taxonomy rather than from label values. Expect the held-out figure to land below 0.911.
The largest single risk is `turn_mirror`: it is one entry (id 21), the reference rubric names
turn-mirrors as label 0, and the incumbent ranks that same entry first — I have bet on the rubric
and scored it 1.09 (rank 25 of 61). If the reference labels it high, that one item costs.

**A defect the checks actually caught.** The first archetype pass matched ADRs with
`^\d{4}-`, which also matches `2026-08-04-…` — five dated work notes were being scored as ADRs and
the train Spearman read 0.840 with a *perfect-looking* nDCG. The per-item archetype dump in
`eval.py` is what exposed it; the fix (exclude `YYYY-MM-DD`) moved Spearman to 0.911. The empty-value
and null-path edge cases were likewise probed by hand (`scorer.py` on a two-row synthetic file) and
produced 2.67 for an empty organic note before the <25-word cap was added.

## Ranking on the full 61

Top 10 by score: **61, 20, 1, 40, 19, 17, 3, 30, 31, 4** — the organic cross-project gotcha, both
ADRs, the review charter's standing evidence rules, two parameter sweeps, the memory-architecture
explanation, and the framework-architecture and research-synthesis chunks. All eight `doc_index`
chunks score ≤ 0.40 and six of them score 0.00; every `PLAN-integrated.md` fragment lands below
1.65.

## Files

- `scorer.py` — the deliverable.
- `eval.py` — dev-set harness: Spearman, nDCG, and a per-item archetype dump. Not imported by
  `scorer.py`.
