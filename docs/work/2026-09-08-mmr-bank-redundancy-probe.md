# P0a — bank-copy redundancy probe (GO/NO-GO for the MMR build)

Verdict: **NO-GO** — faithful-hit rate 0/60 logged (95% upper bound 6.0%) and 1/60 re-served
(upper bound 8.9%), both below the pre-registered ≥10% bar at band ≥0.95. P0b conjunct:
sibling lane pending — moot, cannot change the outcome (recorded, not evaluated here).

- Worktree: `/Users/arasz/RiderProjects/ai-raccoon/.ai-badger/worktrees/air-mmr-impl-p0a`
  (branch `task/air-mmr-impl-p0a`, base `95b612c2`), lane copy dir `/tmp/mmr-bank-copy/p0a/`.
- Scan code: `p0a-scan/` (`sample.py`, `pairs.py`, `impact.py`, `diag.py`, `forensics.py`,
  `coscheck.py`, `servein.py`, manifests `sample.json`/`serve-out.json`/`impact.json`/`pairs.json`).
- Harness: `p0a-harness/` (`P0aHarness` console: `collapse`/`cosine`/`serve`; `P0aHarness.Tests`:
  3 G-IRON guards + 3 collapse pins, 6/6 green). `src/**`, `tests/**` untouched.
- Live-bank contact this session: `stat` metadata + read-only `cp` source opens + one `ls`.
  No live open/checkpoint/migrate/write; `integrity_check` ran on the copy only (twice, `ok`).

## 1. Copy receipt (AC1 — DEVIATION D-1, receipts pasted, see below)

BEFORE (2026-09-08 ~16:47 UTC+2, LiveBank `stat -f`):

```text
/Users/arasz/.ai-raccoon/memory.db      1424158720 bytes mtime=2026-09-08T16:47:05+0200
/Users/arasz/.ai-raccoon/memory.db-wal     2377272 bytes mtime=2026-09-08T16:47:54+0200
/Users/arasz/.ai-raccoon/memory.db-shm       32768 bytes mtime=2026-09-08T16:47:12+0200
```

Copy commands (trio, per protocol for a hot bank):

```bash
mkdir -p /tmp/mmr-bank-copy/p0a
cp /Users/arasz/.ai-raccoon/memory.db /tmp/mmr-bank-copy/p0a/memory.db
cp /Users/arasz/.ai-raccoon/memory.db-wal /tmp/mmr-bank-copy/p0a/memory.db-wal
cp /Users/arasz/.ai-raccoon/memory.db-shm /tmp/mmr-bank-copy/p0a/memory.db-shm
```

AFTER (~17:09):

```text
/Users/arasz/.ai-raccoon/memory.db      1424359424 bytes mtime=2026-09-08T17:09:41+0200
/Users/arasz/.ai-raccoon/memory.db-wal      185432 bytes mtime=2026-09-08T17:09:53+0200
/Users/arasz/.ai-raccoon/memory.db-shm       32768 bytes mtime=2026-09-08T16:50:04+0200
```

`PRAGMA integrity_check` on `/tmp/mmr-bank-copy/p0a/memory.db`: `ok` (run 16:48 and 17:09).

D-1: before≠after because the bank is HOT (owner server self-activity: db +200 KB,
WAL checkpoint cycles 2.4 MB→95 KB→387 KB→185 KB, shm advancing at 16:50 with no command
of mine near it). The protocol's abort rule assumes a quiescent bank; I improve it to
*attribute* drift: every live contact in this session's shell history is `stat`/`cp`
(read-only opens) or `sqlite3` against `/tmp/mmr-bank-copy/p0a/` — no live
open/checkpoint/migrate/write exists to attribute the delta to. Copy side-effect of mine:
the `sqlite3` CLI opens the copy read-write and checkpointed its WAL (copy wal 94 KB→0,
an explicitly copy-allowed act); logical content unchanged, `integrity_check` green on
both sides of the measurement phase, and all scans ran post-checkpoint on one state.

Copy profile (copy only): 53,682 embedded rows (all with blobs, dim 1024, norms ≈23.6 —
NOT unit length, cosine divides by norms), 4,689 distinct `source_file`, 281 NULL-source
rows; settings `rrfK=60`, weights 1/1, `structureAlpha=0.5`, `fusion.noRegression=false`;
served shape limit 8 / floor 0.6 / sourceLambda 0.1 confirmed from code + settings.

