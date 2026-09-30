# 254 vs 1022 chunk budget: #809 measurement report

Date: 2026-09-30. Task: `air-254-vs-1022-retrieval-measurement-809` (issue #809), step `s-report-docs`.

This is the one consolidation for the task's measured numbers. It closes issue #809's three items:
the retrieval A/B (item 1), the WebGPU footprint at the 1022 budget (item 2) and the re-chunk
phase wall-clock (item 3), plus the stamp-gate outcome the plan records regardless of branch
(PR-1.R5). Two owner-review routes stay open at the end; a below-floor or over-threshold
measurement is an outcome, not a failure of the work (PR-2.R15).

## 0. Reading rules

**Grades force binary.** `MEASURED` means read from an artifact on disk (JSON row, log line, bank
metric, file diff) or arithmetic on measured inputs. `INFERRED` means derived by reasoning over
measured or read inputs, not directly observed. Where a source note itself grades a claim
differently, the report carries the note's weaker grade.

**Provenance boundaries are absolute.** Three labels apply:

- `LIVE`: read from the live bank `~/.ai-raccoon/memory.db`, the owner paste, or the
  owner-initiated live restart. Never written by this task.
- `COPY-RUN`: produced on a disposable clone or copy root (root A at `/tmp/aira-rechunk-measure`,
  copy-1022 at `/tmp/aira-copy1022-converge`, the A/B arms under `$HOME/ai-raccoon-eval/arms`).
- `SCRATCH`: produced on fresh `/tmp` roots that are not a bank clone (the WebGPU benchmark).

Live and copy-run numbers never mix across a table boundary. The one declared cross-boundary
figure is the scan-only ratio in block b.2, which is labelled as copy-run over a live anchor.

**Artifact paths are named for every number.** The full path appears where the number first
lands; the spot audit in section 6 repeats paths, commands and outputs for 15 of them.

---

## (a) LIVE block

Every number in this section describes the live bank. Nothing here is a copy-run result.

**a.1 The live 2026-09-29 migration: re-chunk counts and embed drain.** The counts come from the
preserved owner paste
`.ai-badger/task-tracking/air-254-vs-1022-retrieval-measurement-809/migration-log-pasted-2026-09-29.txt`
(event 448). The paste carries no wall-clock token on that line, and its provenance note marks the
source terminal location `UNVERIFIED`; grade as stated per number below.

| number | value | artifact | grade |
|---|---|---|---|
| live event 448 counts (pass 1) | 783 note + 3009 mirror groups re-chunked, 1702 unchanged, 14 retryable, 0 unprovable, 1 terminal | preserved paste (§CAPTURE 1), event 448 | MEASURED by owner; source log location UNVERIFIED |
| live pass 1 whole-pass job | 1,927,976.4253 ms | bank metrics `job.model-migration.duration_ms`, recorded in `.ai-badger/task-tracking/air-254-vs-1022-retrieval-measurement-809/d-30-full-report.md` F5 | MEASURED |
| live pass 1 drain | 1,927,826.9008 ms over 19,604 rows | bank metrics `drain.memory.duration_ms` / `drain.memory.rows`, `d-30-full-report.md` F5 | MEASURED |
| live pass 1 outage window | ≈ 12:33:54 to 13:06:09 CEST, about 32 min 15 s | `model_migration` row + metric anchors, `d-30-full-report.md` F8 | MEASURED |
| live pass 2 window | 13:06:26 to 13:07:48 CEST | `model_migration` row, `d-30-full-report.md` F6/F8 | MEASURED |
| live pass 2 job / drain | 82,267.5515 ms job, 82,267.32 ms drain, 0 rows | bank metrics rows, `d-30-full-report.md` F6 | MEASURED |
| live total refused time | about 33.6 min (32 min 15 s + 82 s) | `d-30-full-report.md` F8 | MEASURED windows, arithmetic |
| live in-window refusal observed | one refusal at ≈13:07:22 CEST inside pass 2; calls passed after the close | `d-30-full-report.md` F10 | MEASURED |
| live refusal granularity | not captured; the two paste 910 lines sit in a ≤ 60.9 s bracket with no timestamp of their own | `d-30-full-report.md` F11/F17 | bracket INFERRED; missing sink MEASURED |

The pass 1 figure rests on the job-metric `recorded_at` anchor plus subtraction, so it is ± about
8 s rather than exact (`d-30-full-report.md`, "Still open: first-edge precision"). The ≈305 s
re-chunk-only share of pass 1 is INFERRED from the paste's first 1013 stride minus the 60 s
progress stride (F3/F4), and the report repeats that grade rather than promoting it.

**a.2 The live 2026-09-30 stamp event (opportunistic, fired).** The owner-initiated restart ran
the third unproven pass and wrote the stamp. Source:
`docs/work/2026-09-30-live-stamp-event-capture.md` (read-only probes only).

| number | value | artifact | grade |
|---|---|---|---|
| live stamp window | 2026-09-30T15:13:22Z to 15:14:55Z, 93.0 s | `model_migration` row pasted in `docs/work/2026-09-30-live-stamp-event-capture.md` | MEASURED |
| live job duration | 92,831.2693 ms | live bank `job.model-migration.duration_ms`, same note | MEASURED |
| attempt progression | `retryAttempts` `1022:2` (09:06:10Z) to `1022:3` + `embedding.chunkBudget=1022` stamp (probe 15:41:24Z) | same note, before/after probe blocks | MEASURED |
| in-window write-refusal timestamps | NOT CAPTURED (window closed before the trigger reached the orchestrator; no live write was attempted) | same note, "Refusal window" | labelled gap; refusal behaviour INFERRED from the bank rows plus the root A refusal character |

**a.3 Stamp-gate outcome and disposition (PR-1.R5).** The gate went **FALLBACK**, not GATED and
not STAMPED. Source: `docs/work/2026-09-30-stamp-gate-decision.md`; disposition artifact:
`docs/work/2026-09-30-copy-1022.pin.json`.

- Probe at 2026-09-30T09:06:10Z on the live bank: `embedding.chunkBudget` absent (0 rows),
  `embedding.chunkBudget.retryAttempts|1022:2`, 0 entries not embedded. MEASURED.
- Why FALLBACK: an un-stamped source is exactly what the fallback branch exists for. STAMPED
  would have required the stamp on arrival; GATED had no evidence that disposable convergence
  was impossible.
- Disposition (run in the COPY-RUN block): copy-1022 was converged on the disposable root
  `/tmp/aira-copy1022-converge` until the ceiling stamp appeared, then pinned. The final artifact
  is `d23ee28e52d181a65786e41f7e2d39f3aa7fc67dd17a8202e2675026c076b4eb`, 21,765 entries,
  `embedding.chunkBudget=1022`, `retryAttempts=1022:3`. MEASURED.
- No downstream step was skipped. The GATED branch's skip path (PR-1.R5) was not needed; it stays
  documented in the gate note for any future run where the gate fires.

---

## (b) COPY-RUN block

Every number in this section was produced on a disposable clone or copy. Nothing here is a live
observation. The live counts and windows are in block (a) and are not repeated here.

**b.1 Root A, full re-chunk run.** Sources: `docs/work/2026-09-30-re-chunk-phase-measurement.md`
and the raw log `/tmp/aira-rechunk-measure/quiet.log` (present at report time, 913,879 bytes,
mtime Sep 30 12:27; three 448 lines, 243 refusal lines).

| number | value | artifact | grade |
|---|---|---|---|
| phase 1 elapsed | 00:05:11.4600913 (311.46 s) | quiet.log, event 448 at 09:43:25.6452710Z | MEASURED |
| phase 1 counts | 764 note + 1,043 mirror re-chunked, 3,684 unchanged, 13 retryable, 0 unprovable, 1 terminal | quiet.log event 448 | MEASURED |
| phase 2 elapsed | 00:03:35.2905774 (215.29 s) | quiet.log event 448 at 10:06:02.9997020Z | MEASURED |
| phase 2 counts | 0 + 0 re-chunked, 5,491 unchanged, 13 retryable, 0 unprovable, 1 terminal | quiet.log event 448 | MEASURED |
| outage window 1 | 09:38:06Z to 10:02:06Z, 1,440 s (24 min 00 s) | `model_migration` read-only polls, measurement note headline table | MEASURED |
| outage gap | 21 s available (probe at 10:02:25Z succeeded) | measurement note, AC4 | MEASURED |
| outage window 2 | 10:02:27Z to 10:06:02Z, 215 s (3 min 35 s) | final `model_migration` row | MEASURED |
| refusal coverage | first 09:40:44.4558880Z, last 10:04:55.2215140Z | quiet.log 910 lines, measurement note (10 refusal lines) | MEASURED |
| phase metrics rows | `chunk.rechunk.duration_ms` 311,460.0913 ms, `chunk.rechunk.groups` 1,807 | bank metrics rows recorded in the measurement note AC5 | MEASURED |
| embed drain | 44,107 owed, 21,757 embedded, 23m52.17s | quiet.log + `drain.memory.duration_ms` 1,432,172.6063 ms, measurement note | MEASURED |
| root entries | 21,757 after the run (from 40,855 before the migration) | read-only probes, measurement note | MEASURED |

The root's first migration took the engine-change path (all rows re-embedded), so the drain window
is embedding-dominated and the phase is 5m11s of a 24m00s outage. The corpus had already been
partly re-chunked once before event 448 (3,684 of 5,505 groups unchanged), which is the named
confound on the phase time. That split between the watch re-ingest and
`chunk-boundary-repair-v2` is a HYPOTHESIS, not verified per file.

**b.2 Root A, scan-only run.** Source: `docs/work/2026-09-30-re-chunk-scan-only-measurement.md`
and the pass-2 section of the same `quiet.log` (marker at line 4730).

| number | value | artifact | grade |
|---|---|---|---|
| scan-only elapsed | 00:08:16.0222926 (496.02 s) | quiet.log pass-2 event 448 at 10:27:08.7803070Z | MEASURED |
| scan-only counts | 0 note + 0 mirror re-chunked, 5,491 unchanged, 0 retryable, 13 unprovable, 1 terminal | quiet.log pass-2 event 448 | MEASURED |
| copy-run over live anchor | 496.0222926 / 82.267 = 6.0294x, +413.755 s | cross-boundary arithmetic: COPY-RUN numerator (quiet.log) over LIVE anchor `job.model-migration.duration_ms` 82,267.5515 ms (`d-30-full-report.md` F6) | MEASURED arithmetic, boundary declared |
| scan-only window | 10:18:52Z to 10:27:08Z, 496 s; refusals 10:18:51Z to 10:27:07Z | `model_migration` row + quiet.log + probe loop | MEASURED |
| refusal count in window | 233 refusals, then first success 10:27:09Z | `probe-pass2-loop.txt` (240 records), scan-only note | MEASURED |
| closing state | 13 groups unprovable, `retryAttempts 1022:2` to `1022:3`, stamp `embedding.chunkBudget=1022` written | quiet.log 448 line + settings probes | MEASURED |
| entries | 21,757 before and after | read-only probes, scan-only note | MEASURED |

Pass 2 is 2.30x root A's own scan-only phase (496 s vs 215 s). Two labelled HYPOTHESES cover most
of it: cold page cache in a fresh process, and startup-time concurrency (the migration opened
1.8 s after the server started listening). Neither is instrumented. Code-corpus contention is
excluded: the Code pump resumed at 10:27:08.802, after the migration closed.

**b.3 Copy-1022 FALLBACK convergence (the gate's disposition run).** Source:
`docs/work/2026-09-30-copy-1022.pin.json`, `convergence` block. This ran on the disposable root
`/tmp/aira-copy1022-converge`, never on the live bank.

| number | value | artifact | grade |
|---|---|---|---|
| convergence pass | 0 + 0 re-chunked, 5,501 unchanged, 0 retryable, 14 unprovable, 1 terminal, in 00:02:43.8813227 | `docs/work/2026-09-30-copy-1022.pin.json`, `convergence.passes[0].event448` | MEASURED |
| convergence window | 2026-09-30T09:17:13Z to 09:19:57Z | pin json `convergence.migration` | MEASURED |
| refusal window | first 09:17:29Z, last 09:19:52Z, closed 09:19:57Z; 25 refused / 10 ok probes | pin json `convergence.refusalWindow` | MEASURED |
| stamp after convergence | `embedding.chunkBudget=1022`, `retryAttempts=1022:3` at 09:20:01Z | pin json `convergence.stampProbe.after` | MEASURED |
| final copy | sha256 `d23ee28e52d181a65786e41f7e2d39f3aa7fc67dd17a8202e2675026c076b4eb`, 21,765 entries, journal mode delete | pin json top level | MEASURED |

The inherited retry counter made this the budget's third pass: the copy arrived at `1022:2`, the
convergence pass hit `MaxRetryAttempts`, reclassified the 14 residual groups unprovable, and the
gate wrote the stamp anyway (ADR-0125). The pin's `branch` field reads `FALLBACK`.

---

## 1. Retrieval A/B: 254 vs 1022 (COPY-RUN arms)

The arms are scratch clones, so the whole section is COPY-RUN. Full record:
`docs/work/2026-09-30-254-vs-1022-retrieval-ab.md`; artifacts under
`docs/work/2026-09-30-254-vs-1022-retrieval-ab/`. arm-1022 is the pinned copy-1022 clone
(`d23ee28e…76b4eb`); arm-254 is the same population re-chunked and re-embedded at 254 (distinct
engine fingerprint by construction, PR-3.R6). Both arms hold 5,514 distinct paths.

### 1.1 Objective metrics (three byte-identical repeats, spread 0.0)

Source: `docs/work/2026-09-30-254-vs-1022-retrieval-ab/metrics.json`.

| metric | chunk254 | chunk1022 | delta | grade |
|---|---|---|---|---|
| nDCG@5 | 0.4038 | 0.5848 | +0.1811 for 1022 | MEASURED |
| MRR@5 | 0.3652 | 0.5267 | +0.1615 for 1022 | MEASURED |
| hit@3 | 0.4800 | 0.6500 | +0.1700 for 1022 | MEASURED |
| hit@1 | 0.2800 | 0.3900 | +0.1100 for 1022 | MEASURED |

The means come from the `spread` block (`0.4037681350120576` and `0.5848226968818493` for nDCG@5)
and every range and stddev is 0.0. The largest per-query gap is 7 queries at 0.0000 on 254
against 1.0000 on 1022 (E002, E007, E029, E044, E048, E050, E073).

### 1.2 Paired instrument (reported separately, never merged)

Source: `docs/work/2026-09-30-254-vs-1022-retrieval-ab/paired.json`.

| number | value | grade |
|---|---|---|
| mean top-8 set overlap | 0.0350 | MEASURED |
| mean RBO (p=0.9) | 0.0190 | MEASURED |
| queries changed / unchanged | 100 / 0 | MEASURED |
| total drops / backfills | 772 / 769 | MEASURED |

Near-zero hash overlap is by construction: the budget changes chunk boundaries, so identical
content carries different hashes. The file marks the instrument
`reportedSeparately: never merged with the objective metrics or the blind grader`.

### 1.3 Blind pairwise grader (reported separately, never merged)

Source: `docs/work/2026-09-30-254-vs-1022-retrieval-ab/blind/ab-results.json`.

| number | value | grade |
|---|---|---|
| picks for chunk254 / chunk1022 | 24 / 3 of 27 calls | MEASURED |
| mean compScore | 0.8889 (min 0.6667, max 1.0) | MEASURED |
| graders | grader-1 8-1, grader-2 7-2, grader-3 9-0, all for 254 | MEASURED |
| abstentions / re-asks | 0 / 0 | MEASURED |
| position randomisation | 254 first in 4 queries, 1022 first in 5; 254 won each way | MEASURED |

Sample limits carried: 9 graded queries out of 10 picked, all `changed` (no control stratum), so
the grader speaks only to the reordered subset. The instrument split (anchors favour 1022, blind
preference favours 254) is left as a split; section 4 hands it to the owner.

### 1.4 Regressions and the G4 floor

Sources: `docs/work/2026-09-30-254-vs-1022-retrieval-ab/metrics.json` and
`docs/work/2026-09-30-254-vs-1022-retrieval-ab/regressions-both-ways.md`.

| number | value | grade |
|---|---|---|
| 254-side regressions (1022 higher) | 43 | MEASURED |
| 1022-side regressions (254 higher) | 14 | MEASURED |
| owner flag | true, both sides above the 5-regression threshold | MEASURED |
| drift-confounded regressions | 0 (checked independently 3 ways) | MEASURED |
| G4 floor (mean nDCG@5 ≥ 0.5) on 254 | below on all 3 repeats (0.4038 each); `ownerReviewRequired: true` | MEASURED |
| G4 floor on 1022 | passes all 3 repeats (0.5848 each) | MEASURED |

### 1.5 Shared-tier structural artifact

Queries E090-E100 (shared scope) score 0.0 in both arms because both serve a project-scope twin
row with a different hash and identical content, and the shared-scope anchor hash is never served.
Source: `docs/work/2026-09-30-254-vs-1022-retrieval-ab/shared-tier-artifact.json`. MEASURED. The
11 zero scores depress both arms by the same denominator, so they do not change the comparison's
sign.

---

## 2. WebGPU footprint at 1022 (SCRATCH-RUN)

Issue item 2 falsification against F12. Fresh per-run scratch roots under
`/tmp/aira-254-vs-1022-webgpu/20260930-164226/runs/`; the live bank and port 7721 were never
written. Sources: `docs/work/device-benchmark/2026-09-30-m4-webgpu-1022/result.json` and
`docs/work/device-benchmark/2026-09-30-m4-webgpu-1022/findings.md`.

**Verdict: SURVIVES against F12.** The pre-registered rule falsifies the "footprint is fine"
claim only if the auto (WebGPU) median peak exceeds either threshold, and neither holds:

| test | arithmetic | result | grade |
|---|---|---|---|
| threshold 1 | auto median 2,662,883 KiB vs 2 x 254-era anchor 2,068,770 = 4,137,540 KiB | 2,662,883 ≤ 4,137,540 (64% of threshold) | inputs MEASURED; comparison arithmetic |
| threshold 2 | auto median 2,662,883 KiB vs 2 x same-run cpu median 2,357,555 = 4,715,110 KiB | 2,662,883 ≤ 4,715,110 (56% of threshold) | inputs MEASURED; comparison arithmetic |
| worst auto run | 2,675,475 KiB vs 4,137,540 and 2 x cpu max 2,552,899 = 5,105,798 | both hold | MEASURED |
| adversarial variant | auto max 2,675,475 vs 2 x cpu min 2,220,051 = 4,440,102 | holds | MEASURED |

The 254-era anchor comes from `docs/work/device-benchmark/2026-09-26-m4/result.json`. The median
delta against it is +28.7%, bounded growth, not the MLX per-shape curve that reached 10.6-17.9 GB
at these lengths.

All six runs are `status: ok`; auto peaks are 2,675,475 / 2,662,883 / 2,350,867 KiB and cpu peaks
2,357,555 / 2,552,899 / 2,220,051 KiB; neural peak is 0 for all. Per-run values are in
`docs/work/device-benchmark/2026-09-30-m4-webgpu-1022/result.json` under `.runs[]`. MEASURED.
A HYPOTHESIS, not needed for the verdict, attributes the monotone auto wall-time decline
(100.5 to 78.4 to 62.0 s) to WebGPU shader cache warming.

**The pre-registered AC4 jq check is vacuous.** As written it compares summary objects to numbers
(`.summary.devices.auto.phys_footprint_peak_kib as $a | $a > 4137540`), and jq's type ordering
makes any object compare greater than any number. It returns true even for a hypothetical auto
median of `99,999,999 KiB`, which the findings note demonstrated. MEASURED (mutation). The verdict
above uses the corrected numeric form on the `.median` fields. The residual is named: this is a
whole-process peak, not WebGPU per-shape buffer attribution, and no 254-era bank was upgraded
under `auto`.

---

## 3. Eval re-anchor (COPY-RUN)

Source of record: `docs/work/2026-09-30-eval-reanchor-decisions.md`, plus a direct diff of
`scripts/retrieval_tuning/corpora/eval-set-100.json` against its pre-rebase commit (this step ran
that diff; see the spot audit). The copy is the FALLBACK-converged pin `d23ee28e…76b4eb`.

| number | value | artifact | grade |
|---|---|---|---|
| query text changes | 0 of 100 | eval-set-100.json diff vs `ab5b789c^`; `d-30`/re-anchor note | MEASURED |
| `expectedHash` changes | 93 | same diff | MEASURED |
| `expectedSource` changes | 58 | same diff | MEASURED |
| `answerSpan` changes | 60 | same diff | MEASURED |
| other fields changed | 0 | same diff | MEASURED |
| snapshot sha | `d23ee28e…76b4eb`, equal to the pin | eval-set header + pin json | MEASURED |
| ADR family resolution | 75/75 resolve; 74/75 keep their old target slug | re-anchor note, Decision 1 | MEASURED |
| E026 anchor drift | family-first-match picks the merged Context+Decision-1 chunk | re-anchor note, Known drift; `docs/work/2026-09-30-254-vs-1022-retrieval-ab.md` 5(d) | MEASURED |
| P3 legacy parity | skip with a copy-conditioned reason; no copy both generators process exists | re-anchor note, Decision 3 | MEASURED |
| hermes markers | 25/25 resolve to exactly one row | re-anchor note, Decision 2 | MEASURED |

The re-anchor is generator output, not hand edits; a fresh generator run is byte-identical. The
rowid stability behind `ORDER BY chunk_index` ties is a HYPOTHESIS in the source note.

---

## 4. Owner-review items (carry to the owner)

These are the two routes the measurement leaves open. Both are outcomes; neither makes the work
incorrect (PR-2.R15).

1. **Regression asymmetry, 43 vs 14, both above the 5-regression threshold.** The 254 arm has
   43 queries where 1022 scores higher; 1022 has 14 where 254 scores higher. Neither side is
   drift-confounded (0 flagged). The owner decides whether the asymmetry is acceptable at the
   1022 budget.
2. **The 254-token arm is below the G4 floor on all three repeats** (mean nDCG@5 0.4038 against
   the 0.5 floor); 1022 passes. A below-floor outcome routes to owner review exactly like the
   regression flag.
3. **Instrument split** (carried with them): the objective anchors favor 1022, the blind pairwise
   grader favors 254 on a 9-query sample. The two answer different questions, so neither cancels
   the other. A larger blind sample or a control stratum would be needed to settle it.

A below-threshold or below-floor result from a correctly run measurement is evidence, not a
defect. Nothing in this task's scope adjudicates the budget choice.

---

## 5. Residual confounds and hypotheses

Carried from `docs/work/2026-09-30-254-vs-1022-retrieval-ab.md` section 5 (AC5), with grades.

- (a) **Distinct fingerprint re-embed, by construction (PR-3.R6).** arm-254's manifest edit
  forces `MarkAllEmbeddedPending`; its fingerprint is
  `local:…arm-254/models/granite-embedding-small-english-r2#777268da…` against arm-1022's
  `local:bundled#ef600cb9…`. The fingerprint delta is part of the treatment. MEASURED.
- (b) **Two-path disk drift.** `docs/reference/extension-catalog.md` and
  `docs/howto/update-integrations.md` changed on disk at 11:34Z mid-re-chunk (2 of 5,514 paths).
  The drift check found 0 confounded regressions in either direction. MEASURED.
- (c) **G6: 32 migration tombstones for hashes absent from COPY1022. HYPOTHESIS:
  retry-window intermediate chunks.** 35 `sync_tombstones` rows written by arm-254 between 13:00Z
  and 14:00Z, 32 without a COPY1022 twin. The mechanism is consistent with retry bookkeeping but
  was not re-derived. HYPOTHESIS.
- (d) **E026 anchor drift.** The generator's family-first match picks a merged
  `Context | Decision 1` chunk over the old `Decision 2` chunk for one of 75 ADR queries; the
  anchor is regenerated per arm, not hand-edited. MEASURED.
- (e) **Query trim follows the budget by design** (ADR-0071 amendment 2026-09-23, ADR-0108
  decision 5): 254 trims queries at 254 tokens, 1022 at 1022. The query effect is part of the
  treatment and was not measured separately. MEASURED (code/ADR read).
- (f) Root A scan pass 2 at 496 s against pass 1's 215 s scan-only window: cold page cache and
  startup concurrency. HYPOTHESIS (source note).
- (g) Root A drain throughput decay of about 30% across the drain, and the root A entry drop
  split between watcher re-ingest and `chunk-boundary-repair-v2`. HYPOTHESIS (source note).
- (h) The live stamp event's in-window refusal behaviour is INFERRED from the bank rows plus the
  refusal-window character measured on root A; the timestamps themselves are NOT CAPTURED.

---

## 6. Spot audit: artifact path and grade for 15 numbers

Commands were run from the worktree root on 2026-09-30. `D` is
`docs/work/2026-09-30-254-vs-1022-retrieval-ab`, `W` is
`docs/work/device-benchmark/2026-09-30-m4-webgpu-1022`, `P` is the preserved paste path at the
repo task-tracking root, `L` is `/tmp/aira-rechunk-measure/quiet.log`. The audit crosses the
provenance boundaries on purpose: each row carries its own artifact and grade, no row attributes a
copy-run value to the live bank or the reverse, and row 11 declares its cross-boundary arithmetic.

| # | number | command (abridged) | output (abridged) | artifact | grade |
|---|---|---|---|---|---|
| 1 | nDCG@5 254 / 1022 | `jq -c '.spread.chunk254.ndcg5.values[0], .spread.chunk1022.ndcg5.values[0]' $D/metrics.json` | `0.4037681350120576`, `0.5848226968818493` | metrics.json | MEASURED |
| 2 | regressions 43 / 14 | `jq -c '.regressions' $D/metrics.json` | `{"forChunk254":43,"forChunk1022":14,"ownerReviewFlagged":true}` | metrics.json | MEASURED |
| 3 | G4 floor 254 / 1022 | `jq -c '{pass254:.floor.evaluated.chunk254.pass, pass1022:.floor.evaluated.chunk1022.pass}' $D/metrics.json` | `{"pass254":false,"pass1022":true}` | metrics.json | MEASURED |
| 4 | blind picks 24 / 3 | `python3`: count `pickArm` over `$D/blind/ab-results.json` | `picks 27 chunk254 24 chunk1022 3; mean compScore 0.8888888888888888; abstentions 0` | blind/ab-results.json | MEASURED |
| 5 | paired overlap / RBO | `jq -c '.aggregate' $D/paired.json` | `{"meanTop8SetOverlap":0.035,"meanRbo":0.01901170157785714,"queriesChanged":100,"queriesUnchanged":0,"totalDrops":772,"totalBackfills":769}` | paired.json | MEASURED |
| 6 | WebGPU auto / cpu median | `jq -c '{auto:.summary.devices.auto.phys_footprint_peak_kib, cpu:.summary.devices.cpu.phys_footprint_peak_kib}' $W/result.json` | auto `{"median":2662883,"min":2350867,"max":2675475}`, cpu `{"median":2357555,...}` | result.json | MEASURED |
| 7 | WebGPU corrected F12 check | `jq` on `.median` fields vs `4137540` / `2*$c` | `falsified_by_anchor:false, falsified_by_cpu:false, verdict:"SURVIVES"`, cpu_med_2x `4715110` | result.json + findings.md | MEASURED inputs, arithmetic verdict |
| 8 | live pass 1 whole-pass job | live-bank metric read recorded in `d-30-full-report.md` F5 (`SELECT … FROM metrics WHERE name='job.model-migration.duration_ms'`) | `1927976.4253` ms job; `1927826.9008` ms / 19,604 rows drain | `d-30-full-report.md` F5 (bank `metrics` rows) | MEASURED |
| 9 | root A phase 1 | `python3` scan of `$L` for `Chunk-budget rebudget` | `764 note ... 1043 mirror ..., 3684 unchanged, 13 retryable, 0 unprovable, 1 terminal in 00:05:11.4600913` | quiet.log | MEASURED |
| 10 | root A scan-only | same scan, pass-2 line | `0 note ..., 5491 unchanged, 0 retryable, 13 unprovable, 1 terminal in 00:08:16.0222926` | quiet.log | MEASURED |
| 11 | scan-only ratio 6.03x | `496.0222926 / 82.267` | `6.0294`, +413.755 s | quiet.log over live anchor (`d-30-full-report.md` F6) | MEASURED arithmetic, cross-boundary labelled |
| 12 | copy-1022 convergence | `jq -c '.convergence.passes[0], .convergence.refusalWindow' docs/work/2026-09-30-copy-1022.pin.json` | `0 note ..., 5501 unchanged, 0 retryable, 14 unprovable, 1 terminal in 00:02:43.8813227`; first `09:17:29Z` last `09:19:52Z` | copy-1022.pin.json | MEASURED |
| 13 | eval re-anchor counts | `python3` diff of `git show ab5b789c^:scripts/retrieval_tuning/corpora/eval-set-100.json` vs working tree | `changed fields: {'answerSpan':60,'expectedHash':93,'expectedSource':58}; query changes: 0; snapshotSha256 new: d23ee28e…76b4eb` | eval-set-100.json | MEASURED |
| 14 | live stamp window | probe block in `docs/work/2026-09-30-live-stamp-event-capture.md` | `2026-09-30 15:13:22` to `15:14:55`; `job.model-migration.duration_ms 92831.2693` | live-stamp-event-capture.md | MEASURED |
| 15 | stamp-gate probe | probe block in `docs/work/2026-09-30-stamp-gate-decision.md` | `probe_time_utc: 2026-09-30T09:06:10Z`; `embedding.chunkBudget.retryAttempts|1022:2`; stamp count `0` | stamp-gate-decision.md | MEASURED |

Every other number in this report carries its path and grade inline in its table. No number is
reported without a source.

---

## 7. Artifact and pin index

| artifact | role |
|---|---|
| `.ai-badger/task-tracking/air-254-vs-1022-retrieval-measurement-809/migration-log-pasted-2026-09-29.txt` | LIVE owner paste, event 448 counts |
| `.ai-badger/task-tracking/air-254-vs-1022-retrieval-measurement-809/d-30-full-report.md` | LIVE bank-metric recovery (F5/F6/F8/F10/F11/F17) |
| `docs/work/2026-09-30-stamp-gate-decision.md` | stamp-gate probe and FALLBACK decision |
| `docs/work/2026-09-30-live-stamp-event-capture.md` | LIVE attempt-3 stamp event |
| `docs/work/2026-09-30-copy-1022.pin.json` | FALLBACK-converged copy pin (`d23ee28e…76b4eb`) |
| `docs/work/2026-09-30-re-chunk-phase-measurement.md` | COPY-RUN root A full run |
| `docs/work/2026-09-30-re-chunk-scan-only-measurement.md` | COPY-RUN root A scan-only run |
| `/tmp/aira-rechunk-measure/quiet.log` | root A raw log (three 448 lines, 243 refusal lines) |
| `docs/work/2026-09-30-254-vs-1022-retrieval-ab.md` + `…-ab/` | COPY-RUN arms, metrics, paired, blind, regressions |
| `docs/work/device-benchmark/2026-09-30-m4-webgpu-1022/` | SCRATCH-RUN WebGPU result and findings |
| `docs/work/device-benchmark/2026-09-26-m4/result.json` | 254-era footprint anchor |
| `docs/work/2026-09-30-eval-reanchor-decisions.md` + `scripts/retrieval_tuning/corpora/eval-set-100.json` | COPY-RUN eval re-anchor |

Build pins carried from the A/B record: VERSION `1.55.0`, source commit
`7dc285f1cad921459b6343a4c8b500f42fcc33ae`, apphost sha256
`86d3fee11755c3ae6b89b1fd97bcdcf7a4cd6dfa6f5ca9993a1283a368673ed3`. Weights pin:
`ibm-granite/granite-embedding-small-english-r2` at revision
`2ab6fa8ea2d674564defd37171ae19079b864b33`. MEASURED (A/B record section 2).

## 8. Version bump and what this step rejected

**Version bump: `1.55.0` to `1.55.1`, a patch bump**, applied through `scripts/version-bump.py patch`
so the single hand-written version marker moved with its drift check. Rationale: this change set is
documentation plus measurement/eval pins only. No runtime behaviour changes, no new feature, no
breaking change; the instrumentation feature that this measurement exercised already shipped in
1.55.0 (PR #811), and the eval re-anchor is the follow-up 1.54.0 named. Patch is the only accurate
semver level for a no-behaviour-change release.

- **A second changelog name.** PR-1.R6 flagged two conflicting patterns
  (`{version}-809-measurement.md` vs `<version>-<slug>.md`). One canonical name is used:
  `docs/changelog/1.55.1-809-measurement.md`.
- **A minor or major version bump.** No user-visible feature and no breaking change: this change
  set is docs plus measurement pins only, and the instrumentation feature already shipped in
  1.55.0. Patch `1.55.1` is the accurate semantic level.
- **Merging live and copy-run numbers into one table.** PR-1.R8 names exactly this drop path; the
  live counts live only in block (a) and the copy counts only in block (b).
- **Stating the WebGPU verdict as the bare 2,662,883 KiB number.** The verdict is stated against
  F12 with both numeric thresholds (4,137,540 and 4,715,110 KiB).
- **Trusting the pre-registered AC4 jq check.** It is non-discriminating; the corrected numeric
  check is the verdict basis, and the broken one is documented with its mutation.
- **Treating the metric-versus-blind split as resolved.** Both instruments stay visible.
- **Re-running any measurement.** Every number is consumed from the artifact already on disk; a
  re-run would not add evidence and would spend the window again.
- **Counting the 6.03x ratio as a pure copy-run number.** It is labelled copy-run-over-live-anchor
  arithmetic.

## 9. Acceptance criteria evidence

Run from the worktree root after this report, the changelog and the version bump landed.

AC1, report exists with grades and named paths (the spot audit in section 6 is the deep check):

```text
$ test -f docs/work/2026-09-30-809-measurement-report.md && grep -q MEASURED docs/work/2026-09-30-809-measurement-report.md && echo AC1-OK
AC1-OK
```

AC2, WebGPU verdict against F12:

```text
$ grep -qE 'FALSIFIED|SURVIVES' docs/work/2026-09-30-809-measurement-report.md && grep -q '4,137,540' docs/work/2026-09-30-809-measurement-report.md && grep -q '4,715,110' docs/work/2026-09-30-809-measurement-report.md && echo AC2-OK
AC2-OK
```

AC3, live and copy-run separation, with the live counts only in block (a):

```text
$ grep -q '783' docs/work/2026-09-30-809-measurement-report.md && grep -qiE 'live|copy' docs/work/2026-09-30-809-measurement-report.md && echo AC3-OK
AC3-OK
$ python3 -c "t=open('docs/work/2026-09-30-809-measurement-report.md').read(); body=t[:t.index('## 9. Acceptance criteria evidence')]; a=body.index('## (a) LIVE block'); b=body.index('## (b) COPY-RUN block'); print('live-counts-in-A-only:', '783' in body[a:b] and '783' not in body[b:])"
live-counts-in-A-only: True
```

AC4, VERSION bumped, one canonical changelog name, indexed:

```text
$ cat VERSION
1.55.1
$ test -f "docs/changelog/$(cat VERSION)-809-measurement.md" && grep -q "$(cat VERSION)" docs/changelog/README.md && echo AC4-OK
AC4-OK
```

AC5, stamp-gate outcome and disposition recorded (PR-1.R5; gate went FALLBACK, not GATED):

```text
$ grep -q 'FALLBACK' docs/work/2026-09-30-809-measurement-report.md && grep -q 'd23ee28e' docs/work/2026-09-30-809-measurement-report.md && echo AC5-OK
AC5-OK
```
