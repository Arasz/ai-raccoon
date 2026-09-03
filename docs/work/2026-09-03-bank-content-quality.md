# Research: bank content quality vs the "marginal content" claim

**Date:** 2026-09-03
**Question:** Is it true that our whole memory bank — with watches on docs and code — holds only marginal content, as the merge lane's live-bank data point (scorer-v2 average 2.63, only 21/1000 queued rows ≥3.5) suggests?

```chart:bars
title: promotion-queue average score by project (scorer v2)
ai-badger: 3.05
jsaa: 3.03
ai-raccoon: 2.90
global: 2.63
deepseek-harness: 2.47
arasz-home-page: 2.38
```

## Findings

### F1 — The cited global queue numbers reproduce almost exactly [MEASURED]

The promotion queue holds 999 rows (one fewer than the report's 1000 — one row drained
since), with average score 2.6344, max 4.0, and exactly 21 rows scoring ≥3.5. The "2.63 /
21-per-thousand" data point is real, not a misquote — but it describes the queue across
*all* projects, not the ai-raccoon bank.

**Evidence:** `sqlite3 ~/.ai-raccoon/memory.db "SELECT COUNT(*), AVG(score), MAX(score) FROM promotion_queue;"` → `999|2.6344740225099|4.0`, and `... WHERE score>=3.5` → `21`, run on this machine (macOS, live bank) 2026-09-03. Single read per query; counts/averages are exact over the table, not sampled.

### F2 — The quality report behind the claim covers all projects, not ai-raccoon [READ]

The report's score-shape table (≥3.5: 21; 3.0–3.5: 259; 2.5–3.0: 384; 2.0–2.5: 254;
<2.0: 82) and its "fat middle / thin head" verdict are computed over the 1000-row global
queue, and every named head sample (jsaa CircleCI facts, resume-probe pattern, ai-badger
pi-mcp-tools fork behavior) belongs to another project. Nothing in it isolates the
ai-raccoon slice.

**Evidence:** `docs/work/2026-09-03-promotion-queue-quality-report.md:1-40` — queue state line, score-shape table, and Samples section.

### F3 — The ai-raccoon queue slice scores above the global average with a substantive head [MEASURED]

