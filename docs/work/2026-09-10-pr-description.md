# PR: retrieval-harness — full-100 parity eval + measurement rigor + one-harness consolidation

Branch: `task/air-full-hundred-query-parity-eval` → `main`. Task:
`air-full-hundred-query-parity-eval`.

## What this delivers

1. **Full-100 harness-vs-bank parity eval on `project-corpus-100.json`** (the
   merged LlamaIndex+Chroma harness from #625 vs the live ai-raccoon bank, both
   legs on the same frozen bank copy). Extension from the 2-bucket rule to every
   corpus-targeted project + the global shared tier; a three-legged
   `custom→project` scope fix (harness FTS predicate, Chroma `where`, bank-leg
   mapping — verified against the bank's own read predicate); null/stale anchor
   accounting; provenance (corpus snapshot, model revision + bytes, bank-copy
   path + SHA); a deterministic vector leg (tie-complete k expansion).
2. **Measurement rigor (P2)**: `--repeats N` from a fresh quiesced scratch base
   per repeat with per-query served-set stability, a window-level classified gap
   taxonomy, anchor verdicts, and a committed memory guard (`memwatch.py`).
3. **One-harness consolidation (P3)**: one scope/bucket builder in
   `scripts/src/retrieval_tuning/scopes.py` (the harness module is an
   identity-gated shim), behavior constants in `data/*.json` with a derived
   no-hardcode gate, a refresh-corpora script with an exit-code contract
   (0/1/2/3), an old-vs-new CLI parity harness, and an in-tree golden diff tool.

## Frozen numbers (clean rig, runs C/D byte-identical modulo `sessionId`)

| | harness | bank |
|---|---|---|
| hit-rate | 0.6768 | 0.8081 |
| mean F1 | 0.1582 | 0.1874 |

MCC 0.5955 · contingency a=65 b=2 c=15 d=17 · n=99 (100 = 99 scored + 1
null-anchor filtered, `staleAnchors=[C035]`) · gap taxonomy: c-cell 15/15
`fusion` (embedding 0, unrecoverable 0, unknown 0.0%).

## Findings worth the reviewer's attention

- **The bank leg requires a quiesced scratch copy.** A scratch copied straight
  from the bank inherits its watches and re-ingests live files mid-eval
  (rows 56,457→53,637; bank hit-rate swung 0.798→0.869 with no code change).
  `make_quiesced_scratch.py` builds the verified base; the frozen golden was
  measured under it. Historical numbers from a drifted scratch (0.788/0.183) are
  superseded.
- **The embedding seam is ONNX-vs-HF, not pooling.** The harness pools CLS
  (llama-index's default; verified cos 1.00000 against the model-card recipe);
  the bank runs its own ONNX export (bank self-consistency 0.9826; ONNX-vs-HF
  0.5934 on the same text). Disclosed in the report with numbers (harness
  `vector_hit=1` on 8/99 rows; 14/15 c-cell rows are `fts=1, vec=0`). An earlier
  pooling explanation was retracted (C14); a whole-tree grep confirms no
  remaining carriers.
- **The c-cell deficit is fusion-side, not embedding-side.** The taxonomy shows
  all 15 bank-only hits drop after the legs (6 of them only visible at window
  rank 9–100; 11 drop at Take(8), 4 at the relative floor). Query composition is
  disclosed (23/99 markup-debris stratum) so relevance-flavoured readings stay
  honest.

## Testing

- Harness + retrieval-tuning suites green on the merged tree (incl. the new
  scope/no-hardcode/refresh/parity/golden-diff gates).
- Frozen-contract collector 13/13; post-refactor eval runs diff CLEAN vs both
  goldens under the stated tolerance.
- Full `scripts/tests`: the **same pre-existing failures as `main`** —
  `optuna`/`matplotlib` dependency tests and the stale `eval-set-100.json`
  fixture (its provenance amendment is documented in the refresh exit-3 report).
  None are caused by this branch.

## Docs

`docs/work/`: P1/round-2/P2/P3/P4 lane reports, the C10 trace + the determinism
finding, the merge map, the regenerated eval report, the frozen goldens +
repeats artifact, and the refresh/RUNBOOK recipes.
