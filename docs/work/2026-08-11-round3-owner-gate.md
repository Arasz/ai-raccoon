# Research: round-3 promotion scorer shipping and owner gate

**Date:** 2026-08-11
**Question:** Is round-3 lane A already shipped, and should the owner retain the full lane-A scorer or approve only a portability/recentred subset after separating queue hygiene from scorer quality?

## Findings

### F1 — Round-3 lane A is already in the current release and live process [MEASURED]

PR #249 merged commit `c1535d507a93dc2bf9f91b5b18c3f54f87642605`. The commit is an ancestor of the task worktree's `main` and of the 1.6.4 release commit. The installed CLI reports `1.6.4+c72a1ded…`; the live `/observability` endpoint reports the same release; and the live bank contains 1,000 queue rows, all with `scorer_version = 2`.

This supersedes the diagnostic's original statement that lane A "never shipped" (since corrected in the diagnostic's scorer-pedigree and findings sections). The stale wording was an older observation, not evidence that a second port is needed.

**Evidence:** In the task worktree, `git merge-base --is-ancestor c1535d50 c72a1ded` and `git merge-base --is-ancestor c72a1ded HEAD` both returned success; `gh pr view 249 --json ...` returned `state: MERGED` and merge commit `c1535d50`; `/Users/arasz/.dotnet/tools/ai-raccoon --version` returned `1.6.4+c72a1ded…`; `curl http://127.0.0.1:7721/observability` returned the same version; `sqlite3 ~/.ai-raccoon/memory.db "select scorer_version,count(*) from promotion_queue group by scorer_version"` returned `2|1000`. The diagnostic's original stale wording was corrected as part of this gate (see diagnostic scorer-pedigree section and finding 3).

### F2 — Lane A remains the owner-guard winner, not the absolute holdout winner [READ]

The round-3 source record selects lane A because it removed the ADR bias and was the only candidate to beat the incumbent on the owner's rows. Lane C has a slightly higher holdout correlation, but its owner-57 score is materially lower. The selection therefore targets the owner's usefulness judgment rather than jury holdout optimization alone.

**Evidence:** `docs/work/2026-08-09-promotion-scoring-round3.md:35-49` records baseline holdout `+0.602`, owner-59 `+0.710`, ADR bias `+1.35`; lane A holdout `+0.683`, owner-57 `+0.720`, ADR bias `+0.03`; and lane C holdout `+0.700`, owner-57 `+0.538`.

### F3 — The shipped C# implementation contains lane A's recentred evidence, refitted priors, and restricted portability [READ]

The port preserves the classifier → content evidence → organic refinement shape. Its document evidence uses a symmetric `[-1.60, +1.60]` clamp, a zero-centred rule term, the substance and durability terms, and portability only for the six considered-document channels. The scorer version is 2 and stale queue rows are deleted/re-admitted rather than rescored in place.

**Evidence:** `docs/adr/0018-promotion-scoring-v2.md:256-325`; `/Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/re-evaluate-round-3/src/AiRaccoon.Core/Memory/PromotionContentEvidence.cs:13-18,25-45,71-100,176,247-253`; `src/AiRaccoon.Core/Memory/PromotionScorer.cs:6-15,87-96`; `src/AiRaccoon.Core/Memory/SharedExtractionRunner.cs:29-31`.

### F4 — Current build and scorer-focused regression tests are green, but the private real-data fixture is not present [MEASURED]

After restoring the task worktree, the configured build completed with 0 warnings and 0 errors. The scorer-focused filter completed with 58 passed, 0 failed, and 1 skipped. The skipped test is the local-only hand-labeled fixture test; `AIRACCOON_SCORING_EVAL_FIXTURE` was not set, so the current run does not independently reproduce the 406-row round-3 metrics.

**Evidence:** `dotnet restore --nologo`; `dotnet build --no-restore --nologo -v:q` → `Build succeeded`, `0 Warning(s)`, `0 Error(s)`; `dotnet test tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj --no-restore --filter 'FullyQualifiedName~AiRaccoon.Tests.Unit.Memory.Promotion' --logger 'console;verbosity=minimal'` → `Passed! Failed: 0, Passed: 58, Skipped: 1, Total: 59`; test declaration and skip behavior are in `tests/AiRaccoon.Tests/Unit/Memory/PromotionScoringRealDataTests.cs:10-37`.

### F5 — The live queue is contaminated by already-shared and noise-shaped rows, but that does not isolate a scorer defect [MEASURED]

The live bank is in propose mode with 1,000 version-2 rows. A live top-50 audit found 19 values already present in the shared tier and 19 rows carrying noise tags. A direct current-bank query also found the documented score bands: 8 rows at or above 3.5, 79 from 3.0 to 3.5, 477 from 2.5 to 3.0, 368 from 2.0 to 2.5, and 68 below 2.0.

This is evidence for queue hygiene work, not a clean A/B scorer comparison: duplicate shared values and previously rejected content can make any scorer's queue look poor. Queue hygiene has since been implemented (PR #258) and must be re-measured on a clean queue before using it as a scorer-quality gate.

