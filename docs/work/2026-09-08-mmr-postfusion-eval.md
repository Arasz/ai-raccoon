# Post-fusion MMR + threshold-filter PoC — rho-grid eval (2026-09-08, evening)

Branch: `task/air-mmr-postfusion-poc` (worktree `.ai-badger/worktrees/air-mmr-postfusion-poc-eval-run`).
Build: `dotnet build src/AiRaccoon` green. Hook: `SearchResultMerger.Merge`, between
`SourceAffinityRanker.Rank` and the floor/limit — fuse → diversify → floor, per rev.1 §1.4.
Env-gated (`MMR_MODE=mmr|threshold|off`, `MMR_RHO`, `MMR_TAU`, `MMR_DISABLE`), order-only,
scores untouched, one batched sidecar vector fetch on stored blobs (no model loads).
NOT SHIPPABLE. Raw arm outputs: `/tmp/mmr-post-{off,r0001,r001,r003,r005,threshold}.json`
(Q3–Q5, project `ai-raccoon`), `/tmp/mmr-jsaa-{...}.json` (Q1–Q2, project `jsaa`).

## Method note (harness bug caught mid-run)

First pass ran all 5 queries under project `ai-raccoon`; Q1/Q2 came back 0/8 overlap vs
the leg-level baseline and looked like bank drift. Root cause was mine, not the bank:
Q1/Q2 are `jsaa`-domain queries whose answers are `scope='project', project_id='jsaa'`
rows (198 spec: 27 rows, 196 spec: 41 rows — all present and `embedded`). Re-ran Q1/Q2
under project `jsaa`: off is **byte-identical** to the leg-level `ours-off` (8/8, same
order, both queries). No drift; legacy grades carry over untouched.

## Grades (same rubric as leg-level: A=1 iff the chunk answers the query; single grader, no blind)

| q (project) | off | mmr ρ=0.001 | ρ=0.01 | ρ=0.03 | ρ=0.05 | threshold τ=0.95 |
|---|---|---|---|---|---|---|
| Q1 gmail spec (jsaa) | 6/8 | identical | identical | identical | identical | identical |
| Q2 dossier/ADR-0026 (jsaa) | 8/8 | identical | identical | identical | identical | identical |
| Q3 file-watcher (ai-raccoon) | 7/8 | identical | identical | identical | set-identical, one adjacent swap (4↔5) | 7/8 — drops archive mirror, promotes live spec chunk |
| Q4 agent instructions (ai-raccoon) | 3/8 | identical | identical | identical | set-identical, one adjacent swap (3↔4) | 2/8 — drops HERMES.md, promotes RUBRIC.md |
| Q5 second fusion (ai-raccoon) | 5/8 | identical | identical | identical | identical | identical |
| **TOTAL answer-chunks** | **29/40** | **29** | **29** | **29** | **29** | **28** |

RBO (p=0.9, truncated form shared with `mmr-rbo.py`; identical lists = 0.513 ceiling):
every `identical` cell is RBO 0.513; r005 swaps read 0.488–0.496; threshold Q3/Q4 0.439–0.463.
Code section identical in all arms (control holds). Markers (`[mmr-poc] mode/rho/pool/fetched`)
present on every active call; pools 230–365 rows fetched in one query each.

## Reading

1. **Post-fusion MMR at ρ ≤ 0.05 is neutral — and that exonerates the position, not the idea.**
   Leg-level ρ=0.1 went 29→11 with Q2 8/8→0/8; post-fusion at every tested ρ keeps Q2 at
   8/8 and moves at most one adjacent pair. The vandalism mechanism was leg-rank laundering
   (junk handed a top leg rank, RRF trusting it) — post-fusion cannot do that by construction.
   What remains is honest: on this bank there is almost nothing in the mild band worth
   reordering, so MMR takes almost nothing.
2. **Threshold τ=0.95 did exactly its advertised job, twice, both verified true near-dupes:**
   Q3 dropped `work/archive/features-file-watcher/spec.json` (cos 0.9994 vs the live
   `features/file-watcher/spec.json` kept at rank 2) and promoted a second live-spec chunk —
   stale mirror out, fresh answer in, 7→7. Q4 dropped `HERMES.md` (cos 0.9823 vs `CLAUDE.md`;
   all three instruction files generate from one source) and promoted `RUBRIC.md` — third
   copy of the same instructions out, unrelated file in, 3→2. Same mechanism, opposite
   verdicts: worth it where the dupe is stale, costs a point where the answers themselves
   are near-dupes of each other. That is the whole threshold-filter trade in one table.
3. **Recommendation stands, now measured on both sides:** ship nothing; keep threshold τ=0.95
   as the only candidate, gated behind a per-query decision that does not exist yet
   (multi-facet vs answer-dupe queries). MMR needs no further spend until a query class
   appears where below-τ facets crowd each other — none observed in 40 slots × 6 arms.

## Caveats

n=5 queries; single grader, no blind; RBO truncated (ordering preserved, absolute scale
capped at 0.513); ρ grid stops at 0.05 by design (0.1 already killed at leg level, and the
W-1 mechanism says the mild band lives here); Q-cost was 13 sequential single-server runs,
no model loads beyond per-query embeddings, peak extra RSS ≈ one server process.
