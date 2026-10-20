# Research: the embed drain's rows-per-run — where 128 came from, and what range it should accept

**Date:** 2026-10-20
**Question:** How was the embed drain's rows-per-run value of 128 chosen, and what range should the configurable value accept (64 to 2048)?

## Findings

### F1 — 128 is the pre-governor drain size written as 4 × the embedder's 32-row sub-batch [READ]

`BankMaintenanceConfigKeys.DefaultEmbedRowsPerRun` is 128 and the key's own comment says it is
"today's 4 * EntryEmbedder.BatchSize". The embedder's sub-batch is 32 rows for both corpora, so
128 is four generator calls per drain pass — not a throughput target derived from a benchmark.

**Evidence:** `src/AiRaccoon.Core/Memory/BankMaintenanceConfigKeys.cs:44-49`;
`src/AiRaccoon.Infrastructure/Embedding/EntryEmbedder.cs:34` (`internal const int BatchSize = 32`);
`src/AiRaccoon.Infrastructure/Embedding/CodeEmbedder.cs:22` (same 32).

### F2 — the value was chosen to hold the pacing still while the drain became a single consumer [READ]

The load governor (WP11) replaced several unbounded drains with one consumer that takes one
signal and drains a bounded number of rows. The plan states the reason for the number outright:
"the pace stays exactly today's: 128 rows per signal" — the change was about not saturating the
inference pool, and the row count was held at the value the previous code already used.

**Evidence:** `docs/work/2026-08-22-post-delta-3-plan.md:839` (the pacing decision) and `:852`
(the owner-visible knob table: "**128** (today's `4 * BatchSize`, unchanged)"); the trigger and
owner's complaint in the same file at `docs/work/2026-08-22-post-delta-3-plan.md:546-560`.

### F3 — the option already existed; what was missing was the range, not the knob [READ]

`maintenance.embed-rows-per-run.global` was already settable at runtime
(`ai-raccoon settings maintenance embed-rows-per-run {rows}`), read by the drain on every pass,
and applied live with no restart. What it lacked was a floor: any positive integer was accepted
up to a ceiling of 4096, so "one row per pass" was a legal value.

**Evidence:** `src/AiRaccoon/Setup/Cli/CliCommandTree.cs:626-628` (the command) and
`src/AiRaccoon/Setup/Cli/Commands/MaintenanceCommands.cs:42-57` (the setter);
`src/AiRaccoon.Infrastructure/Embedding/EmbedDrainService.cs:113-120` (the setting is re-read on
every pass, so a change needs no restart).

### F4 — the 4096 ceiling bounded the drain's one-shot materialisation of its `SELECT … LIMIT` [READ]

The drain selects up to rows-per-run pending rows and materialises the whole result as one List
before sub-batching it at 32. The old ceiling (4096 = 128 × 32) was chosen to bound that burst
while leaving headroom above the 128 default.

**Evidence:** `src/AiRaccoon.Core/Memory/BankMaintenanceConfigKeys.cs:65` (the ceiling's
rationale, as written before this change); the sub-batching loops in
`src/AiRaccoon.Infrastructure/Embedding/EntryEmbedder.cs:446-448` and
`src/AiRaccoon.Infrastructure/Embedding/CodeEmbedder.cs:87-89`.

### F5 — a pass that reaches its budget re-signals itself, so a tiny rows-per-run multiplies per-pass overhead [READ]

After a pass, the drain re-enqueues its own corpus when it drained at least rows-per-run rows.
Each pass opens the bank, reads the setting, and opens a telemetry span before it embeds
anything. Below 64 rows a pass does at most one generator call (the 32-row sub-batch), so that
fixed per-pass cost is not amortised over the work.

**Evidence:** `src/AiRaccoon.Infrastructure/Embedding/EmbedDrainService.cs:128` (the re-signal
rule) and `:112-135` (what a pass does before it embeds).

### F6 — 64…2048 is a coherent bracket of the 32-row sub-batch [INFERRED]

Reasoning from F1, F4 and F5: the floor of 64 is two full sub-batches, the ceiling of 2048 is
64 sub-batches — exactly half the old 128-sub-batch burst, with the 128 default still inside the
range so an unset bank behaves exactly as before. This is a shape argument, not a measurement:
nothing here shows that 2048 rows is a comfortable materialisation on a small machine, or that
64 is where the per-pass overhead stops dominating.

### F7 — the new range is covered by tests that go red without it [MEASURED]

The four touched test classes pass with the range in place and fail when the range is mutated
away (floor 64 → 1, ceiling 2048 → 4096), so the checks are real rather than vacuous.

**Evidence:** `dotnet test tests/AiRaccoon.Tests/AiRaccoon.Tests.csproj --filter
"FullyQualifiedName~BankMaintenanceConfigKeysTests|FullyQualifiedName~RowBudgetTests|FullyQualifiedName~ConfigCommandsMaintenanceTests|FullyQualifiedName~CliSetterTranscriptTests"`
on this machine (macOS 27.0.1, arm64, 10 cores, 24 GB, .NET SDK 10.0.402): 91 tests, 0 failed,
2.2 s. The same command with `MinEmbedRowsPerRun` mutated to 1 and `MaxEmbedRowsPerRun` to 4096:
91 tests, **6 failed** (the floor, the ceiling, the CLI's range message and the drain's floor
clamp). The mutation was reverted before the run above was repeated green.

### F8 — the real-CPU effect of the floor is unmeasured [UNVERIFIED]

No run with the bundled ONNX model compared a 64-row pass against a 128-row pass on a real
backlog, so the claim "below 64 the per-pass overhead outweighs the work" rests on the shape of
the code (F5), not on a measurement. Settling it needs a real bank with a pending backlog and the
bundled model, measuring pass duration and CPU across the range.

## Still open

- The memory cost of the one-shot materialisation at the new ceiling (2048 pending rows of text
  held in one List) was not measured; 4096 was the previous bound and 2048 halves it, but the
  per-row size depends on the corpus.
- An operator who set a value outside the new range before this release (e.g. 4096, or 7) keeps
  that raw value in the settings table: the drain clamps it and warns once per distinct bad
  value, but nothing rewrites the stored value. Whether a migration should rewrite those rows
  was not decided here.
- The floor's real-CPU effect (F8) needs a measurement with the bundled model before the floor
  can be called measured rather than reasoned.
