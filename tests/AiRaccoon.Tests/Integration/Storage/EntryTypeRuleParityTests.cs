using AiRaccoon.Core.Memory;
using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

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
    /// <summary>The row shapes the rule has to tell apart, and the kind each one carries.</summary>
    private static readonly (string? Path, string? SourceFile, string Expected)[] Fixture =
    [
        // A row that mirrors a file on disk: path = source_file.
        ("docs/guide.md", "docs/guide.md", "file"),
        ("notes/probe.md", "notes/probe.md", "file"),
        // A note that CITES a file: a source file, but a path of its own — still a memory.
        ("9f2c.md", "notes/probe.md", "memory"),
        // A note from a transcript: a source file, a synthetic path — still a memory.
        ("9f2c.md", "hermes/20260806_215718_fd7f66", "memory"),
        // A manual note: no source file at all.
        ("9f2c.md", null, "memory"),
        // An empty source file is no locator.
        ("9f2c.md", "", "memory")
    ];

    [Fact]
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
            foreach (var (path, sourceFile, _) in Fixture)
            {
                insert.Parameters.Clear();
                insert.Parameters.AddWithValue("@path", (object?)path ?? DBNull.Value);
                insert.Parameters.AddWithValue("@sourceFile", (object?)sourceFile ?? DBNull.Value);
                await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }
        }

        var classifier = new EntryTypeClassifier();
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
            var (path, sourceFile, expected) = Fixture[i];

            var csharp = classifier.ToStoredValue(classifier.Classify(path, sourceFile));
            csharp.ShouldBe(expected, $"the C# rule disagrees about ({path}, {sourceFile})");
            sqlKinds[i].ShouldBe(expected, $"the SQL rule disagrees about ({path}, {sourceFile})");
            sqlKinds[i].ShouldBe(csharp, $"the two rules disagree about ({path}, {sourceFile})");
        }
    }
}
