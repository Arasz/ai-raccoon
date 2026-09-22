# Lane E — .NET language quality & reliability (project-scope review, base 5bca1900)

Worktree: `.ai-badger/worktrees/psr-e-dotnet-quality` (read-only; no tracked file modified, nothing committed).
Product runs used `ai-raccoon --data-root docs/work/2026-09-22-project-scope-review/e-dotnet-quality/<case> …` only (scratch roots; the
user's bank was never read or written). Harnesses live in `docs/work/2026-09-22-project-scope-review/e-dotnet-quality/` and reference
the reviewed build's own output dlls. Every transcript below is from this session.

Build measurements in this worktree:
- `dotnet build --nologo` → `Build succeeded. 0 Warning(s), 0 Error(s)` (exit 0);
  `dotnet build src/AiRaccoon/AiRaccoon.csproj -t:Rebuild` → `0 Warning(s)` with `/warnaserror+`,
  `Nullable=enable`, `AnalysisLevel=latest`, `EnforceCodeStyleInBuild=true` (`Directory.Build.props:6-12`).
- The 0-warning claim is **not** bought by suppressions: **zero `#pragma warning disable` and zero
  `SuppressMessage`** in `src/`. The only disabled diagnostics are `NoWarn=NU1901;NU1903` (repo-wide, NuGet
  audit), `CA2255` in `AiRaccoon.Core.csproj:7`, and `dotnet_diagnostic.IDE0055.severity = None` plus one
  `IDE0011` carve-out in `.editorconfig` — each with a recorded reason. 86 null-forgiving `!` uses across
  47.3k production lines; I read all 86 and found no dangerous one (required System.CommandLine arguments,
  `JsonElement.GetString()` after a kind check, `Process.Start(...)!`).

## Findings

### F1 — a corrupt bank file makes `doctor` exit 15, contradicting the code's own definition of 15, the derived doctor exit-code table, and the `NoBank` guard's stated purpose [MEASURED]
**Severity:** MEDIUM
**Evidence:** transcript, garbage at the resolved bank path, then the CLI's own doctor verb:
```
$ printf 'this is not a sqlite database at all, just text' > docs/work/2026-09-22-project-scope-review/e-dotnet-quality/corrupt/memory.db
$ ai-raccoon --data-root docs/work/2026-09-22-project-scope-review/e-dotnet-quality/corrupt doctor
stdout: (empty)      EXITCODE=15      stderr: ai-raccoon: SQLite Error 26: 'file is not a database'.
```
Mechanism (traced): `DoctorCommands.RunAsync` wraps only `OpenBankReadOnlyAsync` in `catch (SqliteException)`
(`DoctorCommands.cs:41-51`); Microsoft.Data.Sqlite defers "file is not a database" to the first statement,
which is `SchemaDoctor.DiagnoseAsync` (`DoctorCommands.cs:55`) — outside that catch. The exception escapes to
the one catch-all in `ConfigCommands.cs:168-172`, which returns `ExitCode.InvalidArgument`. `ExitCode.cs:44`
defines 15 as "A CLI verb's own argument failed validation … so a script can tell 'you mistyped' from 'the
bank/server is broken'" — a corrupt bank is the archetypal "bank is broken". The project's own derived
contract says doctor cannot return 15: `docs/how-to/configure-ai-raccoon-server.md:377-385` lists doctor's
codes as exactly {0,1,2,19,20,22,24}, asserted exact by
`HowToExitTableTests.DoctorTable_ListsExactlyTheDoctorReachableExitCodes`. No test covers "bank path exists
but holds non-SQLite bytes" (`grep -n "not a database" tests/` hits only bank-busy classification units),
which is why it shipped green. Smallest fix: wrap `DoctorCommands.cs:53-64` in a `catch (SqliteException)`
returning `FailedToOpenEncryptedBank` (2 — the doc's "could not be opened read-only").

### F2 — the same catch-all absorbs `OperationCanceledException`: Ctrl-C is reported as an error and exits 15 [MEASURED]
**Severity:** MEDIUM
**Evidence:** `ConfigCommands.cs:168` is a bare `catch (Exception ex)` — read in the file, not inferred from a
grep. Transcript, SIGINT at t=2.5 s while the CLI was inside the auto-start acquire:
```
$ ai-raccoon --data-root docs/work/2026-09-22-project-scope-review/e-dotnet-quality/data5 --port 7911 settings maintenance list
stdout: (empty)      EXITCODE=15
stderr: info: AiRaccoon.Hosting.Proxy.BackendLauncher[633]
              ai-raccoon: starting the backend on port 7911
        ai-raccoon: The operation was canceled.
```
The codebase's convention is the filtered form — 14 catches carry `when (ex is not OperationCanceledException)`
(`SettingsCommands.cs:181`, `DoctorCommands.cs:252/303`, `ProxyForwarder.cs:59/81/94`, …) — and the
process-exit-code decision is the one place that does not. Costs: a script cannot tell an interrupt from a
typo (it gets the "you mistyped" code), and the user is told a cancellation is an error. The interrupt also
leaves the freshly auto-started backend running (observed PID 187 alive on port 7911, reparented to PID 1)
with nothing said about it — see F3. Smallest fix: `catch (OperationCanceledException) when
(Token.IsCancellationRequested) { return 130; }` ahead of the catch-all.

### F3 — a successful one-shot CLI command leaves an orphaned `serve` backend running for up to 4 hours, started with no `--idle-timeout` [MEASURED]
**Severity:** MEDIUM
**Evidence:** `BackendLaunchArguments.ServeArguments` (`BackendLaunchArguments.cs:46-59`) builds
`--data-root … --install-scope … serve --port N` and never passes `--idle-timeout`, so the child runs under
`DefaultOptions.IdleTimeout = TimeSpan.FromHours(4)` (`src/AiRaccoon/Setup/DefaultOptions.cs:14`); the launcher
"never kills, signals or terminates the backend — lifetime belongs to IdleWatchdog alone"
(`BackendLauncher.cs:12-16`). Transcript after a *successful* read (exit 0, 6.5 s):
```
$ ai-raccoon --data-root docs/work/2026-09-22-project-scope-review/e-dotnet-quality/data6 --port 7912 settings maintenance list
CLI exit: 0    stderr: info: … ai-raccoon: starting the backend on port 7912
--- backend processes alive after the CLI exited ---
2433  1  …/AiRaccoon --data-root docs/work/2026-09-22-project-scope-review/e-dotnet-quality/data6 --install-scope user serve --port 7912
```
It was still alive and serving 25 s later (POST /mcp → 401 token refusal, i.e. a working endpoint).
The user's only disclosure is one `info:` line saying a backend *started*; nothing says it outlives the
command, and nothing names a stop command. What the orphan then runs unattended is the point: per the CLI's
own help the sweep reaper is ON by default, and the extractor, watch, embed drain and metrics flusher are all
live in it. ADR-0075:64-66 ratified auto-*start* and says nothing about lifetime; `docs/adr/0075` has no
occurrence of "idle"/"lifetime"/"terminate". A read-only command therefore buys up to four hours of background
writes — including deletions — on the user's bank. Smallest fix: pass a short `--idle-timeout` for
CLI-acquired backends, or stop the backend the CLI itself started when the command returns.

### F4 — a mistyped `--data-root` on any settings verb silently *creates* a bank there; the `NoBank` guard that exists for exactly this is doctor-only [MEASURED]
**Severity:** LOW
**Evidence:** fresh empty directory, read-only verb:
```
before: []
$ ai-raccoon --data-root docs/work/2026-09-22-project-scope-review/e-dotnet-quality/data7 --port 7913 settings access show
exit: 0     stdout: default: rw     stderr: … starting the backend on port 7913
after: ['mcp-token', 'memory.db', 'memory.db-shm', 'memory.db-wal']
```
`ExitCode.NoBank`'s doc says the code exists "so a wrong `--data-root` cannot read as a healthy bank"
(`ExitCode.cs:64-66`) — that is true for `doctor` only. Every other settings verb is routed to the
auto-started server (ADR-0075 §5.1; `CliWriteOptOuts.cs:16` exempts only `encryption`), and the server's first
open runs `MemorySchema.EnsureAsync`, minting a full bank plus token file in the directory the typo named.
Cost: a typo leaves bank files behind at a path the user believes does not exist (and, per F3, a server on
it), and the next run against that path looks healthy. Smallest fix: refuse (or warn loudly on stderr) when
the CLI is about to auto-start a server against a data root whose bank does not exist.

### F5 — `WatchScanGuard.Cancel`/`CancelAll` can throw `ObjectDisposedException`: the in-flight entry's CTS is disposed concurrently with the cancel it is called on [MEASURED]
**Severity:** MEDIUM
**Evidence:** scratch harness `docs/work/2026-09-22-project-scope-review/e-dotnet-quality/harness` (200,000 iterations of
`Run(…, _ => Task.CompletedTask)` immediately followed by `Cancel`/`CancelAll`, referencing the reviewed
build's `AiRaccoon.Infrastructure.dll`) — reproduced twice, with stacks:
```
FIRST ObjectDisposedException stack:
   at System.Threading.CancellationTokenSource.Cancel()
   at AiRaccoon.Infrastructure.Watch.WatchScanGuard.Cancel(String projectId, String path) …WatchScanGuard.cs:line 74
iterations=200000 ObjectDisposed=4 otherExceptions=0 joined=200000
FIRST CancelAll ObjectDisposedException stack:
   at System.Threading.CancellationTokenSource.Cancel()
   at AiRaccoon.Infrastructure.Watch.WatchScanGuard.CancelAll() …WatchScanGuard.cs:line 88
iterations=200000 CancelAll-ObjectDisposed=9
```
Mechanism: `Cancel`/`CancelAll` capture the entry under `_gate`, release the lock, then call
`entry.Cts.Cancel()` (`WatchScanGuard.cs:66-75`, `:78-90`) — while the scan's completion path
(`Complete`, `:107-131`) removes the entry and calls `entry.Cts.Dispose()` (`:117`) after its own lock
release; a `Cancel` that captured the entry first then cancels a disposed source. Both entry points are the
product's real callers: `WatchPipeline.UnregisterWatch:124` (from `WatchService`'s `memory_watch_remove` and
the stale-registration reconcile) and `WatchHostedService.StopAsync:97` → `WatchCatchUp.CancelAllScans()`.
Measured hit rate is ~2–5e-5 per racing call (it needs the cancel to land inside the completion's disposal
window), so production frequency is low — but the consequences are not: in `StopAsync` the throw skips
`_eventSource.StopAll()` (`:98`) and `base.StopAsync` (`:105`, which awaits the pipeline loop) and surfaces on
the host's shutdown path, and in `UnregisterWatch` it skips `Unregistered?.Invoke` (`:125`), leaving
`WatchHostedService._active` holding the key — the exact D-1 invariant ("a remove-then-re-add never reads as
continuously active") the removal choke point exists to keep. Smallest fix: dispose the CTS inside the same
`_gate` critical section that removes the entry, or guard `Cancel`/`CancelAll` with
`catch (ObjectDisposedException)`.

### F6 — the #636–#640 "one line, no stack for transient contention" convention is applied to 8 call sites but not to the background pass failures that hit the same lock [READ]
**Severity:** LOW
**Evidence:** `ExceptionExtensions.IsBankBusy` (`ExceptionExtensions.cs:10-19`) is used by the extraction
service (3), the tool filter, the best-effort search-quality write, the rating bump, the VACUUM swallow and
the metrics retry. The drain pass instead logs the raw exception at Warning for the same condition —
`EmbedDrainService.DrainOnceAsync`'s `catch (Exception ex)` → `pass.Failed(ex)` +
`reporter.PassFailed(logger, corpus, ex)`, whose declaration is
`EmbedDrainReporter.cs:80-81` `[LoggerMessage(EventId = 1005, Level = LogLevel.Warning, Message = "Embed drain
pass failed for {Corpus}")] … (ILogger logger, EmbedCorpus corpus, Exception exception)`; likewise
`SweepHostedService.cs:146-149` → `ProjectFailed` (533, Warning + exception) and
`MaintenanceJobRunner.cs:135-141` → `JobRowsCountFailed` (528, Warning + exception).
Name the wrong outcome: a BUSY/LOCKED convoy on the bank while rows are pending produces a full stack trace
(and, for the drain, a failed `embed.drain` OTLP span) at the poll cadence, where #639's own rationale —
"a raw SqliteException with a stack for it is the noise class the tool path's 912 already dropped" — says the
transient case should be one line. I did not reproduce a live convoy, so this is traced, not observed; the
pass does genuinely fail and retry, which is the counter-argument, and the owner may rule the stack wanted
here. Smallest fix: classify busy first in these three catches (`when (ex.IsBankBusy())` → the deferred line),
keeping the exception for genuine faults, as `SqliteSearchQualityService.cs:36-43` already does.

## Still open
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

## Grade mix
MEASURED 5 (F1, F2, F3, F4, F5) · READ 1 (F6) · INFERRED 0 · UNVERIFIED 0.

## Owner questions
1. **F1** — for a bank that exists but is not a SQLite database, should `doctor` return 2
   (`FailedToOpenEncryptedBank`) to match its documented table, or should there be a distinct
   infra-failure code (i.e. is `InvalidArgument` ever allowed to mean "the bank is broken")?
2. **F2** — is exit 130 the wanted interrupt code for one-shot CLI commands, or should Ctrl-C keep a distinct
   ai-raccoon code (and must the message also say nothing was changed)?
3. **F3** — should a CLI command stop a backend *it* started when it exits, or is leaving it to the 4-hour
   watchdog intended (and if intended, is a short `--idle-timeout` the wanted middle ground)?
4. **F4** — should any verb other than `doctor`/`encryption` refuse to run against a data root with no bank, or
   is silently minting one the accepted cost of "the server auto-starts"?
5. **F5** — is an `ObjectDisposedException` guard in `Cancel`/`CancelAll` acceptable, or should the CTS
   disposal move inside the entry-removal critical section (which is the fix that removes the window)?
6. **F6** — does the #636–#640 "one line, no stack" rule apply to a background *pass* failure, or only to
   per-row best-effort writes (in which case the three sites stay as they are)?
