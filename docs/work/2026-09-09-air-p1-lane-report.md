# Lane report: P1 bucket extension + full-100 eval + close-out

Task: air-full-hundred-query-parity-eval, package P1.
Branch: `task/air-full-hundred-query-parity-eval-lane-impl-p1` (C1–C7);
close-out round 2 (C9–C11) on `task/air-full-hundred-query-parity-eval-lane-close2`
from merged P1 close-out `44546818`. Base: `a0bab95c`.
Status: P1 close-out items C1–C7 done; golden frozen under the measured
quiesced-scratch precondition (see "Close-out deviations"); C8 report attached.
Round 2: C9 + C10 + C11 done 2026-09-10 — C10 verdict is per-spec (drop at
Take(8); embedding-seam leg inputs), so the golden is unchanged (no re-freeze).

## What changed (commits)

- `38cb3908` test: P1 TDD RED — bucket/scope/gap/report gates (all fail pre-fix)
- `1b6ce543` feat: P1 bucket extension + custom-scope 3-leg map + null-anchor filter + gaps + provenance
- `eb3ae062` docs: ingest rule prose no longer names the frozen 2-bucket pair
- `fecdbdd6` feat: multi-home dedupe (first-by-id + audit) + params provenance keys
- `3ab72822` fix: subset disclaimer accounts null-filtered stale rows; stale prose distinguishes scored-vs-filtered
- `b1e1efcd` fix: model_weights_info uses top-level os; hermetic HF_HOME test
- `5e60634e` refactor: slice resolves buckets via scopes.resolve_buckets (legacy default only for bare lists)
- `d85c6704` **C1** fix: deterministic vector leg — tie-complete k expansion at the window cut (F1)
- `72814462` **C2** fix: exclusion manifest committed-vs-shared split + resolved-bucket report prose (F2/F3)
- `b4797c29` **C3** feat: eval-side weights/copy provenance gates (F4)
- `d6792572` **C4** fix: AC evidence collector — model pin compares to `ingest.PINNED_MODEL_REVISION` (F5)
- `05acb20c` **C5** fix: memwatch with full stdout passthrough + tree kill; slice blank `--buckets` exits 2 (F11)
- `c88ce57b` **C5** fix: memwatch closes its status log after the final result line
- `2aee89d4` **C6** feat: ingest `--refresh-params` metadata-only fast path
- (this commit) **C7** docs: golden `results.json` + run-2 + eval report + refreshed lane report

Close-out round 2 (branch `…-lane-close2`, base `44546818`):

- `1f9fdd1e` **C10** docs: shared-scope fusion trace — drop is Take(8), per-spec
- `63720868` **C9+C10** docs/report: composition strata, stale invariant, fusion-drop marking
- `568157da` **C11** feat: tested quiesced-scratch builder + row-stability snapshot

## Finding verification (reproduced before fixing; corrections marked)

Measured on the corpus + live bank before any fix (RED pastes: `/tmp/p1-closeout/`,
originals in `/tmp/p1-red/`):

1. 2-bucket rule: 74/100 queries target projects outside {ai-raccoon,
   hermes-default}; of those 63 are non-custom (silent-empty under the frozen
   rule) and 11 are custom. 3 more custom queries sit inside the 2 buckets
   (C067/C069/C071, hermes-default) but still crash. Total needing the fix:
   63 silent-empty + 14 crash + C034 null = 77; the plan's "76 (62+14)"
   counts C034 separately (62 = 63 minus C034, which is ai-sheepdog/project
   but null-anchored). `load_rows` on a 3-project fixture served only the
   2-bucket hashes (RED-1).
   **Corrected breakdown (F7)** — copy = 56,457 rows; pre-dedupe ingest set =
   56,322; delta 135 = **106 raw-spelled committed rows** (`job-search-ai-assistant`
   77 custom + 29 project; **`aib` has zero committed rows**) **+ 29 NULL-scope
   rows** (26 `ai-raccoon` + 2 `job-search-ai-assistant` + 1 `jsaa`); the ingest
   rule's `OR scope='shared'` keeps the 9 raw-spelled shared rows (aib 1,
   jsaa-raw 8), so they are NOT part of the 135. Post-dedupe store = 56,321,
   `dupesDropped=1`.