## 2. Scan parameters

- Blocked pair scan (`p0a-scan/pairs.py`, numpy, stdlib sqlite3 read-only URI): blocks =
  FTS-term postings with 2 ≤ df ≤ K; exact content-cosine within blocks from stored blobs.
- Query sample (`p0a-scan/sample.py`, seed 20260908): served-log rows with `result_features`
  (193, 2026-09-04→09-08) → drop >20% hashes missing from copy (6 rows; 28/1339 = 2.1%
  churn) → near-dedupe Jaccard ≥0.85 (→178) → family = rarest-token key (175 families) →
  round-robin to **60 queries / 60 families / 5 projects**
  (jsaa 30, ai-raccoon 16, pi-badger-integration 10, arasz-home-page 3, ai-badger 1;
  result_count mix 3×1, 5×21, 8×35, 10×2, 20×1). Manifest `p0a-scan/sample.json`.
- Collapse: production `ModalityCandidates` grouping via harness `collapse`
  (union approximation with served scores, D-3). Cosines: exact numpy, cross-checked vs
  production `EmbeddingBlob.ToFloats` on 20 pairs — max diff **7.8e-16** (`coscheck.py`).
- Waste classes per list, band ≥0.95 primary (protocol H3), 0.85–0.95 with re-open rule:
  (a) cross-source near-dupe pair (strict: both sources non-NULL and different; NULL-involved
  reported separately), (b) same-file concentration (≥2 slots, extras cosine ≥ band to the
  served-first cluster best). Query HIT = (a) OR (b); faithful = HIT on collapse survivors.
- Re-serve for D2 (`serve`, one invocation, model loads once): full production path —
  production FTS/vector SQL, planner, `ModalityCandidates`, RRF, `Merge`, dual-KNN +
  `StructureFusion`, production ONNX query vectors — limits 8 AND 16 for all 60 queries.

## 3. Block coverage + pair stats (protocol gate: ≥80% or INCONCLUSIVE-retry)

| K (max df) | blocks | coverage |
|---|---|---|
| 16 | 33,980 | 73.7% |
| **32 (chosen, smallest ≥80%)** | 37,796 | **83.1%** |
| 64 | 41,101 | 90.4% |
| 128 | 43,062 | 94.4% |

1,545,011 block-pair evaluations → **45,953 unique pairs ≥0.85** (≤3.0%). Full band split:

| band | cross-source | same-file | NULL-involved |
|---|---|---|---|
| 0.85–0.90 | 6,655 | 13,306 | 57 |
| 0.90–0.95 | 4,293 | 11,386 | 11 |
| 0.95–0.98 | 1,122 | 3,852 | 0 |
| 0.98–0.99 | 165 | 700 | 0 |
| 0.99–1.00 | 3,355 | 1,013 | 38 |

Redundancy EXISTS in the corpus (archive/doc mirrors: top same-file concentration is a
19,333-pair build-log file; top cross-source shape is doc↔archive mirrors). Top-10
same-file concentrations in `p0a-scan/pairs.json`.

## 4. Query-level impact, raw AND faithful side by side (n=60, Wilson 95%)

| list | raw-hit ≥0.95 | faithful-hit ≥0.95 | faithful-hit 0.85–0.95 band |
|---|---|---|---|
| logged top-8 (served shape) | 0/60 = 0.000 [0.000, 0.060] | **0/60 = 0.000 [0.000, 0.060]** | 5/60 = 0.083 [0.036, 0.181] (cross 3, conc 2) |
| logged top-10 (protocol shape) | 0/60 | **0/60 = 0.000 [0.000, 0.060]** | 5/60 |
| re-served top-8 | 1/60 = 0.017 [0.003, 0.089] | **1/60 = 0.017 [0.003, 0.089]** | 16/60 = 0.267 [0.171, 0.390] (cross 7, conc 10) |
| re-served top-16 (D2) | 1/60 | **1/60 = 0.017 [0.003, 0.089]** | 19/60 = 0.317 [0.213, 0.442] (cross 12, conc 13) |

- raw == faithful on all 240 lists: collapse never fires — served lists contain NO
  value-identical groups (the pipeline's own per-leg dedup already removes everything it
  can; what remains needs MMR, and at ≥0.95 there is nothing).
