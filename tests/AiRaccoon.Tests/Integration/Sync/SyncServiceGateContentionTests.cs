using AiRaccoon.Infrastructure.Sync;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Sync;

/// <summary>
///     D21 gap 1: <c>SyncService._gate</c> is a <c>SemaphoreSlim(1,1)</c> wrapping the whole sync
///     cycle, but no test ever put two callers against it. This proves what it actually guarantees:
///     mutual exclusion with FIFO waiting, not refusal — a second concurrent caller blocks until the
///     first caller's entire cycle (VACUUM, integrity check, pull, merge, push, watermark) has
///     finished, then runs its own cycle to completion. Neither caller is rejected, and the two
///     cycles' I/O never overlaps.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SyncServiceGateContentionTests : IDisposable
{
    // Bounds wall-clock waiting only (and doubles as deadlock detection); every meaningful
    // assertion is event-driven, so a broken gate fails on those regardless of this bound.
    // 15 s was exceeded by a full-suite-parallel run (17.9 s observed, 2026-08-20 nightly F6);
    // 60 s is 3.3x the observed worst, same shape as the 08-19 IdleTimeout 15s->30s absorption.
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    private readonly string _dataRoot = TestData.CreateTempRoot("sync-gate-contention");

    private string BankPath => Path.Combine(_dataRoot, "memory.db");

    public void Dispose() => Directory.Delete(_dataRoot, true);

    private static async Task<SqliteConnection> CreateAndOpenAsync(string path, CancellationToken ct = default)
    {
        var conn = new SqliteConnection($"Data Source={path}");
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
                          CREATE TABLE IF NOT EXISTS entries (
                              id INTEGER PRIMARY KEY,
                              hash TEXT,
                              path TEXT,
                              value TEXT,
                              source_file TEXT NULL,
                              section TEXT NULL,
                              scope TEXT CHECK(scope IN ('shared','project','custom')) NULL,
                              project_id TEXT NULL,
                              context_label TEXT NULL,
                              workspace_id TEXT NULL,
                              agent_id TEXT NULL,
                              created_at INTEGER NOT NULL,
                              updated_at INTEGER NOT NULL,
                              access_count INTEGER NOT NULL DEFAULT 0,
                              last_accessed_at INTEGER NULL,
                              rating REAL NOT NULL DEFAULT 0.5,
                              ttl_days INTEGER NULL,
                              embed_state TEXT NOT NULL DEFAULT 'pending',
                              embedding BLOB NULL,
                              heading_path TEXT NULL,
                              structure_embedding BLOB NULL,
                              chunk_index INTEGER NOT NULL DEFAULT 0,
                              total_chunks INTEGER NOT NULL DEFAULT 0,
                              source_id INTEGER NULL
                          );
                          CREATE TABLE IF NOT EXISTS settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                          CREATE TABLE IF NOT EXISTS workspaces (id TEXT PRIMARY KEY, project_id TEXT NOT NULL, agent_id TEXT NULL,
                              name TEXT NULL, status TEXT NOT NULL, created_at INTEGER NOT NULL, closed_at INTEGER NULL);
                          CREATE TABLE IF NOT EXISTS sync_meta (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                          CREATE TABLE IF NOT EXISTS projects (id TEXT PRIMARY KEY, name TEXT NULL, created_at INTEGER NOT NULL);
                          CREATE TABLE IF NOT EXISTS sync_tombstones (project_id TEXT NOT NULL, hash TEXT NOT NULL, scope TEXT NOT NULL,
                              deleted_at INTEGER NOT NULL, PRIMARY KEY (project_id, hash, scope));
                          CREATE TABLE IF NOT EXISTS memory_source (
                              id INTEGER PRIMARY KEY,
                              source_type TEXT NOT NULL,
                              source_locator TEXT NOT NULL,
                              section TEXT NULL,
                              heading_path TEXT NULL);
                          CREATE UNIQUE INDEX IF NOT EXISTS uq_memory_source_identity
                              ON memory_source(source_type, source_locator, COALESCE(section, ''));
                          CREATE INDEX IF NOT EXISTS idx_entries_source_id ON entries(source_id);
                          """;
        await cmd.ExecuteNonQueryAsync(ct);
        return conn;
    }

    private static async Task<SqliteConnection> OpenPlainAsync(string path, CancellationToken ct)
    {
        var c = new SqliteConnection($"Data Source={path}");
        await c.OpenAsync(ct);
        return c;
    }

    /// <summary>
    ///     Forces two <c>MemorySyncAsync</c> calls to start at the same instant (Barrier), tags every
    ///     hook invocation with the logical cycle that made it via <see cref="AsyncLocal{T}" /> (correct
    ///     regardless of whether the gate actually serializes), and tracks the running peak of
    ///     concurrently-active cycles between "entered the cycle" (resolveCloud) and "pushed to cloud"
    ///     (the last shared-state operation before the fast local watermark write). Whichever caller's
    ///     resolveCloud fires first is deliberately parked there until released, so the second caller —
    ///     if the gate really serializes — has no choice but to sit on <c>_gate.WaitAsync</c> the whole
    ///     time, unable to make a single hook call, before the park is released.
    /// </summary>
    [RetryFact]
    public async Task ConcurrentMemorySync_SecondCallerWaitsForFirst_BothSucceed_NeverConcurrentlyActive()
    {
        var recordLock = new Lock();
        var events = new List<(int Cycle, string Hook)>();
        var cycleCounter = 0;
        var activeCycles = 0;
        var maxActiveCycles = 0;
        var cycle1Entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCycle1 = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var currentCycle = new AsyncLocal<int>();

        void Record(string hook)
        {
            lock (recordLock)
            {
                events.Add((currentCycle.Value, hook));
            }
        }

        // Exits the "active" window right after the push completes — the last operation that
        // touches shared state before the fast local watermark write.
        var cloud = new ActiveCycleExitingCloudStore(new FakeCloudStore(), () =>
        {
            Interlocked.Decrement(ref activeCycles);
            Record("pushAsync-exit");
        });

        async Task<ICloudStore> ResolveCloudAsync(CancellationToken ct)
        {
            var cycle = Interlocked.Increment(ref cycleCounter);
            currentCycle.Value = cycle;
            var active = Interlocked.Increment(ref activeCycles);
            lock (recordLock)
            {
                events.Add((cycle, "resolveCloud"));
                maxActiveCycles = Math.Max(maxActiveCycles, active);
            }

            if (cycle == 1)
            {
                cycle1Entered.TrySetResult();
                await releaseCycle1.Task.WaitAsync(ct).ConfigureAwait(false);
            }

            return cloud;
        }

        async Task<SqliteConnection> OpenBankAsync(CancellationToken ct)
        {
            Record("openBank");
            return await CreateAndOpenAsync(BankPath, ct).ConfigureAwait(false);
        }

        async Task<SqliteConnection> OpenReadOnlyAsync(string path, CancellationToken ct)
        {
            Record("openReadOnly");
            return await OpenPlainAsync(path, ct).ConfigureAwait(false);
        }

        async Task<SqliteConnection> OpenSnapshotAsync(string path, CancellationToken ct)
        {
            Record("openSnapshot");
            return await OpenPlainAsync(path, ct).ConfigureAwait(false);
        }

        var service = new SyncService(ResolveCloudAsync, OpenBankAsync, OpenSnapshotAsync, OpenReadOnlyAsync,
            TimeProvider.System, NullLogger<SyncService>.Instance);

        using var start = new Barrier(2);
        var task1 = Task.Run(async () =>
        {
            start.SignalAndWait(Patience);
            return await service.MemorySyncAsync("acme", "gate-race", TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
        }, TestContext.Current.CancellationToken);
        var task2 = Task.Run(async () =>
        {
            start.SignalAndWait(Patience);
            return await service.MemorySyncAsync("acme", "gate-race", TestContext.Current.CancellationToken)
                .ConfigureAwait(false);
        }, TestContext.Current.CancellationToken);

        await cycle1Entered.Task.WaitAsync(Patience, TestContext.Current.CancellationToken);

        // The peak must already be 1 here, before release: with a working gate the second caller
        // cannot have reached resolveCloud yet (it is parked on _gate.WaitAsync), so cycleCounter
        // is still 1 and no hook has fired for a second cycle.
        maxActiveCycles.ShouldBe(1, "only one cycle may be active before the parked cycle is released");

        releaseCycle1.SetResult();
        var results = await Task.WhenAll(task1, task2).WaitAsync(Patience, TestContext.Current.CancellationToken);

        results[0].Sent.ShouldBe(1);
        results[1].Sent.ShouldBe(1);
        cycleCounter.ShouldBe(2, "both callers must have run a full cycle — neither is refused");
        maxActiveCycles.ShouldBe(1, "the gate guarantees mutual exclusion: at most one cycle is ever active at once");

        var cycle1Events = events.Where(e => e.Cycle == 1).Select(e => e.Hook).ToArray();
        var cycle2Events = events.Where(e => e.Cycle == 2).Select(e => e.Hook).ToArray();
        cycle1Events.ShouldNotBeEmpty();
        cycle2Events.ShouldNotBeEmpty();
        var firstCycle2Index = events.FindIndex(e => e.Cycle == 2);
        var lastCycle1Index = events.FindLastIndex(e => e.Cycle == 1);
        firstCycle2Index.ShouldBeGreaterThan(lastCycle1Index,
            "cycle 2's I/O must not start until cycle 1's I/O has entirely finished");
    }

    /// <summary>The same contention, run several times to rule out a scheduler-lucky pass — a broken gate would eventually let two cycles overlap.</summary>
    [RetryTheory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public async Task ConcurrentMemorySync_RepeatedRuns_BothCallersAlwaysSucceed(int run)
    {
        var dataRoot = TestData.CreateTempRoot($"sync-gate-contention-repeat-{run}");
        try
        {
            var bankPath = Path.Combine(dataRoot, "memory.db");
            var cloud = new FakeCloudStore();
            var service = new SyncService(cloud,
                ct => CreateAndOpenAsync(bankPath, ct),
                OpenPlainAsync,
                OpenPlainAsync,
                TimeProvider.System, NullLogger<SyncService>.Instance);

            using var start = new Barrier(2);
            var task1 = Task.Run(async () =>
            {
                start.SignalAndWait(Patience);
                return await service.MemorySyncAsync("acme", $"gate-race-{run}", TestContext.Current.CancellationToken)
                    .ConfigureAwait(false);
            }, TestContext.Current.CancellationToken);
            var task2 = Task.Run(async () =>
            {
                start.SignalAndWait(Patience);
                return await service.MemorySyncAsync("acme", $"gate-race-{run}", TestContext.Current.CancellationToken)
                    .ConfigureAwait(false);
            }, TestContext.Current.CancellationToken);

            var results = await Task.WhenAll(task1, task2).WaitAsync(Patience, TestContext.Current.CancellationToken);

            results[0].Sent.ShouldBe(1, $"run {run}: the first caller must not be refused");
            results[1].Sent.ShouldBe(1, $"run {run}: the second caller must wait, then also succeed — not be refused");
        }
        finally
        {
            Directory.Delete(dataRoot, true);
        }
    }

    /// <summary>Decorates an <see cref="ICloudStore" /> to run <paramref name="onPushCompleted" /> right after a push finishes (success or failure) — the point this test treats as "cycle no longer active".</summary>
    private sealed class ActiveCycleExitingCloudStore(ICloudStore inner, Action onPushCompleted) : ICloudStore
    {
        public Task<CloudObject?> PullAsync(string objectKey, CancellationToken cancellationToken = default) =>
            inner.PullAsync(objectKey, cancellationToken);

        public async Task<string> PushAsync(string objectKey, byte[] data, string? etag,
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await inner.PushAsync(objectKey, data, etag, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                onPushCompleted();
            }
        }
    }
}
