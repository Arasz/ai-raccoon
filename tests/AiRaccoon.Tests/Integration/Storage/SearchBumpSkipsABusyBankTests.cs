using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Sqlite.Encryption;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Storage;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     The search's access-rating bump is bookkeeping written after the results are already fused:
///     a busy bank must cost that bookkeeping, never the search. Before this fix one SQLITE_BUSY on
///     the per-hash UPDATE abandoned a fully-computed result set and surfaced to the caller as an
///     unmapped exception (EventId 912). The store now logs one friendly Warning (897) and returns
///     the results; the skipped bump is simply lost, which the relative rating tolerates
///     (docs/adr/0053).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SearchBumpSkipsABusyBankTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 9, 14, 0, 0, 0, TimeSpan.Zero);
    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-search-bump-busy");
    private readonly SqliteConnectionFactory _factory;

    public SearchBumpSkipsABusyBankTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task Search_WhileAnotherWriterHoldsTheLock_ReturnsResultsAndLogsOneBusyWarning()
    {
        var ct = TestContext.Current.CancellationToken;
        var logger = new FakeLogger<SqliteMemoryStore>();
        var store = SearchTimingsHarness.CreateStore(new ShortBusyTimeoutFactory(_factory, milliseconds: 200),
            new FakeTimeProvider(FixedNow), new SearchTimingsHarness.VectorEmbedderStub(), logger);

        var entry = await store.WriteAsync(new MemoryWriteRequest("proj-1", "widgets are stocked on the shelf"), ct);

        // A second connection takes the write lock for the whole search: the FTS read still works
        // (WAL), the rating UPDATE is the one that loses. The short busy_timeout keeps the RED fast.
        await using var holder = await _factory.OpenBankAsync(ct);
        await holder.ExecuteAsync(new CommandDefinition("BEGIN IMMEDIATE", cancellationToken: ct));

        var result = await store.SearchAsync(new SearchQuery("proj-1", "widgets", VectorWeight: 0), ct);

        result.Results.ShouldNotBeEmpty("a busy rating bump must not fail the search itself");
        var warning = logger.Collector.GetSnapshot()
            .Where(r => r.Level == LogLevel.Warning)
            .Single(r => r.Message.Contains("bank is busy", StringComparison.Ordinal));
        warning.Message.ShouldContain("1 result");
        warning.Exception.ShouldBeNull("the adjusted busy line carries no raw exception");

        await holder.ExecuteAsync(new CommandDefinition("ROLLBACK", cancellationToken: ct));

        await using var verify = await _factory.OpenBankAsync(ct);
        var accessCount = await verify.ExecuteScalarAsync<long>(
            "SELECT access_count FROM entries WHERE project_id = @projectId AND hash = @hash",
            new { projectId = "proj-1", hash = entry.Hash });
        accessCount.ShouldBe(0, "the skipped bump must not leave a partial update behind");
    }

    /// <summary>
    ///     Shortens <c>busy_timeout</c> on every connection the store opens, so the locked-bank case
    ///     costs 200 ms instead of the production 5 s. Everything else delegates to the real factory.
    /// </summary>
    private sealed class ShortBusyTimeoutFactory(ISqliteConnectionFactory inner, int milliseconds)
        : ISqliteConnectionFactory
    {
        public string BankPath => inner.BankPath;

        public async Task<SqliteConnection> OpenBankAsync(CancellationToken cancellationToken = default)
        {
            var connection = await inner.OpenBankAsync(cancellationToken);
            await ShortenAsync(connection, cancellationToken);
            return connection;
        }

        public async Task<SqliteConnection> OpenBankSkippingEnsureAsync(CancellationToken cancellationToken = default)
        {
            var connection = await inner.OpenBankSkippingEnsureAsync(cancellationToken);
            await ShortenAsync(connection, cancellationToken);
            return connection;
        }

        public Task<bool> MigrateLegacyKeyAsync(CancellationToken cancellationToken = default) =>
            inner.MigrateLegacyKeyAsync(cancellationToken);

        public Task<SqliteConnection> OpenBankWithResolvedKeyAsync(ResolvedKey resolvedKey,
            CancellationToken cancellationToken = default) =>
            inner.OpenBankWithResolvedKeyAsync(resolvedKey, cancellationToken);

        public Task RekeyBankAsync(string newKey, CancellationToken cancellationToken = default) =>
            inner.RekeyBankAsync(newKey, cancellationToken);

        public Task RekeyBankAsync(string newKey, string? currentKey, CancellationToken cancellationToken = default) =>
            inner.RekeyBankAsync(newKey, currentKey, cancellationToken);

        public Task<SqliteConnection> OpenBankWithKeyAsync(string? key, CancellationToken cancellationToken = default) =>
            inner.OpenBankWithKeyAsync(key, cancellationToken);

        private async Task ShortenAsync(SqliteConnection connection, CancellationToken cancellationToken) =>
            await connection.ExecuteAsync(new CommandDefinition($"PRAGMA busy_timeout = {milliseconds};",
                cancellationToken: cancellationToken));
    }
}
