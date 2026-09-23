# Plan: multi-language code eval, baseline, measured code-retrieval improvements

**Date:** 2026-09-23 · **Task:** `air-code-retrieval-eval-measured-improvements` · **PR:** #673 (one PR)
**Effort:** high · **Research:** `docs/work/2026-09-23-semantic-code-retrieval-comparison.md`

This plan merges three planning lanes (architecture, eval design, implementation feasibility).
The owner made these decisions on 2026-09-23: one PR; a corpus of 3 small, 3 medium and 3 large files
per language; add .html/.css/.sql to indexing; permissive GitHub repos chosen here and listed for veto;
start from a baseline; every improvement goes spike → measure → keep/drop → proper implementation.

## Session todo list

1. E1: corpus builder, manifest, vendored files
2. E2: query set with validators (leak gate, family split)
3. E3: runner (scratch bank, engine, ingest, drain, metrics, report)
4. B0: baseline on the current binary
5. P1: index .html/.htm/.css/.scss/.sql
6. B1: re-baseline, which every spike is measured against
7. Spikes P2-A (identifier FTS), P3-A (test de-rank), P4-A (embed header), all in parallel
8. Keep/drop verdicts recorded in the results record
9. Phase B, only for kept items: P2-B → P4-B, with P3-B alongside
10. P5 gate check, then P5-A/B only if it triggers
11. P6 integration: combined arm, ADRs, docs, EventId register, merge main

## Order

```
E1 → E2 → E3 → B0 → P1 → B1 → {P2-A ∥ P3-A ∥ P4-A} → verdicts → {P2-B → P4-B} ∥ P3-B → P5 gate → P6
```

Spike code is never merged. P2-A and P3-A are harness-only, with no C# change. P4-A and P5-A live on
local `spike/*` branches cut from the task branch and are measured with `--binary <spike build>`.

## E: eval corpus, query set, runner

**E1 corpus.** The corpus is 12 languages: Rust, Python, C, C++, C#, JS, TS, JSX, HTML, CSS, SQL and Go.
Each language gets 3 files in each of 3 size bands, 108 files in all. Bands count non-blank lines:
small is 20–80 (one chunk), medium 150–400 (2–5 chunks), large 600–1500 (many chunks). C# and
Python come mostly from this repository (family `self`). External files come from permissive repos
pinned to a commit SHA. The license is checked in the LICENSE file at that SHA, not from the badge.
Candidates are serde (Rust), requests (Python), libuv (C), nlohmann/json (C++), lodash (JS),
zod (TS), create-react-app templates (JSX), html5-boilerplate (HTML), normalize.css and bootstrap (CSS),
dbt-core (SQL) and gin (Go). At least 8 src/test pairs are included for P3.
- Layout: `scripts/retrieval_tuning/code-corpus/files/<family>/<orig-path>`, `LICENSES/<family>`,
  `MANIFEST.json` rows `{family, repo, sha, path, vendoredPath, language, sizeBand, lines, sha256, license}`.
  The generator is `scripts/retrieval_tuning/build_code_eval_corpus.py`, and its seeds live in
  `data/corpora/code-eval-corpus.json`.
- AC: 108 files; every band is populated for every language; the manifest's sha256 matches the vendored
  bytes; each family has a license file. Gate: manifest validator test.

**E2 queries.** Each file gets one behaviour-NL query and one identifier-fragment query (216). Each large
file gets one path/context query and each medium file one more (~72). Each src/test pair gets one
src-vs-test distractor and one test-intent query (≥16). There are 20 negative queries. That is about
320 in total, with a minimum per category enforced by the validator. Every entry carries
`expectedSource`, `expectedLines [start,end]`, `language`, `sizeBand`, `family` and `category`.
- Authoring: one subagent drafts queries from each file's implementation, ignoring its comments. A
  second, independent pass checks that each query is unique to its target and does not leak its answer.
- Leak gate: the validator rejects a query whose word-set Jaccard overlap with any comment or docstring
  line of its target exceeds 0.4. It targets the smoke set's flaw.
- Split (ADR-0056): tuning and held-out are split by family. 4 of the 12 families are held out, and a
  test pins the held-out count at ≥3. `self` stays in the tuning split.

