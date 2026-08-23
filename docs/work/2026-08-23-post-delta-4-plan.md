# Plan — post-delta session 4 (rev 1.0 — gate pending)

**Date:** 2026-08-23 · **Base:** main `72e15088` (`VERSION` = 1.33.0; one open PR, draft #499) ·
**Status:** rev 1.0 — **gate pending**. Nothing gated starts before the owner rules ·
**Task:** `post-delta-4` · **Lane:** architect (plan + gate), Opus.

**What this session inherits.** Six carried items from `docs/work/2026-08-22-post-delta-3-plan.md`
rev 1.4: WP12-B/C/D/E (gate G20–G22 never answered — the form
`docs/work/2026-08-22-post-delta-3-wp12-review.html` is open and its feedback file never arrived),
WP10 (ADR-0089, approved at G15, not started), WP11-C follow-up Option B, and WP6 (#455, parked
LAST by owner ruling 2026-08-22 ~19:38). Plus six filed-not-started issues in `.ai-badger/state.json`
and two owner calls surfaced by merged PR #524.

**Sources.** Every fact in §Review was re-derived today against the tree at `72e15088` and against
GitHub — not quoted from the predecessor. Where the predecessor's text and the tree disagree,
§Review says so and the gate card carries the corrected number.

## Session todo

1. Open the draft PR carrying this plan; generate the owner gate form from §*Owner gate* (G1–G10).
2. Fold the gate answers into rev 2.0; architect re-reviews before any gated WP opens.
3. **WP4** — #524 follow-ups: one thread-count rendering, EventId 428 `Threads` as an int (G4, G5).
4. **WP1** — WP12-B: the embed drain re-signals itself on a full row budget (G1).
5. **WP2** — the directory walk: ancestor ignore root (WP12-D) **and** the `code_entries` prune
   leg (#485), one PR on `IngestDirectoryAsync` (G2, G9).
6. **WP3** — job-run counters: rows/batches on the run record (G6, shaped by #477).
7. **WP5** — WP11-C Option B: drop the enqueue-only `PendingEmbedJob`/`CodeReindexJob` (G7 —
   the review recommends declining; the WP exists only if the owner overrules).
8. **WP7** — WP12-E research: quantized / CoreML inference for the code engine (G3; architect).
9. **WP8** — model-manifest repair pair: #497 and #504, one `ModelDownloadPlanner` strand (G9).
10. **WP9** — small filed issues: #519 (`TestData` optional Null defaults), #493 (PerformanceTools
    description drift) and the EventId 517-519 doc drift found today (G9).
11. **WP6** — ADR-0089 implementation, five PRs (G8).
12. **WP10 — #455 re-derived corpus, queries and parity golden. LAST**, per the standing owner
    ruling; resume `task/pd3-455-public-benchmark-corpus` @ `ea174faf`, draft #499 (G10).

---

## Review — every carried item re-derived from the tree and GitHub

### (a) G20 / WP12-B — continuous drain inside `EmbedDrainService`

**What is merged.** `src/AiRaccoon.Infrastructure/Embedding/EmbedDrainService.cs` (#507, then #517)
is a `BackgroundService` with a single consumer loop at `:54-98`: `pump.WaitForItemAsync` (`:60`) →
`pump.DrainUpTo(1)` (`:70`) → `DrainOnceAsync` (`:83`) → `Drains.Increment()` (`:95`). `DrainOnceAsync`
(`:101-132`) reads `BankMaintenanceConfigKeys.EmbedRowsPerRunGlobal` from `ISettingsStore` **on every
pass** (`:107-109`), resolves it through `ResolveRowsPerRun` (`:138-154`, default 128, ceiling
`MaxEmbedRowsPerRun` = 4096, warn-once-per-distinct-bad-value via `_lastWarnedRowsPerRun` `:135`),
and calls `codeEmbedder`/`entryEmbedder`.`EmbedPendingBatchAsync(connection, rowsPerRun, ct)`
(`:111-113`), keeping the returned count in the local `drained` (`:111`).

**Exactly what a "re-signal on a full budget" touches — three edits, one file.**
1. `DrainOnceAsync` `:115-121`: after `drained` is known, `if (drained >= rowsPerRun)
   pump.TryEnqueue(request);`. The pump is already injected (`:33`) and `TryEnqueue` never blocks
   and never throws (`IEventPump<T>` `:22-23`). The topic coalesces on `EmbedDrainRequest`'s record
   equality (`EmbedDrainRequest.cs:20`), so a re-signal for a corpus already queued is a no-op, not
   a duplicate — and `PumpCeiling`/`PumpCapacity` are both 8 (`:43`, `:46`), item space 2.
2. The class doc comment `:22-24` currently promises the **opposite** in prose — *"It never
   re-enqueues itself when rows remain — a large backlog drains over several signals … not in one
   inference-pool-saturating burst."* That sentence is the contract this change reverses and must
   be rewritten in the same commit, not left to rot.
3. `docs/reference/logging-event-ids.md` needs no new id: EventId 1003 `DrainFinished` (`:161-163`)
   already carries `{Rows}` per pass, so the new behaviour is observable on day one.

**How the test proves it — and the predecessor's stated RED test is wrong.** The part-3 plan
specified *"Fake `TimeProvider`: a job reporting `HasWork` and consuming a full row budget is
invoked N times without the clock being advanced."* **`EmbedDrainService` takes no `TimeProvider`**
— its constructor (`:32-40`) is pump, connection factory, two embedders, settings, telemetry,
logger. The 15 s clock lives in a different component (`BankMaintenanceHostedService.cs:79`,
`OnDemandPollInterval`, used at `:158`), which this WP does not edit. The RED test that actually
demands the behaviour needs **no clock at all**: a fake `ICodeEmbedder` returning exactly
`rowsPerRun` for the first *N-1* calls and fewer on the *N*-th, one single `TryEnqueue`, then await
`Drains` reaching N. **Red today:** `Drains` stops at 1, because nothing re-signals. This is a
count-and-ordering assertion with no wall clock, which is what owner ruling #464 requires.

**Where the 15 s still lives.** `BankMaintenanceHostedService.cs:79` stays at 15 s and unedited. Its
job path changed under #517 — the two jobs now only `TryEnqueue` (see (f)) — so the poll no longer
*paces* a drain, it *starts* one. WP12-B's gain is therefore the full 19.5 % the profile measured.

### (b) G21 / WP12-D — the directory walk's ignore root. **The headline claim is false.**

**What is merged.** After #518 `FileIngestor` has required deps only — the primary constructor
(`:20-30`) takes nine, including `IEventPump<EmbedDrainRequest> embedDrainPump` (`:29`); the
`embedInline` flag is gone. `IngestFileAsync` (`:37`) resolves an ignore root first:
`ResolveIgnoreRootAsync(connection, projectId, path, ct)` (`:46`) then `LoadAsync(ignoreRoot, ct)`
(`:47`). `IngestDirectoryAsync` (`:125`) does **not**: it calls `LoadAsync(path, ct)` at the walk
root (`:131`) and filters at `:133` with
`!IsHidden(path, file) && !IsIgnored(ignoreRules, path, file) && IsInScope(scope, file)`.

**`ResolveIgnoreRootAsync` is ancestor resolution, not nested discovery.** Its own doc comment
(`:91-96`) says so: *"the containing registered watch if one exists (longest match), else the
ingest-scope allowlist entry that admits the path, else the file's own parent directory.
`IgnoreRulesProvider` reads **one** file at whatever root it is given — no nested discovery."* The
body (`:97-121`) is exactly those three steps. So "nested ignore roots" is the wrong name for this
change: it makes directory ingest honour an ignore file at an **ancestor** of the walk root. It does
not, and would not, discover `ai-raccoon.ignore` files *below* the walk root at any level.

**The 379 MB bin/obj claim does not hold, and never did.** `IsHidden(root, path)` at `:400` is
`WatchDenySet.Excludes(root, path)`, and its doc at `:397-398` names the set:
*"node_modules/bin/obj/.git/.venv/__pycache__/dist/build/target"*. That filter runs unconditionally
at `:133`, with or without an ignore file. **`bin/` and `obj/` are already excluded from the
directory walk today**, and the repo's own `ai-raccoon.ignore` header says exactly that. (Re-measured
for the record: `src/` build output is now **1.8 GB** against **2.0 MB** of `.cs` across 473 files.
The number grew; its relevance did not survive.)

**The real behaviour gap, precisely.** The root `ai-raccoon.ignore` lists paths the deny set does
**not** cover: `src/AiRaccoon/Models/` (23 MB — `model_qint8_arm64.onnx`, `code-sentencepiece.bpe.model`,
`vocab.txt`), `tests/AiRaccoon.Tests/Resources/` (17 MB),
`tests/AiRaccoon.Tests/Unit/Retrieval/assets/` (25 MB), `docs/work/checklist/` (600 KB, 22 files),
`*.g.cs`, `known-flakes.json`. Point `memory_ingest_directory` at `src/` today and the root ignore
file is never loaded, so `src/AiRaccoon/Models/vocab.txt` — **231,508 B**, and `.txt` is a
memory-owned extension (`src/AiRaccoon.Core/Ingestion/CodeExtensions.cs:6`) — is ingested as a
memory document. That is the harm: **231 KB of tokenizer vocabulary as memory rows**, not 379 MB of
build output. Real, verified, and two orders of magnitude smaller than the card claimed.

**What the change costs.** `IngestDirectoryAsync:131` becomes a `ResolveIgnoreRootAsync` call and
`:133`'s two `path` arguments to `IsIgnored` become `ignoreRoot` (the relative-path base at
`:405-427` must match the root the rules were loaded from, or every pattern misses). It is still a
behaviour change: an ignore file that has no effect today starts having one, and a bank can lose
rows on its next re-ingest. The documentation-only alternative is real and now looks *stronger*,
because the motivating number shrank by 1,600×.

### (c) G22 / WP12-E — quantized / CoreML research. **Both arms are cheaper than the card assumed.**

**What the csproj references.** One package: `Microsoft.ML.OnnxRuntime`
(`src/AiRaccoon.Infrastructure/AiRaccoon.Infrastructure.csproj:19`) pinned at **1.29.0**
(`Directory.Packages.props:34`), which transitively brings `Microsoft.ML.OnnxRuntime.Managed`
1.29.0 (nuspec `<dependencies>`). No `.Gpu`, no `.DirectML`, no separate EP package.

**Is CoreML available? Yes — measured, not assumed.**
`~/.nuget/packages/microsoft.ml.onnxruntime/1.29.0/runtimes/osx-arm64/native/libonnxruntime.dylib`
(43,184,400 B) exports `_OrtSessionOptionsAppendExecutionProvider_CoreML` (`nm -gU`), and the managed
assembly carries the matching P/Invoke name. **The CoreML EP is compiled into the package we already
reference, on the platform we already run on, at the version we already pin** — the arm costs a
`SessionOptions` call, not a dependency change.

**Is int8 novel here? No.** The bundled *memory* model is already quantized —
`src/AiRaccoon/Models/model_qint8_arm64.onnx` (23,026,053 B), *"48 `MatMulInteger`"* ops per ADR-0049
`:55`. The code engine (`code-daemon-embed-v1`, 768-dim, `architecture.md:203`) is the fp32 outlier,
so the quantization arm applies a shipped pattern to a second model. ADR-0049 still binds both arms:
they change the arithmetic path, so stored vectors, the parity golden and `MiniLmGoldenVectorTests`
are downstream.

**What the measurement plan must reuse** — `docs/work/2026-08-22-code-ingestion-profile.md` §3 and §9.
The S3–S5 protocol, verbatim from §3: *set the thread cap, kill and restart `serve` (sessions are
cached per engine fingerprint, so a restart is mandatory), re-activate the code engine — which
invalidates all 1,762 rows to `pending` in one transaction — then count `embed_state='pending'` at
the start and end of a fixed 150-second window.* §9 carries the commands: scratch bank on **port
7931**, `--idle-timeout 0`, `--data-root <scratch>/bank1`; the 469-file `git ls-files src | grep
'\.cs$'` corpus; ingest over MCP/HTTP (there is no CLI ingest verb); `model download` + `model set
code local`; `top -l 6 -s 5 -stats cpu,th`. **Baselines: S4 = 2.347 rows/s (cap 5), S5 = 1.902 (cap
0), S2 = 1,061.3 s end to end.** Anything off that protocol is not comparable.

### (d) WP12-C — job-line counters. The signature cannot carry a count, and #477 asks for something else.

**Today.** `MaintenanceJobRunner.Log.JobRan` is EventId **525** — *"maintenance job '{DisplayName}'
({JobName}) ran in {ElapsedMs:F0} ms"* (`MaintenanceJobRunner.cs:103-105`); the runner's record is
`MaintenanceJobOutcome(string Name, bool Ran, string? Error, bool CreatedWork = false)` (`:9`), fed
from `job.RunAsync(...)` at `:75`.

**Can `ValueTask<bool>` carry counts? No.** `IMaintenanceJob.RunAsync` returns `ValueTask<bool>`
(`IMaintenanceJob.cs:25`) and that bool has a documented scheduling meaning — `:20-23`: *"returning
true when it created rows that still need embedding. Only then does the pass sweep again."* One bit
with a contract attached, no free channel. Counts need either
`ValueTask<MaintenanceJobResult>` across the interface and its **seven** implementations, or an
out-of-band recorder.

**What #477 actually asks for** — quoted from the issue: *"`MaintenanceJobRunner` records
`job.<jobName>.duration_ms` (histogram) and `job.<jobName>.rows` (gauge, where a job reports a
count — code-reindex: rows embedded / still pending) through `IMeasurementRecorder` under the
self-metrics project id, and `SelfMetricNames` lists them so `memory_performance` shows one series
per job."* That is **not** three integers on a log line. It is a metrics series, reachable from
`memory_performance` — `IMeasurementRecorder` lives at `src/AiRaccoon.Core/Metrics/IMeasurementRecorder.cs`
and the self-metric name list at `src/AiRaccoon.Core/Memory/MetricsConfigKeys.cs`. The predecessor's
WP12-C scope and the issue it cites are two different pieces of work.

**And the simplest thing may already exist.** EventId **1003** `DrainFinished` already logs
`"Embed drain pass finished for {Corpus}: {Rows} row(s)"` per pass
(`EmbedDrainService.cs:161-163`) — for the drain, which is 99.66 % of the clock, the row count is
*already* observable. What EventId 525 is missing is counts for the *other* six jobs, none of which
is on the hot path. This is the ask-if-simpler pressure on the whole item, and it is gate **G6**.

### (e) WP10 — ADR-0089 implementation

**Status re-read.** `docs/adr/0089-the-project-id-is-a-guidv7-and-that-is-not-access-control.md:5`
is *"Status: **Accepted** — ratified by the owner on 2026-08-22 (post-delta-3 gate G2, 15/15
APPROVE…)"*. G2 is spent; nothing blocks this on ratification. Verified it is still unimplemented:
`git grep -c "project_id_token_get" -- src tests` → **no matches**; no `projects` table in
`MemorySchema.cs`.

