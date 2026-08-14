using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Integration.Storage;

[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class SqliteVectorTests : IDisposable
{
    private readonly string _dataRoot = CreateTempRoot();

    public void Dispose() => Directory.Delete(_dataRoot, true);

    [Fact]
    public async Task LoadVector_LoadsVec0_AndCreatesVec0VirtualTable()
    {
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "vec0.db")}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.EnableExtensions();

        connection.LoadVector();

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE VIRTUAL TABLE probe_vec USING vec0(embedding float[384])";
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using var exists = connection.CreateCommand();
        exists.CommandText =
            "SELECT count(*) FROM sqlite_master WHERE type = 'table' AND name = 'probe_vec'";
        var count = (long)(await exists.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        count.ShouldBe(1);
    }

    private static string CreateTempRoot() => TestData.CreateTempRoot("airaccoon-store-tests");
}
