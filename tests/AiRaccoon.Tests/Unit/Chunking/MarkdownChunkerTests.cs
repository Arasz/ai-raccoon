using AiRaccoon.Core.Chunking;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Chunking;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class MarkdownChunkerTests
{
    private static int CharCount(string text) => text.Length;

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
    public void Split_FencedCodeBlock_IsNeverSplit()
    {
        var text = "# Title\n\n```csharp\nvar x = 1;\nvar y = 2;\n```\n\nTail.\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 12);

        chunks.ShouldBe(["# Title\n\n", "```csharp\nvar x = 1;\nvar y = 2;\n```\n", "\nTail.\n"]);
    }

    [Fact]
    public void Split_FenceLargerThanMaxTokens_IsEmittedWhole()
    {
        var fence = "```\nvar x = 1;\nvar y = 2;\n```\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(fence, 5);

        chunks.ShouldBe([fence]);
    }

    [Fact]
    public void Split_TildeFence_IsNeverSplit()
    {
        var chunks = new MarkdownChunker(CharCount).Chunk("~~~\ncode\n~~~\n", 5);

        chunks.ShouldBe(["~~~\ncode\n~~~\n"]);
    }

    [Fact]
    public void Split_UnclosedFence_KeepsRestOfNoteInOneChunk()
    {
        var chunks = new MarkdownChunker(CharCount).Chunk("```\ncode\nmore\n", 5);

        chunks.ShouldBe(["```\ncode\nmore\n"]);
    }

    [Fact]
    public void Split_MultipleFences_EachFenceStaysIntact()
    {
        var text = "a\n\n```\nx\n```\n\nb\n\n```\ny\n```\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 3);

        chunks.ShouldBe(["a\n\n", "```\nx\n```\n", "\nb\n", "\n", "```\ny\n```\n"]);
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
