# Code-ingestion performance profile — where the time actually goes

**Date:** 2026-08-22 · **Branch:** `task/pd3-code-ingest-profile` · **Base:** `origin/main` `b56d7fb3`
(`VERSION` 1.32.2; #492 `embedding.threads`, #490 `EventPump<T>`, #494 watch-event deny-set all
merged) · **Lane:** architect · **Scope:** measurement only — no production edit in this branch.

Every number below carries the command that produced it and a tag:

- **[measured]** — I ran it on this machine, in this session, and the command is quoted.
- **[read]** — read out of the tree at `b56d7fb3`, with `file:line`.
- **[inferred]** — derived from a measured number plus a read fact; the derivation is shown.
- **[unverified]** — stated so it can be checked later; I did not settle it.

**The headline.** For a 469-file / 2.0 MB C# corpus, chunking and writing the rows costs **3.6
seconds**; embedding those same rows costs **1,061 seconds**. Ingest is 0.34 % of the wall
clock. **Ingestion is not the performance problem — the embed drain is, and inside the drain
99.6 % of the time is ONNX native inference.** Every fix ranked below is therefore a fix to
*how many* inferences run, *how big* each one is, or *how much of the machine* each one gets;
nothing else moves the number.

---

## 1. Machine and build facts

| Fact | Value | How |
|---|---|---|
| Model | `Mac16,12`, arm64 | `sysctl hw.model` **[measured]** |
| Physical cores | **10** | `sysctl hw.physicalcpu` **[measured]** |
| Logical cores | **10** | `sysctl hw.logicalcpu` **[measured]** |
| RAM | 25,769,803,776 B (24 GiB) | `sysctl hw.memsize` **[measured]** |
| OS | macOS 26.6.2, build 25G83 | `sw_vers` **[measured]** |
| SDK | .NET 10.0.400 | `dotnet --version` **[measured]** |
| Build | `dotnet build AiRaccoon.slnx -c Release` → exit 0 | **[measured]** |
| Binary under test | `src/AiRaccoon/bin/Release/net10.0/AiRaccoon`, `1.32.2+b56d7fb319a0f42c9d82a1cd822494006fa1fae9` | `AiRaccoon --version` **[measured]** |
| Server | `AiRaccoon --data-root <scratch> serve --port 7931 --idle-timeout 0` | **[measured]** |

The owner's live bank (`~/.ai-raccoon`) and port 7721 were never touched. All banks live under the
session scratchpad; every CLI verb was invoked with `--data-root <scratch>` and `--port 7931`.

**Code engine.** `faxenoff/code-daemon-embed-v1` downloaded once
(`AiRaccoon model download faxenoff/code-daemon-embed-v1 --dir <scratch>/codemodel --yes`,
4 files, `model.onnx` = 187,286,767 B) and activated per bank with
`AiRaccoon --data-root <bank> --port 7931 model set code local <scratch>/codemodel`. **[measured]**

---

## 2. Corpus facts

