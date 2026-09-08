# P0b spike report: reference-vs-proxies similarity signal

Lane `task/air-mmr-impl-p0b`, base `95b612c2`. Date 2026-09-08.
Plan: `mmr-implementation-plan.md` §2 P0b with rulings §1.18 (reference-vs-proxies,
τ=0.90, cost gate), §1.10 (own copy dir), §1.13 (fetch shape), §4 G-IRON.
Detail: lane B `plan-section-b-signal.md` §§B.2–B.4.

## Winner

**Content-vector fetch wins by default. Token-overlap agreement 0.821 (55/67) is
below τ=0.90. Structure-vector candidacy is unevaluable on decision-relevant triples
(0/67 full coverage). Cost gate passes for every signal, so there is no NO-GO.**

Decision rule applied verbatim from §1.18: cheapest ≥ τ wins; none ≥ τ →
content vectors win by default (fetch ships per §1.13). τ = 0.90 fixed before running.
Cost gate ≤ 1 batched query/search.

## Metric (pre-registered before any run, fixed throughout)

Content vectors are the REFERENCE and excluded from candidacy. Candidates are
token-overlap and structure vectors. The yardstick is anchored-triple
pair-orientation agreement: for anchor `a` with competitors `b, c`, both reference
cosines in [0.85, 1.0] and margin |R_ab − R_ac| ≥ 0.1, the proxy scores if it orders
`b, c` the same way as the reference. Agreement is the matched-order fraction.
Proxy ties count as misses. Triples missing any structure vector are excluded from
the structure denominator, with coverage reported separately.

Two readings were rejected. Unanchored pairs-of-pairs were refused because MMR's
greedy step always compares similarities that share the picked set, so disjoint
pairs would pad the denominator with comparisons MMR never makes. Single-pair
thresholding was refused because it needs a proxy cutoff the plan never sets, and
orientation avoids inventing one. The anchored triple is the MMR-faithful shape
and it stays non-circular: pool selection uses only metadata (source_file, id),
never vectors or scores.

## Results

Final pool (pinned rule, §Reproducibility): 1348 rows after value-dedup (1440
before), from the 120 largest files, 12 lowest ids per file. Content dim 1024,
structure dim 1024, numpy 2.5.1.

| Signal | Agreement (margin-filtered) | Denominator | Queries/search | Measured on copy |
|---|---|---|---|---|
| Token-overlap (Jaccard, lowercase `[a-z0-9]+`) | 0.821 (55/67) | 67 triples | 0 (resident) | matrix 29.8 s Python-prototype |
| Structure cosine | unevaluable (null) | 0 triples, coverage 0.0 | 1 batched | fetch 337 rows 3.1 ms |
| Content fetch (reference/default) | yardstick, not scored | n/a | 1 batched | fetch 1348 rows 6.4 ms |

Margin-filtered pair counts: 584 unique band pairs, of which 26 have structure on
both sides. Anchored partner-pairs pre-margin 3293. Post-margin triples (the only
denominator behind any agreement above) 67. Margin count curve, counts only:
≥0.0 → 3293, ≥0.05 → 468, ≥0.10 → 67, ≥0.15 → 0, ≥0.20 → 0. No agreement is
reported off the pre-registered filter anywhere in this file.

Miss anatomy for token (12 misses): 7 are proxy ties at 0.0 on JSON chunks, which
the pre-registration counts as misses, and 5 are genuine order flips. Per-triple
detail (ids, refs, proxies, match flags) is in `p0b-spike/results_run1.json`
under `triples_detail`.

D3 context curves, decision unchanged at τ=0.90. Same filtered agreements, three
thresholds: at τ=0.40 token (0.821) would win on cost; at τ=0.80 token would win
on cost; at τ=0.90 nothing clears, so content fetch wins by default. The lower
curves are context only and never renegotiate the call.

Cost gate verdict: token 0, structure 1, content 1, every signal ≤ 1. No cost-gate
failure, hence no NO-GO. The token matrix timing is a Python-prototype number for
1348 rows, not a production cost; production MMR input is capped (proposed 50)
and the gate counts round trips, which is why token still loses on agreement
rather than winning on price.

## Residency (criterion 4, both legs verified in code)

