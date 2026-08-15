using AiRaccoon.Infrastructure.Sqlite;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Storage;

/// <summary>
///     `memory_list`'s tree shape. Extracted from <c>SqliteMemoryStore</c> as a pure function, so it
///     is testable without a bank — it never touched one.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public sealed class FileTreeTests
{
    [Fact]
    public void Build_NestsEachSegment_AndLeavesTheFileAsALeaf() =>
        FileTree.Build(["docs/adr/0001.md"])
            .ShouldBe("""{"docs":{"adr":{"0001.md":{}}}}""");

    [Fact]
    public void Build_MergesPathsSharingAPrefix_IntoOneDirectory() =>
        FileTree.Build(["docs/a.md", "docs/b.md"])
            .ShouldBe("""{"docs":{"a.md":{},"b.md":{}}}""");

    /// <summary>Ordinal, not culture-aware: the output is a wire format, so it must not move with the host locale.</summary>
    [Fact]
    public void Build_OrdersSiblingsOrdinally() =>
        FileTree.Build(["b.md", "A.md", "a.md"])
            .ShouldBe("""{"A.md":{},"a.md":{},"b.md":{}}""");

    [Fact]
    public void Build_OnNoPaths_IsAnEmptyObject() =>
        FileTree.Build([]).ShouldBe("{}");

    /// <summary>
    ///     A file and a directory competing for one name. The directory wins, because a leaf that
    ///     later turns out to have children would otherwise silently swallow them.
    /// </summary>
    [Fact]
    public void Build_WhenANameIsBothAFileAndADirectory_KeepsTheDirectory() =>
        FileTree.Build(["docs", "docs/a.md"])
            .ShouldBe("""{"docs":{"a.md":{}}}""");
}