The corpus is a clean copy of this repository's own tracked C# sources — `git ls-files src | grep
'\.cs$'`, copied into `<scratch>/corpus-src` so no `bin/`–`obj/` output could contaminate the walk.

| Fact | Value | How |
|---|---|---|
| Files | **469** `.cs` | `find <corpus> -type f \| wc -l` **[measured]** |
| Lines | 40,930 | `find … -exec cat {} + \| wc -lc` **[measured]** |
| Bytes | **2,045,873** | same **[measured]** |
| Chunks produced | **1,762** rows in `code_entries` | `sqlite3 <bank>/memory.db "select count(*) from code_entries;"` **[measured]** |
| Rows in `entries` (memory corpus) | **0** | same query on `entries` **[measured]** |
| Total chunk text | 2,042,739 chars | `select sum(length(value)) from code_entries` **[measured]** |
| Mean chunk | **1,159 chars**; max 3,495 | `select avg(length(value)), max(length(value))` **[measured]** |
| Chunk size histogram (chars) | `<500`: 326 · `500–999`: 273 · `1000–1499`: 543 · `1500–1999`: 559 · `2000–2499`: 46 · `2500–2999`: 12 · `3000+`: 3 | `select (length(value)/500)*500 …` **[measured]** |

So the chunker emits ~3.75 chunks per file and covers the corpus almost exactly once
(2,042,739 of 2,045,873 bytes) — no overlap, which matches `CodeChunker`'s documented
"overlay is always 0" (`src/AiRaccoon.Infrastructure/Chunking/CodeChunker.cs:14-15`) **[read]**.

---

## 3. Measurement table

Every row is one command. `rows/s` scenarios use an identical protocol: set the thread cap, kill
and restart `serve` (sessions are cached per engine fingerprint, so a restart is mandatory),
re-activate the code engine — which invalidates all 1,762 rows to `pending` in one transaction
(`CodeReindexJob`'s doc comment, `:8-11`) **[read]** — then count `embed_state='pending'` at the
start and end of a fixed 150-second window.

| # | Scenario | Wall | CPU (`top -l 6 -s 5 -stats cpu,th`) | Rows | Pending before → after |
|---|---|---|---|---|---|
| S1 | `memory_ingest_directory` of the 469-file corpus, **no code engine configured** | **3.57 s** | mean 52 %, max 83.5 % (`ps -o %cpu,rss` @1 Hz, n=4) | 1,762 `code_entries` written, 0 `entries` | 0 → 1,762 pending |
| S2 | Full code embed drain to zero, thread cap = **default** (`max(1, 10/2)` = 5) | **1,061.3 s** (17.7 min) | mean 94.4 %, max 297.6 %, RSS 1.67 GB (n=944 samples @1 Hz) | 1,762 embedded | 1,762 → 0 |
| S3 | Drain rate, cap **1** | 150.0 s window | 18.8–22.8 %, 35–37 threads | 32 rows | 1,762 → 1,730 = **0.213 rows/s** |
| S4 | Drain rate, cap **5** (the merged default) | 150.0 s window | 124.3–140.3 %, ~41 threads | 352 rows | 1,762 → 1,410 = **2.347 rows/s** |
| S5 | Drain rate, cap **0** (ORT default = 10 physical cores) | 151.4 s window | 81.7–123.8 %, ~41 threads | 288 rows | 1,762 → 1,474 = **1.902 rows/s** |

All **[measured]**.

**Read the table this way.** S1 + S2 is one end-to-end code ingest of this repository's `src/`:
**1,065 seconds, of which 3.6 s (0.34 %) is ingest and 1,061 s (99.66 %) is the embed drain.**
Throughput at the merged default is **1.66 rows/s** overall (1,762 ÷ 1,061.3) — versus 2.347 rows/s
measured inside a clean 150-second window, so roughly **29 % of the drain's wall clock is not
spent embedding at all** (§5, Finding 4).

**A sixth run is deliberately excluded.** A re-ingest of the same corpus (every chunk a dedup hit)
was launched while the cap-1 drain was still running; it took ~10 s against S1's 3.57 s. The two
workloads shared the process, so the number is confounded and is **not** used anywhere below.
**[measured, discarded]**

---

## 4. Phase attribution

**Tool:** `dotnet-trace` 9.0.661903, already on the box (`dotnet tool list -g`) — no install needed.
Command: `dotnet-trace collect -p <serve pid> --duration 00:00:45 --format speedscope -o trace-drain.nettrace`,
taken mid-drain at the default cap. Speedscope JSON aggregated by a scratch script that walks the
evented profile and sums inclusive time per frame; one time unit = 1 ms (grand total 629,940 units
across 14 live threads over a 45 s window). **[measured]**

### 4a. The drain (phase d)

| Frame | Inclusive | Share of the 45 s wall |
|---|---|---|
| `OnnxEmbeddingGenerator.RunBatch` | 44,811 ms | **99.6 %** |
| └ `Microsoft.ML.OnnxRuntime.InferenceSession.RunImpl` | 44,787 ms | **99.5 %** |
| `Microsoft.Data.Sqlite.SqliteCommand.ExecuteReader` | 673 ms | 1.5 % |
| `SentencePieceUnigramModel.Encode` (embed-time tokenize) | 9.3 ms | **0.02 %** |
| `OnnxEmbeddingGenerator.Encode` | 9.3 ms | 0.02 % |

The drain thread is inside ORT's native `Run` for essentially the entire window. The 135,662 ms
attributed to `ConsoleLoggerProcessor.ProcessLogQueue` is three threads parked in `TryDequeue` —
idle, not work.

**ORT's own intra-op pool is native and invisible to the managed sampler**, so these percentages are
per-thread wall, not machine CPU; §3's `top` column is the machine-CPU view. **[measured]**

### 4b. Ingest (phases a/b/c)

No trace was taken of the 3.6-second ingest: at 0.34 % of the end-to-end wall it cannot repay the
measurement (`measure-when-it-pays`). What can be said from the code and S1 together:

- 469 files walked and chunked, 1,762 rows written, **3.57 s** → 7.6 ms/file, 2.03 ms/chunk,
  573 KB/s. Mean CPU 52 % of *one* core — the walk is single-threaded and never approaches
  saturation. **[measured + inferred]**
- The chunker's tokenizer is **not** `O200kTokenizer` (`Microsoft.ML.Tokenizers` tiktoken,
  `src/AiRaccoon.Infrastructure/Chunking/O200kTokenizer.cs:8`). `CodeChunker` takes an
  `ICodeTokenizer` (`CodeChunker.cs:34`), which for the code corpus is the bundled SentencePiece
  `CodeTokenizer` (`src/AiRaccoon.Infrastructure/Embedding/CodeTokenizer.cs:15-41`). The task brief's
  "suspect: tokenizing every chunk with a BPE tokenizer" applies to the *markdown/memory* path, not
  this one. **[read]** — and the trace puts SentencePiece encode at 0.02 % anyway.
- **Ingestion-phase timings do not exist.** `grep -rln "IngestTimings\|ingest.walk\|ingest.chunk" src/`
  returns nothing; `SearchTimings` (`src/AiRaccoon.Core/Memory/SearchResults.cs:17`) is the only
  phase-attribution record in the tree, and it covers the read path only. **[measured/read]** — §7.

---

## 5. Findings

**Finding 1 — the embed drain is 99.66 % of a code ingest, and 99.5 % of the drain is one native
call.** S1 (3.57 s) vs S2 (1,061.3 s); trace §4a. **[measured]** Every ranked fix below is aimed at
`InferenceSession.RunImpl`'s three inputs — call count, tensor size, thread pool — because nothing
else is on the clock.

**Finding 2 — #492's `max(1, cores/2)` cap is not merely polite, it is 23 % faster than ORT's own
default.** Cap 5: 2.347 rows/s at 124–140 % CPU. Cap 0 (= 10 threads): 1.902 rows/s at 82–124 %
CPU. Same protocol, same backlog, same window length. **[measured]** Over-subscription on this
10-core box costs throughput as well as headroom, so WP11-A already banked a real win. Cap 1 is
**11× slower** (0.213 rows/s) — a "be quiet" setting must never be defaulted to 1.

**Finding 3 — even at the fastest cap the code drain uses ~14 % of the machine.** 140 % of 1,000 %
available. **[measured]** This matters for how the owner's saturation report is read: one code
drain alone does not saturate this hardware. WP11 Finding (b) already names the real multiplier —
two cached ONNX sessions, `PendingEmbedJob` and `CodeReindexJob` running in both maintenance loops
at once, plus up to four *unbounded* watch-digest drains
(`WatchDigestExecutor.cs:88`/`:116`, `limit: null`) **[read]**. Saturation is a concurrency
problem, not a per-drain-cost problem, and WP11-B2 is the right fix for it.

**Finding 4 — roughly 29 % of the drain's wall clock is not inference.** Windowed throughput
2.347 rows/s (S4) vs whole-drain 1.66 rows/s (S2). **[measured]** The mechanism is read out of the
tree: `CodeReindexJob.RowsPerRun = 4 * CodeEmbedder.BatchSize` = 128 (`CodeReindexJob.cs:31`),
and the job is re-offered only by `BankMaintenanceHostedService.OnDemandPollInterval =
TimeSpan.FromSeconds(15)` (`:79`) **[read]**. 1,762 ÷ 128 = 13.8 runs × 15 s = **207 s of pure
idle inside a 1,061 s drain (19.5 %)** **[inferred]**; the remainder of the 29 % gap is startup
(the 187 MB session load) and per-run setup. A backlog that is *known* to be non-empty should not
wait out a 15-second timer 14 times.

**Finding 5 — batches are padded to their longest member, and rows are selected in id order.**
`OnnxEmbeddingGenerator.RunBatch:130` — `maxLen = Math.Min(_window, items.Max(i => i.Ids.Length))`,
then every row is zero-padded to `maxLen` (`:135-147`) **[read]**. Rows arrive from
`MemorySql.SelectAllPendingCodeForEmbed` (`MemorySql.cs:378-381`) with `ORDER BY id LIMIT @limit`
— never by length **[read]**. Measured chunk lengths span 3,495 chars down to under 500, with
**326 of 1,762 (18.5 %) under 500 chars** against a mean of 1,159 **[measured]**, so a typical
32-row batch pads a large minority of its rows to roughly three times their real length, and
attention cost is superlinear in sequence length. Expected saving is real but **[unverified]** —
see §6.3 for the one measurement that would settle it.

**Finding 6 — a directory ingest of the *memory* corpus still embeds one chunk per generator call.**
`FileIngestor.InsertChunksAsync` is reached from `IngestDirectoryAsync` (`FileIngestor.cs:169-171`)
with `embedInline` at its default `true`, and calls `embedder.EmbedIfConfiguredAsync(connection,
chunkId, chunk, …)` per chunk (`FileIngestor.cs:296-298`) **[read]**. This is WP11 Finding (b)'s
"one loose end", already in WP11-B2's scope. It did not appear in these measurements because a
`.cs`-only corpus produces zero `entries` rows (**[measured]**: `entries` = 0); a markdown-heavy
directory ingest would hit it at batch size 1, i.e. 32× fewer rows per inference than the code path.

**Finding 7 — `IngestDirectoryAsync` walks with no ignore file below the walk root.**
`Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)` (`FileIngestor.cs:148`) filtered
by `_ignoreRulesProvider.LoadAsync(path, …)` (`:147`), and `IgnoreRulesProvider` reads exactly one
file at the root it is handed — no nested discovery (`FileIngestor.cs:109-111` says so in prose)
**[read]**. This repository's `ai-raccoon.ignore` sits at the repo root, so
`memory_ingest_directory` pointed at `src/` would enumerate `src/**/bin` and `src/**/obj` — 379 MB
against 2.0 MB of source on this checkout **[measured]** (`du -sh src`). Not on the inference clock,
but it is real I/O and a real correctness surprise. *This corpus was deliberately built clean so the
walk cost is not smeared into S1.*

**Finding 8 — `CodeChunker` re-tokenizes and re-concatenates on every shed step, and concatenates
the winning text twice.** `VerifyAndShed` (`CodeChunker.cs:115-129`) builds
`string.Concat(units.Skip(cursor).Take(end - cursor + 1)…)` and calls `countTokens` on it once per
shed iteration; `BuildChunks:68` then builds the *same* string again for the chunk it just proved.
**[read]** At 0.02 % of the end-to-end clock this is a correctness-neutral tidy-up, not a
performance fix — recorded so nobody spends a day on it.

---

## 6. Ranked fix list

Ranked by expected wall-clock seconds removed from the measured 1,065-second end-to-end ingest.
Per `.ai-badger/invariants/prove-the-check-fails.md`, each named test must be seen failing first.
Per the owner's ruling on #464, **no acceptance criterion asserts a wall clock** — every gate below
asserts a count, an ordering, or a call argument.

### Rank 1 — stop waiting out the 15-second poll while the backlog is known non-empty

- **What to change.** `src/AiRaccoon.Infrastructure/Maintenance/BankMaintenanceHostedService.cs`
  `RunOnDemandPollLoopAsync` (`:158`, `OnDemandPollInterval` at `:79`) — after a pass in which a
  job ran and still reports work, re-offer immediately instead of awaiting the next tick. Equivalently
  and more in the grain of what is already merged: make the embed topic's consumer
  (`EventPump<T>`, #490 / WP11-B2) re-signal itself when `EmbedPendingBatchAsync` returns a full
  `RowsPerRun`, so `WaitForItemAsync` returns at once.
- **Expected gain.** **~207 s of the 1,061 s drain (19.5 %)**, derived in Finding 4 from
  13.8 runs × 15 s. **[inferred from measured]**
- **Risk.** Low-to-moderate. Removing the pause removes the *pacing* the 15 s accidentally provided;
  it must not be removed before WP11-B2's single-consumer drain lands, or all three uncoordinated
  drains speed up together and the owner's saturation gets worse, not better. **Sequence after B2.**
- **Test that proves it.** `MaintenanceJobRunner`/pump test on a fake `TimeProvider`: a job that
  reports `HasWork` true and consumes exactly `RowsPerRun` is invoked **N times without the timer
  being advanced**. Red first: today it is invoked once per advanced tick. Counts only, no clock.
- **Already covered?** Adjacent to **WP11-C** (`maintenance.embed-rows-per-run.global`) and enabled
  by **WP11-B2**. Not itself in either. **New.**
- **Simpler alternative considered.** Raise `RowsPerRun` from 128 to, say, 1,024 — one config edit,
  no new mechanism, and it collapses 13.8 idle gaps to 1.7. It is strictly worse on two counts the
  code already documents: a longer run widens `CodeEmbedder`'s S1 stale-engine race window and S2's
  poison-row blast radius (`CodeEmbedder.cs:14-18`, `:95-100`) **[read]**, and it makes the drain
  less interruptible. But it is *one line* and WP11-C already ships the key — **so ship the key
  first, measure at 512, and only build the self-signal if the number still justifies it.**

### Rank 2 — sort each drain run's rows by token length before batching

- **What to change.** `src/AiRaccoon.Infrastructure/Embedding/CodeEmbedder.cs`
  `EmbedPendingBatchAsync:85-90` — order `rows` by `Value.Length` (a cheap proxy for token count)
  before the `for (offset …) Skip/Take(BatchSize)` slicing, so each 32-row generator call is
  length-homogeneous. Nothing in `OnnxEmbeddingGenerator` changes; `MemorySql.SelectAllPendingCodeForEmbed`
  keeps `ORDER BY id` so the *claim* order and the retry semantics are untouched.
- **Expected gain.** Bounded by the padding waste in Finding 5: 18.5 % of rows are under 500 chars
  against a 3,495-char maximum, and `maxLen` is the batch maximum. A **conservative 10–20 % of the
  1,061 s drain (105–210 s)**, on the reasoning that attention is superlinear in sequence length.
  **[inferred, and the derivation is explicitly weaker than the others]**
- **Risk.** Low. Pure reordering inside one run; no row escapes the run, no `embed_state` semantics
  move. Ordering by *chars* is a proxy — a chunk of dense CJK or hashes tokenizes differently.
- **Test that proves it.** A `CodeEmbedder` test with a fake generator that records the batch it was
  handed: given 64 pending rows of alternating short/long text, assert **every generator call
  receives rows whose lengths are within one sort position of each other** — i.e. batch 1 is the 32
  shortest. Red first: today batch 1 is rows 1–32 in id order. Ordering assertion, no clock.
- **Already covered?** **No.** Not in WP11-A/B1/B2/C, not in #494.
- **Simpler alternative considered.** Do nothing and shrink `BatchSize` instead — a smaller batch
  narrows the length spread by luck. Rejected: it also multiplies the per-call overhead and the doc
  comment at `CodeEmbedder.cs:14-18` pins 32 to a stated risk argument. Sorting is the smaller change.
- **Measure before building.** This is the one fix whose gain I did **not** establish. §6.3 below.

### Rank 3 — keep #492's cap, and pin it with a test that would notice a regression to ORT's default

- **What to change.** Nothing in behaviour — `EmbeddingSettingsKeys.Threads` (`:32`) and
  `OnnxEmbeddingGenerator:58-66` are already right. Add the missing *proof*.
- **Expected gain.** Protects the measured **23 %** (2.347 vs 1.902 rows/s, S4 vs S5) that #492
  already banked. **[measured]**
- **Risk.** None — a test-only change.
- **Test that proves it.** Assert `OnnxEmbeddingGenerator.IntraOpThreads` equals the resolved
  setting, and that an *unset* `embedding.threads` resolves to `max(1, Environment.ProcessorCount/2)`
  rather than 0. Red first: delete the resolution and watch it report 0. A property assertion,
  no clock, no golden vectors involved.
- **Already covered?** **WP11-A / #492** shipped the behaviour. Whether it shipped this assertion
  is **[unverified]** — I did not read `#492`'s tests. Check before writing a duplicate.

