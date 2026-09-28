# 0125 — config D: memory chunks at 1022, code stays at 510

Date: 2026-09-28

Status: Accepted

## Context

ADR-0108 Decision 5 pinned `chunkTokens: 254` in the bundled manifest so memory chunks stayed
"the size every measurement in this ADR used". That was a stopgap with a MiniLM pedigree: 254 is
the old all-MiniLM-L6-v2 content window (256 minus the two special tokens), not a property of
granite-embedding-small-english-r2, whose window is 8,190 tokens. Three research records now
measure the question directly.

- The CPU measurement (`docs/work/2026-09-24-chunk-size-vs-attention-window.md`, F1-F8) finds no
  attention cliff at 128 tokens (F1-F2) and the cost curve bending at 512, not 128 or 254 (F5).
  Memory retrieval gains at 1022-token chunks: section MRR@10 rises 0.551 → 0.716 (F4; the
  interval there reads +0.077..+0.165..+0.253, and the MLX re-run reports +0.166 with
  +0.078..+0.254 — part of the gain is a bigger net, F4 says so itself). Code retrieval loses:
  file MRR@10 falls 0.822 → 0.788 at 1022 (−0.034, interval −0.066..−0.034..−0.002, F3), with
  targets past token 512 inside a chunk found markedly less often.
- The MLX re-run (`docs/work/2026-09-25-chunk-size-vs-attention-window-mlx.md`, F1-F6 and its
  config D table) reproduces those recall findings within ±0.007 MRR and changes the cost picture:
  embed cost per 1k tokens is flat from 128 to 1022 (46-57 ms per 1k, F5), and the real risk is
  process footprint, which grows with the number of distinct row lengths. Unpadded, a 1022-token
  memory corpus peaks at 17.9 GB on a 24 GB machine; padded to 64-token buckets the same corpus
  peaks near 3.7 GB with identical vectors (cosine ≥ 0.999998). Config D is the best trade on that
  record only together with bucket padding — the precondition ADR-0114 ships for MLX.
- The query-budget record (`docs/work/2026-09-28-why-the-query-budget-stays-254.md`, F1-F9) shows
  254 was never a query decision: it is `chunkTokens` seen through the query-trim coupling
  (F2-F3), and the later keep-254 re-ratification rested on chunk-size and eval-comparability
  reasons (F4), not on any query measurement. No source on record argues 254 is the right query
  cap for granite (F8). Its third "Still open" bullet — that adopting 1022 would silently move the
  query budget too, which "deserves an explicit decision either way" — is decided here (see
  Decision 3).

Config D is the combination the measurements support: memory chunks at 1022, code chunks at 510.
It supersedes ADR-0108 Decision 5's eval-comparability reasoning, which this measurement set
replaces.

## Decision

**1. The bundled manifest's `chunkTokens` becomes 1022; the code corpus stays at 510.** Memory
chunks are budgeted by the bundled manifest
(`src/AiRaccoon/Models/granite-embedding-small-english-r2/ai-raccoon.manifest.json`, `chunkTokens`)
and resolve through `EmbeddingService.ManifestContentBudget`
(`src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs:470-482`), which reads an explicit
`chunkTokens` ahead of the `min(510, ctx − 2)` fallback. Code chunks are budgeted by
`CodeChunker.DefaultBudget = EmbeddingService.MaxManifestChunkTokens` — a fixed 510 that reads no
`chunkTokens` (`src/AiRaccoon.Infrastructure/Chunking/CodeChunker.cs:21-33`) — so the code corpus
does not move.

**2. Existing banks migrate by re-chunking to the new budget AND re-embedding — both.** Changing
`chunkTokens` changes the bundled manifest's bytes, and the engine fingerprint is a hash of exactly
those bytes (`EmbeddingService.cs:308-314`), so every bank re-embeds once on the change through
ADR-0108's "one engine, one re-embed" mechanism. A re-chunk phase rides the same migration, before
the embed loop, so every vector written is computed from post-re-chunk text and no row reaches
`embedded` at stale bounds. Budget drift (a bank that consumed the new manifest without the
re-chunk) re-chunks without discarding vectors that are already in the engine's space.

