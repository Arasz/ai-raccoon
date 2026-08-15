using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     The vec0 `ctx` column (ADR-0068 demoted it from partition key to metadata): one context-key
///     encoding shared by the SQL fragment and its C# twin. Length-prefixed rather than
///     ':'-joined so a project id containing ':' cannot collide with a label containing ':'.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class MemorySqlContextKeyTests
{
    /// <summary>
    ///     (project_id='a:b', label='c') and (project_id='a', label='b:c') both encode to
    ///     'custom:a:b:c' under naive ':' joining — verified red before the length-prefixed fix.
    /// </summary>
    [Fact]
    public void ContextKeyFor_DoesNotCollide_WhenAProjectIdAndLabelSplitDifferently()
    {
        var first = MemorySql.ContextKeyFor("c", "a:b");
        var second = MemorySql.ContextKeyFor("b:c", "a");

        first.ShouldNotBe(second);
    }

    [Theory]
    [InlineData("shared", "acme", "shared")]
    [InlineData("project:acme", "acme", "project:acme")]
    [InlineData("workspace:ws-1", "acme", "workspace:4:acme:ws-1")]
    [InlineData("docs-notes", "acme", "custom:4:acme:docs-notes")]
    public void ContextKeyFor_EncodesEachContextShape(string context, string projectId, string expected) => MemorySql.ContextKeyFor(context, projectId).ShouldBe(expected);
}
