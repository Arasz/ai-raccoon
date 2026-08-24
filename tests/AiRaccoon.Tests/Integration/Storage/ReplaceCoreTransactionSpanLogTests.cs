using AiRaccoon.Core.Ingestion;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using SqliteMemoryStore = AiRaccoon.Infrastructure.Sqlite.Memory.SqliteMemoryStore;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     WP11 Finding (b): which writer holds the bank's write lock long enough to time out another
///     one (owner-observed `SQLITE_BUSY` after 5s) was not established — two candidates, and the
///     log alone could not separate them. This is that instrumentation: the held span of
///     <c>ReplaceCoreAsync</c>'s <c>BEGIN IMMEDIATE … COMMIT</c>, recorded as a logged value (never
///     asserted against a threshold — a wall-clock assertion on a real transaction would be flaky).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class ReplaceCoreTransactionSpanLogTests : IDisposable
{
    private const int TransactionHeldEventId = 899;
    private static readonly DateTimeOffset FixedNow = new(2026, 1, 15, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-replace-span-log");
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeLogger<SqliteMemoryStore> _logger = new();
    private readonly SqliteMemoryStore _store;

    public ReplaceCoreTransactionSpanLogTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _store = TestData.CreateMemoryStore(_factory, _logger, new SqliteMemorySourceStore(_factory),
            TestData.RealMarkdownChunker(), new FakeTimeProvider(FixedNow), TestData.CreateEmbeddingService(), null, null, null, null, null, null, null);
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [Fact]
    public async Task ReplaceAsync_LogsTheHeldTransactionSpan()
    {
        var ct = TestContext.Current.CancellationToken;
        var file = Path.Combine(_dataRoot, "span-log.md");
        await File.WriteAllTextAsync(file, "content for the span log test", ct);
        await _store.SetSettingAsync(IngestScopeKeys.ScopeGlobal, IngestScopeKeys.Serialize([_dataRoot]), ct);

        await _store.ReplaceAsync("acme", file, "fixed-hash", ct);

        var record = _logger.Collector.GetSnapshot()
            .SingleOrDefault(r => r.Id.Id == TransactionHeldEventId);
        record.ShouldNotBeNull("ReplaceCoreAsync must log the held span of its BEGIN IMMEDIATE … COMMIT");
        record.Level.ShouldBe(LogLevel.Information);
        record.Message.ShouldContain("ms");
    }
}
