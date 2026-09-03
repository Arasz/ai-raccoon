using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Ingestion;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Maintenance;

/// <summary>
///     Air-merge P2 gate: the repair serializes with concurrent writers through per-batch BEGIN
///     IMMEDIATE + busy_timeout + WAL — there is no ToolGate lock to hold (review MUST-1), so the
///     SQL itself must not fail with SQLITE_BUSY while a writer holds the bank.
///     <para>
///         Honesty ledger (mutation : filter : fixture): BEGIN IMMEDIATE back to deferred BEGIN :
///         RepairVsWrite_ContendedLock : a writer holding an open write transaction across the
///         repair — deferred upgrade hits SQLITE_BUSY_SNAPSHOT (never retried by busy_timeout),
///         immediate waits it out. Timing-sensitive by construction, hence RetryFact.
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

    [RetryFact]
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
        await jobConnection.ExecuteAsync("PRAGMA busy_timeout=5000");
        await writerConnection.ExecuteAsync("PRAGMA busy_timeout=5000");

        // A writer holding an open write transaction across the repair's first write step: the
        // repair's BEGIN IMMEDIATE waits it out instead of failing with SQLITE_BUSY_SNAPSHOT.
        var writer = Task.Run(async () =>
        {
            await writerConnection.ExecuteAsync("BEGIN IMMEDIATE");
            try
            {
                await writerConnection.ExecuteAsync(
                    "UPDATE entries SET access_count = access_count + 1 WHERE hash = 'w1'");
                await Task.Delay(500, ct);
            }
            finally
            {
                await writerConnection.ExecuteAsync("COMMIT");
            }
        }, ct);
        await Task.Delay(100, ct);
        var job = new ProjectIdsRepairJob(
            new FileTypeMatcher([new MarkdownFileTypeHandler(new StubChunker())]),
            TestData.CreateEmbeddingService(), new FakeTimeProvider(FixedNow));
        var repair = Task.Run(() => job.RunAsync(jobConnection, ct).AsTask(), ct);

        await Task.WhenAll(writer, repair);

        repair.Result.ShouldBeTrue("the contended repair still folds its rows");
        await using var verify = await _factory.OpenBankAsync(ct);
        (await verify.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM entries WHERE project_id = 'job-search-ai-assistant'"))
            .ShouldBe(0);
        (await verify.ExecuteScalarAsync<long>("SELECT access_count FROM entries WHERE hash = 'w1'"))
            .ShouldBe(1, "the contending writer's write lands too — serialization, not failure");
    }
}
