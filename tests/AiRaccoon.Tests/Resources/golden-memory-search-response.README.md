# golden-memory-search-response.json

Captured pre-feature (WP1, `docs/work/2026-08-21-code-search-implementation-plan.md` §3.6):
the exact `memory_search` response BEFORE the `kind` parameter or the `code` envelope key
exist. Produced by `GoldenMemorySearchResponseTests.Search_TodaysPreKindEnvelope_MatchesTheCommittedGoldenFile`
(`tests/AiRaccoon.Tests/Integration/GoldenMemorySearchResponseTests.cs`) against a small,
deterministic two-entry fixture bank (fixed content, fixed embedding endpoint, fixed clock).

`Meta.CorrelationId` is a per-call random value (`Guid.CreateVersion7()`,
`src/AiRaccoon/Tools/MemoryTools.cs`); this file pins it to the literal placeholder
`<CORRELATION_ID>` rather than a real id, and the capturing test normalizes its own fresh
capture the same way before comparing.

**WP6-T01** (envelope/`kind` work) reuses this same fixture bank and comparison technique to
prove the post-feature `kind=memory` response is semantically identical to this file — same
keys, order, and values, modulo `Meta.CorrelationId` — i.e. it never gains a `code` key and
never reorders `results`.
