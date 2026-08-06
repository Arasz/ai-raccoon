# Worked example: reviewing a SQL data path against the live store (Hermes state.db)

Adversarial review (2026-08-03) of PR #301's Hermes token-tracking in the ai-badger
repo: `parse_hermes_session_usage` / `hermes_delegation_usage` / `make_hermes_checkpoint`
read `~/.hermes/state.db` read-only and map it onto an existing Claude-transcript
checkpoint shape. The review judged SQL correctness, test honesty, and edge cases, with a
per-area verdict (PASS / CORRECTED / FAIL) and a final APPROVE / REVISE.

## The two real bugs found (both survived a green 82-test run)

### Bug 1: row→dict loop assigned instead of accumulated

`tracker_lib.py:603-612` (in the PR):

```python
for (model, api_calls, inp, out, cr, cw) in con.execute(...):
    by_model[model] = {"inputTokens": inp or 0, ...}   # ASSIGNMENT
```

The real table `session_model_usage` has a composite PK
`(session_id, model, billing_provider, billing_base_url, billing_mode, task)`, so one
`(session, model)` spans multiple rows split by `task` (`''` main thread, `'approval'`,
`'title_generation'`, `'compression'`). Measured: 590 rows vs 367 distinct
`(session_id, model)` pairs; 157 pairs have >1 row; 166 sessions carry `task != ''` rows.
Assignment keeps the LAST row in PK-ordered iteration — simulated on a real 65-child
parent, a 2,620,488-input-token model reported as **350** input tokens (the
`title_generation` row). `compute_usage` reads `byModel` for `outputByModel`/`modelMix`, so
those were corrupted ~4 orders of magnitude.

Detected by: `PRAGMA table_info(session_model_usage)` → noticed the `task` column and
composite PK; `GROUP BY session_id, model HAVING COUNT(*) > 1` → 157 pairs; then
simulating the exact loop against real rows and comparing last-wins vs accumulate.

### Bug 2: "no fold needed" ground truth was false

The plan doc asserted (as VERIFIED GROUND TRUTH) that parent sessions rows already include
child (delegated) sessions. Measured 8/8 parents including ended ones:
`20260802_222214_fed404` row input = 309,344; its only child `20260802_223850_7430ea` =
59,265; 309,344 + 59,265 = 368,609 ≠ 309,344. The parent row equals its own `task=''`
smu row exactly — main-thread usage only, children excluded. Consequence: Hermes
`cumulative`/`grandTotal` excluded delegation spend, AND `compute_usage` deliberately does
not add `subagentTokens` to `grandTotal` (a rule designed for Claude, where the checkpoint
delta already folds subagents) — so delegated tokens appeared in no total at all.

## Queries that worked (all read-only)

```python
import sqlite3
con = sqlite3.connect("file:/Users/arasz/.hermes/state.db?mode=ro", uri=True)
# schema reality check
con.execute("PRAGMA table_info(session_model_usage)").fetchall()
con.execute("SELECT sql FROM sqlite_master WHERE name='session_model_usage'").fetchone()
# cardinality: does one logical key span multiple rows?
con.execute("SELECT COUNT(*) FROM (SELECT 1 FROM session_model_usage "
            "GROUP BY session_id, model HAVING COUNT(*) > 1)").fetchone()
# arithmetic spot-check: parent row vs parent+children sum
con.execute("SELECT input_tokens FROM sessions WHERE id='20260802_222214_fed404'").fetchone()
con.execute("SELECT SUM(input_tokens) FROM sessions WHERE parent_session_id='20260802_222214_fed404'").fetchone()
```

Other checks that paid off: `PRAGMA journal_mode` (=wal, so mode=ro reads never contend);
distinct `messages.role` values before trusting `COUNT(*) WHERE role='assistant'`;
sampling `async_delegations.result_json` to confirm the `results[].tokens/model/api_calls`
shape the parser assumes; verifying the CLI resume flag exists (`hermes --help | grep resume`)
before accepting a `resumeCommand` string.

## Test-honesty verdict pattern

The fake store was file-backed sqlite with the real column names for the columns the code
SELECTs — honest for what it covers. But its `session_model_usage` fake had NO `task`
column, so multi-row-per-model could not be represented: Bug 1 was structurally
untestable by the suite. Lesson: audit whether the fake can even model the cardinality
the real PK allows; a fake that cannot means the bug ships green.

## Delivery shape that worked

Per-area verdicts (A correctness FAIL, B test honesty CORRECTED, C architecture PASS,
D edge cases CORRECTED) each with path:line findings, then a final REVISE with exact
`patch`-ready edits (accumulate block, child-fold query, isinstance guards for
non-dict `result_json`, regression tests, docs-drift fix in extension.md). The edge-case
table (missing db / locked db / absent session id / malformed json / empty results /
zero totals → which returns zeroed vs None vs exit 2) was the review's spine.