Ai-raccoon contributes 155 queue rows averaging 2.897 (above the 2.634 global mean), with
max 3.437 and zero rows ≥3.5 — so all 21 global head rows belong to other projects
(F1's ≥3.5 list is jsaa, job-search-ai-assistant, ai-badger, pi-badger-integration only).
The bucket split is 42 rows at 3.0–3.5 and 113 at 2.5–3.0: no thin-head-plus-fat-middle
collapse, but a ceiling — nothing in the ai-raccoon slice clears the report's "genuinely
shareable" bar. The top row is genuinely substantive: the released-1.2.0 search-latency
profile (p50 57ms, window-function overhead measured 52%, vec0 KNN at 4.7ms vs 18.3ms
scalar scan).

**Evidence:** `sqlite3 ~/.ai-raccoon/memory.db` on this machine 2026-09-03: `SELECT COUNT(*), AVG(score) ... WHERE project_id='ai-raccoon'` → `155|2.89678053835021`; `MAX(score)` → `3.43722222222222`; bucket GROUP BY query → `2.5-3.0|113`, `3.0-3.5|42`; `WHERE score>=3.5` ordered list shows no `ai-raccoon` rows (exact queries, full-table).

### F4 — The live ai-raccoon bank is large, fully embedded, and watched — but ~9% is test/eval fixture mirror [MEASURED]

Project `ai-raccoon` holds 9563 project-scoped entries plus 70 shared and 7034 code
entries; every entry and code row reports `embed_state='embedded'` (zero pending). One
watch covers `/Users/arasz/RiderProjects/ai-raccoon` with 1719 indexed files. The largest
single sources are test/eval artefacts: `search_quality_eval.json` (335 rows),
`feature-parity-fixture.json` (181), `held-out-reproduction-fixture.json` (122),
`eval-set-100.json` (102) — 836 rows (8.7%) that mirror test data rather than durable
knowledge. So the bank is a faithful mirror of the repo, fixtures included, not a curated
knowledge base.

**Evidence:** `sqlite3 ~/.ai-raccoon/memory.db` on this machine 2026-09-03: entries GROUP BY → `project|ai-raccoon|9563`, `shared|ai-raccoon|70`; code_entries `WHERE project_id='ai-raccoon'` → `7034`; embed_state GROUP BY → all `embedded`; `watches` → `ai-raccoon|/Users/arasz/RiderProjects/ai-raccoon`; watch_files → `ai-raccoon|1719`; source_file GROUP BY top rows as listed; fixture-LIKE count → `836`. Exact counts, full-table.

### F5 — Three live retrieval probes returned on-point results, even an adversarial one — but usage signal is flat [MEASURED]

`memory_search` (projectId `ai-raccoon`, 2026-09-03, this session) for "promotion queue
scoring rules share durability", "how does memory_search hybrid retrieval fusion work",
and the deliberately noise-seeking "eval set fixture grade test data" all returned
substantive docs and code (scoring-rules catalog, ADR-0095, hybrid-retrieval
investigation, fusion legs, tuning report) — the fixture-seeking query surfaced tuning
*reports about* fixtures, not the 836 raw fixture rows. Retrieval ranks prose over mirrored
test data. Counterweight: only 398 of 9563 project rows were ever accessed
(`access_count>0`), and ratings sit at the 0.5 default (mean 0.501) — there is no usage
signal separating good rows from mediocre ones.

**Evidence:** three `memory_search` calls with projectId `ai-raccoon` (limits 10/5/5) in this session, result hashes/snippets listed above; `sqlite3 ~/.ai-raccoon/memory.db`: `access_count>0` count → `398`, `AVG(rating)` → `0.500958863425953`. Searches run once each against the live bank.

### F6 — "Marginal" is a scorer-scale-relative verdict, not a retrieval-relative one [INFERRED]

Scorer v2 centers every large project in the 2.4–3.1 band (ai-badger 3.05, jsaa 3.03,
ai-raccoon 2.90, deepseek-harness 2.47), so calling 2.6 "mediocre" assumes the 5-point
scale is linear and that 3.5 marks a real durability cliff. The ai-raccoon head at
3.1–3.44 reads as strong, specific, dated engineering knowledge (F3), and retrieval
prefers it over fixture noise (F5). The fairer reading of the data point: the queue's
*head* is thin everywhere and the scorer compresses most durable content into a
2.0–3.5 band where review order is nearly arbitrary — a ranking-resolution problem, not
proof the bank holds nothing worth surfacing.

Reasoning from F1–F5 above plus the quality report's own F1 (rule-phrasing overweight,
74% rule-language).

### F7 — Whether low-scoring rows actually outrank good rows in mixed retrieval is untested [UNVERIFIED]

No relevance-graded A/B (fixed query set, judged rankings, low-vs-high-score mix) was run
in this pass. F5 shows three queries behave well; it does not show the failure mode the
claim implies — mediocre rows crowding out good ones — either way.

What would settle it: a ~20-query graded relevance set over the ai-raccoon bank scoring
whether ≥3.0 rows outrank 2.0–2.5 rows for the same queries.

## Still open

- What the scorer's 5-point scale is calibrated to: is 2.6 "mediocre content" or "solid content the scorer centers at 2.6"? Needs the scorer's calibration set, not more bank reads.
- Whether the 836 fixture rows ever surface for realistic (non-adversarial) queries — one probe is not a distribution.
- The F1 count moved 1000 → 999 mid-investigation: queue drains/refills concurrently, so any per-bucket claim needs a timestamped snapshot to be quotable.
- GUID project id (`cfe47dab-…` in `.ai-badger/project-id`) resolves to zero entries via MCP while the `ai-raccoon` name id holds all 9563 — worth a separate look at id mapping before quoting per-project stats.
