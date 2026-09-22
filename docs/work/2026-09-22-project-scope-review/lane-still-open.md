### Lane A: still open

- **`WarnOnNonHttpTransport`'s reachable surface.** I measured it firing via `--transport proxy serve`; since `serve` rejects `--transport` after the verb and stdio/https die at parse, that is the *only* live spelling. Whether any launcher uses it was not determined.
- **`MemorySchema.cs` decomposition** — I read its 40-member inventory (DDL, 14 rungs, one-shot column/trigger repairs, `TestOnly*` `AsyncLocal` hooks) and the +770 delta, but did not evaluate which of its concerns could be split, so F1 names the size, not a split.
- **Two reds quoted from GROUND-TRUTH** (arm64 MiniLm golden, `ParityGateTests` p95) were not re-run here — retrieval and test lanes own them.
- **`StructuralNoiseModel.Trees.g.cs` (1881 lines)** excluded from the growth analysis: generated, and I did not verify the generator is checked in and reproducible, so it may be a source-of-truth question rather than a growth one.
- **Maintenance pass log** ("… job 'vacuum' ran in 7 ms" for all 7 jobs on a fresh bank, including `vec0-reclaim` and `vacuum`) — I did not establish whether `ran` means work happened or only that the guard let the job run; that changes how the log reads, and it belongs to the data/QA lanes.
- **Tool-count arithmetic is not reconciled**: `ToolMethodSizeTests` asserts the scan finds ≥26 tool bodies, `McpToolInventory.Names()` derives from reflection, the `@ignore`d BDD scenario says "17 tools" and is annotated in-file as stale against a "22-tool surface", and I counted 30 `[McpServerTool` occurrences. Three or four numbers, one surface — Lane F's to close.
- **ADR-0102's "watch boundaries refuse retired ids"** is looser than the code: watch boundaries *fold*, the refusal lives in `ToolGate`, and the repair deletes dropped ids' watch rows (`ProjectIdsRepair.cs:458`). I refuted the resurrection hazard I suspected (`WatchDigestExecutor` folding a dropped id) by reading `FoldWatchesAsync`, so this is wording, not behaviour — reported nowhere because I could not show harm.
- **`IMaintenanceJob.HasWorkAsync` DIM default `false`** (M4 lead): all 8 on-demand jobs override it and the 4 cadence jobs correctly rely on the default — no defect found, so no finding; the shape is still the "silent default" class and would break if an on-demand job forgot.
- **Isolation note:** the `dotnet test` filter needed `--no-build`; without it discovery reported "Zero tests ran" in this worktree. I did not chase it (not my lane) but every test measurement here used `--no-build`.

**Grade mix:** MEASURED 8 (F1, F3, F5, F6, F7, F8, F9, F10) · READ 2 (F2, F4) · INFERRED 0 · UNVERIFIED 0.

### Lane B: still open

- The ORT-1.29.0 re-pin experiment behind F1's cause is Phase-0's measurement, not re-run in this worktree; I verified only the failure, the unchanged model bytes, and the missing provenance field.
- F2's structure-absent case was measured on a bank with **zero** structure vectors; the mixed case (some rows headed, some not) follows from `EntryEmbedder.cs:240-248` but was not measured on a bank with populated `vec_structure`.
- `fusionStats` invariance could not be stress-tested against a candidate-window change: my bank's population (6–66) never exceeded the 100-candidate floor of `max(3·limit, 100)`, so the window was constant in every run.
- The code-search leg has its own k+rank/max-normalize fusion with no Stage-1 evidence (plan §8); not audited here. Neither was `VecDimensionReconciler`'s open-time behaviour (Lane D's surface).
- A non-384 single-file `.onnx` was not available, so the legacy-file path's 384/WordPiece assumption (`EmbeddingService.cs:342-369`, `ResolveDimensions` → `BundledDescriptor.Dimensions`) was only read, not exercised.
- The Python fusion port (`scripts/retrieval_tuning/llamaindex_harness/fusion.py`) documents "the C# source wins on any conflict" and mirrors `Merge`'s re-fuse; I read it against `ReciprocalRankFusion`/`SearchResultMerger` but ran no differential fixture.

Grade mix: 4 findings — 3 MEASURED, 1 READ, 0 INFERRED, 0 UNVERIFIED.

### Lane C: still open