### Rank 4 — give `IngestDirectoryAsync` the same ignore-root resolution single-file ingest already has

- **What to change.** `src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs:147` — reuse
  `ResolveIgnoreRootAsync` (`:113-139`), or walk parents for the nearest `ai-raccoon.ignore`, instead
  of loading rules only at the walk root.
- **Expected gain.** Not on the inference clock. On this checkout it is the difference between
  enumerating 2.0 MB and 379 MB **[measured]**, and it stops build output entering the code corpus —
  which *is* on the inference clock, via rows that should never have existed.
- **Risk.** Moderate and it is a behaviour change, not a speed-up: an ignore file that currently has
  no effect would start having one, and a bank could lose rows on the next re-ingest. **Wants an
  owner gate before it is built.**
- **Test that proves it.** `FileIngestorIgnoreTests` (the file exists): ingest `root/sub` where
  `root/ai-raccoon.ignore` excludes `sub/skip/**`; assert the row count for `skip/` is 0.
  Red first: today it is non-zero.
- **Already covered?** ADR-0086 covers watch-overlap ignore semantics; **#494** made watch *events*
  skip hidden and deny-set dirs. Whether #494 also changed the directory-ingest walk is
  **[unverified]** — read #494 before opening this.
- **Simpler alternative.** Document the behaviour instead of changing it: "point
  `memory_ingest_directory` at a directory that has its own ignore file." One doc line, zero risk.
  Given the corpus-pollution consequence, I think the code fix is right — but the cheap option is
  real and the owner should choose.

