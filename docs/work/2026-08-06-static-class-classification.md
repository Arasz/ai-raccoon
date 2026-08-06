# Static-class classification audit — 2026-08-06

> Part-2 plan item 4, executed as a classification under the codified rule
> (`.ai-badger/invariants/static-classes.md`): **static classes are allowed for
> extensions, constants, and pure functions (no state, no I/O, no injectable deps);
> anything with state, I/O, or dependencies is an injectable component.**
> The ConfigCommands static dispatcher is the one sanctioned exception.
> Coverage map verified by the MoE review (test-engineer coverage matrix + code-reviewer
> spot-checks, main @ c1709c1).

## Verdict: 0 conversions required

Every listed class is exempt under the codified rule — all are pure functions,
constants, one-shot bootstrap, or stateless helpers. The audit's value is the
classification + the coverage-gap note; no class needs to become injectable.

## Core layer (`src/AiRaccoon.Core/`)

| Class | Shape | Rule status | Test coverage |
|---|---|---|---|
| `AccessModePolicy` | parse/serialize pure functions | exempt — pure | dedicated tests |
| `MarkdownChunker` | pure splitter (no state) | exempt — pure | dedicated tests |
| `DegradationPolicy` | pure policy | exempt — pure | dedicated tests |
| `RatingPolicy` | pure policy | exempt — pure | dedicated tests |
| `ContentHash` | SHA-256 pure function | exempt — pure | pinned vectors |
| `OpenSshPrivateKeyParser` | pure parser | exempt — pure | dedicated tests |
| `SshKeyDerivation` | pure derivation | exempt — pure | dedicated tests |
| `ContextNaming` | pure naming | exempt — pure | dedicated tests |
| `WatchListFormat` | pure formatting | exempt — pure | dedicated tests |
| `WatchPath` | pure path logic | exempt — pure | dedicated tests |
| `WatchScopeList` | pure scope logic | exempt — pure | **gap: only CLI-level tests, no Parse/ToJson edge cases** |
| `WatchConfigKeys` | constants | exempt — constants | n/a |
| `ValidatorConfiguration` | internal static ModuleInitializer | exempt — one-shot bootstrap | n/a |

## Infrastructure layer (`src/AiRaccoon.Infrastructure/`)

| Class | Shape | Rule status | Test coverage |
|---|---|---|---|
| `RuntimePlatform` | RID→asset map, static readonly immutable dict | exempt — constants | **gap: zero test references** |
| `EmbeddingBlob` | blob ops | exempt — pure | indirect |
| `EmbeddingMath` | math ops | exempt — pure | dedicated tests |
| `StructureFusion` | fusion logic | exempt — pure | dedicated tests |
| `SyncProviderParser` | pure parser | exempt — pure | **gap: light (one typo-provider row)** |
| `ContextResolver` | pure resolution | exempt — pure | dedicated tests |
| `FtsQueryNormalizer` | query normalization | exempt — pure | dedicated tests |
| `LikePattern` | pattern building | exempt — pure | indirect |
| `MemorySchema` | DDL building | exempt — pure | indirect |
| `MemorySql` | SQL building | exempt — pure | indirect |
| `ReciprocalRankFusion` | pure ranking | exempt — pure | dedicated tests |
| `SearchContexts` | pure context building | exempt — pure | indirect |
| `SearchResultMerger` | pure merging | exempt — pure | indirect |
| `SnippetFallback` | pure fallback | exempt — pure | dedicated tests |
| `SourceAffinityRanker` | pure ranking | exempt — pure | dedicated tests |
| `SourcePathQuery` | query building | exempt — pure | dedicated tests |
| `SqliteEncryptionInit` | one-shot native-init guard (`_initialized`) | exempt — one-shot bootstrap; static is correct | indirect |

## Coverage gaps (the only real follow-up from this audit)

1. **`RuntimePlatform`** — zero test references. If it is ever touched (new RID, path
   change), it must get its first tests in the same change (OS/RID/home-dir detection is
   the one conversion-adjacent class that could silently change behavior).
2. **`WatchScopeList`** — only exercised through CLI tests; no direct Parse/ToJson edge
   cases (malformed JSON array, empty, star).
3. **`SyncProviderParser`** — one indirect case; a direct parse table (s3/azure/unknown/
   case-insensitivity) is cheap and worth adding with the sync family extraction (PR E).

These are NOT conversion tasks — they are test-investment notes. None blocks Part 2.

## Evidence

- Coverage matrix: test-engineer review (17 dedicated test files, 8 indirect, 3 gaps),
  spot-checked by code-reviewer (independently counted 17 dedicated).
- Rule text approved by owner: ruling D5 (2026-08-06, APPROVE).
- Code-reviewer fold-in: `WatchConfigKeys` + `ValidatorConfiguration` added to the table;
  `WatchConfig` is a record, correctly exempt.