2. Custom scope, three legs (RED-2a/b/c + live bank probe):
   - harness FTS: `ingest._scope_predicate(pid, 'custom')` raised ValueError
     (custom not in `_VALID_SCOPES`) — 14 queries crash.
   - harness vector: `_chroma_where(pid, 'custom')` returned output IDENTICAL
     to scope='all' ($or fallthrough) — would over-search.
   - bank leg: `build_airaccoon_fn` sent scope='custom' verbatim; live scratch
     server answered `isError: true, invalid-params: Invalid scope 'custom':
     expected all, project, or shared.` (MemoryTools.cs:188). The harness
     `_call_tool` then died parsing the non-JSON error text (JSONDecodeError
     → recorded transport error).
   Bank scope=project already covers custom labels (SearchContexts.cs:44-50),
   so custom→project on all three legs is semantics-preserving.
3. C034 (expectedHash null, ai-sheepdog/project, content-targeted by generator
   contract): `run_eval` raised `ValueError: run_eval: C034: missing
   expectedHash` — warn-and-record never saw it (RED-3). Only 1/100 queries
   is null-anchored; corpus hashes otherwise distinct (99).
4. Live-bank anchor census (fresh read-only copy `/tmp/p1-live-copy.db`,
   sha `e0434a72…` vs corpus header `f2cb1210…` — bank drifted since corpus
   build; both legs run on the same copy so parity is unaffected): 98/99
   anchors present; missing = [C026] only. Anchor gate re-proves C026 at build.

## Decisions / rejections

- New module `scopes.py` (stdlib-only) for VALID_SCOPES + normalize_scope +
  resolve_buckets + load_corpus. Rejected: putting them in ingest.py —
  evaluate.py must stay stdlib-importable (CI scripts-harness lane runs it
  without chromadb/torch) and slice_copy.py is stdlib-only too.
- `resolve_buckets(copy, corpus, explicit)`: explicit `--buckets` >
  corpus-header derivation > ValueError (never a frozen fallback).
  Copy-validates resolved buckets (typos fail loud, not silent-empty).
  Excluded manifest is ALWAYS the header excludedProjects verbatim.
- Three-legged custom→project mapping via one SCOPE_FALLBACK table:
  `ingest._scope_predicate` + `retrieve._chroma_where` normalize at the
  predicate layer; `evaluate.build_airaccoon_fn` maps before `_call_tool`.
- Null anchor filtered pre-`run_eval` via `partition_null_anchors` into
  `staleAnchors`: 100 = 99 scored + 1 accounted. `run_eval` keeps raising.
- Multi-homed rows (measured: 1 hash, byte-identical, under
  hermes-default/project id 11669 + jsaa/project id 11713): `dedupe_rows`
  keeps first-by-id, audits via stdout + params `dupesDropped`; differing
  values under one hash still fail loud. **F10 (tightened):** the dropped hash
  `3f76c7890d5a…` is not an anchor and appears in **0/198 served sets** of the
  frozen golden (re-verified after regeneration).
- Model freeze: `PINNED_MODEL_REVISION` + `model_weights_info` (cache
  `refs/main` + snapshot bytes) recorded into `params.json`; ingest refuses on
  mismatch. Evaluations now refuse too (C3) and production runs `--offline`.
- Gap counts: `evaluate.aggregate_gaps` over paired rows, c-cell only, from
  existing `fts_hit`/`vector_hit` columns.
- `slice_copy` keeps its legacy 2-bucket default ONLY for bare-list corpora;
  an explicit blank `--buckets ""` now exits 2 (F11).
- Report: bucket prose derived from `resolvedBuckets` + observed shared-tier
  spellings; stale-aware SUBSET disclaimer; provisional gap table; exclusion
  disclosure now renders committed/shared counts; provenance line (C3).

## Close-out deviations (must read for C8/P2)

