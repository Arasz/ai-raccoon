# Follow-up definition — retrieval-harness parity (successor to `air-full-hundred-query-parity-eval`, PR #633)

Status: **proposed, not started** · Provenance: deferred items + filed findings + measured
unknowns from the closed task (`docs/work/2026-09-10-p4-lane-report.md`, review d-761,
C13/C14, P3 amendments) · Frozen base: PR #633 merge `c4b37a15` (golden harness
0.6768/0.1582 vs bank 0.8081/0.1874, MCC 0.5955, cont 65/2/15/17, stale `[C035]`).

Each package is independently startable; the defaults below let the task close without an
owner ruling.

## Package A — Embedding seam resolution (C13)

**Problem.** The harness (HF safetensors, CLS — verified at cos 1.00000 against the model
card) and the bank (its own ONNX export) embed the same text differently: ONNX-vs-HF
cos **0.5934**; bank self-consistency 0.9826. The harness's vector leg holds the anchor in
only **8/99** rows (78/99 are `fts=1, vec=0`; 14/15 c-cell rows likewise). The headline gap
is therefore confounded by a runtime/model-conversion seam — measured and disclosed, not
resolved.

**Options.**
- **A1 (control, in-repo).** Give the harness the bank's embedding runtime — either the
  bank's exact ONNX file or the bank's stored vectors — and re-run the pair. Isolates
  fusion from embedding; publishes the fusion-only gap. Scope: an embedding-provider seam
  in the harness + one control artifact. Estimated ~1–2 days.
- **A2 (upstream, bank repo).** Pin the bank manifest's `source.revision` (currently
  unpinned `'main'`) and re-export; re-measure ONNX-vs-HF. Scope: bank-side change.
- **A3 (accept).** Keep the seam documented (current state, zero cost).

**Decision required.** A1 vs A2 vs A3. **Default: A3**, with A1/A2 filed here.
**AC (A1).** A control-run artifact showing leg ranks and the fused metrics with the bank
embedder; a one-page delta vs the frozen golden; no change to the golden itself.

## Package B — Corpus quality (composition)

**Problem.** 23/99 queries are markup/JSON debris (e.g. `How is { "extends handled?`),
because the generator takes the first clause of a chunk value. Relevance-flavoured readings
are composition-sensitive: clean n=76 → harness 0.697 / bank 0.842; debris n=23 → 0.609 /
0.696. The parity (pipeline) reading stands; the relevance reading does not yet have a
clean instrument.

**Scope.** Improve the corpus generator's topic derivation (or add a paraphrase/quality
filter), regenerate the corpus **with a snapshot pin**, re-run the pair on the clean subset,
publish stratified + clean-subset numbers and the delta. **AC.** A documented generator
change; a regenerated corpus whose debris share is materially reduced (target ≤5/99 or an
explicit floor); a re-run artifact; no silent re-baseline of the frozen golden (a new
golden version is explicitly named if the corpus changes).

## Package C — `eval-set-100.json` provenance (P3 amendment #1)

**Problem.** It cannot be reproduced from the pinned copy (predates it, no `snapshotSha256`),
so the refresh script exits 3 and the corpus tests fail on stale anchors. **Scope.**
Regenerate it from a pinned copy and commit with a provenance header — or retire it
explicitly (delete + document). **AC.** `refresh-retrieval-corpora.py` exits 0 for every
committed corpus (or the retirement is recorded); the repo's corpus tests pass without the
local-copy workaround. Small (~hours).

## Package D — Taxonomy depth (per-row leg ranks)

**Problem.** The measured within-fusion split (11 rows drop at Take(8), 4 at the relative
floor) is not attributable per row because only window-level `fts_hit`/`vector_hit` booleans
are persisted. **Scope.** Persist per-row leg ranks/scores in `results.json` (additive
schema), extend the taxonomy labels (`fusion_take` / `fusion_floor`), re-render the report.
**AC.** Additive schema change + tests; a re-labelled artifact whose summary block is
unchanged (goldens stay the numeric reference). Small–medium.

## Package E — Repo/test hygiene (the measured failures)

- **E1.** `test_retrieval_tuning_eval_corpus.py` hardcodes a default copy path and asserts
  its existence → `pytest.skip` when the env var/copy is absent (matches the repo's
  env-gating convention); the stale-anchor tests become meaningful again after C.
- **E2.** `test_dependencies_declared` + the two optuna report tests fail on a clean
  checkout (optuna/matplotlib undeclared/absent) → declare them or gate those tests behind
  an extras marker.

**AC.** Full `scripts/tests` on a clean checkout reports no failures (skips only, with
reasons); the CI lane mirrors it. Small.

## Package F — Upstream notes to the bank repo

- **F1.** `MemorySql.cs:113` FTS SQL has no hash tiebreak (the harness port added one) —
  latent order instability on exact bm25 ties.
- **F2.** ONNX default threading (`embedding.threads` unset → 5 on this box) makes
  responses order-unstable at near-ties; `embedding.threads=1` is the documented
  belt-and-braces.

**AC.** Filed with the bank repo (issues or PR) or explicitly accepted with a record. Small,
external.

## Package G — Full-volume fresh-bank E2E — **DROPPED (owner decision, 2026-09-10)**

The literal P4 AC3 at real volume (fresh copy → refresh → full ingest ~13 h → evaluate →
report) is **dropped by the owner**: no 13-hour test runs. The fixture-scoped E2E plus the
P1 full ingest at real volume (already proven, exit 0) cover the wiring and the volume path
respectively. Recorded here so the decision is not silently revisited.

## Sequencing suggestion

`C + E` (unblock clean CI) → `B` (the instrument) → `A1` (the confounder) → `D` → `F`
(external, parallel). `G` is dropped. Execute as per-package tasks in this order; each
package's AC is the task's pass condition.
