# 0043. Fixtures Are Built by the Product, Not Beside It

Date: 2026-08-14

## Status
Accepted

## Context
`tests/AiRaccoon.Tests/Resources/jsaa-memory.db` — the committed retrieval corpus behind
`GoldenFileTests`, `RetrievalBaselineTests`, `SectionTargetedRetrievalTests`,
`RrfParameterSweepTests`, `SourceAffinitySweepTests`, `SourceIdentityTests`,
`BaselineMetricsTests` and `QueryConstructionTests` — was, until WP4
(`docs/plans/2026-08-14-code-quality-improvement-plan.md`), produced by
`scripts/ingest-jsaa-docs.py`: a second, independent chunker
(`scripts/src/chunking.py`) that walked the job-search-ai-assistant docs tree, split
markdown into sections itself, and wrote the resulting chunks straight into a bank over the
`memory_write` MCP tool — bypassing `FileIngestor`, the chunker registry, and every other
piece of the ingestion path a real caller goes through.

That re-chunker populated the `section` column on every chunk it wrote. The production
`FileIngestor` has never written `section` — it writes `heading_path` instead. So the
documented retrieval feature "a source-path-shaped query (`file#section`) ranks the exact
chunk first" **could not work for a single ingested document**, and every gate covering it
passed for its entire life, because the fixture was never exercised by the code path it was
supposed to be standing in for. Measured on the live bank before the fix: `section`
populated on 517 of 15,325 rows, `heading_path` on 5,604 — the two columns the corpus was
supposed to jointly exercise disagreed almost everywhere, and no test noticed because no
test compared them against what `FileIngestor` actually does. The defect only became
visible once WP4 regenerated the corpus through the production `FileIngestor`
(`JsaaCorpusRegenerationTool`, `tests/AiRaccoon.Tests/Integration/JsaaCorpusRegenerationTool.cs`)
and the row counts, `structure_embedding` population, and `heading_path` coverage all moved.

`scripts/src/hash_map.py` carried the same disease one step further: it computed a
`{structured_path: hash}` map from the *re-chunker's* output and wrote it to
`scripts/chunk-hash-map.json`, which seven C# integration test files read to decide which
row was the "expected" match for a query. Once the corpus stopped being built by the
re-chunker, that file no longer identified any row in it — a fact recorded plainly in
`db74e6f6`'s commit message when the tests were switched to derive expected hashes from the
live corpus instead (`CorpusHashMap.cs`). The JSON file was deleted with that change, but the
Python code that produced it, and the re-chunker beneath it, survived — both fully
orphaned, both still imported by `scripts/src/pipeline.py`, and both invisible unless someone
went looking for their callers.

The general shape: a fixture (or a live bank) built by a parallel implementation does not
test the product — it tests the parallel implementation. It cannot fail on any defect that
is specific to how the real path chunks, hashes, scopes, or writes, because it never runs the
real path in the first place. Divergence between the two implementations is exactly the
class of bug the fixture exists to catch, and it is exactly the class of bug this shape hides.

## Decision
1. **Deleted** `scripts/src/chunking.py`, `scripts/src/hash_map.py`, and their tests
   (`scripts/tests/test_chunking.py`, `scripts/tests/test_hash_map.py`). Nothing in the tree
   imports either module (`grep -rn "from chunking\|from hash_map"` returns no hits outside
   the deleted files themselves).
2. **Rewrote `scripts/src/pipeline.py`** to stop chunking locally. It now curates *which*
   files to ingest (`scripts/src/sources.py`'s include/exclude globs — unique to this
   deployment, not product logic, so it stays) and hands each one to the production
   `memory_ingest_file` MCP tool, which runs the same `FileIngestor`/chunker every other
   caller goes through. `scripts/src/mcp_client.py`'s `memory_write` wrapper (unused once
   pipeline.py stopped writing raw chunk content) was replaced with a `memory_ingest_file`
   wrapper. The wrapper and the script's usage docstring now say plainly that this requires
   an ingest scope configured first (`ai-raccoon ingest scope add <project> <path>`) — a new
   prerequisite the old raw-write path didn't have, because raw writes don't check scope.
