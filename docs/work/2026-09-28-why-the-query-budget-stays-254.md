# Research: why the search-query trim budget stayed 254

**Date:** 2026-09-28
**Question:** Why was it decided to keep the search-query trim budget at 254 content tokens when the bundled embedding engine's context window is 8,190?

```chart:bars
title: code file MRR@10 by chunk budget (2026-09-24 record, 240 queries, vector leg)
128: 0.831
254: 0.825
510 (shipped): 0.822
1022: 0.788
```

## Findings

### F1 — The live engine resolves to the granite bundled manifest, so its `chunkTokens: 254` is the budget that trims queries today [MEASURED]

The live bank's `embedding.engine` setting is `local:bundled#777268daa8…`, and the SHA-256 of the
shipped bundled manifest is exactly that hash. The manifest declares
`contextWindowTokens: 8190` and `chunkTokens: 254`; `TrimQueryToWindow` trims to the latter via
`ManifestContentBudget`, so the observed "311 tokens exceeded the 254-token window" warning is this
manifest's number, not a stale constant.

**Evidence:** `sqlite3 -readonly ~/.ai-raccoon/memory.db "select key, value from settings where key like 'embedding%'"` (live bank, read-only) and `shasum -a 256 src/AiRaccoon/Models/granite-embedding-small-english-r2/ai-raccoon.manifest.json` (→ `777268daa83e…`) — macOS 24 GB M4 host, run 2026-09-28. Code path: `src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs:143-180` (`TrimQueryToWindow`), `:475-484` (`ManifestContentBudget`), `src/AiRaccoon/Models/granite-embedding-small-english-r2/ai-raccoon.manifest.json:10,75`.

### F2 — 254 was never chosen as a query parameter; it is the old bundled model's hard content window, and the query trim was bounded by it [READ]

254 originates as `MaxContentTokens = MaxSequenceLength − 2 = 254`: all-MiniLM-L6-v2's 256-token
window minus the [CLS]/[SEP] tokens the generator adds (ADR-0036). ADR-0071 (2026-08-15) introduced
query trimming at exactly that bound — "trims a query to `MaxContentTokens`" — to replace the
generator's silent truncation with a visible one. It explicitly declined to make long queries work:
embedding a query faithfully (chunk it, pool the vectors) "is a retrieval-semantics change that needs
measurement before it ships".

**Evidence:** `docs/adr/0036-engine-aware-chunk-token-budget.md` (Decision: "`OnnxEmbeddingGenerator.MaxContentTokens = MaxSequenceLength - 2 = 254`"), `docs/adr/0071-a-query-is-trimmed-deliberately-and-said-so.md` (Decision; Consequences, "What this does not do"), `src/AiRaccoon.Infrastructure/Embedding/OnnxEmbeddingGenerator.cs:27`.

### F3 — The query budget is the chunk budget by construction, decided in the 2026-08-21 manifest plan [READ]

When budgets became engine-aware, one review finding folded the query trim into the chunk budget:
"`TrimQueryToWindow` trims to the same effective budget for manifest models ⟲ (F10c); remote stays
8191; **bundled stays 254**". The architecture record gives the rationale: the budget is one
per engine (the ADR-0036 invariant that the counter and the embedder agree), and any quality impact
of a bigger budget is "an eval question for WP5, not a silent change". So "keep the query budget at
254" was never an independent decision — it is `chunkTokens` kept at 254, seen through the coupling.

**Evidence:** `docs/work/2026-08-21-arbitrary-embedding-models-plan.md:90` (D6, review F10c), `docs/work/2026-08-21-embedding-moe-architecture.md:185-191` (§5.3), `src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs:475-484` (`descriptor.ChunkTokens ?? Math.Min(MaxManifestChunkTokens, …)`).

### F4 — When the window grew to 8,190 the keep-254 decision was made again, on purpose, with chunk-size and eval-comparability reasons [READ]

ADR-0108 (2026-09-23) swapped MiniLM for granite (8,190-token window) and decided:
"`chunkTokens: 254` keeps memory chunks the size every measurement in this ADR used, instead of the
min(510, window − 2) its 8,190-token window would give. Smaller chunks also cost less per embed."
The eval table that certified granite over MiniLM and code-daemon was measured at 254-token memory
chunks, so changing the chunk size in the same change would have moved the retrieval numbers it was
choosing by. A test pins the intent verbatim: "the bundled manifest pins chunkTokens 254 — the size
its retrieval was measured at".

