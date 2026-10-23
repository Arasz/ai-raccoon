using AiRaccoon.Infrastructure.Options;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration;

/// <summary>An absent window start means the last seven days on the service's own clock.</summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SearchQualityDefaultWindowTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot();
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeTimeProvider _time = new(Now.AddDays(-8));
    private readonly SqliteSearchQualityService _sut;

    public SearchQualityDefaultWindowTests()
    {
        var options = new InfrastructureOptions { DataRoot = _dataRoot, Rid = "osx-arm64", Scope = InstallScope.User };
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _sut = new SqliteSearchQualityService(_factory, NullLogger<SqliteSearchQualityService>.Instance, _time);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task NoWindowStart_CountsOnlyTheLastSevenDays()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var connection = await _factory.OpenBankAsync(ct))
        {
            await MemorySchema.EnsureAsync(connection, ct);
        }

        await RecordAsync("too-old", ct);
        _time.Advance(TimeSpan.FromDays(2));
        await RecordAsync("inside", ct);
        _time.SetUtcNow(Now);

        var metrics = await _sut.GetMetricsAsync("proj-a", null, ct);

        metrics.TotalSearches.ShouldBe(1);
    }

    [RetryFact]
    public async Task AnExplicitWindowStart_IsNotReplacedByTheDefault()
    {
        var ct = TestContext.Current.CancellationToken;
        await using (var connection = await _factory.OpenBankAsync(ct))
        {
            await MemorySchema.EnsureAsync(connection, ct);
        }

        await RecordAsync("too-old", ct);
        _time.SetUtcNow(Now);

        var metrics = await _sut.GetMetricsAsync("proj-a", Now.AddDays(-9), ct);

        metrics.TotalSearches.ShouldBe(1);
    }

    private Task RecordAsync(string correlationId, CancellationToken ct) =>
        _sut.RecordSearchAsync(correlationId, "query", "all", "proj-a", "memory", "session-1", 1, [], ct);
}