1. **Corpus regenerated from the live-bank copy, not the pinned snapshot.**
   The corpus pinned `f2cb1210…`, whose source copy is no longer present at
   the documented path (and was not found under `/tmp`); the store and the only
   available copy pin `e0434a72…`.
   C2 regenerated `corpora/project-corpus-100.json` from `/tmp/p1-live-copy.db`
   (read-only) so corpus ↔ copy ↔ store pin one snapshot. Consequence: the
   query set is rebuilt deterministically from the current copy (73/100
   queries byte-identical to the old corpus; 27 targets moved because the bank
   changed since `f2cb1210`), and the stale-anchor instance changed from
   {C026, C034} to **{C035}** (the single content-targeted null anchor; no
   missing-from-store anchors because corpus and store now come from the same
   copy). The AC2/AC4 semantics are preserved — 100 = 99 scored + 1 null-anchor
   accounted, warn-and-record verified by the collector. The checked-in
   regeneration-equality test passes **armed**:
   `AI_RACCOON_EVAL_COPY=/tmp/p1-live-copy.db python3 -m pytest scripts/tests/test_build_project_corpus.py -q`
   → `5 passed in 4.32s` (0 skipped); without the env var the committed-artifact
   halves skip by the test's H3 snapshot gating.
2. **The eval's bank leg required a quiesced scratch copy — the plan assumed
   it was stable and it is not.** With the scratch copied straight from the
   bank copy, the scratch server's inherited watch registrations re-ingest
   live files while the eval runs: measured scratch row counts 56,457 →
   56,481 (run 1), 56,457 → 53,637 (run 3); `serve.log` shows VACUUM,
   pending-embed drain and WAL checkpoints; the copy itself has 0 pending rows,
   so the churn is watch-driven. Bank-leg metrics across no-quiesce runs:
   hit-rate 0.798 / 0.798 / 0.869, contingency c = 14 / 14 / 21, MCC 0.6205 /
   0.6205 / 0.4347. **Mechanism used:** a scratch base copy with
   `watch.enabled.*`, `sweep.enabled.global` and `extract.enabled.global` set
   to `false` (12 settings rows; base sha `aec21bf7…`, rows 56,457); each
   repeat starts from that base byte-for-byte. Under it the bank leg is
   byte-identical across fresh processes and fresh copies (A/B/C/D).
   This is an eval-rig precondition and should become a documented step /
   P2-owned helper; the golden's run log carries the SHA warning
   `scratch copy sha aec21bf7… != store copy sha e0434a72…` (row count
   matches — settings-only delta).
3. **The historical `0.788/0.183` numbers were produced on a drifted scratch.**
   `/tmp/p1-eval-scratch/memory.db` holds 56,481 rows and 43 hashes absent from
   the pinned copy (19 copy-only), i.e. the bank leg read a different universe
   than the harness store. C3's row-count gate refuses that scratch now. The
   frozen golden's numbers come from the quiesced, row-matched rig.
4. **C4's collector expectations were derived, not hardcoded.** The old
   collector pinned `{C026,C034}` and `n==99` as literals; with the corpus
   regenerated those instances moved. The committed collector derives
   n/stale from the corpus + store (null anchors ∪ anchors missing from the
   store) and keeps the model-pin literal comparison C4 asked for. The gate is
   still fail-capable (unit test red on wrong/missing/zero-byte records).

## Acceptance criteria evidence

### C1 — deterministic vector leg (F1, MUST)

RED first (fake collection whose tie subset varies with k, both collections):

```
$ python3 -m pytest scripts/tests/test_llamaindex_harness_retrieve.py -q -k "tie_is_k_complete"
FAILED ...::test_vector_leg_content_tie_is_k_complete_across_process_variants
FAILED ...::test_vector_leg_structure_tie_is_k_complete_across_process_variants
2 failed, 12 deselected
  AssertionError: content-leg tie truncation is k/process dependent
```

Fix: `_query_tie_complete` grows the request while the (window+1)-th distance
equals the window-th, bounded by the collection count; `_top_window_similarity`
sorts by `(distance, hash)` and cuts at `window`; applied to content AND
structure. No frozen knob touched (`params.json` verified by the collector).

