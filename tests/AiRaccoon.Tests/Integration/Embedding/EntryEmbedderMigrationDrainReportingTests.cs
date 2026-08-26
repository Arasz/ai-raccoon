using AiRaccoon.Core.Metrics;
using AiRaccoon.Core.Observability;
using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Tests.TestHelpers;
using AiRaccoon.Tests.Unit.Observability;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     LANE P4 (docs/work/2026-08-26-doctor-parity-moe-p4-observability.md): the migration relay
///     (<see cref="EntryEmbedder.DrainMigrationAsync" />) reports through the same
///     <see cref="EmbedDrainReporter" /> as the pump drain — 1008 start with rows owed, 1003 finish
///     with rows, 1005 failure, the same drain.memory.* series, and one embed.drain span with
///     <c>RecordRows</c> before <c>Succeeded</c>. Loggers and recorders are built fresh inside each
///     test (R2 J8: xRetry re-runs the method with state persisting across attempts).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class EntryEmbedderMigrationDrainReportingTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(FixedNow);
    private readonly string _dataRoot = TestData.CreateTempRoot("entry-embedder-migration-reporting");
    private readonly SqliteConnectionFactory _factory;

    public EntryEmbedderMigrationDrainReportingTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private EntryEmbedder NewEmbedder(FakeLogger<EntryEmbedder> logger, RecordingMeasurementRecorder measurements,
        IEmbeddingService? embeddings = null, IOperationTelemetry? telemetry = null) =>
        new(embeddings ?? new CountingEmbeddingService(), new SqliteModelMigrationLease(_time), _time,
            new VecDimensionReconciler(), new EmbedDrainReporter(measurements, _time),
            telemetry ?? TestTelemetry.None, logger);

    /// <summary>WP-P4-2: a drain that embeds N rows emits 1008 (Information, rows owed) then 1003
    /// (Information, {Rows}=N), and records both drain.memory.* series through the shared reporter.</summary>
    [RetryFact]
    public async Task Drain_Emits1008Then1003WithCorpusMemoryAndRecordsBothDrainSeries()
    {
        await using var connection = await _factory.OpenBankAsync(Ct);
        await SeedPendingRowsAsync(connection, 5);
        await ConfigureProviderAsync(connection);
        await OpenMigrationAsync(connection, startedAt: 0);

        var logger = new FakeLogger<EntryEmbedder>();
        var measurements = new RecordingMeasurementRecorder();
        var embedder = NewEmbedder(logger, measurements);

        (await embedder.DrainMigrationAsync(connection, Ct)).ShouldBeTrue();

        var records = logger.Collector.GetSnapshot();
        records.ShouldContain(r => r.Id.Id == 1008 && r.Level == LogLevel.Information
                                   && r.Message == "Embed drain for Memory started under the model migration: 5 row(s) owed");
        records.ShouldContain(r => r.Id.Id == 1003 && r.Level == LogLevel.Information
                                   && r.Message == "Embed drain pass finished for Memory: 5 row(s)");
        records.TakeWhile(r => r.Id.Id != 1003).ShouldContain(r => r.Id.Id == 1008,
            "1008 (start, with rows owed) must precede 1003 (finish)");

        measurements.Recorded.Single(m => m.Name == "drain.memory.rows").Value.ShouldBe(5);
        measurements.Recorded.Single(m => m.Name == "drain.memory.duration_ms")
            .Kind.ShouldBe(MeasurementKind.Histogram);
    }

    /// <summary>WP-P4-2's ordering pin: RecordRows must land on the scope BEFORE Succeeded()
    /// claims its one measurement, or the OTLP background rows histogram never sees the count.</summary>
    [RetryFact]
    public async Task Drain_ExportsTheRowCountOnTheBackgroundRowsHistogram()
    {
        using var probe = new BackgroundTelemetryProbe(EmbedDrainService.OperationName);
        await using var connection = await _factory.OpenBankAsync(Ct);
        await SeedPendingRowsAsync(connection, 5);
        await ConfigureProviderAsync(connection);
        await OpenMigrationAsync(connection, startedAt: 0);

        var embedder = NewEmbedder(new FakeLogger<EntryEmbedder>(), new RecordingMeasurementRecorder(),
            telemetry: probe.Telemetry);

        (await embedder.DrainMigrationAsync(connection, Ct)).ShouldBeTrue();

        probe.Passes.ShouldHaveSingleItem("the migration drain must open exactly one embed.drain scope");
        probe.Rows.ShouldHaveSingleItem().Value.ShouldBe(5,
            "RecordRows must run before Succeeded() claims the scope's one measurement");
    }

    /// <summary>WP-P4-3: a live lease held by a foreign relay pass → false + 1010 (was a silent return).</summary>
    [RetryFact]
    public async Task Drain_WhenAnotherRelayHoldsALiveLease_ReturnsFalseAndEmits1010()
    {
        await using var connection = await _factory.OpenBankAsync(Ct);
        await SeedPendingRowsAsync(connection, 1);
        await ConfigureProviderAsync(connection);
        var liveExpiry = FixedNow.ToUnixTimeSeconds() + 30;
        await OpenMigrationAsync(connection, startedAt: 0, leaseOwner: "other:2:y", leaseExpiresAt: liveExpiry);

        var logger = new FakeLogger<EntryEmbedder>();
        var embedder = NewEmbedder(logger, new RecordingMeasurementRecorder());

        (await embedder.DrainMigrationAsync(connection, Ct)).ShouldBeFalse();

        logger.Collector.GetSnapshot().ShouldContain(r => r.Id.Id == 1010 && r.Level == LogLevel.Debug);
    }

    /// <summary>WP-P4-3: the open-migration re-check under the lease (S7) — the row closed after
    /// acquisition, so the pass returns false and emits 1011 instead of draining a ghost migration.
    /// The substitute lease stands in for the race window: acquire succeeds, then the re-check
    /// sees the row already finished.</summary>
    [RetryFact]
    public async Task Drain_WhenTheMigrationClosedAfterAcquisition_ReturnsFalseAndEmits1011()
    {
        await using var connection = await _factory.OpenBankAsync(Ct);
        await SeedPendingRowsAsync(connection, 1);
        await ConfigureProviderAsync(connection);
        await OpenMigrationAsync(connection, startedAt: 0);
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE model_migration SET finished_at = 12345 WHERE id = 1", cancellationToken: Ct));

        var lease = Substitute.For<IModelMigrationLease>();
        lease.TryAcquireAsync(Arg.Any<SqliteConnection>(), Arg.Any<CancellationToken>()).Returns(true);
        var logger = new FakeLogger<EntryEmbedder>();
        var embedder = new EntryEmbedder(new CountingEmbeddingService(), lease, _time,
            new VecDimensionReconciler(), new EmbedDrainReporter(new RecordingMeasurementRecorder(), _time),
            TestTelemetry.None, logger);

        (await embedder.DrainMigrationAsync(connection, Ct)).ShouldBeFalse();

        logger.Collector.GetSnapshot().ShouldContain(r => r.Id.Id == 1011 && r.Level == LogLevel.Debug);
    }

    /// <summary>WP-P4-3, M8 shape: a blank provider emits 1012 Warning naming the state — once per
    /// process per migration — and the drain still THROWS: the outage is only fixed by the
    /// model-reset guard, and returning false would pretend otherwise. On the pre-lane code this
    /// test goes red by throwing from CreateGenerator with no 1012 at all.</summary>
    [RetryFact]
    public async Task Drain_WithNoProviderConfigured_Emits1012OncePerMigrationAndStillThrows()
    {
        await using var connection = await _factory.OpenBankAsync(Ct);
        await SeedPendingRowsAsync(connection, 3);
        await OpenMigrationAsync(connection, startedAt: 42); // NO provider setting

        var logger = new FakeLogger<EntryEmbedder>();
        var embedder = NewEmbedder(logger, new RecordingMeasurementRecorder(),
            embeddings: new ProviderAwareEmbeddingService());

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => embedder.DrainMigrationAsync(connection, Ct));
        ex.Message.ShouldContain("no embedding provider");

        logger.Collector.GetSnapshot().ShouldContain(r => r.Id.Id == 1012 && r.Level == LogLevel.Warning);
        (await ReadFinishedAtAsync(connection)).ShouldBeNull("the migration stays open — only the model-reset guard closes it");

        // Once per process per migration: a second relay pass on the SAME migration row does not re-warn.
        await Should.ThrowAsync<InvalidOperationException>(() => embedder.DrainMigrationAsync(connection, Ct));
        logger.Collector.GetSnapshot().Count(r => r.Id.Id == 1012).ShouldBe(1,
            "1012 must not flood the 15s poll — one Warning per process per migration");
    }

    private static async Task SeedPendingRowsAsync(SqliteConnection connection, int count)
    {
        var values = string.Join(", ", Enumerable.Range(0, count)
            .Select(i => $"('h{i}', 'value {i}', 'p', 'project', 0, 0, 'pending')"));
        await connection.ExecuteAsync(new CommandDefinition(
            $"INSERT INTO entries(hash, value, project_id, scope, created_at, updated_at, embed_state) VALUES {values}",
            cancellationToken: Ct));
    }

    private static async Task ConfigureProviderAsync(SqliteConnection connection) =>
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO settings(key, value) VALUES ('embedding.provider', 'local') " +
            "ON CONFLICT(key) DO UPDATE SET value = excluded.value", cancellationToken: Ct));

    private static async Task OpenMigrationAsync(SqliteConnection connection, long startedAt,
        string? leaseOwner = null, long? leaseExpiresAt = null) =>
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO model_migration(id, provider, model, base_url, engine, started_at, finished_at, lease_owner, lease_expires_at)
            VALUES (1, 'local', NULL, NULL, 'test-engine', @startedAt, NULL, @leaseOwner, @leaseExpiresAt)
            ON CONFLICT(id) DO UPDATE SET started_at = @startedAt, finished_at = NULL,
                lease_owner = @leaseOwner, lease_expires_at = @leaseExpiresAt
            """,
            new { startedAt, leaseOwner, leaseExpiresAt }, cancellationToken: Ct));

    private static async Task<long?> ReadFinishedAtAsync(SqliteConnection connection) =>
        await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
            "SELECT finished_at FROM model_migration WHERE id = 1", cancellationToken: Ct));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Mimics the real EmbeddingService's provider switch: CreateGenerator throws on a
    /// blank provider — the pre-lane behaviour the 1012 guard replaces with a named warning.</summary>
    private sealed class ProviderAwareEmbeddingService : IEmbeddingService
    {
        public string EngineFingerprint(string provider, string? model, string? baseUrl) =>
            $"test:{provider}:{model}@{baseUrl}";

        public IEmbeddingGenerator<string, Embedding<float>> CreateGenerator(EmbeddingSettings settings)
        {
            if (string.IsNullOrWhiteSpace(settings.Provider))
            {
                throw new ArgumentOutOfRangeException(nameof(settings), settings.Provider,
                    "Unknown embedding provider; expected 'local' or 'openai'.");
            }

            return new CountingEmbeddingService().CreateGenerator(settings);
        }

        public string TrimQueryToWindow(EmbeddingSettings settings, string query) => query;

        public int ResolveChunkBudgetFor(EmbeddingSettings settings) => OnnxEmbeddingGenerator.MaxContentTokens;

        public int ResolveDimensions(EmbeddingSettings settings) => 384;

        public IEmbeddingTokenizer? ResolveTokenizer(EmbeddingSettings settings) => null;
    }
}
