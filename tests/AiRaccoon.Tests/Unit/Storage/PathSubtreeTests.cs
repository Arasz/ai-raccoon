using System.Reflection;
using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     PathSubtree bounds the path cascade deletes (SqliteMemoryStore.DeleteSourcePathAsync,
///     ReplaceCoreAsync, WatchStore.RemoveWatchAsync): the range must hold exactly the paths under
///     the directory — never a sibling that only shares its name as a prefix.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class PathSubtreeTests
{
    private const string Dir = "/repo/.ai-badger/task-tracking/tracking.db";

    [Theory]
    [InlineData(Dir + "/a.md", true)]
    [InlineData(Dir + "/deep/er/b.md", true)]
    [InlineData(Dir + "/", true)]
    [InlineData(Dir, false)]
    [InlineData(Dir + "-shm", false)]
    [InlineData(Dir + "-wal", false)]
    [InlineData(Dir + ".bak", false)]
    [InlineData(Dir + "0", false)]
    [InlineData(Dir + "_x/c.md", false)]
    [InlineData("/repo/.ai-badger/task-tracking/tracking.dc/a.md", false)]
    public void Range_HoldsExactlyThePathsUnderTheDirectory(string candidate, bool expected)
    {
        var inRange = string.CompareOrdinal(candidate, PathSubtree.Low(Dir)) >= 0 &&
                      string.CompareOrdinal(candidate, PathSubtree.High(Dir)) < 0;

        inRange.ShouldBe(expected);
    }

    /// <summary>
    ///     Derived from MemorySql rather than listed: a new path cascade written as a LIKE prefix
    ///     scans the project instead of seeking the index, and fails here.
    /// </summary>
    [Fact]
    public void NoPathCascadeQuery_MatchesTheSubtreeWithLike()
    {
        var queries = typeof(MemorySql).GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.FieldType == typeof(string))
            .Select(f => (f.Name, Sql: (string)f.GetValue(null)!))
            .ToList();

        queries.Where(q => q.Sql.Contains("@subtreeLow", StringComparison.Ordinal))
            .ShouldNotBeEmpty("no MemorySql query cascades over a path subtree any more");
        var likePrefixes = queries.Where(q => q.Sql.Contains("path LIKE @", StringComparison.Ordinal))
            .Select(q => q.Name).ToList();
        likePrefixes.ShouldBeEmpty("these path cascades still use LIKE: " + string.Join(", ", likePrefixes));
    }
}
