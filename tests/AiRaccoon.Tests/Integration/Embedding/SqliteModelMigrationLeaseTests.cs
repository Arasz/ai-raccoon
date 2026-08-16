using AiRaccoon.Infrastructure.Embedding;
using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Extensions.Time.Testing;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Embedding;

/// <summary>
///     SqliteModelMigrationLease against a real bank (ADR-0076): only one relay may drain the open
///     migration at a time, and a crashed holder's lease is freed by expiry alone — the same shape
///     as SqliteWatchScanLease (docs/plans/2026-08-07-watch-scan-runaway-fix.md WP4).
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SqliteModelMigrationLeaseTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-model-migration-lease");
    private readonly SqliteConnectionFactory _factory;
    private readonly FakeTimeProvider _time = new(Now);

    public SqliteModelMigrationLeaseTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    private SqliteModelMigrationLease NewLease() => new(_time);

    private async Task OpenMigrationAsync()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO model_migration (id, provider, model, base_url, engine, started_at, finished_at) " +
            "VALUES (1, 'local', NULL, NULL, 'local:bundled', @startedAt, NULL)",
            new { startedAt = Now.ToUnixTimeSeconds() }, cancellationToken: TestContext.Current.CancellationToken));
    }

    private Task<Microsoft.Data.Sqlite.SqliteConnection> OpenAsync() =>
        _factory.OpenBankAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task Owner_IsDistinctPerInstance() => NewLease().Owner.ShouldNotBe(NewLease().Owner);

    [Fact]
    public async Task TryAcquireAsync_WhenNoMigrationIsOpen_IsDenied()
    {
        await using var connection = await OpenAsync();

        var granted = await NewLease().TryAcquireAsync(connection, TestContext.Current.CancellationToken);

        granted.ShouldBeFalse();
    }

    [Fact]
    public async Task TryAcquireAsync_WhenOpenAndUnowned_GrantsTheLease()
    {
        await OpenMigrationAsync();
        await using var connection = await OpenAsync();

        var granted = await NewLease().TryAcquireAsync(connection, TestContext.Current.CancellationToken);

        granted.ShouldBeTrue();
    }

    [Fact]
    public async Task TryAcquireAsync_WhenHeldByALiveOwner_IsDenied()
    {
        await OpenMigrationAsync();
        await using var connection = await OpenAsync();
        var holder = NewLease();
        var rival = NewLease();
        (await holder.TryAcquireAsync(connection, TestContext.Current.CancellationToken)).ShouldBeTrue();

        var granted = await rival.TryAcquireAsync(connection, TestContext.Current.CancellationToken);

        granted.ShouldBeFalse();
    }

    /// <summary>A crashed relay never releases; expiry is the only thing that frees the lease (ADR-0037's hazard).</summary>
    [Fact]
    public async Task TryAcquireAsync_AfterTheHoldersLeaseExpired_IsGrantedToTheNextOwner()
    {
        await OpenMigrationAsync();
        await using var connection = await OpenAsync();
        var crashed = NewLease();
        await crashed.TryAcquireAsync(connection, TestContext.Current.CancellationToken);

        _time.Advance(SqliteModelMigrationLease.LeaseTtl + TimeSpan.FromSeconds(1));
        var granted = await NewLease().TryAcquireAsync(connection, TestContext.Current.CancellationToken);

        granted.ShouldBeTrue();
    }

    [Fact]
    public async Task TryAcquireAsync_BeforeTheHoldersLeaseExpires_IsStillDenied()
    {
        await OpenMigrationAsync();
        await using var connection = await OpenAsync();
        await NewLease().TryAcquireAsync(connection, TestContext.Current.CancellationToken);

        _time.Advance(SqliteModelMigrationLease.LeaseTtl - TimeSpan.FromSeconds(1));
        var granted = await NewLease().TryAcquireAsync(connection, TestContext.Current.CancellationToken);

        granted.ShouldBeFalse();
    }

    [Fact]
    public async Task TryRenewAsync_ByTheOwner_KeepsTheLeaseBeyondTheOriginalExpiry()
    {
        await OpenMigrationAsync();
        await using var connection = await OpenAsync();
        var holder = NewLease();
        await holder.TryAcquireAsync(connection, TestContext.Current.CancellationToken);

        // Renew at the TTL's halfway point, then advance past where the ORIGINAL (unrenewed)
        // expiry would have fallen — proves the renewal, not the original acquire, is what still
        // holds the rival off.
        var halfway = TimeSpan.FromTicks(SqliteModelMigrationLease.LeaseTtl.Ticks / 2);
        _time.Advance(halfway);
        (await holder.TryRenewAsync(connection, TestContext.Current.CancellationToken)).ShouldBeTrue();

        _time.Advance(halfway + TimeSpan.FromSeconds(1));
        var granted = await NewLease().TryAcquireAsync(connection, TestContext.Current.CancellationToken);

        granted.ShouldBeFalse();
    }

    [Fact]
    public async Task TryRenewAsync_ByANonOwner_ReturnsFalse()
    {
        await OpenMigrationAsync();
        await using var connection = await OpenAsync();
        await NewLease().TryAcquireAsync(connection, TestContext.Current.CancellationToken);

        var renewed = await NewLease().TryRenewAsync(connection, TestContext.Current.CancellationToken);

        renewed.ShouldBeFalse();
    }

    [Fact]
    public async Task ReleaseAsync_ByTheOwner_LetsTheNextOwnerAcquireImmediately()
    {
        await OpenMigrationAsync();
        await using var connection = await OpenAsync();
        var holder = NewLease();
        await holder.TryAcquireAsync(connection, TestContext.Current.CancellationToken);

        await holder.ReleaseAsync(connection, TestContext.Current.CancellationToken);
        var granted = await NewLease().TryAcquireAsync(connection, TestContext.Current.CancellationToken);

        granted.ShouldBeTrue();
    }

    [Fact]
    public async Task ReleaseAsync_ByANonOwner_LeavesTheLeaseIntact()
    {
        await OpenMigrationAsync();
        await using var connection = await OpenAsync();
        var holder = NewLease();
        await holder.TryAcquireAsync(connection, TestContext.Current.CancellationToken);

        await NewLease().ReleaseAsync(connection, TestContext.Current.CancellationToken);
        var granted = await NewLease().TryAcquireAsync(connection, TestContext.Current.CancellationToken);

        granted.ShouldBeFalse();
    }

    /// <summary>Once the row is marked finished, the lease is no longer acquirable — the migration is done.</summary>
    [Fact]
    public async Task TryAcquireAsync_OnceTheMigrationIsFinished_IsDenied()
    {
        await OpenMigrationAsync();
        await using var connection = await OpenAsync();
        await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE model_migration SET finished_at = @now WHERE id = 1",
            new { now = Now.ToUnixTimeSeconds() }, cancellationToken: TestContext.Current.CancellationToken));

        var granted = await NewLease().TryAcquireAsync(connection, TestContext.Current.CancellationToken);

        granted.ShouldBeFalse();
    }
}
