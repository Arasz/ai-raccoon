# Research: the "semantic code retrieval from scratch" pipeline against AiRaccoon's code corpus

**Date:** 2026-09-23
**Question:** How does the pipeline in "Building a Semantic Code Retrieval System from Scratch"
(M K Pavan Kumar, Towards Dev, 2026-03-08) and the AST-first advice around it compare with
AiRaccoon's code-corpus pipeline, and which of its ideas would improve ours?

Sources compared: the article (read in a browser, 2026-09-23 — plain fetch returns 403), arXiv
2503.17502v1 (supplied alongside it), and the "parse first, chunk logically, Roslyn for .NET" advice
pasted with the request.

```chart:matrix
title: pipeline stages, article vs AiRaccoon (1 = has it, 0 = does not)
stage, article, AiRaccoon
structural (AST) chunking, 1, 0
chunks fit the embedding window, 0, 1
code-trained embedding model, 0, 1
keyword leg (BM25/FTS5), 0, 1
rank fusion (RRF), 0, 1
line ranges on every hit, 0, 1
incremental re-ingest by hash, 0, 1
identifier sub-token matching, 0, 0
retrieval quality measured, 0, 0
```

```chart:bars
title: code smoke set, rank of the expected file (live bank, kind=code, limit 5; 6 = not in top 5)
CS-01 ignore rules: 1
CS-02 watch overlap: 1
CS-03 scan guard: 1
CS-04 query length guard: 1
CS-05 token budget: 2
CS-06 code chunker: 2
CS-07 kind parameter: 6
CS-08 code_get: 3
```

## Findings

### F1 — The article's pipeline is AST chunking, a general text embedder and dense-only search, with no evaluation [READ]

Files are read by extension, split by Chonkie's `CodeChunker` (tree-sitter based), embedded with
`all-MiniLM-L6-v2`, and stored in Qdrant; a query is embedded with the same model and answered by
cosine `query_points` with no filter. There is no keyword leg, no fusion, no reranker and no
metric: the "results" are two anecdotal query dumps (vLLM, LlamaIndex) and a 4.4 s wall time for
one search.

