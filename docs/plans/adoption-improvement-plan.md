# Plan: memory adoption improvement (MoE synthesis)

**Date:** 2026-08-06
**Context:** v2 audit (`docs/work/2026-08-06-memory-usage-audit.md`, F1–F16) measured adoption at ~4 organic searches/wk and 5 organic writes ever, zero shared rows, graded search quality 4/5–5/5. Owner extension (e:) mandates: (1) a plan to improve adoption, (2) two MoE expert reports with the task to increase adoption as much as possible, (3) a server-side integration task that checks each project's memories and extracts the shared-worthy ones, (4) hardened input-parameter validation.
**MoE reports:** `docs/work/2026-08-06-adoption-moe-report-a-systems.md` (systems/architecture lens) and `docs/work/2026-08-06-adoption-moe-report-b-workflow.md` (adoption/workflow lens). Both agree: quality and plumbing are NOT the problem — nothing makes the first memory touch automatic or free.

**Goal:** measurable growth of organic search (K1), grade coverage (K2), organic writes (K4), and shared-tier size + consumption (K5/K6) over a 30-day sprint, with a self-correcting weekly report loop.

## KPIs (sources: memory-quality.jsonl, memory.db read-only, mcp-stderr.log; reader: owner weekly)

| KPI | Metric | Baseline | Target (30d) |
|---|---|---|---|
| K1 | organic searches/week (host≠null, not tool-test-*) | ~4/wk | ≥ 20/wk |
| K2 | grade coverage (graded/total) | 2/11 | ≥ 50% |
| K3 | avg usefulness (human grades; auto-judge reported separately) | 4.5 (n=2) | ≥ 4.0 |
| K4 | organic writes/week (source_file NULL, real projects) | 5 ever | ≥ 1/day |
| K5 | shares/week + shared tier size | 0 | 3 seeds m1, curated growth |
| K6 | shared-tier hits (scope=shared searches with >0 results) | 0 | > 0 weekly |
| K7 | miss rate (0-result searches) | n/a | downward |
| K8 | embed/watch health (pending count, watch errors/wk) | 0 / 56 baseline | 0 / no growth |
| K9 | corpus engagement (% access_count>0) | 11% | gated on access_count settlement |
| K10 | telemetry health (env var + log freshness) | ok | always WARN loudly when lost |

## Work packages (one PR per task; repo noted)

### WP1 — `memory_share_extract` integration task (OWNER REQUIREMENT) — ai-raccoon
Server-side shared-extraction: checks memories from each project and extracts the shared-worthy ones. Full design in Report A §2 (contract, criteria, safety) — summary:
- Tool `memory_share_extract(projectIds[1..8], mode=propose|promote, limit 1..50, includeTtlRows=false, sourceTypes=project)`; propose = Read access, promote = Write access (parity with memory_share).
- Core `SharedExtractionService` (pure, testable): mechanical signals only (no LLM): organic write (+2), cross-project reference (+2), usage signal access_count>0/rating>0.5 (+1), recency 30d (+0.5); excludes workspace/label/shared/pending rows, TTL rows unless opted in; three exact dedup checks vs existing shared rows (value, path `shared/<path>`, already-shared).
- Promote reuses the existing idempotent ShareAsync path; the tool NEVER touches any delete path (delete_context('shared') hazard stays unreachable).
- "memory_share never automatic" preserved: propose is the default and the only advertised mode.
- Tests: selection, ranking pin, dedup + idempotent re-promote, access tiers per projectId, cold-start (empty tier → candidates; empty bank → empty result), never-deletes assertion; inventory 16→17 + name parity.
- Gate: full suite + script gates. Effort: M.

### WP2 — Input-parameter validation hardening (OWNER REQUIREMENT) — ai-raccoon
Full 19-tool matrix in Report A §3. Highlights: `project_id` format/length → `invalid-params`; `hash` `^[0-9a-f]{64}$` + `hash-not-found` mapping; **BLOCK `memory_delete_context(context='shared')` → `shared-context-protected`** (measured data-loss path, currently unprotected); reserved-context rejection on `memory_write(context=...)` → `reserved-context` (kills the custom-scope invisibility trap); absolute-path checks → `path-not-absolute`; `contextLabel` requires scope=project → `context-label-requires-project-scope`; limit/weight bounds (search limit 1..100, rrfK 1..500, weights 0..100); workspaceId format + `workspace-not-found`; content cap 64KB → `content-too-large`. Value rules in Core validators (existing FluentValidation pattern), MCP-shape/safety rules in the tool layer as typed McpException (existing `watching-disabled` pattern), one shared `ToolArgs` helper (two consumers, identical rules).
- Tests: parametrized theories per rule; E2E scenarios for the shared-context block and reserved-context write.
- **Sequencing: WP1 first, WP2 second** — both touch MemoryTools.cs + inventory tests; separate sequential PRs.
- Gate: full suite. Effort: M.

