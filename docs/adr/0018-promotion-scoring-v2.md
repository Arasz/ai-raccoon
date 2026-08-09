# 0018 — Promotion scoring v2: archetype prior + content evidence

Date: 2026-08-08

Status: Accepted

## Context

`SharedExtractionService` scores project memory candidates for promotion to the shared
tier with four flat additive bonuses: `+2` organic write (no `source_file`), `+2`
cross-project (any sibling project id as a bare substring, anywhere in the value or
`source_file`), `+1` accessed, `+0.5` recent (created within 30 days). The shared tier
filled with noise this way: 86 entries, ~53 of them Hermes conversation turn-mirrors
bulk-shared on 2026-08-07, plus doc chunks promoted for merely mentioning another
project's name. The owner wiped the tier (backup:
`~/.ai-raccoon/backups/shared-tier-backup-2026-08-08.json`) and commissioned a 3-agent
scoring evaluation, with scorers and harness preserved under
`docs/work/promotion-scoring-eval/`.

> **Citation correction, 2026-08-09.** This paragraph read "landed via PR #179's
> `docs/work/2026-08-08-promotion-scoring-eval.md`". That round-1 report is **not on `main`** —
> it exists only in `3efb97e` on the unmerged branch `task/otlp-research`, so the citation has
> never resolved for anyone reading this ADR from `main`. The round-**2** tournament that selected
> the shipped v3 design is committed and is the reachable record:
> `docs/work/2026-08-08-promotion-scoring-round2.md`. The labeled fixtures cited further down
> remain deliberately uncommitted (public repo, private-repo doc quotes), which means the parity
> gate `PromotionScoringRealDataTests` cannot be re-run without them — it skips unless
> `AIRACCOON_SCORING_EVAL_FIXTURE` is set. That is a known reproducibility gap, not an oversight.

**Measured incumbent failure.** Full-set Spearman **+0.125** against 61 hand-labeled
candidates (usefulness 0–4). The incumbent's own signals are near-constant on a
multi-project bank: 61/61 candidates matched `cross-project` (filenames like
`ingest-jsaa-docs.py` count), 61/61 matched `recent`, and 60/61 have a `source_file` so
`organic-write` almost never fires. The score collapses to three distinct values (2.5 ×50,
3.5 ×10, 4.5 ×1) — it measures nothing that varies on this corpus.

