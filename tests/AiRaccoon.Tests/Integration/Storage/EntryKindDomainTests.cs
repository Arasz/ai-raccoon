using AiRaccoon.Infrastructure.Sqlite;
using Dapper;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Storage;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class EntryKindDomainTests
{
    [RetryTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FreshAndReleasedBanks_RefuseUnknownStoredKinds(bool releasedBank)
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.EnableExtensions();
        connection.LoadVector();
        await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        if (releasedBank)
        {
            await connection.ExecuteAsync("""
                DROP TRIGGER IF EXISTS vec_entries_au;
                DROP TRIGGER IF EXISTS vec_structure_au;
                ALTER TABLE entries DROP COLUMN entry_type;
                PRAGMA user_version = 19;
                PRAGMA application_id = 0;
                """);
            await MemorySchema.EnsureAsync(connection, TestContext.Current.CancellationToken);
        }

        await connection.ExecuteAsync("""
            INSERT INTO entries(hash,path,value,scope,project_id,created_at,updated_at)
            VALUES ('h','note','text','project','acme',1,1)
            """);
        var error = await Should.ThrowAsync<SqliteException>(() => connection.ExecuteAsync(
            "UPDATE entries SET entry_type = 'unrecognized' WHERE hash = 'h'"));
        error.SqliteErrorCode.ShouldBe(19);
        (await connection.ExecuteScalarAsync<string>("SELECT entry_type FROM entries WHERE hash='h'"))
            .ShouldBe("none");
    }
}
