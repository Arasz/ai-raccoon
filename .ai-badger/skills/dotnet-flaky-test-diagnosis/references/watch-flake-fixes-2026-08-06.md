# the tool watch-flake fixes — 2026-08-06

Two watch tests cost repeated full-suite re-runs (~3 min each). Both diagnosed to a
distinct root cause and fixed. This is the concrete transcript behind the
`dotnet-flaky-test-diagnosis` skill.

## The symptoms

- `WatchPipelineTests.Tick_EventsForMultipleFiles_EachDigestedOnce` — `Ingested.Count`
  was 1, expected 3. Failed 2/2 full-suite runs, passed 2/3 isolated.
- `WatchIntegrationTests.DeletedDirectory_Cascades_RemovesChunksAndFingerprintsOfNestedFiles`
  — failed on PRISTINE origin/main once (1/3 full-suite), passed 3/3 isolated. A
  different watch-integration test (`RenameOntoExistingPath_KeepsOnlyTheIncomingContent`)
  flaked on the next run — different test, same class.

## Diagnosis path

1. Full-suite fail → isolated re-run of each → both passed isolated → flake, not
   regression.
2. Clean-main check: `DeletedDirectory_Cascades` failed on pristine main too → proves
   pre-existing, NOT the branch's fault (branch had zero watch-pipeline changes).
3. Read the code: the failing unit test enqueues 3 file events, one tick runs them
   through `WatchScheduler.RunBatchAsync` which fires `Task.Run` per job (concurrency 4).
   The jobs all call `FakeMemoryStore.IngestFileAsync` → `Ingested.Add(...)` on a PLAIN
   `List<>`. Concurrent `Add()` on an unsynchronized List loses entries (the internal
   array resize race) — Class A intra-test race.
4. The integration tests drive a real FileSystemWatcher + real SQLite with
   `StepUntilAsync(..., maxSeconds: 15)` bounded by `DateTime.UtcNow` wall clock — under
   full-suite parallel load the deadline expires — Class B inter-test contention.

## The fixes

### Class A fix — lock-guard the fake (WatchTestFakes.cs)

```csharp
private readonly object _sync = new();
// IngestFileAsync:
lock (_sync) { Ingested.Add((projectId, path, content)); }
// DeleteSourcePathAsync:
lock (_sync) { DeletedPaths.Add((projectId, path)); }
```

Do NOT serialize the scheduler — the concurrency is by design (feature rule 12:
round-robin admission, per-project gates).

### Class B fix — sequential xunit collection (WatchIntegrationCollection.cs)

```csharp
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WatchIntegrationCollection
{
    public const string Name = "watch-integration";
}
// WatchIntegrationTests gets:
[Collection(WatchIntegrationCollection.Name)]
```

Pattern matches the repo's existing `E2ETestCollection` (same shape, its own name).
Attribute name `DisableParallelization` verified from https://xunit.net/docs/running-tests-in-parallel
(v3; the per-test `[Fact(DisableParallelism = true)]` variant uses the other spelling).

## Evidence

- Pre-fix: `Tick_EventsForMultipleFiles` failed 2/2 full-suite; `DeletedDirectory_Cascades`
  failed on clean main.
- Post-fix: full suite 1125 passed / 0 failed / 43 skipped (exit 0) — both previously
  flaky tests green, zero flakes in the run.
- The one remaining full-suite failure on the branch WITHOUT the fix merged was another
  member of the same watch-integration class — confirming Class B (fix the class, not
  the individual test name).

## Notes for the repo

- Known-flake list (owner record-and-tolerate, Q1 ruling): `Startup_WrongKey_Exits2WithOpenError`
  (child-process, env-sensitive) and the BDD watch-rename scenario. The two above were
  FIXED, not tolerated.
- The unit fake (`FakeMemoryStore`) is shared by 5 watch test files — the lock guard
  benefits all of them.