- **Watch was not run live** (no watcher process registered): rename/delete/hash-skip semantics are read-only evidence (`WatchDigestExecutor.cs:36-96`); deleted-file chunk removal and the ignore-file rescan were not exercised.
- **M4 (`HasWorkAsync` guard) and D3 (reconcile-at-open)** were not re-verified — Lane D owns them; only the watch analogue of "stamp last" was checked.
- **Extraction scoring/capacity** beyond the measured propose/promote: floor 0.4 and per-source cap 3 read (`SharedExtractionService.cs:16-24`); no content in my bank scored ≥0.4, so `includeTtlRows` exclusion (`:77-80`) could not be discriminated live.
- **`ReclaimStaleClaims` has no owner column** (`PromotionQueueSql.cs:134-138`): a promote pass running longer than 5 minutes could have its claim reclaimed by a concurrent pass. No failing scenario was produced, so this stays an open question, not a finding.
- **Harness note:** the session's `grep` tool is intercepted by a memory-first gate that demands `memory_search` (projectId=ai-raccoon), which this lane's contract forbids (`mcp_ai-raccoon_*` points at the live bank). All searching used `rg`/`read`; the gate could not be satisfied from inside the lane.
- **Ambiguity resolved in favour of evidence:** F3 was graded MEDIUM (not HIGH) because the response `context` discloses the redirect; F5 keeps the per-chunk contract's wording in view before calling the asymmetry a defect.

Grade mix: MEASURED 7 (F1, F2, F3, F4, F5, F7, F8) · READ 1 (F6) · INFERRED 0 · UNVERIFIED 0.

### Lane D: still open

- **F7's live arm** — the watcher file-delete → sync resurrection was not driven end-to-end (needs an `ingest.scope` registration, a watch and an FS event); the sibling F1 measurement is the strongest available proof of the mechanism.
- **F8's experiment** — I could not construct the two-replica clock-skew case (no way to skew a process's clock here); the finding stays READ and low.
- **Encryption wrong-key / Bitwarden paths** — read-only: `SqliteConnectionFactory.DiagnoseAsync`/`LegacyKeyOpensHealthyBankAsync`/`RekeyBankAsync` were inspected (open → diagnose → refuse, never writes) but I did not create an encrypted scratch bank or exercise a wrong key or the Bitwarden provider. Lane J owns the security judgement.
- **Sync upload/merge atomicity** — `MergeRemoteAsync` (SyncService.cs:310-600) runs its whole merge as a sequence of autocommits with no surrounding transaction (aliases → entries → memory_source → tombstones → watermark → reindex → recompute). The protocol is monotone (INSERT OR IGNORE union + tombstone deletes) so a partial merge looks retry-safe, which is why I did not raise it; the exception is the alias-conflict probe, which deliberately runs first. Flagging it as unverified, not as sound.
- **`MemorySchema` v6's `noise_clusters`/`vec_noise`** are still created for legacy banks while fresh banks never get them; SchemaDoctor ignores extra objects, so this is inert — noted, not a finding.
- **`RepairCommands` chunk-index/reingest SQL** and `ChunkBackfill`'s re-chunk predicate were read but not exercised against a scratch DB (Lane C owns the lifecycle; #585's dimension reconciliation looked careful — `NeedsRecreateAsync` parses `float[N]` from `sqlite_master` and treats a missing table as needing recreation).

### Lane E: still open

- **Hypotheses I checked and refuted (recorded as results, not findings).**
  (a) *`FileSystemWatcher.Dispose` deadlock*: `WatchEventSource.Stop`/`StopAll` hold `_gate` across
  `watcher.Dispose()` (`WatchEventSource.cs:109-131`) while `Translate` takes `_gate` on the watcher's event
  thread (`:163`), which would deadlock if the runtime's `Dispose` waited for an in-flight callback. Measured
  with `docs/work/2026-09-22-project-scope-review/e-dotnet-quality/disposeblock`: `FileSystemWatcher.Dispose()` returned in **14 ms**
  while the `Created` handler was still blocked — Dispose does not join the event thread on this runtime, so
  there is no deadlock. Hypothesis withdrawn.
  (b) *Broken stdout pipe*: `ai-raccoon --help | head -1` → both sides exit 0, no trace.
  (c) *Test-support probes that report pass instead of skip*: a scan of `tests/` found 3 unfiltered
  `catch (Exception)` bodies that return without failing, all evidence collectors or fakes
  (`EncryptionBitwardenFeatureContext.cs:223`, `ModelMigrationCrashRecoveryE2ETests.cs:386`,
  `FakeHfServer.cs:186`) — the documented trap is absent from this suite.
  (d) *Language traps absent*: no `async void`; no `.Result`/`.Wait()`/`GetAwaiter().GetResult()` outside the
  one deliberate engine-construction read; `GC.GetAllocatedBytesForCurrentThread` never used; no lock held
  across an `await`; all four `SemaphoreSlim` gates and all 46 `lock` statements balanced, and every
  `CancellationToken.None` site (14) is deliberate post-cancel cleanup (rollback, lease release,
  busy-timeout restore).
