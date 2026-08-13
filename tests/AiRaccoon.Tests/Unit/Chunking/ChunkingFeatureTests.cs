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
    public void ChunkingIdenticalNoteTwice_ProducesIdenticalChunks()
    {
        var chunker = TestData.RealMarkdownChunker();

        var first = chunker.Chunk(BuildLongNote(), 128, 24);
        var second = chunker.Chunk(BuildLongNote(), 128, 24);

        first.SequenceEqual(second).ShouldBeTrue();
    }

    [Fact]
    public void IngestingNoteWithFencedCodeBlock_NeverSplitsTheFence()
    {
        var chunker = TestData.RealMarkdownChunker();
        var fence = $"```csharp\n{string.Join("\n", Enumerable.Range(1, 40).Select(i => $"var value{i} = Compute({i});"))}\n```\n";
        var note = $"# Code sample\n\n{fence}Trailing prose after the fence.\n";

        var chunks = chunker.Chunk(note, 50);

        var fenceChunk = chunks.Single(chunk => chunk.Contains("```"));
        fenceChunk.ShouldContain(fence);
        chunks.Where(chunk => !chunk.Contains("```"))
            .ShouldAllBe(chunk => !chunk.Contains("var value"));
    }
}