**3. The query-trim budget follows `chunkTokens` to 1022. The coupling is deliberate and
ratified.** The trim budget is the chunk budget by construction, not by coincidence:
`TrimQueryToWindow` trims manifest models to `ManifestContentBudget`
(`EmbeddingService.cs:143-165`, budget at `:154`) — the same `descriptor.ChunkTokens` value that
bounds chunks — and the caller-visible warning takes the same resolved budget through
`ResolveChunkBudgetFor` (`src/AiRaccoon.Core/Memory/QueryGuard/QueryLengthGuard.cs:33-45`,
ADR-0071's 2026-09-23 amendment). Flipping `chunkTokens` therefore moves the trim point to 1022
and the warning threshold to about 4,024 characters of English prose (the guard's 1,000/254
chars-per-token ratio scaled to 1022) with no code change. Queries and chunks move together: that
is the intended behavior, decided here, not an accident of implementation.

**4. No pre-adoption retrieval measurement is run** (owner's call). Queries under the old
~255-token budget are processed exactly as before — their trim never fired, and does not fire now.
Longer queries were already trimmed before, so more context is strictly better than the trim they
were getting. A post-merge measurement is named in Consequences instead.

**5. Config D ships only with the padding precondition in place, pinned by tests.** MLX sessions
pad rows to 64-token buckets (ADR-0114), and 1022 + 2 = 1024 is both a multiple of 64 and exactly
the CoreML per-bucket window — zero waste, and one token more would push every CoreML row to the
CPU fallback. The precondition (a real MLX session pads) and the manifest's alignment with the
bucket rules are pinned by tests so the unpadded 17.9 GB footprint cannot return unnoticed.

## Consequences

- **Migration on first start.** Every bank re-chunks and re-embeds once on its own after
  upgrading, with tool calls refused for the duration (the ADR-0076 outbox lock, the shape
  `docs/reference/breaking-changes.md` has promised since 1.47.0). Minutes on a large bank:
  ADR-0076 measured 357 s of refused calls draining 25,917 rows on the old engine, and ADR-0108
  quoted roughly 6 minutes for that bank. Embed cost per 1k tokens is flat at 1022 on MLX
  (2026-09-25 record F5), and re-chunking shrinks the row count where content was split, so the
  drain stays minutes-scale — an estimate from those anchors, not a measurement of this migration.
- **Accepted cost: per-row metadata resets where chunk boundaries move.** For a note group exactly
  three columns are lost — `rating`, `access_count` and `last_accessed_at` (the same loss
  `repair reingest` already documents). Everything else is carried forward to the replacement
  rows: `path`, `source_file`, `section`, the scope/context/workspace keys, `source_id`,
  `agent_id`, `created_at` and `ttl_days`. A mirror group re-chunked from disk re-ingests inside
  its own scope/context/workspace partition (the same file stored in two contexts keeps both;
  workspace rows re-chunk like any other bucket) and loses one more column, `ttl_days`: the file
  content carries no TTL for `FileIngestor` to read, so a mirror row that had a TTL stops expiring
  until one is set again (`memory_set_ttl`). Carrying a rating across moved boundaries was
  considered and refused: there is no 1:1 row mapping after boundaries move, and inventing one is
  policy, not mechanics.
- **Terminal skips: rows with nothing re-chunkable are not re-chunked.** A mirror group is
  terminal-skipped when its source file is not usable — deleted or renamed, unreadable, or a path
  no file-type handler claims — because there is nothing to re-chunk from, and its `path` carries
  no content commitment a reassembly could be verified against. Those rows keep their stored chunk
  bounds and are still re-embedded at those bounds by the same migration. The phase report counts
  them and every other skip class, and is read in production: event 448 logs the counts once per
  pass. None of the skips gate the migration's completion.
- **Unproven note groups: bounded retry, then a named residual population.** A note group whose
  merge no join proves — rows with line-ending mixes `NoteTextOrder` cannot reproduce, a row
  rewritten outside the product, or a reassembly that exhausts its work bound — is never guessed
  at. It is retried once per server start while `embedding.chunkBudget.retryAttempts` (keyed by
  the resolved budget) has windows left; after 3 unproven passes the group is left terminal for
  this budget and the `embedding.chunkBudget` stamp is written anyway, so one
  permanently-unprovable group cannot re-open the migration (and its tool-refusal window) forever.
  The counter clears whenever a pass converges. A single row that already fits the resolved budget
  counts unchanged — it needs no merge, and would otherwise stay retryable forever. The residual
  population that ships at old bounds is exactly this class plus the terminal mirror rows above.
- **Code-corpus re-embed churn (bundled-default code engine only).** For a bank running the
  bundled default for code, the fingerprint change re-embeds the code corpus too, to identical
  vectors — its chunk boundaries never moved (Decision 1). That is accepted churn inside "one
  engine, one re-embed", whose fingerprint deliberately covers every manifest byte (ADR-0108
  Decision 3). A bank with a separately-activated code model fingerprints that model's own
  manifest and sees no churn at all.
- Queries now trim at 1022 tokens and the query-length warning threshold widens to about 4,024
  characters (Decision 3). A top-5 at 1022 hands the agent about 3,700 tokens of context per
  search, against about 1,000 at 254 (2026-09-24 record F4) — more context per hit, which is the
  point.
- On MLX the process footprint stays near the padded curve (about 3.7 GB for the memory corpus at
  1022) as long as ADR-0114's padding holds — hence Decision 5's test pin. The WebGPU path pads by
  decision (ADR-0114: CPU, WebGPU and CUDA run rows at their own length), and per-shape footprint
  growth there at 1022 is **unverified** (the 2026-09-24 record's F7 and the MLX record's own
  "Still open" both say so). Residual risk, carried by the follow-up below.
- ADR-0108 Decision 5 and its Consequences bullet on the 254-token memory budget are superseded in
  part by this record (pointer blockquotes added there). ADR-0071's query trim at 254 stays as the
  historical record; its 2026-09-23 amendment (the budget comes from `ResolveChunkBudgetFor`) is
  what makes the move automatic.
- **Planned follow-up, not part of this change: a post-merge 254-vs-1022 retrieval measurement on
  the live bank**, as a separate task. It includes the WebGPU footprint question above, and the
  re-chunk phase's own wall-clock — its scan, file reads, note-reassembly verification and column
  repair are not in the embed-token estimate and must be measured, not inferred — along with the
  outage window actually observed on a real bank.

## Alternatives considered

- **B — both corpora at 510.** Moves nothing measurable: the memory gain is at 1022, and
  equalizing memory at 510 buys +0.011 section MRR, inside the noise (2026-09-24 record F4).
- **C — both corpora at 1022.** Loses code retrieval: −0.034 file MRR with an interval that
  excludes zero (2026-09-24 record F3). Rejected on that record's own interval.
- **Keep 254.** Rejects the measured memory gain (+0.165..+0.166 span/section MRR, intervals
  excluding zero in both records) to preserve eval comparability with measurements that are now
  superseded — the exact reason ADR-0108 Decision 5 gave is what this measurement set replaces.
- **Widen only the query budget, leave `chunkTokens` at 254.** Untested: queries would range far
  longer than the chunks they match against and nothing measures whether that helps (2026-09-28
  record, "Still open" bullet 2). It would also break the ratified coupling of Decision 3 for no
  measured gain.
