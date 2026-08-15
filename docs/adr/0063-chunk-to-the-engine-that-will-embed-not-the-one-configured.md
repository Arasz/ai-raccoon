# 0063. Chunk to the engine that will embed, not the one configured

Date: 2026-08-15

Status: Accepted

## Context

WP3 step 2, one of the two code defects behind **blocker B2** — 42.7% of the live bank's entries
exceed the embedder's 256-token window, with the overflow dropped rather than split.

`FileIngestor.ChunkSizeForAsync` resolved the chunk budget from `embedding.provider`, and when that
setting was **absent** it returned the default budget with the **o200k** counter:

```csharp
if (string.IsNullOrWhiteSpace(provider))
{
    return (DefaultMaxTokens, ChunkingDefaults.OverlayTokens, null);   // null = o200k
}
```

The bundled model tokenizes **BERT WordPiece**, which produces more tokens than o200k for the same
English prose. So an unconfigured bank chunked against a counter that does not match the model that
will eventually embed those chunks.

**And "eventually" is the whole problem.** Configuring an engine later re-embeds the bank —
`EmbeddingService.EngineFingerprint` drives that — but it **never re-chunks** it. So
ingest-then-configure, a supported and documented order, wrote boundaries that were permanently
wrong: every one of those chunks is silently truncated at embed time, forever, and the row is still
marked `embed_state='embedded'`.

## Decision

**An unset provider resolves to the bundled local engine, not to the o200k default.**

```csharp
var provider = string.IsNullOrWhiteSpace(configured) ? BundledProvider : configured;
```

Nothing embeds while the bank is unconfigured. That is exactly why the budget must be the *future*
engine's: the boundaries drawn now are the ones the engine is handed later.

**Chunking to the most restrictive plausible window is safe in the direction that matters.** A chunk
that fits the bundled model's 254 content tokens also fits any larger window, so a bank later
configured for a remote engine gets slightly more chunks than it strictly needed. A chunk sized for
o200k does **not** fit the bundled model, and that asymmetry is the defect.

Rejected: **refusing to ingest until a provider is configured**, the plan's other option. It converts
a supported order into an error and breaks every bank that ingests before configuring — a larger
behaviour change than the defect warrants, for no additional safety over choosing the conservative
budget.

## Consequences

- Ingest into an unconfigured bank produces in-budget chunks instead of a permanent truncation.
- A bank later configured for a larger-window engine carries slightly smaller chunks than optimal.
  Measured against the alternative — permanently truncated vectors — this is not a close call.
- `BundledProvider` names the default engine in one place in `FileIngestor`. `EmbeddingService`
  still spells `"local"` inline in its own switches; unifying those is a tidy, not part of this fix.
- **This does not fix `memory_write`**, which does not chunk at all (WP3 step 1, the other code
  defect: 555 live rows, 320 of them over-window). Nor does it fix rows already in a bank — the
  backfill is WP3 steps 3-4 and is an operational change against a live 167 MB bank, deliberately
  left to be sequenced by hand.

## Evidence

`tests/AiRaccoon.Tests/Integration/ChunkBudgetWithNoProviderTests.cs`, which ingests real repo prose
into a bank with **no** configured provider — synthetic filler tokenizes with a lower BERT/o200k ratio
and does not reproduce the mismatch, as `ChunkBudgetIsEngineAwareTests` already records.

Watched red first, with the measurement in the failure message:

```
23 of 37 chunks exceed the bundled model's 256-token window; worst 295 tokens.
Configuring the engine later re-embeds but never re-chunks, so these boundaries are permanent.
```

Green after, alongside the existing `ChunkBudgetIsEngineAwareTests` (configured `local`), so the
configured path is unchanged. `Speed=Fast` 2165 passed; the retrieval, ingest and chunking suites 224
passed.
