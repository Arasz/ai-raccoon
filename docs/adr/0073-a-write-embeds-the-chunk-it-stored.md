# 0073. A write embeds the chunk it stored, not the document it came from

Date: 2026-08-15

Status: Accepted

Completes ADR-0064, which chunked `memory_write`. The chunking stands; what got embedded did not.

## Context

Found in production, by a log line rewritten hours earlier. On a live 1.19.0 server:

```
warn: EmbeddingService[414] A stored entry was shortened before embedding:
      818 tokens exceeded the 256-token window ...
```

**And the bank held zero rows over the window** — 24,707 entries, none above 254 tokens, measured
with the same tokenizer the chunker uses. Both facts were true at once, which is what made the
defect visible rather than merely present.

`SqliteMemoryStore.WriteAsync` chunks the content, inserts `chunks[0]` as the row — and then embeds
**`request.Content`**, the whole undivided document, into that row's vector.

So the text was chunked and the vector was not. The row stored an in-budget chunk while its vector
was built from the entire document truncated at 254 tokens. Every check WP3 put in place looked at
stored text, so all of them passed.

**ADR-0071 is why this was caught.** The old message said `Chunk truncated at embed time`, which was
consistent with a stored chunk being too long — a claim the bank contradicted, but only if someone
went and measured. The rewritten message asserts *"this one is always a write or an ingest"*, which
turned a vague warning into a testable statement about a specific code path.

## Decision

**Embed `chunks[0]`.**

```csharp
await _embedder.EmbedIfConfiguredAsync(connection, row.Id, chunks[0], cancellationToken);
```

One argument. The row's vector now comes from the row's own text.

## Consequences

**Event 414 becomes meaningful again.** It was firing on ordinary writes, so a non-zero count proved
nothing — the same way it proved nothing while queries shared it (ADR-0071). WP3's acceptance
criterion, *"EventId 414 count is zero"*, is measurable for the first time.

**Retrieval improves for multi-chunk writes, slightly.** The old vector was the document's first 254
tokens; the new one is `chunks[0]` as the chunker produced it. Those are close — chunking is
sequential — but not equal: chunk boundaries respect structure and carry overlay, a token cut does
neither. The gain is real and small, and overstating it would be its own defect.

**The test asserts the sharp form**: *every* text reaching the embedder is one of the stored chunks.
Two weaker versions were tried and rejected. `CallCountFor(chunk)` reports zero against a *correct*
implementation, because `EmbedIfConfiguredAsync` deliberately batches `[value, headingPath]` into one
call. And "the first chunk by `chunk_index`" is arbitrary for a write with no `source_file`, since
nothing recomputes that column — the test would have asserted against the wrong chunk.

**Existing rows are not repaired by this.** Any multi-chunk `memory_write` before 1.19.1 still has a
first chunk whose vector came from its document. `chunk-backfill` will not touch them: their text is
in budget, which is the only thing it looks at. Left alone deliberately — the divergence is small,
and a second bank-wide re-embed to correct it is not obviously worth the cost.