```
$ python3 -m pytest scripts/tests/test_llamaindex_harness_retrieve.py -q
14 passed
```

Double-run proof (fresh scratch copy per repeat, fresh processes): run C vs
run D — **0 row diffs, 0 top-level diffs modulo `sessionId`; harness hashes and
bank hashes byte-identical; mean-F1 Δ = 0.0 (≤1e-9); contingency and MCC
equal.** The harness leg was also byte-identical across **all six runs** (no-quiesce
1/2/3 and quiet A/B/C/D) — the old C045/C066 set divergence is gone.

### C2 — manifest truth + report wording (F2/F3)

RED → GREEN gate:

```
$ python3 -m pytest scripts/tests/test_build_project_corpus.py -q -k splits_committed   # pre-fix
E  KeyError: 'committedRows'
$ AI_RACCOON_EVAL_COPY=/tmp/p1-live-copy.db python3 -m pytest scripts/tests/test_build_project_corpus.py -q
5 passed
```

Registry now renders the split (`aib` committed=0/shared=1; job-search
committed=106/shared=8) and the corpus/params/report carry the same manifest.
Store `params.json` gained `bucketCounts`; the report renders
`Resolved project buckets (11): …` plus
`Additional shared-tier spellings … aib/shared (1), job-search-ai-assistant/shared (8)`.

### C3 — provenance (F4)

- `evaluate.model_revision_check`: the cache revision must equal the store's
  `modelRevision`, the record must exist, bytes > 0 (unit-tested red on all
  three failure shapes).
- `evaluate.scratch_copy_check`: row-count drift fails loud; SHA-only drift
  warns as an access-bump mutation (unit-tested, both paths).
- `results.json` carries `modelRevision`/`modelBytes`/`copyPath`/
  `copySnapshotSha256`; `params.json` carries `copyPath`/`copySnapshotSha256`;
  the report renders a provenance line.
- The gate caught the drifted legacy scratch: `FAIL: scratch copy rows 56481
  != store copy rows 56457` (run-2 attempt against `/tmp/p1-eval-scratch`).

Golden provenance lines:

```
Provenance: weights revision cb950dc80d67... (869254400 bytes); bank copy /private/tmp/p1-live-copy.db (sha256 e0434a7214ac...)
model: revision=cb950dc80d67... bytes=869254400 | scratch copy sha=aec21bf7... rows=56457 | copy path /private/tmp/p1-live-copy.db
```

### C4 — collector (F5)

`scripts/retrieval_tuning/collect_ac_evidence.py` committed with the pin check
comparing to `ingest.PINNED_MODEL_REVISION`; pure `model_pin_failures()` unit
test fails red on wrong/missing/zero-byte records. Run output (13/13 PASS):

```
[PASS] AC2 summary.n == scorable count: n=99 scorable=99 (100 = 99 scored + 1 null-anchor accounted)
[PASS] AC2 staleAnchors == corpus/store-derived stale set: staleAnchors=['C035'] expected=['C035'] (null=['C035'], missing-from-store=[])
[PASS] AC2 eval_gate_failures == []: failures=[]
[PASS] AC4 WARNING line matches the derived stale count: ['WARNING: 1 stale anchors (re-chunked upstream, unhittable by either leg): ['C035']']
[PASS] AC1 buckets cover corpus or manifest: resolvedBuckets=11 excluded=2
[PASS] manifest seed-equal to header: params.excludedProjects == header.excludedProjects
[PASS] snapshot SHA recorded: e0434a7214ac...
[PASS] model revision is the pinned revision: params.modelRevision=cb950dc80d67... pinned=cb950dc80d67... bytes=869254400
[PASS] copy provenance recorded: copyPath=/private/tmp/p1-live-copy.db sha=e0434a7214ac...
[PASS] corpus snapshot == store copy snapshot: corpus=e0434a7214ac... copy=e0434a7214ac...
[PASS] frozen knobs untouched: rrfK/weights/limit/floor/lambda/threshold/Max/Max3X100/alpha per contract
[PASS] gap counts conserved: gaps={'n_paired': 99, 'c_cell': 15, 'c_fts_only': 14, 'c_vec_only': 0, 'c_both_legs': 1, 'c_neither_leg': 0, 'c_unknown': 0}
[PASS] contingency sums to paired: cont={'a': 65, 'b': 2, 'c': 15, 'd': 17} paired=99
hit-rates: harness=0.677 f1=0.158 | bank=0.808 f1=0.187 | mcc=0.5954897352959921
```

