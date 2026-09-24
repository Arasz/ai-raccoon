using AiRaccoon.Infrastructure.Sqlite;
using Microsoft.Data.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     Behavioural half of the PathSubtree contract: every assertion runs the range through a real
///     SQLite comparison (BINARY collation, the one the path columns use), not .NET string ordering,
///     so a candidate the engine sorts differently would be caught here.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class PathSubtreeSqliteBehaviourTests
{
    [Theory]
    [InlineData("/r/note_1", "/r/note_1/child.md", true)]
    [InlineData("/r/note_1", "/r/noteX1/child.md", false)]
    [InlineData("/r/50%off", "/r/50%off/a.md", true)]
    [InlineData("/r/50%off", "/r/50Xoff/a.md", false)]
    [InlineData(@"/r/a\b", @"/r/a\b/c.md", true)]
    [InlineData("/r/docs", "/r/docs-old/a.md", false)]
    [InlineData("/r/docs", "/r/docs.v2/a.md", false)]
    [InlineData("/r/docs", "/r/Docs/a.md", false)]
    [InlineData("/r/zażółć", "/r/zażółć/ę.md", true)]
    [InlineData("/r/zażółć", "/r/zażółćx/ę.md", false)]
    public void Range_InTheEngine_HoldsExactlyTheSubtree(string directory, string candidate, bool expected)
    {
        using var connection = new SqliteConnection("Data Source=:memory:");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT @candidate >= @low AND @candidate < @high";
        command.Parameters.AddWithValue("@candidate", candidate);
        command.Parameters.AddWithValue("@low", PathSubtree.Low(directory));
        command.Parameters.AddWithValue("@high", PathSubtree.High(directory));

        ((long)command.ExecuteScalar()! == 1).ShouldBe(expected);
    }
}
