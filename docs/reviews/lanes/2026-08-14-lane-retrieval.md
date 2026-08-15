# Lane report — retrieval & search quality

Campaign: project-scope review, base `1d1889d517baf840df0b839f547091bd7f46808b`.
Model: opus · read-only on `src/`; throwaway probes under a scratch directory, run against the real
bundled MiniLM model, the real internal types by reflection, and the live bank. Lane verified the
base SHA.

---

### F1 — The `ranking` every search returns is exactly `(rrfK+1)/(rrfK+rank)`, a closed-form function of rank position that carries zero match-quality information [MEASURED]
**Severity:** HIGH
**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/SqliteMemoryStore.cs:266-273` fuses the two
modality lists, then hands the **single** fused list to `SearchResultMerger.Merge([fused], …)`,
which at `SearchResultMerger.cs:26` re-runs `ReciprocalRankFusion.Fuse` on it. A second RRF pass over
one list rebuilds every score from rank position (`ReciprocalRankFusion.cs:36` → `1/(k+rank)`) and
re-normalises by `max = 1/(k+1)` (`:46-50`). **The fused modality scores are discarded.**

Probe replicating the exact production call chain, with a **strong** match set (bm25 −12.5…−8.0,
cosine distance 0.02…0.28) and a **junk** set (bm25 ≈ −0.4, cosine distance 0.95…0.99, i.e.
near-orthogonal), in the same order:

```
== STRONG match set (k=60) ==      == JUNK match set (k=60) ==
rank  Ranking              (k+1)/(k+rank)     match
   1  1                    1                  EXACT
   2  0.9838709677419354   0.9838709677419355 EXACT
   3  0.9682539682539681   0.9682539682539683 EXACT
   4  0.953125             0.953125           EXACT
   5  0.9384615384615385   0.9384615384615385 EXACT
  -> identical score curves? True   (also True at k=10 and k=1000)
```

Independently confirmed against the **live server**:
- relevant query, `rrfK=10` → `1, 0.916667, 0.846154, 0.785714, 0.733333, 0.6875, 0.647059, 0.611111` = `11/11 … 11/18`
- pure gibberish `"completely unrelated gibberish zzzqqq flibbertigibbet quantum banana"`, `rrfK=60` → `1, 0.983871, 0.968254, 0.953125, …` = `61/61 … 61/68` — **identical curve**, top hit an unrelated project's ADR.

**Why it matters:** two independently-tuned knobs (`ftsWeight`, `vectorWeight`) and the whole hybrid
fusion feed a number that is then thrown away and re-derived from ordering alone. Any consumer that
reasons about score *gaps* — a caller thresholding, a future calibration, an agent deciding "is this
good enough" — is reading a constant. It also makes the second RRF pass pure cost.

**Fix:** drop the second fusion — have `SearchResultMerger.Merge` take the already-fused list and
apply `SourceAffinityRanker` + floor + limit directly. One-line-scale change at
`SearchResultMerger.cs:26`.

---

### F2 — `minRelativeScore` is a rank cutoff with a closed form, and at the shipped defaults it filters nothing [MEASURED]
**Severity:** MEDIUM
**Evidence:** follows from F1: `(k+1)/(k+r) ≥ floor` ⟺ `r ≤ ⌊(k+1)/floor⌋ − k`. Probe output over a
100-deep candidate list:

```
rrfK   floor   last rank kept   filters anything at default limit=20?
60     0.5     62               NO
60     0.7     27               NO
60     0.9      7               yes
1000   0.7     430              NO

