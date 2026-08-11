# Live-bank diagnostic — search + promotion quality audit (verified 2026-08-11)

Worked end-to-end on the real bank (`~/.ai-raccoon/memory.db`, 149 MB, live `serve` on 7721,
concurrent writers). Output shape: `docs/work/2026-08-11-ai-raccoon-diagnostic.md`.

## Order of operations

1. **Memory first** (`memory_search` scope=all) — surfaces prior reports (mem-test,
   shared-tier curation, promotion audits) and log paths before any file search.
2. **Log inventory** — what each log actually covers (see below).
3. **WAL-safe copy** — Python sqlite3 backup API (below), then analyze the COPY, never the live
   file.
4. **Quantitative pass** — schema dump → per-project/per-tool/per-day tables → queue audit.
5. **Perceived-quality pass** — graded-search log stats + 3-4 live probes (project-scoped via
   the MCP bridge with projectId; the built-in hermes bridge searches only hermes-default +
   shared).
6. **Report** — findings numbered with evidence; recommendations with gates.

## Log inventory (what each file covers)

| Path | Covers | Gaps |
|---|---|---|
| `~/.ai-raccoon/memory-operations.jsonl` | tool ops {ts, tool, project_id, status, duration_ms, agent_id, session_id} | **hermes provider channel ONLY** (memory_search/write/stats/share — 4 of 23 tools); bridge/HTTP calls unlogged; early rows have placeholder session ids (`s1`) |
| `~/.ai-badger/memory-grade/memory-quality.jsonl` + `pending.json` | graded memory_search calls (usefulness 1-5 + note) | only ~12 % ever rated (grading fatigue); agent can self-grade via `python3 ~/.hermes/plugins/ai-badger/memory_grade.py grade <ts> <1-5> [note]` |
| `~/.ai-raccoon/{quiet,serve,rollout}.log` | extraction passes, serve lifecycle | quiet.log covers only the period a --quiet serve ran; the current serve may not append |
| `maintenance-stats.json` | db/wal bytes | single sample, not a series |

## WAL-safe copy of the LIVE bank

```python
import sqlite3, os
src = sqlite3.connect(f"file:{os.path.expanduser('~/.ai-raccoon/memory.db')}?mode=ro", uri=True, timeout=30)
dst = sqlite3.connect("/tmp/ai-raccoon-diagnostic/memory-copy.db")
src.backup(dst)   # online backup API: consistent snapshot incl. WAL, safe with the server writing
dst.close(); src.close()
# 149 MB in 0.3 s; PRAGMA integrity_check = ok
```

The backup API needs no vec0 module (page-level copy), unlike reading vec0 tables (system
sqlite3 lacks vec0 — see the vec0 pitfall in SKILL.md). Plain tables (entries, promotion_queue,
settings, watches, workspaces) read fine with stock Python sqlite3; FTS5 works too.

## Semantics that mislead audits

- **`waitingPromotionsCount` / capacity meta is the ASKING project's queue.** The hermes
  bridge defaults to project `hermes-default` → meta shows `used: 11` while the DB has 1,000
  rows bank-wide and 278 for ai-raccoon. The mem-test saw 246 because it passed projectId.
  Always state which project the meta counts.
- **`memory_share` returns `{shared:true}` unconditionally (idempotent)** — a promotion audit
  that counts `shared:true` responses over-counts re-promotions. Verify with the shared-tier
  timeline: `SELECT date(created_at,'unixepoch') day, count(*) FROM entries WHERE
  scope='shared' GROUP BY day` (2026-08-11 audit: 17 reported, only 5 new rows).
- **Memory entries can cite phantom report paths.** The 08-11 promotion audit wrote
  `sourceFile: docs/work/2026-08-11-memory-promotion-report.md` into memory; the file never
  existed (`git log --all` + file search). Verify cited docs exist before trusting a citation.
- **Queue rows carry reasons as JSON arrays in one column** — parse with `json.loads` when the
  value starts with `[`, else comma-split; a naive comma-split double-counts tags.

## Queue-audit SQL (the "top-N audit")

```sql
-- already-shared: queue row whose VALUE exists in the shared tier (hash differs by design —
-- shared rows are re-hashed against their shared/ path, so join on value, never on hash)
SELECT count(*) FROM promotion_queue q
WHERE EXISTS (SELECT 1 FROM entries e WHERE e.scope='shared' AND e.value = q.value);

-- shared-tier formats: value-addressed (new, post-1.6.3) vs legacy path-addressed (pre-1.6.3)
--   value-addressed = path ~ '^shared/[0-9a-f]{64}\.md$'; the rest are legacy "shared/<orig path>"

-- noise tags bank-wide: reasons LIKE '%mid-sentence%' / '%changelog-entry%' /
--   '%status-vocabulary%' / '%superseded%' / '%ephemera%'; source_file LIKE '%/docs/work/archive/%'
--   and 'hermes/%' / '.claude/projects/%' for the session-transcript class

-- orphans: queue rows with no backing project-scope entry (ADR-0023 era; 0 after 1.6.0)
-- vec coverage: vec_entries_rowids / vec_structure_rowids are PLAIN shadow tables (countable);
--   entries.heading_path vs structure_embedding columns give the eligible-coverage check
```

Top-50 audit verdict (2026-08-11): 19/50 already-shared values, 19/50 noise-tagged,
~⅓ genuinely new durable candidates — the number the mem-imp-1 fix gates on (~0 / ~0).

## Perceived-quality pass

- Graded log: count rated vs unrated by day (coverage collapses: 08-10 = 1/37); avg + dist per
  project; read the notes — they carry the qualitative verdicts ("decisive hit", "session
  transcripts = noise").
- Live probes: use the FULL MCP bridge (`mcp__ai_raccoon__memory_search`, requires projectId)
  for project-scoped queries; the built-in hermes `memory_search` only reaches hermes-default +
  shared. Known-weak class to probe: identifier-heavy questions (ADR-0020 worked 5/5 on the
  live bank; the jsaa CI-gate probe put the canonical ADR-0016 at rank 3 behind plan docs).
- Session-transcript noise: `hermes/` sources (201 rows + 153 queue rows on 08-11) are the
  main precision drag; they also feed the propose queue (extract.exclude.prefixes=hermes/
  notwithstanding).