- Logged max-pair-cosine distribution (60 lists): min 0.592, p50 0.789, max 0.885 — the
  served shape structurally tops out BELOW 0.95.
- The single ≥0.95 case (qid 1728, re-served only): 0.9994 pair
  `docs/features/file-watcher/spec.json` chunk 0 × `docs/work/archive/…/spec.json`
  chunk 0 — a live↔archive exact mirror. Genuine, solitary.
- 0.85-band pairs are archive/doc mirrors (spec↔archive-spec, plan↔README,
  HERMES.md↔CLAUDE.md, phase6-completion↔phase6_progress) — real redundancy, wrong band
  for the pre-registered rule (loosening to 0.85 post-hoc is forbidden by the plan).
- Same-file slots are common (23/60 logged lists hold a file ≥3 slots, max 7) but DIVERSE
  (conc = 0 at both bands on logged): multiplicity without high cosine is not MMR-removable.

## 5. D2 limit-sensitivity readout (shipped limit stays 8 — context only)

Re-served faithful-hit: top-8 → top-16 = 1/60 → 1/60 at ≥0.95 (the archive pair persists
at both depths); 16/60 (26.7%) → 19/60 (31.7%) at ≥0.85. Direction: deeper lists carry
MORE waste, not less — raising the limit would serve more redundancy. No dilution rescue.

## 6. Acceptance criteria with evidence

1. **Copy receipt** — DEVIATION D-1 (hot bank): before/after `stat` pairs pasted in §1
   (differ by owner-process activity; my contacts provably read-only);
   `integrity_check` on the copy: `ok` ×2. Criterion as written (identical) is unmeetable
   on a self-checkpointing bank; the improved attribute-drift rule is stated in §1.
2. **Probe report** — this file (`p0a-report.md`, worktree root): parameters §2, coverage
   §3, pair stats + full band distribution §3, query impact + intervals §4, D2 readout §5,
   verdict from the pre-registered rule (≥10% faithful-hit AND P0b signal): my conjunct
   FAILS (0/60 logged, upper bound 6.0% < 10%; re-served 1/60, upper 8.9% < 10%) → **NO-GO**;
   P0b conjunct (sibling lane, not done) moot and recorded as such.
3. **Guard tests RED-first** — `BankCopyProbe_LivePath_RefusesToOpen` + companion
   `BankCopyProbe_StorePath_RefusesOutsideCopy` (+ `…_AcceptsLaneCopy`), in
   `p0a-harness/P0aHarness.Tests/`; RED output saved at `p0a-scan/red-output.txt`
   (`should throw InvalidOperationException but did not`, 2 failed / 1 passed pre-fix);
   GREEN 6/6 after `BankPaths` allowlist (`dotnet test`, MTP). Every harness entry resolves
   via `MMR_BANK_COPY` and aborts unless under `/tmp/mmr-bank-copy/p0a/` (literal-prefix
   match; refuses live path, sibling `p0b`, parent dir, relative, unset) — the C# gate in
   `p0a-harness/P0aHarness/BankPaths.cs` and its Python twin in `p0a-scan/bankpath.py`
   (verified: unset/live-path/sibling-lane all abort); all serves/collapses/scans ran with
   `MMR_BANK_COPY=/tmp/mmr-bank-copy/p0a/memory.db`.
4. **Drift guard** — option (a): the probe links the production assemblies and calls the
   shipped statics (`ModalityCandidates.ByBm25/ByCosine`, `ReciprocalRankFusion.Fuse`,
   `SearchResultMerger.Merge`, `StructureFusion`, FTS planner, production SQL consts);
   row-mapping/context lines are quoted verbatim in-report (§7). Pins:
   `CollapseMatchesPipelineDedupTests` (project-wins-over-shared, same-tier-score-wins,
   distinct-survive, 3/3 green) + serve-vs-log reproduction (§7).
5. **Reproducibility** — deterministic sample (seed 20260908; re-run byte-identical),
   pinned code (this worktree, base 95b612c2 + untracked `p0a-*/` only); counting step run
   twice, outputs byte-identical (`cmp` clean). Serve is single-invocation deterministic
   (top-8 == top-16 prefix on all 60).

## 7. Validation evidence (why the numbers are trusted)