**E3 runner** `scripts/retrieval_tuning/run_code_eval.py --binary <path> --arm <name> [--settings k=v]`.
It runs these steps: fresh scratch data root → `serve --port 0 --idle-timeout 0` → `model code set default
--port <n>` → set `ingest scope` → MCP `memory_ingest_directory` → poll `code_entries.embed_state='pending'`
until it reaches 0 → run queries (`kind=code`, limit 10) → write `results-<arm>.json` plus markdown.
Metrics are nDCG@5 and @10, MRR@10, hit@1 and hit@5 (file level) and span-hit@5 (line overlap), broken
down overall and by split, language, size band and category. The run also reports chunk counts per
language, drain seconds and binary SHA. It reuses `scratch.py`, `server.py`, `evaluate.py` and
`scoring.py`. The new pieces are idle-timeout plumbing, the ingest/drain helpers, `line_overlap_gain`,
the per-language and per-band grouping, and the family split.
- AC: one command produces a reproducible results file. Two B0 runs agree within 0.005 mean nDCG@5,
  which is the measured noise band. Gate: the two-run agreement check, recorded in the results record.

## Keep/drop rule (fixed before any measurement)

An improvement is kept when all of these hold against B1:
1. held-out mean nDCG@5 improves by at least **+0.02**, and by at least 4× the measured noise band;
2. its target category improves on the held-out split (identifier-fragment for P2, distractor for P3,
   path/context for P4);
3. no language drops by more than 0.05 on held-out, and the test-intent category does not drop by more
   than 0.05;
4. the tuning split does not move in the opposite direction.

Discrimination proof: a reversed ranking must fail rule 1.
Anything else is dropped, and its numbers stay in the results record.

## P1: index .html/.htm/.css/.scss/.sql (phase B only, owner decision)

Files: `src/AiRaccoon.Core/Ingestion/CodeExtensions.cs`, `CodeExtensionsTests`, `CodeFileTypeMatcherTests`,
a BDD scenario in `CodeCorpusSteps`. No double ingest is possible: memory takes only .json/.md/.markdown/.txt,
and a test asserts that. CodeChunker is unchanged. Braces suit CSS/SQL, and HTML falls back to
blank-line blocks and the budget.
- AC: the extensions are present; the disjointness test stays green; a `.sql` file is found by `kind=code`.
  Gate: those tests, red first; B1 reports the new chunk counts.

## P2: identifier-split keyword column