k=60 minRelativeScore=0.0: 100 of 100 candidates survive
k=60 minRelativeScore=0.5:  62 of 100
k=60 minRelativeScore=0.7:  27 of 100
k=60 minRelativeScore=0.9:   7 of 100
```

**Why it matters:** the parameter's effect is entirely determined by `rrfK`, which callers tune for a
different reason. A caller raising `rrfK` to widen fusion silently disables their own floor.

**Fix:** document the closed form in the description, or express the parameter as what it is — a rank
cutoff (`maxRank`) — and let `limit` do the job.

> **Note on the deployed server:** the running MCP server still exposes the *old* contract —
> parameter named `minScore`, **default `0.7`**, description citing ADR-0006. The base has it renamed
> to `minRelativeScore` with default `0.0` and an honest description. The campaign brief's premise
> ("default `minScore: 0.7`") describes production, not this base. See F6.

---

### F3 — 63.9% of the bank has no structure embedding, and the dual-vector fusion halves their achievable score [MEASURED]
**Severity:** HIGH · **converges with the data-access lane's F4, found independently**
**Evidence:** live bank (16,145 entries):

```
total | no_structure | no_content | pending
16145 | 10311        | 10         | 10        (63.9% have structure_embedding IS NULL)
```

`StructureFusion.Fused` (`src/AiRaccoon.Infrastructure/Embedding/StructureFusion.cs:26`) returns
`alpha * contentSim + (1.0 - alpha) * (structureSim ?? 0.0)`, `DefaultAlpha = 0.5`. An entry with no
structure vector never appears in `structureRows` (`SqliteMemoryStore.cs:896-899`), so it takes the
`?? 0.0` branch: its fused score is capped at `0.5 × contentSim`, while an entry with a heading can
reach `1.0`. **A perfect content match with no heading (0.5) loses to a mediocre one with a good
heading (`0.5×0.6 + 0.5×0.9 = 0.75`).** `HealStructureAsync` (`MemorySql.cs:330-335`) only backfills
rows whose heading parses, so a genuinely heading-less chunk is penalised permanently, not pending.

**Why it matters:** a systematic ranking bias against two-thirds of the corpus, on an axis unrelated
to relevance — whether the chunk happened to sit under a parseable heading.

**Fix:** when `structureSim` is absent, renormalise rather than substitute zero — score the entry on
content alone (`contentSim`) instead of `alpha × contentSim`.

---

### F4 — The query is embedded once, as content, and then matched against heading-path vectors [READ]
**Severity:** MEDIUM
**Evidence:** `EntryEmbedder.EmbedQueryAsync` (`src/AiRaccoon.Infrastructure/Embedding/EntryEmbedder.cs:120-133`)
generates exactly one vector from the raw query. `SqliteMemoryStore.QueryDualVectorBatchAsync`
(`:887-899`) passes that same `@queryVector` (built once at `:256-263`) to **both**
`MemorySql.VectorSearchByFilter` and `MemorySql.StructureVectorSearchByFilter`. The index side is
asymmetric: `EmbedIfConfiguredAsync` (`EntryEmbedder.cs:63-71`) embeds `value` **and**
`HeadingPathParser.Parse(value)` as two separate vectors.

**Why it matters:** half the fused vector score compares a full natural-language query against short
heading strings ("Decision", "Context") in the same embedding space. That is a different text
distribution, and MiniLM similarity across distributions is not calibrated. Tokenisation, pooling and
L2 normalisation **are** identical on both paths — the asymmetry is in *what text* is embedded, not
how.

**Fix:** either embed a heading-shaped projection of the query for the structure modality, or make
`alpha` reflect measured value rather than a 0.5 default nobody swept.

---

### F5 — 42.7% of live entries exceed the embedder's 256-token window; 9.67% of all indexed text is never embedded, and the rate is *rising* after the fix landed [MEASURED]
**Severity:** BLOCKER
**Evidence:** truncation site
`src/AiRaccoon.Infrastructure/Embedding/OnnxEmbeddingGenerator.cs:129-133` — overflow is **silently
dropped, not split**:

```csharp
if (ids.Count > MaxSequenceLength) { Log.ChunkTruncatedAtEmbedTime(...); ids = [.. ids.Take(MaxSequenceLength)]; }
```

(EventId 414 at `:148-150`; the class's own doc comment at `:147` says this "should stay at zero once
chunk budgets are engine-aware".) No code path splits instead of truncating.

Measured with the **real bundled BERT WordPiece tokenizer** over the live bank:

```
entries total                    : 16145
exceeding the 256 window         : 6897   (42.72%)
WordPiece tokens in bank         : 4129520
tokens never embedded            : 399243 (9.668% of all indexed text)
mean % of an oversized entry lost: 11.8%     worst: 96.5% (7378 tok)
```

This replaces the orchestrator's estimate table: the true ratio is 15,428,635 chars / 4,129,520
tokens = **3.74 chars/token**, putting the real figure at the low (≈8.5%) end of that range —
**9.67% measured**.

Split at the WP7 merge (`1a8c2644`, 2026-08-14T13:49:43+02:00) and at the installed binary's commit
(`c5f3fa26`, 22:10:24+02:00):

```
created BEFORE WP7                     : 6066/14336 over (42.3%)
created AFTER  WP7                     :  831/1809  over (45.9%)
created AFTER  installed HEAD c5f3fa26 :  643/1337  over (48.1%)
  post-HEAD by extension:  .md 312/901 (34.6%)   .json 331/436 (75.9%)