### Rank 5 — `CodeChunker`: reuse the joined string `VerifyAndShed` already built

- **What to change.** `CodeChunker.cs:115-129` return the verified text alongside `end`, so
  `BuildChunks:68` does not re-concatenate it.
- **Expected gain.** **Below the noise floor** — the whole chunk+tokenize phase is inside a 3.57 s
  ingest and SentencePiece encode measured at 0.02 % of the end-to-end clock. **[measured]**
- **Risk.** Low, but it touches the packing algorithm, which `docs/adr/0036` and the shed loop's own
  comment guard carefully.
- **Test that proves it.** Existing `CodeChunker` tests must stay green unchanged; that *is* the
  proof, since the change must be behaviour-identical.
- **Already covered?** No. **Recommendation: do not build it as a performance fix.** File it as
  tidy-up if it ever falls out of other work in that file.

### 6.3 The measurement that must precede Rank 2

Rank 2 is the only proposal whose gain is a guess. One run settles it, and it is cheap:

```
# same protocol as S3–S5, but with the rows pre-sorted by length in the bank
sqlite3 <bank>/memory.db "…"   # no: do it in the code, behind a temporary flag
```

Concretely: on a throwaway branch, add the one-line `OrderBy(r => r.Value.Length)` at
`CodeEmbedder.cs:87`, restart, re-activate, and take a 150-second window exactly as S4 did. If
rows/s does not beat 2.347 by more than run-to-run noise (S4 vs the earlier mid-drain 2.398
suggests noise is ~2 %), the fix is dead and costs nothing further. **~6 minutes of wall time.**

