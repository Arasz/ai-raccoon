using AiRaccoon.Core.Chunking;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Chunking;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class MarkdownChunkerTests
{
    private static int CharCount(string text) => text.Length;

    /// <summary>
    ///     Everything that is not a fence delimiter and not a line break — the payload the chunker
    ///     must preserve exactly even though it may re-emit delimiters and break a long line
    ///     (docs/adr/0048).
    /// </summary>
    private static string FencedPayload(string text) =>
        string.Concat(text.Split('\n')
            .Where(line => !line.TrimStart().StartsWith("```", StringComparison.Ordinal)
                           && !line.TrimStart().StartsWith("~~~", StringComparison.Ordinal)));

    private static int FenceDelimiterCount(string text) =>
        text.Split('\n').Count(line =>
            line.TrimStart().StartsWith("```", StringComparison.Ordinal)
            || line.TrimStart().StartsWith("~~~", StringComparison.Ordinal));

    [Fact]
    public void Split_NoteLongerThanMaxTokens_SplitsAtLineBoundariesWithinBudget()
    {
        var chunks = new MarkdownChunker(CharCount).Chunk("aaaa\nbbbb\ncccc\ndddd\n", 10);

        chunks.ShouldBe(["aaaa\nbbbb\n", "cccc\ndddd\n"]);
    }

    [Fact]
    public void Split_WithOverlay_ReusesTailOfPreviousChunk()
    {
        var chunks = new MarkdownChunker(CharCount).Chunk("aaaa\nbbbb\ncccc\ndddd\n", 10, 5);

        chunks.ShouldBe(["aaaa\nbbbb\n", "bbbb\ncccc\n", "cccc\ndddd\n"]);
    }

    [Fact]
    public void Split_NoteWithinBudget_ReturnsWholeNoteAsSingleChunk()
    {
        var chunks = new MarkdownChunker(CharCount).Chunk("aaaa\nbbbb\n", 10);

        chunks.ShouldBe(["aaaa\nbbbb\n"]);
    }

    [Fact]
    public void Split_FencedCodeBlockWithinBudget_IsNeverSplit()
    {
        var text = "# Title\n\n```csharp\nvar x = 1;\nvar y = 2;\n```\n\nTail.\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 40);

        var fenceChunk = chunks.Single(chunk => chunk.Contains("```", StringComparison.Ordinal));
        fenceChunk.ShouldContain("```csharp\nvar x = 1;\nvar y = 2;\n```\n");
        chunks.ShouldAllBe(chunk => CharCount(chunk) <= 40);
    }

    [Fact]
    public void Split_FenceLargerThanMaxTokens_SplitsIntoBoundedWellFormedFences()
    {
        var fence = "```\nvar x = 1;\nvar y = 2;\n```\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(fence, 15);

        // An over-budget fence is no longer atomic (docs/adr/0036) but is still a fence: each piece
        // repeats the delimiters, so no chunk begins inside the block (docs/adr/0048).
        chunks.ShouldAllBe(chunk => CharCount(chunk) <= 15);
        chunks.ShouldAllBe(chunk => FenceDelimiterCount(chunk) % 2 == 0);
        FencedPayload(string.Concat(chunks)).ShouldBe(FencedPayload(fence));
    }

    [Fact]
    public void Split_TildeFenceWithinBudget_IsNeverSplit()
    {
        var chunks = new MarkdownChunker(CharCount).Chunk("~~~\ncode\n~~~\n", 20);

        chunks.ShouldBe(["~~~\ncode\n~~~\n"]);
    }

    [Fact]
    public void Split_TildeFenceLargerThanMaxTokens_FallsBackToLineGranularSplitting()
    {
        var chunks = new MarkdownChunker(CharCount).Chunk("~~~\ncode\n~~~\n", 5);

        chunks.ShouldAllBe(chunk => CharCount(chunk) <= 5);
        string.Concat(chunks).ShouldBe("~~~\ncode\n~~~\n");
    }

    [Fact]
    public void Split_UnclosedFence_IsClosedAndStillFallsBackWhenTheDelimitersDoNotFitTheBudget()
    {
        // A never-closed fence is closed rather than abandoned (docs/adr/0048); it still must not
        // glue the rest of the note into one unbounded chunk (docs/adr/0036). At a budget too small
        // to hold the delimiters at all, the budget wins and the region degrades to bare lines.
        var chunks = new MarkdownChunker(CharCount).Chunk("```\ncode\nmore\n", 5);

        chunks.ShouldAllBe(chunk => CharCount(chunk) <= 5);
        string.Concat(chunks).ShouldBe("```\ncode\nmore\n```\n");
    }

    [Fact]
    public void Split_UnclosedFenceWithinBudget_IsClosedAndKeptAtomic()
    {
        var chunks = new MarkdownChunker(CharCount).Chunk("```\ncode\nmore\n", 40);

        chunks.ShouldBe(["```\ncode\nmore\n```\n"]);
    }

    [Fact]
    public void Split_MultipleFencesWithinBudget_EachFenceStaysIntact()
    {
        var text = "a\n\n```\nx\n```\n\nb\n\n```\ny\n```\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 10);

        chunks.Count(chunk => chunk.Contains("```", StringComparison.Ordinal)).ShouldBe(2);
        chunks.ShouldContain("```\nx\n```\n");
        chunks.ShouldContain("```\ny\n```\n");
        string.Concat(chunks).ShouldBe(text);
    }

    [Fact]
    public void Split_UnbalancedFence_NoChunkExceedsMaxTokens()
    {
        // Mirrors the review's measured shape: a stray, never-closed fence followed by a long
        // document body. Before the fix, this glues everything past the fence into one unbounded
        // chunk (RED); after, no chunk exceeds maxTokens (docs/adr/0036) and the region is closed
        // and re-fenced rather than de-fenced (docs/adr/0048).
        var body = string.Join("\n", Enumerable.Range(1, 30).Select(i => $"Paragraph {i} with several words of prose."));
        var text = $"# Notes\n\nSee the fence marker below.\n\n```\n{body}\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 60);

        chunks.ShouldAllBe(chunk => CharCount(chunk) <= 60);
        chunks.ShouldAllBe(chunk => FenceDelimiterCount(chunk) % 2 == 0);
        FencedPayload(string.Concat(chunks)).ShouldBe(FencedPayload(text));
    }

    [Fact]
    public void Split_HeadingWouldDangleAtChunkTail_DefersHeadingToNextChunk()
    {
        // Issue #489: the greedy pack lets a heading line squeeze into the tail of a chunk even
        // though none of its own section's content fits alongside it — that chunk then gets
        // mislabeled with the NEXT section's heading. A heading must open the chunk that holds
        // its content, never end the previous one empty-handed.
        var text = "# Title\n\n## Section A\nAAAA\n\n## Section B\nBBBB\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 41);

        chunks.ShouldBe(["# Title\n\n## Section A\nAAAA\n\n", "## Section B\nBBBB\n"]);
    }

    [Fact]
    public void Split_SectionContinuesPastChunkTail_DefersHeadingAndItsBodyToNextChunk()
    {
        // Issue #538 (a #489 residual): #489 only handed back a heading that dangled bare at a
        // chunk's tail. When the budget instead runs out a few body lines AFTER the heading, the
        // heading plus those lines stay at the tail and the whole chunk gets mislabeled with a
        // section that is ~90% absent from it. A heading opens its chunk unless its whole section
        // fits in the one that would otherwise hold it.
        var text = "# Title\n\n## Section A\nAAAA\n\n## Section B\nBBBB\nCCCC\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 46);

        chunks.ShouldBe(["# Title\n\n## Section A\nAAAA\n\n", "## Section B\nBBBB\nCCCC\n"]);
        chunks[0].ShouldNotContain("## Section B");
        HeadingPathParser.Parse(chunks[0]).ShouldBe("Title > Section A");
        chunks[1].ShouldStartWith("## Section B");
    }

    [Fact]
    public void Split_NextSectionFitsEntirelyInRemainingBudget_StaysInTheSameChunk()
    {
        // Negative control for #538: a heading must be deferred only when its section is actually
        // cut by the chunk boundary. When the whole next section already fits alongside the
        // previous one, deferring it anyway would just shrink chunks for no reason.
        var text = "# Title\n\n## Section A\nAAAA\n\n## Section B\nBBBB\n\n## Section C\nCCCC\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 47);

        chunks.ShouldBe(["# Title\n\n## Section A\nAAAA\n\n## Section B\nBBBB\n\n", "## Section C\nCCCC\n"]);
    }

    [Fact]
    public void Split_SectionLargerThanMaxTokens_HeadingOpensItsFirstChunkAndSplittingTerminates()
    {
        // Negative control for #538: a section too long to fit any chunk must still have its
        // heading open the first chunk that carries its content — deferring it forever would
        // either loop or emit an empty chunk. The heading-as-first-new-unit guard is what stops
        // the generalized rule from deferring it past the point where it can make no progress.
        var text = "# Title\n\n## Big Section\nLINE1\nLINE2\nLINE3\nLINE4\nLINE5\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 30);

        chunks.ShouldBe(["# Title\n\n", "## Big Section\nLINE1\nLINE2\n", "LINE3\nLINE4\nLINE5\n"]);
        chunks.ShouldAllBe(chunk => CharCount(chunk) <= 30 && chunk.Length > 0);
    }

    [Fact]
    public void Split_IdenticalInput_ProducesIdenticalChunks()
    {
        var text = "aaaa\nbbbb\ncccc\ndddd\n";

        var first = new MarkdownChunker(CharCount).Chunk(text, 10, 5);
        var second = new MarkdownChunker(CharCount).Chunk(text, 10, 5);

        first.SequenceEqual(second).ShouldBeTrue();
    }

    [Fact]
    public void Split_CrLfInput_NormalizesLineEndings()
    {
        var chunks = new MarkdownChunker(CharCount).Chunk("aa\r\nbb\r\ncc", 5);

        chunks.ShouldBe(["aa\n", "bb\ncc"]);
        chunks.ShouldAllBe(chunk => !chunk.Contains('\r'));
    }

    [Fact]
    public void Split_EmptyText_ReturnsNoChunks()
    {
        var chunks = new MarkdownChunker(CharCount).Chunk("", 10);

        chunks.ShouldBeEmpty();
    }

    [Fact]
    public void Split_LineWithoutTrailingNewline_IsKeptWhole()
    {
        var chunks = new MarkdownChunker(CharCount).Chunk("aa\nbb\ncc", 4);

        chunks.ShouldBe(["aa\n", "bb\n", "cc"]);
    }

    [Fact]
    public void Split_WithMaxTokensZero_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new MarkdownChunker(CharCount).Chunk("aa", 0));

    [Fact]
    public void Split_WithNegativeOverlay_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new MarkdownChunker(CharCount).Chunk("aa", 10, -1));

    [Fact]
    public void Split_WithOverlayNotBelowMaxTokens_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            new MarkdownChunker(CharCount).Chunk("aa", 10, 10));

    [Fact]
    public void Split_WithNullText_Throws() =>
        Should.Throw<ArgumentNullException>(() =>
            new MarkdownChunker(CharCount).Chunk(null!, 10));

    [Fact]
    public void Split_WithNullTokenCounter_Throws() =>
        Should.Throw<ArgumentNullException>(() =>
            new MarkdownChunker(null!));
}