- **A (harness, no C#).** On a copy of the B1 bank, with its `watches` rows deleted: `ALTER TABLE
  code_entries ADD identifiers` → fill it with a Python splitter → recreate `code_fts(value, source_file,
  identifiers)` → `'rebuild'`. Arm 1b also splits query terms in the harness. Serving the modified bank
  works because the schema digest is unchanged and `MATCH` is unqualified.
- **B.** New pure static `src/AiRaccoon.Core/Ingestion/IdentifierSplitter.cs`, acronym- and digit-aware,
  emitting only tokens with 2 or more parts. `code_entries.identifiers` is filled in `CodeIngestor`. A new
  `EnsureCodeFtsIdentifiersAsync` in `MemorySchema.cs` follows the entries_fts `section` precedent: add the
  column, backfill, drop and recreate `code_fts` and its 3 triggers, then `'rebuild'`, all in one
  idempotent, race-tolerant transaction. The column weight goes through `bm25(code_fts,1,1,w)`, with `w`
  a constant taken from the spike. Query splitting is added only if 1b won, and only on the code path,
  never in the shared `FtsQueryNormalizer`.
- AC and gates: `IdentifierSplitterTests`; `Search_SubwordQuery_MatchesCamelCaseIdentifier` (red on
  main); `OpenBank_TwoColumnCodeFts_RebuildsWithIdentifiersAndBackfills`;
  `OpenBank_AlreadyMigrated_IsNoOp`; `UpdateValue_ReindexesIdentifiers`; the held-out delta. New ADR.

## P3: test-file ranking penalty

- **Decision:** multiply the fused score of test-file paths by the bank setting `retrieval.codeTestWeight`
  (in (0,1], 1.0 = off), in `SqliteCodeSearchService.Fuse`, before normalisation. The MCP surface does
  not change, and "where is X tested" stays answerable.
- **A (harness).** Request 20 results, multiply the test hits by w ∈ {1, 0.8, 0.6, 0.4}, re-sort, and
  cut to 5.
- **B.** New pure static `src/AiRaccoon.Core/Memory/Code/TestPathClassifier.cs`, which classifies by
  directory segments (`test(s)`, `__tests__`, `spec(s)`, `*.Tests`) and filename conventions per
  language. A drift guard test iterates `CodeExtensions.All`: every extension needs a filename rule or an
  explicit directory-only decision. Classification happens at query time, with no column. The key is
  added to `SearchParameterSettingsKeys` and `settings.py`.
- AC and gates: `TestPathClassifierTests` (includes `src/Testing/Foo.cs` → false);
  `Search_CodeTestWeightBelowOne_RanksSrcAboveEquallyScoredTest`;
  `Search_CodeTestWeightAbsent_RankingUnchanged`; the coverage guard; the held-out delta with no
  test-intent regression.

## P4: path header on the embedded text

- The embedded text is `"{path}\n"`, optionally followed by the enclosing declaration line, and then the
  chunk. It is built only at embed time by a new pure `CodeEmbedText`, used at `CodeEmbedder.cs:111,147`.
  The stored `value`, FTS and `code_get` are unchanged, and queries are not prefixed. The header fills
  only the room left under 510 tokens and the value is never trimmed; that is design (a), which needs no
  re-chunk.
- **A (spike branch).** An env-var switch in the spike build only, with arms: path only, path plus
  enclosing line, and (b) chunk budget 510−48 plus full header. Each arm gets a fresh bank and a drain,
  and records header coverage and drain seconds.
- **B (path only, unless enclosing wins).** Existing banks re-embed through ADR-0087's reconcile. The
  fingerprint is `engineFp + "|text:v2"`, applied in both `SqliteCodeEngineStore.ActivateCodeEngineAsync`
  and `CodeEmbedder.ReconcileFingerprintAsync`. The `code_entries.enclosing` column is added only if that
  arm wins.
- AC and gates: `CodeEmbedText_ChunkAtBudget_DropsHeaderNeverValue`; `CodeEmbedText_Result_NeverExceedsWindow`;
  `CodeEmbedder_EmbedsHeaderedText_StoresOriginalValue`; `CodeGet_ReturnsSourceWithoutHeader`;
  `ReconcileFingerprint_PreHeaderFingerprint_InvalidatesEmbeddedRows`;
  `Activate_And_Reconcile_ComputeSameFingerprint`; the held-out delta and drain cost. New ADR, amending
  ADR-0087's trigger set.

## P5: AST chunking (gated)

Trigger: measured on B1 plus the kept items, **span-split rate ≥ 20%** (answer spans crossing 2 or more
chunks) or **file-hit/span-miss ≥ 30% of top-5 misses**. If neither holds, the results record says "not
triggered" and gives the numbers. If one holds, P5-A chunks offline with py-tree-sitter while keeping the
510 packing, in two arms: AST, and a regex "cut before a def/class line" heuristic, the simpler shape.
P5-B then uses whichever wins. `TreeSitter.DotNet` 1.3.0 exists; its native assets and grammars are
unverified, so it needs a restore-and-parse smoke test on osx-arm64 before any commitment, and an ADR.

## P6: integration

Merge main (a peer session is editing `SqliteCodeSearchService` fusion and a `MemorySchema` watch-prune
list). Run one combined arm with every kept item and compare it with B0 and B1; each kept delta must
hold. Then: the EventId register count, the ADR files plus their `docs/adr/README.md` rows, the settings
docs, one README "What's new" line, the results record `docs/work/2026-09-2x-code-retrieval-eval-results.md`,
a build, and the touched suites. CI runs the rest.

## Parallelism and shared files

- E1 and E3 can run in parallel. E2 needs E1.
- The spikes P2-A, P3-A and P4-A run in parallel, each in its own worktree and on its own scratch data
  root. The P4-A drains are CPU-bound, so its arms run one after another, alone, on a quiet machine.
- P2-B and P4-B are serialised, because both touch `MemorySchema.cs`, `CodeIngestor.cs` and `MemorySql.cs`.
- P3-B runs in parallel with them, sharing only `SqliteCodeSearchService.cs`, where the second change
  merges.
- P4-B and P5-B are serialised on `CodeChunker.cs` and DI.

## Plan acceptance

Every package's ACs are checked and met; every kept or dropped verdict is backed by numbers in the
results record; the combined arm holds.
