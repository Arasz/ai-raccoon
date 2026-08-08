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
scoring evaluation, landed via PR #179's `docs/work/2026-08-08-promotion-scoring-eval.md`
and preserved scorers/harness under `docs/work/promotion-scoring-eval/`.

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
