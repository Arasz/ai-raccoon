# P1 determinism finding — vector-leg tie truncation (2026-09-10)

Status: RESOLVED by C1 (see Resolution below); the golden is frozen.
Owner: continuation plan (P1 tail / P2 rigour).

## Symptom (measured)

Two full-100 eval runs on the frozen store (`results-f1.json`,
`results-f1-run2.json`, same store, same corpus, same params):

- Aggregates identical: hit-rate 0.6566/0.7879, mean-F1 0.1537598204264871 /
  0.1829405162738496, MCC 0.6133632769186855, contingency equal, staleAnchors
  equal — all EXACT.
- 2 of 99 queries (C045, C066) serve a DIFFERENT hash list: same size (8),
  one hash swapped into/out of the list, expected anchor present in both.
- `sessionId` is the only other top-level difference (expected — allow-listed).

## Isolation (fresh-process probes, /tmp/p1-red/)

1. Query embedding bit-identical across processes (`qvec_sha` equal) — torch
   path deterministic. NOT the cause.
2. Chroma **structure** collection: identical across processes. NOT the cause.
3. Chroma **content** collection query, same qvec, same `where`, k=100:
   - same-process repeat: identical ids AND distances (stable);
   - across processes: different id SET at the boundary — `content_last3`
     run1 `['17e646f7c2f1','5635662dc365','4bcab857449b']` vs run2
     `['5635662dc365','e604f35385a5','4bcab857449b']`; `content_sha` differs.
   - boundary: 8 items share the k=100 cut distance exactly
     (d = 5.960464477539063e-08 = 2^-24); Chroma returns an arbitrary subset of
     that tie group per process.
4. Expansion test (C066, two fresh processes): k=100 unstable; **k=200 gives an
   identical sorted top-100 across processes** (`top100_sha` equal; tie group at
   the cut = 17 items, fully covered by k=200).

Conclusion: Chroma's ANN top-k truncates an exact-distance tie group at the
requested k and the surviving subset is process-dependent. All downstream
stages (dedupe_by_content, structure_rank, fuse_rrf, merge_results) are
deterministic functions of the leg's returned list, so the variance enters and
propagates from here.

## Fix direction (to be scoped by the continuation plan)

Tie-complete window expansion in `retrieve.FusionRetriever.vector_leg` for both
collections: request `window`, and while the (window+1)-th distance equals the
window-th distance (i.e. the cut lands inside a tie group), grow the request
(bounded by collection count) until the tie group is fully covered; then sort by
(distance, hash) and cut at `window`. Preserves the ANN head semantics — only
resolves boundary ties deterministically — and removes the process dependency.
Alternative if expansion ever fails to stabilize (large tie groups): exact
in-process cosine over the filtered scope (`get(include=['embeddings'])`), with
its ANN-vs-exact semantic change called out explicitly.

Acceptance: two fresh-process full-100 runs byte-identical on per-query hashes
(under the plan's tolerance), plus a unit test with a fake collection whose tie
subset varies with k.

## Side defect found while probing

`/tmp/memwatch.py` (to be committed as the plan's memory guard) prints only
`out[-6000:]` of the child's stdout, silently clipping output. The eval artifacts
were unaffected (written to files), but probe/CLI stdout was lost. The committed
version must stream or pass through the child's stdout in full (or cap only its
own log lines, never the child's output).

## Resolution (2026-09-10, P1 close-out)

- **C1 implemented**: `retrieve._query_tie_complete` grows the Chroma request
  while the (window+1)-th distance equals the window-th (bounded by the
  collection count); `_top_window_similarity` sorts by `(distance, hash)` and
  cuts at `window`, for both content and structure. Unit gates (fake
  collection whose tie subset varies with k) were red pre-fix; the harness
  leg is now byte-identical across six fresh-process/fresh-copy runs, and the
  frozen golden's run-1-vs-run-2 diff is empty modulo `sessionId`.
- **Bank-leg addendum**: the production leg is only repeatable with a
  quiesced scratch copy. A scratch copied straight from the bank re-ingests
  live files via its inherited watches mid-eval (row counts 56,457 → 56,481 /
  53,637; bank metrics swing 0.798 → 0.869 hit-rate). The golden uses a
  scratch base with `watch.enabled.*` / `sweep.enabled.global` /
  `extract.enabled.global` set false. Details and evidence: lane report,
  "Close-out deviations".
- **memwatch fixed**: committed `scripts/retrieval_tuning/memwatch.py`
  streams child stdout in full (the probe clipping is pinned by a test).