```

**Why it matters:** ~400k tokens of indexed text are invisible to vector search while counted as
`embed_state='embedded'` everywhere else. The text *is* still in `entries.value` and in `entries_fts`,
so it remains keyword-reachable — this is a **silent ranking defect, not data loss**. The semantic
list can never surface those passages.

**Fix:** a re-chunk + re-embed backfill for existing rows, plus a restart of the deployed servers (see
F6). `EntryEmbedder` re-embeds stored text unchanged on engine switch and never re-chunks — the
migration has to go through the chunker, not the embedder.

---

### F6 — The chunker is correct at this base; the truncation persists because the running servers predate the fix [MEASURED]
**Severity:** HIGH — *this is the discriminating result*
**Evidence:** ran the **current** production chunking configuration
(`maxTokens = min(256, SafeChunkBudgetFor("local", null)) = 254`, overlay 48, counter = real
`BertTokenizer`, exactly as `FileIngestor.ChunkSizeForAsync` at `FileIngestor.cs:198-216` resolves it
for this bank's `embedding.provider='local'`) over the exact files whose post-HEAD bank rows overflow:

```
resolved production budget: maxTokens=254, overlay=48, counter=real BERT WordPiece
agent-memory-server.md   chunks=73  max=256 tok  over-256=0  (0.0%)
architecture.md          chunks=38  max=256 tok  over-256=0  (0.0%)
0036-engine-aware...md   chunks=13  max=254 tok  over-256=0  (0.0%)
```

`agent-memory-server.md` has a **934-token** row in the live bank; the current code produces 73
chunks with max 256. **The landed code holds on real, out-of-gate-corpus files.**

The discriminator:

```
ps -eo pid,lstart,args | grep ai-raccoon
74572  Fri Aug 14 17:21:24 2026   /Users/arasz/.dotnet/tools/ai-raccoon --quiet   (etime 05:16:19)
68074  Fri Aug 14 18:58:52 2026   /Users/arasz/.dotnet/tools/ai-raccoon --quiet   (etime 03:38:51)
54524  Fri Aug 14 22:12:10 2026   /Users/arasz/.dotnet/tools/ai-raccoon --quiet
```

Long-lived MCP server processes started **before** the binary at `~/.dotnet/tools/ai-raccoon` was
updated to `c5f3fa26` (22:10:24). Writes landing at 22:24 came from a process that loaded pre-WP7
code hours earlier.

**Verdict on the orchestrator's three candidate explanations:**
1. **Right, but sharper than stated** — it is not only that the bank holds stale rows; the fix is not in any running process, so new writes keep being produced by old code.
2. **Refuted** — the gate is in-sample (F7), but the ceiling nevertheless holds on files outside the gate corpus.
3. **Refuted for the file-ingest path** — `FileIngestor` threads the real BERT counter correctly, and `JsonFileTypeChunker.Chunk` (`:38`) honours the `countTokens` override and re-verifies via `EnsureWithinBudget` (`:159-170`).

**Fix:** restart the deployed servers (needed regardless), then run the F5 backfill. A
version/consistency check at startup logging the running assembly version against the bank's recorded
engine fingerprint would make this class of drift visible.

---

### F7 — Every published RRF number is in-sample: the same 11 queries select the parameters and then gate them [READ]
**Severity:** HIGH
**Evidence:** `docs/adr/0006-rrf-parameter-optimization.md:23-29` — the 96-point grid was swept over
eleven expected-source queries. `tests/AiRaccoon.Tests/Integration/RrfParameterSweepTests.cs:56-58`
(`RrfGateQueryIds`) and `tests/AiRaccoon.Tests/Integration/SourceAffinitySweepTests.cs:42-43`
(`SourceAffinityGateQueryIds`) use the **identical** 11-query list, and
`RrfParameterSweepTests.cs:191-192` asserts
`chosen.AdrNdcg5.ShouldBeGreaterThanOrEqualTo(PinnedAdrNdcg5 - tolerance)` against it.

**Why it matters:** every nDCG figure in ADR-0006's chain (0.722 → 0.674 → 0.532 → 0.5260827785380623)
is **in-sample** and cannot justify work. Leave-one-family-out was **not run** — `UNVERIFIED` whether
any number survives it.

*Credit where due:* ADR-0006's own final amendment (`:276-286`) reports that after corpus regeneration
`k=60, 1:1` is no longer the grid optimum (`k=120, 2:1, Max5X50` scores 0.775 vs 0.532) and **declines
to re-tune** rather than silently re-picking. That is the right call and unusually honest.

**Fix:** partition `scripts/baseline-queries.json` and the JSAA corpus by generating family (jsaa /
ai-badger / arasz-home-page), tune on some, gate on the held-out ones. Until then, label every
ADR-0006 number in-sample.

---

### F8 — The headline retrieval metrics gate asserts only that nDCG/MRR/recall lie in [0,1] — it cannot fail [READ]
**Severity:** HIGH
**Evidence:** `tests/AiRaccoon.Tests/Integration/BaselineMetricsTests.cs:107-112`:

```csharp
metric.Ndcg5.ShouldBeInRange(0.0, 1.0, $"nDCG@5 for {metric.Id}");
metric.Mrr.ShouldBeInRange(0.0, 1.0, $"MRR for {metric.Id}");
metric.Recall5.ShouldBeInRange(0.0, 1.0, $"recall@5 for {metric.Id}");
```

`RetrievalMetrics.NdcgAtK/Mrr/RecallAtK` return values in `[0,1]` by construction for any input, so
**no retrieval quality whatsoever makes these fail**. The file concedes it (`:115-116`): *"logged as a
data point, not asserted."* Adjacent weak gates: `RetrievalBaselineTests.cs:151-154` requires `>= 1` of
19 graded queries to hit at rank ≤ 3 (passes with 18/19 misses); `:125-126` requires `>= 1` query to
return any result at all.

**Why it matters:** the suite's most quality-sounding assertions are decorative. A ranker regression
that halves nDCG passes green.

**Fix:** replace the range checks with a pinned per-query floor measured on a **held-out** family
(F7), watched RED first.

---

### F9 — `search_quality_eval.json` is a dead production telemetry dump, not an eval corpus [READ]
**Severity:** LOW
**Evidence:** `tests/AiRaccoon.Tests/search_quality_eval.json` is 181 rows of the live `search_quality`
table (`correlation_id`, `top_source_files`, `usefulness_grade`), added in `3be2c0d9`. **No `.cs` file
in `src/` or `tests/` references the filename** — it is cited only from narrative plan docs.
The real gate corpora are `scripts/baseline-queries.json`, `tests/AiRaccoon.Tests/Resources/jsaa-memory.db`,
and `benchmarks/.../RealWorldQueries.cs`.

**Fix:** delete it, or move it under `docs/work/` where telemetry dumps belong.

---

### F10 — 47% of the benchmark queries mark ~42% of the whole corpus as relevant [MEASURED]
**Severity:** MEDIUM
**Evidence:** `benchmarks/AiRaccoon.Benchmarks/Corpus/RealWorldQueries.cs` is auto-generated
(`scripts/generate-benchmark-corpus.py`), with relevance assigned by a **keyword-overlap heuristic**
(the `// judgment:` comments say "same-topic docs keyword-verified"). 27 of 57 queries have
relevant-sets of 72-73 documents out of a 174-document corpus.

