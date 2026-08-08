# Agent C (freestyle) — promotion-usefulness scorer

## Approach

Channel-routed prior + bounded content evidence, single stdlib-only file, no
per-id or per-string memorization.

1. **Provenance channel.** Every candidate is routed by its most informative
   path (`source_file`, else `path` with the `shared/` promotion prefix
   stripped) to one of 19 channels, each carrying a prior on the 0-4 scale.
   Beyond the baseline's docs-tree archetypes (adr / plan / review / index /
   changelog / …), this scorer adds channels the fresh deep-sweep slice
   introduced:
   - `.remember/` status journals → hard noise (0.30, no content rescue);
   - Claude auto-memory tree (`~/.claude/projects/*/memory/`), split three
     ways: `MEMORY.md` index rows (0.55), `session-*`/status/handoff dumps
     (0.30), and *named* durable notes (2.70 — these are exactly the
     curated-gotcha shape the shared tier wants);
   - dated `YYYY-MM-DD-*-charter.md` under docs/work → review coordination,
     not a durable project charter.
   Turn-mirrors are detected by embedded tool-call tags, but only when the
   transcript starts near the top: an entry whose first ~300+ chars are prose
   is scored on the prose prefix (fixes a label-4 measured fact the baseline
   sank to 0.45 because a transcript was appended to it).

2. **Content evidence (doc channels).** Durable-rule language (never/always/
   must/contract/by design/regression/root cause…, with `I cannot`-style
   first-person uncertainty excluded), measurement words paired with numeric
   units, cross-project mentions, and a `verified + rule` combination push up;
   pointer shapes (link/doc-name density, version tables, `## Contents`),
   coordination ephemera (AC:/Gate:/worktree/PR #), metadata-header blocks
   (`**Task:** … **Worktree:** …`), imperative next-step checklists,
   first-person investigation narrative, frontmatter-only chunks, and
   mid-sentence chunk starts push down. Plan-channel rule lift is capped low:
   plans quote gates as "must" without the fact being durable.

3. **Organic refinement (source_file null).** The dominant organic failure
   mode is a conversation status recap. Openers ("Done", "Rollout complete",
   generic "<X> complete/closed/delivered", checked after stripping leading
   markdown decoration), status vocabulary, second person, and multiple commit
   hashes push down; durable phrasing (`[facts]`, REGRESSION, gotcha,
   contract-semantics "means … not …"), dated/verified facts, and real
   measurements push up; doc-pointer/table/README shapes are penalized here
   too (promoted doc extracts masquerading as notes). A short definitional
   fact keeps a floor of 2.4 so one-breath durable rules survive.

4. **Floors/caps.** <8 words → capped at 0.5 regardless of channel; noise
   channels cannot be bought back by content.

## Self-reported results (train = 84 labeled candidates)

- Spearman overall: **+0.800** (bootstrap 5–95%: 0.72–0.86)
- Per slice: doc-queue +0.808, organic +0.865, fresh-sweep +0.775
- Reimplemented baseline (base_scorer + refine) on the same 84: **+0.371**
  (doc-queue 0.728, organic 0.469, fresh-sweep 0.321)

## Overfit checks

- **Perturbation robustness:** jittering *every* float constant in the file
  by ±15% (30 random trials) keeps train rho in 0.759–0.826 (median 0.799) —
  performance is not a knife-edge on tuned constants.
- **Slice balance:** every change was accepted only if no slice degraded
  materially; final per-slice rhos are within 0.09 of each other, so the
  score is not carried by one sub-population.
- **Bootstrap CI** (2000 resamples): 0.724–0.857.
- **Edge cases:** empty value, missing path/project, unicode, 100 KB value
  all score without error; two consecutive full-pool runs are byte-identical.
- No id tables, no memorized per-candidate strings; all rules are shape- or
  provenance-level and were sanity-checked against the unlabeled pool's
  channel distributions.
- The parent eval directory contains round-1 labeled files covering the full
  reference set (`labeled_v3.json`). **Deliberately not read or used** —
  neither for tuning nor for a final self-evaluation — since it is the
  held-out evaluation data.

## Contract verification

`python3 scorer.py candidates_all_unlabeled.json` → exit 0, valid JSON array,
292 entries, unique ids, float scores in [0, 4]. Deterministic (verified by
diffing two runs).
