using AiRaccoon.Infrastructure.Chunking;
using Microsoft.ML.Tokenizers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Chunking;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class ChunkingFeatureTests
{
    private static string BuildLongNote() =>
        string.Join(
            "\n",
            Enumerable.Range(1, 40).Select(i =>
                $"## Section {i}\n\nThis is paragraph {i} with enough prose to clearly exceed the token budget for a single chunk."));

    [Fact]
    public void IngestingMarkdownNoteLongerThanMaxTokens_ProducesTokenBoundedChunksWithOverlay()
    {
        var chunker = TestData.RealMarkdownChunker();
        var tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

        var chunks = chunker.Chunk(BuildLongNote(), 128, 24);

        chunks.Count.ShouldBeGreaterThan(1);
        chunks.ShouldAllBe(chunk => tokenizer.CountTokens(chunk) <= 128);
        for (var i = 1; i < chunks.Count; i++)
        {
            chunks[i - 1].Split('\n').ShouldContain(chunks[i].Split('\n')[0]);
        }
    }

    [Fact]
    public void IngestingNoteWithUnbalancedFence_NoChunkExceedsMaxTokens()
    {
        // Mirrors RAG-F3/RAG-F4 (docs/adr/0036): a stray, never-closed fence in a long document
        // must not glue the rest of the document into one unbounded chunk.
        var chunker = TestData.RealMarkdownChunker();
        var body = string.Join("\n", Enumerable.Range(1, 200).Select(i =>
            $"Paragraph {i} with enough prose to resemble a real document body worth chunking."));
        var note = $"# Notes\n\nSee the fence marker below.\n\n```\n{body}\n";

        var chunks = chunker.Chunk(note, 256, 48);

        var tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");
        chunks.ShouldAllBe(chunk => tokenizer.CountTokens(chunk) <= 256);
    }

    [Fact]
    public void ChunkingIdenticalNoteTwice_ProducesIdenticalChunks()
    {
        var chunker = TestData.RealMarkdownChunker();

        var first = chunker.Chunk(BuildLongNote(), 128, 24);
        var second = chunker.Chunk(BuildLongNote(), 128, 24);

        first.SequenceEqual(second).ShouldBeTrue();
    }

    [Fact]
    public void IngestingNoteWithFencedCodeBlockWithinBudget_NeverSplitsTheFence()
    {
        var chunker = TestData.RealMarkdownChunker();
        var fence = "```csharp\nvar value = Compute(1);\n```\n";
        var note = $"# Code sample\n\n{fence}Trailing prose after the fence.\n";

        var chunks = chunker.Chunk(note, 50);

        var tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");
        var fenceChunk = chunks.Single(chunk => chunk.Contains("```", StringComparison.Ordinal));
        fenceChunk.ShouldContain(fence);
        chunks.ShouldAllBe(chunk => tokenizer.CountTokens(chunk) <= 50);
    }

    [Fact]
    public void IngestingNoteWithFencedCodeBlockOverBudget_FallsBackToTokenBoundedChunks()
    {
        // Mirrors RAG-F4 (docs/adr/0036): an oversized fence is already a broken chunk, so keeping
        // it atomic buys nothing — it must fall back to token-bounded splitting like any other content.
        var chunker = TestData.RealMarkdownChunker();
        var fence = $"```csharp\n{string.Join("\n", Enumerable.Range(1, 40).Select(i => $"var value{i} = Compute({i});"))}\n```\n";
        var note = $"# Code sample\n\n{fence}Trailing prose after the fence.\n";

        var chunks = chunker.Chunk(note, 50);

        var tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");
        chunks.ShouldAllBe(chunk => tokenizer.CountTokens(chunk) <= 50);
        string.Concat(chunks).ShouldBe(note);
    }
}