**What the ADR binds** (§Decision, re-read): ids canonicalized to lowercase `D` form at the tool
boundary (2); **a project exists when registered — a write to an id with no registry row is refused,
guid or not** (3), except a raw-text id the bank *already holds rows for*, which keeps working with a
warning (`SelectProjectIds`, `MemorySql.cs:58-64`); `project_id_token_get` **mints and registers**
(4); the `projects` table is `id TEXT PRIMARY KEY / name TEXT / created_at INTEGER NOT NULL` in the
**unconditional `Ddl` block with no `CurrentVersion` bump** — it stays **10** (`MemorySchema.cs:54`),
because a bump hard-fails every concurrent session and peer (ADR-0086).

**Sizing — five PRs, files named.**

| PR | Files | Notes |
|---|---|---|
| **6a** table + registration write path | `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs` (`Ddl` block near `:347`, the `metrics` precedent), `MemorySql.cs`, a new `Projects/` store | No `CurrentVersion` bump. Smallest, and everything else waits on it |
| **6b** `project_id_token_get` | `src/AiRaccoon/Tools/MemoryTools.cs` (502 lines; `Guid.CreateVersion7` already used at `:186`) | Thin per `mcp-thin`: mint + register + return |
| **6c** refusal of an unregistered id | `src/AiRaccoon/Tools/ToolGate.cs` (43 lines, `RequireAsync` `:17-35`) and/or `IMemoryAccessGuard` | **The risk-bearing part.** Every existing caller passes a raw id today. RED set first: legacy-known id works with a warning; legacy-unknown is refused; an unregistered guidv7 is refused |
| **6d** CLI `project id generate` / `project id convert` (one-way) | `src/AiRaccoon/Setup/Cli/CliCommandTree.cs` (655 lines), a new `Commands/ProjectCommands.cs` | System.CommandLine; option names keep their `--` prefix at `GetValue`; test argv, not just the handler |
| **6e** storage guidance + `ai-raccoon.ignore` entry | `docs/reference/agent-memory-server.md`, `ai-raccoon.ignore` | **Not** `.gitignore` — the owner reversed that in the #448 review. Parallel to all of the above |

