using AiRaccoon.Core.Chunking;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Chunking;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class MarkdownChunkerTests
{
    private static int CharCount(string text) => text.Length;

    [Fact]
    public void Split_NoteLongerThanMaxTokens_SplitsAtLineBoundariesWithinBudget()
    {
        var chunks = MarkdownChunker.Split("aaaa\nbbbb\ncccc\ndddd\n", maxTokens: 10, overlayTokens: 0, CharCount);

        chunks.ShouldBe(new[] { "aaaa\nbbbb\n", "cccc\ndddd\n" });
    }

    [Fact]
    public void Split_WithOverlay_ReusesTailOfPreviousChunk()
    {
        var chunks = MarkdownChunker.Split("aaaa\nbbbb\ncccc\ndddd\n", maxTokens: 10, overlayTokens: 5, CharCount);

        chunks.ShouldBe(new[] { "aaaa\nbbbb\n", "bbbb\ncccc\n", "cccc\ndddd\n" });
    }

    [Fact]
    public void Split_NoteWithinBudget_ReturnsWholeNoteAsSingleChunk()
    {
        var chunks = MarkdownChunker.Split("aaaa\nbbbb\n", maxTokens: 10, overlayTokens: 0, CharCount);

        chunks.ShouldBe(new[] { "aaaa\nbbbb\n" });
    }

    [Fact]
    public void Split_FencedCodeBlock_IsNeverSplit()
    {
        var text = "# Title\n\n```csharp\nvar x = 1;\nvar y = 2;\n```\n\nTail.\n";

        var chunks = MarkdownChunker.Split(text, maxTokens: 12, overlayTokens: 0, CharCount);

        chunks.ShouldBe(new[] { "# Title\n\n", "```csharp\nvar x = 1;\nvar y = 2;\n```\n", "\nTail.\n" });
    }

    [Fact]
    public void Split_FenceLargerThanMaxTokens_IsEmittedWhole()
    {
        var fence = "```\nvar x = 1;\nvar y = 2;\n```\n";

        var chunks = MarkdownChunker.Split(fence, maxTokens: 5, overlayTokens: 0, CharCount);

        chunks.ShouldBe(new[] { fence });
    }

    [Fact]
    public void Split_TildeFence_IsNeverSplit()
    {
        var chunks = MarkdownChunker.Split("~~~\ncode\n~~~\n", maxTokens: 5, overlayTokens: 0, CharCount);

        chunks.ShouldBe(new[] { "~~~\ncode\n~~~\n" });
    }

    [Fact]
    public void Split_UnclosedFence_KeepsRestOfNoteInOneChunk()
    {
        var chunks = MarkdownChunker.Split("```\ncode\nmore\n", maxTokens: 5, overlayTokens: 0, CharCount);

        chunks.ShouldBe(new[] { "```\ncode\nmore\n" });
    }

    [Fact]
    public void Split_MultipleFences_EachFenceStaysIntact()
    {
        var text = "a\n\n```\nx\n```\n\nb\n\n```\ny\n```\n";

        var chunks = MarkdownChunker.Split(text, maxTokens: 3, overlayTokens: 0, CharCount);

        chunks.ShouldBe(new[] { "a\n\n", "```\nx\n```\n", "\nb\n", "\n", "```\ny\n```\n" });
    }

    [Fact]
    public void Split_IdenticalInput_ProducesIdenticalChunks()
    {
        var text = "aaaa\nbbbb\ncccc\ndddd\n";

        var first = MarkdownChunker.Split(text, maxTokens: 10, overlayTokens: 5, CharCount);
        var second = MarkdownChunker.Split(text, maxTokens: 10, overlayTokens: 5, CharCount);

        first.SequenceEqual(second).ShouldBeTrue();
    }

    [Fact]
    public void Split_CrLfInput_NormalizesLineEndings()
    {
        var chunks = MarkdownChunker.Split("aa\r\nbb\r\ncc", maxTokens: 5, overlayTokens: 0, CharCount);

        chunks.ShouldBe(new[] { "aa\n", "bb\ncc" });
        chunks.ShouldAllBe(chunk => !chunk.Contains('\r'));
    }

    [Fact]
    public void Split_EmptyText_ReturnsNoChunks()
    {
        var chunks = MarkdownChunker.Split("", maxTokens: 10, overlayTokens: 0, CharCount);

        chunks.ShouldBeEmpty();
    }

    [Fact]
    public void Split_LineWithoutTrailingNewline_IsKeptWhole()
    {
        var chunks = MarkdownChunker.Split("aa\nbb\ncc", maxTokens: 4, overlayTokens: 0, CharCount);

        chunks.ShouldBe(new[] { "aa\n", "bb\n", "cc" });
    }

    [Fact]
    public void Split_WithMaxTokensZero_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            MarkdownChunker.Split("aa", maxTokens: 0, overlayTokens: 0, CharCount));

    [Fact]
    public void Split_WithNegativeOverlay_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            MarkdownChunker.Split("aa", maxTokens: 10, overlayTokens: -1, CharCount));

    [Fact]
    public void Split_WithOverlayNotBelowMaxTokens_Throws() =>
        Should.Throw<ArgumentOutOfRangeException>(() =>
            MarkdownChunker.Split("aa", maxTokens: 10, overlayTokens: 10, CharCount));

    [Fact]
    public void Split_WithNullText_Throws() =>
        Should.Throw<ArgumentNullException>(() =>
            MarkdownChunker.Split(null!, maxTokens: 10, overlayTokens: 0, CharCount));

    [Fact]
    public void Split_WithNullTokenCounter_Throws() =>
        Should.Throw<ArgumentNullException>(() =>
            MarkdownChunker.Split("aa", maxTokens: 10, overlayTokens: 0, null!));
}