### C5 — report/lane corrections, slice, memwatch

- F7/F8/F10 fixes are in this report (breakdown above; TDD honesty below;
  dedupe sentence above).
- `slice_copy --buckets ""` exits 2 (RED pre-fix silently sliced with the
  legacy default); test `test_slice_explicit_blank_buckets_fails_loud`.
- `memwatch.py` committed (import-safe argparse + main, default cap 12 GB,
  tree-RSS sampling documented as a backstop, SIGTERM/SIGKILL of the whole
  tree, stdout reader thread). Tests: 20 k-char child HEAD survives (the old
  `/tmp/memwatch.py` printed only `out[-6000:]` — proven: HEAD absent, 6001
  chars captured), and the live-kill spawns a hog under a 50 MB cap and
  asserts exit 99 + grandchild death.

TDD honesty notes (F8): the `test_partition_null_anchors` expectation was
deliberately changed inside implementation commit `1b6ce543` (RED expected
`["E001","E003"]`; final expects `["E001"]`, because an empty-string anchor is
filtered like a null — `run_eval` refuses `""` too); `RED-retrieve-custom.txt`
shows one failure as an argparse `SystemExit` for the then-missing `--buckets`
flag rather than a behavioral assertion; the behavioral predicate tests are
the load-bearing RED there. The C1/C2/C3/C4/C5/C6 gates above were all
red-pasted on the shipping assertion before implementation.

### C6 — params refresh without re-embedding

`--refresh-params` validates the fresh row id set + per-id text hashes +
structure id set + model revision against the store, then rewrites FTS +
params + verify; any drift refuses loud ("a full ingest is required"). Tests
pin the accept path (with a raising embed seam) and both refusal paths.
Live refresh of `/tmp/p1-full-store` (12 s, peak 1887 MB, no re-embed):

```
$ python3 memwatch.py --cap-mb 12288 --log /tmp/p1-closeout/refresh-mem.log -- \
    python3 -m llamaindex_harness.ingest --copy /tmp/p1-live-copy.db \
    --store-dir /tmp/p1-full-store --corpus corpora/project-corpus-100.json \
    --refresh-params --offline
refreshed: rows=56321 copyEntries=56457 dupesDropped=1 (no re-embed)
VERIFIED: refreshed store is chunk-faithful to the copy; FTS parity probe clean
memwatch: peak=1887MB cap=12288MB result=exit(0)
```

Refreshed `params.json`: `corpusSnapshotSha256 = copySnapshotSha256 =
e0434a72…`, corrected manifest, `bucketCounts`, model pin unchanged.

### C7 — golden freeze (double-run)

Both runs on the refreshed store, new corpus, quiesced scratch base
(`aec21bf7…`, fresh copy per repeat), under memwatch 12288:

| run | command | peak RSS | exit | bank hit/F1 | c | MCC |
|---|---|---|---|---|---|---|
| C (golden) | eval `--out results-f1.json --offline` | 6633 MB | 0 | 0.808 / 0.187 | 15 | 0.5954897352959921 |
| D (run-2) | eval `--out results-f1-run2.json --offline` | 6561 MB | 0 | 0.808 / 0.187 | 15 | 0.5954897352959921 |

Run-1-vs-run-2 diff (modulo `sessionId`) — **empty**:

```
top-level diff keys (modulo sessionId): []
row diffs: 0
harness hashes byte-identical: True
bank hashes byte-identical: True
mean-F1 harness delta: 0.0  | bank delta: 0.0
```

Raw no-quiesce repeats (evidence for deviation 2; not the golden):
run 1 = run 2 (0.798/0.185, c=14, peaks 8551/7904 MB), run 3
(0.869/0.201, c=21, peak 9033 MB).

## Peak RSS

