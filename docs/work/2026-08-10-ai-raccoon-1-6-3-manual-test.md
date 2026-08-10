# AiRaccoon 1.6.3 — manual test session (2026-08-10)

Task: `manual-test-1-6-3` — verify the fixes shipped in the last two versions on the live
deployment: watch replace-by-path collateral deletion (1.6.2, PR #254) and honest promotion
accounting + per-chunk sharing (1.6.3, PR #255). Method per the mcp-tool-surface-testing
skill: expectations written before calls, live contract outranks docs, dedicated test
project (`manualtest163`, zero residue verified after), SQL as the verification oracle.

## Environment (measured)

- Installed global tool `ai-raccoon` = **1.6.3+11dadc04** (`--version`); serve (PID 2004,
  `ai-raccoon serve --restart`) started 10:26:15 today on 127.0.0.1:7721 — the running
  server IS 1.6.3. Wire check: HTTP `/mcp` initialize → `serverInfo.version 1.6.3.0`;
  `tools/list` → 23 tools.
- Real bank `~/.ai-raccoon/memory.db` (WAL, ~147 MB): entries per project — jsaa 7,787 ·
  ai-raccoon 3,082 · ai-badger 1,764 · arasz-home-page 814 · hermes-default 123 (+ custom
  rows). **Shared tier: 68 rows** (jsaa 40, ai-raccoon 12, arasz-home-page 9, ai-badger 6,
  hermes-default 1) — up from 1 row at mem-test (2026-08-09); queue 241 rows bank-wide
  (was 998). Extraction hosted service: enabled, **mode=propose**; watches enabled per
  project (ai-raccoon, jsaa, ai-badger, arasz-home-page); access.mode=full.
- Watch pipeline runs in every process (serve + stdio bridges); extraction/sweep loops are
  HTTP-only (Dependencies.cs: `RegisterWatchServices` unconditional,
  `RegisterLongLivedBackgroundServices` gated on Http/Https).
- Tool payloads are wrapped `{"data":…,"meta":…}`; SQL helper
  `sqlite3 "file:$HOME/.ai-raccoon/memory.db?mode=ro"` (WAL-safe).

## Fix 1 (1.6.2) — watch replace-by-path deletes manual rows citing the watched file

Expected: `DeleteBySourcePath` now matches `path` (mirror/ingest rows carry the real path in
both columns) not `source_file` (manual rows carry `path=<sha256>.md` + caller's sourceFile),
so the digest replaces exactly the mirror rows.

### S0 — deterministic control (scratch server, re-runnable script)

`scripts/watch-replace-by-path-manual-test.py` (repo Debug build, fresh `--port` +
`--data-root`): **PASS** — manual write citing the watched file survives the digest; mirror
row replaced (`path == source_file == real path`, new content); 0 rows with the old token.

### S1.1 — manual row survives digest replace (live bank)

1. `memory_watch_add(manualtest163, /tmp/manualtest163-watch)` → echoed. Initial scan
   ingested `alpha.md` (wrx163a1) within 15 s (SQL row present, `embed_state=embedded`).
2. `memory_write(content=<wrx163m1 … mentions manualtest163b>, sourceFile=alpha.md)` →
   hash `4f1431…`, path `395741…md` (hex filename) — manual row shape confirmed in SQL.
3. Appended `wrx163a2` to alpha.md → digest ran (~20 s). **ACCEPT: manual row still present
   (count 1); exactly one mirror row whose value == current file content (wrx163a2 present,
   mirror value byte-identical to the file modulo the chunk-separator newline).** Note: the
   file was appended, not rewritten, so the old token legitimately remains in the mirror
   value — "old token absent" is the wrong assertion for appends; the correct one is
   "mirror value == current file content".

### S1.2 — queue round-trip: manual-backed candidate survives, mirror-backed orphans die

1. `memory_share_extract(mode=propose)` → manual row queued (score 2.60, reasons
   organic-note/durable-fact-language) alongside the alpha mirror chunk.
2. Appended `wrx163a3` → digest ran (~20 s). **ACCEPT: manual row survives (count 1) AND its
   promotion_queue row survives (count 1); 0 orphan queue rows** (queue hash with no
   backing project entry) — the old mirror chunk's queue row died with its backing row via
   the ADR-0023 trigger, the manual-backed candidate was untouched.

## Fix 2 (1.6.3) — honest promotion accounting + per-chunk sharing

Expected contract (docs/reference/agent-memory-server.md + PR #255): `promotedHashes` =
only actually-created rows; `absorbed` = identical value already shared / insert-race loss;
`skippedDuplicates` = whitespace-normalized value twins (checked FIRST); invariant
claimed = promoted + absorbed + skipped + failures; shared rows value-addressed
`shared/<sha256(value)>.md` so every promoted chunk gets its own row.

### S2.1 — N chunks of one file → N shared rows, all promoted, 0 absorbed

1. Wrote bravo.md (~1,000 words) into the watched dir; the watch digest ingested it as
   **5 chunks** (the real chunker is finer than the plan's 2-chunk guess — better
   coverage). `memory_ingest_file` returned `{indexed: 0}` (hash-skip dedup — already
   mirrored). SQL: 5 rows, `path == source_file == bravo.md`.
2. Propose → 5 bravo candidates + manual + alpha mirror listed; **3 of the 5 bravo chunks
   queued** (per-document flooding cap, score DESC) + manual + alpha mirror.
3. Discarded manual + alpha-mirror queue rows (S2.3 needs the manual row unshared) →
   queue = exactly the 3 bravo chunks.
4. `memory_share_extract(mode=promote, limit=3)` → **`promotedHashes` length 3** (35f472…,
   d7af7e…, a3e4be…), `absorbed` 0, `skippedDuplicates` 0, `failures` []. SQL: **3 distinct
   shared rows, each path byte-exact `shared/<sha256(value)>.md`** (computed independently
   in Python); queue drained (0). Invariant 3+0+0+0 = 3 ✓.

Pre-fix behavior this kills: one coalesced `shared/<filepath>` row + every chunk reported
promoted (measured 99 claimed → 64 rows in the 1.6.3 plan doc).

### S2.2 — value twins (whitespace variants) → 1 promoted + 1 skipped, never absorbed

1. Two `memory_write` calls, same prose, second with every space doubled → distinct hashes
   `0b6294…` / `89119e…` (byte-distinct values, separate rows).
2. Pruned the queue to exactly the two twins; promote limit=2 → **`promotedHashes` length 1
   (89119e…), `skippedDuplicates` 1, `absorbed` 0**, failures []. Invariant 1+0+1+0 = 2 ✓.
3. SQL: exactly **1** shared row for the twin pair, path byte-exact
   `shared/<sha256(promoted value)>.md`; queue drained.

Value-twin check runs before the absorbed check — the twin lands in `skipped`, never
`absorbed`, exactly as the code classifies (valueKey first, sharedPath second).

### S2.3 — re-share of the same hash → idempotent, no duplicate row

`memory_share(manualtest163, 4f1431…)` twice → both `{shared: true, context: "shared"}`,
no error; SQL: exactly 1 shared row for the value (ON CONFLICT DO NOTHING on (path,hash)).

### S2.4 — accounting invariant + drained-queue sanity

Empty-queue promote → `promotedHashes []`, `absorbed 0`, `skippedDuplicates 0`,
`failures []` (claimed 0). Invariant held on every promote call of the session (3+0+0+0,
1+0+1+0, 1+4+0+0 over HTTP).

### S2.5 — EventId 702 log-line field order (live)

The live serve writes to an interactive terminal (fd 1/2 → ttys001) — not grep-able, so
verified on a scratch serve (fresh `--data-root`, output file-redirected, same Debug build):
after a 2-candidate promote, the log line reads exactly

```
Promoted from the queue for logtest: 2 shared, 0 absorbed (already shared), 0 duplicate-skipped
```

Field order Promoted → Absorbed → Skipped — the order that shipped swapped once (1.6.3
commit history: "live test caught swapped args") and is now pinned by
`Promote_TwoChunksOfOneFile_LogLinePinsFieldOrder`. Also re-verified the accounting on the
scratch bank end-to-end (2 entries → 2 candidates → 2 promoted, 0 absorbed).

## Observations (no defects)

1. **Propose re-queues already-shared chunks.** After S2.1, a second propose listed the
   already-shared bravo chunks as candidates again (the old `shared/{row.Path}`
   `IsDuplicate` pre-check no longer matches value-addressed paths). Harmless: promote
   classifies them as `skipped` (value twin first), never double-inserts. The propose tier
   shows stale candidates until promoted — consistent with the documented "propose →
   review → promote" loop, worth knowing when reading `memory_promotion_list`.
2. **Per-document flooding cap interacts with per-chunk sharing.** A 5-chunk file queues at
   most 3 chunks (cap per doc); the remaining chunks never reach the queue via propose
   (they stay in the project tier; a later file change re-chunks them). Not a regression —
   the cap predates 1.6.3 — but the honest-accounting fix only sees chunks that are queued.
3. **`memory_share` reports `{shared: true}` unconditionally** — no `Created`/`absorbed`
   signal in the direct tool (the store-level idempotency is real: re-share creates no
   duplicate row). Absorption is observable only via `memory_share_extract(mode=promote)`.
   Cosmetic; docs table matches the response.

## Cleanup + zero residue

`memory_watch_remove` → CLI `watch remove manualtest163` (cleared enable/scope/concurrency
settings) → 6 shared rows deleted via `memory_delete` by their own shared hashes →
`memory_delete_context(project:manualtest163)` (9 rows; queue orphans die via trigger) →
fixtures removed. Final SQL: **0** in entries / shared / promotion_queue / watches /
watch_files / settings for `manualtest163`; watch list clean.

## Verdict

**PASS — both fixes verified on the live 1.6.3 deployment.** The 1.6.2 watch-digest fix
stops collateral deletion of manual rows citing watched files (mirror rows still replaced,
queue round-trip consistent). The 1.6.3 accounting fix is honest end-to-end: every promoted
chunk gets its own value-addressed shared row, twins skip, re-shares absorb, the invariant
`claimed = promoted + absorbed + skipped + failures` holds on every call, and the 702 log
line renders in the pinned field order. Zero test residue on the real bank.
