using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using Dapper;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Maintenance;

/// <summary>
///     ADR-0075 amendment: PromotionQueuePruneJob is the relay half of the promotion-queue-prune
///     outbox — on-demand (<see cref="IMaintenanceJob.Interval" /> is null), it only ever runs
///     because a CLI-submitted promotion_queue_prune_requests row exists, never on a clock. It
///     applies the same delete `extract prune --apply` used to run in the CLI process.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class PromotionQueuePruneJobTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);
    private readonly string _dataRoot = TestData.CreateTempRoot("promotion-queue-prune-job");
    private readonly SqliteConnectionFactory _factory;
    private readonly SqlitePromotionQueueStore _queueStore;

    public PromotionQueuePruneJobTests()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _queueStore = new SqlitePromotionQueueStore(_factory, new FakeTimeProvider(FixedNow));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private static PromotionQueuePruneJob NewJob() => new(new FakeTimeProvider(FixedNow));

    [Fact]
    public async Task HasWorkAsync_WithNoOpenRequest_IsFalse()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        (await NewJob().HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }

    [Fact]
    public async Task HasWorkAsync_AfterARequest_IsTrue()
    {
        await _queueStore.RequestPruneOrphansAsync(TestContext.Current.CancellationToken);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        (await NewJob().HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeTrue();
    }

    [Fact]
    public async Task RunAsync_WithNoOpenRequest_IsANoOp() =>
        await Should.NotThrowAsync(async () =>
        {
            await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
            await NewJob().RunAsync(connection, TestContext.Current.CancellationToken);
        });

    [Fact]
    public async Task RunAsync_DeletesTheOrphans_AndMarksTheRequestFinished()
    {
        await _queueStore.UpsertAsync("acme",
            [new QueueCandidate("orphan-1", "orphan-1.md", "gone", null, 1.0, ["organic-write"])],
            TestContext.Current.CancellationToken);
        await _queueStore.RequestPruneOrphansAsync(TestContext.Current.CancellationToken);

        await using (var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken))
        {
            await NewJob().RunAsync(connection, TestContext.Current.CancellationToken);
        }

        (await _queueStore.ListAsync(null, TestContext.Current.CancellationToken)).ShouldBeEmpty();
        await using var verify = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        (await verify.ExecuteScalarAsync<long>(
                "SELECT count(*) FROM promotion_queue_prune_requests WHERE id = 1 AND finished_at IS NOT NULL"))
            .ShouldBe(1);
    }

    [Fact]
    public async Task HasWorkAsync_AfterRunAsync_IsFalseAgain()
    {
        await _queueStore.RequestPruneOrphansAsync(TestContext.Current.CancellationToken);
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        var job = NewJob();
        await job.RunAsync(connection, TestContext.Current.CancellationToken);

        (await job.HasWorkAsync(connection, TestContext.Current.CancellationToken)).ShouldBeFalse();
    }
}