| phase | peak RSS | cap | result |
|---|---|---|---|
| ingest (historical) | 4840 MB | 12288 | exit(0) |
| refresh (C6) | 1887 MB | 12288 | exit(0) |
| eval C (golden) | 6633 MB | 12288 | exit(0) |
| eval D (run-2) | 6561 MB | 12288 | exit(0) |
| eval A/B (quiet, same copy) | 6644 / 5686 MB | 12288 | exit(0) |
| eval run 1/2/3 (no-quiesce) | 8551 / 7904 / 9033 MB | 12288 | exit(0) |
| C10 trace (harness replica) | 2493 MB | 12288 | exit(0) |
| C10 bank probe (real server) | 3482 MB | 12288 | exit(0) |
| C10 exact-recall / profile | 2212 / 2212 MB | 12288 | exit(0) |

## Artefact locations

- Golden: `docs/work/results-f1.json` (run C), `docs/work/results-f1-run2.json` (run D).
- Eval report: `docs/work/2026-09-10-p1-full-100-eval-report.md` (regenerated round 2).
- Determinism analysis: `docs/work/2026-09-10-p1-determinism-finding.md`.
- **C10 evidence: `docs/work/2026-09-10-p1-c10-shared-fusion-trace.md`; repro
  `docs/work/c10_shared_leg_trace.py`; census strata `docs/work/mmr_transfer_checks.py`.**
- Review: `docs/work/2026-09-10-p1-review-d756.md`; plan: `docs/work/2026-09-09-air-full-hundred-query-parity-eval.md`.
- RED/collector/run logs: `/tmp/p1-closeout/` (RED-c1-tie.txt,
  RED-c2-manifest.txt, RED-c2-report.txt, RED-c2-bucketcounts.txt,
  RED-c3-provenance.txt, RED-c4-collector.txt, RED-c5-slice.txt,
  RED-c5-memwatch.txt, ac-evidence.txt, evalC.log, evalD.log, eval1-3 logs).
- Test suite: `140 passed, 0 skipped` with `AI_RACCOON_EVAL_COPY` armed
  (`137 passed, 3 skipped` on the default absent copy — the committed-artifact
  halves skip by design).
- Quiesced scratch base: `/tmp/p1-eval-scratch-quiet-base.db` (sha `aec21bf7…`).
- Refreshed store: `/tmp/p1-full-store`; pinned copy: `/tmp/p1-live-copy.db`
  (never written; `file:…?mode=ro` everywhere).

## Review findings: closed vs open

| # | Sev | Status | Evidence |
|---|---|---|---|
| F1 | MUST | **CLOSED** | C1 tie-complete expansion; harness byte-identical across fresh processes; C/D exact |
| F2 | SHOULD | **CLOSED** | manifest split + reasons; corpus regenerated; equals test armed green |
| F3 | SHOULD | **CLOSED** | report renders resolvedBuckets + extra shared spellings |
| F4 | SHOULD | **CLOSED** | eval-side revision gate; results/params provenance; report line |
| F5 | SHOULD | **CLOSED** | collector compares to `PINNED_MODEL_REVISION`; 13/13 PASS |
| F6 | SHOULD | **OPEN (P3)** | `scripts/src/retrieval_tuning/corpus.py` second `VALID_SCOPES` rejects `custom`; P3 AC1's seventh spelling — not a C-item |
| F7 | NOTE | **CLOSED** | corrected 135-row breakdown in this report |
| F8 | NOTE | **CLOSED** | TDD honesty notes above |
| F9 | NOTE | **OPEN (P2)** | candidate-window gap taxonomy is P2 AC2's amendment |
| F10 | NOTE | **CLOSED** | dropped hash `3f76c7890d5a…` in 0/198 golden served sets |
| F11 | NOTE | **CLOSED** | `slice_copy --buckets ""` exits 2 + test |

## New findings for C8/P2

- **N1 (rig):** a scratch bank copy inherits 12 watch registrations and the
  sweep/extract kill switches, so the scratch server re-ingests live files
  mid-eval and the bank leg moves. The golden therefore requires the quiesced
  scratch base (deviation 2); P2's repeats should build on it.
