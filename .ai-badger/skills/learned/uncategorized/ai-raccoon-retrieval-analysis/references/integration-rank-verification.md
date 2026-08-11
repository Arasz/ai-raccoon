# Wave-join integration rank verification (Plan C, 2026-08-04)

Protocol used for every parallel-wave merge in Plan C. The point: each wave's branch
passed its own gates; the JOINED state is a new measurement that must be taken from
scratch, rank deltas content-verified, and results appended to
`docs/work/2026-08-04-comparison-clean.md`.

## The per-merge protocol

1. Merge branch → main (resolve conflicts, remove duplicate method copies — see
   development-skill pitfall).
2. Full suite on main: `dotnet test` (the canonical gate).
3. Baseline re-measure:
   `dotnet test tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj --filter "FullyQualifiedName~RetrievalBaselineTests|FullyQualifiedName~BaselineMetricsTests" --logger "console;verbosity=detailed"`
   — grep the `A\d+|C\d+|S\d+:` per-query lines + the `nDCG@5=` category lines.
4. Content-verify every non-obvious rank change (user rule): map hash prefixes via
   `scripts/chunk-hash-map.json`, read `entries.value` with plain sqlite3, judge
   same-knowledge alternative vs real miss.
5. Append a dated section to comparison-clean.md: per-query table vs previous wave,
   metrics table, notes with the content-verification evidence, verdict
   (improve/degrade + analysis + plan revision). Commit.
6. Degradation rule: any real degradation → analyze WHY and revise the plan gate
   BEFORE the next wave (C2 → Wave 4 gate criterion; A4 boundary → Wave 1 trigger fix).

## Live-probe recipe (server + MCP over SSE)

Start the server against a scratch copy of the committed corpus:

```
rm -rf /tmp/probe && mkdir -p /tmp/probe && cp tests/AiRaccoon.Tests/Resources/jsaa-memory.db /tmp/probe/memory.db
MCP_TRANSPORT=http AIRACCOON_DATA_ROOT=/tmp/probe ASPNETCORE_URLS=http://localhost:5099 \
  dotnet run --project src/AiRaccoon --no-build --no-launch-profile
```

Probe via MCP Streamable HTTP (SSE): POST initialize (capture `mcp-session-id`
header, consume the stream), POST `notifications/initialized`, POST `tools/call`
with `memory_search` arguments `{projectId, query, scope:"project", limit, minScore:0.0,
ftsWeight, vectorWeight}`. Parse `content[0].text` (a JSON list; may be a dict with
`results`). Results carry `sourceFile` and `hash` — map the hash to the exact chunk
key for section-level checks.

- Port 5000 is taken by macOS ControlCenter; use a free port.
- The bundled ONNX model must exist in `src/AiRaccoon/bin/Debug/net10.0/Models/`
  (fresh worktrees lack it — copy from a built worktree or run the model downloader).
- `memory_configure` only accepts embedding-engine keys (provider/baseUrl/model/
  apiKey) — retrieval keys like `structureAlpha` are REJECTED with a generic error.
  To sweep α: write the settings row directly with sqlite3
  (`UPDATE settings SET value='0.8' WHERE key='retrieval.structureAlpha';`) — the
  server reads it per search, no restart needed.

## The A1 debugging timeline (what NOT to repeat)

Symptom: W1 gate test `HybridRanks_DoNotRegress_VsWave0` failed after the W6 merge —
A1 file rank 1 → 2. Investigation sequence and the traps:

1. **Wrong query string** (hours lost): probed "What is ADR-0011 about?" while the
   baseline A1 is "Why was shadcn/ui chosen over gluestack.io?" — read
   baseline-queries.json FIRST. Trap: the expected source (ADR-0011#decision) invites
   an assumed query.
2. **Stale build-output db**: test-vs-server ranking divergence on "identical" dbs.
   Tests copy `bin/.../Resources/jsaa-memory.db` (PreserveNewest — mtime-equal writes
   can skip the copy); the vec_structure backfill only reaches the test db after a
   rebuild. Verify with `shasum` of both files + rebuild + re-run.
3. **vec0 sqlite3 errors misread twice**: `SELECT count(*) FROM vec_structure` →
   "no such module: vec0" was read as "table missing". Plain tables work in the CLI;
   vec0 tables don't.
4. **Silent configure failure invalidating an α-sweep**: `memory_configure
   structureAlpha=1.0` returned "An error occurred invoking 'memory_configure'" —
   swallowed by the probe — so the "α=1.0" probe ran at the default 0.5. The
   α-invariant result was misread as "structure dominates at every α". The real
   conclusion (measured after fixing the sweep): A1 stays rank 2 even at α=0.9
   because the structure gap is large (rank-1's heading contains the query's terms) —
   but that's a content question, not a sweep artifact.
5. **Content verdict**: A1's rank-1 (frontend-architecture.md#3) IS the evidence
   section ADR-0011 links to — same knowledge, cross-linked. File rank 2 = bounded
   trade, not regression. Same pattern verified for A4 (behaviour-spec#3 states "The
   MCP server was deleted; see ADR-0060") and A6's rank-1 (ADR-0069#consequences).
   The one real miss in the same measurement: S2's decision chunk at rank 5 behind
   the metadata header — a within-file ranking problem (Wave 3 domain).

## Gate-amendment pattern

When a measured trade conflicts with a plan gate, amend the plan with the evidence
rather than silently relaxing the test: state the measured number, the content
verification, the bounded cost, and the forward pointer (which wave's gate owns the
fix). Example: "S2's decision-chunk ≤ 3 target moves to Wave 3's gate"; "C2 hybrid
rank-1 is the Wave-4 acceptance criterion — already satisfied by Wave 6's structure
signal". Update the failing test's expectation WITH the analysis comment so the
assertion still guards the bounded bound.