3. **`scripts/src/pipeline.py` is not orphaned** — `scripts/ingest-jsaa-docs.py` is its only
   caller and stays a thin CLI wrapper over it. `--chunk-only` has no local chunking left to
   preview post-fix, so it now behaves as an alias of `--dry-run` (enumerate and stop) rather
   than being removed, so existing invocations keep working.
4. **`scripts/list-jsaa-corpus-files.py` and `scripts/src/sources.py` are kept as-is** — the
   former is the file-selector `JsaaCorpusRegenerationTool.cs` already calls to decide which
   files to feed the production `FileIngestor` when regenerating the committed corpus; the
   latter is its curation logic, shared with `pipeline.py`. Neither reimplements anything the
   product does; both decide *what* to hand the product, never *how* to process it.
5. Everything else in `scripts/` was audited against the same question — does a CLI verb or
   MCP tool already do this, and does the script produce or write data the product also
   produces or writes — and is either **operational tooling with no product equivalent**
   (packaging/publish helpers, coredump triage, the embedding-model downloader, one-off data
   migrations, release-checklist runners) or genuinely borderline and left for the owner to
   decide, recorded in the work-package report rather than acted on here.

## Consequences
- **Positive**: the retrieval corpus is now built by the exact code path every real ingest
  call goes through, so a defect in `FileIngestor`, its chunker, or its `section`/
  `heading_path` handling can actually fail the gates that claim to cover it.
- **Positive**: `scripts/` no longer carries a second markdown chunker to keep in sync with
  `AiRaccoon.Core`'s — one less place a chunking change can drift unnoticed.
- **Negative**: `scripts/ingest-jsaa-docs.py` now requires the operator to run
  `ai-raccoon ingest scope add` once before it can write anything, where the old raw-write
  path had no such prerequisite (a consequence of going through the real, scope-checked
  ingest path instead of around it).
- **Watch for elsewhere**: `scripts/prometheus_grade.py`'s grade write-back
  (`_write_grade`) does a raw `UPDATE search_quality SET usefulness_grade = …` against
  `~/.ai-raccoon/memory.db` directly, duplicating the production `memory_record_grade` MCP
  tool (`src/AiRaccoon/Tools/QualityTools.cs` → `SqliteSearchQualityService.RecordGradeAsync`)
  in miniature: same UPDATE, but the script's path skips the tool's write-access gate and the
  app's shared connection factory. Its read side (bulk-listing ungraded rows, pulling
  snippets by `source_file`) has no MCP equivalent, so a full migration is a bigger job than
  this ADR's scope — recorded here as a finding, not fixed. The next time someone touches
  that script, route the write through `memory_record_grade` instead of the raw `UPDATE`.
- **Watch for elsewhere (lower severity)**: `scripts/src/bundle.py` hand-duplicates
  `src/AiRaccoon.Infrastructure/Embedding/BundledModel.cs`'s model filename and both SHA-256
  pins in Python, with no shared source of truth between the two languages. Unlike the
  chunker/section case, this can't drift silently: both copies exist only to verify a
  download against a pin, so a mismatch fails loudly (`download-embedding-model.py`,
  `verify-tool-package.py`) instead of shipping wrong data unnoticed. Still the same
  underlying pattern — product-owned data duplicated beside it — and worth a single-source
  fix if the bundled model ever changes.
- **Decision rule for future scripts**: before a script writes a row shaped like something
  the product writes (a chunk, a hash, a grade, a settings row), check whether a CLI verb or
  MCP tool already performs that write. If one exists, call it — a script curates inputs and
  drives calls, it does not re-derive the product's output shape. If none exists and the
  script only inspects or migrates data offline (packaging, crash triage, a one-off schema
  repair), that is legitimate operational tooling and stays outside the product.
