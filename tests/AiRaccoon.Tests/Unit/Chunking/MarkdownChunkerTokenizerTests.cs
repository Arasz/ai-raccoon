using AiRaccoon.Infrastructure.Chunking;
using Shouldly;
using Xunit;

namespace AiRaccoon.Tests.Unit.Chunking;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class MarkdownChunkerTokenizerTests
{
    private static string BuildLongNote() =>
        string.Join(
            "\n",
            Enumerable.Range(1, 60).Select(i =>
                $"## Section {i}\n\nThis is paragraph {i} with enough prose to clearly exceed the token budget for a single chunk."));

    [Fact]
    public void Chunk_ShortNoteWithinBudget_ReturnsWholeNoteAsSingleChunk()
    {
        var chunker = TestData.RealMarkdownChunker();

        var chunks = chunker.Chunk("# Hello\n\nShort note.\n", 512);

        chunks.ShouldBe(["# Hello\n\nShort note.\n"]);
    }

    [Fact]
    public void Chunk_LongNote_ProducesChunksWithinMaxTokens()
    {
        var chunker = TestData.RealMarkdownChunker();
        var tokenizer = new O200kTokenizer();

        var chunks = chunker.Chunk(BuildLongNote(), 96, 16);

        chunks.Count.ShouldBeGreaterThan(1);
        chunks.ShouldAllBe(chunk => tokenizer.CountTokens(chunk) <= 96);
    }

    [Fact]
    public void Chunk_DefaultBounds256Overlay48_KeepChunksWithinTheModelWindow()
    {
        var chunker = TestData.RealMarkdownChunker();
        var tokenizer = new O200kTokenizer();

        var chunks = chunker.Chunk(BuildLongNote(), 256, 48);

        chunks.Count.ShouldBeGreaterThan(1);
        chunks.ShouldAllBe(chunk => tokenizer.CountTokens(chunk) <= 256);
    }

    [Fact]
    public void Chunk_IdenticalInput_ProducesIdenticalChunks()
    {
        var chunker = TestData.RealMarkdownChunker();

        var first = chunker.Chunk(BuildLongNote(), 96, 16);
        var second = chunker.Chunk(BuildLongNote(), 96, 16);

        first.SequenceEqual(second).ShouldBeTrue();
    }

    [Fact]
    public void Chunk_EmptyText_ReturnsNoChunks()
    {
        var chunker = TestData.RealMarkdownChunker();

        chunker.Chunk("", 100).ShouldBeEmpty();
    }

    [Fact]
    public void Chunk_NullText_Throws() => Should.Throw<ArgumentNullException>(() => TestData.RealMarkdownChunker().Chunk(null!, 100));

    [Fact]
    public void O200kBase_CountsKnownStrings()
    {
        var tokenizer = new O200kTokenizer();

        tokenizer.CountTokens("").ShouldBe(0);
        tokenizer.CountTokens("Hello").ShouldBe(1);
        tokenizer.CountTokens("Hello world").ShouldBe(2);
        tokenizer.CountTokens("Hello, world!").ShouldBe(4);
    }

    [Fact]
    public void O200kBase_CountingIsDeterministic()
    {
        var tokenizer = new O200kTokenizer();
        const string text = "The quick brown fox jumps over the lazy dog.";

        tokenizer.CountTokens(text).ShouldBe(tokenizer.CountTokens(text));
    }
}