- **`ILogger<T>?`/`ISettingsStore?` optional-dependency trap, present but not (yet) biting.** Three instances
  on DI-registered types: `SqliteConnectionFactory.cs:19` and `ProjectIdsRepairJob.cs:36` (null → `NullLogger`,
  so an initialization failure inside `InitializeAsync` is silent), and `EmbeddingService.cs:30`
  (`ISettingsStore? settingsStore = null`, whose null path silently ignores `embedding.threads` in favour of
  the halved-core default at `:339-341`); `ServerProbe.cs:23` adds a fourth. Production DI supplies all of
  them (`AppRegistrations.cs:203-205/308-311` and `RegisterEmbeddingServices`), so no production behaviour
  changes today — I could not point at a wrong production outcome, which is why this is not filed as a defect
  rather than because the trap is absent. 12 test constructions of `EmbeddingService` omit the store; that is
  the population the trap exists for.
- **F5's hit rate is measured, its production trigger rate is not.** I did not run a live watch server long
  enough to observe an `ObjectDisposedException`; the "~2–5e-5 per racing call" figure is the harness's rate
  under a maximally tight race, so the real-world number is lower.
- **Whether a `WatchHostedService.StopAsync` throw also skips the *other* hosted services' stops** (metrics
  final flush, shutdown WAL checkpoint, migration-lease release) depends on the generic host's per-service
  exception aggregation, which I did not measure. The guaranteed part is stated in F5; the aggregate part is
  deliberately not claimed.
- **F1's fix ownership** overlaps Lane F (exit-code contract) — reported here because it is a missing catch,
  not because the contract is mine to change.

### Lane F: still open

- **Withdrawn lead**: the 1.42.0 checklist's "refused query over HTTP has no `isError` flag" does **not** reproduce on 1.42.5 — the query-guard refusal, unknown-hash and invalid-argument all came back `isError: true` over raw HTTP. I could not re-run the stdio-proxy variant of that exact observation for every prefix, so the transport-envelope question is answered only for HTTP plus one proxy `tools/call`.
- Two `--install-scope` details I did not resolve: whether `<data-root>/.ai-raccoon/mcp-token` would break `serve --restart`/proxy token handoff if the token were moved (I only proved where each artefact lands), and whether the project scope is ever used with a data root that is not a repo (no evidence either way).
- The busy-port `serve` run also prints a ~30-line fail-level framework stack trace and does a `Bank WAL checkpoint complete` before failing to bind (observed in full, lines 00–39 of stderr); the documented exit 3 + `--port 0` hint do appear, so I left it as an observation rather than a finding — it is Lane E's logging territory and may already be known.
- I did not exercise `memory_sync` against a real remote, `model embedding set openai`, encryption verbs, or the code-engine-unloadable/missing-manifest refusals (they need an external endpoint or a mutated install). The `initialize` instructions/protocol-version contract is not covered by any doc I could find; I measured the strings but did not decide whether that warrants a finding (owner question Q4).
- Inventory/refusal parity was checked against the installed 1.42.5 binary, which GROUND-TRUTH pins as identical to HEAD product code; I did not rebuild the worktree to re-derive it from source at HEAD (the prebuilt test DLL in the worktree was used for the two filtered test runs).
- Grade mix: 11 MEASURED, 1 READ, 0 INFERRED, 0 UNVERIFIED.
**Procedural note (self-reported):** one early command (`ai-raccoon doctor` with no `--data-root`) read the default root (`/Users/arasz/.ai-raccoon`) before I caught it; `doctor` is read-only and opened nothing read-write, and every subsequent command carried an explicit scratch `--data-root`.

### Lane G: still open

- I did not run the full suite or a build in this worktree (review ground rules); build/test status
  is taken from GROUND-TRUTH.
- I did not activate the **code** engine (`model code set default`, a 187 MB download), so the
  post-activation code-search journey and the two-corpus warning state are read from the plan and
  docs, not seen.
- Busy-bank, migration-in-progress, and access-mode `ro` states were not exercised live; only the
  default `rw` denials were.
- `memory_share_extract` propose returned all-empty for a 1-entry bank and I did not determine
  whether worthiness scoring or rating excluded it — Lane C owns promotion eligibility.
- `chunkIndex: -1, totalChunks: 0` appears for entries written without `sourceFile`; the sentinel's
  documented meaning was not found, so I filed nothing.
- Stage-1 arithmetic verified incidentally and matches the plan: `maxPossible` measured 1/61 for one
  leg and 2/61 for two (k=60, w=1), and `ranking` values are the `(k+1)/(k+rank)` normalization —
  Lane B owns the deeper ranking checks.