**Why it matters:** `ParityGateTests` computes nDCG@10/recall@10 against that ground truth. With 42% of
the corpus "relevant", almost any ranker returning same-topic chunks scores well — the gate has very
little dynamic range.

**Fix:** sample ~30 query/doc pairs for manual judgment and measure the heuristic's precision before
trusting the parity number.

---

### F11 — Two of the most substantive retrieval gates are not tagged `Category=Retrieval` [READ]
**Severity:** LOW
**Evidence:** `ParityGateTests.cs` (the nDCG parity gate) and `StructureFusionGateTests.cs`
(dual-vector acceptance) both carry `[Trait(Category, Integration)]`.
`dotnet test --filter "Category=Retrieval"` yields **53 tests across 11 files** and misses both.

**Why it matters:** anyone doing a quick retrieval check gets false confidence.

**Fix:** add the `Retrieval` trait, or derive the category from namespace rather than hand-tagging.

---

### F12 — On a purely semantic vector hit, the snippet is a 200-character window at a SHA256-derived offset [READ]
**Severity:** MEDIUM
**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/SnippetFallback.cs` — the prior review's finding is
**partly** fixed: `From()` (`:50-68`) now first tries `QueryMatchStart`, centring the window on the
earliest literal ≥2-char query token. But `HashSeededStart` (`:70-71`, `SHA256(hash) % maxStart`)
survives unchanged as the fallback, and it is taken exactly when no query token literally occurs — the
defining case of a semantic-only hit. `WindowChars = 200` (`:15`). Live bank: mean entry 955.6 chars →
**200/955.6 ≈ 20.9%** of an average entry visible. FTS hits use
`snippet(entries_fts, 0, '', '', '…', 12)` (`MemorySql.cs:125-130`) — max **12 tokens**, materially
less.

**Why it matters:** the agent's decision to call `memory_get` rests on the snippet. For the hits only
vector search could have found, the snippet is content-irrelevant by construction.

**Fix:** for vector-only hits, centre the window on the highest-similarity sentence, or return the
entry's first 200 chars — either beats a hash-derived offset.

---

### F13 — A chunk splits mid-table and mid-word; ADR-0048's guarantee covers fences only [MEASURED]
**Severity:** MEDIUM
**Evidence:** probe on a 200-row markdown table at `maxTokens=150` → 34 chunks, **33 of 34** carry
orphaned table body with no header row. On a 20 KB single-line prose document at `maxTokens=100` →
**14 of 31** boundaries land mid-word (`AddUnitOrSplit`/`LargestPrefixWithinBudget`,
`MarkdownChunker.cs:318-364`, is a pure token binary search with no word awareness). Fences are
genuinely safe: 50 KB single fence at `maxTokens=200` → 96 chunks, **96/96 balanced**.

**Why it matters:** ADR-0048's title claims "a chunk is a well-formed markdown fragment"; the guarantee
it actually delivers is fence balance. A header-less table fragment is poor retrieval material and poor
agent input.

**Fix:** narrow ADR-0048's claim to fences, or add table-header carry-over the way the fence re-fencing
works.

---

## Still open

- **Leave-one-family-out on the RRF parameters.** *Settled by:* partitioning `scripts/baseline-queries.json` by source repo, re-running the grid on a subset, reporting nDCG@5 on held-out families.
- **Whether `alpha = 0.5` in `StructureFusion` was ever swept.** `DefaultAlpha` is a bare constant with an ADR reference and no sweep artefact.
- **Actual character length of an FTS snippet.** `snippet(…, 12)` is 12 *tokens*; the char distribution was not measured.
- **Whether the deployed servers, once restarted, drive the EventId 414 counter to zero.** F6 predicts yes. *Settled by:* restarting and watching the warning count over a day of ingest.
- **Whether F3's halving actually reorders real results.** The arithmetic and the 63.9% population are proved; a concrete rank flip on the live bank was not demonstrated. *Settled by:* one query at `alpha=0.5` vs `alpha=1.0`, diffing the top 20.
- **Whether the `[UNK]` collapse (EventId 415) is as rare as claimed.** `FileIngestor.cs:31-38` cites 1/15,246 entries.

## Grade mix

MEASURED 7 (F1, F2, F3, F5, F6, F10, F13) · READ 6 (F4, F7, F8, F9, F11, F12) · INFERRED 0 ·
UNVERIFIED 0 findings, 1 open item (leave-one-family-out).

## Owner questions

1. Do you want `ranking` to carry a real fused score, or is rank-order the intended contract — F1's fix changes a published output field?
2. Should a chunk with no parseable heading score on content alone (F3), or is the structure penalty deliberate?
3. Is the deployed-server restart discipline (F6) something to enforce at startup, or handle operationally?
4. Should the F5 backfill re-chunk the whole bank, or only rows currently over the window?
5. Is `benchmarks/` a CI gate or a scratch harness — it holds a second, duplicate `RetrievalMetrics` implementation?
6. Can `tests/AiRaccoon.Tests/search_quality_eval.json` be deleted (F9)?

## Healthy

- **The nDCG/MRR/recall math is correct.** `tests/AiRaccoon.Tests/Unit/Retrieval/RetrievalMetrics.cs:9-76`: IDCG over `min(k, relevant.Count)`, recall divides by full relevant-set size, MRR returns 0 on a total miss and is unconditionally averaged in (`SweepRunner.cs:48`) rather than skipped. `NdcgAtK_HandComputedExample` verified by hand.
- **ADR-0047 is honestly implemented and genuinely gated.** `RelativeScoreFloorTests.OffCorpusQueries_StillScoreOneAtRankOne` pins that gibberish still scores 1.0 at rank 1 with a real assertion, and the tool description at this base says plainly that a high score is not evidence of a good match. The opposite of a vacuous gate.
- **The engine-aware chunk budget works.** `FileIngestor.ChunkSizeForAsync` (`:198-216`) resolves 254 BERT content tokens and threads the *identical* tokenizer used at embed time; the repro produced 0/124 over-window chunks on three real files.
- **Fence safety is real and verified.** 96/96 balanced on a 50 KB single fence; headings inside fences never leak into `HeadingPathParser`.
- **`chunk_index`/`total_chunks` cannot drift.** `MemorySql.cs:480-491` recomputes both with `ROW_NUMBER()`/`COUNT(*) OVER (PARTITION BY ctx, source_file)` inside the same transaction as every mutation — no gaps possible after delete or re-ingest.
- **Index/query tokenisation, pooling and normalisation are identical** — both paths go through one `OnnxEmbeddingGenerator.Encode` and `EmbeddingMath.MeanPoolAndNormalize`. The most common silent RAG killer is not present.
- **The project deletes its own unfailable tests.** `GoldenFileTests.cs:11-17` documents removing a golden-file test because "it cannot go red on any AiRaccoon retrieval change."

## Disconfirmed

- **"`memory_search` defaults to `minScore: 0.7`."** False at this base — the parameter is `minRelativeScore`, default `0.0`, with an accurate description (`MemoryTools.cs:101-106`). True only of the *deployed* server, which is older than the base.
- **"The normalisation makes `minScore` meaningless or dangerous."** Worse *and* better than supposed: it is a precisely predictable rank cutoff (F2), and the project already documents its relative nature. Not dangerous — under-specified.
- **"`SnippetFallback` opens a 200-char window at a hash-derived offset."** Half-refuted: a query-match window was added ahead of it; the hash-derived offset survives only as the fallback — which is still the semantic-hit case (F12).
- **"The chunker is the source of the production truncation."** Refuted (F6). The current chunker produces 0 over-window chunks on the exact offending files.
- **Orchestrator's explanation 2, "the WP7 gate is in-sample so the ceiling fails in the field."** Refuted — the gate *is* in-sample (F7 applies to RRF), but the ceiling holds on files outside the gate corpus.
- **Orchestrator's explanation 3, "a second path bypasses the budgeted chunker."** Refuted for file ingest. (`memory_write` does not chunk at all, but accounts for only 318 of 6,897 oversized rows.)
- **"Legacy data explains the truncation warnings."** Refuted by the WP7 timestamp split: the oversized rate *rises* after the fix landed (42.3% → 45.9% → 48.1%).
