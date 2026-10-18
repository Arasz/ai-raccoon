using System.Diagnostics;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.Unit.Storage;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     Independent searches must overlap. Eight searches that run one after another take at least
///     8x one search; native contention (SQLite's global allocation mutex before MEMSTATUS was
///     turned off) made them take 10x or more. Best-of-rounds wall time keeps one noisy round
///     from deciding the verdict.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class ConcurrentSearchOverlapTests : IDisposable
{
    private const int Concurrency = 8;
    private const int Rounds = 7;
    private const double MaxSlowdownVersusSingle = 6.0;

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-concurrent-search");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task ConcurrentSearches_RunInParallel_NotOneAfterAnother()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        var factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        var store = SearchTimingsHarness.CreateStore(factory, TimeProvider.System,
            new SearchTimingsHarness.VectorEmbedderStub());
        var ct = TestContext.Current.CancellationToken;
        for (var i = 0; i < 1500; i++)
        {
            await store.WriteAsync(new MemoryWriteRequest("proj-1",
                $"widget number {i} stocked on shelf {i % 17} with gadget{i % 31} notes about memory search"), ct);
        }

        var query = new SearchQuery("proj-1", "widget shelf memory search");
        await store.SearchAsync(query, ct);

        var single = await BestWallAsync(1, () => store.SearchAsync(query, ct));
        var concurrent = await BestWallAsync(Concurrency, () => store.SearchAsync(query, ct));

        (concurrent / single).ShouldBeLessThan(MaxSlowdownVersusSingle,
            $"{Concurrency} concurrent searches took {concurrent:F1} ms against {single:F1} ms for one: they are serialising");
    }

    private static async Task<double> BestWallAsync(int parallelism, Func<Task> work)
    {
        var best = double.MaxValue;
        for (var round = 0; round < Rounds; round++)
        {
            var clock = Stopwatch.StartNew();
            await Task.WhenAll(Enumerable.Range(0, parallelism).Select(_ => Task.Run(work)));
            best = Math.Min(best, clock.Elapsed.TotalMilliseconds);
        }

        return best;
    }
}
