using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Search;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class SnippetFallbackTests
{
    private const string LongValue =
        "Architecture decision records capture the rationale behind significant technical choices. " +
        "Each record names the context, the decision itself, and the consequences that follow from it. " +
        "Reviewers rely on these records to understand why the system evolved the way it did, and to " +
        "avoid repeating mistakes that earlier teams already documented. The registry grows monotonically " +
        "as new decisions are ratified; superseded records are amended rather than deleted, preserving the " +
        "full decision history for future maintainers.";

    [Fact]
    public void From_ShortValue_IsReturnedUnchanged() => SnippetFallback.From("short text", "hash").ShouldBe("short text");

    [Fact]
    public void From_ValueAtWindowBoundary_IsReturnedUnchanged()
    {
        var value = new string('x', SnippetFallback.WindowChars);
        SnippetFallback.From(value, "hash").ShouldBe(value);
    }

    [Fact]
    public void From_LongValue_IsDeterministicForTheSameEntry() => SnippetFallback.From(LongValue, "entry-0000").ShouldBe(SnippetFallback.From(LongValue, "entry-0000"));

    [Fact]
    public void From_LongValue_IsBoundedToTheWindow_WithEllipsesMarkingTheCuts()
    {
        var snippet = SnippetFallback.From(LongValue, "entry-0000");

        snippet.Length.ShouldBeLessThanOrEqualTo(SnippetFallback.WindowChars + 4);
        snippet.ShouldStartWith("… ");
        snippet.ShouldEndWith(" …");
    }

    [Fact]
    public void From_LongValue_WindowIsKeyedByTheEntryHash() =>
        // Different entries (hashes) over the same value open on different windows.
        SnippetFallback.From(LongValue, "entry-0000").ShouldNotBe(SnippetFallback.From(LongValue, "entry-0001"));
}