- **Serve reproduction**: probe query id 1535 re-served 8/8 logged hashes (7/8 identical
  order; the tail swap is two logged-top-10 members exchanging rank 8). Global overlap
  median 5/8; low-overlap cases are drift-PROVEN (re-served tops dominated by rows with
  `created_at` after the logged serve; logged rows intact) — the bank ingests daily, the
  harness serves today's bank faithfully. Zero path queries; both legs queried on all 60;
  contexts 3–18 per project (shared + project + custom labels, via production
  `SearchContexts`); no-regression path asserted off (bank flag `false`).
- **Cosine cross-check**: harness-vs-numpy max diff 7.8e-16 over 20 pairs (adjacent-served
  + random), `p0a-scan/coscheck.py` PASS.
- **Quoted mirrors** (production line shapes, verified at base 95b612c2): FTS map
  (`new MemorySearchResult(row.Hash, row.Ranking, row.Path, string.Empty, row.SourceFile,
  row.ChunkIndex, row.TotalChunks)`), dual-vector map (same with fused `Score`), fusion
  `(legs, RrfK, 0, int.MaxValue)`, merge-then-floor-then-`Take(limit)`, fallback rule
  (`plan.Fallback is null || count > Max(TokenCount, Limit)`), `CopyDefaults` mirrors the
  store's `SettingsBackedSearchParameters` parser-for-parser.
- **Cheaper faithful scan taken; rejected**: (i) full-DI `SqliteMemoryStore` re-serve —
  12-dependency construction + write side-effects on the copy for a context-only readout;
  (ii) Python ONNX query embedding — query-preprocessing drift on the PRIMARY numerator;
  (iii) FTS-only re-serve — drops the vector leg, unfaithful. Taken: log-faithful primary
  (served lists are ground truth, no model, no rerun) + production-statics re-serve for D2.

## 8. Deviations from the protocol (with reasons)

- **D-1** receipts (§1): hot-bank attribute-drift rule replaces the identical-receipt abort.
- **D-2** primary shape top-8, not top-10: ADR-0096 ships limit 8; top-10 reported alongside
  (identical numbers: 0/60 both). GO rule applied at the served shape; protocol-compat readout
  included so the verdict is shape-robust.
- **D-3** union-collapse approximation: production dedups per leg pre-fusion; the probe
  collapses the served union with the production grouping + winner rule (served scores as
  the within-group order). Effect measured: zero — no served list contained a collapsible
  group, so raw == faithful is exact here, not approximate.
- **D-4** model weights read from `/Users/arasz/.ai-raccoon/models/` (outside worktree,
  read-only inference input the production path itself requires; G-IRON covers the bank).
- **D-5** pair cosine uses content vectors only — the §1.18 reference signal by design.

## 9. Hypotheses with settlers

- H-blocktok (my tokenizer ≈ FTS5 unicode61 for blocks): settled by the coverage gate —
  83.1% ≥ 80%; all reported cosines exact regardless of blocking.
- H-sidecar-order (sidecar array = served order): settled by writer comment ("joined to
  the served rows in served order") + 8/8 reproduction.
- H-collapse-scores (sidecar strength as within-group order): settled as approximation with
  measured-zero effect (D-3).
- H-norms (blobs unit length): REJECTED by measurement (norms ≈23.6) — cosine divides by
  norms; vec0 `distance` semantics unaffected (KNN normalizes internally).
- H-serve (harness == production): settled by 8/8 reproduction + all-production-statics
  construction + median-5/8 global overlap with drift attribution for the tail.
- H-power (n=60 decides): settled — Wilson upper bounds (6.0% logged, 8.9% re-served)
  exclude the 10% bar, so NO-GO is not an underpowered fluke.

## 10. Forward notes (not findings)

- The 0.85–0.95 band holds the corpus's real redundancy (archive mirrors, instruction-file
  mirrors). If a future protocol re-registers the band, today's numbers are §4 + per-query
  detail in `p0a-scan/impact.json` (`crossPairs85`, `concentration85`).
- Settler for any revival: a P4 pilot at the re-registered threshold must show paired
  separation — this probe says the ≥0.95 regime has nothing to separate.
- Live-bank note for later lanes: this bank self-checkpoints roughly every minute and
  ingests daily — any lane needing stillness must re-derive D-1 or work from a fresh copy.
