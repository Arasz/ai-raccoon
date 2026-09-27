using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration;

/// <summary>
///     ADR-0121: <c>watch_files.size</c> arrives on the digest-gated path, with no version bump;
///     fingerprints written before it keep a NULL size until their next digest.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class MemorySchemaWatchFilesSizeTests
{
    [RetryFact]
    public async Task FreshBank_WatchFilesHasANullableSizeColumn()
    {
        await using var connection = await OpenAsync();

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        var size = (await connection.QueryAsync<(string Name, long NotNull)>(new CommandDefinition(
                """SELECT name, "notnull" FROM pragma_table_info('watch_files')""",
                cancellationToken: TestContext.Current.CancellationToken)))
            .Single(c => c.Name == "size");
        size.NotNull.ShouldBe(0);
    }

    [RetryFact]
    public async Task BankWithoutTheColumn_GainsItOnTheNextOpen_AndKeepsItsRowsWithANullSize()
    {
        await using var connection = await OpenAsync();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        await connection.ExecuteAsync(new CommandDefinition(
            """
            DROP TABLE watch_files;
            CREATE TABLE watch_files (
                project_id TEXT NOT NULL, path TEXT NOT NULL, file_hash TEXT NOT NULL,
                updated_at INTEGER NOT NULL, PRIMARY KEY (project_id, path));
            INSERT INTO watch_files VALUES ('acme', '/repo/a.md', 'h', 100);
            PRAGMA application_id = 0;
            """, cancellationToken: TestContext.Current.CancellationToken));

        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);

        (await connection.ExecuteScalarAsync<long?>(new CommandDefinition(
                "SELECT size FROM watch_files WHERE path = '/repo/a.md'",
                cancellationToken: TestContext.Current.CancellationToken)))
            .ShouldBeNull();
    }

    private static async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }
}
