using AiRaccoon.Infrastructure.Embedding.Manifest;
using AiRaccoon.Core.Memory;
using AiRaccoon.Core.Metrics;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     WP11 (log-values-as-metrics): EventId 426's "tokens exceeded the window" value, recorded
///     under the self-metrics id — TrimQueryToWindow/EmbedQueryAsync have no project id to record
///     under today (the embedding engine is a bank-wide singleton keyed by fingerprint, not scoped
///     per project); same footing as job.*/drain.* (see PR body "Deviations").
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class QueryTruncationMetricsTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("query-truncation-metrics");
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeLogger<EmbeddingService> _logger = new();
    private readonly RecordingMeasurementRecorder _measurements = new();
    private readonly IModelMigrationLease _modelMigrationLease = Substitute.For<IModelMigrationLease>();
    private readonly FakeTimeProvider _timeProvider = new();

    public QueryTruncationMetricsTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private EmbeddingService NewEmbeddingService() => new(_logger, new LocalTokenizer(), new EmbeddingTokenizerFactory(),
        new EmbeddingManifestLoader(new EmbeddingManifestSerializer(), new EmbeddingManifestValidator()),
        _measurements, _timeProvider);

    [RetryFact]
    public async Task ALongQuery_RecordsTokensOverTheWindow()
    {
        await using var connection = await OpenConfiguredAsync();
        var embedder = new EntryEmbedder(NewEmbeddingService(), _modelMigrationLease, _timeProvider, new VecDimensionReconciler());

        await embedder.EmbedQueryAsync(connection, LongQuery(), TestContext.Current.CancellationToken);

        var truncated = _measurements.Recorded.Single(m => m.Name == "search.query.truncated_tokens");
        truncated.Kind.ShouldBe(MeasurementKind.Histogram);
        truncated.ProjectId.ShouldBe(MetricsConfigKeys.SelfMetricsProjectId);
        truncated.Value.ShouldBeGreaterThan(0);
    }

    [RetryFact]
    public async Task AShortQuery_RecordsNothing()
    {
        await using var connection = await OpenConfiguredAsync();
        var embedder = new EntryEmbedder(NewEmbeddingService(), _modelMigrationLease, _timeProvider, new VecDimensionReconciler());

        await embedder.EmbedQueryAsync(connection, "how does the promotion queue decide?",
            TestContext.Current.CancellationToken);

        _measurements.Recorded.ShouldNotContain(m => m.Name == "search.query.truncated_tokens");
    }

    private static string LongQuery() =>
        string.Join(' ', Enumerable.Repeat(
            "how does the retrieval pipeline weigh full text against vectors when the corpus is large", 40));

    private async Task<SqliteConnection> OpenConfiguredAsync()
    {
        var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync("INSERT OR REPLACE INTO settings (key, value) VALUES (@key, 'local')",
            new { key = EmbeddingSettingsKeys.Provider });
        return connection;
    }
}
