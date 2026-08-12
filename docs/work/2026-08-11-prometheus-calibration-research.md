
## Update: M-Prometheus 14B comparison (2026-08-11)

M-Prometheus 14B loaded successfully in LM Studio (previously reported as unable to load).
Comparison on 10 queries:

| Query | 7B | 14B | Delta |
|-------|----|----|-------|
| search_quality table schema | 3 | 2 | -1 |
| docker run issue | 1 | 2 | +1 |
| staticwebapp.config.json | 1 | 2 | +1 |
| check current main | 1 | 1 | 0 |
| round3 owner gate decisions | 1 | 1 | 0 |
| hosted service wiring | 1 | 2 | +1 |
| integration review gate | 1 | 1 | 0 |
| memory source normalization | 5 | 2 | **-3** |
| hook called + queue cleanup | 5 | 2 | **-3** |
| git push lefthook results | 5 | 2 | **-3** |

**14B is consistently more conservative** — downgrades transcript-heavy queries from 5→2 (closer to human estimate of 2-3). Upgrades some file-not-found queries from 1→2. Average: 7B=2.8, 14B=1.7.

**Decision:** Switched cron to use `m-prometheus-14b` with `--limit 10` (14B is slower per query). Grades will be more defensible but still need human calibration.

**Evidence:** `curl -s http://localhost:1234/v1/models`; comparison script run on 2026-08-11.
