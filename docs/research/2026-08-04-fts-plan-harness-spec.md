# FTS Plan-Fix Harness — Build Spec (subagent brief)

**Task**: build `tools/AiRaccoon.FtsPlanPrototype` in `/Users/arasz/RiderProjects/ai-raccoon-prototype`
(branch `prototype/dual-vector-alpha`) — a console app that measures the plans' FTS-side fixes
(Wave 1 query construction, Wave 3 source consolidation) against the CURRENT FTS behavior, on the
same corpus, queries and metric definitions as the dual-vector harness. This is a SPIKE — copy
what you need, never modify production code (`src/` stays untouched).

## Corpus & queries

- DB: `tests/AiRaccoon.Tests/Resources/jsaa-memory.db` (6675 chunks; 1056 docs/adr, 4723 docs/work).
  Copy to a temp file together with `-wal`/`-shm` sidecars, open `Mode=ReadOnly` (see
  `tools/AiRaccoon.DualVectorPrototype/Program.cs` `LoadChunksAsync` for the pattern).
- Queries: `scripts/baseline-queries.json` (35 queries). Primary metric set: A1–A7 (7 reachable
  expected-source queries, all `#decision` fragments).

## Arms (real FTS5 via SQL — use the production query shape)

```sql
SELECT e.id AS Id, e.hash AS Hash, e.path AS Path, e.value AS Value,
       bm25(entries_fts) AS Ranking
FROM entries_fts JOIN entries e ON e.id = entries_fts.rowid
WHERE entries_fts MATCH @query AND e.scope='project' AND e.project_id='jsaa'
ORDER BY bm25(entries_fts) LIMIT 100
```
(bm25() lower = better; this mirrors `MemorySql.SearchByFilter` in the real pipeline.)

- **F1 current**: MATCH expression = the EXISTING `FtsQueryNormalizer.Normalize` (copy it
  verbatim from `src/AiRaccoon.Infrastructure/Sqlite/FtsQueryNormalizer.cs` — alphanumeric tokens
  joined ` OR `, reserved words dropped).
- **F2 fixed** (plan Wave 1): same tokenization, then:
  1. stopword strip: `what is the how does about are do can should will would could has have been was
     were being a an in on at to for of by with from`
  2. identifier detection: original query matches `\bADR-\d+\b` → emit `adr AND <number>` and stop
     (also handle a lone `\b\d{4}\b` number as identifier when preceded by `adr` in the token list)
  3. otherwise: remaining tokens joined ` AND ` when ≤4 tokens, ` OR ` when longer (recall)
  4. still drop FTS5 reserved words; guard every MATCH in try/catch — on FTS5 syntax error fall
     back to the F1 expression and log the query id (do NOT crash the run)
- **F3**: F2's ranked list (window 100) post-processed by source consolidation: group by `path`,
  keep the best-ranked chunk per path, re-rank by its bm25 score, take top-5 for evaluation.
- **L (length analysis)**: for queries A7 and A1: take F2's top-200 matched chunks; re-rank them by
  a length-unaware score = Σ over query tokens of raw term count in the chunk (no length
  normalization, no IDF); compare the expected file's rank under bm25 vs raw-TF; report both ranks
  and the length distribution of chunks outranking the expected file. Small table in the report.

## Output (MUST match the dual-vector harness schema)

1. `results-plan.md` — metrics table with EXACTLY the same columns as `results-dual-vector.md`
   (Arm | Positive Results | File Hits @5 (A1-A7) | Section Hits @5 (A1-A7) | MRR (file) | Mean α —
   for F-arms Mean α = `—`), plus per-query A1-A7 detail and the L-analysis table.
2. `results-plan.json` — machine-readable ranked lists, schema:
   `{ "corpus": "...", "arms": [...], "queries": [ { "id", "expectedSource",
   "arms": { "<arm>": [ { "rank", "hash", "path", "headingPath", "score" } × top-100 ] } } ] }`.
   `headingPath` = the chunk's markdown heading path (see `ParseHeadingStructure` in the
   dual-vector Program.cs — copy it verbatim so both harnesses agree). FTS arms: `score` = bm25.

## Shared metric definitions (copy from dual-vector Program.cs, keep byte-identical)

- `ExpectedFile` / `ExpectedSection` (strip `#fragment`, last segment after `:` or `/`).
- File-level hit: any top-5 result path ends with `/` + expected file (case-insensitive).
- Section-level hit: file hit AND the result's headingPath last segment (trim, trim `:`) equals the
  fragment (`decision`), case-insensitive.
- MRR over A1-A7 (file-level; 1/rank on first hit, 0 on miss). Coverage: queries with ≥1 result.

## Constraints

- Memory: run under `scripts/run-with-memcap.sh 6 <apphost>` — hard 6 GB RSS cap (the previous
  prototype run OOM-killed the machine at 50 GB; never again). FTS-only work is light, but the
  rule is non-negotiable.
- Deterministic: no randomness anywhere.
- Build ONLY this project (`dotnet build tools/AiRaccoon.FtsPlanPrototype/...`); never build the
  whole solution (other work is running in this worktree).
- Commit your work on the spike branch with small commits; do not touch `src/`.
- The FTS harness needs no ONNX model and no Infrastructure project reference — reference only
  Dapper + Microsoft.Data.Sqlite (copy the csproj shape from the dual-vector prototype).

## Done = evidence

The app runs to completion under the memcap, `results-plan.md` + `results-plan.json` exist and
parse, and the F2 normalizer's behavior is spot-checked by a `--selfcheck` mode with at least:
`"What is ADR-0070 about?" → "adr AND 0070"`, `"How does the project handle data erasure?" →
`data AND erasure AND work` (stopwords stripped: how, the, does, project? — 'project' is NOT a
stopword: verify against the list), and one identifier case. Report the actual emitted MATCH
expressions for A1–A7 in the summary.
