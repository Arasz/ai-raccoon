using AiRaccoon.Core.Chunking;
using AiRaccoon.Infrastructure.Chunking;
using Xunit;

namespace AiRaccoon.Tests.Unit.Ingestion;

[Trait(TestCategories.Category, TestCategories.Unit)]
[Trait(TestCategories.Speed, TestCategories.Fast)]
public class JsonFileTypeChunkerTests
{
    [Fact]
    public void Chunk_ValidJsonObject_ReturnsFormattedJsonChunks()
    {
        var chunker = TestData.RealJsonChunker();
        var json = """
                   {
                     "name": "ai-raccoon",
                     "version": "1.7.0",
                     "description": "C# .NET 10 MCP server exposing agent memory management"
                   }
                   """;

        var chunks = chunker.Chunk(json, maxTokens: 256, overlayTokens: 0);

        Assert.NotEmpty(chunks);
        Assert.All(chunks, chunk => Assert.False(string.IsNullOrWhiteSpace(chunk)));
    }

    [Fact]
    public void Chunk_LargeJsonObject_SplitsIntoMultipleTokenBoundedChunks()
    {
        var chunker = TestData.RealJsonChunker();
        var properties = new List<string>();
        for (var i = 0; i < 50; i++)
        {
            properties.Add($"\"key_{i}\": \"This is value number {i} with a detailed description to exceed token budget\"");
        }

        var json = "{\n" + string.Join(",\n", properties) + "\n}";

        var chunks = chunker.Chunk(json, maxTokens: 60, overlayTokens: 0);

        Assert.True(chunks.Count > 1, $"Expected multiple chunks, got {chunks.Count}");
    }

    [Fact]
    public void Chunk_StructuralPath_IgnoresOverlayTokens()
    {
        var chunker = TestData.RealJsonChunker();
        var properties = new List<string>();
        for (var i = 0; i < 50; i++)
        {
            properties.Add($"\"key_{i}\": \"This is value number {i} with a detailed description to exceed token budget\"");
        }

        var json = "{\n" + string.Join(",\n", properties) + "\n}";

        // The structural grouping is key/item-bounded and non-overlapping by design (D5):
        // overlayTokens must not change the emitted chunks, or it would duplicate whole
        // properties across adjacent chunks. Only the line-based fallback honors it.
        var withOverlay = chunker.Chunk(json, maxTokens: 60, overlayTokens: 48);
        var withoutOverlay = chunker.Chunk(json, maxTokens: 60, overlayTokens: 0);

        Assert.Equal(withoutOverlay, withOverlay);
    }

    [Fact]
    public void Chunk_FallbackUsesCtorOverlayBudget_NotPerCallArgument()
    {
        // A malformed JSON document falls back to the markdown line splitter, which honors
        // overlay. The budget the fallback uses is the chunker's ctor config (WP3), not the
        // per-call argument — the per-call arg is the structural path's, which is non-overlapping.
        var tokenizer = new O200kTokenizer();
        var fallback = new MarkdownChunker(tokenizer.CountTokens);
        var chunker = new JsonFileTypeChunker(tokenizer.CountTokens, fallback, overlayTokens: 6);
        // Short lines so several fit in the 6-token overlay budget (a unit bigger than the
        // budget is dropped whole — the spotty-overlap property, see the F4 research record).
        var text = string.Join("\n", Enumerable.Range(1, 40).Select(i => $"l{i} x"));

        var chunks = chunker.Chunk(text, maxTokens: 30, overlayTokens: 0);

        Assert.True(chunks.Count > 1);
        // Ctor budget 6 must show up as a reused tail line between adjacent chunks.
        for (var i = 1; i < chunks.Count; i++)
        {
            var tail = chunks[i - 1].Split('\n').Last(l => l.Length > 0);
            Assert.Contains(tail, chunks[i]);
        }
    }

    [Fact]
    public void Chunk_MalformedJson_FallsBackToLineChunkingWithoutThrowing()
    {
        var chunker = TestData.RealJsonChunker();
        var invalidJson = "{\n  \"name\": \"ai-raccoon\",\n  \"unclosed_string\": \"oops\n";

        var chunks = chunker.Chunk(invalidJson, maxTokens: 256, overlayTokens: 0);

        Assert.NotEmpty(chunks);
        Assert.Contains("ai-raccoon", chunks[0]);
    }

    [Fact]
    public void Chunk_EmptyOrWhitespace_ReturnsEmptyOrSingleChunk()
    {
        var chunker = TestData.RealJsonChunker();
        var emptyChunks = chunker.Chunk("", maxTokens: 256);
        Assert.Empty(emptyChunks);

        var emptyObjChunks = chunker.Chunk("{}", maxTokens: 256);
        Assert.Single(emptyObjChunks);
        Assert.Equal("{}", emptyObjChunks[0]);
    }
}
