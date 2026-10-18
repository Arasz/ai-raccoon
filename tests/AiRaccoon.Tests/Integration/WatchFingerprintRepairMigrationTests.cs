using AiRaccoon.Core.Ingestion;
using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using AiRaccoon.Infrastructure.Watch;
using AiRaccoon.Setup;
using Dapper;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     The v18 step drops fingerprints with no memory or code rows behind them, once, so a file a
///     pre-skip build fingerprinted while it could not index it (every `.ipynb` under 1.58) re-ingests.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class WatchFingerprintRepairMigrationTests : IDisposable
{
    private readonly string _dataRoot = TestData.CreateTempRoot("airaccoon-fingerprint-repair");

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    [RetryFact]
    public async Task EnsureAsync_OnAV17Bank_DropsOnlyFingerprintsWithNoRows()
    {
        await using var connection = await OpenInMemoryAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO entries (hash, path, value, source_file, scope, project_id, created_at, updated_at)
                VALUES ('h1', '/repo/a.md', 'v', '/repo/a.md', 'project', 'acme', 1, 1);
            INSERT INTO code_entries (hash, path, value, source_file, line_start, line_end, project_id, created_at, updated_at)
                VALUES ('h2', '/repo/b.cs', 'v', '/repo/b.cs', 1, 1, 'acme', 1, 1);
            INSERT INTO watch_files (project_id, path, file_hash, updated_at) VALUES ('acme', '/repo/a.md', 'f1', 1);
            INSERT INTO watch_files (project_id, path, file_hash, updated_at) VALUES ('acme', '/repo/b.cs', 'f2', 1);
            INSERT INTO watch_files (project_id, path, file_hash, updated_at) VALUES ('acme', '/repo/c.ipynb', 'f3', 1);
            INSERT INTO watch_files (project_id, path, file_hash, updated_at) VALUES ('other', '/repo/a.md', 'f4', 1);
            PRAGMA user_version = 17;
            """, cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var kept = await connection.QueryAsync<string>(new CommandDefinition(
            "SELECT project_id || ':' || path FROM watch_files ORDER BY 1",
            cancellationToken: TestContext.Current.CancellationToken));
        kept.ShouldBe(["acme:/repo/a.md", "acme:/repo/b.cs"],
            "only a fingerprint whose own project holds memory or code rows for the path survives");
        (await connection.ExecuteScalarAsync<long>(new CommandDefinition("PRAGMA user_version",
            cancellationToken: TestContext.Current.CancellationToken))).ShouldBe(MemorySchema.CurrentVersion);
    }

    [RetryFact]
    public async Task EnsureAsync_OnACurrentBank_LeavesRowlessFingerprintsAlone()
    {
        await using var connection = await OpenInMemoryAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            "INSERT INTO watch_files (project_id, path, file_hash, updated_at) VALUES ('acme', '/repo/empty.md', 'f', 1)",
            cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await connection.ExecuteScalarAsync<long>(new CommandDefinition("SELECT count(*) FROM watch_files",
            cancellationToken: TestContext.Current.CancellationToken))).ShouldBe(1L,
            "the repair runs once per bank, not on every open — a blank file's fingerprint has no rows by design");
    }

    [RetryFact]
    public async Task DigestAfterRepair_NotebookFingerprintedWhileUnsupported_IndexesWithoutAContentChange()
    {
        var notebook = Path.Combine(_dataRoot, "analysis.ipynb");
        await File.WriteAllTextAsync(notebook,
            """{"nbformat":4,"metadata":{},"cells":[{"cell_type":"markdown","source":["Quarterly churn analysis notes."]}]}""",
            TestContext.Current.CancellationToken);
        var normalized = IngestPath.Normalize(notebook);
        var hash = WatchDigestExecutor.ComputeHash(normalized, await File.ReadAllTextAsync(notebook, TestContext.Current.CancellationToken));

        await using (var legacy = BuildProvider())
        {
            var store = legacy.GetRequiredService<IMemoryStore>();
            await store.SetSettingAsync(IngestScopeKeys.ScopeProject("acme"), IngestScopeKeys.Serialize([_dataRoot]),
                TestContext.Current.CancellationToken);
            await using var connection = await legacy.GetRequiredService<ISqliteConnectionFactory>()
                .OpenBankAsync(TestContext.Current.CancellationToken);
            await connection.ExecuteAsync(new CommandDefinition(
                """
                INSERT INTO watch_files (project_id, path, file_hash, updated_at) VALUES ('acme', @path, @hash, 1);
                PRAGMA user_version = 17;
                """, new { path = normalized, hash }, cancellationToken: TestContext.Current.CancellationToken));
        }

        await using var upgraded = BuildProvider();
        var digest = await upgraded.GetRequiredService<IMemoryStore>()
            .ReplaceIfFileChangedAsync("acme", normalized, hash, TestContext.Current.CancellationToken);

        digest.Replaced.ShouldBeTrue("the repair dropped the rowless fingerprint, so the unchanged notebook is digested");
        await using var check = await upgraded.GetRequiredService<ISqliteConnectionFactory>()
            .OpenBankAsync(TestContext.Current.CancellationToken);
        (await check.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT count(*) FROM entries WHERE project_id = 'acme' AND path = @path", new { path = normalized },
            cancellationToken: TestContext.Current.CancellationToken))).ShouldBeGreaterThan(0L);
    }

    private ServiceProvider BuildProvider()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
        services.RegisterCoreMemoryServices(TestData.CreateInfrastructureOptions(_dataRoot));
        return services.BuildServiceProvider();
    }

    private static async Task<SqliteConnection> OpenInMemoryAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }
}