`6a → 6b/6c → 6d` serialise; `6e` is parallel. **This is the largest item in the session** and the
only one that can collide with WP10/#455 for calendar, which is gate **G8**.

### (f) WP11-C Option B — dropping the enqueue-only jobs. **It is not a small PR, and it deletes two live guarantees.**

**The premise is true.** Both `RunAsync` bodies are one `TryEnqueue`:
`PendingEmbedJob.cs:47-51` → `embedDrainPump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Memory)); return ValueTask.FromResult(false);`
and `CodeReindexJob.cs:44-48`, identical for `EmbedCorpus.Code`.

**But `RunAsync` is not what these classes are for — `HasWorkAsync` is, and it is load-bearing three ways.**

1. **`CodeReindexJob.HasWorkAsync` is not side-effect-free.** `CodeReindexJob.cs:37-41` calls
   `embedder.ReconcileFingerprintAsync(connection, ct)` *before* checking pending work. That reconcile
   is what invalidates code rows to `pending` when a manifest changes in place on disk — pinned by
   `CodeReindexJobTests.cs:161-187`
   (`HasWorkAsync_ManifestChangedInPlaceSinceActivation_InvalidatesAndUpdatesTheStoredFingerprint`).
   Deleting the job deletes reconcile-on-every-poll, which is not a drain signal at all.
2. **`HasWorkAsync` is the only durable recovery path for a dropped or coalesced pump signal.** The
   poll loop (`BankMaintenanceHostedService.cs:151-183`) passes the whole `_jobs` list to
   `RunDueAsync` every 15 s with no per-job filtering; `HasWorkAsync` reads `embed_state='pending'`
   directly, so a signal that coalesced away is picked up on the next tick. That is
   `EmbedDrainServiceTests.CoalescedSignal_IsRecoveredByTheNextPoll` (`:195`), and it is the concrete
   form of ADR-0076's "the channel is a wake-up, not the record" — restated in
   `EmbedDrainService.cs:27-29`. Remove the jobs without replacing this and a dropped signal has no
   fallback.
3. **The same-pass ordering guarantee.** `AppRegistrations.cs:158-163` documents why `PendingEmbedJob`
   is registered **last** — *"chunk-backfill produced 13,578 of them on a real bank"* — and
   `BankMaintenanceHostedService.cs:243-248` repeats it from the consumer side. Proved live by
   `EmbedSweepAfterJobsTests.APassWhoseJobCreatesPendingRows_SignalsTheDrainInTheSamePass`.