---

## 7. The gap: ingestion has no phase timings

`SearchTimings` (`src/AiRaccoon.Core/Memory/SearchResults.cs:17-37`) records nine read-path phases,
exports eight through `Phases()`, and feeds `memory_performance`. **There is no write-path
equivalent** — `grep -rln "IngestTimings\|ingest.walk\|ingest.chunk" src/` is empty **[measured]**.
Everything in §3 and §4 had to be reconstructed from outside the process, with `ps`, `sqlite3` and
`dotnet-trace`. That is why S2 took 17 minutes to learn one number, and why the confounded re-ingest
run had to be thrown away rather than explained.

**The smallest thing that closes it**, shaped like ADR-0076/0087 and reusing `SearchTimings`' exact
shape rather than inventing a second one:

- A `record IngestTimings(TimeSpan Walk, TimeSpan Match, TimeSpan Chunk, TimeSpan Write, TimeSpan Embed)`
  in `AiRaccoon.Core/Ingestion/`, with `PhaseNames`/`Phases()` **derived** from the record, not a
  second hand-written list (`derive-or-delete-the-list`).
- Populated by one collector on `IFileIngestor`/`ICodeIngestor`, the same way
  `SearchTimingsCollector` does it — and, per WP1's lesson, with a closure test that asserts
  Σ(phases) ≈ total so a phase can never be silently dropped from the export.
