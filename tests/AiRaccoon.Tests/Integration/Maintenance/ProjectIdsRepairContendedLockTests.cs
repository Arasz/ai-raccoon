using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Maintenance;

/// <summary>
///     Air-merge P2 gate: the repair serializes with concurrent writers through per-batch BEGIN
///     IMMEDIATE + busy_timeout + WAL — there is no ToolGate lock to hold (review MUST-1), so the
///     SQL itself must not fail with SQLITE_BUSY while a writer holds the bank.
///     <para>
///         d-427 MUST-1: no sleep-and-hope timing — the repair starts strictly AFTER the writer
///         signals its held lock (TCS latch), and a probe BEGIN observed mid-wait proves a live
///         waiter sat on that lock — the repair actually contended instead of running unopposed.
///         Deterministic by construction, hence a plain Fact (the RetryFact is dropped, not retained).
///     </para>
///     <para>
///         Honesty ledger (mutation : filter : fixture): BEGIN IMMEDIATE back to deferred BEGIN :
///         RepairVsWrite_ContendedLock : a writer holding an open write transaction across the
///         repair — deferred upgrade hits SQLITE_BUSY_SNAPSHOT (never retried by busy_timeout),
///         immediate waits it out; neuter-the-writer-hold : same : with no lock held the probe
///         completes instantly and the blocked-waiter assert reddens, so an uncontended run
///         cannot pass as contended.
///     </para>
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ProjectIdsRepairContendedLockTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dataRoot = TestData.CreateTempRoot("project-ids-repair-lock");
    private readonly SqliteConnectionFactory _factory;

    public ProjectIdsRepairContendedLockTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>How long the writer-held latch and the repair start are given before absence is believed.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    /// <summary>Positions the probe after the repair's start; ordering itself is latch-guaranteed, not timed.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(500);

    [Fact]
    public async Task RepairVsWrite_ContendedLock()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var seed = await _factory.OpenBankAsync(ct))
        {
            await seed.ExecuteAsync(
                "INSERT INTO entries (hash, path, value, source_file, scope, project_id, context_label, created_at, updated_at, embed_state) VALUES " +
                "('w1', 'w1', 'winner', 'seed.md', 'project', 'jsaa', 'ctx-a', 1, 1, 'pending')," +
                "('l1', 'l1', 'loser', 'seed.md', 'project', 'job-search-ai-assistant', 'ctx-a', 2, 2, 'pending')");
            await seed.ExecuteAsync(MemorySql.RequestRepair,
                new { kind = RepairKinds.ProjectIds, requestedAt = FixedNow.ToUnixTimeSeconds() });
        }

        await using var jobConnection = await _factory.OpenBankAsync(ct);
        await using var writerConnection = await _factory.OpenBankAsync(ct);
        await using var probeConnection = await _factory.OpenBankAsync(ct);
        await jobConnection.ExecuteAsync("PRAGMA busy_timeout=5000");
        await writerConnection.ExecuteAsync("PRAGMA busy_timeout=5000");

        // Latch pair: the writer signals once its RESERVED lock is held (after the first UPDATE,
        // so the lock is proven, not just requested); the test releases it only after proving the
        // repair is contending. The repair therefore starts strictly inside the writer's hold —
        // no sleep orders the two tasks.
        var lockHeld = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseWriter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(async () =>
        {
            await writerConnection.ExecuteAsync("BEGIN IMMEDIATE");
            try
            {
                await writerConnection.ExecuteAsync(
                    "UPDATE entries SET access_count = access_count + 1 WHERE hash = 'w1'");
                lockHeld.TrySetResult(true);
                await releaseWriter.Task.WaitAsync(Patience, ct);
            }
            finally
            {
                await writerConnection.ExecuteAsync("COMMIT");
            }
        }, ct);
        await lockHeld.Task.WaitAsync(Patience, ct);

        var job = new ProjectIdsRepairJob(
            new FileTypeMatcher([new MarkdownFileTypeHandler(new StubChunker())]),
            TestData.CreateEmbeddingService(), new FakeTimeProvider(FixedNow));
        var repair = Task.Run(() => job.RunAsync(jobConnection, ct).AsTask(), ct);
        await Task.Delay(Grace, ct);

        // Contention-occurred observables, both deterministic. The writer holds RESERVED
        // continuously from the latch until the release below, so any BEGIN IMMEDIATE issued in
        // between can only complete after the release (or on the 5s factory timeout — the graces
        // are milliseconds against it, never near it):
        //   1. the repair is still running — it cannot have finished without the write lock;
        //   2. a probe BEGIN issued on a third connection is still BLOCKED — a live waiter on the
        //      writer's lock, observed mid-wait rather than inferred after the fact. The probe
        //      rolls itself back the instant it acquires, so it never interferes past the release.
        repair.IsCompleted.ShouldBeFalse("the repair needs the write lock the writer still holds");
        var probe = Task.Run(async () =>
        {
            await probeConnection.ExecuteAsync("BEGIN IMMEDIATE");
            try
            {
            }
            finally
            {
                await probeConnection.ExecuteAsync("ROLLBACK");
            }
        }, ct);
        await Task.Delay(Grace, ct);
        probe.IsCompleted.ShouldBeFalse("a live waiter sits on the writer's lock right now");
        releaseWriter.TrySetResult(true);

        await Task.WhenAll(writer, probe, repair);

        (await repair).ShouldBeTrue("the contended repair still folds its rows");
        await using var verify = await _factory.OpenBankAsync(ct);
        (await verify.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM entries WHERE project_id = 'job-search-ai-assistant'"))
            .ShouldBe(0);
        (await verify.ExecuteScalarAsync<long>("SELECT access_count FROM entries WHERE hash = 'w1'"))
            .ShouldBe(1, "the contending writer's write lands too — serialization, not failure");
    }
}