**Evidence:** The read-only live query in the task worktree returned `scorer_version 2 / n 1000 / min 0.4648 / max 4.0`, the score-band counts above, and `19|50` for the top-50 shared-value audit. The canonical audit records the same contamination at `docs/work/2026-08-11-ai-raccoon-diagnostic.md:156-165` and identifies the separate queue-hygiene recommendation at `:205-208`. Queue hygiene was implemented in PR #258 and documented at `docs/work/2026-08-11-mem-imp-1-queue-hygiene.md`.

### F6 — The measurement-channel disagreement remains an owner decision, not a safe n=2 retuning target [READ]

Lane A predicts `0.08` and `0.37` for two owner-labeled measurement rows whose labels are both 2. The nine jury-labeled measurement rows have mean 0.22. Removing the two owner rows changes lane A's owner-57 result from `+0.637` to `+0.720`. The round-3 record deliberately did not retune this channel on two rows because doing so would be the overfitting the round was designed to avoid.

**Evidence:** `docs/work/2026-08-09-promotion-scoring-round3.md:70-88`; `docs/adr/0018-promotion-scoring-v2.md:316-319`; the thin-channel and generalisation risks are also recorded in `docs/work/promotion-scoring-eval/round3/agentA/METHOD.md:227-253`.

### F7 — The owner gate should decide retention and sequencing, not silently authorize a new model fit [INFERRED]

F1–F6 support treating lane A as the current baseline and separating two decisions: retain the shipped model (including the six-channel portability scope), and defer any measurement-prior correction until the owner supplies a target or enough additional labels. The current queue supports cleaning and then remeasuring; it does not prove that portability or recentring alone caused every live ranking problem.

**Reasoning from:** the source-selected owner guard in F2, the implementation identity in F3, the missing private fixture in F4, the queue confounding in F5, and the n=2 warning in F6.

### F8 — The diagnostic contained a stale scorer-shipping claim (corrected) [INFERRED]

~~The diagnostic's scorer pedigree paragraph said that lane A never shipped.~~ This was corrected as part of this gate: the diagnostic now states that lane A is scorer v2 in release 1.6.4 (PR #249, commit c1535d50). The correction is recorded in the diagnostic's scorer-pedigree section and finding 3.

**Reasoning from:** F1 and the original diagnostic wording (before correction); the diagnostic was updated to match the measured shipping evidence.

### F9 — The owner gate retains lane A and defers measurement retuning [READ]

The owner approved treating lane A as the shipped scorer v2, retaining its recentred evidence,
refitted priors, and six-channel portability scope. The owner deferred retuning the measurement
prior from the two owner-labeled rows and approved a post-queue-hygiene scorer audit instead of
using the contaminated current queue as an isolated model gate.

**Evidence:** `docs/work/2026-08-11-round3-owner-gate-feedback.md:5-51`.

### F10 — The owner decision is determinate for retention, but the gate is not yet a reusable change gate [INFERRED]

The independent architecture review found the cards conceptually sound but underspecified for a
future full-A-versus-subset change: the approved path does not need to choose a replacement, so the
missing component-set and non-inferiority fields do not invalidate this retention decision. The
independent code review confirmed the shipped implementation, while identifying acceptance debt:
the private fixture can declare no quality gate, several synthetic tests derive their thresholds
from the scorer under test, and the six-channel `Portability` term should be distinguished from
the separate organic-note technology-breadth refinement.

**Reasoning from:** the owner feedback's retention/defer path in F9; the form's D1-D4 decision
cards; `tests/AiRaccoon.Tests/Unit/Memory/PromotionScoringRealDataTests.cs` fixture validation;
`src/AiRaccoon.Core/Memory/PromotionContentEvidence.cs` document-family restriction; and the
read-only architecture/code reviews completed for this gate.

This is follow-up gate debt, not a reason to rewrite scorer v2: no replacement or measurement-prior
change was approved. Any future scorer change must name the exact retained components, use a frozen
evaluation set, apply a predeclared superiority/non-inferiority threshold, and report ADR bias.

## Still open

- The measurement-prior calibration remains deferred; settle it only with more owner-labeled
  examples or an explicit owner target and a gate that is not fitted to the same two rows.
- The post-queue-hygiene scorer audit has not run yet; it should compare a clean candidate pool
  without already-shared or previously discarded rows.
- The private hand-labeled fixture is not available in this public worktree, so the round-3 correlations remain read from the committed research record rather than independently re-run here.
- The reusable change gate still needs a non-vacuous fixture-manifest check and independent quality
  thresholds; the current synthetic gate tests validate harness plumbing, not scorer quality.
- Wording should keep the six-channel `Portability` formula distinct from the organic-note
  technology-breadth refinement when the next scorer decision is documented.

<!-- Owner gate form: docs/work/2026-08-11-round3-owner-gate-review.html -->
<!-- Owner feedback: docs/work/2026-08-11-round3-owner-gate-feedback.md -->

<!--
Render with:
  python3 /Users/arasz/.hermes/skills/ai-badger/evidence-first-research/scripts/render_report.py docs/work/2026-08-11-round3-owner-gate.md
-->
