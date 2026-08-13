# Research: origin of the 48-token chunking overlay budget in FileIngestor

**Date:** 2026-08-13 **Question:** Why was the 48-token overlay budget (`DefaultOverlayTokens = 48` in `src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs`) settled on for chunking, and what is it tied to (model context windows,
retrieval-quality experiments, or an arbitrary constant)?

## Findings

### F1 — The 48-token overlay is a hardcoded default introduced on 2026-08-04 (P6b ADOPT-4), replacing an original 64 [READ]

`FileIngestor.cs:23` declares `DefaultOverlayTokens = 48`; the original constants were 512/64 (introduced with the single-memory.db foundation, P1) and the pair was changed to 256/48 in commit `b11fbe3b` ("feat: P6b ADOPT-4 — chunk defaults
256/48, tied to engine context window (P6b)"). The change moved the constants from `SqliteMemoryStore` to the ingest path; the WI-8 refactor (`33e5179e`) carried them into `FileIngestor.cs`. The rationale comment that accompanied the change
("512 tokens exceeded the bundled all-MiniLM-L6-v2's 256-token window, diluting embeddings via truncation") survives in the `b11fbe3b` diff but is absent from the current working-tree file — the constant stands bare at
`FileIngestor.cs:22-23` today.

**Evidence:** `git log -S "DefaultOverlayTokens" --all --oneline` → `5f2b69bf` (first, 512/64), `b11fbe3b` (→256/48), `33e5179e` (moved into FileIngestor.cs); `git show b11fbe3b` (diff on `SqliteMemoryStore.cs` with the truncation-dilution
comment, tests `TokenizerChunkerTests.cs`/`EmbeddingContextTests.cs`); `src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs:22-23`.

### F2 — The value descends from an adopted "overlay 32–48" range in the P6b plan's research addendum; the code picked the top of the range [READ]

The 2026-08-03 native-memory plan's §8 research addendum adopts, as P6b ADOPT item (4): "default maxTokens 256 (or 128–192 per S1's ~340-char sweet spot), overlay 32–48, chunk size tied to configured model context". No document inside the
plan or the code picks 48 specifically over 32/40/44 — the commit simply implements the constant 48, the upper bound of the adopted range. The range itself is attributed to two external ceaksan articles, and the plan grades its own primary
source: "rag-chunking-guide (PAYWALLED — only public TL;DR/FAQ analyzed; body UNVERIFIED, no guesses)". The other source (hybrid-search-fts5-vector-rrf) was fetched in full but its chunking-overlay content is not re-quoted in the plan. So
"48" is inherited external guidance, not an in-repo derivation.

**Evidence:** `docs/work/archive/2026-08-03-native-memory-plan.md:163` (source grading) and `:165` (ADOPT item 4 with "overlay 32–48"); `docs/plans/retrieval-improvement-a.md:294` ("Current: 256 tokens max, 48 token overlap" as status quo)
and `:298` (128/32 floated only as a consideration); `git show b11fbe3b` (the constant lands as 48 with no range discussion).

### F3 — The "tied to model context window" is a safety clamp, not a derivation of 48 [READ]

`ChunkSizeForAsync` returns `(Math.Min(256, context), Math.Min(48, Math.Max(0, context - 1)))` where `context = EmbeddingService.ContextTokensFor(provider, model)`. The context window bounds the chunk size (256 for the bundled
all-MiniLM-L6-v2, 8191 for OpenAI-compatible models, unknown providers default to 256) and the overlay is only clamped so it cannot exceed the model's window; the 48 itself is not computed from any window. The commit's rationale ties the
*chunk size* change (512→256) to the model's 256-token window (truncation dilution), not the overlay change (64→48).

**Evidence:** `src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs:167-184` (clamp at `:182-183`); `src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs` `ContextTokensFor` (local=256, openai=8191, default=256) as introduced in
`git show b11fbe3b`; `docs/explanation/architecture.md:219-222` ("Chunk bounds are clamped to the configured embedding engine's maximum input tokens…").

### F4 — For both known engines the clamp leaves the overlay at 48; it only bites for an engine with context ≤ 49 tokens [MEASURED]

Reproducing the exact formula with the constants from the code: local (context=256) → maxTokens=256, overlay=48; openai (context=8191) → maxTokens=256, overlay=48. The clamp changes the overlay only when `context - 1 < 48`, i.e. context ≤
49. So for every engine AiRaccoon currently knows, the effective overlay is exactly the constant 48 — the "tied to context" mechanism is dormant.

**Evidence:** `python3 -c` one-liner computing `min(48, max(0, ctx-1))` and `min(256, ctx)` for ctx ∈ {256, 8191} (constants read from `FileIngestor.cs:22-23,182-183` and `EmbeddingService.ContextTokensFor`): output "local all-MiniLM-L6-v2:
context=256 -> maxTokens=256, overlay=48 (unchanged=True)"; "openai-compatible: context=8191 -> maxTokens=256, overlay=48 (unchanged=True)". Machine: this Mac (macOS 26.6.1), Python 3.14.6, no repo code executed — pure formula reproduction.

