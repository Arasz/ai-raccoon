using AiRaccoon.Infrastructure.Chunking;
using Microsoft.ML.Tokenizers;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Chunking;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class TokenizerChunkerTests
{
    private static string BuildLongNote() =>
        string.Join(
            "\n",
            Enumerable.Range(1, 60).Select(i =>
                $"## Section {i}\n\nThis is paragraph {i} with enough prose to clearly exceed the token budget for a single chunk."));

    [Fact]
    public void Chunk_ShortNoteWithinBudget_ReturnsWholeNoteAsSingleChunk()
    {
        var chunker = new TokenizerChunker();

        var chunks = chunker.Chunk("# Hello\n\nShort note.\n", maxTokens: 512);

        chunks.ShouldBe(new[] { "# Hello\n\nShort note.\n" });
    }

    [Fact]
    public void Chunk_LongNote_ProducesChunksWithinMaxTokens()
    {
        var chunker = new TokenizerChunker();
        var tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

        var chunks = chunker.Chunk(BuildLongNote(), maxTokens: 96, overlayTokens: 16);

        chunks.Count.ShouldBeGreaterThan(1);
        chunks.ShouldAllBe(chunk => tokenizer.CountTokens(chunk) <= 96);
    }

    [Fact]
    public void Chunk_DefaultBounds256Overlay48_KeepChunksWithinTheModelWindow()
    {
        var chunker = new TokenizerChunker();
        var tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

        var chunks = chunker.Chunk(BuildLongNote(), maxTokens: 256, overlayTokens: 48);

        chunks.Count.ShouldBeGreaterThan(1);
        chunks.ShouldAllBe(chunk => tokenizer.CountTokens(chunk) <= 256);
    }

    [Fact]
    public void Chunk_IdenticalInput_ProducesIdenticalChunks()
    {
        var chunker = new TokenizerChunker();

        var first = chunker.Chunk(BuildLongNote(), maxTokens: 96, overlayTokens: 16);
        var second = chunker.Chunk(BuildLongNote(), maxTokens: 96, overlayTokens: 16);

        first.SequenceEqual(second).ShouldBeTrue();
    }

    [Fact]
    public void Chunk_EmptyText_ReturnsNoChunks()
    {
        var chunker = new TokenizerChunker();

        chunker.Chunk("", maxTokens: 100).ShouldBeEmpty();
    }

    [Fact]
    public void Chunk_NullText_Throws() => Should.Throw<ArgumentNullException>(() => new TokenizerChunker().Chunk(null!, maxTokens: 100));

    [Fact]
    public void O200kBase_CountsKnownStrings()
    {
        var tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");

        tokenizer.CountTokens("").ShouldBe(0);
        tokenizer.CountTokens("Hello").ShouldBe(1);
        tokenizer.CountTokens("Hello world").ShouldBe(2);
        tokenizer.CountTokens("Hello, world!").ShouldBe(4);
    }

    [Fact]
    public void O200kBase_CountingIsDeterministic()
    {
        var tokenizer = TiktokenTokenizer.CreateForEncoding("o200k_base");
        const string text = "The quick brown fox jumps over the lazy dog.";

        tokenizer.CountTokens(text).ShouldBe(tokenizer.CountTokens(text));
    }
}
