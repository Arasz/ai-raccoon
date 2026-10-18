using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Shouldly;
using SQLitePCL;
using Xunit;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     SQLite's allocation statistics take one process-wide mutex on every malloc and free, so
///     concurrent connections queue behind it. <see cref="SqliteEncryptionInit" /> turns the
///     statistics off before the library initialises; with them off the engine reports no usage.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SqliteMemoryStatusTests
{
    [Fact]
    public void EnsureInitialized_DisablesTheGlobalAllocationStatistics()
    {
        SqliteEncryptionInit.EnsureInitialized();

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE t(x TEXT); INSERT INTO t VALUES (hex(randomblob(4096)));";
        command.ExecuteNonQuery();

        raw.sqlite3_memory_used().ShouldBe(0,
            "allocation statistics are on: every SQLite malloc/free serialises on one global mutex");
    }
}
