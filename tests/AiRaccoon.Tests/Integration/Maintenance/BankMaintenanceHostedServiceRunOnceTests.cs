using System.Diagnostics;
using AiRaccoon.Core.Memory.Filtering;
using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Observability;
using AiRaccoon.Tests.Unit.Observability;
using AiRaccoon.Tests.Unit.Watch;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

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
    private readonly FakeTimeProvider _time;
    private readonly FakeLogger<BankMaintenanceHostedService> _logger;
    private readonly BackgroundTelemetryProbe _probe = new(BankMaintenanceHostedService.OperationName);
    private readonly FakeWatchMemoryStore _memory = new();
    private readonly BankMaintenanceHostedService _service;

    public BankMaintenanceHostedServiceRunOnceTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _time = new FakeTimeProvider(FixedNow);
        _logger = new FakeLogger<BankMaintenanceHostedService>();
        _service = new BankMaintenanceHostedService(_factory, _time, _probe.Telemetry, _logger, _memory, NoOpNoiseEntryStore.Instance,
            new SqlitePromotionQueueStore(_factory, _time), new SqliteSearchQualityService(_factory));
    }

    public void Dispose()
    {
        _probe.Dispose();
        TestData.DeleteTempRoot(_dataRoot);
    }

    private string WalPath => Path.Combine(_dataRoot, "memory.db-wal");

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
        var hash = insert.Parameters.Add("@hash", Microsoft.Data.Sqlite.SqliteType.Text);
        var path = insert.Parameters.Add("@path", Microsoft.Data.Sqlite.SqliteType.Text);
        var value = insert.Parameters.Add("@value", Microsoft.Data.Sqlite.SqliteType.Text);
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

    [Fact]
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

    [Fact]
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

    [Fact]
    public async Task RunOnce_VacuumNotDue_OnFirstTick_SeedsTheClock()
    {
        await SeedFreelistAsync(TestContext.Current.CancellationToken);
        var freelistBefore = await ReadFreelistCountAsync(TestContext.Current.CancellationToken);
        freelistBefore.ShouldBeGreaterThan(0);

        // First run seeds the vacuum clock and skips; a second run without advancing
        // must also skip — the seed survives.
        await _service.RunOnceAsync(TestContext.Current.CancellationToken);
        await _service.RunOnceAsync(TestContext.Current.CancellationToken);

        (await ReadFreelistCountAsync(TestContext.Current.CancellationToken)).ShouldBe(freelistBefore);
        _logger.Collector.GetSnapshot().ShouldNotContain(r => r.Id.Id == 512);
    }

    [Fact]
    public async Task RunOnce_VacuumDeferred_WhenBankBusy_ThenRunsOnRetry()
    {
        await SeedFreelistAsync(TestContext.Current.CancellationToken);
        await _service.RunOnceAsync(TestContext.Current.CancellationToken); // seeds the clock

        // SQLite quirk: a reader alone never blocks VACUUM, only the write lock does —
        // holding one forces SQLITE_BUSY, so the pass defers (Warning 516) and retries.
        await using var writer = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await using (var begin = writer.CreateCommand())
        {
            begin.CommandText = "BEGIN IMMEDIATE";
            await begin.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        _time.Advance(TimeSpan.FromDays(7));
        await _service.RunOnceAsync(TestContext.Current.CancellationToken);

        _logger.Collector.GetSnapshot().ShouldContain(r => r.Id.Id == 516 && r.Level == LogLevel.Warning);
        _logger.Collector.GetSnapshot().ShouldNotContain(r => r.Id.Id == 513);
        (await ReadFreelistCountAsync(TestContext.Current.CancellationToken)).ShouldBeGreaterThan(0);

        await using (var rollback = writer.CreateCommand())
        {
            rollback.CommandText = "ROLLBACK";
            await rollback.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await _service.RunOnceAsync(TestContext.Current.CancellationToken); // retry: vacuum now runs
        (await ReadFreelistCountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        _logger.Collector.GetSnapshot().ShouldContain(r => r.Id.Id == 512);
    }

    [Fact]
    public async Task RunOnce_VacuumAndAnalyze_AfterIntervalElapses()
    {
        await _service.RunOnceAsync(TestContext.Current.CancellationToken); // seeds the clock
        await SeedFreelistAsync(TestContext.Current.CancellationToken);
        (await ReadFreelistCountAsync(TestContext.Current.CancellationToken)).ShouldBeGreaterThan(0);

        _time.Advance(TimeSpan.FromDays(7));

        await _service.RunOnceAsync(TestContext.Current.CancellationToken);

        // The vacuum pass ends with a second checkpoint: the WAL the VACUUM rewrites
        // through must not sit untruncated until the next tick.
        new FileInfo(WalPath).Length.ShouldBe(0);
        (await ReadFreelistCountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        (await CountStatTablesAsync(TestContext.Current.CancellationToken)).ShouldBe(1);
        _logger.Collector.GetSnapshot().ShouldContain(r => r.Id.Id == 512);
    }

    [Fact]
    public async Task RunOnce_VacuumUsesConfiguredIntervalDays()
    {
        await InsertSettingAsync("maintenance.vacuum-interval-days.global", "1",
            TestContext.Current.CancellationToken);
        await SeedFreelistAsync(TestContext.Current.CancellationToken);

        await _service.RunOnceAsync(TestContext.Current.CancellationToken); // seeds the clock

        _time.Advance(TimeSpan.FromDays(1));

        await _service.RunOnceAsync(TestContext.Current.CancellationToken);

        (await ReadFreelistCountAsync(TestContext.Current.CancellationToken)).ShouldBe(0);
        _logger.Collector.GetSnapshot().ShouldContain(r => r.Id.Id == 512);
    }

    [Fact]
    public async Task RunOnce_EmitsASpanAndADurationForThePass()
    {
        await _service.RunOnceAsync(TestContext.Current.CancellationToken);

        var span = _probe.Spans.ShouldHaveSingleItem();
        span.Source.Name.ShouldBe(OtlpNames.BackgroundScope);
        span.Status.ShouldBe(ActivityStatusCode.Ok);
        _probe.Durations.ShouldHaveSingleItem().Tags["result"].ShouldBe("success");
    }

    /// <summary>
    ///     A watch-triggered embed failure leaves a row `embed_state='pending'` with nothing else
    ///     retrying it (.NET-F1) — the maintenance pass is the only sweep, so it must pick up any
    ///     project reporting a nonzero PendingCount and retry EmbedPendingAsync for it.
    /// </summary>
    [Fact]
    public async Task RunOnce_ProjectHasPendingRows_RetriesEmbedForThatProject()
    {
        _memory.ProjectIds.Add("acme");
        _memory.PendingCounts["acme"] = 2;

        await _service.RunOnceAsync(TestContext.Current.CancellationToken);

        _memory.EmbedCalls.ShouldContain("acme");
    }

    [Fact]
    public async Task RunOnce_ProjectHasNoPendingRows_NeverCallsEmbedPending()
    {
        _memory.ProjectIds.Add("acme");
        _memory.PendingCounts["acme"] = 0;

        await _service.RunOnceAsync(TestContext.Current.CancellationToken);

        _memory.EmbedCalls.ShouldBeEmpty();
    }

    [Fact]
    public async Task RunOnce_MultipleProjectsPending_RetriesEachOne()
    {
        _memory.ProjectIds.Add("acme");
        _memory.ProjectIds.Add("beta");
        _memory.PendingCounts["acme"] = 1;
        _memory.PendingCounts["beta"] = 3;

        await _service.RunOnceAsync(TestContext.Current.CancellationToken);

        _memory.EmbedCalls.ShouldContain("acme");
        _memory.EmbedCalls.ShouldContain("beta");
    }

    /// <summary>A retry failure for one project must not abort the checkpoint/vacuum pass — same silent-failure discipline as the watch digest's own best-effort embed.</summary>
    [Fact]
    public async Task RunOnce_EmbedRetryThrows_PassStillSucceeds()
    {
        _memory.ProjectIds.Add("acme");
        _memory.PendingCounts["acme"] = 1;
        _memory.EmbedError = new InvalidOperationException("embed boom");

        await Should.NotThrowAsync(() => _service.RunOnceAsync(TestContext.Current.CancellationToken));

        _memory.EmbedCalls.ShouldContain("acme");
        _logger.Collector.GetSnapshot().ShouldContain(r => r.Id.Id == 510); // checkpoint still ran
    }

    [Fact]
    public async Task RunOnce_WhenThePassThrows_RecordsTheFailure()
    {
        // A directory where the bank file belongs: opening the bank fails, so the pass fails.
        var brokenRoot = TestData.CreateTempRoot("bank-maintenance-broken");
        Directory.CreateDirectory(Path.Combine(brokenRoot, "memory.db"));
        var options = TestData.CreateInfrastructureOptions(brokenRoot);
        using var probe = new BackgroundTelemetryProbe(BankMaintenanceHostedService.OperationName);
        var service = new BankMaintenanceHostedService(
            new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options)), _time, probe.Telemetry,
            _logger, _memory, NoOpNoiseEntryStore.Instance,
            new SqlitePromotionQueueStore(_factory, _time), new SqliteSearchQualityService(_factory));

        var thrown = await Should.ThrowAsync<Exception>(
            () => service.RunOnceAsync(TestContext.Current.CancellationToken));

        probe.Spans.ShouldHaveSingleItem().Status.ShouldBe(ActivityStatusCode.Error);
        var duration = probe.Durations.ShouldHaveSingleItem();
        duration.Tags["result"].ShouldBe("error");
        duration.Tags["error.type"].ShouldBe(thrown.GetType().Name);
        TestData.DeleteTempRoot(brokenRoot);
    }
}
