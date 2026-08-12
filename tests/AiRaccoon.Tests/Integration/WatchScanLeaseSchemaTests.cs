using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     WP4 persistence layer (docs/plans/2026-08-07-watch-scan-runaway-fix.md D-2): the
///     scan-owner/expiry columns on `watches` and the acquire/renew/release SQL, driven
///     against real SQLite. No lease abstraction exists yet — these pin the schema and SQL only.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
[Collection(WatchIntegrationCollection.Name)]
public sealed class WatchScanLeaseSchemaTests : IDisposable
{
    private const string ProjectId = "acme";
    private const string Path = "/repo/docs";

    // Legacy pre-D-2 shape: `watches` with no lease columns.
    private const string LegacyWatchesDdl = """
                                            CREATE TABLE IF NOT EXISTS watches (
                                                project_id      TEXT NOT NULL,
                                                path            TEXT NOT NULL,
                                                created_at      INTEGER NOT NULL,
                                                last_change_ts  INTEGER NOT NULL,
                                                PRIMARY KEY (project_id, path)
                                            );
                                            """;

    private const long BaseTime = 1_700_000_000;
    private const long Past = BaseTime - 120;
    private const long Future = BaseTime + 120;

    private readonly string _dataRoot = TestData.CreateTempRoot("ai-raccoon-watch-lease-schema");
    private readonly SqliteConnectionFactory _factory;

    public WatchScanLeaseSchemaTests()
    {
        var options = TestData.CreateInfrastructureOptions(_dataRoot);
        _factory = new SqliteConnectionFactory(options, NullKeyProvider.Resolver(options));
    }

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public async Task EnsureAsync_OnALegacyBankWithoutLeaseColumns_AddsThem()
    {
        await CreateLegacyBankAsync();

        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);

