# 0064. `memory_write` chunks like everything else

Date: 2026-08-15

Status: Accepted

## Context

WP3 step 1, the second of two code defects behind **blocker B2**, and the one the review called out as
having no operational workaround.

**`memory_write` did not chunk at all.** The only budget-aware chunking in the codebase lived in
`FileIngestor`; `SqliteMemoryStore.WriteAsync` inserted the caller's whole body as one row and handed
it to the embedder, which embeds only what fits the model's window and drops the rest — while the row
is still marked `embed_state='embedded'`.

Measured on the live bank: **555 `memory_write` rows, 320 of them over-window (57.7%), 114,883 tokens
never embedded.** Reproduced here on a 29,200-character body:

```
a 29200-character body must be split; it stored as 1 row(s) of 8123 tokens
```

8,123 tokens into a 256-token window — roughly **97% of that note absent from its own vector**, and
keyword-reachable only.

## Decision

**Route `memory_write` through the same chunker and the same budget resolution file ingest uses.**

`IFileIngestor` gains `ChunkToBudgetAsync(connection, content, ct)`. It resolves the budget through the
existing `ChunkSizeForAsync` — so it inherits ADR-0063's fix for an unset provider — and chunks with
the markdown handler, which is what agents write and what a `.md` ingest would pick for the same text.
Content that already fits comes back as a single chunk, so **the common short write is byte-for-byte
what it was**.

Three details that are decisions rather than mechanics:

**The path stays derived from the whole body.** `WritePathFor(request.Content)` is unchanged, so a
document keeps one identity however many rows it becomes — exactly as a file does. Each row's hash is
`ContentHash.Of(path, chunk)`, the same shape `FileIngestor` uses.

**The returned entry addresses chunk 0.** The post-insert lookup was
`WHERE path = @path … LIMIT 1` with no `ORDER BY`, which was harmless when a path meant one row and
unspecified the moment it meant several. `SelectEntryByPathAndHashInBucket` scopes it by hash, so the
caller is handed the row whose hash it was told. The two existing path-only callers are workspace
paths where path-only is intended and are unchanged.

**Idempotency moves from the value to the chunk.** The old early-return compared the whole
`request.Content` against a stored `value`; after chunking no row holds the whole body, so that check
stops matching for long writes. Each chunk insert now does the same exists-check `FileIngestor` does,
which makes a repeated write a no-op per chunk. Short writes still hit the original value-level
dedup, unchanged.

## Consequences

- A long note is retrievable by meaning across its whole length, not just its first window.
- `SqliteMemoryStore.cs` **shrank**: the per-chunk insert went to `WriteChunks.cs` beside
  `EntryBucket` and `ContextFilter` rather than into the store, so the file went 1,283 → **1,238**
  lines and the size ratchet is **lowered** 1243 → 1238 rather than raised. The ratchet caught this —
  its message says "split it, don't raise the cap", and that is what happened.
- A caller writing a long body gets back one hash addressing one chunk. `MemoryEntry` carries no chunk
  metadata, so the response cannot yet say "of N" — worth adding, not needed for the defect.
- **Rows already in a bank are unchanged.** The backfill is WP3 steps 3-4: an operational change
  against a live 167 MB bank, not revertible by git, and deliberately left to be sequenced by hand.
  Shipping this first is the right order either way — backfilling before `memory_write` chunks would
  re-poison the bank on the next write.

## Evidence

`tests/AiRaccoon.Tests/Integration/Memory/WriteChunksToBudgetTests.cs`, five cases against a real bank
with the bundled engine configured:

| | |
|---|---|
| the defect | a 29,200-char body → multiple rows, none over 256 BERT tokens — **watched red at "1 row(s) of 8123 tokens"** |
| no loss | summed chunk length ≥ the original (overlap may repeat; nothing may vanish) |
| the common case | a short write still stores exactly one row, addressable by its returned hash |
| the returned hash | addresses a real row, and the row it returns |
| idempotency | writing the same long body twice does not double the rows |

`Speed=Fast` 2165 passed. `ToolRefusalsTests` failed on two of five full runs during this work — a
different case each time, 34/34 in isolation — which is ADR-0062's documented signature and not this
change: a real break here would have failed the same cases every run.