**Evidence:** the article body, sections "The Architecture", "Code and Workflow", "The Results",
"The Conclusion" (`SentenceTransformer("all-MiniLM-L6-v2")`, `CodeChunker(language=…,
chunk_size=…)`, `qdrant_client.query_points(…, query_filter=None)`, "Function 'search_codebase' took
4.4271 seconds").

### F2 — The article's chunks are several times longer than its embedder's window, so most of each chunk is never embedded [INFERRED]

The result boxes report chunk sizes of 776–2023 tiktoken tokens (for example "tokens: 1423", "tokens:
1989"). `all-MiniLM-L6-v2` truncates at 256 word-pieces — the same model AiRaccoon bundles for memory,
which is why our memory chunker budgets 254. Whatever sits past the first ~256 tokens of a function
does not affect its vector. Structural chunking without a window-aware budget buys "complete
functions" at the cost of embedding only their heads. Reasoned from the article's own token counts
and the model's window; the article's vectors were not reproduced.

### F3 — The arXiv paper is a survey with no retrieval method to borrow [READ]

"Large Language Models (LLMs) for Source Code Analysis: applications, models and datasets" (Jelodar,
Meymani, Razavi-Far) reviews tasks, models and datasets. Tree-sitter is not mentioned; ASTs appear in
§2.2 as background; chunking appears only in §7.2 "Long Code analysis and Token Size" as a limitation
(split into functions/classes to fit a context window). It supports the general "respect structure"
advice but offers no chunker, embedder, fusion or benchmark number to compare against.

**Evidence:** https://arxiv.org/html/2503.17502v1, full text fetched 2026-09-23 — table of contents
through §8, 0 occurrences of "tree-sitter", 4 of "chunk"; §2.2 and §7.2.

### F4 — AiRaccoon chunks code by blank-line blocks and brace balance, not by AST, and that was a deliberate v1 call [READ]

`CodeChunker` groups lines into blank-line blocks, packs them greedily to a 510-token budget measured
with the code model's own tokenizer, and prefers to cut at the last point where file-wide brace
balance is zero. It admits in its own doc comment that braces in strings and comments are counted. AST /
tree-sitter chunking was rejected for v1 ("new dependency + real complexity") and named the v2 lever,
gated on the code eval showing the heuristic falls short, with Python (indentation, no braces) named
as the weak case.

**Evidence:** `src/AiRaccoon.Infrastructure/Chunking/CodeChunker.cs:7-31,72-100,264-266`;
`docs/work/2026-08-21-code-search-moe-architecture.md:538-540`;
`docs/work/2026-08-21-code-search-moe-ops.md:235`; `docs/features/code-corpus/spec.json:46`.

### F5 — Past the chunker, AiRaccoon's pipeline already does more than the article's [READ]

Code search is hybrid: FTS5 over `code_fts(value, source_file)` plus a vec0 leg on
`faxenoff/code-daemon-embed-v1` (a code-trained, 768-dim model with a measured 512-token window),
fused by weighted RRF from the same `retrieval.*` settings memory uses, degrading to FTS-only with a
warning when no engine is installed. Every chunk carries a line range, and re-ingest deduplicates by
`(project, path, hash)` and refreshes positions instead of re-embedding. The article has none of
these.

**Evidence:** `src/AiRaccoon.Infrastructure/Sqlite/Code/SqliteCodeSearchService.cs:10-24`;
`src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:541-546`;
`src/AiRaccoon.Infrastructure/Ingestion/CodeIngestor.cs:51-95`;
`src/AiRaccoon.Infrastructure/Chunking/CodeChunker.cs:21-31`.

### F6 — On the 8-query smoke set, the expected file ranks first 4 times, second 2 times, and falls outside the top 5 once [MEASURED]

Ranks: CS-01..04 → 1; CS-05 → 2 (tied 1.0 with `MarkdownChunker.cs`); CS-06 → 2 (behind
`CodeChunkerTests.cs`); CS-08 → 3 (behind `CodeCorpusSteps.cs` and `CodeEntry.cs`); CS-07 → not in
the top 5 (`MemoryTools.cs` expected; `SearchKind.cs`, arguably correct, ranked 2). That is 7/8 in
the top 5. **This is not a quality number**: the set says so itself (8 queries, "NOT a tuning or
gate corpus"), and each query paraphrases a doc comment in the target file, so it mostly measures
retrieval of our own prose comments.

**Evidence:** `memory_search` via the running ai-raccoon MCP server, `projectId=ai-raccoon,
kind=code, limit=5, minRelativeScore=0`, one run per query of the 8 queries in
`scripts/src/retrieval_tuning/eval-sets/code-smoke.json`, macOS arm64 dev machine, 2026-09-23. The
server binary's version was not checked (running servers outlive rebuilds).

### F7 — Test files outrank the source they test [MEASURED]

In 2 of the 8 smoke queries (CS-06, CS-08) a test or BDD-steps file outranked the production file,
and test files appear in the top 5 of 7 of the 8. Tests restate the behaviour of the code they
cover in their names and doc comments, so they compete directly with it. Nothing in the query
surface lets a caller prefer `src/` over `tests/`.

**Evidence:** the same eight runs as F6.

### F8 — The code keyword leg cannot match a word inside a CamelCase identifier [MEASURED]

`code_fts` uses FTS5's default `unicode61` tokenizer (no `tokenize=` option), which keeps
`WatchOverlapResolver` as one token. On a scratch FTS5 table with that row, `overlap` → 0 hits,
`resolver` → 0, `WatchOverlapResolver` → 1; `Watch` → 1 only because the path column contains
`/Watch/`. A natural-language query such as "overlap resolver" therefore reaches identifiers only
through the vector leg, or through prose comments.

**Evidence:** `python3` + `sqlite3` 3.53.4, `CREATE VIRTUAL TABLE f USING fts5(value, source_file)`,
one row, four `MATCH` queries, 2026-09-23. Schema: `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs:541-546`.

### F9 — A code chunk is embedded as bare text, without its file path or enclosing symbol [READ]

The code drain passes `row.Value`, the raw chunk text, to the generator. A chunk that begins
mid-class carries no path, namespace or type name in its vector. The FTS leg does index
`source_file`, so the path helps keyword matching but not semantic matching. This is the gap that
the "structured metadata with each chunk" advice targets, and it can be closed without an AST:
the path is known at ingest.

**Evidence:** `src/AiRaccoon.Infrastructure/Embedding/CodeEmbedder.cs:111,147`;
`src/AiRaccoon.Infrastructure/Ingestion/CodeIngestor.cs:79-85`.

### F10 — The real code eval set was never built, so the gate for AST chunking cannot fire [READ]

The architecture set an acceptance floor of mean nDCG@5 ≥ 0.50 on a graded multi-repo code eval set
that includes a Python repo, and made AST chunking conditional on it. The only code eval set in
the repository is the 8-query smoke file. The multi-repo set is marked deferred until the owner
approves the repos, and the smoke file says it "does not become it by growing". No record of a
measured code nDCG was found.

**Evidence:** `docs/work/2026-08-21-code-search-moe-architecture.md:419,586`;
`scripts/src/retrieval_tuning/eval-sets/code-smoke.json` (`_purpose`, `_deferred`);
`git ls-files` filtered for eval + code returns only that file and its test.

### F11 — Roslyn would give exact C# structure but covers one of the ~25 extensions we index [READ]

The code corpus indexes `.cs .fs .fsx .py .ts .tsx .js .jsx .go .rs .java .kt .kts .swift .rb .php
.c .h .cc .cpp .hpp .m .mm .scala .lua` with one chunking algorithm for all of them.
`Microsoft.CodeAnalysis.CSharp` parses only C#, and its `SemanticModel` needs a compilation with
references, which a file watcher does not have. A Roslyn chunker would be a C#-only special case.
The pasted advice's "100 % accurate" holds for syntax, not for semantic binding without a project
load.

**Evidence:** `src/AiRaccoon.Core/Ingestion/CodeExtensions.cs`;
`src/AiRaccoon.Infrastructure/Ingestion/CodeFileTypeMatcher.cs:6-9`.

### F12 — Per-chunk LLM summaries ("inject context JIT") do not fit this server [INFERRED]

The pasted advice ends in an LLM stage that writes semantic summaries of each chunk. AiRaccoon's
engines are local ONNX models with no LLM dependency, and the caller is already an LLM agent that
receives the chunk plus `code_get`. Adding a summariser would bring a model download or a network
call and a large ingest cost for every code chunk. The code embed drain is already 99.66 % of
ingest time (ADR-0092 context). Reasoned from ADR-0092's profile figure and the MCP-thin
invariant, not measured.

### F13 — Ranked improvements for AiRaccoon [INFERRED]

From F4–F12, cheapest and best-evidenced first:

1. **Build the deferred code eval set (F10).** It is the prerequisite for every item below: without
   it none can be shown to help, and ADR-0056's warning about adjudicating on a tiny set applies.
2. **Identifier-aware keyword leg (F8).** Index a second FTS column holding identifiers split on
   CamelCase / snake_case / digits (`WatchOverlapResolver` → `watch overlap resolver`). No new
   dependency, no re-embed. This targets the gap F8 measured.
3. **Contextual header on the embedded text (F9).** Embed `path` (plus the nearest enclosing
   `class`/`def`/`func` line found by a cheap regex) ahead of the chunk, and count it against the
   510-token budget. This needs a code re-embed and must be measured, not assumed.
4. **A path facet or test down-weight (F7).** Let a caller exclude or de-rank `tests/**`, or report
   test hits in their own group.
5. **AST chunking, still gated (F2, F4, F11).** If the eval shows boundary quality is the loss,
   tree-sitter (multi-language) beats Roslyn (C# only). It must keep the token-budget packing: F2
   shows that complete-but-truncated functions are no win.

Two ideas are not worth adopting: dense-only search, which would drop our FTS leg and fusion (F1,
F5), and per-chunk LLM summaries (F12).

### F14 — A .NET tree-sitter binding is mature enough to ship [UNVERIFIED]

No .NET tree-sitter package was evaluated for maintenance, native-asset coverage (osx-arm64,
linux-x64, win-x64) or grammar bundling. This decides whether item 5 of F13 is a dependency swap
or a native-packaging project.

## Still open

- **The actual code retrieval quality.** Settled only by building the deferred multi-repo eval set
  (owner must approve the repos) and running `evaluate.py` against a scratch bank.
- **How much of the smoke-set success comes from doc comments.** Rerun the 8 queries phrased
  after behaviour rather than after the comment text, or on a comment-stripped copy of `src/`. Our
  heavily commented code may flatter the vector leg in ways an external repo would not.
- **Whether a path/symbol header helps or hurts `code-daemon-embed-v1`.** The model's training
  format was not checked; a header it never saw could shift vectors the wrong way.
- **The article's own retrieval quality.** It reports none; F2's truncation effect is reasoned,
  not reproduced.
- **Which .NET tree-sitter binding, if any, is shippable (F14).**
