using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;
using SourcePathQuery = AiRaccoon.Infrastructure.Sqlite.Memory.SourcePathQuery;

namespace AiRaccoon.Tests.Unit.Search;

/// <summary>
///     Behavioural half of the file#section anchor contract: each expression TryBuild produces is run
///     through a real FTS5 table shaped like entries_fts, so what the anchor actually selects is asserted,
///     not only the string it builds.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SourcePathQueryFtsBehaviourTests
{
    [Theory]
    [InlineData("/notes/ferry-control.md")]
    [InlineData("/docs/notes-ferry.md")]
    public void Anchor_NamingOneFile_DoesNotMatchAnotherFileHoldingTheSameWords(string otherFile)
    {
        var matched = Match("ferry-notes.md#fares", ("/docs/ferry-notes.md", "fares"), (otherFile, "fares"));

        matched.ShouldContain("/docs/ferry-notes.md", "the named file must still match — the control");
        matched.ShouldNotContain(otherFile, $"'{otherFile}' is a different file; its path only shares the words of the anchor's file name");
    }

    private static List<string> Match(string query, params (string SourceFile, string Section)[] rows)
    {
        SourcePathQuery.TryBuild(query, out var expression).ShouldBeTrue();

        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using (var create = connection.CreateCommand())
        {
            create.CommandText = "CREATE VIRTUAL TABLE entries_fts USING fts5(value, source_file, section)";
            create.ExecuteNonQuery();
        }

        foreach (var (sourceFile, section) in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO entries_fts(value, source_file, section) VALUES ('body', $file, $section)";
            insert.Parameters.AddWithValue("$file", sourceFile);
            insert.Parameters.AddWithValue("$section", section);
            insert.ExecuteNonQuery();
        }

        using var select = connection.CreateCommand();
        select.CommandText = "SELECT source_file FROM entries_fts WHERE entries_fts MATCH $expression";
        select.Parameters.AddWithValue("$expression", expression);
        using var reader = select.ExecuteReader();
        var matched = new List<string>();
        while (reader.Read())
        {
            matched.Add(reader.GetString(0));
        }

        return matched;
    }
}