### F5 — No harness sweep ever measured the overlay axis; the sweep was proposed, not run [READ]

The retrieval harness sweeps that exist cover other axes only: Wave 3 (source-affinity thresholds/λ, `docs/work/2026-08-04-wave3-source-affinity-sweep.md`) and Wave 4 (RRF k × weights × minScore × window, 96-point grid,
`docs/work/2026-08-04-wave4-rrf-sweep.md`, ADR 0006). Neither doc mentions overlay or chunk size as an axis (zero grep hits). The plan explicitly lists "chunk-size × overlay sweep as a harness axis" under CONSIDER / Wave F ("each with own
TDD + harness validation") — i.e., deliberately deferred, never executed. The harness's own records (baseline-retrieval-report, fts-plan-harness-spec) never vary overlay either. There is therefore no nDCG/recall evidence in the repo for 48
or any other overlay value.

**Evidence:** `grep -n "overlay|overlap" docs/work/2026-08-04-wave3-source-affinity-sweep.md docs/work/2026-08-04-wave4-rrf-sweep.md` (no matches); `docs/work/2026-08-04-wave4-rrf-sweep.md:10` (grid columns: k, weights, minScore, window —
no chunk/overlay); `docs/adr/0006-rrf-parameter-optimization.md`; `docs/work/archive/2026-08-03-native-memory-plan.md:166` (CONSIDER list); `git log --all --oneline --grep="sweep"` and `--grep="overlay" -i` (no chunk/overlay sweep commit).

### F6 — 48 is not derived from 256 by any documented formula; both the old and new pairs sit inside common 10–20% RAG overlap guidance [INFERRED]

48/256 = 18.75% and the superseded 64/512 = 12.5%; both fall in the 10–20% overlap band repeated across public RAG chunking guidance, and both were presumably picked from that band, but nothing in the repo states the ratio or any formula
linking 48 to 256. Reasoning from: the plan's adopted "32–48" range (F2), the two historical (maxTokens, overlay) pairs, and the measured ratios (F4's computation). The inference that the band is the operative heuristic is mine — the repo
never says so.

**Evidence:** measured ratios from the F4 computation (`48/256 = 18.75%`, `64/512 = 12.50%`); pairs from `git show 5f2b69bf` and `git show b11fbe3b`; no formula found in `grep -rn "overlay"` across `src/`, `docs/plans/`, `docs/work/`.

### F7 — The overlay is not user-configurable and no test pins the number 48; behavior (not value) is contracted [READ]

Chunk bounds come only from the constants and the engine clamp — the settings table keys are `embedding.provider/model/baseUrl/engine/apiKey` only, with no chunk-size or overlay key. FR-NM-10 pins "bounds, not sizes" by design ("Chunking is
deterministic, token-accurate and fence-aware"); the feature scenario asserts that a long markdown note yields multiple chunks whose second chunk reuses at least one line of the first ("configured overlay"), never the number 48. The
architecture doc describes the overlay's purpose as "context continuity between chunks" with the default "256 tokens per chunk with a 48-token overlay".

**Evidence:** `src/AiRaccoon.Infrastructure/Embedding/EmbeddingSettingsKeys.cs` (five keys, no chunk keys); `docs/work/features-native-memory/native-memory.feature:213-222` (`@FR-NM-10` rule + markdown overlay scenario);
`tests/AiRaccoon.Tests/BDD/NativeMemorySteps.cs:1207-1216` (line-reuse assertion); `docs/explanation/architecture.md:216-222`; `grep -rn "48"` over `tests/AiRaccoon.Tests/Chunking/` and `Store/SqliteMemoryStoreChunkingTests.cs` (no numeric
pin).

### F8 — What the ceaksan sources actually recommend for overlay could not be re-verified from this session [UNVERIFIED]

The plan's two named sources were not re-checkable here: `web_extract` is configured to a search-only backend and refuses URL extraction, and browser automation requires an interactive Chrome remote-debugging approval that was not granted.
The plan itself already grades the chunking guide's body unverified (paywalled at the time, F2), so this finding is an admission of the same gap, not a new claim.

## Still open

- **Why 48 and not 32/40/44 inside the adopted range:** no plan, commit, or comment decides it; the commit lands the constant without discussion. Settling it needs the paywalled ceaksan chunking-guide body (or its successor) to see whether
  it names 48 specifically — likely either "top of range for maximum continuity" or an inherited example value.
- **Would a sweep keep 48?** The chunk-size × overlay harness axis was proposed (CONSIDER/Wave F) and never built; there is no nDCG evidence for any overlay value. Building that axis and running it against the pinned corpus would answer
  whether 48 is anywhere near an optimum.
- **External-source wording:** the ceaksan articles' exact overlay recommendation is unverified here and was unverified at adoption time (per the plan's own grading); a fetch of both pages (hybrid-search-fts5-vector-rrf is publicly
  accessible) would close the provenance chain.
