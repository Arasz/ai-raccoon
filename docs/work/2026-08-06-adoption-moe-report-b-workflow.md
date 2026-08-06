All evidence gathered. Writing the final report.

---

**MoE Report B — adoption/workflow**

Scope: maximize organic usage of the AiRaccoon memory server across Hermes and Claude Code agents. Lenses: search habits, shared-tier usage, grading loop, KPIs, ai-badger framework changes. No files were modified.

EVIDENCE BASE (all measured this session unless noted):
- Telemetry (M1): memory-quality.jsonl has 11 rows — 10 host=hermes, 1 manual, 0 host=claude; 7 are tool-test probes; 4 organic searches (3 ai-raccoon, 1 ai-badger); 2 graded (4/5, 5/5).
- Claude Code (M2): zero mcp__ai-raccoon__* tool_use calls in ANY ~/.claude/projects session log across all projects (21,682 Bash, 2,805 Edit, 1,072 Agent dispatches; zero memory tools). ai-raccoon tools arrive in Claude sessions only as deferred_tools_delta.
- Claude jsaa (M3): job-search-ai-assistant has had NO Claude session since 2026-08-03 03:32; its .mcp.json (with ai-raccoon) was last written 2026-08-05 23:13 — no Claude session has ever had ai-raccoon declared in jsaa. The "zero claude lines" mystery is solved: jsaa = no sessions since declaration; ai-badger = sessions exist but never invoke memory tools despite them being available.
- Tool index (M4): ai-badger repo's own .ai-badger/mcp-tools.json contains ZERO ai-raccoon/memory entries — the per-turn recommendation hook can never suggest memory tools in ai-badger sessions. jsaa's index has all 19 ai-raccoon tools with catalog intents (features/common/mcp/ai-raccoon/tools.json exists).
- Wiring (M5): all three repos carry HERMES.md/.hermes.md "MCP Tools: ai-raccoon" prose and .claude/settings.json with the PostToolUse memory_search → memory_grade_hook matcher; installed Hermes plugin is 0.81.0 with memory_grade wired (M6).
- Framework (M10): task skill Phase 0 has no memory step; prompt-markers has no memory marker (markers-context.json is no-code editable); mcp-tags taxonomy has no `memory` tag (memory_search is [search, semantic]).
- Audit (M7): 2,649 entries, 5 organic writes ever, zero shared rows, PR #47 fixes (F12-F16, 1,142 tests pass) but the live bridge tool still runs 1.0.7 pre-fix.
- Mechanics (M8/M9): memory_share promotes an existing entry by content hash into the flat shared tier (Write access, sweep-exempt); memory_delete_context('shared') wipes the whole tier (proven in the tool test); the grade ask is one line with a python3 command, popped on Hermes' next pre_llm_call keyed by project path, delivered inline on Claude via additionalContext, filled in-place by ts pointer.

(1) TOP RECOMMENDATIONS

1. Install the PR #47 build as the live bridge tool. Impact: high (precondition for every other measurement — the running server is still the pre-fix 1.0.7; watch-error log noise and pending rows otherwise keep contaminating telemetry). Effort: S. Evidence: MEASURED (F7/F12/F16, live log baseline). Repo: ai-raccoon.

