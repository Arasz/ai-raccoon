# Bank-quality audit — plan + specification for the next agent that looks at the data

**Date:** 2026-09-03
**Status:** Proposed (executable as-is by any agent session; no code changes)
**Problem:** an agent looked at two aggregates — scorer-v2 queue average 2.63, 21/1000 rows
≥3.5 — and concluded *"most stored content is mediocre, and retrieval surfaces what's
there."* Every number was correct; the verdict was unsupported. This spec makes that
failure mode unrepeatable by defining what "bank quality" means, how to measure each
meaning, and the gates an audit report must pass before its verdict is quotable.

## 1. Anatomy of the error being prevented (read first)

The item-3 claim committed five methodological errors. Each maps to a rule below:

| # | Error | Why it misled | Rule that forbids it |
|---|---|---|---|
| E1 | Aggregate queue stats as content verdict | Queue rows are extraction *candidates* (recent, proposed-tier only) — a biased subsample, not the bank. 9563 bank rows were never examined. | §2 (three questions), G2 |
| E2 | Global numbers for a project question | 2.63/21 span all projects; the ai-raccoon slice averages 2.90 with zero ≥3.5 rows of its own. | §3 step 1, G3 |
| E3 | Absolute reading of an uncalibrated scale | Doc-channel priors sit at 1.03–2.06 by construction — 2.63 cannot mean "mediocre" without a calibration the author never did. | §3 step 3, G4 |
| E4 | Zero content reads | No bank row was read for quality; "samples" illustrated score buckets. | §3 step 2, G1 |
| E5 | Unprobed retrieval claim | "Retrieval surfaces what's there" was asserted from zero queries; four probes later hit rank-1 on tail docs. | §3 step 4, G5 |

Prior art: `docs/work/2026-09-03-bank-content-quality.md` (partial audit — did steps 0,1,4;
skipped 2,3), the 4-probe tail-doc grading (2026-09-03 session), the ignore-gap fix
(`4c276fe8`, removed 788 fixture rows — bank state changed; see §5).

## 2. "Bank quality" is three questions, not one

| Question | Estimand | Answered by | NOT answered by |
|---|---|---|---|
| Q-content | Is the stored content itself good? | Reading stratified samples, grading vs rubric | Queue averages, head counts |
| Q-queue | Does the queue rank durable above noise? | Blind grades of top-k vs mid-band rows | Content-sample grades, retrieval probes |
| Q-rank | Do real queries surface good content? | Probe queries with relevance grades | Either of the above |

**Conflation rule:** a finding must name which question it answers. "Avg 2.63" answers
none of the three — it describes the scorer's output distribution, which is evidence
*about the scorer*, admissible only toward Q-queue and only with graded samples.

## 3. Audit protocol (normative)

### Step 0 — Snapshot the moving target

Record before any query: UTC timestamp, `COUNT(*)` per corpus (`entries` by
scope/project, `code_entries` by project), `scorer_version` rows present, current
`ai-raccoon.ignore` commit, bank file mtime. The bank is live (observed 1000→999
mid-investigation, ±1 drift between consecutive counts). Every number in the report
inherits its snapshot timestamp; numbers from two snapshots are never compared without
saying so.

### Step 1 — Slice correctly

- MCP `projectId` is the **name id** (`ai-raccoon`), not the guid in
  `.ai-badger/project-id` (resolves to zero rows). SQL uses the same name ids.
- Global figures are context, never verdicts: any project-scoped claim computed over the
  global queue is E2 and fails G3 on sight.

### Step 2 — Content sample (answers Q-content)

Stratify, then random-sample within strata (order by `RANDOM()`), minimum n:

| Stratum (ai-raccoon `entries`, post-`4c276fe8` sizes) | min n | Why separate |
|---|---|---|
| `docs/` project rows (~8000) | 20 | the bulk; split ADR vs work vs research if n allows |
| `scripts/` + `tests/` rows (~300 post-fix) | 8 | known noise-adjacent; the residual after the ignore fix |
| agent-written (`source_file` empty, ~70) | 5 | only organic tier; different provenance, different bar |
| `shared` scope (~70) | 5 | already-promoted: the revealed preference of past reviews |
| `code_entries` (~7000) | 8 | separate corpus, separate quality shape |