**Winning design.** Agent B's archetype-prior + bounded-content-evidence model, plus an
override the other two agents independently got right: **+0.681 held-out / +0.754
full-set** Spearman, nDCG@10 0.914 (see the eval report's results table).

**A second validation round** extended the labeled set with the 86 previously-promoted
shared entries (all organic — `source_file` null). On that slice, the ported archetype +
evidence model alone scored **+0.145 Spearman**: the organic prior (3.45) saturates
everything near 4.0, and content evidence cannot separate a status/turn-mirror dump
("Done. Everything shipped... 3272 passed, 0 failed, exit 0 in 3m18s") from a durable
fact — test-result counts read as measured evidence. This motivated the organic
refinement layer (below), prototyped and validated in Python at
`docs/work/promotion-scoring-eval/` before the C# port.

## Decision

Replace the four additive bonuses with `PromotionScorer`
(`src/AiRaccoon.Core/Memory/PromotionScorer.cs`), combining three stages:

1. **`ProvenanceArchetypeClassifier`** — classifies a candidate into one of 14 archetypes
   from its path/`source_file` shape (first-match-wins; ported from agentB/scorer.py's
   `archetype()`), each carrying a prior on the 0–4 scale: `organic-note` 3.45 > `adr`
   3.00 > `charter` 2.45 > `explanation` 2.30 > `measurement` 2.10 >
   `research-synthesis` 1.90 > `reference` 1.45 > `work-note` 1.15 > `catalog-page` 1.10 >
   `changelog-entry` 1.05 > `plan` 0.85 > `review` 0.80 > `doc-index` 0.25 >
   `turn-mirror` 0.45. The ADR pattern (`NNNN-slug.md` or `/adr/`) explicitly excludes
   `YYYY-MM-DD-...` dated work notes — a defect the eval harness caught by hand
   (five dated notes were scoring as ADRs before the exclusion).
2. **`PromotionContentEvidence`** — bounded content-shape adjustment, clamped to
   `[-1.60, +1.30]` (capped at `+0.15` for `doc-index`/`turn-mirror`, so prose quality
   cannot lift a directory index into the queue). Bonuses: generalizable-rule language,
   measured-numbers-with-units, a foreign project id proximity-gated to the opening 250
   characters (not a bare substring anywhere), heading start, a small log-scaled
   access-count bonus. Penalties: markdown link/table density, findings-register rows,
   in-flight coordination markers (`AC:`/`Gate:`/`Effort:`/`worktree`/`Wave`), superseded
   markers, frontmatter-only/very-short chunks.
3. **`OrganicRefinement`** — applies only to organic entries (`source_file` null; ported
   from `refine.py`). Penalizes status-opener language, status-vocabulary density,
   second-person address, and ≥2 commit hashes; strips test-result counts ("174 passed",
   "exit 0") before counting real measured evidence so they cannot masquerade as
   measurements; rewards durable-fact language and dated-fact framing
   (`(YYYY-MM-DD): ...`); floors a short (<40-word) definitional fact with durable
   markers and little status language at 2.2, so a one-line contract statement does not
   die to the length penalty.

`access_count` is now a small log-scaled bonus inside the content-evidence stage;
recency is dropped from the score entirely and used only as the tie-break in
`SharedExtractionService`'s final sort. `CandidateFloor` moves from `1.0` (the old
0.5-point-increment scale) to **`0.4`** on the new 0–4 scale — derived from the
reference-labeled data, where the winning scorer put every `doc-index` chunk at or below
0.4 (see the eval report's sorted-candidate table).

Public contract is unchanged: `SharedExtractionService.Run`'s signature, `ShareCandidate`,
`ShareExtractResult`. `reasons` now carries the archetype tag plus whichever evidence/
refinement tags fired (e.g. `["organic-note", "rule-language", "foreign-subject"]`)
instead of the four fixed additive labels.

## Consequences

- **Positive:** the score now varies with what actually distinguishes a durable,
  portable fact from an in-flight or self-referential document — the incumbent's
  near-constant signals are gone.
- **Positive:** measured against the real labeled pool
  (`PromotionScoringRealDataTests`, local-only, `AIRACCOON_SCORING_EVAL_FIXTURE`):
  v1 (61 candidates) full-set Spearman **0.7348**; v2 (147 candidates: the 61 plus the
  86-entry organic backup slice) full-set **0.5094**, organic-only subset **0.5690** —
  all above their respective gates (0.60 / 0.45 / 0.50) and consistent with the Python
  prototype (0.749 / 0.516 / 0.574).
- **Negative:** three cooperating regex-heavy stages (archetype, evidence, organic
  refinement) replace four one-line additive checks — meaningfully more surface to
  reason about per change, mitigated by keeping each stage a separate, independently
  unit-tested pure static class.
- **Negative:** the model's priors and evidence weights were tuned against a
  61-candidate labeled set (147 with the second round) drawn from four projects; a
  fifth project with an unfamiliar docs taxonomy may classify more of its content as
  `work-note` (the fallback archetype) than a hand-tuned model would.
- **Neutral:** a re-propose of an unchanged organic row now returns the same score
  regardless of elapsed time — recency no longer feeds the score, only the tie-break
  sort. This is a deliberate product change from the pre-v2 behavior (issue #135),
  recorded here rather than silently.
- **Operational:** the hosted extraction loop (`ExtractionHostedService`) was running in
  **promote** mode globally with the old scorer and re-polluted the shared tier within
  30 minutes of the manual wipe (31 junk promotions, mostly changelog/plan doc chunks);
  `extract.mode.global` was flipped to **propose** as an interim mitigation. Plan of
  record: this scorer merges, the owner reviews a propose queue produced by it, and
  promote mode is re-enabled once that review is clean.

## Alternatives rejected

- **Reweight the incumbent's four bonuses instead of replacing the model.** Rejected —
  the eval report's diagnosis is that the incumbent's *features*, not their weights,
  carry no information on a multi-project bank (61/61 candidates match `cross-project`
  and `recent`). No reweighting of constants changes what is constant.
- **Rank-fusion of all three agents' scorers (A+B+C).** Measured in the eval at +0.684
  full-set — close to B alone but strictly worse than B + the organic-measured
  refinement (+0.754 in the original round, and the only design validated against the
  second, harder organic-only round). Three cooperating models is also a larger
  surface than one model plus one refinement stage for a marginal-at-best gain.
- **A literal "≥2 measured values floors at the organic prior" override**, as
  originally scoped from the first evaluation round. Superseded before implementation:
  the second validation round (86-entry organic backup slice) showed this simpler rule
  is not enough — a status dump's test-result counts ("174 passed", "exit 0") satisfy
  "≥2 measured values" without being a measurement. `OrganicRefinement`'s test-count
  stripping addresses this directly; the simpler override does not.

## Evidence

Local run, `dotnet test --filter FullyQualifiedName~PromotionScoringRealDataTests` with
`AIRACCOON_SCORING_EVAL_FIXTURE` set to the two labeled fixtures (never committed —
public repo, private-repo doc quotes):

```
v1 (61 candidates, docs/work/promotion-scoring-eval/labeled_all.json): full-set Spearman = 0.7348
v2 (147 candidates, 61 + 86 organic backup slice): full-set Spearman = 0.5094
v2 organic-only subset (id > 1000, 86 candidates): Spearman = 0.5690
```

Gates: v1 full-set >= 0.60, v2 full-set >= 0.45, v2 organic-subset >= 0.50 — all three
pass with margin.

**Informational, no gate.** A third-round fixture (292 candidates: the 147 above plus a
further organic backup slice, 231 total with `id > 1000`) landed after this PR opened.
Not wired into `PromotionScoringRealDataTests` as a gate — it arrived after the model
was finalized, so treating it as a gate would mean tuning against data the model has
already been validated on, the same overfitting risk the eval report's own "honest
caveat" flags for the archetype priors. `dotnet test` against it locally: full-set
Spearman **0.4512** (Python prototype: +0.456, consistent), organic-only subset (n=231)
**0.3875** — lower than the 147-candidate round's 0.5690. The organic-only subset does
not hold up as well as the smaller round did, which is worth a follow-up look at
`OrganicRefinement`'s status/durable-language lexicons against the harder cases in this
slice, but does not block this PR: the two committed gates (v1, v2) are unaffected and
both still measure against the same reference-labeled data the model was designed for.

## v3 — channel-routed prior + bounded evidence (2026-08-08)

The v2-informational gap above (292-candidate full-set 0.4512, organic-subset dropping to
0.3875) motivated a round-2 tournament: three isolated agents on the fable model, each with
~84 labeled training rows drawn from the 292-candidate set, scored against a 42-candidate
orchestrator-only holdout none of them ever saw labeled. Full scoreboard and method:
`docs/work/2026-08-08-promotion-scoring-round2.md`. Winner: **Agent C** (channel-routed
prior + bounded content evidence), alone — best on every uncontaminated comparison, no
fusion beat it on the secret holdout. This section records the C# port of that design as
v3, superseding v2's model (not this ADR's decision record, which stays as history).

**Evolution, not rewrite.** v3 keeps v2's three-stage shape and file seams —
`ProvenanceArchetypeClassifier` (channel routing), `PromotionContentEvidence` (bounded
content evidence), `OrganicRefinement` (organic-note refinement), combined by
`PromotionScorer` — because the winning design is the *same architecture family* as v2's,
just with a finer-grained channel table and more evidence rules; there was no
decomposition mismatch large enough to justify a rewrite. Where v2's structure and the
Python prototype disagreed on where a rule lived (e.g. the auto-memory-note channel needed
its own bespoke evidence, not v2's generic doc-channel evidence), the port added a seam
rather than distorting an existing one. A new `CandidateFeatures`/`CandidateFeatureExtractor`
centralizes the ~20 regex-based features the prototype's `features()` computes once per
candidate, rather than duplicating them across the three evaluator files (the "derive the
list, or delete it" concern applies as much to duplicated regexes as to duplicated lists).

**Channel table delta (14 archetypes → 19 channels).** New channels, all first-match-wins
ahead of the existing table: `turn-mirror` gets a **prose-prefix rescue** — a transcript
starting 300+ chars in no longer sinks the whole entry, only the transcript is dropped and
the prose before it is scored on its own channel (fixes a label-4 measured fact the v2
baseline sank to ~0.45 purely because a tool-call transcript was appended to it).
`.remember/` status journals and Claude auto-memory `session-*`/status/handoff dumps are
new hard-noise channels (prior 0.30, no content rescue — same treatment as `doc-index`
and `turn-mirror`). The Claude auto-memory tree splits three ways: `MEMORY.md` index rows
(0.55, small rescue if rule-language density is high), *named* auto-memory notes (2.70 —
the curated-gotcha shape the shared tier wants, second only to `organic-note`), and
`session-*`/status/handoff dumps (0.30, hard noise). A bare `/docs/` path that isn't any
more specific channel splits from `work-note` into a new `other-doc` channel. A dated
`YYYY-MM-DD-*-charter.md` under docs/work now routes to `review` (in-flight coordination)
instead of `charter` (durable project charter). All 19 priors changed from v2's values;
see `ProvenanceArchetypeClassifierTests.Prior_MatchesTheEvalReport` for the exact table.

**Evidence deltas.** `PromotionContentEvidence`: rule-language detection now excludes
first-person uncertainty (`(?<!\bI )(?<!we )\bcannot\b` — "I cannot" is not a contract);
the plan channel caps its rule-language bonus at 0.45 (vs 1.10 elsewhere) because plans
quote gates as "must" without the fact being durable; a verified-measurement-plus-rule
combination gets a +0.35 bonus; new first-person-narrative, metadata-header-block, and
imperative-checklist penalties; the recency/access-count bonus is gone entirely — the
prototype's `doc_adjust()` never referenced it, so content evidence is shape-only now
(recency remains the `SharedExtractionService` sort tie-break, unchanged from v2).
`OrganicRefinement`: the short-definitional floor moves 2.2 → 2.4 and its clamp range
-2.8..1.5 → -2.2..1.6; it picks up the doc-channel's pointer/table/metadata-header/
imperative-checklist/superseded pushes plus new contents-index, directory-readme,
link-heavy, and docname-heavy penalties and a foreign-subject bonus — the prototype's
`organic_adjust()` shares most of `doc_adjust()`'s vocabulary, just with different weights.
Status-opener detection is checked after stripping leading markdown decoration (`*#>-–`)
from the head, and now also recognizes a generic "`<X> complete/done/closed/finished/
delivered`" opener shape instead of only the earlier enumerated literal openers.

**CandidateFloor: kept at 0.4, re-examined not changed.** All four new hard-noise channel
priors (`remember-log`/`auto-memory-session` 0.30, `turn-mirror` 0.35, `doc-index` 0.35)
sit below 0.4 with no content rescue, so the floor still cleanly excludes them exactly as
it did in v2 (where the floor was derived from `doc-index` sitting at or below 0.4). The
weakest real channel, `plan` (0.70), can still be pushed below 0.4 by heavy ephemera —
matching v2's behavior. `auto-memory-index` (0.55, up to 0.70 with the rule-density lift)
intentionally clears the floor — the round-2 scoreboard's own channel table calls it "low",
not excluded, since an index row pointing at a genuinely durable note is still weak
signal, not noise. No re-derivation was needed; 0.4 continues to sit in the gap between
the hard-noise ceiling (0.35) and the weakest real channel (0.70).

**Measured numbers** (`PromotionScoringRealDataTests`, local-only,
`AIRACCOON_SCORING_EVAL_FIXTURE`; python prototype run via
`docs/work/promotion-scoring-eval/round2/agentC/scorer.py` through
`promotion-scoring-eval/eval.py`):

| Fixture | C# port | Python prototype | Gate |
|---|---|---|---|
| v1 full (61) | 0.705 | 0.735 | >= 0.60 |
| v2 full (147) | 0.684 | 0.696 | >= 0.45 |
| v2 organic-subset (id>1000, 86) | 0.691 | 0.697 | >= 0.50 |
| v3 full (292) | 0.660 | 0.665 | >= 0.60 AND within ±0.03 of prototype |
| secret holdout (42, orchestrator-only) | 0.688 | 0.690 | within ±0.03 of prototype |

All five gates pass; v3-full and secret-holdout parity with the prototype are within
0.005 and 0.002 respectively, well inside the ±0.03 tolerance. The v2 gates (full-set
>= 0.45, organic-subset >= 0.50) and the v1 gate (>= 0.60) — none of which the port was
required to hold, since v3 changed the underlying model — still pass with margin, so v2's
shipped behavior did not regress on its own reference data.