- **N2 (provenance):** the legacy `/tmp/p1-eval-scratch/memory.db` diverged
  from the pinned copy (56,481 vs 56,457 rows; 43 scratch-only, 19 copy-only
  hashes), so historical bank-leg numbers were measured on a different
  universe. C3 now refuses it; the historical `0.788/0.183` must not be
  compared against the quiesced golden.
- **N3 (tolerance):** plan P3 AC4 / P4 AC2's "exact on hashes" is satisfiable
  only with the quiesced rig. If P3 keeps one tolerance term, state the
  precondition in the same breath.

## Close-out round 2 (C9–C11, 2026-09-10, branch `…-lane-close2`)

Round-2 base: merged P1 close-out `44546818`. The golden stays run C/D
(`results-f1.json` / `results-f1-run2.json`): C10 found no fusion defect and no
knob, corpus, store or settings changed, so there is no re-freeze.

### C10 — shared-scope fusion-loss trace (per-spec; stage = Take(8))

Repro committed as `docs/work/c10_shared_leg_trace.py` (defaults resolve to this
checkout); full evidence in `docs/work/2026-09-10-p1-c10-shared-fusion-trace.md`.

```
$ cd scripts/retrieval_tuning && python3 memwatch.py --cap-mb 12288 \
    --log /tmp/p1-closeout2/rss/c10-trace-mem.log -- \
    python3 ../../docs/work/c10_shared_leg_trace.py --harness-dir . \
    --corpus corpora/project-corpus-100.json --store-dir /tmp/p1-full-store --offline
memwatch: peak=2493MB cap=12288MB result=exit(0)
  C019: fts rank 1; vector absent; dedupe kept; pre-RRF 28; post-affinity 28 (0.6932) ≥ 0.6
        VERDICT: outside top-8 after floor (rank 28)
  C065: fts rank 1; vector absent; dedupe kept; pre-RRF 32; post-affinity 32 (0.6630) ≥ 0.6
        VERDICT: outside top-8 after floor (rank 32)
  C081: fts rank 1; vector rank 95; dedupe kept; pre-RRF 10; post-affinity 10 (0.8714) ≥ 0.6
        VERDICT: outside top-8 after floor (rank 10)
```

Stage verdict: **Take(8)** — no per-leg dedupe / RRF / floor / affinity loss.
The bank's `evidenceByHash` sidecar (scratch probe, peak 3482 MB) shows the same
anchors served at bank ranks 1/2/1 with vector ranks **25/19/23** (harness:
absent/absent/95): the leg inputs, not the fusion, differ. Stored vectors for
the same text measure cos(bank, harness) 0.504–0.628 — the measured ONNX-vs-HF
model-conversion seam (bank ONNX self-consistency 0.9826; ONNX-vs-HF 0.5934 on
the same text; the harness already pools CLS at cos 1.00000 vs the model-card
recipe — the earlier pooling explanation is retracted, C14). Rows are marked
**fusion-drop** (never "embedding-gap evidence") in the eval report for P2 AC2.

### C9 — query-composition disclosure

RED pasted before the implementation:

```
$ python3 -m pytest scripts/tests/test_llamaindex_harness_report.py -q \
    -k "stratification or stratified or stale_anchors_are_never"
FAILED ...::test_stratification_is_signature_based_not_id_based
FAILED ...::test_report_renders_stratified_rates_and_composition_disclosure
FAILED ...::test_report_asserts_stale_anchors_are_never_scored
3 failed, 13 deselected
```

GREEN: `16 passed`. The regenerated report recomputes the split from the scored
rows' query text (signature adapted from `docs/work/mmr_transfer_checks.py`,
never an id list):

```
| debris (markup/JSON signature) | 23 | 0.609 | 0.696 | 0.169 | 0.188 |
| clean                          | 76 | 0.697 | 0.842 | 0.155 | 0.187 |
```

