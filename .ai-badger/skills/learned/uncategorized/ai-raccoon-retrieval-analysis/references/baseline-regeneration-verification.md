# Baseline Regeneration Verification (Wave 0 "fix-baseline")

Verified recipe for reviewing/validating a jsaa-memory.db regeneration plan against the
actual repo. Snapshot: jsaa tree HEAD `0bb8ff8a7af47efe248add0c16bcf79e96054e19` (2026-08-04).

**Trust the pipeline, not plan-doc numbers.** Plan B's "Seeded: 681 chunks from 166 files"
(`docs/plans/retrieval-improvement-b.md:20`) is a stale seed log observation, NOT a target.
Running the curated pipeline at this HEAD yields **196 files → 762 chunks**
(docs:adr=456, architecture=98, rules=39, reference=36, invariants=22, explanation=20,
how-to=19, skills=19+11 references, agents=9, root-md=5, meta=3, remember=11, …).
Always re-run the chunker to get the real count for the pinned commit.

## Get the true expected count without touching the repo

`chunk-hash-map.json` is written next to `__file__`, so running in-repo would overwrite the
gitignored map. Copy the script out first:

```bash
cp scripts/ingest-jsaa-docs.py /tmp/review/ && cd /tmp/review
python3 ingest-jsaa-docs.py --chunk-only   # enumerate + chunk + hash map; NO MCP writes
```

Then assert every `expectedSource` in `scripts/baseline-queries.json` is a key of the
generated map. All 10 resolved at this HEAD. `structured_path` format is
`{source_prefix}:{short_rel}#{section}` — `docs/adr/0011-frontend-chassis-stack.md` →
`docs:adr:0011-frontend-chassis-stack.md#decision` (Decision section slug from `chunk_adr`,
which lowercases first). Note `enumerate_files` also pulls `.ai-badger/skills/*/references/*`.

## Hash chain (why hash-map matching works)

Written content (used by BOTH `build_hash_map` and `write_chunks_batched`, byte-identical):
`f"[{context}] {structured_path}\n\n## Source: {structured_path}\n\n{body}"`.
AiRaccoon stores `hash = SHA256(UTF8(SHA256(content).hex()+".md") + UTF8(content))`
(ContentHash.Of + WritePathFor — see ai-raccoon-pitfalls "SHA256 hash formulas") and dedups
by exact value (`SelectCommittedByValue`), so entry count can be < chunk count only via
identical written content. No duplicate structured_paths at this HEAD → expect exactly 762.

## Pitfalls that break a regeneration run

- **Slug bug:** `chunk_heading`'s `[^a-z0-9-]+` is lowercase-only → `## Framework` becomes
  `#ramework`. Only ADR chunks are safe. Match non-ADR expected sources by prefix before `#`.
- **Settings travel with the DB:** `memory_configure` writes GLOBAL keys
  (`embedding.provider`). A copied DB with provider set makes `SearchAsync` embed the query
  at query time and THROW if the gitignored `model_qint8_arm64.onnx` is absent; an empty
  settings table silently degrades to FTS-only. C# tests need `BundledModel.EnsureAsync()`
  (SHA-pinned; main-checkout copy at `src/AiRaccoon/Models` is valid — verified hashes match).
- **WAL:** `SqliteConnectionFactory` forces `journal_mode=WAL` → run
  `PRAGMA wal_checkpoint(TRUNCATE)` before copying memory.db; `sqlite3 integrity_check` +
  entry/embed_state counts after; copy only memory.db (never -wal/-shm sidecars).
- **Unreachable gate:** `run-baseline-queries.py` exits 1 unless expected-source matches ≥25,
  but only 10/35 queries carry `expectedSource` — re-base to 8/10 or make it configurable.
- **Access mode:** `AIRACCOON_ACCESS_MODE` defaults to `rw`; `memory_delete_context`
  (`--reset`) needs `full`. Prefer a fresh `AIRACCOON_DATA_ROOT` (env honored) over `--reset`.
- **Port:** launchSettings http profile = 8080; both scripts hardcode 5000 (AirPlay on macOS)
  — use `ASPNETCORE_URLS` + `MCP_URL`/`MCP_BASE` env overrides. MCP endpoint is `/mcp`.
- **Sweep infra limits:** `SweepMatrix` has no (1,0)/(0,1) ablation points and `SweepRunner`
  aggregates only k=10/20/30 — per-query nDCG@5 / ablation needs a custom loop, ranking by
  `result.Hash` (the DB `path` column is SHA256-derived, not the structured path).

## Determinism chain (why "two identical runs → identical top-5" holds)

FTS `ORDER BY bm25` (ties → rowid, SQLite-deterministic) → vec
`ORDER BY vec_distance_cosine, e.path` → RRF `OrderByDescending(Ranking).ThenBy(Path)`.
All deterministic. ONNX int8 inference is deterministic per machine — run the determinism
gate on the fixed CI host. Implement determinism as a dedicated double-run test (two hybrid
passes, compare per-query top-5 hash sequences); a single-pass report cannot prove it.
