using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using xRetry.v3;

namespace AiRaccoon.Tests.Integration.Storage;

/// <summary>
///     The kind a row holds is decided by ONE rule. It exists twice — as C# (the classifier the
///     write paths ask) and as SQL (the bulk statements that cannot call C#) — so the two can
///     drift. This test runs both over one fixture: if they ever disagree about a row's shape,
///     one of them is wrong and the fixture says which.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Integration)]
[Trait(TestCategories.Speed, TestCategories.Slow)]
public sealed class EntryTypeRuleParityTests
{
    /// <summary>One row of the fixture: the shape the rule reads, and the kind it must give.</summary>
    private sealed record RowShape(string? Path, string? SourceFile, string Expected);

    /// <summary>The row shapes the rule has to tell apart, and the kind each one carries.</summary>
    private static readonly RowShape[] Fixture =
    [
        // A row that mirrors a file on disk: path = source_file.
        new("docs/guide.md", "docs/guide.md", "file"),
        new("notes/probe.md", "notes/probe.md", "file"),
        // A note that CITES a file: a source file, but a path of its own — still a memory.
        new("9f2c.md", "notes/probe.md", "memory"),
        // A note from a transcript: a source file, a synthetic path — still a memory.
        new("9f2c.md", "hermes/20260806_215718_fd7f66", "memory"),
        // A manual note: no source file at all.
        new("9f2c.md", null, "memory"),
        // An empty source file is no locator.
        new("9f2c.md", "", "memory"),
        // No path at all: the row cannot mirror a file it has no path for.
        new(null, "docs/guide.md", "memory"),
        // Neither a path nor a source file.
        new(null, null, "memory"),
        // A path that differs only in case is not the same path.
        new("Docs/Guide.md", "docs/guide.md", "memory")
    ];

    [RetryFact]
    public async Task TheSqlRuleAndTheCsharpRule_ClassifyOneFixtureAlike()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE rows (path TEXT, source_file TEXT)";
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO rows (path, source_file) VALUES (@path, @sourceFile)";
            foreach (var row in Fixture)
            {
                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("@path", (object?)row.Path ?? DBNull.Value);
                insert.Parameters.AddWithValue("@sourceFile", (object?)row.SourceFile ?? DBNull.Value);
                await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
        }

        var classifier = new EntryKindClassifier();
        var sqlKinds = new List<string>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = $"SELECT {MemorySql.EntryTypeFromShape(string.Empty)} FROM rows";
            await using var reader = await select.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                sqlKinds.Add(reader.GetString(0));
            }
        }

        sqlKinds.Count.ShouldBe(Fixture.Length);

        for (var i = 0; i < Fixture.Length; i++)
        {
            var row = Fixture[i];

            var csharp = classifier.ToStoredValue(classifier.Classify(row.Path, row.SourceFile));
            csharp.ShouldBe(row.Expected, $"the C# rule disagrees about ({row.Path}, {row.SourceFile})");
            sqlKinds[i].ShouldBe(row.Expected, $"the SQL rule disagrees about ({row.Path}, {row.SourceFile})");
            sqlKinds[i].ShouldBe(csharp, $"the two rules disagree about ({row.Path}, {row.SourceFile})");
        }
    }

    /// <summary>
    ///     The other half of the rule: a DECLARED kind wins, and only an undeclared row is resolved
    ///     from its shape. The sync merge decides a pulled row's kind with this expression, so the
    ///     two must agree — a merge that filed a declared row by its shape would move it into the
    ///     wrong corpus.
    /// </summary>
    [RetryFact]
    public async Task TheDeclaredKindWins_InSqlAndInCsharp()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE TABLE rows (entry_type TEXT, path TEXT, source_file TEXT)";
            await create.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        // A declared kind, an undeclared row, and a kind this build does not know.
        var stored = new[] { "file", "memory", "none", null, "transcript" };
        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO rows (entry_type, path, source_file) VALUES (@kind, @path, @sourceFile)";
            foreach (var kind in stored)
            {
                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("@kind", (object?)kind ?? DBNull.Value);
                insert.Parameters.AddWithValue("@path", "docs/guide.md");
                insert.Parameters.AddWithValue("@sourceFile", "docs/guide.md");
                await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
        }

        var sqlKinds = new List<string?>();
        await using (var select = connection.CreateCommand())
        {
            select.CommandText = $"SELECT {MemorySql.EntryTypeFromDeclarationOrShape(string.Empty)} FROM rows";
            await using var reader = await select.ExecuteReaderAsync(TestContext.Current.CancellationToken);
            while (await reader.ReadAsync(TestContext.Current.CancellationToken))
            {
                sqlKinds.Add(reader.IsDBNull(0) ? null : reader.GetString(0));
            }
        }

        var classifier = new EntryKindClassifier();
        for (var i = 0; i < stored.Length; i++)
        {
            var kind = stored[i];
            var expected = classifier.ToStoredValue(classifier.Resolve(kind, "docs/guide.md", "docs/guide.md"));
            sqlKinds[i].ShouldBe(expected, $"the two rules disagree about the stored kind '{kind}'");
        }
    }
}