with the composition-sensitivity disclosure. NOTE: the plan's C9 prose records
clean-bank `0.8289`; the recomputation on the frozen golden gives **0.8421**
(plan number came from the sibling's earlier reading) — the artifact holds the
recomputed value. `staleAnchors ∩ scored == ∅` is now asserted in `render`
(ValueError on violation); the obsolete C026-scored sensitivity test was
replaced by an unscored-stale shape that still pins the SUBSET-accounting rule.

### C11 — quiesced scratch builder + run recipe

RED:

```
$ python3 -m pytest scripts/tests/test_make_quiesced_scratch.py -q
ModuleNotFoundError: No module named 'make_quiesced_scratch'
```

GREEN: `5 passed`. Helper `scripts/retrieval_tuning/make_quiesced_scratch.py`;
smoke-tested on the pinned copy (2.1 s, no model):

```
$ python3 make_quiesced_scratch.py --source /tmp/p1-live-copy.db --out /tmp/p1-closeout2/base-rebuilt.db
rows: 56457 (copy parity verified)
settings disabled: 12 (watch/sweep/extract kill switches)
base sha256: eeb431a3efd8933811714d1fb3fa7e97bb26ae9e95f4573bcaa71d8588a5e41d
```

The helper landed now so P2 consumes it (as this report recommended); the
row-stability assertion it exposes is part of the run recipe below.

### Round-2 suite note

Full `scripts/tests` on this box: **551 passed, 8 skipped, 5 failed** — all five
pre-existing and unrelated to round 2. Stashing the round-2 tracked edits
reproduces the three that do not depend on installed extras
(`test_dependencies_declared` matplotlib gap; `test_retrieval_tuning_eval_corpus`
E040 + committed `eval-set-100.json` drift) failing identically; the other two
(`test_retrieval_tuning_report` study-summary) need `optuna`, which is not
installed here (the module itself fails collection without it).

## RUNBOOK — reproducing the frozen golden

1. **Store** (already frozen at `/tmp/p1-full-store`; full rebuild = re-ingest):
   `python3 -m llamaindex_harness.ingest --copy /tmp/p1-live-copy.db
   --store-dir /tmp/p1-full-store --corpus corpora/project-corpus-100.json --offline`
2. **Quiesced base from the pinned copy** (C11 helper; refuses row drift):
   `python3 make_quiesced_scratch.py --source /tmp/p1-live-copy.db
   --out /tmp/p1-eval-scratch-quiet-base.db`
3. **Per-repeat scratch**: byte-copy the base to `<scratch>/memory.db`, record
   the row-stability snapshot before and after with
   `python3 make_quiesced_scratch.py --stability <scratch>/memory.db`
   (entries + max created/updated must be equal across the run; a
   bookkeeping-only WAL from access bumps is expected).
4. **Eval** (one heavy process at a time, ~3 min, never port 7721):
   `python3 memwatch.py --cap-mb 12288 --log <log> -- python3 -m
   llamaindex_harness.evaluate --corpus corpora/project-corpus-100.json
   --store-dir /tmp/p1-full-store --scratch-data-root <scratch>
   --out <results.json> --offline`
5. **Report**: `python3 -m llamaindex_harness.report --results <results.json>
   --out <report.md> --context /tmp/p1-closeout/report-context.json`
6. **Determinism gate**: two fresh-process runs on two fresh copies of ONE base
   must be byte-identical modulo `sessionId` on both legs (C12); mean-F1
   ±1e-9. If an anchor moves, stop — never tolerance-bless it.

## Proposed plan amendments (task-tracking plan not owned here)

- **C9**: replace the recorded clean-bank `0.8289` with the recomputed `0.8421`
  (`report.py` is the generator; the artifact is the source of truth).
- **C10**: check the box as per-spec (no re-freeze); the measured embedding
  seam is ONNX-vs-HF model conversion (not pooling — retracted, C14). Aligning
  it would require a full re-embed or the bank's own ONNX in the harness, so it
  is a decision, not a close-out fix.
- **C11**: helper is built (`make_quiesced_scratch.py`) and P2 should consume
  it; the plan's "P2 ownership" line can become "P2 consumes".
- **C2**: the external-validation #5b uniform-8 wording fix landed with this
  round's report regeneration (the report no longer claims `searchLimit=5`).
