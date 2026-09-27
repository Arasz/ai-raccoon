using AiRaccoon.Core.Chunking;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Chunking;

/// <summary>
///     A chunk boundary inside an over-budget line falls on whitespace, so every word of the input
///     appears whole in some chunk and keyword search can still find it. Only a single word longer
///     than the whole budget is cut, and only that word.
/// </summary>
[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ChunkWordBoundaryTests
{
    private static int CharCount(string text) => text.Length;

    private static string LongNote() =>
        string.Join(" ", Enumerable.Repeat(
            "The village fete committee met in the church hall to plan stalls, bunting, the tombola and the cake competition.", 10))
        + " Invoice reference vk83jq was filed with the parish council.";

    private static string[] Words(string text) => text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

    [Theory]
    [InlineData(60, 0)]
    [InlineData(97, 0)]
    [InlineData(100, 0)]
    [InlineData(100, 30)]
    [InlineData(254, 0)]
    public void Chunk_OverBudgetLine_CutsOnlyAtWhitespace(int maxTokens, int overlayTokens)
    {
        var text = LongNote();

        var chunks = new MarkdownChunker(CharCount).Chunk(text, maxTokens, overlayTokens);

        chunks.Count.ShouldBeGreaterThan(1, "premise: the note is longer than the budget");
        var inputWords = Words(text).ToHashSet(StringComparer.Ordinal);
        chunks.SelectMany(Words).ShouldAllBe(word => inputWords.Contains(word), "no chunk may hold part of a word");
        chunks.ShouldAllBe(chunk => chunk.Length <= maxTokens);
        chunks.ShouldContain(chunk => chunk.Contains("vk83jq"));
    }

    [Fact]
    public void Chunk_OverBudgetLine_ConcatenatesBackToTheInput()
    {
        var text = LongNote();

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 97);

        string.Concat(chunks).ShouldBe(text, "a word-boundary cut still drops no character");
        chunks.ShouldAllBe(chunk => chunk.Length > 0);
    }

    [Fact]
    public void Chunk_PlainTextOverBudgetLine_CutsOnlyAtWhitespace()
    {
        var text = LongNote();

        var chunks = new PlainTextChunker(CharCount).Chunk(text, 97);

        var inputWords = Words(text).ToHashSet(StringComparer.Ordinal);
        chunks.SelectMany(Words).ShouldAllBe(word => inputWords.Contains(word), "no chunk may hold part of a word");
    }

    [Fact]
    public void Chunk_OverBudgetFencedLine_CutsOnlyAtWhitespace()
    {
        var line = string.Join(" ", Enumerable.Range(0, 60).Select(i => $"call_handler_{i:D2}(ref{i});"));
        var text = "```csharp\n" + line + "\n```\n";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 120);

        chunks.Count.ShouldBeGreaterThan(1, "premise: the fenced line is longer than the budget");
        var inputWords = Words(text).ToHashSet(StringComparer.Ordinal);
        chunks.SelectMany(Words).ShouldAllBe(word => inputWords.Contains(word), "no sub-fence may hold part of a word");
        chunks.ShouldAllBe(chunk => chunk.Length <= 120);
    }

    [Fact]
    public void Chunk_WordLongerThanTheBudget_IsTheOnlyWordCut()
    {
        var giant = string.Concat(Enumerable.Range(0, 60).Select(i => $"x{i:D2}"));
        var text = "alpha bravo charlie delta echo foxtrot " + giant + " golf hotel india juliet kilo lima";

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 50);

        var inputWords = Words(text).ToHashSet(StringComparer.Ordinal);
        chunks.SelectMany(Words).ShouldAllBe(word => inputWords.Contains(word) || giant.Contains(word),
            "only the over-budget word may be cut");
        chunks.ShouldAllBe(chunk => chunk.Length > 0 && chunk.Length <= 50);
        string.Concat(chunks).ShouldBe(text);
    }

    [Fact]
    public void Chunk_TextWithNoWhitespace_HardCutsWithinBudgetAndTerminates()
    {
        var text = string.Concat(Enumerable.Repeat("記憶の銀行は検索できる", 50));

        var chunks = new MarkdownChunker(CharCount).Chunk(text, 64);

        chunks.Count.ShouldBe((text.Length + 63) / 64, "no whitespace means full-budget hard cuts");
        chunks.ShouldAllBe(chunk => chunk.Length > 0 && chunk.Length <= 64);
        string.Concat(chunks).ShouldBe(text);
    }
}
