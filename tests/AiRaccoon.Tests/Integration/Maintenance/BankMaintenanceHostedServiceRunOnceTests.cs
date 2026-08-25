using System.Diagnostics;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Observability;
using AiRaccoon.Tests.Unit.Observability;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Maintenance;

/// <summary>
///     Bank maintenance run contract: every run checkpoints the WAL (TRUNCATE) reading the
///     returned tuple (busy&gt;0 defers with a Warning, never throws), and vacuum+analyze
///     rides the vacuum cadence seeded on the first run (short-lived processes never vacuum).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class BankMaintenanceHostedServiceRunOnceTests : IDisposable
{
    private static readonly DateTimeOffset FixedNow = new(2026, 8, 7, 12, 0, 0, TimeSpan.Zero);

    private readonly string _dataRoot = TestData.CreateTempRoot("bank-maintenance");
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeLogger<BankMaintenanceHostedService> _logger;
    private readonly BackgroundTelemetryProbe _probe = new(BankMaintenanceHostedService.OperationName);
    private readonly BankMaintenanceHostedService _service;
    private readonly FakeTimeProvider _time;

    public BankMaintenanceHostedServiceRunOnceTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _time = new FakeTimeProvider(FixedNow);
        _logger = new FakeLogger<BankMaintenanceHostedService>();
        _service = new BankMaintenanceHostedService(_factory, _time, _probe.Telemetry, _logger, NoOpNoiseEntryStore.Instance,
            new SqlitePromotionQueueStore(_factory, _time), new SqliteSearchQualityService(_factory, NullLogger<SqliteSearchQualityService>.Instance));
    }

    private string WalPath => Path.Combine(_dataRoot, "memory.db-wal");

    public void Dispose()
    {
        _probe.Dispose();
        TestData.DeleteTempRoot(_dataRoot);
    }

    private async Task InsertSettingAsync(string key, string value, CancellationToken cancellationToken)
    {
        await using var connection = await _factory.OpenBankAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT OR REPLACE INTO settings (key, value) VALUES (@key, @value)";
        command.Parameters.AddWithValue("@key", key);
        command.Parameters.AddWithValue("@value", value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Writes then deletes rows so the bank's freelist grows; the WAL carries the churn.</summary>
    private async Task SeedFreelistAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _factory.OpenBankAsync(cancellationToken);
        await using var insert = connection.CreateCommand();
        insert.CommandText = """
                             INSERT INTO entries (hash, path, value, scope, project_id, created_at, updated_at)
                             VALUES (@hash, @path, @value, 'project', 'acme', 0, 0)
                             """;
        var hash = insert.Parameters.Add("@hash", SqliteType.Text);
        var path = insert.Parameters.Add("@path", SqliteType.Text);
        var value = insert.Parameters.Add("@value", SqliteType.Text);
        for (var i = 0; i < 2000; i++)
        {
            hash.Value = $"h{i}";
            path.Value = $"p{i}.md";
            value.Value = new string('x', 200);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var delete = connection.CreateCommand();
        delete.CommandText = "DELETE FROM entries WHERE project_id = 'acme'";
        await delete.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<long> ReadFreelistCountAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _factory.OpenBankAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA freelist_count";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task<long> CountStatTablesAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _factory.OpenBankAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE name = 'sqlite_stat1'";
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
    }

    [RetryFact]
    public async Task RunOnce_CheckpointTruncatesTheWal()
    {
        await InsertSettingAsync("probe.x", "1", TestContext.Current.CancellationToken);
        var wal = new FileInfo(WalPath);
        wal.Exists.ShouldBeTrue();
        wal.Length.ShouldBeGreaterThan(0);

        await _service.RunOnceAsync(TestContext.Current.CancellationToken);

        new FileInfo(WalPath).Length.ShouldBe(0);
        _logger.Collector.GetSnapshot().ShouldContain(r => r.Id.Id == 510);
    }

    [RetryFact]
    public async Task RunOnce_CheckpointDeferred_WhenReaderPinsTheWal()
    {
        await InsertSettingAsync("probe.x", "1", TestContext.Current.CancellationToken);
        var walBefore = new FileInfo(WalPath).Length;

        // A reader with an open snapshot pins the WAL: the checkpoint must defer
        // (busy>0, Warning 511), never throw. Contention defers rather than blocks
        // (ADR-0010): the maintenance connection's short busy_timeout avoids the stock 5s wait.
        await using var reader = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await using (var begin = reader.CreateCommand())
        {
            begin.CommandText = "BEGIN";
            await begin.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using (var select = reader.CreateCommand())
        {
            select.CommandText = "SELECT count(*) FROM settings";
            await select.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        }

        await _service.RunOnceAsync(TestContext.Current.CancellationToken);

        new FileInfo(WalPath).Length.ShouldBe(walBefore);
        _logger.Collector.GetSnapshot().ShouldContain(r => r.Id.Id == 511 && r.Level == LogLevel.Warning);
        _logger.Collector.GetSnapshot().ShouldNotContain(r => r.Id.Id == 510);
        _logger.Collector.GetSnapshot().ShouldNotContain(r => r.Id.Id == 513);

        await using (var rollback = reader.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK";
            await rollback.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
    }

    [RetryFact]
    public async Task RunOnce_EmitsASpanAndADurationForThePass()
    {
        await _service.RunOnceAsync(TestContext.Current.CancellationToken);

        var span = _probe.Spans.ShouldHaveSingleItem();
        span.Source.Name.ShouldBe(OtlpNames.BackgroundScope);
        span.Status.ShouldBe(ActivityStatusCode.Ok);
        _probe.Durations.ShouldHaveSingleItem().Tags["result"].ShouldBe("success");
    }

    [RetryFact]
    public async Task RunOnce_WhenThePassThrows_RecordsTheFailure()
    {
        // A directory where the bank file belongs: opening the bank fails, so the pass fails.
        var brokenRoot = TestData.CreateTempRoot("bank-maintenance-broken");
        Directory.CreateDirectory(Path.Combine(brokenRoot, "memory.db"));
        var options = TestData.CreateInfrastructureOptions(brokenRoot);
        using var probe = new BackgroundTelemetryProbe(BankMaintenanceHostedService.OperationName);
        var service = new BankMaintenanceHostedService(
            new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options)), _time, probe.Telemetry,
            _logger, NoOpNoiseEntryStore.Instance,
            new SqlitePromotionQueueStore(_factory, _time), new SqliteSearchQualityService(_factory, NullLogger<SqliteSearchQualityService>.Instance));

        var thrown = await Should.ThrowAsync<Exception>(() => service.RunOnceAsync(TestContext.Current.CancellationToken));

        probe.Spans.ShouldHaveSingleItem().Status.ShouldBe(ActivityStatusCode.Error);
        var duration = probe.Durations.ShouldHaveSingleItem();
        duration.Tags["result"].ShouldBe("error");
        duration.Tags["error.type"].ShouldBe(thrown.GetType().Name);
        TestData.DeleteTempRoot(brokenRoot);
    }
}