2. Ship a one-call memory brief: new MCP tool memory_brief(projectId) plus human-friendly CLI verb `ai-raccoon memory brief <project>`. Returns in one call: bank stats, top-5 recent entries, top-5 most-accessed, shared-tier count, pending-embed count, 2-3 suggested query formulations, and a pointer to memory-usage-guide. This removes the 2-3-call cost of the search-first ritual (the v2 audit's lever #1, taken to its logical end: the brief IS the ritual, one call, no skill loading required). Recency+access blend with labeled sections so it can't read as pure recency. Impact: high. Effort: M (TDD, xunit.v3). Evidence: INFERRED (trigger/habit gap is measured; brief targets it directly). Repo: ai-raccoon.

3. Inject the brief at session start on both hosts: extend the ai-badger Hermes plugin's on_session_start hook (drift_notice exists) and add a Claude SessionStart hook (session_start_hook.py pattern) that calls memory_brief when ai-raccoon tools are present and injects capped output (~400 tokens, snippets truncated, silent failure on any error). This makes the FIRST memory touch free and automatic — the strongest single habit lever. Impact: high. Effort: M-L (both transports, pytest for the hook). Evidence: INFERRED (M2 shows nothing fires today). Repo: ai-badger.

4. Add a memory check step to task-skill Phase 0: after the state.json step, run one memory_search with the task's scope keywords (max 2 formulations) and require citing hits in the plan; no-hit path = search externally then memory_write the finding back. Phase 0 is the only reliably-triggered ritual in ai-badger sessions, and it currently has no memory step (M10). Impact: high. Effort: S (skill text + review; scripted helper if any, pytest). Evidence: INFERRED (audit lever #3). Repo: ai-badger.

5. Regenerate the ai-badger repo's own mcp-tools.json (`mcp-index update --host hermes` and the Claude/Copilot equivalent). Measured: zero ai-raccoon entries in ai-badger's index, so the per-turn recommendation hook can never fire for memory there (M4). Impact: high. Effort: S (one command + validate). Evidence: MEASURED (M4). Repo: ai-badger.

6. Add a `memory` tag to the mcp-tags taxonomy, retag memory_* tools, and extend the catalog intents with trigger phrases ("remember", "project knowledge", "context", "what did we decide") so the BM25 recommendation fires on prompts that don't literally say "search". Impact: med. Effort: S. Evidence: INFERRED (M4/M6; jsaa has the index yet still zero searches — index presence alone is insufficient, keyword coverage is the lever). Repo: ai-badger.

7. Seed the shared tier — curated, never automatic: add CLI verb `ai-raccoon memory promote <project> <hash|query>` (wraps memory_share, prints what it promoted and why, dry-run default with --apply), then run an owner-approved seed task promoting the 3 genuinely cross-project facts that already exist (the 1.0.4 multi-RID tool fix, the watch-tools MCP regression note, and a project-id/conventions fact). ai-badger may ship a seed-pack script that PROPOSES candidates and requires explicit confirmation. The tier is empty; promotion only pays once consumption exists, so seeding is the cold-start unlock (audit lever #2). Impact: high (network effect). Effort: S-M. Evidence: INFERRED (zero shared rows measured). Repo: ai-raccoon (CLI) + ai-badger (seed pack).

8. Surface "share candidates" in the weekly report: entries accessed from >=2 projects, or rating > 0.6 with recent access — listed for owner review, never auto-promoted. Impact: med. Effort: S (part of the report script). Evidence: INFERRED. Repo: ai-raccoon.

9. Upgrade the grading loop without nagging: (a) the one-line ask gains a trailing stats fragment ("week: 9 searches, 3 graded, avg 4.3") so grading becomes a visible habit with a score; (b) add an auto-judge backfill that writes usefulness_auto (never overwrites a human grade) so the KPI can show estimated quality when humans skip — clearly labeled in the report; (c) weekly report shows graded % as loop-health. Current state: 2/11 graded, the ask is easy to ignore (M1/M9). Impact: med. Effort: S-M (pytest for the ask format + auto-judge). Evidence: INFERRED. Repo: ai-badger.

10. Verify Claude coverage with a probe: run one organic memory_search inside a Claude session in the ai-badger repo and confirm exactly one host=claude line lands (hook path is wired per M5; the script fallback paths exist). This converts the open "never searches vs hook doesn't fire" question into a measured fact and is the gate for all Claude-side investment. Impact: med (diagnostic). Effort: S. Evidence: MEASURED gap (M2/M3). Repo: ai-badger.

11. Build the weekly adoption report: scripts/adoption-report.py reading memory-quality.jsonl + memory.db (read-only) + mcp-stderr.log error counts, run by cron, writing docs/work/adoption-weekly.md and printing a digest. This closes the feedback loop (audit lever #5) and feeds recommendations 8 and 9. Impact: high. Effort: M (script + fixture tests). Evidence: INFERRED. Repo: ai-raccoon (owns the DB; reads ~/.ai-badger paths).

12. Docs and announcement: README "Using AiRaccoon with agents" section; docs/how-to/use-memory-with-agents.md (brief ritual, CLI verbs, grading, report, Claude notes); changelog entries in both repos (ai-raccoon next release, ai-badger 0.82.0) with a short announcement describing the brief + seeding + report. Impact: med. Effort: S. Evidence: INFERRED. Repo: both.

13. Add a `mem:` / `remember:` prompt marker ("treat this as a durable fact — memory_write it with a source") via markers-context.json (no-code) + tests. Gives the user a machine-detectable way to demand writes, targeting the writes KPI (5 organic writes ever). Impact: med. Effort: S. Evidence: INFERRED. Repo: ai-badger.

14. Settle access_count semantics with a code read of the search path (SqliteMemoryStore.SearchAsync), then decide whether search hits bump access marks; gate K9 on this. Impact: med (KPI soundness). Effort: S. Evidence: MEASURED open item (F9). Repo: ai-raccoon.

15. Telemetry health guard: the report must fail loudly (a WARN line) when AI_BADGER_MEMORY_GRADE is unset or the log is stale (no lines in 7 days while Hermes is active), so telemetry loss never masquerades as zero usage. Impact: med. Effort: S. Evidence: INFERRED (audit notes the log's start boundary). Repo: ai-badger.

(2) FIRST-30-DAYS ADOPTION SPRINT (ordered, each with acceptance criteria)

Week 0 — preconditions (days 1-3):
A1. Build + install PR #47 as the bridge tool. Accept: watch-error count in mcp-stderr.log stops growing within 24h; memory_stats pending = 0.
A2. Regenerate ai-badger repo mcp-tools.json. Accept: `mcp-index validate` exits 0; memory_search present with catalog intent.
A3. Claude probe (rec. 10). Accept: exactly one host=claude line in memory-quality.jsonl from one manual search in a Claude session.
A4. Baseline snapshot via the report script v0 (ad-hoc). Accept: recorded baseline for searches/wk, writes/wk, shares, graded %.

Week 1 — the habit loop (days 4-10):
A5. Ship memory_brief tool + CLI brief verb (TDD). Accept: xunit tests green; one manual brief call returns all sections with correct counts vs SQL.
A6. Hermes session-start brief injection. Accept: pytest green; next Hermes session in ai-raccoon shows the brief once; injected <= ~400 tokens.
A7. Task-skill Phase 0 memory step. Accept: skill text merged; one /task run shows memory_search in Phase 0 before any planning output.
A8. Owner seed task (rec. 7): promote the 3 known facts via the new CLI. Accept: memory_stats shows shared > 0; a scope=shared search from a fresh session returns all 3 seeds ranked.

Week 2 — Claude + the loop (days 11-17):
A9. Claude SessionStart brief injection (or UserPromptSubmit nudge if A3 failed). Accept: probe session shows the brief; zero hook errors in Claude logs.
A10. Grading ask upgrade with stats fragment + auto-judge backfill. Accept: pytest green; the ask text contains week stats; at least 3 graded searches in the following 7 days.
A11. Weekly report v1 + cron. Accept: docs/work/adoption-weekly.md generated on schedule with every KPI section populated; first issue committed.

Week 3 — content and docs (days 18-24):
A12. README/how-to docs, changelogs, announcement (rec. 12). Accept: docs linked from README; changelog entries in both repos.
A13. `mem:` prompt marker (rec. 13). Accept: marker test passes; one manual marker prompt results in a memory_write with a source path.
A14. Second seed round from the report's share candidates (owner-confirmed only). Accept: shared rows grow by exactly the owner-approved count.

Week 4 — measure and iterate (days 25-30):
A15. Month-end measurement vs baseline. Accept: report shows per-KPI deltas; each lever (brief, ask stats, marker, seeds) explicitly kept, cut, or tuned; follow-up issue filed with MEASURED deltas only.
A16. Claude organic check: any host=claude searches this month? If zero and A3 passed, escalate — add memory keywords to Claude's context_enrichment hook and re-check next month.

(3) KPI DEFINITION

Sources: ~/.ai-badger/memory-grade/memory-quality.jsonl (searches, grades, host, project, session), ~/.ai-raccoon/memory.db read-only (writes, shares, access, ratings, pending), ~/.hermes/logs/mcp-stderr.log (watch error counts). Reader: the owner, weekly, via the report; the grade-ask stats fragment is the in-loop read.

- K1 organic searches/week — log rows with host!=null, projectId not tool-test-*; grouped by host and project. Growth KPI. Baseline ~4/wk.
- K2 grade coverage — graded (human usefulness!=null) / total searches. Loop health; target >= 50% after week 2.
- K3 avg usefulness — human first; usefulness_auto reported separately and never blended. Quality gate (current: 4.5 on n=2).
- K4 organic writes/week — DB rows with source_file NULL/empty, project in the three real ids, created in window (F4 method). Target >= 1/day across projects by week 4.
- K5 shares/week + shared tier size — DB scope='shared' count (the log doesn't capture shares; DB is authoritative). Target: 3 seeds month 1, then slow curated growth.
- K6 shared-tier hits — scope=shared searches returning >0 results (proves the tier is consumable).
- K7 miss rate — searches returning 0 results from the result payload.
- K8 embed/watch health — pending embed count (target 0) and watch error count/week from mcp-stderr.log.
- K9 corpus engagement — % entries with access_count>0. Gated on rec. 14 (semantics currently unsettled).
- K10 telemetry health — log freshness + env var presence; a status line in the report, not a growth KPI.

(4) RISKS

1. Context bloat from brief/hook injection (token cost + distraction). Mitigate: hard caps, truncated snippets, silent-failure discipline (existing hook ethos); measure injected tokens in the report.
2. Grade fatigue — repeated asks train agents to skip, degrading K2/K3. Mitigate: one line, once per search, stats make it self-rewarding; never block on it.
3. Shared-tier wipe — memory_delete_context('shared') deletes the entire tier (proven in the tool test); one wrong call loses all seeds. Mitigate: document prominently; require an explicit confirm flag when deleting the shared context.
4. Bad seeds poison every project (shared is cross-project by design). Mitigate: seeding stays owner-confirmed; the framework seed pack is proposal-only, never auto-promoting.
5. Spam writes degrade retrieval (chatter). Mitigate: write discipline stays in the skill; the weekly report lists recent organic writes for spot-check.
6. Goodhart — search count up, usefulness down. Mitigate: K2/K3 as the counterweight; the grade loop is the quality gate, not a vanity metric.
7. Recommendation-hook noise — per-turn MCP injections annoy and get ignored. Mitigate: keep the BM25 gate and TOP_N=3; tag conservatively.
8. Telemetry loss masquerading as zero usage (env var drift on new shells/launch paths). Mitigate: K10 health check in every report + the probe instruction in the skill checklist.
9. Claude deferred tools — even with hooks, the model may ignore the nudge; tools are invisible until discovered. Mitigate: A3 probe gates all Claude investment; escalation path is the context_enrichment keyword list + SessionStart brief.
10. KPIs built on access_count/rating before semantics are settled would mislead. Mitigate: K9 gated on rec. 14; rating-based claims labeled.
11. Live bridge version skew — anything measured before A1 is against the pre-fix build. Mitigate: install first, measure after; state the tool version on every report.
12. Future cloud sync breaks local-only telemetry. Mitigate: report reads DB + log only (sync-agnostic by construction); revisit when sync is configured.
13. Brief exposure of recent-but-irrelevant entries could mislead. Mitigate: recency+access blend, labeled sections, source paths retained, agents still cite.
14. Widening the ai-raccoon-memory skill trigger risks over-triggering and skill fatigue. Mitigate: do NOT widen the trigger; Phase 0 step + hooks are the controlled triggers.

Bottom line: quality is not the problem (graded searches scored 4/5 and 5/5) and plumbing is not the problem (tools, hooks, and prose exist everywhere; the Claude gap is explained by no-sessions-since-declaration in jsaa and never-invoked deferred tools in ai-badger). The problem is that nothing makes the first memory touch automatic or free. The brief (recs 2-3), the Phase 0 step (rec 4), the ai-badger index fix (rec 5), and curated seeding (recs 7-8) are the four highest-leverage moves; the report (rec 11) turns the whole thing into a measurable, self-correcting loop within 30 days.