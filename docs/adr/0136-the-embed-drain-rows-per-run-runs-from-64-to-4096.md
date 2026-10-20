# 0136 — The embed drain's rows-per-run runs from 64 to 4096

Date: 2026-10-20

Status: Accepted

## Context

The embed drain (`EmbedDrainService`, ADR-0091's single consumer) takes one signal, opens the
bank, and drains up to `maintenance.embed-rows-per-run.global` pending rows for one corpus
(both corpora share the key; the default is 128). The key has been settable since WP11-C and
its value was chosen — not measured — as `4 * EntryEmbedder.BatchSize` = 128, simply to keep
the pre-governor pacing unchanged.

What that value *does* was never written down, and an operator reading the help text ("sets the
embed drain's rows-per-signal") has no way to tell what raising it costs. This ADR records the
mechanics, the measured anchors, and the range the key accepts.

### What one pass does

1. Reads the setting (every pass — no restart needed).
2. Opens a connection and runs one `SELECT ... LIMIT <rows-per-run>` over the pending rows.
3. **Materialises the whole result as one `List`** before it does anything else with it.
4. Sub-batches that list at `BatchSize` = 32: each 32-row sub-batch is marked inside **one**
   `BEGIN IMMEDIATE`/`COMMIT`.
5. Embeds: the local ONNX generator runs **one row per session call**, so a pass of N rows
   makes N session calls in ceil(N/32) transactions.
6. Records the pass's rows and duration in the metrics histogram, and — if the pass drained at
   least `rows-per-run` rows — **re-signals its own corpus**, so the next pass starts at once
   instead of waiting out the 15 s on-demand poll.

### The measured anchors

Row sizes, read read-only from the live bank (`/Users/arasz/.ai-raccoon/memory.db`, 25 190
`entries` rows and 72 129 `code_entries` rows, all embedded, no pending rows):

| corpus | rows | mean value size | p95 | max |
|---|---|---|---|---|
| memory (`entries`) | 25 190 | 2 325.6 B | 3 814 B | 11 945 B |
| code (`code_entries`) | 72 129 | 1 297.5 B | — | 4 657 B |

A .NET `string` is UTF-16, so a memory row of 2 325.6 UTF-8 bytes occupies roughly
2 × 2 325.6 + 22 ≈ 4 673 bytes of managed memory once materialised.

Throughput, from the shipped device benchmark (README, Apple M4, the same 4 914-chunk corpus):
33 s on the Neural Engine, 49 s on MLX, 70 s on the default WebGPU engine — i.e. roughly
70–150 rows/s, whatever the pass size.

### What the value does *not* change

- **Per-row embedding cost.** The session call is one row either way; the padded sequence length
  is `LengthBuckets.PaddedLength` (a multiple of 64, never past the window). A bigger pass does
  not embed a row faster.
- **The native ONNX peak.** The session shape is 1 × padded length, not N × padded length; the
  5.9–9.5 GB figure in `docs/work/2026-09-23-server-memory-usage.md` is a property of the
  32-row DB batch and the model weights, not of the rows-per-run. ADR-0110/0118's memory work
  did not touch this key.
- **The transaction scope per row.** A row is still marked inside a 32-row transaction
  (`EntryEmbedderMarksABatchInOneTransactionTests`), and the remote-provider request size is
  still 32 rows.

## Decision

**`maintenance.embed-rows-per-run.global` accepts 64 to 4096, default 128.**

- **The floor is 64** — two of the embedder's 32-row sub-batches. Below 64 a pass does at most
  one generator call, and because a pass that reaches its budget re-signals itself, the fixed
  per-pass cost (open the bank, read the setting, open a telemetry span, a pump round-trip) is
  paid once per 64 rows or fewer instead of being amortised over real work. The default 128
  already sits at four sub-batches, so the floor only rules out the pathological values.
- **The ceiling stays 4096** — unchanged from WP11-C, and still `128 * BatchSize`. It is the
  bound on the one-shot materialisation: 4 096 rows × ~4 673 B ≈ **19 MB** of managed memory
  for the memory corpus at once, and ≈ 11 MB for the code corpus (1 297.5 B/row × 2 + 22).
  A value above 4096 buys nothing — the per-row cost does not fall — and pays in a larger
  resident set, so it is refused rather than clamped silently at the CLI.

A value outside the range is invalid: the CLI refuses to write it, and a value already in the
settings table is clamped to the nearest bound by the drain, which warns once per distinct bad
value (event 1006). The default (128) is inside the range, so a bank that never set the key
behaves exactly as it did before.

### What the value is for

It is a **pass-size** knob, not a throughput knob. Its two effects are:

| rows-per-run | passes for a 4 914-row backlog | transactions per pass (÷32) | managed rows held at once (memory corpus) |
|---|---|---|---|
| 64 (floor) | 77 | 2 | ~0.3 MB |
| **128 (default)** | **39** | **4** | **~0.6 MB** |
| 512 | 10 | 16 | ~2.4 MB |
| 4096 (ceiling) | 2 | 128 | ~19 MB |

Raising it buys fewer passes (less per-pass overhead, less waiting on the poll) at the price of
a proportionally larger resident set for the duration of a pass. Lowering it towards the floor
buys a smaller resident set at the price of more passes. The throughput curve is flat between
them, because the row is the unit of work, not the pass.

## Consequences

- An operator who wants a faster drain on a large backlog can raise the value to 512 or 1024
  and know what it costs: a few MB of managed memory, and no change to the per-row cost.
- An operator on a memory-constrained host can lower it to 64 and know the floor is safe: two
  sub-batches per pass, and the re-signal rule cannot spin.
- The number 128 stays the default, so nothing changes for anyone who has not touched the key.
- The old accepted range ("any positive integer, at most 4096") is narrowed only at the bottom.
  A stored value below 64 is clamped to 64 and warned about; a stored value above 4096 is
  clamped to 4096 and warned about. Neither is rewritten in the settings table.

**Evidence:** `src/AiRaccoon.Core/Memory/BankMaintenanceConfigKeys.cs` (the constants and the
parse rule); `src/AiRaccoon.Infrastructure/Embedding/EmbedDrainService.cs:113-135` (the pass,
the re-signal rule) and `:160-170` (the clamp and the warning);
`src/AiRaccoon.Infrastructure/Embedding/EntryEmbedder.cs:34` and `CodeEmbedder.cs:22`
(`BatchSize` = 32) and `:446-448`/`:87-89` (the sub-batch loops);
`src/AiRaccoon.Infrastructure/Embedding/OnnxEmbeddingGenerator.cs:746-756` (one row per
session call); `tests/AiRaccoon.Tests/Integration/Embedding/EntryEmbedderMarksABatchInOneTransactionTests.cs`
(one transaction per 32-row sub-batch); `docs/work/2026-09-23-server-memory-usage.md` (the
native peak and the decision to leave `BatchSize` at 32); `README.md` (the M4 drain benchmark);
`/Users/arasz/.ai-raccoon/memory.db` read with `mode=ro` for the row-size table;
`docs/work/2026-10-20-embed-rows-per-run-range.md` (the provenance record for the 128).
