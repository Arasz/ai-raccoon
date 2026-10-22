using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     The typed vector layout (ADR-0141): the memory vector tables carry a second native partition
///     column for the row's declared kind, so a typed query restricts BEFORE the KNN truncation
///     rather than after it. This file pins the layout the shipped sqlite-vec actually accepts:
///     two partition columns, and a typed KNN that reads only the rows of the asked-for kind.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class TypedVectorPartitionLayoutTests : IDisposable
{
    private const int Dimension = 4;
    private readonly string _dataRoot = CreateTempRoot();

    public void Dispose() => TestData.DeleteTempRoot(_dataRoot);

    /// <summary>One row of the typed layout: its id, the kind it carries, and its vector.</summary>
    private sealed record VectorRow(long Id, string Type, byte[] Embedding);

    /// <summary>
    ///     The layout itself: two partition columns on one vec0 table, and a KNN restricted to one
    ///     kind that returns only that kind's rows — the whole point of restricting natively rather
    ///     than postfiltering, which starves a minority kind.
    /// </summary>
    [RetryFact]
    public async Task TwoPartitionColumns_RestrictTheKnnToOneKind()
    {
        await using var connection = await OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText =
                $"CREATE VIRTUAL TABLE probe_vec USING vec0(ctx TEXT partition key, type TEXT partition key, embedding float[{Dimension}] distance_metric=cosine)";
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO probe_vec(rowid, ctx, type, embedding) VALUES (@id, @ctx, @type, @embedding)";
            insert.Parameters.AddWithValue("@id", 0L);
            insert.Parameters.AddWithValue("@ctx", "project:acme");
            insert.Parameters.AddWithValue("@type", "file");
            insert.Parameters.AddWithValue("@embedding", Vector(1, 0, 0, 0));
            // Three File rows and one Memory row in the same context: the Memory row is the
            // minority that a postfilter would lose.
            foreach (var row in new VectorRow[]
                     {
                         new(1, "file", Vector(1, 0, 0, 0)),
                         new(2, "file", Vector(0.99f, 0.1f, 0, 0)),
                         new(3, "file", Vector(0.98f, 0.2f, 0, 0)),
                         new(4, "memory", Vector(0.97f, 0.3f, 0, 0))
                     })
            {
                insert.Parameters["@id"].Value = row.Id;
                insert.Parameters["@type"].Value = row.Type;
                insert.Parameters["@embedding"].Value = row.Embedding;
                await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
        }

        await using var select = connection.CreateCommand();
        select.CommandText = """
                             SELECT rowid FROM probe_vec
                             WHERE ctx = 'project:acme' AND type = 'memory'
                             ORDER BY distance
                             LIMIT 10
                             """;
        var ids = new List<long>();
        await using (var reader = await select.ExecuteReaderAsync(TestContext.Current.CancellationToken))
        {
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                ids.Add(reader.GetInt64(0));
            }
        }

        ids.ShouldBe([4L], "a typed KNN reads only the rows of the asked-for kind");
    }

    /// <summary>
    ///     An unrestricted query omits the kind predicate and reads the whole context — the case
    ///     every query falls back to when the caller asks for everything.
    /// </summary>
    [RetryFact]
    public async Task AnUnrestrictedQuery_ReadsTheWholeContext()
    {
        await using var connection = await OpenAsync();
        await using (var create = connection.CreateCommand())
        {
            create.CommandText =
                $"CREATE VIRTUAL TABLE probe_vec USING vec0(ctx TEXT partition key, type TEXT partition key, embedding float[{Dimension}] distance_metric=cosine)";
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO probe_vec(rowid, ctx, type, embedding) VALUES (@id, @ctx, @type, @embedding)";
            insert.Parameters.AddWithValue("@id", 0L);
            insert.Parameters.AddWithValue("@ctx", "project:acme");
            insert.Parameters.AddWithValue("@type", "file");
            insert.Parameters.AddWithValue("@embedding", Vector(1, 0, 0, 0));
            foreach (var row in new VectorRow[]
                     {
                         new(1, "file", Vector(1, 0, 0, 0)),
                         new(2, "memory", Vector(0.9f, 0.1f, 0, 0))
                     })
            {
                insert.Parameters["@id"].Value = row.Id;
                insert.Parameters["@type"].Value = row.Type;
                insert.Parameters["@embedding"].Value = row.Embedding;
                await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
        }

        await using var select = connection.CreateCommand();
        select.CommandText = "SELECT count(*) FROM probe_vec WHERE ctx = 'project:acme'";
        var count = (long)(await select.ExecuteScalarAsync(TestContext.Current.CancellationToken))!;
        count.ShouldBe(2, "omitting the kind predicate reads every kind in the context");
    }

    private static byte[] Vector(float a, float b, float c, float d)
    {
        var bytes = new byte[Dimension * sizeof(float)];
        var values = new[] { a, b, c, d };
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private async Task<SqliteConnection> OpenAsync()
    {
        var connection = new SqliteConnection($"Data Source={Path.Combine(_dataRoot, "typed-vec.db")}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        connection.EnableExtensions();
        connection.LoadVector();
        return connection;
    }

    private static string CreateTempRoot() => TestData.CreateTempRoot("airtyped-vec-layout-tests");
}
