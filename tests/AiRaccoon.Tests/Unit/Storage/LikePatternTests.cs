using System.Reflection;
using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     LikePattern.Escape guards the path-prefix cascade deletes (SqliteMemoryStore.DeleteSourcePathAsync,
///     ReplaceFileAsync, WatchStore.RemoveWatchAsync): an unescaped '_' or '%' in a path turns a
///     literal prefix into a wildcard and deletes sibling rows.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class LikePatternTests
{
    /// <summary>The escape character the cascade SQL declares, and the one Escape must emit.</summary>
    private const string EscapeClause = @"ESCAPE '\'";

    [Theory]
    [InlineData("docs/api.md", "docs/api.md")]
    [InlineData("", "")]
    [InlineData("foo_bar", @"foo\_bar")]
    [InlineData("50%off", @"50\%off")]
    [InlineData(@"a\b", @"a\\b")]
    [InlineData("a_b_c", @"a\_b\_c")]
    [InlineData("%a%", @"\%a\%")]
    [InlineData("_x_", @"\_x\_")]
    [InlineData(@"_%\", @"\_\%\\")]
    // Every axis at once: the backslash must be doubled before '%' and '_' introduce their own.
    [InlineData(@"a_b%c\d", @"a\_b\%c\\d")]
    // A path that already looks escaped is still raw input: its backslash is data, not an escape.
    [InlineData(@"a\_b", @"a\\\_b")]
    public void Escape_MakesEveryLikeMetacharacterLiteral(string value, string expected) =>
        LikePattern.Escape(value).ShouldBe(expected);

    /// <summary>
    ///     Escape and the SQL are two halves of one contract; derived from MemorySql rather than
    ///     listed, so a new path-prefix query without ESCAPE fails here too.
    /// </summary>
    [Fact]
    public void EveryPathPrefixQuery_DeclaresTheEscapeCharacterThatEscapeEmits()
    {
        var prefixQueries = PathPrefixQueries();

        prefixQueries.ShouldNotBeEmpty("no MemorySql query matches on @pathPrefix any more");
        var undeclared = prefixQueries.Where(q => !q.Sql.Contains(EscapeClause, StringComparison.Ordinal))
            .Select(q => q.Name).ToList();
        undeclared.ShouldBeEmpty("these path-prefix queries do not declare ESCAPE: " + string.Join(", ", undeclared));
        LikePattern.Escape("_").ShouldBe(@"\_");
        LikePattern.Escape("%").ShouldBe(@"\%");
    }

    private static List<(string Name, string Sql)> PathPrefixQueries() =>
    [
        .. typeof(MemorySql).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f is { IsLiteral: true } && f.FieldType == typeof(string))
            .Select(f => (f.Name, Sql: (string)f.GetRawConstantValue()!))
            .Where(q => q.Sql.Contains("LIKE @pathPrefix", StringComparison.Ordinal))
    ];
}