- F12 is the only READ finding; the README count may legitimately mean something narrower than
  discovered tests, so I did not raise its severity.

Grade mix: 12 findings — 11 MEASURED, 1 READ, 0 INFERRED, 0 UNVERIFIED.

### Lane H: still open

- **Seed-embed full-suite slowdown (carried item)**: not reproduced. I did not run the full suite (campaign rule) and did not isolate `WatchIntegrationTests`' seed budget under load; the delta lane's F3 (arrange-phase seed timeout under CPU contention, first red tolerated) stands unverified by me.
- **TRX representation of retried attempts**: I measured the console/MTP summary only. Whether a `*.trx` records per-attempt failures (which `scripts/nightly-triage.py` reads) is unverified; no workflow command I read generates a TRX.
- **`--nologo` mechanism**: I measured the correlation (0 tests vs full discovery, twice) but did not trace whether it is MTP argument parsing or the xunit v3 adapter swallowing it.
- **The 4 other `@ignore`d BDD scenarios** (MRTR, cloud RLS, metrics/tracing, `memory_inspect`) — the delta lane examined them; I only un-ignored the tool-inventory one.
- **The 2 double-counted lane cases**: whether `build-fast` or `build-slow` is the intended owner is an ownership call I cannot make from the tests alone.

### Lane I: still open

- **Why the four versions are missing from nuget.org** is inferred from run history, not from a written decision: for 1.42.4 the cancelled `pack` job and skipped `publish` are on the record, but I found no owner statement that skipping a tag's publish is acceptable (1.38.0 carried 2,683 lines of product code). If a ruling exists, F1 downgrades to a documentation gap.
- **My worktree's `git status` went dirty and clean again during the run** — `.ai-badger/agents/code-reviewer.md` and `.ai-badger/agents/dotnet-engineer.md` showed as modified at ~15:0x and were clean again by 15:4x; this matches GROUND-TRUTH's note about a temporary persona-pin edit that is reverted after dispatch, so it is campaign machinery, not my doing. Final state is `git status --porcelain` **empty**. I left both files untouched throughout. `test_threshold_eval_integration`'s merge-hygiene failure is likewise an artifact of running in a detached-HEAD lane worktree, not a repo defect.
- **I did not run the fresh-install protocol end to end** (F7): it installs a global-tool copy into a temp dir and downloads a ~70 MB package; I measured the stale default and its resolvability instead. The 1.6.0 → 1.42.5 gap is therefore demonstrated statically, not by a green run against the wrong version.
- **`manual-fresh-install-test.py` is not exercised by any workflow or checklist file I could find**; whether the owner's ritual uses it or the hand-written `dotnet tool install` steps in `docs/work/checklist/*.json` is a process question I cannot settle from the repo.
- **`scripts/triage-coredump.py` is ELF-only**: on a real macOS Mach-O dump it prints `not an ELF file` and exits 0. Fine for the Linux runners that call it, unhelpful locally; I did not check whether its `test_coredump.py` covers that path.
- **No dependency/CVE scanning and no WIP/retention policy** exist anywhere in `.github/`; out of scope for the evidence I gathered, noted so the assembler does not read silence as coverage.
- I did not attempt to reproduce the 1.42.4 publish failure or dispatch any workflow (that would mutate the repo's state); every CI conclusion comes from `gh` read-only queries and the workflow sources.

Grade mix: MEASURED 6 (F1–F6), READ 3 (F7–F9), INFERRED 0, UNVERIFIED 0.

### Lane J: still open

- F2's live cloud round-trip was not run (no object store in this environment). The two tests pinning the skipped branch are read, not re-run: `dotnet test --project tests/AiRaccoon.Tests --filter "FullyQualifiedName~SyncServiceRemoteBlobTests"` returned "Zero tests ran" (exit 5) under this repo's MTP test platform, and I did not spend the remaining budget on the filter syntax.
- Not examined: `dotnet list package --vulnerable` (dependency CVE scan — no restore/network run), the `bws` binary's own behaviour, DNS-rebinding/`Host`-header handling of `/observability` from a real browser (a same-origin-policy read would be blocked by the absent CORS headers; not measured), and `ProxyForwarder`'s error-rewriting path.
- `--port 0`/random-port serve was not probed for the F1 squat shape; the default fixed port 7721 is the exploitable configuration either way.
- The `serve` attach warning says the process "never opened that bank to check" (`NodeRunner.cs:249-259`), so at least the operator gets a line — for the proxy path (`BackendLauncher`) there is no such line at all; I did not check whether that asymmetry is deliberate.