**The blast radius, counted: 49 distinct files** (excluding 10 dated `docs/work/*` records).
**16 src** — the two classes, `AppRegistrations.cs:158-193,200`,
`BankMaintenanceHostedService.cs:243-248`, `ReingestRepairJob.cs:38`, `ReingestRepair.cs:27`,
`EmbedDrainService.cs:15`, `EntryEmbedder.cs:20,235`, `IEntryEmbedder.cs:43`, `ICodeEmbedder.cs:43,55`,
`CodeEmbedder.cs:16`, `MemorySql.cs:321,367`, **plus four a class-name grep misses** because they name
the job by its `JobName` string in *user-facing* text: `SqliteCodeEngineStore.cs:73`,
`SettingsEndpoint.cs:135` (EventId 675's message), `CliCommandTree.cs:178` (help) and
`SettingsCommands.cs:264` (CLI stdout). **13 tests** — `PendingEmbedJobTests.cs` (140 lines) and
`CodeReindexJobTests.cs` (301) deleted outright, plus `CliBankWriteTests.cs:56-65`,
`EmbedSweepAfterJobsTests.cs`, `PendingEmbedMaintenanceDrainTests.cs`,
`MaintenanceJobRunnerTests.cs:56`, `EmbedDrainServiceTests.cs:20,195`,
`CodeCorpusFeatureContext.cs:39,69,114`, `CodeCorpusSteps.cs:768`, `MaintenanceJobShapeTests.cs:8`,
`CodeEngineActivationTests.cs:127` (`CliBankWriteLedgerDriftTests` self-adjusts — it is the gate that
goes red if the ledger list is edited wrong). **20 docs + one live template** — including **ADR-0091**,
whose entire subject *is* these jobs' enqueue-only shape (`:11,45-46,129-133,150-162`) and which needs
an amendment not a line edit; `docs/features/code-corpus/code-corpus.feature:196-217` (a whole `Rule:`
block); `docs/how-to/configure-embedding-engines.md:186,202`;
`docs/reference/agent-memory-server.md:190`; and
`.ai-badger/skills/ai-raccoon-manual-checklist/templates/checklist-template.json:183`.

**Eight other `IMaintenanceJob` implementations remain**, so the interface survives either way. That
was never the question.

**Fix-what-you-find, surfaced by this pass.** `docs/reference/logging-event-ids.md:53` claims EventIds
**517-519** exist on `BankMaintenanceHostedService`. They do not — its `Log` class declares
510-516 and 520-524 only. Pre-existing drift, unrelated to Option B; folded into WP9.

### (g) WP6 / #455 — the corpus branch, and how far main moved

`git fetch origin` then `git log --oneline origin/main..origin/task/pd3-455-public-benchmark-corpus`
→ **5 commits**, tip `ea174faf` (*"wip(455): doc edits … parked; WP6 resumes last per owner
ruling"*), under it `946d9090` (regenerated golden), `cb9c2e3b` (a merge of origin/main at PR #486),
`3b08d573` (re-derived `benchmark_corpus.py`), `5190da5f` (`CorpusFixtureGuardTests` extended).
Merge base: **`a747da1a`** (2026-08-22 19:23:19 +0200, PR #486, the p95 split). Main has moved
**20 commits** and **137 files** since.

**The collision set is empty.** The branch touches 11 files —
`benchmarks/AiRaccoon.Benchmarks/Corpus/RealWorldCorpus.cs`, `…/RealWorldQueries.cs`,
`benchmarks/README.md`, `docs/reference/embedding-benchmark.md`,
`scripts/generate-benchmark-corpus.py`, `scripts/src/benchmark_corpus.py`,
`scripts/tests/test_benchmark_corpus.py`,
`tests/AiRaccoon.Tests/Integration/Retrieval/GoldenFileRegenerationTool.cs`,
`tests/AiRaccoon.Tests/Unit/Retrieval/CorpusFixtureGuardTests.cs`, `…/Unit/Retrieval/README.md`,
`…/Unit/Retrieval/assets/reference-topk.json` — and `comm -12` against main's 137 changed files
returns **nothing**. The branch already folded main in at the merge base via `cb9c2e3b`, and main's
20 commits since have not opened any of those 11 files. PR **#499** is `OPEN`, `isDraft: true`,
`mergeable: UNKNOWN` (stale computation, not a conflict signal). So the "drift risk" of leaving WP6
last is, measured today, **zero file collisions** — the only cost of waiting is calendar, and the
run itself (the parity/retrieval gate in the foreground).

### (h) The filed-not-started issues (all OPEN; `gh issue view` today)

| # | What it is | Session 4? |
|---|---|---|
| **#477** | Job-level metrics: `job.<name>.duration_ms` + `job.<name>.rows` via `IMeasurementRecorder`, surfaced by `memory_performance` | **WP3** — but see (d): its scope ≠ the predecessor's WP12-C. G6 |
| **#485** | Directory walk passes `keepCode: null`, so a shrinking code file re-ingested via `memory_ingest_directory` strands `code_entries` rows (single-file leg fixed by #481) | **Fold into WP2** — same method, `IngestDirectoryAsync` |
| **#493** | `PerformanceTools` description hand-lists six search phases; `SearchResults.PhaseNames` has nine after #483 (`open`, `embed`, `adjustment` missing) | **WP9.** A *derive-or-delete-the-list* violation in our own code |
| **#497** | `ManifestPoolingRepair` checks `TokenEmbeddingsOutput`'s rank but never `EmbeddingOutput`'s, so a bge-m3 manifest downloaded before #496 stays wrong | **WP8.** Anyone who downloaded early stays broken until they re-download |
| **#504** | `ModelDownloadPlanner` output selection: two two-output name shapes untested, one regresses under the distinctness guard; wants rank-based selection via #475's `OnnxOutputRanks` | **WP8** — same file family as #497 |
| **#519** | `TestData.CreateMemoryStore` (`tests/AiRaccoon.Tests/TestData.cs:86,89`) still defaults two deps to Null objects; ~74 callers | **WP9** — the owner's own #518 ruling, unenforced |

### (i) The two owner calls from merged PR #524

**#524 is a PR, not an issue** — `fix(embedding): log and show in doctor the ORT intra-op thread
count that took effect (#522)`, **MERGED**, branch `fix/522-log-embedding-threads`.

1. **Three renderings of one value (G4).** `SettingsCommands.cs:337`
   (`$"threads: {threadsRaw}{(threadsRaw == "0" ? " (ORT default)" : "")}"`) → `settings model show`
   prints **`threads: 0 (ORT default)`**: keeps the digit, appends an explanation. `:298` has a
   third spelling for the `set` confirmation. `DoctorCommands.cs:73`
   (`$"embedding threads: {EmbeddingService.ThreadCountDisplay(threads.Threads)} ({threads.Source})"`,
   helper at `EmbeddingService.cs:310-311`) → doctor prints **`embedding threads: ORT default
   (setting)`**: replaces the digit, appends the *source*. The parenthesis means a different thing on
   each surface. Neither was called out in #524's body.
2. **EventId 428 logs `Threads` as a string (G5).** `EmbeddingService.cs:434-436` declares
   `EmbeddingSessionCreated(ILogger logger, string threads, string source)` for
   *"Embedding session created: intra-op threads {Threads} ({Source})"* — `threads` is `string`, fed
   `ThreadCountDisplay(...)`. A structured sink gets `"5"` and `"ORT default"` in one field, so
   nothing can aggregate or alert on it numerically.

---

## How work lands (inherited from part 3; every WP obeys it)

- **One work package = one PR.** Branch `task/pd4-<slug>` in its **own worktree**
  (`.ai-badger/worktrees/<slug>`), never in the main checkout, never in this one.
- **Draft at the first commit.** `gh pr create --draft` on commit #1. **Push after every commit** —
  the owner may squash-merge at any moment and unpushed commits are lost in the squash.
- **RED first.** Every WP below names the test that must be *seen failing* before the production
  edit, and the failure expected. A check that has only ever passed is not a check.
- **Review loop.** After each change post `@pd4-<wp> Ready to review` and `gh pr ready <n>`; poll
  every 5 min (`gh pr view <n> --json comments,reviews` + `gh api …/pulls/<n>/comments`) filtered
  after the latest *Ready to review*. **Merge on the substantive review, not the first approve**
  (#517's lesson), and only with a green CI rollup — check the rollup before `--admin`, which
  bypasses red required checks. `gh pr merge <n> --squash --delete-branch --admin`.
- **New test classes carry class-level traits**, or they fall outside every filter and CI's rollup
  goes green having run nothing.
- **No wall-clock assertions.** Counts and ordering only (owner ruling #464).
- **Lanes never run the unfiltered suite**: `dotnet build` plus the one `--filter` in the WP. CI
  owns the rest. Run the Slow lane too when shared infrastructure changed.
- **Merge `origin/main`, never rebase a pushed branch.** `git fetch origin && git merge origin/main`
  before every integration step. Never `git pull`, never a force variant, **never `git stash`** (it is
  shared across worktrees). **Broadcast when main moves** — message every peer session, naming the collision.
- **Never write `~/.ai-raccoon`; never bind 7721.** Scratch banks only: `--data-root <scratch>`,
  `--port 79xx`, `--idle-timeout 0` for anything that drains.
- Models: **Sonnet** implements, **Opus** plans and reviews; the reviewer is never the implementer.

---

## Work packages

Each row's gate is the question it waits on; **ungated** means it may start once rev 2.0 exists.

### WP1 — WP12-B: the drain re-signals itself on a full row budget **[G1]**

- **Scope / files.** `src/AiRaccoon.Infrastructure/Embedding/EmbedDrainService.cs` **only** —
  `DrainOnceAsync` `:115-121` gains the re-enqueue, the class doc `:22-24` is rewritten to state the
  new contract. No edit to `BankMaintenanceHostedService.cs`.
- **RED first.** New `EmbedDrainContinuousTests` (class-level trait): fake `ICodeEmbedder` returns
  exactly `rowsPerRun` twice then 0; one `TryEnqueue`; await `Drains` == 3. **No `TimeProvider`.**
  Red today: `Drains` stops at 1.
- **Acceptance.** One signal drains a backlog of N·budget rows in ⌈N⌉ passes; a partial budget does
  not re-signal; a re-signal for an already-queued corpus coalesces (assert `CoalescedCount`).
- **Gate command.** `dotnet test --filter "FullyQualifiedName~EmbedDrain|FullyQualifiedName~EventPump" --nologo -v m`
- **Lane.** dotnet-engineer / Sonnet. **Collisions.** Shares `EmbedDrainService.cs` with **WP5** —
  run WP1 first (WP5 deletes producers, WP1 edits the consumer).

### WP2 — the directory walk: ancestor ignore root + the `code_entries` prune leg **[G2, G9]**

- **Scope / files.** `src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs` `IngestDirectoryAsync`
  (`:125-…`) and its `SqliteMemoryStore` caller. Two changes in one PR because they are the same
  method: (i) `:131` `LoadAsync(path, …)` → `ResolveIgnoreRootAsync(...)` + `LoadAsync(ignoreRoot, …)`,
  and `:133`'s `IsIgnored(ignoreRules, path, file)` → `ignoreRoot` (the relative base at `:405-427`
  must match the load root); (ii) #485 — track per-file code chunk hashes through the walk and stop
  passing `keepCode: null`.
- **RED first, two tests.** `FileIngestorIgnoreTests`: ingest `root/sub` where `root/ai-raccoon.ignore`
  excludes `sub/skip/**` and `root` is a registered watch → row count for `skip/` is **0**; red today,
  non-zero. `DirectIngestReplacesStaleChunksTests` (extend): a shrinking code file re-ingested via the
  **directory** walk leaves 0 stranded `code_entries`; red today, non-zero.
- **Acceptance.** Both above; **and** a walk with no ancestor watch/scope entry behaves exactly as
  today (the third `ResolveIgnoreRootAsync` branch returns the parent) — the no-regression leg.
- **Gate command.** `dotnet test --filter "FullyQualifiedName~FileIngestor|FullyQualifiedName~DirectIngest" --nologo -v m`
- **Lane.** dotnet-engineer / Sonnet. **Collisions.** Owns `FileIngestor.cs` alone this session.
  If G2 says *docs only*, this WP shrinks to (ii) plus one line in
  `docs/reference/agent-memory-server.md`.

### WP3 — job-run counters **[G6, shaped by #477]**

- **Scope depends on G6.** Three shapes, smallest first: **(i)** nothing new — EventId 1003 already
  carries `{Rows}` for the drain; close #477 as covered for the hot path. **(ii)** EventId 525 gains
  `rows`/`batches`, which requires `IMaintenanceJob.RunAsync` → `ValueTask<MaintenanceJobResult>`
  across **seven** implementations plus `MaintenanceJobOutcome` (`MaintenanceJobRunner.cs:9`).
  **(iii)** #477 as written — `job.<name>.duration_ms` + `job.<name>.rows` via `IMeasurementRecorder`,
  listed in `MetricsConfigKeys`, visible from `memory_performance`.
- **RED first (for ii/iii).** `MaintenanceJobRunnerTests`: a job embedding 128 rows in 4 batches
  produces a record carrying `Rows=128, Batches=4` / a `job.code-reindex.rows` series. Red today:
  no such field/series exists.
- **Gate command.** `dotnet test --filter "FullyQualifiedName~MaintenanceJobRunner|FullyQualifiedName~Metrics" --nologo -v m`,
  plus `docs/reference/logging-event-ids.md` updated if 525's template changes.
- **Lane.** dotnet-engineer / Sonnet. **Collisions.** Shape (ii) touches every `IMaintenanceJob`,
  which **WP5 deletes two of** — run WP5 first if both are approved.

### WP4 — #524 follow-ups: one rendering, one numeric field **[G4, G5]**

- **Scope / files.** `src/AiRaccoon/Setup/Cli/Commands/SettingsCommands.cs:298,337`,
  `src/AiRaccoon/Setup/Cli/Commands/DoctorCommands.cs:73`,
  `src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs:310-311,434-436`,
  `docs/reference/logging-event-ids.md`.
- **RED first.** A CLI test asserting `settings model show` and `doctor` print the **same** phrase
  for the same stored `0`; a logging test asserting EventId 428's `Threads` state value is an `int`.
  Red today on both.
- **Gate command.** `dotnet test --filter "FullyQualifiedName~SettingsCommands|FullyQualifiedName~Doctor|FullyQualifiedName~ThreadResolution" --nologo -v m`
- **Lane.** dotnet-engineer / Sonnet. **Ungated on files** — no other WP touches these. Smallest
  item here; run it first as the session's warm-up.

### WP5 — WP11-C Option B: drop the enqueue-only jobs **[G7 — recommendation is now DECLINE]**

- **Scope, if approved.** Delete `PendingEmbedJob` and `CodeReindexJob`; the on-demand poll enqueues
  both corpora from a new small component that must **also** carry `CodeReindexJob.HasWorkAsync`'s
  fingerprint reconcile and both jobs' `embed_state='pending'` read. **49 files** — see §Review (f).
- **RED first.** A test asserting the poll enqueues an `EmbedDrainRequest` per corpus with pending
  rows and none otherwise; **plus** the two guarantees that must survive verbatim —
  `EmbedDrainServiceTests.CoalescedSignal_IsRecoveredByTheNextPoll` and
  `EmbedSweepAfterJobsTests.APassWhoseJobCreatesPendingRows_SignalsTheDrainInTheSamePass` — kept as
  behaviour assertions with only their construction lines changed. If either has to be rewritten to
  pass, the change has broken something and the PR is wrong.
- **Gate command.** `dotnet test --filter "FullyQualifiedName~Maintenance|FullyQualifiedName~EmbedDrain|FullyQualifiedName~CodeCorpus" --nologo -v m`, **plus the Slow lane and a full-scope fast run** — shared infrastructure, 13 test files, and a BDD feature block.
- **Lane.** dotnet-engineer / Sonnet, architect / Opus for the ADR-0091 amendment first.
  **Collisions.** `EmbedDrainService.cs` (after **WP1**), `AppRegistrations.cs`,
  `BankMaintenanceHostedService.cs`, every `IMaintenanceJob` (before **WP3** shape ii), and
  `docs/features/code-corpus/code-corpus.feature`.
- **If G7 declines**, this WP disappears and §Review (f) is its record — the two classes stay, and
  the one-line `RunAsync` is the correct shape for a job whose value is `HasWorkAsync`.

### WP7 — WP12-E: quantized / CoreML inference for the code engine **[G3]**

- **Scope.** No production file. Output: one dated research record under `docs/work/` plus an ADR
  draft (an ADR-0049 amendment if an arm wins).
- **Arms** — three, on the S3–S5 protocol re-stated in §Review (c): today's fp32 CPU session;
  `code-daemon-embed-v1` int8-quantized; the CoreML EP via `AppendExecutionProvider_CoreML`
  (**available in the pinned 1.29.0 osx-arm64 package — verified**). Plus a vector-drift check of the
  same 1,762 chunks against the fp32 vectors.
- **Acceptance.** Every figure carries its command and a measured/read/inferred tag; every rows/s
  number comes from a fixed 150 s window after a restart and a re-activate; baselines quoted are
  S2/S4/S5. **No production edit and no engine swap** — the record ends in a recommendation.
- **Lane.** architect / Opus. **No file collision.** Runs in parallel with everything.

### WP8 — model-manifest repair pair: #497 and #504 **[G9]**

- **Scope / files.** `src/AiRaccoon.Infrastructure/Embedding/Download/ModelDownloadPlanner.cs` and
  `ManifestPoolingRepair`. One strand, two commits: #497 (repair also checks `EmbeddingOutput`'s
  real ONNX rank when it names a distinct output) then #504 (rank-based output selection via #475's
  `OnnxOutputRanks`; names as tie-breaker only).
- **RED first.** A bge-m3-shaped manifest written *before* #496 is left uncorrected by the repair
  today; and both untested two-output name shapes (`[sentence_embedding, <tail>]`,
  `[token_embeddings, <non-embedding tail>]`) get a failing test before the fix.
- **Gate command.** `dotnet test --filter "FullyQualifiedName~ModelDownloadPlanner|FullyQualifiedName~ManifestPooling" --nologo -v m`
- **Lane.** dotnet-engineer / Sonnet. **No collision** with any other WP.

### WP9 — small filed issues: #519 and #493 **[G9]**

- **#519** — `tests/AiRaccoon.Tests/TestData.cs:86,89`: remove the two optional Null-object defaults,
  make every caller pass its double explicitly (~74 call sites, mechanical). Done when
  `grep -rn "?? Null[A-Za-z]*\.Instance" src tests benchmarks` matches nothing outside
  intent-named single-file private helpers.
- **#493** — `PerformanceTools`' description stops enumerating phase names, **or** a test asserts
  every `SearchResults.PhaseNames` entry appears in the description. Prefer the first: it is the
  *derive-or-delete-the-list* invariant's own answer.
- **Found during this plan's research, unfiled** (*fix-what-you-find*):
  `docs/reference/logging-event-ids.md:53` says EventIds **517-519** exist on
  `BankMaintenanceHostedService`. They do not — its `Log` class declares 510-516 and 520-524 only.
  One-line doc fix; ride it into this PR rather than filing a seventh issue.
- **Gate command.** `dotnet build` plus `dotnet test --filter "FullyQualifiedName~Performance" --nologo -v m`;
  #519's proof is that the whole suite still builds, so this one **must** get a full-scope fast run.
- **Lane.** dotnet-engineer / Sonnet. **Collisions.** #519 touches ~74 test files — run it when no
  other lane has an open PR adding tests, i.e. **late**, or it conflicts with everything.

### WP6 — ADR-0089 implementation, five PRs **[G8]**

Sized in §Review (e), with the file list and the `6a → 6b/6c → 6d`, `6e ∥` ordering. Lane:
architect / Opus writes the implementation plan first (the ADR defers acceptance criteria to it),
then dotnet-engineer / Sonnet ×5. **The largest item in the session**; G8 asks whether all five
parts run now or only `6a`+`6b`+`6c`.

### WP10 — #455: the re-derived corpus, queries and parity golden. **LAST** **[G10]**

Resume `task/pd3-455-public-benchmark-corpus` @ `ea174faf` (draft #499). Merge `origin/main` in
first — **20 commits of drift, zero file collisions** (§Review (g)) — then re-run the parity and
retrieval gates **in the foreground**, regenerate the golden if they move, and mark ready. Standing
owner ruling: everything else finishes before the corpus moves under it. After it merges, S6b (the
history rewrite, #414) becomes the owner's to run over all three private-prose paths in one pass.

---

## Sequencing

**Immediately after rev 2.0, in parallel** (no shared files): **WP4** (CLI/doctor/EventId 428),
**WP7** (research, no files), **WP8** (`ModelDownloadPlanner` family).

**The maintenance/embed chain, strictly serial:** **WP1** (`EmbedDrainService` consumer) →
**WP5** (delete the two producer jobs) → **WP3** shape (ii)/(iii) (the `IMaintenanceJob` signature
or the metrics series). If G6 picks shape (i), WP3 disappears and the chain is WP1 → WP5.

**The ingestion strand, alone:** **WP2** owns `FileIngestor.cs` for the session. Independent of the
chain above — the two files stopped sharing a producer once #518 made the pump a required dep.

**WP6 (ADR-0089)** is its own strand: `6a → 6b/6c → 6d`, `6e` parallel. It touches
`MemorySchema.cs`, `MemoryTools.cs`, `ToolGate.cs`, `CliCommandTree.cs` — none of which any other
WP opens. It is the calendar risk, not the collision risk.

**WP9** runs **late**: #519 rewrites ~74 test call sites and conflicts with any open PR that adds a
test.

**WP10 (#455) is last**, then the owner's S6b.

**Shared-file map.** `EmbedDrainService.cs` → WP1 then WP5; `IMaintenanceJob` + implementations →
WP5 then WP3(ii); `FileIngestor.cs` → WP2 only; `EmbeddingService.cs`/`SettingsCommands.cs`/
`DoctorCommands.cs` → WP4 only; `ModelDownloadPlanner.cs` → WP8 only; `TestData.cs` → WP9 only. **No
two parallel WPs touch the same file.**

---

## Owner-only items

| Item | The session prepares | Strictly yours |
|---|---|---|
| S6b (#414 history rewrite) | Nothing new — WP5 of part 3 shipped the runbook (#473) and `scripts/verify-history-scrubbed.py` | Lifting the push guard, running `filter-repo` over all three paths, pushing and re-planting refs, telling every session to re-clone. **After WP10** |
| 1.33.0 publish | Nothing — `VERSION` is already 1.33.0 on main | The production-environment approval on the publish job |
| `~/.claude/settings.json` soft-deny patterns | The exact patterns, in #474 §5.2 | Editing the file. An agent must not touch it |
| #479 (unattributed force-pushes) | Nothing — the investigation is merged | Confirming or denying the IDE/interactive-shell hypothesis |
| G1–G10 below | This plan and the gate form | The answers |

---

## What session 4 deliberately leaves out (ask-if-simpler)

- **`IngestTimings`** — the profile's §7 five-phase ingest record. Ingest is **0.34 %** of the clock;
  a five-phase split of it buys nothing. Parked, and WP3 is the cheap first step instead.
- **Re-opening WP12-A (length-sorted batching).** Measured **negative** in #511 across six 150 s
  windows (branch never faster; ~30 % slower under lane contention, same-binary variance 2.8×).
  Closed unmerged. It gets re-opened on an isolated machine or not at all.
- **`CodeChunker.VerifyAndShed`'s double concatenation** (`CodeChunker.cs:68,115-129`). Measured
  below the noise floor — its phase is 0.02 % of the end-to-end clock. Tidy-up only.
- **A second embedding-model tuning round** (code-chunk-size vs recall). Still needs a measurement
  budget this session does not have, and nothing queued depends on it.
- **`ToolGate`'s own optional-null dep** (`ToolGate.cs:14`, `IModelMigrationStore? migrations = null`).
  It is the same class of defect as #519 and WP6c opens that file anyway — fold it into WP6c rather
  than filing a third ticket.
- **A repo-wide private-prose scanner** and **further review-loop automation.** Unchanged from part 3.

---

## Owner gate — decisions only you can make

Ten cards. Each says what becomes true if you approve, carries the number behind it, and ends in a
recommendation. **G1–G3 are part 3's G20–G22, re-numbered and corrected against the tree as merged**
— read them again even if you had made up your mind, because two of the three headline numbers moved.

### Carried from part 3 (re-derived)

**G1 — The embed drain re-signals itself while the backlog is non-empty, instead of waiting out the
15 s poll.** *(was G20)*
*Detail.* Unchanged in substance and in payoff: 1,762 rows drained end to end at **1.66 rows/s**
(1,061.3 s) while a clean 150 s window measured **2.347 rows/s** — ~29 % of the drain's wall clock
is not inference, of which **207 s (19.5 %)** is 13.8 poll gaps at 15 s. The bound stays
(`embedding.threads`, measured: cap 5 → 2.347 rows/s at 124–140 % CPU; cap 0 → 1.902; cap 1 → 0.213);
only the idle goes.
*What changed since the card was written.* The mechanism is now inside `EmbedDrainService.cs`, which
#507/#517 merged: `rowsPerRun` is read from `ISettingsStore` on **every** pass (`:107-109`, default
128, ceiling 4096) and the drained count already sits in a local at `:111`. The change is three
lines plus a doc rewrite — `if (drained >= rowsPerRun) pump.TryEnqueue(request);` — and the topic
coalesces on record equality, so a re-signal for an already-queued corpus is a no-op. **The RED test
named in part 3 was wrong**: `EmbedDrainService` takes no `TimeProvider` (its ctor is `:32-40`), so
the test uses no clock at all — one signal, a fake embedder returning a full budget twice, assert
the `Drains` counter reaches 3. Also note the class doc at `:22-24` currently promises the opposite
in prose and must be rewritten in the same commit.
*Why it matters.* A fifth of every code re-index is a timer, not work, and the component to change
now exists.
*Recommendation.* **Approve**, sequenced before WP5 (which deletes the producers). The cheaper first
move still stands and needs no code: set `maintenance.embed-rows-per-run.global` to 512 and 13.8
gaps become 3.4.

**G2 — Directory ingest resolves its ignore root the way single-file ingest already does.**
*(was G21 — and its headline number was wrong.)*
*Detail — the correction first.* Part 3's card said `memory_ingest_directory` on `src/` would
enumerate **379 MB of `bin`/`obj`**. It would not. `FileIngestor.cs:400`'s `IsHidden(root, path)` is
`WatchDenySet.Excludes(root, path)` and its own doc at `:397-398` names the set:
*node_modules/bin/obj/.git/.venv/__pycache__/dist/build/target*. That filter runs unconditionally at
`:133`, with or without an ignore file. This repo's `ai-raccoon.ignore` header says the same. Build
output has never entered the corpus. (For the record the number is now **1.8 GB** against 2.0 MB of
`.cs` — it just is not the argument.)
*Detail — what is actually broken.* `IngestDirectoryAsync` loads rules from the **walk root**
(`:131`), while `IngestFileAsync` first calls `ResolveIgnoreRootAsync` (`:46`, body `:97-121`) —
containing watch → admitting scope entry → parent. So an ignore file at an **ancestor** of the walk
root is honoured for one file and ignored for a directory. Concretely: point it at `src/` and the
root ignore file's `src/AiRaccoon/Models/` rule never loads, so **`vocab.txt` (231,508 B) is ingested
as a memory document** — `.txt` is memory-owned (`CodeExtensions.cs:6`). Real, verified, and ~1,600×
smaller than the card claimed. Note also that this is *ancestor resolution*, **not** nested
discovery: `IgnoreRulesProvider` reads exactly one file at whatever root it is given (`:91-96`), so
an `ai-raccoon.ignore` *below* the walk root still does nothing after this change.
*Why it matters.* It is a real correctness gap between two entry points into the same pipeline — but
the cost you were asked to weigh (a behaviour change that can silently drop rows on the next
re-ingest) is unchanged while the benefit shrank by three orders of magnitude.
*Recommendation.* **Approve the code fix, folded into the same PR as #485** (both edit
`IngestDirectoryAsync`; two PRs on one method is the waste). The documentation-only option is now
genuinely competitive — say so if you prefer it and WP2 shrinks to #485 plus one line.

**G3 — A time-boxed research item on quantized / CoreML inference for the code engine.** *(was G22 —
and both arms turn out to be cheaper than the card assumed.)*
*Detail.* The lever is unchanged: `InferenceSession.Run` is **99.6 %** of drain wall, every ranked
fix shaves 10–20 %, and at the best cap the drain uses ~140 % of 1,000 % available CPU — ~7× headroom
no batching reaches. What is new is that neither arm is speculative. **CoreML is already in the box:**
the pinned `Microsoft.ML.OnnxRuntime` **1.29.0** (`Directory.Packages.props:34`) ships
`runtimes/osx-arm64/native/libonnxruntime.dylib` exporting
`_OrtSessionOptionsAppendExecutionProvider_CoreML`, and the managed assembly carries the matching
P/Invoke — verified with `nm`, not assumed. No new package, no version bump; the arm costs a
`SessionOptions` call. **And int8 is not new here either:** the bundled *memory* model is already
`model_qint8_arm64.onnx` (23 MB) with 48 `MatMulInteger` ops (ADR-0049 `:55`). The code engine
(`code-daemon-embed-v1`, 768-dim) is the fp32 outlier. So this is applying a shipped pattern to a
second model, not opening a new one.
*The constraint, unchanged.* ADR-0049 binds both arms: they change the arithmetic path, so stored
vectors, the parity golden and `MiniLmGoldenVectorTests` are downstream. The experiment is one run of
#508's S3–S5 protocol per arm (restart, re-activate all 1,762 rows, fixed 150 s window, rows/s +
`top` CPU) against fp32-CPU / int8 / CoreML, plus a vector-drift check against the fp32 vectors.
*Why it matters.* Without it, WP12 tops out at roughly a third off a seventeen-minute drain and
nobody can say whether that is the ceiling or the floor.
*Recommendation.* **Approve as research only** — architect lane, one dated record plus an ADR draft,
**no production edit and no engine swap** until you rule on what it finds.

### New — the two calls from PR #524

**G4 — One rendering of the resolved thread count across every surface.**
*Detail.* The same stored `0` prints three ways today. `settings model show`:
`threads: 0 (ORT default)` (`SettingsCommands.cs:337`). `settings model threads 0`'s confirmation:
`embedding threads set to 0 (ORT default); …` (`:298`). `doctor`:
`embedding threads: ORT default (setting)` (`DoctorCommands.cs:73`, via
`EmbeddingService.ThreadCountDisplay` `:310-311`). The parenthesis means different things: on the
first two it explains the digit, on doctor it names the **source** (`setting` vs `halved-core
default`, `ThreadCountSource` `:305-306`). #524 introduced doctor's shape and did not reconcile the
others.
*Why it matters.* Three spellings of one value is how a support answer becomes "which command did
you run?". This is small enough to be free and only gets more expensive as surfaces multiply.
*Recommendation.* **Adopt doctor's shape everywhere: `<value-or-"ORT default"> (<source>)`,** with
`ThreadCountDisplay`/`ThreadCountSource` as the single pair of helpers all three call. Say the word
if you would rather keep the digit visible (`0 (ORT default, from setting)`) — either is fine, one
of them has to win.

**G5 — EventId 428 logs `Threads` as an integer, with the display string as its own field.**
*Detail.* `EmbeddingService.cs:434-436` declares
`EmbeddingSessionCreated(ILogger logger, string threads, string source)` — `threads` is a **string**,
fed `ThreadCountDisplay(...)`. So a structured sink receives `"5"` for a cap of five and
`"ORT default"` for zero, in the same field. Nothing can aggregate, alert or chart on it, which is
most of the reason the event was added (#522). The fix is `int threads` plus, if you want the words
kept, a third parameter — the log template already has two placeholders and gaining a third is not a
breaking change to any consumer, since nothing consumes it yet.
*Why it matters.* An observability field that cannot be aggregated is a comment with an EventId. It
is cheapest to fix now, before anything is built on the string shape.
*Recommendation.* **Approve — `int Threads` plus a separate `{Source}`,** and let `0` mean ORT's
default in the *documentation* (`docs/reference/logging-event-ids.md`) rather than in the value.

### Scope

**G6 — What "job-line counters" means: three shapes, and #477 asks for the largest.**
*Detail.* Part 3 scoped WP12-C as *"the EventId 525 'ran in N ms' message gains rows, batches,
elapsed"*. Two things it did not know. First, **the signature cannot carry a count**:
`IMaintenanceJob.RunAsync` returns `ValueTask<bool>` (`IMaintenanceJob.cs:25`) and that bool has a
documented scheduling meaning (`:20-23`, "created rows that still need embedding" → sweep again). A
row count needs `ValueTask<MaintenanceJobResult>` across the interface, its **seven**
implementations, and `MaintenanceJobOutcome` (`MaintenanceJobRunner.cs:9`). Second, **#477 asks for
something else entirely** — verbatim: *"`MaintenanceJobRunner` records `job.<jobName>.duration_ms`
(histogram) and `job.<jobName>.rows` (gauge …) through `IMeasurementRecorder` under the self-metrics
project id, and `SelfMetricNames` lists them so `memory_performance` shows one series per job."*
*And the cheap answer may already be shipped.* EventId **1003** already logs
`"Embed drain pass finished for {Corpus}: {Rows} row(s)"` every pass
(`EmbedDrainService.cs:161-163`). The drain is 99.66 % of the clock, so the hot path's row count is
**already observable**. What 525 lacks is counts for six jobs that are not on the hot path.
*Why it matters.* The gap #477 was filed for ("5k rows pending for an hour with no way to observe
progress except raw SQLite") is closed by EventId 1003 for the case that actually hurt. Shapes (ii)
and (iii) buy coverage of the quiet jobs, at the price of an interface change or a metrics series.
*Recommendation.* **Shape (iii) or nothing.** If job observability is worth the work, `#477` as
written is the version that reaches `memory_performance` where you would actually look; shape (ii)
changes seven files to improve a log line nobody greps. If it is not worth it this session, **park
#477 with a comment naming EventId 1003** and drop WP3.

**G7 — WP11-C Option B: delete `PendingEmbedJob`/`CodeReindexJob`. **I now recommend declining, and
the reason is new evidence, not a change of taste.**
*Detail — the premise is true.* Both `RunAsync` bodies really are one line:
`PendingEmbedJob.cs:47-51` and `CodeReindexJob.cs:44-48` each do a bare `TryEnqueue` and return
`ValueTask.FromResult(false)`. Part 3 recorded your ruling that Option B is *"the real fix"* and
that #517 only took Option A because the blast radius was disproportionate **to a settings-key
task**.
*Detail — what the enumeration found.* `RunAsync` is not what these classes are for. **(1)**
`CodeReindexJob.HasWorkAsync` (`:37-41`) is **not side-effect-free** — it calls
`ReconcileFingerprintAsync` first, which is what invalidates code rows to `pending` when a manifest
changes in place on disk (`CodeReindexJobTests.cs:161-187`). **(2)** `HasWorkAsync` is the **only
durable recovery path** for a coalesced or dropped pump signal: the 15 s poll passes the whole job
list to `RunDueAsync` with no filtering (`BankMaintenanceHostedService.cs:151-183`) and
`HasWorkAsync` reads `embed_state='pending'` directly — that is ADR-0076's "the channel is a wake-up,
not the record" made real, pinned by `EmbedDrainServiceTests.CoalescedSignal_IsRecoveredByTheNextPoll`.
**(3)** `PendingEmbedJob` is registered **last** on purpose so rows an earlier job leaves pending are
swept in the same pass — *"chunk-backfill produced 13,578 of them on a real bank"*
(`AppRegistrations.cs:158-163`), pinned by `EmbedSweepAfterJobsTests`. And the cost is **49 distinct
files** (16 src, 13 tests, 20 docs plus a live checklist template), including four user-facing
strings a class-name grep never finds and **ADR-0091, whose entire subject is these two jobs' shape**
and which would need an amendment, not a line edit.
*Why it matters.* The thing being called dead code is a one-line method on an object whose other
method carries three guarantees. Deleting the object to delete the one-liner means re-implementing
all three somewhere else and re-proving them — for no behaviour change at all.
*Recommendation.* **Decline, and close the follow-up with §Review (f) as its record.** A one-line
`RunAsync` on a job whose value is `HasWorkAsync` is not a smell, it is the right shape. If what
actually bothers you is the misleading `ValueTask<bool>` return, **G6 shape (ii)** is where to fix
that. Approve anyway and it becomes the session's largest item after WP6 — say so and WP6 or WP10
slips to pay for it.

**G8 — ADR-0089 (WP6): all five parts this session, or the registry and the refusal only.**
*Detail.* The ADR is **Accepted** (`0089-….md:5`, ratified at part 3's G2) and still entirely
unimplemented — `project_id_token_get` has zero matches in `src`/`tests`, and there is no `projects`
table. §Review (e) sizes it at five PRs with the files named: **6a** table + registration write path
(`MemorySchema.cs`'s unconditional `Ddl` block, **no `CurrentVersion` bump** — it stays 10, per
ADR-0086); **6b** the `project_id_token_get` tool; **6c** the refusal of an unregistered id — *the
risk-bearing part*, because every existing caller passes a raw id today and the compatibility rule
is "no registry row **and** no existing rows"; **6d** CLI `project id generate|convert`; **6e**
storage guidance plus the `ai-raccoon.ignore` entry (**not** `.gitignore` — you reversed that in the
#448 review). `6a → 6b/6c → 6d` serialise; `6e` is parallel.
*Why it matters.* This is the biggest item in the session and the only one that competes with WP10
(#455) for calendar. Half of it (6a/6b) is inert new surface; the value lands at **6c**, which is
also the part that can refuse a real user's write.
*Recommendation.* **6a + 6b + 6c this session, 6d + 6e next** — the refusal is the whole point and
the CLI is convenience on top of a tool that already exists. Approve all five if you would rather
not leave it half-landed; say so and WP10 slips.

**G9 — Which filed issues ride along: #485, #493, #497, #504, #519.**
*Detail.* All five are OPEN and all five are small. **#485** (directory walk strands `code_entries`;
the single-file leg shipped in #481) edits the **same method** as G2, so folding it into WP2 costs
nothing and splitting it costs a second PR on `IngestDirectoryAsync`. **#497** (the bge-m3 pooling
repair never checks `EmbeddingOutput`'s rank, so anyone who downloaded before #496 stays wrong) and
**#504** (rank-based output selection, two untested name shapes) are the same file family and pair
into one strand. **#493** (`PerformanceTools` lists six phases; `PhaseNames` has nine) is a
*derive-or-delete-the-list* violation in our own code — the fix is to stop enumerating. **#519**
(`TestData.CreateMemoryStore:86,89` still defaults two deps to Null objects) is your own #518 ruling
left unenforced, but it rewrites ~74 call sites and conflicts with every open PR that adds a test.
*Why it matters.* Four of the five cost hours, not days, and two of them (#485, #497) are live
defects a user can hit. #519 is the one with a scheduling cost rather than a work cost.
*Recommendation.* **Take all five: #485 into WP2, #497+#504 as WP8, #493+#519 as WP9 — and run WP9
last**, when no other lane has an open PR adding tests. Drop any of them and the WP shrinks; drop
#519 and nothing else moves.

**G10 — WP10 (#455) still runs LAST, now that the drift is measured.**
*Detail.* Your ruling of 2026-08-22 ~19:38 was that #455 runs last so nothing else moves the corpus
under it. Measured today: the branch `task/pd3-455-public-benchmark-corpus` @ `ea174faf` (draft
**#499**, OPEN) is 5 commits ahead of merge-base `a747da1a`; main has moved **20 commits / 137 files**
since; and the **intersection of the two changed-file sets is empty** — none of the branch's 11
files has been touched on main. So the feared cost of waiting (a growing conflict) is, today,
**zero**. The remaining costs of waiting are calendar and the fact that the S6b history rewrite is
blocked behind it. The remaining cost of promoting it is that the parity/retrieval gate must run in
the foreground while other lanes are live, which is exactly the contention that made #511's
measurements unusable.
*Why it matters.* #455 is the last thing standing between you and the one-pass history rewrite over
all three private-prose paths, and it has been parked for a day.
*Recommendation.* **Keep it last, as ruled** — the collision risk is nil either way, and the
foreground gate wants a quiet machine. But if the S6b rewrite is what you want soonest, promoting
#455 to first is now cheap, and G8's "6a+6b+6c only" is what pays for it.