**Evidence:** `docs/adr/0108-one-bundled-engine-granite-small-fp16-on-the-gpu.md` (Decision 5; Consequences, "The bundled engine's memory chunk budget stays 254 tokens"), `tests/AiRaccoon.Tests/Integration/Embedding/EmbeddingServiceManifestBudgetTests.cs:72-80`, commit `4e6336c5` ("254 (the chunk size it was measured at)").

### F5 — The same day, the caller-visible query warning was re-ratified to quote the chunk budget [READ]

The 2026-09-23 amendment to ADR-0071 fixed `QueryLengthGuard` to take "its own
`IEmbeddingService.ResolveChunkBudgetFor` result — 254 for the bundled model, wider for a manifest
model" — so the pre-search warning and the actual trim name the same number by design. Whatever the
budget is, the query path speaks of it consistently; the amendment's concern was truthfulness of the
stated number, not its size.

**Evidence:** `docs/adr/0071-a-query-is-trimmed-deliberately-and-said-so.md` (Amendment 2026-09-23), `src/AiRaccoon.Core/Memory/QueryGuard/QueryLengthGuard.cs:5-31`.

### F6 — The project itself says 254 is a model-limit number, not a query-quality tuning [READ]

ADR-0072 rejected copying the 254 bound to the keyword leg with the words: "That is the embedding
model's hard limit (ADR-0071), not a retrieval argument, and the tail's rare terms are what keyword
search is best at." Its framing section states the position the query budget rests on: "The best
search quality is for queries that fit within the limit… a floor under a degraded case, not a
feature." ADR-0071 agrees: trimming "does not make a long query work. 407 tokens still become 254."

**Evidence:** `docs/adr/0072-a-term-budget-for-long-queries-is-not-adjudicable.md` (Decision, rejected option 5; "The framing this record exists to preserve"), `docs/adr/0071-a-query-is-trimmed-deliberately-and-said-so.md` (Consequences).

### F7 — Later chunk-size measurements support keeping chunks small but never tested long queries [READ]

The 2026-09-24 chunk-size record (re-run on MLX 2026-09-25) found recall flat from 128→510 tokens,
code file MRR **worse** at 1022 (0.822 → 0.788, interval excludes zero), embed cost superlinear
past ~256 tokens, and memory span recall better at 1022 but partly confounded ("a bigger net, not a
better vector"). Its corpora used 75 memory and 240 code **eval queries — short by construction**.
ADR-0072's live-bank measurements show why that gap matters: median query 61 characters / 8 tokens,
while the extremes are pasted machine output (largest 448,900 characters). The 254 budget binds
almost only on pastes, and no measurement covers that case on the vector leg.

**Evidence:** `docs/work/2026-09-24-chunk-size-vs-attention-window.md` (F1-F8, Method), `docs/work/2026-09-25-chunk-size-vs-attention-window-mlx.md` (F4, F8), `docs/adr/0072-a-term-budget-for-long-queries-is-not-adjudicable.md` (Context, live-bank measurements).

### F8 — No source on record argues 254 is the right query cap for the current engine [INFERRED]

Reasoning from F2-F7: the number's provenance is the retired MiniLM window (F2); it survives as the
chunk budget through a coupling set for engine consistency (F3) and kept for eval-comparability and
per-embed cost reasons (F4); every retrieval measurement held queries short (F7); and the project
has explicitly called 254 "not a retrieval argument" (F6). Therefore the decision "keep the query
budget at 254" is an unowned consequence of "keep `chunkTokens` 254" — defensible on cost and
comparability, unevidenced as a query-retrieval choice. This is a conclusion drawn from the cited
records, not a claim any of them makes in these words.

**Evidence:** reasoned from F2-F7 as cited above; no record states this conclusion directly.

### F9 — Whether the PR review threads argued the query budget separately is not checked [UNVERIFIED]

GitHub review discussion for #404 and #687 was not read; this record covers committed docs, code
and tests only.

## Still open

- **What a query budget should be for granite is unmeasured.** ADR-0072 already names what would
  settle it: a held-out family of long, pasted-output queries with expected documents pinned by
  someone other than the author of the change. Until that exists, 254 vs 510 vs 1022 for *queries*
  is not adjudicable — same trap ADR-0072 refused to walk into.
- **Widening only the query budget (leaving `chunkTokens` 254) is untested**: queries would then
  range far longer than the chunks they match against. Nothing measures whether that helps.
- **If the 2026-09-24/25 recommendation (memory at 1022) is ever adopted**, the same coupling that
  kept the query budget at 254 would silently move it to 1022 — queries and chunks together. No
  record says whether that is intended for queries; it deserves an explicit decision either way.
