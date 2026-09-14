using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using NSubstitute;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.SearchQuality;

/// <summary>
///     <c>RecordSearchSafeAsync</c> is the best-effort search_quality write: it must never fail a
///     search, and under the WP12 write-lock convoy its catch-all logged EventId 965 with the raw
///     <c>SqliteException</c> and a full stack — the same noise class the tool path's 912 had.
///     Busy (5/6) is now classified and logs one friendly Warning (964) with no exception; every
///     other failure keeps 965 and its exception, so real faults stay diagnosable.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SqliteSearchQualityServiceBusyTests
{
    private const string CorrelationId = "corr-busy-1";

    [Fact]
    public async Task RecordSearchSafe_WhenTheBankIsBusy_LogsTheDeferredLineWithoutTheException()
    {
        var logger = new FakeLogger<SqliteSearchQualityService>();
        var service = ServiceThrowing(new SqliteException("database is locked", 5), logger);

        await service.RecordSearchSafeAsync(CorrelationId, "q", "all", "proj-a", "memory", "sess-test",
            1, [], TestContext.Current.CancellationToken);

        var records = logger.Collector.GetSnapshot();
        var deferred = records.Single(r => r.Id.Id == 964);
        deferred.Level.ShouldBe(LogLevel.Warning);
        deferred.Exception.ShouldBeNull("a transient busy line carries no raw exception");
        deferred.Message.ShouldContain(CorrelationId);
        deferred.Message.ShouldContain("bank is busy");
        records.ShouldNotContain(r => r.Id.Id == 965, "busy is not a genuine failure");
    }

    [Fact]
    public async Task RecordSearchSafe_WhenTheBankIsLocked_LogsTheDeferredLine()
    {
        var logger = new FakeLogger<SqliteSearchQualityService>();
        var service = ServiceThrowing(new SqliteException("database table is locked", 6), logger);

        await service.RecordSearchSafeAsync(CorrelationId, "q", "all", "proj-a", "memory", "sess-test",
            1, [], TestContext.Current.CancellationToken);

        logger.Collector.GetSnapshot().Single(r => r.Id.Id == 964).Exception.ShouldBeNull();
    }

    /// <summary>A connection open can wrap the busy error; the chain walk is what keeps that from reverting to a stack.</summary>
    [Fact]
    public async Task RecordSearchSafe_WhenBusyIsNested_LogsTheDeferredLine()
    {
        var logger = new FakeLogger<SqliteSearchQualityService>();
        var service = ServiceThrowing(
            new InvalidOperationException("open failed", new SqliteException("database is locked", 5)), logger);

        await service.RecordSearchSafeAsync(CorrelationId, "q", "all", "proj-a", "memory", "sess-test",
            1, [], TestContext.Current.CancellationToken);

        logger.Collector.GetSnapshot().ShouldContain(r => r.Id.Id == 964 && r.Exception == null);
    }

    /// <summary>
    ///     The negative control: code 26 is a corrupt/mismatched file, a real fault. It keeps the
    ///     exception-carrying 965 record rather than being laundered into a busy line.
    /// </summary>
    [Fact]
    public async Task RecordSearchSafe_WithANonBusySqliteError_KeepsTheExceptionAt965()
    {
        var logger = new FakeLogger<SqliteSearchQualityService>();
        var service = ServiceThrowing(new SqliteException("file is not a database", 26), logger);

        await service.RecordSearchSafeAsync(CorrelationId, "q", "all", "proj-a", "memory", "sess-test",
            1, [], TestContext.Current.CancellationToken);

        var records = logger.Collector.GetSnapshot();
        var failed = records.Single(r => r.Id.Id == 965);
        failed.Level.ShouldBe(LogLevel.Warning);
        failed.Exception.ShouldNotBeNull("a genuine fault keeps its exception");
        records.ShouldNotContain(r => r.Id.Id == 964);
    }

    [Fact]
    public async Task RecordSearchSafe_WithANonSqliteFailure_KeepsTheExceptionAt965()
    {
        var logger = new FakeLogger<SqliteSearchQualityService>();
        var service = ServiceThrowing(new InvalidOperationException("boom"), logger);

        await service.RecordSearchSafeAsync(CorrelationId, "q", "all", "proj-a", "memory", "sess-test",
            1, [], TestContext.Current.CancellationToken);

        logger.Collector.GetSnapshot().Single(r => r.Id.Id == 965).Exception.ShouldNotBeNull();
    }

    private static SqliteSearchQualityService ServiceThrowing(Exception exception,
        FakeLogger<SqliteSearchQualityService> logger)
    {
        var factory = Substitute.For<ISqliteConnectionFactory>();
        factory.OpenBankAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Microsoft.Data.Sqlite.SqliteConnection>(exception));
        return new SqliteSearchQualityService(factory, logger);
    }
}