Token-overlap rides resident `ValueByHash` with zero new I/O. FTS leg populates
it at `src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.Search.cs:78`
(`byHashIndex.ValueByHash[row.Hash] = row.Value`, with `FtsQueryByHash` and
`IdByHash` on :79-80). Vector leg populates it at
`src/AiRaccoon.Infrastructure/Sqlite/Memory/SqliteMemoryStore.cs:850`
(`byHashIndex.ValueByHash[row.Hash] = row.Value` inside `QueryDualVectorsAsync`,
:822-851). Carrier type:
`src/AiRaccoon.Infrastructure/Sqlite/Memory/ByHashIndex.cs:3`. Neither leg's row
shape carries vectors (`VectorRow` in `SqliteMemoryStore.Rows.cs` has Hash, Path,
Value, Distance, SourceFile, ChunkIndex, TotalChunks, no embedding), so every
vector signal needs its batched fetch while token needs none. Structure is the
honest second candidate and it fails as predicted: `structureEmbedding` is null
whenever no heading parsed (`EntryEmbedder.cs:248`), only 15648/53682 copy rows
(29.1%) carry one, only 26/584 band pairs have both sides, and exactly 0/67
decision triples have full coverage. Its failure is ADR data, as the plan
expected.

## Copy receipt and G-IRON (criterion 1)

Live bank `/Users/arasz/.ai-raccoon/memory.db` was never opened, read, or
written by this spike. The only live operations were `stat` and one byte `cp`.
Spike code contains no live path (grep for `ai-raccoon/memory.db` and
`.ai-raccoon/` under `p0b-spike/` returns empty). The sole SQLite open in
`run_spike.py:38` targets `file:/tmp/mmr-bank-copy/p0b/bank.db?mode=ro` with
`PRAGMA query_only=ON`, guarded by a prefix check that refuses any other path.

Live `stat` before copy:

```
16777234 166780557 -rw-r--r-- 1 arasz staff 0 1424158720 "Sep  8 16:48:09 2026" "Sep  8 16:48:09 2026" "Sep  8 16:48:09 2026" "Aug  4 11:05:48 2026" 4096 2813032 0 /Users/arasz/.ai-raccoon/memory.db
```

Live `stat` immediately after the copy (identical size and mtime):

```
16777234 166780557 -rw-r--r-- 1 arasz staff 0 1424158720 "Sep  8 16:48:09 2026" "Sep  8 16:48:09 2026" "Sep  8 16:48:09 2026" "Aug  4 11:05:48 2026" 4096 2813032 0 /Users/arasz/.ai-raccoon/memory.db
```

Copy `integrity_check` on the copy only: `ok`. Copy tables confirmed (`entries`
with `embedding`/`structure_embedding` BLOBs, vec mirrors present but never
queried by the spike).

One disclosure. A final live `stat` after the runs shows size 1424195584 and
mtime `Sep  8 16:51:31 2026`: the live server wrote during the four-minute spike
window (its `quiet.log` grew in the same span). That drift is external, after the
copy was carved, and it cannot reach the results: the copy is immutable
(`bank.db` still size 1424158720, mtime `Sep  8 16:49:28 2026`, the cp instant),
every measurement opened it read-only, and no spike process ever named the live
path. The identical before/after pair above covers my own operation window.

## RED-first tests (criterion 3)

`p0b-spike/test_agreement.py` was written before `p0b-spike/agreement.py`
existed. First run output (both RED):

```
FAILED test_agreement.py::test_p0b_agreement_margin_filter_excludes_ties - Mo...
FAILED test_agreement.py::test_p0b_reference_excluded_from_candidates - Modul...
=================================== FAILURES ===================================
________________ test_p0b_agreement_margin_filter_excludes_ties ________________
    def test_p0b_agreement_margin_filter_excludes_ties():
        """P0b_Agreement_MarginFilterExcludesTies: near-ties must not count."""
>       from agreement import filtered_triples
E       ModuleNotFoundError: No module named 'agreement'
_________________ test_p0b_reference_excluded_from_candidates __________________
    def test_p0b_reference_excluded_from_candidates():
        """P0b_ReferenceExcludedFromCandidates: content vectors are the yardstick, never a candidate."""
>       from agreement import CANDIDATE_SIGNALS, validate_candidates
E       ModuleNotFoundError: No module named 'agreement'
```