- Emitted where the drain already logs: one `[LoggerMessage]` per drain run carrying rows, batches,
  and the split. `MaintenanceJobRunner` already logs "ran in N ms" per job (EventId 525, seen in
  every serve log this session) — the split belongs beside it, not in a new channel.

**Scope discipline.** Do *not* also add the WP11 `ReplaceCoreAsync` BEGIN…COMMIT span logging here;
WP11's own first commit already claims it, and two lanes instrumenting the same file collide.

**Is this over-engineered?** (`ask-if-simpler`) A cheaper version exists: log only `rows`,
`batches` and `elapsed` per drain run — three integers, one log line, no new record type — and skip
the ingest-side split entirely, since §4 shows ingest is 0.34 % of the clock. **That cheaper version
is the right first step.** The full five-phase record only earns its place if the drain-run line
ever shows a run whose time is *not* explained by rows × mean-tokens.

---

## 8. What is unverified

1. **Rank 2's gain (10–20 %).** Inferred from the chunk-length histogram plus the padding code, not
   measured. §6.3 says how to settle it in six minutes.
2. **Whether #492 already ships the thread-resolution assertion** in Rank 3 — I read the production
   code, not `#492`'s tests.
3. **Whether #494 changed the directory-ingest walk** as well as watch events (Rank 4).
4. **Why cap 1 → cap 5 is superlinear** (0.213 → 2.347 rows/s, an 11× gain for 5× the threads, at
   only ~6.4× the CPU). Measured twice with the same protocol, unexplained; most likely ORT selects
   different kernels below a thread threshold. It does not change any recommendation.
