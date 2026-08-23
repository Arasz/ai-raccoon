# Plan — post-delta session 4 (rev 2.0 — gate answered 9/9; lanes open)

**Date:** 2026-08-23 · **Base:** main `72e15088` (`VERSION` = 1.33.0; one open PR, draft #499) ·
**Status:** rev 2.0 — **gate answered 9/9** (`docs/work/2026-08-23-post-delta-4-feedback.md`,
2026-08-23 05:32Z): G1–G4 APPROVE, **G6 APPROVE "3"** (shape (iii) — #477 as written), **G7 REJECT
"dont delete"** (WP5 dropped; the two jobs stay — standing ruling), **G8 APPROVE "3 now, 2 later —
create plan for the next session before starting"**, **G9 APPROVE "all"**, **G10 APPROVE "move it to
the next session"**. Review round 1 (PR #526) folded at rev 1.1. **Lanes open** — see §Re-review for
what the answers changed and which wave dispatches first ·
**Task:** `post-delta-4` · **Lane:** architect (plan + gate), Opus.

**What this session inherits**, from `docs/work/2026-08-22-post-delta-3-plan.md` rev 1.4: WP12-B/C/D/E
(gate G20–G22 never answered — the form `…-post-delta-3-wp12-review.html` is open and its feedback file
never arrived), WP10 (ADR-0089, approved at G15, not started), WP11-C follow-up Option B, and WP6
(#455, parked LAST by owner ruling 2026-08-22 ~19:38). Plus six filed-not-started issues in
`.ai-badger/state.json` and two owner calls surfaced by merged PR #524.

**Sources.** Every fact in §Review was re-derived today against `72e15088` and GitHub, not quoted from
the predecessor. Where the predecessor and the tree disagree, §Review says so and the gate card carries
the corrected number.

## Session todo — execution order (rev 2.0)

1. Fold the gate answers into this revision and re-review the affected designs (**done** — §Re-review).
2. **Wave 1, five code lanes in parallel** (file-disjoint, verified in §Sequencing):
   **WP1** drain re-signal · **WP2** ignore root + #485 prune · **WP3** #477 job metrics ·
   **WP4** #524 rendering + EventId 428 · **WP8** #497 + #504 manifest repairs.
3. **In parallel with wave 1, architect lane:** write `docs/work/2026-08-23-post-delta-5-plan.md`
   — carry-over: ADR-0089 **parts 4–5** (CLI `project id generate|convert`; storage guidance +
   `ai-raccoon.ignore`), **#455 LAST** (branch `task/pd3-455-public-benchmark-corpus` @ `ea174faf`,
   draft #499), the owner's S6b rewrite behind it, and anything session 4 does not finish.
   **G8 makes this a hard precondition: WP6 does not open until this document exists.**
4. **In parallel with wave 1, architect lane:** WP7's *desk* half — ORT/CoreML/ADR-0049 reading and
   the ADR draft skeleton. **Its measured arms do not run yet** (see item 6).
5. **Wave 2, after wave 1 merges and item 3 exists:** **WP6** — ADR-0089 parts 1–3, serial:
   **6a** `projects` table + registration write path → then **6b** `project_id_token_get` and
   **6c** the unregistered-id refusal.
6. **Wave 3, quiet machine, no other lane running:** WP7's measured arms (S3–S5 protocol).
   Scheduling this against live lanes is what made #511's numbers unusable.
7. **Wave 4, strictly last:** **WP9** — #493 and #519 (83 call sites across 77 files; it conflicts
   with every open PR that adds a test).
8. Close the session: update `.ai-badger/state.json` and `status-notes.json`, and hand session 5 the
   plan from item 3.

**Dropped / moved by the gate.** **WP5 is DROPPED** (G7 REJECT — record as a standing ruling).
**WP10 (#455) is MOVED to session 5** (G10) and stays LAST there. **ADR-0089 parts 4–5 are MOVED to
session 5** (G8).

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

**Exactly what a "re-signal on a full budget" touches — three edits, one file.** (1) `DrainOnceAsync`
`:115-121`: after `drained` is known, `if (drained >= rowsPerRun) pump.TryEnqueue(request);`. The pump
is already injected (`:33`), `TryEnqueue` never blocks or throws (`IEventPump<T>` `:22-23`), and the
topic coalesces on `EmbedDrainRequest`'s record equality (`EmbedDrainRequest.cs:20`), so a re-signal
for an already-queued corpus is a no-op — `PumpCeiling`/`PumpCapacity` are both 8 (`:43`, `:46`), item
space 2. (2) The class doc `:22-24` promises the **opposite** in prose — *"It never re-enqueues itself
when rows remain…"* — the contract this reverses, rewritten in the same commit. (3) No new EventId:
1003 `DrainFinished` (`:161-163`) already carries `{Rows}` per pass.

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

**Where the 15 s still lives.** `BankMaintenanceHostedService.cs:79` stays at 15 s and unedited.
Post-#517 the poll **still paces** the drain at one `rowsPerRun` pass per 15 s — the two jobs now only
`TryEnqueue` (see (f)), but nothing else signals a backlog that is already known non-empty. That is
precisely why the fix recovers the full 19.5 %: it removes the pacing without touching the poll.

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
`:133`'s two `path` arguments to `IsIgnored` become `ignoreRoot` (the relative base at `:405-427` must
match the load root or every pattern misses). Still a behaviour change — an ignore file that has no
effect today starts having one, and a bank can lose rows on its next re-ingest — and the docs-only
alternative now looks *stronger*, because the motivating number shrank by 1,600×.

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
invalidates all 1,762 rows to `pending` in one transaction — then count `embed_state='pending'` at the
start and end of a fixed 150-second window.* §9 carries the commands (scratch bank on **port 7931**,
`--idle-timeout 0`, the 469-file corpus, ingest over MCP/HTTP, `top -l 6 -s 5 -stats cpu,th`).
**Baselines: S4 = 2.347 rows/s (cap 5), S5 = 1.902 (cap 0), S2 = 1,061.3 s end to end.** Anything off
that protocol is not comparable.

### (d) WP12-C — job-line counters. The signature cannot carry a count, and #477 asks for something else.

**Today.** `MaintenanceJobRunner.Log.JobRan` is EventId **525** — *"maintenance job '{DisplayName}'
({JobName}) ran in {ElapsedMs:F0} ms"* (`MaintenanceJobRunner.cs:103-105`); the runner's record is
`MaintenanceJobOutcome(string Name, bool Ran, string? Error, bool CreatedWork = false)` (`:9`), fed
from `job.RunAsync(...)` at `:75`.

**Can `ValueTask<bool>` carry counts? No.** `IMaintenanceJob.RunAsync` returns `ValueTask<bool>`
(`IMaintenanceJob.cs:25`) and that bool has a documented scheduling meaning — `:20-23`: *"returning
true when it created rows that still need embedding. Only then does the pass sweep again."* One bit
with a contract attached, no free channel. Counts need either
`ValueTask<MaintenanceJobResult>` across the interface and its **ten implementations in seven files**
(`git grep -n ": IMaintenanceJob\|, IMaintenanceJob" -- src` — `MaintenanceJobs.cs` alone holds
`VacuumJob`, `Vec0ReclaimJob`, `ChunkBackfillJob` and `MetricsRetentionJob`), or an out-of-band
recorder.

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
guid or not** (3), except a raw-text id the bank *already holds rows for* (`SelectProjectIds`,
`MemorySql.cs:58-64`); `project_id_token_get` **mints and registers** (4); the `projects` table goes in
the **unconditional `Ddl` block with no `CurrentVersion` bump** — it stays **10**
(`MemorySchema.cs:54`), because a bump hard-fails every concurrent session and peer (ADR-0086).

**Sizing — five PRs, files named.**

| PR | Files | Notes |
|---|---|---|
| **6a** table + registration write path | `src/AiRaccoon.Infrastructure/Sqlite/MemorySchema.cs` (`Ddl` block near `:347`, the `metrics` precedent), `MemorySql.cs`, a new `Projects/` store | No `CurrentVersion` bump. Smallest, and everything else waits on it |
| **6b** `project_id_token_get` | `src/AiRaccoon/Tools/MemoryTools.cs` (502 lines; `Guid.CreateVersion7` already used at `:191`) | Thin per `mcp-thin`: mint + register + return |
| **6c** refusal of an unregistered id | `src/AiRaccoon/Tools/ToolGate.cs` (43 lines, `RequireAsync` `:17-35`) and/or `IMemoryAccessGuard` | **The risk-bearing part.** Every existing caller passes a raw id today. RED set first: legacy-known id works with a warning; legacy-unknown is refused; an unregistered guidv7 is refused |
| **6d** CLI `project id generate` / `project id convert` (one-way) | `src/AiRaccoon/Setup/Cli/CliCommandTree.cs` (655 lines), a new `Commands/ProjectCommands.cs` | System.CommandLine; option names keep their `--` prefix at `GetValue`; test argv, not just the handler |
| **6e** storage guidance + `ai-raccoon.ignore` entry | `docs/reference/agent-memory-server.md`, `ai-raccoon.ignore` | **Not** `.gitignore` — the owner reversed that in the #448 review. Parallel to all of the above |

`6a → 6b/6c → 6d` serialise; `6e` is parallel. **The largest item in the session**, and the only one
competing with WP10/#455 for calendar — gate **G8**.

### (f) WP11-C Option B — dropping the enqueue-only jobs. **It is not a small PR, and it deletes two live guarantees.**

**The premise is true.** Both `RunAsync` bodies are one `TryEnqueue`:
`PendingEmbedJob.cs:47-51` → `embedDrainPump.TryEnqueue(new EmbedDrainRequest(EmbedCorpus.Memory)); return ValueTask.FromResult(false);`
and `CodeReindexJob.cs:44-48`, identical for `EmbedCorpus.Code`.

**But `RunAsync` is not what these classes are for — `HasWorkAsync` is, and it is load-bearing three ways.**

1. **`CodeReindexJob.HasWorkAsync` is not side-effect-free.** `CodeReindexJob.cs:37-41` calls
   `embedder.ReconcileFingerprintAsync` *before* checking pending work — that reconcile is what
   invalidates code rows to `pending` when a manifest changes in place on disk, pinned by
   `CodeReindexJobTests.cs:161-187`. Deleting the job deletes reconcile-on-every-poll, which is not a
   drain signal at all.
2. **`HasWorkAsync` is the only durable recovery path for a dropped or coalesced signal.** The poll
   (`BankMaintenanceHostedService.cs:151-183`) passes the whole job list to `RunDueAsync` every 15 s
   with no filtering; `HasWorkAsync` reads `embed_state='pending'` directly, so a coalesced signal is
   picked up next tick — `EmbedDrainServiceTests.CoalescedSignal_IsRecoveredByTheNextPoll` (`:181`),
   the concrete form of ADR-0076's "the channel is a wake-up, not the record"
   (`EmbedDrainService.cs:27-29`).
3. **The same-pass ordering guarantee.** `AppRegistrations.cs:158-163` documents why `PendingEmbedJob`
   is registered **last** — *"chunk-backfill produced 13,578 of them on a real bank"*,
   `BankMaintenanceHostedService.cs:243-248` repeats it consumer-side, and
   `EmbedSweepAfterJobsTests.APassWhoseJobCreatesPendingRows_SignalsTheDrainInTheSamePass` proves it.

**The blast radius — and the count depends on which grep you run, which is itself the finding.**
`git grep -l "PendingEmbedJob\|CodeReindexJob"` returns **42 files** here (**40** at `72e15088`; this
plan and its review HTML are the extra two) and **understates the work**, because four live sites name
the job by its `JobName` *string* and no class-name grep reaches them.
`git grep -l "PendingEmbedJob\|CodeReindexJob\|pending-embed\|code-reindex"` returns **72**, of which
**22** are dated `docs/work/*` records a change would not touch — leaving **~50 live files: 16 src, 15
tests, 16 non-dated docs, 3 `.ai-badger`.** Treat 50 as an order of magnitude: only the src
enumeration below was verified file by file.
**The 16 src files** — the two classes, `AppRegistrations.cs:158-193,200`,
`BankMaintenanceHostedService.cs:243-248`, `ReingestRepairJob.cs:38`, `ReingestRepair.cs:27`,
`EmbedDrainService.cs:15`, `EntryEmbedder.cs:20,235`, `IEntryEmbedder.cs:43`, `ICodeEmbedder.cs:43,55`,
`CodeEmbedder.cs:16`, `MemorySql.cs:321,367`, **plus four a class-name grep misses** because they name
the job by its `JobName` string in *user-facing* text: `SqliteCodeEngineStore.cs:73`,
`SettingsEndpoint.cs:135` (EventId 675's message), `CliCommandTree.cs:178` (help) and
`SettingsCommands.cs:264` (CLI stdout). **15 tests** — `PendingEmbedJobTests.cs` (140 lines) and
`CodeReindexJobTests.cs` (301) deleted outright, plus `CliBankWriteTests.cs:56-65`,
`EmbedSweepAfterJobsTests.cs`, `PendingEmbedMaintenanceDrainTests.cs`,
`MaintenanceJobRunnerTests.cs:56`, `EmbedDrainServiceTests.cs:20,181`,
`CodeCorpusFeatureContext.cs:39,69,114`, `CodeCorpusSteps.cs:768`, `MaintenanceJobShapeTests.cs:8`,
`CodeEngineActivationTests.cs:127`, and three this enumeration missed (`CliBankWriteLedgerDriftTests`
self-adjusts — it is the gate that goes red if the ledger list is edited wrong).
**16 non-dated docs + 3 `.ai-badger` files** — including **ADR-0091**,
whose entire subject *is* these jobs' enqueue-only shape (`:11,45-46,129-133,150-162`) and which needs
an amendment not a line edit; `docs/features/code-corpus/code-corpus.feature:196-217` (a whole `Rule:`
block); `docs/how-to/configure-embedding-engines.md:186,202`;
`docs/reference/agent-memory-server.md:190`; and
`.ai-badger/skills/ai-raccoon-manual-checklist/templates/checklist-template.json:183`.

**Eight of the ten `IMaintenanceJob` implementations remain** (see (d): ten classes in seven files),
so the interface survives either way. That was never the question.

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

**The collision set is empty.** The branch touches 11 files (`RealWorldCorpus.cs`,
`RealWorldQueries.cs`, `benchmarks/README.md`, `docs/reference/embedding-benchmark.md`, two
`benchmark_corpus` scripts + their test, `GoldenFileRegenerationTool.cs`, `CorpusFixtureGuardTests.cs`,
`Unit/Retrieval/README.md`, `reference-topk.json`) and `comm -12` against main's 137 changed files
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

**#524 is a PR, not an issue** — `fix(embedding): log and show in doctor the ORT intra-op thread count
that took effect (#522)`, **MERGED**, branch `fix/522-log-embedding-threads`.

1. **Three renderings of one value (G4).** `SettingsCommands.cs:337` prints **`threads: 0 (ORT
   default)`** — keeps the digit, appends an explanation; `:298` is a third spelling for the `set`
   confirmation. `DoctorCommands.cs:73`, via `EmbeddingService.ThreadCountDisplay` (`:310-311`) and
   `ThreadCountSource` (`:305-306`), prints **`embedding threads: ORT default (setting)`** — replaces
   the digit, appends the *source*. The parenthesis means a different thing on each surface, and
   neither was called out in #524's body.
2. **EventId 428 logs `Threads` as a string (ungated, folded into WP4).**
   `EmbeddingService.cs:434-436` declares `EmbeddingSessionCreated(ILogger, string threads, string
   source)` for *"Embedding session created: intra-op threads {Threads} ({Source})"* — fed
   `ThreadCountDisplay(...)`, so a structured sink gets `"5"` and `"ORT default"` in one field and
   nothing can aggregate on it. Nothing consumes the field yet, so this is *fix-what-you-find*, not a
   decision (rev 1.0 carried it as G5; the card is withdrawn).

### Change log — review round 1 (ox-alpha, PR #526); all findings applied

| | Applied |
|---|---|
| **B1** blast radius | §Review (f) + G7 state both greps: strict **42** (understates — four `JobName`-string sites), union **72**, minus **22** dated `docs/work` = **~50 live**. DECLINE stands |
| **B2** implementations | **Ten in seven files** in §Review (d), WP3, G6; reconciled with "eight of the ten remain" in (f) |
| **B3** ordering | Todo 6/7 swapped — WP5 before WP3 *(superseded at rev 2.0: G7 dropped WP5)* |
| **B4** proof-of-done | WP10 gained scope/files/acceptance/**gate commands**/lane; WP6 gained a lane + *"no `6x` PR opens until the implementation plan defines its gate"*; `Acceptance` added to WP3/4/5/8 |
| **N5–N9** | `MemoryTools.cs:191`; `FileIngestorIgnoreTests` **(extend)** + path; `EmbedDrainServiceTests.cs:181` (**two** sites — one survived the first pass); **83 call sites / 73 files**; §Review (a) reworded — the poll *still paces* at one pass per 15 s |
| **Gate shape** | **G5 withdrawn** into WP4 (ungated); G6–G10 ids unchanged (**nine cards**); **G10 leads with the promote-#455 alternative** |
---

## How work lands (inherited from part 3; every WP obeys it)

- **One work package = one PR.** Branch `task/pd4-<slug>` in its **own worktree**
  (`.ai-badger/worktrees/<slug>`), never in the main checkout, never in this one.
- **Draft at the first commit.** `gh pr create --draft` on commit #1. **Push after every commit** —
  the owner may squash-merge at any moment and unpushed commits are lost in the squash.
- **RED first.** Every WP names the test that must be *seen failing* before the production edit, and
  the failure expected. A check that has only ever passed is not a check.
- **Review loop.** Post `@pd4-<wp> Ready to review` + `gh pr ready <n>`; poll every 5 min
  (`gh pr view <n> --json comments,reviews` + `gh api …/pulls/<n>/comments`) filtered after the latest
  *Ready to review*. **Merge on the substantive review, not the first approve** (#517's lesson), and
  only with a green rollup — check it before `--admin`, which bypasses red required checks.
- **New test classes carry class-level traits**, or they fall outside every filter and the rollup goes
  green having run nothing. **No wall-clock assertions** — counts and ordering only (#464).
- **Lanes never run the unfiltered suite**: `dotnet build` plus the WP's one `--filter`. CI owns the
  rest; run the Slow lane too when shared infrastructure changed.
- **Merge `origin/main`, never rebase a pushed branch.** `git fetch origin && git merge origin/main`
  before every integration step. Never `git pull`, never a force variant, **never `git stash`** (it is
  shared across worktrees). **Broadcast when main moves** — message every peer session, naming the collision.
- **Never write `~/.ai-raccoon`; never bind 7721.** Scratch banks only: `--data-root <scratch>`,
  `--port 79xx`, `--idle-timeout 0` for anything that drains.
- Models: **Sonnet** implements, **Opus** plans and reviews; the reviewer is never the implementer.

---

## Work packages

Each row's gate is the question it waits on; **ungated** means it may start once rev 2.0 exists.

### WP1 — WP12-B: the drain re-signals itself on a full row budget

**Status: OPEN — G1 APPROVE.** Wave 1.

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

### WP2 — the directory walk: ancestor ignore root + the `code_entries` prune leg

**Status: OPEN — G2 APPROVE, G9 APPROVE "all".** Both legs ship in one PR, as recommended. Wave 1.

- **Scope / files.** `src/AiRaccoon.Infrastructure/Ingestion/FileIngestor.cs` `IngestDirectoryAsync`
  (`:125-…`) and its `SqliteMemoryStore` caller. Two changes in one PR because they are the same
  method: (i) `:131` `LoadAsync(path, …)` → `ResolveIgnoreRootAsync(...)` + `LoadAsync(ignoreRoot, …)`,
  and `:133`'s `IsIgnored(ignoreRules, path, file)` → `ignoreRoot` (the relative base at `:405-427`
  must match the load root); (ii) #485 — track per-file code chunk hashes through the walk and stop
  passing `keepCode: null`.
- **RED first, two tests.** `FileIngestorIgnoreTests` **(extend — the class already exists at
  `tests/AiRaccoon.Tests/Integration/Ingestion/FileIngestorIgnoreTests.cs:18`; do not create it
  fresh or the build breaks on a duplicate type)**: ingest `root/sub` where `root/ai-raccoon.ignore`
  excludes `sub/skip/**` and `root` is a registered watch → row count for `skip/` is **0**; red today,
  non-zero. `DirectIngestReplacesStaleChunksTests` (extend): a shrinking code file re-ingested via the
  **directory** walk leaves 0 stranded `code_entries`; red today, non-zero.
- **Acceptance.** Both above, **plus** a walk with no ancestor watch/scope entry behaving exactly as
  today (the third `ResolveIgnoreRootAsync` branch returns the parent) — the no-regression leg.
- **Gate command.** `dotnet test --filter "FullyQualifiedName~FileIngestor|FullyQualifiedName~DirectIngest" --nologo -v m`
- **Lane.** dotnet-engineer / Sonnet. **Collisions.** Owns `FileIngestor.cs` alone this session.
  If G2 says *docs only*, this WP shrinks to (ii) plus one line in
  `docs/reference/agent-memory-server.md`.

### WP3 — job-run metrics (#477 as written)

**Status: OPEN — G6 APPROVE, note "3" = shape (iii).** Shapes (i) and (ii) are off the table; the
deliverable is the `IMeasurementRecorder` series reachable from `memory_performance`. **G7's REJECT
changes this WP's blast radius — see §Re-review.** Wave 1.

- **Scope depends on G6.** Three shapes, smallest first: **(i)** nothing new — EventId 1003 already
  carries `{Rows}` for the drain; close #477 as covered for the hot path. **(ii)** EventId 525 gains
  `rows`/`batches`, which requires `IMaintenanceJob.RunAsync` → `ValueTask<MaintenanceJobResult>`
  across its **ten implementations in seven files** plus `MaintenanceJobOutcome`
  (`MaintenanceJobRunner.cs:9`).
  **(iii)** #477 as written — `job.<name>.duration_ms` + `job.<name>.rows` via `IMeasurementRecorder`,
  listed in `MetricsConfigKeys`, visible from `memory_performance`.
- **RED first (for ii/iii).** `MaintenanceJobRunnerTests`: a job embedding 128 rows in 4 batches
  produces a record carrying `Rows=128, Batches=4` / a `job.code-reindex.rows` series. Red today:
  no such field/series exists.
- **Acceptance.** The chosen shape's counter is observable for a job that did work **and** absent (not
  zero) for a job that was not due; the other nine `IMaintenanceJob` implementations compile and their
  existing tests pass unmodified; for shape (iii), `memory_performance` returns the new series and
  `MetricsConfigKeys` lists its name. For shape (i): #477 is closed with a comment naming EventId 1003
  and no code changes.
- **Gate command.** `dotnet test --filter "FullyQualifiedName~MaintenanceJobRunner|FullyQualifiedName~Metrics" --nologo -v m`,
  plus `docs/reference/logging-event-ids.md` updated if 525's template changes.
- **Lane.** dotnet-engineer / Sonnet. **Collisions — none (rev 2.0).** WP5 is dropped, so nothing
  else opens `IMaintenanceJob` or its implementations this session, and **route (a) does not touch
  them either**. This WP owns `MaintenanceJobRunner.cs` and the `Metrics/` tree for wave 1. The rev
  1.1 dependency "run WP5 first" is void.

### WP4 — #524 follow-ups: one rendering, one numeric field

**Status: OPEN — G4 APPROVE**; the EventId 428 `int` rides along ungated. Both items, one PR. Wave 1.

- **Two items, one PR.** The rendering choice is **G4**. The EventId 428 `Threads`-as-a-string fix
  rides along **ungated** (*fix-what-you-find*): nothing consumes the field yet, so a mixed-type
  structured-log value is a defect, not a decision. It was G5 in rev 1.0; that card is withdrawn.
- **Scope / files.** `src/AiRaccoon/Setup/Cli/Commands/SettingsCommands.cs:298,337`,
  `src/AiRaccoon/Setup/Cli/Commands/DoctorCommands.cs:73`,
  `src/AiRaccoon.Infrastructure/Embedding/EmbeddingService.cs:310-311,434-436`,
  `docs/reference/logging-event-ids.md`.
- **RED first.** A CLI test asserting `settings model show` and `doctor` print the **same** phrase
  for the same stored `0`; a logging test asserting EventId 428's `Threads` state value is an `int`.
  Red today on both.
- **Acceptance.** `settings model show`, the `settings model threads` confirmation and `doctor` print
  the **same** phrase for the same stored value, from one pair of helpers; EventId 428's `Threads`
  state value is an `int` and `{Source}` is its own field; `logging-event-ids.md`'s 428 row matches.
- **Gate command.** `dotnet test --filter "FullyQualifiedName~SettingsCommands|FullyQualifiedName~Doctor|FullyQualifiedName~ThreadResolution" --nologo -v m`
- **Lane.** dotnet-engineer / Sonnet. **Ungated on files** — no other WP touches these. Smallest
  item here; run it first as the session's warm-up.

### WP5 — WP11-C Option B: drop the enqueue-only jobs — **DROPPED**

**Status: DROPPED — G7 REJECT, owner's words: _"dont delete"_.**

**Recorded as a standing ruling, not a deferral.** `PendingEmbedJob` and `CodeReindexJob` stay.
A one-line `RunAsync` on a job whose value is `HasWorkAsync` is the accepted shape here, and the three
guarantees enumerated in §Review (f) — the fingerprint reconcile, the coalesced-signal recovery path,
and the same-pass sweep ordering — are the reason. **Do not re-propose this without new evidence that
speaks to those three.** The WP12/WP11-C follow-up item is closed; §Review (f) is its record, and
`.ai-badger/state.json` should drop it from `next` at session close.

<details><summary>The design that would have been built, kept for the record</summary>

- **Scope, if approved.** Delete `PendingEmbedJob` and `CodeReindexJob`; the on-demand poll enqueues
  both corpora from a new small component that must **also** carry `CodeReindexJob.HasWorkAsync`'s
  fingerprint reconcile and both jobs' `embed_state='pending'` read. **49 files** — see §Review (f).
- **RED first.** A test asserting the poll enqueues an `EmbedDrainRequest` per corpus with pending rows
  and none otherwise; **plus** `CoalescedSignal_IsRecoveredByTheNextPoll` and
  `APassWhoseJobCreatesPendingRows_SignalsTheDrainInTheSamePass` kept as behaviour assertions with only
  their construction lines changed. **If either must be rewritten to pass, the change broke something.**
- **Acceptance.** Both named guarantees still hold with their assertions unchanged (only construction
  lines may move); a manifest changed in place on disk still invalidates code rows to `pending`; a
  pass whose job creates pending rows still signals the drain **in the same pass**; no
  `IMaintenanceJob` remains whose `RunAsync` is a bare enqueue; ADR-0091 carries an amendment naming
  what replaced the two jobs.
- **Gate command.** `dotnet test --filter "FullyQualifiedName~Maintenance|FullyQualifiedName~EmbedDrain|FullyQualifiedName~CodeCorpus" --nologo -v m`, **plus the Slow lane and a full-scope fast run** — shared infrastructure, 15 test files, and a BDD feature block.
- **Lane.** dotnet-engineer / Sonnet, architect / Opus for the ADR-0091 amendment first.
  **Collisions.** `EmbedDrainService.cs` (after **WP1**), `AppRegistrations.cs`,
  `BankMaintenanceHostedService.cs`, every `IMaintenanceJob` (before **WP3** shape ii), and
  `docs/features/code-corpus/code-corpus.feature`.

</details>

### WP7 — WP12-E: quantized / CoreML inference for the code engine

**Status: OPEN — G3 APPROVE.** Split across waves: **desk half in wave 1**, **measured arms in wave 3
on a quiet machine** (§Re-review explains why this changed).

- **Scope.** No production file. Output: one dated research record under `docs/work/` plus an ADR
  draft (an ADR-0049 amendment if an arm wins).
- **Arms** — three, on the S3–S5 protocol in §Review (c): fp32 CPU today; `code-daemon-embed-v1`
  int8-quantized; the CoreML EP via `AppendExecutionProvider_CoreML` (**available in the pinned 1.29.0
  osx-arm64 package — verified**). Plus a vector-drift check of the same 1,762 chunks against fp32.
- **Acceptance.** Every figure carries its command and a measured/read/inferred tag; every rows/s comes
  from a fixed 150 s window after a restart and re-activate; baselines quoted are S2/S4/S5. **No
  production edit and no engine swap** — the record ends in a recommendation.
- **Lane.** architect / Opus. **No file collision.** Runs in parallel with everything.

### WP8 — model-manifest repair pair: #497 and #504

**Status: OPEN — G9 APPROVE "all".** Wave 1.

- **Scope / files.** `src/AiRaccoon.Infrastructure/Embedding/Download/ModelDownloadPlanner.cs` and
  `ManifestPoolingRepair`. One strand, two commits: #497 (repair also checks `EmbeddingOutput`'s
  real ONNX rank when it names a distinct output) then #504 (rank-based output selection via #475's
  `OnnxOutputRanks`; names as tie-breaker only).
- **RED first.** A bge-m3-shaped manifest written *before* #496 is left uncorrected by the repair
  today; and both untested two-output name shapes (`[sentence_embedding, <tail>]`,
  `[token_embeddings, <non-embedding tail>]`) get a failing test before the fix.
- **Acceptance.** A pre-#496 bge-m3 manifest is corrected in place by the repair on next open, with
  no re-download; output selection is decided by ONNX rank with names as tie-breaker only; both
  previously-untested two-output name shapes are pinned; the distinctness guard from #501 still
  refuses a manifest whose two outputs resolve to the same tensor.
- **Gate command.** `dotnet test --filter "FullyQualifiedName~ModelDownloadPlanner|FullyQualifiedName~ManifestPooling" --nologo -v m`
- **Lane.** dotnet-engineer / Sonnet. **No collision** with any other WP.

### WP9 — small filed issues: #519 and #493

**Status: OPEN — G9 APPROVE "all".** **Wave 4, strictly last.**

- **#519** — `tests/AiRaccoon.Tests/TestData.cs:86,89`: remove the two optional Null-object defaults,
  make every caller pass its double explicitly — **83 call sites across 73 files**
  (`git grep -o "CreateMemoryStore(" -- tests ':!tests/AiRaccoon.Tests/TestData.cs' | wc -l`),
  mechanical. Done when
  `grep -rn "?? Null[A-Za-z]*\.Instance" src tests benchmarks` matches nothing outside
  intent-named single-file private helpers.
- **#493** — `PerformanceTools`' description stops enumerating phase names, **or** a test asserts
  every `SearchResults.PhaseNames` entry appears in the description. Prefer the first: it is the
  *derive-or-delete-the-list* invariant's own answer.
- **Found during this plan's research, unfiled** (*fix-what-you-find*):
  `docs/reference/logging-event-ids.md:53` says EventIds **517-519** exist on
  `BankMaintenanceHostedService`. They do not — its `Log` class declares 510-516 and 520-524 only.
  One-line doc fix; ride it into this PR rather than filing a seventh issue.
- **Acceptance.** No optional Null-object default remains on a `TestData` factory and the suite builds
  with every caller explicit; `PerformanceTools`' description no longer hand-lists phase names (or a
  test derives them from `SearchResults.PhaseNames`); `logging-event-ids.md:53` names only EventIds
  `BankMaintenanceHostedService.Log` actually declares.
- **Gate command.** `dotnet build` + `dotnet test --filter "FullyQualifiedName~Performance" --nologo -v m`;
  #519's proof is that the suite still builds, so this one **must** get a full-scope fast run.
- **Lane.** dotnet-engineer / Sonnet. **Collisions.** #519 touches 73 test files — run it when no
  other lane has an open PR adding tests, i.e. **late**, or it conflicts with everything.

### WP6 — ADR-0089 implementation: parts 1–3 this session

**Status: OPEN for 6a/6b/6c — G8 APPROVE, note _"3 now, 2 later - create plan for the next session
before starting"_. 6d and 6e are MOVED to session 5.** Wave 2, and **blocked on the session-5 plan
existing** (todo item 3).

**Which three, and why that trio.** **6a** (the `projects` table + registration write path), **6b**
(`project_id_token_get`, which mints *and* registers) and **6c** (the refusal of an unregistered id).
This is the smallest set that is coherent in production: 6c is the whole point of the ADR — the
accident it removes — but **6c without 6b is a trap**, because it refuses unregistered ids while
leaving no supported way to register one, so a new project becomes uncreatable. 6a is the substrate
both need. Shipping 6a+6b without 6c would be the opposite failure: new surface that changes nothing.
**6d** (CLI `project id generate|convert`) is convenience over a tool that already exists, and **6e**
(storage guidance + the `ai-raccoon.ignore` entry) is documentation — both are additive, neither is
load-bearing, and both are the right things to carry.

Sized in §Review (e), with the file list and the `6a → 6b/6c → 6d`, `6e ∥` ordering. **The largest
item in the session**; G8 asks whether all five parts run now or only `6a`+`6b`+`6c`.

- **Lane.** architect / **Opus** writes the implementation plan first; dotnet-engineer / **Sonnet**
  ×5 builds it. Reviewer: code-reviewer / Opus, per §How work lands.
- **Acceptance / Gate — deferred, explicitly.** The ADR defers acceptance criteria to the
  implementation plan, so
  **the architect's implementation plan defines the RED test, the acceptance criteria and the exact
  gate command for each sub-PR, and no `6x` PR opens until it does.** Deferring is fine here; silence
  is not. The one criterion fixed now, because it is the risk: **6c** lands its RED set first — a
  legacy id the bank knows keeps working with a warning, a legacy id it does not know is refused, an
  unregistered guidv7 is refused.

### WP10 — #455: the re-derived corpus, queries and parity golden — **MOVED to session 5**

**Status: MOVED — G10 APPROVE, owner's words: _"- move it to the next session"_.** It stays **LAST**
there. The scope, files, acceptance and gate commands below are complete and carry over verbatim into
`docs/work/2026-08-23-post-delta-5-plan.md`; nothing about them needs re-deriving, and §Review (g)'s
measurement (5 commits ahead of `a747da1a`, main +20 commits/137 files, **zero file collisions**) is
the evidence that waiting stays cheap. **The owner's S6b history rewrite moves with it.**

- **Scope.** Resume `task/pd3-455-public-benchmark-corpus` @ `ea174faf` (draft #499). Merge
  `origin/main` in first — **20 commits of drift, zero file collisions** (§Review (g)) — re-run the
  gates **in the foreground on a quiet machine**, regenerate the golden if they move, mark ready.
- **Files.** The 11 the branch already owns (§Review (g)): `RealWorldCorpus.cs`,
  `RealWorldQueries.cs`, `scripts/src/benchmark_corpus.py`, `reference-topk.json`,
  `CorpusFixtureGuardTests.cs` and six docs/tooling files.
- **Acceptance.** No `jsaa-*` id and no verbatim private prose survives in any of the three artefacts;
  `CorpusFixtureGuardTests` fails if one is reintroduced (seen failing); the parity and retrieval
  gates pass against the regenerated golden, and any metric that moved is recorded before/after.
- **Gate command.** `dotnet test --filter "FullyQualifiedName~ParityGateTests" --nologo -v m` — note
  `ParityGateTests` carries `[Trait(Speed, Nightly)]` (`ParityGateTests.cs:16-17`, class at `:19`), so
  it is **not** in any Fast lane and must be named explicitly. Then
  `dotnet test --filter "Category=Retrieval" --nologo -v m` (19 classes carry
  `TestCategories.Retrieval`, `TestCategories.cs:19`), plus
  `dotnet test --filter "FullyQualifiedName~CorpusFixtureGuardTests" --nologo -v m`.
- **Lane.** dotnet-engineer / **Sonnet** runs and regenerates; architect / **Opus** reviews any metric
  that moved before the golden is accepted.

Standing owner ruling: everything else finishes before the corpus moves under it. After it merges,
S6b (the history rewrite, #414) becomes the owner's to run over all three private-prose paths in one
pass.

---

## Sequencing — four waves (rev 2.0, WP5 dropped and WP10 moved out)

**Wave 1 — five code lanes plus two architect lanes, all at once.** Verified file-disjoint:

| Lane | WP | Files it owns for the wave |
|---|---|---|
| dotnet-engineer / Sonnet | **WP1** | `Embedding/EmbedDrainService.cs` |
| dotnet-engineer / Sonnet | **WP2** | `Ingestion/FileIngestor.cs` (+ its `SqliteMemoryStore` caller) |
| dotnet-engineer / Sonnet | **WP3** | `Maintenance/MaintenanceJobRunner.cs`, `Core/Metrics/*`, `Infrastructure/Metrics/*`, `MetricsConfigKeys.cs` |
| dotnet-engineer / Sonnet | **WP4** | `Embedding/EmbeddingService.cs`, `Cli/Commands/SettingsCommands.cs`, `Cli/Commands/DoctorCommands.cs` |
| dotnet-engineer / Sonnet | **WP8** | `Embedding/Download/ModelDownloadPlanner.cs`, `ManifestPoolingRepair` |
| architect / Opus | **session-5 plan** | `docs/work/2026-08-23-post-delta-5-plan.md` (new file) |
| architect / Opus | **WP7 desk half** | no source file |

No two of those touch the same file. The one shared *test* surface is `tests/…/TestData.cs`, which
nobody edits until wave 4.

**Wave 2 — WP6, serial, gated on the session-5 plan existing (G8).** `6a` (`MemorySchema.cs`,
`MemorySql.cs`, a new `Projects/` store) → then `6b` (`MemoryTools.cs`) and `6c` (`ToolGate.cs` /
`IMemoryAccessGuard`), which are disjoint from each other and may run in parallel once `6a` merges.
None of these files is opened by any wave-1 lane.

**Wave 3 — WP7's measured arms, alone on a quiet machine.** Not a file collision: a *measurement*
collision. See §Re-review.

**Wave 4 — WP9, strictly last.** #519 rewrites **83 call sites across 77 files**, so it conflicts with
every wave-1 and wave-2 PR that adds a test. #493 and the `logging-event-ids.md:53` doc fix ride with it.

**Shared-file map (rev 2.0).** `EmbedDrainService.cs` → WP1 only (WP5 is dropped, so it has no second
owner). `MaintenanceJobRunner.cs` + `Metrics/` → WP3 only. `FileIngestor.cs` → WP2 only.
`EmbeddingService.cs`/`SettingsCommands.cs`/`DoctorCommands.cs` → WP4 only. `ModelDownloadPlanner.cs`
→ WP8 only. `MemorySchema.cs`/`MemoryTools.cs`/`ToolGate.cs` → WP6 only, in wave 2.
`tests/…/TestData.cs` → WP9 only, in wave 4. **No two concurrent WPs touch the same file.**

---

## Owner-only items

| Item | The session prepares | Strictly yours |
|---|---|---|
| S6b (#414 rewrite) | Nothing new — part 3 shipped the runbook (#473) + `scripts/verify-history-scrubbed.py` | Lifting the push guard, running `filter-repo` over all three paths, re-planting refs, telling every session to re-clone. **Moves to session 5 with WP10 (G10); it sits behind #455 there** |
| 1.33.0 publish | Nothing — `VERSION` is already 1.33.0 on main | The production-environment approval on the publish job |
| `~/.claude/settings.json` soft-deny | The exact patterns, in #474 §5.2 | Editing the file. An agent must not touch it |
| #479 force-pushes | Nothing — the investigation is merged | Confirming or denying the IDE/shell hypothesis |
| The gate below | This plan and the form | **Answered 9/9 on 2026-08-23** — nothing outstanding |

---

## What session 4 deliberately leaves out (ask-if-simpler)

- **`IngestTimings`** — the profile's §7 five-phase ingest record. Ingest is **0.34 %** of the clock;
  a five-phase split of it buys nothing. Parked, and WP3 is the cheap first step instead.
- **Re-opening WP12-A (length-sorted batching).** Measured **negative** in #511 across six 150 s
  windows (never faster; ~30 % slower under lane contention, same-binary variance 2.8×). Closed
  unmerged — re-opened on an isolated machine or not at all.
- **`CodeChunker.VerifyAndShed`'s double concatenation** (`CodeChunker.cs:68,115-129`) — below the
  noise floor, its phase is 0.02 % of the clock. Tidy-up only. **A second embedding-model tuning
  round** — needs a measurement budget this session does not have.
- **`ToolGate`'s own optional-null dep** (`ToolGate.cs:14`, `IModelMigrationStore? migrations = null`).
  It is the same class of defect as #519 and WP6c opens that file anyway — fold it into WP6c rather
  than filing a third ticket.
- **A repo-wide private-prose scanner** and **further review-loop automation.** Unchanged from part 3.

**Moved out by the gate, not by this section** — these are scheduled, not abandoned: ADR-0089 **parts
4–5** (G8) and **#455 plus the S6b rewrite behind it** (G10) all go to session 5, and the session-5
plan (todo item 3) is where they land. **Dropped outright:** WP5 (G7 REJECT) — a ruling, not a park.

---

## Re-review — what the answers changed (architect, rev 2.0)

The plan promised *"architect re-reviews the plan before any gated WP opens."* Five findings; two
change a WP's design, and one changes the schedule.

**1. G6 shape (iii) needs far less than the plan implied — and G7's REJECT is why.** Rev 1.1 said
shape (iii)'s row count would force `IMaintenanceJob.RunAsync` → `ValueTask<MaintenanceJobResult>`
across **ten implementations in seven files**. Re-reading the runner, **half of #477 needs no job
change at all**: `duration_ms` is already computed there — `Stopwatch.GetTimestamp()` at
`MaintenanceJobRunner.cs:71`, `Stopwatch.GetElapsedTime(started)` at `:94` — so the histogram is a
recorder call on data the runner already holds. Only `job.<name>.rows` needs a count, and **#477's own
wording leaves the escape hatch open**: *"gauge, **where a job reports a count**"*. So WP3 has two
routes, and G7 decides between them:

- **Route (a), recommended — no interface change.** The gauge means *outstanding rows* and the runner
  reads it (or the job's existing `HasWorkAsync` query surfaces it). Touches `MaintenanceJobRunner.cs`,
  `MetricsConfigKeys.cs` and the recorder wiring. **Zero of the ten implementations change.**
- **Route (b) — the interface change.** Carries a real per-run count, and touches all ten.

**G7 makes route (b) actively awkward.** #477 names `code-reindex` as the example series
(*"rows embedded / still pending"*), but post-#517 `CodeReindexJob` **embeds nothing** — it enqueues
(`CodeReindexJob.cs:44-48`). Because G7 keeps both jobs, route (b) would add a count field that **two
of the ten jobs can never populate**, on the very job the issue names. Route (a) sidesteps that: the
*embedded* count for the hot path is already on EventId **1003** (`EmbedDrainService.cs:161-163`), and
the *pending* count is a query. **WP3's brief takes route (a); if the lane finds it cannot express the
series that way, it stops and reports rather than expanding into ten files.**

**2. WP3's collisions and order, re-stated.** With WP5 dropped, nothing else opens
`IMaintenanceJob` or its implementations this session, so **WP3 no longer waits on anything** and
moves from "after WP5" into **wave 1**. Under route (a) it owns `MaintenanceJobRunner.cs` and the
`Metrics/` tree, which no other WP touches. The rev 1.1 sequencing line "WP1 → WP5 → WP3" is void.

**3. WP7 must not run beside the other lanes — a measurement collision, not a file one.** G3 approved
a WP whose method is a fixed 150-second throughput window, into a session that now runs five code
lanes at once. **That is exactly what invalidated #511**: six windows under 5–7 concurrent lanes,
same-binary variance 2.8×, aggregate ~30 % slower on an unchanged branch. WP7's *desk* half (the ORT
1.29.0 / CoreML EP facts, ADR-0049, the draft skeleton) has no such constraint and runs in wave 1; its
**measured arms move to wave 3, alone**. Neither the plan nor the gate card said this, and it would
have produced unusable numbers.

**4. WP2 and WP4 are unchanged.** WP2 keeps both legs — the ancestor ignore root (G2) and #485's
`code_entries` prune (G9 "all") — in one PR on `IngestDirectoryAsync`, as recommended and approved;
the two RED tests and the no-regression leg stand. WP4 keeps both items in one PR: the rendering
choice (G4) and the EventId 428 `int` (ungated).

**5. Two scheduling consequences worth naming.** G10 moving #455 out and G8 deferring 6d/6e both
freed calendar, so session 4 is smaller at *both* ends than the plan sized it — wave 2 is unlikely to
be the squeeze G8 was protecting against. And **G8 inverts a dependency**: WP6 is now gated on a
document that does not exist, which makes the session-5 plan a wave-1 deliverable rather than a
closing chore. It is scheduled that way.

### First wave to dispatch — lane briefs

Five code lanes (dotnet-engineer / **Sonnet**; reviewer code-reviewer / **Opus**, never the
implementer) plus two architect / **Opus** lanes. Each lane's full brief is its WP section; the
load-bearing inputs are:

| WP | Files | RED test (seen failing first) | Gate command | Collides with |
|---|---|---|---|---|
| **WP1** | `Embedding/EmbedDrainService.cs` only (`DrainOnceAsync:115-121`; rewrite the class doc `:22-24`, which promises the opposite) | `EmbedDrainContinuousTests` (**new class — needs class-level traits**): fake `ICodeEmbedder` returns exactly `rowsPerRun` twice then 0; **one** `TryEnqueue`; assert `Drains` reaches 3. **No `TimeProvider`** — the service takes none. Red today: stops at 1 | `dotnet test --filter "FullyQualifiedName~EmbedDrain\|FullyQualifiedName~EventPump" --nologo -v m` | nothing |
| **WP2** | `Ingestion/FileIngestor.cs` `IngestDirectoryAsync` (`:131` load root, `:133` filter args) + its `SqliteMemoryStore` caller | `FileIngestorIgnoreTests` **(extend — exists at `tests/…/Integration/Ingestion/FileIngestorIgnoreTests.cs:18`)**: `skip/` row count 0. Plus `DirectIngestReplacesStaleChunksTests` (extend): 0 stranded `code_entries` after a directory re-ingest of a shrunk file | `dotnet test --filter "FullyQualifiedName~FileIngestor\|FullyQualifiedName~DirectIngest" --nologo -v m` | nothing |
| **WP3** | `Maintenance/MaintenanceJobRunner.cs`, `MetricsConfigKeys.cs`, recorder wiring. **Route (a) — do not change `IMaintenanceJob`** | `MaintenanceJobRunnerTests`: a completed job run records `job.<name>.duration_ms`; a job with outstanding rows records `job.<name>.rows`; a not-due job records **neither**. Red today: no series exists | `dotnet test --filter "FullyQualifiedName~MaintenanceJobRunner\|FullyQualifiedName~Metrics" --nologo -v m` | nothing |
| **WP4** | `Embedding/EmbeddingService.cs:310-311,434-436`, `Cli/Commands/SettingsCommands.cs:298,337`, `Cli/Commands/DoctorCommands.cs:73`, `docs/reference/logging-event-ids.md` | All three surfaces print the same phrase for one stored `0`; EventId 428's `Threads` state value is an `int`. Red today on both | `dotnet test --filter "FullyQualifiedName~SettingsCommands\|FullyQualifiedName~Doctor\|FullyQualifiedName~ThreadResolution" --nologo -v m` | nothing |
| **WP8** | `Embedding/Download/ModelDownloadPlanner.cs`, `ManifestPoolingRepair` | A pre-#496 bge-m3 manifest is left uncorrected by the repair today; both untested two-output name shapes fail before the fix | `dotnet test --filter "FullyQualifiedName~ModelDownloadPlanner\|FullyQualifiedName~ManifestPooling" --nologo -v m` | nothing |

Architect lanes: **the session-5 plan** (`docs/work/2026-08-23-post-delta-5-plan.md` — ADR-0089 parts
4–5, #455 LAST + S6b behind it, session-4 spillover) and **WP7's desk half**. Both are documents; both
block nothing in wave 1, and the first one **blocks wave 2**.

---

## Owner gate — **ANSWERED 9/9, 2026-08-23** (`docs/work/2026-08-23-post-delta-4-feedback.md`)

| id | Verdict | Owner's note | Effect |
|---|---|---|---|
| **G1** | APPROVE | — | WP1 OPEN, wave 1 |
| **G2** | APPROVE | — | WP2 OPEN, wave 1 (folded with #485 as recommended) |
| **G3** | APPROVE | — | WP7 OPEN — desk half wave 1, measured arms wave 3 |
| **G4** | APPROVE | — | WP4 OPEN, wave 1 |
| **G6** | APPROVE | *"3"* | Shape **(iii)** — #477 as written. WP3 OPEN, wave 1 |
| **G7** | **REJECT** | *"dont delete"* | **WP5 DROPPED**; the two jobs stay — standing ruling |
| **G8** | APPROVE | *"3 now, 2 later - create plan for the next session before starting"* | WP6 = 6a/6b/6c, wave 2, **blocked on the session-5 plan**; 6d/6e → session 5 |
| **G9** | APPROVE | *"all"* | #485 → WP2; #497+#504 → WP8; #493+#519 → WP9 |
| **G10** | APPROVE | *"- move it to the next session"* | **WP10 MOVED to session 5**, still LAST; S6b moves with it |

**The nine cards are spent.** They were the asking device; the evidence behind every one of them is
in §Review with file:line, and the verdicts are in
`docs/work/2026-08-23-post-delta-4-feedback.md`. What a later session needs is what each answer
*binds*, so that is what this records.

**G1 — APPROVE.** The embed drain re-signals itself while the backlog is non-empty.
`EmbedDrainService.cs` only; the 15 s poll (`BankMaintenanceHostedService.cs:79`) is not edited. The
bound stays `embedding.threads`; only the idle goes. Expected recovery: the **207 s (19.5 %)** of poll
gaps in a 1,061 s drain. Binding detail: the RED test uses **no clock** — the service takes no
`TimeProvider`.

**G2 — APPROVE.** Directory ingest resolves its ignore root like single-file ingest
(`ResolveIgnoreRootAsync`). Approved on the **corrected** basis: the 379 MB bin/obj claim was false
(`WatchDenySet` already excludes them, `FileIngestor.cs:397-400`); the real harm is ancestor ignore
rules going unapplied — e.g. `src/AiRaccoon/Models/vocab.txt`, 231,508 B, ingested as a memory
document. It is **ancestor resolution, not nested discovery**: an `ai-raccoon.ignore` *below* the walk
root still does nothing. Folded with #485 in one PR, as recommended.

**G3 — APPROVE, research only.** Three arms on #508's S3–S5 protocol: fp32 CPU, int8, CoreML EP.
**No production edit and no engine swap** until the owner rules on what it finds. Two facts that
de-risk it: the CoreML EP ships in the pinned `Microsoft.ML.OnnxRuntime` **1.29.0** osx-arm64 dylib
(`_OrtSessionOptionsAppendExecutionProvider_CoreML`, verified with `nm`), and the bundled *memory*
model is already int8 (`model_qint8_arm64.onnx`, ADR-0049 `:55`). ADR-0049 binds both arms: they
change the arithmetic path, so the parity golden and `MiniLmGoldenVectorTests` are downstream.

**G4 — APPROVE.** One rendering of the resolved thread count across `settings model show`, the
`settings model threads` confirmation and `doctor`, from one pair of helpers
(`ThreadCountDisplay`/`ThreadCountSource`). Recommended shape: `<value-or-"ORT default"> (<source>)`.

**G6 — APPROVE, note "3" = shape (iii).** #477 as written: `job.<name>.duration_ms` and
`job.<name>.rows` through `IMeasurementRecorder`, listed in `MetricsConfigKeys`, visible from
`memory_performance`. Shapes (i) (park it) and (ii) (EventId 525 + an interface change) are rejected
by that choice. **§Re-review finding 1 constrains how**: route (a), no `IMaintenanceJob` change.

**G7 — REJECT, owner's words: _"dont delete"_. A standing ruling.** `PendingEmbedJob` and
`CodeReindexJob` stay. The one-line `RunAsync` is the accepted shape for a job whose value is
`HasWorkAsync`, and the three guarantees in §Review (f) — the fingerprint reconcile
(`CodeReindexJob.cs:37-41`), the coalesced-signal recovery path
(`EmbedDrainServiceTests.cs:181`, ADR-0076) and the same-pass sweep ordering
(`AppRegistrations.cs:158-163`) — are why. **Do not re-propose without new evidence addressing those
three.** ADR-0091 stands unamended. The WP11-C follow-up is closed.

**G8 — APPROVE, note _"3 now, 2 later - create plan for the next session before starting"_.**
WP6 ships **6a + 6b + 6c** (registry table, `project_id_token_get`, unregistered-id refusal) — the
trio justified in WP6's own section: 6c is the value, 6c without 6b is a trap, 6a is the substrate.
**6d** (CLI) and **6e** (docs + `ai-raccoon.ignore`, *not* `.gitignore`) move to session 5. The
"before starting" clause is a hard precondition: **`docs/work/2026-08-23-post-delta-5-plan.md` must
exist before the first `6x` PR opens.** ADR-0089 constraints that still bind: the table goes in the
unconditional `Ddl` block with **no `CurrentVersion` bump** (stays 10), and 6c's compatibility rule is
"no registry row **and** no existing rows".

**G9 — APPROVE, note _"all"_.** All five filed issues ride: **#485** → WP2 (same method),
**#497 + #504** → WP8 (same file family), **#493 + #519** → WP9. #519 is 83 call sites across 77
files, which is why WP9 is wave 4 rather than wave 1.

**G10 — APPROVE, note _"- move it to the next session"_.** WP10 (#455) moves to session 5 entirely
and stays **LAST** there; the owner's S6b history rewrite moves with it and sits behind it. §Review
(g)'s measurement — 5 commits ahead of `a747da1a`, main +20 commits/137 files, **zero file
collisions** — is the standing evidence that waiting stays cheap, and it should be re-derived, not
re-quoted, when session 5 opens.
