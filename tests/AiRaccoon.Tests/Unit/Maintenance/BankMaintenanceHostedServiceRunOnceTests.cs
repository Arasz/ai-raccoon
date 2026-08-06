using AiRaccoon.Infrastructure.Maintenance;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Maintenance;

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
    private readonly BankMaintenanceHostedService _service;

    public BankMaintenanceHostedServiceRunOnceTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
        _time = new FakeTimeProvider(FixedNow);
        _logger = new FakeLogger<BankMaintenanceHostedService>();
        _service = new BankMaintenanceHostedService(_factory, _time, _logger);
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

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
        // (busy>0, Warning 511), never throw, and leave the WAL untruncated. The
        // maintenance connection's short busy timeout keeps this fast (a stock
        // busy_timeout would block the checkpoint for its full 5 seconds).
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
    public async Task RunOnce_VacuumAndAnalyze_AfterIntervalElapses()
    {
        await _service.RunOnceAsync(TestContext.Current.CancellationToken); // seeds the clock
        await SeedFreelistAsync(TestContext.Current.CancellationToken);
        (await ReadFreelistCountAsync(TestContext.Current.CancellationToken)).ShouldBeGreaterThan(0);

        _time.Advance(TimeSpan.FromDays(7));

        await _service.RunOnceAsync(TestContext.Current.CancellationToken);

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
}