        var columns = (await connection.QueryAsync<string>(
                new CommandDefinition("SELECT name FROM pragma_table_info('watches')",
                    cancellationToken: TestContext.Current.CancellationToken)))
            .ToHashSet(StringComparer.Ordinal);
        columns.ShouldContain("scan_owner");
        columns.ShouldContain("scan_lease_expires_at");
    }

    [Fact]
    public async Task AcquireWatchScanLease_WhenUnowned_GrantsTheLease()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await InsertWatchAsync(connection, scanOwner: null, scanLeaseExpiresAt: 0);

        var affected = await AcquireAsync(connection, owner: "owner-a", now: BaseTime, expiresAt: Future);

        affected.ShouldBe(1);
        (await SelectLeaseAsync(connection)).ScanOwner.ShouldBe("owner-a");
    }

    [Fact]
    public async Task AcquireWatchScanLease_WhenHeldByALiveOwner_IsDenied()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await InsertWatchAsync(connection, scanOwner: "owner-a", scanLeaseExpiresAt: Future);

        var affected = await AcquireAsync(connection, owner: "owner-b", now: BaseTime, expiresAt: Future);

        affected.ShouldBe(0);
        (await SelectLeaseAsync(connection)).ScanOwner.ShouldBe("owner-a");
    }

    [Fact]
    public async Task AcquireWatchScanLease_AfterTheHoldersLeaseExpired_IsGrantedToTheNextOwner()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await InsertWatchAsync(connection, scanOwner: "owner-a", scanLeaseExpiresAt: Past);

        var affected = await AcquireAsync(connection, owner: "owner-b", now: BaseTime, expiresAt: Future);

        affected.ShouldBe(1);
        (await SelectLeaseAsync(connection)).ScanOwner.ShouldBe("owner-b");
    }

    [Fact]
    public async Task AcquireWatchScanLease_ByTheSameOwnerAgain_IsReEntrant()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await InsertWatchAsync(connection, scanOwner: "owner-a", scanLeaseExpiresAt: Future);

        var affected = await AcquireAsync(connection, owner: "owner-a", now: BaseTime, expiresAt: Future + 60);

        affected.ShouldBe(1);
        var lease = await SelectLeaseAsync(connection);
        lease.ScanOwner.ShouldBe("owner-a");
        lease.ScanLeaseExpiresAt.ShouldBe(Future + 60);
    }

    [Fact]
    public async Task RenewWatchScanLease_ByANonOwner_ReturnsZeroRows()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await InsertWatchAsync(connection, scanOwner: "owner-a", scanLeaseExpiresAt: Future);

        var affected = await connection.ExecuteAsync(new CommandDefinition(MemorySql.RenewWatchScanLease,
            new { projectId = ProjectId, path = Path, owner = "owner-b", expiresAt = Future + 60 },
            cancellationToken: TestContext.Current.CancellationToken));

        affected.ShouldBe(0);
    }

    [Fact]
    public async Task RenewWatchScanLease_AfterTheWatchRowWasRemoved_ReturnsZeroRows()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await InsertWatchAsync(connection, scanOwner: "owner-a", scanLeaseExpiresAt: Future);
        await connection.ExecuteAsync(new CommandDefinition(MemorySql.DeleteWatch,
            new { projectId = ProjectId, path = Path },
            cancellationToken: TestContext.Current.CancellationToken));

        var affected = await connection.ExecuteAsync(new CommandDefinition(MemorySql.RenewWatchScanLease,
            new { projectId = ProjectId, path = Path, owner = "owner-a", expiresAt = Future + 60 },
            cancellationToken: TestContext.Current.CancellationToken));

        affected.ShouldBe(0);
    }

    [Fact]
    public async Task ReleaseWatchScanLease_ByANonOwner_LeavesTheLeaseIntact()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await InsertWatchAsync(connection, scanOwner: "owner-a", scanLeaseExpiresAt: Future);

        var affected = await connection.ExecuteAsync(new CommandDefinition(MemorySql.ReleaseWatchScanLease,
            new { projectId = ProjectId, path = Path, owner = "owner-b" },
            cancellationToken: TestContext.Current.CancellationToken));

        affected.ShouldBe(0);
        (await SelectLeaseAsync(connection)).ScanOwner.ShouldBe("owner-a");
    }

    [Fact]
    public async Task ReleaseWatchScanLease_ByTheOwner_ClearsIt()
    {
        await using var connection = await _factory.OpenBankAsync(TestContext.Current.CancellationToken);
        await InsertWatchAsync(connection, scanOwner: "owner-a", scanLeaseExpiresAt: Future);

        var affected = await connection.ExecuteAsync(new CommandDefinition(MemorySql.ReleaseWatchScanLease,
            new { projectId = ProjectId, path = Path, owner = "owner-a" },
            cancellationToken: TestContext.Current.CancellationToken));

        affected.ShouldBe(1);
        var lease = await SelectLeaseAsync(connection);
        lease.ScanOwner.ShouldBeNull();
        lease.ScanLeaseExpiresAt.ShouldBe(0);
    }

    private static Task<int> AcquireAsync(SqliteConnection connection, string owner, long now, long expiresAt) =>
        connection.ExecuteAsync(new CommandDefinition(MemorySql.AcquireWatchScanLease,
            new { projectId = ProjectId, path = Path, owner, now, expiresAt },
            cancellationToken: TestContext.Current.CancellationToken));

    private static async Task InsertWatchAsync(SqliteConnection connection, string? scanOwner, long scanLeaseExpiresAt) =>
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO watches (project_id, path, created_at, last_change_ts, scan_owner, scan_lease_expires_at)
            VALUES (@projectId, @path, @createdAt, @lastChangeTs, @scanOwner, @scanLeaseExpiresAt)
            """,
            new { projectId = ProjectId, path = Path, createdAt = BaseTime, lastChangeTs = BaseTime, scanOwner, scanLeaseExpiresAt },
            cancellationToken: TestContext.Current.CancellationToken));

    private static async Task<(string? ScanOwner, long ScanLeaseExpiresAt)> SelectLeaseAsync(SqliteConnection connection) =>
        await connection.QuerySingleAsync<(string? ScanOwner, long ScanLeaseExpiresAt)>(new CommandDefinition(
            """
            SELECT scan_owner AS ScanOwner, scan_lease_expires_at AS ScanLeaseExpiresAt
            FROM watches WHERE project_id = @projectId AND path = @path
            """,
            new { projectId = ProjectId, path = Path },
            cancellationToken: TestContext.Current.CancellationToken));

    private async Task CreateLegacyBankAsync()
    {
        var bankPath = System.IO.Path.Combine(_dataRoot, "memory.db");
        Directory.CreateDirectory(_dataRoot);
        await using var connection = new SqliteConnection($"Data Source={bankPath}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = LegacyWatchesDdl;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