After implementing the metric, `pytest` is 2 passed. A report listing content
vectors as a candidate fails by construction: `validate_candidates` raises
`ValueError` on `content`/`reference`, and `filtered_triples` calls it on every
invocation, so the runner cannot score the yardstick as a contender.

## Reproducibility (criterion 5)

Pinned: pool SQL (top-120 files by chunk count with ≥6 chunks, 12 lowest ids per
file, value-dedup keeping lowest id), tokenizer (`[a-z0-9]+`, lowercase,
Jaccard with empty-empty → 1.0), metric constants (band [0.85, 1.0], margin 0.1,
τ=0.90), code at this worktree (`p0b-spike/agreement.py`, `p0b-spike/run_spike.py`),
numpy 2.5.1, copy bytes (`bank.db` size 1424158720 as carved). Two full runs:

```
digest run1: ebe35693534b307fdb62588d012190208dd24347f811b7ab9dbcae007161c107
digest run2: ebe35693534b307fdb62588d012190208dd24347f811b7ab9dbcae007161c107
stable identical: True
triples_post_margin run1= 67 run2= 67
token_agreement run1= 0.8208955223880597 run2= 0.8208955223880597
structure_triples run1= 0 run2= 0
margin_counts run1= {'0.0': 3293, '0.05': 468, '0.1': 67, '0.15': 0, '0.2': 0}
            run2= {'0.0': 3293, '0.05': 468, '0.1': 67, '0.15': 0, '0.2': 0}
```

Full JSON for both runs plus stdout logs: `p0b-spike/results_run1.json`,
`p0b-spike/results_run2.json`, `p0b-spike/run1_stdout.txt`,
`p0b-spike/run2_stdout.txt`. The digest covers every stable field (counts,
agreements, costs); only wall-clock timings sit outside it.

## Deviations from the recommended path (each owned)

1. Pool re-pinned twice, metric never touched. The alphabetical-first-30 pool
   gave 20 partner-pairs and 0 post-margin triples. The top-by-size rule
   replaced it (same metadata-only, content-blind basis), then grew 30×6 to
   120×12 under the identical rule to lift the denominator 6 → 67.
2. Value-dedup added after an interim 104/104 token score. That score rode on
   byte-identical pairs, which `ModalityCandidates.Deduplicated` removes before
   MMR ever runs (lane B §B.0-2), so the pool was unfaithful and the number was
   discarded, not reported as a result. Dedup keeps lowest id per value.
3. "Pair-orientation" operationalized as anchored triples (see §Metric for the
   two rejected readings and their reasons).
4. D3 curves rendered as a three-τ decision table plus a counts-only margin
   curve, so no off-filter agreement appears anywhere.
5. Live end-of-spike drift disclosed above; copy-window receipts identical.
6. No `dotnet build`: no C# was touched. Tests are pytest under `p0b-spike/`;
   `src/**` and `tests/**` are untouched (`git status` shows only `p0b-spike/`
   and this report).

## Hypotheses and settlers

- H1: token misses split into JSON ties (7) and genuine flips (5); a C#
  tokenizer with number-shape tokens could recover some ties. Settler: P2
  fallback-tokenizer choice, measured against this 67-triple set.
- H2: top-by-size pool geometry stands in for real fused pools. Settler: P2
  `MmrFetchCount` plus the stage gate on live fused hashes (§1.14).
- H3: cosine ulps are host-deterministic (reruns here bit-identical); cross-host
  float variance is accepted per H-F1. Settler: P1 exact oracles run per host.
- H4: structure coverage on real fused pools may beat this pool's 0/67. The
  verdict does not depend on it (structure already costs a fetch while scoring
  null here), but P2 fetch logs should confirm rather than assume.

## Files

- `p0b-report.md` (this file, worktree root)
- `p0b-spike/agreement.py` (metric), `p0b-spike/test_agreement.py` (RED-first
  tests), `p0b-spike/run_spike.py` (runner, copy-only guard)
- `p0b-spike/results_run1.json`, `p0b-spike/results_run2.json`,
  `p0b-spike/run1_stdout.txt`, `p0b-spike/run2_stdout.txt`