Read **full values** (`memory_get`/`code_get` by hash — snippets are E4-adjacent), grade
each 0–4 against `docs/work/promotion-scoring-eval/RUBRIC.md`, record hash + grade +
one-line why. Report stratum means **with n** — never a bank-wide mean without its
sampling weights.

### Step 3 — Queue ranking eval (answers Q-queue)

- Blind-grade (grade before seeing score) top-10 by score vs 10 random rows from the
  2.0–3.0 band, same project slice. Report head precision and whether the grader can
  distinguish the bands — this is the open discrimination question the calibration
  debate depends on.
- Score distribution may be *reported* (it is cheap); it may not be *interpreted* (E3)
  without a fitted map. If one exists, cite fit date + n; else say UNVERIFIED.

### Step 4 — Retrieval probes (answers Q-rank)

- ≥6 queries: ≥3 from real agent needs (recent task topics), ≥2 from tail-doc facts
  (unaccessed rows — the "dead tail" hypothesis), **≥1 adversarial/noise-seeking**
  (vocabulary overlapping known-noisy families; the 2026-09-03 probe caught a fixture
  row at rank 4 this way).
- Grade per probe: rank of the answering chunk, snippet sufficiency (answers / points /
  misses), and any noise row in top-5 with its source.
- Existing assets: `tests/AiRaccoon.Tests/search_quality_eval.json` (on disk; now
  ignore-listed from ingest — probes may use it, the bank no longer mirrors it),
  `scripts/baseline-queries.json` (44 queries; jsaa-flavored, say so if reused).

### Step 5 — Report

Evidence-first record (`MEASURED`/`READ`/`INFERRED`/`UNVERIFIED` per finding, grade mix
up front, non-empty `Still open`), rendered via
`.ai-badger/skills/evidence-first-research/scripts/render_report.py`.
Template/worked example: `docs/work/2026-09-03-bank-content-quality.md`.
A verdict sentence without a hash, a command, or a probe behind it is E4 and fails G1.

## 4. Acceptance gates (auditor self-check; reviewer re-check)

- **G1 — no content claim without reads:** every Q-content sentence cites ≥3 sample
  hashes with grades; the sample table (stratum × n × mean) is in the report.
- **G2 — no queue verdict without blind grades:** any "queue ranks well/poorly" claim
  cites the step-3 head-vs-band comparison, not the average.
- **G3 — slices labeled:** every number carries its population (project + scope +
  snapshot time); no global figure appears in a project verdict.
- **G4 — no absolute scale readings:** any "X is mediocre/good" cites the rubric
  (for grades) or a fitted map with date (for scores) — never the raw number alone.
- **G5 — no retrieval claim without probes:** every Q-rank sentence cites ≥1 probe
  (query text + rank + grade), including the adversarial one.
- **G6 — grade mix reported:** counts of MEASURED/READ/INFERRED/UNVERIFIED in the
  summary, so the next reader knows what kind of answer this is.

## 5. Pointers (so the next agent starts at the data, not at discovery)

- Bank: `~/.ai-raccoon/memory.db` — `entries` (scope, project_id, source_file, value,
  rating, access_count, created_at), `code_entries` (project_id, source_file, line
  range), `promotion_queue` (project_id, score, reasons, scorer_version).
- State cautions: post-`4c276fe8` the three fixture families read 0 rows — an audit
  that "discovers" fixture noise without checking the ignore commit is stale; the
  guid-vs-name id mapping (§3 step 1) has bitten twice.
- Grading ground truth: `RUBRIC.md` + `reference-labels.json` (61 owner labels);
  agent-labeled round sets are stability data, never primary grades.

## 6. Open questions

- Minimum-n statistics: current n's are judgment-based (±~1 grade at stratum level).
  A second owner-labeling round would harden both this protocol and any future map fit.
- Who grades: owner grades are ground truth; agent grades are the scalable path. Blind
  + dual-grade a pilot batch to measure the gap before trusting agent grades alone.
- Promotion: if this protocol gets run twice with the same errors caught, promote it to
  a skill (candidate: `memory-quality-audit`) rather than a third plan doc.