### WP3 — Operational seeding of the shared tier (zero code, can run immediately) — ops
Owner-approved promotion of the genuinely cross-project facts that already exist: the 1.0.4 multi-RID tool-fix fact (id 986, ai-badger), the watch-tools MCP regression note (id 2577, ai-raccoon), + one conventions fact. Either direct `memory_share` (before WP1 lands) or `memory_share_extract(propose)` review → promote (after WP1). Accept: `memory_stats` shows shared > 0; `scope=shared` search from a fresh session returns the seeds ranked. Effort: S.

### WP4 — `memory_brief` one-call surface — ai-raccoon
Tool `memory_brief(projectId)` + CLI verb `ai-raccoon memory brief <project>`: bank stats, top-5 recent, top-5 most-accessed, shared-tier count, pending count, 2–3 suggested query formulations, pointer to memory-usage-guide. Removes the 2–3-call cost of the search-first ritual — the brief IS the ritual. Recency+access blend with labeled sections. Inventory 17→18. Gate: full suite + manual brief vs SQL parity check. Effort: M.

### WP5 — Session-start brief injection — ai-badger
Hermes plugin `on_session_start` (drift_notice pattern) + Claude `SessionStart` hook: when ai-raccoon tools are present, call memory_brief and inject capped output (~400 tokens, truncated snippets, silent failure). Makes the FIRST memory touch automatic. Gate: pytest green; one real session shows the brief once, ≤ ~400 tokens. Effort: M–L (both transports).

### WP6 — Memory in the default agent loop — ai-badger
(a) Task-skill Phase 0 gains a memory-check step (one memory_search with task keywords, cite hits in the plan; no-hit → search externally + write back). (b) Regenerate the ai-badger repo's own mcp-tools.json (MEASURED: zero ai-raccoon entries — the per-turn hook can never recommend memory there). (c) Add a `memory` tag to the mcp-tags taxonomy + trigger phrases ("remember", "project knowledge", "what did we decide") in catalog intents. Gates: skill merged; `mcp-index validate` 0; one /task run shows memory_search before planning. Effort: S–M.

### WP7 — Grading loop upgrade — ai-badger
Ask line gains a week-stats fragment ("week: 9 searches, 3 graded, avg 4.3"); auto-judge backfill writes `usefulness_auto` (never overwrites human grades, labeled separately in the report). Gate: pytest; ask format test; ≥3 graded searches in the following week. Effort: S–M.

### WP8 — Weekly adoption report + KPI visibility — ai-raccoon
`scripts/adoption-report.py` reading memory-quality.jsonl + memory.db (read-only) + mcp-stderr.log; cron weekly; writes `docs/work/adoption-weekly.md` + digest; lists share candidates (accessed from ≥2 projects or rating > 0.6 recent) for owner review, never auto-promoted; K10 health guard (WARN when env var lost or log stale). `memory_stats` gains sharedCount + recent search/share counts. Gate: script fixture tests; one generated issue. Effort: M.

### WP9 — `mem:` / `remember:` prompt marker — ai-badger
Markers-context.json (no-code) + tests: "treat this as a durable fact — memory_write it with a source". Targets K4. Gate: marker test passes; one manual marker prompt produces a memory_write with a source path. Effort: S.

### WP10 — Docs + announcement — both
README "Using AiRaccoon with agents" section; `docs/how-to/use-memory-with-agents.md` (brief ritual, CLI verbs, grading, report); changelog entries in both repos (ai-raccoon next release incl. WP1/WP2/WP4/WP8; ai-badger 0.82.0); short announcement. Effort: S.

### WP11 — Deferred (recorded, not scheduled)
- access_count semantics settlement (code read of SearchAsync) → gate K9 + wire rating/access into extraction signals (small WP1 follow-up).
- Claude coverage probe (one organic search in a Claude session; gate for all Claude-side investment beyond WP5 hooks).
- CLI operator batch `ai-raccoon extract` (stretch, only if WP1 usage shows need).
- Structure vectors (heading_path/structure_embedding): explicitly out of scope (tool surface lacks the field; revisit when section-targeted demand appears).

## Sequencing

1. **Precondition: install a build with PR #47 fixes as the live bridge tool** (release ≥1.0.8; the update watcher cron `ai-raccoon-1.0.8-update-watcher` monitors the upload). All post-fix measurements are invalid until then (watch-error noise, pending rows).
2. WP1 → WP2 (sequential, same files); WP3 anytime (immediate win — seed before WP1 if desired); WP4 after WP1/WP2 merge; WP5–WP7, WP9 ai-badger PRs can run in parallel (separate files); WP8 after WP4 (report covers brief usage) — or v1 report earlier; WP10 last.
3. Owner gates: seed content approval (WP3), extract promote runs (WP3/WP8), release + announcement.

## Risks (from Report B §4, condensed)

Context bloat from hooks (hard caps + silent failure); grade fatigue (one-line ask, never blocking); shared-tier wipe (delete_context('shared') — WP2 blocks it + docs); bad seeds poison all projects (owner-confirmed only); Goodhart (K2/K3 counterweight); hook noise (BM25 gate, TOP_N=3); telemetry loss masquerading as zero usage (K10); Claude deferred-tool invisibility (probe-gated); version skew (state the tool version on every report); brief showing irrelevant recency (blend + labeled sections + cite discipline).
