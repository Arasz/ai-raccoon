# Research: AiRaccoon memory usage audit v3 — post-fix re-measurement

**Date:** 2026-08-06
**Question:** How does the live memory bank look after the v2 fix cycle (PR #47 → tool 1.0.8/1.0.9) and the adoption work (shared tier seeded, extract service #55)? Did the fixes hold in production, and is usage/adoption moving?

**Method:** 20-minute observation window 14:46–15:06 local (12:46–13:04 UTC), 10 samples at 2-minute intervals over the live bank (~/.ai-raccoon/memory.db, read-only SQL), the bridge log (~/.hermes/logs/mcp-stderr.log), the grade log, live MCP probes, and code reads. MoE: 3 expert lanes analyzed independently in parallel (usage/adoption, reliability/fix-verification, data-quality/growth-model); findings merged below. All timestamps UTC unless noted.

## Environment (measured)

- Installed tool `~/.dotnet/tools/ai-raccoon` = **1.0.9+5a61b5c**, and 5a61b5c **is the PR #55 feature-branch tip** (the squash f9e1eea and the branch tip are tree-equivalent siblings — an ancestry check against the squash is invalid). The concurrent session swapped the official 1.0.9 for the local #55 build at 14:42 local and ran `extract enable true` + `extract mode promote` against the live bank. `ai-raccoon extract list` → `enabled: True, mode: promote, interval: 60 min`: **the hosted extraction service is installed, configured, and running in the 1.0.9 stdio servers** (first auto-pass expected ~13:45 UTC = 15:45 local, after this window; not yet observed at write time).
- Live processes during the window: PID 3336 HTTP bridge 127.0.0.1:5094 (the Hermes-agent MCP target), started Aug 5 19:46, **pre-fix 1.0.7-era build**; ~7 stdio MCP servers (hermes watchdog pairs), 1.0.6–1.0.9 mix (histogram: 1.0.6 ×36, 1.0.7 ×2, 1.0.8 ×12, 1.0.9 ×36); 2 stale worktree dev servers — PID 11882 (w6-dual-vector DEBUG, :5081, up since Aug 4, **cwd = the w6 worktree, default data-root → writes the LIVE bank**) and PID 78436 (memory-share-extract scratch, :5096, `--data-root /tmp/measure-root`).
- Version skew is real but inverted vs v2's fear: the erroring stdio instances predate 1.0.8; the bridge is now the only pre-fix process and its stderr is not captured anywhere monitored (its errors never appear in mcp-stderr.log).

## 20-minute series (10 samples, 2-min intervals)

| ts (UTC) | entries | pending | watchErr | grade | db MB | wal MB | created/10m | nProcs | ΣRSS MB |
|---|---|---|---|---|---|---|---|---|---|
| 12:46 | 2882 | 0 | 76 | 12 | 23.5 | 19.6 | 43 | 15 | 1467 |
| 12:48 | 2886 | 0 | 76 | 12 | 23.5 | 19.6 | 47 | 13 | 1231 |
| 12:50 | 2886 | 0 | 76 | 12 | 23.5 | 19.6 | 47 | 9 | 1461 |
| 12:52 | 2886 | 0 | 76 | 12 | 23.5 | 19.6 | 47 | 5 | 231 |
| 12:54 | 2886 | 0 | 76 | 12 | 23.5 | 19.6 | 47 | 5 | 319 |
| 12:56 | 2886 | 0 | 76 | 12 | 23.5 | 19.6 | 4 | 13 | 1177 |
| 12:58 | 2886 | 0 | 76 | 12 | 23.5 | 19.6 | 0 | 13 | 1261 |
| 13:00 | 2899 | 0 | 76 | 12 | 23.5 | 19.6 | 17 | 14 | 1421 |
| 13:02 | 2899 | 0 | 76 | 12 | 23.5 | 19.6 | 17 | 14 | 1466 |
| 13:04 | 2899 | 0 | 76 | 12 | 23.5 | 19.6 | 17 | 14 | 1509 |

## Findings

### A. Fix verification (v2 F12–F16 → v3)

**A1 — F12 is LIVE and PROVEN, with a positive sync event: watch-error count flat at 76 across all 10 samples; zero errors from ~22 post-fix server starts [MEASURED]**
The error count grew to 76 by ~12:45 UTC and then stopped; the last error line (1349893) predates the 13:33:32 UTC banner and no "Watch event source error" line exists after it (log has ~22 subsequent ai-raccoon starts, 0 errors). The fix went live with **1.0.8** (~13:30 local installs), not 1.0.9. **Positive proof:** `mcp-tools.json` mtime 12:44:27 UTC and the file watch's `last_change_ts` 12:44:28 UTC — a post-fix instance ingested a real file change 1 s after it happened; the watcher is not merely error-free, it syncs. **Correction to v2's framing:** the 56→76 growth was NOT the HTTP bridge retrying (~1/7 min) — the bridge's stderr never lands in mcp-stderr.log (no `transport http` lines); all 76 errors are per-start retry pairs from pre-fix stdio instances (12 at the 11:12:05 instance, 2 per instance thereafter).

**A2 — The agent-facing bridge is still pre-fix and unmonitored: split-brain state, silent risk [MEASURED]**
PID 3336 (5094, the path Hermes agents use) still runs the broken `FileSystemWatcher(file)` implementation. It answers `memory_watch_status` with `state=Healthy` for the file watch — now accurate-by-luck (the lastSync 12:44:28 is a real sync performed by post-fix siblings), but its own watcher cannot start and its errors are invisible (stderr not redirected). If the stdio channels closed, the file watch would silently break again with no log trail. Restarting 3336 onto the installed 1.0.9 closes both the capability gap and the blind spot.

**A3 — F13 holds, stronger than v2: 0 pending at every sample, +17 rows created during the window all embedded [MEASURED]**
embed_state = {embedded: 2882→2899} ×10 samples; `memory_stats` pending=0. The WatchDigestExecutor retry-embed works live.

**A4 — F14 holds exactly: jsaa 761 rows, 761 source_file, 672 sections, 0 agent_id; stable through the window [MEASURED]**
Total sections 673 = 672 jsaa + 1 ai-raccoon. `heading_path` 0, `structure_embedding` 0 bank-wide (v2 out-of-scope decision; unchanged).

**A5 — F15 holds, with a description drift: config ghosts gone; workspaces = 3 acme rows, 2 Active + 1 Closed (v2 said all Closed) [MEASURED]**
No `CLAUDE.md`/`tool-test` settings keys; watch_files 112 → 118→120 (6–8 new tracked files); the 2 Active acme workspaces are empty shells (0 entries carry workspace_id) — the ghost-cleanup claim itself is unaffected.

**A6 — F16 not re-runnable here (read-only audit); v2 suite evidence stands; no new error classes since v2, series window error-clean [MEASURED]**
Error-class scan: Watch event source error ×76 (last ~13:28 local); ObjectDisposedException ×346 (last between 07:47/09:13 banners — pre-v2); InvalidOperationException ×4 (09:13, "Bundled embedding model 'model_qint8_arm64.onnx' not found" — a worktree dev run, not the live bank); OperationCanceledException ×279 (shutdowns). Zero `fail:`/`Exception` lines after the 14:45:02 banner — the entire window is error-clean.

### B. New capabilities and adoption

**B1 — Shared tier: seeded by design at 11:24 UTC per v2 lever 2; findable (top-3 on a live scope=shared search) but consumed zero times organically [MEASURED]**
Rows 4302/4303/4305 = the multi-RID tool-fix fact, the watch-tools MCP regression, and a deployment fact (the latter: organic write at 11:24:35 promoted 4 s later — scripted seeding, not agent work). All embedded. Adoption-plan KPI K5 (3 seeds) met; **K6 (shared-tier hits > 0) not met** — no organic scope=shared search exists (the only two, 09:17 UTC, are tool-test probes predating the rows).

**B2 — Extract auto-promotion is CONFIGURED but NOT yet executing: no running server has the #55 binary [MEASURED]**
`ai-raccoon extract list` on the installed tool: `enabled: True, mode: promote, interval: 60 min` — the swapped 1.0.9+5a61b5c binary (PR #55 feature-branch tip) contains the service and the settings are armed. **However, at 13:48 UTC no running ai-raccoon process executes it**: every live stdio server (started 07:52–14:39 local) predates the 14:42-local binary swap and runs the official 1.0.9 (no #55); the only post-swap instance (14:45 local banner) exited within minutes; the 5094 bridge runs 1.0.7-era. Confirmed by zero "Extraction pass" log lines anywhere in mcp-stderr.log despite a pass being due at ~13:45 UTC (PeriodicTimer first tick = start + 60 min, and a completed pass always logs at Information: EventIds 502/504). **Structural blocker: the Hermes gateway recycles stdio MCP connections on a strict ~5-minute cadence (1048 ai-raccoon registrations today, 5min03s apart)** — each spawned instance serves the initialize/tools-list handshake, idles, then shuts down cleanly on client stdin EOF ("transport completed reading messages" → "Application is shutting down...", no crash traces). A recycled stdio instance can therefore never reach a 60-minute first tick: auto-promotion requires a process that survives ≥60 min (a long-lived session server or the HTTP bridge). Follow-up task (in flight): gate the hosted service to the HTTP/s transport (it is pointless in stdio) and lower the default interval to 30 min. Caveats (from the #55 review): the interval read sits outside the exception shield (SHOULD-FIX S1), and promote shares without confirmation by design.

**B3 — Custom scope used exactly once, apparently by accident: row 4376 is stale (the 5 retrieval-gate failures it records were fixed by #54), invisible to project/all search, and structurally permanent [MEASURED]**
`context_label='final-suite classification'`, `source_file=tests/.../SourceIdentityTests.cs`, rating 0.5, access 0. The `context` param silently set scope='custom' — exactly the trap the adoption plan's WP2 (reserved-context rejection) proposes to block.

**B4 — Adoption: +1 organic search since v2 (10:52, rank-1 hit, ungraded); zero searches in the window; grading coverage 2/12; still zero claude/copilot lines [MEASURED]**
Organic total 5 in the grade log's lifetime (plus this audit's own probe at 13:07:44 — the log's 13th line). The new organic search ("watch tools registration McpServerSetup") returned the exactly-right doc at rank 1 — retrieval quality holds when tried. The ask-after-search loop fires ~17% (K2 target ≥50%). 10:52 → 13:04 UTC: the busiest work period of the day, zero memory reads.

**B5 — v2 adoption-lever scorecard: supply shipped, demand is paper [MEASURED/INFERRED]**
L1 memory_brief — no traction (unshipped, not on the 20-tool surface). L2 seed shared tier — shipped (B1), demand zero. L3 memory in the default loop — minimal (the one new organic search; WP5/WP6 unshipped). L4 retrieval ceiling — holds (0 pending, provenance contract, gates re-pinned #54, error loop dead). L5 KPI loop — machinery exists, loop weak (2/12 graded, log demonstrably incomplete — the jsaa re-ingest's self-search bumps at 10:59 are unlogged, no weekly report).

### C. Data quality and growth model

**C1 — Growth is event-driven file-ingest, NOT a steady churn rate: the "43 rows/10 min" was one new-file ingest (agent-memory-server.md); the series decays to 0 between events [MEASURED]**
created_last_10min: 43 → 47,47,47,47 → 4 → 0 → 17,17,17 — the 43 = one doc (ids 4566–4608, mtime matches to the second), the 17 = the integration-review doc. Today decomposes into ~10–12 discrete file events (08:16: 157; 09:xx: 45; 10:13 watch-init: 171; 10–11h jsaa re-ingest: 761; 11:01 audit doc: 63; 12:21 wave4: 52; 12:46 agent-memory: 43; 12:59 integration-review: 17). A linear 43/10min extrapolation (~186K/month) is wrong by 10–30×.

**C2 — Zero version accumulation exists, and the 1.0.9 watcher cannot create it: replace-by-path [MEASURED]**
No source_file has rows on 2+ distinct days; no file has >60 s creation spread. `WatchDigestExecutor.cs:57-58` deletes the path then re-ingests, with a whole-file SHA-256(path+content) hash-skip (lines 44–52): an edit replaces the old generation. v2's "additive ingest on change" described the pre-fix build. Caveat: this holds only while every live watcher is 1.0.9+ — the 1.0.7 bridge and the zombie w6 server still run old semantics against the live bank.

**C3 — NEW: 14.2% of rows are duplicates (412/2,899, 176 groups, all intra-project; 0 cross-project collisions) — two mechanisms, one serious [MEASURED]**
(a) **Concurrent-watcher race:** 5–6 uncoordinated stdio servers all load the same watch config and race the non-atomic check-then-insert (`SqliteMemoryStore.cs:882-898`: SELECT-then-INSERT, no UNIQUE constraint, no wrapping transaction) — one chunk that occurs once in a doc exists 5× with interleaved ids/seconds. (b) **Zombie dev server:** the w6-dual-vector DEBUG build (PID 11882, up 42 h, worktree deleted, default data-root → the LIVE bank) blind-inserts pre-dedup ingests (one doc's duplicates sit in a single contiguous transaction). Dedup works in single-process paths (jsaa re-ingest 0 dups; unchanged files hash-skipped).

**C4 — Self-correction is structurally impossible today: 0 rows with ttl_days; sweep is a no-op by design; stale row 4376 is permanent [MEASURED]**
`DegradationPolicy.cs:10` degrades nothing without `ttlDays` (the global ttl knob was removed); SweepService exists but is manual-only and shared-exempt. Row 4376 (now factually wrong) has no TTL, default rating, no access path to re-rate — it can only be manually deleted. The bank's only removal stories are manual deletes and source-path deletes.

**C5 — Access/rating loop near-idle: 94.5% never accessed; the only bumps in the last hour were this audit's own probe [MEASURED]**
2,740/2,899 rows access_count=0; 159 accessed (143 once, 14 twice, 2 thrice). The v2→v3 drop (292→159) is the F14 jsaa re-ingest wiping ~130 marks; jsaa's 20 accessed rows were all bumped at creation (10:59:03–26 UTC) by the re-ingest's own spot-check searches — pipeline self-search, no organic jsaa consumption. The shared rows' access_count 0→1 at 13:07:44 UTC was this audit's live scope=shared probe: **search hits bump access (settling v2's open question — SqliteMemoryStore.cs:200-202, 930-951), and the audit itself is now part of the bank's history.**

**C6 — Memory cost: ~1.4 GB RSS for a 41 MB bank (~34:1); 6–7 copies of the bundled ONNX model resident [MEASURED]**
Per-sample ΣRSS 231 MB → 1.5 GB (5–15 processes; per-process embedding model ~150 MB). Bridge 3336 mean 147 MB. The per-channel stdio design is the footprint driver; the two worktree dev servers (~215 MB) are throwaway residue.

**C7 — Growth model (30 days): 6–9K rows quiet month / 18–21K heavy co-dev month (~55–170 MB); never the linear churn extrapolation [MEASURED base + INFERRED projection]**
Bank 2,899 rows in 19.5 h; ~7.8 KB/row live (22.1 MB db + 1.4 MB freelist + 19.6 MB WAL). Embedding throughput is not the constraint (a 43-chunk doc embeds+commits in seconds). Caveat: `created_at` is insert-stamped, not commit-stamped — a late-committing transaction explains the sampler's +4 "phantom" delta (samples 2–5) and any age logic must account for it.

**C8 — sync/workspace: zero production signal [MEASURED]** sync_meta 0, sync_tombstones 0, 0 rows carry workspace_id, 3 acme test workspaces (2 Active empty shells). No sweep.threshold / retrieval.alpha / encryption keys.

## Still open

- **First auto-promote pass not yet observed — and structurally blocked until the transport gating ships**: (a) no running server has the #55 binary (all stdio servers predate the 14:42-local swap; the post-swap instance exited quickly), and (b) the Hermes gateway recycles stdio connections every ~5 min, so a recycled stdio instance can never survive the 60-min first tick. Follow-up task (in flight): gate ExtractionHostedService to the HTTP/s transport and lower the default interval to 30 min; then verify on a long-lived HTTP bridge restart.
- **Zombie processes:** PID 11882 (w6 debug, writing the live bank with old semantics) and PID 78436 (scratch server) — owner decision to kill; the 5094 bridge restart onto 1.0.9 + stderr capture is the reliability fix (A2).
- **Multi-watcher dedup race (C3):** needs a UNIQUE index on (project_id, path, hash, scope, context_label, workspace_id) or a single-ingester watcher; currently the bank accumulates duplicates on every concurrent-session burst.
- **Self-correction (C4):** per-entry TTLs on watch-ingested rows + a scheduled sweep + access-driven re-rate; row 4376 should be manually deleted.
- **Adoption demand side (B5):** L1/L3 unshipped — memory_brief, session-start injection, task Phase-0 step; grading coverage 17% (K2 ≥50%).
- Bridge version skew means the bank's replace-by-path and dedup guarantees (C2/C3) are not yet guaranteed end-to-end.

## Verdict (merged from 3 lanes)

The v2 fixes **held in production**: F12 is live with positive sync evidence and an error-clean window, F13/F14/F15 verified, no new error classes, and the plumbing is in its best state ever — 0 pending, provenance contract intact, shared tier seeded and findable, and the extract auto-promotion engine installed and armed (first pass pending). The bank is written to heavily and read almost never: 5 organic searches in the grade log's lifetime, 94.5% of rows never accessed, the shared tier unconsumed — the supply side shipped, the demand side is still paper. Two NEW risks surfaced: 14.2% duplicate rows from multi-watcher races (plus a zombie dev server writing the live bank with pre-dedup semantics), and structural immunity to self-correction (no TTLs, sweep a no-op, one already-wrong stale row permanent). At 10× scale those two dominate: unbounded accumulation with no removal story, and duplication widening with every concurrent session.