5. **`top`'s CPU percentages on macOS** are a decayed average, so §3's CPU column is directionally
   right and not precise. The *ratios* between scenarios were taken under identical conditions.
6. **Scale.** Only one corpus was measured (469 files / 2.0 MB / 1,762 chunks). Whether cost tracks
   files, bytes or chunks was **not** established — the second corpus fell outside the time box.
   The trace makes chunks the obvious candidate (cost = inference calls = chunks ÷ 32), but that is
   a prediction, not a measurement.
7. **The owner's live-bank numbers remain inflated** by the peer's #494 finding (watch digests
   re-indexing agent worktrees). Nothing here was measured on the live bank.

---

## 9. Reproducing this

```sh
# 1. build
dotnet build AiRaccoon.slnx -c Release

# 2. scratch server — never 7721, never ~/.ai-raccoon
BIN=src/AiRaccoon/bin/Release/net10.0/AiRaccoon
$BIN --data-root <scratch>/bank1 serve --port 7931 --idle-timeout 0 > serve.log 2>&1 &

# 3. corpus + scope
git ls-files src | grep '\.cs$' | while read f; do mkdir -p <scratch>/corpus-src/$(dirname $f); cp $f <scratch>/corpus-src/$f; done
$BIN --data-root <scratch>/bank1 --port 7931 settings ingest scope add '*' <scratch>

# 4. S1 — ingest (MCP over HTTP; there is no CLI ingest verb)
curl -s -X POST http://127.0.0.1:7931/mcp \
  -H 'Content-Type: application/json' -H 'Accept: application/json, text/event-stream' \
  -H "X-AiRaccoon-Token: $(cat <scratch>/bank1/mcp-token)" \
  -d '{"jsonrpc":"2.0","id":1,"method":"tools/call","params":{"name":"memory_ingest_directory","arguments":{"projectId":"…","path":"<scratch>/corpus-src"}}}'

# 5. code engine, then S2 — drain to zero
$BIN --data-root <scratch>/bank1 --port 7931 model download faxenoff/code-daemon-embed-v1 --dir <scratch>/codemodel --yes
$BIN --data-root <scratch>/bank1 --port 7931 model set code local <scratch>/codemodel
watch -n2 'sqlite3 <scratch>/bank1/memory.db "select count(*) from code_entries where embed_state=\"pending\";"'

# 6. S3–S5 — thread cap; the server MUST be restarted (sessions cache per engine fingerprint)
$BIN --data-root <scratch>/bank1 --port 7931 settings model threads {0|1|5}
```

The scratch scripts used here (`start.sh`, `mcp.sh`, `run.sh`, `drain.sh`, `rate.sh`, `threads.sh`,
`trace.sh`, `spd.py`) live in this session's scratchpad and are not committed — everything they do
is in the block above.
