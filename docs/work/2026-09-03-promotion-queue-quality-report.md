# Promotion-queue quality report — 2026-09-03

Bank read-only. Queue state: **1000/1000 rows (at `extract.queue-capacity.global`)**,
avg score 2.63, max 4.0, scorer v2 throughout. Zero queued hashes already in shared
(no double-promotion waste), but the queue has been at cap while extract keeps proposing —
see finding F3.

## Score shape

| bucket | rows |
|---|---|
| ≥3.5 | 21 |
| 3.0–3.5 | 259 |
| 2.5–3.0 | 384 |
| 2.0–2.5 | 254 |
| <2.0 | 82 |

A fat middle (2.0–3.0 holds 638 rows) and a thin head. The scorer discriminates weakly:
most of the bank's durable content lands in one undifferentiated band.

## What the reasons say (top tokens over 1000 rows)

rule-language 743 · durable-rule-language 669 · work-note 350 · mid-sentence 295 ·
verified-contract 295 · portability 188 · organic-note 216 · measured-values 177 ·
foreign-subject 152 · durable-fact-language 142 · adr 107 · status-vocabulary 97 ·
auto-memory-note 70 · heading-start 65.

The extractor votes overwhelmingly for **imperative/rule-shaped text** (743+669). That is a
style detector more than a durability detector: anything phrased as a rule outranks
observations, measurements and decisions. `foreign-subject` (152) is the genuinely
sharing-shaped reason and it is a minority.

## Samples

**Head (4.0, genuinely shareable):** jsaa CircleCI platform facts (verified 2026-09-01,
PR #1042 — org slug, status-vs-checks distinction); jsaa resume-probe pattern (PR #1006,
hook contract + mirror-format pin); ai-badger pi-mcp-tools fork behavior (.mcp.json
trust-gating, filterPatterns-are-regexes trap). Specific, sourced, dated, cross-project
useful. The head is good.

**Mid (2.4–2.6, project-useful, sharing-debatable):** jsaa lane-timeout recovery pattern
("treat the lane worktree as ground truth"); deepseek-harness agent-constraint notes
(ts AgentContext caps, no-fs/network APIs). True and useful *inside* their project; as
shared cross-project knowledge they are process anecdotes. This band is where a promote
review earns its keep — and it is 64% of the queue.

## Findings

- **F1 — reasons overweight rule phrasing.** 74% rule-language means the queue ranks _tone_
  above durability. A measured value (`measured-values` 177) or ADR (`adr` 107) should
  outrank an imperative sentence; today the reverse is likelier.
- **F2 — jsaa's queue is split across two project ids** (157 under `jsaa`, 89 under
  `job-search-ai-assistant`; same logical project — see project_id analysis). Any
  per-project cap, ranking or review happens on fragments.
- **F3 — at cap with unknown shedding.** 1000/1000 with `extract.mode=propose` firing every
  30 min. Which rows lose when a full queue meets a new candidate was not verified in this
  pass. If it is lowest-score-evicts, the 82 sub-2.0 rows are the buffer; if it is
  insert-refused, the queue is frozen and new extractions silently die. Check
  `promotion_queue_prune_requests` + the extract path before trusting propose-tier
  completeness. 967 historical discards show review *does* happen; the queue just refills
  faster than review drains it (530 rows arrived Aug 22 alone).
- **F4 — 0% hash overlap with shared** is the one clean bill: nothing queued is already
  promoted.

## Recommendation

Review from the head down (21 rows ≥3.5 first — mostly jsaa/ai-badger platform facts),
then decide the F3 shedding question before spending review effort on the 2.0–3.0 band:
if inserts are refused at cap, drain-then-review is the only order that works.
