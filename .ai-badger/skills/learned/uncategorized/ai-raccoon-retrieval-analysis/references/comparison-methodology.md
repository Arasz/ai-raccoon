# Retrieval A/B Comparison — Reusable Recipe

> Provenance: retrieval-improvement-cont task (2026-08-04), comparing dual-vector
> structure signal vs plan FTS fixes on a 6675-chunk corpus. The methodology
> survived plan-review (APPROVE-WITH-CHANGES, 9 points folded in) and produced
> actionable findings.

## Design rule: one scorer, many harnesses

Two harnesses computing their own metrics WILL produce different numbers (tie-breaking,
case folding, consolidation edge cases). **The scorer is the single source of truth.**
Harnesses emit ONLY raw ranked lists; the scorer computes every metric.

### Harness output contract

```json
{
  "corpus": "<description>",
  "wallSeconds": 81.5,
  "arms": ["content-only", "fixed-a0.5", ...],
  "queries": [
    {
      "id": "A1",
      "expectedSource": "docs:adr:0011-...#decision",
      "arms": {
        "content-only": [ { "rank": 1, "hash": "...", "path": "...",
          "headingPath": "ADR-0011 > Decision", "score": 0.565 } × top-100 ]
      }
    }
  ]
}
```

Every entry MUST carry `headingPath` — the scorer needs it for section-level hits.

### Scorer computes (single implementation)

- File-level hit@5: any top-5 chunk whose path ends with `/` + the expected file name
  (last segment of fragment-stripped `expectedSource`, split on `:` and `/`)
- Section-level hit@5: any-segment match (`headingPath.split(" > ")` contains
  the fragment segment, case-insensitive, trim trailing `:`)
- MRR(file), MRR(section): over queries with reachable expected sources
- Pre-flight: for each expected-source query, scan the union of all arms' top-100
  lists — if NO chunk of the expected file has a heading path containing the fragment,
  mark section-ground-truth-unavailable (report, don't score section for that query)
- Multi-answer override: when `expectedKnowledge` names multiple files (e.g. "ADR-0067
  + ADR-0068") but `expectedSource` names only one, the scorer's `MULTI_ANSWER_FILES`
  dict accepts any of them for file/section hits.

## Pre-registered win rule

7 queries = low power. Pre-register before running either harness:
- An arm BEATS the content-only baseline iff ≥2 section-level hit flips OR
  MRR(file) delta ≥ 0.1 vs content-only
- Below = tie
- Publish the per-query rank table so single-query flips don't hide in averages
- Best-of selection (pick the temperature that wins) is test-set leakage → report
  ALL temperatures; F+V fusion with every V-arm, not just the winner

## Arms design

Always include:
- **Content-only baseline** (α=1.0, or current pipeline without the candidate change)
- **Structure-only diagnostic** (α=0.0) — if structure signal alone is degenerate,
  flag it (the dual-vector experiment found structure-only top result = README.md
  with heading path "adr")
- **Fixed-α control** — attributes any per-query-α-arm win to the ADAPTIVE machinery
  vs merely adding a signal. Without this control, a sigmoid-arm win can't be attributed
  to per-query tuning (the dual-vector experiment found sigmoid ≈ fixed-α=0.5)
- **Per-query α arms** — sigmoid, confidence-weighted, the candidate being tested

## Section-level matching: any-segment, not last-segment

ADR Decision sections carry sub-headings (`Decision > D9 — Orchestration`).
The last heading-path segment is the sub-heading, NOT "decision". Matching only the
last segment misses the chunk entirely and drops section-hit counts (4/7 → 6/7 on
the corpus tested). Use ANY-segment: `any(seg.strip().rstrip(":").lower() == "decision"
for seg in heading_path.split(" > "))`. Both harnesses AND the scorer must agree.

## F+V RRF hybrid

Fuse the FTS arm's ranked list with every vector arm's list via RRF(k=60, 1:1).
Report ALL combinations, not the best. If the FTS arm is degraded (e.g. zero-match
on several queries), its misses poison the hybrid — MRR drops below both parents.
This is a finding, not a bug: "F+V dilutes" tells you the FTS ranker must be
functional before fusion helps.

## Memory-bounded execution

A previous prototype OOM-killed the machine at 50 GB (24 GB host). The fix:
- Content embeddings keyed by hash, not text (dictionary lookup by text on a hash
  → all zeros → 0/7 hits, detected by asserting top-1 score > 0)
- Batched generator calls (batch size 32)
- 6 GB RSS watchdog (`scripts/run-with-memcap.sh 6 <apphost>`)
- Wall time + peak RSS reported in the output

### run-with-memcap.sh pattern

```bash
#!/bin/bash
CAP_GB="${1:?cap in GB}"; shift; CAP_KB=$((CAP_GB * 1024 * 1024))
"$@" & PID=$!; PEAK=0
while kill -0 "$PID" 2>/dev/null; do
    TOTAL=0
    for P in $(pgrep -P "$PID" 2>/dev/null; echo "$PID"); do
        RSS=$(ps -o rss= -p "$P" 2>/dev/null | tr -d ' '); [ -n "$RSS" ] && TOTAL=$((TOTAL + RSS))
    done
    [ "$TOTAL" -gt "$PEAK" ] && PEAK=$TOTAL
    [ "$TOTAL" -gt "$CAP_KB" ] && { echo "MEM CAP EXCEEDED: ${TOTAL}KB" >&2; pkill -9 -P "$PID"; kill -9 "$PID"; exit 42; }
    sleep 5
done; wait "$PID"; RC=$?; echo "PEAK_RSS_MB=$((PEAK / 1024))" >&2; exit $RC
```

## Zero-match handling

When an FTS5 MATCH returns zero rows, `bm25(entries_fts)` returns a result typed as
`Byte[]` (not `Double`). Dapper record-ctor mapping fails with `InvalidOperationException`.
Fix: either pre-check `SELECT COUNT(*) ... WHERE MATCH @q` and return an empty list,
or query `rows > 0` before reading bm25. Zero-match is a FINDING (the query
construction may be too aggressive), not a crash — record the query ids, report them,
do not fabricate results.

## Corpus drift

Every comparison number is corpus-conditional. The original experiment ran on a
6675-chunk DB (71% docs/work pollution) with project_id `jsaa`. The Wave 0 clean
corpus is 762 chunks (project_id `job-search-ai-assistant`, paths are SHA256 hashes).
When re-running: update project_id, re-verify expected-source reachability (C1/C2/C5
may now be present), and re-validate the section-ground-truth pre-flight on the new
ranked lists.
