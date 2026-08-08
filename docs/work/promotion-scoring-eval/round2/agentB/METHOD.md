# Agent B — Speech-act evidence-grammar scorer

## Design

The baseline asks "which archetype bucket is this entry?" and hand-sets a prior per
bucket. This scorer asks a different question: **what kind of speech is this text?**
A durable cross-project fact is *written differently* from a status dump, regardless
of where it lives — it states contracts in the present tense with modals ("any job
using dotnet on ubuntu-slim **must** call setup-dotnet"), explains mechanism
("**because** the runner has no preinstalled dotnet"), names failure modes
("builds **fail** with exit 127, **silently**"), and cites evidence
(`file.cs:123`, "measured", "verified"). A status dump narrates in the past tense
("merged", "pushed", "dispatched"), references PRs, lists progress bullets, and
talks about the future ("pending", "next steps"). Plan minutiae is scaffolding
lines (`AC:`, `Gate:`, `Scope`, `Wave 2`), and review headers are metadata rows
(`Reviewer:`, `Base → HEAD`).

19 generic signals, four groups:

- **Durability grammar (+):** modal-contract density, causal-connective density,
  failure/gotcha vocabulary, measurement vocabulary, `file:line` citation density,
  code-fence density.
- **Narration grammar (−):** past-tense verb ratio (past / (past + present-copula)),
  status-verb density, future/backlog vocabulary, PR-reference density,
  plan-scaffold line fraction, metadata-header fraction.
- **Structure shape (−):** bullet/pipe line fraction, table-row fraction.
- **Provenance:** organic write citing a real source path (+) — an organic entry is
  not re-findable by doc search, and one that carries provenance is the canonical
  "durable fact" shape; ADR path (+); session-log path under `/memory/` or
  `/.remember/` (−); README path (−).

Score = Σ signᵢ · zᵢ, where zᵢ standardizes feature i against statistics of the full
292-candidate corpus (unsupervised — no labels involved). **No fitted weights at
all**: signs are domain-assigned, weights are equal. The only supervised decisions
were feature-set membership (a handful of add/drop choices checked by split-half CV).
This is the key anti-overfit move: with 83 labels, fitted coefficients are noise —
ridge regression on the same features scored 0.57 CV, the unfitted sign-sum 0.67.

Why it should beat the baseline where it is weakest: within-organic discrimination
(durable fact vs status dump, both `source_file=null`) is exactly a speech-act
distinction, not an archetype distinction — and within-doc-chunk discrimination
(ADR body vs plan minutiae) falls out of contract-grammar vs scaffold-grammar
densities on the same path prior.

No id lookups, no candidate-specific strings — every regex is generic vocabulary.
Deterministic, Python 3 stdlib only, no network.

## Results (self-reported)

- **Train Spearman (83 labeled): +0.684**
- Per-slice train: doc chunks +0.82, previously-promoted organic +0.56, fresh
  deep-sweep +0.69 — positive on the two "hard" slices the baseline tops out on.
- **Overfit check — repeated split-half validation** (train on a random half,
  score the other half, 100 reps × 2 halves × 3 seeds, standardization stats from
  the training half only): **+0.672 / +0.673 / +0.670** per seed. Train-vs-CV gap
  ≈ 0.01, consistent with a model that has no fitted weights to overfit with.
  (Honest caveat: the ~5 feature add/drop decisions were made against this CV, so
  a small selection optimism remains; a fully nested estimate of a greedy-selection
  variant scored +0.30, which is why greedy selection was rejected in favor of a
  domain-fixed set.)
- Baseline reference: +0.456 full set / +0.442 fresh.

## Files

- `scorer.py` — self-contained scorer (`python3 scorer.py <candidates.json>`).
- `features.py`, `fit.py` — development harness (feature prototypes, ridge/CV code).
- `model.json` — discarded ridge experiment output, kept for the audit trail.
