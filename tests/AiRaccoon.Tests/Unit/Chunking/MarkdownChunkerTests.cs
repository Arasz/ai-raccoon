using AiRaccoon.Core.Chunking;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Chunking;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class MarkdownChunkerTests
{
    private static int CharCount(string text) => text.Length;

    private static IReadOnlyList<string> Chunk(string text, int maxTokens, int overlayTokens = 0) => new MarkdownChunker(CharCount).Chunk(text, maxTokens, overlayTokens);

    [Fact]
    public void Chunk_NoteLongerThanMaxTokens_SplitsAtLineBoundariesWithinBudget()
    {
        var chunks = Chunk("aaaa\nbbbb\ncccc\ndddd\n", 10, 0);

        chunks.ShouldBe(["aaaa\nbbbb\n", "cccc\ndddd\n"]);
    }

    [Fact]
    public void Chunk_WithOverlay_ReusesTailOfPreviousChunk()
    {
        var chunks = Chunk("aaaa\nbbbb\ncccc\ndddd\n", 10, 5);

        chunks.ShouldBe(["aaaa\nbbbb\n", "bbbb\ncccc\n", "cccc\ndddd\n"]);
    }

    [Fact]
    public void Chunk_NoteWithinBudget_ReturnsWholeNoteAsSingleChunk()
    {
        var chunks = Chunk("aaaa\nbbbb\n", 10, 0);

        chunks.ShouldBe(["aaaa\nbbbb\n"]);
    }

    [Fact]
    public void Chunk_FencedCodeBlock_IsNeverSplit()
    {
        var text = "# Title\n\n```csharp\nvar x = 1;\nvar y = 2;\n```\n\nTail.\n";

        var chunks = Chunk(text, 12, 0);

        chunks.ShouldBe(["# Title\n\n", "```csharp\nvar x = 1;\nvar y = 2;\n```\n", "\nTail.\n"]);
    }

    [Fact]
    public void Chunk_FenceLargerThanMaxTokens_IsEmittedWhole()
    {
        var fence = "```\nvar x = 1;\nvar y = 2;\n```\n";

        var chunks = Chunk(fence, 5, 0);

        chunks.ShouldBe([fence]);
    }

    [Fact]
    public void Chunk_TildeFence_IsNeverSplit()
    {
        var chunks = Chunk("~~~\ncode\n~~~\n", 5, 0);

        chunks.ShouldBe(["~~~\ncode\n~~~\n"]);
    }

    [Fact]
    public void Chunk_UnclosedFence_KeepsRestOfNoteInOneChunk()
    {
        var chunks = Chunk("```\ncode\nmore\n", 5, 0);

        chunks.ShouldBe(["```\ncode\nmore\n"]);
    }

    [Fact]
    public void Chunk_MultipleFences_EachFenceStaysIntact()
    {
        var text = "a\n\n```\nx\n```\n\nb\n\n```\ny\n```\n";

        var chunks = Chunk(text, 3, 0);

        chunks.ShouldBe(["a\n\n", "```\nx\n```\n", "\nb\n", "\n", "```\ny\n```\n"]);
    }

    [Fact]
    public void Chunk_IdenticalInput_ProducesIdenticalChunks()
    {
        var text = "aaaa\nbbbb\ncccc\ndddd\n";

        var first = Chunk(text, 10, 5);
        var second = Chunk(text, 10, 5);

        first.SequenceEqual(second).ShouldBeTrue();
    }

    [Fact]
    public void Chunk_CrLfInput_NormalizesLineEndings()
    {
        var chunks = Chunk("aa\r\nbb\r\ncc", 5, 0);

        chunks.ShouldBe(["aa\n", "bb\ncc"]);
        chunks.ShouldAllBe(chunk => !chunk.Contains('\r'));
    }

    [Fact]
    public void Chunk_EmptyText_ReturnsNoChunks()
    {
        var chunks = Chunk("", 10, 0);

        chunks.ShouldBeEmpty();
    }

    [Fact]
    public void Chunk_LineWithoutTrailingNewline_IsKeptWhole()
    {
        var chunks = Chunk("aa\nbb\ncc", 4, 0);

        chunks.ShouldBe(["aa\n", "bb\n", "cc"]);
    }

    [Fact]
    public void Chunk_WithMaxTokensZero_Throws() => Should.Throw<ArgumentOutOfRangeException>(() => Chunk("aa", 0, 0));

    [Fact]
    public void Chunk_WithNegativeOverlay_Throws() => Should.Throw<ArgumentOutOfRangeException>(() => Chunk("aa", 10, -1));

    [Fact]
    public void Chunk_WithOverlayNotBelowMaxTokens_Throws() => Should.Throw<ArgumentOutOfRangeException>(() => Chunk("aa", 10, 10));

    [Fact]
    public void Chunk_WithNullText_Throws() => Should.Throw<ArgumentNullException>(() => new MarkdownChunker(CharCount).Chunk(null!, 10, 0));

    [Fact]
    public void Chunk_WithNullTokenCounter_Throws() => Should.Throw<ArgumentNullException>(() => new MarkdownChunker(null!));
}
